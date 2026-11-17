// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Helpers;
using Harness.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Harness.Core.Tests;

[TestClass]
public sealed class StartupSettingsTests
{
    private string _directory = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"harness-startup-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public void OlderSettingsKeepNormalStartupAndExistingCloseBehavior()
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), """{"systemTray":true,"launchAtStartup":true}""");
        var configuration = new ConfigurationService(_directory);
        configuration.Load();

        Assert.IsTrue(configuration.Settings.LaunchAtStartup);
        Assert.IsTrue(configuration.Settings.SystemTray);
        Assert.IsFalse(configuration.Settings.StartMinimized);
    }

    [TestMethod]
    public void StartupChoicesRoundTripWithoutEnablingCloseToTray()
    {
        var configuration = new ConfigurationService(_directory);
        configuration.Load();
        var draft = configuration.CreateSnapshot();
        draft.LaunchAtStartup = true;
        draft.StartMinimized = true;

        Assert.IsFalse(configuration.Settings.StartMinimized);
        Assert.IsTrue(configuration.Save(draft).Succeeded);
        var reloaded = new ConfigurationService(_directory);
        reloaded.Load();
        Assert.IsTrue(reloaded.Settings.LaunchAtStartup);
        Assert.IsTrue(reloaded.Settings.StartMinimized);
        Assert.IsFalse(reloaded.Settings.SystemTray);
    }

    [TestMethod]
    public void StartupEditsMergeWithAnotherInstancesWindowEdits()
    {
        var first = new ConfigurationService(_directory);
        first.Load();
        var second = new ConfigurationService(_directory);
        second.Load();
        var startupDraft = first.CreateSnapshot();
        startupDraft.LaunchAtStartup = true;
        startupDraft.StartMinimized = true;
        second.Settings.WindowWidth = 1440;
        second.Settings.WindowHeight = 900;
        Assert.IsTrue(second.Save().Succeeded);
        Assert.IsTrue(first.Save(startupDraft).Succeeded);

        var reloaded = new ConfigurationService(_directory);
        reloaded.Load();
        Assert.IsTrue(reloaded.Settings.LaunchAtStartup);
        Assert.IsTrue(reloaded.Settings.StartMinimized);
        Assert.AreEqual(1440, reloaded.Settings.WindowWidth);
        Assert.AreEqual(900, reloaded.Settings.WindowHeight);
    }

    [TestMethod]
    public void FailedSaveDoesNotCommitStartupChoicesInMemoryOrOnDisk()
    {
        var rejectWrite = false;
        var configuration = new ConfigurationService(_directory, writeAllText: (path, json) =>
        {
            if (rejectWrite)
            {
                throw new IOException("Synthetic settings write failure.");
            }

            AtomicFileWriter.WriteAllText(path, json);
        });
        configuration.Load();
        var original = File.ReadAllText(Path.Combine(_directory, "settings.json"));
        var draft = configuration.CreateSnapshot();
        draft.StartMinimized = true;
        draft.LaunchAtStartup = true;
        rejectWrite = true;

        Assert.IsFalse(configuration.Save(draft).Succeeded);
        Assert.IsFalse(configuration.Settings.StartMinimized);
        Assert.IsFalse(configuration.Settings.LaunchAtStartup);
        Assert.AreEqual(original, File.ReadAllText(Path.Combine(_directory, "settings.json")));
    }
}
