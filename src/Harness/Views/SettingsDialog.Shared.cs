// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Helpers;
using Harness.Models;
using Harness.Services;
using Harness.ViewModels;
using Microsoft.UI.Xaml;

namespace Harness.Views;

public sealed partial class SettingsDialog
{
    private const string LanguagePanelTag = "Language";
    private const string ShellPanelTag = "Shell";
    private const string EnvironmentsPanelTag = "Environments";
    private const string SessionsPanelTag = "Sessions";
    private const string DevToolsPanelTag = "DevTools";
    private const string AboutPanelTag = "About";
    private bool _hasRestoredSettingsBoundsAfterActivation;
    private ElementTheme? _appliedRequestedTheme;
    private ElementTheme? _appliedActualTheme;
    private long? _settingsOpenStartedAt;
    private bool _isSyncingLanguageSelection;
    private bool _isSaving;
    private bool _isClosed;
    private bool _isClearingSession;
    private bool _isRestoringEnvironmentSelection;
    private Task _settingsSaveTask = Task.CompletedTask;
    private Task _startupStatusRefreshTask = Task.CompletedTask;
    private readonly SettingsPersistenceAdapter _settingsPersistence;
    private readonly StartupRegistrationService _startupRegistration = new();

    internal bool IsSavingSettings => _isSaving;

    internal Task WaitForPendingSaveAsync() => _settingsSaveTask;

    public SettingsViewModel ViewModel { get; private set; }

    public IReadOnlyList<EnvironmentConfig> SavedSessionEnvironments => _settingsPersistence.CreateDraft().Environments;

    /// <summary>
    /// Gets the main view model for developer tools commands.
    /// </summary>
    public MainViewModel? MainViewModel { get; set; }

    /// <summary>
    /// Raised when settings are saved, so MainWindow can refresh.
    /// </summary>
    public event Action<SettingsSaveResult>? SettingsSaved;

    private static string ValidationErrorTitle => StringResources.SettingsValidationError;
}
