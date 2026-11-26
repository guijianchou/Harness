// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.Updates;

public static class PortableUpdateInstaller
{
    public static async Task<PortableUpdateResult> ApplyAsync(string job,
        Func<string, Task<bool>> startAndVerify, Action<string> restartPrevious, CancellationToken token)
    {
        var plan = PortableUpdatePaths.ReadPlan(job);
        var payload = Path.Combine(job, "payload");
        var backup = Path.Combine(job, "backup");
        var failed = Path.Combine(job, "failed-version");
        var movedOld = false;
        var installedNew = false;
        var confirmed = false;
        try
        {
            if (Directory.Exists(backup) || Directory.Exists(failed)) { throw new PortableUpdateException("AlreadyApplied"); }
            await PortablePackage.VerifyAsync(payload, plan.Version, false, token).ConfigureAwait(false);
            var current = PortablePackage.ReadManifest(plan.Installation);
            if (current.Version != plan.CurrentVersion) { throw new PortableUpdateException("InstallationChanged"); }
            await PreserveUserFilesAsync(plan.Installation, payload, current, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            SaveResult(job, plan, "Replacing", backup, required: true);
            Directory.Move(plan.Installation, backup);
            movedOld = true;
            Directory.Move(payload, plan.Installation);
            installedNew = true;
            SaveResult(job, plan, "Starting", backup, required: true);
            if (!await startAndVerify(Path.Combine(plan.Installation, "Harness.exe")).ConfigureAwait(false))
            {
                throw new PortableUpdateException("StartupFailed");
            }
            confirmed = true;
            return SaveResult(job, plan, "Completed", backup);
        }
        catch (Exception error)
        {
            if (confirmed) { return new PortableUpdateResult("Completed", plan.Version, backup); }
            if (error is PortableUpdateException { Code: "RecoveryRequired" })
            {
                return SaveResult(job, plan, "RecoveryRequired", backup, "OtherInstances");
            }
            var code = error is PortableUpdateException updateError ? updateError.Code : error is UnauthorizedAccessException ? "AccessDenied" : "ApplyFailed";
            try
            {
                if (installedNew) { Directory.Move(plan.Installation, failed); }
                if (movedOld) { Directory.Move(backup, plan.Installation); }
            }
            catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
            {
                return SaveResult(job, plan, "RecoveryRequired", backup, code);
            }
            var result = SaveResult(job, plan, movedOld ? "RolledBack" : "Failed", backup, code);
            restartPrevious(Path.Combine(plan.Installation, "Harness.exe"));
            return result;
        }
    }

    private static async Task PreserveUserFilesAsync(string installation, string payload, PortableManifest current, CancellationToken token)
    {
        var owned = current.Files.Select(file => file.Path).Append(PortablePackage.ManifestName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var source in PortablePackage.EnumerateFiles(installation))
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(installation, source).Replace('\\', '/');
            if (owned.Contains(relative)) { continue; }
            var destination = PortablePackage.ResolvePath(payload, relative);
            if (File.Exists(destination) || Directory.Exists(destination)) { throw new PortableUpdateException("UserFileConflict"); }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = File.OpenRead(source);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, token).ConfigureAwait(false);
        }
    }

    private static PortableUpdateResult SaveResult(string job, PortableUpdatePlan plan, string state, string backup, string? code = null, bool required = false)
    {
        var result = new PortableUpdateResult(state, plan.Version, backup, code);
        var path = Path.Combine(job, "result.json");
        if (required) { PortableUpdatePaths.WriteJson(path, result); }
        else { PortableUpdatePaths.TryWriteResult(path, result); }
        return result;
    }
}
