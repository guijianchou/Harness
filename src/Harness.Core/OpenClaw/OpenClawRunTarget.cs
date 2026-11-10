// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Text.Json;

namespace Harness.OpenClaw;

/// <summary>Chooses an exact active run from one authoritative history snapshot.</summary>
public static class OpenClawRunTarget
{
    public static string? Resolve(OpenClawChatHistory? history, string? preferredRunId = null,
        IReadOnlySet<string>? terminalRunIds = null)
    {
        if (string.IsNullOrWhiteSpace(history?.SessionId) || history.SessionInfo?.HasActiveRun == false ||
            history.SessionInfo?.ActiveRunIds is { Length: 0 })
        {
            return null;
        }

        if (history.SessionInfo is { } session &&
            ((!string.IsNullOrEmpty(session.SessionId) && session.SessionId != history.SessionId) ||
             (!string.IsNullOrEmpty(history.SessionKey) && !string.IsNullOrEmpty(session.Key) && session.Key != history.SessionKey)))
        {
            return null;
        }

        var candidates = new HashSet<string>(StringComparer.Ordinal);
        if (history.SessionInfo?.ActiveRunIds is { } active)
        {
            candidates.UnionWith(active.Where(run => !string.IsNullOrWhiteSpace(run)));
        }
        else if (history.InFlightRun is { ValueKind: JsonValueKind.Object } inFlight)
        {
            var run = ReadText(inFlight, "runId");
            var sessionId = ReadText(inFlight, "sessionId");
            var sessionKey = ReadText(inFlight, "sessionKey");
            if (!string.IsNullOrWhiteSpace(run) &&
                (string.IsNullOrEmpty(sessionId) || sessionId == history.SessionId) &&
                (string.IsNullOrEmpty(sessionKey) || string.IsNullOrEmpty(history.SessionKey) || sessionKey == history.SessionKey))
            {
                candidates.Add(run);
            }
        }

        if (terminalRunIds is not null)
        {
            candidates.ExceptWith(terminalRunIds);
        }

        // A stale explicit target must not silently become another current run.
        if (!string.IsNullOrWhiteSpace(preferredRunId))
        {
            return candidates.Contains(preferredRunId) ? preferredRunId : null;
        }

        return candidates.Count == 1 ? candidates.Single() : null;
    }

    private static string? ReadText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
