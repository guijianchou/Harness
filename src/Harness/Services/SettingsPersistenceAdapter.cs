// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Helpers;
using Harness.Models;

namespace Harness.Services;

internal sealed class SettingsPersistenceAdapter
{
    private readonly ConfigurationService _configuration;
    private readonly IAppLogger _logger;

    public SettingsPersistenceAdapter(ConfigurationService configuration, IAppLogger logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public AppSettings Current => _configuration.Settings;

    public AppSettings CreateDraft() => _configuration.CreateSnapshot();

    public AppSettings CreateDraft(out AppSettings baseline) => _configuration.CreateSnapshot(out baseline);

    public SettingsPersistenceSaveResult Save(AppSettings settings, AppSettings? draftBaseline = null)
    {
        var result = _configuration.Save(settings, draftBaseline);
        if (result.Succeeded)
        {
            _logger.Info("Settings saved.");
            return SettingsPersistenceSaveResult.Success();
        }

        var message = string.IsNullOrWhiteSpace(result.ErrorMessage)
            ? "Unknown settings write failure."
            : result.ErrorMessage;
        _logger.Error($"Settings save failed: {message}");
        return SettingsPersistenceSaveResult.Failure(result.HasConflict
            ? StringResources.SettingsValidationConflict
            : message);
    }

    public EnvironmentConfig? GetSelectedEnvironment()
    {
        return _configuration.GetSelectedEnvironment();
    }
}

internal readonly record struct SettingsPersistenceSaveResult(bool Succeeded, string? ErrorMessage)
{
    public static SettingsPersistenceSaveResult Success() => new(true, null);

    public static SettingsPersistenceSaveResult Failure(string errorMessage) => new(false, errorMessage);
}
