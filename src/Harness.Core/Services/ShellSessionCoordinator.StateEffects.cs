// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Models;

namespace Harness.Services;

public sealed partial class ShellSessionCoordinator
{
    private bool _reportedQuietBusySession;

    private void UpdateSnapshotActivity()
    {
        _lastTransportActivityAt = DateTimeOffset.Now;
        _lastEventAt = _lastTransportActivityAt;
    }

    private void ApplySnapshotHealth(ControlUiProbeSnapshot snapshot)
    {
        _hostedUiHealth = MapHostedUiHealth(snapshot);
        _sessionHealth = MapSessionHealth(snapshot);
        _streamHealth = MapStreamHealth(snapshot);
    }

    private void LogHostedUiState(ControlUiProbeSnapshot snapshot)
    {
        _logger.Info("hosted_ui.state", new
        {
            phase = snapshot.Phase,
            summary = snapshot.Summary,
            shellDetected = snapshot.ShellDetected,
            currentModel = string.IsNullOrWhiteSpace(snapshot.CurrentModel) ? null : snapshot.CurrentModel,
            modelSource = string.IsNullOrWhiteSpace(snapshot.ModelSource) ? null : snapshot.ModelSource
        });
    }

    private void ApplyHostedUiRecoveryState(ControlUiProbeSnapshot snapshot)
    {
        ObserveQuietBusySession(snapshot);

        switch (snapshot.Phase)
        {
            case ControlUiPhase.Connected when _recoveryState is RecoveryState.Connecting or RecoveryState.Reconnecting or RecoveryState.Resyncing:
                MarkRecoveryReady();
                ResetEscalationCounters();
                break;
            case ControlUiPhase.AuthRequired:
            case ControlUiPhase.PairingRequired:
            case ControlUiPhase.OriginRejected:
                MarkRecoveryAuthIssue(snapshot.DetailOrSummary);
                break;
            case ControlUiPhase.GatewayError:
            case ControlUiPhase.Unavailable:
                MarkRecoveryDegraded(snapshot.DetailOrSummary);
                break;
        }
    }

    private void ObserveQuietBusySession(ControlUiProbeSnapshot snapshot)
    {
        var quietBusySession = snapshot.Phase == ControlUiPhase.Connected && snapshot.IsBusyStale;
        if (!quietBusySession)
        {
            _reportedQuietBusySession = false;
            return;
        }
        if (_reportedQuietBusySession)
        {
            return;
        }

        // A model thinking or a tool running can leave the DOM unchanged for minutes.
        // Silence alone is not a failed connection and must not resync or reload the page.
        _reportedQuietBusySession = true;
        _logger.Info("stream.busy_quiet.observed", new { snapshot.BusyStaleSeconds, snapshot.WorkState });
    }

    private void LogIgnoredGap(EventGapEventArgs args)
    {
        _logger.Info("stream.gap.ignored", new
        {
            reason = "background",
            expectedSeq = args.ExpectedSeq,
            gotSeq = args.GotSeq
        });
    }

    private void ApplyDetectedGap(EventGapEventArgs args)
    {
        _recentGapCount++;
        _lastEventSeq = args.GotSeq;
        _lastStateVersion = args.CurrentStateVersion;
        _streamHealth = HealthStatus.Degraded;

        _logger.Warning("stream.gap.detected", new
        {
            expectedSeq = args.ExpectedSeq,
            gotSeq = args.GotSeq,
            gapSize = args.GotSeq - args.ExpectedSeq
        });
    }
}
