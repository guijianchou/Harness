// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Text.Json;

namespace Harness.OpenClaw;

public sealed partial class OpenClawGatewayClient
{
    public Task<OpenClawSessionList> ListSessionsAsync(string? agentId = null, int limit = 60, int offset = 0, CancellationToken cancellationToken = default, string? search = null) =>
        RequestAsync<OpenClawSessionList>("sessions.list", new { agentId, limit, offset, search, includeLastMessage = true, includeDerivedTitles = true }, cancellationToken: cancellationToken);

    public Task<OpenClawSessionCreated> CreateSessionAsync(string? agentId = null, string? label = null, CancellationToken cancellationToken = default) =>
        RequestAsync<OpenClawSessionCreated>("sessions.create", new { agentId, label, idempotencyKey = Guid.NewGuid().ToString("N") }, cancellationToken: cancellationToken);

    public Task<OpenClawChatHistory> GetChatHistoryAsync(string sessionKey, string? agentId = null, string? cursor = null, int limit = 100, CancellationToken cancellationToken = default) =>
        RequestAsync<OpenClawChatHistory>("chat.history", new { sessionKey, agentId, cursor, limit, maxBytes = 512 * 1024 }, cancellationToken: cancellationToken);

    public Task<OpenClawModelList> ListModelsAsync(string? agentId = null, string? sessionKey = null, CancellationToken cancellationToken = default)
    {
        // Session scoping is additive. Older v4 gateways still support the baseline catalog request.
        var scoped = Hello?.Features.Capabilities.Contains("session-scoped-model-catalog", StringComparer.Ordinal) == true;
        return RequestAsync<OpenClawModelList>("models.list", new { agentId, sessionKey = scoped ? sessionKey : null, view = "configured" }, cancellationToken: cancellationToken);
    }

    /// <summary>Dictionary preserves explicit null clears; omitted keys remain unchanged.</summary>
    public Task<JsonElement> PatchSessionAsync(string key, string? agentId, string? expectedSessionId,
        IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = key };
        if (agentId is not null)
        {
            parameters["agentId"] = agentId;
        }

        if (expectedSessionId is not null)
        {
            parameters["expectedSessionId"] = expectedSessionId;
        }

        foreach (var (field, value) in changes)
        {
            if (field is not ("model" or "thinkingLevel" or "label"))
            {
                throw new ArgumentException("Unsupported session patch field.", nameof(changes));
            }

            parameters[field] = value;
        }
        return RequestAsync<JsonElement>("sessions.patch", parameters, cancellationToken: cancellationToken);
    }

    public Task<OpenClawAbortResult> AbortChatAsync(string sessionKey, string runId, string sessionId, string? agentId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionKey) || string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Precise Stop requires session key, session ID, and run ID.");
        }

        return RequestAsync<OpenClawAbortResult>("chat.abort", new { sessionKey, agentId, sessionId, runId, userInitiated = true }, cancellationToken: cancellationToken);
    }
}
