// Copyright (c) Lanstack @openclaw. All rights reserved.

using Microsoft.Web.WebView2.Core;
using Windows.Foundation;

namespace Harness.Services;

public partial class WebViewService
{
    private double _contentZoomFactor = 1;
    private TypedEventHandler<CoreWebView2, CoreWebView2DOMContentLoadedEventArgs>? _zoomDocumentLoadedHandler;

    public void SetContentZoom(double factor)
    {
        _contentZoomFactor = double.IsFinite(factor) ? Math.Clamp(factor, 0.5, 2) : 1;
        if (GetCoreWebView() is { } core)
        {
            _ = ApplyContentZoomAsync(core);
        }
    }

    private void AttachContentZoom(CoreWebView2 core, int hostGeneration)
    {
        _zoomDocumentLoadedHandler = (sender, args) =>
        {
            if (IsCurrentHost(hostGeneration))
            {
                _ = ApplyContentZoomAsync(sender);
            }
        };
        core.DOMContentLoaded += _zoomDocumentLoadedHandler;
    }

    private async Task ApplyContentZoomAsync(CoreWebView2 core)
    {
        try
        {
            await WebViewContentZoom.ApplyAsync(core, _contentZoomFactor);
        }
        catch (Exception ex)
        {
            if (!_isDisposed)
            {
                _logger.Warning($"Page content scaling was unavailable: {ex.GetType().Name}");
            }
        }
    }
}
