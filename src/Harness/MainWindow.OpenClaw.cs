// Copyright (c) Lanstack @openclaw. All rights reserved.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Harness;

public sealed partial class MainWindow
{
    private Visibility ShowWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private Visibility ShowGatewayProgress(bool busy, bool loading) => ShowWhen(busy || loading);

    private Visibility ShowGatewayEmptyState(int count, bool busy, bool loading, bool hasError, bool loaded) =>
        count == 0 && loaded && !busy && !loading && !hasError ? Visibility.Visible : Visibility.Collapsed;

    private string GetWorkStatusTooltip(bool isOpenClaw, string runText, string workText) => isOpenClaw ? runText : workText;

    private void OnGatewayRecoveryClick(object sender, RoutedEventArgs e)
    {
        switch (ViewModel.GatewayErrorAction)
        {
            case ViewModels.GatewayRecoveryAction.Configure:
                ShowSettingsWindow(showEnvironment: true);
                break;
            case ViewModels.GatewayRecoveryAction.Refresh:
                ViewModel.RefreshGatewayCommand.Execute(null);
                break;
            case ViewModels.GatewayRecoveryAction.ReturnToPage:
                ViewModel.IsOpenClawPanelOpen = false;
                foreach (var child in WebViewHost.Children.OfType<Control>())
                {
                    child.Focus(FocusState.Programmatic);
                }
                break;
        }
    }

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
}
