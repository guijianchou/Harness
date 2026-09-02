// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;

namespace Harness.Services;

/// <summary>
/// Routes content the hosted page opens outside the current document: popups,
/// target="_blank" links, and downloads.
/// </summary>
public partial class WebViewService
{
    /// <summary>
    /// Raised when a download finishes, with the absolute result path.
    /// </summary>
    public event Action<string>? DownloadCompleted;

    /// <summary>
    /// Raised when a link was handed to the system default browser.
    /// </summary>
    public event Action<string>? ExternalNavigationOpened;

    private TypedEventHandler<CoreWebView2, CoreWebView2NewWindowRequestedEventArgs> CreateNewWindowRequestedHandler(int hostGeneration)
    {
        return (sender, args) => OnNewWindowRequested(sender, args, hostGeneration);
    }

    private TypedEventHandler<CoreWebView2, CoreWebView2DownloadStartingEventArgs> CreateDownloadStartingHandler(int hostGeneration)
    {
        return (sender, args) => OnDownloadStarting(sender, args, hostGeneration);
    }

    /// <summary>
    /// Without this handler WebView2 opens target="_blank" and window.open in a
    /// bare popup window with no address bar, navigation, or chrome -- worse than
    /// the browser the shell replaces. Same-origin links stay in the current
    /// WebView so the hosted session is never orphaned in an unreachable popup;
    /// anything cross-origin goes to the system default browser.
    /// </summary>
    private void OnNewWindowRequested(
        CoreWebView2 sender,
        CoreWebView2NewWindowRequestedEventArgs args,
        int hostGeneration)
    {
        if (!IsCurrentHost(hostGeneration))
        {
            return;
        }

        var targetUri = args.Uri;
        if (string.IsNullOrWhiteSpace(targetUri))
        {
            return;
        }

        args.Handled = true;

        if (IsSameOriginAsCurrentSession(targetUri))
        {
            _logger.Info("webview.new_window.same_origin_inline", new { uri = targetUri });
            NavigateCurrentWebView(targetUri);
            return;
        }

        OpenInSystemBrowser(targetUri);
    }

    // NewWindowRequested is raised on the UI thread, so this navigates inline:
    // dispatching would let the handler return before Handled took effect.
    private void NavigateCurrentWebView(string targetUri)
    {
        try
        {
            _coreWebView?.Navigate(targetUri);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Same-origin popup navigation failed, opening externally instead: {ex.Message}");
            OpenInSystemBrowser(targetUri);
        }
    }

    private void OpenInSystemBrowser(string targetUri)
    {
        // Only ever hand http/https to the shell. A page-supplied file:, or any
        // other scheme reaches ShellExecute as an arbitrary local command.
        if (!Uri.TryCreate(targetUri, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            _logger.Warning("webview.new_window.rejected_scheme", new { uri = targetUri });
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            });

            _logger.Info("webview.new_window.opened_externally", new { uri = uri.AbsoluteUri });
            ExternalNavigationOpened?.Invoke(uri.AbsoluteUri);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to open external link in the default browser: {ex.Message}");
        }
    }

    private bool IsSameOriginAsCurrentSession(string targetUri)
    {
        var currentUrl = _lastNavigatedUrl;
        if (string.IsNullOrWhiteSpace(currentUrl) ||
            !Uri.TryCreate(currentUrl, UriKind.Absolute, out var current) ||
            !Uri.TryCreate(targetUri, UriKind.Absolute, out var target))
        {
            return false;
        }

        return string.Equals(current.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.Host, target.Host, StringComparison.OrdinalIgnoreCase) &&
            current.Port == target.Port;
    }

    /// <summary>
    /// Logs downloads and reports completion so the shell can surface a native
    /// "open folder" affordance. WebView2's own download UI is left in place --
    /// suppressing it without building a replacement would remove the only
    /// progress signal the user has.
    /// </summary>
    private void OnDownloadStarting(
        CoreWebView2 sender,
        CoreWebView2DownloadStartingEventArgs args,
        int hostGeneration)
    {
        if (!IsCurrentHost(hostGeneration))
        {
            return;
        }

        var operation = args.DownloadOperation;
        _logger.Info(
            "webview.download.starting",
            new { uri = operation.Uri, path = operation.ResultFilePath });

        operation.StateChanged += (downloadSender, _) =>
        {
            if (downloadSender is not CoreWebView2DownloadOperation download)
            {
                return;
            }

            switch (download.State)
            {
                case CoreWebView2DownloadState.Completed:
                    _logger.Info("webview.download.completed", new { path = download.ResultFilePath });
                    DownloadCompleted?.Invoke(download.ResultFilePath);
                    break;
                case CoreWebView2DownloadState.Interrupted:
                    _logger.Warning(
                        $"Download interrupted: {download.InterruptReason} ({download.Uri})");
                    break;
            }
        };
    }
}
