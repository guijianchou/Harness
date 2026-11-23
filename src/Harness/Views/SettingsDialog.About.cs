// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Helpers;
using Microsoft.UI.Xaml;

namespace Harness.Views;

public sealed partial class SettingsDialog
{
    private void InitializeAboutPage()
    {
        AboutVersionText.Text = string.Format(StringResources.AboutVersionFormat, AppMetadata.GetDisplayVersion());
    }

    private async void OnAboutProjectClick(object sender, RoutedEventArgs e)
    {
        await OpenAboutLinkAsync(new Uri("https://github.com/Guijianchou/Harness"));
    }

    private async void OnAboutReleaseNotesClick(object sender, RoutedEventArgs e)
    {
        await OpenAboutLinkAsync(Updates.ReleasePage);
    }

}
