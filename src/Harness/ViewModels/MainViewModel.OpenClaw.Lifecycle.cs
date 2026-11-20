// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Helpers;
using Harness.Models;
using Harness.OpenClaw;
using Harness.Services;

namespace Harness.ViewModels;

public partial class MainViewModel
{
    private void SynchronizeGatewayEnvironment(bool force = false)
    {
        var environment = _selectedEnvironment?.Clone();
        var fingerprint = environment is null ? null : JsonSerializer.Serialize(new
        {
            environment.Id,
            environment.GatewayUrl,
            environment.Backend,
            environment.OpenClawConnection,
        });
        if (!force && fingerprint == _gatewayEnvironmentFingerprint)
        {
            return;
        }

        _gatewayEnvironmentFingerprint = fingerprint;
        StopGatewayRuntime();
        _gatewayTargetVersion++;
        IsOpenClawPanelOpen = false;
        _gatewayRoster.Clear();
        GatewaySessions.Clear();
        GatewayModels.Clear();
        _selectedGatewaySession = null;
        _selectedGatewayModel = null;
        _gatewayPage = null;
        _gatewayHistory = null;
        ResetGatewaySessionDetails();
        _gatewaySubscribedKey = null;
        _gatewayEventRunId = null;
        _gatewayCanWrite = false;
        _gatewaySelectionChanging = false;
        _gatewayEventDuringSelection = false;
        _gatewayDataStale = false;
        ResetGatewayRefreshRetry();
        InvalidateGatewayModels(clear: true);
        Interlocked.Exchange(ref _gatewayRefreshRequested, 0);
        _gatewayObservedRuns.Clear();
        _gatewayNotifiedRuns.Clear();
        GatewayBusy = false;
        _gatewayRosterLoaded = false;
        _gatewayRosterLoading = false;
        NotifyGatewayRosterState();
        GatewayHasMore = false;
        GatewayErrorText = "";
        GatewayVersionText = "";
        GatewayRunText = GatewayStrings.Text("SelectSession");
        GatewayStatusText = GatewayStrings.Text("Disconnected");
        foreach (var property in new[] { nameof(IsOpenClawAvailable), nameof(GatewayEntryVisibility), nameof(GatewayPanelVisibility),
                     nameof(SelectedGatewaySession), nameof(SelectedGatewayModel), nameof(GatewaySessionTitle) })
        {
            OnPropertyChanged(property);
        }

        if (!IsOpenClawAvailable || environment?.OpenClawConnection is not { Enabled: true } preferences)
        {
            return;
        }

        SetGatewayRunPresentation("SelectSession", false);
        ModelSummaryText = DefaultModelSummary;

        Uri endpoint;
        try
        {
            endpoint = OpenClawEndpoint.Resolve(environment.GatewayUrl, preferences.WebSocketUrl);
        }
        catch (UriFormatException)
        {
            SetGatewayIssue(GatewayStrings.Text("InvalidEndpoint"), GatewayRecoveryAction.Configure);
            return;
        }

        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(environment.Id + "|" + endpoint.AbsoluteUri)));
        var tokenScope = scope + "|" + preferences.AuthMode + "|" + preferences.CredentialReference;
        var credentials = new OpenClawCredentialStore();
        var identityService = new OpenClawDeviceIdentityService(credentials);
        Task<OpenClawDeviceIdentity>? identity = null;
        string[] scopes = ["operator.read", "operator.write"];
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _gatewayCancellation = cancellation;
        var runtime = new OpenClawSessionRuntime(async token =>
        {
            identity ??= identityService.GetOrCreateAsync(scope, token);
            var device = await identity.ConfigureAwait(false);
            var stored = string.IsNullOrEmpty(preferences.CredentialReference) ? null :
                await credentials.ReadAsync(preferences.CredentialReference, token).ConfigureAwait(false);
            if (stored is not null && !string.Equals(stored.Endpoint, endpoint.AbsoluteUri, StringComparison.Ordinal))
            {
                throw new OpenClawGatewayException(OpenClawFailureKind.Rejected, detailCode: "CREDENTIAL_ENDPOINT_CHANGED");
            }
            var deviceToken = await credentials.ReadDeviceTokenAsync(tokenScope, "operator", scopes, token).ConfigureAwait(false);
            return new Harness.OpenClaw.OpenClawConnectionOptions
            {
                Endpoint = endpoint,
                DeviceIdentity = device,
                DeviceToken = deviceToken,
                Token = preferences.AuthMode == OpenClawAuthMode.Token ? stored?.Token : null,
                Password = preferences.AuthMode == OpenClawAuthMode.Password ? stored?.Password : null,
                Origin = new Uri(environment.GatewayUrl).GetLeftPart(UriPartial.Authority),
                ClientVersion = AppMetadata.GetDisplayVersion(),
                Scopes = scopes,
                Capabilities = new string[] { "tool-events", "model-selection-policy" },
                CloudflareAccess = string.IsNullOrEmpty(stored?.AccessToken) ? null : new OpenClawAccessCredentials
                {
                    Origin = new UriBuilder(endpoint) { Scheme = "https", Path = "", Query = "", Fragment = "" }.Uri,
                    AccessToken = stored.AccessToken,
                },
            };
        }, async (hello, token) =>
        {
            if (!string.IsNullOrEmpty(hello.Auth.DeviceToken) && scopes.All(scopeName => hello.Auth.Scopes.Contains(scopeName, StringComparer.Ordinal)))
            {
                await credentials.SaveDeviceTokenAsync(tokenScope, "operator", scopes, hello.Auth.DeviceToken, token).ConfigureAwait(false);
            }
        }, AutoRefreshEnabled);
        _gatewayRuntime = runtime;
        runtime.Connected += hello => DispatchUiUpdate(() =>
        {
            if (!IsCurrentGateway(runtime) || !ReferenceEquals(runtime.Client?.Hello, hello))
            {
                return;
            }

            GatewayStatusText = GatewayStrings.Text("Connected");
            _gatewayRosterLoaded = false;
            _gatewayRosterLoading = true;
            NotifyGatewayRosterState();
            GatewayVersionText = $"OpenClaw {hello.Server.Version} · v{hello.Protocol}";
            GatewayErrorText = "";
            _gatewayCanWrite = hello.Auth.Scopes.Contains("operator.write", StringComparer.Ordinal) || hello.Auth.Scopes.Contains("operator.admin", StringComparer.Ordinal);
            _gatewayTargetVersion++;
            _gatewayHistory = null;
            _gatewayDataStale = false;
            ResetGatewayRefreshRetry();
            InvalidateGatewayModels(clear: true);
            ResetGatewaySessionDetails();
            _gatewayPage = null;
            _gatewaySubscribedKey = null;
            _gatewayEventRunId = null;
            RequestGatewayRefresh(GatewayRefreshScope.All);
            NotifyGatewayCapabilities();
            ObserveGatewayUiTask(SubscribeGatewayRosterAsync(runtime, cancellation.Token));
        });
        runtime.ConnectionLost += (error, retrying) => DispatchUiUpdate(() =>
        {
            if (!IsCurrentGateway(runtime) || runtime.Client?.IsConnected == true)
            {
                return;
            }

            _gatewayCanWrite = false;
            _gatewayRosterLoading = false;
            NotifyGatewayRosterState();
            _gatewayTargetVersion++;
            _gatewayHistory = null;
            InvalidateGatewayModels(clear: true);
            ResetGatewaySessionDetails();
            GatewayStatusText = GatewayStrings.Text(retrying ? "Reconnecting" : "Disconnected");
            SetGatewayRunPresentation("UnknownRun", false);
            if (error is not null)
            {
                ShowGatewayError(error);
            }
            NotifyGatewayCapabilities();
        });
        runtime.EventReceived += value => DispatchUiUpdate(() => HandleGatewayEvent(runtime, value));
        GatewayStatusText = GatewayStrings.Text("Connecting");
        _gatewayRosterLoading = true;
        NotifyGatewayRosterState();
        IsOpenClawPanelOpen = true;
        runtime.Start();
        _gatewayMonitorTask = MonitorGatewayAsync(runtime, cancellation.Token);
        _gatewayRetirements.RemoveAll(task => task.IsCompleted);
        _gatewayRetirements.Add(DisposeGatewayIdentityWhenStoppedAsync(runtime, () => identity, cancellation, _gatewayMonitorTask));
    }

    private bool IsCurrentGateway(OpenClawSessionRuntime runtime) => !_isDisposed && ReferenceEquals(_gatewayRuntime, runtime);

    private void StopGatewayRuntime()
    {
        _gatewaySearchCancellation?.Cancel();
        _gatewaySearchCancellation = null;
        _gatewayCancellation?.Cancel();
        _gatewayCancellation = null;
        _gatewayRuntime = null;
    }

    private async Task DisposeGatewayIdentityWhenStoppedAsync(OpenClawSessionRuntime runtime,
        Func<Task<OpenClawDeviceIdentity>?> identityAccessor, CancellationTokenSource cancellation, Task monitor)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await runtime.DisposeAsync();
            await monitor;
            if (identityAccessor() is { } task)
            {
                try
                {
                    (await task).Dispose();
                }
                catch (Exception ex) when (ex is OperationCanceledException or CryptographicException or IOException)
                {
                }
            }
        }
        catch (Exception ex)
        {
            _runtime.Logger.Warning("gateway.retirement.failed", new { kind = ex.GetType().Name });
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task MonitorGatewayAsync(OpenClawSessionRuntime runtime, CancellationToken cancellationToken)
    {
        var dispatcher = new UiTaskDispatcher(_dispatchToUi);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await dispatcher.RunAsync(async () =>
                    {
                        var client = runtime.Client;
                        if (!IsCurrentGateway(runtime) || client?.IsConnected != true || _gatewaySelectionChanging)
                        {
                            return;
                        }

                        var version = _gatewayTargetVersion;
                        var page = await _webViewService.ReadOpenClawSelectionAsync(new Uri(CurrentUrl), cancellationToken, client.Hello);
                        if (!IsCurrentGateway(runtime) || !ReferenceEquals(client, runtime.Client) || !client.IsConnected || _gatewaySelectionChanging || version != _gatewayTargetVersion)
                        {
                            return;
                        }

                        var sessionChanged = page?.SessionKey != _gatewayPage?.SessionKey;
                        var changed = sessionChanged || page?.RunId != _gatewayPage?.RunId;
                        SetGatewayPageSelection(page);
                        var scope = TakeGatewayRefreshRequest(changed, DateTimeOffset.UtcNow);
                        if (scope != GatewayRefreshScope.None)
                        {
                            await RefreshGatewayDataAsync(runtime, scope, cancellationToken);
                        }
                    }, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    await dispatcher.RunAsync(() =>
                    {
                        if (IsCurrentGateway(runtime))
                        {
                            SetGatewayPageSelection(null);
                            ShowGatewayError(ex);
                            NotifyGatewayCapabilities();
                        }
                    }, cancellationToken);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DispatchUiUpdate(() =>
            {
                if (IsCurrentGateway(runtime))
                {
                    ShowGatewayError(ex);
                }
            });
        }
    }
}
