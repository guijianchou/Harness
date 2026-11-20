// Copyright (c) Lanstack @openclaw. All rights reserved.

using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Harness.Views;

public sealed partial class SettingsDialog
{
    private void OnSettingsRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SettingsContentScroll is null || ApplyEnvironmentDraft is null)
        {
            return;
        }

        var compact = e.NewSize.Width < 680;
        SettingsNavigationColumn.Width = new GridLength(compact ? 0 : 180);
        SettingsNavigationSidebar.Visibility = ToPanelVisibility(!compact);
        CompactNavigation.Visibility = ToPanelVisibility(compact);
        SettingsContentScroll.Padding = compact ? new Thickness(12) : new Thickness(24, 20, 24, 20);
        Grid.SetColumn(ApplyEnvironmentDraft, compact ? 0 : 1);
        Grid.SetRow(ApplyEnvironmentDraft, compact ? 1 : 0);
        Grid.SetColumnSpan(DefaultEnvironmentRow, compact ? 2 : 1);
        Grid.SetColumnSpan(ApplyEnvironmentDraft, compact ? 2 : 1);
        ApplyEnvironmentDraft.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        SetSettingsCardLayout(SettingsContentScroll, compact);
    }

    private static void SetSettingsCardLayout(DependencyObject parent, bool compact)
    {
        if (parent is SettingsCard card)
        {
            card.ContentAlignment = compact ? ContentAlignment.Vertical : ContentAlignment.Right;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            SetSettingsCardLayout(VisualTreeHelper.GetChild(parent, index), compact);
        }
    }
}
