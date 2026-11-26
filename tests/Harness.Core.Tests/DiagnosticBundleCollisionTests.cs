// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.IO.Compression;
using Harness.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Harness.Core.Tests;

[TestClass]
public sealed class DiagnosticBundleCollisionTests
{
    private static readonly DiagnosticRuntimeInfo Runtime = new("test", "test", "test", "test", "x64", 1, "hash");

    [TestMethod]
    public async Task RepeatedExportsKeepIndependentBundles()
    {
        var directory = Directory.CreateTempSubdirectory("harness-diagnostics-collision-");
        try
        {
            var first = await ExportAsync(directory.FullName, "first report");
            var originalBytes = await File.ReadAllBytesAsync(first);
            var second = await ExportAsync(directory.FullName, "second report");

            Assert.AreNotEqual(first, second, "Repeated exports must not reuse the same output file.");
            CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(first));
            using var archive = ZipFile.OpenRead(second);
            using var reader = new StreamReader(archive.GetEntry("diagnostic-summary.txt")!.Open());
            Assert.AreEqual("second report", await reader.ReadToEndAsync());
        }
        finally { directory.Delete(recursive: true); }
    }

    [TestMethod]
    public async Task FailedExportCannotOverwriteAnEarlierBundle()
    {
        var directory = Directory.CreateTempSubdirectory("harness-diagnostics-failure-");
        try
        {
            var first = await ExportAsync(directory.FullName, "complete report");
            var originalBytes = await File.ReadAllBytesAsync(first);
            var logFile = Path.Combine(directory.FullName, "harness-locked.log");
            using (var lockedLog = new FileStream(logFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                await Assert.ThrowsExactlyAsync<IOException>(() => ExportAsync(directory.FullName, "incomplete report"));
            }
            CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(first),
                "A failed export must leave the previously completed archive intact.");
        }
        finally { directory.Delete(recursive: true); }
    }

    private static Task<string> ExportAsync(string directory, string summary) =>
        DiagnosticBundleService.ExportBundleAsync("{}", directory, summary, directory, Runtime);
}
