// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Text.Json;
using Harness.Helpers;
using Harness.Models;
using Harness.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Harness.Core.Tests;

[TestClass]
public sealed class ConfigurationServiceTests
{
    private string _tempFolder = string.Empty;

    [TestInitialize]
    public void CreateTempFolder()
    {
        _tempFolder = Path.Combine(Path.GetTempPath(), $"openclaw-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempFolder);
    }

    [TestCleanup]
    public void DeleteTempFolder()
    {
        try
        {
            Directory.Delete(_tempFolder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ConfigurationService CreateService() => new(_tempFolder);

    private string SettingsPath => Path.Combine(_tempFolder, "settings.json");

    [TestMethod]
    public void LegacyCompactModeReturnsToNormalWindowAndCannotBeReintroducedBySave()
    {
        File.WriteAllText(SettingsPath, """{"compactMode":true,"windowWidth":1440,"windowHeight":900,"appTheme":"Dark"}""");
        var service = CreateService();
        service.Load();
        Assert.IsFalse(service.Settings.CompactMode);
        Assert.AreEqual(1440d, service.Settings.WindowWidth);
        Assert.AreEqual(900d, service.Settings.WindowHeight);
        Assert.AreEqual("Dark", service.Settings.AppTheme);

        var oldDraft = service.CreateSnapshot();
        oldDraft.CompactMode = true;
        Assert.IsTrue(service.Save(oldDraft).Succeeded);
        var reloaded = CreateService();
        reloaded.Load();
        Assert.IsFalse(reloaded.Settings.CompactMode);
        Assert.AreEqual(1440d, reloaded.Settings.WindowWidth);
    }

    [TestMethod]
    public void CommittedNotificationRunsOutsideLocksAndSubscriberFailureDoesNotUndoSave()
    {
        var service = CreateService();
        var calls = 0;
        service.SettingsCommitted += () => throw new InvalidOperationException("Injected subscriber failure");
        service.SettingsCommitted += () =>
        {
            using var fileLock = File.Open($"{SettingsPath}.lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var snapshot = Task.Run(service.CreateSnapshot).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            Assert.IsNotNull(snapshot);
            calls++;
        };
        service.Load();
        Assert.AreEqual(1, calls, "Default initialization must also publish outside the settings lock.");
        service.Settings.AutoRefresh = false;
        Assert.IsTrue(service.Save().Succeeded);
        Assert.AreEqual(2, calls);
        var other = CreateService();
        other.Load();
        Assert.IsFalse(other.Settings.AutoRefresh);
    }

    [TestMethod]
    public void FailedSaveDoesNotPublishCommittedNotification()
    {
        var service = CreateService();
        service.Load();
        var calls = 0;
        service.SettingsCommitted += () => calls++;
        using (File.Open($"{SettingsPath}.lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.IsFalse(service.Save().Succeeded);
        }
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void GatewayEndpointCanonicalizationMatchesWebSocketOriginAndPath()
    {
        Assert.AreEqual("wss://gateway.test/control", OpenClawEndpoint.Resolve("https://GATEWAY.test:443/control?session=old#token", null).AbsoluteUri);
        Assert.AreEqual("ws://127.0.0.1:18789/", OpenClawEndpoint.Resolve("http://127.0.0.1:18789/", null).AbsoluteUri);
        Assert.AreEqual("wss://other.test/socket", OpenClawEndpoint.Resolve("https://gateway.test", "wss://other.test/socket").AbsoluteUri);
        Assert.ThrowsExactly<UriFormatException>(() => OpenClawEndpoint.Resolve("https://user:password@gateway.test", null));
    }

    [TestMethod]
    public void LegacyEnvironmentIdentityPersistsAcrossRenameAndReload()
    {
        File.WriteAllText(SettingsPath, """{"environments":[{"name":"VPS","gatewayUrl":"https://gateway.test","backend":"OpenClaw"}]}""");
        var service = CreateService();
        service.Load();
        var identity = service.Settings.Environments[0].Id;
        Assert.IsFalse(string.IsNullOrWhiteSpace(identity));
        Assert.IsNull(service.Settings.Environments[0].OpenClawConnection);
        var draft = service.CreateSnapshot();
        draft.Environments[0].Name = "Renamed";
        Assert.IsTrue(service.Save(draft).Succeeded);
        var reloaded = CreateService();
        reloaded.Load();
        Assert.AreEqual(identity, reloaded.Settings.Environments[0].Id);
    }

    [TestMethod]
    public void EnvironmentCloneAndApplyOwnIndependentGatewayOptions()
    {
        var original = new EnvironmentConfig { Id = "stable", OpenClawConnection = new OpenClawConnectionOptions { Enabled = true, CredentialReference = "reference-only" } };
        var draft = original.Clone();
        draft.OpenClawConnection!.CredentialReference = "replacement";
        Assert.AreEqual("reference-only", original.OpenClawConnection.CredentialReference);
        original.ApplyFrom(draft);
        draft.OpenClawConnection.Enabled = false;
        Assert.AreEqual("stable", original.Id);
        Assert.IsTrue(original.OpenClawConnection!.Enabled);
    }

    [TestMethod]
    public void FailedGatewayConfigurationSavePreservesPriorReference()
    {
        var failWrites = false;
        var service = new ConfigurationService(_tempFolder, writeAllText: (path, content) =>
        {
            if (failWrites) { throw new IOException("Synthetic failure"); }
            AtomicFileWriter.WriteAllText(path, content);
        });
        service.Load();
        var draft = service.CreateSnapshot();
        draft.Environments[0].OpenClawConnection = new OpenClawConnectionOptions { CredentialReference = "original" };
        Assert.IsTrue(service.Save(draft).Succeeded);
        draft = service.CreateSnapshot();
        draft.Environments[0].OpenClawConnection!.CredentialReference = "new";
        failWrites = true;
        Assert.IsFalse(service.Save(draft).Succeeded);
        Assert.AreEqual("original", service.Settings.Environments[0].OpenClawConnection!.CredentialReference);
        var reloaded = CreateService();
        reloaded.Load();
        Assert.AreEqual("original", reloaded.Settings.Environments[0].OpenClawConnection!.CredentialReference);
    }

    [TestMethod]
    public void GatewayConfigurationMergesUnrelatedWindowEditAndRejectsCompetingEndpointEdit()
    {
        var first = CreateService();
        first.Load();
        var second = CreateService();
        second.Load();
        var firstDraft = first.CreateSnapshot();
        firstDraft.Environments[0].OpenClawConnection = new OpenClawConnectionOptions { Enabled = true, WebSocketUrl = "wss://gateway.test", CredentialReference = "reference-only" };
        Assert.IsTrue(first.Save(firstDraft).Succeeded);
        var secondDraft = second.CreateSnapshot();
        secondDraft.WindowWidth = 1400;
        Assert.IsTrue(second.Save(secondDraft).Succeeded);
        Assert.AreEqual("wss://gateway.test", second.Settings.Environments[0].OpenClawConnection!.WebSocketUrl);
        firstDraft = first.CreateSnapshot();
        firstDraft.Environments[0].OpenClawConnection!.CredentialReference = "new-reference";
        Assert.IsTrue(first.Save(firstDraft).Succeeded);
        secondDraft.Environments[0].OpenClawConnection = new OpenClawConnectionOptions { WebSocketUrl = "wss://different.test" };
        Assert.IsFalse(second.Save(secondDraft).Succeeded);
        var reloaded = CreateService();
        reloaded.Load();
        Assert.AreEqual(1400d, reloaded.Settings.WindowWidth);
        Assert.AreEqual("wss://gateway.test", reloaded.Settings.Environments[0].OpenClawConnection!.WebSocketUrl);
    }

    [TestMethod]
    public void LoadWithMissingFileCreatesPlaceholderDefaults()
    {
        var service = CreateService();

        service.Load();

        Assert.IsTrue(File.Exists(SettingsPath));
        Assert.AreEqual(1, service.Settings.Environments.Count);
        Assert.IsTrue(service.Settings.Environments[0].IsPlaceholder);
        Assert.AreEqual("Default", service.Settings.SelectedEnvironmentName);
        Assert.IsFalse(service.Settings.SystemTray);
        Assert.IsTrue(service.Settings.AutoRefresh);
    }

    [TestMethod]
    public void LoadPreservesDisabledAutoRefresh()
    {
        File.WriteAllText(SettingsPath, """{ "autoRefresh": false }""");

        var service = CreateService();
        service.Load();

        Assert.IsFalse(service.Settings.AutoRefresh);
    }

    [TestMethod]
    public void LoadWithValidFilePreservesEnvironments()
    {
        var service = CreateService();
        service.Load();
        service.Settings.Environments.Add(new EnvironmentConfig
        {
            Name = "Production",
            GatewayUrl = "https://gateway.example.org",
        });
        service.Settings.SelectedEnvironmentName = "Production";
        Assert.IsTrue(service.Save().Succeeded);

        var reloaded = CreateService();
        reloaded.Load();

        Assert.AreEqual(2, reloaded.Settings.Environments.Count);
        Assert.AreEqual("Production", reloaded.Settings.SelectedEnvironmentName);
        Assert.AreEqual("https://gateway.example.org", reloaded.GetSelectedEnvironment()?.GatewayUrl);
    }

    [TestMethod]
    public void LoadWithCorruptFileBacksItUpBeforeWritingDefaults()
    {
        const string corruptContent = "{ this is not valid json";
        File.WriteAllText(SettingsPath, corruptContent);

        var service = CreateService();
        service.Load();

        var backups = Directory.GetFiles(_tempFolder, "settings.json.corrupt-*");
        Assert.AreEqual(1, backups.Length, "corrupt settings file should be backed up exactly once");
        Assert.AreEqual(corruptContent, File.ReadAllText(backups[0]));

        // The live file is replaced with valid defaults.
        using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.IsTrue(service.Settings.Environments[0].IsPlaceholder);
    }

    [TestMethod]
    public void LoadWithLockedFileDoesNotOverwriteIt()
    {
        var service = CreateService();
        service.Load();
        service.Settings.Environments.Add(new EnvironmentConfig
        {
            Name = "Keep",
            GatewayUrl = "https://keep.example.org",
        });
        Assert.IsTrue(service.Save().Succeeded);
        var originalContent = File.ReadAllText(SettingsPath);

        var lockedLoad = CreateService();
        using (File.Open(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            lockedLoad.Load();
        }

        Assert.AreEqual(originalContent, File.ReadAllText(SettingsPath), "a transient read failure must not overwrite the settings file");
        Assert.AreEqual(0, Directory.GetFiles(_tempFolder, "settings.json.corrupt-*").Length);

        // In-memory settings fall back to defaults for this session.
        Assert.IsTrue(lockedLoad.Settings.Environments[0].IsPlaceholder);

        lockedLoad.Settings.WindowWidth = 1440;
        Assert.IsFalse(lockedLoad.Save().Succeeded);
        lockedLoad.SaveDeferred();
        lockedLoad.FlushDeferredSave();
        Assert.AreEqual(originalContent, File.ReadAllText(SettingsPath), "later automatic saves must not persist fallback defaults");

        lockedLoad.Load();
        lockedLoad.Settings.WindowWidth = 1440;
        Assert.IsTrue(lockedLoad.Save().Succeeded);
        Assert.IsTrue(lockedLoad.Settings.Environments.Any(env => env.Name == "Keep"));
    }

    [TestMethod]
    public void FailedDraftSaveDoesNotChangeRuntimeSettingsAndCanBeRetried()
    {
        var rejectWrite = false;
        var service = new ConfigurationService(_tempFolder, writeAllText: (path, text) =>
        {
            if (rejectWrite)
            {
                throw new IOException("Injected write failure");
            }

            AtomicFileWriter.WriteAllText(path, text);
        });
        service.Load();
        var originalContent = File.ReadAllText(SettingsPath);
        var draft = service.CreateSnapshot();
        draft.AlwaysOnTop = true;
        draft.Diagnostics.EnableVerboseRecoveryLogging = true;

        rejectWrite = true;
        Assert.IsFalse(service.Save(draft).Succeeded);
        Assert.IsFalse(service.Settings.AlwaysOnTop);
        Assert.IsFalse(service.Settings.Diagnostics.EnableVerboseRecoveryLogging);
        Assert.AreEqual(originalContent, File.ReadAllText(SettingsPath));

        rejectWrite = false;
        Assert.IsTrue(service.Save(draft).Succeeded);
        Assert.IsTrue(service.Settings.AlwaysOnTop);
        Assert.IsTrue(service.Settings.Diagnostics.EnableVerboseRecoveryLogging);
    }

    [TestMethod]
    public void IndependentInstancesMergeUnrelatedChangesAcrossRepeatedSaves()
    {
        var first = CreateService();
        first.Load();
        var second = CreateService();
        second.Load();

        first.Settings.Environments.Add(new EnvironmentConfig { Name = "Keep", GatewayUrl = "https://keep.example.org" });
        Assert.IsTrue(first.Save().Succeeded);
        second.Settings.WindowWidth = 1440;
        Assert.IsTrue(second.Save().Succeeded);
        second.Settings.WindowHeight = 900;
        Assert.IsTrue(second.Save().Succeeded);
        first.Settings.AlwaysOnTop = true;
        Assert.IsTrue(first.Save().Succeeded);

        var reloaded = CreateService();
        reloaded.Load();
        Assert.IsTrue(reloaded.Settings.Environments.Any(env => env.Name == "Keep"));
        Assert.AreEqual(1440, reloaded.Settings.WindowWidth);
        Assert.AreEqual(900, reloaded.Settings.WindowHeight);
        Assert.IsTrue(reloaded.Settings.AlwaysOnTop);
    }

    [TestMethod]
    public void DraftSaveCommitsMergedAppearanceToCurrentAndAdvancesBaseline()
    {
        var first = CreateService();
        first.Load();
        var current = first.Settings;
        var environments = current.Environments;
        var environment = environments[0];
        var heartbeat = current.Heartbeat;
        var draft = first.CreateSnapshot();
        draft.NotifyOnlyWhenUnfocused = false;

        var second = CreateService();
        second.Load();
        second.Settings.AppTheme = "Dark";
        second.Settings.ZoomFactor = 1.4;
        Assert.IsTrue(second.Save().Succeeded);
        Assert.IsTrue(first.Save(draft).Succeeded);

        Assert.AreSame(current, first.Settings);
        Assert.AreSame(environments, current.Environments);
        Assert.AreSame(environment, current.Environments[0]);
        Assert.AreSame(heartbeat, current.Heartbeat);
        Assert.AreEqual("Dark", current.AppTheme);
        Assert.AreEqual(1.4, current.ZoomFactor);
        Assert.IsFalse(current.NotifyOnlyWhenUnfocused);
        Assert.AreEqual("System", draft.AppTheme, "Saving must not replace the caller's draft with the merged state.");
        Assert.AreEqual("Dark", first.CreateSnapshot().AppTheme);

        second.Settings.AppTheme = "Light";
        Assert.IsTrue(second.Save().Succeeded);
        current.ZoomFactor = 1.6;
        Assert.IsTrue(first.Save().Succeeded, "Imported appearance values must become the baseline, not a pending local edit.");
        Assert.AreEqual("Light", current.AppTheme);
        Assert.AreEqual(1.6, current.ZoomFactor);
        var reloaded = CreateService();
        reloaded.Load();
        Assert.AreEqual("Light", reloaded.Settings.AppTheme);
        Assert.AreEqual(1.6, reloaded.Settings.ZoomFactor);
        Assert.IsFalse(reloaded.Settings.NotifyOnlyWhenUnfocused);
    }

    [TestMethod]
    public void OldDraftMergesOnlyItsEditsAfterOrdinarySaveImportsRemoteEnvironment()
    {
        var first = CreateService();
        first.Load();
        var draft = first.CreateSnapshot(out var baseline);
        draft.NotifyOnRunCompleted = false;
        var second = CreateService();
        second.Load();
        second.Settings.Environments.Add(new EnvironmentConfig
        {
            Id = "remote-environment",
            Name = "Remote",
            GatewayUrl = "https://remote.example.org",
        });
        second.Settings.AppTheme = "Dark";
        Assert.IsTrue(second.Save().Succeeded);
        first.Settings.WindowLeft = 120;
        first.Settings.WindowTop = 60;
        Assert.IsTrue(first.Save().Succeeded);
        first.Settings.WindowTop = 80;

        Assert.IsTrue(first.Save(draft, baseline).Succeeded);
        Assert.AreEqual(1, draft.Environments.Count, "The dialog snapshot must remain independent of imported environments.");
        Assert.AreEqual(1, baseline.Environments.Count, "Saving must not advance the caller's draft baseline.");
        Assert.IsFalse(first.Settings.NotifyOnRunCompleted);
        Assert.AreEqual("Dark", first.Settings.AppTheme);
        Assert.AreEqual("remote-environment", first.Settings.Environments[1].Id);
        var reloaded = CreateService();
        reloaded.Load();
        Assert.AreEqual(2, reloaded.Settings.Environments.Count);
        Assert.AreEqual("remote-environment", reloaded.Settings.Environments[1].Id);
        Assert.AreEqual("Dark", reloaded.Settings.AppTheme);
        Assert.AreEqual(120d, reloaded.Settings.WindowLeft);
        Assert.AreEqual(80d, reloaded.Settings.WindowTop);
        Assert.IsFalse(reloaded.Settings.NotifyOnRunCompleted);
    }

    [TestMethod]
    [DataRow("add", false)]
    [DataRow("edit", false)]
    [DataRow("remove", false)]
    [DataRow("add", true)]
    [DataRow("edit", true)]
    public void OldEnvironmentDraftConflictsAfterOrdinarySaveAdvancesDiskBaseline(string remoteChange, bool localRemoval)
    {
        var first = CreateService();
        first.Load();
        var draft = first.CreateSnapshot(out var baseline);
        if (localRemoval)
        {
            draft.Environments.Clear();
        }
        else
        {
            draft.Environments[0].GatewayUrl = "https://local.example.org";
        }
        draft.AlwaysOnTop = true;
        var second = CreateService();
        second.Load();
        switch (remoteChange)
        {
            case "add":
                second.Settings.Environments.Add(new EnvironmentConfig
                {
                    Id = "remote-environment",
                    Name = "Remote",
                    GatewayUrl = "https://remote.example.org",
                });
                break;
            case "edit":
                second.Settings.Environments[0].OpenClawConnection = new OpenClawConnectionOptions
                {
                    CredentialReference = "remote-reference",
                };
                break;
            case "remove":
                second.Settings.Environments.Clear();
                break;
        }
        Assert.IsTrue(second.Save().Succeeded);
        first.Settings.WindowLeft = 120;
        Assert.IsTrue(first.Save().Succeeded);
        var diskBefore = File.ReadAllText(SettingsPath);
        var environments = first.Settings.Environments;
        var notifications = 0;
        first.SettingsCommitted += () => notifications++;

        var result = first.Save(draft, baseline);

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.HasConflict);
        Assert.AreEqual(diskBefore, File.ReadAllText(SettingsPath));
        Assert.AreSame(environments, first.Settings.Environments);
        Assert.IsFalse(first.Settings.AlwaysOnTop, "A conflict must reject the entire save.");
        Assert.AreEqual(0, notifications);
        Assert.IsTrue(draft.AlwaysOnTop, "The failed draft remains available for retry.");
    }

    [TestMethod]
    public void CurrentSaveKeepsEditsMadeDuringWriteForNextSave()
    {
        Action? duringWrite = null;
        var service = new ConfigurationService(_tempFolder, writeAllText: (path, text) =>
        {
            AtomicFileWriter.WriteAllText(path, text);
            var action = duringWrite;
            duringWrite = null;
            action?.Invoke();
        });
        service.Load();
        var current = service.Settings;
        var heartbeat = current.Heartbeat;
        var environments = current.Environments;
        var other = CreateService();
        other.Load();
        other.Settings.AppTheme = "Dark";
        Assert.IsTrue(other.Save().Succeeded);

        current.WindowWidth = 1440;
        duringWrite = () =>
        {
            current.WindowHeight = 920;
            heartbeat.IntervalSeconds = 75;
        };
        Assert.IsTrue(service.Save().Succeeded);
        Assert.AreSame(current, service.Settings);
        Assert.AreSame(heartbeat, current.Heartbeat);
        Assert.AreSame(environments, current.Environments);
        Assert.AreEqual("Dark", current.AppTheme);
        Assert.AreEqual(920d, current.WindowHeight);
        Assert.AreEqual(75, current.HeartbeatIntervalSeconds, "The legacy interval must track the newer live heartbeat edit.");
        using (var disk = JsonDocument.Parse(File.ReadAllText(SettingsPath)))
        {
            Assert.AreEqual(800d, disk.RootElement.GetProperty("windowHeight").GetDouble());
            Assert.AreEqual(45, disk.RootElement.GetProperty("heartbeat").GetProperty("intervalSeconds").GetInt32());
        }

        Assert.IsTrue(service.Save().Succeeded);
        var reloaded = CreateService();
        reloaded.Load();
        Assert.AreEqual(1440d, reloaded.Settings.WindowWidth);
        Assert.AreEqual(920d, reloaded.Settings.WindowHeight);
        Assert.AreEqual(75, reloaded.Settings.Heartbeat.IntervalSeconds);
    }

    [TestMethod]
    public void FailedMergedSaveLeavesCurrentUnchangedAndRetriesAgainstOriginalBaseline()
    {
        var rejectWrite = false;
        var first = new ConfigurationService(_tempFolder, writeAllText: (path, text) =>
        {
            if (rejectWrite) { throw new IOException("Injected merged-save failure"); }
            AtomicFileWriter.WriteAllText(path, text);
        });
        first.Load();
        var current = first.Settings;
        var draft = first.CreateSnapshot(out var baseline);
        draft.NotifyOnlyWhenUnfocused = false;
        var second = CreateService();
        second.Load();
        second.Settings.AppTheme = "Dark";
        Assert.IsTrue(second.Save().Succeeded);
        var diskBefore = File.ReadAllText(SettingsPath);

        rejectWrite = true;
        Assert.IsFalse(first.Save(draft, baseline).Succeeded);
        Assert.AreSame(current, first.Settings);
        Assert.AreEqual("System", current.AppTheme);
        Assert.IsTrue(current.NotifyOnlyWhenUnfocused);
        Assert.AreEqual(diskBefore, File.ReadAllText(SettingsPath));

        rejectWrite = false;
        Assert.IsTrue(first.Save(draft, baseline).Succeeded);
        Assert.AreSame(current, first.Settings);
        Assert.AreEqual("Dark", current.AppTheme);
        Assert.IsFalse(current.NotifyOnlyWhenUnfocused);
    }

    [TestMethod]
    public void IndependentInstancesRejectConflictingEnvironmentEdits()
    {
        var first = CreateService();
        first.Load();
        var second = CreateService();
        second.Load();

        first.Settings.Environments[0].GatewayUrl = "https://first.example.org";
        Assert.IsTrue(first.Save().Succeeded);
        var savedContent = File.ReadAllText(SettingsPath);
        var draft = second.CreateSnapshot();
        draft.Environments[0].GatewayUrl = "https://second.example.org";

        var result = second.Save(draft);
        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.ErrorMessage!, "another instance");
        Assert.IsTrue(second.Settings.Environments[0].IsPlaceholder);
        Assert.AreEqual(savedContent, File.ReadAllText(SettingsPath));
    }

    [TestMethod]
    public void ConcurrentWriterLockPreventsReadModifyWriteOverlap()
    {
        var service = CreateService();
        service.Load();
        var originalContent = File.ReadAllText(SettingsPath);
        service.Settings.AlwaysOnTop = true;
        using (File.Open($"{SettingsPath}.lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.IsFalse(service.Save().Succeeded);
        }

        Assert.AreEqual(originalContent, File.ReadAllText(SettingsPath));
        Assert.IsTrue(service.Save().Succeeded);
    }

    [TestMethod]
    public void LoadMigratesLegacyHeartbeatIntervalWhenHeartbeatObjectIsMissing()
    {
        File.WriteAllText(SettingsPath, """{ "heartbeatIntervalSeconds": 20 }""");

        var service = CreateService();
        service.Load();

        Assert.AreEqual(20, service.Settings.Heartbeat.IntervalSeconds);
        Assert.IsTrue(service.Settings.Heartbeat.EnableHeartbeat);
        Assert.AreEqual(20, service.Settings.HeartbeatIntervalSeconds);
    }

    [TestMethod]
    public void LoadKeepsHeartbeatObjectWhenBothFieldsArePresent()
    {
        File.WriteAllText(
            SettingsPath,
            """{ "heartbeatIntervalSeconds": 20, "heartbeat": { "enableHeartbeat": true, "intervalSeconds": 60 } }""");

        var service = CreateService();
        service.Load();

        Assert.AreEqual(60, service.Settings.Heartbeat.IntervalSeconds);
        Assert.AreEqual(60, service.Settings.HeartbeatIntervalSeconds, "legacy field must be synchronized from the heartbeat object");
    }

    [TestMethod]
    public void LoadMigratesLegacyTraySettingsToSystemTray()
    {
        File.WriteAllText(SettingsPath, """{ "minimizeToTray": false, "closeToTray": true }""");

        var service = CreateService();
        service.Load();

        Assert.IsTrue(service.Settings.SystemTray, "close-to-tray is the closest legacy match for the merged setting");
        var savedJson = File.ReadAllText(SettingsPath);
        StringAssert.Contains(savedJson, "\"systemTray\": true");
        Assert.IsFalse(savedJson.Contains("minimizeToTray", StringComparison.Ordinal));
        Assert.IsFalse(savedJson.Contains("closeToTray", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LoadMigratesLegacyMinimizeSettingWhenCloseSettingIsMissing()
    {
        File.WriteAllText(SettingsPath, """{ "minimizeToTray": false }""");

        var service = CreateService();
        service.Load();

        Assert.IsFalse(service.Settings.SystemTray);
    }

    [TestMethod]
    public void GetSelectedEnvironmentFallsBackToDefaultForUnknownName()
    {
        var service = CreateService();
        service.Load();
        service.Settings.SelectedEnvironmentName = "DoesNotExist";

        var selected = service.GetSelectedEnvironment();

        Assert.IsNotNull(selected);
        Assert.AreEqual("Default", selected.Name);
    }
}
