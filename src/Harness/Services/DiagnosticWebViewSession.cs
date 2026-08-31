// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.Services;

public interface IDiagnosticWebViewSession
{
    bool IsInitialized { get; }

    ControlUiProbeSnapshot LatestControlUiSnapshot { get; }

    int TotalControlUiInspectionRequests { get; }

    int CachedControlUiInspectionRequests { get; }

    int CoalescedControlUiInspectionRequests { get; }

    int HeartbeatRecoveryRequests { get; }

    Task<ControlUiProbeSnapshot> InspectControlUiStateAsync(
        CancellationToken cancellationToken = default,
        bool publishSnapshot = true);
}
