// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Helpers;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Harness.Views;

public sealed partial class SettingsDialog
{
    private void ConfigureWindowChrome()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(SettingsTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        Title = Helpers.StringResources.SettingsTitle;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "WindowIcon.ico"));
        RestoreSettingsWindowBounds();
        this.Closed += OnSettingsDialogClosed;
        this.Activated += OnWindowActivated;
        AppWindow.Closing += OnSettingsAppWindowClosing;
        _settingsUiSettings.ColorValuesChanged += OnSettingsSystemColorsChanged;
    }

    private void AttachRootEventHandlers()
    {
        if (this.Content is not FrameworkElement rootElement)
        {
            return;
        }

        rootElement.Loaded += OnRootLoaded;
        rootElement.ActualThemeChanged += OnRootActualThemeChanged;
        ApplyTheme(App.Configuration.Settings.AppTheme);
    }

    private void InitializeEnvironmentBindings()
    {
        EnvironmentList.ItemsSource = ViewModel.Environments;
        var selectedId = _settingsPersistence.GetSelectedEnvironment()?.Id;
        SelectEnvironment(ViewModel.Environments.FirstOrDefault(environment => environment.Id == selectedId)
            ?? ViewModel.Environments.FirstOrDefault());
    }

    private void InitializeNavigationState()
    {
        InitializeAboutPage();
        PopulateLanguageOptions();
        NavList.SelectedIndex = 0;
    }

    private void RestoreSettingsWindowBounds()
    {
        _ = TryRestoreSettingsWindowBounds(logOffscreenRecovery: false);
    }

    private void RestoreSettingsWindowBoundsAfterActivation()
    {
        if (_hasRestoredSettingsBoundsAfterActivation)
        {
            return;
        }

        _hasRestoredSettingsBoundsAfterActivation = true;
        _ = TryRestoreSettingsWindowBounds(logOffscreenRecovery: true);
    }

    private bool TryRestoreSettingsWindowBounds(bool logOffscreenRecovery)
    {
        var settings = App.Configuration.Settings;
        var width = (int)settings.SettingsWindowWidth;
        var height = (int)settings.SettingsWindowHeight;

        if (!WindowBoundsUtilities.HasPersistableSize(
                width,
                height,
                WindowBoundsUtilities.MinimumPersistedSettingsWindowWidth,
                WindowBoundsUtilities.MinimumPersistedSettingsWindowHeight))
        {
            width = WindowBoundsUtilities.DefaultSettingsWindowWidth;
            height = WindowBoundsUtilities.DefaultSettingsWindowHeight;
        }

        if (!WindowBoundsUtilities.HasSavedPosition(settings.SettingsWindowLeft, settings.SettingsWindowTop))
        {
            ResizeSettingsWindowIfNeeded(width, height);
            return false;
        }

        var left = (int)settings.SettingsWindowLeft;
        var top = (int)settings.SettingsWindowTop;
        if (WindowFrameHelper.IsNativeWindowRectVisibleWithinAnyMonitor(left, top, width, height))
        {
            SetSettingsWindowBoundsIfChanged(left, top, width, height);

            return true;
        }

        if (WindowFrameHelper.TryCenterNativeWindowRectInNearestMonitor(
                left,
                top,
                width,
                height,
                out var centeredLeft,
                out var centeredTop))
        {
            var moved = SetSettingsWindowBoundsIfChanged(centeredLeft, centeredTop, width, height);

            if (logOffscreenRecovery && moved)
            {
                App.Logger.Info("Saved Settings window bounds were outside current displays; moved window to the current display.");
            }

            return true;
        }

        ResizeSettingsWindowIfNeeded(width, height);
        return false;
    }

    private bool SetSettingsWindowBoundsIfChanged(int left, int top, int width, int height)
    {
        if (WindowFrameHelper.TryGetWindowRect(this, out var currentLeft, out var currentTop, out var currentWidth, out var currentHeight) &&
            currentLeft == left && currentTop == top && currentWidth == width && currentHeight == height)
        {
            return false;
        }

        if (!WindowFrameHelper.TrySetWindowRect(this, left, top, width, height))
        {
            ResizeSettingsWindowIfNeeded(width, height);
            var currentPosition = AppWindow.Position;
            if (currentPosition.X != left || currentPosition.Y != top)
            {
                AppWindow.Move(new PointInt32(left, top));
            }
        }

        return true;
    }

    private void ResizeSettingsWindowIfNeeded(int width, int height)
    {
        var currentSize = AppWindow.Size;
        if (currentSize.Width != width || currentSize.Height != height)
        {
            AppWindow.Resize(new SizeInt32(width, height));
        }
    }

    private void OnSettingsDialogClosed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        _updateCleanupTask = Updates.CloseAsync();
        ViewModel.RetireStartupStatusRequests();
        StopOpenDiagnostics();
        if (this.Content is FrameworkElement rootElement)
        {
            rootElement.Loaded -= OnRootLoaded;
            rootElement.ActualThemeChanged -= OnRootActualThemeChanged;
        }

        SaveSettingsWindowBounds();
        this.Closed -= OnSettingsDialogClosed;
        this.Activated -= OnWindowActivated;
        AppWindow.Closing -= OnSettingsAppWindowClosing;
        _settingsUiSettings.ColorValuesChanged -= OnSettingsSystemColorsChanged;
    }

    private void OnSettingsAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_isSaving)
        {
            args.Cancel = true;
        }
    }

    private void SaveSettingsWindowBounds()
    {
        if (WindowFrameHelper.IsWindowMinimized(this))
        {
            App.Logger.Info("Skipping Settings window bounds save because the window is minimized.");
            return;
        }

        try
        {
            if (!WindowFrameHelper.TryGetWindowRect(this, out var left, out var top, out var width, out var height))
            {
                App.Logger.Warning("Skipping Settings window bounds save because native window bounds were unavailable.");
                return;
            }

            if (!WindowBoundsUtilities.CanPersistWindowBounds(
                    left,
                    top,
                    width,
                    height,
                    WindowBoundsUtilities.MinimumPersistedSettingsWindowWidth,
                    WindowBoundsUtilities.MinimumPersistedSettingsWindowHeight))
            {
                App.Logger.Warning($"Skipping invalid Settings window bounds: x={left}, y={top}, width={width}, height={height}");
                return;
            }

            App.Configuration.Settings.SettingsWindowWidth = width;
            App.Configuration.Settings.SettingsWindowHeight = height;
            App.Configuration.Settings.SettingsWindowLeft = left;
            App.Configuration.Settings.SettingsWindowTop = top;
            App.Configuration.SaveDeferred();
        }
        catch (Exception ex)
        {
            App.Logger.Warning($"Failed to save Settings window bounds: {ex.Message}");
        }
    }
}
