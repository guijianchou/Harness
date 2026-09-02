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
    /// probe the environment URL itself. Backend-specific paths are attempted
    /// first and fall back to the root when the origin answers 404/405.
    /// </summary>
    public string? LatencyProbePath { get; init; }

    /// <summary>
    /// Gets the custom element name carrying hosted app state, or null when the
    /// backend exposes none and only generic DOM heuristics apply.
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

    private static readonly HostedBackendProfile AutoProfile = OpenClawProfile with
    {
        Kind = HostedBackendKind.Auto,
    };

    private static readonly HostedBackendProfile GenericProfile = new()
    {
        Kind = HostedBackendKind.Generic,
        LatencyProbePath = null,
        AppStateElement = null,
        HostCommandEventPrefix = null,
        CommandGlobals = ["__APP__", "app"],
    };

    /// <summary>
    /// Resolves the profile for a backend kind.
    /// </summary>
    public static HostedBackendProfile For(HostedBackendKind kind) => kind switch
    {
        HostedBackendKind.OpenClaw => OpenClawProfile,
        HostedBackendKind.Generic => GenericProfile,
        _ => AutoProfile,
    };

    /// <summary>
    /// Gets whether a failed backend-specific latency probe should retry against
    /// the environment root. Only <see cref="HostedBackendKind.Auto"/> does, so a
    /// misconfigured OpenClaw environment still surfaces its missing probe path
    /// instead of silently reporting the web server's latency.
    /// </summary>
    public bool AllowsRootLatencyFallback => Kind == HostedBackendKind.Auto;
}
