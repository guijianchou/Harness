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

    private static AppSettings CloneSettings(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        return JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;
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
                throw new IOException($"Setting '{name}' changed in another instance. Reopen Harness to load the latest settings before editing it.");
            }

            current[name] = value?.DeepClone();
        }

        // Legacy aliases must not undo the normalized values on the next load.
        current.Remove("closeToTray");
        current.Remove("minimizeToTray");
        return current;
    }
}
