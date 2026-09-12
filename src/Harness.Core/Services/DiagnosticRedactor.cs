// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Harness.Services;

public static partial class DiagnosticRedactor
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public static string RedactJson(string json, bool indented = false)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        try
        {
            return RedactNode(JsonNode.Parse(json))?.ToJsonString(indented ? IndentedJson : null) ?? "null";
        }
        catch (JsonException)
        {
            // Malformed legacy entries cannot be safely inspected for escaped credentials.
            return "\"<unstructured diagnostic entry omitted>\"";
        }
    }

    public static string RedactText(string text)
    {
        var redacted = UrlPattern().Replace(text, match => $"{match.Groups[1].Value}://<host>/<path>");
        redacted = AuthorizationPattern().Replace(redacted, "$1 <redacted>");
        return CredentialPattern().Replace(redacted, "$1<redacted>");
    }

    private static JsonNode? RedactNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var name in obj.Select(property => property.Key).ToArray())
            {
                obj[name] = IsSensitiveField(name) ? JsonValue.Create("<redacted>") : RedactNode(obj[name]);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                array[index] = RedactNode(array[index]);
            }
        }
        else if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return JsonValue.Create(RedactText(text));
        }

        return node;
    }

    private static bool IsSensitiveField(string name) =>
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("key", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("activity", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("activitySignature", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(https?|wss?)://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"\b(Bearer|Basic)\s+[A-Za-z0-9+/=_\-.]+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex(@"(\b[\w-]*(?:token|secret|password|credential|api[_-]?key)\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s;,}]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialPattern();
}
