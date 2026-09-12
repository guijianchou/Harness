// Copyright (c) Lanstack @openclaw. All rights reserved.

using Microsoft.Web.WebView2.Core;
using Windows.Foundation;

namespace Harness.Services;

public partial class WebViewService
{
    private TypedEventHandler<CoreWebView2, CoreWebView2WebMessageReceivedEventArgs> CreateWebMessageReceivedHandler(int hostGeneration)
    {
        return (sender, args) => OnWebMessageReceived(sender, args, hostGeneration);
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args, int hostGeneration)
    {
        try
        {
            if (!IsCurrentHost(hostGeneration))
            {
                return;
            }

            var message = args.WebMessageAsJson;
            using var document = System.Text.Json.JsonDocument.Parse(message);
            var root = document.RootElement;

            // Handle error page actions
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                root.TryGetProperty("type", out var typeElement) &&
                typeElement.ValueKind == System.Text.Json.JsonValueKind.String &&
                typeElement.GetString() == "harness-error-page-action" &&
                IsCurrentErrorPageAction(args, root) &&
                root.TryGetProperty("action", out var actionElement) &&
                actionElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var action = actionElement.GetString();
                if (action == "retry")
                {
                    _logger.Info("error-page.retry-requested");
                    RetryNavigation();
                }
                else if (action == "settings")
                {
                    _logger.Info("error-page.settings-requested");
                    ErrorPageSettingsRequested?.Invoke();
                }
                return;
            }

            if (!_messageOwnership.TryCaptureCurrentVersion(args, root, out var pageVersion))
            {
                return;
            }

            if (!_statusInspector.TryApplyHostMessage(message, pageVersion, out var snapshot))
            {
                return;
            }

            if (!_messageOwnership.IsCurrentAcceptedPageVersion(pageVersion))
            {
                return;
            }

            ApplyControlUiSnapshot(snapshot, raiseIssueEvent: true);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to process Control UI status message: {ex.Message}");
        }
    }
}
