// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.Services;

public sealed class TrayClosePolicy
{
    private bool _isExitRequested;

    public TrayCloseDisposition GetCloseDisposition(bool systemTray) =>
        _isExitRequested || !systemTray ? TrayCloseDisposition.Exit : TrayCloseDisposition.HideToTray;

    public void RequestExit()
    {
        _isExitRequested = true;
    }
}

public enum TrayCloseDisposition
{
    HideToTray,
    Exit,
}
