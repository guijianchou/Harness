// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Helpers;
using Harness.OpenClaw;
using Harness.Services;

namespace Harness.ViewModels;

public partial class MainViewModel
{
    private async Task RefreshGatewayManuallyAsync()
    {
        if (_gatewayRuntime?.Client?.IsConnected != true)
        {
            SynchronizeGatewayEnvironment(force: true);
            return;
        }

        GatewayErrorText = "";
        var runtime = _gatewayRuntime;
        var client = runtime.Client;
        var token = _gatewayCancellation?.Token ?? _lifetimeCts.Token;
        var page = await _webViewService.ReadOpenClawSelectionAsync(new Uri(CurrentUrl), token, client?.Hello);
        if (IsCurrentGateway(runtime) && ReferenceEquals(client, runtime.Client) && client?.IsConnected == true)
        {
            _gatewayPage = page;
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
        _gatewaySelectionChanging = true;
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
                GatewayErrorText = GatewayStrings.Text("DraftBeforeSwitch");
                return;
            }
            if (before?.SessionKey != item.Key)
            {
                if (before is null)
                {
                    GatewayErrorText = GatewayStrings.Text("WaitingForPage");
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
                    _gatewayPage = null;
                    _gatewayHistory = null;
                    GatewayErrorText = GatewayStrings.Text("SelectionNotConfirmed");
                    return;
                }
                _gatewayPage = confirmed;
            }
            else
            {
                _gatewayPage = before;
            }
            _gatewayHistory = null;
            _gatewayEventRunId = null;
            _selectedGatewayModel = null;
            SetGatewaySelection(item);
            await RefreshGatewayDataAsync(runtime, false, token);
        }
        finally
        {
            if (IsCurrentGateway(runtime))
            {
                _gatewaySelectionChanging = false;
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
            GatewayErrorText = GatewayStrings.Text("SelectionNotConfirmed");
            return;
        }
        if (page?.HasDraft == true || !TryGetGatewayAgentId(page?.SessionKey, out var agentId))
        {
            GatewayErrorText = GatewayStrings.Text(page?.HasDraft == true ? "DraftBeforeSwitch" : "WaitingForPage");
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
            Interlocked.Exchange(ref _gatewayRefreshRequested, 1);
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
                _gatewayPage = page;
                GatewayErrorText = GatewayStrings.Text("SelectionNotConfirmed");
                NotifyGatewayCapabilities();
            }
            return false;
        }
        _gatewayPage = page;
        return true;
    }

    private async Task ApplyGatewayModelAsync()
    {
        var runtime = _gatewayRuntime;
        var client = runtime?.Client;
        var selection = SelectedGatewaySession;
        var model = SelectedGatewayModel;
        var sessionId = _gatewayHistory?.SessionId;
        if (runtime is null || client is null || selection is null || model is null || !CanModifyGatewaySession ||
            !TryGetGatewayAgentId(selection.Key, out var agentId))
        {
            return;
        }
        var token = _gatewayCancellation?.Token ?? _lifetimeCts.Token;
        GatewayBusy = true;
        GatewayErrorText = "";
        try
        {
            if (!await ConfirmGatewayTargetAsync(runtime, client, selection.Key, sessionId, token) ||
                !ReferenceEquals(client, runtime.Client) || !client.IsConnected)
            {
                return;
            }
            await client.PatchSessionAsync(selection.Key, agentId, sessionId,
                new Dictionary<string, object?> { ["model"] = model.Id.Length == 0 ? null : model.Id }, token);
            if (IsCurrentGateway(runtime) && ReferenceEquals(client, runtime.Client))
            {
                _selectedGatewayModel = null;
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
            GatewayErrorText = GatewayStrings.Text("NoExactRun");
            return;
        }
        var token = _gatewayCancellation?.Token ?? _lifetimeCts.Token;
        GatewayBusy = true;
        GatewayErrorText = "";
        try
        {
            if (!await ConfirmGatewayTargetAsync(runtime, client, selection.Key, sessionId, token) ||
                !ReferenceEquals(client, runtime.Client) || !client.IsConnected || GetExactGatewayRunId() != runId)
            {
                return;
            }
            var result = await client.AbortChatAsync(selection.Key, runId, sessionId, agentId, token);
            if (IsCurrentGateway(runtime) && ReferenceEquals(client, runtime.Client))
            {
                SetGatewayRunPresentation(GatewayStrings.Text(result.Aborted == true || result.RunIds?.Contains(runId, StringComparer.Ordinal) == true ? "Stopping" : "Reconciling"), true);
                Interlocked.Exchange(ref _gatewayRefreshRequested, 1);
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
        if (runtime is null || client?.IsConnected != true || !GatewayHasMore || !_gatewayNextOffset.HasValue || GatewayBusy)
        {
            return;
        }
        var token = _gatewayCancellation?.Token ?? _lifetimeCts.Token;
        if (!await _gatewayDataGate.WaitAsync(0, token))
        {
            return;
        }
        GatewayBusy = true;
        try
        {
            var roster = await client.ListSessionsAsync(offset: _gatewayNextOffset.Value, cancellationToken: token);
            if (!IsCurrentGateway(runtime) || !ReferenceEquals(client, runtime.Client) || !client.IsConnected)
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
