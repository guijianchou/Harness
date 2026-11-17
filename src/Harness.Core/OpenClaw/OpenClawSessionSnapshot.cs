// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.OpenClaw;

/// <summary>Orders read observations without treating timestamps as event or identity fences.</summary>
public static class OpenClawSessionSnapshot
{
    public static bool CanReplace(OpenClawChatHistory? current, OpenClawChatHistory incoming)
    {
        if (current is null || (current.SessionKey is not null && incoming.SessionKey is not null &&
            !string.Equals(current.SessionKey, incoming.SessionKey, StringComparison.Ordinal)))
        {
            return true;
        }

        // Gateways can return cached rows. Missing sampling metadata retains request-order behavior.
        return current.SessionInfo?.SnapshotAt is not { } previous ||
            incoming.SessionInfo?.SnapshotAt is not { } next || next >= previous;
    }

    public static bool CanAcknowledgeStop(OpenClawChatHistory? current, string? currentKey,
        string selectedKey, string selectedSessionId, string selectedRunId,
        string? currentRunId, bool terminalObserved)
    {
        return !terminalObserved && currentKey == selectedKey && OpenClawSessionIdentity.Matches(selectedKey, current) &&
            current?.SessionId == selectedSessionId && currentRunId == selectedRunId;
    }
}
