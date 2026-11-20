// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Harness.Updates;

public sealed record PortableFile(string Path, long Size, string Sha256);
public sealed record PortableManifest(int Schema, string Version, string Runtime, PortableFile[] Files);

public static class PortablePackage
{
    public const string ManifestName = "portable-manifest.json";
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const long MaximumExpandedSize = 2L * 1024 * 1024 * 1024;
    private static readonly string[] RequiredFiles = ["Harness.exe", "Harness.dll", "Harness.Core.dll", "Harness.pri",
        "Microsoft.WindowsAppRuntime.dll", "Microsoft.ui.xaml.dll", "hostfxr.dll", "coreclr.dll", "BUILD-INFO.json",
        "Updater/Harness.Updater.exe", "Updater/Harness.Updater.dll", "Updater/Harness.Core.dll", "Updater/Harness.Updater.runtimeconfig.json"];

    public static PortableManifest ReadManifest(string directory)
    {
        var path = Path.Combine(directory, ManifestName);
        RejectReparsePoints(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) { throw new PortableUpdateException("InvalidPackage"); }
        try
        {
            var manifest = JsonSerializer.Deserialize<PortableManifest>(File.ReadAllText(path), JsonOptions);
            if (manifest is null || manifest.Schema != 1 || manifest.Runtime != "win-x64" || manifest.Files is null || manifest.Files.Length is < 1 or > 12000)
            {
                throw new PortableUpdateException("InvalidPackage");
            }
            PortableReleaseVersion.Parse(manifest.Version);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var file in manifest.Files)
            {
                if (file is null || string.IsNullOrEmpty(file.Path) || !names.Add(file.Path) || file.Path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) ||
                    file.Size < 0 || file.Size > MaximumExpandedSize || file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                {
                    throw new PortableUpdateException("InvalidPackage");
                }
                ResolvePath(directory, file.Path);
                total += file.Size;
                if (total > MaximumExpandedSize) { throw new PortableUpdateException("InvalidPackage"); }
            }
            if (RequiredFiles.Any(name => !names.Contains(name))) { throw new PortableUpdateException("InvalidPackage"); }
            return manifest;
        }
        catch (JsonException ex) { throw new PortableUpdateException("InvalidPackage", ex); }
    }

    public static async Task VerifyAsync(string directory, string expectedVersion, bool allowExtraFiles, CancellationToken token)
    {
        var manifest = ReadManifest(directory);
        if (manifest.Version != expectedVersion) { throw new PortableUpdateException("InvalidPackage"); }
        var owned = manifest.Files.Select(file => file.Path).Append(ManifestName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateFiles(directory))
        {
            if (!allowExtraFiles && !owned.Contains(Path.GetRelativePath(directory, path).Replace('\\', '/')))
            {
                throw new PortableUpdateException("InvalidPackage");
            }
        }
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            var path = ResolvePath(directory, file.Path);
            RejectReparsePoints(path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size) { throw new PortableUpdateException("InvalidPackage"); }
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(file.Sha256))) { throw new PortableUpdateException("ChecksumMismatch"); }
        }
    }

    public static async Task ExtractAsync(string archive, string destination, string version, CancellationToken token)
    {
        if (Directory.Exists(destination)) { throw new PortableUpdateException("InvalidPackage"); }
        Directory.CreateDirectory(destination);
        RejectReparsePoints(destination);
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count is < 1 or > 12000) { throw new PortableUpdateException("InvalidPackage"); }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName.TrimEnd('/');
            var path = ResolvePath(destination, name);
            if (!names.Add(name) || (entry.ExternalAttributes & 0x400) != 0 || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                entry.Length < 0 || entry.Length > MaximumExpandedSize)
            {
                throw new PortableUpdateException("InvalidPackage");
            }
            total += entry.Length;
            if (total > MaximumExpandedSize) { throw new PortableUpdateException("InvalidPackage"); }
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long copied = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                copied += count;
                if (copied > entry.Length) { throw new PortableUpdateException("InvalidPackage"); }
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            if (copied != entry.Length) { throw new PortableUpdateException("InvalidPackage"); }
        }
        await VerifyAsync(destination, version, false, token).ConfigureAwait(false);
    }

    public static string ResolvePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.StartsWith('/') ||
            relative.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny([':', '<', '>', '"', '|', '?', '*']) >= 0 || part.Any(char.IsControl) || IsDeviceName(part)))
        {
            throw new PortableUpdateException("InvalidPackage");
        }
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new PortableUpdateException("InvalidPackage");
        }
        return path;
    }

    private static bool IsDeviceName(string part)
    {
        var name = part.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" ||
            (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(name[3]));
    }

    public static void RejectReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new PortableUpdateException("UnsafeLocation");
            }
        }
    }

    public static IEnumerable<string> EnumerateFiles(string directory)
    {
        RejectReparsePoints(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectReparsePoints(entry);
            if (Directory.Exists(entry))
            {
                foreach (var child in EnumerateFiles(entry)) { yield return child; }
            }
            else { yield return entry; }
        }
    }
}
