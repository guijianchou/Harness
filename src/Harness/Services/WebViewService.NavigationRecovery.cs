// Copyright (c) Lanstack @openclaw. All rights reserved.

using Microsoft.Web.WebView2.Core;

namespace Harness.Services;

public partial class WebViewService
{
    private async Task<AutoRetryOutcome> TryAutoRetryAfterConnectionErrorAsync(
        CoreWebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args,
        int hostGeneration)
    {
        if (!AutoRefreshEnabled || _retryCount >= MaxRetries || string.IsNullOrEmpty(_lastNavigatedUrl))
        {
            return AutoRetryOutcome.NotAttempted;
        }

        _retryCount++;
        var retryGeneration = _generations.Current;
        var retryNavigationId = args.NavigationId;
        var navigationCancellation = _navigationCancellation;
        using var navigationLease = navigationCancellation?.TryAcquire();
        if (navigationLease is null)
        {
            return AutoRetryOutcome.Stale;
        }

        var token = navigationLease.Token;
        _logger.Info($"Auto-retry {_retryCount}/{MaxRetries} in {RetryDelay.TotalSeconds}s...");

        try
        {
            await Task.Delay(RetryDelay, token);
        }
        catch (TaskCanceledException)
        {
            _logger.Info("Auto-retry cancelled (new navigation started).");
            return AutoRetryOutcome.Stale;
        }

        if (!AutoRefreshEnabled)
        {
            return AutoRetryOutcome.NotAttempted;
        }

        var coreWebView = GetCoreWebView();
        if (_isDisposed ||
            !_generations.IsCurrent(retryGeneration) ||
            retryNavigationId != _currentNavigationId ||
            !IsCurrentHost(hostGeneration) ||
            coreWebView is null ||
            string.IsNullOrEmpty(_lastNavigatedUrl))
        {
            return AutoRetryOutcome.Stale;
        }

        var navigationGeneration = PrepareNavigationStart();
        var previousSource = coreWebView.Source;
        ObserveNavigationStartTimeout(navigationGeneration, _lastNavigatedUrl, previousSource);
        if (!TryNavigateCoreWebView(coreWebView, _lastNavigatedUrl, "Auto-retry"))
        {
            CancelNavigationStartWatchdog();
            CancelNavigationCancellation();
            return AutoRetryOutcome.Failed;
        }

        return AutoRetryOutcome.Started;
    }

    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args, int hostGeneration)
    {
        if (!IsCurrentHost(hostGeneration) || _coreWebView is null)
        {
            return;
        }

        var kind = args.ProcessFailedKind;
        if (kind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited))
        {
            // Auxiliary processes recover independently. An unresponsive renderer may
            // resume; keep its page ownership so a slow script cannot force a reload.
            _logger.Warning("webview.process.failure_observed", new { kind, args.Reason });
            return;
        }

        var requiresRecreation = kind == CoreWebView2ProcessFailedKind.BrowserProcessExited;
        if (requiresRecreation)
        {
            _requiresHostRecreation = true;
            _isInitialized = false;
            StopHeartbeat();
        }

        CancelActiveNavigation();
        _statusInspector.SetUnavailableSnapshot("Browser process failed.");
        _logger.Error($"WebView2 process failed: {args.Reason} ({args.ProcessFailedKind})");
        SetState(ConnectionState.Error);
        NavigationErrorOccurred?.Invoke($"Browser process failed: {args.Reason}");
        if (requiresRecreation)
        {
            HostRecreationRequired?.Invoke();
        }
    }

    private enum AutoRetryOutcome
    {
        NotAttempted,
        Started,
        Stale,
        Failed,
    }
}
