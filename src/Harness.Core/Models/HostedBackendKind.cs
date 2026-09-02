// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.Models;

/// <summary>
/// Identifies which hosted Control UI product an environment points at.
/// The value selects the latency probe path and the in-page bridge behaviour;
/// it does not change how the page itself is rendered.
/// </summary>
public enum HostedBackendKind
{
    /// <summary>
    /// Detect at runtime. Backend-specific probes and DOM readers are all
    /// attempted, and the latency probe falls back to the site root when the
    /// backend-specific path is absent. Correct default for an unknown backend.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// OpenClaw Gateway. Enables the <c>__openclaw__/a2ui/</c> latency probe,
    /// <c>openclaw-app</c> DOM state reads, and the <c>openclaw:</c> host-command
    /// event contract.
    /// </summary>
    OpenClaw = 1,

    /// <summary>
    /// A hosted Control UI with no Harness-specific integration contract.
    /// Latency probes the site root and the bridge uses generic DOM heuristics only.
    /// </summary>
    Generic = 2,
}
