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
                Interlocked.Exchange(ref _gatewayRefreshRequested, 1);
            }
        }
    }

    private async Task RefreshGatewayDataAsync(OpenClawSessionRuntime runtime, bool refreshRoster, CancellationToken cancellationToken)
    {
        if (!await _gatewayDataGate.WaitAsync(0, cancellationToken))
        {
            Interlocked.Exchange(ref _gatewayRefreshRequested, 1);
            return;
        }

        var client = runtime.Client;
        var pageKey = _gatewayPage?.SessionKey;
        var targetVersion = _gatewayTargetVersion;
        var searchVersion = _gatewaySearchVersion;
        var search = GatewaySearchText.Trim();
        var historyConfirmed = false;
        try
        {
            if (client?.IsConnected != true)
            {
                return;
            }

            bool Current() => IsCurrentGateway(runtime) && ReferenceEquals(client, runtime.Client) &&
                client.IsConnected && pageKey == _gatewayPage?.SessionKey && targetVersion == _gatewayTargetVersion &&
                searchVersion == _gatewaySearchVersion && !cancellationToken.IsCancellationRequested;

            if (refreshRoster)
            {
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
                _gatewayNextOffset = roster.NextOffset;
                GatewayHasMore = roster.HasMore == true && roster.NextOffset.HasValue;
                ProjectGatewayRoster();
            }

            if (string.IsNullOrEmpty(pageKey))
            {
                _gatewayHistory = null;
                SetGatewayRunPresentation(GatewayStrings.Text("WaitingForPage"), false);
                NotifyGatewayCapabilities();
                return;
            }

            var selected = _gatewayRoster.FirstOrDefault(session => session.Key == pageKey);
            var agentId = TryGetGatewayAgentId(pageKey, out var canonicalAgentId) ? canonicalAgentId : selected?.AgentId;
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
                Interlocked.Exchange(ref _gatewayRefreshRequested, 1);
                return;
            }
            if (!OpenClawSessionIdentity.Matches(pageKey, history))
            {
                _gatewayHistory = null;
                GatewayModels.Clear();
                SelectedGatewayModel = null;
                GatewayErrorText = GatewayStrings.Text("SelectionNotConfirmed");
                SetGatewayRunPresentation(GatewayStrings.Text("UnknownRun"), false);
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
            SynchronizeGatewaySessionDetails();
            historyConfirmed = true;
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
            var models = await client.ListModelsAsync(agentId, pageKey, cancellationToken);
            if (!Current())
            {
                return;
            }
            var priorSelection = _gatewayModelEdited ? _selectedGatewayModel?.Id : null;
            var priorThinking = _gatewayThinkingEdited ? _selectedGatewayThinkingLevel?.Id : null;
            _gatewayProjectingModel = true;
            try
            {
                GatewayModels.Clear();
                GatewayModels.Add(new OpenClawModelItem("", GatewayStrings.Text("DefaultModel")));
                foreach (var model in models.Models.Where(model => model.ManualSelectionAllowed == true && model.Available != false))
                {
                    var id = ModelIdentifier(model.Id, model.Provider);
                    if (!string.IsNullOrEmpty(id) && !GatewayModels.Any(item => item.Id == id))
                    {
                        GatewayModels.Add(new OpenClawModelItem(id, string.IsNullOrWhiteSpace(model.Name) ? id : $"{model.Name} · {model.Provider}", model));
                    }
                }

                SelectedGatewayModel = GatewayModels.FirstOrDefault(model => model.Id == priorSelection) ??
                    GatewayModels.FirstOrDefault(model => model.Id == effective) ?? GatewayModels.FirstOrDefault();
                var retainsModelDraft = priorSelection is not null && priorSelection == SelectedGatewayModel?.Id;
                _gatewayModelEdited = retainsModelDraft;
                var thinking = priorThinking ?? (retainsModelDraft && priorSelection != effective ? "" : row?.ThinkingLevel ?? "");
                ProjectGatewayThinkingOptions(thinking);
                _gatewayThinkingEdited = priorThinking is not null && priorThinking == SelectedGatewayThinkingLevel?.Id;
            }
            finally
            {
                _gatewayProjectingModel = false;
            }
            ResetGatewayRefreshRetry();
            _gatewayDataStale = false;
            if (GatewayErrorText == _gatewayRefreshErrorText)
            {
                GatewayErrorText = "";
            }
            _gatewayRefreshErrorText = null;
            NotifyGatewayCapabilities();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentGateway(runtime) && ReferenceEquals(client, runtime.Client) && client?.IsConnected == true &&
                pageKey == _gatewayPage?.SessionKey && targetVersion == _gatewayTargetVersion && searchVersion == _gatewaySearchVersion)
            {
                _gatewayDataStale = true;
                if (!historyConfirmed)
                {
                    SetGatewayRunPresentation(GatewayStrings.Text("UnknownRun"), false);
                }
                ScheduleGatewayRefreshRetry(ex);
                NotifyGatewayCapabilities();
                ShowGatewayError(ex);
                _gatewayRefreshErrorText = GatewayErrorText;
            }
        }
        finally
        {
            _gatewayDataGate.Release();
        }
    }

    private void ResetGatewayRefreshRetry()
    {
        _gatewayRefreshFailures = 0;
        _gatewayRefreshNotBefore = default;
        _gatewayRefreshRetryable = false;
        _gatewayEventRecoveryRequested = false;
    }

    private void ScheduleGatewayRefreshRetry(Exception error)
    {
        var retryable = error is OpenClawGatewayException gateway &&
            (gateway.Retryable || gateway.Code == "UNAVAILABLE" ||
             gateway.Kind is OpenClawFailureKind.Timeout or OpenClawFailureKind.Transport or OpenClawFailureKind.Disconnected);
        _gatewayRefreshRetryable = retryable;
        _gatewayEventRecoveryNotBefore = DateTimeOffset.UtcNow.AddSeconds(8);
        if (retryable && ++_gatewayRefreshFailures <= 3)
        {
            _gatewayRefreshNotBefore = DateTimeOffset.UtcNow.AddSeconds(1 << _gatewayRefreshFailures);
            Interlocked.Exchange(ref _gatewayRefreshRequested, 1);
        }
        else
        {
            // Recovery waits for a new target, connection, manual refresh, or server change.
            _gatewayRefreshNotBefore = DateTimeOffset.MaxValue;
            Interlocked.Exchange(ref _gatewayRefreshRequested, 0);
        }
    }

    private void RequestGatewayEventRefresh()
    {
        if (_gatewayRefreshRetryable && _gatewayRefreshFailures > 3)
        {
            _gatewayEventRecoveryRequested = true;
        }
        Interlocked.Exchange(ref _gatewayRefreshRequested, 1);
    }

    private bool TakeGatewayRefreshRequest(bool pageChanged, DateTimeOffset now)
    {
        if (pageChanged)
        {
            ResetGatewayRefreshRetry();
            Interlocked.Exchange(ref _gatewayRefreshRequested, 0);
            return true;
        }
        if (AutoRefreshEnabled && _gatewayEventRecoveryRequested && now >= _gatewayEventRecoveryNotBefore)
        {
            ResetGatewayRefreshRetry();
        }
        return (_gatewayRefreshFailures == 0 || AutoRefreshEnabled) && now >= _gatewayRefreshNotBefore && now >= _gatewaySearchNotBefore &&
            Interlocked.Exchange(ref _gatewayRefreshRequested, 0) != 0;
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
        GatewaySessions.Clear();
        foreach (var row in _gatewayRoster)
        {
            GatewaySessions.Add(new OpenClawSessionItem(row.Key, SessionTitle(row), row.LastMessagePreview ?? row.Key, row));
        }

        var selection = GatewaySessions.FirstOrDefault(item => item.Key == _gatewayPage?.SessionKey);
        if (selection is not null)
        {
            SetGatewaySelection(selection);
        }
    }

    private void SetGatewaySelection(OpenClawSessionItem value)
    {
        var listed = GatewaySessions.FirstOrDefault(item => item.Key == value.Key);
        if (listed is not null)
        {
            value = value with { Subtitle = listed.Subtitle };
            _selectedGatewaySession = value;
            // ListView.SelectedItem must be the same item that the collection exposes.
            GatewaySessions[GatewaySessions.IndexOf(listed)] = value;
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
            _gatewayDataStale = false;
            _gatewayHistory = null;
            _gatewayEventRunId = null;
            _selectedGatewayModel = null;
            _selectedGatewaySession = null;
            GatewayModels.Clear();
            ResetGatewaySessionDetails();
            ModelSummaryText = DefaultModelSummary;
            SetGatewayRunPresentation(GatewayStrings.Text("WaitingForPage"), false);
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
        SetGatewayRunPresentation(GatewayStrings.Text(exact is not null || busy == true ? "Running" : busy == false ? "Idle" : "UnknownRun"), exact is not null || busy == true);
        NotifyGatewayCapabilities();
    }

    private void SetGatewayRunPresentation(string text, bool running)
    {
        GatewayRunText = text;
        WorkStatusText = text;
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
            Interlocked.Exchange(ref _gatewayRefreshRequested, 1);
        }

        if (value.Name == "sessions.changed")
        {
            if (value.Payload.ValueKind == JsonValueKind.Object)
            {
                RequestGatewayEventRefresh();
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
            Interlocked.Exchange(ref _gatewayRefreshRequested, 1);
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
            SetGatewayRunPresentation(GatewayStrings.Text(state == "final" ? "Completed" : state == "aborted" ? "Stopped" : "Failed"), false);
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
            SetGatewayRunPresentation(GatewayStrings.Text(ReadGatewayText(value.Payload, "stream") == "tool" ? "ToolRunning" : "Running"), true);
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
