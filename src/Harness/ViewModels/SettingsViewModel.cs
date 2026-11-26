// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Harness.Helpers;
using Harness.Models;
using Harness.Services;

namespace Harness.ViewModels;

/// <summary>
/// ViewModel for the Settings dialog.
/// Manages CRUD operations on gateway environment configurations.
/// </summary>
public partial class SettingsViewModel : INotifyPropertyChanged
{
    private static readonly HostedBackendKind[] SelectableBackends =
    [HostedBackendKind.Auto, HostedBackendKind.OpenClaw, HostedBackendKind.Hermes, HostedBackendKind.Generic];

    private readonly Dictionary<EnvironmentConfig, EnvironmentConfig> _originalSnapshots = [];
    private readonly SettingsPersistenceAdapter _settingsPersistence;
    private List<EnvironmentConfig> _environmentBaseline;
    private readonly string? _originalSelectedEnvironmentId;
    private bool _didEditSelectedLanguage;
    private bool _didEditEnableDevLog;
    private bool _didEditSystemTray;
    private bool _didEditAutoRefresh;
    private bool _didEditAllowMultipleInstances;
    private bool _didEditEnableGlobalHotkey;
    private bool _didEditGlobalHotkey;
    private bool _didEditAlwaysOnTop;
    private EnvironmentConfig? _selectedEnvironment;
    private string _editName = string.Empty;
    private string _editUrl = string.Empty;
    private bool _editIsDefault;
    private HostedBackendKind _editBackend = HostedBackendKind.Auto;
    private bool _isEditing;
    private string _selectedLanguage = "System";
    private bool _enableDevLog;
    private bool _systemTray;
    private bool _autoRefresh;
    private bool _allowMultipleInstances;
    private bool _enableGlobalHotkey;
    private string _globalHotkey = string.Empty;
    private bool _alwaysOnTop;
    private string _validationMessage = string.Empty;

    internal SettingsViewModel(SettingsPersistenceAdapter settingsPersistence)
    {
        _settingsPersistence = settingsPersistence ?? throw new ArgumentNullException(nameof(settingsPersistence));
        var settings = _settingsPersistence.CreateDraft();
        _environmentBaseline = settings.Environments.Select(env => env.Clone()).ToList();
        _originalSelectedEnvironmentId = settings.Environments
            .FirstOrDefault(env => string.Equals(env.Name, settings.SelectedEnvironmentName, StringComparison.Ordinal))?.Id;

        // Load a copy of environments so we can cancel without persisting
        foreach (var env in settings.Environments)
        {
            var clone = env.Clone();
            Environments.Add(clone);
            _originalSnapshots[clone] = env.Clone();
        }

        // Load language preference
        _selectedLanguage = settings.AppLanguage ?? "System";
        _enableDevLog = settings.Diagnostics.EnableVerboseRecoveryLogging;
        _systemTray = settings.SystemTray;
        _autoRefresh = settings.AutoRefresh;
        _allowMultipleInstances = settings.AllowMultipleInstances;
        _enableGlobalHotkey = settings.EnableGlobalHotkey;
        _globalHotkey = settings.GlobalHotkey;
        _alwaysOnTop = settings.AlwaysOnTop;
        LoadAppearanceSettings(settings);
        _validationMessage = StringResources.SettingsValidationDefaultMessage;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<EnvironmentConfig> Environments { get; } = [];

    public EnvironmentConfig? SelectedEnvironment
    {
        get => _selectedEnvironment;
        set
        {
            _selectedEnvironment = value;
            OnPropertyChanged();
            LoadEditFields();
        }
    }

    public string EditName
    {
        get => _editName;
        set { _editName = value; OnPropertyChanged(); }
    }

    public string EditUrl
    {
        get => _editUrl;
        set { _editUrl = value; OnPropertyChanged(); }
    }

    public bool EditIsDefault
    {
        get => _editIsDefault;
        set { _editIsDefault = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Gets the selectable backend labels in display order, independent of saved enum values.
    /// </summary>
    public IReadOnlyList<string> BackendOptions { get; } = (string[])
    [
        StringResources.SettingsBackendAuto,
        StringResources.SettingsBackendOpenClaw,
        StringResources.SettingsBackendHermes,
        StringResources.SettingsBackendGeneric,
    ];

    /// <summary>
    /// Gets or sets the edited environment's backend as an index into
    /// <see cref="BackendOptions"/>, because ComboBox SelectedIndex binds to int.
    /// </summary>
    public int EditBackendIndex
    {
        get => GetBackendIndex(_editBackend);
        set
        {
            if (value < 0 || value >= SelectableBackends.Length)
            {
                return;
            }

            var kind = SelectableBackends[value];
            if (_editBackend == kind)
            {
                return;
            }

            _editBackend = kind;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOpenClawBackend));
            OnPropertyChanged(nameof(EditBackendDescription));
        }
    }

    public string EditBackendDescription => _editBackend switch
    {
        HostedBackendKind.OpenClaw => StringResources.SettingsBackendOpenClawHint,
        HostedBackendKind.Hermes => StringResources.SettingsBackendHermesHint,
        HostedBackendKind.Auto => StringResources.SettingsBackendHint,
        _ => StringResources.SettingsBackendGenericHint,
    };

    private static int GetBackendIndex(HostedBackendKind kind)
    {
        var index = Array.IndexOf(SelectableBackends, kind);
        return index >= 0 ? index : Array.IndexOf(SelectableBackends, HostedBackendKind.Generic);
    }

    public bool IsEditing
    {
        get => _isEditing;
        set { _isEditing = value; OnPropertyChanged(); }
    }

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (string.Equals(_selectedLanguage, value, StringComparison.Ordinal))
            {
                return;
            }

            _selectedLanguage = value;
            _didEditSelectedLanguage = true;
            OnPropertyChanged();
        }
    }

    public bool EnableDevLog
    {
        get => _enableDevLog;
        set
        {
            if (_enableDevLog == value)
            {
                return;
            }

            _enableDevLog = value;
            _didEditEnableDevLog = true;
            OnPropertyChanged();
        }
    }

    public bool SystemTray
    {
        get => _systemTray;
        set
        {
            if (_systemTray == value)
            {
                return;
            }

            _systemTray = value;
            _didEditSystemTray = true;
            OnPropertyChanged();
        }
    }

    public bool AutoRefresh
    {
        get => _autoRefresh;
        set
        {
            if (_autoRefresh == value)
            {
                return;
            }

            _autoRefresh = value;
            _didEditAutoRefresh = true;
            OnPropertyChanged();
        }
    }

    public bool AllowMultipleInstances
    {
        get => _allowMultipleInstances;
        set
        {
            if (_allowMultipleInstances == value)
            {
                return;
            }

            _allowMultipleInstances = value;
            _didEditAllowMultipleInstances = true;
            OnPropertyChanged();
        }
    }

    public bool EnableGlobalHotkey
    {
        get => _enableGlobalHotkey;
        set
        {
            if (_enableGlobalHotkey == value)
            {
                return;
            }

            _enableGlobalHotkey = value;
            _didEditEnableGlobalHotkey = true;
            OnPropertyChanged();
        }
    }

    public string GlobalHotkey
    {
        get => _globalHotkey;
        set
        {
            if (string.Equals(_globalHotkey, value, StringComparison.Ordinal))
            {
                return;
            }

            _globalHotkey = value;
            _didEditGlobalHotkey = true;
            OnPropertyChanged();
        }
    }

    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set
        {
            if (_alwaysOnTop == value)
            {
                return;
            }

            _alwaysOnTop = value;
            _didEditAlwaysOnTop = true;
            OnPropertyChanged();
        }
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            _validationMessage = value;
            OnPropertyChanged();
        }
    }

    public bool DidChangeSessionTopology { get; private set; }

    public bool DidChangeEnvironmentState { get; private set; }

    public void ResetGlobalHotkey()
    {
        EnableGlobalHotkey = true;
        GlobalHotkey = new AppSettings().GlobalHotkey;
    }

    /// <summary>
    /// Adds a new environment with placeholder values.
    /// </summary>
    public void AddEnvironment()
    {
        var env = new EnvironmentConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.Format(StringResources.SettingsGeneratedEnvironmentName, Environments.Count + 1),
            GatewayUrl = "https://",
            IsDefault = Environments.Count == 0,
        };
        Environments.Add(env);
        _originalSnapshots[env] = env.Clone();
        DidChangeEnvironmentState = true;
        DidChangeSessionTopology = true;
        SelectedEnvironment = env;
        IsEditing = true;
    }

    /// <summary>
    /// Removes the currently selected environment.
    /// </summary>
    public void RemoveEnvironment()
    {
        if (_selectedEnvironment is null)
        {
            return;
        }

        _originalSnapshots.Remove(_selectedEnvironment);
        _pendingCredentials.Remove(_selectedEnvironment.Id);
        Environments.Remove(_selectedEnvironment);
        DidChangeEnvironmentState = true;
        DidChangeSessionTopology = true;
        SelectedEnvironment = Environments.FirstOrDefault();
    }

    /// <summary>
    /// Applies edit field values back to the selected environment.
    /// </summary>
    public bool TryApplyEdit()
    {
        if (_selectedEnvironment is null)
        {
            ValidationMessage = StringResources.SettingsValidationSelectEnvironment;
            return false;
        }

        var draft = CreateDraftEnvironment();

        if (!TryValidateEnvironment(draft, out var errorMessage))
        {
            ValidationMessage = errorMessage;
            return false;
        }

        RetireChangedGatewayCredential(draft);
        _selectedEnvironment.ApplyFrom(draft);
        StageGatewayCredential();
        DidChangeEnvironmentState |= DidEnvironmentMetadataChange(_selectedEnvironment);
        DidChangeSessionTopology |= DidEnvironmentSessionIdentityChange(_selectedEnvironment);

        // If setting as default, clear others
        if (EditIsDefault)
        {
            foreach (var env in Environments)
            {
                env.IsDefault = env == _selectedEnvironment;
            }
        }
        else
        {
            _selectedEnvironment.IsDefault = false;
        }

        IsEditing = true;

        ValidationMessage = StringResources.SettingsValidationDefaultMessage;
        return true;
    }

    /// <summary>
    /// Saves all environments to the configuration service.
    /// Returns true if save was successful.
    /// </summary>
    public bool SaveAll(out SettingsSaveResult result)
    {
        result = default;

        if (!TryValidateAppearanceSettings())
        {
            return false;
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var env in Environments)
        {
            if (!TryValidateEnvironment(env, out var errorMessage))
            {
                ValidationMessage = errorMessage;
                return false;
            }

            if (!seenNames.Add(env.Name.Trim()))
            {
                ValidationMessage = string.Format(StringResources.SettingsValidationDuplicateEnvironment, env.Name);
                return false;
            }
        }

        if (!TryValidateHotkey(out var hotkeyErrorMessage))
        {
            ValidationMessage = hotkeyErrorMessage;
            return false;
        }

        var settings = _settingsPersistence.CreateDraft(out var baseline);
        var didChangeEnvironmentState = HasEnvironmentMetadataChanges();
        var beforeEnvironments = settings.Environments;
        var beforeSelectedEnvironment = settings.SelectedEnvironmentName;
        var beforeLanguage = settings.AppLanguage ?? "System";
        var beforeLiveSettings = LiveShellSettings.From(settings);

        if (didChangeEnvironmentState)
        {
            baseline.Environments = _environmentBaseline;
            settings.SelectedEnvironmentName = ResolveSelectedEnvironmentName(settings);
            settings.Environments = Environments.Select(env => env.Clone()).ToList();
            EnsureAtLeastOneDefault(settings.Environments);
        }

        ApplyChangedShellSettings(settings);

        SettingsPersistenceSaveResult saveResult;
        try
        {
            if (didChangeEnvironmentState)
            {
                WebViewService.RegisterEnvironmentProfiles(beforeEnvironments);
            }

            saveResult = _settingsPersistence.Save(settings, baseline);
        }
        catch (Exception ex)
        {
            saveResult = SettingsPersistenceSaveResult.Failure(ex.Message);
        }
        if (!saveResult.Succeeded)
        {
            ValidationMessage = string.Format(
                StringResources.SettingsValidationSaveFailedFormat,
                saveResult.ErrorMessage ?? StringResources.SettingsValidationSaveFailedUnknown);
            return false;
        }

        if (didChangeEnvironmentState)
        {
            UpdateEnvironmentBaseline();
        }

        ResetCommittedShellEdits();
        ValidationMessage = StringResources.SettingsValidationDefaultMessage;
        var committedSettings = _settingsPersistence.CreateDraft();
        var afterLiveSettings = LiveShellSettings.From(committedSettings);
        var didChangeSelection = !string.Equals(beforeSelectedEnvironment, committedSettings.SelectedEnvironmentName, StringComparison.Ordinal);
        result = new SettingsSaveResult(
            didChangeSelection || HaveCommittedEnvironmentsChanged(beforeEnvironments, committedSettings.Environments, includeDefault: true),
            didChangeSelection || HaveCommittedEnvironmentsChanged(beforeEnvironments, committedSettings.Environments, includeDefault: false),
            !string.Equals(beforeLanguage, committedSettings.AppLanguage ?? "System", StringComparison.Ordinal),
            new LiveShellSettingsChange(beforeLiveSettings, afterLiveSettings));
        return true;
    }

    private static bool HaveCommittedEnvironmentsChanged(
        IReadOnlyList<EnvironmentConfig> before,
        IReadOnlyList<EnvironmentConfig> after,
        bool includeDefault)
    {
        if (before.Count != after.Count)
        {
            return true;
        }

        for (var index = 0; index < before.Count; index++)
        {
            var previous = before[index];
            var current = after[index];
            if (!string.Equals(previous.Id, current.Id, StringComparison.Ordinal) ||
                !string.Equals(previous.Name, current.Name, StringComparison.Ordinal) ||
                !string.Equals(previous.GatewayUrl, current.GatewayUrl, StringComparison.Ordinal) ||
                previous.Backend != current.Backend || previous.OpenClawConnection != current.OpenClawConnection ||
                (includeDefault && previous.IsDefault != current.IsDefault))
            {
                return true;
            }
        }

        return false;
    }

    private void LoadEditFields()
    {
        if (_selectedEnvironment is not null)
        {
            EditName = _selectedEnvironment.Name;
            EditUrl = _selectedEnvironment.GatewayUrl;
            EditIsDefault = _selectedEnvironment.IsDefault;
            EditBackendIndex = GetBackendIndex(_selectedEnvironment.Backend);
            LoadGatewayFields();
            IsEditing = true;
        }
        else
        {
            EditName = string.Empty;
            EditUrl = string.Empty;
            EditIsDefault = false;
            EditBackendIndex = GetBackendIndex(HostedBackendKind.Auto);
            LoadGatewayFields();
            IsEditing = false;
        }
    }

    private string? ResolveSelectedEnvironmentName(AppSettings settings)
    {
        // Resolve against the live list before replacing it with renamed draft entries.
        var selectedId = settings.Environments
            .FirstOrDefault(env => string.Equals(env.Name, settings.SelectedEnvironmentName, StringComparison.Ordinal))?.Id;
        var currentSelection = FindEnvironmentById(selectedId);
        if (currentSelection is not null)
        {
            return currentSelection.Name;
        }

        var originalSelection = FindEnvironmentById(_originalSelectedEnvironmentId);
        if (originalSelection is not null)
        {
            return originalSelection.Name;
        }

        return Environments.FirstOrDefault(e => e.IsDefault)?.Name
            ?? Environments.FirstOrDefault()?.Name;
    }

    private EnvironmentConfig? FindEnvironmentById(string? environmentId)
    {
        if (string.IsNullOrEmpty(environmentId))
        {
            return null;
        }

        return Environments.FirstOrDefault(env => string.Equals(env.Id, environmentId, StringComparison.Ordinal));
    }

    private EnvironmentConfig CreateDraftEnvironment() => new()
    {
        Id = _selectedEnvironment?.Id ?? Guid.NewGuid().ToString("N"),
        OpenClawConnection = CreateGatewayOptions(),
        Name = EditName.Trim(),
        GatewayUrl = EditUrl.Trim(),
        IsDefault = EditIsDefault,
        Backend = _editBackend,
    };

    private void UpdateEnvironmentBaseline()
    {
        EnsureAtLeastOneDefault(Environments);
        _environmentBaseline = Environments.Select(env => env.Clone()).ToList();
        foreach (var env in Environments)
        {
            _originalSnapshots[env] = env.Clone();
        }
    }

    private void ApplyChangedShellSettings(AppSettings settings)
    {
        ApplyChangedAppearanceSettings(settings);

        if (_didEditSelectedLanguage)
        {
            settings.AppLanguage = SelectedLanguage;
        }

        if (_didEditSystemTray)
        {
            settings.SystemTray = SystemTray;
        }

        if (_didEditAutoRefresh)
        {
            settings.AutoRefresh = AutoRefresh;
        }

        if (_didEditAllowMultipleInstances)
        {
            settings.AllowMultipleInstances = AllowMultipleInstances;
        }

        if (_didEditEnableGlobalHotkey)
        {
            settings.EnableGlobalHotkey = EnableGlobalHotkey;
        }

        if (_didEditGlobalHotkey)
        {
            settings.GlobalHotkey = NormalizeHotkey(GlobalHotkey);
        }

        if (_didEditAlwaysOnTop)
        {
            settings.AlwaysOnTop = AlwaysOnTop;
        }

        if (_didEditEnableDevLog)
        {
            settings.Diagnostics.EnableVerboseRecoveryLogging = EnableDevLog;
        }
    }

    private void ResetCommittedShellEdits()
    {
        _didEditSelectedLanguage = false;
        _didEditEnableDevLog = false;
        _didEditSystemTray = false;
        _didEditAutoRefresh = false;
        _didEditAllowMultipleInstances = false;
        _didEditEnableGlobalHotkey = false;
        _didEditGlobalHotkey = false;
        _didEditAlwaysOnTop = false;
        ResetCommittedAppearanceEdits();
    }

    private bool HasEnvironmentMetadataChanges()
    {
        return HaveCommittedEnvironmentsChanged(_environmentBaseline, Environments, includeDefault: true);
    }

    private bool HasEnvironmentSessionIdentityChanges()
    {
        return Environments.Count != _originalSnapshots.Count ||
               Environments.Any(DidEnvironmentSessionIdentityChange);
    }

    private static void EnsureAtLeastOneDefault(IList<EnvironmentConfig> environments)
    {
        if (!environments.Any(e => e.IsDefault) &&
            environments.Count > 0)
        {
            environments[0].IsDefault = true;
        }
    }

    private static string NormalizeHotkey(string? hotkey)
    {
        return hotkey?.Trim() ?? string.Empty;
    }

    private bool DidEnvironmentSessionIdentityChange(EnvironmentConfig environment)
    {
        if (!_originalSnapshots.TryGetValue(environment, out var original))
        {
            return true;
        }

        // Backend belongs here, not only in metadata: the bridge script is injected
        // at document-creation time, so a new profile only takes effect once the
        // WebView is recreated.
        return !string.Equals(original.Name, environment.Name, StringComparison.Ordinal) ||
               !string.Equals(original.GatewayUrl, environment.GatewayUrl, StringComparison.Ordinal) ||
               original.Backend != environment.Backend ||
               original.OpenClawConnection != environment.OpenClawConnection;
    }

    private bool DidEnvironmentMetadataChange(EnvironmentConfig environment)
    {
        if (!_originalSnapshots.TryGetValue(environment, out var original))
        {
            return true;
        }

        return !string.Equals(original.Name, environment.Name, StringComparison.Ordinal) ||
               !string.Equals(original.GatewayUrl, environment.GatewayUrl, StringComparison.Ordinal) ||
               original.IsDefault != environment.IsDefault ||
               original.Backend != environment.Backend ||
               original.OpenClawConnection != environment.OpenClawConnection;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static bool TryValidateEnvironment(EnvironmentConfig environment, out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(environment.Name))
        {
            errorMessage = StringResources.SettingsValidationEnvironmentNameRequired;
            return false;
        }

        if (string.IsNullOrWhiteSpace(environment.GatewayUrl))
        {
            errorMessage = StringResources.SettingsValidationControlUiUrlRequired;
            return false;
        }

        if (!Uri.TryCreate(environment.GatewayUrl.Trim(), UriKind.Absolute, out var uri))
        {
            errorMessage = StringResources.SettingsValidationControlUiUrlAbsolute;
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            errorMessage = uri.Scheme is "ws" or "wss"
                ? StringResources.SettingsValidationControlUiUrlWs
                : StringResources.SettingsValidationControlUiUrlScheme;
            return false;
        }

        errorMessage = StringResources.SettingsValidationDefaultMessage;
        return TryValidateGatewayOptions(environment, out errorMessage);
    }

    private bool TryValidateHotkey(out string errorMessage)
    {
        if (!EnableGlobalHotkey)
        {
            errorMessage = StringResources.SettingsValidationDefaultMessage;
            return true;
        }

        if (string.IsNullOrWhiteSpace(GlobalHotkey))
        {
            errorMessage = StringResources.SettingsValidationGlobalHotkeyRequired;
            return false;
        }

        var binding = HotkeyBinding.Parse(GlobalHotkey);
        if (binding is null || binding.GetVirtualKeyCode() == 0)
        {
            errorMessage = StringResources.SettingsValidationGlobalHotkeyInvalid;
            return false;
        }

        errorMessage = StringResources.SettingsValidationDefaultMessage;
        return true;
    }
}
