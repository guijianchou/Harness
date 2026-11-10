// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Text.Json.Serialization;

namespace Harness.Models;

public enum OpenClawAuthMode
{
    Token,
    Password,
    ExistingProxy,
}

/// <summary>Persisted connection preferences. Secrets are stored by the Windows app.</summary>
public sealed record OpenClawConnectionOptions
{
    public bool Enabled { get; set; }

    public string? WebSocketUrl { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<OpenClawAuthMode>))]
    public OpenClawAuthMode AuthMode { get; set; } = OpenClawAuthMode.Token;

    public string? CredentialReference { get; set; }
}
