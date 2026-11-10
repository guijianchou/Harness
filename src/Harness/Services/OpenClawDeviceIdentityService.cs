// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Security.Cryptography;
using System.Text;
using Harness.OpenClaw;
using NSec.Cryptography;

namespace Harness.Services;

public sealed class OpenClawDeviceIdentityService(OpenClawCredentialStore? store = null)
{
    private readonly OpenClawCredentialStore _store = store ?? new();

    public async Task<OpenClawDeviceIdentity> GetOrCreateAsync(string environmentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);
        using var identityLock = await _store.AcquireIdentityLockAsync(environmentId, cancellationToken);
        var keyName = "identity:" + environmentId;
        var privateBytes = await _store.ReadProtectedAsync(keyName, cancellationToken);
        Key key;
        if (privateBytes is null)
        {
            key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
            privateBytes = key.Export(KeyBlobFormat.RawPrivateKey);
            try { await _store.WriteProtectedAsync(keyName, privateBytes, cancellationToken); }
            catch { key.Dispose(); throw; }
            finally { CryptographicOperations.ZeroMemory(privateBytes); }
        }
        else
        {
            try { key = Key.Import(SignatureAlgorithm.Ed25519, privateBytes, KeyBlobFormat.RawPrivateKey); }
            finally { CryptographicOperations.ZeroMemory(privateBytes); }
        }

        return new OpenClawDeviceIdentity(key);
    }
}

public sealed class OpenClawDeviceIdentity : IOpenClawDeviceIdentity, IDisposable
{
    private readonly Key _key;

    internal OpenClawDeviceIdentity(Key key)
    {
        _key = key;
        var publicBytes = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        DeviceId = Convert.ToHexString(SHA256.HashData(publicBytes)).ToLowerInvariant();
        PublicKeyBase64Url = Base64Url(publicBytes);
    }

    public string DeviceId { get; }

    public string PublicKeyBase64Url { get; }

    public ValueTask<string> SignAsync(string payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Base64Url(SignatureAlgorithm.Ed25519.Sign(_key, Encoding.UTF8.GetBytes(payload))));
    }

    public void Dispose() => _key.Dispose();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
