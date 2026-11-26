// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.Models;

public sealed record LiveShellSettings(
    bool AlwaysOnTop,
    bool EnableGlobalHotkey,
    string GlobalHotkey,
    bool AllowMultipleInstances,
    bool AutoRefresh)
{
    public string AppTheme { get; init; } = "System";

    public double ZoomFactor { get; init; } = 1.0;

    public string AppFontFamily { get; init; } = "System UI";

    public double AppFontSize { get; init; } = 14;

    public static LiveShellSettings From(AppSettings settings)
    {
        return new LiveShellSettings(
            settings.AlwaysOnTop,
            settings.EnableGlobalHotkey,
            settings.GlobalHotkey.Trim(),
            settings.AllowMultipleInstances,
            settings.AutoRefresh)
        {
            AppTheme = settings.AppTheme,
            ZoomFactor = settings.ZoomFactor,
            AppFontFamily = settings.AppFontFamily,
            AppFontSize = settings.AppFontSize,
        };
    }
}
