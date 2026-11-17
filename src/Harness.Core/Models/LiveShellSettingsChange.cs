// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.Models;

public sealed record LiveShellSettingsChange(
    LiveShellSettings Before,
    LiveShellSettings After)
{
    public bool DidChangeAlwaysOnTop => Before.AlwaysOnTop != After.AlwaysOnTop;

    public bool DidChangeGlobalHotkey =>
        Before.EnableGlobalHotkey != After.EnableGlobalHotkey ||
        !string.Equals(Before.GlobalHotkey, After.GlobalHotkey, StringComparison.Ordinal);

    public bool DidChangeAllowMultipleInstances =>
        Before.AllowMultipleInstances != After.AllowMultipleInstances;

    public bool DidChangeAutoRefresh =>
        Before.AutoRefresh != After.AutoRefresh;

    public bool DidChangeAppTheme =>
        !string.Equals(Before.AppTheme, After.AppTheme, StringComparison.Ordinal);

    public bool DidChangeZoomFactor => Before.ZoomFactor != After.ZoomFactor;

    public bool HasChanges =>
        DidChangeAlwaysOnTop ||
        DidChangeGlobalHotkey ||
        DidChangeAllowMultipleInstances ||
        DidChangeAutoRefresh ||
        DidChangeAppTheme ||
        DidChangeZoomFactor;
}
