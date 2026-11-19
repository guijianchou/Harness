// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Helpers;
using Harness.Models;

namespace Harness.Services;

/// <summary>
/// Manages application settings persistence using JSON file storage.
/// Settings are stored in %LOCALAPPDATA%\Harness\settings.json.
/// </summary>
public partial class ConfigurationService
{
    private static readonly string DefaultAppDataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Harness");

    private readonly string _appDataFolder;
    private readonly string _settingsFilePath;
    private readonly IAppLogger _logger;
    private readonly TimeSpan _deferredSaveDelay;
    private readonly Action<string, string> _writeAllText;

    private readonly object _lock = new();
    private readonly object _deferredSaveGate = new();
    private CancellationTokenSource? _deferredSaveCts;
    private Task? _deferredSaveTask;
    private int _saveQueued;
    private int _saveVersion;
    private int _deferredSaveRequests;
    private int _deferredSaveCoalescedRequests;
    private JsonObject _settingsBaseline = new();
    private bool _settingsReadFailed;
    private string? _backedUpCorruptJson;

    public ConfigurationService()
        : this(DefaultAppDataFolder, NullAppLogger.Instance)
    {
    }

    public ConfigurationService(IAppLogger? logger)
        : this(DefaultAppDataFolder, logger)
    {
    }

    public ConfigurationService(
        string appDataFolder,
        IAppLogger? logger = null,
        TimeSpan? deferredSaveDelay = null,
        Action<string, string>? writeAllText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataFolder);
        _appDataFolder = Path.GetFullPath(appDataFolder);
        _settingsFilePath = Path.Combine(_appDataFolder, "settings.json");
        _logger = logger ?? NullAppLogger.Instance;
        _deferredSaveDelay = deferredSaveDelay ?? TimeSpan.FromMilliseconds(250);
        _writeAllText = writeAllText ?? AtomicFileWriter.WriteAllText;
    }

    /// <summary>
    /// Gets the current application settings.
    /// </summary>
    public AppSettings Settings { get; private set; } = new();

    public event Action? SettingsCommitted;

    /// <summary>
    /// Gets the full path to the settings JSON file.
    /// </summary>
    public string SettingsFilePath => _settingsFilePath;

    /// <summary>
    /// Gets the full path to the logs directory.
    /// </summary>
    public string LogsDirectory => Path.Combine(_appDataFolder, "logs");

    public int DeferredSaveRequests => Volatile.Read(ref _deferredSaveRequests);

    public int DeferredSaveCoalescedRequests => Volatile.Read(ref _deferredSaveCoalescedRequests);

    /// <summary>
    /// Loads settings from disk. Creates defaults if the file doesn't exist.
    /// A corrupt file is backed up and replaced with defaults; a transient read
    /// failure (lock, permissions) falls back to in-memory defaults without
    /// overwriting the file on disk.
    /// </summary>
    public void Load()
    {
        bool committed;
        lock (_lock)
        {
            committed = LoadSettings() && SaveSettings(null).Succeeded;
        }
        if (committed)
        {
            PublishSettingsCommitted();
        }
    }

    private bool LoadSettings()
    {
        string? json = null;
        try
        {
            json = ReadSettingsFile();
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to read settings file; using in-memory defaults without overwriting it: {ex.Message}");
            Settings = CreateDefaultSettings();
            NormalizeSettings(Settings);
            _settingsReadFailed = true;
            return false;
        }

        _settingsReadFailed = false;
        _settingsBaseline = new JsonObject();
        _backedUpCorruptJson = null;
        if (json is not null)
        {
            try
            {
                var settings = JsonSerializer.Deserialize(json!, AppSettingsJsonContext.Default.AppSettings);
                if (settings is not null)
                {
                    _settingsBaseline = JsonNode.Parse(json!)!.AsObject();
                    var settingsChanged = NormalizeSettings(settings, json);
                    Settings = settings;
                    return settingsChanged;
                }

                _logger.Error("Settings file deserialized to null; treating it as corrupt.");
            }
            catch (Exception ex)
            {
                _logger.Error($"Settings file is corrupt: {ex.Message}");
            }

            _settingsReadFailed = !BackUpCorruptSettingsFile(json);
        }

        Settings = CreateDefaultSettings();
        NormalizeSettings(Settings);
        return true;
    }

    private static AppSettings CreateDefaultSettings()
    {
        // Default settings with a sample environment
        return new AppSettings
        {
            Environments =
            [
                new EnvironmentConfig
                {
                    Name = "Default",
                    GatewayUrl = EnvironmentConfig.PlaceholderGatewayUrl,
                    IsDefault = true,
                }
            ],
            SelectedEnvironmentName = "Default",
        };
    }

    private bool BackUpCorruptSettingsFile(string json)
    {
        try
        {
            var backupPath = $"{_settingsFilePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            File.WriteAllText(backupPath, json);
            _backedUpCorruptJson = json;
            _logger.Warning($"Backed up corrupt settings file to '{backupPath}'.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to back up corrupt settings file: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Saves current settings to disk.
    /// </summary>
    public SettingsWriteResult Save(AppSettings? updatedSettings = null, AppSettings? draftBaseline = null)
    {
        SettingsWriteResult result;
        lock (_lock)
        {
            result = SaveSettings(updatedSettings, draftBaseline);
        }
        if (result.Succeeded)
        {
            PublishSettingsCommitted();
        }
        return result;
    }

    private void PublishSettingsCommitted()
    {
        var handlers = SettingsCommitted;
        if (handlers is null)
        {
            return;
        }
        foreach (Action handler in handlers.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                // Persistence already succeeded; a subscriber failure must not roll it back.
                _logger.Error($"Failed to publish committed settings: {ex.Message}");
            }
        }
    }

    private SettingsWriteResult SaveSettings(AppSettings? updatedSettings, AppSettings? draftBaseline = null)
    {
        try
        {
            if (_settingsReadFailed)
            {
                return SettingsWriteResult.Failure("Settings could not be read. Restart Harness after restoring access before saving.");
            }

            var runtimeBeforeSave = JsonSerializer.SerializeToNode(Settings, AppSettingsJsonContext.Default.AppSettings)!.AsObject();
            var settings = updatedSettings is null
                ? runtimeBeforeSave.Deserialize(AppSettingsJsonContext.Default.AppSettings)!
                : CloneSettings(updatedSettings);
            NormalizeSettings(settings);
            Directory.CreateDirectory(_appDataFolder);
            // Serialize the read/merge/replace transaction across app instances.
            using var fileLock = new FileStream($"{_settingsFilePath}.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var currentJson = ReadSettingsFile();
            var current = currentJson is null
                ? (JsonObject)_settingsBaseline.DeepClone()
                : currentJson == _backedUpCorruptJson
                    ? new JsonObject()
                    : JsonNode.Parse(currentJson)!.AsObject();
            var desired = JsonSerializer.SerializeToNode(settings, AppSettingsJsonContext.Default.AppSettings)!.AsObject();
            if (draftBaseline is not null)
            {
                var baseline = JsonSerializer.SerializeToNode(draftBaseline, AppSettingsJsonContext.Default.AppSettings)!.AsObject();
                desired = MergeSettings(baseline, desired, (JsonObject)runtimeBeforeSave.DeepClone());
            }
            var merged = MergeSettings(_settingsBaseline, desired, current);
            var committedSettings = merged.Deserialize(AppSettingsJsonContext.Default.AppSettings)!;
            NormalizeSettings(committedSettings);
            var committedValues = JsonSerializer.SerializeToNode(committedSettings, AppSettingsJsonContext.Default.AppSettings)!.AsObject();
            foreach (var (name, value) in committedValues)
            {
                merged[name] = value?.DeepClone();
            }

            _writeAllText(_settingsFilePath, merged.ToJsonString(AppSettingsJsonContext.Default.Options));
            CommitSavedSettings(committedSettings, committedValues, runtimeBeforeSave);
            _settingsBaseline = merged;
            _backedUpCorruptJson = null;
            return SettingsWriteResult.Success();
        }
        catch (SettingsConflictException ex)
        {
            _logger.Error($"Failed to save settings: {ex.Message}");
            return SettingsWriteResult.Failure(ex.Message) with { HasConflict = true };
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to save settings: {ex.Message}");
            return SettingsWriteResult.Failure(ex.Message);
        }
    }

    public void SaveDeferred()
    {
        Interlocked.Increment(ref _deferredSaveRequests);

        if (!TryStartDeferredSaveWorker())
        {
            Interlocked.Increment(ref _deferredSaveCoalescedRequests);
            _logger.Info("settings.save_deferred.coalesced", new
            {
                requests = DeferredSaveRequests,
                coalesced = DeferredSaveCoalescedRequests
            });
            return;
        }

        _logger.Info("settings.save_deferred.queued", new
        {
            requests = DeferredSaveRequests,
            coalesced = DeferredSaveCoalescedRequests
        });
    }

    private bool TryStartDeferredSaveWorker()
    {
        var cancellation = new CancellationTokenSource();

        lock (_deferredSaveGate)
        {
            _saveVersion++;
            if (_deferredSaveTask is { IsCompleted: false })
            {
                cancellation.Dispose();
                return false;
            }

            _saveQueued = 1;
            _deferredSaveCts = cancellation;
            _deferredSaveTask = Task.Run(() => ProcessDeferredSaveQueueAsync(cancellation));
            return true;
        }
    }

    private async Task ProcessDeferredSaveQueueAsync(CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            while (true)
            {
                var versionToFlush = GetDeferredSaveVersion(cancellation);
                if (versionToFlush is null)
                {
                    return;
                }

                await Task.Delay(_deferredSaveDelay, token).ConfigureAwait(false);
                var saveResult = Save();
                _logger.Info("settings.save_deferred.flushed", new
                {
                    requests = DeferredSaveRequests,
                    coalesced = DeferredSaveCoalescedRequests,
                    saveResult.Succeeded
                });

                if (TryCompleteDeferredSaveBatch(cancellation, versionToFlush.Value))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error($"Deferred settings save failed: {ex.Message}");
        }
        finally
        {
            CompleteDeferredSaveWorker(cancellation);
        }
    }

    private int? GetDeferredSaveVersion(CancellationTokenSource cancellation)
    {
        lock (_deferredSaveGate)
        {
            if (!ReferenceEquals(_deferredSaveCts, cancellation))
            {
                return null;
            }

            return _saveVersion;
        }
    }

    private bool TryCompleteDeferredSaveBatch(CancellationTokenSource cancellation, int flushedVersion)
    {
        lock (_deferredSaveGate)
        {
            if (!ReferenceEquals(_deferredSaveCts, cancellation))
            {
                return true;
            }

            if (_saveVersion != flushedVersion)
            {
                return false;
            }

            _saveQueued = 0;
            _deferredSaveCts = null;
            _deferredSaveTask = null;
            cancellation.Dispose();
            return true;
        }
    }

    private void CompleteDeferredSaveWorker(CancellationTokenSource cancellation)
    {
        lock (_deferredSaveGate)
        {
            if (!ReferenceEquals(_deferredSaveCts, cancellation))
            {
                return;
            }

            _saveQueued = 0;
            _deferredSaveCts = null;
            _deferredSaveTask = null;
            cancellation.Dispose();
        }
    }

    private bool CancelDeferredSaveWorker()
    {
        var hadQueuedSave = false;
        CancellationTokenSource? cancellation;
        Task? task;

        lock (_deferredSaveGate)
        {
            hadQueuedSave = _saveQueued != 0 || _deferredSaveTask is not null;
            cancellation = _deferredSaveCts;
            task = _deferredSaveTask;
            _saveQueued = 0;
            _deferredSaveCts = null;
            _deferredSaveTask = null;
        }

        if (cancellation is null)
        {
            return hadQueuedSave;
        }

        cancellation.Cancel();

        var completed = true;
        if (task is not null)
        {
            try
            {
                completed = task.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                completed = true;
            }
        }

        if (completed)
        {
            cancellation.Dispose();
        }
        else if (task is not null)
        {
            _ = ObserveDeferredSaveWorkerShutdownAsync(task, cancellation);
        }

        return hadQueuedSave;
    }

    private static async Task ObserveDeferredSaveWorkerShutdownAsync(
        Task task,
        CancellationTokenSource cancellation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The worker already logs operational failures before it exits.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    public void FlushDeferredSave()
    {
        var hadQueuedSave = CancelDeferredSaveWorker();

        if (!hadQueuedSave)
        {
            return;
        }

        var saveResult = Save();
        _logger.Info("settings.save_deferred.flush_on_shutdown", new
        {
            requests = DeferredSaveRequests,
            coalesced = DeferredSaveCoalescedRequests,
            saveResult.Succeeded
        });
    }

    /// <summary>
    /// Gets the default environment, or the first available, or null.
    /// </summary>
    public EnvironmentConfig? GetDefaultEnvironment()
    {
        return Settings.Environments.FirstOrDefault(e => e.IsDefault)
            ?? Settings.Environments.FirstOrDefault();
    }

    /// <summary>
    /// Gets the currently selected environment by persisted name.
    /// Falls back to default if the named environment is not found.
    /// </summary>
    public EnvironmentConfig? GetSelectedEnvironment()
    {
        if (!string.IsNullOrEmpty(Settings.SelectedEnvironmentName))
        {
            var env = Settings.Environments.FirstOrDefault(
                e => e.Name.Equals(Settings.SelectedEnvironmentName, StringComparison.Ordinal));
            if (env is not null)
            {
                return env;
            }
        }

        return GetDefaultEnvironment();
    }

    private static bool NormalizeSettings(AppSettings settings, string? rawJson = null)
    {
        var changed = false;

        // Compact mode has no supported UI entry or exit. Retire saved legacy state
        // while retaining the normal window bounds and existing recovery paths.
        if (settings.CompactMode)
        {
            settings.CompactMode = false;
            changed = true;
        }

        if (settings.Environments is null)
        {
            settings.Environments = [];
            changed = true;
        }

        var environmentIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < settings.Environments.Count; index++)
        {
            var environment = settings.Environments[index];
            if (string.IsNullOrWhiteSpace(environment.Id) || !environmentIds.Add(environment.Id))
            {
                // Deterministic legacy migration lets two instances normalize the same file safely.
                environment.Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"Harness.environment.v1|{index}|{environment.Name}|{environment.GatewayUrl}")))[..32].ToLowerInvariant();
                environmentIds.Add(environment.Id);
                changed = true;
            }
        }

        if (settings.RecoveryPolicy is null)
        {
            settings.RecoveryPolicy = new RecoveryPolicyOptions();
            changed = true;
        }

        if (settings.Heartbeat is null)
        {
            settings.Heartbeat = new HeartbeatOptions();
            changed = true;
        }

        if (settings.Diagnostics is null)
        {
            settings.Diagnostics = new DiagnosticsOptions();
            changed = true;
        }

        var hasParsedRawJson = false;
        var hasSystemTray = false;
        var hasLegacyTraySetting = false;
        var legacySystemTray = false;

        changed |= NormalizeWindowBounds(settings);
        changed |= NormalizeSettingsWindowBounds(settings);

        var normalizedHeartbeatInterval = Math.Max(0, settings.Heartbeat.IntervalSeconds);
        changed |= normalizedHeartbeatInterval != settings.Heartbeat.IntervalSeconds;
        settings.Heartbeat.IntervalSeconds = normalizedHeartbeatInterval;

        var normalizedHeartbeatFailureThreshold = Math.Max(1, settings.Heartbeat.FailureThreshold);
        changed |= normalizedHeartbeatFailureThreshold != settings.Heartbeat.FailureThreshold;
        settings.Heartbeat.FailureThreshold = normalizedHeartbeatFailureThreshold;

        var normalizedHeartbeatConnectingThreshold = Math.Max(1, settings.Heartbeat.ConnectingThreshold);
        changed |= normalizedHeartbeatConnectingThreshold != settings.Heartbeat.ConnectingThreshold;
        settings.Heartbeat.ConnectingThreshold = normalizedHeartbeatConnectingThreshold;

        var normalizedLegacyHeartbeatInterval = Math.Max(0, settings.HeartbeatIntervalSeconds);
        changed |= normalizedLegacyHeartbeatInterval != settings.HeartbeatIntervalSeconds;
        settings.HeartbeatIntervalSeconds = normalizedLegacyHeartbeatInterval;

        var hasLegacyInterval = false;
        var hasHeartbeatObject = false;
        var hasHeartbeatInterval = false;

        if (!string.IsNullOrWhiteSpace(rawJson))
        {
            try
            {
                using var document = JsonDocument.Parse(rawJson);
                var root = document.RootElement;
                hasParsedRawJson = true;
                hasSystemTray = root.TryGetProperty("systemTray", out _);

                if (root.TryGetProperty("closeToTray", out var closeToTrayElement) &&
                    closeToTrayElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    hasLegacyTraySetting = true;
                    legacySystemTray = closeToTrayElement.GetBoolean();
                }
                else if (root.TryGetProperty("minimizeToTray", out var minimizeToTrayElement) &&
                         minimizeToTrayElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    hasLegacyTraySetting = true;
                    legacySystemTray = minimizeToTrayElement.GetBoolean();
                }

                hasLegacyInterval = root.TryGetProperty("heartbeatIntervalSeconds", out _);

                if (root.TryGetProperty("heartbeat", out var heartbeatElement) &&
                    heartbeatElement.ValueKind == JsonValueKind.Object)
                {
                    hasHeartbeatObject = true;
                    hasHeartbeatInterval = heartbeatElement.TryGetProperty("intervalSeconds", out _);
                }
            }
            catch (JsonException)
            {
                // Deserialization already succeeded; leave normalization on the object graph only.
            }
        }

        if (hasParsedRawJson && !hasSystemTray)
        {
            if (hasLegacyTraySetting)
            {
                settings.SystemTray = legacySystemTray;
            }

            // Persist the merged setting and remove the obsolete legacy fields.
            changed = true;
        }

        if (hasLegacyInterval && (!hasHeartbeatObject || !hasHeartbeatInterval))
        {
            changed |= settings.Heartbeat.IntervalSeconds != settings.HeartbeatIntervalSeconds;
            changed |= settings.Heartbeat.EnableHeartbeat != (settings.HeartbeatIntervalSeconds > 0);
            settings.Heartbeat.IntervalSeconds = settings.HeartbeatIntervalSeconds;
            settings.Heartbeat.EnableHeartbeat = settings.HeartbeatIntervalSeconds > 0;
        }

        var synchronizedHeartbeatInterval = settings.Heartbeat.EnableHeartbeat
            ? settings.Heartbeat.IntervalSeconds
            : 0;
        changed |= settings.HeartbeatIntervalSeconds != synchronizedHeartbeatInterval;
        settings.HeartbeatIntervalSeconds = synchronizedHeartbeatInterval;

        return changed;
    }

    private static bool NormalizeWindowBounds(AppSettings settings)
    {
        if (!WindowBoundsUtilities.HasPersistableSize(settings.WindowWidth, settings.WindowHeight) ||
            WindowBoundsUtilities.HasMinimizedSentinelPosition(settings.WindowLeft, settings.WindowTop))
        {
            settings.WindowWidth = WindowBoundsUtilities.DefaultWindowWidth;
            settings.WindowHeight = WindowBoundsUtilities.DefaultWindowHeight;
            settings.WindowLeft = -1;
            settings.WindowTop = -1;
            return true;
        }

        if (!WindowBoundsUtilities.HasSavedPosition(settings.WindowLeft, settings.WindowTop) &&
            (settings.WindowLeft != -1 || settings.WindowTop != -1))
        {
            settings.WindowLeft = -1;
            settings.WindowTop = -1;
            return true;
        }

        return false;
    }

    private static bool NormalizeSettingsWindowBounds(AppSettings settings)
    {
        if (!WindowBoundsUtilities.HasPersistableSize(
                settings.SettingsWindowWidth,
                settings.SettingsWindowHeight,
                WindowBoundsUtilities.MinimumPersistedSettingsWindowWidth,
                WindowBoundsUtilities.MinimumPersistedSettingsWindowHeight) ||
            WindowBoundsUtilities.HasMinimizedSentinelPosition(settings.SettingsWindowLeft, settings.SettingsWindowTop))
        {
            settings.SettingsWindowWidth = WindowBoundsUtilities.DefaultSettingsWindowWidth;
            settings.SettingsWindowHeight = WindowBoundsUtilities.DefaultSettingsWindowHeight;
            settings.SettingsWindowLeft = -1;
            settings.SettingsWindowTop = -1;
            return true;
        }

        if (!WindowBoundsUtilities.HasSavedPosition(settings.SettingsWindowLeft, settings.SettingsWindowTop) &&
            (settings.SettingsWindowLeft != -1 || settings.SettingsWindowTop != -1))
        {
            settings.SettingsWindowLeft = -1;
            settings.SettingsWindowTop = -1;
            return true;
        }

        return false;
    }
}
