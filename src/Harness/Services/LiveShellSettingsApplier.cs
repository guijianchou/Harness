// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Models;

namespace Harness.Services;

internal sealed class LiveShellSettingsApplier
{
    private readonly Action<bool> _setAlwaysOnTop;
    private readonly Action _reapplyGlobalHotkey;
    private readonly Action<bool> _setAutoRefresh;
    private readonly Action<bool> _applySingleInstancePreference;

    public LiveShellSettingsApplier(
        Action<bool> setAlwaysOnTop,
        Action reapplyGlobalHotkey,
        Action<bool> setAutoRefresh,
        Action<bool> applySingleInstancePreference)
    {
        _setAlwaysOnTop = setAlwaysOnTop;
        _reapplyGlobalHotkey = reapplyGlobalHotkey;
        _setAutoRefresh = setAutoRefresh;
        _applySingleInstancePreference = applySingleInstancePreference;
    }

    public void Apply(LiveShellSettingsChange change)
    {
        if (change.DidChangeAlwaysOnTop)
        {
            _setAlwaysOnTop(change.After.AlwaysOnTop);
        }

        if (change.DidChangeGlobalHotkey)
        {
            _reapplyGlobalHotkey();
        }

        if (change.DidChangeAutoRefresh)
        {
            _setAutoRefresh(change.After.AutoRefresh);
        }

        if (change.DidChangeAllowMultipleInstances)
        {
            _applySingleInstancePreference(change.After.AllowMultipleInstances);
        }
    }
}
