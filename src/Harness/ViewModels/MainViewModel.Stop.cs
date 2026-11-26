// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Models;
using Harness.Services;

namespace Harness.ViewModels;

public partial class MainViewModel
{
    private bool _canStopHostedRun;
    private int _hostedStopPageVersion;

    private bool CanStopHostedRun => _canStopHostedRun &&
        (_selectedEnvironment?.Backend is HostedBackendKind.OpenClaw or HostedBackendKind.Hermes or HostedBackendKind.Auto) &&
        _messageOwnership.IsCurrentAcceptedPageVersion(_hostedStopPageVersion);

    private void ApplyHostedStopAvailability(ControlUiProbeSnapshot snapshot)
    {
        var supported = _selectedEnvironment?.Backend is
            HostedBackendKind.OpenClaw or HostedBackendKind.Hermes or HostedBackendKind.Auto;
        var previousPageVersion = _hostedStopPageVersion;
        _hostedStopPageVersion = _messageOwnership.CaptureAcceptedPageVersion();
        SetHostedStopAvailability(supported && snapshot.Phase == ControlUiPhase.Connected &&
            snapshot.IsBusy && snapshot.CanAbortRun);
        if (previousPageVersion != _hostedStopPageVersion)
        {
            OnPropertyChanged(nameof(CanStop));
        }
    }

    private void ResetHostedStopAvailability()
    {
        _hostedStopPageVersion = 0;
        _canStopHostedRun = false;
        OnPropertyChanged(nameof(CanStop));
    }

    private void SetHostedStopAvailability(bool available)
    {
        if (_canStopHostedRun == available)
        {
            return;
        }

        _canStopHostedRun = available;
        OnPropertyChanged(nameof(CanStop));
    }
}
