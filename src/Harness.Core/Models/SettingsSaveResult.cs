// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.Models;

public readonly record struct SettingsSaveResult(
    bool DidChangeEnvironmentState,
    bool DidChangeSessionTopology,
    bool DidChangeLanguage,
    LiveShellSettingsChange LiveShellSettingsChange)
{
    public bool DidChangeLiveShellOptions => LiveShellSettingsChange.HasChanges;
}
