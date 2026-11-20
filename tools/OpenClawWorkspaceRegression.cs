// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using System.Xml.Linq;
using Harness.Helpers;
using Harness.OpenClaw;
using Harness.Services;
using Harness.ViewModels;

public static class OpenClawWorkspaceRegression
{
    private const string Key = "agent:main:workspace";
    private static readonly List<string> Passed = [];
    private static readonly List<JsonElement> Requests = [];
    private static readonly object[] Models = [
        new { id = "one", provider = "provider", available = true, manualSelectionAllowed = true, thinkingLevels = new[] { new { id = "deep", label = "Deep" } } },
        new { id = "two", provider = "provider", available = true, manualSelectionAllowed = true, thinkingLevels = new[] { new { id = "quick", label = "Quick" } } },
        new { id = "restricted", provider = "provider", available = true, manualSelectionAllowed = false },
        new { id = "offline", provider = "provider", available = false, manualSelectionAllowed = true },
        new { id = "unknown-policy", provider = "provider", available = true },
    ];

    public static void Run(string root, string output)
    {
        StringResources.Load(Path.Combine(root, "src/Harness/Strings/en-us/Resources.resw"));
        using var context = new RegressionContext();
        SynchronizationContext.SetSynchronizationContext(context);
        var task = RunAsync(output);
        while (!task.IsCompleted)
        {
            context.DispatchOne();
        }
        task.GetAwaiter().GetResult();
    }

    private static async Task RunAsync(string output)
    {
        Exception? failure = null;
        try
        {
            await ToolbarStopAvailabilityAsync();
            await StopTerminalBeforeAcknowledgementAsync();
            await OldHistoryAfterSelectionAsync();
            await ModelAndReasoningPatchAsync();
            await LabelClearPatchAsync();
            await LabelWithoutModelsAsync();
            await RecoveryPresentationAsync();
            await StreamingHistoryAsync();
            await TerminalWhileSelectingAsync();
            await TransientRefreshFailureAsync();
            await AutomaticRefreshRecoveryAsync();
            await ServerModelChangesAsync();
            await ServerSearchAsync();
            await SearchWhileHistoryPendingAsync();
            await FilteredAgentScopeAsync();
            await CatalogFailureRetainsExactStopAsync();
            await ScopedLargeRosterRefreshAsync();
            await CatalogOwnershipAndInvalidationAsync();
            await CatalogRetryBudgetAsync();
            await CatalogConnectionOwnershipAsync();
            await RosterReconciliationAsync();
            Console.WriteLine($"PASS: {Passed.Count} actual ViewModel checks using loopback Gateway.");
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { checks = Passed.Count, passed = Passed, requests = Requests, failure = failure?.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static async Task ToolbarStopAvailabilityAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        Check(!fixture.View.CanStop, "Toolbar Stop is disabled without a confirmed native run");
        await fixture.View.StopFromToolbar();
        Check(!fixture.View.GatewayHasError && fixture.View.BrowserStopCount == 0,
            "A stale toolbar Stop click cannot manufacture a no-run error");
        fixture.View.SetLoading(true);
        Check(fixture.View.CanStop && fixture.View.ChangedProperties.Contains(nameof(MainViewModel.CanStop)),
            "Page loading notifies and enables toolbar Stop even without a native run");
        await fixture.View.StopFromToolbar();
        Check(fixture.View.BrowserStopCount == 1, "Toolbar Stop cancels page loading before considering native runs");
        fixture.View.SetLoading(false);
        Check(!fixture.View.CanStop, "Ending page loading restores native Stop availability");
        fixture.View.UseGenericBackend();
        Check(fixture.View.CanStop, "Generic hosted pages retain their existing Stop behavior");
        await fixture.View.StopFromToolbar();
        Check(fixture.View.BrowserStopCount == 2, "Generic toolbar Stop routes to the hosted browser");
    }

    private static async Task StopTerminalBeforeAcknowledgementAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.View.SetSession(History("physical", active: true), "run-a");
        Check(fixture.View.CanStop && fixture.View.CanStopGatewayRun, "Both native Stop surfaces share the confirmed run availability");
        var command = fixture.View.StopFromToolbar();
        var request = await ReceiveAsync(fixture, "chat.abort");
        Check(request.GetProperty("params").GetProperty("runId").GetString() == "run-a", "Stop sends precise run ID");
        Check(request.GetProperty("params").GetProperty("sessionId").GetString() == "physical", "Stop sends physical session fence");
        await fixture.Server.SendAsync(new { type = "event", @event = "chat", seq = 1, payload = new { sessionKey = Key, sessionId = "physical", runId = "run-a", state = "aborted" } });
        await UntilAsync(() => fixture.View.GatewayRunText == GatewayStrings.Text("Stopped"));
        await fixture.Server.ReplyAsync(request, new { aborted = true, runIds = new[] { "run-a" } });
        await command;
        Check(fixture.View.GatewayRunText == GatewayStrings.Text("Stopped"), "Late Stop ACK preserves terminal presentation");
        Check(!fixture.View.CanStopGatewayRun && !fixture.View.GatewayBusy, "Terminal run cannot be stopped twice and command releases busy state");
        Check(!fixture.View.CanStop && fixture.View.ChangedProperties.Contains(nameof(MainViewModel.CanStop)),
            "Native terminal events notify and disable toolbar Stop");
        var refresh = fixture.View.RefreshData();
        await CompleteRefreshAsync(fixture, History("physical", active: true, sample: 250));
        await refresh;
        Check(fixture.View.GatewayRunText == GatewayStrings.Text("Stopped"), "Cached active history cannot undo observed terminal state");
    }

    private static async Task OldHistoryAfterSelectionAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.View.SetSession(History("first", sample: 100));
        var refresh = fixture.View.RefreshData();
        var old = await ReceiveAsync(fixture, "chat.history");
        fixture.View.SetSession(History("other", key: "agent:main:other", sample: 200));
        fixture.View.SetSession(History("replacement", sample: 300));
        await fixture.Server.ReplyAsync(old, History("first", sample: 150));
        await refresh;
        Check(fixture.View.PhysicalSession == "replacement", "A to B to A selection rejects old same-key history");
        Check(fixture.View.SelectedGatewaySession?.Session.SessionId == "replacement", "Old history cannot replace selected session details");
    }

    private static async Task ModelAndReasoningPatchAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.View.SetSession(History("physical"));
        var initial = fixture.View.RefreshData();
        await CompleteRefreshAsync(fixture, History("physical"));
        await initial;
        Check(fixture.View.GatewayModels.All(model => model.Id is not ("provider/restricted" or "provider/offline")), "Model picker honors Gateway manual selection policy and runtime availability");
        Check(fixture.View.GatewayModels.All(model => model.Id != "provider/unknown-policy"), "Missing manual-selection permission does not become an allowed model choice");
        fixture.View.SelectedGatewayModel = fixture.View.GatewayModels.Single(model => model.Id == "provider/one");
        fixture.View.SelectedGatewayThinkingLevel = fixture.View.GatewayThinkingLevels.Single(level => level.Id == "deep");
        fixture.View.SelectedGatewayModel = fixture.View.GatewayModels.Single(model => model.Id == "provider/two");
        Check(fixture.View.SelectedGatewayThinkingLevel?.Id == "", "Changing model clears incompatible reasoning selection");
        Check(fixture.View.GatewayThinkingLevels.All(level => level.Id is "" or "quick"), "Reasoning selector contains only advertised model options and default");
        fixture.View.SelectedGatewayThinkingLevel = fixture.View.GatewayThinkingLevels.Single(level => level.Id == "quick");
        var command = ExecuteAsync(fixture.View.ApplyGatewayModelCommand);
        var request = await ReceiveAsync(fixture, "sessions.patch");
        var parameters = request.GetProperty("params");
        Check(parameters.GetProperty("model").GetString() == "provider/two" && parameters.GetProperty("thinkingLevel").GetString() == "quick", "Model and reasoning are sent in one patch");
        Check(parameters.GetProperty("expectedSessionId").GetString() == "physical" && parameters.GetProperty("agentId").GetString() == "main", "Model patch carries agent and physical session fence");
        await fixture.Server.ReplyAsync(request, new { ok = true });
        await CompleteRefreshAsync(fixture, History("physical", model: "two", thinking: "quick", sample: 200), roster: true);
        await command;
        Check(fixture.View.SelectedGatewayModel?.Id == "provider/two" && fixture.View.SelectedGatewayThinkingLevel?.Id == "quick", "Model command reconciles authoritative model and reasoning");
        Check(!fixture.View.GatewayBusy && !fixture.View.GatewayHasError, "Model command completes without stale busy or error state");
    }

    private static async Task LabelClearPatchAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.View.SetSession(History("physical", label: "Original"));
        var initial = fixture.View.RefreshData();
        await CompleteRefreshAsync(fixture, History("physical", label: "Original"));
        await initial;
        fixture.View.GatewaySessionLabel = "   ";
        var command = ExecuteAsync(fixture.View.ApplyGatewayLabelCommand);
        var request = await ReceiveAsync(fixture, "sessions.patch");
        var parameters = request.GetProperty("params");
        Check(parameters.GetProperty("label").ValueKind == JsonValueKind.Null, "Clearing session label sends explicit null");
        Check(parameters.GetProperty("expectedSessionId").GetString() == "physical", "Label clear preserves physical session fence");
        Check(!parameters.TryGetProperty("model", out _) && !parameters.TryGetProperty("thinkingLevel", out _), "Label patch leaves model and reasoning untouched");
        await fixture.Server.ReplyAsync(request, new { ok = true });
        await CompleteRefreshAsync(fixture, History("physical", label: null, sample: 200), roster: true, models: false);
        await command;
        Check(fixture.View.GatewaySessionLabel == "" && !fixture.View.CanApplyGatewayLabel, "Label clear reconciles and retires edited state");
    }

    private static async Task StreamingHistoryAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.View.SetSession(History("physical"));
        var initial = fixture.View.RefreshData();
        var request = await ReceiveAsync(fixture, "chat.history");
        await fixture.Server.SendAsync(new { type = "event", @event = "chat", seq = 1, payload = new { sessionKey = Key, sessionId = "physical", runId = "run-a", state = "delta" } });
        await UntilAsync(() => fixture.View.EventCount == 1);
        await fixture.Server.ReplyAsync(request, History("physical", active: true));
        await initial;
        Check(fixture.View.GatewayRunText == GatewayStrings.Text("Running"), "A new run event is retained over an in-flight read");
        var refresh = fixture.View.RefreshData();
        request = await ReceiveAsync(fixture, "chat.history");
        await fixture.Server.SendAsync(new { type = "event", @event = "chat", seq = 2, payload = new { sessionKey = Key, sessionId = "physical", runId = "run-a", state = "delta" } });
        await UntilAsync(() => fixture.View.EventCount == 2);
        await fixture.Server.ReplyAsync(request, History("physical", active: true, sample: 200));
        request = await ReceiveAsync(fixture, "models.list");
        await fixture.Server.ReplyAsync(request, new { models = Models });
        await refresh;
        Check(fixture.View.CanStopGatewayRun, "Repeated streaming deltas allow history to confirm an exact Stop target");
    }

    private static async Task TerminalWhileSelectingAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.View.SetSession(History("physical", active: true), "run-a");
        var selecting = fixture.View.SelectCurrentSession();
        var history = await ReceiveAsync(fixture, "chat.history");
        await fixture.Server.ReplyAsync(history, History("physical", active: true));
        var models = await ReceiveAsync(fixture, "models.list");
        await fixture.Server.SendAsync(new { type = "event", @event = "chat", seq = 1, payload = new { sessionKey = Key, sessionId = "physical", runId = "run-a", state = "final" } });
        await fixture.Server.SendAsync(new { type = "event", @event = "sessions.changed", seq = 2, payload = new { } });
        await UntilAsync(() => fixture.View.EventCount == 2);
        await fixture.Server.ReplyAsync(models, new { models = Models });
        await selecting;
        Check(fixture.View.GatewayRunText == GatewayStrings.Text("Completed"), "Terminal event arriving during selection survives a late model response");
        Check(!fixture.View.CanStopGatewayRun, "Selection-time terminal event retires exact Stop target");
        Check(fixture.View.TakeRefresh(), "Selection-time session events request a reconciled snapshot");
    }

    private static async Task TransientRefreshFailureAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.View.SetSession(History("physical", active: true), "run-a");
        var initial = fixture.View.RefreshData();
        await CompleteRefreshAsync(fixture, History("physical", active: true));
        await initial;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var refresh = fixture.View.RefreshData();
            var history = await ReceiveAsync(fixture, "chat.history");
            await fixture.Server.SendAsync(new { type = "res", id = history.GetProperty("id").GetString(), ok = false, error = new { code = "UNAVAILABLE" } });
            await refresh;
            if (attempt == 0)
            {
                Check(fixture.View.PhysicalSession == "physical" && fixture.View.GatewayModels.Count == 3, "Transient RPC failure preserves the prior session and model snapshot");
                Check(!fixture.View.CanModifyGatewaySession && !fixture.View.CanStopGatewayRun, "Stale data cannot authorize session mutations or exact Stop");
                Check(!fixture.View.TakeRefresh(), "Retry does not execute before its backoff deadline");
                fixture.View.AutoRefreshEnabled = false;
                Check(!fixture.View.TakeRefresh(DateTimeOffset.UtcNow.AddMinutes(1)), "Disabled automatic recovery suppresses RPC retry without consuming it");
                fixture.View.AutoRefreshEnabled = true;
            }
            Check(fixture.View.TakeRefresh(DateTimeOffset.UtcNow.AddMinutes(1)) == (attempt < 3), "Automatic refresh retry budget is bounded: attempt " + attempt);
        }
        await fixture.Server.SendAsync(new { type = "event", @event = "chat", seq = 1, payload = new { sessionKey = Key, sessionId = "physical", runId = "run-a", state = "delta" } });
        await UntilAsync(() => fixture.View.EventCount == 1);
        Check(!fixture.View.TakeRefresh(DateTimeOffset.UtcNow.AddMinutes(1)), "Repeated streaming for the same observed run cannot reopen an exhausted retry budget");
        await fixture.Server.SendAsync(new { type = "event", @event = "sessions.changed", seq = 2, payload = new { key = Key } });
        await UntilAsync(() => fixture.View.EventCount == 2);
        Check(!fixture.View.TakeRefresh(), "A new server change respects the recovery cooldown after retry exhaustion");
        Check(fixture.View.TakeRefresh(DateTimeOffset.UtcNow.AddMinutes(1)), "A new server change reopens exhausted retries after the cooldown");
        var eventRecovery = fixture.View.RefreshData();
        await CompleteRefreshAsync(fixture, History("physical", active: true, sample: 150));
        await eventRecovery;
        Check(!fixture.View.GatewayHasError && fixture.View.CanStopGatewayRun, "Server events can recover a stable page without requiring manual refresh");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var refresh = fixture.View.RefreshData();
            var history = await ReceiveAsync(fixture, "chat.history");
            await fixture.Server.SendAsync(new { type = "res", id = history.GetProperty("id").GetString(), ok = false, error = new { code = "UNAVAILABLE" } });
            await refresh;
            fixture.View.TakeRefresh(DateTimeOffset.UtcNow.AddMinutes(1));
        }
        var manual = ExecuteAsync(fixture.View.RefreshGatewayCommand);
        await CompleteRefreshAsync(fixture, History("physical", active: true, sample: 200), roster: true);
        await manual;
        Check(!fixture.View.GatewayHasError && fixture.View.CanStopGatewayRun, "Manual refresh recovers after the automatic retry budget is exhausted");
        var failed = fixture.View.RefreshData();
        var request = await ReceiveAsync(fixture, "chat.history");
        fixture.View.SetSession(History("replacement", key: "agent:main:other"));
        await fixture.Server.SendAsync(new { type = "res", id = request.GetProperty("id").GetString(), ok = false, error = new { code = "UNAVAILABLE" } });
        await failed;
        Check(fixture.View.PhysicalSession == "replacement" && !fixture.View.GatewayHasError && !fixture.View.TakeRefresh(DateTimeOffset.UtcNow.AddMinutes(1)), "An old target failure cannot mark or schedule retries for a new session");
    }

    private static async Task ServerModelChangesAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.View.SetSession(History("physical"));
        var refresh = fixture.View.RefreshData();
        await CompleteRefreshAsync(fixture, History("physical"));
        await refresh;
        refresh = fixture.View.RefreshData();
        await CompleteRefreshAsync(fixture, History("physical", model: "two", thinking: "quick", sample: 200));
        await refresh;
        Check(fixture.View.SelectedGatewayModel?.Id == "provider/two" && fixture.View.SelectedGatewayThinkingLevel?.Id == "quick", "Unedited selectors follow model and thinking changes from another client");
        fixture.View.SelectedGatewayModel = fixture.View.GatewayModels.Single(model => model.Id == "provider/one");
        fixture.View.SelectedGatewayThinkingLevel = fixture.View.GatewayThinkingLevels.Single(level => level.Id == "deep");
        refresh = fixture.View.RefreshData();
        await CompleteRefreshAsync(fixture, History("physical", model: "two", thinking: "quick", sample: 300), models: false);
        await refresh;
        Check(fixture.View.SelectedGatewayModel?.Id == "provider/one" && fixture.View.SelectedGatewayThinkingLevel?.Id == "deep", "A real unsubmitted model and thinking draft survives background refresh");
        fixture.View.SetSession(History("next", key: "agent:main:other", model: "two", thinking: "quick"));
        refresh = fixture.View.RefreshData();
        await CompleteRefreshAsync(fixture, History("next", key: "agent:main:other", model: "two", thinking: "quick", sample: 200));
        await refresh;
        Check(fixture.View.SelectedGatewayModel?.Id == "provider/two" && fixture.View.SelectedGatewayThinkingLevel?.Id == "quick", "Session changes retire the previous model draft");
    }

    private static async Task AutomaticRefreshRecoveryAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.View.SetSession(History("physical", active: true), "run-a");
        var initial = fixture.View.RefreshData();
        await CompleteRefreshAsync(fixture, History("physical", active: true));
        await initial;
        fixture.View.ExpireModels();
        var failed = fixture.View.RefreshData();
        var request = await ReceiveAsync(fixture, "chat.history");
        await fixture.Server.ReplyAsync(request, History("physical", active: true, sample: 200));
        request = await ReceiveAsync(fixture, "models.list");
        await fixture.Server.SendAsync(new { type = "res", id = request.GetProperty("id").GetString(), ok = false, error = new { code = "UNAVAILABLE" } });
        await failed;
        Check(fixture.View.GatewayModels.Count == 3 && !fixture.View.CanModifyGatewaySession && fixture.View.CanStopGatewayRun,
            "Model-catalog failure preserves displayed choices, blocks model mutations, and retains exact Stop");
        Check(fixture.View.TakeRefresh(DateTimeOffset.UtcNow.AddMinutes(1)), "Catalog failure schedules automatic recovery");
        fixture.View.AllowModelRetry();
        var recovered = fixture.View.RefreshData();
        await CompleteRefreshAsync(fixture, History("physical", active: true, sample: 300));
        await recovered;
        Check(!fixture.View.GatewayHasError && fixture.View.CanStopGatewayRun, "Successful retry restores controls and clears only its own read error");
        failed = fixture.View.RefreshData();
        request = await ReceiveAsync(fixture, "chat.history");
        await fixture.Server.SendAsync(new { type = "res", id = request.GetProperty("id").GetString(), ok = false, error = new { code = "FORBIDDEN" } });
        await failed;
        Check(!fixture.View.TakeRefresh(DateTimeOffset.UtcNow.AddMinutes(1)), "Permission rejection does not enter automatic RPC retries");
    }

    private static async Task ServerSearchAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.View.SetSession(History("physical"));
        fixture.View.AutoRefreshEnabled = false;
        fixture.View.GatewaySearchText = "discarded";
        fixture.View.GatewaySearchText = "old";
        Check(!fixture.View.TakeRefresh() && fixture.View.GatewaySessionsLoading && !fixture.View.CanSelectGatewaySession,
            "Search debounces independently of automatic recovery and disables old results immediately");
        var oldRefresh = fixture.View.PendingSearch;
        var oldRequest = await ReceiveAsync(fixture, "sessions.list");
        Check(oldRequest.GetProperty("params").GetProperty("search").GetString() == "old", "Rapid typing sends only the latest debounced query without a monitor tick");
        fixture.View.GatewaySearchText = " needle ";
        Check(!fixture.View.GatewayHasMore, "Changing search immediately invalidates pagination");
        await fixture.Server.ReplyAsync(oldRequest, new { sessions = new[] { new OpenClawSession { Key = "agent:main:old" } }, hasMore = false });
        await oldRefresh;
        Check(fixture.View.GatewaySessions.All(item => item.Key != "agent:main:old") && fixture.View.GatewaySessionsLoading,
            "Retired search cannot publish results or clear the replacement loading state");
        var list = await ReceiveAsync(fixture, "sessions.list");
        Check(list.GetProperty("params").GetProperty("search").GetString() == "needle" && list.GetProperty("params").GetProperty("offset").GetInt32() == 0, "Search is sent to Gateway from offset zero");
        var matches = Enumerable.Range(0, 60).Select(i => new OpenClawSession { Key = "agent:main:remote" + i, AgentId = "main", SessionId = "remote" + i, Label = "Server match " + i }).ToArray();
        await fixture.Server.ReplyAsync(list, new { sessions = matches, hasMore = true, nextOffset = 60 });
        await fixture.View.PendingSearch;
        Check(fixture.View.GatewaySessions.Count == 60 && !fixture.View.GatewaySessionsLoading && fixture.View.CanSelectGatewaySession,
            "Search completion publishes server matches and restores list interaction");
        Check(fixture.View.SelectedGatewaySession?.Key == Key && fixture.View.PhysicalSession == "physical", "A filtered-out session remains the exact active page target");
        var more = ExecuteAsync(fixture.View.LoadMoreGatewaySessionsCommand);
        list = await ReceiveAsync(fixture, "sessions.list");
        Check(list.GetProperty("params").GetProperty("search").GetString() == "needle" && list.GetProperty("params").GetProperty("offset").GetInt32() == 60, "Load more retains the active search and server cursor");
        fixture.View.GatewaySearchText = "";
        await fixture.Server.ReplyAsync(list, new { sessions = new[] { new OpenClawSession { Key = "agent:main:late-page" } }, hasMore = false });
        await more;
        Check(fixture.View.GatewaySessions.All(item => item.Key != "agent:main:late-page"), "Late pages cannot append into a changed query");
        list = await ReceiveAsync(fixture, "sessions.list");
        Check(!list.GetProperty("params").TryGetProperty("search", out var search) || search.ValueKind == JsonValueKind.Null, "Clearing search requests the unfiltered server roster");
        await fixture.Server.ReplyAsync(list, new { sessions = new[] { History("physical").SessionInfo }, hasMore = false });
        await fixture.View.PendingSearch;
        Check(fixture.View.GatewaySessions.Count == 1 && fixture.View.GatewaySessions[0].Key == Key, "Clearing search replaces the filtered list with the default roster");
        fixture.View.GatewaySearchText = "failing";
        var oldFailure = fixture.View.PendingSearch;
        list = await ReceiveAsync(fixture, "sessions.list");
        fixture.View.GatewaySearchText = "replacement";
        await fixture.Server.SendAsync(new { type = "res", id = list.GetProperty("id").GetString(), ok = false, error = new { code = "FORBIDDEN" } });
        await oldFailure;
        Check(!fixture.View.GatewayHasError && fixture.View.GatewaySessionsLoading, "A retired search error cannot invalidate or block its replacement query");
        list = await ReceiveAsync(fixture, "sessions.list");
        await fixture.Server.ReplyAsync(list, new { sessions = Array.Empty<OpenClawSession>(), hasMore = false });
        await fixture.View.PendingSearch;
        Check(fixture.View.GatewayRosterLoaded && !fixture.View.GatewaySessionsLoading && fixture.View.GatewaySessions.Count == 0,
            "An empty result becomes an empty state only after the current query completes");
        fixture.View.GatewaySearchText = "cancel before dispatch";
        await fixture.View.StopSearchForTest();
        Check(!fixture.View.GatewaySessionsLoading, "Runtime cancellation retires the pending debounce and clears loading");
        await AssertNoExtraRequestsAsync(fixture);
    }

    private static async Task LabelWithoutModelsAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.View.SetSession(History("physical", label: "Original"));
        var refresh = fixture.View.RefreshData();
        var request = await ReceiveAsync(fixture, "chat.history");
        await fixture.Server.ReplyAsync(request, History("physical", label: "Original"));
        request = await ReceiveAsync(fixture, "models.list");
        await fixture.Server.SendAsync(new { type = "res", id = request.GetProperty("id").GetString(), ok = false, error = new { code = "FORBIDDEN" } });
        await refresh;
        fixture.View.GatewaySessionLabel = "Renamed";
        Check(fixture.View.CanEditGatewayLabel && fixture.View.CanApplyGatewayLabel && !fixture.View.CanModifyGatewaySession,
            "Unavailable model catalog does not disable session label editing");
        fixture.View.SetReadOnly(true);
        Check(!fixture.View.CanEditGatewayLabel && !fixture.View.CanApplyGatewayLabel, "Read-only sessions still reject label writes");
        fixture.View.SetReadOnly(false);
        fixture.View.SetStale(true);
        Check(!fixture.View.CanApplyGatewayLabel, "Stale session identity still rejects label writes");
        fixture.View.SetStale(false);
        var command = ExecuteAsync(fixture.View.ApplyGatewayLabelCommand);
        request = await ReceiveAsync(fixture, "sessions.patch");
        Check(request.GetProperty("params").GetProperty("label").GetString() == "Renamed", "Label can be saved after catalog failure");
        await fixture.Server.ReplyAsync(request, new { ok = true });
        await CompleteRefreshAsync(fixture, History("physical", label: "Renamed", sample: 200), roster: true, models: false);
        await command;
        Check(!fixture.View.CanApplyGatewayLabel && fixture.View.GatewaySessionLabel == "Renamed", "Label save reconciles with catalog still unavailable");
    }

    private static async Task SearchWhileHistoryPendingAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.View.SetSession(History("physical"));
        var history = fixture.View.RefreshData();
        var request = await ReceiveAsync(fixture, "chat.history");
        fixture.View.GatewaySearchText = "queued old query";
        var oldSearch = fixture.View.PendingSearch;
        await Task.Delay(350);
        Check(!oldSearch.IsCompleted && fixture.View.GatewaySessionsLoading, "Debounced search waits for the active history read without losing loading state");
        fixture.View.GatewaySearchText = "latest query";
        fixture.View.SetSession(History("replacement", key: "agent:main:replacement"));
        await fixture.Server.ReplyAsync(request, History("physical", sample: 200));
        await history;
        await oldSearch;
        request = await ReceiveAsync(fixture, "sessions.list");
        Check(request.GetProperty("params").GetProperty("search").GetString() == "latest query",
            "A superseded search waiting for the gate never dispatches its old query");
        await fixture.Server.ReplyAsync(request, new { sessions = Array.Empty<OpenClawSession>(), hasMore = false });
        await fixture.View.PendingSearch;
        Check(fixture.View.PhysicalSession == "replacement" && fixture.View.GatewayRosterLoaded && !fixture.View.GatewaySessionsLoading,
            "Roster-only search preserves the newer confirmed page target");
        await AssertNoExtraRequestsAsync(fixture);
    }

    private static async Task RecoveryPresentationAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.View.SetSession(History("physical"));
        fixture.View.PresentFailure(new OpenClawGatewayException(OpenClawFailureKind.Timeout));
        Check(fixture.View.GatewayErrorAction == GatewayRecoveryAction.Refresh && fixture.View.GatewayHasErrorAction,
            "Uncertain writes offer a read refresh instead of configuration or replay");
        fixture.View.PresentFailure(new OpenClawGatewayException(OpenClawFailureKind.Rejected, detailCode: "CREDENTIAL_ENDPOINT_CHANGED"));
        Check(fixture.View.GatewayErrorAction == GatewayRecoveryAction.Configure, "Credential endpoint mismatch offers configuration");
        fixture.View.PresentFailure(new OpenClawGatewayException(OpenClawFailureKind.Rejected, code: "UNAVAILABLE"));
        Check(fixture.View.GatewayErrorAction == GatewayRecoveryAction.Refresh, "Temporary server rejection offers refresh");
        fixture.View.PresentFailure(new OpenClawGatewayException(OpenClawFailureKind.Rejected, pairingRequestId: "pairing-fixture"));
        Check(fixture.View.GatewayErrorAction == GatewayRecoveryAction.Refresh, "Pending pairing offers reconnect after approval");
        fixture.View.SetDraft(true);
        await fixture.View.SelectCurrentSession();
        Check(fixture.View.GatewayErrorAction == GatewayRecoveryAction.ReturnToPage, "Unsaved chat draft offers return to the page");
        fixture.View.SetDraft(false);
        fixture.View.GatewaySessionLabel = new string('a', 513);
        await ExecuteAsync(fixture.View.ApplyGatewayLabelCommand);
        Check(fixture.View.GatewayHasError && !fixture.View.GatewayHasErrorAction, "Invalid label has no unrelated configuration action");
        foreach (var key in new[] { "WaitingForPage", "Stopping", "Reconciling" })
        {
            fixture.View.PresentRun(key);
            Check(fixture.View.WorkStatusText.Length < fixture.View.GatewayRunText.Length, "Toolbar uses a compact status while details retain guidance: " + key);
        }
    }

    private static async Task FilteredAgentScopeAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string agentKey = "agent:research:workspace";
        fixture.View.SetSession(History("research-session", key: agentKey, agentId: "research"));
        fixture.View.GatewaySearchText = "unrelated";
        var refresh = fixture.View.RefreshRoster();
        var request = await ReceiveAsync(fixture, "sessions.list");
        await fixture.Server.ReplyAsync(request, new { sessions = Array.Empty<OpenClawSession>(), hasMore = false });
        request = await ReceiveAsync(fixture, "chat.history");
        Check(request.GetProperty("params").GetProperty("agentId").GetString() == "research" && request.GetProperty("params").GetProperty("sessionKey").GetString() == agentKey,
            "Filtered-out nondefault session retains canonical agent scope for history reads");
        await fixture.Server.ReplyAsync(request, History("research-session", key: agentKey, agentId: "research", sample: 200));
        request = await ReceiveAsync(fixture, "models.list");
        Check(request.GetProperty("params").GetProperty("agentId").GetString() == "research",
            "Baseline v4 model catalog retains nondefault agent scope without session-scoped catalog capability");
        await fixture.Server.ReplyAsync(request, new { models = Models });
        await refresh;
        Check(fixture.View.GatewaySessions.Count == 0 && fixture.View.SelectedGatewaySession?.Key == agentKey && fixture.View.CanModifyGatewaySession,
            "Empty search results preserve the confirmed nondefault session control target");
    }

    private static OpenClawChatHistory History(string sessionId, bool active = false, long sample = 100, string key = Key,
        string model = "one", string thinking = "deep", string? label = "Original", string agentId = "main") => new()
    {
        SessionKey = key,
        SessionId = sessionId,
        SessionInfo = new OpenClawSession { Key = key, AgentId = agentId, SessionId = sessionId, Label = label, Model = model, ModelProvider = "provider", ThinkingLevel = thinking,
            HasActiveRun = active, ActiveRunIds = active ? ["run-a"] : [], SnapshotAt = sample, UpdatedAt = 123456, TotalTokens = 1200, TotalTokensFresh = true },
    };

    private static async Task CatalogFailureRetainsExactStopAsync()
    {
        foreach (var code in new[] { "UNAVAILABLE", "FORBIDDEN" })
        {
            await using var fixture = await Fixture.CreateAsync();
            fixture.View.SetSession(History("physical", active: true), "run-a");
            var initial = fixture.View.RefreshData();
            await CompleteRefreshAsync(fixture, History("physical", active: true));
            await initial;
            fixture.View.ExpireModels();
            var refresh = fixture.View.RefreshData();
            var request = await ReceiveAsync(fixture, "chat.history");
            await fixture.Server.ReplyAsync(request, History("physical", active: true, sample: 200));
            request = await ReceiveAsync(fixture, "models.list");
            Check(fixture.View.CanStopGatewayRun && !fixture.View.CanModifyGatewaySession,
                "Pending catalog refresh retains confirmed Stop and fences expired model choices: " + code);
            await fixture.Server.SendAsync(new { type = "res", id = request.GetProperty("id").GetString(), ok = false, error = new { code } });
            await refresh;
            Check(fixture.View.CanStop && fixture.View.GatewayHasError && !fixture.View.CanModifyGatewaySession,
                "Catalog rejection preserves toolbar Stop and exposes its error: " + code);
            await ExecuteAsync(fixture.View.ApplyGatewayModelCommand);
            var stop = fixture.View.StopFromToolbar();
            request = await ReceiveAsync(fixture, "chat.abort");
            var parameters = request.GetProperty("params");
            Check(parameters.GetProperty("runId").GetString() == "run-a" && parameters.GetProperty("sessionId").GetString() == "physical" &&
                parameters.GetProperty("agentId").GetString() == "main" && parameters.GetProperty("sessionKey").GetString() == Key,
                "Catalog failure permits only the precise fenced abort, never a model patch: " + code);
            await fixture.Server.ReplyAsync(request, new { aborted = true });
            await stop;
            var recovery = fixture.View.RefreshPending();
            request = await ReceiveAsync(fixture, "chat.history");
            await fixture.Server.ReplyAsync(request, History("physical", active: true, sample: 300));
            await recovery;
            await AssertNoExtraRequestsAsync(fixture);
            Check(fixture.View.CanStopGatewayRun, "History continues independently of catalog retry backoff: " + code);
            if (code == "UNAVAILABLE")
            {
                fixture.View.AutoRefreshEnabled = false;
                Check(!fixture.View.TakeRefresh(DateTimeOffset.UtcNow.AddMinutes(1)), "AutoRefresh off preserves pending catalog retry without executing it");
                fixture.View.AutoRefreshEnabled = true;
                fixture.View.AllowModelRetry();
                recovery = fixture.View.RefreshPending();
                request = await ReceiveAsync(fixture, "models.list");
                await fixture.Server.ReplyAsync(request, new { models = Models });
                await recovery;
                await AssertNoExtraRequestsAsync(fixture);
                Check(fixture.View.CanModifyGatewaySession, "Catalog-only retry restores model controls without history or roster reads");
            }
        }
    }

    private static async Task ScopedLargeRosterRefreshAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.View.SetSession(History("physical", active: true), "run-a");
        var rows = Enumerable.Range(0, 600).Select(index => index == 0 ? History("physical", active: true).SessionInfo! :
            new OpenClawSession { Key = "agent:main:session" + index, AgentId = "main", SessionId = "session" + index, Label = "Session " + index }).ToArray();
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        fixture.View.GatewaySessions.CollectionChanged += (_, change) => changes.Add(change.Action);
        var refresh = fixture.View.RefreshRoster();
        var request = await ReceiveAsync(fixture, "sessions.list");
        await fixture.Server.ReplyAsync(request, new { sessions = rows.Take(60), hasMore = true, nextOffset = 60 });
        await CompleteRefreshAsync(fixture, History("physical", active: true));
        await refresh;
        for (var offset = 60; offset < 600; offset += 60)
        {
            var existing = fixture.View.GatewaySessions.ToArray();
            changes.Clear();
            var more = ExecuteAsync(fixture.View.LoadMoreGatewaySessionsCommand);
            request = await ReceiveAsync(fixture, "sessions.list");
            Check(request.GetProperty("params").GetProperty("offset").GetInt32() == offset, "Large roster follows server cursor " + offset);
            await fixture.Server.ReplyAsync(request, new { sessions = rows.Skip(offset).Take(60), hasMore = offset < 540, nextOffset = offset + 60 });
            await more;
            Check(changes.Count == 60 && changes.All(action => action == System.Collections.Specialized.NotifyCollectionChangedAction.Add) &&
                existing.Select((item, index) => ReferenceEquals(item, fixture.View.GatewaySessions[index])).All(same => same),
                "Load more appends exactly one page and retains existing item identities at " + offset);
        }
        var stable = fixture.View.GatewaySessions.ToArray();
        var modelItem = fixture.View.SelectedGatewayModel;
        changes.Clear();
        var start = Requests.Count;
        await fixture.Server.SendAsync(new { type = "event", @event = "chat", seq = 1, payload = new { sessionKey = Key, sessionId = "physical", runId = "run-a", state = "final" } });
        await UntilAsync(() => fixture.View.EventCount == 1);
        refresh = fixture.View.RefreshPending();
        request = await ReceiveAsync(fixture, "chat.history");
        var overlap = fixture.View.RefreshData();
        await overlap;
        await fixture.Server.ReplyAsync(request, History("physical", sample: 200));
        await refresh;
        await AssertNoExtraRequestsAsync(fixture);
        Check(Requests.Skip(start).Select(item => item.GetProperty("method").GetString()).SequenceEqual(new[] { "chat.history", "fixture.barrier" }),
            "Single terminal event on 600 loaded sessions sends one history read and no roster or catalog requests");
        Check(changes.Count == 1 && changes[0] == System.Collections.Specialized.NotifyCollectionChangedAction.Replace &&
            stable.Skip(1).Select((item, index) => ReferenceEquals(item, fixture.View.GatewaySessions[index + 1])).All(same => same),
            "Terminal refresh replaces only the changed current row and retains the other 599 identities");
        Check(ReferenceEquals(modelItem, fixture.View.SelectedGatewayModel), "History-only refresh preserves cached model item identity");
        refresh = fixture.View.RefreshPending();
        await CompleteRefreshAsync(fixture, History("physical", sample: 200), models: false);
        await refresh;
        Check(!fixture.View.TakeRefresh(), "Overlapping reads coalesce to one follow-up and leave no queued work");
        changes.Clear();
        refresh = fixture.View.RefreshRoster();
        for (var offset = 0; offset < 600; offset += 60)
        {
            request = await ReceiveAsync(fixture, "sessions.list");
            await fixture.Server.ReplyAsync(request, new { sessions = rows.Skip(offset).Take(60), hasMore = offset < 540, nextOffset = offset + 60 });
        }
        await CompleteRefreshAsync(fixture, History("physical", sample: 200), models: false);
        await refresh;
        Check(changes.Count == 0 && fixture.View.GatewaySessions.Count == 600, "Unchanged 600-session refresh emits no collection events, including no Reset");
        start = Requests.Count;
        for (var seq = 2; seq <= 4; seq++)
        {
            await fixture.Server.SendAsync(new { type = "event", @event = "agent", seq, payload = new { sessionKey = Key, sessionId = "physical", runId = "run-a", stream = "lifecycle", data = new { phase = "end" } } });
        }
        await UntilAsync(() => fixture.View.EventCount == 4);
        fixture.View.AutoRefreshEnabled = false;
        refresh = fixture.View.RefreshPending();
        await CompleteRefreshAsync(fixture, History("physical", sample: 200), models: false);
        await refresh;
        await AssertNoExtraRequestsAsync(fixture);
        Check(Requests.Count - start == 2 && !fixture.View.TakeRefresh(), "Lifecycle burst coalesces to one history read even with automatic recovery disabled");
    }

    private static async Task CatalogOwnershipAndInvalidationAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.View.SetSession(History("physical", active: true), "run-a");
        var refresh = fixture.View.RefreshData();
        var request = await ReceiveAsync(fixture, "chat.history");
        await fixture.Server.ReplyAsync(request, History("physical", active: true));
        request = await ReceiveAsync(fixture, "models.list");
        await fixture.Server.SendAsync(new { type = "event", @event = "chat", seq = 1, payload = new { sessionKey = Key, sessionId = "physical", runId = "run-a", state = "final" } });
        await UntilAsync(() => fixture.View.EventCount == 1);
        await fixture.Server.ReplyAsync(request, new { models = Models });
        await refresh;
        Check(fixture.View.CanModifyGatewaySession && !fixture.View.CanStopGatewayRun && fixture.View.GatewayRunText == GatewayStrings.Text("Completed"),
            "A terminal event during model fetch accepts the owned catalog while preserving terminal run state");
        refresh = fixture.View.RefreshPending();
        await CompleteRefreshAsync(fixture, History("physical", sample: 200), models: false);
        await refresh;
        fixture.View.ExpireModels();
        fixture.View.AutoRefreshEnabled = false;
        Check(!fixture.View.TakeRefresh() && !fixture.View.CanModifyGatewaySession, "Expired catalog blocks mutation and AutoRefresh off suppresses idle expiry work");
        fixture.View.AutoRefreshEnabled = true;
        refresh = fixture.View.RefreshPending();
        request = await ReceiveAsync(fixture, "models.list");
        await fixture.Server.ReplyAsync(request, new { models = Models });
        await refresh;
        Check(fixture.View.CanModifyGatewaySession, "Five-minute catalog expiry refreshes without a history or roster request");
        refresh = ExecuteAsync(fixture.View.RefreshGatewayCommand);
        await CompleteRefreshAsync(fixture, History("physical", sample: 300), roster: true);
        await refresh;
        Check(fixture.View.CanModifyGatewaySession, "Manual refresh invalidates an otherwise fresh catalog");
        await fixture.Server.SendAsync(new { type = "event", @event = "models.changed", seq = 2, payload = new { agentId = "other" } });
        await UntilAsync(() => fixture.View.EventCount == 2);
        Check(fixture.View.CanModifyGatewaySession && !fixture.View.TakeRefresh(), "Another agent's model event leaves the current scoped cache intact");
        await fixture.Server.SendAsync(new { type = "event", @event = "models.changed", seq = 3, payload = new { agentId = "main" } });
        await UntilAsync(() => fixture.View.EventCount == 3);
        Check(!fixture.View.CanModifyGatewaySession, "Current-agent model event immediately fences stale catalog mutations");
        refresh = fixture.View.RefreshPending();
        request = await ReceiveAsync(fixture, "chat.history");
        await fixture.Server.ReplyAsync(request, History("physical", sample: 400));
        request = await ReceiveAsync(fixture, "models.list");
        await fixture.Server.SendAsync(new { type = "event", @event = "models.changed", seq = 4, payload = new { agentId = "main" } });
        await UntilAsync(() => fixture.View.EventCount == 4);
        await fixture.Server.ReplyAsync(request, new { models = Models });
        await refresh;
        Check(!fixture.View.CanModifyGatewaySession, "Model invalidation during fetch rejects the older catalog response");
        refresh = fixture.View.RefreshPending();
        await CompleteRefreshAsync(fixture, History("physical", sample: 500));
        await refresh;
        Check(fixture.View.CanModifyGatewaySession, "Coalesced model invalidation recovers with a newly owned catalog");
        refresh = fixture.View.RefreshData();
        await CompleteRefreshAsync(fixture, History("replacement", sample: 600));
        await refresh;
        Check(fixture.View.PhysicalSession == "replacement" && fixture.View.CanModifyGatewaySession, "Same-key physical session replacement obtains a separate catalog");
        fixture.View.ExpireModels();
        refresh = fixture.View.RefreshData();
        request = await ReceiveAsync(fixture, "chat.history");
        await fixture.Server.ReplyAsync(request, History("replacement", sample: 700));
        request = await ReceiveAsync(fixture, "models.list");
        fixture.View.SetSession(History("other", key: "agent:research:other", agentId: "research"));
        fixture.View.SetSession(History("newest", sample: 800));
        await fixture.Server.ReplyAsync(request, new { models = Models });
        await refresh;
        Check(fixture.View.GatewayModels.Count == 0 && !fixture.View.CanModifyGatewaySession && fixture.View.PhysicalSession == "newest",
            "A to B to A target changes reject late model results despite the matching logical session key");
        await AssertNoExtraRequestsAsync(fixture);
    }

    private static async Task AssertNoExtraRequestsAsync(Fixture fixture)
    {
        var barrier = fixture.Runtime.Client!.RequestAsync<JsonElement>("fixture.barrier", new { });
        var request = await ReceiveAsync(fixture, "fixture.barrier");
        await fixture.Server.ReplyAsync(request, new { });
        await barrier;
    }

    private static async Task CatalogRetryBudgetAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.View.SetSession(History("physical", active: true), "run-a");
        var refresh = fixture.View.RefreshData();
        await CompleteRefreshAsync(fixture, History("physical", active: true));
        await refresh;
        fixture.View.ExpireModels();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            fixture.View.AllowModelRetry();
            refresh = fixture.View.RefreshPending();
            var request = await ReceiveAsync(fixture, "models.list");
            await fixture.Server.SendAsync(new { type = "res", id = request.GetProperty("id").GetString(), ok = false, error = new { code = "UNAVAILABLE" } });
            await refresh;
            Check(fixture.View.CanStopGatewayRun && !fixture.View.CanModifyGatewaySession && !fixture.View.TakeRefresh(),
                "Catalog retry failure retains exact Stop and respects backoff: attempt " + attempt);
        }
        Check(!fixture.View.TakeRefresh(DateTimeOffset.UtcNow.AddMinutes(1)), "Catalog retries stop after the initial attempt and three automatic retries");
        await fixture.Server.SendAsync(new { type = "event", @event = "models.changed", seq = 1, payload = new { agentId = "main" } });
        await UntilAsync(() => fixture.View.EventCount == 1);
        refresh = fixture.View.RefreshPending();
        await CompleteRefreshAsync(fixture, History("physical", active: true, sample: 200), models: false);
        await refresh;
        await AssertNoExtraRequestsAsync(fixture);
        Check(fixture.View.CanStopGatewayRun && !fixture.View.TakeRefresh(), "Server change during catalog cooldown refreshes history without reopening model retries");
        fixture.View.AllowModelRecovery();
        await fixture.Server.SendAsync(new { type = "event", @event = "models.changed", seq = 2, payload = new { agentId = "main" } });
        await UntilAsync(() => fixture.View.EventCount == 2);
        refresh = fixture.View.RefreshPending();
        await CompleteRefreshAsync(fixture, History("physical", active: true, sample: 300));
        await refresh;
        Check(fixture.View.CanModifyGatewaySession && !fixture.View.GatewayHasError, "A later model change reopens exhausted catalog retries after cooldown");
    }

    private static async Task CatalogConnectionOwnershipAsync()
    {
        await using var previous = await Fixture.CreateAsync();
        await using var replacement = await Fixture.CreateAsync();
        previous.View.SetSession(History("physical"));
        var refresh = previous.View.RefreshData();
        await CompleteRefreshAsync(previous, History("physical"));
        await refresh;
        previous.View.UseRuntime(replacement.Runtime);
        Check(!previous.View.CanModifyGatewaySession, "A new connection cannot authorize mutation with the previous connection's cached catalog");
        refresh = previous.View.RefreshData();
        await CompleteRefreshAsync(replacement, History("physical", sample: 200));
        await refresh;
        Check(previous.View.CanModifyGatewaySession, "The replacement connection fetches its own catalog even for identical agent and session IDs");
        previous.View.ExpireModels();
        refresh = previous.View.RefreshPending();
        var request = await ReceiveAsync(replacement, "models.list");
        previous.View.UseRuntime(previous.Runtime);
        await replacement.Server.SendAsync(new { type = "res", id = request.GetProperty("id").GetString(), ok = false, error = new { code = "FORBIDDEN" } });
        await refresh;
        Check(!previous.View.GatewayHasError && !previous.View.CanModifyGatewaySession,
            "A late retired-connection catalog error cannot publish errors or authorize the current connection");
        refresh = previous.View.RefreshData();
        await CompleteRefreshAsync(previous, History("physical", sample: 300));
        await refresh;
        Check(previous.View.CanModifyGatewaySession, "Returning to a previous connection obtains fresh ownership instead of retaining another connection's catalog");
    }

    private static async Task RosterReconciliationAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.View.SetSession(History("physical"));
        var rows = new[] { History("physical").SessionInfo!,
            new OpenClawSession { Key = "agent:main:b", AgentId = "main", Label = "B" },
            new OpenClawSession { Key = "agent:main:c", AgentId = "main", Label = "C" },
            new OpenClawSession { Key = "agent:main:d", AgentId = "main", Label = "D" } };
        var refresh = fixture.View.RefreshRoster();
        var request = await ReceiveAsync(fixture, "sessions.list");
        await fixture.Server.ReplyAsync(request, new { sessions = rows, hasMore = false });
        await CompleteRefreshAsync(fixture, History("physical"));
        await refresh;
        var selected = fixture.View.SelectedGatewaySession;
        var retained = fixture.View.GatewaySessions[2];
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        fixture.View.GatewaySessions.CollectionChanged += (_, change) => changes.Add(change.Action);
        await fixture.Server.SendAsync(new { type = "event", @event = "sessions.changed", seq = 1, payload = new { key = "agent:main:b" } });
        await UntilAsync(() => fixture.View.EventCount == 1);
        refresh = fixture.View.RefreshPending();
        request = await ReceiveAsync(fixture, "sessions.list");
        await fixture.Server.ReplyAsync(request, new { sessions = new[] { rows[2], rows[0],
            new OpenClawSession { Key = "agent:main:b", AgentId = "main", Label = "Renamed" },
            new OpenClawSession { Key = "agent:main:e", AgentId = "main", Label = "New" } }, hasMore = false });
        await refresh;
        await AssertNoExtraRequestsAsync(fixture);
        Check(fixture.View.GatewaySessions.Select(item => item.Title).SequenceEqual(new[] { "C", "Original", "Renamed", "New" }),
            "Roster refresh reconciles server ordering, changes, insertion, and deletion");
        Check(ReferenceEquals(retained, fixture.View.GatewaySessions[0]) && ReferenceEquals(selected, fixture.View.SelectedGatewaySession) &&
            ReferenceEquals(selected, fixture.View.GatewaySessions[1]) && !changes.Contains(System.Collections.Specialized.NotifyCollectionChangedAction.Reset),
            "Roster moves retain unchanged item and selection identity without a collection Reset");
        Check(fixture.View.CanModifyGatewaySession, "An unrelated session change refreshes only the roster and retains the current model cache");
        fixture.View.GatewaySearchText = "needle";
        refresh = fixture.View.PendingSearch;
        request = await ReceiveAsync(fixture, "sessions.list");
        Check(request.GetProperty("params").GetProperty("search").GetString() == "needle", "Queued search refresh sends its server query");
        await fixture.Server.ReplyAsync(request, new { sessions = Array.Empty<OpenClawSession>(), hasMore = false });
        await refresh;
        await AssertNoExtraRequestsAsync(fixture);
        Check(fixture.View.GatewaySessions.Count == 0 && ReferenceEquals(selected, fixture.View.SelectedGatewaySession) && fixture.View.CanModifyGatewaySession,
            "Search-only refresh retains the confirmed filtered-out target without history or catalog requests");
    }

    private static async Task CompleteRefreshAsync(Fixture fixture, OpenClawChatHistory history, bool roster = false, bool models = true)
    {
        if (roster)
        {
            var list = await ReceiveAsync(fixture, "sessions.list");
            await fixture.Server.ReplyAsync(list, new { sessions = new[] { history.SessionInfo }, hasMore = false });
        }
        var request = await ReceiveAsync(fixture, "chat.history");
        await fixture.Server.ReplyAsync(request, history);
        if (models)
        {
            request = await ReceiveAsync(fixture, "models.list");
            await fixture.Server.ReplyAsync(request, new { models = Models });
        }
    }

    private static async Task<JsonElement> ReceiveAsync(Fixture fixture, string method)
    {
        var request = await fixture.Server.ReceiveAsync();
        Requests.Add(request);
        if (request.GetProperty("method").GetString() != method) { throw new Exception("Expected RPC " + method + ", received " + request); }
        return request;
    }

    private static async Task ExecuteAsync(ICommand command)
    {
        command.Execute(null);
        await UntilAsync(() => command.CanExecute(null));
    }

    private static async Task UntilAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) { await Task.Delay(10, timeout.Token); }
    }

    private static void Check(bool passed, string description)
    {
        if (!passed) { throw new Exception("FAIL: " + description); }
        Passed.Add(description);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public GatewayFixtures.FakeGateway Server { get; } = new();
        public OpenClawSessionRuntime Runtime { get; private set; } = null!;
        public MainViewModel View { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            var accepted = fixture.Server.AcceptHelloAsync();
            fixture.Runtime = new OpenClawSessionRuntime(_ => Task.FromResult(fixture.Server.Options()), (_, _) => Task.CompletedTask, false);
            var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Runtime.Connected += _ => connected.TrySetResult(true);
            fixture.Runtime.Start();
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await accepted;
            fixture.View = new MainViewModel(fixture.Runtime, SynchronizationContext.Current!);
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await View.StopSearchForTest();
            await Runtime.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    private sealed class RegressionContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));
        public void DispatchOne()
        {
            if (_queue.TryTake(out var next, 250)) { next.Callback(next.State); }
        }
        public void Dispose() => _queue.Dispose();
    }
}

namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
}

namespace Harness.Models
{
    public enum HostedBackendKind { OpenClaw, Generic }
    internal sealed class RegressionEnvironment
    {
        public bool IsPlaceholder => false;
        public HostedBackendKind Backend { get; set; } = HostedBackendKind.OpenClaw;
        public RegressionConnection OpenClawConnection { get; } = new();
        public string GatewayUrl => "https://fixture.invalid/";
    }
    internal sealed class RegressionConnection { public bool Enabled => true; }
}

namespace Harness.Helpers
{
    internal static class StringResources
    {
        private static Dictionary<string, string> _values = [];
        public static void Load(string path) => _values = XDocument.Load(path).Root!.Elements("data").ToDictionary(item => item.Attribute("name")!.Value, item => item.Element("value")!.Value);
        public static string Get(string key) => _values.TryGetValue(key, out var value) ? value : key;
        public static string NotificationRunCompletedTitle => Get(nameof(NotificationRunCompletedTitle));
    }
}

namespace Harness.ViewModels
{
    public partial class MainViewModel
    {
        private readonly RegressionRuntime _runtime = new();
        private readonly RegressionWebView _webViewService = new();
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly Harness.Models.RegressionEnvironment _selectedEnvironment = new();
        private bool _isDisposed;
        private bool _isLoading;
        public List<string> ChangedProperties { get; } = [];
        public int BrowserStopCount => _webViewService.StopCount;
        public Task StopFromToolbar() => OnStopAsync();
        public void SetLoading(bool loading) => IsLoading = loading;
        public void UseGenericBackend() => _selectedEnvironment.Backend = Harness.Models.HostedBackendKind.Generic;
        public string CurrentUrl => "https://fixture.invalid/";
        public string SelectedEnvironmentName => "Loopback fixture";
        public string ModelSummaryText { get; private set; } = "";
        public string DefaultModelSummary => "Default";
        public string WorkStatusText { get; private set; } = "";
        public object? WorkStatusBrush { get; private set; }
        public object? WarningBrush => null;
        public object? NeutralBrush => null;
        public bool IsWindowFocused => true;
        public bool AutoRefreshEnabled { get; set; } = true;
        public event Action<NativeNotificationRequest>? NotificationRequested;
        public string? PhysicalSession => _gatewayHistory?.SessionId;
        public int EventCount { get; private set; }
        internal MainViewModel(OpenClawSessionRuntime runtime, SynchronizationContext context)
        {
            _gatewayRuntime = runtime;
            _gatewayCancellation = new CancellationTokenSource();
            _gatewayCanWrite = true;
            InitializeGatewayCommands();
            runtime.EventReceived += value => context.Post(_ => { HandleGatewayEvent(runtime, value); EventCount++; }, null);
        }
        public void SetSession(OpenClawChatHistory history, string? run = null)
        {
            _webViewService.Selection = new OpenClawPageSelection(history.SessionKey!, run, false, false);
            SetGatewayPageSelection(_webViewService.Selection);
            _gatewayHistory = history;
            _gatewayRoster.Clear();
            _gatewayRoster.Add(history.SessionInfo!);
            ProjectGatewayRoster();
            SynchronizeGatewaySessionDetails();
            if (run is not null) { _gatewayObservedRuns.Add(history.SessionKey + "|" + run); }
            ApplyGatewayRunProjection();
        }
        public Task RefreshData() => RefreshGatewayDataAsync(_gatewayRuntime!, false, _gatewayCancellation!.Token);
        public Task RefreshRoster() => RefreshGatewayDataAsync(_gatewayRuntime!, true, _gatewayCancellation!.Token);
        public Task SelectCurrentSession() => SelectGatewaySessionAsync(SelectedGatewaySession!);
        public bool TakeRefresh(DateTimeOffset? now = null) => TakeGatewayRefreshRequest(false, now ?? DateTimeOffset.UtcNow) != GatewayRefreshScope.None;
        public void ExpireModels() => _gatewayModelsExpireAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        public void AllowModelRetry() => _gatewayModelRetryAt = default;
        public void AllowModelRecovery() => _gatewayModelRecoveryNotBefore = default;
        public Task PendingSearch => _gatewaySearchTask;
        public async Task StopSearchForTest()
        {
            var cancellation = _gatewayCancellation;
            StopGatewayRuntime();
            await _gatewaySearchTask;
            cancellation?.Dispose();
        }
        public void SetReadOnly(bool value) => _gatewayCanWrite = !value;
        public void SetStale(bool value) => _gatewayDataStale = value;
        public void PresentFailure(Exception error) => ShowGatewayError(error);
        public void PresentRun(string key) => SetGatewayRunPresentation(key, false);
        public void SetDraft(bool value) => _webViewService.Selection = _webViewService.Selection! with { HasDraft = value };
        internal void UseRuntime(OpenClawSessionRuntime runtime) => _gatewayRuntime = runtime;
        public Task RefreshPending()
        {
            var scope = TakeGatewayRefreshRequest(false, DateTimeOffset.UtcNow);
            return scope == GatewayRefreshScope.None ? Task.CompletedTask :
                RefreshGatewayDataAsync(_gatewayRuntime!, scope, _gatewayCancellation!.Token);
        }
        private void SynchronizeGatewayEnvironment(bool force = false) => throw new InvalidOperationException("Connection replacement is outside this regression fixture.");
        private void SetRunIndicatorMode(RunIndicatorMode mode) { }
        private void OnPropertyChanged([CallerMemberName] string name = "") => ChangedProperties.Add(name);
        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) { return false; }
            field = value;
            return true;
        }
        private sealed class RegressionWebView
        {
            public int StopCount { get; private set; }
            public Task StopAsync() { StopCount++; return Task.CompletedTask; }
            public OpenClawPageSelection? Selection { get; set; }
            public Task<OpenClawPageSelection?> ReadOpenClawSelectionAsync(Uri uri, CancellationToken token, OpenClawHello? hello) => Task.FromResult(Selection);
            public void Navigate(string uri) => throw new InvalidOperationException("Interactive browser navigation is outside this fixture.");
        }
        private sealed class RegressionRuntime
        {
            public RegressionLogger Logger { get; } = new();
            public RegressionConfiguration Configuration { get; } = new();
        }
        private sealed class RegressionLogger { public void Warning(string text, object context) { } }
        private sealed class RegressionConfiguration { public RegressionSettings Settings { get; } = new(); }
        private sealed class RegressionSettings
        {
            public bool NotifyOnRunCompleted => false;
            public bool NotifyOnlyWhenUnfocused => true;
        }
    }
}
