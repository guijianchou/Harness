// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.Services;

/// <summary>
/// Identifies why a notification was raised, so the shell can suppress the
/// categories a user does not want without dropping the rest.
/// </summary>
public enum NativeNotificationKind
{
    /// <summary>
    /// A hosted run finished: busy went idle. This is the category a browser tab
    /// cannot deliver on its own.
    /// </summary>
    RunCompleted,

    /// <summary>
    /// A Web Notification the hosted page raised, forwarded to the OS. Without
    /// forwarding, page notifications are silently dropped inside the shell.
    /// </summary>
    PageForwarded,

    /// <summary>
    /// A session needs the user: auth, pairing, or origin rejection.
    /// </summary>
    AttentionRequired,
}

/// <summary>
/// A notification the shell wants the OS to show.
/// </summary>
public sealed record NativeNotificationRequest
{
    /// <summary>
    /// Gets the notification category.
    /// </summary>
    public required NativeNotificationKind Kind { get; init; }

    /// <summary>
    /// Gets the short title line.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the body text.
    /// </summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>
    /// Gets the environment this notification belongs to, used to disambiguate
    /// when several environments are configured.
    /// </summary>
    public string EnvironmentName { get; init; } = string.Empty;
}
