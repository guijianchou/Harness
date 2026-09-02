// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Harness.Services;

namespace Harness.ViewModels;

public partial class MainViewModel
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised when the user requests to open the settings dialog.
    /// </summary>
    public event Action? OpenSettingsRequested;

    /// <summary>
    /// Raised when the active environment requires the embedded WebView2 to be recreated.
    /// </summary>
    public event Action<string>? WebViewRecreationRequested;

    public event Action? NavigationTimeoutRecoveryNoLongerNeeded;

    /// <summary>
    /// Raised when the user requests to view logs.
    /// </summary>
    public event Action? ViewLogsRequested;

    /// <summary>
    /// Raised when a navigation error occurs, for display to the user.
    /// </summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>
    /// Raised when the view model decides a native notification is warranted.
    /// </summary>
    public event Action<NativeNotificationRequest>? NotificationRequested;

    /// <summary>
    /// Gets or sets whether the shell window is currently focused. Set by the
    /// view so the notification logic can suppress completion notices while the
    /// user is watching.
    /// </summary>
    public bool IsWindowFocused { get; set; } = true;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
