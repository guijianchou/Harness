// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.Views;

public sealed partial class SettingsDialog
{
    // The caller supplies an environment only after loading a fresh draft. An already
    // open settings window keeps its selection and unapplied environment edits.
    public void ShowEnvironmentSettings(string? environmentId = null)
    {
        if (_isSaving)
        {
            return;
        }

        if (environmentId is not null &&
            ViewModel.Environments.FirstOrDefault(environment => environment.Id == environmentId) is { } selected)
        {
            EnvironmentList.SelectedItem = selected;
        }

        NavList.SelectedItem = NavEnvironments;
    }
}
