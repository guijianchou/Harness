// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Helpers;
using Harness.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Harness.Core.Tests;

[TestClass]
public sealed class AtomicFileWriterTests
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

    [TestMethod]
    public void WriteCreatesNewFileAndReplacesExisting()
    {
        var path = Path.Combine(_tempFolder, "data.json");

        AtomicFileWriter.WriteAllText(path, "first");
        Assert.AreEqual("first", File.ReadAllText(path));

        AtomicFileWriter.WriteAllText(path, "second");
        Assert.AreEqual("second", File.ReadAllText(path));

        Assert.AreEqual(0, Directory.GetFiles(_tempFolder, "*.tmp").Length, "temp files must be cleaned up");
        Assert.AreEqual(0, Directory.GetFiles(_tempFolder, "*.bak").Length, "backup files must be cleaned up");
    }

    [TestMethod]
    public void WriteCreatesMissingDirectories()
    {
        var path = Path.Combine(_tempFolder, "nested", "deep", "data.json");

        AtomicFileWriter.WriteAllText(path, "content");

        Assert.AreEqual("content", File.ReadAllText(path));
    }
}

[TestClass]
public sealed class WebViewCircuitBreakerTests
{
    [TestMethod]
    public void TripsAfterMaxAttemptsAndResetClears()
    {
        var breaker = new WebViewCircuitBreaker(maxAttempts: 2, windowSeconds: 600);

        Assert.IsTrue(breaker.CanAttempt());
        breaker.RecordAttempt();
        Assert.IsTrue(breaker.CanAttempt());
        breaker.RecordAttempt();

        Assert.IsFalse(breaker.CanAttempt());
        Assert.IsTrue(breaker.IsTripped);

        breaker.Reset();
        Assert.IsTrue(breaker.CanAttempt());
    }
}

[TestClass]
public sealed class HotkeyBindingTests
{
    [TestMethod]
    public void ParsesModifiersAndKey()
    {
        var binding = HotkeyBinding.Parse("Ctrl+Alt+Space");

        Assert.IsNotNull(binding);
        Assert.IsTrue(binding.Ctrl);
        Assert.IsTrue(binding.Alt);
        Assert.IsFalse(binding.Shift);
        Assert.IsFalse(binding.Win);
        Assert.AreEqual("Space", binding.Key, ignoreCase: true);
    }

    [TestMethod]
    public void ParseRejectsEmptyInput()
    {
        Assert.IsNull(HotkeyBinding.Parse(null));
        Assert.IsNull(HotkeyBinding.Parse(string.Empty));
        Assert.IsNull(HotkeyBinding.Parse("   "));
    }
}

[TestClass]
public sealed class TrayClosePolicyTests
{
    [TestMethod]
    public void SystemTrayEnabledHidesWindowToTray()
    {
        var policy = new TrayClosePolicy();

        Assert.AreEqual(TrayCloseDisposition.HideToTray, policy.GetCloseDisposition(systemTray: true));
    }

    [TestMethod]
    public void SystemTrayDisabledExitsApplication()
    {
        var policy = new TrayClosePolicy();

        Assert.AreEqual(TrayCloseDisposition.Exit, policy.GetCloseDisposition(systemTray: false));
    }

    [TestMethod]
    public void ExplicitExitAlwaysExitsApplication()
    {
        var policy = new TrayClosePolicy();
        policy.RequestExit();

        Assert.AreEqual(TrayCloseDisposition.Exit, policy.GetCloseDisposition(systemTray: true));
    }
}
