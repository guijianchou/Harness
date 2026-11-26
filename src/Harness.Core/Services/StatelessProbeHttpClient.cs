// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.Services;

/// <summary>Creates HTTP probes that do not carry authentication state between environments.</summary>
public static class StatelessProbeHttpClient
{
    public static HttpClient Create(TimeSpan timeout) => new(new HttpClientHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
    })
    {
        Timeout = timeout,
    };
}
