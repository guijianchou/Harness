// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Text.Json.Serialization;

namespace Harness.Models;

/// <summary>
/// Application-level settings persisted as JSON to local storage.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// Gets or sets the list of configured gateway environments.
    /// </summary>
    public List<EnvironmentConfig> Environments { get; set; } = [];

    /// <summary>
    /// Gets or sets the name of the currently selected environment.
    /// </summary>
    public string? SelectedEnvironmentName { get; set; }

    /// <summary>
    /// Gets or sets the remembered main window width.
    /// </summary>
    public double WindowWidth { get; set; } = 1280;

    /// <summary>
    /// Gets or sets the remembered main window height.
    /// </summary>
    public double WindowHeight { get; set; } = 800;

    /// <summary>
    /// Gets or sets the remembered main window left position.
    /// </summary>
    public double WindowLeft { get; set; } = -1;

    /// <summary>
    /// Gets or sets the remembered main window top position.
    /// </summary>
    public double WindowTop { get; set; } = -1;

    /// <summary>
    /// Gets or sets the remembered Settings window width.
    /// </summary>
    public double SettingsWindowWidth { get; set; } = 720;

    /// <summary>
    /// Gets or sets the remembered Settings window height.
    /// </summary>
    public double SettingsWindowHeight { get; set; } = 520;

    /// <summary>
    /// Gets or sets the remembered Settings window left position.
    /// </summary>
    public double SettingsWindowLeft { get; set; } = -1;

    /// <summary>
    /// Gets or sets the remembered Settings window top position.
    /// </summary>
    public double SettingsWindowTop { get; set; } = -1;

    /// <summary>
    /// Gets or sets the preferred application theme (System, Light, Dark).
    /// </summary>
    public string AppTheme { get; set; } = "System";

    /// <summary>
    /// Gets or sets the preferred application language (System, en-US, zh-CN).
    /// </summary>
    public string AppLanguage { get; set; } = "en-US";

    /// <summary>
    /// Gets or sets whether closing the main window hides it to the system tray.
    /// </summary>
    public bool SystemTray { get; set; } = false;

    /// <summary>
    /// Gets or sets whether Harness automatically recovers and refreshes hosted sessions.
    /// Default is true for compatibility with existing installations.
    /// </summary>
    public bool AutoRefresh { get; set; } = true;

    /// <summary>
    /// Gets or sets whether Harness allows multiple app instances on Windows.
    /// </summary>
    public bool AllowMultipleInstances { get; set; } = false;

    /// <summary>
    /// Gets or sets the global hotkey binding string (e.g. "Ctrl+Alt+Space").
    /// Empty or null disables the hotkey.
    /// </summary>
    public string GlobalHotkey { get; set; } = "Ctrl+Alt+Space";

    /// <summary>
    /// Gets or sets whether the global hotkey is enabled.
    /// </summary>
    public bool EnableGlobalHotkey { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the main window stays on top of other windows.
    /// </summary>
    public bool AlwaysOnTop { get; set; } = false;

    /// <summary>
    /// Gets or sets whether compact mode is active (reduced control/status window).
    /// </summary>
    public bool CompactMode { get; set; } = false;

    /// <summary>
    /// Gets or sets the compact mode window left position.
    /// </summary>
    public double CompactWindowLeft { get; set; } = -1;

    /// <summary>
    /// Gets or sets the compact mode window top position.
    /// </summary>
    public double CompactWindowTop { get; set; } = -1;

    /// <summary>
    /// Gets or sets whether Harness raises a native notification when a hosted run
    /// finishes. This is the signal a browser tab cannot deliver on its own, so it
    /// defaults to on.
    /// </summary>
    public bool NotifyOnRunCompleted { get; set; } = true;

    /// <summary>
    /// Gets or sets whether run-completion notifications are suppressed while the
    /// window is focused. On by default: if the user is watching the output, the
    /// notification is redundant.
    /// </summary>
    public bool NotifyOnlyWhenUnfocused { get; set; } = true;

    /// <summary>
    /// Gets or sets whether Web Notifications raised by the hosted page are
    /// forwarded to the OS. Without forwarding they are silently dropped inside
    /// the shell, which is worse than the browser it replaces.
    /// </summary>
    public bool ForwardPageNotifications { get; set; } = true;

    /// <summary>
    /// Gets or sets the remembered WebView2 zoom factor. Persisted so the hosted UI
    /// does not reset to 100% on every launch.
    /// </summary>
    public double ZoomFactor { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets whether Harness starts when the user signs in.
    /// </summary>
    public bool LaunchAtStartup { get; set; } = false;

    /// <summary>
    /// Gets or sets the heartbeat probe interval in seconds. 0 = disabled.
    /// Default is 30s, which works well with Cloudflare Tunnel / reverse proxy idle timeouts (60-100s).
    /// </summary>
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the connection recovery policy options.
    /// </summary>
    public RecoveryPolicyOptions RecoveryPolicy { get; set; } = new();

    /// <summary>
    /// Gets or sets the heartbeat options.
    /// </summary>
    public HeartbeatOptions Heartbeat { get; set; } = new();

    /// <summary>
    /// Gets or sets the diagnostics options.
    /// </summary>
    public DiagnosticsOptions Diagnostics { get; set; } = new();
}

/// <summary>
/// Source generation context for System.Text.Json serialization.
/// Enables AOT-friendly JSON serialization for AppSettings.
/// </summary>
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(RecoveryPolicyOptions))]
[JsonSerializable(typeof(HeartbeatOptions))]
[JsonSerializable(typeof(DiagnosticsOptions))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class AppSettingsJsonContext : JsonSerializerContext
{
}
