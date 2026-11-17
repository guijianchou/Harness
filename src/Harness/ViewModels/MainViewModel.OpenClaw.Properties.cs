// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Collections.ObjectModel;
using System.Windows.Input;
using Harness.Helpers;
using Harness.OpenClaw;
using Harness.Services;
using Microsoft.UI.Xaml;

namespace Harness.ViewModels;

public sealed record OpenClawSessionItem(string Key, string Title, string Subtitle, OpenClawSession Session);
public sealed record OpenClawModelItem(string Id, string DisplayName, OpenClawModel? Model = null);
public sealed record OpenClawThinkingItem(string Id, string DisplayName);

public partial class MainViewModel
{
    private OpenClawSessionRuntime? _gatewayRuntime;
    private CancellationTokenSource? _gatewayCancellation;
    private Task? _gatewayMonitorTask;
    private readonly List<Task> _gatewayRetirements = [];
    private string? _gatewayEnvironmentFingerprint;
    private readonly List<OpenClawSession> _gatewayRoster = [];
    private readonly HashSet<string> _gatewayNotifiedRuns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _gatewayObservedRuns = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gatewayDataGate = new(1, 1);
    private OpenClawSessionItem? _selectedGatewaySession;
    private OpenClawModelItem? _selectedGatewayModel;
    private OpenClawPageSelection? _gatewayPage;
    private OpenClawChatHistory? _gatewayHistory;
    private string _gatewayStatusText = GatewayStrings.Text("Disconnected");
    private string _gatewayRunText = GatewayStrings.Text("SelectSession");
    private string _gatewayErrorText = "";
    private string _gatewayVersionText = "";
    private string _gatewaySearchText = "";
    private bool _isOpenClawPanelOpen;
    private bool _gatewayBusy;
    private bool _gatewaySelectionChanging;
    private bool _gatewayEventDuringSelection;
    private bool _gatewayDataStale;
    private int _gatewayRefreshFailures;
    private DateTimeOffset _gatewayRefreshNotBefore;
    private string? _gatewayRefreshErrorText;
    private bool _gatewayRefreshRetryable;
    private bool _gatewayEventRecoveryRequested;
    private DateTimeOffset _gatewayEventRecoveryNotBefore;
    private bool _gatewayCanWrite;
    private int _gatewayRefreshRequested;
    private int? _gatewayNextOffset;
    private bool _gatewayHasMore;
    private long _gatewayTargetVersion;
    private long _gatewayEventVersion;
    private long _gatewaySearchVersion;
    private long _gatewayRosterSearchVersion;
    private DateTimeOffset _gatewaySearchNotBefore;

    public ObservableCollection<OpenClawSessionItem> GatewaySessions { get; } = [];
    public ObservableCollection<OpenClawModelItem> GatewayModels { get; } = [];
    public ICommand ToggleGatewayPanelCommand { get; private set; } = null!;
    public ICommand RefreshGatewayCommand { get; private set; } = null!;
    public ICommand NewGatewaySessionCommand { get; private set; } = null!;
    public ICommand ApplyGatewayModelCommand { get; private set; } = null!;
    public ICommand StopGatewayRunCommand { get; private set; } = null!;
    public ICommand LoadMoreGatewaySessionsCommand { get; private set; } = null!;

    public bool IsOpenClawAvailable => _selectedEnvironment is { IsPlaceholder: false, Backend: Models.HostedBackendKind.OpenClaw, OpenClawConnection.Enabled: true };
    public Visibility GatewayEntryVisibility => IsOpenClawAvailable ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GatewayPanelVisibility => IsOpenClawAvailable && IsOpenClawPanelOpen ? Visibility.Visible : Visibility.Collapsed;
    public bool IsOpenClawPanelOpen
    {
        get => _isOpenClawPanelOpen;
        set
        {
            if (SetProperty(ref _isOpenClawPanelOpen, value))
            {
                OnPropertyChanged(nameof(GatewayPanelVisibility));
            }
        }
    }

    public string GatewayStatusText { get => _gatewayStatusText; private set => SetProperty(ref _gatewayStatusText, value); }
    public string GatewayRunText { get => _gatewayRunText; private set => SetProperty(ref _gatewayRunText, value); }
    public string GatewayVersionText { get => _gatewayVersionText; private set => SetProperty(ref _gatewayVersionText, value); }
    public string GatewaySessionTitle => SelectedGatewaySession?.Title ?? GatewayStrings.Text("SelectSession");
    public string GatewayErrorText
    {
        get => _gatewayErrorText;
        private set
        {
            if (SetProperty(ref _gatewayErrorText, value))
            {
                OnPropertyChanged(nameof(GatewayHasError));
            }
        }
    }
    public bool GatewayHasError => !string.IsNullOrEmpty(GatewayErrorText);
    public bool GatewayBusy
    {
        get => _gatewayBusy;
        private set
        {
            SetProperty(ref _gatewayBusy, value);
            NotifyGatewayCapabilities();
        }
    }
    public bool GatewayHasMore { get => _gatewayHasMore; private set => SetProperty(ref _gatewayHasMore, value); }
    public bool CanCreateGatewaySession => !GatewayBusy && !_gatewaySelectionChanging && _gatewayCanWrite &&
        _gatewayRuntime?.Client?.IsConnected == true && TryGetGatewayAgentId(_gatewayPage?.SessionKey, out _);
    public bool CanModifyGatewaySession => !GatewayBusy && !_gatewaySelectionChanging && !_gatewayDataStale && _gatewayCanWrite &&
        _gatewayRuntime?.Client?.IsConnected == true && _gatewayPage?.SessionKey == SelectedGatewaySession?.Key &&
        TryGetGatewayAgentId(SelectedGatewaySession?.Key, out _) && !string.IsNullOrEmpty(_gatewayHistory?.SessionId) &&
        OpenClawSessionIdentity.Matches(_gatewayPage?.SessionKey ?? "", _gatewayHistory);
    public bool CanStopGatewayRun => CanModifyGatewaySession && GetExactGatewayRunId() is not null;
    public string GatewaySearchText
    {
        get => _gatewaySearchText;
        set
        {
            var priorQuery = _gatewaySearchText.Trim();
            if (SetProperty(ref _gatewaySearchText, value))
            {
                if (priorQuery != value.Trim())
                {
                    _gatewaySearchVersion++;
                    _gatewaySearchNotBefore = DateTimeOffset.UtcNow.AddMilliseconds(300);
                    _gatewayNextOffset = null;
                    GatewayHasMore = false;
                    ResetGatewayRefreshRetry();
                    Interlocked.Exchange(ref _gatewayRefreshRequested, 1);
                }
            }
        }
    }
    public OpenClawSessionItem? SelectedGatewaySession
    {
        get => _selectedGatewaySession;
        set
        {
            if (value is null || value.Key == _selectedGatewaySession?.Key || _gatewaySelectionChanging)
            {
                return;
            }

            ObserveGatewayUiTask(SelectGatewaySessionAsync(value));
        }
    }
    public OpenClawModelItem? SelectedGatewayModel
    {
        get => _selectedGatewayModel;
        set
        {
            var changedId = value?.Id != _selectedGatewayModel?.Id;
            if (SetProperty(ref _selectedGatewayModel, value))
            {
                if (changedId && !_gatewayProjectingModel)
                {
                    _gatewayModelEdited = true;
                }
                var priorThinking = _selectedGatewayThinkingLevel?.Id;
                ProjectGatewayThinkingOptions();
                if (!_gatewayProjectingModel && priorThinking != _selectedGatewayThinkingLevel?.Id)
                {
                    _gatewayThinkingEdited = true;
                }
            }
        }
    }

    private static bool TryGetGatewayAgentId(string? key, out string agentId) =>
        OpenClawSessionIdentity.TryGetAgentId(key, out agentId);

    private void InitializeGatewayCommands()
    {
        ToggleGatewayPanelCommand = new SimpleCommand(() => IsOpenClawPanelOpen = !IsOpenClawPanelOpen);
        RefreshGatewayCommand = new AsyncCommand(() => ExecuteGatewayCommandAsync(RefreshGatewayManuallyAsync));
        NewGatewaySessionCommand = new AsyncCommand(() => ExecuteGatewayCommandAsync(CreateGatewaySessionAsync));
        ApplyGatewayModelCommand = new AsyncCommand(() => ExecuteGatewayCommandAsync(ApplyGatewayModelAsync));
        ApplyGatewayLabelCommand = new AsyncCommand(() => ExecuteGatewayCommandAsync(ApplyGatewayLabelAsync));
        StopGatewayRunCommand = new AsyncCommand(() => ExecuteGatewayCommandAsync(StopGatewayRunAsync));
        LoadMoreGatewaySessionsCommand = new AsyncCommand(() => ExecuteGatewayCommandAsync(LoadMoreGatewaySessionsAsync));
    }

    private async Task ExecuteGatewayCommandAsync(Func<Task> action)
    {
        var runtime = _gatewayRuntime;
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(runtime, _gatewayRuntime))
            {
                ShowGatewayError(ex);
            }
        }
    }

    private void NotifyGatewayCapabilities()
    {
        OnPropertyChanged(nameof(CanCreateGatewaySession));
        OnPropertyChanged(nameof(CanModifyGatewaySession));
        OnPropertyChanged(nameof(CanStopGatewayRun));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanApplyGatewayLabel));
        OnPropertyChanged(nameof(CanSelectGatewayThinkingLevel));
        OnPropertyChanged(nameof(GatewayRunIdText));
    }

    private async void ObserveGatewayUiTask(Task task)
    {
        var runtime = _gatewayRuntime;
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(runtime, _gatewayRuntime))
            {
                ShowGatewayError(ex);
            }
        }
    }

    private void ShowGatewayError(Exception ex)
    {
        if (_isDisposed)
        {
            return;
        }

        GatewayErrorText = ex switch
        {
            OpenClawGatewayException { DetailCode: "CREDENTIAL_ENDPOINT_CHANGED" } => GatewayStrings.Text("CredentialEndpointChanged"),
            OpenClawGatewayException { PairingRequestId: not null } gateway => GatewayStrings.Format("Pairing", gateway.PairingRequestId),
            OpenClawGatewayException { Kind: OpenClawFailureKind.UnsupportedProtocol } => GatewayStrings.Text("UnsupportedProtocol"),
            OpenClawGatewayException { Kind: OpenClawFailureKind.Timeout } => GatewayStrings.Text("Timeout"),
            OpenClawGatewayException { Kind: OpenClawFailureKind.Rejected } gateway => GatewayStrings.Format("Rejected", gateway.DetailCode ?? gateway.Code ?? "REJECTED"),
            OpenClawGatewayException { Kind: OpenClawFailureKind.Transport } => GatewayStrings.Text("TransportError"),
            OpenClawGatewayException { Kind: OpenClawFailureKind.EdgeAuthenticationRequired } => GatewayStrings.Text("AccessRequired"),
            _ => GatewayStrings.Text("OperationFailed"),
        };
        _runtime.Logger.Warning("gateway.operation.failed", new { kind = ex.GetType().Name });
    }
}
