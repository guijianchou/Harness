// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Models;

namespace Harness.Services;

public partial class ConfigurationService
{
    public AppSettings CreateSnapshot()
    {
        lock (_lock)
        {
            return CloneSettings(Settings);
        }
    }

    public AppSettings CreateSnapshot(out AppSettings baseline)
    {
        lock (_lock)
        {
            baseline = CloneSettings(Settings);
            return CloneSettings(baseline);
        }
    }

    private static AppSettings CloneSettings(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        return JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;
    }

    private void CommitSavedSettings(AppSettings committedSettings, JsonObject committedValues, JsonObject runtimeBeforeSave)
    {
        var runtimeNow = JsonSerializer.SerializeToNode(Settings, AppSettingsJsonContext.Default.AppSettings)!.AsObject();
        foreach (var property in AppSettingsJsonContext.Default.AppSettings.Properties)
        {
            if (property.Get is null || property.Set is null ||
                !JsonNode.DeepEquals(runtimeNow[property.Name], runtimeBeforeSave[property.Name]) ||
                JsonNode.DeepEquals(runtimeNow[property.Name], committedValues[property.Name]))
            {
                continue;
            }

            // Keep the live root and unchanged option graphs. Edits made while disk I/O
            // was in progress remain pending against the newly committed disk baseline.
            property.Set(Settings, property.Get(committedSettings));
        }

        NormalizeSettings(Settings);
    }

    private string? ReadSettingsFile()
    {
        try
        {
            return File.ReadAllText(_settingsFilePath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static JsonObject MergeSettings(JsonObject baseline, JsonObject desired, JsonObject current)
    {
        foreach (var (name, value) in desired)
        {
            if (JsonNode.DeepEquals(baseline[name], value))
            {
                continue;
            }

            if (!JsonNode.DeepEquals(current[name], baseline[name]) && !JsonNode.DeepEquals(current[name], value))
            {
                throw new SettingsConflictException(name);
            }

            current[name] = value?.DeepClone();
        }

        // Legacy aliases must not undo the normalized values on the next load.
        current.Remove("closeToTray");
        current.Remove("minimizeToTray");
        return current;
    }

    private sealed class SettingsConflictException(string name)
        : IOException($"Setting '{name}' changed in another instance. Reopen Harness to load the latest settings before editing it.");
}
