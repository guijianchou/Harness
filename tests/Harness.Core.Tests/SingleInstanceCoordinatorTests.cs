// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Diagnostics;
using System.IO.Pipes;
using Harness.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Harness.Core.Tests;

[TestClass]
public sealed class SingleInstanceCoordinatorTests
{
    [TestMethod]
    public void DefaultActivationPipeIsScopedToTheCurrentWindowsSession()
    {
        using var process = Process.GetCurrentProcess();
        Assert.AreEqual(@"Local\Harness.SingleInstance", SingleInstanceCoordinator.DefaultLockName);
        Assert.AreEqual($"Harness.SingleInstance.Session{process.SessionId}", SingleInstanceCoordinator.DefaultPipeName);
    }

    [TestMethod]
    public async Task ListenerRecoversFromPipeCollisionWithoutFloodingLogs()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Named semaphore integration requires Windows."); }
        var suffix = Guid.NewGuid().ToString("N");
        var pipeName = "Harness-test-" + suffix;
        using var blocker = CreateBlocker(pipeName);
        var logger = new ListenerLogger();
        using var coordinator = SingleInstanceCoordinator.CreatePrimaryOrSecondary(@"Local\Harness-test-" + suffix, pipeName, logger);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.ActivationRequested += () => activated.TrySetResult();
        coordinator.StartListening();
        await logger.FirstWarning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(250);
        Assert.AreEqual(1, logger.WarningCount, "A persistent collision should log once while backing off.");

        blocker.Dispose();
        Assert.IsTrue(await SingleInstanceCoordinator.RequestActivationOfPrimaryInstanceAsync(pipeName, logger, 3000));
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ListenerCanStopWhileBackingOffFromPipeCollision()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Named semaphore integration requires Windows."); }
        var suffix = Guid.NewGuid().ToString("N");
        var pipeName = "Harness-test-" + suffix;
        using var blocker = CreateBlocker(pipeName);
        var logger = new ListenerLogger();
        using var coordinator = SingleInstanceCoordinator.CreatePrimaryOrSecondary(@"Local\Harness-test-" + suffix, pipeName, logger);
        coordinator.StartListening();
        await logger.FirstWarning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, logger.WarningCount);
    }

    private static NamedPipeServerStream CreateBlocker(string pipeName) =>
        new(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private sealed class ListenerLogger : IAppLogger
    {
        private int _warningCount;
        public int WarningCount => Volatile.Read(ref _warningCount);
        public TaskCompletionSource FirstWarning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Info(string message) { }
        public void Warning(string message)
        {
            Interlocked.Increment(ref _warningCount);
            FirstWarning.TrySetResult();
        }
        public void Error(string message) { }
        public void Info(string eventKey, object? context = null) { }
        public void Warning(string eventKey, object? context = null) => Warning(eventKey);
        public void Error(string eventKey, object? context = null) { }
    }
}
