// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using Harness.Helpers;
using Harness.Services;
using Harness.Updates;
using Harness.ViewModels;
using Microsoft.UI.Xaml;

/// <summary>Runs the authored update ViewModel against deterministic external-operation boundaries.</summary>
public static class UpdatePresentationRegression
{
    private static readonly List<string> Passed = [];
    private static readonly List<object> Snapshots = [];
    private static readonly PortableRelease Release = new("9.9.9", new("https://fixture.invalid/releases/9.9.9"),
        new("https://fixture.invalid/package.zip"), new("https://fixture.invalid/package.sha256"), 100 * 1048576);

    public static void Run(string root, string output)
    {
        StringResources.Load(Path.Combine(root, "src/Harness/Strings/en-us/Resources.resw"));
        ReviewUpdateBackend.OutputDirectory = output;
        using var context = new RegressionContext();
        SynchronizationContext.SetSynchronizationContext(context);
        var task = RunAsync(output);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!task.IsCompleted)
        {
            if (DateTimeOffset.UtcNow >= deadline) { throw new TimeoutException("Update presentation regression exceeded 30 seconds."); }
            context.DispatchOne();
        }
        task.GetAwaiter().GetResult();
    }

    private static async Task RunAsync(string output)
    {
        Exception? failure = null;
        try
        {
            await FullLifecycleAsync();
            await CancellationAndLateProgressAsync();
            await ErrorAndRetryAsync();
            await RateLimitPresentationAsync();
            await PreviousUpdateFailureAsync();
            await ReadyCleanupAsync();
            await CancelledPreparationCleanupAsync();
            await ManualInstallationAsync();
            Console.WriteLine($"PASS: {Passed.Count} authored update presentation checks with controlled external operations.");
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
            {
                checks = Passed.Count,
                passed = Passed,
                snapshots = Snapshots,
                failure = failure?.ToString(),
                boundaries = "Actual UpdateViewModel, localization, and commands; controlled release/preparation/helper boundaries. No network, updater process, or user configuration access.",
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static async Task FullLifecycleAsync()
    {
        ReviewUpdateBackend.Reset();
        var view = new UpdateViewModel(NullAppLogger.Instance);
        var changed = new HashSet<string>();
        view.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);
        try
        {
            AssertState(view, UpdatePhase.Idle, UpdatePrimaryAction.Check, busy: false, activity: false, download: false, cancel: false);
            Check(view.CanExecutePrimaryAction && !string.IsNullOrWhiteSpace(view.CurrentVersionText), "Idle shows the installed version and an enabled check action");
            view.CheckCommand.Execute(null);
            AssertState(view, UpdatePhase.Checking, UpdatePrimaryAction.Check, busy: true, activity: true, download: false, cancel: true);
            view.CheckCommand.Execute(null);
            Check(ReviewUpdateBackend.CheckCalls == 1, "A repeated check cannot overlap the current operation");
            ReviewUpdateBackend.CheckCompletion!.SetResult(Release);
            await UntilAsync(() => !view.IsBusy);
            AssertState(view, UpdatePhase.Available, UpdatePrimaryAction.Download, busy: false, activity: false, download: false, cancel: false);
            Check(view.CanExecutePrimaryAction && view.ReleasePage == Release.PageUrl, "Available release enables download and links the checked release");

            view.DownloadCommand.Execute(null);
            AssertState(view, UpdatePhase.Downloading, UpdatePrimaryAction.Download, busy: true, activity: false, download: true, cancel: true);
            var progress = ReviewUpdateBackend.Progress!;
            progress.Report(new("Downloading", 25 * 1048576, Release.Size));
            await UntilAsync(() => view.Progress == 25);
            Check(!view.IsIndeterminate, "A known download length shows determinate progress");
            progress.Report(new("Downloading", 2 * 1048576, 0));
            await UntilAsync(() => view.IsIndeterminate);
            Check(view.IsDownloadProgressVisible && !view.IsActivityVisible, "An unknown download length retains the download bar without a second activity indicator");
            progress.Report(new("Downloading", 50 * 1048576, Release.Size));
            await UntilAsync(() => view.Progress == 50 && !view.IsIndeterminate);
            progress.Report(new("Verifying"));
            await UntilAsync(() => view.Phase == UpdatePhase.Verifying);
            AssertState(view, UpdatePhase.Verifying, UpdatePrimaryAction.Download, busy: true, activity: true, download: false, cancel: true);
            ReviewUpdateBackend.PrepareCompletion!.SetResult("prepared-job-full");
            await UntilAsync(() => !view.IsBusy);
            AssertState(view, UpdatePhase.Ready, UpdatePrimaryAction.Restart, busy: false, activity: false, download: false, cancel: false);
            Check(view.CanRestart && view.CanExecutePrimaryAction && view.RestartHintVisibility == Visibility.Visible,
                "A verified package enables restart and explains the restart requirement");
            var readyProgress = view.Progress;
            var readyDescription = view.StatusDescription;
            progress.Report(new("Downloading", 80 * 1048576, Release.Size));
            await DrainAsync();
            Check(view.Phase == UpdatePhase.Ready && view.Progress == readyProgress && view.StatusDescription == readyDescription,
                "Queued download progress cannot overwrite the ready state");

            var handoff = view.HandOffAsync();
            AssertState(view, UpdatePhase.Restarting, UpdatePrimaryAction.Restart, busy: true, activity: true, download: false, cancel: false);
            view.CancelCommand.Execute(null);
            Check(!ReviewUpdateBackend.HelperToken.IsCancellationRequested, "The download Cancel command cannot cancel a helper handoff");
            ReviewUpdateBackend.HelperCompletion!.SetResult(true);
            await handoff;
            Check(!view.CanExecutePrimaryAction && !view.CanRestart && !view.CanCancel, "Successful handoff prevents another update action");
            Check(new[] { nameof(view.Phase), nameof(view.PrimaryAction), nameof(view.PrimaryActionText), nameof(view.StatusTitle),
                nameof(view.StatusDescription), nameof(view.ActivityVisibility), nameof(view.DownloadProgressVisibility), nameof(view.CancelVisibility) }.All(changed.Contains),
                "Lifecycle transitions notify every bound status, action, and progress region");
        }
        finally { await view.CloseAsync(); }
        Check(ReviewUpdateBackend.DeletedJobs.Count == 0, "Closing after successful handoff preserves the helper-owned prepared job");
    }

    private static async Task CancellationAndLateProgressAsync()
    {
        ReviewUpdateBackend.Reset();
        var view = new UpdateViewModel(NullAppLogger.Instance);
        try
        {
            view.CheckCommand.Execute(null);
            view.CancelCommand.Execute(null);
            Check(!view.CanCancel, "Cancellation immediately disables repeated cancellation");
            await UntilAsync(() => !view.IsBusy);
            AssertState(view, UpdatePhase.Cancelled, UpdatePrimaryAction.Check, busy: false, activity: false, download: false, cancel: false);
            Check(view.CanExecutePrimaryAction, "A cancelled check can be started again");
            await MakeAvailableAsync(view);
            view.DownloadCommand.Execute(null);
            var oldProgress = ReviewUpdateBackend.Progress!;
            oldProgress.Report(new("Downloading", 10 * 1048576, Release.Size));
            await UntilAsync(() => view.Progress == 10);
            view.CancelCommand.Execute(null);
            await UntilAsync(() => !view.IsBusy);
            AssertState(view, UpdatePhase.Cancelled, UpdatePrimaryAction.Download, busy: false, activity: false, download: false, cancel: false);
            Check(view.PrimaryActionText == UpdateLocalization.Download, "Cancelling a download restores its normal download action label");
            var cancelledDescription = view.StatusDescription;
            oldProgress.Report(new("Verifying"));
            await DrainAsync();
            Check(view.Phase == UpdatePhase.Cancelled && view.StatusDescription == cancelledDescription,
                "Progress queued before cancellation cannot replace its result");

            view.DownloadCommand.Execute(null);
            oldProgress.Report(new("Downloading", 90 * 1048576, Release.Size));
            await DrainAsync();
            Check(view.Phase == UpdatePhase.Downloading && view.Progress == 0,
                "An older operation's progress cannot change a restarted download");
            ReviewUpdateBackend.Progress!.Report(new("Verifying"));
            await UntilAsync(() => view.Phase == UpdatePhase.Verifying);
            view.CancelCommand.Execute(null);
            await UntilAsync(() => !view.IsBusy);
            AssertState(view, UpdatePhase.Cancelled, UpdatePrimaryAction.Download, busy: false, activity: false, download: false, cancel: false);
        }
        finally { await view.CloseAsync(); }
    }

    private static async Task ErrorAndRetryAsync()
    {
        ReviewUpdateBackend.Reset();
        var view = new UpdateViewModel(NullAppLogger.Instance);
        try
        {
            view.CheckCommand.Execute(null);
            ReviewUpdateBackend.CheckCompletion!.SetException(new HttpRequestException("fixture network failure"));
            await UntilAsync(() => !view.IsBusy);
            AssertState(view, UpdatePhase.Failed, UpdatePrimaryAction.Check, busy: false, activity: false, download: false, cancel: false);
            Check(view.CanExecutePrimaryAction && view.PrimaryActionText == UpdateLocalization.Retry && view.StatusDescription == UpdateLocalization.NetworkError,
                "A failed check names the network problem and offers a usable retry");
            view.CheckCommand.Execute(null);
            ReviewUpdateBackend.CheckCompletion!.SetException(new OperationCanceledException("fixture timeout"));
            await UntilAsync(() => !view.IsBusy);
            Check(view.Phase == UpdatePhase.Failed && view.StatusDescription == UpdateLocalization.NetworkError,
                "A transport timeout is an update failure rather than a user cancellation");
            view.CheckCommand.Execute(null);
            ReviewUpdateBackend.CheckCompletion!.SetResult(null);
            await UntilAsync(() => !view.IsBusy);
            AssertState(view, UpdatePhase.UpToDate, UpdatePrimaryAction.Check, busy: false, activity: false, download: false, cancel: false);

            await MakeAvailableAsync(view);
            view.DownloadCommand.Execute(null);
            ReviewUpdateBackend.PrepareCompletion!.SetException(new PortableUpdateException("ChecksumMismatch"));
            await UntilAsync(() => !view.IsBusy);
            AssertState(view, UpdatePhase.Failed, UpdatePrimaryAction.Download, busy: false, activity: false, download: false, cancel: false);
            Check(view.CanExecutePrimaryAction && !view.CanRestart && view.PrimaryActionText == UpdateLocalization.Retry &&
                view.StatusDescription == UpdateLocalization.ChecksumMismatch,
                "A failed verification explains the checksum problem and retries download rather than enabling restart");
            view.DownloadCommand.Execute(null);
            ReviewUpdateBackend.PrepareCompletion!.SetResult("prepared-job-retry");
            await UntilAsync(() => !view.IsBusy);
            var firstHandoff = view.HandOffAsync();
            ReviewUpdateBackend.HelperCompletion!.SetException(new PortableUpdateException("HelperFailed"));
            try { await firstHandoff; throw new Exception("Expected the helper failure to reach its caller."); }
            catch (PortableUpdateException error) when (error.Code == "HelperFailed") { }
            AssertState(view, UpdatePhase.Failed, UpdatePrimaryAction.Restart, busy: false, activity: false, download: false, cancel: false);
            Check(view.CanExecutePrimaryAction && view.CanRestart, "A failed helper launch retains the verified package and restart retry");
            var retryHandoff = view.HandOffAsync();
            ReviewUpdateBackend.HelperCompletion!.SetResult(true);
            await retryHandoff;
            Check(ReviewUpdateBackend.HelperCalls == 2 && !view.CanExecutePrimaryAction, "Restart retry hands off once and disables further actions");
        }
        finally { await view.CloseAsync(); }
    }

    private static async Task RateLimitPresentationAsync()
    {
        ReviewUpdateBackend.Reset();
        var view = new UpdateViewModel(NullAppLogger.Instance);
        var retryAfter = DateTimeOffset.UtcNow.AddMinutes(10);
        try
        {
            view.CheckCommand.Execute(null);
            ReviewUpdateBackend.CheckCompletion!.SetException(new PortableUpdateException("RateLimited", retryAfter: retryAfter));
            await UntilAsync(() => !view.IsBusy);
            AssertState(view, UpdatePhase.RateLimited, UpdatePrimaryAction.Check, busy: false, activity: false, download: false, cancel: false);
            Check(!view.CanCheck && !view.CanExecutePrimaryAction && view.StatusTitle == UpdateLocalization.RateLimitedTitle,
                "Rate limiting pauses check actions without claiming an installation failed");
            Check(view.StatusDescription.Contains(retryAfter.ToLocalTime().ToString("g")) && view.ReleasePage.IsAbsoluteUri,
                "Rate limiting explains the local retry time and keeps the manual release page available");
            view.CheckCommand.Execute(null);
            Check(ReviewUpdateBackend.CheckCalls == 1 && !view.IsBusy,
                "Repeated check commands cannot bypass the server cooldown or start a spinner");
        }
        finally { await view.CloseAsync(); }

        view = new UpdateViewModel(NullAppLogger.Instance);
        try
        {
            view.CheckCommand.Execute(null);
            ReviewUpdateBackend.CheckCompletion!.SetException(new PortableUpdateException("RateLimited", retryAfter: DateTimeOffset.UtcNow.AddSeconds(-1)));
            await UntilAsync(() => !view.IsBusy);
            Check(view.CanExecutePrimaryAction && view.StatusDescription == UpdateLocalization.RetryAvailable,
                "An elapsed server cooldown restores retry with an accurate description");
            view.CheckCommand.Execute(null);
            ReviewUpdateBackend.CheckCompletion!.SetException(new PortableUpdateException("ReleaseAccessDenied"));
            await UntilAsync(() => !view.IsBusy);
            Check(view.Phase == UpdatePhase.Failed && view.CanExecutePrimaryAction &&
                view.StatusTitle == UpdateLocalization.CheckFailedTitle && view.StatusDescription != UpdateLocalization.RateLimited,
                "Access denied stays a retryable check failure and is not mislabeled as rate limiting");
        }
        finally { await view.CloseAsync(); }
    }

    private static async Task PreviousUpdateFailureAsync()
    {
        foreach (var state in new[] { "RolledBack", "Failed", "RecoveryRequired" })
        {
            ReviewUpdateBackend.Reset();
            var resultFile = Path.Combine(ReviewUpdateBackend.OutputDirectory, "prior-result-" + state + ".json");
            File.WriteAllText(resultFile, JsonSerializer.Serialize(new PortableUpdateResult(state, "5.3.10", "fixture-backup"), PortablePackage.JsonOptions));
            ReviewUpdateBackend.ResultFileOverride = resultFile;
            var view = new UpdateViewModel(NullAppLogger.Instance);
            try
            {
                Check(view.Phase == UpdatePhase.Failed && view.StatusTitle == UpdateLocalization.FailedTitle,
                    $"Previous {state} remains an installation result rather than a check failure");
                Check(view.StatusDescription == (state == "RecoveryRequired" ? string.Format(UpdateLocalization.RecoveryRequired, "fixture-backup") : UpdateLocalization.Restored),
                    $"Previous {state} preserves its recovery instructions");
                view.CheckCommand.Execute(null);
                ReviewUpdateBackend.CheckCompletion!.SetException(new HttpRequestException("fixture"));
                await UntilAsync(() => !view.IsBusy);
                Check(view.StatusTitle == UpdateLocalization.CheckFailedTitle,
                    $"A subsequent failed check replaces the previous {state} title correctly");
            }
            finally { await view.CloseAsync(); File.Delete(resultFile); }
        }
    }

    private static async Task ReadyCleanupAsync()
    {
        ReviewUpdateBackend.Reset();
        var view = new UpdateViewModel(NullAppLogger.Instance);
        await MakeAvailableAsync(view);
        view.DownloadCommand.Execute(null);
        ReviewUpdateBackend.PrepareCompletion!.SetResult("prepared-job-close");
        await UntilAsync(() => !view.IsBusy);
        await view.CloseAsync();
        Check(ReviewUpdateBackend.DeletedJobs.SequenceEqual(new[] { "prepared-job-close" }), "Closing a ready update cleans its unclaimed staging job");
        var checks = ReviewUpdateBackend.CheckCalls;
        view.CheckCommand.Execute(null);
        Check(ReviewUpdateBackend.CheckCalls == checks, "Closed presentation commands cannot start another network operation");
    }

    private static async Task CancelledPreparationCleanupAsync()
    {
        ReviewUpdateBackend.Reset();
        ReviewUpdateBackend.IgnorePrepareCancellation = true;
        var view = new UpdateViewModel(NullAppLogger.Instance);
        try
        {
            await MakeAvailableAsync(view);
            view.DownloadCommand.Execute(null);
            view.CancelCommand.Execute(null);
            Check(view.IsBusy && !view.CanCancel, "Preparation cancellation waits for cleanup with cancellation unavailable");
            ReviewUpdateBackend.PrepareCompletion!.SetResult("prepared-after-cancel");
            await UntilAsync(() => !view.IsBusy);
            Check(view.Phase == UpdatePhase.Cancelled && !view.CanRestart && ReviewUpdateBackend.DeletedJobs.Contains("prepared-after-cancel"),
                "A preparation result racing cancellation is deleted and never becomes restartable");
        }
        finally { await view.CloseAsync(); }
    }

    private static async Task ManualInstallationAsync()
    {
        ReviewUpdateBackend.Reset(portable: false);
        var view = new UpdateViewModel(NullAppLogger.Instance);
        try
        {
            AssertState(view, UpdatePhase.Idle, UpdatePrimaryAction.Check, busy: false, activity: false, download: false, cancel: false);
            await MakeAvailableAsync(view);
            AssertState(view, UpdatePhase.Available, UpdatePrimaryAction.OpenReleasePage, busy: false, activity: false, download: false, cancel: false);
            Check(view.CanExecutePrimaryAction && !view.CanDownload && view.ManualVisibility == Visibility.Visible,
                "A non-portable install can inspect the release without offering in-place replacement");
            view.DownloadCommand.Execute(null);
            await UntilAsync(() => !view.IsBusy);
            Check(ReviewUpdateBackend.PrepareCalls == 0, "A direct download command cannot bypass non-portable eligibility");
        }
        finally { await view.CloseAsync(); }
    }

    private static async Task MakeAvailableAsync(UpdateViewModel view)
    {
        view.CheckCommand.Execute(null);
        ReviewUpdateBackend.CheckCompletion!.SetResult(Release);
        await UntilAsync(() => !view.IsBusy);
    }

    private static void AssertState(UpdateViewModel view, UpdatePhase phase, UpdatePrimaryAction action, bool busy, bool activity, bool download, bool cancel)
    {
        Check(view.Phase == phase && view.PrimaryAction == action && view.IsBusy == busy &&
            view.IsActivityVisible == activity && view.IsDownloadProgressVisible == download && view.CanCancel == cancel,
            $"{phase} presents {action} with the expected activity, progress, and cancellation controls");
        Check(view.ActivityVisibility == (activity ? Visibility.Visible : Visibility.Collapsed) &&
            view.DownloadProgressVisibility == (download ? Visibility.Visible : Visibility.Collapsed) &&
            view.CancelVisibility == (cancel ? Visibility.Visible : Visibility.Collapsed),
            $"{phase} visibility properties match the displayed controls");
        Check(!string.IsNullOrWhiteSpace(view.StatusTitle) && !string.IsNullOrWhiteSpace(view.PrimaryActionText) && (!busy || !view.CanExecutePrimaryAction),
            $"{phase} has a title and action label, with its primary action disabled while busy");
        Snapshots.Add(new { phase = view.Phase.ToString(), action = view.PrimaryAction.ToString(), view.StatusTitle, view.StatusDescription,
            view.PrimaryActionText, view.CanExecutePrimaryAction, view.IsBusy, view.IsActivityVisible, view.IsDownloadProgressVisible, view.CanCancel, view.Progress, view.IsIndeterminate });
    }

    private static async Task UntilAsync(Func<bool> ready, [CallerArgumentExpression(nameof(ready))] string? condition = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (!ready()) { await Task.Delay(5, timeout.Token); }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Update presentation did not reach: " + condition);
        }
    }

    private static Task DrainAsync() => Task.Delay(20);

    private static void Check(bool passed, string description)
    {
        if (!passed) { throw new Exception("FAIL: " + description); }
        Passed.Add(description);
    }

    private sealed class RegressionContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));
        public void DispatchOne() { if (_queue.TryTake(out var next, 25)) { next.Callback(next.State); } }
        public void Dispose() => _queue.Dispose();
    }
}

internal static class ReviewUpdateBackend
{
    public static string OutputDirectory { get; set; } = "";
    public static string? ResultFileOverride { get; set; }
    public static bool IsPortable { get; private set; }
    public static bool IgnorePrepareCancellation { get; set; }
    public static int CheckCalls { get; set; }
    public static int PrepareCalls { get; set; }
    public static int HelperCalls { get; set; }
    public static TaskCompletionSource<PortableRelease?>? CheckCompletion { get; set; }
    public static TaskCompletionSource<string>? PrepareCompletion { get; set; }
    public static TaskCompletionSource<bool>? HelperCompletion { get; set; }
    public static IProgress<UpdateProgress>? Progress { get; set; }
    public static CancellationToken HelperToken { get; set; }
    public static List<string> DeletedJobs { get; } = [];

    public static void Reset(bool portable = true)
    {
        IsPortable = portable;
        ResultFileOverride = null;
        IgnorePrepareCancellation = false;
        CheckCalls = PrepareCalls = HelperCalls = 0;
        CheckCompletion = null;
        PrepareCompletion = null;
        HelperCompletion = null;
        Progress = null;
        HelperToken = default;
        DeletedJobs.Clear();
    }
}

namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
}

namespace CommunityToolkit.Mvvm.ComponentModel
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new(propertyName));
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) { return false; }
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}

namespace Harness.Helpers
{
    public static class AppMetadata { public static string GetDisplayVersion() => "5.3.10"; }
    internal static class StringResources
    {
        private static Dictionary<string, string> _values = [];
        public static void Load(string path) => _values = XDocument.Load(path).Root!.Elements("data").ToDictionary(item => item.Attribute("name")!.Value, item => item.Element("value")!.Value);
        public static string Get(string key) => _values.TryGetValue(key, out var value) ? value : throw new InvalidOperationException("Missing update string: " + key);
    }
}

namespace Harness.Updates
{
    public sealed record PortableRelease(string Version, Uri PageUrl, Uri DownloadUrl, Uri ChecksumUrl, long Size);
    public sealed record UpdateProgress(string Phase, long Received = 0, long Total = 0);
    public sealed record PortableUpdateResult(string State, string Version, string BackupDirectory, string? ErrorCode = null);
    public sealed class PortableUpdateException(string code, Exception? inner = null, DateTimeOffset? retryAfter = null) : Exception(code, inner)
    {
        public string Code { get; } = code;
        public DateTimeOffset? RetryAfter { get; } = retryAfter;
    }
    public static class PortablePackage { public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web); }
    public static class PortableUpdatePaths
    {
        public static string ResultFile(string installation) => ReviewUpdateBackend.ResultFileOverride ??
            Path.Combine(ReviewUpdateBackend.OutputDirectory, "absent-prior-result-" + Guid.NewGuid().ToString("N") + ".json");
    }
    public sealed class GitHubReleaseClient(HttpClient http)
    {
        public static HttpClient CreateHttpClient() => new(new NoNetworkHandler());
        public Task<PortableRelease?> CheckAsync(string currentVersion, CancellationToken token)
        {
            ReviewUpdateBackend.CheckCalls++;
            var completion = new TaskCompletionSource<PortableRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
            ReviewUpdateBackend.CheckCompletion = completion;
            return completion.Task.WaitAsync(token);
        }
        private sealed class NoNetworkHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
                throw new InvalidOperationException("Update presentation regression must not access the network.");
        }
    }
    public static class PortableUpdatePreparation
    {
        public static Task<string> PrepareAsync(GitHubReleaseClient client, PortableRelease release, string installation,
            string currentVersion, IProgress<UpdateProgress>? progress, CancellationToken token)
        {
            ReviewUpdateBackend.PrepareCalls++;
            ReviewUpdateBackend.Progress = progress;
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            ReviewUpdateBackend.PrepareCompletion = completion;
            return ReviewUpdateBackend.IgnorePrepareCancellation ? completion.Task : completion.Task.WaitAsync(token);
        }
        public static void DeletePreparedJob(string job) => ReviewUpdateBackend.DeletedJobs.Add(job);
    }
}

namespace Harness.Services
{
    public static class PortableUpdateService
    {
        public static bool IsSupported => ReviewUpdateBackend.IsPortable;
        public static Task StartHelperAsync(string job, CancellationToken token)
        {
            ReviewUpdateBackend.HelperCalls++;
            ReviewUpdateBackend.HelperToken = token;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ReviewUpdateBackend.HelperCompletion = completion;
            return completion.Task.WaitAsync(token);
        }
    }
}
