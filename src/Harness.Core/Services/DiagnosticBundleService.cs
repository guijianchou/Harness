// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.IO.Compression;
using System.Reflection;

namespace Harness.Services;

public sealed record DiagnosticRuntimeInfo(
    string? WebView2RuntimeVersion,
    string OsVersion,
    string DotNetVersion,
    string AppVersion,
    string ProcessArchitecture,
    int ProcessorCount,
    string MachineHash);

/// <summary>
/// Collects diagnostic data (logs, redacted settings, runtime info) and exports as a zip bundle.
/// </summary>
public static partial class DiagnosticBundleService
{
    private static readonly TimeSpan DefaultLogRetention = TimeSpan.FromDays(7);

    /// <summary>
    /// Redacts sensitive fields from a settings JSON string.
    /// - Gateway URLs: scheme preserved, host replaced with &lt;host&gt;, path replaced with &lt;path&gt;.
    /// - Token/key/secret field values: replaced with &lt;redacted&gt;.
    /// </summary>
    public static string RedactSettingsJson(string json)
    {
        return DiagnosticRedactor.RedactJson(json, indented: true);
    }

    /// <summary>
    /// Collects runtime environment information from platform-neutral inputs.
    /// </summary>
    public static DiagnosticRuntimeInfo CollectRuntimeInfo(string? webView2RuntimeVersion)
    {
        return new DiagnosticRuntimeInfo(
            WebView2RuntimeVersion: webView2RuntimeVersion,
            OsVersion: Environment.OSVersion.ToString(),
            DotNetVersion: Environment.Version.ToString(),
            AppVersion: GetAppVersion(),
            ProcessArchitecture: System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorCount: Environment.ProcessorCount,
            MachineHash: HashMachineName(Environment.MachineName));
    }

    public static string FormatRuntimeInfo(DiagnosticRuntimeInfo runtimeInfo)
    {
        var webView2 = string.IsNullOrWhiteSpace(runtimeInfo.WebView2RuntimeVersion)
            ? "unavailable"
            : runtimeInfo.WebView2RuntimeVersion;

        return string.Join(Environment.NewLine, new[]
        {
            $"OS: {runtimeInfo.OsVersion}",
            $".NET: {runtimeInfo.DotNetVersion}",
            $"App: {runtimeInfo.AppVersion}",
            $"Architecture: {runtimeInfo.ProcessArchitecture}",
            $"Machine: {runtimeInfo.MachineHash}",
            $"Processors: {runtimeInfo.ProcessorCount}",
            $"WebView2: {webView2}",
        });
    }

    /// <summary>
    /// Collects log files from the specified directory that are within the retention window.
    /// </summary>
    public static IReadOnlyList<string> CollectRecentLogFiles(string logsDirectory, TimeSpan? retention = null)
    {
        var maxAge = retention ?? DefaultLogRetention;
        var cutoff = DateTimeOffset.UtcNow - maxAge;

        if (!Directory.Exists(logsDirectory))
        {
            return [];
        }

        return Directory.GetFiles(logsDirectory, "harness-*.log")
            .Where(f => File.GetLastWriteTimeUtc(f) >= cutoff.UtcDateTime)
            .OrderByDescending(f => f)
            .ToArray();
    }

    /// <summary>
    /// Creates a diagnostic zip bundle at the specified output path.
    /// </summary>
    public static async Task<string> ExportBundleAsync(
        string settingsJson,
        string logsDirectory,
        string diagnosticSummary,
        string outputDirectory,
        DiagnosticRuntimeInfo runtimeInfo)
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmm");
        var fileName = $"harness-diagnostics-{timestamp}.zip";
        var outputPath = Path.Combine(outputDirectory, fileName);

        Directory.CreateDirectory(outputDirectory);

        using var zipStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

        // 1. Redacted settings
        var redactedSettings = RedactSettingsJson(settingsJson);
        await AddTextEntryAsync(archive, "settings-redacted.json", redactedSettings);

        // 2. Runtime info
        await AddTextEntryAsync(archive, "runtime-info.txt", FormatRuntimeInfo(runtimeInfo));

        // 3. Diagnostic summary
        if (!string.IsNullOrWhiteSpace(diagnosticSummary))
        {
            await AddTextEntryAsync(archive, "diagnostic-summary.txt", DiagnosticRedactor.RedactText(diagnosticSummary));
        }

        // 4. Recent log files
        var logFiles = CollectRecentLogFiles(logsDirectory);
        foreach (var logFile in logFiles)
        {
            var entryName = $"logs/{Path.GetFileName(logFile)}";
            await AddRedactedLogAsync(archive, entryName, logFile);
        }

        return outputPath;
    }

    private static async Task AddTextEntryAsync(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var writer = new StreamWriter(entry.Open());
        await writer.WriteAsync(content);
    }

    private static async Task AddRedactedLogAsync(ZipArchive archive, string entryName, string logFile)
    {
        using var input = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(input);
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var writer = new StreamWriter(entry.Open());
        while (await reader.ReadLineAsync() is { } line)
        {
            await writer.WriteLineAsync(DiagnosticRedactor.RedactJson(line));
        }
    }

    private static string GetAppVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version is not null
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "unknown";
    }

    private static string HashMachineName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unknown";
        }

        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(name));
        return Convert.ToHexString(bytes[..4]).ToLowerInvariant();
    }
}
