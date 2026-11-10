// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Helpers;
using Harness.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Harness.Views;

public sealed partial class SettingsDialog
{
    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_isSaving || (ViewModel.IsEditing && !TryApplyEdit()))
        {
            return;
        }
        _isSaving = true;
        SettingsInteractionRoot.IsEnabled = false;
        try
        {
            var result = await ViewModel.SaveAllAsync(new Services.OpenClawCredentialStore());
            if (result is not { } saveResult)
            {
                ShowEnvironmentMessage(ValidationErrorTitle, InfoBarSeverity.Error);
                return;
            }
            if (saveResult.DidChangeLanguage) { App.ApplyLanguage(ViewModel.SelectedLanguage); }
            SettingsSaved?.Invoke(saveResult);
            this.Close();
        }
        catch (Exception ex)
        {
            App.Logger.Warning($"Settings save failed: {ex.GetType().Name}");
            ShowEnvironmentMessage(ValidationErrorTitle, InfoBarSeverity.Error, StringResources.SettingsGatewaySecureSaveFailed);
        }
        finally { _isSaving = false; SettingsInteractionRoot.IsEnabled = true; }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private void OnRunDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        MainViewModel?.RunDiagnosticsCommand.Execute(null);
        this.Close();
    }

    private void OnViewLogsClick(object sender, RoutedEventArgs e)
    {
        var mainViewModel = MainViewModel;
        this.Close();
        mainViewModel?.ViewLogsCommand.Execute(null);
    }

    private void OnDevToolsClick(object sender, RoutedEventArgs e)
    {
        MainViewModel?.DevToolsCommand.Execute(null);
        this.Close();
    }

    private void OnExportDiagnosticBundleClick(object sender, RoutedEventArgs e)
    {
        MainViewModel?.ExportDiagnosticBundleCommand.Execute(null);
        this.Close();
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
        try
        {
            await ClearEnvironmentSessionAsync(sender as Button);
        }
        catch (Exception ex)
        {
            var environmentName = GetSessionButtonEnvironmentName(sender as Button);
            App.Logger.Warning($"Failed to clear environment session: {ex.Message}");
            ShowSessionMessage(
                InfoBarSeverity.Error,
                string.Format(StringResources.SettingsSessionResetFailedFormat, environmentName, ex.Message));
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
        if (MainViewModel is null ||
            button?.Tag is not string environmentName ||
            string.IsNullOrWhiteSpace(environmentName))
        {
            ShowSessionMessage(InfoBarSeverity.Warning, StringResources.SettingsSessionResetSelectEnvironment);
            return;
        }

        button.IsEnabled = false;
        try
        {
            await MainViewModel.ClearSessionForEnvironmentAsync(environmentName);
            ShowSessionMessage(
                InfoBarSeverity.Informational,
                string.Format(StringResources.SettingsSessionResetCompleted, environmentName));
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private static string GetSessionButtonEnvironmentName(Button? button)
    {
        return button?.Tag is string environmentName && !string.IsNullOrWhiteSpace(environmentName)
            ? environmentName
            : StringResources.SettingsSessionReset;
    }

    private bool TryApplyEdit()
    {
        if (ViewModel.TryApplyEdit())
        {
            return true;
        }

        ShowEnvironmentMessage(ValidationErrorTitle, InfoBarSeverity.Error);
        return false;
    }

}
