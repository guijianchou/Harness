// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Harness.Updates;

public sealed record PortableUpdatePlan(string Installation, string CurrentVersion, string Version, int ParentProcessId, long ParentStartTicks);
public sealed record PortableUpdateResult(string State, string Version, string BackupDirectory, string? ErrorCode = null);

public static class PortableUpdatePaths
{
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Harness", "Updates");
    public static string Key(string installation) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation)).ToUpperInvariant())));
    public static string ResultFile(string installation) => Path.Combine(DataRoot, Key(installation) + ".json");

    public static void WriteJson<T>(string path, T value)
    {
        PortablePackage.RejectReparsePoints(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, PortablePackage.JsonOptions));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    public static PortableUpdatePlan ReadPlan(string job)
    {
        PortablePackage.RejectReparsePoints(job);
        var planPath = Path.Combine(job, "plan.json");
        if (new FileInfo(planPath).Length > 16384) { throw new PortableUpdateException("InvalidPackage"); }
        var plan = JsonSerializer.Deserialize<PortableUpdatePlan>(File.ReadAllText(planPath), PortablePackage.JsonOptions)
            ?? throw new PortableUpdateException("InvalidPackage");
        ValidateInstallation(plan.Installation);
        if (PortableReleaseVersion.Parse(plan.Version) <= PortableReleaseVersion.Parse(plan.CurrentVersion) || plan.ParentProcessId <= 0 || plan.ParentStartTicks <= 0)
        {
            throw new PortableUpdateException("InvalidPackage");
        }
        var directory = new DirectoryInfo(Path.GetFullPath(job));
        var target = new DirectoryInfo(plan.Installation);
        if (!string.Equals(directory.Parent?.FullName, target.Parent?.FullName, StringComparison.OrdinalIgnoreCase) ||
            !directory.Name.StartsWith(".harness-update-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(directory.Name[16..], "N", out _))
        {
            throw new PortableUpdateException("UnsafeLocation");
        }
        return plan;
    }

    public static void ValidateInstallation(string installation)
    {
        if (!Path.IsPathFullyQualified(installation) || new DirectoryInfo(installation).Parent is null || installation.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new PortableUpdateException("UnsafeLocation");
        }
        PortablePackage.RejectReparsePoints(installation);
    }

    public static FileStream AcquireLease(string installation, bool exclusive)
    {
        Directory.CreateDirectory(DataRoot);
        var path = Path.Combine(DataRoot, Key(installation) + ".lock");
        PortablePackage.RejectReparsePoints(path);
        if (!File.Exists(path))
        {
            try { using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite); }
            catch (IOException) when (File.Exists(path)) { }
        }
        return new FileStream(path, FileMode.Open, exclusive ? FileAccess.ReadWrite : FileAccess.Read, exclusive ? FileShare.None : FileShare.Read);
    }
}
