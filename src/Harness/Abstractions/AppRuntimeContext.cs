// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Services;

namespace Harness.Abstractions;

public sealed class AppRuntimeContext
{
    public AppRuntimeContext(IAppLogger logger, ConfigurationService configuration)
    {
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public IAppLogger Logger { get; }

    public ConfigurationService Configuration { get; }
}
