// Copyright (c) Lanstack @openclaw. All rights reserved.
// URL escaping and literal route grammar adapted from OpenClaw Foundation (MIT),
// commit 7ecb9cec67740e1afedc0a8e20542c6cfa456358, packages/session-url-contract.
// The upstream license is reproduced in OpenClawSessionRoute.LICENSE.txt.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Harness.OpenClaw;

/// <summary>Only reads exact session identities; discovery slugs and short IDs need Gateway resolution.</summary>
public static partial class OpenClawSessionRoute
{
    public static bool TryRead(Uri pageUri, Uri configuredUiUri, OpenClawHello? hello, out string key)
    {
        if (TryRead(pageUri, configuredUiUri, out key))
        {
            return true;
        }

        if (!IsUiUri(pageUri) || !IsUiUri(configuredUiUri) || pageUri.Query.Length != 0 ||
            !pageUri.Scheme.Equals(configuredUiUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !pageUri.IdnHost.Equals(configuredUiUri.IdnHost, StringComparison.OrdinalIgnoreCase) || pageUri.Port != configuredUiUri.Port ||
            hello?.Snapshot is not { ValueKind: JsonValueKind.Object } snapshot ||
            !snapshot.TryGetProperty("sessionDefaults", out var defaults) || defaults.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var mainKey = ReadText(defaults, "mainKey")?.Trim().ToLowerInvariant();
        var defaultAgent = ReadText(defaults, "defaultAgentId");
        var advertised = ReadText(defaults, "mainSessionKey");
        if (string.IsNullOrWhiteSpace(mainKey) || ReadText(defaults, "scope") == "global" || advertised == "global")
        {
            return false;
        }

        var basePath = BasePath(configuredUiUri);
        var path = pageUri.AbsolutePath.TrimEnd('/');
        var prefix = new[] { basePath + "/chat/", basePath + "/dashboard/" }
            .FirstOrDefault(candidate => path.StartsWith(candidate, StringComparison.Ordinal));
        var agent = prefix is null ? "" : path[prefix.Length..];
        if (!CanonicalAgent().IsMatch(agent))
        {
            return false;
        }

        var target = "agent:" + agent + ":" + mainKey;
        if (agent == defaultAgent && TrySplitKey(advertised ?? "", out var advertisedAgent, out _) && advertisedAgent == agent)
        {
            target = advertised!;
        }

        if (!TrySplitKey(target, out _, out _))
        {
            return false;
        }

        key = target;
        return true;
    }

    public static Uri Build(Uri configuredUiUri, string key, bool modern)
    {
        if (!IsUiUri(configuredUiUri) || !TrySplitKey(key, out var agent, out var rest))
        {
            throw new ArgumentException("An HTTP UI origin and a canonical agent-qualified session key are required.");
        }

        var builder = new UriBuilder(configuredUiUri) { Query = "", Fragment = "" };
        var basePath = BasePath(configuredUiUri);
        if (!modern)
        {
            builder.Path = basePath + "/chat";
            builder.Query = "session=" + Uri.EscapeDataString(key);
            return builder.Uri;
        }

        // Force all single-segment keys to literal mode. A normal single segment may
        // resolve by display-name slug, and /chat/{agent} depends on configured mainKey.
        var path = basePath + "/chat/" + agent + "/" +
            (rest.Length == 1 ? "~key/" : "") + string.Join('/', rest.Select(EncodeSegment));
        // Normal Uri canonicalization decodes %2E and makes keys such as release.js
        // look like static asset requests to the upstream SPA host.
        return new Uri(builder.Uri.GetLeftPart(UriPartial.Authority) + path,
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
    }

    public static bool TryRead(Uri pageUri, Uri configuredUiUri, out string key)
    {
        key = "";
        if (!IsUiUri(pageUri) || !IsUiUri(configuredUiUri) ||
            !pageUri.Scheme.Equals(configuredUiUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !pageUri.IdnHost.Equals(configuredUiUri.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            pageUri.Port != configuredUiUri.Port)
        {
            return false;
        }

        var basePath = BasePath(configuredUiUri);
        var path = pageUri.AbsolutePath.TrimEnd('/');
        var isLegacyPath = path == basePath || path == basePath + "/chat" || path == basePath + "/dashboard";
        if (isLegacyPath)
        {
            string? candidate = null;
            foreach (var part in pageUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = part.Split('=', 2);
                if (Uri.UnescapeDataString(fields[0].Replace('+', ' ')) != "session")
                {
                    continue;
                }

                if (candidate is not null || fields.Length != 2)
                {
                    return false;
                }

                candidate = Uri.UnescapeDataString(fields[1].Replace('+', ' '));
            }

            if (candidate is null || !TrySplitKey(candidate, out _, out _))
            {
                return false;
            }

            key = candidate;
            return true;
        }

        // Catalog/search navigation may override the route. Do not infer identity
        // from an exact-looking pathname carrying an unrecognized query contract.
        if (pageUri.Query.Length != 0)
        {
            return false;
        }

        var prefix = new[] { basePath + "/chat/", basePath + "/dashboard/" }
            .FirstOrDefault(candidate => path.StartsWith(candidate, StringComparison.Ordinal));
        if (prefix is null)
        {
            return false;
        }

        var segments = path[prefix.Length..].Split('/');
        if (segments.Length < 2 || !CanonicalAgent().IsMatch(segments[0]))
        {
            return false;
        }

        var forceLiteral = segments[1] == "~key";
        var rest = segments.Skip(forceLiteral ? 2 : 1).Select(DecodeSegment).ToArray();
        if (rest.Length == 0 || rest.Any(segment => string.IsNullOrEmpty(segment) || segment.Contains(':') || segment.Any(char.IsControl)))
        {
            return false;
        }

        // Plain non-reserved single segments can be slugs or short references.
        if (!forceLiteral && rest.Length == 1 && rest[0] is not ("main" or "global" or "boot" or "sessions"))
        {
            return false;
        }

        var parsed = "agent:" + segments[0] + ":" + string.Join(':', rest);
        if (!TrySplitKey(parsed, out _, out _))
        {
            return false;
        }

        key = parsed;
        return true;
    }

    private static bool IsUiUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0;

    private static string BasePath(Uri uri)
    {
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var route = Array.FindIndex(segments, segment => segment is "chat" or "dashboard");
        var baseSegments = route >= 0 ? segments.Take(route) : segments;
        var path = string.Join('/', baseSegments);
        return path.Length == 0 ? "" : "/" + path;
    }

    private static bool TrySplitKey(string key, out string agent, out string[] rest)
    {
        agent = "";
        rest = [];
        if (string.IsNullOrEmpty(key) || key != key.Trim() || !key.StartsWith("agent:", StringComparison.Ordinal) || key.Any(char.IsControl))
        {
            return false;
        }

        var parts = key.Split(':');
        if (parts.Length < 3 || !CanonicalAgent().IsMatch(parts[1]) || parts.Skip(2).Any(string.IsNullOrEmpty))
        {
            return false;
        }

        agent = parts[1];
        rest = parts[2..];
        return true;
    }

    private static string EncodeSegment(string segment)
    {
        if (segment is "." or "..")
        {
            return segment.Length == 1 ? "~dot" : "~dotdot";
        }

        var encoded = Uri.EscapeDataString(segment).Replace(".", "%2E", StringComparison.Ordinal);
        return encoded.StartsWith('~') ? "~" + encoded : encoded;
    }

    private static string DecodeSegment(string segment) => segment switch
    {
        "~dot" => ".",
        "~dotdot" => "..",
        _ => Uri.UnescapeDataString(segment.StartsWith("~~", StringComparison.Ordinal) ? segment[1..] : segment),
    };

    private static string? ReadText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalAgent();
}
