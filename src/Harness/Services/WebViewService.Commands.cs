// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace Harness.Services;

public partial class WebViewService
{
    private static readonly TimeSpan CommandScriptTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Stops navigation while loading, or sends one run-owned hosted stop transaction.
    /// </summary>
    public async Task StopAsync()
    {
        var coreWebView = GetCoreWebView();
        if (coreWebView is null)
        {
            return;
        }

        var pageVersion = _messageOwnership.CaptureAcceptedPageVersion();
        if (pageVersion == 0)
        {
            StopCurrentNavigation(coreWebView);
            return;
        }

        var aborted = await TryAbortActiveRunAsync(coreWebView, pageVersion);
        if (aborted)
        {
            _logger.Info("Triggered the hosted UI stop action.");
            return;
        }

        // The page owns every fallback under the originally captured run. A
        // timeout/false result must not issue another command against a new SPA
        // conversation or stop the network activity of an unrelated loaded page.
        _logger.Info("Hosted UI stop was unavailable or its original target expired.");
    }

    private void StopCurrentNavigation(CoreWebView2 coreWebView)
    {
        if (_isDisposed || !ReferenceEquals(coreWebView, _coreWebView))
        {
            return;
        }

        try
        {
            coreWebView.Stop();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _logger.Warning($"Stop skipped because CoreWebView2 became unavailable: {ex.Message}");
            return;
        }

        CancelActiveNavigation();
        if (CurrentState == ConnectionState.Loading)
        {
            SetState(ConnectionState.Offline);
        }
    }

    /// <summary>
    /// Attempts to use the hosted UI's built-in stop/abort affordance before falling back to command injection.
    /// </summary>
    public async Task<bool> TryAbortActiveRunAsync()
    {
        var coreWebView = GetCoreWebView();
        if (coreWebView is null)
        {
            return false;
        }

        var pageVersion = _messageOwnership.CaptureAcceptedPageVersion();
        if (pageVersion == 0)
        {
            return false;
        }

        return await TryAbortActiveRunAsync(coreWebView, pageVersion);
    }

    private async Task<bool> TryAbortActiveRunAsync(CoreWebView2 coreWebView, int pageVersion)
    {
        try
        {
            var result = await ExecuteCommandScriptAsync(coreWebView, pageVersion, WebViewCommandScripts.AbortRun);
            if (!IsStillCurrentWebViewCommandTarget(coreWebView, pageVersion))
            {
                return false;
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.Warning($"Hosted UI stop action timed out after {CommandScriptTimeout.TotalSeconds:0.#}s.");
            return false;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _logger.Warning($"Abort skipped because CoreWebView2 became unavailable: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to trigger hosted UI stop action: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ExecuteCommandScriptAsync(CoreWebView2 coreWebView, int pageVersion, string script)
    {
        using var timeout = new CancellationTokenSource(CommandScriptTimeout);
        return await WebViewScriptExecutor.ExecuteBooleanAsync(
            coreWebView, _messageOwnership, pageVersion, script, timeout.Token, CommandScriptTimeout);
    }

    private bool IsStillCurrentWebViewCommandTarget(CoreWebView2 coreWebView, int pageVersion)
    {
        return !_isDisposed &&
            ReferenceEquals(coreWebView, _coreWebView) &&
            _messageOwnership.IsCurrentAcceptedPageVersion(pageVersion);
    }
}
