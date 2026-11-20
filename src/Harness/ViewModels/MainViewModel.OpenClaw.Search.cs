// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Services;

namespace Harness.ViewModels;

public partial class MainViewModel
{
    private CancellationTokenSource? _gatewaySearchCancellation;
    private Task _gatewaySearchTask = Task.CompletedTask;
    private bool _gatewayRosterLoading;
    private bool _gatewayRosterLoaded;

    public bool GatewaySessionsLoading => _gatewayRosterLoading || _gatewaySearchCancellation is not null;
    public bool GatewayRosterLoaded => _gatewayRosterLoaded;
    public bool CanSelectGatewaySession => !GatewayBusy && !GatewaySessionsLoading && _gatewayRosterLoaded && _gatewayRuntime?.Client?.IsConnected == true &&
        _gatewayRosterSearchVersion == _gatewaySearchVersion;

    private void NotifyGatewayRosterState()
    {
        OnPropertyChanged(nameof(GatewaySessionsLoading));
        OnPropertyChanged(nameof(GatewayRosterLoaded));
        OnPropertyChanged(nameof(CanSelectGatewaySession));
    }

    private void ScheduleGatewaySearch()
    {
        _gatewaySearchCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_gatewayCancellation?.Token ?? _lifetimeCts.Token);
        _gatewaySearchCancellation = cancellation;
        NotifyGatewayRosterState();
        _gatewaySearchTask = RefreshGatewaySearchAsync(_gatewayRuntime, _gatewaySearchVersion, cancellation);
    }

    private async Task RefreshGatewaySearchAsync(OpenClawSessionRuntime? runtime, long version, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(300, cancellation.Token);
            if (runtime is null || !IsCurrentGateway(runtime) || runtime.Client?.IsConnected != true ||
                version != _gatewaySearchVersion || (_gatewayRosterLoaded && _gatewayRosterSearchVersion == version))
            {
                return;
            }

            Interlocked.And(ref _gatewayRefreshRequested, ~(int)GatewayRefreshScope.Roster);
            await RefreshGatewayDataAsync(runtime, GatewayRefreshScope.Roster, cancellation.Token, waitForGate: true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (runtime is not null && IsCurrentGateway(runtime) && version == _gatewaySearchVersion)
            {
                ShowGatewayError(ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_gatewaySearchCancellation, cancellation))
            {
                _gatewaySearchCancellation = null;
                if (!_isDisposed)
                {
                    NotifyGatewayRosterState();
                }
            }
            cancellation.Dispose();
        }
    }
}
