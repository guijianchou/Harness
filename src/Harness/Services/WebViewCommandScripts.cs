// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.Services;

internal static class WebViewCommandScripts
{
    private const string StopInjectionResourceName = "Harness.Services.WebViewCommands.StopInjection.js";
    private const string AbortRunResourceName = "Harness.Services.WebViewCommands.AbortRun.js";
    private const string ExecuteAsyncResourceName = "Harness.Services.WebViewCommands.ExecuteAsync.js";

    private static readonly Lazy<string> StopInjectionScript = new(() => Load(StopInjectionResourceName));
    private static readonly Lazy<string> AbortRunScript = new(() => Load(AbortRunResourceName));
    private static readonly Lazy<string> ExecuteAsyncScript = new(() => Load(ExecuteAsyncResourceName));

    public static string StopInjection => StopInjectionScript.Value;

    public static string AbortRun => AbortRunScript.Value
        .Replace("__HARNESS_STOP_INJECTION_SCRIPT__", StopInjection, StringComparison.Ordinal);

    public static string WrapAsync(string expression, string messageJson) => ExecuteAsyncScript.Value
        .Replace("__HARNESS_COMMAND_MESSAGE_JSON__", messageJson, StringComparison.Ordinal)
        .Replace("__HARNESS_COMMAND_EXPRESSION__", expression, StringComparison.Ordinal);

    private static string Load(string resourceName)
    {
        var assembly = typeof(WebViewCommandScripts).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded WebView command script: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
