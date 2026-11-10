// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.Models;

public static class OpenClawEndpoint
{
    public static Uri Resolve(string gatewayUrl, string? webSocketUrl)
    {
        var input = string.IsNullOrWhiteSpace(webSocketUrl) ? gatewayUrl : webSocketUrl;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var source) ||
            source.Scheme is not ("https" or "http" or "wss" or "ws") ||
            !string.IsNullOrEmpty(source.UserInfo))
        {
            throw new UriFormatException("A Gateway HTTP(S) or WebSocket endpoint without embedded credentials is required.");
        }

        var builder = new UriBuilder(source)
        {
            Scheme = source.Scheme switch { "https" => "wss", "http" => "ws", _ => source.Scheme },
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    public static string Canonical(EnvironmentConfig environment) =>
        Resolve(environment.GatewayUrl, environment.OpenClawConnection?.WebSocketUrl).AbsoluteUri;
}
