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
    /// Detect a supported agent from explicit page markers. Until identified,
    /// use ordinary Web behavior and probe only the configured page URL.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// OpenClaw Gateway. Enables the <c>__openclaw__/a2ui/</c> latency probe,
    /// <c>openclaw-app</c> DOM state reads, and the <c>openclaw:</c> host-command
    /// event contract.
    /// </summary>
    OpenClaw = 1,

    /// <summary>
    /// Legacy persisted value. Settings migrate it to <see cref="Generic"/>.
    /// Its numeric value must not be reused for a different product.
    /// </summary>
    DeepSeek = 2,

    /// <summary>
    /// An ordinary Web application. Probe the configured page URL without
    /// agent-specific state detection or page commands.
    /// </summary>
    Generic = 3,

    /// <summary>
    /// Hermes Agent Web UI. Enables the Hermes page adapter while retaining
    /// the configured page URL for transport probes.
    /// </summary>
    Hermes = 4,
}
