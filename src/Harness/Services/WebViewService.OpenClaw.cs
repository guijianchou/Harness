// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Text.Json;
using Harness.OpenClaw;

namespace Harness.Services;

internal sealed record OpenClawPageSelection(string SessionKey, string? RunId, bool HasDraft, bool IsLegacy);

public partial class WebViewService
{
    // Read only the Control UI's selected session. Gateway credentials never cross this bridge.
    private const string OpenClawSelectionScript = """
        (() => {
          const app = document.querySelector('openclaw-app');
          if (app && (app.connected !== true || typeof app.sessionKey !== 'string')) return null;
          const editors = Array.from(document.querySelectorAll('textarea, [contenteditable="true"]'));
          if (!app && (document.readyState !== 'complete' || !editors.length)) return null;
          const hasDraft = editors.some(el => String(el.value ?? el.textContent ?? '').trim().length > 0)
            || Array.from(document.querySelectorAll('input[type="file"]')).some(el => el.files?.length > 0)
            || Boolean(app?.chatMessage?.trim?.()) || Boolean(app?.chatAttachments?.length);
          return { sessionKey: app?.sessionKey ?? '', runId: typeof app?.chatRunId === 'string' ? app.chatRunId : null,
            hasDraft, legacy: Boolean(app), href: location.href };
        })()
        """;

    internal Task<OpenClawPageSelection?> ReadOpenClawSelectionAsync(Uri configuredUiUri, CancellationToken cancellationToken, OpenClawHello? hello = null) =>
        _uiDispatcher.RunAsync(async () =>
        {
            var core = GetCoreWebView();
            var version = _messageOwnership.CaptureAcceptedPageVersion();
            if (core is null || version == 0)
            {
                return null;
            }

            try
            {
                var json = await core.ExecuteScriptAsync(OpenClawSelectionScript).AsTask(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                if (!ReferenceEquals(core, GetCoreWebView()) || !_messageOwnership.IsCurrentAcceptedPageVersion(version))
                {
                    return null;
                }

                using var document = JsonDocument.Parse(json);
                var value = document.RootElement;
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("href", out var href) ||
                    !Uri.TryCreate(href.GetString(), UriKind.Absolute, out var source) ||
                    source.GetLeftPart(UriPartial.Authority) != configuredUiUri.GetLeftPart(UriPartial.Authority))
                {
                    return null;
                }

                var legacy = value.TryGetProperty("legacy", out var old) && old.ValueKind == JsonValueKind.True;
                var selectedKey = legacy && value.TryGetProperty("sessionKey", out var key) && key.ValueKind == JsonValueKind.String ? key.GetString() : null;
                if (!legacy && OpenClawSessionRoute.TryRead(source, configuredUiUri, hello, out var routeKey))
                {
                    selectedKey = routeKey;
                }
                return new OpenClawPageSelection(selectedKey ?? "",
                    value.TryGetProperty("runId", out var run) && run.ValueKind == JsonValueKind.String ? run.GetString() : null,
                    value.TryGetProperty("hasDraft", out var draft) && draft.ValueKind == JsonValueKind.True, legacy);
            }
            catch (Exception ex) when (ex is TimeoutException or JsonException or System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                return null;
            }
        }, cancellationToken);
}
