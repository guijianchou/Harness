// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Text.Json;
using Harness.OpenClaw;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Harness.Core.Tests;

[TestClass]
public sealed class OpenClawSessionRouteTests
{
    [TestMethod]
    public void ModernLiteralRoutesRoundTripOpaqueKeysUnderUiBasePath()
    {
        var ui = new Uri("https://example.test/openclaw/?unrelated=discard#secret");
        foreach (var key in new[]
        {
            "agent:main:main", "agent:bot-a:deadbeef", "agent:main:my-session-deadbeef",
            "agent:main:release.js", "agent:main:.", "agent:main:..", "agent:main:~dot",
            "agent:main:~key", "agent:main:chat:你好/100% +?#", "agent:main:task:~dotdot:..",
        })
        {
            var route = OpenClawSessionRoute.Build(ui, key, modern: true);
            Assert.IsTrue(route.AbsolutePath.StartsWith("/openclaw/chat/", StringComparison.Ordinal), route.ToString());
            Assert.AreEqual("", route.Query);
            Assert.AreEqual("", route.Fragment);
            if (key.EndsWith("release.js", StringComparison.Ordinal))
            {
                Assert.IsTrue(route.AbsoluteUri.EndsWith("/release%2Ejs", StringComparison.Ordinal));
            }
            Assert.IsTrue(OpenClawSessionRoute.TryRead(route, ui, out var actual), route.ToString());
            Assert.AreEqual(key, actual);
        }
    }

    [TestMethod]
    public void RouteReadingRejectsDiscoveryAndUntrustedOrigins()
    {
        var ui = new Uri("https://example.test/openclaw/");
        foreach (var value in new[]
        {
            "https://evil.test/openclaw/chat/main/~key/main",
            "http://example.test/openclaw/chat/main/~key/main",
            "https://example.test:8443/openclaw/chat/main/~key/main",
            "https://example.test/other/chat/main/~key/main",
            "https://example.test/openclaw/chat/main",
            "https://example.test/openclaw/chat/main/deadbeef",
            "https://example.test/openclaw/chat/main/a-title-deadbeef",
            "https://example.test/openclaw/chat/main/some-title",
            "https://example.test/openclaw/chat/main/~key/main?session=agent%3Amain%3Aother",
            "https://example.test/openclaw/chat/main/~key/",
            "https://example.test/openclaw/chat/Main/~key/main",
            "https://example.test/openclaw/chat/main/~key/bad%3Asegment",
            "https://user:secret@example.test/openclaw/chat/main/~key/main",
        })
        {
            Assert.IsFalse(OpenClawSessionRoute.TryRead(new Uri(value), ui, out _), value);
        }
    }

    [TestMethod]
    public void LegacyQueryRequiresOneFullyQualifiedCanonicalKey()
    {
        var ui = new Uri("https://example.test/openclaw/");
        var route = OpenClawSessionRoute.Build(ui, "agent:main:task:你好 +%", modern: false);
        Assert.IsTrue(OpenClawSessionRoute.TryRead(route, ui, out var key));
        Assert.AreEqual("agent:main:task:你好 +%", key);
        foreach (var query in new[] { "session=main", "session=agent:main:", "session=agent:Main:task", "session=agent:main:a&session=agent:main:b" })
        {
            Assert.IsFalse(OpenClawSessionRoute.TryRead(new Uri(ui, "chat?" + query), ui, out _), query);
        }
    }

    [TestMethod]
    public void ConfiguredChatDeepLinkStillUsesOriginalUiBase()
    {
        var ui = new Uri("https://example.test/prefix/chat/main/old");
        var route = OpenClawSessionRoute.Build(ui, "agent:main:new:session", modern: true);
        Assert.AreEqual("/prefix/chat/main/new/session", route.AbsolutePath);
        Assert.IsTrue(OpenClawSessionRoute.TryRead(route, ui, out var key));
        Assert.AreEqual("agent:main:new:session", key);
    }

    [TestMethod]
    public void MainRoutesUseOnlyCurrentServerDefaultsAndKeepAgentOwnership()
    {
        var ui = new Uri("https://example.test/prefix/");
        var hello = new OpenClawHello
        {
            Snapshot = JsonSerializer.SerializeToElement(new
            {
                sessionDefaults = new { defaultAgentId = "research", mainKey = "workspace", mainSessionKey = "agent:research:home", scope = "per-sender" },
            }),
        };
        Assert.IsTrue(OpenClawSessionRoute.TryRead(new Uri(ui, "chat/research"), ui, hello, out var own));
        Assert.AreEqual("agent:research:home", own);
        Assert.IsTrue(OpenClawSessionRoute.TryRead(new Uri(ui, "dashboard/other"), ui, hello, out var other));
        Assert.AreEqual("agent:other:workspace", other);
        Assert.IsFalse(OpenClawSessionRoute.TryRead(new Uri(ui, "chat/research"), ui, null, out _));
        Assert.IsFalse(OpenClawSessionRoute.TryRead(new Uri("https://other.test/prefix/chat/research"), ui, hello, out _));
        Assert.IsFalse(OpenClawSessionRoute.TryRead(new Uri(ui, "chat/research/deadbeef"), ui, hello, out _));
    }

    [TestMethod]
    public void GlobalHomeRequiresExplicitOwnerAwareSupport()
    {
        var ui = new Uri("https://example.test/");
        var hello = new OpenClawHello
        {
            Snapshot = JsonSerializer.SerializeToElement(new
            {
                sessionDefaults = new { defaultAgentId = "main", mainKey = "main", mainSessionKey = "global", scope = "global" },
            }),
        };
        Assert.IsFalse(OpenClawSessionRoute.TryRead(new Uri(ui, "chat/main"), ui, hello, out _));
    }
}
