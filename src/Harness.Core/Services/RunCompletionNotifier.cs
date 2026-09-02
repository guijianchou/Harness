// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.Services;

/// <summary>
/// Decides when a hosted run has finished and a notification is warranted.
/// Pure state machine so the edge-detection rules are testable without WinUI.
/// </summary>
public sealed class RunCompletionNotifier
{
    private bool _wasBusy;
    private bool _sawBusyThisSession;

    /// <summary>
    /// Gets whether the last observed work state was busy.
    /// </summary>
    public bool IsTrackingBusyRun => _wasBusy;

    /// <summary>
    /// Records a work-state observation and returns whether it completed a run.
    /// </summary>
    /// <param name="isBusy">Whether the hosted UI currently reports work in flight.</param>
    /// <param name="isConnected">
    /// Whether the session is connected. A busy→idle edge that coincides with
    /// losing the session is a disconnect, not a completion, so it does not notify.
    /// </param>
    /// <param name="isWindowFocused">Whether the shell window currently has focus.</param>
    /// <param name="notifyOnRunCompleted">Whether completion notifications are enabled.</param>
    /// <param name="notifyOnlyWhenUnfocused">
    /// Whether to suppress the notification while the user is watching.
    /// </param>
    public bool ShouldNotifyOnObservation(
        bool isBusy,
        bool isConnected,
        bool isWindowFocused,
        bool notifyOnRunCompleted,
        bool notifyOnlyWhenUnfocused)
    {
        var wasBusy = _wasBusy;
        _wasBusy = isBusy;

        if (isBusy)
        {
            _sawBusyThisSession = true;
            return false;
        }

        var completedRun = wasBusy && _sawBusyThisSession;
        if (!completedRun)
        {
            return false;
        }

        _sawBusyThisSession = false;

        // A run that "finishes" because the session dropped is a failure, and the
        // recovery path already surfaces it. Notifying here would report a lost
        // connection as a completed run.
        if (!isConnected)
        {
            return false;
        }

        if (!notifyOnRunCompleted)
        {
            return false;
        }

        return !notifyOnlyWhenUnfocused || !isWindowFocused;
    }

    /// <summary>
    /// Clears tracked state. Called when the session is replaced (environment
    /// switch, WebView recreation) so a stale busy flag cannot fire a completion
    /// notification for a run that belonged to the previous session.
    /// </summary>
    public void Reset()
    {
        _wasBusy = false;
        _sawBusyThisSession = false;
    }
}
