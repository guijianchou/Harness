// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Runtime.InteropServices;
using Harness.Helpers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Harness.Services;

public partial class WebViewService
{
    private static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(30);
    private CancellationTokenSource? _initializationCancellation;

    /// <summary>
    /// Initializes the WebView2 control with a custom user data folder.
    /// </summary>
    public async Task InitializeAsync(WebView2 webView, string environmentId, string environmentName, CancellationToken cancellationToken = default)
    {
        if (_isDisposed || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        DetachCurrentWebView();
        _webView = webView;
        _coreWebView = null;
        var initializationGeneration = _generations.Next();
        _messageOwnership.ResetForNewWebView();
        CurrentEnvironmentName = environmentName;
        CurrentEnvironmentId = environmentId;
        _isInitialized = false;
        var initializationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _initializationCancellation = initializationCancellation;
        initializationCancellation.CancelAfter(InitializationTimeout);
        var initializationToken = initializationCancellation.Token;

        try
        {
            var userDataFolder = await Task.Run(() => PrepareUserDataFolderForEnvironment(environmentId), initializationToken);
            initializationToken.ThrowIfCancellationRequested();
            if (!IsCurrentInitialization(webView, initializationGeneration))
            {
                return;
            }

            // Create an explicit environment per user data folder. The
            // WEBVIEW2_USER_DATA_FOLDER variable only influences the process-wide
            // default environment, which is created once and cached, so it cannot
            // switch profiles when the user changes environments at runtime.
            var environmentOperation = CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: null).AsTask();
            await WaitForInitializationOperationAsync(environmentOperation, initializationToken);
            var webView2Environment = await environmentOperation;
            initializationToken.ThrowIfCancellationRequested();

            if (!IsCurrentInitialization(webView, initializationGeneration))
            {
                return;
            }

            await WaitForInitializationOperationAsync(webView.EnsureCoreWebView2Async(webView2Environment).AsTask(), initializationToken);
            initializationToken.ThrowIfCancellationRequested();

            if (!IsCurrentInitialization(webView, initializationGeneration))
            {
                return;
            }

            var coreWebView = TryGetCoreWebView2(webView);
            if (coreWebView is null)
            {
                throw new InvalidOperationException("CoreWebView2 became unavailable immediately after initialization.");
            }

            if (!IsCurrentInitialization(webView, initializationGeneration))
            {
                return;
            }

            _coreWebView = coreWebView;

            ApplyWebViewColorScheme();
            webView.ActualThemeChanged += OnWebViewActualThemeChanged;

            // Set default background to transparent (blends with Mica)
            webView.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;

            var hostGeneration = _hostGeneration;
            AttachContentZoom(coreWebView, hostGeneration);
            _navigationStartingHandler = CreateNavigationStartingHandler(hostGeneration);
            _navigationCompletedHandler = CreateNavigationCompletedHandler(hostGeneration);
            _processFailedHandler = CreateProcessFailedHandler(hostGeneration);
            _webMessageReceivedHandler = CreateWebMessageReceivedHandler(hostGeneration);

            _newWindowRequestedHandler = CreateNewWindowRequestedHandler(hostGeneration);
            _downloadStartingHandler = CreateDownloadStartingHandler(hostGeneration);

            coreWebView.NavigationStarting += _navigationStartingHandler;
            coreWebView.NavigationCompleted += _navigationCompletedHandler;
            coreWebView.ProcessFailed += _processFailedHandler;
            coreWebView.WebMessageReceived += _webMessageReceivedHandler;
            coreWebView.NewWindowRequested += _newWindowRequestedHandler;
            coreWebView.DownloadStarting += _downloadStartingHandler;

            // Allow file input dialog
            coreWebView.Settings.AreDefaultContextMenusEnabled = true;
            coreWebView.Settings.IsStatusBarEnabled = false;
            coreWebView.Settings.AreDevToolsEnabled = true;

            coreWebView.Settings.IsGeneralAutofillEnabled = true;

            _isInitialized = true;
            _logger.Info("WebView2 initialized successfully.");
        }
        catch (OperationCanceledException) when (initializationToken.IsCancellationRequested)
        {
            if (!IsCurrentInitialization(webView, initializationGeneration))
            {
                return;
            }

            DetachCurrentWebView();
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.Info("WebView2 initialization cancelled.");
                return;
            }

            _requiresHostRecreation = true;
            _logger.Warning("webview.initialization.timed_out");
            _statusInspector.SetUnavailableSnapshot("WebView2 initialization timed out.");
            SetState(ConnectionState.Error);
            NavigationErrorOccurred?.Invoke(StringResources.WebViewInitializationTimedOut);
        }
        catch (Exception ex)
        {
            if (!IsCurrentInitialization(webView, initializationGeneration))
            {
                _logger.Info("webview.initialization.stale_failure", new { ex.Message });
                return;
            }

            _logger.Error($"WebView2 initialization failed: {ex}");
            DetachCurrentWebView();
            _requiresHostRecreation = true;
            SetState(ConnectionState.Error);
            NavigationErrorOccurred?.Invoke($"WebView2 initialization failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_initializationCancellation, initializationCancellation))
            {
                _initializationCancellation = null;
            }
            initializationCancellation.Dispose();
        }
    }

    private static async Task WaitForInitializationOperationAsync(Task operation, CancellationToken cancellationToken)
    {
        try
        {
            await operation.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // WinRT initialization may ignore cancellation. Observe a late failure
            // without retaining or accessing the retired control, service, or logger.
            _ = operation.ContinueWith(static completed => { _ = completed.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    public void DetachCurrentWebViewHost()
    {
        if (_isDisposed)
        {
            return;
        }

        DetachCurrentWebView();
    }

    private void DetachCurrentWebView()
    {
        var initializationCancellation = _initializationCancellation;
        _initializationCancellation = null;
        ClearErrorPageOwnership();
        if (_webView is not null)
        {
            _webView.ActualThemeChanged -= OnWebViewActualThemeChanged;
        }

        CancelStatusProbeLoop();
        StopHeartbeat();
        CancelNavigationStartWatchdog();
        CancelNavigationCompletionWatchdog();
        _hostGeneration++;
        _generations.Next();
        initializationCancellation?.Cancel();
        _messageOwnership.BeginNavigation();
        InvalidateControlUiInspectionCache();
        CancelNavigationCancellation();

        var coreWebView = _coreWebView ?? TryGetCoreWebView2(_webView);
        try
        {
            if (coreWebView is not null)
            {
                if (_zoomDocumentLoadedHandler is not null)
                {
                    coreWebView.DOMContentLoaded -= _zoomDocumentLoadedHandler;
                }
                if (_navigationStartingHandler is not null)
                {
                    coreWebView.NavigationStarting -= _navigationStartingHandler;
                }

                if (_navigationCompletedHandler is not null)
                {
                    coreWebView.NavigationCompleted -= _navigationCompletedHandler;
                }

                if (_processFailedHandler is not null)
                {
                    coreWebView.ProcessFailed -= _processFailedHandler;
                }

                if (_webMessageReceivedHandler is not null)
                {
                    coreWebView.WebMessageReceived -= _webMessageReceivedHandler;
                }

                if (_newWindowRequestedHandler is not null)
                {
                    coreWebView.NewWindowRequested -= _newWindowRequestedHandler;
                }

                if (_downloadStartingHandler is not null)
                {
                    coreWebView.DownloadStarting -= _downloadStartingHandler;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _logger.Info("webview.detach.closed_core", new { ex.Message });
        }

        _zoomDocumentLoadedHandler = null;
        _navigationStartingHandler = null;
        _navigationCompletedHandler = null;
        _processFailedHandler = null;
        _webMessageReceivedHandler = null;
        _newWindowRequestedHandler = null;
        _downloadStartingHandler = null;
        _webView = null;
        _coreWebView = null;
        _isInitialized = false;
        _requiresHostRecreation = false;
        CurrentEnvironmentId = null;
        CurrentEnvironmentName = null;
        _statusInspector.SetUnknownSnapshot();
        _lastPublishedControlUiSnapshot = ControlUiProbeSnapshot.Unknown;
        _lastLifecycleLogKey = null;
    }

    public void Dispose()
    {
        _isDisposed = true;
        DetachCurrentWebView();
        CancelNavigationStartWatchdog();
        CancelNavigationCompletionWatchdog();
        CancelNavigationCancellation();
        _statusInspector.Dispose();
        _heartbeatRuntime.Dispose();
    }

    private static CoreWebView2? TryGetCoreWebView2(WebView2? webView)
    {
        if (webView is null)
        {
            return null;
        }

        try
        {
            return webView.CoreWebView2;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return null;
        }
    }

    private CoreWebView2? GetCoreWebView()
    {
        if (_requiresHostRecreation)
        {
            return null;
        }

        if (_coreWebView is not null)
        {
            return _coreWebView;
        }

        _coreWebView = TryGetCoreWebView2(_webView);
        return _coreWebView;
    }

    private bool IsCurrentInitialization(WebView2 webView, int generation)
    {
        return !_isDisposed &&
            _generations.IsCurrent(generation) &&
            ReferenceEquals(webView, _webView);
    }

    private bool IsCurrentHost(int hostGeneration)
    {
        return !_isDisposed &&
            !_requiresHostRecreation &&
            _coreWebView is not null &&
            _hostGeneration == hostGeneration;
    }

    private bool IsCurrentNavigation(ulong navigationId, int generation, int hostGeneration)
    {
        return !_isDisposed &&
            navigationId == _currentNavigationId &&
            _generations.IsCurrent(generation) &&
            IsCurrentHost(hostGeneration);
    }
}
