// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Models;

namespace Harness.Services;

public partial class WebViewService
{
    private static readonly EnvironmentProfileStore ProfileStore = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Harness",
        "WebView2Data"));

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
