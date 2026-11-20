// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using Harness.Helpers;
using Harness.OpenClaw;

namespace Harness.ViewModels;

public partial class MainViewModel
{
    private string _gatewaySessionLabel = "";
    private string? _gatewayDetailsIdentity;
    private bool _gatewayLabelEdited;
    private OpenClawThinkingItem? _selectedGatewayThinkingLevel;
    private bool _gatewayModelEdited;
    private bool _gatewayThinkingEdited;
    private bool _gatewayProjectingModel;

    public ObservableCollection<OpenClawThinkingItem> GatewayThinkingLevels { get; } = [];
    public ICommand ApplyGatewayLabelCommand { get; private set; } = null!;
    public bool CanApplyGatewayLabel => CanEditGatewayLabel && _gatewayLabelEdited;
    public bool CanSelectGatewayThinkingLevel => CanModifyGatewaySession && GatewayThinkingLevels.Count > 1;
    public string GatewayRunIdText => GetExactGatewayRunId() ?? "";
    public string GatewaySessionLabel
    {
        get => _gatewaySessionLabel;
        set
        {
            if (SetProperty(ref _gatewaySessionLabel, value))
            {
                _gatewayLabelEdited = true;
                OnPropertyChanged(nameof(CanApplyGatewayLabel));
            }
        }
    }
    public OpenClawThinkingItem? SelectedGatewayThinkingLevel
    {
        get => _selectedGatewayThinkingLevel;
        set
        {
            var changedId = value?.Id != _selectedGatewayThinkingLevel?.Id;
            if (SetProperty(ref _selectedGatewayThinkingLevel, value) && changedId && !_gatewayProjectingModel)
            {
                _gatewayThinkingEdited = true;
            }
        }
    }
    public string GatewaySessionMetadataText
    {
        get
        {
            var row = _gatewayHistory?.SessionInfo ?? SelectedGatewaySession?.Session;
            var parts = new List<string>();
            if (TryGetGatewayAgentId(_gatewayPage?.SessionKey ?? row?.Key, out var agent))
            {
                parts.Add(agent);
            }
            if (row?.UpdatedAt is >= 0 and <= 253402300799999)
            {
                parts.Add(GatewayStrings.Format("SessionUpdated", DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAt.Value).ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
            }
            if (row?.TotalTokens is { } total && double.IsFinite(total) && total >= 0 && row.TotalTokensFresh != false)
            {
                parts.Add(GatewayStrings.Format("SessionTokens", total.ToString("N0", CultureInfo.CurrentCulture)));
            }
            return string.Join(" · ", parts);
        }
    }

    private void ProjectGatewayThinkingOptions(string? preferred = null)
    {
        var previous = preferred ?? _selectedGatewayThinkingLevel?.Id;
        var projecting = _gatewayProjectingModel;
        _gatewayProjectingModel = true;
        try
        {
            GatewayThinkingLevels.Clear();
            GatewayThinkingLevels.Add(new OpenClawThinkingItem("", GatewayStrings.Text("ThinkingDefault")));
            foreach (var option in OpenClawModelSelection.ThinkingOptions(_selectedGatewayModel?.Model))
            {
                GatewayThinkingLevels.Add(new OpenClawThinkingItem(option.Id, string.IsNullOrWhiteSpace(option.Label) ? option.Id : option.Label));
            }
            SelectedGatewayThinkingLevel = GatewayThinkingLevels.FirstOrDefault(option => option.Id == previous) ?? GatewayThinkingLevels[0];
        }
        finally
        {
            _gatewayProjectingModel = projecting;
        }
        OnPropertyChanged(nameof(CanSelectGatewayThinkingLevel));
    }

    private void SynchronizeGatewaySessionDetails()
    {
        var identity = _gatewayHistory is { } history ? history.SessionKey + "|" + history.SessionId : null;
        if (identity != _gatewayDetailsIdentity || !_gatewayLabelEdited)
        {
            _gatewayDetailsIdentity = identity;
            _gatewaySessionLabel = _gatewayHistory?.SessionInfo?.Label ?? SelectedGatewaySession?.Session.Label ?? "";
            _gatewayLabelEdited = false;
            OnPropertyChanged(nameof(GatewaySessionLabel));
        }
        OnPropertyChanged(nameof(GatewaySessionMetadataText));
        OnPropertyChanged(nameof(CanApplyGatewayLabel));
    }

    private void ResetGatewaySessionDetails()
    {
        _gatewayDetailsIdentity = null;
        _gatewaySessionLabel = "";
        _gatewayLabelEdited = false;
        ResetGatewayModelDraft();
        GatewayThinkingLevels.Clear();
        OnPropertyChanged(nameof(GatewaySessionLabel));
        OnPropertyChanged(nameof(GatewaySessionMetadataText));
        OnPropertyChanged(nameof(GatewayRunIdText));
    }

    private void ResetGatewayModelDraft()
    {
        _gatewayModelEdited = false;
        _gatewayThinkingEdited = false;
        _selectedGatewayModel = null;
        _selectedGatewayThinkingLevel = null;
        OnPropertyChanged(nameof(SelectedGatewayModel));
        OnPropertyChanged(nameof(SelectedGatewayThinkingLevel));
    }

    private async Task ApplyGatewayLabelAsync()
    {
        var runtime = _gatewayRuntime;
        var client = runtime?.Client;
        var selection = SelectedGatewaySession;
        var sessionId = _gatewayHistory?.SessionId;
        if (runtime is null || client is null || selection is null || sessionId is null || !CanApplyGatewayLabel ||
            !TryGetGatewayAgentId(selection.Key, out var agentId))
        {
            return;
        }
        var label = GatewaySessionLabel.Trim();
        if (label.EnumerateRunes().Count() > 512)
        {
            SetGatewayIssue(GatewayStrings.Text("SessionLabelTooLong"), GatewayRecoveryAction.None);
            return;
        }
        var token = _gatewayCancellation?.Token ?? _lifetimeCts.Token;
        var version = ++_gatewayTargetVersion;
        GatewayBusy = true;
        GatewayErrorText = "";
        try
        {
            if (!await ConfirmGatewayTargetAsync(runtime, client, selection.Key, sessionId, token) || version != _gatewayTargetVersion)
            {
                return;
            }
            await client.PatchSessionAsync(selection.Key, agentId, sessionId,
                new Dictionary<string, object?> { ["label"] = label.Length == 0 ? null : label }, token);
            if (IsCurrentGateway(runtime) && ReferenceEquals(client, runtime.Client) && version == _gatewayTargetVersion &&
                _gatewayPage?.SessionKey == selection.Key && _gatewayHistory?.SessionId == sessionId)
            {
                _gatewayTargetVersion++;
                _gatewayLabelEdited = GatewaySessionLabel.Trim() != label;
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
}
