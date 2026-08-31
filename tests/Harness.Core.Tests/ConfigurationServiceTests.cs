// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Text.Json;
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
