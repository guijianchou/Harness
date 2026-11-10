// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.OpenClaw;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Harness.Core.Tests;

[TestClass]
public sealed class OpenClawGatewayClientTests
{
    [TestMethod]
    public async Task NativeSocketHandshakeUsesChallengeTimeAndLegalIdentity()
    {
        await using var server = new FakeGateway();
        await using var client = new OpenClawGatewayClient();
        var signer = new TestIdentity();
        var serverTask = server.AcceptHelloAsync();
        var hello = await client.ConnectAsync(server.Options(signer));
        var connect = await serverTask;
        var parameters = connect.GetProperty("params");
        Assert.AreEqual(4, parameters.GetProperty("minProtocol").GetInt32());
        Assert.AreEqual("gateway-client", parameters.GetProperty("client").GetProperty("id").GetString());
        Assert.AreEqual("ui", parameters.GetProperty("client").GetProperty("mode").GetString());
        Assert.AreEqual(123456L, parameters.GetProperty("device").GetProperty("signedAt").GetInt64());
        Assert.AreEqual("v3|device|gateway-client|ui|operator|operator.read,operator.write|123456|token-for-test|nonce|windows|desktop", signer.Payload);
        Assert.AreEqual(4, hello.Protocol);
        Assert.IsTrue(client.IsConnected);
    }

    [TestMethod]
    public async Task DeviceTokenUsesSeparateAuthFieldAndOnlyImplementedCapabilitiesAreAdvertised()
    {
        await using var server = new FakeGateway();
        await using var client = new OpenClawGatewayClient();
        var signer = new TestIdentity();
        var accept = server.AcceptHelloAsync();
        await client.ConnectAsync(new OpenClawConnectionOptions
        {
            Endpoint = server.Options().Endpoint,
            DeviceIdentity = signer,
            DeviceToken = " device-secret ",
            Capabilities = ["tool-events"],
        });
        var parameters = (await accept).GetProperty("params");
        var auth = parameters.GetProperty("auth");
        Assert.IsFalse(auth.TryGetProperty("token", out _));
        Assert.AreEqual("device-secret", auth.GetProperty("deviceToken").GetString());
        Assert.AreEqual("tool-events", parameters.GetProperty("caps")[0].GetString());
        Assert.IsTrue(signer.Payload!.Contains("|device-secret|nonce|", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SharedTokenRemainsSignatureBindingWhenDeviceTokenIsAlsoSupplied()
    {
        await using var server = new FakeGateway();
        await using var client = new OpenClawGatewayClient();
        var signer = new TestIdentity();
        var accept = server.AcceptHelloAsync();
        await client.ConnectAsync(new OpenClawConnectionOptions
        {
            Endpoint = server.Options().Endpoint,
            DeviceIdentity = signer,
            Token = " shared-secret ",
            DeviceToken = "device-secret",
        });
        var auth = (await accept).GetProperty("params").GetProperty("auth");
        Assert.AreEqual("shared-secret", auth.GetProperty("token").GetString());
        Assert.AreEqual("device-secret", auth.GetProperty("deviceToken").GetString());
        Assert.IsTrue(signer.Payload!.Contains("|shared-secret|nonce|", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ConcurrentRequestsCorrelateOutOfOrderResponses()
    {
        await using var server = new FakeGateway();
        await using var client = new OpenClawGatewayClient();
        await ConnectAsync(server, client);
        var first = client.RequestAsync<JsonElement>("sessions.list", new { request = "first" });
        var second = client.RequestAsync<JsonElement>("models.list", new { request = "second" });
        var a = await server.ReceiveAsync();
        var b = await server.ReceiveAsync();
        await server.ReplyAsync(b, new { received = b.GetProperty("method").GetString() });
        await server.ReplyAsync(a, new { received = a.GetProperty("method").GetString() });
        Assert.AreEqual("sessions.list", (await first).GetProperty("received").GetString());
        Assert.AreEqual("models.list", (await second).GetProperty("received").GetString());
    }

    [TestMethod]
    public async Task TimedOutRequestDoesNotAbortRemoteWorkAndLateReplyIsIgnored()
    {
        await using var server = new FakeGateway();
        await using var client = new OpenClawGatewayClient();
        await ConnectAsync(server, client);
        var timedOut = client.RequestAsync<JsonElement>("sessions.create", new { label = "test" }, TimeSpan.FromMilliseconds(80));
        var old = await server.ReceiveAsync();
        var error = await Assert.ThrowsExactlyAsync<OpenClawGatewayException>(async () => await timedOut);
        Assert.AreEqual(OpenClawFailureKind.Timeout, error.Kind);
        await server.ReplyAsync(old, new { key = "late-session" });
        var next = client.RequestAsync<JsonElement>("sessions.list");
        var request = await server.ReceiveAsync();
        Assert.AreEqual("sessions.list", request.GetProperty("method").GetString(), "Cancellation must not emit chat.abort.");
        await server.ReplyAsync(request, new { sessions = Array.Empty<object>() });
        await next;
        Assert.IsTrue(client.IsConnected);
    }

    [TestMethod]
    public async Task DisconnectRetiresPendingRequestsAndNewGenerationConnects()
    {
        await using var server = new FakeGateway();
        await using var client = new OpenClawGatewayClient();
        await ConnectAsync(server, client);
        var firstGeneration = client.Generation;
        var pending = client.RequestAsync<JsonElement>("sessions.list");
        await server.ReceiveAsync();
        await client.DisconnectAsync();
        var error = await Assert.ThrowsExactlyAsync<OpenClawGatewayException>(async () => await pending);
        Assert.AreEqual(OpenClawFailureKind.Disconnected, error.Kind);
        await using var nextServer = new FakeGateway();
        await ConnectAsync(nextServer, client);
        Assert.IsTrue(client.Generation > firstGeneration);
    }

    [TestMethod]
    public async Task OuterGapDeliversTerminalThenDisconnectsButRunSequenceCanSkip()
    {
        await using var server = new FakeGateway();
        await using var client = new OpenClawGatewayClient();
        await ConnectAsync(server, client);
        var observed = new List<string>();
        var completed = new TaskCompletionSource<OpenClawDisconnectInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.EventReceived += e => observed.Add(e.Payload.GetProperty("state").GetString()!);
        client.Disconnected += e => { observed.Add("disconnected"); completed.TrySetResult(e); };
        await server.SendAsync(new { type = "event", @event = "chat", seq = 1, payload = new { state = "delta", seq = 2 } });
        await server.SendAsync(new { type = "event", @event = "chat", seq = 2, payload = new { state = "delta", seq = 9 } });
        await server.SendAsync(new { type = "event", @event = "chat", seq = 4, payload = new { state = "final", seq = 11 } });
        var disconnected = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(OpenClawFailureKind.EventGap, disconnected.Reason);
        CollectionAssert.AreEqual(new[] { "delta", "delta", "final", "disconnected" }, observed);
    }

    [TestMethod]
    public async Task UnsupportedHelloIsNotAcceptedAsCompatible()
    {
        await using var server = new FakeGateway();
        await using var client = new OpenClawGatewayClient();
        var accept = server.AcceptHelloAsync(protocol: 3);
        var error = await Assert.ThrowsExactlyAsync<OpenClawGatewayException>(() => client.ConnectAsync(server.Options()));
        await accept;
        Assert.AreEqual(OpenClawFailureKind.UnsupportedProtocol, error.Kind);
        Assert.IsFalse(client.IsConnected);
    }

    [TestMethod]
    public async Task RemoteRejectionDoesNotExposeMessageOrArbitraryDetails()
    {
        await using var server = new FakeGateway();
        await using var client = new OpenClawGatewayClient();
        await ConnectAsync(server, client);
        var requestTask = client.RequestAsync<JsonElement>("sessions.list");
        var request = await server.ReceiveAsync();
        await server.SendAsync(new
        {
            type = "res",
            id = request.GetProperty("id").GetString(),
            ok = false,
            error = new { code = "FORBIDDEN", message = "password=super-secret", details = new { code = "MISSING_SCOPE", reason = "url=https://secret.example/?token=secret" } }
        });
        var error = await Assert.ThrowsExactlyAsync<OpenClawGatewayException>(async () => await requestTask);
        Assert.AreEqual("FORBIDDEN", error.Code);
        Assert.AreEqual("MISSING_SCOPE", error.DetailCode);
        Assert.IsNull(error.Reason);
        Assert.IsFalse(error.ToString().Contains("super-secret", StringComparison.Ordinal));
        Assert.IsFalse(error.ToString().Contains("secret.example", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PatchPreservesNullAndAbortCarriesExactSessionFence()
    {
        await using var server = new FakeGateway();
        await using var client = new OpenClawGatewayClient();
        await ConnectAsync(server, client);
        var patch = client.PatchSessionAsync("agent:main:test", "main", "physical", new Dictionary<string, object?> { ["model"] = null });
        var request = await server.ReceiveAsync();
        Assert.AreEqual(JsonValueKind.Null, request.GetProperty("params").GetProperty("model").ValueKind);
        Assert.IsFalse(request.GetProperty("params").TryGetProperty("thinkingLevel", out _));
        await server.ReplyAsync(request, new { ok = true });
        await patch;
        var abort = client.AbortChatAsync("agent:main:test", "run-1", "physical", "main");
        request = await server.ReceiveAsync();
        Assert.AreEqual("chat.abort", request.GetProperty("method").GetString());
        Assert.AreEqual("run-1", request.GetProperty("params").GetProperty("runId").GetString());
        Assert.AreEqual("physical", request.GetProperty("params").GetProperty("sessionId").GetString());
        await server.ReplyAsync(request, new { aborted = true, runIds = new[] { "run-1" } });
        Assert.IsTrue((await abort).Aborted);
    }

    [TestMethod]
    public async Task AccessCredentialsRejectOriginMismatchBeforeNetwork()
    {
        await using var client = new OpenClawGatewayClient();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.ConnectAsync(new OpenClawConnectionOptions
        {
            Endpoint = new Uri("wss://one.example/"),
            DeviceIdentity = new TestIdentity(),
            CloudflareAccess = new() { Origin = new Uri("https://two.example/"), ClientId = "id", ClientSecret = "secret" },
        }));
    }

    [TestMethod]
    public async Task AccessCredentialsRejectMixedOrIncompleteModesBeforeNetwork()
    {
        foreach (var access in new[]
        {
            new OpenClawAccessCredentials { Origin = new Uri("https://one.example/"), AccessToken = "user", ClientId = "id", ClientSecret = "secret" },
            new OpenClawAccessCredentials { Origin = new Uri("https://one.example/"), AccessToken = "user", ClientId = "id" },
            new OpenClawAccessCredentials { Origin = new Uri("https://one.example/"), ClientId = "id" },
            new OpenClawAccessCredentials { Origin = new Uri("https://one.example/"), ClientSecret = "secret" },
            new OpenClawAccessCredentials { Origin = new Uri("https://one.example/"), AccessToken = " " },
            new OpenClawAccessCredentials { Origin = new Uri("https://one.example/"), AccessToken = "user\r\ninjected: header" },
        })
        {
            await using var client = new OpenClawGatewayClient();
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.ConnectAsync(new OpenClawConnectionOptions
            {
                Endpoint = new Uri("wss://one.example/"),
                DeviceIdentity = new TestIdentity(),
                CloudflareAccess = access,
            }));
            Assert.AreEqual(0L, client.Generation);
        }
    }

    [TestMethod]
    public async Task FragmentedInboundPayloadIsBounded()
    {
        await using var server = new FakeGateway();
        await using var client = new OpenClawGatewayClient();
        var accept = server.AcceptHelloAsync();
        await client.ConnectAsync(server.Options(maximumPayload: 1024));
        await accept;
        var ended = new TaskCompletionSource<OpenClawDisconnectInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += e => ended.TrySetResult(e);
        await server.SendFragmentAsync(new string('x', 700), false);
        await server.SendFragmentAsync(new string('x', 700), true);
        Assert.AreEqual(OpenClawFailureKind.PayloadTooLarge, (await ended.Task.WaitAsync(TimeSpan.FromSeconds(5))).Reason);
    }

    [TestMethod]
    public async Task UpgradeAuthorizationAndRedirectResponsesHaveNonRetryableEdgeFailure()
    {
        foreach (var status in new[] { 401, 403, 302, 307 })
        {
            await using var server = new FakeGateway();
            await using var client = new OpenClawGatewayClient();
            var reject = server.RejectUpgradeAsync(status);
            var failure = await Assert.ThrowsExactlyAsync<OpenClawGatewayException>(() => client.ConnectAsync(server.Options()));
            await reject;
            Assert.AreEqual(OpenClawFailureKind.EdgeAuthenticationRequired, failure.Kind, status.ToString());
            Assert.IsFalse(failure.Retryable);
            Assert.IsFalse(client.IsConnected);
        }
    }

    [TestMethod]
    public async Task UpgradeServerFailureRemainsTransportFailure()
    {
        await using var server = new FakeGateway();
        await using var client = new OpenClawGatewayClient();
        var reject = server.RejectUpgradeAsync(503);
        var failure = await Assert.ThrowsExactlyAsync<OpenClawGatewayException>(() => client.ConnectAsync(server.Options()));
        await reject;
        Assert.AreEqual(OpenClawFailureKind.Transport, failure.Kind);
    }

    [TestMethod]
    public async Task ExplicitNullHelloFeaturesAreProtocolFailureInsteadOfRetryableTransport()
    {
        await using var server = new FakeGateway();
        await using var client = new OpenClawGatewayClient();
        var accept = server.AcceptHelloAsync(nullFeatures: true);
        var failure = await Assert.ThrowsExactlyAsync<OpenClawGatewayException>(() => client.ConnectAsync(server.Options()));
        await accept;
        Assert.AreEqual(OpenClawFailureKind.Protocol, failure.Kind);
    }

    private static async Task ConnectAsync(FakeGateway server, OpenClawGatewayClient client)
    {
        var accept = server.AcceptHelloAsync();
        await client.ConnectAsync(server.Options());
        await accept;
    }

    private sealed class TestIdentity : IOpenClawDeviceIdentity
    {
        public string DeviceId => "device";
        public string PublicKeyBase64Url => "public-key";
        public string? Payload { get; private set; }
        public ValueTask<string> SignAsync(string payload, CancellationToken cancellationToken)
        {
            Payload = payload;
            return ValueTask.FromResult("signature");
        }
    }

    /// <summary>Actual loopback TCP/WebSocket server; exercises ClientWebSocket rather than a transport mock.</summary>
    private sealed class FakeGateway : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(12));
        private TcpClient? _tcp;
        private WebSocket? _socket;

        public FakeGateway() { _listener.Start(); }
        public OpenClawConnectionOptions Options(TestIdentity? signer = null, int maximumPayload = 1024 * 1024) => new()
        {
            Endpoint = new Uri($"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/"),
            Token = "token-for-test",
            DeviceIdentity = signer ?? new(),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaximumPayloadBytes = maximumPayload,
        };

        public async Task<JsonElement> AcceptHelloAsync(int protocol = 4, bool nullFeatures = false)
        {
            _tcp = await _listener.AcceptTcpClientAsync(_deadline.Token);
            var stream = _tcp.GetStream();
            var header = new StringBuilder();
            var single = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (header.Length > 16 * 1024 || await stream.ReadAsync(single, _deadline.Token) == 0)
                {
                    throw new IOException("Invalid websocket upgrade.");
                }

                header.Append((char)single[0]);
            }
            var key = header.ToString().Split("\r\n").First(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _deadline.Token);
            _socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(30));
            await SendAsync(new { type = "event", @event = "connect.challenge", payload = new { nonce = "nonce", ts = 123456 } });
            var connect = await ReceiveAsync();
            await ReplyAsync(connect, new
            {
                type = "hello-ok",
                protocol,
                server = new { version = "test-v4", connId = "connection" },
                features = nullFeatures ? null : new { methods = new[] { "sessions.list" }, events = new[] { "chat" } },
                snapshot = new { },
                auth = new { role = "operator", scopes = new[] { "operator.read", "operator.write" } },
                policy = new { maxPayload = 1024 * 1024, maxBufferedBytes = 2 * 1024 * 1024, tickIntervalMs = 15000 }
            });
            return connect;
        }

        public async Task RejectUpgradeAsync(int status)
        {
            _tcp = await _listener.AcceptTcpClientAsync(_deadline.Token);
            var stream = _tcp.GetStream();
            var header = new StringBuilder();
            var single = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (header.Length > 16 * 1024 || await stream.ReadAsync(single, _deadline.Token) == 0)
                {
                    throw new IOException("Invalid websocket upgrade.");
                }

                header.Append((char)single[0]);
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Rejected\r\nContent-Length: 0\r\nLocation: https://login.invalid/secret-must-not-be-followed\r\nConnection: close\r\n\r\n"), _deadline.Token);
        }

        public async Task<JsonElement> ReceiveAsync()
        {
            using var content = new MemoryStream();
            var bytes = new byte[4096];
            ValueWebSocketReceiveResult result;
            do
            {
                result = await _socket!.ReceiveAsync(bytes.AsMemory(), _deadline.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new IOException("Peer closed.");
                }

                content.Write(bytes, 0, result.Count);
            }
            while (!result.EndOfMessage);
            using var json = JsonDocument.Parse(content.ToArray());
            return json.RootElement.Clone();
        }

        public Task ReplyAsync(JsonElement request, object payload) => SendAsync(new { type = "res", id = request.GetProperty("id").GetString(), ok = true, payload });
        public Task SendAsync(object value) => _socket!.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value).AsMemory(), WebSocketMessageType.Text, true, _deadline.Token).AsTask();
        public Task SendFragmentAsync(string value, bool final) => _socket!.SendAsync(Encoding.UTF8.GetBytes(value).AsMemory(), WebSocketMessageType.Text, final, _deadline.Token).AsTask();

        public ValueTask DisposeAsync()
        {
            _socket?.Abort();
            _socket?.Dispose();
            _tcp?.Dispose();
            _listener.Stop();
            _deadline.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
