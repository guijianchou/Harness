// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.OpenClaw;

namespace Harness.Services;

/// <summary>Owns one selected environment's connection and its bounded retry loop.</summary>
internal sealed class OpenClawSessionRuntime : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<OpenClawConnectionOptions>> _options;
    private readonly Func<OpenClawHello, CancellationToken, Task> _saveIdentity;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Task? _run;
    private OpenClawGatewayClient? _client;
    private CancellationTokenSource? _retry;
    private volatile bool _autoRefresh;
    private long _connectionGeneration;

    public OpenClawSessionRuntime(Func<CancellationToken, Task<OpenClawConnectionOptions>> options,
        Func<OpenClawHello, CancellationToken, Task> saveIdentity, bool autoRefresh)
    {
        _options = options;
        _saveIdentity = saveIdentity;
        _autoRefresh = autoRefresh;
    }

    public event Action<OpenClawHello>? Connected;
    public event Action<OpenClawGatewayEvent>? EventReceived;
    public event Action<Exception?, bool>? ConnectionLost;
    public OpenClawGatewayClient? Client => Volatile.Read(ref _client);
    public long ConnectionGeneration => Interlocked.Read(ref _connectionGeneration);

    public void Start()
    {
        _run ??= Task.Run(RunAsync);
    }

    public void SetAutoRefresh(bool enabled)
    {
        _autoRefresh = enabled;
        lock (_gate)
        {
            if (!enabled)
            {
                _retry?.Cancel();
            }
        }
    }

    private async Task RunAsync()
    {
        var attempt = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            Exception? failure = null;
            var mayRetry = true;
            await using (var client = new OpenClawGatewayClient())
            {
                var generation = Interlocked.Increment(ref _connectionGeneration);
                var disconnected = new TaskCompletionSource<OpenClawDisconnectInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Disconnected += info => disconnected.TrySetResult(info);
                client.EventReceived += value => EventReceived?.Invoke(value with { Generation = generation });
                try
                {
                    var options = await _options(_lifetime.Token).ConfigureAwait(false);
                    var hello = await client.ConnectAsync(options, _lifetime.Token).ConfigureAwait(false);
                    await _saveIdentity(hello, _lifetime.Token).ConfigureAwait(false);
                    Volatile.Write(ref _client, client);
                    attempt = 0;
                    Connected?.Invoke(hello);
                    var ended = await disconnected.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                    mayRetry = ended.Reason is OpenClawFailureKind.Transport or OpenClawFailureKind.Disconnected or OpenClawFailureKind.EventGap;
                    if (!mayRetry)
                    {
                        failure = new OpenClawGatewayException(ended.Reason);
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    mayRetry = ex is OpenClawGatewayException gateway &&
                        (gateway.Kind is OpenClawFailureKind.Transport or OpenClawFailureKind.Timeout or OpenClawFailureKind.Disconnected || gateway.Retryable);
                }
                finally
                {
                    Volatile.Write(ref _client, null);
                }
            }

            if (_lifetime.IsCancellationRequested)
            {
                return;
            }

            var retrying = mayRetry && _autoRefresh;
            ConnectionLost?.Invoke(failure, retrying);
            if (!retrying)
            {
                return;
            }

            using var retry = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            lock (_gate)
            {
                _retry = retry;
                if (!_autoRefresh)
                {
                    retry.Cancel();
                }
            }

            try
            {
                var seconds = Math.Min(30, Math.Pow(2, Math.Min(attempt++, 5))) + Random.Shared.NextDouble();
                await Task.Delay(TimeSpan.FromSeconds(seconds), retry.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!_lifetime.IsCancellationRequested)
                {
                    ConnectionLost?.Invoke(null, false);
                }

                return;
            }
            finally
            {
                lock (_gate)
                {
                    _retry = null;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_run is not null)
        {
            await _run.ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }
}
