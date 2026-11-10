// Copyright (c) Lanstack @openclaw. All rights reserved.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Harness;

public sealed partial class MainWindow
{
    private Visibility ShowWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private Visibility ShowGatewayEmptyState(int count, bool busy, bool hasError) =>
        count == 0 && !busy && !hasError ? Visibility.Visible : Visibility.Collapsed;

    private void OnGatewayPaneSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (GatewayPaneLayout is null || GatewayPaneViewport is null || GatewayPaneHeader is null ||
            GatewayPaneDetails is null || GatewaySessionArea is null)
        {
            return;
        }

        // Keep the list's viewport finite while allowing the whole pane to scroll in short windows.
        var minimumHeight = GatewayPaneHeader.ActualHeight + GatewayPaneDetails.ActualHeight + GatewaySessionArea.MinHeight +
            GatewayPaneLayout.Padding.Top + GatewayPaneLayout.Padding.Bottom + (GatewayPaneLayout.RowSpacing * 2);
        var height = Math.Max(GatewayPaneViewport.ActualHeight, minimumHeight);
        if (double.IsNaN(GatewayPaneLayout.Height) || Math.Abs(GatewayPaneLayout.Height - height) > 0.5)
        {
            GatewayPaneLayout.Height = height;
        }
    }

    private void OnCompactModeMenuClick(object sender, RoutedEventArgs e) => ToggleCompactMode();

    private void OnOverflowThemeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string theme })
        {
            return;
        }

        var themeButton = theme switch
        {
            "Light" => LightThemeButton,
            "Dark" => DarkThemeButton,
            _ => SystemThemeButton,
        };
        OnThemeSelectionClick(themeButton, e);
    }
}
