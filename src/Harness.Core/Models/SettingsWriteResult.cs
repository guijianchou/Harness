// Copyright (c) Lanstack @openclaw. All rights reserved.

namespace Harness.Models;

public readonly record struct SettingsWriteResult(bool Succeeded, string? ErrorMessage)
{
    public static SettingsWriteResult Success() => new(true, null);

    public static SettingsWriteResult Failure(string errorMessage) => new(false, errorMessage);
}
