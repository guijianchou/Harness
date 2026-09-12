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
