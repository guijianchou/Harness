// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Harness.Helpers;
using Harness.Services;
using Harness.Updates;
using Microsoft.UI.Xaml;

namespace Harness.ViewModels;

public sealed class UpdateViewModel : ObservableObject
{
    private readonly HttpClient _http = GitHubReleaseClient.CreateHttpClient();
    private readonly IAppLogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private Task _operationTask = Task.CompletedTask;
    private Task _handoffTask = Task.CompletedTask;
    private bool _closed;
    private bool _handedOff;
    private bool _busy;
    private bool _indeterminate;
    private double _progress;
    private string _status = string.Empty;
    private PortableRelease? _release;
    private string? _job;

    public UpdateViewModel(IAppLogger logger)
    {
        _logger = logger;
        CheckCommand = new SimpleCommand(() => StartOperation(CheckAsync));
        DownloadCommand = new SimpleCommand(() => StartOperation(DownloadAsync));
        CancelCommand = new SimpleCommand(() => _operation?.Cancel());
        ReadLastResult();
    }

    public ICommand CheckCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand CancelCommand { get; }
    public bool IsPortable { get; } = PortableUpdateService.IsSupported;
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); NotifyActions(); } }
    public bool CanCheck => !IsBusy && _job is null && !_handedOff;
    public bool CanDownload => !IsBusy && _release is not null && _job is null && IsPortable;
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
        foreach (var name in new[] { nameof(CanCheck), nameof(CanDownload), nameof(CanRestart), nameof(BusyVisibility), nameof(DownloadVisibility), nameof(RestartVisibility), nameof(ReleasePage) })
        {
            OnPropertyChanged(name);
        }
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
        catch (OperationCanceledException) { if (!_closed) { Status = cancellation.IsCancellationRequested ? UpdateLocalization.Cancelled : UpdateLocalization.NetworkError; } }
        catch (Exception ex)
        {
            _logger.Warning($"Portable update preparation failed: {ex.GetType().Name}");
            if (!_closed) { Status = UpdateLocalization.Error(ex); }
        }
        finally
        {
            _operation = null;
            if (!_closed) { IsBusy = false; }
        }
    }

    private async Task CheckAsync(CancellationToken token)
    {
        if (_job is not null) { return; }
        IsIndeterminate = true;
        Status = UpdateLocalization.Checking;
        _release = null;
        var release = await new GitHubReleaseClient(_http).CheckAsync(AppMetadata.GetDisplayVersion(), token);
        token.ThrowIfCancellationRequested();
        _release = release;
        Status = release is null ? UpdateLocalization.UpToDate : string.Format(UpdateLocalization.Available, release.Version, release.Size / 1048576d);
    }

    private async Task DownloadAsync(CancellationToken token)
    {
        if (_release is null || _job is not null || !IsPortable) { return; }
        IsIndeterminate = false;
        Progress = 0;
        Status = UpdateLocalization.Downloading;
        var owner = _operation;
        var progress = new Progress<UpdateProgress>(value =>
        {
            if (_closed || token.IsCancellationRequested || !IsBusy || !ReferenceEquals(owner, _operation)) { return; }
            IsIndeterminate = value.Phase == "Verifying";
            if (IsIndeterminate) { Status = UpdateLocalization.Verifying; }
            else
            {
                Progress = value.Total > 0 ? 100d * value.Received / value.Total : 0;
                Status = string.Format(UpdateLocalization.DownloadProgress, value.Received / 1048576d, value.Total / 1048576d);
            }
        });
        var prepared = await PortableUpdatePreparation.PrepareAsync(new GitHubReleaseClient(_http), _release, AppContext.BaseDirectory,
            AppMetadata.GetDisplayVersion(), progress, token);
        if (token.IsCancellationRequested)
        {
            await Task.Run(() => PortableUpdatePreparation.DeletePreparedJob(prepared));
            token.ThrowIfCancellationRequested();
        }
        _job = prepared;
        if (!_closed) { Status = string.Format(UpdateLocalization.Ready, _release.Version); }
    }

    internal Task HandOffAsync() => _handoffTask = HandOffCoreAsync();

    private async Task HandOffCoreAsync()
    {
        if (!CanRestart || _job is null) { throw new PortableUpdateException("NotReady"); }
        IsBusy = true;
        IsIndeterminate = true;
        Status = UpdateLocalization.Restarting;
        try
        {
            await PortableUpdateService.StartHelperAsync(_job, _lifetime.Token);
            _handedOff = true;
        }
        catch (Exception ex)
        {
            Status = UpdateLocalization.Error(ex);
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
            try { await _handoffTask; }
            catch (Exception) { /* The handoff caller already reports the failure. */ }
            if (!_handedOff && _job is { } job) { await Task.Run(() => PortableUpdatePreparation.DeletePreparedJob(job)); }
        }
        catch (Exception ex) { _logger.Warning($"Update staging cleanup failed: {ex.GetType().Name}"); }
        finally { _http.Dispose(); _lifetime.Dispose(); }
    }
}
