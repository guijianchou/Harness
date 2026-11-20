// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Helpers;
using Harness.Updates;

namespace Harness;

public partial class App
{
    private FileStream? _portableInstanceLease;

    private async Task AcquirePortableLeaseAsync()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, PortablePackage.ManifestName))) { return; }
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (_portableInstanceLease is null)
        {
            try { _portableInstanceLease = PortableUpdatePaths.AcquireLease(AppContext.BaseDirectory, false); }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline) { await Task.Delay(200); }
        }
    }

    private static void ConfirmUpdatedLaunch()
    {
        var job = Environment.GetEnvironmentVariable("HARNESS_UPDATE_JOB");
        Environment.SetEnvironmentVariable("HARNESS_UPDATE_JOB", null);
        if (string.IsNullOrEmpty(job)) { return; }
        try
        {
            var plan = PortableUpdatePaths.ReadPlan(job);
            if (plan.Version == AppMetadata.GetDisplayVersion() &&
                string.Equals(plan.Installation, Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
            {
                PortableUpdatePaths.WriteJson(Path.Combine(job, "healthy.json"), new { processId = Environment.ProcessId, version = plan.Version });
            }
        }
        catch (Exception ex) { Logger.Warning($"Update startup confirmation failed: {ex.GetType().Name}"); }
    }
}
