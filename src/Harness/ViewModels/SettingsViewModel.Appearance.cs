// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Runtime.CompilerServices;
using Harness.Helpers;
using Harness.Models;
using Harness.Services;

namespace Harness.ViewModels;

public partial class SettingsViewModel
{
    private int _themeIndex;
    private double _zoomPercent = 100;
    private int _fontFamilyIndex = 1;
    private double _fontSize = 14;
    private bool _launchAtStartup;
    private bool _startMinimized;
    private bool _notifyOnRunCompleted;
    private bool _notifyOnlyWhenUnfocused;
    private bool _forwardPageNotifications;
    private bool _didEditTheme;
    private bool _didEditZoomPercent;
    private bool _didEditFontFamily;
    private bool _didEditFontSize;
    private bool _didEditLaunchAtStartup;
    private bool _didEditStartMinimized;
    private bool _didEditNotifyOnRunCompleted;
    private bool _didEditNotifyOnlyWhenUnfocused;
    private bool _didEditForwardPageNotifications;
    private string _startupRegistrationStatus = string.Empty;
    private int _startupStatusRequest;

    public string StartupRegistrationStatus => _startupRegistrationStatus;

    internal void RetireStartupStatusRequests() => _startupStatusRequest++;

    internal async Task RefreshStartupRegistrationStatusAsync(StartupRegistrationService registration)
    {
        var request = ++_startupStatusRequest;
        string status;
        try
        {
            status = (await registration.ReadStateAsync()) switch
            {
                StartupRegistrationState.Registered => StringResources.SettingsStartupRegistered,
                StartupRegistrationState.NotRegistered => StringResources.SettingsStartupNotRegistered,
                StartupRegistrationState.DisabledByWindows => StringResources.SettingsStartupDisabledByWindows,
                _ => StringResources.SettingsStartupDifferentLocation,
            };
        }
        catch (Exception)
        {
            status = StringResources.SettingsStartupStatusUnavailable;
        }

        if (request != _startupStatusRequest || string.Equals(_startupRegistrationStatus, status, StringComparison.Ordinal))
        {
            return;
        }

        _startupRegistrationStatus = status;
        OnPropertyChanged(nameof(StartupRegistrationStatus));
    }

    public IReadOnlyList<string> ThemeOptions { get; } = (string[])
    [StringResources.ThemeSystem, StringResources.ThemeLight, StringResources.ThemeDark];

    public int ThemeIndex
    {
        get => _themeIndex;
        set => SetAppearanceValue(ref _themeIndex, value is >= 0 and <= 2 ? value : 0, ref _didEditTheme);
    }

    public double ZoomPercent
    {
        get => _zoomPercent;
        set => SetAppearanceValue(ref _zoomPercent, value, ref _didEditZoomPercent);
    }

    public IReadOnlyList<string> FontFamilyOptions { get; } = (string[])
    ["Noto Sans SC", "System UI"];

    public int FontFamilyIndex
    {
        get => _fontFamilyIndex;
        set
        {
            if (value is < 0 or > 1)
            {
                return;
            }

            SetAppearanceValue(ref _fontFamilyIndex, value, ref _didEditFontFamily);
        }
    }

    public double FontSize
    {
        get => _fontSize;
        set => SetAppearanceValue(ref _fontSize, value, ref _didEditFontSize);
    }

    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set => SetAppearanceValue(ref _launchAtStartup, value, ref _didEditLaunchAtStartup);
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set => SetAppearanceValue(ref _startMinimized, value, ref _didEditStartMinimized);
    }

    public bool NotifyOnRunCompleted
    {
        get => _notifyOnRunCompleted;
        set => SetAppearanceValue(ref _notifyOnRunCompleted, value, ref _didEditNotifyOnRunCompleted);
    }

    public bool NotifyOnlyWhenUnfocused
    {
        get => _notifyOnlyWhenUnfocused;
        set => SetAppearanceValue(ref _notifyOnlyWhenUnfocused, value, ref _didEditNotifyOnlyWhenUnfocused);
    }

    public bool ForwardPageNotifications
    {
        get => _forwardPageNotifications;
        set => SetAppearanceValue(ref _forwardPageNotifications, value, ref _didEditForwardPageNotifications);
    }

    private void LoadAppearanceSettings(AppSettings settings)
    {
        _themeIndex = settings.AppTheme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        _zoomPercent = settings.ZoomFactor * 100;
        _fontFamilyIndex = settings.AppFontFamily == "Noto Sans SC" ? 0 : 1;
        _fontSize = double.IsFinite(settings.AppFontSize) && settings.AppFontSize is >= 10 and <= 32 &&
            settings.AppFontSize == Math.Truncate(settings.AppFontSize) ? settings.AppFontSize : 14;
        _launchAtStartup = settings.LaunchAtStartup;
        _startMinimized = settings.StartMinimized;
        _notifyOnRunCompleted = settings.NotifyOnRunCompleted;
        _notifyOnlyWhenUnfocused = settings.NotifyOnlyWhenUnfocused;
        _forwardPageNotifications = settings.ForwardPageNotifications;
    }

    private void ApplyChangedAppearanceSettings(AppSettings settings)
    {
        if (_didEditTheme)
        {
            settings.AppTheme = ThemeIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
        }

        if (_didEditZoomPercent)
        {
            settings.ZoomFactor = ZoomPercent / 100;
        }

        if (_didEditFontFamily)
        {
            settings.AppFontFamily = FontFamilyOptions[FontFamilyIndex];
        }

        if (_didEditFontSize)
        {
            settings.AppFontSize = FontSize;
        }

        if (_didEditLaunchAtStartup)
        {
            settings.LaunchAtStartup = LaunchAtStartup;
        }

        if (_didEditStartMinimized)
        {
            settings.StartMinimized = StartMinimized;
        }

        if (_didEditNotifyOnRunCompleted)
        {
            settings.NotifyOnRunCompleted = NotifyOnRunCompleted;
        }

        if (_didEditNotifyOnlyWhenUnfocused)
        {
            settings.NotifyOnlyWhenUnfocused = NotifyOnlyWhenUnfocused;
        }

        if (_didEditForwardPageNotifications)
        {
            settings.ForwardPageNotifications = ForwardPageNotifications;
        }
    }

    private void ResetCommittedAppearanceEdits()
    {
        _didEditTheme = false;
        _didEditZoomPercent = false;
        _didEditFontFamily = false;
        _didEditFontSize = false;
        _didEditStartMinimized = false;
        _didEditNotifyOnRunCompleted = false;
        _didEditNotifyOnlyWhenUnfocused = false;
        _didEditForwardPageNotifications = false;
    }

    private bool TryValidateAppearanceSettings()
    {
        if (!double.IsFinite(ZoomPercent) || ZoomPercent is < 50 or > 200)
        {
            ValidationMessage = StringResources.SettingsZoomInvalid;
            return false;
        }

        if (!double.IsFinite(FontSize) || FontSize is < 10 or > 32 || FontSize != Math.Truncate(FontSize))
        {
            ValidationMessage = StringResources.SettingsFontSizeInvalid;
            return false;
        }

        return true;
    }

    private void SetAppearanceValue<T>(ref T field, T value, ref bool didEdit, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        didEdit = true;
        OnPropertyChanged(propertyName);
    }
}
