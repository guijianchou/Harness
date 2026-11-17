// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.Models;

public readonly record struct SettingsSaveResult(
    bool DidChangeEnvironmentState,
    bool DidChangeSessionTopology,
    bool DidChangeLanguage,
    LiveShellSettingsChange LiveShellSettingsChange)
{
    public bool DidChangeLiveShellOptions => LiveShellSettingsChange.HasChanges;

    public static SettingsSaveResult Between(AppSettings before, AppSettings after)
    {
        var selectionChanged = !string.Equals(before.SelectedEnvironmentName, after.SelectedEnvironmentName, StringComparison.Ordinal);
        return new SettingsSaveResult(
            selectionChanged || HaveEnvironmentsChanged(before.Environments, after.Environments, includeDefault: true),
            selectionChanged || HaveEnvironmentsChanged(before.Environments, after.Environments, includeDefault: false),
            !string.Equals(before.AppLanguage, after.AppLanguage, StringComparison.Ordinal),
            new LiveShellSettingsChange(LiveShellSettings.From(before), LiveShellSettings.From(after)));
    }

    private static bool HaveEnvironmentsChanged(
        IReadOnlyList<EnvironmentConfig> before,
        IReadOnlyList<EnvironmentConfig> after,
        bool includeDefault)
    {
        if (before.Count != after.Count)
        {
            return true;
        }
        for (var index = 0; index < before.Count; index++)
        {
            var previous = before[index];
            var current = after[index];
            if (!string.Equals(previous.Id, current.Id, StringComparison.Ordinal) ||
                !string.Equals(previous.Name, current.Name, StringComparison.Ordinal) ||
                !string.Equals(previous.GatewayUrl, current.GatewayUrl, StringComparison.Ordinal) ||
                previous.Backend != current.Backend || previous.OpenClawConnection != current.OpenClawConnection ||
                (includeDefault && previous.IsDefault != current.IsDefault))
            {
                return true;
            }
        }
        return false;
    }
}
