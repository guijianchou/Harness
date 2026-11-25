// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Globalization;
using System.Net;
using Harness.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Harness.Core.Tests;

public sealed partial class PortableUpdateTests
{
    [TestMethod]
    public async Task PrimaryRateLimitWaitsForResetWithoutSendingRepeatedChecks()
    {
        var clock = new UpdateTestClock();
        var reset = clock.GetUtcNow().AddMinutes(20);
        var requests = 0;
        using var http = Http(_ => ++requests == 1 ? Limited(HttpStatusCode.Forbidden,
            ("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))) : Json(Release("5.10.0")));
        var client = new GitHubReleaseClient(http, clock);

        var first = await Assert.ThrowsExactlyAsync<PortableUpdateException>(() => client.CheckAsync("5.3.8", default));
        Assert.AreEqual("RateLimited", first.Code);
        Assert.AreEqual(reset, first.RetryAfter);
        clock.Advance(TimeSpan.FromMinutes(19));
        var repeated = await Assert.ThrowsExactlyAsync<PortableUpdateException>(() => client.CheckAsync("5.3.9", default));
        Assert.AreEqual(reset, repeated.RetryAfter);
        Assert.AreEqual(1, requests, "The shared anonymous quota must also fence checks for a different installed version.");

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual("5.10.0", (await client.CheckAsync("5.3.8", default))?.Version);
        Assert.AreEqual(2, requests);
    }

    [TestMethod]
    public async Task RetryAfterSupportsSecondsAndHttpDates()
    {
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
        {
            foreach (var dateHeader in new[] { false, true })
            {
                var clock = new UpdateTestClock();
                var retryAt = clock.GetUtcNow().AddSeconds(90);
                var header = dateHeader ? retryAt.ToString("R", CultureInfo.InvariantCulture) : "90";
                using var http = Http(_ => Limited(status, ("X-RateLimit-Remaining", "42"), ("Retry-After", header)));
                var error = await Assert.ThrowsExactlyAsync<PortableUpdateException>(() => new GitHubReleaseClient(http, clock).CheckAsync("5.3.8", default));
                Assert.AreEqual("RateLimited", error.Code);
                Assert.AreEqual(retryAt, error.RetryAfter, $"{status}, HTTP date: {dateHeader}");
            }
        }
    }

    [TestMethod]
    public async Task RateLimitHonorsTheLaterServerDeadline()
    {
        var clock = new UpdateTestClock();
        var reset = clock.GetUtcNow().AddMinutes(5);
        using var http = Http(_ => Limited(HttpStatusCode.TooManyRequests, ("Retry-After", "60"),
            ("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))));
        var error = await Assert.ThrowsExactlyAsync<PortableUpdateException>(() => new GitHubReleaseClient(http, clock).CheckAsync("5.3.8", default));
        Assert.AreEqual(reset, error.RetryAfter);
    }

    [TestMethod]
    public async Task RateLimitWithoutAUsableFutureDeadlineWaitsAtLeastOneMinute()
    {
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
        {
            foreach (var headers in new[]
            {
                Array.Empty<(string, string)>(),
                new[] { ("Retry-After", "not-a-date"), ("X-RateLimit-Reset", "9999999999999999999") },
                new[] { ("Retry-After", "0"), ("X-RateLimit-Reset", "1") },
            })
            {
                var clock = new UpdateTestClock();
                var requests = 0;
                using var http = Http(_ =>
                {
                    requests++;
                    return Limited(status, headers.Append(("X-RateLimit-Remaining", "0")).ToArray());
                });
                var client = new GitHubReleaseClient(http, clock);
                var error = await Assert.ThrowsExactlyAsync<PortableUpdateException>(() => client.CheckAsync("5.3.8", default));
                Assert.AreEqual("RateLimited", error.Code);
                Assert.AreEqual(clock.GetUtcNow().AddMinutes(1), error.RetryAfter);
                clock.Advance(TimeSpan.FromSeconds(59));
                await Assert.ThrowsExactlyAsync<PortableUpdateException>(() => client.CheckAsync("5.3.8", default));
                Assert.AreEqual(1, requests);
            }
        }
    }

    [TestMethod]
    public async Task Headerless429IsRateLimitedButUnproven403RemainsAccessDenied()
    {
        var clock = new UpdateTestClock();
        using var throttled = Http(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var limited = await Assert.ThrowsExactlyAsync<PortableUpdateException>(() => new GitHubReleaseClient(throttled, clock).CheckAsync("5.3.8", default));
        Assert.AreEqual("RateLimited", limited.Code);
        Assert.AreEqual(clock.GetUtcNow().AddMinutes(1), limited.RetryAfter);

        foreach (var headers in new[]
        {
            Array.Empty<(string, string)>(),
            new[] { ("Retry-After", "bad"), ("X-RateLimit-Reset", clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)) },
        })
        {
            using var forbidden = Http(_ => Limited(HttpStatusCode.Forbidden, headers));
            var denied = await Assert.ThrowsExactlyAsync<PortableUpdateException>(() => new GitHubReleaseClient(forbidden, clock).CheckAsync("5.3.8", default));
            Assert.AreEqual("ReleaseAccessDenied", denied.Code);
            Assert.IsNull(denied.RetryAfter);
        }
    }

    [TestMethod]
    public async Task SuccessfulChecksCacheBothAvailableAndNoUpdateResultsPerInstalledVersion()
    {
        var clock = new UpdateTestClock();
        var requests = 0;
        using var http = Http(_ => { requests++; return Json(Release("5.10.0")); });
        var client = new GitHubReleaseClient(http, clock);

        var offered = await client.CheckAsync("5.3.8", default);
        Assert.AreSame(offered, await client.CheckAsync("5.3.8", default));
        Assert.IsNull(await client.CheckAsync("5.10.0", default));
        Assert.IsNull(await client.CheckAsync("5.10.0", default));
        Assert.AreSame(offered, await client.CheckAsync("5.3.8", default));
        Assert.AreEqual(2, requests, "A cached no-update result must not hide an available update for an older installation.");

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.IsNotNull(await client.CheckAsync("5.3.8", default));
        Assert.AreEqual(3, requests, "A completed result expires after five minutes.");
    }

    [TestMethod]
    public async Task MissingReleaseIsCachedWithoutBecomingAnError()
    {
        var clock = new UpdateTestClock();
        var requests = 0;
        using var http = Http(_ => { requests++; return new HttpResponseMessage(HttpStatusCode.NotFound); });
        var client = new GitHubReleaseClient(http, clock);
        Assert.IsNull(await client.CheckAsync("5.3.8", default));
        Assert.IsNull(await client.CheckAsync("5.3.8", default));
        Assert.AreEqual(1, requests);
    }

    [TestMethod]
    public async Task ConcurrentChecksShareTheCompletedResultAndCancelledWaitersDoNotCancelIt()
    {
        var clock = new UpdateTestClock();
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var http = new HttpClient(new AsyncUpdateHandler((_, token) =>
        {
            Interlocked.Increment(ref requests);
            entered.TrySetResult();
            return reply.Task.WaitAsync(token);
        }));
        var client = new GitHubReleaseClient(http, clock);
        var first = client.CheckAsync("5.3.8", default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var others = Enumerable.Range(0, 4).Select(_ => client.CheckAsync("5.3.8", default)).ToArray();
        using var cancelled = new CancellationTokenSource();
        var waiter = client.CheckAsync("5.3.8", cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiter);
        reply.SetResult(Json(Release("5.10.0")));
        var result = await first;
        foreach (var other in others) { Assert.AreSame(result, await other); }
        Assert.AreEqual(1, requests);
    }

    [TestMethod]
    public async Task FailedAndCancelledChecksDoNotCacheAnUpToDateResultOrLeakTheGate()
    {
        foreach (var fail in new Func<HttpResponseMessage>[]
        {
            () => new(HttpStatusCode.Forbidden),
            () => Json("not json"),
            () => throw new HttpRequestException("fixture"),
            () => throw new OperationCanceledException(),
        })
        {
            var requests = 0;
            using var http = Http(_ => ++requests == 1 ? fail() : Json(Release("5.10.0")));
            var client = new GitHubReleaseClient(http, new UpdateTestClock());
            await Assert.ThrowsAsync<Exception>(() => client.CheckAsync("5.3.8", default));
            Assert.AreEqual("5.10.0", (await client.CheckAsync("5.3.8", default))?.Version);
            Assert.AreEqual(2, requests);
        }
    }

    [TestMethod]
    public async Task DownloadRateLimitRetainsTheServerDeadlineAndCreatesNoArchive()
    {
        using var tree = new TempTree();
        var clock = new UpdateTestClock();
        using var http = Http(_ => Limited(HttpStatusCode.TooManyRequests, ("Retry-After", "120")));
        var path = Path.Combine(tree.Root, "limited.zip");
        var error = await Assert.ThrowsExactlyAsync<PortableUpdateException>(() => new GitHubReleaseClient(http, clock)
            .DownloadAsync(DownloadRelease(100), path, null, default));
        Assert.AreEqual("RateLimited", error.Code);
        Assert.AreEqual(clock.GetUtcNow().AddMinutes(2), error.RetryAfter);
        Assert.IsFalse(File.Exists(path));
        Assert.IsFalse(File.Exists(path + ".partial"));
    }

    private static HttpResponseMessage Limited(HttpStatusCode status, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status);
        foreach (var (name, value) in headers) { response.Headers.TryAddWithoutValidation(name, value); }
        return response;
    }

    private sealed class UpdateTestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class AsyncUpdateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
