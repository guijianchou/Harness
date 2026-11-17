// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Globalization;
using Microsoft.Web.WebView2.Core;

namespace Harness.Services;

internal static class WebViewContentZoom
{
    // WinUI's WebView2 does not expose the controller's browser zoom API. CSS zoom
    // scales page content with layout reflow; it does not scale the native shell.
    public static async Task ApplyAsync(CoreWebView2 core, double factor)
    {
        factor = double.IsFinite(factor) ? Math.Clamp(factor, 0.5, 2) : 1;
        var script = """
            (() => {
              const root = document.documentElement;
              if (!root) return;
              const factor = __FACTOR__;
              const key = '__harnessContentZoomOriginal';
              if (factor === 1) {
                if (Object.prototype.hasOwnProperty.call(root, key)) {
                  const previous = root[key];
                  if (previous.value) root.style.setProperty('zoom', previous.value, previous.priority);
                  else root.style.removeProperty('zoom');
                  delete root[key];
                }
                return;
              }
              if (!Object.prototype.hasOwnProperty.call(root, key)) {
                root[key] = { value: root.style.getPropertyValue('zoom'), priority: root.style.getPropertyPriority('zoom') };
              }
              root.style.setProperty('zoom', String(factor), 'important');
            })();
            """;
        await core.ExecuteScriptAsync(script.Replace("__FACTOR__", factor.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }
}
