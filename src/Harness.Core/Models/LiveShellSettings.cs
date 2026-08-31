// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.Models;

public sealed record LiveShellSettings(
    bool AlwaysOnTop,
    bool EnableGlobalHotkey,
    string GlobalHotkey,
    bool AllowMultipleInstances,
    bool AutoRefresh)
{
    public static LiveShellSettings From(AppSettings settings)
    {
        return new LiveShellSettings(
            settings.AlwaysOnTop,
            settings.EnableGlobalHotkey,
            settings.GlobalHotkey.Trim(),
            settings.AllowMultipleInstances,
            settings.AutoRefresh);
    }
}
