// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Helpers;

namespace Harness.Views;

public sealed partial class SettingsDialog
{
    private void InitializeAboutPage()
    {
        AboutVersionText.Text = string.Format(StringResources.AboutVersionFormat, AppMetadata.GetDisplayVersion());
    }
}
