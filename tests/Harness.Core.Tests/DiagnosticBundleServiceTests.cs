// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.IO.Compression;
using System.Text.Json;
using Harness.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Harness.Core.Tests;

[TestClass]
public sealed class DiagnosticBundleServiceTests
{
    [TestMethod]
    public void RedactionHandlesEscapedUrlsCredentialsAndChatSignatures()
    {
        const string json = """
            {"Message":"Navigating to: https:\u002f\u002fuser:pass@example.test/chat?token=query-secret#fragment-secret",
             "Context":{"apiToken":"escaped\"secret","activity":"private chat fragment","Authorization":"Bearer auth-secret"}}
            """;
        var redacted = DiagnosticRedactor.RedactJson(json);
        using var document = JsonDocument.Parse(redacted);
        Assert.AreEqual("Navigating to: https://<host>/<path>", document.RootElement.GetProperty("Message").GetString());
        Assert.AreEqual("<redacted>", document.RootElement.GetProperty("Context").GetProperty("activity").GetString());
        Assert.IsFalse(redacted.Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("private chat", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("example.test", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExportRedactsLegacyLogsAndSummaryWithoutChangingOriginals()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"harness-diagnostics-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var logFile = Path.Combine(directory, "harness-test.log");
            var log = JsonSerializer.Serialize(new { Message = "https://example.test/?token=url-secret", Context = new { activity = "chat-secret" } });
            await File.WriteAllTextAsync(logFile, log + "\nmalformed password=legacy-secret");
            var original = await File.ReadAllTextAsync(logFile);
            var runtime = new DiagnosticRuntimeInfo("test", "test", "test", "test", "x64", 1, "hash");
            var output = await DiagnosticBundleService.ExportBundleAsync(
                """{"gatewayUrl":"https://example.test/#settings-secret","apiKey":"settings-secret"}""",
                directory, "Target: https://example.test/?token=summary-secret", directory, runtime);

            using var archive = ZipFile.OpenRead(output);
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var contents = await reader.ReadToEndAsync();
                Assert.IsFalse(contents.Contains("secret", StringComparison.Ordinal), entry.FullName);
                Assert.IsFalse(contents.Contains("example.test", StringComparison.Ordinal), entry.FullName);
            }

            Assert.AreEqual(original, await File.ReadAllTextAsync(logFile));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
