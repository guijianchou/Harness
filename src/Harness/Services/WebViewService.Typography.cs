// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;

namespace Harness.Services;

public partial class WebViewService
{
    private const string TypographyFontPath = "/__harness__/fonts/NotoSansSC.ttf";
    private const string TypographyHttpsFontFilter = "https://*/__harness__/fonts/NotoSansSC.ttf";
    private const string TypographyHttpFontFilter = "http://*/__harness__/fonts/NotoSansSC.ttf";
    private static readonly Lazy<Task<byte[]>> TypographyFont = new(() => File.ReadAllBytesAsync(
        Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "NotoSansSC.ttf")));
    private static readonly Lazy<string> TypographyScript = new(() =>
    {
        using var stream = typeof(WebViewService).Assembly.GetManifestResourceStream("Harness.Services.WebViewTypography.js")
            ?? throw new InvalidOperationException("Missing embedded typography script.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private string _contentFontFamily = "System UI";
    private double _contentFontSize = 14;
    private long _typographyRevision;
    private Task _typographyApplyTask = Task.CompletedTask;
    private TypedEventHandler<CoreWebView2, CoreWebView2DOMContentLoadedEventArgs>? _typographyDocumentLoadedHandler;
    private TypedEventHandler<CoreWebView2, CoreWebView2WebResourceRequestedEventArgs>? _typographyFontRequestedHandler;

    public void SetContentTypography(string? family, double size)
    {
        _contentFontFamily = family == "Noto Sans SC" ? family : "System UI";
        _contentFontSize = double.IsFinite(size) && size is >= 10 and <= 32 && size == Math.Truncate(size) ? size : 14;
        if (!_isDisposed && GetCoreWebView() is { } core)
        {
            QueueContentTypography(core, _hostGeneration);
        }
    }

    private void AttachContentTypography(CoreWebView2 core, int hostGeneration)
    {
        core.AddWebResourceRequestedFilter(TypographyHttpsFontFilter, CoreWebView2WebResourceContext.Font);
        core.AddWebResourceRequestedFilter(TypographyHttpFontFilter, CoreWebView2WebResourceContext.Font);
        _typographyFontRequestedHandler = (sender, args) => OnTypographyFontRequested(sender, args, hostGeneration);
        core.WebResourceRequested += _typographyFontRequestedHandler;
        _typographyDocumentLoadedHandler = (sender, args) =>
        {
            if (args.NavigationId == _currentNavigationId)
            {
                QueueContentTypography(sender, hostGeneration);
            }
        };
        core.DOMContentLoaded += _typographyDocumentLoadedHandler;
    }

    private async void OnTypographyFontRequested(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs args, int hostGeneration)
    {
        Deferral? deferral = null;
        try
        {
            if (!IsCurrentHost(hostGeneration) || args.ResourceContext != CoreWebView2WebResourceContext.Font ||
                args.Request.Method != "GET" ||
                !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var requested) ||
                requested.AbsolutePath != TypographyFontPath || requested.Query.Length != 0 ||
                !Uri.TryCreate(core.Source, UriKind.Absolute, out var page))
            {
                return;
            }

            var sameOrigin = page.Scheme is "http" or "https" &&
                requested.GetLeftPart(UriPartial.Authority) == page.GetLeftPart(UriPartial.Authority);
            var localPageFont = page.Scheme is "data" or "about" && requested.Scheme == "https" &&
                requested.Host == "harness-fonts.invalid" && requested.Port == 443;
            if (!sameOrigin && !localPageFont)
            {
                return;
            }

            deferral = args.GetDeferral();
            // The reserved request must never fall through to the remote gateway,
            // including when the bundled font is missing or the host is retired.
            args.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");
            var font = await TypographyFont.Value;
            if (!IsCurrentHost(hostGeneration))
            {
                return;
            }

            var stream = new MemoryStream(font, writable: false).AsRandomAccessStream();
            args.Response = core.Environment.CreateWebResourceResponse(stream, 200, "OK",
                "Content-Type: font/ttf\r\nCache-Control: private, max-age=31536000\r\nX-Content-Type-Options: nosniff\r\nAccess-Control-Allow-Origin: *");
        }
        catch (Exception ex)
        {
            if (IsCurrentHost(hostGeneration))
            {
                _logger.Warning("webview.typography.font_unavailable", new { error = ex.GetType().Name });
            }
        }
        finally
        {
            try
            {
                deferral?.Complete();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // A retired browser request no longer needs its deferral.
            }
        }
    }

    private void QueueContentTypography(CoreWebView2 core, int hostGeneration)
    {
        if (!IsCurrentHost(hostGeneration))
        {
            return;
        }

        _typographyApplyTask = ApplyContentTypographyAsync(core, hostGeneration, ++_typographyRevision);
    }

    private async Task ApplyContentTypographyAsync(CoreWebView2 core, int hostGeneration, long revision)
    {
        try
        {
            var options = JsonSerializer.Serialize(new
            {
                family = _contentFontFamily,
                size = _contentFontSize,
                revision,
            });
            var script = TypographyScript.Value.Replace("__HARNESS_TYPOGRAPHY_OPTIONS__", options, StringComparison.Ordinal);
            await core.ExecuteScriptAsync(script).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            if (IsCurrentHost(hostGeneration) && revision == _typographyRevision)
            {
                _logger.Warning("webview.typography.apply_failed", new { error = ex.GetType().Name });
            }
        }
    }

    private void DetachContentTypography(CoreWebView2? core)
    {
        ++_typographyRevision;
        var handler = _typographyDocumentLoadedHandler;
        _typographyDocumentLoadedHandler = null;
        var fontHandler = _typographyFontRequestedHandler;
        _typographyFontRequestedHandler = null;
        if (core is null)
        {
            return;
        }

        try
        {
            if (handler is not null)
            {
                core.DOMContentLoaded -= handler;
            }
            if (fontHandler is not null)
            {
                core.WebResourceRequested -= fontHandler;
            }
            core.RemoveWebResourceRequestedFilter(TypographyHttpsFontFilter, CoreWebView2WebResourceContext.Font);
            core.RemoveWebResourceRequestedFilter(TypographyHttpFontFilter, CoreWebView2WebResourceContext.Font);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Closing the native host also releases its handlers and request filters.
        }
    }
}
