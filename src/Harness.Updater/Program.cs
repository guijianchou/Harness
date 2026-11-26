// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Diagnostics;
using System.Text.Json;
using Harness.Updates;

return await RunAsync(args);

static async Task<int> RunAsync(string[] arguments)
{
    if (arguments.Length != 1) { return 2; }
    var job = Path.GetFullPath(arguments[0]);
    PortableUpdatePlan? plan = null;
    try
    {
        plan = PortableUpdatePaths.ReadPlan(job);
        if (!string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), Path.Combine(job, "runner"), StringComparison.OrdinalIgnoreCase))
        {
            throw new PortableUpdateException("UnsafeLocation");
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await PortablePackage.VerifyAsync(Path.Combine(job, "payload"), plan.Version, false, timeout.Token);
        using var parent = Process.GetProcessById(plan.ParentProcessId);
        if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartTicks ||
            !string.Equals(parent.MainModule?.FileName, Path.Combine(plan.Installation, "Harness.exe"), StringComparison.OrdinalIgnoreCase))
        {
            throw new PortableUpdateException("InstallationChanged");
        }
        PortableUpdatePaths.WriteJson(Path.Combine(job, "helper-ready.json"), new { processId = Environment.ProcessId });
        await parent.WaitForExitAsync(timeout.Token);
        // Every portable Harness instance holds a shared lease until process shutdown.
        FileStream? lease = null;
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (lease is null)
            {
                try { lease = PortableUpdatePaths.AcquireLease(plan.Installation, true); }
                catch (IOException) when (DateTimeOffset.UtcNow < deadline) { await Task.Delay(250, timeout.Token); }
            }
            var result = await PortableUpdateInstaller.ApplyAsync(job, async executable =>
            {
                lease.Dispose();
                lease = null;
                PortableUpdatePaths.TryWriteResult(PortableUpdatePaths.ResultFile(plan.Installation),
                    new PortableUpdateResult("Starting", plan.Version, Path.Combine(job, "backup")));
                var healthy = false;
                try { healthy = await StartAndVerifyAsync(executable, job, plan.Version); }
                catch (Exception) { /* Reacquire exclusive ownership before attempting any rollback. */ }
                if (!healthy)
                {
                    try { lease = PortableUpdatePaths.AcquireLease(plan.Installation, true); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        throw new PortableUpdateException("RecoveryRequired", error);
                    }
                }
                return healthy;
            }, executable =>
            {
                lease?.Dispose();
                lease = null;
                try
                {
                    var result = JsonSerializer.Deserialize<PortableUpdateResult>(File.ReadAllText(Path.Combine(job, "result.json")), PortablePackage.JsonOptions);
                    if (result is not null) { PortableUpdatePaths.TryWriteResult(PortableUpdatePaths.ResultFile(plan.Installation), result); }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException) { }
                Start(executable, null)?.Dispose();
            }, timeout.Token);
            PortableUpdatePaths.TryWriteResult(PortableUpdatePaths.ResultFile(plan.Installation), result);
            return result.State == "Completed" ? 0 : 1;
        }
        finally { lease?.Dispose(); }
    }
    catch (Exception error)
    {
        if (plan is not null)
        {
            var code = error is PortableUpdateException update ? update.Code : error is IOException ? "OtherInstances" : "ApplyFailed";
            try
            {
                var result = new PortableUpdateResult("Failed", plan.Version, Path.Combine(job, "backup"), code);
                PortableUpdatePaths.TryWriteResult(Path.Combine(job, "result.json"), result);
                PortableUpdatePaths.TryWriteResult(PortableUpdatePaths.ResultFile(plan.Installation), result);
                // Only restart after the initiating process exited; it may still show the helper error.
                // A failed rollback must never fall through to launching the unconfirmed new version.
                if (PortablePackage.ReadManifest(plan.Installation).Version == plan.CurrentVersion &&
                    !Process.GetProcessesByName("Harness").Any(process => IsTargetProcess(process, plan.Installation)))
                {
                    Start(Path.Combine(plan.Installation, "Harness.exe"), null)?.Dispose();
                }
            }
            catch (Exception) { /* The durable job retains both payload and backup for manual recovery. */ }
        }
        return 1;
    }
}

static bool IsTargetProcess(Process process, string installation)
{
    using (process)
    {
        try { return string.Equals(process.MainModule?.FileName, Path.Combine(installation, "Harness.exe"), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return true; }
    }
}

static Process? Start(string executable, string? job)
{
    var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
    info.Environment.Remove("HARNESS_UPDATE_JOB");
    if (job is not null) { info.Environment["HARNESS_UPDATE_JOB"] = job; }
    return Process.Start(info);
}

static async Task<bool> StartAndVerifyAsync(string executable, string job, string version)
{
    using var process = Start(executable, job) ?? throw new PortableUpdateException("StartupFailed");
    var accepted = false;
    try
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (!process.HasExited && DateTimeOffset.UtcNow < deadline)
        {
            var healthFile = Path.Combine(job, "healthy.json");
            if (File.Exists(healthFile))
            {
                using var health = JsonDocument.Parse(await File.ReadAllTextAsync(healthFile));
                if (health.RootElement.GetProperty("processId").GetInt32() == process.Id && health.RootElement.GetProperty("version").GetString() == version)
                {
                    await Task.Delay(2000);
                    accepted = !process.HasExited;
                    return accepted;
                }
            }
            await Task.Delay(200);
        }
    }
    finally
    {
        // A timed-out candidate must release its files before rollback; never stop an unrelated instance.
        if (!process.HasExited && !accepted)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
    return false;
}
