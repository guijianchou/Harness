// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Harness.Helpers;
using Harness.Services;
using Harness.Updates;
using Microsoft.UI.Xaml;

namespace Harness.ViewModels;

public enum UpdatePhase
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Verifying,
    Ready,
    Restarting,
    Cancelled,
    RateLimited,
    Failed,
}

public enum UpdatePrimaryAction
{
    Check,
    Download,
    Restart,
    OpenReleasePage,
}

public sealed class UpdateViewModel : ObservableObject
{
    // Keep the release cache and server cooldown across settings windows.
    private static readonly GitHubReleaseClient ReleaseClient = new(GitHubReleaseClient.CreateHttpClient());
    private readonly IAppLogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private Task _operationTask = Task.CompletedTask;
    private Task _handoffTask = Task.CompletedTask;
    private Task _cooldownTask = Task.CompletedTask;
    private DateTimeOffset? _retryAfter;
    private bool _closed;
    private bool _handedOff;
    private bool _busy;
    private bool _indeterminate;
    private bool _failureDuringCheck;
    private double _progress;
    private string _status = string.Empty;
    private UpdatePhase _phase;
    private PortableRelease? _release;
    private string? _job;

    public UpdateViewModel(IAppLogger logger)
    {
        _logger = logger;
        CheckCommand = new SimpleCommand(() => { if (CanCheck) { StartOperation(CheckAsync); } });
        DownloadCommand = new SimpleCommand(() => { if (CanDownload) { StartOperation(DownloadAsync); } });
        CancelCommand = new SimpleCommand(CancelOperation);
        ReadLastResult();
    }

    public ICommand CheckCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand CancelCommand { get; }
    public bool IsPortable { get; } = PortableUpdateService.IsSupported;
    public string CurrentVersionText => string.Format(UpdateLocalization.InstalledVersion, AppMetadata.GetDisplayVersion());
    public UpdatePhase Phase
    {
        get => _phase;
        private set
        {
            if (SetProperty(ref _phase, value))
            {
                IsIndeterminate = value is UpdatePhase.Checking or UpdatePhase.Verifying or UpdatePhase.Restarting;
                NotifyActions();
            }
        }
    }
    public UpdatePrimaryAction PrimaryAction => _job is not null ? UpdatePrimaryAction.Restart :
        _release is not null ? IsPortable ? UpdatePrimaryAction.Download : UpdatePrimaryAction.OpenReleasePage : UpdatePrimaryAction.Check;
    public string PrimaryActionText => Phase switch
    {
        UpdatePhase.Checking => UpdateLocalization.Checking,
        UpdatePhase.Downloading => UpdateLocalization.Downloading,
        UpdatePhase.Verifying => UpdateLocalization.VerifyingTitle,
        UpdatePhase.Restarting => UpdateLocalization.Restarting,
        UpdatePhase.Failed or UpdatePhase.RateLimited => UpdateLocalization.Retry,
        _ => PrimaryAction switch
        {
            UpdatePrimaryAction.Download => UpdateLocalization.Download,
            UpdatePrimaryAction.Restart => UpdateLocalization.RestartAction,
            UpdatePrimaryAction.OpenReleasePage => UpdateLocalization.OpenReleasePage,
            _ => UpdateLocalization.Check,
        },
    };
    public bool CanExecutePrimaryAction => !IsBusy && !_closed && !_handedOff && (PrimaryAction switch
    {
        UpdatePrimaryAction.Download => CanDownload,
        UpdatePrimaryAction.Restart => CanRestart,
        UpdatePrimaryAction.OpenReleasePage => true,
        _ => CanCheck,
    });
    public string StatusTitle => Phase switch
    {
        UpdatePhase.Checking => UpdateLocalization.Checking,
        UpdatePhase.UpToDate => UpdateLocalization.UpToDateTitle,
        UpdatePhase.Available => UpdateLocalization.AvailableTitle,
        UpdatePhase.Downloading => UpdateLocalization.Downloading,
        UpdatePhase.Verifying => UpdateLocalization.VerifyingTitle,
        UpdatePhase.Ready => UpdateLocalization.ReadyTitle,
        UpdatePhase.Restarting => UpdateLocalization.Restarting,
        UpdatePhase.Cancelled => UpdateLocalization.CancelledTitle,
        UpdatePhase.RateLimited => UpdateLocalization.RateLimitedTitle,
        UpdatePhase.Failed => _failureDuringCheck ? UpdateLocalization.CheckFailedTitle : UpdateLocalization.FailedTitle,
        _ => UpdateLocalization.IdleTitle,
    };
    public string StatusDescription => Phase switch
    {
        UpdatePhase.Idle => IsPortable ? UpdateLocalization.IdleDescription : UpdateLocalization.ManualCheckDescription,
        UpdatePhase.Checking => UpdateLocalization.CheckingDescription,
        UpdatePhase.Available when !IsPortable => Status + " " + UpdateLocalization.ManualAvailableDescription,
        UpdatePhase.Downloading when Status == UpdateLocalization.Downloading => UpdateLocalization.DownloadHint,
        UpdatePhase.Verifying => UpdateLocalization.VerifyingDescription,
        UpdatePhase.Restarting => UpdateLocalization.RestartingDescription,
        UpdatePhase.RateLimited when !IsRateLimitActive => UpdateLocalization.RetryAvailable,
        _ => Status,
    };
    public bool IsActivityVisible => IsBusy && Phase is UpdatePhase.Checking or UpdatePhase.Verifying or UpdatePhase.Restarting;
    public Visibility ActivityVisibility => IsActivityVisible ? Visibility.Visible : Visibility.Collapsed;
    public bool IsDownloadProgressVisible => IsBusy && Phase == UpdatePhase.Downloading;
    public Visibility DownloadProgressVisibility => IsDownloadProgressVisible ? Visibility.Visible : Visibility.Collapsed;
    public bool CanCancel => IsBusy && _operation is { IsCancellationRequested: false } &&
        Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Verifying;
    public Visibility CancelVisibility => CanCancel ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RestartHintVisibility => _job is not null ? Visibility.Visible : Visibility.Collapsed;
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); NotifyActions(); } }
    private bool IsRateLimitActive => _retryAfter is { } retryAfter && retryAfter > DateTimeOffset.UtcNow;
    public bool CanCheck => !IsBusy && _job is null && !_handedOff && !IsRateLimitActive;
    public bool CanDownload => !IsBusy && _release is not null && _job is null && IsPortable && !IsRateLimitActive;
    public bool CanRestart => !IsBusy && _job is not null && !_handedOff;
    public bool IsIndeterminate { get => _indeterminate; private set => SetProperty(ref _indeterminate, value); }
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusVisibility));
                OnPropertyChanged(nameof(DescriptionVisibility));
                OnPropertyChanged(nameof(StatusDescription));
            }
        }
    }
    public Visibility StatusVisibility => string.IsNullOrEmpty(Status) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DescriptionVisibility => IsPortable && string.IsNullOrEmpty(Status) ? Visibility.Collapsed : Visibility.Visible;
    public Uri ReleasePage => _release?.PageUrl ?? new Uri("https://github.com/guijianchou/Harness/releases/latest");
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DownloadVisibility => _release is not null && _job is null && IsPortable ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RestartVisibility => _job is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ManualVisibility => IsPortable ? Visibility.Collapsed : Visibility.Visible;

    private void NotifyActions()
    {
        foreach (var name in new[] { nameof(CanCheck), nameof(CanDownload), nameof(CanRestart), nameof(BusyVisibility), nameof(DownloadVisibility),
                     nameof(RestartVisibility), nameof(ReleasePage), nameof(PrimaryAction), nameof(PrimaryActionText), nameof(CanExecutePrimaryAction),
                     nameof(StatusTitle), nameof(StatusDescription), nameof(IsActivityVisible), nameof(ActivityVisibility), nameof(IsDownloadProgressVisible),
                     nameof(DownloadProgressVisibility), nameof(CanCancel), nameof(CancelVisibility), nameof(RestartHintVisibility) })
        {
            OnPropertyChanged(name);
        }
    }

    private void CancelOperation()
    {
        if (!CanCancel) { return; }
        _operation?.Cancel();
        NotifyActions();
    }

    private void StartOperation(Func<CancellationToken, Task> action)
    {
        if (IsBusy || _closed || _handedOff) { return; }
        _operationTask = ObserveAsync(action);
    }

    private async Task ObserveAsync(Func<CancellationToken, Task> action)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = cancellation;
        IsBusy = true;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                _failureDuringCheck = Phase == UpdatePhase.Checking;
                Status = cancellation.IsCancellationRequested ? UpdateLocalization.Cancelled : UpdateLocalization.NetworkError;
                Phase = cancellation.IsCancellationRequested ? UpdatePhase.Cancelled : UpdatePhase.Failed;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Portable update preparation failed: {ex.GetType().Name}");
            if (!_closed)
            {
                _failureDuringCheck = Phase == UpdatePhase.Checking;
                Status = UpdateLocalization.Error(ex);
                if (ex is PortableUpdateException { Code: "RateLimited" } limited)
                {
                    _retryAfter = limited.RetryAfter ?? DateTimeOffset.UtcNow.AddMinutes(1);
                    Phase = UpdatePhase.RateLimited;
                    _cooldownTask = RefreshAfterCooldownAsync(_retryAfter.Value);
                }
                else { Phase = UpdatePhase.Failed; }
            }
        }
        finally
        {
            _operation = null;
            if (!_closed) { IsBusy = false; }
        }
    }

    private async Task CheckAsync(CancellationToken token)
    {
        if (_job is not null || IsRateLimitActive) { return; }
        Status = UpdateLocalization.Checking;
        _release = null;
        Phase = UpdatePhase.Checking;
        var release = await ReleaseClient.CheckAsync(AppMetadata.GetDisplayVersion(), token);
        token.ThrowIfCancellationRequested();
        _release = release;
        Status = release is null ? UpdateLocalization.UpToDate : string.Format(UpdateLocalization.Available, release.Version, release.Size / 1048576d);
        Phase = release is null ? UpdatePhase.UpToDate : UpdatePhase.Available;
    }

    private async Task DownloadAsync(CancellationToken token)
    {
        if (_release is null || _job is not null || !IsPortable || IsRateLimitActive) { return; }
        Progress = 0;
        Status = UpdateLocalization.Downloading;
        Phase = UpdatePhase.Downloading;
        var owner = _operation;
        var progress = new Progress<UpdateProgress>(value =>
        {
            if (_closed || token.IsCancellationRequested || !IsBusy || !ReferenceEquals(owner, _operation) ||
                Phase is not (UpdatePhase.Downloading or UpdatePhase.Verifying)) { return; }
            if (value.Phase == "Verifying")
            {
                Status = UpdateLocalization.Verifying;
                Phase = UpdatePhase.Verifying;
            }
            else
            {
                Progress = value.Total > 0 ? 100d * value.Received / value.Total : 0;
                Status = value.Total > 0 ?
                    string.Format(UpdateLocalization.DownloadProgress, value.Received / 1048576d, value.Total / 1048576d) :
                    string.Format(UpdateLocalization.DownloadReceived, value.Received / 1048576d);
                Phase = UpdatePhase.Downloading;
                IsIndeterminate = value.Total <= 0;
            }
        });
        var prepared = await PortableUpdatePreparation.PrepareAsync(ReleaseClient, _release, AppContext.BaseDirectory,
            AppMetadata.GetDisplayVersion(), progress, token);
        if (token.IsCancellationRequested)
        {
            await Task.Run(() => PortableUpdatePreparation.DeletePreparedJob(prepared));
            token.ThrowIfCancellationRequested();
        }
        _job = prepared;
        if (!_closed)
        {
            Status = string.Format(UpdateLocalization.Ready, _release.Version);
            Phase = UpdatePhase.Ready;
        }
    }

    private async Task RefreshAfterCooldownAsync(DateTimeOffset retryAfter)
    {
        try
        {
            while (retryAfter > DateTimeOffset.UtcNow)
            {
                var remaining = retryAfter - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) { break; }
                await Task.Delay(remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining, _lifetime.Token);
            }
            if (!_closed && _retryAfter == retryAfter) { NotifyActions(); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    internal Task HandOffAsync() => _handoffTask = HandOffCoreAsync();

    private async Task HandOffCoreAsync()
    {
        if (!CanRestart || _job is null) { throw new PortableUpdateException("NotReady"); }
        IsBusy = true;
        Status = UpdateLocalization.Restarting;
        Phase = UpdatePhase.Restarting;
        try
        {
            await PortableUpdateService.StartHelperAsync(_job, _lifetime.Token);
            _handedOff = true;
        }
        catch (Exception ex)
        {
            _failureDuringCheck = false;
            Status = UpdateLocalization.Error(ex);
            Phase = UpdatePhase.Failed;
            throw;
        }
        finally { IsBusy = false; }
    }

    private void ReadLastResult()
    {
        try
        {
            var file = PortableUpdatePaths.ResultFile(AppContext.BaseDirectory);
            if (!File.Exists(file) || new FileInfo(file).Length > 16384) { return; }
            var result = JsonSerializer.Deserialize<PortableUpdateResult>(File.ReadAllText(file), PortablePackage.JsonOptions);
            if (result?.State is "RolledBack" or "Failed" or "RecoveryRequired")
            {
                Status = result.State == "RecoveryRequired" ? string.Format(UpdateLocalization.RecoveryRequired, result.BackupDirectory) : UpdateLocalization.Restored;
                Phase = UpdatePhase.Failed;
            }
        }
        catch (Exception ex) { _logger.Warning($"Update result could not be read: {ex.GetType().Name}"); }
    }

    internal async Task CloseAsync()
    {
        _closed = true;
        _lifetime.Cancel();
        try
        {
            await _operationTask;
            await _cooldownTask;
            try { await _handoffTask; }
            catch (Exception) { /* The handoff caller already reports the failure. */ }
            if (!_handedOff && _job is { } job) { await Task.Run(() => PortableUpdatePreparation.DeletePreparedJob(job)); }
        }
        catch (Exception ex) { _logger.Warning($"Update staging cleanup failed: {ex.GetType().Name}"); }
        finally { _lifetime.Dispose(); }
    }
}
