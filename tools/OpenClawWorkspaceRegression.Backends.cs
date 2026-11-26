// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Reflection;
using Harness.Models;
using Harness.Services;

public static partial class OpenClawWorkspaceRegression
{
    private static async Task HostedToolbarStopAvailabilityAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var running = new ControlUiProbeSnapshot(ControlUiPhase.Connected, "", "", "https://fixture.invalid/",
            true, true, false, "busy", "model") { CanAbortRun = true };

        foreach (var backend in new[] { HostedBackendKind.Generic, HostedBackendKind.DeepSeek })
        {
            fixture.View.UseBackend(backend);
            fixture.View.AcceptHostedPage();
            fixture.View.ApplyHostedSnapshot(running);
            Check(!fixture.View.CanStop, $"{backend} cannot enable agent Stop even from an unexpected busy snapshot");
        }

        foreach (var backend in new[] { HostedBackendKind.Hermes, HostedBackendKind.OpenClaw, HostedBackendKind.Auto })
        {
            fixture.View.UseBackend(backend);
            fixture.View.AcceptHostedPage();
            fixture.View.ApplyHostedSnapshot(running with { CanAbortRun = false });
            Check(!fixture.View.CanStop, $"{backend} busy status alone does not authorize Stop");
            fixture.View.ApplyHostedSnapshot(running with { IsBusy = false });
            Check(!fixture.View.CanStop, $"{backend} idle page cannot authorize Stop");
            fixture.View.ApplyHostedSnapshot(running with { Phase = ControlUiPhase.AuthRequired });
            Check(!fixture.View.CanStop, $"{backend} unavailable session cannot authorize Stop");
            fixture.View.ApplyHostedSnapshot(running);
            Check(fixture.View.CanStop, $"{backend} confirmed active page action enables Stop");
            await fixture.View.StopFromToolbar();
            Check(fixture.View.ChangedProperties.Contains("CanStop"), "Hosted action changes notify toolbar availability");
            fixture.View.InvalidateHostedPage();
            Check(!fixture.View.CanStop, "Page invalidation immediately retires hosted Stop before UI navigation callbacks");
            fixture.View.AcceptHostedPage();
            Check(!fixture.View.CanStop, "Accepting a replacement page does not inherit the old Stop action");
        }

        fixture.View.UseBackend(HostedBackendKind.Hermes);
        fixture.View.AcceptHostedPage();
        fixture.View.QueueHostedSnapshot(running);
        fixture.View.InvalidateHostedPage();
        fixture.View.AcceptHostedPage();
        fixture.View.FlushHostedSnapshots();
        Check(!fixture.View.CanStop, "Queued snapshot from an old accepted page cannot enable Stop on its replacement");

        fixture.View.QueueHostedSnapshot(running);
        fixture.View.UseBackend(HostedBackendKind.Hermes);
        fixture.View.FlushHostedSnapshots();
        Check(!fixture.View.CanStop, "Queued snapshot from a previous environment cannot enable Stop at the same URL");

        fixture.View.InvalidateHostedPage();
        fixture.View.QueueHostedSnapshot(running);
        fixture.View.AcceptHostedPage();
        fixture.View.FlushHostedSnapshots();
        Check(!fixture.View.CanStop, "A snapshot without accepted page ownership cannot grant Stop to a later page");

        fixture.View.ApplyHostedSnapshot(running);
        fixture.View.ResetHostedProjection();
        Check(!fixture.View.CanStop, "Resetting hosted telemetry retires confirmed Stop capability");

        var parse = typeof(WebViewStatusInspector).GetMethod("ParseControlUiSnapshot", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var value in new[] { "true", "false", "\"true\"", "null" })
        {
            var snapshot = (ControlUiProbeSnapshot)parse.Invoke(null,
                ["{\"kind\":\"harness-control-ui-status\",\"phase\":\"connected\",\"canAbortRun\":" + value + "}"])!;
            Check(snapshot.CanAbortRun == (value == "true"), "Only an explicit boolean page capability is parsed: " + value);
        }
        var missing = (ControlUiProbeSnapshot)parse.Invoke(null, ["{\"kind\":\"harness-control-ui-status\",\"phase\":\"connected\"}"])!;
        Check(!missing.CanAbortRun, "Older snapshots without a Stop capability remain read-only");
    }
}

namespace Harness.ViewModels
{
    public partial class MainViewModel
    {
        private readonly WebViewMessageOwnership _messageOwnership = new();
        private readonly Queue<Action> _hostedSnapshotQueue = new();

        public void UseBackend(HostedBackendKind backend)
        {
            _selectedEnvironment = new Harness.Models.RegressionEnvironment { Backend = backend };
            _selectedEnvironment.OpenClawConnection.Enabled = false;
            ResetHostedStopAvailability();
        }

        public void AcceptHostedPage() => _messageOwnership.AcceptPageToken(CurrentUrl, Guid.NewGuid().ToString("N"));
        public void InvalidateHostedPage() => _messageOwnership.BeginNavigation();
        public void ApplyHostedSnapshot(ControlUiProbeSnapshot snapshot) => ApplyHostedStopAvailability(snapshot);
        public void QueueHostedSnapshot(ControlUiProbeSnapshot snapshot) => OnControlUiSnapshotUpdated(snapshot);
        public void ResetHostedProjection() => ResetHostedStopAvailability();
        public void FlushHostedSnapshots()
        {
            while (_hostedSnapshotQueue.TryDequeue(out var update))
            {
                update();
            }
        }

        private void DispatchUiUpdate(Action update) => _hostedSnapshotQueue.Enqueue(update);
        private void ApplyControlUiSnapshot(ControlUiProbeSnapshot snapshot) => ApplyHostedStopAvailability(snapshot);
    }
}

namespace Microsoft.Web.WebView2.Core
{
    public sealed class CoreWebView2WebMessageReceivedEventArgs
    {
        public string Source { get; set; } = "";
    }
}
