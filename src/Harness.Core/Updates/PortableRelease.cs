// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Text.RegularExpressions;

namespace Harness.Updates;

public sealed record PortableRelease(string Version, Uri PageUrl, Uri DownloadUrl, Uri ChecksumUrl, long Size);
public sealed record UpdateProgress(string Phase, long Received = 0, long Total = 0);
public sealed class PortableUpdateException(string code, Exception? inner = null, DateTimeOffset? retryAfter = null) : Exception(code, inner)
{
    public string Code { get; } = code;
    public DateTimeOffset? RetryAfter { get; } = retryAfter;
}

public static partial class PortableReleaseVersion
{
    public static Version Parse(string value)
    {
        if (!NumericVersion().IsMatch(value) || !Version.TryParse(value, out var version))
        {
            throw new PortableUpdateException("InvalidRelease");
        }
        return version;
    }

    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericVersion();
}
