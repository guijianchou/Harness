// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Text.Json;
using Harness.Helpers;
using Harness.OpenClaw;
using Harness.Services;

namespace Harness.ViewModels;

public partial class MainViewModel
{
    private string? _gatewaySubscribedKey;
    private string? _gatewayEventRunId;

    [Flags]
    private enum GatewayRefreshScope
    {
        None = 0,
        History = 1,
        Roster = 2,
        Models = 4,
        All = History | Roster | Models,
    }

    private void RequestGatewayRefresh(GatewayRefreshScope scope) => Interlocked.Or(ref _gatewayRefreshRequested, (int)scope);

    private async Task SubscribeGatewayRosterAsync(OpenClawSessionRuntime runtime, CancellationToken cancellationToken)
    {
        var client = runtime.Client;
        if (client?.Hello?.Features.Methods.Contains("sessions.subscribe", StringComparer.Ordinal) != true)
        {
            return;
        }

        try
        {
            await client.RequestAsync<JsonElement>("sessions.subscribe", new { limit = 60 }, cancellationToken: cancellationToken);
        }
        catch (OpenClawGatewayException)
        {
            // A conservative capability list is not a promise of permission. Manual refresh remains usable.
        }
        finally
        {
            if (IsCurrentGateway(runtime))
            {
                RequestGatewayRefresh(GatewayRefreshScope.All);
            }
        }
    }

    private Task RefreshGatewayDataAsync(OpenClawSessionRuntime runtime, bool refreshRoster, CancellationToken cancellationToken) =>
        RefreshGatewayDataAsync(runtime, refreshRoster ? GatewayRefreshScope.Roster | GatewayRefreshScope.History : GatewayRefreshScope.History, cancellationToken);

    private async Task RefreshGatewayDataAsync(OpenClawSessionRuntime runtime, GatewayRefreshScope scope, CancellationToken cancellationToken, bool waitForGate = false)
    {
        if (!await _gatewayDataGate.WaitAsync(waitForGate ? Timeout.Infinite : 0, cancellationToken))
        {
            RequestGatewayRefresh(scope);
            return;
        }

        var client = runtime.Client;
        var pageKey = _gatewayPage?.SessionKey;
        var targetVersion = _gatewayTargetVersion;
        var searchVersion = _gatewaySearchVersion;
        var search = GatewaySearchText.Trim();
        var targetsSession = scope.HasFlag(GatewayRefreshScope.History) || scope.HasFlag(GatewayRefreshScope.Models);
        var readingHistory = false;
        try
        {
            if (!IsCurrentGateway(runtime) || client?.IsConnected != true)
            {
                return;
            }

            bool Current() => IsCurrentGateway(runtime) && ReferenceEquals(client, runtime.Client) &&
                client.IsConnected && (!targetsSession || (pageKey == _gatewayPage?.SessionKey && targetVersion == _gatewayTargetVersion)) &&
                (!scope.HasFlag(GatewayRefreshScope.Roster) || searchVersion == _gatewaySearchVersion) && !cancellationToken.IsCancellationRequested;

            if (scope.HasFlag(GatewayRefreshScope.Roster))
            {
                _gatewayRosterLoading = true;
                NotifyGatewayRosterState();
                var expectedCount = searchVersion == _gatewayRosterSearchVersion ? Math.Clamp(_gatewayRoster.Count, 60, 600) : 60;
                var roster = await client.ListSessionsAsync(cancellationToken: cancellationToken, search: search.Length == 0 ? null : search);
                var rows = roster.Sessions.ToList();
                var offsets = new HashSet<int>();
                while (rows.Count < expectedCount && roster.HasMore == true && roster.NextOffset is { } offset && offsets.Add(offset))
                {
                    roster = await client.ListSessionsAsync(offset: offset, cancellationToken: cancellationToken, search: search.Length == 0 ? null : search);
                    rows.AddRange(roster.Sessions);
                }
                if (!Current())
                {
                    return;
                }
                _gatewayRoster.Clear();
                _gatewayRoster.AddRange(rows.DistinctBy(row => (row.AgentId, row.Key)));
                _gatewayRosterSearchVersion = searchVersion;
                _gatewayRosterLoaded = true;
                _gatewayNextOffset = roster.NextOffset;
                GatewayHasMore = roster.HasMore == true && roster.NextOffset.HasValue;
                ProjectGatewayRoster();
            }

            if (!scope.HasFlag(GatewayRefreshScope.History) && !scope.HasFlag(GatewayRefreshScope.Models))
            {
                CompleteGatewayDataRefresh();
                return;
            }

            if (string.IsNullOrEmpty(pageKey))
            {
                _gatewayHistory = null;
                SetGatewayRunPresentation("WaitingForPage", false);
                NotifyGatewayCapabilities();
                return;
            }

            var selected = _gatewayRoster.FirstOrDefault(session => session.Key == pageKey);
            var agentId = TryGetGatewayAgentId(pageKey, out var canonicalAgentId) ? canonicalAgentId : selected?.AgentId;
            if (!scope.HasFlag(GatewayRefreshScope.History) && _gatewayHistory is { } confirmed && !_gatewayDataStale)
            {
                await RefreshGatewayModelsAsync(runtime, client, agentId, pageKey, confirmed.SessionId,
                    confirmed.SessionInfo ?? selected, Current, cancellationToken);
                NotifyGatewayCapabilities();
                return;
            }
            readingHistory = true;
            if (_gatewaySubscribedKey != pageKey)
            {
                await SubscribeGatewaySessionAsync(client, pageKey, agentId, cancellationToken);
                if (!Current())
                {
                    return;
                }
                _gatewaySubscribedKey = pageKey;
            }

            var eventVersion = _gatewayEventVersion;
            var history = await client.GetChatHistoryAsync(pageKey, agentId, limit: 1, cancellationToken: cancellationToken);
            if (!Current())
            {
                return;
            }
            if (eventVersion != _gatewayEventVersion || !OpenClawSessionSnapshot.CanReplace(_gatewayHistory, history))
            {
                RequestGatewayRefresh(GatewayRefreshScope.History);
                return;
            }
            if (!OpenClawSessionIdentity.Matches(pageKey, history))
            {
                _gatewayHistory = null;
                InvalidateGatewayModels(clear: true);
                GatewayModels.Clear();
                SelectedGatewayModel = null;
                SetGatewayIssue(GatewayStrings.Text("SelectionNotConfirmed"), GatewayRecoveryAction.Refresh);
                SetGatewayRunPresentation("UnknownRun", false);
                NotifyGatewayCapabilities();
                return;
            }
            if (_gatewayHistory?.SessionId is { } oldSessionId && oldSessionId != history.SessionId)
            {
                targetVersion = ++_gatewayTargetVersion;
                _gatewayEventRunId = null;
                _selectedGatewayModel = null;
                ResetGatewaySessionDetails();
                _gatewayObservedRuns.RemoveWhere(id => id.StartsWith(pageKey + "|", StringComparison.Ordinal));
                _gatewayNotifiedRuns.RemoveWhere(id => id.StartsWith(pageKey + "|", StringComparison.Ordinal));
            }
            _gatewayHistory = history;
            _gatewayDataStale = false;
            readingHistory = false;
            SynchronizeGatewaySessionDetails();
            if (history.SessionInfo?.HasActiveRun == false || history.SessionInfo?.ActiveRunIds is { Length: 0 })
            {
                _gatewayEventRunId = null;
                _gatewayObservedRuns.RemoveWhere(id => id.StartsWith(pageKey + "|", StringComparison.Ordinal));
            }
            var row = history.SessionInfo ?? selected;
            if (row is not null)
            {
                SetGatewaySelection(new OpenClawSessionItem(pageKey, SessionTitle(row), row.Model ?? pageKey, row));
            }

            if (history.InFlightRun is { ValueKind: JsonValueKind.Object } inFlight && ReadGatewayText(inFlight, "runId") is { } inFlightId)
            {
                _gatewayObservedRuns.Add(pageKey + "|" + inFlightId);
                _gatewayEventRunId = inFlightId;
            }

            ApplyGatewayRunProjection();
            var effective = ModelIdentifier(row?.Model, row?.ModelProvider);
            ModelSummaryText = ModelIdentifier(row?.ActiveModel, row?.ActiveModelProvider) is { Length: > 0 } active ? active :
                effective.Length > 0 ? effective : DefaultModelSummary;
            CompleteGatewayDataRefresh();
            await RefreshGatewayModelsAsync(runtime, client, agentId, pageKey, history.SessionId, row, Current, cancellationToken);
            NotifyGatewayCapabilities();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentGateway(runtime) && ReferenceEquals(client, runtime.Client) && client?.IsConnected == true &&
                (!targetsSession || (pageKey == _gatewayPage?.SessionKey && targetVersion == _gatewayTargetVersion)) && searchVersion == _gatewaySearchVersion)
            {
                if (readingHistory)
                {
                    _gatewayDataStale = true;
                    SetGatewayRunPresentation("UnknownRun", false);
                }
                ScheduleGatewayRefreshRetry(ex, scope);
                NotifyGatewayCapabilities();
                ShowGatewayError(ex);
                _gatewayRefreshErrorText = GatewayErrorText;
            }
        }
        finally
        {
            if (IsCurrentGateway(runtime) && ReferenceEquals(client, runtime.Client) && scope.HasFlag(GatewayRefreshScope.Roster))
            {
                _gatewayRosterLoading = false;
                NotifyGatewayRosterState();
            }
            _gatewayDataGate.Release();
        }
    }

    private void CompleteGatewayDataRefresh()
    {
        ResetGatewayRefreshRetry();
        if (GatewayErrorText == _gatewayRefreshErrorText)
        {
            GatewayErrorText = "";
        }
        _gatewayRefreshErrorText = null;
    }

    private void ResetGatewayRefreshRetry()
    {
        _gatewayRefreshFailures = 0;
        _gatewayRefreshNotBefore = default;
        _gatewayRefreshRetryable = false;
        _gatewayEventRecoveryRequested = false;
    }

    private void ScheduleGatewayRefreshRetry(Exception error, GatewayRefreshScope scope)
    {
        var retryable = error is OpenClawGatewayException gateway &&
            (gateway.Retryable || gateway.Code == "UNAVAILABLE" ||
             gateway.Kind is OpenClawFailureKind.Timeout or OpenClawFailureKind.Transport or OpenClawFailureKind.Disconnected);
        _gatewayRefreshRetryable = retryable;
        _gatewayEventRecoveryNotBefore = DateTimeOffset.UtcNow.AddSeconds(8);
        if (retryable && ++_gatewayRefreshFailures <= 3)
        {
            _gatewayRefreshNotBefore = DateTimeOffset.UtcNow.AddSeconds(1 << _gatewayRefreshFailures);
            RequestGatewayRefresh(scope);
        }
        else
        {
            // Recovery waits for a new target, connection, manual refresh, or server change.
            _gatewayRefreshNotBefore = DateTimeOffset.MaxValue;
        }
    }

    private void RequestGatewayEventRefresh(GatewayRefreshScope scope = GatewayRefreshScope.History)
    {
        if (_gatewayRefreshRetryable && _gatewayRefreshFailures > 3)
        {
            _gatewayEventRecoveryRequested = true;
        }
        RequestGatewayRefresh(scope);
    }

    private GatewayRefreshScope TakeGatewayRefreshRequest(bool pageChanged, DateTimeOffset now)
    {
        if (pageChanged)
        {
            ResetGatewayRefreshRetry();
            RequestGatewayRefresh(GatewayRefreshScope.History);
        }
        if (AutoRefreshEnabled && _gatewayEventRecoveryRequested && now >= _gatewayEventRecoveryNotBefore)
        {
            ResetGatewayRefreshRetry();
        }
        if ((_gatewayRefreshFailures != 0 && !AutoRefreshEnabled) || now < _gatewayRefreshNotBefore)
        {
            return GatewayRefreshScope.None;
        }
        var scope = (GatewayRefreshScope)Interlocked.Exchange(ref _gatewayRefreshRequested, 0);
        if ((now < _gatewaySearchNotBefore || _gatewaySearchCancellation is not null) && scope.HasFlag(GatewayRefreshScope.Roster))
        {
            RequestGatewayRefresh(GatewayRefreshScope.Roster);
            scope &= ~GatewayRefreshScope.Roster;
        }
        if (_gatewayModelCatalog is not null && now >= _gatewayModelsExpireAt)
        {
            NotifyGatewayCapabilities();
            if (AutoRefreshEnabled)
            {
                scope |= GatewayRefreshScope.Models;
            }
        }
        if (scope.HasFlag(GatewayRefreshScope.Models) && !CanRefreshGatewayModels(now))
        {
            RequestGatewayRefresh(GatewayRefreshScope.Models);
            scope &= ~GatewayRefreshScope.Models;
        }
        return scope;
    }

    private async Task SubscribeGatewaySessionAsync(OpenClawGatewayClient client, string key, string? agentId, CancellationToken cancellationToken)
    {
        if (client.Hello?.Features.Methods.Contains("sessions.messages.subscribe", StringComparer.Ordinal) != true)
        {
            return;
        }

        if (_gatewaySubscribedKey is { } previous)
        {
            try
            {
                await client.RequestAsync<JsonElement>("sessions.messages.unsubscribe", new { key = previous }, cancellationToken: cancellationToken);
            }
            catch (OpenClawGatewayException)
            {
            }
        }

        await client.RequestAsync<JsonElement>("sessions.messages.subscribe", new { key, agentId }, cancellationToken: cancellationToken);
    }

    private void ProjectGatewayRoster()
    {
        var retained = GatewaySessions.ToDictionary(item => (item.Session.AgentId, item.Key));
        var desired = new List<OpenClawSessionItem>();
        foreach (var rosterRow in _gatewayRoster)
        {
            var row = rosterRow;
            if (row.Key == _gatewayPage?.SessionKey && _gatewayHistory?.SessionInfo is { } current &&
                row.SessionId == _gatewayHistory.SessionId && (row.SnapshotAt ?? 0) <= (current.SnapshotAt ?? 0))
            {
                row = current;
            }
            var item = new OpenClawSessionItem(row.Key, SessionTitle(row), rosterRow.LastMessagePreview ?? row.Key, row);
            desired.Add(retained.TryGetValue((row.AgentId, row.Key), out var previous) && SameGatewaySessionItem(previous, item) ? previous : item);
        }
        var identities = desired.Select(item => (item.Session.AgentId, item.Key)).ToHashSet();
        for (var index = GatewaySessions.Count - 1; index >= 0; index--)
        {
            if (!identities.Contains((GatewaySessions[index].Session.AgentId, GatewaySessions[index].Key)))
            {
                GatewaySessions.RemoveAt(index);
            }
        }
        for (var index = 0; index < desired.Count; index++)
        {
            var item = desired[index];
            var existing = GatewaySessions.FirstOrDefault(candidate => candidate.Key == item.Key && candidate.Session.AgentId == item.Session.AgentId);
            if (existing is null)
            {
                GatewaySessions.Insert(index, item);
            }
            else
            {
                var previousIndex = GatewaySessions.IndexOf(existing);
                if (previousIndex != index)
                {
                    GatewaySessions.Move(previousIndex, index);
                }
                if (!ReferenceEquals(existing, item))
                {
                    GatewaySessions[index] = item;
                }
            }
        }

        var selection = GatewaySessions.FirstOrDefault(item => item.Key == _gatewayPage?.SessionKey);
        if (selection is not null)
        {
            SetGatewaySelection(selection);
        }
    }

    private static bool SameGatewaySessionItem(OpenClawSessionItem left, OpenClawSessionItem right) =>
        left.Key == right.Key && left.Title == right.Title && left.Subtitle == right.Subtitle &&
        (ReferenceEquals(left.Session, right.Session) ||
         JsonElement.DeepEquals(JsonSerializer.SerializeToElement(left.Session), JsonSerializer.SerializeToElement(right.Session)));

    private void SetGatewaySelection(OpenClawSessionItem value)
    {
        var listed = GatewaySessions.FirstOrDefault(item => item.Key == value.Key);
        if (listed is not null)
        {
            value = value with { Subtitle = listed.Subtitle };
            if (SameGatewaySessionItem(listed, value))
            {
                value = listed;
            }
            else
            {
                GatewaySessions[GatewaySessions.IndexOf(listed)] = value;
            }
        }
        if (ReferenceEquals(_selectedGatewaySession, value))
        {
            return;
        }
        _selectedGatewaySession = value;
        OnPropertyChanged(nameof(SelectedGatewaySession));
        OnPropertyChanged(nameof(GatewaySessionTitle));
        SynchronizeGatewaySessionDetails();
    }

    private void SetGatewayPageSelection(OpenClawPageSelection? page)
    {
        if (page?.SessionKey != _gatewayPage?.SessionKey)
        {
            _gatewayTargetVersion++;
            ResetGatewayRefreshRetry();
            InvalidateGatewayModels(clear: true);
            Interlocked.And(ref _gatewayRefreshRequested, (int)GatewayRefreshScope.Roster);
            _gatewayDataStale = false;
            _gatewayHistory = null;
            _gatewayEventRunId = null;
            _selectedGatewayModel = null;
            _selectedGatewaySession = null;
            GatewayModels.Clear();
            ResetGatewaySessionDetails();
            ModelSummaryText = DefaultModelSummary;
            SetGatewayRunPresentation("WaitingForPage", false);
            OnPropertyChanged(nameof(SelectedGatewaySession));
            OnPropertyChanged(nameof(SelectedGatewayModel));
            OnPropertyChanged(nameof(GatewaySessionTitle));
        }
        _gatewayPage = page;
        NotifyGatewayCapabilities();
    }

    private void ApplyGatewayRunProjection()
    {
        var busy = _gatewayHistory?.SessionInfo?.HasActiveRun;
        var exact = GetExactGatewayRunId();
        if (busy == true && _gatewayHistory?.SessionInfo?.ActiveRunIds is { Length: > 0 } active &&
            active.All(run => _gatewayNotifiedRuns.Contains(_gatewayPage?.SessionKey + "|" + run)))
        {
            // A cached active set cannot undo terminal events already delivered on this connection.
            NotifyGatewayCapabilities();
            return;
        }
        SetGatewayRunPresentation(exact is not null || busy == true ? "Running" : busy == false ? "Idle" : "UnknownRun", exact is not null || busy == true);
        NotifyGatewayCapabilities();
    }

    private void SetGatewayRunPresentation(string key, bool running)
    {
        GatewayRunText = GatewayStrings.Text(key);
        WorkStatusText = GatewayStrings.Text(key is "WaitingForPage" or "Stopping" or "Reconciling" or "UnknownRun" or "SelectSession" ? key + "Short" : key);
        WorkStatusBrush = running ? WarningBrush : NeutralBrush;
        SetRunIndicatorMode(running ? RunIndicatorMode.Live : RunIndicatorMode.Wait);
    }

    private string? GetExactGatewayRunId()
    {
        var key = _gatewayPage?.SessionKey;
        if (string.IsNullOrEmpty(key) || _gatewayHistory is null || _gatewayDataStale)
        {
            return null;
        }
        var candidate = OpenClawRunTarget.Resolve(_gatewayHistory, _gatewayPage?.RunId ?? _gatewayEventRunId);
        return candidate is not null && !_gatewayNotifiedRuns.Contains(key + "|" + candidate) ? candidate : null;
    }

    private void HandleGatewayEvent(OpenClawSessionRuntime runtime, OpenClawGatewayEvent value)
    {
        if (!IsCurrentGateway(runtime) || value.Generation != runtime.ConnectionGeneration)
        {
            return;
        }

        if (_gatewaySelectionChanging && value.Name is "sessions.changed" or "chat" or "agent")
        {
            _gatewayEventDuringSelection = true;
            RequestGatewayRefresh(GatewayRefreshScope.History);
        }

        if (value.Name is "sessions.changed" or "models.changed")
        {
            if (value.Payload.ValueKind == JsonValueKind.Object)
            {
                var changedKey = ReadGatewayText(value.Payload, "sessionKey") ?? ReadGatewayText(value.Payload, "key");
                var changedAgent = ReadGatewayText(value.Payload, "agentId");
                var currentAgent = TryGetGatewayAgentId(_gatewayPage?.SessionKey, out var agent) ? agent : null;
                var affectsCurrent = (changedKey is null || changedKey == _gatewayPage?.SessionKey) &&
                    (changedAgent is null || changedAgent == currentAgent);
                var scope = value.Name == "sessions.changed" ? GatewayRefreshScope.Roster : GatewayRefreshScope.None;
                if (affectsCurrent)
                {
                    InvalidateGatewayModels();
                    scope |= GatewayRefreshScope.History | GatewayRefreshScope.Models;
                }
                RequestGatewayEventRefresh(scope);
            }
            return;
        }
        if (value.Name is not ("chat" or "agent") || value.Payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var key = ReadGatewayText(value.Payload, "sessionKey");
        var run = ReadGatewayText(value.Payload, "runId");
        var sessionId = ReadGatewayText(value.Payload, "sessionId");
        if (key != _gatewayPage?.SessionKey || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(run))
        {
            return;
        }
        if (sessionId is not null && _gatewayHistory?.SessionId is { } currentSessionId && sessionId != currentSessionId)
        {
            RequestGatewayRefresh(GatewayRefreshScope.History);
            return;
        }
        var identity = key + "|" + run;
        var state = ReadGatewayText(value.Payload, "state");
        if (value.Name == "agent" && ReadGatewayText(value.Payload, "stream") == "lifecycle" &&
            value.Payload.TryGetProperty("data", out var lifecycle) &&
            ReadGatewayText(lifecycle, "phase") is "end" or "error")
        {
            // Agent lifecycle settlement is not a chat success receipt.
            _gatewayEventVersion++;
            RequestGatewayEventRefresh();
            return;
        }
        if (_gatewayNotifiedRuns.Contains(identity))
        {
            return;
        }
        if (state is "final" or "error" or "aborted")
        {
            _gatewayEventVersion++;
            var observed = _gatewayObservedRuns.Contains(identity);
            _gatewayObservedRuns.Remove(identity);
            if (_gatewayNotifiedRuns.Add(identity) && observed && state == "final")
            {
                var settings = _runtime.Configuration.Settings;
                if (settings.NotifyOnRunCompleted && (!settings.NotifyOnlyWhenUnfocused || !IsWindowFocused))
                {
                    NotificationRequested?.Invoke(new NativeNotificationRequest
                    {
                        Kind = NativeNotificationKind.RunCompleted,
                        Title = StringResources.NotificationRunCompletedTitle,
                        Body = GatewayStrings.Format("RunCompleted", GatewaySessionTitle),
                        EnvironmentName = SelectedEnvironmentName,
                    });
                }
            }
            _gatewayEventRunId = null;
            SetGatewayRunPresentation(state == "final" ? "Completed" : state == "aborted" ? "Stopped" : "Failed", false);
            RequestGatewayEventRefresh();
        }
        else
        {
            if (_gatewayObservedRuns.Add(identity))
            {
                _gatewayEventVersion++;
                RequestGatewayEventRefresh();
            }
            _gatewayEventRunId = run;
            SetGatewayRunPresentation(ReadGatewayText(value.Payload, "stream") == "tool" ? "ToolRunning" : "Running", true);
        }
        NotifyGatewayCapabilities();
    }

    private static string? ReadGatewayText(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static string SessionTitle(OpenClawSession row) =>
        new[] { row.Label, row.DisplayName, row.DerivedTitle, row.Key }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? row.Key;
    private static string ModelIdentifier(string? model, string? provider) => string.IsNullOrWhiteSpace(model) ? "" :
        model.Contains('/') || string.IsNullOrWhiteSpace(provider) ? model : provider + "/" + model;
}
