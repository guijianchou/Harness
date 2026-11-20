// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Diagnostics;

namespace Harness.Updates;

public static class PortableUpdatePreparation
{
    public static async Task<string> PrepareAsync(GitHubReleaseClient client, PortableRelease release, string installation,
        string currentVersion, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        installation = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation));
        PortableUpdatePaths.ValidateInstallation(installation);
        if (PortablePackage.ReadManifest(installation).Version != currentVersion ||
            PortableReleaseVersion.Parse(release.Version) <= PortableReleaseVersion.Parse(currentVersion))
        {
            throw new PortableUpdateException("InstallationChanged");
        }
        var job = Path.Combine(Directory.GetParent(installation)!.FullName, ".harness-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        try
        {
            var archive = Path.Combine(job, "package.zip");
            await client.DownloadAsync(release, archive, progress, token).ConfigureAwait(false);
            progress?.Report(new("Verifying"));
            await Task.Run(() => PortablePackage.ExtractAsync(archive, Path.Combine(job, "payload"), release.Version, token), token).ConfigureAwait(false);
            // Run the updater from the verified download, outside the directory being replaced.
            var updater = Path.Combine(job, "payload", "Updater");
            var runner = Path.Combine(job, "runner");
            foreach (var source in PortablePackage.EnumerateFiles(updater))
            {
                token.ThrowIfCancellationRequested();
                var destination = PortablePackage.ResolvePath(runner, Path.GetRelativePath(updater, source).Replace('\\', '/'));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = File.OpenRead(source);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            }
            using var process = Process.GetCurrentProcess();
            PortableUpdatePaths.WriteJson(Path.Combine(job, "plan.json"), new PortableUpdatePlan(installation, currentVersion,
                release.Version, process.Id, process.StartTime.ToUniversalTime().Ticks));
            File.Delete(archive);
            return job;
        }
        catch
        {
            DeletePreparedJob(job);
            throw;
        }
    }

    public static void DeletePreparedJob(string job)
    {
        // Only directories created by this preparation flow can be supplied by its owner.
        var directory = new DirectoryInfo(job);
        if (!directory.Name.StartsWith(".harness-update-", StringComparison.Ordinal) || !Guid.TryParseExact(directory.Name[16..], "N", out _))
        {
            throw new PortableUpdateException("UnsafeLocation");
        }
        if (!directory.Exists) { return; }
        _ = PortablePackage.EnumerateFiles(job).ToArray();
        directory.Delete(recursive: true);
    }
}
