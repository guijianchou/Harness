// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Harness.Models;
using Harness.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Harness.Core.Tests;

[TestClass]
public sealed class HostedBackendTests
{
    [TestMethod]
    [DataRow("\"DeepSeek\"")]
    [DataRow("2")]
    [DataRow("99")]
    public void LegacyAndUnsupportedBackendsMigrateWithoutChangingEnvironmentIdentity(string backendJson)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"harness-backend-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var settingsPath = Path.Combine(folder, "settings.json");
            File.WriteAllText(settingsPath, $$"""
                {
                  "systemTray": true,
                  "selectedEnvironmentName": "Existing",
                  "environments": [{
                    "id": "persistent-profile-id",
                    "name": "Existing",
                    "gatewayUrl": "https://example.org/chat",
                    "backend": {{backendJson}},
                    "openClawConnection": {
                      "enabled": false,
                      "credentialReference": "existing-credential"
                    }
                  }]
                }
                """);

            var configuration = new ConfigurationService(folder);
            configuration.Load();
            var environment = configuration.GetSelectedEnvironment();
            Assert.IsNotNull(environment);
            Assert.AreEqual(HostedBackendKind.Generic, environment.Backend);
            Assert.AreEqual("persistent-profile-id", environment.Id);
            Assert.AreEqual("https://example.org/chat", environment.GatewayUrl);
            Assert.AreEqual("existing-credential", environment.OpenClawConnection?.CredentialReference);
            Assert.IsTrue(configuration.Settings.SystemTray);

            using var saved = JsonDocument.Parse(File.ReadAllText(settingsPath));
            Assert.AreEqual("Generic", saved.RootElement.GetProperty("environments")[0].GetProperty("backend").GetString());
            var reloaded = new ConfigurationService(folder);
            reloaded.Load();
            Assert.AreEqual(HostedBackendKind.Generic, reloaded.GetSelectedEnvironment()?.Backend);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(HostedBackendKind.Auto)]
    [DataRow(HostedBackendKind.OpenClaw)]
    [DataRow(HostedBackendKind.Hermes)]
    [DataRow(HostedBackendKind.Generic)]
    public void SupportedBackendsRoundTripThroughSettingsSnapshots(HostedBackendKind backend)
    {
        var settings = new AppSettings
        {
            Environments = [new EnvironmentConfig { Name = "Agent", Backend = backend }],
        };
        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        var restored = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
        Assert.IsNotNull(restored);
        Assert.AreEqual(backend, restored.Environments[0].Backend);
        Assert.AreEqual(backend, restored.Environments[0].Clone().Backend);
    }

    [TestMethod]
    public void HermesDoesNotReuseThePersistedDeepSeekValue()
    {
        const string json = """{"environments":[{"backend":2},{"backend":4}]}""";
        var restored = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
        Assert.IsNotNull(restored);
        Assert.AreEqual(HostedBackendKind.Generic, restored.Environments[0].BackendProfile.Kind);
        Assert.AreEqual(HostedBackendKind.Hermes, restored.Environments[1].Backend);
    }

    [TestMethod]
    [DataRow(HostedBackendKind.Auto)]
    [DataRow(HostedBackendKind.Hermes)]
    [DataRow(HostedBackendKind.Generic)]
    [DataRow(HostedBackendKind.DeepSeek)]
    [DataRow((HostedBackendKind)99)]
    public void UnidentifiedAndNonOpenClawPagesHaveNoOpenClawCommandsOrProbe(HostedBackendKind backend)
    {
        var profile = HostedBackendProfile.For(backend);
        Assert.IsNull(profile.LatencyProbePath);
        Assert.IsNull(profile.AppStateElement);
        Assert.IsNull(profile.HostCommandEventPrefix);
        Assert.AreEqual(0, profile.CommandGlobals.Count);
        Assert.IsFalse(profile.AllowsRootLatencyFallback);
    }

    [TestMethod]
    [DataRow(HostedBackendKind.Auto)]
    [DataRow(HostedBackendKind.Hermes)]
    [DataRow(HostedBackendKind.Generic)]
    public async Task OrdinaryWebProbesPreserveTheConfiguredPath(HostedBackendKind backend)
    {
        using var handler = new RecordingProbeHandler();
        using var service = new ControlUiLatencyService(handler, TimeSpan.FromMinutes(1));
        service.Start("https://example.org/apps/index.html?view=chat#latest", HostedBackendProfile.For(backend));
        Assert.AreEqual("https://example.org/apps/index.html?view=chat", (await handler.NextRequestAsync()).AbsoluteUri);
    }

    [TestMethod]
    public async Task DedicatedOpenClawProbeOmitsPageQueryAndFragment()
    {
        using var handler = new RecordingProbeHandler();
        using var service = new ControlUiLatencyService(handler, TimeSpan.FromMinutes(1));
        service.Start("https://example.org/agent?session=main#chat", HostedBackendProfile.For(HostedBackendKind.OpenClaw));
        Assert.AreEqual("https://example.org/agent/__openclaw__/a2ui/", (await handler.NextRequestAsync()).AbsoluteUri);
    }

    [TestMethod]
    public async Task ChangingBackendAtTheSameUrlReplacesTheLatencyProbe()
    {
        using var handler = new RecordingProbeHandler();
        using var service = new ControlUiLatencyService(handler, TimeSpan.FromMinutes(1));
        const string url = "https://example.org/agent";
        service.Start(url, HostedBackendProfile.For(HostedBackendKind.OpenClaw));
        Assert.AreEqual("/agent/__openclaw__/a2ui/", (await handler.NextRequestAsync()).AbsolutePath);

        service.Start(url, HostedBackendProfile.For(HostedBackendKind.Hermes));
        Assert.AreEqual("/agent", (await handler.NextRequestAsync()).AbsolutePath);

        service.Start(url, HostedBackendProfile.For(HostedBackendKind.OpenClaw));
        Assert.AreEqual("/agent/__openclaw__/a2ui/", (await handler.NextRequestAsync()).AbsolutePath);
    }

    [TestMethod]
    [DataRow(HostedBackendKind.Auto)]
    [DataRow(HostedBackendKind.Hermes)]
    [DataRow(HostedBackendKind.Generic)]
    [DataRow(HostedBackendKind.OpenClaw)]
    public async Task MissingProbePathsDoNotSilentlyReportAnotherPagesLatency(HostedBackendKind backend)
    {
        using var handler = new RecordingProbeHandler(HttpStatusCode.NotFound);
        using var service = new ControlUiLatencyService(handler, TimeSpan.FromMinutes(1));
        var observed = new TaskCompletionSource<ControlUiLatencySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.LatencyUpdated += snapshot => observed.TrySetResult(snapshot);
        service.Start("https://example.org/agent", HostedBackendProfile.For(backend));

        var result = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(ControlUiLatencyState.Failure, result.State);
        Assert.IsTrue(result.IsPathAbsent);
        Assert.AreEqual(1, handler.RequestCount);
    }

    private sealed class RecordingProbeHandler(HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        private readonly Channel<Uri> _requests = Channel.CreateUnbounded<Uri>();
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public async Task<Uri> NextRequestAsync() =>
            await _requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            _requests.Writer.TryWrite(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }
}
