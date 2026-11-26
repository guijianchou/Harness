// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Helpers;

namespace Harness;

public sealed partial class MainWindow
{
    private void ApplyTypography(Models.AppSettings settings)
    {
        AppTypography.Current.Apply(settings);
        ViewModel.SetContentTypography(settings.AppFontFamily, settings.AppFontSize);
    }
}
