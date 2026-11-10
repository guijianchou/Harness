// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.Helpers;

internal static class GatewayStrings
{
    public static string Text(string key) => StringResources.Get("GatewayClient" + key);
    public static string Format(string key, params object[] values) => string.Format(Text(key), values);
}
