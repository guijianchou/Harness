// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.OpenClaw;

/// <summary>Signs the UTF-8 v3 device-auth payload using the installation's Ed25519 key.</summary>
public interface IOpenClawDeviceIdentity
{
    string DeviceId { get; }
    string PublicKeyBase64Url { get; }
    ValueTask<string> SignAsync(string payload, CancellationToken cancellationToken);
}

/// <summary>Secrets belong to the caller's credential store, never to application logs.</summary>
public sealed class OpenClawConnectionOptions
{
    public required Uri Endpoint { get; init; }
    public string? Token { get; init; }
    public string? Password { get; init; }
    public string? DeviceToken { get; init; }
    public required IOpenClawDeviceIdentity DeviceIdentity { get; init; }
    public string ClientVersion { get; init; } = "5.3.9";
    public string Platform { get; init; } = "windows";
    public string DeviceFamily { get; init; } = "desktop";
    public string? Origin { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = ["operator.read", "operator.write"];
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public OpenClawAccessCredentials? CloudflareAccess { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public int MaximumPayloadBytes { get; init; } = 8 * 1024 * 1024;

    public override string ToString() => nameof(OpenClawConnectionOptions);
}

public sealed class OpenClawAccessCredentials
{
    /// <summary>The exact HTTPS origin authorized to receive these Access headers.</summary>
    public required Uri Origin { get; init; }
    public string? AccessToken { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public override string ToString() => nameof(OpenClawAccessCredentials);
}

public enum OpenClawFailureKind
{
    Transport,
    Protocol,
    UnsupportedProtocol,
    Rejected,
    Timeout,
    Disconnected,
    PayloadTooLarge,
    EventGap,
    EdgeAuthenticationRequired,
}

/// <summary>Only bounded protocol identifiers are retained; remote error text can contain secrets.</summary>
public sealed class OpenClawGatewayException : Exception
{
    public OpenClawGatewayException(OpenClawFailureKind kind, string? code = null,
        string? detailCode = null, string? reason = null, bool retryable = false, string? pairingRequestId = null)
        : base($"OpenClaw Gateway: {kind}.")
    {
        Kind = kind;
        Code = SafeIdentifier(code);
        DetailCode = SafeIdentifier(detailCode);
        Reason = SafeIdentifier(reason);
        Retryable = retryable;
        PairingRequestId = SafeIdentifier(pairingRequestId);
    }

    public OpenClawFailureKind Kind { get; }
    public string? Code { get; }
    public string? DetailCode { get; }
    public string? Reason { get; }
    public bool Retryable { get; }
    public string? PairingRequestId { get; }

    private static string? SafeIdentifier(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.') ? value : null;
}

public sealed record OpenClawDisconnectInfo(long Generation, OpenClawFailureKind Reason);
