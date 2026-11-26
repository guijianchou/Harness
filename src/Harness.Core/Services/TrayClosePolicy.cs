// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.Services;

public sealed class TrayClosePolicy
{
    private static readonly TimeSpan UnavailableTrayExitConfirmationWindow = TimeSpan.FromSeconds(15);
    private bool _isExitRequested;
    private DateTimeOffset? _unavailableTrayCloseAt;

    public TrayCloseDisposition GetCloseDisposition(bool systemTray)
    {
        if (_isExitRequested || !systemTray)
        {
            _unavailableTrayCloseAt = null;
            return TrayCloseDisposition.Exit;
        }

        return TrayCloseDisposition.HideToTray;
    }

    public TrayCloseDisposition GetUnavailableTrayCloseDisposition(DateTimeOffset now)
    {
        if (_isExitRequested)
        {
            return TrayCloseDisposition.Exit;
        }

        if (_unavailableTrayCloseAt is { } previousClose &&
            now >= previousClose && now - previousClose <= UnavailableTrayExitConfirmationWindow)
        {
            RequestExit();
            return TrayCloseDisposition.Exit;
        }

        _unavailableTrayCloseAt = now;
        return TrayCloseDisposition.KeepVisible;
    }

    public void NotifyTrayAvailable()
    {
        _unavailableTrayCloseAt = null;
    }

    public void RequestExit()
    {
        _isExitRequested = true;
        _unavailableTrayCloseAt = null;
    }
}

public enum TrayCloseDisposition
{
    HideToTray,
    Exit,
    KeepVisible,
}
