// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.OpenClaw;

public sealed class OpenClawHello
{
    public string Type { get; init; } = "";
    public int Protocol { get; init; }
    public OpenClawServer Server { get; init; } = new();
    public OpenClawFeatures Features { get; init; } = new();
    public OpenClawAuth Auth { get; init; } = new();
    public OpenClawPolicy Policy { get; init; } = new();
    public JsonElement Snapshot { get; init; }
    public string? ControlUiUrl { get; init; }
}

public sealed class OpenClawServer
{
    public string Version { get; init; } = "";
    public string ConnId { get; init; } = "";
    public string? Commit { get; init; }
}

public sealed class OpenClawFeatures
{
    public string[] Methods { get; init; } = [];
    public string[] Events { get; init; } = [];
    public string[] Capabilities { get; init; } = [];
}

public sealed class OpenClawAuth
{
    public string Role { get; init; } = "";
    public string[] Scopes { get; init; } = [];
    public string? DeviceToken { get; init; }
    public override string ToString() => nameof(OpenClawAuth);
}

public sealed class OpenClawPolicy
{
    public int MaxPayload { get; init; }
    public int MaxBufferedBytes { get; init; }
    public int TickIntervalMs { get; init; }
}

public sealed record OpenClawGatewayEvent(long Generation, string Name, JsonElement Payload,
    long? Sequence, JsonElement? StateVersion);

public sealed class OpenClawSessionList
{
    public List<OpenClawSession> Sessions { get; init; } = [];
    public int? Count { get; init; }
    public int? TotalCount { get; init; }
    public int? NextOffset { get; init; }
    public bool? HasMore { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Additional { get; init; }
}

public sealed class OpenClawSession
{
    private string _key = "";
    internal bool IsKeySpecified { get; private set; }
    public string Key
    {
        get => _key;
        init { _key = value; IsKeySpecified = true; }
    }
    public string? AgentId { get; init; }
    public string? SessionId { get; init; }
    public string? Label { get; init; }
    public string? DisplayName { get; init; }
    public string? DerivedTitle { get; init; }
    public string? LastMessagePreview { get; init; }
    public string? Model { get; init; }
    public string? ModelProvider { get; init; }
    public string? ActiveModel { get; init; }
    public string? ActiveModelProvider { get; init; }
    public string? ThinkingLevel { get; init; }
    public string? ThinkingDefault { get; init; }
    public bool? HasActiveRun { get; init; }
    public string[]? ActiveRunIds { get; init; }
    public long? UpdatedAt { get; init; }
    public long? SnapshotAt { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Additional { get; init; }
}

public sealed class OpenClawSessionCreated
{
    public bool Ok { get; init; }
    public string Key { get; init; } = "";
    public string? SessionId { get; init; }
    public JsonElement? Entry { get; init; }
    public bool? RunStarted { get; init; }
    public string? RunId { get; init; }
}

public sealed class OpenClawChatHistory
{
    public string? SessionKey { get; init; }
    public string? SessionId { get; init; }
    public string? Kind { get; init; }
    public List<JsonElement> Messages { get; init; } = [];
    public OpenClawSession? SessionInfo { get; init; }
    public JsonElement? InFlightRun { get; init; }
    public string? OlderCursor { get; init; }
    public string? NewerCursor { get; init; }
    public string? DeltaCursor { get; init; }
    public int? NextOffset { get; init; }
    public bool? HasMore { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Additional { get; init; }
}

public sealed class OpenClawModelList
{
    public List<OpenClawModel> Models { get; init; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Additional { get; init; }
}

public sealed class OpenClawModel
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    public string? Provider { get; init; }
    public bool? Available { get; init; }
    public bool? ManualSelectionAllowed { get; init; }
    public string? UnavailableReason { get; init; }
    public bool? Reasoning { get; init; }
    public List<OpenClawThinkingOption>? ThinkingLevels { get; init; }
    public string? ThinkingDefault { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Additional { get; init; }
}

public sealed class OpenClawThinkingOption
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
}

public sealed class OpenClawAbortResult
{
    public bool? Aborted { get; init; }
    public string[]? RunIds { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Additional { get; init; }
}
