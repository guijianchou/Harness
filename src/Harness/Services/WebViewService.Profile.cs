// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Models;

namespace Harness.Services;

public partial class WebViewService
{
    private static readonly string ProfileDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Harness",
        "WebView2Data");
    private static readonly EnvironmentProfileStore ProfileStore = new(ProfileDataRoot);

    public static void RegisterEnvironmentProfiles(IEnumerable<EnvironmentConfig> savedEnvironments)
    {
        ProfileStore.RegisterEnvironments(savedEnvironments);
    }

    public static string GetUserDataFolderForEnvironment(string environmentId)
    {
        return ProfileStore.GetUserDataFolder(environmentId);
    }

    private static string PrepareUserDataFolderForEnvironment(string environmentId)
    {
        return ProfileStore.PrepareUserDataFolder(environmentId);
    }

    private void ValidateUserDataFolder(string expectedFolder, string actualFolder)
    {
        // WebView2 policy and environment overrides can supersede the explicit path.
        // Validate before creating a controller, which would expose the shared profile.
        if (!string.IsNullOrWhiteSpace(actualFolder) &&
            Path.IsPathFullyQualified(actualFolder) &&
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedFolder)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(actualFolder)),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _logger.Error("webview.profile.path_mismatch", new { expectedFolder, actualFolder });
        throw new InvalidOperationException(string.Format(System.Globalization.CultureInfo.CurrentCulture,
            Harness.Helpers.StringResources.Get("WebViewProfilePathMismatch"), expectedFolder, actualFolder));
    }

    private string ReportProfileAccessDenied(UnauthorizedAccessException error)
    {
        string? identityName = null;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            identityName = identity.Name;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Identity lookup must not hide the original profile access failure.
        }

        _logger.Error("webview.profile.access_denied", new
        {
            stage = "prepare_profile",
            dataDirectory = ProfileDataRoot,
            processPath = Environment.ProcessPath,
            processId = Environment.ProcessId,
            windowsIdentity = identityName,
            hResult = $"0x{error.HResult:X8}",
            reason = error.Message,
        });
        return string.Format(System.Globalization.CultureInfo.CurrentCulture,
            Harness.Helpers.StringResources.Get("WebViewProfileAccessDenied"), ProfileDataRoot);
    }

    public static void DeleteUserDataFolderForEnvironment(string environmentId, IAppLogger? logger = null)
    {
        try
        {
            ProfileStore.DeleteUserDataFolder(environmentId);
            logger?.Info("webview.profile.deleted", new { environmentId });
        }
        catch (Exception ex)
        {
            logger?.Warning("webview.profile.delete_failed", new { environmentId, ex.Message });
            throw;
        }
    }
}
