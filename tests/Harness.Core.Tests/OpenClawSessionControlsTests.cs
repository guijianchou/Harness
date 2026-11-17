// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Text.Json;
using Harness.OpenClaw;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Harness.Core.Tests;

[TestClass]
public sealed class OpenClawSessionControlsTests
{
    [TestMethod]
    public void CachedHistoryCannotReplaceNewerSampleEvenWhenRequestedLater()
    {
        var current = History("physical-a", 200);
        Assert.IsFalse(OpenClawSessionSnapshot.CanReplace(current, History("physical-a", 199)));
        Assert.IsFalse(OpenClawSessionSnapshot.CanReplace(current, History("old-physical", 199)));
        Assert.IsTrue(OpenClawSessionSnapshot.CanReplace(current, History("physical-a", 200)));
        Assert.IsTrue(OpenClawSessionSnapshot.CanReplace(current, History("replacement", 201)));
    }

    [TestMethod]
    public void MissingSamplingMetadataKeepsRequestOrderCompatibility()
    {
        Assert.IsTrue(OpenClawSessionSnapshot.CanReplace(History("a", null), History("a", 1)));
        Assert.IsTrue(OpenClawSessionSnapshot.CanReplace(History("a", 10), History("a", null)));
    }

    [TestMethod]
    public void LateStopAcknowledgementCannotOverwriteTerminalOrReplacementRun()
    {
        var history = History("physical-a", 200);
        Assert.IsTrue(OpenClawSessionSnapshot.CanAcknowledgeStop(history, "agent:main:task", "agent:main:task", "physical-a", "run-a", "run-a", false));
        Assert.IsFalse(OpenClawSessionSnapshot.CanAcknowledgeStop(history, "agent:main:task", "agent:main:task", "physical-a", "run-a", "run-a", true));
        Assert.IsFalse(OpenClawSessionSnapshot.CanAcknowledgeStop(history, "agent:main:task", "agent:main:task", "physical-a", "run-a", "run-b", false));
        Assert.IsFalse(OpenClawSessionSnapshot.CanAcknowledgeStop(History("physical-b", 201), "agent:main:task", "agent:main:task", "physical-a", "run-a", "run-a", false));
        Assert.IsFalse(OpenClawSessionSnapshot.CanAcknowledgeStop(history, "agent:main:other", "agent:main:task", "physical-a", "run-a", "run-a", false));
        Assert.IsFalse(OpenClawSessionSnapshot.CanAcknowledgeStop(null, "agent:main:task", "agent:main:task", "physical-a", "run-a", "run-a", false));
    }

    [TestMethod]
    public void StopAcknowledgementToleratesOmittedOptionalKeyButRejectsConflict()
    {
        var history = new OpenClawChatHistory { SessionId = "physical-a" };
        Assert.IsTrue(OpenClawSessionSnapshot.CanAcknowledgeStop(history, "agent:main:task", "agent:main:task", "physical-a", "run-a", "run-a", false));
        history = new OpenClawChatHistory { SessionKey = "agent:other:task", SessionId = "physical-a" };
        Assert.IsFalse(OpenClawSessionSnapshot.CanAcknowledgeStop(history, "agent:main:task", "agent:main:task", "physical-a", "run-a", "run-a", false));
    }

    [TestMethod]
    public void ReasoningOptionsComeOnlyFromGatewayAndPreserveFutureIds()
    {
        var model = new OpenClawModel
        {
            Id = "model",
            ThinkingLevels = [new() { Id = "future-depth", Label = "Future depth" }, new() { Id = "", Label = "Invalid" }, new() { Id = "future-depth", Label = "Duplicate" }],
        };
        var options = OpenClawModelSelection.ThinkingOptions(model);
        Assert.AreEqual(1, options.Count);
        Assert.AreEqual("Future depth", options[0].Label);
        var patch = OpenClawModelSelection.BuildPatch("provider/model", model, "future-depth");
        Assert.AreEqual("provider/model", patch["model"]);
        Assert.AreEqual("future-depth", patch["thinkingLevel"]);
    }

    [TestMethod]
    public void ChangingModelClearsUnadvertisedReasoningInsteadOfGuessing()
    {
        var model = new OpenClawModel { Id = "other", ThinkingLevels = [new() { Id = "supported", Label = "Supported" }] };
        var patch = OpenClawModelSelection.BuildPatch("provider/other", model, "previous-only");
        Assert.IsNull(patch["thinkingLevel"]);
        Assert.IsNull(OpenClawModelSelection.BuildPatch("provider/other", null, "high")["thinkingLevel"]);
        Assert.AreEqual(0, OpenClawModelSelection.ThinkingOptions(new OpenClawModel { Reasoning = true }).Count);
    }

    [TestMethod]
    public void DefaultModelAndReasoningUseExplicitNullClears()
    {
        var patch = OpenClawModelSelection.BuildPatch("", null, "");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(patch));
        Assert.AreEqual(JsonValueKind.Null, json.RootElement.GetProperty("model").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, json.RootElement.GetProperty("thinkingLevel").ValueKind);
        Assert.IsFalse(json.RootElement.TryGetProperty("label", out _));
        var previousModel = new OpenClawModel { ThinkingLevels = [new() { Id = "supported", Label = "Supported" }] };
        Assert.IsNull(OpenClawModelSelection.BuildPatch("", previousModel, "supported")["thinkingLevel"]);
    }

    [TestMethod]
    public void SessionMetadataRetainsSamplingAndTokenFreshness()
    {
        var session = JsonSerializer.Deserialize<OpenClawSession>("""
            {"key":"agent:main:task","sessionId":"physical-a","label":"Custom name","updatedAt":123,"snapshotAt":456,"totalTokens":1200,"totalTokensFresh":false,"contextTokens":64000}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.AreEqual("Custom name", session.Label);
        Assert.AreEqual(456L, session.SnapshotAt);
        Assert.AreEqual(1200d, session.TotalTokens);
        Assert.IsFalse(session.TotalTokensFresh);
        Assert.AreEqual(64000d, session.ContextTokens);
    }

    private static OpenClawChatHistory History(string sessionId, long? sample) => new()
    {
        SessionKey = "agent:main:task",
        SessionId = sessionId,
        SessionInfo = new OpenClawSession { Key = "agent:main:task", SessionId = sessionId, SnapshotAt = sample },
    };
}
