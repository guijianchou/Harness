// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Text.Json;
using Harness.OpenClaw;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Harness.Core.Tests;

[TestClass]
public sealed class OpenClawRunTargetTests
{
    [TestMethod]
    public void IdleSnapshotsOverridePageEventsAndInFlightHints()
    {
        Assert.IsNull(OpenClawRunTarget.Resolve(History(false, ["run-1"], "run-1"), "run-1"));
        Assert.IsNull(OpenClawRunTarget.Resolve(History(true, [], "run-1"), "run-1"));
        Assert.IsNull(OpenClawRunTarget.Resolve(History(true, null), "historically-observed"));
    }

    [TestMethod]
    public void SingleAuthoritativeRunDoesNotRequirePageInternals()
    {
        Assert.AreEqual("run-1", OpenClawRunTarget.Resolve(History(true, ["run-1"])));
        Assert.AreEqual("run-1", OpenClawRunTarget.Resolve(History(null, null, "run-1")));
        Assert.AreEqual("run-1", OpenClawRunTarget.Resolve(History(true, ["run-1", "run-1"])));
    }

    [TestMethod]
    public void MultipleRunsRequireOneExactServerVerifiedPreference()
    {
        var history = History(true, ["run-1", "run-2"], "run-1");
        Assert.IsNull(OpenClawRunTarget.Resolve(history));
        Assert.AreEqual("run-2", OpenClawRunTarget.Resolve(history, "run-2"));
        Assert.IsNull(OpenClawRunTarget.Resolve(history, "unknown"));
        Assert.IsNull(OpenClawRunTarget.Resolve(History(true, ["new-run"]), "old-run"));
    }

    [TestMethod]
    public void CompleteRunListTakesPrecedenceOverConflictingInFlightHint()
    {
        var history = History(true, ["run-2"], "run-1");
        Assert.IsNull(OpenClawRunTarget.Resolve(history, "run-1"));
        Assert.AreEqual("run-2", OpenClawRunTarget.Resolve(history));
    }

    [TestMethod]
    public void MissingOrMismatchedPhysicalIdentityCannotProduceATarget()
    {
        Assert.IsNull(OpenClawRunTarget.Resolve(new OpenClawChatHistory { SessionInfo = new() { ActiveRunIds = ["run"] } }));
        Assert.IsNull(OpenClawRunTarget.Resolve(new OpenClawChatHistory
        {
            SessionId = "physical",
            SessionInfo = new() { SessionId = "replaced", ActiveRunIds = ["run"] },
        }));
        Assert.IsNull(OpenClawRunTarget.Resolve(new OpenClawChatHistory
        {
            SessionId = "physical",
            InFlightRun = JsonSerializer.SerializeToElement(new { runId = "run", sessionId = "replaced" }),
        }));
        Assert.IsNull(OpenClawRunTarget.Resolve(new OpenClawChatHistory
        {
            SessionId = "physical",
            SessionKey = "agent:main:one",
            SessionInfo = new() { Key = "agent:main:two", ActiveRunIds = ["run"] },
        }));
    }

    [TestMethod]
    public void TerminalRunCannotBeRecoveredFromAnOlderSnapshot()
    {
        var terminal = new HashSet<string>(StringComparer.Ordinal) { "run-1" };
        Assert.IsNull(OpenClawRunTarget.Resolve(History(true, ["run-1"]), terminalRunIds: terminal));
        Assert.IsNull(OpenClawRunTarget.Resolve(History(true, ["run-1"]), "run-1", terminal));
    }

    private static OpenClawChatHistory History(bool? active, string[]? runs, string? inFlight = null) => new()
    {
        SessionId = "physical",
        SessionKey = "agent:main:task",
        SessionInfo = new() { Key = "agent:main:task", HasActiveRun = active, ActiveRunIds = runs },
        InFlightRun = inFlight is null ? null : JsonSerializer.SerializeToElement(new { runId = inFlight }),
    };
}
