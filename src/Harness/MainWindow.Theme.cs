// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Helpers;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Harness;

public sealed partial class MainWindow
{
    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        var selectedTheme = App.Configuration.Settings.AppTheme switch
        {
            ThemeModeHelper.Light => ThemeModeHelper.Dark,
            ThemeModeHelper.Dark => ThemeModeHelper.System,
            _ => ThemeModeHelper.Light,
        };

        App.Configuration.Settings.AppTheme = selectedTheme;
        App.Configuration.SaveDeferred();
        ApplyTheme(selectedTheme);
        UpdateThemeSelector(selectedTheme);
        _settingsWindow?.SyncWithCurrentSettings();
    }

    private void UpdateThemeSelector(string themeMode)
    {
        var (currentLabel, nextLabel) = themeMode switch
        {
            ThemeModeHelper.Light => (StringResources.ThemeLight, StringResources.ThemeDark),
            ThemeModeHelper.Dark => (StringResources.ThemeDark, StringResources.ThemeSystem),
            _ => (StringResources.ThemeSystem, StringResources.ThemeLight),
        };
        var description = string.Format(StringResources.ThemeCycleTooltipFormat, currentLabel, nextLabel);
        SystemThemeIcon.Visibility = themeMode is not (ThemeModeHelper.Light or ThemeModeHelper.Dark) ? Visibility.Visible : Visibility.Collapsed;
        LightThemeIcon.Visibility = themeMode == ThemeModeHelper.Light ? Visibility.Visible : Visibility.Collapsed;
        DarkThemeIcon.Visibility = themeMode == ThemeModeHelper.Dark ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(ThemeButton, description);
        ToolTipService.SetToolTip(ThemeButton, description);
    }

    private void ApplyTheme(string themeMode)
    {
        _isDarkThemeActive = WindowFrameHelper.ApplyWindowTheme(
            this,
            themeMode,
            _isDarkThemeActive,
            UpdateTitleBarColors,
            DispatcherQueue,
            RefreshTitleBarVisualState,
            redrawWindow: true,
            useSizeNudgeOnDarkTransition: true,
            includeTrailingRefresh: true);
    }

    private void RefreshTitleBarVisualState()
    {
        UpdateTitleBarInsets();

        if (this.Content is FrameworkElement rootElement)
        {
            UpdateTitleBarColors(rootElement.ActualTheme);
            AppTitleBar.InvalidateMeasure();
            AppTitleBar.InvalidateArrange();
            rootElement.InvalidateMeasure();
            rootElement.InvalidateArrange();
            rootElement.UpdateLayout();
        }
    }

    private void UpdateTitleBarInsets()
    {
        var titleBar = AppWindow.TitleBar;
        LeftInsetColumn.Width = new GridLength(titleBar.LeftInset);
        RightInsetColumn.Width = new GridLength(titleBar.RightInset);
    }

    private void UpdateTitleBarColors(ElementTheme actualTheme)
    {
        var palette = WindowFrameHelper.CreateThemePalette(actualTheme);
        var inactiveBackground = palette.IsDark
            ? Windows.UI.Color.FromArgb(255, 40, 40, 40)
            : Windows.UI.Color.FromArgb(255, 248, 248, 248);
        var currentCaptionColor = _isWindowActive ? palette.BackgroundColor : inactiveBackground;
        var currentForeground = _isWindowActive ? palette.ForegroundColor : palette.InactiveForegroundColor;

        WindowFrameHelper.ApplyTitleBarColors(this, new WindowTitleBarColors
        {
            IsDark = palette.IsDark,
            ForegroundColor = palette.ForegroundColor,
            BackgroundColor = currentCaptionColor,
            InactiveForegroundColor = palette.InactiveForegroundColor,
            InactiveBackgroundColor = inactiveBackground,
            ButtonForegroundColor = currentForeground,
            ButtonBackgroundColor = Colors.Transparent,
            ButtonInactiveForegroundColor = palette.InactiveForegroundColor,
            ButtonInactiveBackgroundColor = Colors.Transparent,
            ButtonHoverForegroundColor = palette.ForegroundColor,
            ButtonHoverBackgroundColor = palette.ButtonHoverBackgroundColor,
            ButtonPressedForegroundColor = palette.ForegroundColor,
            ButtonPressedBackgroundColor = palette.ButtonPressedBackgroundColor,
            NativeBackgroundColor = currentCaptionColor,
            NativeBorderColor = currentCaptionColor,
            NativeTextColor = currentForeground,
        });

        UpdateTitleBarContentState(currentForeground, _isWindowActive);
        AppTitleBar.Background = new SolidColorBrush(currentCaptionColor);
    }

    private void UpdateTitleBarContentState(Windows.UI.Color foregroundColor, bool isWindowActive)
    {
        AppTitleText.Foreground = new SolidColorBrush(foregroundColor);
        AppIcon.Opacity = isWindowActive ? 1.0 : 0.72;
    }
}
