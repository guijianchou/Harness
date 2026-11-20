// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Helpers;
using Harness.OpenClaw;

namespace Harness.ViewModels;

public enum GatewayRecoveryAction { None, Configure, Refresh, ReturnToPage }

public partial class MainViewModel
{
    private GatewayRecoveryAction _gatewayErrorAction;
    public GatewayRecoveryAction GatewayErrorAction => _gatewayErrorAction;
    public bool GatewayHasErrorAction => GatewayHasError && GatewayErrorAction != GatewayRecoveryAction.None;
    public string GatewayErrorActionText => GatewayStrings.Text(GatewayErrorAction switch
    {
        GatewayRecoveryAction.Configure => "Configure",
        GatewayRecoveryAction.ReturnToPage => "ReturnToPage",
        _ => "Refresh",
    });

    private void SetGatewayIssue(string message, GatewayRecoveryAction action)
    {
        GatewayErrorText = message;
        _gatewayErrorAction = action;
        OnPropertyChanged(nameof(GatewayErrorAction));
        OnPropertyChanged(nameof(GatewayErrorActionText));
        OnPropertyChanged(nameof(GatewayHasErrorAction));
    }

    private static GatewayRecoveryAction GetGatewayRecoveryAction(Exception error) => error switch
    {
        OpenClawGatewayException { PairingRequestId: not null } => GatewayRecoveryAction.Refresh,
        OpenClawGatewayException { DetailCode: "CREDENTIAL_ENDPOINT_CHANGED" } => GatewayRecoveryAction.Configure,
        OpenClawGatewayException { Retryable: true } => GatewayRecoveryAction.Refresh,
        OpenClawGatewayException { Code: "UNAVAILABLE" } => GatewayRecoveryAction.Refresh,
        OpenClawGatewayException { Kind: OpenClawFailureKind.EdgeAuthenticationRequired or OpenClawFailureKind.Rejected or OpenClawFailureKind.UnsupportedProtocol } => GatewayRecoveryAction.Configure,
        _ => GatewayRecoveryAction.Refresh,
    };
}
