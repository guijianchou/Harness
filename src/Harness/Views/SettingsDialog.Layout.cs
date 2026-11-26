// Copyright (c) Lanstack @openclaw. All rights reserved.

using CommunityToolkit.WinUI.Controls;
using Harness.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Harness.Views;

public sealed partial class SettingsDialog
{
    private void OnSettingsRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SettingsContentScroll is null || SettingsFooterButtons is null)
        {
            return;
        }

        var compact = e.NewSize.Width < 760;
        SettingsNavigationColumn.Width = new GridLength(compact ? 0 : 208);
        SettingsNavigationSidebar.Visibility = ToPanelVisibility(!compact);
        CompactNavigation.Visibility = ToPanelVisibility(compact);
        SettingsContentScroll.Padding = compact ? new Thickness(16) : new Thickness(24, 16, 24, 24);
        SettingsFooter.Padding = compact ? new Thickness(16, 12, 16, 12) : new Thickness(24, 16, 24, 16);
        Grid.SetColumnSpan(SettingsStatus, compact ? 2 : 1);
        Grid.SetColumn(SettingsFooterButtons, compact ? 0 : 1);
        Grid.SetRow(SettingsFooterButtons, compact ? 1 : 0);
        Grid.SetColumnSpan(SettingsFooterButtons, compact ? 2 : 1);
    }

    private void OnSettingsCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not SettingsCard card || e.NewSize.Width <= 0)
        {
            return;
        }

        // Measure the card itself, including when a previously hidden page first opens.
        var textScale = Math.Max(1, AppTypography.Current.Size / 14);
        var stacked = e.NewSize.Width < (card.Content is ToggleSwitch ? 340 : 560) * textScale;
        card.ContentAlignment = stacked ? ContentAlignment.Vertical : ContentAlignment.Right;
        if (card.Content is FrameworkElement editor)
        {
            editor.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            editor.MaxWidth = Math.Max(0, e.NewSize.Width - card.Padding.Left - card.Padding.Right - 2);
            if (editor is ComboBox or NumberBox)
            {
                editor.Width = 160 * textScale;
            }
        }
    }

    private void OnUpdateCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (UpdatePrimaryButton is null || e.NewSize.Width <= 0) { return; }
        var stacked = e.NewSize.Width < 520;
        Grid.SetColumn(UpdatePrimaryButton, stacked ? 0 : 1);
        Grid.SetRow(UpdatePrimaryButton, stacked ? 1 : 0);
        Grid.SetColumnSpan(UpdatePrimaryButton, stacked ? 2 : 1);
        UpdatePrimaryButton.MinWidth = stacked ? 0 : 160;
        UpdatePrimaryButton.MaxWidth = stacked ? double.PositiveInfinity : 280;
        UpdatePrimaryButton.HorizontalAlignment = stacked ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
    }

    private void OnEnvironmentActionsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ApplyEnvironmentDraft is null)
        {
            return;
        }

        var stacked = e.NewSize.Width < 460;
        Grid.SetColumn(ApplyEnvironmentDraft, stacked ? 0 : 1);
        Grid.SetRow(ApplyEnvironmentDraft, stacked ? 1 : 0);
        Grid.SetColumnSpan(DefaultEnvironmentRow, stacked ? 2 : 1);
        Grid.SetColumnSpan(ApplyEnvironmentDraft, stacked ? 2 : 1);
        ApplyEnvironmentDraft.HorizontalAlignment = stacked ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
    }
}
