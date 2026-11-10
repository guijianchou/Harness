// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.OpenClaw;

/// <summary>
/// One operator connection. The app owns reconnect policy and credential persistence.
/// Request cancellation retires the local waiter; it never sends a remote abort.
/// Contract: OpenClaw protocol v4, upstream 7ecb9cec67740e1afedc0a8e20542c6cfa456358.
/// </summary>
public sealed partial class OpenClawGatewayClient : IAsyncDisposable
{
    public const int ProtocolVersion = 4;
    public const string ClientId = "gateway-client";
    public const string ClientMode = "ui";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 64,
    };
    private readonly object _gate = new();
    private Connection? _connection;
    private long _generation;
    private bool _disposed;

    public event Action<OpenClawGatewayEvent>? EventReceived;
    public event Action<OpenClawDisconnectInfo>? Disconnected;
    public bool IsConnected { get { lock (_gate) { return _connection is { Authenticated: true, Retired: false }; } } }
    public long Generation { get { lock (_gate) { return _generation; } } }
    public OpenClawHello? Hello { get { lock (_gate) { return _connection?.Hello; } } }

    public async Task<OpenClawHello> ConnectAsync(OpenClawConnectionOptions options, CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        Connection connection;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection is not null)
            {
                throw new InvalidOperationException("Disconnect the existing Gateway connection first.");
            }

            connection = new Connection(++_generation, options);
            _connection = connection;
        }

        using var deadline = new CancellationTokenSource(options.ConnectTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, connection.Lifetime.Token);
        try
        {
            connection.Socket.Options.CollectHttpResponseDetails = true;
            connection.Socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            connection.Socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
            if (options.Origin is not null)
            {
                connection.Socket.Options.SetRequestHeader("Origin", options.Origin);
            }

            if (options.CloudflareAccess is { } access)
            {
                if (NormalizeCredential(access.AccessToken) is { } accessToken)
                {
                    connection.Socket.Options.SetRequestHeader("Cf-Access-Token", accessToken);
                }
                else
                {
                    connection.Socket.Options.SetRequestHeader("CF-Access-Client-Id", NormalizeCredential(access.ClientId)!);
                    connection.Socket.Options.SetRequestHeader("CF-Access-Client-Secret", NormalizeCredential(access.ClientSecret)!);
                }
            }

            // Redirects must never carry Access credentials to another origin.
            await connection.Socket.ConnectAsync(options.Endpoint, connection.HttpInvoker, linked.Token).ConfigureAwait(false);
            connection.ReceiveTask = ReceiveLoopAsync(connection);
            var challenge = await connection.Challenge.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            var token = NormalizeCredential(options.Token);
            var deviceToken = NormalizeCredential(options.DeviceToken);
            var password = NormalizeCredential(options.Password);
            var scopes = options.Scopes.ToArray();
            var payload = BuildDeviceAuthPayload(options.DeviceIdentity.DeviceId, scopes, challenge.Timestamp,
                token ?? deviceToken, challenge.Nonce, options.Platform, options.DeviceFamily);
            var signature = await options.DeviceIdentity.SignAsync(payload, linked.Token).ConfigureAwait(false);
            var connect = new
            {
                minProtocol = ProtocolVersion,
                maxProtocol = ProtocolVersion,
                client = new { id = ClientId, mode = ClientMode, displayName = "Harness", version = options.ClientVersion, platform = options.Platform, deviceFamily = options.DeviceFamily },
                role = "operator",
                scopes,
                caps = options.Capabilities.ToArray(),
                auth = new { token, deviceToken, password },
                device = new { id = options.DeviceIdentity.DeviceId, publicKey = options.DeviceIdentity.PublicKeyBase64Url, signature, signedAt = challenge.Timestamp, nonce = challenge.Nonce },
            };
            await RequestCoreAsync(connection, "connect", connect, options.ConnectTimeout, linked.Token).ConfigureAwait(false);
            return connection.Hello ?? throw new OpenClawGatewayException(OpenClawFailureKind.Protocol);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Retire(connection, OpenClawFailureKind.Disconnected);
            throw;
        }
        catch (OperationCanceledException)
        {
            if (connection.RetirementFailure is { } failure)
            {
                throw failure;
            }

            Retire(connection, deadline.IsCancellationRequested ? OpenClawFailureKind.Timeout : OpenClawFailureKind.Disconnected);
            throw new OpenClawGatewayException(deadline.IsCancellationRequested ? OpenClawFailureKind.Timeout : OpenClawFailureKind.Disconnected);
        }
        catch (OpenClawGatewayException ex)
        {
            Retire(connection, ex.Kind);
            throw;
        }
        catch
        {
            var status = (int)connection.Socket.HttpStatusCode;
            var kind = status is 401 or 403 or >= 300 and <= 399
                ? OpenClawFailureKind.EdgeAuthenticationRequired
                : OpenClawFailureKind.Transport;
            Retire(connection, kind);
            throw new OpenClawGatewayException(kind);
        }
    }

    public async Task<T> RequestAsync<T>(string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(method) || method.Length > 128 || method == "connect")
        {
            throw new ArgumentException("Invalid Gateway method.", nameof(method));
        }

        Connection connection;
        lock (_gate)
        {
            connection = _connection is { Authenticated: true, Retired: false } current ? current :
                throw new OpenClawGatewayException(OpenClawFailureKind.Disconnected);
        }
        var payload = await RequestCoreAsync(connection, method, parameters ?? new { }, timeout ?? connection.RequestTimeout, cancellationToken).ConfigureAwait(false);
        try
        {
            return payload.Deserialize<T>(JsonOptions) ?? throw new OpenClawGatewayException(OpenClawFailureKind.Protocol);
        }
        catch (JsonException)
        {
            throw new OpenClawGatewayException(OpenClawFailureKind.Protocol);
        }
    }

    private async Task<JsonElement> RequestCoreAsync(Connection connection, string method, object parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var id = $"{connection.Generation}:{Interlocked.Increment(ref connection.RequestId)}";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { type = "req", id, method, @params = parameters }, JsonOptions);
        if (bytes.Length > (connection.Authenticated ? connection.MaximumOutboundBytes : 64 * 1024))
        {
            throw new OpenClawGatewayException(OpenClawFailureKind.PayloadTooLarge);
        }

        var pending = new PendingRequest(method == "connect");
        if (!connection.Pending.TryAdd(id, pending))
        {
            throw new OpenClawGatewayException(OpenClawFailureKind.Protocol);
        }

        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, connection.Lifetime.Token);
        try
        {
            await connection.SendGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                if (connection.Retired)
                {
                    throw new OpenClawGatewayException(OpenClawFailureKind.Disconnected);
                }

                await connection.Socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, linked.Token).ConfigureAwait(false);
            }
            finally { connection.SendGate.Release(); }
            return await pending.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            if (connection.RetirementFailure is { } failure)
            {
                throw failure;
            }

            throw new OpenClawGatewayException(deadline.IsCancellationRequested ? OpenClawFailureKind.Timeout : OpenClawFailureKind.Disconnected);
        }
        catch (WebSocketException)
        {
            Retire(connection, OpenClawFailureKind.Transport);
            throw new OpenClawGatewayException(OpenClawFailureKind.Transport);
        }
        finally
        {
            connection.Pending.TryRemove(id, out _);
            if (!connection.Retired && connection.Socket.State != WebSocketState.Open)
            {
                Retire(connection, OpenClawFailureKind.Transport);
            }
        }
    }

    private async Task ReceiveLoopAsync(Connection connection)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!connection.Lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await connection.Socket.ReceiveAsync(buffer.AsMemory(), connection.Lifetime.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) { Retire(connection, OpenClawFailureKind.Disconnected); return; }
                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        throw new OpenClawGatewayException(OpenClawFailureKind.Protocol);
                    }

                    if (message.Length + result.Count > connection.MaximumInboundBytes)
                    {
                        throw new OpenClawGatewayException(OpenClawFailureKind.PayloadTooLarge);
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
                using var document = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length), new JsonDocumentOptions { MaxDepth = 64 });
                ProcessFrame(connection, document.RootElement);
            }
        }
        catch (OperationCanceledException) when (connection.Lifetime.IsCancellationRequested) { }
        catch (OpenClawGatewayException ex) { Retire(connection, ex.Kind, ex); }
        catch (JsonException) { Retire(connection, OpenClawFailureKind.Protocol); }
        catch { Retire(connection, OpenClawFailureKind.Transport); }
    }

    private void ProcessFrame(Connection connection, JsonElement frame)
    {
        if (connection.Retired)
        {
            return;
        }

        var type = ReadString(frame, "type");
        if (type == "res")
        {
            var id = ReadString(frame, "id");
            if (id is null || !connection.Pending.TryGetValue(id, out var pending))
            {
                return;
            }

            if (!frame.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new OpenClawGatewayException(OpenClawFailureKind.Protocol);
            }

            if (!ok.GetBoolean())
            {
                var error = frame.TryGetProperty("error", out var e) ? e : default;
                var details = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("details", out var d) ? d : default;
                var code = ReadString(error, "code");
                var detailCode = ReadString(details, "code");
                var reason = ReadString(details, "reason");
                var retryable = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("retryable", out var retry) && retry.ValueKind == JsonValueKind.True;
                var failure = new OpenClawGatewayException(
                    pending.IsConnect && (detailCode == "PROTOCOL_VERSION_MISMATCH" || reason == "protocol-mismatch") ? OpenClawFailureKind.UnsupportedProtocol : OpenClawFailureKind.Rejected,
                    code, detailCode, reason, retryable, ReadString(details, "requestId"));
                pending.Completion.TrySetException(failure);
                return;
            }
            if (!frame.TryGetProperty("payload", out var payload))
            {
                throw new OpenClawGatewayException(OpenClawFailureKind.Protocol);
            }

            if (pending.IsConnect)
            {
                var hello = payload.Deserialize<OpenClawHello>(JsonOptions) ?? throw new OpenClawGatewayException(OpenClawFailureKind.Protocol);
                if (hello.Protocol != ProtocolVersion)
                {
                    throw new OpenClawGatewayException(OpenClawFailureKind.UnsupportedProtocol);
                }

                if (hello.Type != "hello-ok" || hello.Auth is null || hello.Policy is null || hello.Server is null || hello.Features is null ||
                    hello.Auth.Scopes is null || hello.Features.Methods is null || hello.Features.Events is null || hello.Features.Capabilities is null ||
                    hello.Auth.Role != "operator" || hello.Policy.MaxPayload <= 0 || hello.Policy.MaxBufferedBytes <= 0 || hello.Policy.TickIntervalMs <= 0 ||
                    string.IsNullOrEmpty(hello.Server.Version) || string.IsNullOrEmpty(hello.Server.ConnId) || hello.Snapshot.ValueKind != JsonValueKind.Object ||
                    !payload.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Object ||
                    !features.TryGetProperty("methods", out var methods) || methods.ValueKind != JsonValueKind.Array ||
                    !features.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
                {
                    throw new OpenClawGatewayException(OpenClawFailureKind.Protocol);
                }

                connection.Hello = hello;
                connection.MaximumOutboundBytes = Math.Min(connection.MaximumInboundBytes, hello.Policy.MaxPayload);
                connection.Authenticated = true;
            }
            pending.Completion.TrySetResult(payload.Clone());
            return;
        }
        if (type != "event")
        {
            throw new OpenClawGatewayException(OpenClawFailureKind.Protocol);
        }

        var name = ReadString(frame, "event") ?? throw new OpenClawGatewayException(OpenClawFailureKind.Protocol);
        if (!frame.TryGetProperty("payload", out var eventPayload))
        {
            throw new OpenClawGatewayException(OpenClawFailureKind.Protocol);
        }

        if (name == "connect.challenge")
        {
            var nonce = ReadString(eventPayload, "nonce");
            if (connection.Authenticated || string.IsNullOrWhiteSpace(nonce) || nonce.Length > 4096 ||
                !eventPayload.TryGetProperty("ts", out var timestamp) || timestamp.ValueKind != JsonValueKind.Number || !timestamp.TryGetInt64(out var ts) || ts < 0)
            {
                throw new OpenClawGatewayException(OpenClawFailureKind.Protocol);
            }

            connection.Challenge.TrySetResult(new Challenge(nonce, ts));
            return;
        }
        if (!connection.Authenticated)
        {
            throw new OpenClawGatewayException(OpenClawFailureKind.Protocol);
        }

        long? sequence = null;
        if (frame.TryGetProperty("seq", out var seq))
        {
            if (seq.ValueKind != JsonValueKind.Number || !seq.TryGetInt64(out var value) || value < 0)
            {
                throw new OpenClawGatewayException(OpenClawFailureKind.Protocol);
            }

            sequence = value;
            if (connection.LastSequence is { } last && value != last + 1)
            {
                if (name == "chat" && ReadString(eventPayload, "state") is "final" or "aborted" or "error")
                {
                    PublishEvent(connection, name, eventPayload, sequence, frame);
                }

                Retire(connection, OpenClawFailureKind.EventGap);
                return;
            }
            connection.LastSequence = value;
        }
        PublishEvent(connection, name, eventPayload, sequence, frame);
    }

    private void PublishEvent(Connection connection, string name, JsonElement payload, long? sequence, JsonElement frame)
    {
        var value = new OpenClawGatewayEvent(connection.Generation, name, payload.Clone(), sequence,
            frame.TryGetProperty("stateVersion", out var version) ? version.Clone() : null);
        foreach (var callback in EventReceived?.GetInvocationList() ?? [])
        {
            try { ((Action<OpenClawGatewayEvent>)callback)(value); } catch { /* UI observers cannot kill transport. */ }
        }
    }

    private void Retire(Connection connection, OpenClawFailureKind reason, OpenClawGatewayException? failure = null)
    {
        lock (_gate)
        {
            if (connection.Retired)
            {
                return;
            }

            connection.RetirementFailure = failure ?? new OpenClawGatewayException(reason);
            connection.Retired = true;
            connection.Authenticated = false;
        }
        var error = connection.RetirementFailure;
        connection.Challenge.TrySetException(error);
        foreach (var pending in connection.Pending.Values)
        {
            pending.Completion.TrySetException(error);
        }

        connection.Lifetime.Cancel();
        connection.Socket.Abort();
        foreach (var callback in Disconnected?.GetInvocationList() ?? [])
        {
            try { ((Action<OpenClawDisconnectInfo>)callback)(new(connection.Generation, reason)); } catch { }
        }
    }

    public async Task DisconnectAsync()
    {
        Connection? connection;
        lock (_gate) { connection = _connection; _connection = null; }
        if (connection is null)
        {
            return;
        }

        Retire(connection, OpenClawFailureKind.Disconnected);
        if (connection.ReceiveTask is not null)
        {
            await connection.ReceiveTask.ConfigureAwait(false);
        }

        connection.Socket.Dispose();
        connection.HttpInvoker.Dispose();
        // Request waiters can still be unwinding; their linked tokens and SendGate remain valid.
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) { _disposed = true; }
        await DisconnectAsync().ConfigureAwait(false);
    }

    public static string BuildDeviceAuthPayload(string deviceId, IReadOnlyList<string> scopes, long signedAt,
        string? token, string nonce, string platform, string deviceFamily) => string.Join('|',
        "v3", deviceId, ClientId, ClientMode, "operator", string.Join(',', scopes),
        signedAt.ToString(CultureInfo.InvariantCulture), token ?? "", nonce,
        NormalizeMetadata(platform), NormalizeMetadata(deviceFamily));

    private static string NormalizeMetadata(string value) => new(value.Trim().Select(ch => ch is >= 'A' and <= 'Z' ? (char)(ch + 32) : ch).ToArray());
    private static string? NormalizeCredential(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? ReadString(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void ValidateOptions(OpenClawConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var endpoint = options.Endpoint;
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("ws" or "wss") || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment) || !string.IsNullOrEmpty(endpoint.Query) ||
            (endpoint.Scheme == "ws" && !endpoint.IsLoopback))
        {
            throw new ArgumentException("Gateway requires WSS, or WS on loopback, with no URL credentials, query, or fragment.", nameof(options));
        }

        if (options.MaximumPayloadBytes is < 1024 or > 64 * 1024 * 1024 || options.ConnectTimeout <= TimeSpan.Zero || options.ConnectTimeout > TimeSpan.FromMinutes(5) ||
            options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentException("Invalid Gateway limits.", nameof(options));
        }

        ArgumentNullException.ThrowIfNull(options.DeviceIdentity);
        ArgumentNullException.ThrowIfNull(options.Capabilities);
        if (options.Capabilities.Count > 64 || options.Capabilities.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128))
        {
            throw new ArgumentException("Invalid Gateway capabilities.", nameof(options));
        }
        if (string.IsNullOrWhiteSpace(options.DeviceIdentity.DeviceId) || string.IsNullOrWhiteSpace(options.DeviceIdentity.PublicKeyBase64Url) || options.Scopes.Count == 0 ||
            options.Scopes.Any(scope => string.IsNullOrWhiteSpace(scope) || scope.Contains('|') || scope.Contains(',')))
        {
            throw new ArgumentException("Invalid device or scopes.", nameof(options));
        }

        if (options.Origin is not null && (!Uri.TryCreate(options.Origin, UriKind.Absolute, out var origin) || origin.Scheme is not ("https" or "http") || origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment)))
        {
            throw new ArgumentException("Invalid Gateway origin.", nameof(options));
        }

        if (options.CloudflareAccess is { } access)
        {
            if (endpoint.Scheme != "wss" || !access.Origin.IsAbsoluteUri || access.Origin.Scheme != "https" ||
                !string.Equals(endpoint.IdnHost, access.Origin.IdnHost, StringComparison.OrdinalIgnoreCase) || endpoint.Port != access.Origin.Port ||
                access.Origin.AbsolutePath != "/" || !string.IsNullOrEmpty(access.Origin.UserInfo) || !string.IsNullOrEmpty(access.Origin.Query) || !string.IsNullOrEmpty(access.Origin.Fragment))
            {
                throw new ArgumentException("Access credentials must be bound to the exact HTTPS Gateway origin.", nameof(options));
            }

            var hasAccessToken = NormalizeCredential(access.AccessToken) is not null;
            var hasClientId = NormalizeCredential(access.ClientId) is not null;
            var hasClientSecret = NormalizeCredential(access.ClientSecret) is not null;
            if (hasAccessToken ? hasClientId || hasClientSecret : !hasClientId || !hasClientSecret)
            {
                throw new ArgumentException("Provide an Access user token or both service-token fields, never both modes.", nameof(options));
            }

            if (new[] { access.AccessToken, access.ClientId, access.ClientSecret }.Any(value => value?.Any(char.IsControl) == true))
            {
                throw new ArgumentException("Access credentials contain invalid header characters.", nameof(options));
            }
        }
    }

    private sealed class Connection(long generation, OpenClawConnectionOptions options)
    {
        public long Generation { get; } = generation;
        public ClientWebSocket Socket { get; } = new();
        public HttpMessageInvoker HttpInvoker { get; } = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        public CancellationTokenSource Lifetime { get; } = new();
        public SemaphoreSlim SendGate { get; } = new(1, 1);
        public ConcurrentDictionary<string, PendingRequest> Pending { get; } = new();
        public TaskCompletionSource<Challenge> Challenge { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan RequestTimeout { get; } = options.RequestTimeout;
        public int MaximumInboundBytes { get; } = options.MaximumPayloadBytes;
        public int MaximumOutboundBytes = options.MaximumPayloadBytes;
        public volatile bool Authenticated;
        public volatile bool Retired;
        public OpenClawGatewayException? RetirementFailure;
        public OpenClawHello? Hello;
        public Task? ReceiveTask;
        public long RequestId;
        public long? LastSequence;
    }

    private sealed class PendingRequest(bool isConnect)
    {
        public bool IsConnect { get; } = isConnect;
        public TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record Challenge(string Nonce, long Timestamp);
}
