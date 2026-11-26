// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Harness.Models;
using Harness.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Harness.Core.Tests;

[TestClass]
public sealed class HttpProbeIsolationTests
{
    [TestMethod]
    public async Task DefaultLatencyProbeDoesNotCarryCookiesAcrossEnvironmentSwitches()
    {
        await using var first = new ProbeServer();
        await using var second = new ProbeServer();
        using var service = new ControlUiLatencyService();
        var results = Channel.CreateUnbounded<ControlUiLatencySnapshot>();
        service.LatencyUpdated += snapshot => results.Writer.TryWrite(snapshot);
        service.Start(first.Url("set-cookie"), HostedBackendProfile.For(HostedBackendKind.Generic));
        Assert.IsTrue((await results.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        await first.NextRequestAsync();
        service.Stop();

        // Cookies ignore the TCP port: both environments share a host, but separate origins.
        service.Start(second.Url("other-environment"), HostedBackendProfile.For(HostedBackendKind.Generic));
        var request = await second.NextRequestAsync();
        Assert.IsNull(request.Cookie, "A background probe must not send the previous environment's cookie.");
        Assert.IsTrue((await results.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        service.Stop();
    }

    [TestMethod]
    public async Task SharedProbeClientDoesNotRememberSetCookieResponses()
    {
        await using var server = new ProbeServer();
        using var client = StatelessProbeHttpClient.Create(TimeSpan.FromSeconds(5));
        using var first = await client.GetAsync(server.Url("set-cookie"));
        await server.NextRequestAsync();
        using var second = await client.GetAsync(server.Url("next"));
        var request = await server.NextRequestAsync();
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        Assert.IsNull(request.Cookie);
    }

    [TestMethod]
    public async Task SharedProbeClientReturnsRedirectWithoutFollowingItsTarget()
    {
        await using var server = new ProbeServer();
        using var client = StatelessProbeHttpClient.Create(TimeSpan.FromSeconds(5));
        using var response = await client.GetAsync(server.Url("redirect"));
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.AreEqual(1, server.RequestCount);
    }

    [TestMethod]
    public async Task DefaultLatencyProbeReportsRedirectWithoutFollowingItsTarget()
    {
        await using var server = new ProbeServer();
        using var service = new ControlUiLatencyService();
        var completion = new TaskCompletionSource<ControlUiLatencySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.LatencyUpdated += snapshot => completion.TrySetResult(snapshot);
        service.Start(server.Url("redirect"), HostedBackendProfile.For(HostedBackendKind.Generic));
        var snapshot = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Stop();
        Assert.IsTrue(snapshot.IsSuccess);
        Assert.IsTrue(snapshot.Detail?.Contains("302", StringComparison.Ordinal) == true);
        Assert.AreEqual(1, server.RequestCount);
    }

    private sealed record ProbeRequest(string Path, string? Cookie);

    private sealed class ProbeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
        private readonly Channel<ProbeRequest> _requests = Channel.CreateUnbounded<ProbeRequest>();
        private readonly Task _server;
        private int _requestCount;

        public ProbeServer()
        {
            _listener.Start();
            _server = ServeAsync();
        }

        public int RequestCount => Volatile.Read(ref _requestCount);
        public string Url(string path) => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{path}";
        public Task<ProbeRequest> NextRequestAsync() => _requests.Reader.ReadAsync(_stop.Token).AsTask();

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = connection.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync(_stop.Token) ?? throw new IOException("Missing request line.");
                    string? cookie = null;
                    while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
                    {
                        if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) { cookie = line[7..].Trim(); }
                    }
                    var path = requestLine.Split(' ')[1];
                    Interlocked.Increment(ref _requestCount);
                    _requests.Writer.TryWrite(new(path, cookie));
                    var redirect = path == "/redirect";
                    var extraHeader = path == "/set-cookie" ? "Set-Cookie: probe-session=environment-a; Path=/\r\n" :
                        redirect ? $"Location: {Url("redirect-target")}\r\n" : string.Empty;
                    var status = redirect ? "302 Found" : "200 OK";
                    var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{extraHeader}Content-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _server;
            _stop.Dispose();
        }
    }
}
