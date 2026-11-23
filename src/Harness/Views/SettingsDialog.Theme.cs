// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Diagnostics;
using Harness.Helpers;
using Harness.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Harness.Views;

public sealed partial class SettingsDialog
{
    private readonly AccessibilitySettings _settingsAccessibilitySettings = new();
    private readonly UISettings _settingsUiSettings = new();

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement rootElement)
        {
            rootElement.Loaded -= OnRootLoaded;
        }

        ApplyTheme(App.Configuration.Settings.AppTheme);
    }

    public void SyncWithCurrentSettings()
    {
        ApplyTheme(App.Configuration.Settings.AppTheme);
        SetLanguageSelection(ViewModel.SelectedLanguage);
    }

    public void ReloadFromCurrentSettings()
    {
        if (_isSaving || _isClosed)
        {
            return;
        }

        ViewModel.RetireStartupStatusRequests();
        ViewModel = new SettingsViewModel(_settingsPersistence);
        EnvironmentList.ItemsSource = ViewModel.Environments;
        SessionEnvironmentList.ItemsSource = SavedSessionEnvironments;
        SelectFirstEnvironment();
        SetLanguageSelection(ViewModel.SelectedLanguage);
        ValidationInfoBar.IsOpen = false;
        SessionInfoBar.IsOpen = false;
        Bindings.Update();
        RefreshStartupRegistrationStatus();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        RestoreSettingsWindowBoundsAfterActivation();
        if (!_isSaving)
        {
            RefreshStartupRegistrationStatus();
        }
    }

    private void OnRootActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyActualThemeIfChanged(sender.ActualTheme);
    }

    private void ApplyTheme(string themeMode)
    {
        if (this.Content is not FrameworkElement rootElement)
        {
            return;
        }

        var requestedTheme = ThemeModeHelper.ToElementTheme(themeMode);
        if (_appliedRequestedTheme != requestedTheme || rootElement.RequestedTheme != requestedTheme)
        {
            _appliedRequestedTheme = requestedTheme;
            rootElement.RequestedTheme = requestedTheme;
        }

        ApplyActualThemeIfChanged(ThemeModeHelper.GetEffectiveTheme(rootElement, themeMode));
    }

    private void ApplyActualThemeIfChanged(ElementTheme actualTheme)
    {
        if (_appliedActualTheme == actualTheme)
        {
            return;
        }

        var wasDark = _appliedActualTheme == ElementTheme.Dark;
        _appliedActualTheme = actualTheme;
        WindowFrameHelper.ApplyActualTheme(
            this,
            actualTheme,
            wasDark,
            UpdateTitleBarColors,
            DispatcherQueue,
            RefreshTitleBarVisualState);
    }

    private void RefreshTitleBarVisualState()
    {
        if (!_isClosed && this.Content is FrameworkElement rootElement)
        {
            UpdateTitleBarColors(rootElement.ActualTheme);
        }
    }

    private void OnSettingsSystemColorsChanged(UISettings sender, object args)
    {
        DispatcherQueue.TryEnqueue(RefreshTitleBarVisualState);
    }

    internal void BeginOpenDiagnostics(long startedAt)
    {
        StopOpenDiagnostics();
        _settingsOpenStartedAt = startedAt;
        CompositionTarget.Rendering += OnSettingsRendering;
    }

    private void OnSettingsRendering(object? sender, object args)
    {
        if (_settingsOpenStartedAt is not { } startedAt || !AppWindow.IsVisible ||
            this.Content is not FrameworkElement { IsLoaded: true })
        {
            return;
        }

        StopOpenDiagnostics();
        App.Logger.Info("settings.window.rendering_callback", new
        {
            elapsedMs = Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, 2),
            measurement = "First rendering callback after Settings became visible and loaded; not presentation or animation completion.",
        });
    }

    private void StopOpenDiagnostics()
    {
        CompositionTarget.Rendering -= OnSettingsRendering;
        _settingsOpenStartedAt = null;
    }

    private void UpdateTitleBarColors(ElementTheme actualTheme)
    {
        var palette = WindowFrameHelper.CreateThemePalette(actualTheme);
        var titleBar = AppWindow.TitleBar;
        var highContrast = _settingsAccessibilitySettings.HighContrast;

        // Keep the custom title bar on Mica. Windows owns contrast-theme colors
        // and the backdrop's solid fallback when transparency is unavailable.
        titleBar.ForegroundColor = highContrast ? null : palette.ForegroundColor;
        titleBar.BackgroundColor = highContrast ? null : Colors.Transparent;
        titleBar.InactiveForegroundColor = highContrast ? null : palette.InactiveForegroundColor;
        titleBar.InactiveBackgroundColor = highContrast ? null : Colors.Transparent;
        titleBar.ButtonForegroundColor = highContrast ? null : palette.ForegroundColor;
        titleBar.ButtonBackgroundColor = highContrast ? null : Colors.Transparent;
        titleBar.ButtonInactiveForegroundColor = highContrast ? null : palette.InactiveForegroundColor;
        titleBar.ButtonInactiveBackgroundColor = highContrast ? null : Colors.Transparent;
        titleBar.ButtonHoverForegroundColor = highContrast ? null : palette.ForegroundColor;
        titleBar.ButtonHoverBackgroundColor = highContrast ? null : palette.ButtonHoverBackgroundColor;
        titleBar.ButtonPressedForegroundColor = highContrast ? null : palette.ForegroundColor;
        titleBar.ButtonPressedBackgroundColor = highContrast ? null : palette.ButtonPressedBackgroundColor;
    }
}
