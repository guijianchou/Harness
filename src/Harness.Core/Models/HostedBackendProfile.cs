// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.Models;

/// <summary>
/// Per-backend behaviour for the latency probe and the injected bridge.
/// This is the single place that knows what is OpenClaw-specific, so adding a
/// backend does not mean adding branches across the shell.
/// </summary>
public sealed record HostedBackendProfile
{
    /// <summary>
    /// Gets the backend this profile describes.
    /// </summary>
    public required HostedBackendKind Kind { get; init; }

    /// <summary>
    /// Gets the Control UI relative path used for the latency probe, or null to
    /// probe the environment URL itself.
    /// </summary>
    public string? LatencyProbePath { get; init; }

    /// <summary>
    /// Gets the custom element name carrying hosted app state, or null when the
    /// backend exposes none.
    /// </summary>
    public string? AppStateElement { get; init; }

    /// <summary>
    /// Gets the CustomEvent name prefix for host→page commands, or null when the
    /// backend has no event contract. Dispatching a prefix the page does not
    /// listen for is a silent no-op, so this stays empty for unknown backends.
    /// </summary>
    public string? HostCommandEventPrefix { get; init; }

    /// <summary>
    /// Gets the window-scoped globals probed for a scriptable command API.
    /// </summary>
    public IReadOnlyList<string> CommandGlobals { get; init; } = [];

    private static readonly HostedBackendProfile OpenClawProfile = new()
    {
        Kind = HostedBackendKind.OpenClaw,
        LatencyProbePath = "__openclaw__/a2ui/",
        AppStateElement = "openclaw-app",
        HostCommandEventPrefix = "openclaw",
        CommandGlobals = ["__openclaw", "__OPENCLAW__", "__APP__", "app"],
    };

    private static readonly HostedBackendProfile HermesProfile = new()
    {
        Kind = HostedBackendKind.Hermes,
    };

    private static readonly HostedBackendProfile AutoProfile = new()
    {
        Kind = HostedBackendKind.Auto,
    };

    private static readonly HostedBackendProfile GenericProfile = new()
    {
        Kind = HostedBackendKind.Generic,
    };

    /// <summary>
    /// Resolves the profile for a backend kind.
    /// </summary>
    public static HostedBackendProfile For(HostedBackendKind kind) => kind switch
    {
        HostedBackendKind.OpenClaw => OpenClawProfile,
        HostedBackendKind.Hermes => HermesProfile,
        HostedBackendKind.Auto => AutoProfile,
        _ => GenericProfile,
    };

    /// <summary>
    /// Gets whether a failed backend-specific latency probe should retry against
    /// the environment URL. An automatic profile must first specify a dedicated
    /// path; the default automatic profile probes the page directly.
    /// </summary>
    public bool AllowsRootLatencyFallback => Kind == HostedBackendKind.Auto && !string.IsNullOrWhiteSpace(LatencyProbePath);
}
