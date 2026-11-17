// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Diagnostics;
using Harness.Helpers;
using Harness.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Harness.Views;

public sealed partial class SettingsDialog
{
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
        if (this.Content is FrameworkElement rootElement)
        {
            UpdateTitleBarColors(rootElement.ActualTheme);
        }
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
        WindowFrameHelper.ApplyTitleBarColors(this, new WindowTitleBarColors
        {
            IsDark = palette.IsDark,
            ForegroundColor = palette.ForegroundColor,
            BackgroundColor = palette.BackgroundColor,
            InactiveForegroundColor = palette.InactiveForegroundColor,
            InactiveBackgroundColor = palette.BackgroundColor,
            ButtonForegroundColor = palette.ForegroundColor,
            ButtonBackgroundColor = palette.BackgroundColor,
            ButtonInactiveForegroundColor = palette.InactiveForegroundColor,
            ButtonInactiveBackgroundColor = palette.BackgroundColor,
            ButtonHoverForegroundColor = palette.ForegroundColor,
            ButtonHoverBackgroundColor = palette.ButtonHoverBackgroundColor,
            ButtonPressedForegroundColor = palette.ForegroundColor,
            ButtonPressedBackgroundColor = palette.ButtonPressedBackgroundColor,
            NativeBackgroundColor = palette.BackgroundColor,
            NativeBorderColor = palette.BackgroundColor,
            NativeTextColor = palette.ForegroundColor,
        });
    }
}
