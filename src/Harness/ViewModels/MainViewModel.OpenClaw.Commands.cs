// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Helpers;
using Harness.OpenClaw;
using Harness.Services;

namespace Harness.ViewModels;

public partial class MainViewModel
{
    private async Task RefreshGatewayManuallyAsync()
    {
        if (GatewayBusy || _gatewaySelectionChanging)
        {
            return;
        }
        if (_gatewayRuntime?.Client?.IsConnected != true)
        {
            SynchronizeGatewayEnvironment(force: true);
            return;
        }

        GatewayErrorText = "";
        ResetGatewayRefreshRetry();
        InvalidateGatewayModels(manual: true);
        var runtime = _gatewayRuntime;
        var client = runtime.Client;
        var version = _gatewayTargetVersion;
        var token = _gatewayCancellation?.Token ?? _lifetimeCts.Token;
        var page = await _webViewService.ReadOpenClawSelectionAsync(new Uri(CurrentUrl), token, client?.Hello);
        if (IsCurrentGateway(runtime) && ReferenceEquals(client, runtime.Client) && client?.IsConnected == true && version == _gatewayTargetVersion)
        {
            SetGatewayPageSelection(page);
            await RefreshGatewayDataAsync(runtime, true, token);
        }
    }

    private async Task SelectGatewaySessionAsync(OpenClawSessionItem item)
    {
        var runtime = _gatewayRuntime;
        var client = runtime?.Client;
        if (runtime is null || client?.IsConnected != true || _gatewaySelectionChanging || GatewayBusy)
        {
            OnPropertyChanged(nameof(SelectedGatewaySession));
            return;
        }
        var token = _gatewayCancellation?.Token ?? _lifetimeCts.Token;
        _gatewayTargetVersion++;
        _gatewaySelectionChanging = true;
        _gatewayEventDuringSelection = false;
        ResetGatewayRefreshRetry();
        GatewayBusy = true;
        GatewayErrorText = "";
        try
        {
            var before = await _webViewService.ReadOpenClawSelectionAsync(new Uri(CurrentUrl), token, client.Hello);
            if (!IsCurrentGateway(runtime) || !ReferenceEquals(client, runtime.Client) || !client.IsConnected)
            {
                return;
            }
            if (before?.HasDraft == true)
            {
                SetGatewayIssue(GatewayStrings.Text("DraftBeforeSwitch"), GatewayRecoveryAction.ReturnToPage);
                return;
            }
            if (before?.SessionKey != item.Key)
            {
                if (before is null)
                {
                    SetGatewayIssue(GatewayStrings.Text("WaitingForPage"), GatewayRecoveryAction.ReturnToPage);
                    return;
                }
                var target = OpenClawSessionRoute.Build(new Uri(_selectedEnvironment!.GatewayUrl), item.Key, modern: !before.IsLegacy);
                _webViewService.Navigate(target.AbsoluteUri);

                var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
                OpenClawPageSelection? confirmed = null;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(300, token);
                    confirmed = await _webViewService.ReadOpenClawSelectionAsync(new Uri(CurrentUrl), token, client.Hello);
                    if (!IsCurrentGateway(runtime) || !ReferenceEquals(client, runtime.Client) || !client.IsConnected)
                    {
                        return;
                    }
                    if (confirmed?.SessionKey == item.Key)
                    {
                        break;
                    }
                }
                if (confirmed?.SessionKey != item.Key)
                {
                    SetGatewayPageSelection(null);
                    SetGatewayIssue(GatewayStrings.Text("SelectionNotConfirmed"), GatewayRecoveryAction.Refresh);
                    return;
                }
                SetGatewayPageSelection(confirmed);
            }
            else
            {
                SetGatewayPageSelection(before);
            }
            _gatewayHistory = null;
            _gatewayEventRunId = null;
            _selectedGatewayModel = null;
            ResetGatewaySessionDetails();
            SetGatewaySelection(item);
            await RefreshGatewayDataAsync(runtime, false, token);
        }
        finally
        {
            if (IsCurrentGateway(runtime))
            {
                _gatewaySelectionChanging = false;
                if (_gatewayEventDuringSelection)
                {
                    RequestGatewayRefresh(GatewayRefreshScope.History);
                    _gatewayEventDuringSelection = false;
                }
                GatewayBusy = false;
                OnPropertyChanged(nameof(SelectedGatewaySession));
                OnPropertyChanged(nameof(GatewaySessionTitle));
            }
        }
    }

    private async Task CreateGatewaySessionAsync()
    {
        var runtime = _gatewayRuntime;
        var client = runtime?.Client;
        if (runtime is null || client?.IsConnected != true || !_gatewayCanWrite || GatewayBusy || _gatewaySelectionChanging)
        {
            return;
        }
        var token = _gatewayCancellation?.Token ?? _lifetimeCts.Token;
        var page = await _webViewService.ReadOpenClawSelectionAsync(new Uri(CurrentUrl), token, client.Hello);
        if (!IsCurrentGateway(runtime))
        {
            return;
        }
        if (!ReferenceEquals(client, runtime.Client) || !client.IsConnected || _gatewaySelectionChanging || GatewayBusy)
        {
            SetGatewayIssue(GatewayStrings.Text("SelectionNotConfirmed"), GatewayRecoveryAction.Refresh);
            return;
        }
        if (page?.HasDraft == true || !TryGetGatewayAgentId(page?.SessionKey, out var agentId))
        {
            SetGatewayIssue(GatewayStrings.Text(page?.HasDraft == true ? "DraftBeforeSwitch" : "WaitingForPage"), GatewayRecoveryAction.ReturnToPage);
            return;
        }
        GatewayBusy = true;
        try
        {
            var created = await client.CreateSessionAsync(agentId, cancellationToken: token);
            if (!IsCurrentGateway(runtime) || !ReferenceEquals(client, runtime.Client) || !client.IsConnected)
            {
                return;
            }
            if (!created.Ok || string.IsNullOrEmpty(created.Key))
            {
                throw new OpenClawGatewayException(OpenClawFailureKind.Protocol);
            }
            var session = new OpenClawSession { Key = created.Key, SessionId = created.SessionId, AgentId = agentId };
            GatewayBusy = false;
            await SelectGatewaySessionAsync(new OpenClawSessionItem(created.Key, GatewayStrings.Text("NewSession"), created.Key, session));
            RequestGatewayRefresh(GatewayRefreshScope.Roster | GatewayRefreshScope.History);
        }
        finally
        {
            if (IsCurrentGateway(runtime))
            {
                GatewayBusy = false;
            }
        }
    }

    private async Task<bool> ConfirmGatewayTargetAsync(OpenClawSessionRuntime runtime, OpenClawGatewayClient client, string key, string? sessionId, CancellationToken token)
    {
        var page = await _webViewService.ReadOpenClawSelectionAsync(new Uri(CurrentUrl), token, client.Hello);
        if (!IsCurrentGateway(runtime) || !ReferenceEquals(client, runtime.Client) || !client.IsConnected)
        {
            return false;
        }
        if (page?.SessionKey != key || SelectedGatewaySession?.Key != key ||
            string.IsNullOrEmpty(sessionId) || _gatewayHistory?.SessionId != sessionId)
        {
            if (IsCurrentGateway(runtime))
            {
                SetGatewayPageSelection(page);
                SetGatewayIssue(GatewayStrings.Text("SelectionNotConfirmed"), GatewayRecoveryAction.Refresh);
                NotifyGatewayCapabilities();
            }
            return false;
        }
        SetGatewayPageSelection(page);
        return true;
    }

    private async Task ApplyGatewayModelAsync()
    {
        var runtime = _gatewayRuntime;
        var client = runtime?.Client;
        var selection = SelectedGatewaySession;
        var model = SelectedGatewayModel;
        var thinking = SelectedGatewayThinkingLevel;
        var sessionId = _gatewayHistory?.SessionId;
        if (runtime is null || client is null || selection is null || model is null || !CanModifyGatewaySession ||
            !TryGetGatewayAgentId(selection.Key, out var agentId))
        {
            return;
        }
        var token = _gatewayCancellation?.Token ?? _lifetimeCts.Token;
        var version = ++_gatewayTargetVersion;
        GatewayBusy = true;
        GatewayErrorText = "";
        try
        {
            if (!await ConfirmGatewayTargetAsync(runtime, client, selection.Key, sessionId, token) ||
                !ReferenceEquals(client, runtime.Client) || !client.IsConnected || version != _gatewayTargetVersion || !IsGatewayModelCatalogFresh)
            {
                return;
            }
            await client.PatchSessionAsync(selection.Key, agentId, sessionId,
                OpenClawModelSelection.BuildPatch(model.Id, model.Model, thinking?.Id), token);
            if (IsCurrentGateway(runtime) && ReferenceEquals(client, runtime.Client) && version == _gatewayTargetVersion &&
                _gatewayPage?.SessionKey == selection.Key && _gatewayHistory?.SessionId == sessionId)
            {
                _gatewayTargetVersion++;
                ResetGatewayModelDraft();
                InvalidateGatewayModels(manual: true);
                await RefreshGatewayDataAsync(runtime, true, token);
            }
        }
        finally
        {
            if (IsCurrentGateway(runtime))
            {
                GatewayBusy = false;
            }
        }
    }

    private async Task StopGatewayRunAsync()
    {
        var runtime = _gatewayRuntime;
        var client = runtime?.Client;
        var selection = SelectedGatewaySession;
        var runId = GetExactGatewayRunId();
        var sessionId = _gatewayHistory?.SessionId;
        if (runtime is null || client is null || selection is null || runId is null || sessionId is null || !CanStopGatewayRun ||
            !TryGetGatewayAgentId(selection.Key, out var agentId))
        {
            SetGatewayIssue(GatewayStrings.Text("NoExactRun"), GatewayRecoveryAction.Refresh);
            return;
        }
        var token = _gatewayCancellation?.Token ?? _lifetimeCts.Token;
        var version = _gatewayTargetVersion;
        GatewayBusy = true;
        GatewayErrorText = "";
        try
        {
            if (!await ConfirmGatewayTargetAsync(runtime, client, selection.Key, sessionId, token) ||
                !ReferenceEquals(client, runtime.Client) || !client.IsConnected || version != _gatewayTargetVersion || GetExactGatewayRunId() != runId)
            {
                return;
            }
            var result = await client.AbortChatAsync(selection.Key, runId, sessionId, agentId, token);
            if (IsCurrentGateway(runtime) && ReferenceEquals(client, runtime.Client) && version == _gatewayTargetVersion)
            {
                if (OpenClawSessionSnapshot.CanAcknowledgeStop(_gatewayHistory, _gatewayPage?.SessionKey,
                    selection.Key, sessionId, runId, GetExactGatewayRunId(), _gatewayNotifiedRuns.Contains(selection.Key + "|" + runId)))
                {
                    SetGatewayRunPresentation(result.Aborted == true || result.RunIds?.Contains(runId, StringComparer.Ordinal) == true ? "Stopping" : "Reconciling", true);
                }
                RequestGatewayRefresh(GatewayRefreshScope.History);
            }
        }
        finally
        {
            if (IsCurrentGateway(runtime))
            {
                GatewayBusy = false;
            }
        }
    }

    private async Task LoadMoreGatewaySessionsAsync()
    {
        var runtime = _gatewayRuntime;
        var client = runtime?.Client;
        if (runtime is null || client?.IsConnected != true || !GatewayHasMore || !_gatewayNextOffset.HasValue || GatewayBusy ||
            _gatewayRosterSearchVersion != _gatewaySearchVersion)
        {
            return;
        }
        var token = _gatewayCancellation?.Token ?? _lifetimeCts.Token;
        if (!await _gatewayDataGate.WaitAsync(0, token))
        {
            return;
        }
        GatewayBusy = true;
        var searchVersion = _gatewaySearchVersion;
        var search = GatewaySearchText.Trim();
        try
        {
            var roster = await client.ListSessionsAsync(offset: _gatewayNextOffset.Value, cancellationToken: token, search: search.Length == 0 ? null : search);
            if (!IsCurrentGateway(runtime) || !ReferenceEquals(client, runtime.Client) || !client.IsConnected || searchVersion != _gatewaySearchVersion)
            {
                return;
            }
            foreach (var row in roster.Sessions)
            {
                if (!_gatewayRoster.Any(existing => existing.Key == row.Key && existing.AgentId == row.AgentId))
                {
                    _gatewayRoster.Add(row);
                }
            }
            _gatewayNextOffset = roster.NextOffset;
            GatewayHasMore = roster.HasMore == true && roster.NextOffset.HasValue;
            ProjectGatewayRoster();
        }
        catch (Exception) when (!IsCurrentGateway(runtime) || !ReferenceEquals(client, runtime.Client) || searchVersion != _gatewaySearchVersion)
        {
            // A retired search cannot publish an error into its replacement.
        }
        finally
        {
            _gatewayDataGate.Release();
            if (IsCurrentGateway(runtime))
            {
                GatewayBusy = false;
            }
        }
    }
}
