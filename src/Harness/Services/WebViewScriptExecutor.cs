// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;

namespace Harness.Services;

internal static class WebViewScriptExecutor
{
    public static async Task<bool> ExecuteBooleanAsync(
        CoreWebView2 coreWebView,
        WebViewMessageOwnership ownership,
        int pageVersion,
        string expression,
        CancellationToken cancellationToken,
        TimeSpan? executionTimeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ownership.TryCaptureTokens(pageVersion, out var ownerToken, out var pageToken))
        {
            return false;
        }

        var commandId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TypedEventHandler<CoreWebView2, CoreWebView2WebMessageReceivedEventArgs> handler = (_, args) =>
        {
            try
            {
                using var document = JsonDocument.Parse(args.WebMessageAsJson);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("kind", out var kind) && kind.ValueEquals("harness-command-result") &&
                    root.TryGetProperty("commandId", out var id) && id.ValueEquals(commandId) &&
                    ownership.TryCaptureCurrentVersion(args, root, out var version) && version == pageVersion &&
                    root.TryGetProperty("handled", out var handled) && handled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    completion.TrySetResult(handled.GetBoolean());
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or COMException)
            {
                // Unrelated or malformed page messages cannot complete this command.
            }
        };

        var message = JsonSerializer.Serialize(new
        {
            kind = "harness-command-result",
            commandId,
            nativeOwnerToken = ownerToken,
            nativePageToken = pageToken,
            deadlineUnixMs = DateTimeOffset.UtcNow.Add(executionTimeout ?? TimeSpan.FromSeconds(3)).ToUnixTimeMilliseconds()
        });
        coreWebView.WebMessageReceived += handler;
        try
        {
            // ExecuteScriptAsync serializes a Promise; its eventual value arrives through the owned reply.
            await coreWebView.ExecuteScriptAsync(WebViewCommandScripts.WrapAsync(expression, message)).AsTask(cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            try
            {
                coreWebView.WebMessageReceived -= handler;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // A closed WebView has already retired its native event subscriptions.
            }
        }
    }
}
