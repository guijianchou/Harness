// Copyright (c) Lanstack @openclaw. All rights reserved.
// Standalone native controller tests. WinUI presentation and coordinator edges are stubbed;
// command execution, page ownership, error rendering, action routing, and retry use current source.

using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using System.Xml.Linq;
using Harness.Services;
using Microsoft.Web.WebView2.Core;

public static class HarnessNativeRegression
{
    public static void Run(string loaderFolder, string repoRoot, string outputDirectory)
    {
        CoreWebView2Environment.SetLoaderDllFolderPath(loaderFolder);
        var profile = Directory.CreateTempSubdirectory("Harness-native-regression-");
        Harness.Helpers.StringResources.Load(Path.Combine(repoRoot, "src", "Harness", "Strings", "en-us", "Resources.resw"));
        VerifySettingsAndEnvironment(Path.Combine(profile.FullName, "settings-regression"));
        Exception? failure = null;
        CoreWebView2Controller? controller = null;
        var browserExited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var form = new Form { ShowInTaskbar = false, Opacity = 0, Size = new Size(1280, 800) };
        using var watchdog = new System.Windows.Forms.Timer { Interval = 60000 };
        watchdog.Tick += (_, _) => { failure = new TimeoutException("Native regression watchdog expired."); controller?.Close(); form.Close(); };
        form.Shown += async (_, _) =>
        {
            try
            {
                watchdog.Start();
                var environment = await CoreWebView2Environment.CreateAsync(null, profile.FullName);
                environment.BrowserProcessExited += (_, _) => browserExited.TrySetResult(true);
                controller = await environment.CreateCoreWebView2ControllerAsync(form.Handle);
                controller.Bounds = form.ClientRectangle;
                var core = controller.CoreWebView2;
                Console.WriteLine("WebView2 runtime: " + environment.BrowserVersionString);
                await NavigateAsync(core, () => { core.NavigateToString("<html><body>Native regression</body></html>"); return Task.CompletedTask; });
                await VerifyCommandsAsync(core);
                await VerifyErrorPageAsync(core, controller, environment, repoRoot, outputDirectory);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                watchdog.Stop();
                controller?.Close();
                if (controller is not null)
                {
                    try { await browserExited.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
                    catch (TimeoutException) { }
                }
                form.Close();
            }
        };
        Application.Run(form);
        try
        {
            profile.Delete(recursive: true);
        }
        catch (IOException)
        {
            Console.WriteLine("Temporary browser profile still in use: " + profile.FullName);
        }

        if (failure is not null)
        {
            throw new InvalidOperationException("Native WebView2 regression failed.", failure);
        }
    }

    private static async Task VerifyCommandsAsync(CoreWebView2 core)
    {
        var ownership = new WebViewMessageOwnership();
        ownership.AcceptPageToken(core.Source, "native-test-page");
        var version = ownership.CaptureAcceptedPageVersion();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task<bool> Execute(string script) => await WebViewScriptExecutor.ExecuteBooleanAsync(core, ownership, version, script, deadline.Token);
        Check(await Execute("true"), "synchronous command reply");
        Check(await Execute("Promise.resolve(true)"), "resolved Promise reply");
        Check(!await Execute("Promise.resolve(false)"), "unhandled Promise reply");
        Check(!await Execute("Promise.reject(new Error('synthetic failure'))"), "rejected Promise reply");
        await core.ExecuteScriptAsync("window.abortCount = 0; window.__openClawHostBridge = { onCommand: async () => { window.abortCount++; return true; } };");
        Check(await Execute(WebViewCommandScripts.AbortRun), "actual AbortRun script reports success");
        Check(await core.ExecuteScriptAsync("window.abortCount") == "1", "abort executes exactly once");

        using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
        {
            await ExpectCancellationAsync(WebViewScriptExecutor.ExecuteBooleanAsync(core, ownership, version, "new Promise(() => {})", timeout.Token));
        }

        using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
        {
            var stale = WebViewScriptExecutor.ExecuteBooleanAsync(core, ownership, version, "new Promise(resolve => setTimeout(() => resolve(true), 50))", timeout.Token);
            ownership.BeginNavigation();
            await ExpectCancellationAsync(stale);
        }

        ownership.AcceptPageToken(core.Source, "next-native-test-page");
        version = ownership.CaptureAcceptedPageVersion();
        Check(await Execute("Promise.resolve(true)"), "command recovers after timeout and page retirement");
    }

    private static void VerifySettingsAndEnvironment(string folder)
    {
        var failWrites = false;
        var config = new ConfigurationService(folder, writeAllText: (path, text) =>
        {
            if (failWrites) { throw new IOException("Synthetic write failure"); }
            Harness.Helpers.AtomicFileWriter.WriteAllText(path, text);
        });
        config.Load();
        config.Settings.Environments[0].GatewayUrl = "https://settings-native.test/control";
        Check(config.Save().Succeeded, "seed isolated settings");
        var logger = new WebViewService.TestLogger();
        var settings = new Harness.ViewModels.SettingsViewModel(new SettingsPersistenceAdapter(config, logger));
        settings.AlwaysOnTop = true;
        settings.SelectedEnvironment = settings.Environments[0];
        settings.EditName = "Renamed";
        Check(settings.TryApplyEdit(), "prepare environment rename");
        failWrites = true;
        Check(!settings.SaveAll(out _) && !config.Settings.AlwaysOnTop && WebViewService.ProfileMoves == 0, "failed Settings save preserves runtime configuration and profiles");
        failWrites = false;
        Check(settings.SaveAll(out var saved) && saved.DidChangeLiveShellOptions && saved.DidChangeSessionTopology, "Settings retry retains live and environment changes");

        var main = new Harness.ViewModels.MainViewModel(config);
        var draft = config.CreateSnapshot();
        draft.Environments[0].IsDefault = !draft.Environments[0].IsDefault;
        Check(config.Save(draft).Succeeded, "save default metadata");
        main.Reload();
        Check(main.NavigationCount == 0 && main.RecreationCount == 0 && main.ResetCount == 0, "metadata refresh preserves the current session despite collection selection callbacks");
        draft = config.CreateSnapshot();
        draft.Environments[0].GatewayUrl = "https://settings-native.test/changed";
        Check(config.Save(draft).Succeeded, "save changed gateway");
        main.Reload();
        Check(main.NavigationCount == 1, "changed gateway navigates once");
        draft = config.CreateSnapshot();
        draft.Environments[0].Backend = Harness.Models.HostedBackendKind.Generic;
        Check(config.Save(draft).Succeeded, "save changed backend");
        main.Reload();
        Check(main.NavigationCount == 1 && main.RecreationCount == 1, "changed backend recreates the bridge without an extra navigation");
    }

    private static async Task VerifyErrorPageAsync(CoreWebView2 core, CoreWebView2Controller controller, CoreWebView2Environment environment, string repoRoot, string outputDirectory)
    {
        var requests = 0;
        core.AddWebResourceRequestedFilter("https://harness-native.test/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            requests++;
            args.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes("<html><body>Recovered gateway</body></html>")), 200, "OK", "Content-Type: text/html");
        };
        using var service = new WebViewService(core);
        const string target = "https://harness-native.test/control";
        foreach (var language in new[] { "en-us", "zh-cn" })
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            Harness.Helpers.StringResources.Load(Path.Combine(repoRoot, "src", "Harness", "Strings", language, "Resources.resw"));
            foreach (var dark in new[] { false, true })
            {
                service.SetTheme(dark);
                await NavigateAsync(core, () => { service.ShowError(target, "ServerUnreachable"); return Task.CompletedTask; });
                Check(service.CurrentState == ConnectionState.Error, $"local error page preserves failed navigation state (state={service.CurrentState}, scheme={service.LastNavigationScheme})");
                foreach (var size in new[] { new Size(900, 600), new Size(480, 640), new Size(320, 480) })
                {
                    controller.Bounds = new Rectangle(Point.Empty, size);
                    await Task.Delay(60);
                    using var document = JsonDocument.Parse(await core.ExecuteScriptAsync("({gateway:document.getElementById('gateway-url').textContent,status:document.getElementById('error-status').textContent,retry:document.getElementById('retry').getBoundingClientRect().bottom,settings:document.getElementById('settings').getBoundingClientRect().bottom,width:innerWidth,height:innerHeight,scrollWidth:document.documentElement.scrollWidth,icon:document.getElementById('app-icon').naturalWidth,dark:matchMedia('(prefers-color-scheme: dark)').matches})"));
                    var root = document.RootElement;
                    Check(root.GetProperty("gateway").GetString() == target && root.GetProperty("status").GetString() == "ServerUnreachable", "error details reflect actual arguments");
                    Check(root.GetProperty("retry").GetDouble() < root.GetProperty("height").GetDouble() && root.GetProperty("settings").GetDouble() < root.GetProperty("height").GetDouble(), $"actions visible: {language}, {size.Width}x{size.Height}");
                    Check(root.GetProperty("scrollWidth").GetInt32() <= root.GetProperty("width").GetInt32() && root.GetProperty("icon").GetInt32() > 0, "page fits and embedded icon renders");
                    Check(root.GetProperty("dark").GetBoolean() == dark, "error page follows native theme");
                    var path = Path.Combine(outputDirectory, $"error-{language}-{(dark ? "dark" : "light")}-{size.Width}x{size.Height}.png");
                    await using var screenshot = File.Create(path);
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, screenshot);
                }
            }
        }

        var settingsRequests = 0;
        service.ErrorPageSettingsRequested += () => settingsRequests++;
        await core.ExecuteScriptAsync("window.chrome.webview.postMessage({type:'harness-error-page-action', action:'settings', errorPageToken:'wrong-page'})");
        await Task.Delay(50);
        Check(settingsRequests == 0, "wrong error-page token is rejected");
        await core.ExecuteScriptAsync("document.getElementById('settings').click()");
        await Task.Delay(50);
        Check(settingsRequests == 1, "owned Settings action is accepted");
        await NavigateAsync(core, () => core.ExecuteScriptAsync("document.getElementById('retry').click()"));
        Check(requests > 0 && core.Source == target, "error-page Retry requests the gateway");
        await NavigateAsync(core, () => { service.ShowError(target, "Timeout"); return Task.CompletedTask; });
        await NavigateAsync(core, () => { Check(service.Reload(), "Reload accepts retry from error page"); return Task.CompletedTask; });
        Check(core.Source == target, "native Reload leaves the local error page");

        await NavigateAsync(core, () => { service.ShowError(target, "CertificateExpired", ConnectionState.AuthFailed); return Task.CompletedTask; });
        Check(service.CurrentState == ConnectionState.AuthFailed, "local error page preserves certificate failure state");

        const string hostileStatus = "</script><script>window.errorInjected=true</script>";
        await NavigateAsync(core, () => { service.ShowError(target, hostileStatus); return Task.CompletedTask; });
        Check(await core.ExecuteScriptAsync("window.errorInjected === undefined") == "true", "error details cannot inject script");
    }

    private static async Task NavigateAsync(CoreWebView2 core, Func<Task> navigate)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = (_, args) => completed.TrySetResult(args.IsSuccess);
        core.NavigationCompleted += handler;
        try
        {
            await navigate();
            Check(await completed.Task.WaitAsync(TimeSpan.FromSeconds(8)), "native navigation completed");
        }
        finally
        {
            core.NavigationCompleted -= handler;
        }
    }

    private static async Task ExpectCancellationAsync(Task<bool> task)
    {
        try
        {
            await task;
            throw new InvalidOperationException("A timed-out or retired command was accepted.");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("PASS: timed-out or retired command rejected");
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) { throw new InvalidOperationException(name); }
        Console.WriteLine("PASS: " + name);
    }
}

namespace Microsoft.UI.Xaml
{
    public enum ElementTheme { Light, Dark }
    public class FrameworkElement { public ElementTheme ActualTheme { get; set; } }
}

namespace Harness.Helpers
{
    public static partial class StringResources
    {
        private static Dictionary<string, string> _strings = new();
        public static void Load(string path) => _strings = XDocument.Load(path).Root!.Elements("data").ToDictionary(element => (string)element.Attribute("name")!, element => element.Element("value")!.Value);
        public static string Get(string name) => _strings[name];
        public static string Retry => Get("Retry");
        public static string Settings => Get("Settings");
        public static string MainStatusLabel => Get("MainStatusLabel");
    }
}

namespace Harness.Services
{
    public partial class WebViewService : IDisposable
    {
        private const ulong NoCurrentNavigationId = ulong.MaxValue;
        private readonly CoreWebView2 _coreWebView;
        private readonly Microsoft.UI.Xaml.FrameworkElement _webView = new();
        private readonly WebViewMessageOwnership _messageOwnership = new();
        private readonly TestInspector _statusInspector;
        private readonly IAppLogger _logger = new TestLogger();
        private readonly bool _isInitialized = true;
        private string? _lastNavigatedUrl;
        private int _retryCount;
        private ulong _currentNavigationId;
        private ControlUiProbeSnapshot _lastPublishedControlUiSnapshot = ControlUiProbeSnapshot.Unknown;
        private string? _lastReportedIssueKey;
        public ConnectionState CurrentState { get; private set; } = ConnectionState.Error;
        public string LastNavigationScheme { get; private set; } = string.Empty;
        public event Action? ErrorPageSettingsRequested;
        public event Action<string>? NavigationErrorOccurred;
        public event Action<ControlUiProbeSnapshot>? ControlUiSnapshotUpdated;
        public static int ProfileMoves { get; private set; }
        public static void TryMoveUserDataFolderToRenamedEnvironment(string from, string to) { ProfileMoves++; }
        public WebViewService(CoreWebView2 core) { _coreWebView = core; _statusInspector = new TestInspector(snapshot => ApplyControlUiSnapshot(snapshot, false)); core.WebMessageReceived += HandleMessage; core.NavigationStarting += HandleStarting; }
        public void Dispose() { _coreWebView.WebMessageReceived -= HandleMessage; _coreWebView.NavigationStarting -= HandleStarting; }
        public void ShowError(string target, string status, ConnectionState state = ConnectionState.Error) { _lastNavigatedUrl = target; SetState(state); NavigateToErrorPage(target, status); }
        public void SetTheme(bool dark) { _webView.ActualTheme = dark ? Microsoft.UI.Xaml.ElementTheme.Dark : Microsoft.UI.Xaml.ElementTheme.Light; ApplyWebViewColorScheme(); }
        private void HandleMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args) => OnWebMessageReceived(_coreWebView, args, 1);
        private void HandleStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            LastNavigationScheme = Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ? uri.Scheme : "unknown";
            OnNavigationStarting(_coreWebView, args, 1);
        }
        private bool IsCurrentHost(int generation) => generation == 1;
        private CoreWebView2 GetCoreWebView() => _coreWebView;
        private void SetState(ConnectionState state) => CurrentState = state;
        private int PrepareNavigationStart() { ClearErrorPageOwnership(); _messageOwnership.BeginNavigation(); return 1; }
        private void CancelActiveNavigation() => _messageOwnership.BeginNavigation();
        private void CancelNavigationStartWatchdog() { }
        private void CancelNavigationCancellation() { }
        private void ObserveNavigationStartTimeout(int generation, string? url, string? source) { }
        private void ObserveNavigationCompletionTimeout(ulong id, int generation, string url) { }
        private void LogLifecycleEventOnce(string name, object context) { }
        private bool TryNavigateCoreWebView(CoreWebView2 core, string url, string reason) { core.Navigate(url); return true; }
        private bool TryReloadCoreWebView(CoreWebView2 core) { core.Reload(); return true; }
        private sealed class TestInspector
        {
            private readonly Action<ControlUiProbeSnapshot> _publish;
            public TestInspector(Action<ControlUiProbeSnapshot> publish) => _publish = publish;
            public void SetUnavailableSnapshot(string reason) => _publish(ControlUiProbeSnapshot.Unavailable(reason));
            public void SetLoadingSnapshot(string url) => _publish(ControlUiProbeSnapshot.Loading(url));
            public bool TryApplyHostMessage(string message, int version, out ControlUiProbeSnapshot snapshot) { snapshot = ControlUiProbeSnapshot.Unknown; return false; }
        }
        internal sealed class TestLogger : IAppLogger
        {
            public void Info(string message) { }
            public void Warning(string message) { }
            public void Error(string message) { }
            public void Info(string eventKey, object? context = null) { }
            public void Warning(string eventKey, object? context = null) { }
            public void Error(string eventKey, object? context = null) { }
        }
    }
}

namespace Harness.ViewModels
{
    public partial class MainViewModel
    {
        private readonly TestRuntime _runtime;
        private readonly TestEnvironmentWebView _webViewService = new();
        private readonly TestCoordinator _coordinator = new();
        private readonly TestLatency _latencyService = new();
        private Harness.Models.EnvironmentConfig? _selectedEnvironment;
        public System.Collections.ObjectModel.ObservableCollection<Harness.Models.EnvironmentConfig> Environments { get; } = new();
        public string CurrentUrl => _selectedEnvironment?.GatewayUrl ?? "";
        public string SelectedEnvironmentName => _selectedEnvironment?.Name ?? "";
        public bool IsPlaceholderEnvironment => _selectedEnvironment?.IsPlaceholder == true;
        public string StatusMessage { get; set; } = "";
        public object? StatusIndicatorBrush { get; set; }
        public object? WarningBrush => null;
        public bool IsErrorVisible { get; set; }
        public bool ShowRetryButton { get; set; }
        public event Action<string>? WebViewRecreationRequested;
        public int NavigationCount => _webViewService.NavigationCount;
        public int RecreationCount { get; private set; }
        public int ResetCount => _coordinator.ResetCount;
        public MainViewModel(ConfigurationService config)
        {
            _runtime = new TestRuntime(config);
            _selectedEnvironment = config.GetSelectedEnvironment()!.Clone();
            Environments.Add(_selectedEnvironment);
            Environments.CollectionChanged += (_, _) => SelectedEnvironment = null;
            WebViewRecreationRequested += _ => RecreationCount++;
        }
        public void Reload() => LoadEnvironments();
        private void OnPropertyChanged(string name) { }
        private void RefreshResourceScheduling() { }
        private void ResetTelemetry() { }
        private void ResetResourceProbeProjection() { }
        private void UpdateStatusPresentation() { }
        private void ApplyConnectionState(ConnectionState state) { }
        private void ApplyRecoveryState(Harness.Models.RecoveryState state) { }
        private void DismissError() { }
        private void DismissDiagnostics() { }
        private sealed record TestRuntime(ConfigurationService Configuration);
        private sealed class TestEnvironmentWebView
        {
            public bool IsInitialized => true;
            public int NavigationCount { get; private set; }
            public bool IsUsingEnvironmentProfile(string name) => true;
            public void Navigate(string url) => NavigationCount++;
            public void StopHeartbeat() { }
            public Task ClearEnvironmentSessionAsync(string name) => Task.CompletedTask;
        }
        private sealed class TestCoordinator
        {
            public int ResetCount { get; private set; }
            public void Reset() => ResetCount++;
            public void SetEnvironment(string name, string url) { }
        }
        private sealed class TestLatency { public void Stop() { } }
    }
}
