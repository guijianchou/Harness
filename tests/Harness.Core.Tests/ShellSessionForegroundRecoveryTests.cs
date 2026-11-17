// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Models;
using Harness.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Harness.Core.Tests;

[TestClass]
public sealed class ShellSessionForegroundRecoveryTests
{
    [TestMethod]
    public async Task HiddenAgainWhileInspectingDoesNotReconnectInBackground()
    {
        var webView = new TestWebView();
        var bridge = new TestBridge();
        using var coordinator = new ShellSessionCoordinator();
        await coordinator.AttachAsync(webView, bridge, RecoveryOptions());
        var inspection = new TaskCompletionSource<ControlUiProbeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        webView.Inspections.Enqueue(inspection.Task);
        coordinator.OnHostHidden();
        var resume = coordinator.OnHostVisibleAsync();
        Assert.AreEqual(1, webView.InspectionCount);

        coordinator.OnHostHidden();
        inspection.SetResult(ControlUiProbeSnapshot.Unavailable("old foreground probe"));
        await resume.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, bridge.ReconnectIntentCount);
        Assert.AreEqual(0, webView.ReloadCount);
        Assert.IsFalse(coordinator.IsRecoveryInProgress);
    }

    [TestMethod]
    public async Task RetiredForegroundProbeCannotApplyAuthStateToNewResume()
    {
        var webView = new TestWebView();
        using var coordinator = new ShellSessionCoordinator();
        await coordinator.AttachAsync(webView, new TestBridge(), RecoveryOptions());
        webView.RaiseSnapshot(ConnectedSnapshot());
        var oldInspection = new TaskCompletionSource<ControlUiProbeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        webView.Inspections.Enqueue(oldInspection.Task);
        coordinator.OnHostHidden();
        var oldResume = coordinator.OnHostVisibleAsync();
        coordinator.OnHostHidden();
        webView.Inspections.Enqueue(Task.FromResult(ConnectedSnapshot()));
        await coordinator.OnHostVisibleAsync();

        oldInspection.SetResult(ConnectedSnapshot() with { Phase = ControlUiPhase.AuthRequired, Summary = "retired auth issue" });
        await oldResume.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(RecoveryState.Ready, coordinator.CurrentRecoveryState);
        Assert.AreEqual(0, webView.ReloadCount);
        Assert.AreEqual(2, webView.InspectionCount);
    }

    [TestMethod]
    public async Task DisabledAutoRefreshDoesNotProbeOnForegroundResume()
    {
        var webView = new TestWebView();
        using var coordinator = new ShellSessionCoordinator();
        await coordinator.AttachAsync(webView, new TestBridge(), RecoveryOptions(), autoRefresh: false);
        coordinator.OnHostHidden();
        await coordinator.OnHostVisibleAsync();

        Assert.AreEqual(0, webView.InspectionCount);
        Assert.AreEqual(0, webView.ReloadCount);
    }

    [TestMethod]
    public async Task CurrentForegroundResumeStillReloadsUnavailableSession()
    {
        var webView = new TestWebView();
        var bridge = new TestBridge();
        using var coordinator = new ShellSessionCoordinator();
        await coordinator.AttachAsync(webView, bridge, RecoveryOptions());
        coordinator.OnHostHidden();
        await coordinator.OnHostVisibleAsync();

        Assert.AreEqual(1, bridge.ReconnectIntentCount);
        Assert.AreEqual(1, webView.ReloadCount);
        Assert.AreEqual(RecoveryState.Connecting, coordinator.CurrentRecoveryState);
    }

    private static RecoveryPolicyOptions RecoveryOptions() => new()
    {
        BackgroundResumeThresholdSeconds = 0,
        ReconnectDelayMs = 0,
    };

    private static ControlUiProbeSnapshot ConnectedSnapshot() => new(
        Phase: ControlUiPhase.Connected,
        Summary: "Connected",
        Detail: "",
        Url: "https://foreground.test/",
        ShellDetected: true,
        IsBusy: false,
        InputFocused: false,
        WorkState: "idle",
        CurrentModel: "");

#pragma warning disable CS0067 // Test doubles only raise events used by these scenarios.
    private sealed class TestWebView : IShellSessionWebView
    {
        public Queue<Task<ControlUiProbeSnapshot>> Inspections { get; } = new();
        public int InspectionCount { get; private set; }
        public int ReloadCount { get; private set; }
        public int TotalControlUiInspectionRequests => InspectionCount;
        public int CachedControlUiInspectionRequests => 0;
        public int CoalescedControlUiInspectionRequests => 0;
        public int HeartbeatRecoveryRequests => 0;
        public event Action<ConnectionState>? ConnectionStateChanged;
        public event Action<string>? NavigationErrorOccurred;
        public event Action<string?>? NavigationCompleted;
        public event Action<HeartbeatProbeResult>? HeartbeatObserved;
        public event Action<string>? HeartbeatFailed;
        public event Action<ControlUiProbeSnapshot>? ControlUiSnapshotUpdated;

        public Task<ControlUiProbeSnapshot> InspectControlUiStateAsync(CancellationToken cancellationToken)
        {
            InspectionCount++;
            // Deliberately allow a late result: the coordinator must check its own lifetime.
            return Inspections.Count > 0 ? Inspections.Dequeue() : Task.FromResult(ControlUiProbeSnapshot.Unavailable("offline"));
        }

        public Task<bool> ReloadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReloadCount++;
            return Task.FromResult(true);
        }

        public void RaiseSnapshot(ControlUiProbeSnapshot snapshot) => ControlUiSnapshotUpdated?.Invoke(snapshot);
    }

    private sealed class TestBridge : IShellSessionBridge
    {
        public int ReconnectIntentCount { get; private set; }
        public event Action<SessionReadyEventArgs>? SessionReady;
        public event Action<EventGapEventArgs>? EventGapDetected;
        public Task<bool> RequestSessionRefreshAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> RequestRecentMessagesAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> RequestLightweightSyncAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> NotifyReconnectIntentAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReconnectIntentCount++;
            return Task.FromResult(false);
        }
    }
#pragma warning restore CS0067
}
