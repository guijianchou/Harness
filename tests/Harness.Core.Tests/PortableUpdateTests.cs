// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Harness.Core.Tests;

[TestClass]
public sealed class PortableUpdateTests
{
    [TestMethod]
    public async Task ReleaseSelectionUsesNumericVersionsAndExactAssets()
    {
        using var http = Http(_ => Json(Release("5.10.0")));
        var release = await new GitHubReleaseClient(http).CheckAsync("5.9.9", default);
        Assert.AreEqual("5.10.0", release!.Version);
        Assert.IsNull(await new GitHubReleaseClient(http).CheckAsync("6.0.0", default));
    }

    [TestMethod]
    public async Task DraftPrereleaseAndMissingReleaseAreNotOffered()
    {
        foreach (var json in new[] { Release("5.10.0", draft: true), Release("5.10.0", prerelease: true) })
        {
            using var http = Http(_ => Json(json));
            Assert.IsNull(await new GitHubReleaseClient(http).CheckAsync("5.3.8", default));
        }
        using var missing = Http(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.IsNull(await new GitHubReleaseClient(missing).CheckAsync("5.3.8", default));
    }

    [TestMethod]
    public async Task ReleaseRejectsWrongRepoDuplicateAssetAndUnsafeVersion()
    {
        foreach (var json in new[] { Release("v5.10.0"), Release("5.10.0").Replace("github.com/guijianchou/Harness/", "github.com/elsewhere/Harness/"),
            Release("5.10.0", duplicate: true), Release("5.10.0").Replace(".zip.sha256", ".zip.missing"), Release("5.10.0").Replace("\"size\":100", "\"size\":99999999999") })
        {
            using var http = Http(_ => Json(json));
            await Assert.ThrowsExactlyAsync<PortableUpdateException>(() => new GitHubReleaseClient(http).CheckAsync("5.3.8", default));
        }
    }

    [TestMethod]
    public async Task RateLimitsRemainAnErrorInsteadOfUpToDate()
    {
        using var http = Http(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var error = await Assert.ThrowsExactlyAsync<PortableUpdateException>(() => new GitHubReleaseClient(http).CheckAsync("5.3.8", default));
        Assert.AreEqual("RateLimited", error.Code);
    }

    [TestMethod]
    public async Task DownloadAcceptsOnlyMatchingHashSizeAndFilename()
    {
        using var tree = new TempTree();
        var bytes = Encoding.UTF8.GetBytes("verified portable archive");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var release = DownloadRelease(bytes.Length);
        foreach (var checksum in new[] { hash + "  Harness-5.10.0-win-x64.zip\n", new string('0', 64) + "  Harness-5.10.0-win-x64.zip", hash + "  wrong.zip" })
        {
            using var http = Http(request => request.RequestUri!.AbsolutePath.EndsWith(".sha256") ? Text(checksum) : Bytes(bytes));
            var path = Path.Combine(tree.Root, Guid.NewGuid() + ".zip");
            if (checksum.StartsWith(hash, StringComparison.Ordinal) && checksum.EndsWith("win-x64.zip\n", StringComparison.Ordinal))
            {
                await new GitHubReleaseClient(http).DownloadAsync(release, path, null, default);
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
            }
            else
            {
                await Assert.ThrowsExactlyAsync<PortableUpdateException>(() => new GitHubReleaseClient(http).DownloadAsync(release, path, null, default));
                Assert.IsFalse(File.Exists(path));
                Assert.IsFalse(File.Exists(path + ".partial"));
            }
        }
    }

    [TestMethod]
    public async Task InterruptedDownloadDoesNotCreateAReadyArchive()
    {
        using var tree = new TempTree();
        using var cts = new CancellationTokenSource();
        var release = DownloadRelease(100);
        using var http = Http(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".sha256")) { return Text(new string('0', 64) + "  Harness-5.10.0-win-x64.zip"); }
            cts.Cancel();
            return Bytes(new byte[100]);
        });
        var path = Path.Combine(tree.Root, "cancel.zip");
        await Assert.ThrowsAsync<OperationCanceledException>(() => new GitHubReleaseClient(http).DownloadAsync(release, path, null, cts.Token));
        Assert.IsFalse(File.Exists(path));
        Assert.IsFalse(File.Exists(path + ".partial"));
    }

    [TestMethod]
    public async Task RedirectOutsideGitHubIsRejected()
    {
        using var tree = new TempTree();
        using var http = Http(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://example.test/asset") } });
        await Assert.ThrowsExactlyAsync<PortableUpdateException>(() => new GitHubReleaseClient(http).DownloadAsync(DownloadRelease(100), Path.Combine(tree.Root, "bad.zip"), null, default));
    }

    [TestMethod]
    public async Task PackageRejectsTraversalDevicesSymlinksAndCaseDuplicates()
    {
        foreach (var name in new[] { "../outside.txt", "/rooted.txt", "C:/outside.txt", "folder\\file", "NUL.txt", "file:stream", "folder/../file", "trailing.", "trailing " })
        {
            using var tree = new TempTree();
            var archive = Path.Combine(tree.Root, "bad.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) { WriteEntry(zip, name, "bad"); }
            await Assert.ThrowsExactlyAsync<PortableUpdateException>(() => PortablePackage.ExtractAsync(archive, Path.Combine(tree.Root, "out"), "5.10.0", default));
        }
        foreach (var symlink in new[] { false, true })
        {
            using var tree = new TempTree();
            var archive = Path.Combine(tree.Root, "bad.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                var entry = WriteEntry(zip, "file.txt", "bad");
                if (symlink) { entry.ExternalAttributes = 0xA000 << 16; }
                else { WriteEntry(zip, "FILE.txt", "duplicate"); }
            }
            await Assert.ThrowsExactlyAsync<PortableUpdateException>(() => PortablePackage.ExtractAsync(archive, Path.Combine(tree.Root, "out"), "5.10.0", default));
        }
    }

    [TestMethod]
    public async Task PackageChecksEveryFileAndRejectsUnlistedExecutables()
    {
        using var tree = new TempTree();
        var package = MakePackage(Path.Combine(tree.Root, "package"), "5.10.0");
        await PortablePackage.VerifyAsync(package, "5.10.0", false, default);
        await File.WriteAllTextAsync(Path.Combine(package, "surprise.exe"), "extra");
        await Assert.ThrowsExactlyAsync<PortableUpdateException>(() => PortablePackage.VerifyAsync(package, "5.10.0", false, default));
        File.Delete(Path.Combine(package, "surprise.exe"));
        await File.WriteAllTextAsync(Path.Combine(package, "Harness.dll"), "corrupt");
        await Assert.ThrowsExactlyAsync<PortableUpdateException>(() => PortablePackage.VerifyAsync(package, "5.10.0", false, default));
    }

    [TestMethod]
    public async Task InstallKeepsUserFilesAndBackupButRemovesObsoleteProgramFiles()
    {
        using var tree = new TempTree();
        var (installation, job) = MakeUpdate(tree.Root);
        await File.WriteAllTextAsync(Path.Combine(installation, "notes.txt"), "user notes");
        var restarted = false;
        var result = await PortableUpdateInstaller.ApplyAsync(job, executable =>
        {
            Assert.AreEqual(Path.Combine(installation, "Harness.exe"), executable);
            Assert.AreEqual("5.10.0", PortablePackage.ReadManifest(installation).Version);
            Assert.AreEqual("user notes", File.ReadAllText(Path.Combine(installation, "notes.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(installation, "obsolete.dll")));
            return Task.FromResult(true);
        }, _ => restarted = true, default);
        Assert.AreEqual("Completed", result.State);
        Assert.IsFalse(restarted);
        Assert.AreEqual("5.3.8", PortablePackage.ReadManifest(result.BackupDirectory).Version);
        Assert.IsTrue(File.Exists(Path.Combine(result.BackupDirectory, "notes.txt")));
    }

    [TestMethod]
    public async Task StartupFailureRestoresCompleteOldDirectoryAndRestartsIt()
    {
        using var tree = new TempTree();
        var (installation, job) = MakeUpdate(tree.Root);
        await File.WriteAllTextAsync(Path.Combine(installation, "notes.txt"), "preserve");
        var restarted = false;
        var result = await PortableUpdateInstaller.ApplyAsync(job, _ => Task.FromResult(false), executable =>
        {
            restarted = true;
            Assert.AreEqual("5.3.8", PortablePackage.ReadManifest(Path.GetDirectoryName(executable)!).Version);
        }, default);
        Assert.AreEqual("RolledBack", result.State);
        Assert.IsTrue(restarted);
        Assert.AreEqual("preserve", await File.ReadAllTextAsync(Path.Combine(installation, "notes.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(installation, "obsolete.dll")));
        Assert.IsTrue(Directory.Exists(Path.Combine(job, "failed-version")));
    }

    [TestMethod]
    public async Task UserFileCollisionAndDamagedPayloadNeverReplaceInstallation()
    {
        foreach (var damaged in new[] { true, false })
        {
            using var tree = new TempTree();
            var (installation, job) = MakeUpdate(tree.Root);
            if (damaged) { await File.WriteAllTextAsync(Path.Combine(job, "payload", "Harness.dll"), "damaged"); }
            else { await File.WriteAllTextAsync(Path.Combine(installation, "new-library.dll"), "user custom file"); }
            var started = false;
            var result = await PortableUpdateInstaller.ApplyAsync(job, _ => { started = true; return Task.FromResult(true); }, _ => { }, default);
            Assert.AreEqual("Failed", result.State);
            Assert.IsFalse(started);
            Assert.AreEqual("5.3.8", PortablePackage.ReadManifest(installation).Version);
            Assert.IsFalse(Directory.Exists(Path.Combine(job, "backup")));
        }
    }

    [TestMethod]
    public async Task CancellationAndStaleInstallationNeverStartCandidate()
    {
        using var tree = new TempTree();
        var (installation, job) = MakeUpdate(tree.Root);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var result = await PortableUpdateInstaller.ApplyAsync(job, _ => throw new AssertFailedException(), _ => { }, cancel.Token);
        Assert.AreEqual("Failed", result.State);
        Assert.AreEqual("5.3.8", PortablePackage.ReadManifest(installation).Version);
    }

    [TestMethod]
    public void PlanCannotMoveUnrelatedDirectoriesOrDowngrade()
    {
        using var tree = new TempTree();
        var (installation, job) = MakeUpdate(tree.Root);
        var plan = PortableUpdatePaths.ReadPlan(job);
        PortableUpdatePaths.WriteJson(Path.Combine(job, "plan.json"), plan with { Version = "5.0.0" });
        Assert.ThrowsExactly<PortableUpdateException>(() => PortableUpdatePaths.ReadPlan(job));
        PortableUpdatePaths.WriteJson(Path.Combine(job, "plan.json"), plan with { Installation = Path.Combine(installation, "nested") });
        Assert.ThrowsExactly<PortableUpdateException>(() => PortableUpdatePaths.ReadPlan(job));
    }

    private static (string Installation, string Job) MakeUpdate(string root)
    {
        var installation = MakePackage(Path.Combine(root, "Harness portable"), "5.3.8", "obsolete.dll");
        var job = Path.Combine(root, ".harness-update-" + Guid.NewGuid().ToString("N"));
        MakePackage(Path.Combine(job, "payload"), "5.10.0", "new-library.dll");
        PortableUpdatePaths.WriteJson(Path.Combine(job, "plan.json"), new PortableUpdatePlan(installation, "5.3.8", "5.10.0", 1, 1));
        return (installation, job);
    }

    private static string MakePackage(string root, string version, string? extra = null)
    {
        string[] required = ["Harness.exe", "Harness.dll", "Harness.Core.dll", "Harness.pri", "Microsoft.WindowsAppRuntime.dll", "Microsoft.ui.xaml.dll",
            "hostfxr.dll", "coreclr.dll", "BUILD-INFO.json", "Updater/Harness.Updater.exe", "Updater/Harness.Updater.dll",
            "Updater/Harness.Core.dll", "Updater/Harness.Updater.runtimeconfig.json"];
        var files = new List<PortableFile>();
        foreach (var name in extra is null ? required : required.Append(extra))
        {
            var path = Path.Combine(root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(name + " " + version);
            File.WriteAllBytes(path, bytes);
            files.Add(new PortableFile(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        PortableUpdatePaths.WriteJson(Path.Combine(root, PortablePackage.ManifestName), new PortableManifest(1, version, "win-x64", files.ToArray()));
        return root;
    }

    private static string Release(string version, bool draft = false, bool prerelease = false, bool duplicate = false)
    {
        var name = $"Harness-{version}-win-x64.zip";
        object Asset(string file) => new { name = file, state = "uploaded", size = 100, browser_download_url = $"https://github.com/guijianchou/Harness/releases/download/{version}/{file}" };
        var assets = new List<object> { Asset(name), Asset(name + ".sha256") };
        if (duplicate) { assets.Add(Asset(name)); }
        return JsonSerializer.Serialize(new { tag_name = version, draft, prerelease, html_url = $"https://github.com/guijianchou/Harness/releases/tag/{version}", assets });
    }

    private static PortableRelease DownloadRelease(long size) => new("5.10.0", new Uri("https://github.com/guijianchou/Harness/releases/tag/5.10.0"),
        new Uri("https://github.com/guijianchou/Harness/releases/download/5.10.0/Harness-5.10.0-win-x64.zip"),
        new Uri("https://github.com/guijianchou/Harness/releases/download/5.10.0/Harness-5.10.0-win-x64.zip.sha256"), size);
    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> send) => new(new Handler(send));
    private static HttpResponseMessage Json(string value) => Text(value);
    private static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private static HttpResponseMessage Bytes(byte[] value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };
    private static ZipArchiveEntry WriteEntry(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
        return entry;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Harness-update-tests-" + Guid.NewGuid().ToString("N"));
        public TempTree() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
