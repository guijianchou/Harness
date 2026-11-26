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
    public static string FontBaseDirectory { get; private set; } = string.Empty;

    public static void Run(string loaderFolder, string repoRoot, string outputDirectory)
    {
        FontBaseDirectory = Path.Combine(repoRoot, "src", "Harness");
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
                await VerifyProtectedGatewaySettingsAsync(Path.Combine(profile.FullName, "gateway-settings-regression"));
                await VerifyAppearanceAndStartupSettingsAsync(Path.Combine(profile.FullName, "appearance-settings-regression"));
                await VerifyMergedEnvironmentSettingsAsync(Path.Combine(profile.FullName, "merged-environment-settings-regression"));
                await VerifyProfileIsolationAsync(form.Handle, Path.Combine(profile.FullName, "profile-isolation"));
                var environment = await CoreWebView2Environment.CreateAsync(null, profile.FullName);
                environment.BrowserProcessExited += (_, _) => browserExited.TrySetResult(true);
                controller = await environment.CreateCoreWebView2ControllerAsync(form.Handle);
                controller.Bounds = form.ClientRectangle;
                var core = controller.CoreWebView2;
                Console.WriteLine("WebView2 runtime: " + environment.BrowserVersionString);
                await NavigateAsync(core, () => { core.NavigateToString("<html><body>Native regression</body></html>"); return Task.CompletedTask; });
                await VerifyCommandsAsync(core);
                await VerifyTypographyAsync(core, environment);
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

    private static async Task VerifyProfileIsolationAsync(IntPtr parentWindow, string folder)
    {
        const string target = "https://profile-isolation-native.test/control";
        var controllers = new List<CoreWebView2Controller>();
        var browserExitTasks = new List<Task>();

        async Task<CoreWebView2> OpenProfileAsync(string name)
        {
            var requestedFolder = Path.GetFullPath(Path.Combine(folder, name));
            var environment = await CoreWebView2Environment.CreateAsync(null, requestedFolder);
            // Fail before creating a controller if a host override redirects the
            // requested temporary profile to an unexpected or real user folder.
            Check(string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.UserDataFolder)),
                Path.TrimEndingDirectorySeparator(requestedFolder), StringComparison.OrdinalIgnoreCase),
                $"profile {name} uses its requested temporary UserDataFolder");
            var browserExited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            environment.BrowserProcessExited += (_, _) => browserExited.TrySetResult(true);
            var profileController = await environment.CreateCoreWebView2ControllerAsync(parentWindow);
            controllers.Add(profileController);
            browserExitTasks.Add(browserExited.Task);
            profileController.IsVisible = false;
            var core = profileController.CoreWebView2;
            core.AddWebResourceRequestedFilter(target, CoreWebView2WebResourceContext.Document);
            core.WebResourceRequested += (_, args) =>
            {
                const string html = "<!doctype html><html><head><link rel='icon' href='data:,'></head><body>Profile isolation fixture</body></html>";
                args.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK",
                    "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'");
            };
            await NavigateAsync(core, () => { core.Navigate(target); return Task.CompletedTask; });
            return core;
        }

        try
        {
            var first = await OpenProfileAsync("environment-a-" + Guid.NewGuid().ToString("N"));
            Check(await first.ExecuteScriptAsync("document.cookie === '' && localStorage.getItem('harness_profile_probe') === null") == "true",
                "first temporary profile starts with no fixture cookies or storage");
            await first.ExecuteScriptAsync("document.cookie = 'harness_profile_probe=environment-a; Path=/; Secure; SameSite=Strict'; localStorage.setItem('harness_profile_probe', 'environment-a');");
            Check(await first.ExecuteScriptAsync("document.cookie === 'harness_profile_probe=environment-a' && localStorage.getItem('harness_profile_probe') === 'environment-a'") == "true",
                "first profile stores its own same-origin cookie and localStorage");

            var second = await OpenProfileAsync("environment-b-" + Guid.NewGuid().ToString("N"));
            Check(await second.ExecuteScriptAsync("document.cookie === '' && localStorage.getItem('harness_profile_probe') === null") == "true",
                "second profile cannot read the first profile's same-origin cookie or localStorage");
            await second.ExecuteScriptAsync("document.cookie = 'harness_profile_probe=environment-b; Path=/; Secure; SameSite=Strict'; localStorage.setItem('harness_profile_probe', 'environment-b');");

            await NavigateAsync(first, () => { first.Reload(); return Task.CompletedTask; });
            await NavigateAsync(second, () => { second.Reload(); return Task.CompletedTask; });
            Check(await first.ExecuteScriptAsync("document.cookie === 'harness_profile_probe=environment-a' && localStorage.getItem('harness_profile_probe') === 'environment-a'") == "true",
                "first profile preserves its own cookie and localStorage after the second profile writes and both reload");
            Check(await second.ExecuteScriptAsync("document.cookie === 'harness_profile_probe=environment-b' && localStorage.getItem('harness_profile_probe') === 'environment-b'") == "true",
                "second profile preserves its own cookie and localStorage after reload");
        }
        finally
        {
            foreach (var profileController in controllers) profileController.Close();
            if (browserExitTasks.Count > 0)
            {
                try { await Task.WhenAll(browserExitTasks).WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { Console.WriteLine("Temporary isolation browsers are still exiting; final cleanup will retry."); }
            }
        }
    }

    private static async Task VerifyCommandsAsync(CoreWebView2 core)
    {
        var ownership = new WebViewMessageOwnership();
        ownership.AcceptPageToken(core.Source, "native-test-page");
        var version = ownership.CaptureAcceptedPageVersion();
        await core.ExecuteScriptAsync("window.__openClawHostBridge = " + JsonSerializer.Serialize(new
        {
            ownerToken = ownership.OwnerToken,
            pageToken = "native-test-page"
        }) + ";");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task<bool> Execute(string script) => await WebViewScriptExecutor.ExecuteBooleanAsync(core, ownership, version, script, deadline.Token);
        Check(await Execute("true"), "synchronous command reply");
        Check(await Execute("Promise.resolve(true)"), "resolved Promise reply");
        Check(!await Execute("Promise.resolve(false)"), "unhandled Promise reply");
        Check(!await Execute("Promise.reject(new Error('synthetic failure'))"), "rejected Promise reply");
        await core.ExecuteScriptAsync("window.sideEffectCount = 0; window.__openClawHostBridge.pageToken = 'replacement-page';");
        Check(!await Execute("(++window.sideEffectCount, true)"), "retired page command is unhandled before evaluation");
        Check(await core.ExecuteScriptAsync("window.sideEffectCount") == "0", "retired command cannot execute a side effect");
        await core.ExecuteScriptAsync("window.__openClawHostBridge.pageToken = 'native-test-page';");
        await core.ExecuteScriptAsync("window.abortCount = 0; Object.assign(window.__openClawHostBridge, { getBackendKind: () => 'OpenClaw', onCommand: async () => { window.abortCount++; return true; } });");
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
        await core.ExecuteScriptAsync("window.__openClawHostBridge.pageToken = 'next-native-test-page';");
        Check(await Execute("Promise.resolve(true)"), "command recovers after timeout and page retirement");
    }

    private static async Task VerifyTypographyAsync(CoreWebView2 core, CoreWebView2Environment environment)
    {
        const string target = "https://typography-native.test/control";
        core.AddWebResourceRequestedFilter(target, CoreWebView2WebResourceContext.Document);
        EventHandler<CoreWebView2WebResourceRequestedEventArgs> servePage = (_, args) =>
        {
            const string html = "<html><head><style>.btn,.input{font-size:14px}</style></head><body><h1>Heading 标题</h1><p id='body'>English 中文</p><button id='button' class='btn'>Button 按钮</button><textarea id='input' class='input'>Input 输入</textarea><pre><code id='code'>code 代码</code></pre><span id='icon' class='material-icons' style='font-family:monospace'>M</span></body></html>";
            args.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK",
                "Content-Type: text/html; charset=utf-8\r\nContent-Security-Policy: default-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self'");
        };
        core.WebResourceRequested += servePage;
        try
        {
            using var service = new WebViewService(core, typography: true);
            service.SetContentTypography("Noto Sans SC", 18);
            await NavigateAsync(core, () => { core.Navigate(target); return Task.CompletedTask; });
            await service.WaitForTypographyAsync();
            await core.ExecuteScriptAsync("document.fonts.load('18px \"Harness Noto Sans SC\"', 'English 中文').then(fonts => window.fontReady = fonts.length === 1).catch(() => window.fontReady = false)");
            for (var attempt = 0; attempt < 100 && await core.ExecuteScriptAsync("window.fontReady") == "null"; attempt++)
            {
                await Task.Delay(50);
            }
            Check(await core.ExecuteScriptAsync("window.fontReady") == "true", "bundled Noto font loads through production same-origin route under font-src self CSP");
            Check(await core.ExecuteScriptAsync("getComputedStyle(document.getElementById('body')).fontFamily.includes('Harness Noto Sans SC') && getComputedStyle(document.getElementById('body')).fontSize === '18px'") == "true", "native page applies selected family and base size to English and Chinese text");
            Check(await core.ExecuteScriptAsync("getComputedStyle(document.getElementById('button')).fontSize === '18px' && getComputedStyle(document.getElementById('input')).fontSize === '18px'") == "true", "form controls follow selected typography despite fixed page font-size rules");
            Check(await core.ExecuteScriptAsync("getComputedStyle(document.getElementById('code')).fontFamily.includes('monospace') && getComputedStyle(document.getElementById('icon')).fontFamily.includes('monospace')") == "true", "typography preserves code and icon fonts in a real browser");
            service.SetContentTypography("System UI", 14);
            await service.WaitForTypographyAsync();
            Check(await core.ExecuteScriptAsync("getComputedStyle(document.body).fontFamily.startsWith('system-ui') && getComputedStyle(document.body).fontSize === '14px' && document.querySelectorAll('#harness-typography').length === 1") == "true", "font change applies without reload or duplicate styles");
            service.SetContentTypography("Noto Sans SC", 18);
            await NavigateAsync(core, () => { core.NavigateToString("<html><body>Local page 本地页面</body></html>"); return Task.CompletedTask; });
            await service.WaitForTypographyAsync();
            await core.ExecuteScriptAsync("document.fonts.load('18px \"Harness Noto Sans SC\"', 'English 中文').then(fonts => window.fontReady = fonts.length === 1).catch(() => window.fontReady = false)");
            for (var attempt = 0; attempt < 100 && await core.ExecuteScriptAsync("window.fontReady") == "null"; attempt++)
            {
                await Task.Delay(50);
            }
            Check(await core.ExecuteScriptAsync("window.fontReady") == "true", "local data pages load bundled Noto through the reserved local-only font route");
        }
        finally
        {
            core.WebResourceRequested -= servePage;
            core.RemoveWebResourceRequestedFilter(target, CoreWebView2WebResourceContext.Document);
        }
    }

    private static async Task VerifyProtectedGatewaySettingsAsync(string folder)
    {
        var storeFolder = Path.Combine(folder, "secrets");
        var store = new OpenClawCredentialStore(storeFolder);
        const string syntheticSecret = "synthetic-gateway-test-credential";
        await store.WriteAsync("original", new OpenClawCredentials(Token: syntheticSecret));
        Check((await store.ReadAsync("original"))?.Token == syntheticSecret, "protected credential round trips");
        Check(!Directory.GetFiles(storeFolder).Any(path => File.ReadAllText(path).Contains(syntheticSecret, StringComparison.Ordinal)), "credential is not persisted as plaintext");
        await store.SaveDeviceTokenAsync("entry-a", "operator", ["operator.read", "operator.write"], "scoped-device-token");
        Check(await store.ReadDeviceTokenAsync("entry-a", "operator", ["operator.write", "operator.read"]) == "scoped-device-token", "device token scope order is canonical");
        Check(await store.ReadDeviceTokenAsync("entry-b", "operator", ["operator.read", "operator.write"]) is null, "device token cannot cross endpoints");
        Check(await store.ReadDeviceTokenAsync("entry-a", "operator", ["operator.read"]) is null, "device token cannot cross approved scope sets");
        using (var identity = await new OpenClawDeviceIdentityService(store).GetOrCreateAsync("entry-a"))
        using (var reopened = await new OpenClawDeviceIdentityService(store).GetOrCreateAsync("entry-a"))
        using (var other = await new OpenClawDeviceIdentityService(store).GetOrCreateAsync("entry-b"))
        {
            Check(identity.DeviceId == reopened.DeviceId && identity.PublicKeyBase64Url == reopened.PublicKeyBase64Url, "Ed25519 identity persists across reopening");
            Check(identity.DeviceId != other.DeviceId, "device identity is isolated by endpoint");
            const string payload = "v3|synthetic-device-payload";
            var signature = await identity.SignAsync(payload, CancellationToken.None);
            static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
            var publicKey = NSec.Cryptography.PublicKey.Import(NSec.Cryptography.SignatureAlgorithm.Ed25519, Decode(identity.PublicKeyBase64Url), NSec.Cryptography.KeyBlobFormat.RawPublicKey);
            Check(NSec.Cryptography.SignatureAlgorithm.Ed25519.Verify(publicKey, Encoding.UTF8.GetBytes(payload), Decode(signature)), "device signature validates against raw public key");
            Check(!NSec.Cryptography.SignatureAlgorithm.Ed25519.Verify(publicKey, Encoding.UTF8.GetBytes(payload + "changed"), Decode(signature)), "device signature rejects changed payload");
        }

        var failWrites = false;
        var config = new ConfigurationService(Path.Combine(folder, "settings"), writeAllText: (path, text) =>
        {
            if (failWrites) { throw new IOException("Synthetic write failure"); }
            Harness.Helpers.AtomicFileWriter.WriteAllText(path, text);
        });
        config.Load();
        config.Settings.Environments[0].GatewayUrl = "https://gateway-settings.test";
        config.Settings.Environments[0].Backend = Harness.Models.HostedBackendKind.OpenClaw;
        config.Settings.Environments[0].OpenClawConnection = new Harness.Models.OpenClawConnectionOptions { Enabled = true, CredentialReference = "original" };
        Check(config.Save().Succeeded, "seed protected Gateway settings");
        var model = new Harness.ViewModels.SettingsViewModel(new SettingsPersistenceAdapter(config, new WebViewService.TestLogger()));
        model.SelectedEnvironment = model.Environments[0];
        model.LaunchAtStartup = true;
        var startupRegistered = false;
        var startup = new StartupRegistrationService((enabled, _) =>
        {
            var previous = startupRegistered;
            startupRegistered = enabled;
            return Task.FromResult(new StartupRegistrationChange(_ =>
            {
                startupRegistered = previous;
                return Task.FromResult(true);
            }));
        });
        model.EditGatewaySecret = "replacement-secret";
        Check(model.TryApplyEdit(), "stage replacement credential");
        failWrites = true;
        Check(await model.SaveAllAsync(store, startup) is null, "failed settings write rejects credential replacement");
        Check(config.Settings.Environments[0].OpenClawConnection!.CredentialReference == "original" && (await store.ReadAsync("original"))?.Token == syntheticSecret, "failed save preserves original credential and reference");
        Check(!startupRegistered && !config.Settings.LaunchAtStartup && model.LaunchAtStartup,
            "failed combined credential and startup save rolls back registration and retains the startup draft");
        failWrites = false;
        Check(await model.SaveAllAsync(store, startup) is not null && startupRegistered && config.Settings.LaunchAtStartup,
            "combined credential and startup save can be retried");
        var savedReference = config.Settings.Environments[0].OpenClawConnection!.CredentialReference!;
        Check((await store.ReadAsync(savedReference))?.Token == "replacement-secret", "successful retry stores replacement credential");
        Check((await store.ReadAsync(savedReference))?.Endpoint == "wss://gateway-settings.test/", "protected credential is bound to the canonical endpoint");
        model.EditGatewayAccessToken = "synthetic-access-token";
        Check(model.TryApplyEdit() && await model.SaveAllAsync(store, startup) is not null, "optional Access token can be saved independently");
        savedReference = config.Settings.Environments[0].OpenClawConnection!.CredentialReference!;
        var accessCredential = await store.ReadAsync(savedReference);
        Check(accessCredential?.Token == "replacement-secret" && accessCredential.AccessToken == "synthetic-access-token", "Access credential edit preserves Gateway authorization");
        var settingsJson = File.ReadAllText(config.SettingsFilePath);
        Check(!settingsJson.Contains("replacement-secret", StringComparison.Ordinal) && !settingsJson.Contains("synthetic-access-token", StringComparison.Ordinal), "settings JSON contains credential references only");
        model.EditGatewayClearAccessToken = true;
        Check(model.TryApplyEdit() && await model.SaveAllAsync(store, startup) is not null, "Access token can be removed");
        savedReference = config.Settings.Environments[0].OpenClawConnection!.CredentialReference!;
        accessCredential = await store.ReadAsync(savedReference);
        Check(accessCredential?.Token == "replacement-secret" && string.IsNullOrEmpty(accessCredential.AccessToken), "Access token removal preserves Gateway authorization");
        model.EditUrl = "https://another-gateway.test";
        Check(model.TryApplyEdit() && model.Environments[0].OpenClawConnection!.CredentialReference is null, "changing the endpoint retires the saved credential");
    }

    private static async Task VerifyAppearanceAndStartupSettingsAsync(string folder)
    {
        var failWrites = false;
        var config = new ConfigurationService(folder, writeAllText: (path, text) =>
        {
            if (failWrites) { throw new IOException("Synthetic appearance write failure"); }
            Harness.Helpers.AtomicFileWriter.WriteAllText(path, text);
        });
        config.Load();
        config.Settings.Environments[0].GatewayUrl = "https://appearance-settings.test/control";
        Check(config.Save().Succeeded, "seed appearance settings");
        var logger = new WebViewService.TestLogger();
        var adapter = new SettingsPersistenceAdapter(config, logger);
        var store = new OpenClawCredentialStore(Path.Combine(folder, "secrets"));
        var settingsBeforeDraft = File.ReadAllText(config.SettingsFilePath);
        var cancelled = new Harness.ViewModels.SettingsViewModel(adapter)
        {
            ThemeIndex = 2,
            ZoomPercent = 150,
            LaunchAtStartup = true,
            StartMinimized = true,
            NotifyOnRunCompleted = false,
            NotifyOnlyWhenUnfocused = false,
            ForwardPageNotifications = false,
        };
        Check(config.Settings.AppTheme == "System" && config.Settings.ZoomFactor == 1.0 &&
            !config.Settings.LaunchAtStartup && !config.Settings.StartMinimized && config.Settings.NotifyOnRunCompleted &&
            config.Settings.NotifyOnlyWhenUnfocused && config.Settings.ForwardPageNotifications,
            "discarded appearance and startup draft leaves runtime settings unchanged");
        Check(File.ReadAllText(config.SettingsFilePath) == settingsBeforeDraft,
            "discarded appearance draft does not write settings");

        var applyCount = 0;
        var rollbackCount = 0;
        var registered = false;
        var startup = new StartupRegistrationService((enabled, _) =>
        {
            applyCount++;
            var previous = registered;
            registered = enabled;
            return Task.FromResult(new StartupRegistrationChange(_ =>
            {
                rollbackCount++;
                registered = previous;
                return Task.FromResult(true);
            }));
        });

        cancelled.ZoomPercent = double.NaN;
        Check(await cancelled.SaveAllAsync(store, startup) is null && applyCount == 0,
            "invalid zoom is rejected before startup registration");
        cancelled.ZoomPercent = 150;
        failWrites = true;
        Check(await cancelled.SaveAllAsync(store, startup) is null && applyCount == 1 && rollbackCount == 1 && !registered,
            "failed configuration save restores prior startup registration");
        Check(config.Settings.AppTheme == "System" && config.Settings.ZoomFactor == 1.0 && !config.Settings.LaunchAtStartup &&
            !config.Settings.StartMinimized && config.Settings.NotifyOnRunCompleted,
            "failed appearance save preserves committed configuration");
        Check(cancelled.ThemeIndex == 2 && cancelled.ZoomPercent == 150 && cancelled.LaunchAtStartup && cancelled.StartMinimized &&
            !cancelled.NotifyOnRunCompleted && !cancelled.NotifyOnlyWhenUnfocused && !cancelled.ForwardPageNotifications,
            "failed appearance save retains every edited draft for retry");

        failWrites = false;
        var saved = await cancelled.SaveAllAsync(store, startup);
        Check(saved is { } appearanceResult && appearanceResult.LiveShellSettingsChange.DidChangeAppTheme &&
            appearanceResult.LiveShellSettingsChange.DidChangeZoomFactor && appearanceResult.DidChangeLiveShellOptions &&
            appearanceResult.LiveShellSettingsChange.After.AppTheme == config.Settings.AppTheme &&
            appearanceResult.LiveShellSettingsChange.After.ZoomFactor == config.Settings.ZoomFactor,
            "successful appearance save returns committed theme and zoom for immediate application");
        Check(registered && applyCount == 2 && rollbackCount == 1 && config.Settings.LaunchAtStartup &&
            config.Settings.StartMinimized && !config.Settings.NotifyOnRunCompleted &&
            !config.Settings.NotifyOnlyWhenUnfocused && !config.Settings.ForwardPageNotifications,
            "successful retry commits startup and notification settings without rollback");

        var uneditedTheme = new Harness.ViewModels.SettingsViewModel(adapter);
        uneditedTheme.ThemeIndex = uneditedTheme.ThemeIndex;
        uneditedTheme.ZoomPercent = uneditedTheme.ZoomPercent;
        uneditedTheme.LaunchAtStartup = uneditedTheme.LaunchAtStartup;
        config.Settings.AppTheme = "Light";
        config.Settings.ZoomFactor = 1.2;
        Check(config.Save().Succeeded, "simulate main-window theme change while Settings is open");
        uneditedTheme.NotifyOnRunCompleted = true;
        Check(await uneditedTheme.SaveAllAsync(store, startup) is not null && config.Settings.AppTheme == "Light" &&
            config.Settings.ZoomFactor == 1.2 && applyCount == 2,
            "unedited theme and zoom preserve live main-window changes and do not reapply startup registration");

        var anotherInstance = new ConfigurationService(folder);
        anotherInstance.Load();
        var mergingDraft = new Harness.ViewModels.SettingsViewModel(adapter) { NotifyOnlyWhenUnfocused = true };
        anotherInstance.Settings.AppTheme = "Dark";
        anotherInstance.Settings.ZoomFactor = 1.4;
        Check(anotherInstance.Save().Succeeded, "simulate another instance changing appearance");
        var mergedResult = await mergingDraft.SaveAllAsync(store, startup);
        Check(mergedResult is { } merged && config.Settings.AppTheme == "Dark" && config.Settings.ZoomFactor == 1.4 &&
            config.Settings.NotifyOnlyWhenUnfocused && merged.LiveShellSettingsChange.After.AppTheme == "Dark" &&
            merged.LiveShellSettingsChange.After.ZoomFactor == 1.4,
            "unrelated instance changes are merged and reported from committed configuration");

        var deniedStartup = new Harness.ViewModels.SettingsViewModel(adapter) { LaunchAtStartup = false };
        var failingStartup = new StartupRegistrationService((_, _) =>
            Task.FromException<StartupRegistrationChange>(new IOException("Synthetic startup denial")));
        Check(await deniedStartup.SaveAllAsync(store, failingStartup) is null && config.Settings.LaunchAtStartup &&
            !deniedStartup.LaunchAtStartup && deniedStartup.ValidationMessage == Harness.Helpers.StringResources.SettingsStartupSaveFailed,
            "startup registration failure keeps configuration and edited draft intact");

        var lostRollback = new StartupRegistrationService((_, _) => Task.FromResult(
            new StartupRegistrationChange(_ => Task.FromResult(false))));
        failWrites = true;
        Check(await deniedStartup.SaveAllAsync(store, lostRollback) is null &&
            deniedStartup.ValidationMessage == Harness.Helpers.StringResources.SettingsStartupRollbackFailed,
            "startup rollback conflict reports that the external state could not be restored");
        failWrites = false;
    }

    private static async Task VerifyMergedEnvironmentSettingsAsync(string folder)
    {
        var config = new ConfigurationService(folder);
        config.Load();
        config.Settings.Environments[0].GatewayUrl = "https://merged-settings.test/original";
        config.Settings.Environments.Add(new Harness.Models.EnvironmentConfig
        {
            Name = "Secondary",
            GatewayUrl = "https://merged-settings.test/secondary",
        });
        Check(config.Save().Succeeded, "seed two-instance environment settings");
        var adapter = new SettingsPersistenceAdapter(config, new WebViewService.TestLogger());
        var store = new OpenClawCredentialStore(Path.Combine(folder, "secrets"));
        var main = new Harness.ViewModels.MainViewModel(config);
        var draft = new Harness.ViewModels.SettingsViewModel(adapter) { NotifyOnRunCompleted = false };
        var anotherInstance = new ConfigurationService(folder);
        anotherInstance.Load();
        anotherInstance.Settings.Environments[0].Name = "Renamed remotely";
        anotherInstance.Settings.Environments[0].GatewayUrl = "https://merged-settings.test/changed";
        anotherInstance.Settings.Environments[0].OpenClawConnection = new Harness.Models.OpenClawConnectionOptions
        {
            CredentialReference = "other-instance-reference",
        };
        anotherInstance.Settings.SelectedEnvironmentName = "Renamed remotely";
        anotherInstance.Settings.AppLanguage = "zh-CN";
        Check(anotherInstance.Save().Succeeded, "another instance changes active environment and language");
        var saved = await draft.SaveAllAsync(store);
        Check(saved is { DidChangeEnvironmentState: true, DidChangeSessionTopology: true, DidChangeLanguage: true },
            "merged environment changes are reported even when this draft only changes notifications");
        Check(config.Settings.AppLanguage == "zh-CN" && draft.SelectedLanguage != "zh-CN",
            "committed language can differ from the draft");
        if (saved!.Value.DidChangeEnvironmentState) { main.Reload(); }
        Check(main.SelectedEnvironmentName == "Renamed remotely" && main.CurrentUrl == "https://merged-settings.test/changed" &&
            main.SelectedEnvironment?.OpenClawConnection?.CredentialReference == "other-instance-reference" && main.NavigationCount == 1,
            "save-result refresh applies the merged active environment to the main view model");

        var metadataDraft = new Harness.ViewModels.SettingsViewModel(adapter);
        anotherInstance.Settings.Environments[0].IsDefault = false;
        anotherInstance.Settings.Environments[1].IsDefault = true;
        Check(anotherInstance.Save().Succeeded, "another instance changes default metadata only");
        var metadataResult = await metadataDraft.SaveAllAsync(store);
        Check(metadataResult is { DidChangeEnvironmentState: true, DidChangeSessionTopology: false },
            "merged default-only changes refresh metadata without declaring a session change");
        main.Reload();
        Check(main.NavigationCount == 1 && main.RecreationCount == 0,
            "merged default metadata keeps the current session");

        var selectionDraft = new Harness.ViewModels.SettingsViewModel(adapter);
        anotherInstance.Settings.SelectedEnvironmentName = "Secondary";
        Check(anotherInstance.Save().Succeeded, "another instance changes only selected environment");
        var selectionResult = await selectionDraft.SaveAllAsync(store);
        Check(selectionResult is { DidChangeEnvironmentState: true, DidChangeSessionTopology: true },
            "merged selection-only changes request the environment refresh");
        main.Reload();
        Check(main.SelectedEnvironmentName == "Secondary" && main.NavigationCount == 1 && main.RecreationCount == 1,
            "merged selected environment recreates the host for its own profile");
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
        Check(!settings.SaveAll(out _) && !config.Settings.AlwaysOnTop, "failed Settings save preserves runtime configuration");
        failWrites = false;
        Check(settings.SaveAll(out var saved) && saved.DidChangeLiveShellOptions && saved.DidChangeSessionTopology, "Settings retry retains live and environment changes");

        var main = new Harness.ViewModels.MainViewModel(config);
        var draft = config.CreateSnapshot();
        draft.Environments[0].IsDefault = !draft.Environments[0].IsDefault;
        Check(config.Save(draft).Succeeded, "save default metadata");
        main.Reload();
        Check(main.NavigationCount == 0 && main.RecreationCount == 0 && main.ResetCount == 0, "metadata refresh preserves the current session despite collection selection callbacks");
        draft = config.CreateSnapshot();
        draft.Environments[0].Name = "Renamed again";
        Check(config.Save(draft).Succeeded, "save display-only rename");
        main.Reload();
        Check(main.NavigationCount == 0 && main.RecreationCount == 0 && main.ResetCount == 0, "stable identity rename retains the active profile and session");
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
        draft = config.CreateSnapshot();
        draft.Environments[0].Id = Guid.NewGuid().ToString("N");
        Check(config.Save(draft).Succeeded, "replace environment with the same display name and URL");
        main.Reload();
        Check(main.NavigationCount == 1 && main.RecreationCount == 2, "a reused display name cannot reuse another environment profile");
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
        private readonly int _hostGeneration = 1;
        private readonly bool _typographyAttached;
        private bool _isDisposed;
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
        public static void RegisterEnvironmentProfiles(IEnumerable<Harness.Models.EnvironmentConfig> savedEnvironments) { }
        public WebViewService(CoreWebView2 core, bool typography = false) { _coreWebView = core; _statusInspector = new TestInspector(snapshot => ApplyControlUiSnapshot(snapshot, false)); core.WebMessageReceived += HandleMessage; core.NavigationStarting += HandleStarting; _typographyAttached = typography; if (typography) AttachContentTypography(core, _hostGeneration); }
        public void Dispose() { _isDisposed = true; if (_typographyAttached) DetachContentTypography(_coreWebView); _coreWebView.WebMessageReceived -= HandleMessage; _coreWebView.NavigationStarting -= HandleStarting; }
        public Task WaitForTypographyAsync() => _typographyApplyTask;
        public void ShowError(string target, string status, ConnectionState state = ConnectionState.Error) { _lastNavigatedUrl = target; SetState(state); NavigateToErrorPage(target, status); }
        public void SetTheme(bool dark) { _webView.ActualTheme = dark ? Microsoft.UI.Xaml.ElementTheme.Dark : Microsoft.UI.Xaml.ElementTheme.Light; ApplyWebViewColorScheme(); }
        private void HandleMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args) => OnWebMessageReceived(_coreWebView, args, 1);
        private void HandleStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            LastNavigationScheme = Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ? uri.Scheme : "unknown";
            OnNavigationStarting(_coreWebView, args, 1);
        }
        private bool IsCurrentHost(int generation) => !_isDisposed && generation == 1;
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
        private bool _isDisposed => false;
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
            _webViewService.EnvironmentId = _selectedEnvironment.Id;
            Environments.Add(_selectedEnvironment);
            Environments.CollectionChanged += (_, _) => SelectedEnvironment = null;
            WebViewRecreationRequested += _ => RecreationCount++;
        }
        public void Reload() => LoadEnvironments();
        private void OnPropertyChanged(string name) { }
        private void RefreshResourceScheduling() { }
        private void SynchronizeGatewayEnvironment() { }
        private void ResetTelemetry() { }
        private void ResetResourceProbeProjection() { }
        private void UpdateStatusPresentation() { }
        private void ApplyConnectionState(ConnectionState state) { }
        private void ApplyRecoveryState(Harness.Models.RecoveryState state) { }
        private void DismissError() { }
        private void DismissDiagnostics() { }
        private sealed record TestRuntime(ConfigurationService Configuration)
        {
            public IAppLogger Logger { get; } = new WebViewService.TestLogger();
        }
        private sealed class TestEnvironmentWebView
        {
            public bool IsInitialized => true;
            public string EnvironmentId { get; set; } = "";
            public int NavigationCount { get; private set; }
            public bool IsUsingEnvironmentProfile(string environmentId) => EnvironmentId == environmentId;
            public void Navigate(string url) => NavigationCount++;
            public void StopHeartbeat() { }
            public Task ClearEnvironmentSessionAsync(string environmentId) => Task.CompletedTask;
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
