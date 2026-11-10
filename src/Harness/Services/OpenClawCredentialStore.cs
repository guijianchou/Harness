// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Helpers;

namespace Harness.Services;

public sealed record OpenClawCredentials(string? Token = null, string? Password = null, string? AccessToken = null, string? Endpoint = null)
{
    public override string ToString() => nameof(OpenClawCredentials);
}

/// <summary>Current-user Windows protection; no credential values are persisted in settings.</summary>
public sealed class OpenClawCredentialStore
{
    private readonly string _directory;

    public OpenClawCredentialStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Harness", "secrets");
    }

    public async Task<OpenClawCredentials?> ReadAsync(string reference, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadProtectedAsync("credential:" + reference, cancellationToken);
        if (bytes is null) { return null; }
        try { return JsonSerializer.Deserialize<OpenClawCredentials>(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task WriteAsync(string reference, OpenClawCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try { await WriteProtectedAsync("credential:" + reference, bytes, cancellationToken); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(GetPath("credential:" + reference));
    }, cancellationToken);

    public async Task<string?> ReadDeviceTokenAsync(string environmentId, string role, IEnumerable<string> scopes, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadProtectedAsync(DeviceTokenKey(environmentId, role, scopes), cancellationToken);
        if (bytes is null) { return null; }
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task SaveDeviceTokenAsync(string environmentId, string role, IEnumerable<string> scopes, string token, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        try { await WriteProtectedAsync(DeviceTokenKey(environmentId, role, scopes), bytes, cancellationToken); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal Task<byte[]?> ReadProtectedAsync(string key, CancellationToken cancellationToken) => Task.Run<byte[]?>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var protectedBytes = Convert.FromBase64String(File.ReadAllText(GetPath(key)));
            return ProtectedData.Unprotect(protectedBytes, Encoding.UTF8.GetBytes(key), DataProtectionScope.CurrentUser);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }, cancellationToken);

    internal Task WriteProtectedAsync(string key, byte[] bytes, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var protectedBytes = ProtectedData.Protect(bytes, Encoding.UTF8.GetBytes(key), DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(_directory);
        AtomicFileWriter.WriteAllText(GetPath(key), Convert.ToBase64String(protectedBytes));
    }, cancellationToken);

    private string GetPath(string key) => Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".secret");

    internal async Task<FileStream> AcquireIdentityLockAsync(string environmentId, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(GetPath("identity:" + environmentId) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline) { await Task.Delay(50, cancellationToken); }
        }
    }

    private static string DeviceTokenKey(string environmentId, string role, IEnumerable<string> scopes) =>
        "device-token:" + JsonSerializer.Serialize(new[] { environmentId, role, string.Join(',', scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) });
}
