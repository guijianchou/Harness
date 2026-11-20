// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Updates;

namespace Harness.Helpers;

public static class UpdateLocalization
{
    public static string Title => StringResources.Get("UpdateTitle");
    public static string Idle => StringResources.Get("UpdateIdle");
    public static string Check => StringResources.Get("UpdateCheck");
    public static string Checking => StringResources.Get("UpdateChecking");
    public static string UpToDate => StringResources.Get("UpdateUpToDate");
    public static string Available => StringResources.Get("UpdateAvailable");
    public static string Download => StringResources.Get("UpdateDownload");
    public static string Downloading => StringResources.Get("UpdateDownloading");
    public static string DownloadProgress => StringResources.Get("UpdateDownloadProgress");
    public static string Verifying => StringResources.Get("UpdateVerifying");
    public static string Ready => StringResources.Get("UpdateReady");
    public static string Restart => StringResources.Get("UpdateRestart");
    public static string RestartHint => StringResources.Get("UpdateRestartHint");
    public static string Restarting => StringResources.Get("UpdateRestarting");
    public static string Cancelled => StringResources.Get("UpdateCancelled");
    public static string Manual => StringResources.Get("UpdateManual");
    public static string ReleaseNotes => StringResources.Get("UpdateReleaseNotes");
    public static string NetworkError => StringResources.Get("UpdateNetworkError");
    public static string InvalidRelease => StringResources.Get("UpdateInvalidRelease");
    public static string ChecksumMismatch => StringResources.Get("UpdateChecksumMismatch");
    public static string InvalidPackage => StringResources.Get("UpdateInvalidPackage");
    public static string RateLimited => StringResources.Get("UpdateRateLimited");
    public static string AccessDenied => StringResources.Get("UpdateAccessDenied");
    public static string UnsafeLocation => StringResources.Get("UpdateUnsafeLocation");
    public static string InstallationChanged => StringResources.Get("UpdateInstallationChanged");
    public static string HelperFailed => StringResources.Get("UpdateHelperFailed");
    public static string Failed => StringResources.Get("UpdateFailed");
    public static string Restored => StringResources.Get("UpdateRestored");
    public static string RecoveryRequired => StringResources.Get("UpdateRecoveryRequired");

    public static string Error(Exception error) => error switch
    {
        PortableUpdateException { Code: "InvalidRelease" } => InvalidRelease,
        PortableUpdateException { Code: "InvalidPackage" } => InvalidPackage,
        PortableUpdateException { Code: "ChecksumMismatch" } => ChecksumMismatch,
        PortableUpdateException { Code: "RateLimited" } => RateLimited,
        PortableUpdateException { Code: "UnsafeLocation" } => UnsafeLocation,
        PortableUpdateException { Code: "InstallationChanged" } => InstallationChanged,
        PortableUpdateException { Code: "HelperFailed" } => HelperFailed,
        UnauthorizedAccessException or IOException => AccessDenied,
        System.Net.Http.HttpRequestException or OperationCanceledException => NetworkError,
        _ => Failed,
    };
}
