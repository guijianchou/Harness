// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Helpers;
using Harness.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Harness.Views;

public sealed partial class SettingsDialog
{
    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_isClosed || _isSaving || _isClearingSession || (ViewModel.IsEditing && !TryApplyEdit()))
        {
            return;
        }

        _settingsSaveTask = SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        _isSaving = true;
        SettingsInteractionRoot.IsEnabled = false;
        var closeAfterSave = false;
        try
        {
            var result = await ViewModel.SaveAllAsync(new Services.OpenClawCredentialStore(), _startupRegistration);
            if (_isClosed)
            {
                return;
            }

            RefreshStartupRegistrationStatus();
            if (result is not { } saveResult)
            {
                ShowEnvironmentMessage(ValidationErrorTitle, InfoBarSeverity.Error);
                return;
            }
            try
            {
                if (saveResult.DidChangeLanguage)
                {
                    App.ApplyLanguage(App.Configuration.Settings.AppLanguage);
                }

                SettingsSaved?.Invoke(saveResult);
                closeAfterSave = true;
            }
            catch (Exception ex)
            {
                App.Logger.Warning($"Settings were saved but could not be applied: {ex.GetType().Name}");
                ShowEnvironmentMessage(StringResources.SettingsTitle, InfoBarSeverity.Warning, StringResources.SettingsApplyFailed);
            }
        }
        catch (Exception ex)
        {
            App.Logger.Warning($"Settings save failed: {ex.GetType().Name}");
            if (!_isClosed)
            {
                ShowEnvironmentMessage(ValidationErrorTitle, InfoBarSeverity.Error, StringResources.SettingsGatewaySecureSaveFailed);
            }
        }
        finally
        {
            _isSaving = false;
            if (!_isClosed)
            {
                SettingsInteractionRoot.IsEnabled = true;
            }
        }

        if (closeAfterSave && !_isClosed)
        {
            try
            {
                this.Close();
            }
            catch (Exception ex)
            {
                App.Logger.Warning($"Settings were saved but the window could not close: {ex.GetType().Name}");
            }
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (!_isSaving && !_isClosed)
        {
            this.Close();
        }
    }

    private void RefreshStartupRegistrationStatus()
    {
        if (!_isClosed)
        {
            _startupStatusRefreshTask = ObserveStartupRegistrationStatusAsync();
        }
    }

    private async Task ObserveStartupRegistrationStatusAsync()
    {
        try
        {
            await ViewModel.RefreshStartupRegistrationStatusAsync(_startupRegistration);
        }
        catch (Exception ex)
        {
            App.Logger.Warning($"Startup status refresh failed: {ex.GetType().Name}");
        }
    }

    private void OnRunDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        ExecuteDeveloperCommand(MainViewModel?.RunDiagnosticsCommand);
    }

    private void OnViewLogsClick(object sender, RoutedEventArgs e)
    {
        ExecuteDeveloperCommand(MainViewModel?.ViewLogsCommand);
    }

    private void OnDevToolsClick(object sender, RoutedEventArgs e)
    {
        ExecuteDeveloperCommand(MainViewModel?.DevToolsCommand);
    }

    private void OnExportDiagnosticBundleClick(object sender, RoutedEventArgs e)
    {
        ExecuteDeveloperCommand(MainViewModel?.ExportDiagnosticBundleCommand);
    }

    private void ExecuteDeveloperCommand(System.Windows.Input.ICommand? command)
    {
        if (!_isClosed && !_isSaving && !_isClearingSession && command?.CanExecute(null) == true)
        {
            if (App.MainWindow is MainWindow mainWindow)
            {
                mainWindow.ActivateFromExternalLaunch();
            }
            command.Execute(null);
        }
    }

    private void OnResetHotkeyClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetGlobalHotkey();
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (!TryApplyEdit())
        {
            return;
        }

        ShowEnvironmentMessage(StringResources.SettingsGatewayDraftApplied, InfoBarSeverity.Informational, StringResources.SettingsGatewayDraftHint);
    }

    private async void OnClearEnvironmentSessionClick(object sender, RoutedEventArgs e)
    {
        if (_isClosed)
        {
            return;
        }
        var environmentName = GetSessionButtonEnvironmentName(sender as Button);
        try
        {
            await ClearEnvironmentSessionAsync(sender as Button);
        }
        catch (Exception ex)
        {
            App.Logger.Warning($"Failed to clear environment session: {ex.Message}");
            if (!_isClosed)
            {
                ShowSessionMessage(
                    InfoBarSeverity.Error,
                    string.Format(StringResources.SettingsSessionResetFailedFormat, environmentName, ex.Message));
            }
        }
    }

    private void ShowEnvironmentMessage(string title, InfoBarSeverity severity, string? message = null)
    {
        ValidationInfoBar.Title = title;
        ValidationInfoBar.Severity = severity;
        ValidationInfoBar.Message = message ?? ViewModel.ValidationMessage;
        ValidationInfoBar.IsOpen = true;
    }

    private void ShowSessionMessage(InfoBarSeverity severity, string message)
    {
        SessionInfoBar.Title = StringResources.SettingsSessionReset;
        SessionInfoBar.Severity = severity;
        SessionInfoBar.Message = message;
        SessionInfoBar.IsOpen = true;
    }

    private async Task ClearEnvironmentSessionAsync(Button? button)
    {
        if (_isClosed || _isSaving || _isClearingSession)
        {
            return;
        }

        var environment = GetSavedSessionEnvironment(button);
        if (MainViewModel is null || environment is null)
        {
            ShowSessionMessage(InfoBarSeverity.Warning, StringResources.SettingsSessionResetSelectEnvironment);
            return;
        }

        _isClearingSession = true;
        SettingsInteractionRoot.IsEnabled = false;
        try
        {
            await MainViewModel.ClearSessionForEnvironmentAsync(environment.Id);
            if (!_isClosed)
            {
                ShowSessionMessage(
                    InfoBarSeverity.Informational,
                    string.Format(StringResources.SettingsSessionResetCompleted, environment.Name));
            }
        }
        finally
        {
            _isClearingSession = false;
            if (!_isClosed)
            {
                SettingsInteractionRoot.IsEnabled = true;
            }
        }
    }

    private EnvironmentConfig? GetSavedSessionEnvironment(Button? button)
    {
        return button?.Tag is string environmentId
            ? SavedSessionEnvironments.FirstOrDefault(environment => environment.Id == environmentId)
            : null;
    }

    private string GetSessionButtonEnvironmentName(Button? button)
    {
        return GetSavedSessionEnvironment(button)?.Name ?? StringResources.SettingsSessionReset;
    }

    private bool TryApplyEdit()
    {
        if (ViewModel.TryApplyEdit())
        {
            return true;
        }

        ShowEnvironmentMessage(ValidationErrorTitle, InfoBarSeverity.Error);
        NavList.SelectedItem = NavEnvironments;
        return false;
    }

}
