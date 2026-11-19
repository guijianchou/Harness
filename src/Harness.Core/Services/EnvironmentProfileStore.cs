using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Helpers;
using Harness.Models;

namespace Harness.Services;

public sealed class EnvironmentProfileStore
{
    private const string OwnerFileName = ".harness-environment-id";
    private readonly string _root;
    private readonly object _registrationGate = new();
    private readonly HashSet<string> _registeredEnvironmentIds = new(StringComparer.Ordinal);
    private EnvironmentConfig[]? _pendingRegistration;
    private string RegistryPath => Path.Combine(_root, "environment-profiles.json");

    public EnvironmentProfileStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public string GetUserDataFolder(string environmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(environmentId)));
        return Path.Combine(_root, "Profiles", hash);
    }

    public void RegisterEnvironments(IEnumerable<EnvironmentConfig> environments)
    {
        lock (_registrationGate)
        {
            var snapshot = environments.Select(environment => environment.Clone()).ToArray();
            if (_pendingRegistration is null && snapshot.Length > 0 &&
                snapshot.All(environment => _registeredEnvironmentIds.Contains(environment.Id)))
            {
                return;
            }
            _pendingRegistration ??= snapshot;
            var registeredSnapshot = _pendingRegistration;
            RegisterSnapshot(_pendingRegistration);
            _pendingRegistration = null;
            if (!ReferenceEquals(registeredSnapshot, snapshot))
            {
                RegisterSnapshot(snapshot);
            }
            _registeredEnvironmentIds.UnionWith(snapshot.Select(environment => environment.Id));
        }
    }

    private void RegisterSnapshot(IEnumerable<EnvironmentConfig> environments)
    {
        using var ownership = AcquireOwnership();
        var firstMigration = !File.Exists(RegistryPath);
        var registry = ReadRegistry();
        var changed = firstMigration;
        foreach (var environment in environments)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(environment.Id);
            if (registry.ContainsKey(environment.Id))
            {
                continue;
            }

            var legacyFolder = BuildLegacyFolderName(environment.Name);
            registry.Add(environment.Id, new ProfileRegistration
            {
                LegacyFolder = firstMigration && Directory.Exists(Path.Combine(_root, legacyFolder))
                    ? legacyFolder
                    : null,
            });
            changed = true;
        }

        var legacyOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var registration in registry.Values)
        {
            if (registration.LegacyFolder is { } legacyFolder && !legacyOwners.Add(legacyFolder))
            {
                throw new IOException("Multiple environments claim the same legacy WebView2 profile.");
            }
        }

        if (changed)
        {
            SaveRegistry(registry);
        }
    }

    public string PrepareUserDataFolder(string environmentId)
    {
        var target = GetUserDataFolder(environmentId);
        using var ownership = AcquireOwnership();
        var registry = ReadRegistry();
        var registration = GetRegistration(registry, environmentId);
        if (registration.ClearPending)
        {
            CompleteClear(registry, registration, target);
        }
        if (registration.HasStableProfile)
        {
            if (Directory.Exists(target))
            {
                RequireOwner(target, environmentId, allowUnclaimed: false);
            }
            else
            {
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, OwnerFileName), environmentId);
            }
            return target;
        }

        var legacy = GetLegacyFolder(registration);
        if (Directory.Exists(target))
        {
            RequireOwner(target, environmentId, allowUnclaimed: false);
            if (legacy is not null && Directory.Exists(legacy))
            {
                throw new IOException("Both the legacy and stable WebView2 profiles exist; migration cannot choose a session.");
            }
        }
        else if (legacy is not null && Directory.Exists(legacy))
        {
            RequireOwner(legacy, environmentId, allowUnclaimed: true);
            File.WriteAllText(Path.Combine(legacy, OwnerFileName), environmentId);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(legacy, target);
        }
        else
        {
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, OwnerFileName), environmentId);
        }

        registration.HasStableProfile = true;
        registration.LegacyFolder = null;
        SaveRegistry(registry);
        return target;
    }

    public void DeleteUserDataFolder(string environmentId)
    {
        var target = GetUserDataFolder(environmentId);
        using var ownership = AcquireOwnership();
        var registry = ReadRegistry();
        var registration = GetRegistration(registry, environmentId);
        var legacy = GetLegacyFolder(registration);
        if (!registration.ClearPending && Directory.Exists(target))
        {
            RequireOwner(target, environmentId, allowUnclaimed: false);
        }
        if (!registration.ClearPending && legacy is not null && Directory.Exists(legacy))
        {
            RequireOwner(legacy, environmentId, allowUnclaimed: true);
        }

        registration.ClearPending = true;
        SaveRegistry(registry);
        CompleteClear(registry, registration, target);
    }

    private void CompleteClear(Dictionary<string, ProfileRegistration> registry, ProfileRegistration registration, string target)
    {
        var legacy = GetLegacyFolder(registration);
        if (legacy is not null && Directory.Exists(legacy))
        {
            Directory.Delete(legacy, recursive: true);
        }
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        registration.HasStableProfile = true;
        registration.LegacyFolder = null;
        registration.ClearPending = false;
        SaveRegistry(registry);
    }

    private FileStream AcquireOwnership()
    {
        Directory.CreateDirectory(_root);
        return File.Open(Path.Combine(_root, "environment-profiles.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private Dictionary<string, ProfileRegistration> ReadRegistry()
    {
        return File.Exists(RegistryPath)
            ? JsonSerializer.Deserialize<Dictionary<string, ProfileRegistration>>(File.ReadAllText(RegistryPath))
                ?? throw new InvalidDataException("The WebView2 profile registry is invalid.")
            : new Dictionary<string, ProfileRegistration>(StringComparer.Ordinal);
    }

    private void SaveRegistry(Dictionary<string, ProfileRegistration> registry)
    {
        AtomicFileWriter.WriteAllText(RegistryPath, JsonSerializer.Serialize(registry));
    }

    private static ProfileRegistration GetRegistration(Dictionary<string, ProfileRegistration> registry, string environmentId)
    {
        if (!registry.TryGetValue(environmentId, out var registration))
        {
            throw new InvalidOperationException("The environment has not been registered for WebView2 profile isolation.");
        }

        return registration;
    }

    private string? GetLegacyFolder(ProfileRegistration registration)
    {
        if (registration.LegacyFolder is not { } folder)
        {
            return null;
        }
        if (Path.GetFileName(folder) != folder || folder is "." or ".." || Path.IsPathRooted(folder))
        {
            throw new InvalidDataException("The legacy WebView2 profile path is invalid.");
        }

        return Path.Combine(_root, folder);
    }

    private static void RequireOwner(string folder, string environmentId, bool allowUnclaimed)
    {
        var ownerFile = Path.Combine(folder, OwnerFileName);
        if (File.Exists(ownerFile))
        {
            if (File.ReadAllText(ownerFile) == environmentId)
            {
                return;
            }
        }
        else if (allowUnclaimed)
        {
            return;
        }

        throw new IOException("The WebView2 profile folder belongs to an unknown or different environment.");
    }

    private static string BuildLegacyFolderName(string environmentName)
    {
        var normalized = string.IsNullOrWhiteSpace(environmentName) ? "default" : environmentName.Trim();
        var sanitized = new string(normalized
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "default";
        }

        sanitized = sanitized.Length > 48 ? sanitized[..48] : sanitized;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..8];
        return $"{sanitized}_{hash}";
    }

    private sealed class ProfileRegistration
    {
        public string? LegacyFolder { get; set; }
        public bool HasStableProfile { get; set; }
        public bool ClearPending { get; set; }
    }
}
