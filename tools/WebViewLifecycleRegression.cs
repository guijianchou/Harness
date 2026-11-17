// Copyright (c) Lanstack @openclaw. All rights reserved.
// Executes production lifecycle/event bodies against deterministic platform doubles.
// This does not launch WebView2 or claim to measure native crash recovery latency.

using Harness.Services;
using Harness.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

public static class WebViewLifecycleRegression
{
    public static async Task RunAsync()
    {
        var folder = Directory.CreateTempSubdirectory("Harness-lifecycle-regression-");
        WebViewService.ProfileRoot = folder.FullName;
        try
        {
            await VerifyInitializationAsync();
            await VerifyInitializationTimeoutAsync();
            await VerifyPopupNavigationAsync();
            await VerifyCompletionAsync();
            await VerifyProcessFailuresAsync();
            await VerifyViewModelRecoveryAsync();
            VerifyRecreation();
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static async Task VerifyInitializationAsync()
    {
        var service = new WebViewService();
        var pendingEnvironment = new TaskCompletionSource<CoreWebView2Environment>();
        CoreWebView2Environment.Factory = () => pendingEnvironment.Task;
        var outgoing = new WebView2();
        var oldInitialization = service.InitializeAsync(outgoing, "old");
        CoreWebView2Environment.Factory = () => Task.FromResult(new CoreWebView2Environment());
        var replacement = new WebView2();
        await service.InitializeAsync(replacement, "new");
        pendingEnvironment.SetResult(new CoreWebView2Environment());
        await oldInitialization;
        Check(outgoing.EnsureCount == 0 && service.IsInitialized && service.HasHost(replacement), "retired environment creation never initializes the outgoing control");

        var pendingEnsure = new TaskCompletionSource();
        outgoing = new WebView2 { EnsureOperation = pendingEnsure.Task };
        oldInitialization = service.InitializeAsync(outgoing, "pending");
        await service.InitializeAsync(replacement, "current");
        pendingEnsure.SetException(new InvalidOperationException("old initialization failed"));
        await oldInitialization;
        Check(service.ErrorCount == 0 && service.IsInitialized && service.HasHost(replacement), "late initialization failure cannot mark the replacement host as failed");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await service.InitializeAsync(new WebView2(), "cancelled", cancelled.Token);
        Check(service.IsInitialized && service.HasHost(replacement), "already-cancelled initialization preserves the live host");
        await service.InitializeAsync(new WebView2 { EnsureOperation = Task.FromException(new InvalidOperationException("current failure")) }, "failure");
        Check(service.ErrorCount == 1 && service.State == ConnectionState.Error, "current initialization failures remain actionable");
        Check(service.RequiresHostRecreation && !service.CanAccessCore, "failed initialization retires its host and enables manual recreation");
    }

    private static async Task VerifyInitializationTimeoutAsync()
    {
        var service = new WebViewService();
        var environment = new TaskCompletionSource<CoreWebView2Environment>();
        CoreWebView2Environment.Factory = () => environment.Task;
        var retired = new WebView2();
        var initialization = service.InitializeAsync(retired, "environment-timeout");
        service.ExpireInitializationForTest();
        await initialization.WaitAsync(TimeSpan.FromSeconds(2));
        Check(service.ErrorCount == 1 && service.RequiresHostRecreation && !service.CanAccessCore && !service.HasHost(retired),
            "hung environment creation releases initialization with an actionable replacement state");
        var viewModel = new MainViewModel(service, autoRefresh: false);
        viewModel.RetryForTest();
        Check(viewModel.RecreationRequests.Count == 1 && viewModel.RecreationRequests[0] == "manual_retry",
            "initialization timeout supports manual Retry with AutoRefresh disabled");

        CoreWebView2Environment.Factory = () => Task.FromResult(new CoreWebView2Environment());
        var replacement = new WebView2();
        await service.InitializeAsync(replacement, "after-timeout");
        environment.SetResult(new CoreWebView2Environment());
        Check(retired.EnsureCount == 0 && service.IsInitialized && service.HasHost(replacement),
            "late environment completion cannot initialize or replace the retired control");

        var ensure = new TaskCompletionSource();
        retired = new WebView2 { EnsureOperation = ensure.Task };
        initialization = service.InitializeAsync(retired, "ensure-timeout");
        service.ExpireInitializationForTest();
        await initialization.WaitAsync(TimeSpan.FromSeconds(2));
        Check(service.ErrorCount == 2 && service.RequiresHostRecreation && service.HasNoHandlers,
            "hung controller creation releases initialization without attaching handlers");
        await service.InitializeAsync(replacement, "replacement");
        ensure.SetException(new InvalidOperationException("late native controller failure"));
        Check(service.IsInitialized && service.HasHost(replacement) && service.ErrorCount == 2,
            "late controller failure cannot publish an error for its replacement");

        ensure = new TaskCompletionSource();
        using var cancelled = new CancellationTokenSource();
        retired = new WebView2 { EnsureOperation = ensure.Task };
        initialization = service.InitializeAsync(retired, "cancelled-ensure", cancelled.Token);
        cancelled.Cancel();
        await initialization.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!service.HasHost(retired) && !service.RequiresHostRecreation && service.ErrorCount == 2,
            "caller cancellation releases a hung initialization without reporting a timeout");
        ensure.SetResult();
    }

    private static async Task VerifyPopupNavigationAsync()
    {
        var service = new WebViewService();
        var control = new WebView2();
        await service.InitializeAsync(control, "popup");
        service.Navigate("https://gateway.example/chat/main/original");
        var ownershipVersion = service.OwnershipVersion;
        var target = "https://gateway.example/chat/main/new-session";
        control.CoreWebView2.FailNextNavigate = true;
        Check(service.OpenPopupForTest(target) && service.State == ConnectionState.Error,
            "a same-origin popup is handled even if the native navigation command fails");
        Check(service.OwnershipVersion > ownershipVersion && service.LastWatchdogTarget == target,
            "same-origin popup invalidates old ownership and starts its navigation watchdog");
        Check(service.RetryNavigation() && control.CoreWebView2.NavigationRequests[^1] == target,
            "Retry after a failed same-origin popup preserves its deep link");
        var attempts = control.CoreWebView2.NavigationRequests.Count;
        Check(!service.OpenPopupForTest(target, staleHost: true) && control.CoreWebView2.NavigationRequests.Count == attempts,
            "a retired host popup cannot navigate the replacement page");
        Check(service.OpenPopupForTest("https://external.example/help") && service.ExternalTargets.Count == 1 &&
            control.CoreWebView2.NavigationRequests.Count == attempts,
            "cross-origin popups retain the external-browser route");
    }

    private static async Task VerifyCompletionAsync()
    {
        var service = new WebViewService();
        await service.InitializeAsync(new WebView2(), "completion");
        var pending = new TaskCompletionSource();
        var completion = service.CompleteAsync(1, pending.Task);
        service.BeginNewNavigation(2);
        pending.SetException(new InvalidOperationException("retired completion"));
        await completion;
        Check(service.ErrorCount == 0 && service.UnavailableCount == 0, "late navigation exception cannot overwrite a newer page");

        pending = new TaskCompletionSource();
        completion = service.CompleteAsync(2, pending.Task);
        await service.InitializeAsync(new WebView2(), "replacement");
        pending.SetException(new InvalidOperationException("retired host"));
        await completion;
        Check(service.ErrorCount == 0, "late completion exception cannot overwrite a replacement WebView");
        await service.CompleteAsync(3, Task.FromException(new InvalidOperationException("current completion")));
        Check(service.ErrorCount == 1 && service.UnavailableCount == 1, "owned navigation exceptions still publish unavailable and error");
    }

    private static async Task VerifyProcessFailuresAsync()
    {
        foreach (var kind in Enum.GetValues<CoreWebView2ProcessFailedKind>())
        {
            var service = new WebViewService();
            var control = new WebView2();
            await service.InitializeAsync(control, kind.ToString());
            var cancellationCount = service.CancellationCount;
            var recreations = 0;
            service.HostRecreationRequired += () =>
            {
                Check(!service.IsInitialized && !service.CanAccessCore, "browser exit retires the Core before notifying the host");
                recreations++;
            };
            service.FailProcess(kind);
            if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                service.FailProcess(kind);
                Check(recreations == 1 && !service.IsInitialized && service.ErrorCount == 1 && service.CancellationCount > cancellationCount,
                    "browser exit requires exactly one host replacement and cancels navigation");
                control.CoreWebView2.ThrowOnRemove = true;
                service.DetachForTest();
                Check(!service.CanAccessCore && !service.IsInitialized && service.HasNoHandlers, "closed Core event-removal failure cannot interrupt detach cleanup");
                await service.InitializeAsync(new WebView2(), "after-crash");
                Check(service.IsInitialized && service.CanAccessCore, "host initialization recovers after a closed Core");
            }
            else if (kind == CoreWebView2ProcessFailedKind.RenderProcessExited)
            {
                Check(recreations == 0 && service.IsInitialized && service.ErrorCount == 1 && service.CancellationCount > cancellationCount,
                    "main renderer exit retires the page but keeps the reloadable controller");
            }
            else
            {
                Check(recreations == 0 && service.IsInitialized && service.ErrorCount == 0 && service.CancellationCount == cancellationCount,
                    kind + " preserves page ownership and does not force a reload");
            }
        }
    }

    private static async Task VerifyViewModelRecoveryAsync()
    {
        var service = new WebViewService();
        await service.InitializeAsync(new WebView2(), "manual-recovery");
        var viewModel = new MainViewModel(service, autoRefresh: false);
        service.FailProcess(CoreWebView2ProcessFailedKind.BrowserProcessExited);
        service.FailProcess(CoreWebView2ProcessFailedKind.BrowserProcessExited);
        viewModel.DrainUiUpdates();
        Check(viewModel.RecreationRequests.Count == 0 && viewModel.ResourceRefreshCount == 1,
            "browser exit updates scheduling without automatic host replacement when AutoRefresh is disabled");
        viewModel.RetryForTest();
        Check(viewModel.RecreationRequests.Count == 1 && viewModel.RecreationRequests[0] == "manual_retry",
            "manual Retry replaces a crashed host while AutoRefresh is disabled");
        viewModel.ReloadForTest();
        Check(viewModel.RecreationRequests.Count == 2 && viewModel.RecreationRequests[1] == "manual_reload" && service.ReloadCount == 0,
            "manual Reload replaces a crashed host while AutoRefresh is disabled");

        service = new WebViewService();
        await service.InitializeAsync(new WebView2(), "owned-crash");
        viewModel = new MainViewModel(service, autoRefresh: true);
        service.FailProcess(CoreWebView2ProcessFailedKind.BrowserProcessExited);
        service.FailProcess(CoreWebView2ProcessFailedKind.BrowserProcessExited);
        viewModel.DrainUiUpdates();
        Check(viewModel.RecreationRequests.Count == 1 && viewModel.RecreationRequests[0] == "browser_process_exited" && viewModel.ResourceRefreshCount == 1,
            "an owned browser crash requests exactly one automatic replacement");

        service = new WebViewService();
        await service.InitializeAsync(new WebView2(), "retired-crash");
        viewModel = new MainViewModel(service, autoRefresh: true);
        service.FailProcess(CoreWebView2ProcessFailedKind.BrowserProcessExited);
        var pendingEnsure = new TaskCompletionSource();
        var replacement = new WebView2 { EnsureOperation = pendingEnsure.Task };
        var initialization = service.InitializeAsync(replacement, "replacement-initializing");
        Check(!service.IsInitialized && !service.RequiresHostRecreation,
            "a replacement being initialized is distinct from a crashed host");
        viewModel.DrainUiUpdates();
        Check(viewModel.RecreationRequests.Count == 0 && viewModel.ResourceRefreshCount == 0,
            "a queued crash callback cannot recreate a newer host that is still initializing");
        pendingEnsure.SetResult();
        await initialization;

        service = new WebViewService();
        await service.InitializeAsync(new WebView2(), "auto-disabled-before-dispatch");
        viewModel = new MainViewModel(service, autoRefresh: true);
        service.FailProcess(CoreWebView2ProcessFailedKind.BrowserProcessExited);
        viewModel.SetAutomaticRecoveryForTest(false);
        viewModel.DrainUiUpdates();
        Check(viewModel.RecreationRequests.Count == 0,
            "disabling AutoRefresh before a queued crash callback suppresses automatic replacement");

        service = new WebViewService();
        var liveControl = new WebView2();
        await service.InitializeAsync(liveControl, "healthy-manual-navigation");
        service.Navigate("https://gateway.example/chat/main");
        viewModel = new MainViewModel(service, autoRefresh: false);
        viewModel.RetryForTest();
        viewModel.ReloadForTest();
        Check(viewModel.RecreationRequests.Count == 0 && liveControl.CoreWebView2.NavigationRequests.Count == 2 && service.ReloadCount == 1 && !viewModel.IsErrorVisible && !viewModel.ShowRetryButton,
            "manual Retry and Reload retain the navigation path for a live host");
    }

    private static void VerifyRecreation()
    {
        var service = new WebViewRecreationService();
        service.Schedule("browser_process_exited");
        service.Defer("browser_process_exited");
        Check(service.TryCancelAutomaticRecovery(out var cancelled) && cancelled.CancelledPending && cancelled.CancelledDeferred && !service.HasPendingDeferredOrActiveWork,
            "disabling automatic recovery cancels queued and deferred browser replacement");
        service.Schedule("browser_process_exited");
        Check(service.TryBegin(true).ShouldBegin, "browser replacement can begin");
        Check(!service.TryCancelNavigationTimeoutRecovery(out _), "late navigation success cannot cancel browser-process replacement");
        Check(service.TryCancelAutomaticRecovery(out cancelled) && cancelled.CancelledActive && service.ShouldSkipCurrentRecreation(),
            "disabling automatic recovery cancels active browser replacement");
        service.Schedule("manual_retry");
        Check(service.TryConsumeQueued(out var reason) && reason == "manual_retry" && !service.ShouldSkipCurrentRecreation(),
            "a queued manual retry is not poisoned by the cancelled automatic operation");
        service.Finish();

        service.Schedule("environment_profile_changed");
        service.Schedule("browser_process_exited");
        service.Defer("settings_changed");
        service.Defer("browser_process_exited");
        Check(!service.TryCancelAutomaticRecovery(out _) && service.TryBegin(true).Reason == "environment_profile_changed" &&
            service.TryConsumeDeferred(out reason) && reason == "settings_changed", "crash recovery cannot replace or cancel explicit environment/settings work");
        service.Finish();
        for (var index = 0; index < 5; index++)
        {
            service.RecordAttempt();
        }
        foreach (var automaticReason in new[] { "browser_process_exited", "navigation_start_timeout", "initial_load", "settings_changed" })
        {
            service.Schedule(automaticReason);
            Check(!service.CanAttemptInLoop(), automaticReason + " cannot reset the recreation circuit breaker");
        }
        service.Schedule("manual_retry");
        Check(service.CanAttemptInLoop(), "explicit Retry resets the circuit breaker");
        for (var index = 0; index < 5; index++)
        {
            service.RecordAttempt();
        }
        service.Schedule("manual_reload");
        Check(service.CanAttemptInLoop(), "explicit Reload resets the circuit breaker");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
        Console.WriteLine("PASS: " + message);
    }
}

namespace Harness.Services
{
    public partial class WebViewService
    {
        public static string ProfileRoot { get; set; } = "";
        private WebView2? _webView;
        private CoreWebView2? _coreWebView;
        private bool _isInitialized;
        private bool _isDisposed;
        private bool _requiresHostRecreation;
        private static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(30);
        private CancellationTokenSource? _initializationCancellation;
        private string? _lastNavigatedUrl;
        private int _retryCount;
        private string? _errorPageToken;
        private ulong _errorPageNavigationId;
        private int _heartbeatConnectingCount;
        private string? _lastHeartbeatObservationKey;
        private int _hostGeneration;
        private readonly WebViewGenerationTracker _generations = new();
        private readonly TestOwnership _messageOwnership = new();
        private readonly TestLogger _logger = new();
        private readonly TestInspector _statusInspector = new();
        private const ulong NoCurrentNavigationId = ulong.MaxValue;
        private ulong _currentNavigationId = NoCurrentNavigationId;
        private string? _lastLifecycleLogKey;
        private string? _lastReportedIssueKey;
        private object? _lastPublishedControlUiSnapshot;
        private Action? _zoomDocumentLoadedHandler;
        private Action? _navigationStartingHandler;
        private Action? _navigationCompletedHandler;
        private Action? _processFailedHandler;
        private Action? _webMessageReceivedHandler;
        private Action? _newWindowRequestedHandler;
        private Action? _downloadStartingHandler;
        private Task _completionOperation = Task.CompletedTask;
        public event Action<string>? NavigationErrorOccurred;
        public event Action? HostRecreationRequired;
        public string? CurrentEnvironmentName { get; private set; }
        public ConnectionState State { get; private set; } = ConnectionState.Connected;
        public int ErrorCount { get; private set; }
        public int UnavailableCount => _statusInspector.UnavailableCount;
        public int CancellationCount { get; private set; }
        public int ReloadCount { get; private set; }
        public int OwnershipVersion => _messageOwnership.Version;
        public string? LastWatchdogTarget { get; private set; }
        public List<string> ExternalTargets { get; } = new();
        public bool CanAccessCore => GetCoreWebView() is not null;
        public bool HasHost(WebView2 control) => ReferenceEquals(_webView, control);
        public bool HasNoHandlers => _zoomDocumentLoadedHandler is null && _navigationStartingHandler is null && _navigationCompletedHandler is null &&
            _processFailedHandler is null && _webMessageReceivedHandler is null && _newWindowRequestedHandler is null && _downloadStartingHandler is null;
        public WebViewService() { NavigationErrorOccurred += _ => ErrorCount++; }
        public void DetachForTest() => DetachCurrentWebView();
        public void ExpireInitializationForTest() => _initializationCancellation!.Cancel();
        public bool OpenPopupForTest(string target, bool staleHost = false)
        {
            var args = new CoreWebView2NewWindowRequestedEventArgs { Uri = target };
            OnNewWindowRequested(_coreWebView!, args, _hostGeneration - (staleHost ? 1 : 0));
            return args.Handled;
        }
        public bool Reload() { ReloadCount++; return true; }
        public void BeginNewNavigation(ulong id) { _generations.Next(); _currentNavigationId = id; }
        public Task CompleteAsync(ulong id, Task operation)
        {
            _currentNavigationId = id;
            _completionOperation = operation;
            return OnNavigationCompleted(_coreWebView!, new CoreWebView2NavigationCompletedEventArgs { NavigationId = id }, _hostGeneration);
        }
        public void FailProcess(CoreWebView2ProcessFailedKind kind) => OnProcessFailed(_coreWebView!, new CoreWebView2ProcessFailedEventArgs { ProcessFailedKind = kind }, _hostGeneration);
        private Task HandleNavigationCompletedAsync(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args, int hostGeneration) => _completionOperation;
        private static string GetUserDataFolderForEnvironment(string name) => Path.Combine(ProfileRoot, name);
        private void SetState(ConnectionState state) => State = state;
        private void ApplyWebViewColorScheme() { }
        private void OnWebViewActualThemeChanged() { }
        private void AttachContentZoom(CoreWebView2 core, int generation) { _zoomDocumentLoadedHandler = () => { }; core.DOMContentLoaded += _zoomDocumentLoadedHandler; }
        private Action CreateNavigationStartingHandler(int generation) => () => { };
        private Action CreateNavigationCompletedHandler(int generation) => () => { };
        private Action CreateProcessFailedHandler(int generation) => () => { };
        private Action CreateWebMessageReceivedHandler(int generation) => () => { };
        private Action CreateNewWindowRequestedHandler(int generation) => () => { };
        private Action CreateDownloadStartingHandler(int generation) => () => { };
        private void ClearErrorPageOwnership() { }
        private void ReplaceNavigationCancellation() { }
        private void ObserveNavigationStartTimeout(int generation, string? target, string? previous) => LastWatchdogTarget = target;
        private void LogLifecycleEventOnce(string message, object? context = null) { }
        private void OpenInSystemBrowser(string target) => ExternalTargets.Add(target);
        private void CancelStatusProbeLoop() { }
        private void StopHeartbeat() { }
        private void CancelNavigationStartWatchdog() { }
        private void CancelNavigationCompletionWatchdog() { }
        private void InvalidateControlUiInspectionCache() { }
        private void CancelNavigationCancellation() => CancellationCount++;
        private sealed class TestOwnership { public int Version { get; private set; } public void ResetForNewWebView() { } public void BeginNavigation() => Version++; }
        private sealed class TestLogger { public void Info(string message, object? context = null) { } public void Warning(string message, object? context = null) { } public void Error(string message) { } }
        private sealed class TestInspector { public int UnavailableCount { get; private set; } public void SetUnknownSnapshot() { } public void SetUnavailableSnapshot(string message) => UnavailableCount++; }
    }
    public enum ConnectionState { Connected, Error, Loading }
    public static class ControlUiProbeSnapshot { public static object Unknown { get; } = new(); }
}

namespace Harness.Helpers
{
    public static class StringResources { public static string WebViewInitializationTimedOut => "Initialization timed out"; }
}

internal static class RegressionWinRtTaskExtensions
{
    public static Task AsTask(this Task operation) => operation;
    public static Task<T> AsTask<T>(this Task<T> operation) => operation;
}

namespace Harness.ViewModels
{
    public partial class MainViewModel
    {
        private readonly WebViewService _webViewService;
        private readonly Queue<Action> _uiUpdates = new();
        private readonly TestRuntime _runtime = new();
        private TestEnvironment? _selectedEnvironment = new();
        private bool _autoRefresh;
        private bool _isDisposed;
        public event Action<string>? WebViewRecreationRequested;
        public List<string> RecreationRequests { get; } = new();
        public int ResourceRefreshCount { get; private set; }
        public bool IsErrorVisible { get; set; } = true;
        public bool ShowRetryButton { get; set; } = true;
        public string? ErrorMessage { get; set; }

        public MainViewModel(WebViewService service, bool autoRefresh)
        {
            _webViewService = service;
            _autoRefresh = autoRefresh;
            _webViewService.HostRecreationRequired += OnHostRecreationRequired;
            WebViewRecreationRequested += RecreationRequests.Add;
        }

        public void RetryForTest() => OnRetry();
        public void ReloadForTest() => OnReload();
        public void SetAutomaticRecoveryForTest(bool enabled) => _autoRefresh = enabled;
        public void DrainUiUpdates()
        {
            while (_uiUpdates.TryDequeue(out var update)) { update(); }
        }
        private void DispatchUiUpdate(Action update) => _uiUpdates.Enqueue(update);
        private void RefreshResourceScheduling() => ResourceRefreshCount++;
        private sealed class TestEnvironment { public bool IsPlaceholder { get; set; } }
        private sealed class TestRuntime { public TestLogger Logger { get; } = new(); }
        private sealed class TestLogger { public void Warning(string message) { } }
        private static class StringResources { public static string RetryUnavailable => "Retry unavailable"; }
    }
}

namespace Microsoft.UI
{
    public static class Colors { public static object Transparent { get; } = new(); }
}

namespace Microsoft.UI.Xaml.Controls
{
    public sealed class WebView2
    {
        public CoreWebView2 CoreWebView2 { get; } = new();
        public Task EnsureOperation { get; init; } = Task.CompletedTask;
        public int EnsureCount { get; private set; }
        public object? DefaultBackgroundColor { get; set; }
        public event Action? ActualThemeChanged;
        public Task EnsureCoreWebView2Async(CoreWebView2Environment environment) { EnsureCount++; return EnsureOperation; }
    }
}

namespace Microsoft.Web.WebView2.Core
{
    public sealed class CoreWebView2Environment
    {
        public static Func<Task<CoreWebView2Environment>> Factory { get; set; } = () => Task.FromResult(new CoreWebView2Environment());
        public static Task<CoreWebView2Environment> CreateWithOptionsAsync(string? browserExecutableFolder, string userDataFolder, object? options) => Factory();
    }
    public sealed class CoreWebView2
    {
        public string Source { get; private set; } = "about:blank";
        public bool FailNextNavigate { get; set; }
        public List<string> NavigationRequests { get; } = new();
        public void Navigate(string target)
        {
            NavigationRequests.Add(target);
            if (FailNextNavigate)
            {
                FailNextNavigate = false;
                throw new InvalidOperationException("navigation failed");
            }
            Source = target;
        }
        public CoreSettings Settings { get; } = new();
        public bool ThrowOnRemove { get; set; }
        private void RemoveHandler() { if (ThrowOnRemove) { throw new System.Runtime.InteropServices.COMException("browser exited"); } }
        public event Action DOMContentLoaded { add { } remove { RemoveHandler(); } }
        public event Action NavigationStarting { add { } remove { RemoveHandler(); } }
        public event Action NavigationCompleted { add { } remove { RemoveHandler(); } }
        public event Action ProcessFailed { add { } remove { RemoveHandler(); } }
        public event Action WebMessageReceived { add { } remove { RemoveHandler(); } }
        public event Action NewWindowRequested { add { } remove { RemoveHandler(); } }
        public event Action DownloadStarting { add { } remove { RemoveHandler(); } }
    }
    public sealed class CoreSettings
    {
        public bool AreDefaultContextMenusEnabled { get; set; }
        public bool IsStatusBarEnabled { get; set; }
        public bool AreDevToolsEnabled { get; set; }
        public bool IsGeneralAutofillEnabled { get; set; }
    }
    public sealed class CoreWebView2NavigationCompletedEventArgs { public ulong NavigationId { get; set; } }
    public sealed class CoreWebView2NewWindowRequestedEventArgs { public string Uri { get; set; } = ""; public bool Handled { get; set; } }
    public sealed class CoreWebView2ProcessFailedEventArgs { public CoreWebView2ProcessFailedKind ProcessFailedKind { get; set; } public string Reason => "SyntheticFailure"; }
    public enum CoreWebView2ProcessFailedKind
    {
        BrowserProcessExited, RenderProcessExited, RenderProcessUnresponsive, FrameRenderProcessExited,
        UtilityProcessExited, SandboxHelperProcessExited, GpuProcessExited, PpapiPluginProcessExited,
        PpapiBrokerProcessExited, UnknownProcessExited
    }
}
