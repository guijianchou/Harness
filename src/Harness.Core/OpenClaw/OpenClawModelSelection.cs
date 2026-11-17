// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.OpenClaw;

/// <summary>Uses only reasoning choices advertised by the selected Gateway model.</summary>
public static class OpenClawModelSelection
{
    public static IReadOnlyList<OpenClawThinkingOption> ThinkingOptions(OpenClawModel? model) =>
        model?.ThinkingLevels?.Where(option => !string.IsNullOrWhiteSpace(option.Id))
            .DistinctBy(option => option.Id, StringComparer.Ordinal).ToArray() ?? [];

    public static IReadOnlyDictionary<string, object?> BuildPatch(string modelId, OpenClawModel? model, string? thinkingId)
    {
        var allowed = !string.IsNullOrEmpty(modelId) && !string.IsNullOrEmpty(thinkingId) &&
            ThinkingOptions(model).Any(option => option.Id == thinkingId);
        return new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrEmpty(modelId) ? null : modelId,
            ["thinkingLevel"] = allowed ? thinkingId : null,
        };
    }
}
