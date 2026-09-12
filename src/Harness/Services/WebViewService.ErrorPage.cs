// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Harness.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace Harness.Services;

public partial class WebViewService
{
    private string? _errorPageHtml;
    private string? _errorPageToken;
    private ulong _errorPageNavigationId = NoCurrentNavigationId;

    private string GetErrorPageHtml()
    {
        if (_errorPageHtml is not null)
        {
            return _errorPageHtml;
        }

        using var stream = typeof(WebViewService).Assembly.GetManifestResourceStream("Harness.Assets.ErrorPages.connection-error.html")
            ?? throw new InvalidOperationException("Missing embedded connection error page.");
        using var reader = new StreamReader(stream);
        _errorPageHtml = reader.ReadToEnd();
        return _errorPageHtml;
    }

    private void NavigateToErrorPage(string targetUrl, string errorStatus)
    {
        var coreWebView = _coreWebView;
        if (coreWebView is null)
        {
            return;
        }

        try
        {
            CancelActiveNavigation();
            _errorPageToken = Guid.NewGuid().ToString("N");
            _errorPageNavigationId = NoCurrentNavigationId;
            using var iconStream = typeof(WebViewService).Assembly.GetManifestResourceStream("Harness.Assets.ErrorPageIcon.png");
            using var iconBytes = new MemoryStream();
            iconStream?.CopyTo(iconBytes);
            var data = JsonSerializer.Serialize(new
            {
                token = _errorPageToken,
                language = CultureInfo.CurrentUICulture.Name,
                icon = $"data:image/png;base64,{Convert.ToBase64String(iconBytes.ToArray())}",
                labels = new Dictionary<string, string>
                {
                    ["error-title"] = StringResources.Get("ErrorPageTitle"),
                    ["error-description"] = StringResources.Get("ErrorPageDescription"),
                    ["retry-label"] = StringResources.Retry,
                    ["settings-label"] = StringResources.Settings,
                    ["gateway-label"] = StringResources.Get("ErrorPageGateway"),
                    ["status-label"] = StringResources.MainStatusLabel,
                    ["gateway-url"] = targetUrl,
                    ["error-status"] = errorStatus
                }
            });
            var html = GetErrorPageHtml().Replace("__HARNESS_ERROR_DATA__", data, StringComparison.Ordinal);
            _statusInspector.SetUnavailableSnapshot(errorStatus);
            coreWebView.NavigateToString(html);
            _logger.Info("navigation.error-page-shown", new { targetUrl, errorStatus });
        }
        catch (Exception ex)
        {
            ClearErrorPageOwnership();
            _logger.Warning($"Failed to navigate to error page: {ex.Message}");
        }
    }

    private void ClearErrorPageOwnership()
    {
        _errorPageToken = null;
        _errorPageNavigationId = NoCurrentNavigationId;
    }

    private bool IsCurrentErrorPageAction(CoreWebView2WebMessageReceivedEventArgs args, JsonElement root)
    {
        return _errorPageToken is not null &&
            string.Equals(args.Source, "about:blank", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_coreWebView?.Source, "about:blank", StringComparison.OrdinalIgnoreCase) &&
            root.TryGetProperty("errorPageToken", out var token) &&
            token.ValueKind == JsonValueKind.String && token.ValueEquals(_errorPageToken);
    }

    private void OnWebViewActualThemeChanged(FrameworkElement sender, object args)
    {
        try
        {
            ApplyWebViewColorScheme();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _logger.Warning($"WebView theme update skipped: {ex.Message}");
        }
    }

    private void ApplyWebViewColorScheme()
    {
        if (_coreWebView is not null && _webView is not null)
        {
            _coreWebView.Profile.PreferredColorScheme = _webView.ActualTheme == ElementTheme.Dark
                ? CoreWebView2PreferredColorScheme.Dark
                : CoreWebView2PreferredColorScheme.Light;
        }
    }
}
