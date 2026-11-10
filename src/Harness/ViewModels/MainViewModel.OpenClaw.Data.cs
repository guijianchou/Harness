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
        var historyConfirmed = false;
        try
        {
            if (client?.IsConnected != true)
            {
                return;
            }

            bool Current() => IsCurrentGateway(runtime) && ReferenceEquals(client, runtime.Client) &&
                client.IsConnected && pageKey == _gatewayPage?.SessionKey && !cancellationToken.IsCancellationRequested;

            if (refreshRoster)
            {
                var expectedCount = Math.Clamp(_gatewayRoster.Count, 60, 600);
                var roster = await client.ListSessionsAsync(cancellationToken: cancellationToken);
                var rows = roster.Sessions.ToList();
                var offsets = new HashSet<int>();
                while (rows.Count < expectedCount && roster.HasMore == true && roster.NextOffset is { } offset && offsets.Add(offset))
                {
                    roster = await client.ListSessionsAsync(offset: offset, cancellationToken: cancellationToken);
                    rows.AddRange(roster.Sessions);
                }
                if (!Current())
                {
                    return;
                }
                _gatewayRoster.Clear();
                _gatewayRoster.AddRange(rows.DistinctBy(row => (row.AgentId, row.Key)));
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
            if (_gatewaySubscribedKey != pageKey)
            {
                await SubscribeGatewaySessionAsync(client, pageKey, selected?.AgentId, cancellationToken);
                if (!Current())
                {
                    return;
                }
                _gatewaySubscribedKey = pageKey;
            }

            var history = await client.GetChatHistoryAsync(pageKey, selected?.AgentId, limit: 1, cancellationToken: cancellationToken);
            if (!Current())
            {
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
                _gatewayEventRunId = null;
                _gatewayObservedRuns.RemoveWhere(id => id.StartsWith(pageKey + "|", StringComparison.Ordinal));
                _gatewayNotifiedRuns.RemoveWhere(id => id.StartsWith(pageKey + "|", StringComparison.Ordinal));
            }
            _gatewayHistory = history;
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
            var models = await client.ListModelsAsync(selected?.AgentId, pageKey, cancellationToken);
            if (!Current())
            {
                return;
            }
            var priorSelection = _selectedGatewayModel?.Id;
            GatewayModels.Clear();
            GatewayModels.Add(new OpenClawModelItem("", GatewayStrings.Text("DefaultModel")));
            foreach (var model in models.Models.Where(model => model.ManualSelectionAllowed != false && model.Available != false))
            {
                var id = ModelIdentifier(model.Id, model.Provider);
                if (!string.IsNullOrEmpty(id) && !GatewayModels.Any(item => item.Id == id))
                {
                    GatewayModels.Add(new OpenClawModelItem(id, string.IsNullOrWhiteSpace(model.Name) ? id : $"{model.Name} · {model.Provider}"));
                }
            }

            SelectedGatewayModel = GatewayModels.FirstOrDefault(model => model.Id == priorSelection) ??
                GatewayModels.FirstOrDefault(model => model.Id == effective) ?? GatewayModels.FirstOrDefault();
            NotifyGatewayCapabilities();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentGateway(runtime) && ReferenceEquals(client, runtime.Client) && pageKey == _gatewayPage?.SessionKey)
            {
                if (!historyConfirmed)
                {
                    _gatewayHistory = null;
                    SetGatewayRunPresentation(GatewayStrings.Text("UnknownRun"), false);
                }
                GatewayModels.Clear();
                SelectedGatewayModel = null;
                NotifyGatewayCapabilities();
                ShowGatewayError(ex);
            }
        }
        finally
        {
            _gatewayDataGate.Release();
        }
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
        var search = GatewaySearchText.Trim();
        GatewaySessions.Clear();
        foreach (var row in _gatewayRoster.Where(row => search.Length == 0 ||
                     SessionTitle(row).Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                     row.Key.Contains(search, StringComparison.OrdinalIgnoreCase)))
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
    }

    private void ApplyGatewayRunProjection()
    {
        var busy = _gatewayHistory?.SessionInfo?.HasActiveRun;
        var exact = GetExactGatewayRunId();
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
        if (string.IsNullOrEmpty(key) || _gatewayHistory is null)
        {
            return null;
        }
        var candidate = OpenClawRunTarget.Resolve(_gatewayHistory, _gatewayPage?.RunId ?? _gatewayEventRunId);
        return candidate is not null && !_gatewayNotifiedRuns.Contains(key + "|" + candidate) ? candidate : null;
    }

    private void HandleGatewayEvent(OpenClawSessionRuntime runtime, OpenClawGatewayEvent value)
    {
        if (!IsCurrentGateway(runtime) || value.Generation != runtime.ConnectionGeneration || _gatewaySelectionChanging)
        {
            return;
        }

        if (value.Name == "sessions.changed")
        {
            Interlocked.Exchange(ref _gatewayRefreshRequested, 1);
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
            Interlocked.Exchange(ref _gatewayRefreshRequested, 1);
            return;
        }
        if (_gatewayNotifiedRuns.Contains(identity))
        {
            return;
        }
        if (state is "final" or "error" or "aborted")
        {
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
            Interlocked.Exchange(ref _gatewayRefreshRequested, 1);
        }
        else
        {
            if (_gatewayObservedRuns.Add(identity))
            {
                Interlocked.Exchange(ref _gatewayRefreshRequested, 1);
            }
            _gatewayEventRunId = run;
            SetGatewayRunPresentation(GatewayStrings.Text(ReadGatewayText(value.Payload, "stream") == "tool" ? "ToolRunning" : "Running"), true);
        }
        NotifyGatewayCapabilities();
    }

    private static string? ReadGatewayText(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static string SessionTitle(OpenClawSession row) => row.DisplayName ?? row.DerivedTitle ?? row.Label ?? row.Key;
    private static string ModelIdentifier(string? model, string? provider) => string.IsNullOrWhiteSpace(model) ? "" :
        model.Contains('/') || string.IsNullOrWhiteSpace(provider) ? model : provider + "/" + model;
}
