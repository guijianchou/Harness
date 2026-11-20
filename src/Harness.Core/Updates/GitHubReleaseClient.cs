// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Harness.Updates;

public sealed class GitHubReleaseClient(HttpClient http)
{
    public const long MaximumDownloadSize = 512L * 1024 * 1024;
    private const string Repository = "guijianchou/Harness";
    private static readonly Uri LatestRelease = new($"https://api.github.com/repos/{Repository}/releases/latest");

    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(15),
    };

    public async Task<PortableRelease?> CheckAsync(string currentVersion, CancellationToken token)
    {
        var current = PortableReleaseVersion.Parse(currentVersion);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await SendAsync(LatestRelease, false, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) { return null; }
        EnsureSuccess(response);
        var json = await ReadBoundedAsync(response, 1024 * 1024, timeout.Token).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) { return null; }
            var version = root.GetProperty("tag_name").GetString() ?? "";
            if (PortableReleaseVersion.Parse(version) <= current) { return null; }
            var page = new Uri(root.GetProperty("html_url").GetString()!);
            if (!IsRepositoryUrl(page, $"releases/tag/{version}")) { throw new PortableUpdateException("InvalidRelease"); }
            var name = $"Harness-{version}-win-x64.zip";
            var assets = root.GetProperty("assets").EnumerateArray().ToArray();
            var archive = FindAsset(assets, name, version);
            var checksum = FindAsset(assets, name + ".sha256", version);
            var size = archive.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaximumDownloadSize || checksum.GetProperty("size").GetInt64() is <= 0 or > 1024)
            {
                throw new PortableUpdateException("InvalidRelease");
            }
            return new PortableRelease(version, page, new Uri(archive.GetProperty("browser_download_url").GetString()!),
                new Uri(checksum.GetProperty("browser_download_url").GetString()!), size);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or UriFormatException or FormatException)
        {
            throw new PortableUpdateException("InvalidRelease", ex);
        }
    }

    public async Task DownloadAsync(PortableRelease release, string destination, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        token = timeout.Token;
        var name = $"Harness-{release.Version}-win-x64.zip";
        PortableReleaseVersion.Parse(release.Version);
        if (!IsRepositoryUrl(release.DownloadUrl, $"releases/download/{release.Version}/{name}") ||
            !IsRepositoryUrl(release.ChecksumUrl, $"releases/download/{release.Version}/{name}.sha256") ||
            release.Size <= 0 || release.Size > MaximumDownloadSize)
        {
            throw new PortableUpdateException("InvalidRelease");
        }
        using var checksumResponse = await SendAsync(release.ChecksumUrl, true, token).ConfigureAwait(false);
        EnsureSuccess(checksumResponse);
        var checksum = Encoding.UTF8.GetString(await ReadBoundedAsync(checksumResponse, 1024, token).ConfigureAwait(false))
            .Trim('\uFEFF', ' ', '\r', '\n').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (checksum.Length != 2 || checksum[0].Length != 64 || !checksum[0].All(Uri.IsHexDigit) || checksum[1] != name)
        {
            throw new PortableUpdateException("InvalidRelease");
        }
        using var response = await SendAsync(release.DownloadUrl, true, token).ConfigureAwait(false);
        EnsureSuccess(response);
        if (response.Content.Headers.ContentLength is { } length && length != release.Size)
        {
            throw new PortableUpdateException("ChecksumMismatch");
        }
        var partial = destination + ".partial";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                long received = 0;
                var lastReport = Environment.TickCount64;
                int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    received += count;
                    if (received > release.Size) { throw new PortableUpdateException("ChecksumMismatch"); }
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    if (Environment.TickCount64 - lastReport >= 100 || received == release.Size)
                    {
                        progress?.Report(new("Downloading", received, release.Size));
                        lastReport = Environment.TickCount64;
                    }
                }
                if (received != release.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(checksum[0])))
                {
                    throw new PortableUpdateException("ChecksumMismatch");
                }
            }
            File.Move(partial, destination);
        }
        finally
        {
            if (File.Exists(partial)) { File.Delete(partial); }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, bool download, CancellationToken token)
    {
        for (var redirects = 0; redirects < 6; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Harness", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(download ? "application/octet-stream" : "application/vnd.github+json"));
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) { return response; }
            var location = response.Headers.Location;
            response.Dispose();
            if (!download || location is null) { throw new PortableUpdateException("InvalidRelease"); }
            uri = new Uri(uri, location);
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                (uri.Host != "release-assets.githubusercontent.com" && uri.Host != "github.com"))
            {
                throw new PortableUpdateException("InvalidRelease");
            }
        }
        throw new PortableUpdateException("InvalidRelease");
    }

    private static JsonElement FindAsset(JsonElement[] assets, string name, string version)
    {
        var matches = assets.Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
        if (matches.Length != 1 || matches[0].GetProperty("state").GetString() != "uploaded" ||
            !Uri.TryCreate(matches[0].GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url) ||
            !IsRepositoryUrl(url, $"releases/download/{version}/{name}"))
        {
            throw new PortableUpdateException("InvalidRelease");
        }
        return matches[0];
    }

    private static bool IsRepositoryUrl(Uri uri, string suffix) => uri.Scheme == "https" && uri.Host == "github.com" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.AbsolutePath.Equals($"/{Repository}/{suffix}", StringComparison.OrdinalIgnoreCase);

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) { throw new PortableUpdateException("RateLimited"); }
        response.EnsureSuccessStatusCode();
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > limit) { throw new PortableUpdateException("InvalidRelease"); }
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
