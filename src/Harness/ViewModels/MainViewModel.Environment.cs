// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Helpers;
using Harness.Models;

namespace Harness.ViewModels;

public partial class MainViewModel
{
    private bool _isReloadingEnvironments;

    private void LoadEnvironments()
    {
        _isReloadingEnvironments = true;
        try
        {
            Environments.Clear();
            foreach (var environment in _runtime.Configuration.Settings.Environments)
            {
                Environments.Add(environment);
            }

            UpdateEnvironmentSelection(_runtime.Configuration.GetSelectedEnvironment(), persistSelection: false);
        }
        finally
        {
            _isReloadingEnvironments = false;
        }
    }

    private void UpdateEnvironmentSelection(EnvironmentConfig? environment, bool persistSelection)
    {
        var sameSession = environment is not null && _selectedEnvironment is not null &&
            string.Equals(environment.Name, _selectedEnvironment.Name, StringComparison.Ordinal) &&
            string.Equals(environment.GatewayUrl, _selectedEnvironment.GatewayUrl, StringComparison.Ordinal) &&
            environment.Backend == _selectedEnvironment.Backend;
        var backendChanged = environment?.Backend != _selectedEnvironment?.Backend;
        _selectedEnvironment = environment;
        OnPropertyChanged(nameof(SelectedEnvironment));
        OnPropertyChanged(nameof(CurrentUrl));
        OnPropertyChanged(nameof(SelectedEnvironmentName));
        OnPropertyChanged(nameof(IsPlaceholderEnvironment));
        SynchronizeGatewayEnvironment();

        if (sameSession)
        {
            return;
        }

        if (environment is null)
        {
            ApplyPlaceholderEnvironmentState();
            if (persistSelection)
            {
                _runtime.Configuration.Settings.SelectedEnvironmentName = null;
                _runtime.Configuration.SaveDeferred();
            }
            WebViewRecreationRequested?.Invoke("environment_placeholder_selected");
            return;
        }

        if (environment.IsPlaceholder)
        {
            ApplyPlaceholderEnvironmentState();
            if (persistSelection)
            {
                _runtime.Configuration.Settings.SelectedEnvironmentName = environment.Name;
                _runtime.Configuration.SaveDeferred();
            }

            WebViewRecreationRequested?.Invoke("environment_placeholder_selected");

            return;
        }

        var shouldCreateWebViewHost = !_webViewService.IsInitialized;

        ResetTelemetry();
        _coordinator?.Reset();
        _coordinator?.SetEnvironment(environment.Name, environment.GatewayUrl);
        UpdateStatusPresentation();
        RefreshResourceScheduling();

        if (persistSelection)
        {
            _runtime.Configuration.Settings.SelectedEnvironmentName = environment.Name;
            _runtime.Configuration.SaveDeferred();
        }

        if (shouldCreateWebViewHost)
        {
            WebViewRecreationRequested?.Invoke("environment_placeholder_replaced");
            return;
        }

        if (!backendChanged && _webViewService.IsUsingEnvironmentProfile(environment.Name))
        {
            _webViewService.Navigate(environment.GatewayUrl);
        }
        else
        {
            WebViewRecreationRequested?.Invoke("environment_profile_changed");
        }
    }

    private void ApplyPlaceholderEnvironmentState()
    {
        ResetTelemetry();
        _coordinator?.Reset();
        _webViewService.StopHeartbeat();
        _latencyService.Stop();
        ResetResourceProbeProjection();
        ApplyConnectionState(Harness.Services.ConnectionState.Offline);
        ApplyRecoveryState(RecoveryState.Healthy);
        StatusMessage = StringResources.StatusConfigureGateway;
        StatusIndicatorBrush = WarningBrush;
        IsErrorVisible = false;
        ShowRetryButton = false;
    }

    public async Task ClearSessionForEnvironmentAsync(string environmentName)
    {
        if (string.IsNullOrWhiteSpace(environmentName))
        {
            return;
        }

        await _webViewService.ClearEnvironmentSessionAsync(environmentName);

        if (string.Equals(_selectedEnvironment?.Name, environmentName, StringComparison.Ordinal))
        {
            DismissError();
            DismissDiagnostics();
            WebViewRecreationRequested?.Invoke("active_session_cleared");
        }
    }
}
