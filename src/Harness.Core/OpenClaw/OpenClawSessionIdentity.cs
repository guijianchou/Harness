// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.OpenClaw;

/// <summary>Rejects explicit identity conflicts without inventing missing optional server fields.</summary>
public static class OpenClawSessionIdentity
{
    public static bool Matches(string requestKey, OpenClawChatHistory? history)
    {
        if (history is null || !TryGetAgentId(requestKey, out var agentId))
        {
            return false;
        }

        var session = history.SessionInfo;
        return (history.SessionKey is null || history.SessionKey == requestKey) &&
            (session?.IsKeySpecified != true || session.Key == requestKey) &&
            (session?.AgentId is null || session.AgentId == agentId) &&
            (history.SessionId is null || session?.SessionId is null || history.SessionId == session.SessionId);
    }

    public static bool TryGetAgentId(string? key, out string agentId)
    {
        agentId = "";
        if (string.IsNullOrEmpty(key) || key != key.Trim() || key.Any(char.IsControl))
        {
            return false;
        }

        var parts = key.Split(':');
        if (parts.Length < 3 || parts[0] != "agent" || parts.Skip(2).Any(string.IsNullOrEmpty))
        {
            return false;
        }

        var agent = parts[1];
        if (agent.Length is < 1 or > 64 || !(char.IsAsciiLetterLower(agent[0]) || char.IsAsciiDigit(agent[0])) ||
            agent.Any(ch => !(char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch is '_' or '-')))
        {
            return false;
        }

        agentId = agent;
        return true;
    }
}
