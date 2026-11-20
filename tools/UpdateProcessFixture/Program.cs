// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Updates;

using var lease = PortableUpdatePaths.AcquireLease(AppContext.BaseDirectory, false);
if (args is ["--parent", var ready, var stop])
{
    await File.WriteAllTextAsync(ready, "ready");
    var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
    while (!File.Exists(stop) && DateTimeOffset.UtcNow < deadline) { await Task.Delay(100); }
    return;
}
var job = Environment.GetEnvironmentVariable("HARNESS_UPDATE_JOB");
if (job is null)
{
    await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "restarted.txt"), "previous version restarted");
    return;
}
var mode = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixture-mode.txt"));
if (mode == "fail") { Environment.Exit(42); }
var plan = PortableUpdatePaths.ReadPlan(job);
PortableUpdatePaths.WriteJson(Path.Combine(job, "healthy.json"), new { processId = Environment.ProcessId, version = plan.Version });
await Task.Delay(6000);
