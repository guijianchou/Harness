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
            await StreamingHistoryAsync();
            await TerminalWhileSelectingAsync();
            await TransientRefreshFailureAsync();
            await AutomaticRefreshRecoveryAsync();
            await ServerModelChangesAsync();
            await ServerSearchAsync();
            await FilteredAgentScopeAsync();
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
        fixture.View.GatewaySessionLabel = "   ";
        var command = ExecuteAsync(fixture.View.ApplyGatewayLabelCommand);
        var request = await ReceiveAsync(fixture, "sessions.patch");
        var parameters = request.GetProperty("params");
        Check(parameters.GetProperty("label").ValueKind == JsonValueKind.Null, "Clearing session label sends explicit null");
        Check(parameters.GetProperty("expectedSessionId").GetString() == "physical", "Label clear preserves physical session fence");
        Check(!parameters.TryGetProperty("model", out _) && !parameters.TryGetProperty("thinkingLevel", out _), "Label patch leaves model and reasoning untouched");
        await fixture.Server.ReplyAsync(request, new { ok = true });
        await CompleteRefreshAsync(fixture, History("physical", label: null, sample: 200), roster: true);
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
        await CompleteRefreshAsync(fixture, History("physical", model: "two", thinking: "quick", sample: 300));
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
        var failed = fixture.View.RefreshData();
        var request = await ReceiveAsync(fixture, "chat.history");
        await fixture.Server.ReplyAsync(request, History("physical", active: true, sample: 200));
        request = await ReceiveAsync(fixture, "models.list");
        await fixture.Server.SendAsync(new { type = "res", id = request.GetProperty("id").GetString(), ok = false, error = new { code = "UNAVAILABLE" } });
        await failed;
        Check(fixture.View.GatewayModels.Count == 3 && !fixture.View.CanModifyGatewaySession, "Model-catalog failure preserves displayed choices but disables stale mutations");
        Check(fixture.View.TakeRefresh(DateTimeOffset.UtcNow.AddMinutes(1)), "Catalog failure schedules automatic recovery");
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
        fixture.View.GatewaySearchText = "old";
        Check(!fixture.View.TakeRefresh(), "Search input waits for the debounce deadline");
        var oldRefresh = fixture.View.RefreshRoster();
        var oldRequest = await ReceiveAsync(fixture, "sessions.list");
        fixture.View.GatewaySearchText = " needle ";
        Check(!fixture.View.GatewayHasMore, "Changing search immediately invalidates pagination");
        await fixture.Server.ReplyAsync(oldRequest, new { sessions = new[] { new OpenClawSession { Key = "agent:main:old" } }, hasMore = false });
        await oldRefresh;
        Check(fixture.View.GatewaySessions.All(item => item.Key != "agent:main:old"), "Late results from a prior search cannot replace the current list");
        var refresh = fixture.View.RefreshRoster();
        var list = await ReceiveAsync(fixture, "sessions.list");
        Check(list.GetProperty("params").GetProperty("search").GetString() == "needle" && list.GetProperty("params").GetProperty("offset").GetInt32() == 0, "Search is sent to Gateway from offset zero");
        var matches = Enumerable.Range(0, 60).Select(i => new OpenClawSession { Key = "agent:main:remote" + i, AgentId = "main", SessionId = "remote" + i, Label = "Server match " + i }).ToArray();
        await fixture.Server.ReplyAsync(list, new { sessions = matches, hasMore = true, nextOffset = 60 });
        await CompleteRefreshAsync(fixture, History("physical", sample: 200));
        await refresh;
        Check(fixture.View.GatewaySessions.Count == 60, "Server matches outside loaded titles are displayed without a second local filter");
        Check(fixture.View.SelectedGatewaySession?.Key == Key && fixture.View.PhysicalSession == "physical", "A filtered-out session remains the exact active page target");
        var more = ExecuteAsync(fixture.View.LoadMoreGatewaySessionsCommand);
        list = await ReceiveAsync(fixture, "sessions.list");
        Check(list.GetProperty("params").GetProperty("search").GetString() == "needle" && list.GetProperty("params").GetProperty("offset").GetInt32() == 60, "Load more retains the active search and server cursor");
        fixture.View.GatewaySearchText = "";
        await fixture.Server.ReplyAsync(list, new { sessions = new[] { new OpenClawSession { Key = "agent:main:late-page" } }, hasMore = false });
        await more;
        Check(fixture.View.GatewaySessions.All(item => item.Key != "agent:main:late-page"), "Late pages cannot append into a changed query");
        refresh = fixture.View.RefreshRoster();
        list = await ReceiveAsync(fixture, "sessions.list");
        Check(!list.GetProperty("params").TryGetProperty("search", out var search) || search.ValueKind == JsonValueKind.Null, "Clearing search requests the unfiltered server roster");
        await fixture.Server.ReplyAsync(list, new { sessions = new[] { History("physical").SessionInfo }, hasMore = false });
        await CompleteRefreshAsync(fixture, History("physical", sample: 300));
        await refresh;
        Check(fixture.View.GatewaySessions.Count == 1 && fixture.View.GatewaySessions[0].Key == Key, "Clearing search replaces the filtered list with the default roster");
        fixture.View.GatewaySearchText = "failing";
        var oldFailure = fixture.View.RefreshRoster();
        list = await ReceiveAsync(fixture, "sessions.list");
        fixture.View.GatewaySearchText = "replacement";
        await fixture.Server.SendAsync(new { type = "res", id = list.GetProperty("id").GetString(), ok = false, error = new { code = "FORBIDDEN" } });
        await oldFailure;
        Check(!fixture.View.GatewayHasError && fixture.View.CanModifyGatewaySession && fixture.View.TakeRefresh(DateTimeOffset.UtcNow.AddMinutes(1)), "A retired search error cannot invalidate or block its replacement query");
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

    private static async Task CompleteRefreshAsync(Fixture fixture, OpenClawChatHistory history, bool roster = false)
    {
        if (roster)
        {
            var list = await ReceiveAsync(fixture, "sessions.list");
            await fixture.Server.ReplyAsync(list, new { sessions = new[] { history.SessionInfo }, hasMore = false });
        }
        var request = await ReceiveAsync(fixture, "chat.history");
        await fixture.Server.ReplyAsync(request, history);
        request = await ReceiveAsync(fixture, "models.list");
        await fixture.Server.ReplyAsync(request, new { models = Models });
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
        public bool TakeRefresh(DateTimeOffset? now = null) => TakeGatewayRefreshRequest(false, now ?? DateTimeOffset.UtcNow);
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
