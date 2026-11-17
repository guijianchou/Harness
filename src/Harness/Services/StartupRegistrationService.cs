// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Harness.Services;

internal sealed class StartupRegistrationService
{
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovalSubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Harness";
    private readonly string _executablePath;
    private readonly string _registrySubKeyPath;
    private readonly string _startupApprovalSubKeyPath;
    private readonly string _mutexName;
    private readonly Func<bool, CancellationToken, Task<StartupRegistrationChange>>? _applyOverride;
    private readonly Func<CancellationToken, Task<StartupRegistrationState>>? _readOverride;

    public StartupRegistrationService(
        string? executablePath = null,
        string registrySubKeyPath = RunSubKey,
        string startupApprovalSubKeyPath = ApprovalSubKey)
    {
        _executablePath = executablePath ?? Environment.ProcessPath ?? "";
        _registrySubKeyPath = registrySubKeyPath;
        _startupApprovalSubKeyPath = startupApprovalSubKeyPath;
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}|{registrySubKeyPath}|{ValueName}";
        _mutexName = @"Local\Harness.StartupRegistration." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant())));
    }

    internal StartupRegistrationService(
        Func<bool, CancellationToken, Task<StartupRegistrationChange>> apply,
        Func<CancellationToken, Task<StartupRegistrationState>>? read = null)
        : this()
    {
        _applyOverride = apply ?? throw new ArgumentNullException(nameof(apply));
        _readOverride = read;
    }

    public Task<StartupRegistrationState> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        return _readOverride is not null
            ? _readOverride(cancellationToken)
            : Task.Run(() => WithRegistryLock(ReadStateCore, cancellationToken), cancellationToken);
    }

    private StartupRegistrationState ReadStateCore()
    {
        var registered = ReadValue();
        if (registered is null)
        {
            return StartupRegistrationState.NotRegistered;
        }

        if (IsDisabledByWindows())
        {
            return StartupRegistrationState.DisabledByWindows;
        }

        return registered.Kind == RegistryValueKind.String && registered.Data is string command &&
            string.Equals(command, BuildCommand(), StringComparison.OrdinalIgnoreCase)
            ? StartupRegistrationState.Registered
            : StartupRegistrationState.DifferentLocation;
    }

    private bool IsDisabledByWindows()
    {
        using var approval = Registry.CurrentUser.OpenSubKey(_startupApprovalSubKeyPath);
        return approval?.GetValue(ValueName) is byte[] { Length: >= 4 } state && state[0] is 3 or 7;
    }

    public Task<StartupRegistrationChange> ApplyAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        return _applyOverride is not null
            ? _applyOverride(enabled, cancellationToken)
            : Task.Run(() => WithRegistryLock(() => ApplyCore(enabled), cancellationToken), cancellationToken);
    }

    private StartupRegistrationChange ApplyCore(bool enabled)
    {
        var desired = enabled ? new RegistryValue(BuildCommand(), RegistryValueKind.String) : null;
        if (enabled && IsDisabledByWindows())
        {
            throw new InvalidOperationException("Windows has disabled Harness in Startup apps. Enable it in Windows Settings before enabling launch at startup here.");
        }

        var before = ReadValue();
        if (ValuesEqual(before, desired))
        {
            return new StartupRegistrationChange(_ => Task.FromResult(true));
        }

        try
        {
            WriteValue(desired);
            VerifyValue(desired);
        }
        catch (Exception applyError)
        {
            try
            {
                if (ValuesEqual(ReadValue(), desired))
                {
                    WriteValue(before);
                    VerifyValue(before);
                }
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Startup registration failed and its previous state could not be restored.", applyError, rollbackError);
            }

            throw;
        }

        return new StartupRegistrationChange(token => Task.Run(() => WithRegistryLock(() =>
        {
            if (!ValuesEqual(ReadValue(), desired))
            {
                return false;
            }

            WriteValue(before);
            VerifyValue(before);
            return true;
        }, token), token));
    }

    private string BuildCommand()
    {
        if (string.IsNullOrWhiteSpace(_executablePath) || !Path.IsPathFullyQualified(_executablePath) ||
            _executablePath.IndexOfAny(['"', '\r', '\n', '\0']) >= 0 ||
            !string.Equals(Path.GetExtension(_executablePath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(_executablePath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Startup registration requires the absolute path of the Harness executable.");
        }

        var path = Path.GetFullPath(_executablePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The startup executable does not exist.", path);
        }

        var command = $"\"{path}\"";
        if (command.Length > 260)
        {
            throw new PathTooLongException("The startup command exceeds the Windows Run entry length limit.");
        }

        return command;
    }

    private RegistryValue? ReadValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_registrySubKeyPath);
        var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null ? null : new RegistryValue(value, key!.GetValueKind(ValueName));
    }

    private void WriteValue(RegistryValue? value)
    {
        if (value is null)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(_registrySubKeyPath, writable: true);
            existing?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(_registrySubKeyPath, writable: true)
            ?? throw new IOException("The startup registry key could not be opened for writing.");
        key.SetValue(ValueName, value.Data, value.Kind);
    }

    private void VerifyValue(RegistryValue? expected)
    {
        if (!ValuesEqual(ReadValue(), expected))
        {
            throw new IOException("The startup registry entry did not retain the requested value.");
        }
    }

    private T WithRegistryLock<T>(Func<T> action, CancellationToken cancellationToken)
    {
        // Acquisition, mutation, and release stay on this single worker thread.
        using var mutex = new Mutex(false, _mutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle], TimeSpan.FromSeconds(5)) == 0;
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!acquired)
            {
                throw new TimeoutException("Another Harness instance is updating startup registration. Try saving again.");
            }

            return action();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static bool ValuesEqual(RegistryValue? left, RegistryValue? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left.Kind != right.Kind)
        {
            return false;
        }

        return (left.Data, right.Data) switch
        {
            (byte[] first, byte[] second) => first.AsSpan().SequenceEqual(second),
            (string[] first, string[] second) => first.SequenceEqual(second, StringComparer.Ordinal),
            _ => Equals(left.Data, right.Data),
        };
    }

    private sealed record RegistryValue(object Data, RegistryValueKind Kind);
}

internal enum StartupRegistrationState
{
    NotRegistered,
    Registered,
    DisabledByWindows,
    DifferentLocation,
}

internal sealed class StartupRegistrationChange
{
    private readonly Func<CancellationToken, Task<bool>> _rollback;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _restored;

    internal StartupRegistrationChange(Func<CancellationToken, Task<bool>> rollback)
    {
        _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
    }

    public async Task<bool> RollbackAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_restored)
            {
                _restored = await _rollback(cancellationToken).ConfigureAwait(false);
            }

            return _restored;
        }
        finally
        {
            _gate.Release();
        }
    }
}
