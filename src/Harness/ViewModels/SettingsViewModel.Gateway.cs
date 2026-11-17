// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Helpers;
using Harness.Models;
using Harness.Services;

namespace Harness.ViewModels;

public partial class SettingsViewModel
{
    private readonly Dictionary<string, OpenClawCredentials> _pendingCredentials = new(StringComparer.Ordinal);
    private bool _editGatewayEnabled;
    private string _editGatewayWebSocketUrl = string.Empty;
    private int _editGatewayAuthModeIndex;
    private string _editGatewaySecret = string.Empty;
    private string _editGatewayAccessToken = string.Empty;
    private bool _editGatewayClearAccessToken;

    public bool IsOpenClawBackend => _editBackend == HostedBackendKind.OpenClaw;

    public bool EditGatewayEnabled
    {
        get => _editGatewayEnabled;
        set { _editGatewayEnabled = value; OnPropertyChanged(); }
    }

    public string EditGatewayWebSocketUrl
    {
        get => _editGatewayWebSocketUrl;
        set { _editGatewayWebSocketUrl = value; OnPropertyChanged(); }
    }

    public int EditGatewayAuthModeIndex
    {
        get => _editGatewayAuthModeIndex;
        set { _editGatewayAuthModeIndex = value; OnPropertyChanged(); OnPropertyChanged(nameof(UsesGatewaySecret)); }
    }

    public bool UsesGatewaySecret => EditGatewayAuthModeIndex != (int)OpenClawAuthMode.ExistingProxy;

    public string EditGatewaySecret
    {
        get => _editGatewaySecret;
        set { _editGatewaySecret = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<string> GatewayAuthOptions { get; } = (string[])
    [StringResources.SettingsGatewayAuthToken, StringResources.SettingsGatewayAuthPassword, StringResources.SettingsGatewayAuthProxy];

    public string EditGatewayAccessToken
    {
        get => _editGatewayAccessToken;
        set { _editGatewayAccessToken = value; OnPropertyChanged(); }
    }

    public bool EditGatewayClearAccessToken
    {
        get => _editGatewayClearAccessToken;
        set { _editGatewayClearAccessToken = value; OnPropertyChanged(); }
    }

    public string GatewayCredentialStatus =>
        _selectedEnvironment is not null && (_pendingCredentials.ContainsKey(_selectedEnvironment.Id) ||
            !string.IsNullOrWhiteSpace(_selectedEnvironment.OpenClawConnection?.CredentialReference))
            ? StringResources.SettingsGatewayCredentialSaved
            : StringResources.SettingsGatewayCredentialEmpty;

    private void LoadGatewayFields()
    {
        var options = _selectedEnvironment?.OpenClawConnection;
        EditGatewayEnabled = options?.Enabled ?? false;
        EditGatewayWebSocketUrl = options?.WebSocketUrl ?? string.Empty;
        EditGatewayAuthModeIndex = (int)(options?.AuthMode ?? OpenClawAuthMode.Token);
        EditGatewaySecret = string.Empty;
        EditGatewayAccessToken = string.Empty;
        EditGatewayClearAccessToken = false;
        OnPropertyChanged(nameof(GatewayCredentialStatus));
    }

    private OpenClawConnectionOptions? CreateGatewayOptions()
    {
        if (!IsOpenClawBackend)
        {
            return _selectedEnvironment?.OpenClawConnection is { } existing ? existing with { Enabled = false } : null;
        }

        return new OpenClawConnectionOptions
        {
            Enabled = EditGatewayEnabled,
            WebSocketUrl = string.IsNullOrWhiteSpace(EditGatewayWebSocketUrl) ? null : EditGatewayWebSocketUrl.Trim(),
            AuthMode = Enum.IsDefined((OpenClawAuthMode)EditGatewayAuthModeIndex) ? (OpenClawAuthMode)EditGatewayAuthModeIndex : OpenClawAuthMode.Token,
            CredentialReference = _selectedEnvironment?.OpenClawConnection?.CredentialReference,
        };
    }

    private void StageGatewayCredential()
    {
        if (_selectedEnvironment is not null &&
            ((!string.IsNullOrEmpty(EditGatewaySecret) && UsesGatewaySecret) || !string.IsNullOrEmpty(EditGatewayAccessToken) || EditGatewayClearAccessToken))
        {
            var pending = _pendingCredentials.GetValueOrDefault(_selectedEnvironment.Id) ?? new OpenClawCredentials();
            if (!string.IsNullOrEmpty(EditGatewaySecret) && UsesGatewaySecret)
            {
                pending = EditGatewayAuthModeIndex == (int)OpenClawAuthMode.Password
                    ? pending with { Password = EditGatewaySecret }
                    : pending with { Token = EditGatewaySecret };
            }
            if (EditGatewayClearAccessToken || !string.IsNullOrEmpty(EditGatewayAccessToken))
            {
                pending = pending with { AccessToken = EditGatewayClearAccessToken ? string.Empty : EditGatewayAccessToken.Trim() };
            }
            _pendingCredentials[_selectedEnvironment.Id] = pending;
            EditGatewaySecret = string.Empty;
            EditGatewayAccessToken = string.Empty;
            EditGatewayClearAccessToken = false;
        }
        OnPropertyChanged(nameof(GatewayCredentialStatus));
    }

    private void RetireChangedGatewayCredential(EnvironmentConfig draft)
    {
        var previous = _selectedEnvironment;
        if (previous is null || draft.OpenClawConnection is null) { return; }
        if (!string.Equals(previous.GatewayUrl, draft.GatewayUrl, StringComparison.Ordinal) ||
            !string.Equals(previous.OpenClawConnection?.WebSocketUrl, draft.OpenClawConnection.WebSocketUrl, StringComparison.Ordinal) ||
            previous.OpenClawConnection?.AuthMode != draft.OpenClawConnection.AuthMode)
        {
            draft.OpenClawConnection.CredentialReference = null;
            _pendingCredentials.Remove(draft.Id);
        }
    }

    internal async Task<SettingsSaveResult?> SaveAllAsync(
        OpenClawCredentialStore credentialStore,
        StartupRegistrationService? startupRegistration = null)
    {
        if (!TryValidateAppearanceSettings())
        {
            return null;
        }

        var staged = new List<(EnvironmentConfig Environment, string? PreviousReference, string Reference)>();
        StartupRegistrationChange? startupChange = null;
        try
        {
            foreach (var environment in Environments)
            {
                if (environment.OpenClawConnection is not { } options || !_pendingCredentials.TryGetValue(environment.Id, out var credential)) { continue; }
                var previous = string.IsNullOrWhiteSpace(options.CredentialReference) ? null : await credentialStore.ReadAsync(options.CredentialReference);
                var endpoint = OpenClawEndpoint.Canonical(environment);
                if (!string.Equals(previous?.Endpoint, endpoint, StringComparison.Ordinal)) { previous = null; }
                var replacement = credential with
                {
                    Token = options.AuthMode == OpenClawAuthMode.Token ? credential.Token ?? previous?.Token : null,
                    Password = options.AuthMode == OpenClawAuthMode.Password ? credential.Password ?? previous?.Password : null,
                    AccessToken = credential.AccessToken ?? previous?.AccessToken,
                    Endpoint = endpoint,
                };
                var reference = Guid.NewGuid().ToString("N");
                await credentialStore.WriteAsync(reference, replacement);
                staged.Add((environment, options.CredentialReference, reference));
                environment.OpenClawConnection = options with { CredentialReference = reference };
            }

            if (_didEditLaunchAtStartup)
            {
                try
                {
                    startupChange = await (startupRegistration ?? new StartupRegistrationService()).ApplyAsync(LaunchAtStartup);
                }
                catch (AggregateException)
                {
                    ValidationMessage = StringResources.SettingsStartupRollbackFailed;
                    return null;
                }
                catch (Exception)
                {
                    ValidationMessage = StringResources.SettingsStartupSaveFailed;
                    return null;
                }
            }

            if (!SaveAll(out var result)) { return null; }
            startupChange = null;
            _didEditLaunchAtStartup = false;
            _pendingCredentials.Clear();
            // Old references may still be used by another live instance; never delete them before its configuration reload.
            staged.Clear();
            return result;
        }
        catch (Exception)
        {
            ValidationMessage = StringResources.SettingsGatewaySecureSaveFailed;
            return null;
        }
        finally
        {
            foreach (var entry in staged)
            {
                entry.Environment.OpenClawConnection = entry.Environment.OpenClawConnection! with { CredentialReference = entry.PreviousReference };
                try { await credentialStore.DeleteAsync(entry.Reference); } catch { }
            }

            if (startupChange is not null)
            {
                try
                {
                    if (!await startupChange.RollbackAsync())
                    {
                        ValidationMessage = StringResources.SettingsStartupRollbackFailed;
                    }
                }
                catch (Exception)
                {
                    ValidationMessage = StringResources.SettingsStartupRollbackFailed;
                }
            }
        }
    }

    private static bool TryValidateGatewayOptions(EnvironmentConfig environment, out string errorMessage)
    {
        errorMessage = StringResources.SettingsValidationDefaultMessage;
        if (environment.OpenClawConnection is not { Enabled: true } options) { return true; }
        if (environment.Backend != HostedBackendKind.OpenClaw)
        {
            errorMessage = StringResources.SettingsGatewayRequiresOpenClaw;
            return false;
        }
        if (!string.IsNullOrWhiteSpace(options.WebSocketUrl) &&
            (!Uri.TryCreate(options.WebSocketUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss") ||
             !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)))
        {
            errorMessage = StringResources.SettingsGatewayInvalidUrl;
            return false;
        }
        return true;
    }
}
