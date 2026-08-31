// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Models;

namespace Harness.ViewModels;

public partial class MainViewModel
{
    private void UpdateStatusPresentation()
    {
        var presentation = CreateStatusPresentation();
        StatusMessage = presentation.Text;
        StatusIndicatorBrush = presentation.Brush;
    }

    private StatusPresentation CreateStatusPresentation()
    {
        return _statusPresenter.FormatShellStatus(
            ShellConnectionState,
            RecoveryMessage,
            ConnectionState,
            CurrentStatusBrushes);
    }
}
