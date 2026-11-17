// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Diagnostics;
using Harness.Models;
using Harness.Services;
using Harness.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Harness;

public sealed partial class MainWindow
{
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        ShowSettingsWindow();
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        ToggleAlwaysOnTop();
    }

    private void OnOpenSettingsRequested()
    {
        ShowSettingsWindow(showEnvironment: true);
    }

    private void OnConfigureGatewayClick(object sender, RoutedEventArgs e)
    {
        ShowSettingsWindow(showEnvironment: true);
    }

    private void ShowSettingsWindow(bool showEnvironment = false)
    {
        var startedAt = Stopwatch.GetTimestamp();
        if (_settingsWindow != null)
        {
            ActivateSettingsWindow(startedAt, showEnvironment);
            return;
        }

        _settingsWindow = CreateSettingsWindow();
        ActivateSettingsWindow(startedAt, showEnvironment);
    }

    private void PrewarmSettingsWindow()
    {
        if (_isClosing || _settingsWindow is not null)
        {
            return;
        }

        _settingsWindow = CreateSettingsWindow();
    }

    private void QueueSettingsWindowPrewarm()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => PrewarmSettingsWindow());
    }

    private void OnSettingsSaved(SettingsSaveResult saveResult)
    {
        ApplyCommittedSettings();
    }

    private void InitializeSettingsSynchronization()
    {
        _appliedSettings = App.Configuration.CreateSnapshot();
        App.Configuration.SettingsCommitted += OnSettingsCommitted;
    }

    private void OnSettingsCommitted()
    {
        if (_isClosing || Interlocked.Exchange(ref _settingsApplyQueued, 1) != 0)
        {
            return;
        }
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _settingsApplyQueued, 0);
            if (_isClosing)
            {
                return;
            }
            try
            {
                ApplyCommittedSettings();
            }
            catch (Exception ex)
            {
                App.Logger.Error($"Failed to apply committed settings: {ex.Message}");
            }
        }))
        {
            Interlocked.Exchange(ref _settingsApplyQueued, 0);
            App.Logger.Warning("Could not enqueue committed settings for the main window.");
        }
    }

    private void ApplyCommittedSettings()
    {
        if (_isClosing || _appliedSettings is null)
        {
            return;
        }
        // Read the latest state on the UI thread; queued older saves cannot undo a newer edit.
        var current = App.Configuration.CreateSnapshot();
        var change = SettingsSaveResult.Between(_appliedSettings, current);
        HandleSettingsSaved(change);
        _appliedSettings = current;
    }

    private void ApplyAutoRefresh(bool enabled)
    {
        ViewModel.SetAutoRefresh(enabled);

        if (!enabled)
        {
            CancelAutomaticWebViewRecovery();
        }
    }

    private void CancelAutomaticWebViewRecovery()
    {
        if (!_webViewRecreationService.TryCancelAutomaticRecovery(out var cancelled))
        {
            return;
        }

        if (cancelled.CancelledPending && _webViewRecreationTimer.IsRunning)
        {
            _webViewRecreationTimer.Stop();
        }

        RecordInstrumentationEvent("webview.recreation.cancelled_auto_refresh_disabled", new
        {
            pendingReason = cancelled.PendingReason,
            deferredReason = cancelled.DeferredReason,
            activeReason = cancelled.ActiveReason
        });
    }

    private SettingsDialog CreateSettingsWindow()
    {
        var startedAt = Stopwatch.GetTimestamp();
        var settingsWindow = new SettingsDialog(new SettingsPersistenceAdapter(App.Configuration, App.Logger))
        {
            MainViewModel = this.ViewModel,
        };

        settingsWindow.SettingsSaved += OnSettingsSaved;
        settingsWindow.Closed += OnSettingsWindowClosed;
        App.Logger.Info("settings.window.created", new
        {
            elapsedMs = Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, 2),
        });
        return settingsWindow;
    }

    private void ActivateSettingsWindow(long startedAt, bool showEnvironment)
    {
        if (_settingsWindow is null)
        {
            return;
        }

        var preparationStartedAt = Stopwatch.GetTimestamp();
        var reloadDraft = !_isSettingsWindowVisible;
        if (reloadDraft)
        {
            _settingsWindow.BeginOpenDiagnostics(startedAt);
            _settingsWindow.ReloadFromCurrentSettings();
        }

        if (showEnvironment)
        {
            _settingsWindow.ShowEnvironmentSettings(reloadDraft ? ViewModel.SelectedEnvironment?.Id : null);
        }

        _settingsWindow.SyncWithCurrentSettings();
        var preparationMs = Stopwatch.GetElapsedTime(preparationStartedAt).TotalMilliseconds;
        var activationStartedAt = Stopwatch.GetTimestamp();
        _settingsWindow.Activate();
        var activateCallMs = Stopwatch.GetElapsedTime(activationStartedAt).TotalMilliseconds;
        _isSettingsWindowVisible = true;
        App.Logger.Info("settings.window.open", new
        {
            reloadDraft,
            preparationMs = Math.Round(preparationMs, 2),
            activateCallMs = Math.Round(activateCallMs, 2),
            requestToActivateReturnMs = Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, 2),
        });
    }

    private void OnSettingsWindowClosed(object sender, WindowEventArgs args)
    {
        if (_settingsWindow is null)
        {
            return;
        }

        _settingsWindow.SettingsSaved -= OnSettingsSaved;
        _settingsWindow.Closed -= OnSettingsWindowClosed;
        _settingsWindow = null;
        _isSettingsWindowVisible = false;
        QueueSettingsWindowPrewarm();
    }

    private void CloseSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            return;
        }

        var settingsWindow = _settingsWindow;
        _settingsWindow = null;
        _isSettingsWindowVisible = false;
        settingsWindow.SettingsSaved -= OnSettingsSaved;
        settingsWindow.Closed -= OnSettingsWindowClosed;
        settingsWindow.Close();
    }

    private void HandleSettingsSaved(SettingsSaveResult saveResult)
    {
        if (saveResult.DidChangeEnvironmentState)
        {
            ViewModel.RefreshEnvironments();
        }

        if (saveResult.DidChangeLiveShellOptions)
        {
            _liveShellSettingsApplier.Apply(saveResult.LiveShellSettingsChange);
            if (saveResult.LiveShellSettingsChange.DidChangeAppTheme)
            {
                var theme = App.Configuration.Settings.AppTheme;
                ApplyTheme(theme);
                UpdateThemeSelector(theme);
            }
            if (saveResult.LiveShellSettingsChange.DidChangeZoomFactor)
            {
                ViewModel.SetContentZoom(App.Configuration.Settings.ZoomFactor);
            }
        }
    }

    private void OnError(string message)
    {
        App.Logger.Error($"UI error displayed: {message}");
    }

    private void OnNotificationRequested(NativeNotificationRequest request)
    {
        if (_trayIconService?.IsAvailable != true)
        {
            return;
        }

        var settings = App.Configuration.Settings;
        if (request.Kind == NativeNotificationKind.RunCompleted)
        {
            if (!settings.NotifyOnRunCompleted)
            {
                return;
            }

            if (settings.NotifyOnlyWhenUnfocused && ViewModel.IsWindowFocused)
            {
                return;
            }
        }
        else if (request.Kind == NativeNotificationKind.PageForwarded && !settings.ForwardPageNotifications)
        {
            return;
        }

        _trayIconService.ShowBalloon(request.Title, request.Body);
    }

    private void OnWebViewRecreationRequested(string reason)
    {
        ScheduleWebViewRecreation(reason);
    }

    private void OnNavigationTimeoutRecoveryNoLongerNeeded()
    {
        if (!_webViewRecreationService.TryCancelNavigationTimeoutRecovery(out var cancelled))
        {
            return;
        }

        if (cancelled.CancelledPending && _webViewRecreationTimer.IsRunning)
        {
            _webViewRecreationTimer.Stop();
        }

        RecordInstrumentationEvent("webview.recreation.cancelled_after_navigation_recovered", new
        {
            pendingReason = cancelled.PendingReason,
            deferredReason = cancelled.DeferredReason,
            activeReason = cancelled.ActiveReason
        });
    }

    private void OnInfoBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        ViewModel.DismissError();
    }

    private void OnDiagnosticInfoBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        ViewModel.DismissDiagnostics();
    }

    private async void OnViewLogsRequested()
    {
        try
        {
            await ShowLogViewerAsync();
        }
        catch (Exception ex)
        {
            if (!_isClosing)
            {
                App.Logger.Warning($"Log viewer dialog failed: {ex.Message}");
            }
        }
    }

    private async Task ShowLogViewerAsync()
    {
        if (_isClosing || _isLogViewerOpen)
        {
            return;
        }

        _isLogViewerOpen = true;
        try
        {
            var dialog = new LogViewerDialog
            {
                XamlRoot = this.Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _isLogViewerOpen = false;
        }
    }
}
