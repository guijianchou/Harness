// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Harness.Helpers;
using Harness.Updates;

namespace Harness.Services;

internal static class PortableUpdateService
{
    public static bool IsSupported
    {
        get
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64) { return false; }
            try
            {
                return PortablePackage.ReadManifest(AppContext.BaseDirectory).Version == AppMetadata.GetDisplayVersion() &&
                    File.Exists(Path.Combine(AppContext.BaseDirectory, "Updater", "Harness.Updater.exe"));
            }
            catch (Exception) { return false; }
        }
    }

    public static async Task StartHelperAsync(string job, CancellationToken token)
    {
        var plan = PortableUpdatePaths.ReadPlan(job);
        if (!string.Equals(plan.Installation, Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
        {
            throw new PortableUpdateException("InstallationChanged");
        }
        File.Delete(Path.Combine(job, "helper-ready.json"));
        var info = new ProcessStartInfo(Path.Combine(job, "runner", "Harness.Updater.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = job,
        };
        info.ArgumentList.Add(job);
        using var helper = Process.Start(info) ?? throw new PortableUpdateException("HelperFailed");
        var ready = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            while (!File.Exists(Path.Combine(job, "helper-ready.json")))
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (helper.HasExited) { throw new PortableUpdateException("HelperFailed"); }
                await Task.Delay(100, timeout.Token);
            }
            ready = !helper.HasExited;
            if (!ready) { throw new PortableUpdateException("HelperFailed"); }
        }
        finally
        {
            if (!ready && !helper.HasExited)
            {
                helper.Kill();
                await helper.WaitForExitAsync();
            }
        }
    }
}
