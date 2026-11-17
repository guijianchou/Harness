// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Models;

namespace Harness.Services;

public sealed partial class ShellSessionCoordinator
{
    private CancellationTokenSource? _foregroundResumeCancellation;

    /// <summary>
    /// Called when the host window goes to background.
    /// </summary>
    public void OnHostHidden()
    {
        _isInBackground = true;
        _hiddenAt = DateTimeOffset.Now;
        CancelForegroundResume();
        _logger.Info("host.hidden", new { environment = _currentEnvironmentName, at = _hiddenAt });
        PublishTelemetry();
    }

    /// <summary>
    /// Called when the host window returns to foreground.
    /// </summary>
    public async Task OnHostVisibleAsync(CancellationToken cancellationToken = default)
    {
        if (!_isInBackground)
        {
            return;
        }

        var operationCancellation = CreateObservedOperationCancellation();
        if (operationCancellation is null)
        {
            return;
        }

        lock (_observedOperationGate)
        {
            _foregroundResumeCancellation = operationCancellation;
        }

        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                operationCancellation.Token);

            await OnHostVisibleCoreAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.Info("recovery.background_resume.cancelled");
        }
        finally
        {
            lock (_observedOperationGate)
            {
                if (ReferenceEquals(_foregroundResumeCancellation, operationCancellation))
                {
                    _foregroundResumeCancellation = null;
                }
            }

            ReleaseObservedOperationCancellation(operationCancellation);
        }
    }

    private void CancelForegroundResume()
    {
        CancellationTokenSource? cancellation;
        lock (_observedOperationGate)
        {
            cancellation = _foregroundResumeCancellation;
            _foregroundResumeCancellation = null;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The foreground operation owns disposal and may already have completed.
        }
    }

    private async Task OnHostVisibleCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isInBackground)
        {
            return;
        }

        _isInBackground = false;
        var visibleAt = DateTimeOffset.Now;

        if (_hiddenAt.HasValue)
        {
            _backgroundDuration = visibleAt - _hiddenAt.Value;
            _logger.Info("host.visible", new
            {
                environment = _currentEnvironmentName,
                hiddenAt = _hiddenAt,
                visibleAt,
                durationSeconds = _backgroundDuration.Value.TotalSeconds
            });
        }

        _hiddenAt = null;

        if (AutoRefreshEnabled && _recoveryOptions.EnableBackgroundResume &&
            _backgroundDuration.HasValue &&
            _backgroundDuration.Value.TotalSeconds >= _recoveryOptions.BackgroundResumeThresholdSeconds)
        {
            var requiresReconnect = await RequiresBackgroundReconnectAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _logger.Info("recovery.start", new
            {
                reason = "background_resume",
                durationSeconds = _backgroundDuration.Value.TotalSeconds,
                requiresReconnect
            });

            if (requiresReconnect)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RequestReconnectAsync("Background resume threshold exceeded", cancellationToken);
            }
            else
            {
                _logger.Info("recovery.skipped", new { reason = "background_resume_session_still_healthy" });
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        _backgroundDuration = null;
        PublishTelemetry();
    }

    /// <summary>
    /// Sets the current environment context.
    /// </summary>
    public void SetEnvironment(string environmentName, string gatewayUrl)
    {
        _reportedQuietBusySession = false;
        _currentEnvironmentName = environmentName;
        _currentGatewayUrl = gatewayUrl;
        _logger.Info("environment.changed", new { environmentName, gatewayUrl });
    }

    /// <summary>
    /// Applies the automatic recovery preference at runtime.
    /// </summary>
    public void SetAutoRefresh(bool enabled)
    {
        if (_isDisposed || _autoRefresh == enabled)
        {
            return;
        }

        _autoRefresh = enabled;
        if (!enabled)
        {
            CancelObservedOperations();
            AbortRecoveryOperation();

            if (_recoveryState is RecoveryState.Reconnecting or RecoveryState.Resyncing or RecoveryState.Refreshing)
            {
                MarkRecoveryDegraded("Automatic session recovery is disabled.");
            }
        }

        _logger.Info("recovery.auto_refresh.changed", new { enabled });
        PublishTelemetry();
    }

    /// <summary>
    /// Resets all recovery counters and state.
    /// </summary>
    public void Reset()
    {
        _reportedQuietBusySession = false;
        CancelObservedOperations();
        AbortRecoveryOperation();

        _reconnectAttempts = 0;
        _softResyncAttempts = 0;
        _hardRefreshAttempts = 0;
        _totalReconnectAttempts = 0;
        _totalSoftResyncAttempts = 0;
        _totalHardRefreshAttempts = 0;
        _recentGapCount = 0;
        _recoveryState = RecoveryState.Connecting;
        _lastRecoveryStartedAt = null;
        _lastSuccessfulRecoveryAt = null;
        _lastHardRefreshAt = null;
        _hiddenAt = null;
        _backgroundDuration = null;
        _isInBackground = false;
        _transportHealth = HealthStatus.Unknown;
        _sessionHealth = HealthStatus.Unknown;
        _streamHealth = HealthStatus.Unknown;
        _hostedUiHealth = HealthStatus.Unknown;
        _lastEventSeq = null;
        _lastStateVersion = null;
        _lastEventAt = null;
        _lastHeartbeatAt = null;
        _lastTransportActivityAt = null;
        _degradationReason = null;
        _lastRecoveryReason = null;

        _logger.Info("recovery.reset");
        RecoveryStateChanged?.Invoke(_recoveryState);
        PublishTelemetry();
    }
}
