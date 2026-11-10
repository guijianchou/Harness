// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Text.Json;
using Harness.OpenClaw;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Harness.Core.Tests;

[TestClass]
public sealed class OpenClawSessionIdentityTests
{
    private const string Key = "agent:research:workspace";

    [TestMethod]
    public void ExactQualifiedIdentityAcceptsMatchingServerFields()
    {
        Assert.IsTrue(OpenClawSessionIdentity.Matches(Key, new()
        {
            SessionKey = Key,
            SessionId = "physical",
            SessionInfo = new() { Key = Key, AgentId = "research", SessionId = "physical" },
        }));
    }

    [TestMethod]
    public void MissingOptionalFieldsAreNotInventedOrTreatedAsConflicts()
    {
        Assert.IsTrue(OpenClawSessionIdentity.Matches(Key, new()));
        Assert.IsTrue(OpenClawSessionIdentity.Matches(Key, new() { SessionId = "physical" }));
        Assert.IsTrue(OpenClawSessionIdentity.Matches(Key, new() { SessionInfo = new() { AgentId = "research" } }));
        Assert.IsTrue(OpenClawSessionIdentity.Matches(Key, new() { SessionInfo = new() { SessionId = "physical" } }));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.IsTrue(OpenClawSessionIdentity.Matches(Key,
            JsonSerializer.Deserialize<OpenClawChatHistory>("{\"sessionInfo\":{}}", options)));
        Assert.IsFalse(OpenClawSessionIdentity.Matches(Key,
            JsonSerializer.Deserialize<OpenClawChatHistory>("{\"sessionInfo\":{\"key\":\"\"}}", options)));
    }

    [TestMethod]
    public void AliasesAndGlobalKeysCannotMasqueradeAsExactRequests()
    {
        foreach (var request in new[] { "main", "global", "agent:Research:workspace", "agent:research:", "AGENT:research:workspace" })
        {
            Assert.IsFalse(OpenClawSessionIdentity.Matches(request, new()), request);
        }

        foreach (var response in new[] { "global", "main", "agent:research:main", "agent:other:workspace" })
        {
            Assert.IsFalse(OpenClawSessionIdentity.Matches(Key, new() { SessionKey = response }), response);
            Assert.IsFalse(OpenClawSessionIdentity.Matches(Key, new() { SessionInfo = new() { Key = response } }), response);
        }
    }

    [TestMethod]
    public void ExplicitAgentOrPhysicalSessionConflictsAreRejected()
    {
        Assert.IsFalse(OpenClawSessionIdentity.Matches(Key, new() { SessionInfo = new() { AgentId = "other" } }));
        Assert.IsFalse(OpenClawSessionIdentity.Matches(Key, new() { SessionInfo = new() { AgentId = "Research" } }));
        Assert.IsFalse(OpenClawSessionIdentity.Matches(Key, new()
        {
            SessionId = "physical-before-reset",
            SessionInfo = new() { SessionId = "physical-after-reset" },
        }));
        Assert.IsFalse(OpenClawSessionIdentity.Matches(Key, new() { SessionKey = "" }));
        Assert.IsFalse(OpenClawSessionIdentity.Matches(Key, new() { SessionInfo = new() { Key = "" } }));
        Assert.IsFalse(OpenClawSessionIdentity.Matches(Key, null));
    }
}
