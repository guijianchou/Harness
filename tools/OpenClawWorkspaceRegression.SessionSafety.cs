// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Text.Json;
using Harness.Helpers;

public static partial class OpenClawWorkspaceRegression
{
    private static async Task UnavailableCurrentModelsAsync()
    {
        foreach (var model in new[] { "offline", "restricted", "unknown-policy", "missing" })
        {
            await using var fixture = await Fixture.CreateAsync();
            fixture.View.SetSession(History("physical", model: model));
            var refresh = fixture.View.RefreshData();
            await CompleteRefreshAsync(fixture, History("physical", model: model));
            await refresh;
            Check(fixture.View.SelectedGatewayModel is null && fixture.View.HasGatewayCurrentModelNotice &&
                fixture.View.GatewayCurrentModelNotice.Contains("provider/" + model, StringComparison.Ordinal),
                "Unselectable current model is displayed truthfully instead of Default: " + model);
            Check(!fixture.View.CanApplyGatewayModel && !fixture.View.CanSelectGatewayThinkingLevel,
                "An unselectable current model cannot be applied or assigned reasoning: " + model);
            await ExecuteAsync(fixture.View.ApplyGatewayModelCommand);
            await AssertNoExtraRequestsAsync(fixture);
            Check(!fixture.View.GatewayHasError, "Unedited Apply sends no patch for an unselectable model: " + model);

            fixture.View.SelectedGatewayModel = fixture.View.GatewayModels.Single(item => item.Id == "");
            Check(fixture.View.CanApplyGatewayModel, "Explicit Default is a valid change from the unavailable model: " + model);
            var apply = ExecuteAsync(fixture.View.ApplyGatewayModelCommand);
            var patch = await ReceiveAsync(fixture, "sessions.patch");
            var parameters = patch.GetProperty("params");
            Check(parameters.GetProperty("model").ValueKind == JsonValueKind.Null &&
                parameters.GetProperty("thinkingLevel").ValueKind == JsonValueKind.Null &&
                parameters.GetProperty("expectedSessionId").GetString() == "physical",
                "Only explicit Default clears model and thinking on the confirmed session: " + model);
            await fixture.Server.ReplyAsync(patch, new { ok = true });
            await CompleteRefreshAsync(fixture, History("physical", model: "", thinking: "", sample: 200), roster: true);
            await apply;
            Check(fixture.View.SelectedGatewayModel?.Id == "" && !fixture.View.HasGatewayCurrentModelNotice && !fixture.View.CanApplyGatewayModel,
                "Successful explicit reset retires the model notice and draft: " + model);
        }
    }

    private static async Task ModelDraftReconciliationAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.View.SetSession(History("physical"));
        var refresh = fixture.View.RefreshData();
        await CompleteRefreshAsync(fixture, History("physical"));
        await refresh;
        Check(!fixture.View.CanApplyGatewayModel, "An unchanged available model has no Apply action");
        await ExecuteAsync(fixture.View.ApplyGatewayModelCommand);
        await AssertNoExtraRequestsAsync(fixture);
        fixture.View.SelectedGatewayThinkingLevel = fixture.View.GatewayThinkingLevels.Single(item => item.Id == "");
        Check(fixture.View.CanApplyGatewayModel, "A reasoning-only edit enables Apply");
        fixture.View.SelectedGatewayThinkingLevel = fixture.View.GatewayThinkingLevels.Single(item => item.Id == "deep");
        Check(!fixture.View.CanApplyGatewayModel, "Returning reasoning to its server value retires the draft");
        fixture.View.SelectedGatewayModel = fixture.View.GatewayModels.Single(item => item.Id == "provider/two");
        fixture.View.SelectedGatewayThinkingLevel = fixture.View.GatewayThinkingLevels.Single(item => item.Id == "quick");
        refresh = fixture.View.RefreshData();
        await CompleteRefreshAsync(fixture, History("physical", sample: 200), models: false);
        await refresh;
        Check(fixture.View.CanApplyGatewayModel && fixture.View.SelectedGatewayModel?.Id == "provider/two" &&
            fixture.View.SelectedGatewayThinkingLevel?.Id == "quick", "Background refresh preserves a valid model and reasoning draft");
        refresh = fixture.View.RefreshData();
        await CompleteRefreshAsync(fixture, History("physical", model: "two", thinking: "quick", sample: 300));
        await refresh;
        Check(!fixture.View.CanApplyGatewayModel, "A server update matching the draft retires Apply without another patch");
        refresh = fixture.View.RefreshData();
        await CompleteRefreshAsync(fixture, History("physical", sample: 400));
        await refresh;
        Check(fixture.View.SelectedGatewayModel?.Id == "provider/one" && !fixture.View.CanApplyGatewayModel,
            "Selectors follow later server changes after the matching draft is retired");

        fixture.View.SelectedGatewayModel = fixture.View.GatewayModels.Single(item => item.Id == "provider/two");
        fixture.View.SelectedGatewayThinkingLevel = fixture.View.GatewayThinkingLevels.Single(item => item.Id == "quick");
        fixture.View.ExpireModels();
        refresh = fixture.View.RefreshData();
        var request = await ReceiveAsync(fixture, "chat.history");
        await fixture.Server.ReplyAsync(request, History("physical", sample: 500));
        request = await ReceiveAsync(fixture, "models.list");
        await fixture.Server.ReplyAsync(request, new { models = new[] { Models[0] } });
        await refresh;
        Check(fixture.View.SelectedGatewayModel?.Id == "provider/one" && fixture.View.SelectedGatewayThinkingLevel?.Id == "deep" &&
            !fixture.View.CanApplyGatewayModel, "Revoking a draft model restores the authoritative model and reasoning without an implicit reset");
        await ExecuteAsync(fixture.View.ApplyGatewayModelCommand);
        await AssertNoExtraRequestsAsync(fixture);
    }

    private static async Task ReplySubscriptionAsync(Fixture fixture, string method, string key)
    {
        var request = await ReceiveAsync(fixture, method);
        Check(request.GetProperty("params").GetProperty("key").GetString() == key, method + " targets " + key);
        await fixture.Server.ReplyAsync(request, new { ok = true });
    }

    private static async Task InitializeSubscriptionAsync(Fixture fixture)
    {
        fixture.View.SetSession(History("physical-a"));
        var refresh = fixture.View.RefreshData();
        await ReplySubscriptionAsync(fixture, "sessions.messages.subscribe", Key);
        await CompleteRefreshAsync(fixture, History("physical-a"));
        await refresh;
    }

    private static async Task SubscriptionTargetChangedAsync()
    {
        foreach (var changeDuringUnsubscribe in new[] { false, true })
        {
            await using var fixture = await Fixture.CreateAsync(subscriptions: true);
            await InitializeSubscriptionAsync(fixture);
            const string other = "agent:main:other";
            fixture.View.SetSession(History("physical-b", key: other));
            var refresh = fixture.View.RefreshData();
            var request = await ReceiveAsync(fixture, "sessions.messages.unsubscribe");
            Check(request.GetProperty("params").GetProperty("key").GetString() == Key, "Changing sessions removes the actual prior observer");
            if (changeDuringUnsubscribe)
            {
                fixture.View.SetSession(History("physical-a", sample: 200));
            }
            await fixture.Server.ReplyAsync(request, new { ok = true });
            if (!changeDuringUnsubscribe)
            {
                request = await ReceiveAsync(fixture, "sessions.messages.subscribe");
                Check(request.GetProperty("params").GetProperty("key").GetString() == other, "B subscription is in flight before returning to A");
                fixture.View.SetSession(History("physical-a", sample: 200));
                await fixture.Server.ReplyAsync(request, new { ok = true });
            }
            await refresh;
            refresh = fixture.View.RefreshPending();
            if (!changeDuringUnsubscribe)
            {
                await ReplySubscriptionAsync(fixture, "sessions.messages.unsubscribe", other);
            }
            await ReplySubscriptionAsync(fixture, "sessions.messages.subscribe", Key);
            await CompleteRefreshAsync(fixture, History("physical-a", active: true, sample: 300));
            await refresh;
            Check(fixture.View.PhysicalSession == "physical-a" && !fixture.View.GatewayHasError,
                "Current A is resubscribed after the stale subscription mutation completes: " + changeDuringUnsubscribe);
            await fixture.Server.SendAsync(new { type = "event", @event = "chat", seq = 1,
                payload = new { sessionKey = Key, sessionId = "physical-a", runId = "run-a", state = "final" } });
            await UntilAsync(() => fixture.View.EventCount == 1);
            Check(fixture.View.GatewayRunText == GatewayStrings.Text("Completed"), "The reconciled current session consumes its terminal event");
            await AssertNoExtraRequestsAsync(fixture);
        }
    }

    private static async Task SubscriptionRejectedAsync()
    {
        await using var fixture = await Fixture.CreateAsync(subscriptions: true);
        await InitializeSubscriptionAsync(fixture);
        fixture.View.SetSession(History("physical-b", key: "agent:main:other"));
        var refresh = fixture.View.RefreshData();
        await ReplySubscriptionAsync(fixture, "sessions.messages.unsubscribe", Key);
        var request = await ReceiveAsync(fixture, "sessions.messages.subscribe");
        await fixture.Server.SendAsync(new { type = "res", id = request.GetProperty("id").GetString(), ok = false, error = new { code = "FORBIDDEN" } });
        await refresh;
        Check(fixture.View.GatewayHasError, "A rejected subscription is surfaced rather than remembered as successful");
        fixture.View.SetSession(History("physical-a", sample: 200));
        refresh = fixture.View.RefreshData();
        await ReplySubscriptionAsync(fixture, "sessions.messages.subscribe", Key);
        await CompleteRefreshAsync(fixture, History("physical-a", sample: 300));
        await refresh;
        Check(!fixture.View.GatewayHasError, "Returning to A restores its observer after B was rejected");
    }

    private static async Task SubscriptionConnectionChangedAsync()
    {
        await using var previous = await Fixture.CreateAsync(subscriptions: true);
        await using var replacement = await Fixture.CreateAsync(subscriptions: true);
        await InitializeSubscriptionAsync(previous);
        previous.View.SetSession(History("physical-b", key: "agent:main:other"));
        var refresh = previous.View.RefreshData();
        await ReplySubscriptionAsync(previous, "sessions.messages.unsubscribe", Key);
        var late = await ReceiveAsync(previous, "sessions.messages.subscribe");
        previous.View.UseRuntime(replacement.Runtime);
        previous.View.SetSession(History("physical-a", sample: 200));
        await previous.Server.ReplyAsync(late, new { ok = true });
        await refresh;
        refresh = previous.View.RefreshData();
        await ReplySubscriptionAsync(replacement, "sessions.messages.subscribe", Key);
        await CompleteRefreshAsync(replacement, History("physical-a", sample: 300));
        await refresh;
        Check(previous.View.PhysicalSession == "physical-a" && !previous.View.GatewayHasError,
            "An old connection ACK cannot carry its subscription into the replacement connection");
        await AssertNoExtraRequestsAsync(replacement);
    }

    private static async Task SubscriptionCancelledAsync()
    {
        foreach (var cancelUnsubscribe in new[] { false, true })
        {
            await using var fixture = await Fixture.CreateAsync(subscriptions: true);
            if (cancelUnsubscribe)
            {
                await InitializeSubscriptionAsync(fixture);
                fixture.View.SetSession(History("physical-b", key: "agent:main:other"));
            }
            else
            {
                fixture.View.SetSession(History("physical-a"));
            }
            var client = fixture.Runtime.Client!;
            using var cancellation = new CancellationTokenSource();
            var refresh = fixture.View.RefreshData(cancellation.Token);
            await ReceiveAsync(fixture, cancelUnsubscribe ? "sessions.messages.unsubscribe" : "sessions.messages.subscribe");
            cancellation.Cancel();
            await refresh;
            Check(!client.IsConnected, "A locally cancelled subscription mutation retires its uncertain connection: " + cancelUnsubscribe);
        }
    }
}
