// Copyright (c) Lanstack @openclaw. All rights reserved.
// Exercises the authored Settings save handlers and ViewModel with in-memory startup registration.

using System.Security.Cryptography;
using System.Text;
using Harness.Helpers;
using Harness.Models;
using Harness.Services;
using Harness.ViewModels;
using Harness.Views;

public static class SettingsReliabilityRegression
{
    private static int _checks;

    public static async Task RunAsync()
    {
        var folder = Directory.CreateTempSubdirectory("Harness-settings-reliability-");
        try
        {
            await VerifyProfileRegistrationBeforeRenameAsync(Path.Combine(folder.FullName, "profile-registration"));
            VerifyStaleEnvironmentDraft(Path.Combine(folder.FullName, "stale-addition"), remoteAddition: true);
            VerifyStaleEnvironmentDraft(Path.Combine(folder.FullName, "stale-edit"), remoteAddition: false);
            VerifyDefaultEnvironmentBaseline(Path.Combine(folder.FullName, "default-baseline"));
            await VerifyCommittedSynchronizationAsync(Path.Combine(folder.FullName, "synchronization"));
            VerifyProfileDeletion(folder.FullName);
            VerifyEmptyEnvironment(Path.Combine(folder.FullName, "empty-environment"));
            await VerifyDraftPreservingActionsAsync(Path.Combine(folder.FullName, "draft-actions"));
            await VerifyStartupAsync(Path.Combine(folder.FullName, "startup"));
            await VerifyDialogSaveAsync(Path.Combine(folder.FullName, "dialog"), failWrite: false);
            await VerifyDialogSaveAsync(Path.Combine(folder.FullName, "failed-dialog"), failWrite: true);
            await VerifyCommittedApplyFailureAsync(Path.Combine(folder.FullName, "apply-failure"));
            await VerifyClosedContinuationAsync(Path.Combine(folder.FullName, "closed-window"));
            await VerifyExitWaitAsync(Path.Combine(folder.FullName, "exit-success"), failWrite: false);
            await VerifyExitWaitAsync(Path.Combine(folder.FullName, "exit-failure"), failWrite: true);
            Console.WriteLine($"Settings reliability regression passed: {_checks} checks; no startup registry writes or UI used.");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static ConfigurationService CreateConfiguration(string folder, Func<bool>? failWrite = null)
    {
        var configuration = new ConfigurationService(folder, writeAllText: (path, text) =>
        {
            if (failWrite?.Invoke() == true) { throw new IOException("Synthetic write failure"); }
            AtomicFileWriter.WriteAllText(path, text);
        });
        configuration.Load();
        Harness.App.Configuration = configuration;
        WebViewService.ProfileStore = new EnvironmentProfileStore(Path.Combine(folder, "profiles"));
        return configuration;
    }

    private static SettingsViewModel CreateModel(ConfigurationService configuration) =>
        new(new SettingsPersistenceAdapter(configuration, TestLogger.Instance));

    private static async Task VerifyProfileRegistrationBeforeRenameAsync(string folder)
    {
        var profiles = Path.Combine(folder, "profiles");
        var registry = Path.Combine(profiles, "environment-profiles.json");
        var requireOwnership = false;
        var configuration = CreateConfiguration(folder, () =>
        {
            if (requireOwnership)
            {
                Check(File.Exists(registry), "legacy ownership is durable before the renamed settings file is written");
            }
            return false;
        });
        var saved = configuration.CreateSnapshot().Environments.Single();
        var legacy = CreateLegacyProfile(profiles, saved.Name);
        var unrelated = CreateLegacyProfile(profiles, "Renamed");
        File.WriteAllText(Path.Combine(legacy.FullName, "Cookies"), "original-login");
        File.WriteAllText(Path.Combine(unrelated.FullName, "Cookies"), "unrelated-login");
        var draft = CreateModel(configuration);
        draft.SelectedEnvironment = draft.Environments.Single();
        draft.EditName = "Renamed";
        draft.LaunchAtStartup = true;
        Check(draft.TryApplyEdit(), "stage a rename before first profile ownership registration");
        var notifications = 0;
        configuration.SettingsCommitted += () => notifications++;
        var before = File.ReadAllText(configuration.SettingsFilePath);
        var registeredAtStartup = false;
        var startup = FakeStartup((enabled, _) =>
        {
            var previous = registeredAtStartup;
            registeredAtStartup = enabled;
            return Task.FromResult(new StartupRegistrationChange(_ =>
            {
                registeredAtStartup = previous;
                return Task.FromResult(true);
            }));
        });
        var credentials = new OpenClawCredentialStore(Path.Combine(folder, "secrets"));
        using (File.Open(Path.Combine(profiles, "environment-profiles.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var registrationFailed = false;
            try
            {
                WebViewService.RegisterEnvironmentProfiles(configuration.CreateSnapshot().Environments);
            }
            catch (IOException)
            {
                registrationFailed = true;
            }
            Check(registrationFailed && !File.Exists(registry), "startup profile registration fails without capturing ownership");
            Check(await draft.SaveAllAsync(credentials, startup) is null,
                "a failed startup profile registration cannot allow the later rename to commit");
            Check(File.ReadAllText(configuration.SettingsFilePath) == before &&
                configuration.Settings.Environments.Single().Name == saved.Name && notifications == 0,
                "profile registration failure preserves saved settings and publishes no commit");
            Check(!registeredAtStartup && !configuration.Settings.LaunchAtStartup && draft.LaunchAtStartup,
                "profile registration failure rolls back startup changes and retains the draft");
            Check(draft.ValidationMessage.StartsWith(string.Format(StringResources.SettingsValidationSaveFailedFormat, string.Empty), StringComparison.Ordinal),
                "profile registration failure uses the existing settings save validation path");
            draft.EditUrl = "https://retry.example.org";
            Check(draft.TryApplyEdit() && draft.Environments.Single().Name == "Renamed",
                "the failed rename draft remains editable");
        }

        WebViewService.ProfileStore = new EnvironmentProfileStore(profiles);
        requireOwnership = true;
        Check(await draft.SaveAllAsync(credentials, startup) is not null && registeredAtStartup && notifications == 1,
            "rename retry registers the saved baseline even after discarding pending in-memory ownership");
        var reloaded = new ConfigurationService(folder);
        reloaded.Load();
        var renamed = reloaded.CreateSnapshot().Environments.Single();
        Check(renamed.Id == saved.Id && renamed.Name == "Renamed" && renamed.GatewayUrl == "https://retry.example.org",
            "successful retry persists the editable rename with its stable identity");
        var restarted = new EnvironmentProfileStore(profiles);
        restarted.RegisterEnvironments([renamed]);
        var migrated = restarted.PrepareUserDataFolder(renamed.Id);
        Check(File.ReadAllText(Path.Combine(migrated, "Cookies")) == "original-login" &&
            File.ReadAllText(Path.Combine(unrelated.FullName, "Cookies")) == "unrelated-login",
            "restart migrates the original login using durable ownership and preserves the renamed orphan");
    }

    private static DirectoryInfo CreateLegacyProfile(string root, string name)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];
        return Directory.CreateDirectory(Path.Combine(root, $"{name}_{hash}"));
    }

    private static void VerifyStaleEnvironmentDraft(string folder, bool remoteAddition)
    {
        var first = CreateConfiguration(folder);
        var draft = CreateModel(first);
        var unedited = CreateModel(first);
        draft.SelectedEnvironment = draft.Environments[0];
        draft.EditUrl = "https://local.example.org";
        Check(draft.TryApplyEdit(), "stage local environment edit before the remote commit");

        var second = new ConfigurationService(folder);
        second.Load();
        var originalId = second.Settings.Environments[0].Id;
        if (remoteAddition)
        {
            second.Settings.Environments.Add(new EnvironmentConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Remote",
                GatewayUrl = "https://remote.example.org",
            });
        }
        else
        {
            second.Settings.Environments[0].GatewayUrl = "https://remote.example.org";
            second.Settings.Environments[0].OpenClawConnection = new OpenClawConnectionOptions
            {
                CredentialReference = "remote-reference",
            };
        }
        second.Settings.AppTheme = "Dark";
        Check(second.Save().Succeeded, "B commits its environment and theme changes");
        first.Settings.WindowLeft = 120;
        Check(first.Save().Succeeded, "A ordinary save imports B while the Settings drafts remain open");
        var diskBefore = File.ReadAllText(first.SettingsFilePath);
        var notifications = 0;
        first.SettingsCommitted += () => notifications++;

        Check(!draft.SaveAll(out _), "A stale environment draft must conflict after the ordinary save advanced its baseline");
        Check(draft.ValidationMessage == string.Format(StringResources.SettingsValidationSaveFailedFormat,
            StringResources.SettingsValidationConflict), "environment conflicts use localized validation");
        Check(File.ReadAllText(first.SettingsFilePath) == diskBefore && notifications == 0,
            "conflict leaves disk and committed notifications unchanged");
        Check(draft.Environments[0].GatewayUrl == "https://local.example.org" &&
            first.Settings.Environments[0].GatewayUrl != "https://local.example.org",
            "conflict retains the user's draft without applying it to live settings");
        draft.EditName = "Edited after conflict";
        Check(draft.TryApplyEdit() && draft.Environments[0].Name == "Edited after conflict",
            "the rejected draft remains editable");
        Check(!draft.SaveAll(out _) && File.ReadAllText(first.SettingsFilePath) == diskBefore && notifications == 0,
            "editing and retrying the rejected draft does not advance its baseline or lose remote data");

        unedited.ThemeIndex = unedited.ThemeIndex;
        unedited.NotifyOnRunCompleted = false;
        Check(unedited.SaveAll(out _) && first.Settings.AppTheme == "Dark" && !first.Settings.NotifyOnRunCompleted,
            "an unrelated dialog edit preserves imported fields and same-value binding writes stay clean");
        var reloaded = new ConfigurationService(folder);
        reloaded.Load();
        Check(reloaded.Settings.Environments[0].Id == originalId &&
            (remoteAddition
                ? reloaded.Settings.Environments.Any(environment => environment.Name == "Remote")
                : reloaded.Settings.Environments[0].GatewayUrl == "https://remote.example.org" &&
                  reloaded.Settings.Environments[0].OpenClawConnection?.CredentialReference == "remote-reference"),
            "B environment identity, addition or changed connection survives the stale dialog save");

        var reopened = CreateModel(first);
        reopened.SelectedEnvironment = reopened.Environments[0];
        reopened.EditUrl = "https://local.example.org";
        Check(reopened.TryApplyEdit() && reopened.SaveAll(out _), "reopened draft can reapply its edit against the latest baseline");
        second.Load();
        second.Settings.Environments.Add(new EnvironmentConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Later addition",
            GatewayUrl = "https://later.example.org",
        });
        Check(second.Save().Succeeded && first.Save().Succeeded, "an ordinary save imports a later remote addition");
        reopened.NotifyOnlyWhenUnfocused = false;
        Check(reopened.SaveAll(out _) && first.Settings.Environments.Any(environment => environment.Name == "Later addition"),
            "saving the same dialog again does not replay its previously committed environment changes");
    }

    private static StartupRegistrationService FakeStartup(
        Func<bool, CancellationToken, Task<StartupRegistrationChange>> apply,
        Func<CancellationToken, Task<StartupRegistrationState>>? read = null) =>
        new(apply, read ?? (_ => Task.FromResult(StartupRegistrationState.NotRegistered)));

    private static void VerifyDefaultEnvironmentBaseline(string folder)
    {
        var configuration = CreateConfiguration(folder);
        configuration.Settings.Environments.Add(new EnvironmentConfig
        {
            Id = "remaining-environment",
            Name = "Remaining",
            GatewayUrl = "https://remaining.example.org",
        });
        Check(configuration.Save().Succeeded, "seed a second environment for default normalization");
        var draft = CreateModel(configuration);
        draft.SelectedEnvironment = draft.Environments[0];
        draft.RemoveEnvironment();
        Check(draft.SaveAll(out _) && configuration.Settings.Environments.Single().IsDefault,
            "removing the default promotes the remaining environment");
        draft.Environments[0].GatewayUrl = "https://updated.example.org";
        Check(draft.SaveAll(out _) && configuration.Settings.Environments.Single().GatewayUrl == "https://updated.example.org",
            "later dialog edits compare against its own normalized commit");
    }

    private static async Task VerifyStartupAsync(string folder)
    {
        var configuration = CreateConfiguration(folder);
        configuration.Settings.LaunchAtStartup = true;
        Check(configuration.Save().Succeeded, "seed configured startup preference");
        var model = CreateModel(configuration);
        var applies = 0;
        var startup = FakeStartup((_, _) =>
        {
            applies++;
            return Task.FromResult(new StartupRegistrationChange(_ => Task.FromResult(true)));
        });
        await model.RefreshStartupRegistrationStatusAsync(startup);
        Check(model.StartupRegistrationStatus == StringResources.SettingsStartupNotRegistered && model.LaunchAtStartup,
            "actual startup status is shown separately from the desired draft");
        model.LaunchAtStartup = false;
        model.LaunchAtStartup = true;
        var store = new OpenClawCredentialStore(Path.Combine(folder, "secrets"));
        Check(await model.SaveAllAsync(store, startup) is not null && applies == 1,
            "explicit off/on reconciles registration even when the saved preference was already on");
        Check(await model.SaveAllAsync(store, startup) is not null && applies == 1,
            "success consumes the startup edit and unrelated saves do not re-register");

        var oldRead = new TaskCompletionSource<StartupRegistrationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = model.RefreshStartupRegistrationStatusAsync(FakeStartup((_, _) => throw new InvalidOperationException(), _ => oldRead.Task));
        await model.RefreshStartupRegistrationStatusAsync(FakeStartup((_, _) => throw new InvalidOperationException(),
            _ => Task.FromResult(StartupRegistrationState.DisabledByWindows)));
        oldRead.SetResult(StartupRegistrationState.Registered);
        await pending;
        Check(model.StartupRegistrationStatus == StringResources.SettingsStartupDisabledByWindows,
            "late status reads cannot replace a newer Windows startup result");
        oldRead = new TaskCompletionSource<StartupRegistrationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = model.RefreshStartupRegistrationStatusAsync(FakeStartup((_, _) => throw new InvalidOperationException(), _ => oldRead.Task));
        model.RetireStartupStatusRequests();
        oldRead.SetResult(StartupRegistrationState.Registered);
        await pending;
        Check(model.StartupRegistrationStatus == StringResources.SettingsStartupDisabledByWindows,
            "closing or replacing a draft retires its pending status read");

        model.LaunchAtStartup = false;
        var failedRollback = FakeStartup((_, _) => Task.FromException<StartupRegistrationChange>(
            new AggregateException(new IOException("apply failed"), new IOException("rollback failed"))));
        Check(await model.SaveAllAsync(store, failedRollback) is null && configuration.Settings.LaunchAtStartup &&
            model.ValidationMessage == StringResources.SettingsStartupRollbackFailed,
            "startup apply and rollback failure is reported without committing the draft");
    }

    private static async Task VerifyDialogSaveAsync(string folder, bool failWrite)
    {
        var writesFail = false;
        var configuration = CreateConfiguration(folder, () => writesFail);
        var model = CreateModel(configuration);
        model.LaunchAtStartup = true;
        var gate = new TaskCompletionSource<StartupRegistrationChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applies = 0;
        var rollbacks = 0;
        var dialog = new SettingsDialog(model, FakeStartup((_, _) => { applies++; return gate.Task; }));
        var notifications = 0;
        dialog.SettingsSaved += _ => notifications++;
        dialog.ClickSave();
        Check(dialog.IsSavingSettings && !dialog.InteractionEnabled, "pending save disables editing");
        Check(!dialog.TryNativeClose(), "native close is deferred while the save transaction is pending");
        dialog.ClickCancel();
        dialog.ClickSave();
        Check(dialog.CloseCount == 0 && applies == 1, "cancel and repeated save cannot interrupt or duplicate a pending transaction");
        writesFail = failWrite;
        gate.SetResult(new StartupRegistrationChange(_ => { rollbacks++; return Task.FromResult(true); }));
        await dialog.WaitForPendingSaveAsync();
        if (failWrite)
        {
            Check(dialog.CloseCount == 0 && !dialog.IsSavingSettings && dialog.InteractionEnabled && notifications == 0 && rollbacks == 1,
                "failed save restores startup, re-enables the same draft and keeps the window open");
            dialog.ClickCancel();
            Check(dialog.CloseCount == 1, "cancel works again after failed save has finished");
        }
        else
        {
            Check(dialog.CloseCount == 1 && !dialog.IsSavingSettings && notifications == 1 && rollbacks == 0,
                "successful save applies once then closes after releasing the save guard");
        }
    }

    private static async Task VerifyCommittedApplyFailureAsync(string folder)
    {
        var configuration = CreateConfiguration(folder);
        var model = CreateModel(configuration);
        model.ThemeIndex = 2;
        var dialog = new SettingsDialog(model, FakeStartup((_, _) => throw new InvalidOperationException()));
        dialog.SettingsSaved += _ => throw new InvalidOperationException("Synthetic apply failure");
        dialog.ClickSave();
        await dialog.WaitForPendingSaveAsync();
        Check(configuration.Settings.AppTheme == "Dark" && dialog.CloseCount == 0 && dialog.InteractionEnabled &&
            dialog.LastMessage == StringResources.SettingsApplyFailed,
            "runtime apply failure is distinguished from persistence failure after settings have committed");
    }

    private static async Task VerifyClosedContinuationAsync(string folder)
    {
        var model = CreateModel(CreateConfiguration(folder));
        model.LaunchAtStartup = true;
        var gate = new TaskCompletionSource<StartupRegistrationChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new SettingsDialog(model, FakeStartup((_, _) => gate.Task));
        dialog.SettingsSaved += _ => throw new InvalidOperationException("Closed window must not publish an apply callback");
        dialog.ClickSave();
        dialog.ForceClose();
        gate.SetResult(new StartupRegistrationChange(_ => Task.FromResult(true)));
        await dialog.WaitForPendingSaveAsync();
        Check(!dialog.IsSavingSettings && dialog.CloseCount == 1,
            "forced close retires status updates and late save completion does not touch disposed UI");
    }

    private static async Task VerifyExitWaitAsync(string folder, bool failWrite)
    {
        var writesFail = false;
        var model = CreateModel(CreateConfiguration(folder, () => writesFail));
        model.LaunchAtStartup = true;
        var gate = new TaskCompletionSource<StartupRegistrationChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rolledBack = false;
        var dialog = new SettingsDialog(model, FakeStartup((_, _) => gate.Task));
        var main = new Harness.MainWindow(dialog);
        dialog.ClickSave();
        Check(!main.TryNativeClose(), "main window native exit is deferred during a settings transaction");
        var firstExit = main.PendingExit;
        main.ExitFromTray();
        main.ExitFromTray();
        Check(ReferenceEquals(firstExit, main.PendingExit) && main.CloseCount == 0 && !main.Disposed,
            "repeated native and tray exit requests share one wait and do not dispose the running save");
        writesFail = failWrite;
        gate.SetResult(new StartupRegistrationChange(_ => { rolledBack = true; return Task.FromResult(true); }));
        await main.PendingExit;
        Check(main.CloseCount == 1 && main.Disposed && !dialog.IsSavingSettings && rolledBack == failWrite,
            failWrite ? "failed save completes rollback before the pending exit closes once" : "successful save completes before the pending exit closes once");
        main.ExitFromTray();
        Check(main.CloseCount == 1, "a queued tray exit after shutdown has started does not close twice");
    }

    private static async Task VerifyCommittedSynchronizationAsync(string folder)
    {
        var configuration = CreateConfiguration(folder);
        var dialog = new SettingsDialog(CreateModel(configuration), FakeStartup((_, _) => throw new InvalidOperationException()));
        var main = new Harness.MainWindow(dialog);
        main.StartSynchronization();
        var second = new ConfigurationService(folder);
        second.Load();
        second.Settings.AutoRefresh = false;
        second.Settings.AppTheme = "Dark";
        second.Settings.ZoomFactor = 1.25;
        Check(second.Save().Succeeded, "another instance commits live preferences");
        configuration.Settings.WindowLeft = 120;
        configuration.SaveDeferred();
        await main.DispatcherQueue.Enqueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(main.RuntimeAutoRefresh && main.Theme == "System", "background commit queues runtime application instead of touching UI inline");
        main.DispatcherQueue.Drain();
        Check(!main.RuntimeAutoRefresh && main.Theme == "Dark" && main.ViewModel.Zoom == 1.25,
            "ordinary deferred save applies settings merged from another instance");
        var applications = main.LiveApplyCount;
        main.ApplyDialogResult(default);
        Check(main.LiveApplyCount == applications, "settings dialog callback does not duplicate a queued commit apply");

        configuration.Settings.AppTheme = "Light";
        Check(configuration.Save().Succeeded, "queue a newer local theme");
        configuration.Settings.AppTheme = "Dark";
        Check(configuration.Save().Succeeded, "queue the final local theme before dispatch");
        main.DispatcherQueue.Drain();
        Check(main.Theme == "Dark", "coalesced commits use the latest snapshot and do not restore stale values");

        second.Load();
        second.Settings.AutoRefresh = true;
        Check(second.Save().Succeeded && configuration.Save().Succeeded, "queue an external change before window shutdown");
        main.ExitFromTray();
        main.DispatcherQueue.Drain();
        Check(!main.RuntimeAutoRefresh, "queued committed settings do not touch a closed main window");
    }

    private static void VerifyProfileDeletion(string folder)
    {
        WebViewService.ProfileStore = new EnvironmentProfileStore(Path.Combine(folder, "profiles"));
        WebViewService.ProfileStore.RegisterEnvironments([new EnvironmentConfig { Id = "locked", Name = "Locked" }]);
        var profile = WebViewService.GetUserDataFolderForEnvironment("locked");
        WebViewService.ProfileStore.PrepareUserDataFolder("locked");
        var file = Path.Combine(profile, "Cookies");
        File.WriteAllText(file, "synthetic");
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failed = false;
            try { WebViewService.DeleteUserDataFolderForEnvironment("locked", TestLogger.Instance); }
            catch (IOException) { failed = true; }
            Check(failed && File.Exists(file), "locked profile deletion propagates failure instead of reporting a successful reset");
        }
        WebViewService.DeleteUserDataFolderForEnvironment("locked", TestLogger.Instance);
        Check(!Directory.Exists(profile), "retry removes the profile after its lock is released");
    }

    private static void VerifyEmptyEnvironment(string folder)
    {
        var configuration = CreateConfiguration(folder);
        var model = new MainViewModel(configuration);
        model.SelectForTest(new EnvironmentConfig { Name = "live", GatewayUrl = "https://example.test" });
        model.ClearRequests();
        model.SelectForTest(null);
        Check(model.IsPlaceholderEnvironment && model.StatusMessage == StringResources.StatusConfigureGateway && !model.ShowRetryButton,
            "removing the last environment shows the configure state and removes stale recovery status");
        Check(model.RecreationRequests.SequenceEqual(new[] { "environment_placeholder_selected" }) && model.HeartbeatStopped,
            "empty environment stops probes and requests retirement of the old authenticated host");
        model.ClearRequests();
        model.SetHostInitialized(false);
        model.SelectForTest(new EnvironmentConfig { Name = "placeholder", GatewayUrl = EnvironmentConfig.PlaceholderGatewayUrl });
        Check(model.RecreationRequests.SequenceEqual(new[] { "environment_placeholder_selected" }),
            "placeholder selection retires even an initializing host rather than waiting for IsInitialized");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("FAIL: " + message); }
        _checks++;
        Console.WriteLine("PASS: " + message);
    }

    private static async Task VerifyDraftPreservingActionsAsync(string folder)
    {
        var configuration = CreateConfiguration(folder);
        var model = CreateModel(configuration);
        var dialog = new SettingsDialog(model, FakeStartup((_, _) => throw new InvalidOperationException()));
        var main = new MainViewModel(configuration);
        var shell = new Harness.MainWindow(dialog);
        Harness.App.MainWindow = shell;
        dialog.MainViewModel = main;
        model.ThemeIndex = 2;
        dialog.ClickDeveloperActions();
        Check(main.DeveloperActionCount == 4 && dialog.CloseCount == 0 && model.ThemeIndex == 2 && configuration.Settings.AppTheme != "Dark",
            "developer tools execute without closing Settings or discarding its unsaved appearance draft");
        Check(shell.ActivationCount == 4 && !main.DeveloperActionPrecededActivation,
            "developer tools activate the main window before presenting command results");
        main.DeveloperCommandsEnabled = false;
        dialog.ClickDeveloperActions();
        Check(main.DeveloperActionCount == 4 && shell.ActivationCount == 4, "disabled developer commands neither execute nor steal focus through Settings handlers");

        var saved = dialog.SavedSessionEnvironments.Single();
        model.SelectedEnvironment = model.Environments[0];
        model.SelectedEnvironment!.Name = "Renamed in unsaved draft";
        model.AddEnvironment();
        Check(dialog.SavedSessionEnvironments.Count == 1 && dialog.SavedSessionEnvironments[0].Name == saved.Name,
            "session reset targets only saved environments despite draft rename and addition");
        saved.Name = "mutated snapshot";
        Check(dialog.SavedSessionEnvironments[0].Name == configuration.Settings.Environments[0].Name,
            "session list snapshots cannot mutate live configuration");

        var target = dialog.SavedSessionEnvironments[0].Name;
        var targetId = dialog.SavedSessionEnvironments[0].Id;
        var pendingClear = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        main.ClearSession = _ => pendingClear.Task;
        var button = new Microsoft.UI.Xaml.Controls.Button { Tag = targetId };
        var pending = dialog.ClearSessionForTest(button);
        Check(!dialog.InteractionEnabled && main.ClearSessionCount == 1 && main.LastClearedEnvironment == targetId,
            "session reset uses the saved environment ID and disables conflicting Settings actions");
        await dialog.ClearSessionForTest(button);
        dialog.ClickSave();
        Check(main.ClearSessionCount == 1 && !dialog.IsSavingSettings,
            "session reset cannot overlap another reset or Settings save");
        pendingClear.SetResult();
        await pending;
        Check(dialog.InteractionEnabled && dialog.LastMessage == StringResources.SettingsSessionResetCompleted,
            "successful session reset restores editing and reports completion");

        configuration.Settings.Environments[0].Name = "Renamed after opening Settings";
        main.ClearSession = _ => Task.CompletedTask;
        await dialog.ClearSessionForTest(button);
        Check(main.LastClearedEnvironment == targetId,
            "a stale session row keeps the same profile identity after the saved environment is renamed");
        configuration.Settings.Environments.Clear();
        var cleared = main.ClearSessionCount;
        await dialog.ClearSessionForTest(button);
        Check(main.ClearSessionCount == cleared && dialog.LastMessage == StringResources.SettingsSessionResetSelectEnvironment,
            "a deleted saved environment cannot clear another profile through its stale row");
        configuration.Settings.Environments.Add(new EnvironmentConfig { Id = targetId, Name = target, GatewayUrl = "https://example.test" });

        model.SelectedEnvironment = model.Environments[0];
        model.EditName = "";
        dialog.ClickSave();
        Check(dialog.EnvironmentPanelSelected && !dialog.IsSavingSettings && dialog.CloseCount == 0,
            "environment validation failure returns to the editable environment page before saving");

        pendingClear = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        main.ClearSession = _ => pendingClear.Task;
        pending = dialog.ClearSessionForTest(button);
        dialog.ForceClose();
        pendingClear.SetResult();
        await pending;
        Check(dialog.CloseCount == 1, "late session reset completion does not touch closed Settings controls");
    }
}

namespace Harness
{
    public sealed partial class MainWindow
    {
        private readonly SettingsDialog _settingsWindow;
        private readonly TrayClosePolicy _trayClosePolicy = new();
        private Task? _settingsExitTask;
        private bool _isClosing;
        public int CloseCount { get; private set; }
        public bool Disposed { get; private set; }
        public Task PendingExit => _settingsExitTask ?? Task.CompletedTask;

        private AppSettings? _appliedSettings;
        private int _settingsApplyQueued;
        private readonly LiveShellSettingsApplier _liveShellSettingsApplier;
        public SettingsShellModel ViewModel { get; } = new();
        public SettingsDispatcher DispatcherQueue { get; } = new();
        public bool RuntimeAutoRefresh { get; private set; } = true;
        public int LiveApplyCount { get; private set; }
        public string Theme { get; private set; } = "System";
        public int ActivationCount { get; private set; }
        public void ActivateFromExternalLaunch() => ActivationCount++;
        internal MainWindow(SettingsDialog settingsWindow)
        {
            _settingsWindow = settingsWindow;
            _liveShellSettingsApplier = new LiveShellSettingsApplier(_ => { }, () => { },
                enabled => { RuntimeAutoRefresh = enabled; LiveApplyCount++; }, _ => { });
        }
        public void StartSynchronization() => InitializeSettingsSynchronization();
        public void ApplyDialogResult(SettingsSaveResult result) => OnSettingsSaved(result);
        private void ApplyTheme(string theme) => Theme = theme;
        private void UpdateThemeSelector(string theme) { }

        public void ExitFromTray() => RequestApplicationExit();
        public bool TryNativeClose()
        {
            if (DeferExitUntilSettingsSaved()) { return false; }
            Close();
            return true;
        }

        private void Close()
        {
            if (_settingsWindow.IsSavingSettings) { throw new InvalidOperationException("Exit disposed an active settings transaction."); }
            CloseCount++;
            _isClosing = true;
            App.Configuration.SettingsCommitted -= OnSettingsCommitted;
            Disposed = true;
        }
    }

    public sealed class SettingsShellModel
    {
        public int EnvironmentRefreshes { get; private set; }
        public double Zoom { get; private set; } = 1;
        public void RefreshEnvironments() => EnvironmentRefreshes++;
        public void SetContentZoom(double zoom) => Zoom = zoom;
    }

    public sealed class SettingsDispatcher
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _actions = new();
        public TaskCompletionSource Enqueued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TryEnqueue(Action action)
        {
            _actions.Enqueue(action);
            Enqueued.TrySetResult();
            return true;
        }
        public void Drain()
        {
            while (_actions.TryDequeue(out var action)) { action(); }
        }
    }

    internal static class App
    {
        public static ConfigurationService Configuration { get; set; } = null!;
        public static object? MainWindow { get; set; }
        public static IAppLogger Logger => TestLogger.Instance;
        public static bool ApplyLanguage(string language) => true;
    }
}

namespace Harness.Helpers
{
    public static partial class StringResources
    {
        public static string Get(string key) => key == "SettingsValidationSaveFailedFormat" ? "Save failed: {0}" : key;
    }
}

namespace Harness.Services
{
    internal sealed class TestLogger : IAppLogger
    {
        public static TestLogger Instance { get; } = new();
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
        public void Info(string eventKey, object? context = null) { }
        public void Warning(string eventKey, object? context = null) { }
        public void Error(string eventKey, object? context = null) { }
    }

    public static partial class WebViewService
    {
        public static EnvironmentProfileStore ProfileStore { get; set; } = null!;
        public static void RegisterEnvironmentProfiles(IEnumerable<EnvironmentConfig> savedEnvironments) => ProfileStore.RegisterEnvironments(savedEnvironments);
        public static string GetUserDataFolderForEnvironment(string environmentId) => ProfileStore.GetUserDataFolder(environmentId);
    }
}

namespace Microsoft.UI.Xaml
{
    public sealed class RoutedEventArgs : EventArgs { }
}

namespace Microsoft.UI.Xaml.Controls
{
    public sealed class Button { public object? Tag { get; set; } }
    public enum InfoBarSeverity { Informational, Warning, Error }
}

namespace Microsoft.UI.Windowing
{
    public sealed class AppWindow { }
    public sealed class AppWindowClosingEventArgs : EventArgs { public bool Cancel { get; set; } }
}

namespace Harness.Views
{
    public sealed partial class SettingsDialog
    {
        private bool _isSaving;
        private bool _isClosed;
        private bool _isClearingSession;
        private Task _settingsSaveTask = Task.CompletedTask;
        private Task _startupStatusRefreshTask = Task.CompletedTask;
        private readonly StartupRegistrationService _startupRegistration;
        private readonly SettingsPersistenceAdapter _settingsPersistence;
        private readonly InteractionControl SettingsInteractionRoot = new();
        public SettingsViewModel ViewModel { get; }
        public MainViewModel? MainViewModel { get; set; }
        public event Action<SettingsSaveResult>? SettingsSaved;
        public int CloseCount { get; private set; }
        public string? LastMessage { get; private set; }
        public bool IsSavingSettings => _isSaving;
        public bool InteractionEnabled => SettingsInteractionRoot.IsEnabled;
        private readonly NavigationControl NavList = new();
        private readonly object NavEnvironments = new();
        public bool EnvironmentPanelSelected => ReferenceEquals(NavList.SelectedItem, NavEnvironments);
        private static string ValidationErrorTitle => StringResources.SettingsValidationError;

        internal SettingsDialog(SettingsViewModel viewModel, StartupRegistrationService startup)
        {
            ViewModel = viewModel;
            _startupRegistration = startup;
            _settingsPersistence = new SettingsPersistenceAdapter(Harness.App.Configuration, TestLogger.Instance);
        }

        public Task WaitForPendingSaveAsync() => _settingsSaveTask;
        public void ClickSave() => OnSaveClick(this, new Microsoft.UI.Xaml.RoutedEventArgs());
        public void ClickCancel() => OnCancelClick(this, new Microsoft.UI.Xaml.RoutedEventArgs());
        public Task ClearSessionForTest(Microsoft.UI.Xaml.Controls.Button button) => ClearEnvironmentSessionAsync(button);
        public void ClickDeveloperActions()
        {
            var args = new Microsoft.UI.Xaml.RoutedEventArgs();
            OnRunDiagnosticsClick(this, args);
            OnViewLogsClick(this, args);
            OnDevToolsClick(this, args);
            OnExportDiagnosticBundleClick(this, args);
        }
        private void ShowSessionMessage(Microsoft.UI.Xaml.Controls.InfoBarSeverity severity, string message)
        {
            if (_isClosed) { throw new ObjectDisposedException(nameof(SettingsDialog)); }
            LastMessage = message;
        }
        private void Close() => TryNativeClose();

        public bool TryNativeClose()
        {
            var args = new Microsoft.UI.Windowing.AppWindowClosingEventArgs();
            OnSettingsAppWindowClosing(new Microsoft.UI.Windowing.AppWindow(), args);
            if (!args.Cancel) { ForceClose(); }
            return !args.Cancel;
        }

        public void ForceClose()
        {
            _isClosed = true;
            ViewModel.RetireStartupStatusRequests();
            SettingsInteractionRoot.IsClosed = true;
            CloseCount++;
        }

        private void ShowEnvironmentMessage(string title, Microsoft.UI.Xaml.Controls.InfoBarSeverity severity, string? message = null)
        {
            if (_isClosed) { throw new ObjectDisposedException(nameof(SettingsDialog)); }
            LastMessage = message ?? ViewModel.ValidationMessage;
        }

        private sealed class InteractionControl
        {
            private bool _isEnabled = true;
            public bool IsClosed { get; set; }
            public bool IsEnabled
            {
                get => _isEnabled;
                set
                {
                    if (IsClosed) { throw new ObjectDisposedException(nameof(InteractionControl)); }
                    _isEnabled = value;
                }
            }
        }
        private sealed class NavigationControl { public object? SelectedItem { get; set; } }
    }
}

namespace Harness.ViewModels
{
    public partial class MainViewModel
    {
        public int DeveloperActionCount { get; private set; }
        public bool DeveloperActionPrecededActivation { get; private set; }
        public bool DeveloperCommandsEnabled { get; set; } = true;
        public int ClearSessionCount { get; private set; }
        public string? LastClearedEnvironment { get; private set; }
        public Func<string, Task> ClearSession { get; set; } = _ => Task.CompletedTask;
        public System.Windows.Input.ICommand RunDiagnosticsCommand => new TestCommand(this);
        public System.Windows.Input.ICommand ViewLogsCommand => new TestCommand(this);
        public System.Windows.Input.ICommand DevToolsCommand => new TestCommand(this);
        public System.Windows.Input.ICommand ExportDiagnosticBundleCommand => new TestCommand(this);
        public Task ClearSessionForEnvironmentAsync(string environmentId)
        {
            ClearSessionCount++;
            LastClearedEnvironment = environmentId;
            return ClearSession(environmentId);
        }
        private sealed class TestCommand(MainViewModel owner) : System.Windows.Input.ICommand
        {
            public event EventHandler? CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object? parameter) => owner.DeveloperCommandsEnabled;
            public void Execute(object? parameter)
            {
                if (Harness.App.MainWindow is not Harness.MainWindow window || window.ActivationCount <= owner.DeveloperActionCount)
                {
                    owner.DeveloperActionPrecededActivation = true;
                }
                owner.DeveloperActionCount++;
            }
        }
        private EnvironmentConfig? _selectedEnvironment;
        private readonly EnvironmentRuntime _runtime;
        private readonly EnvironmentWebView _webViewService = new();
        private readonly EnvironmentCoordinator _coordinator = new();
        private readonly EnvironmentLatency _latencyService = new();
        public event Action<string>? WebViewRecreationRequested;
        public List<string> RecreationRequests { get; } = new();
        public EnvironmentConfig? SelectedEnvironment => _selectedEnvironment;
        public string CurrentUrl => _selectedEnvironment?.GatewayUrl ?? "";
        public string SelectedEnvironmentName => _selectedEnvironment?.Name ?? "";
        public string StatusMessage { get; private set; } = "";
        public bool IsErrorVisible { get; private set; }
        public bool ShowRetryButton { get; private set; } = true;
        public object? StatusIndicatorBrush { get; private set; }
        private object WarningBrush { get; } = new();
        public bool HeartbeatStopped => _webViewService.HeartbeatStopped;
        public MainViewModel(ConfigurationService configuration)
        {
            _runtime = new EnvironmentRuntime(configuration);
            WebViewRecreationRequested += RecreationRequests.Add;
        }
        public void SelectForTest(EnvironmentConfig? environment) => UpdateEnvironmentSelection(environment, persistSelection: false);
        public void SetHostInitialized(bool initialized) => _webViewService.IsInitialized = initialized;
        public void ClearRequests() => RecreationRequests.Clear();
        private void OnPropertyChanged(string name) { }
        private void SynchronizeGatewayEnvironment() { }
        private void ResetTelemetry() { }
        private void RefreshResourceScheduling() { }
        private void UpdateStatusPresentation() { }
        private void ResetResourceProbeProjection() { }
        private void ApplyConnectionState(Harness.Services.ConnectionState state) { }
        private void ApplyRecoveryState(Harness.Models.RecoveryState state) { }
    }
    public sealed record EnvironmentRuntime(ConfigurationService Configuration);
    public sealed class EnvironmentWebView
    {
        public bool IsInitialized { get; set; } = true;
        public bool HeartbeatStopped { get; private set; }
        public bool IsUsingEnvironmentProfile(string name) => true;
        public void Navigate(string url) { }
        public void StopHeartbeat() => HeartbeatStopped = true;
    }
    public sealed class EnvironmentCoordinator
    {
        public void Reset() { }
        public void SetEnvironment(string name, string url) { }
    }
    public sealed class EnvironmentLatency { public void Stop() { } }
}
