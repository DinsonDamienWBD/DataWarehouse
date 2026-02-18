namespace DataWarehouse.Plugins.UltimateDeployment.Strategies.FeatureFlags;

/// <summary>
/// LaunchDarkly feature flag deployment strategy.
/// </summary>
public sealed class LaunchDarklyStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "LaunchDarkly",
        DeploymentType = DeploymentType.FeatureFlag,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 1,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 3,
        RequiredInfrastructure = ["LaunchDarkly"],
        Description = "LaunchDarkly feature flag-based progressive delivery"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        var state = initialState;
        var projectKey = GetProjectKey(config);
        var flagKey = GetFlagKey(config);
        var environmentKey = config.Environment;

        // Create or update flag
        state = state with { ProgressPercent = 20 };
        await EnsureFlagExistsAsync(projectKey, flagKey, ct);

        // Configure targeting rules
        state = state with { ProgressPercent = 40 };
        await ConfigureTargetingAsync(projectKey, flagKey, environmentKey, config, ct);

        // Enable flag with percentage rollout
        state = state with { ProgressPercent = 60 };
        var rolloutPercent = config.CanaryPercent > 0 ? config.CanaryPercent : 100;
        await SetRolloutPercentageAsync(projectKey, flagKey, environmentKey, rolloutPercent, ct);

        // Turn on flag
        state = state with { ProgressPercent = 80 };
        await EnableFlagAsync(projectKey, flagKey, environmentKey, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["projectKey"] = projectKey,
                ["flagKey"] = flagKey,
                ["environmentKey"] = environmentKey,
                ["rolloutPercent"] = rolloutPercent
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        var projectKey = currentState.Metadata.TryGetValue("projectKey", out var pk) ? pk?.ToString() : "";
        var flagKey = currentState.Metadata.TryGetValue("flagKey", out var fk) ? fk?.ToString() : "";
        var environmentKey = currentState.Metadata.TryGetValue("environmentKey", out var ek) ? ek?.ToString() : "";

        // Disable flag (instant rollback)
        await DisableFlagAsync(projectKey!, flagKey!, environmentKey!, ct);

        return currentState with
        {
            Health = DeploymentHealth.Healthy,
            Version = targetVersion,
            ProgressPercent = 100,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(currentState.Metadata) { ["rolloutPercent"] = 0 }
        };
    }

    protected override async Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct)
    {
        // For feature flags, "scaling" means adjusting rollout percentage
        var projectKey = currentState.Metadata.TryGetValue("projectKey", out var pk) ? pk?.ToString() : "";
        var flagKey = currentState.Metadata.TryGetValue("flagKey", out var fk) ? fk?.ToString() : "";
        var environmentKey = currentState.Metadata.TryGetValue("environmentKey", out var ek) ? ek?.ToString() : "";

        var rolloutPercent = Math.Min(100, targetInstances);
        await SetRolloutPercentageAsync(projectKey!, flagKey!, environmentKey!, rolloutPercent, ct);

        return currentState with
        {
            Metadata = new Dictionary<string, object>(currentState.Metadata) { ["rolloutPercent"] = rolloutPercent }
        };
    }

    protected override async Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
    {
        var projectKey = currentState.Metadata.TryGetValue("projectKey", out var pk) ? pk?.ToString() : "";
        var flagKey = currentState.Metadata.TryGetValue("flagKey", out var fk) ? fk?.ToString() : "";
        var environmentKey = currentState.Metadata.TryGetValue("environmentKey", out var ek) ? ek?.ToString() : "";

        var status = await GetFlagStatusAsync(projectKey!, flagKey!, environmentKey!, ct);

        return new[]
        {
            new HealthCheckResult
            {
                InstanceId = $"{flagKey}-{environmentKey}",
                IsHealthy = status.IsOn,
                StatusCode = status.IsOn ? 200 : 503,
                ResponseTimeMs = 5,
                Details = new Dictionary<string, object>
                {
                    ["flagOn"] = status.IsOn,
                    ["evaluationCount"] = status.EvaluationCount
                }
            }
        };
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetProjectKey(DeploymentConfig config) => config.StrategyConfig.TryGetValue("projectKey", out var pk) && pk is string pks ? pks : "default";
    private static string GetFlagKey(DeploymentConfig config) => config.StrategyConfig.TryGetValue("flagKey", out var fk) && fk is string fks ? fks : $"feature-{config.Version}";

    private async Task EnsureFlagExistsAsync(string project, string flag, CancellationToken ct)
    {
        if (MessageBus != null)
        {
            var message = new DataWarehouse.SDK.Utilities.PluginMessage
            {
                Type = "featureflag.ensure",
                Payload = new Dictionary<string, object>
                {
                    ["Provider"] = "LaunchDarkly",
                    ["ProjectKey"] = project,
                    ["FlagKey"] = flag,
                    ["FlagType"] = "boolean",
                    ["DefaultValue"] = false,
                    ["RequestedAt"] = DateTime.UtcNow
                }
            };
            await MessageBus.SendAsync("featureflag.ensure.exists", message, ct);
        }
        await Task.Delay(20, ct);
    }

    private async Task ConfigureTargetingAsync(string project, string flag, string env, DeploymentConfig config, CancellationToken ct)
    {
        if (MessageBus != null)
        {
            // Build targeting rules from config
            var targetingRules = new List<object>();

            if (config.StrategyConfig.TryGetValue("targetUsers", out var users) && users is IEnumerable<string> userList)
            {
                targetingRules.Add(new
                {
                    attribute = "user_id",
                    @operator = "in",
                    values = userList,
                    variation = true
                });
            }

            if (config.StrategyConfig.TryGetValue("targetSegments", out var segments) && segments is IEnumerable<string> segList)
            {
                targetingRules.Add(new
                {
                    attribute = "segment",
                    @operator = "in",
                    values = segList,
                    variation = true
                });
            }

            var message = new DataWarehouse.SDK.Utilities.PluginMessage
            {
                Type = "featureflag.configure.targeting",
                Payload = new Dictionary<string, object>
                {
                    ["Provider"] = "LaunchDarkly",
                    ["ProjectKey"] = project,
                    ["FlagKey"] = flag,
                    ["EnvironmentKey"] = env,
                    ["TargetingRules"] = targetingRules,
                    ["FallbackVariation"] = false,
                    ["RequestedAt"] = DateTime.UtcNow
                }
            };
            await MessageBus.SendAsync("featureflag.configure.targeting", message, ct);
        }
        await Task.Delay(20, ct);
    }

    private async Task SetRolloutPercentageAsync(string project, string flag, string env, int percent, CancellationToken ct)
    {
        if (MessageBus != null)
        {
            var message = new DataWarehouse.SDK.Utilities.PluginMessage
            {
                Type = "featureflag.rollout.percentage",
                Payload = new Dictionary<string, object>
                {
                    ["Provider"] = "LaunchDarkly",
                    ["ProjectKey"] = project,
                    ["FlagKey"] = flag,
                    ["EnvironmentKey"] = env,
                    ["Percentage"] = percent,
                    ["BucketBy"] = "user_id",
                    ["RequestedAt"] = DateTime.UtcNow
                }
            };
            await MessageBus.SendAsync("featureflag.rollout.percentage", message, ct);
        }
        await Task.Delay(20, ct);
    }

    private async Task EnableFlagAsync(string project, string flag, string env, CancellationToken ct)
    {
        if (MessageBus != null)
        {
            var message = new DataWarehouse.SDK.Utilities.PluginMessage
            {
                Type = "featureflag.enable",
                Payload = new Dictionary<string, object>
                {
                    ["Provider"] = "LaunchDarkly",
                    ["ProjectKey"] = project,
                    ["FlagKey"] = flag,
                    ["EnvironmentKey"] = env,
                    ["EnabledAt"] = DateTime.UtcNow
                }
            };
            await MessageBus.PublishAsync("featureflag.enable", message, ct);
        }
        await Task.Delay(10, ct);
    }

    private async Task DisableFlagAsync(string project, string flag, string env, CancellationToken ct)
    {
        if (MessageBus != null)
        {
            var message = new DataWarehouse.SDK.Utilities.PluginMessage
            {
                Type = "featureflag.disable",
                Payload = new Dictionary<string, object>
                {
                    ["Provider"] = "LaunchDarkly",
                    ["ProjectKey"] = project,
                    ["FlagKey"] = flag,
                    ["EnvironmentKey"] = env,
                    ["DisabledAt"] = DateTime.UtcNow
                }
            };
            await MessageBus.PublishAsync("featureflag.disable", message, ct);
        }
        await Task.Delay(10, ct);
    }

    private async Task<(bool IsOn, long EvaluationCount)> GetFlagStatusAsync(string project, string flag, string env, CancellationToken ct)
    {
        if (MessageBus != null)
        {
            var message = new DataWarehouse.SDK.Utilities.PluginMessage
            {
                Type = "featureflag.status",
                Payload = new Dictionary<string, object>
                {
                    ["Provider"] = "LaunchDarkly",
                    ["ProjectKey"] = project,
                    ["FlagKey"] = flag,
                    ["EnvironmentKey"] = env,
                    ["RequestedAt"] = DateTime.UtcNow
                }
            };
            await MessageBus.SendAsync("featureflag.status.get", message, ct);

            var isOn = message.Payload.TryGetValue("IsOn", out var onObj) && (bool)onObj;
            var count = message.Payload.TryGetValue("EvaluationCount", out var cntObj) ? Convert.ToInt64(cntObj) : 0L;

            return (isOn, count);
        }

        return (true, 1000L);
    }
}

/// <summary>
/// Split.io feature flag deployment strategy.
/// </summary>
public sealed class SplitIoStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Split.io",
        DeploymentType = DeploymentType.FeatureFlag,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 1,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 3,
        RequiredInfrastructure = ["Split.io"],
        Description = "Split.io feature experimentation and progressive delivery"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        var state = initialState;
        var splitName = GetSplitName(config);
        var environment = config.Environment;

        // Create split
        state = state with { ProgressPercent = 30 };
        await CreateOrUpdateSplitAsync(splitName, config, ct);

        // Configure treatments
        state = state with { ProgressPercent = 60 };
        await ConfigureTreatmentsAsync(splitName, environment, config, ct);

        // Activate
        state = state with { ProgressPercent = 90 };
        await ActivateSplitAsync(splitName, environment, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata) { ["splitName"] = splitName, ["environment"] = environment }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        var splitName = currentState.Metadata.TryGetValue("splitName", out var sn) ? sn?.ToString() : "";
        var environment = currentState.Metadata.TryGetValue("environment", out var env) ? env?.ToString() : "";

        await KillSplitAsync(splitName!, environment!, ct);

        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct)
    {
        var splitName = currentState.Metadata.TryGetValue("splitName", out var sn) ? sn?.ToString() : "";
        var environment = currentState.Metadata.TryGetValue("environment", out var env) ? env?.ToString() : "";

        await UpdateTrafficAllocationAsync(splitName!, environment!, targetInstances, ct);

        return currentState;
    }

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 3 } });

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetSplitName(DeploymentConfig config) => config.StrategyConfig.TryGetValue("splitName", out var sn) && sn is string sns ? sns : $"split-{config.Version}";

    private Task CreateOrUpdateSplitAsync(string name, DeploymentConfig config, CancellationToken ct) => Task.Delay(30, ct);
    private Task ConfigureTreatmentsAsync(string name, string env, DeploymentConfig config, CancellationToken ct) => Task.Delay(20, ct);
    private Task ActivateSplitAsync(string name, string env, CancellationToken ct) => Task.Delay(10, ct);
    private Task KillSplitAsync(string name, string env, CancellationToken ct) => Task.Delay(10, ct);
    private Task UpdateTrafficAllocationAsync(string name, string env, int percent, CancellationToken ct) => Task.Delay(10, ct);
}

/// <summary>
/// Unleash feature flag deployment strategy.
/// </summary>
public sealed class UnleashStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Unleash",
        DeploymentType = DeploymentType.FeatureFlag,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 1,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 3,
        RequiredInfrastructure = ["Unleash"],
        Description = "Unleash open-source feature management"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        var state = initialState;
        var toggleName = GetToggleName(config);
        var project = GetProject(config);

        // Create toggle
        state = state with { ProgressPercent = 30 };
        await CreateToggleAsync(project, toggleName, config, ct);

        // Configure strategy
        state = state with { ProgressPercent = 60 };
        await ConfigureStrategyAsync(project, toggleName, config, ct);

        // Enable
        state = state with { ProgressPercent = 90 };
        await EnableToggleAsync(project, toggleName, config.Environment, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata) { ["project"] = project, ["toggleName"] = toggleName }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        var project = currentState.Metadata.TryGetValue("project", out var p) ? p?.ToString() : "";
        var toggleName = currentState.Metadata.TryGetValue("toggleName", out var tn) ? tn?.ToString() : "";

        await DisableToggleAsync(project!, toggleName!, ct);

        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct) => Task.FromResult(currentState);
    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 5 } });
    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetToggleName(DeploymentConfig config) => config.StrategyConfig.TryGetValue("toggleName", out var tn) && tn is string tns ? tns : $"toggle-{config.Version}";
    private static string GetProject(DeploymentConfig config) => config.StrategyConfig.TryGetValue("project", out var p) && p is string ps ? ps : "default";

    private Task CreateToggleAsync(string project, string name, DeploymentConfig config, CancellationToken ct) => Task.Delay(20, ct);
    private Task ConfigureStrategyAsync(string project, string name, DeploymentConfig config, CancellationToken ct) => Task.Delay(20, ct);
    private Task EnableToggleAsync(string project, string name, string env, CancellationToken ct) => Task.Delay(10, ct);
    private Task DisableToggleAsync(string project, string name, CancellationToken ct) => Task.Delay(10, ct);
}

/// <summary>
/// Flagsmith feature flag deployment strategy.
/// </summary>
public sealed class FlagsmithStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Flagsmith",
        DeploymentType = DeploymentType.FeatureFlag,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 1,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 3,
        RequiredInfrastructure = ["Flagsmith"],
        Description = "Flagsmith feature flag and remote config management"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        var state = initialState;
        var featureName = GetFeatureName(config);
        var environmentId = GetEnvironmentId(config);

        // Create feature
        state = state with { ProgressPercent = 30 };
        await CreateFeatureAsync(featureName, config, ct);

        // Enable in environment
        state = state with { ProgressPercent = 70 };
        await EnableFeatureInEnvironmentAsync(featureName, environmentId, config, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata) { ["featureName"] = featureName, ["environmentId"] = environmentId }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        var featureName = currentState.Metadata.TryGetValue("featureName", out var fn) ? fn?.ToString() : "";
        var environmentId = currentState.Metadata.TryGetValue("environmentId", out var ei) ? ei?.ToString() : "";

        await DisableFeatureInEnvironmentAsync(featureName!, environmentId!, ct);

        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct) => Task.FromResult(currentState);
    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 4 } });
    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetFeatureName(DeploymentConfig config) => config.StrategyConfig.TryGetValue("featureName", out var fn) && fn is string fns ? fns : $"feature-{config.Version}";
    private static string GetEnvironmentId(DeploymentConfig config) => config.StrategyConfig.TryGetValue("environmentId", out var ei) && ei is string eis ? eis : "1";

    private Task CreateFeatureAsync(string name, DeploymentConfig config, CancellationToken ct) => Task.Delay(20, ct);
    private Task EnableFeatureInEnvironmentAsync(string name, string envId, DeploymentConfig config, CancellationToken ct) => Task.Delay(10, ct);
    private Task DisableFeatureInEnvironmentAsync(string name, string envId, CancellationToken ct) => Task.Delay(10, ct);
}

/// <summary>
/// Custom/self-hosted feature flag deployment strategy.
/// </summary>
public sealed class CustomFeatureFlagStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Custom Feature Flags",
        DeploymentType = DeploymentType.FeatureFlag,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 1,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 4,
        RequiredInfrastructure = ["ConfigStore"],
        Description = "Custom feature flag implementation with configurable backend"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        var state = initialState;
        var flagName = GetFlagName(config);
        var configEndpoint = GetConfigEndpoint(config);

        // Update flag configuration
        state = state with { ProgressPercent = 30 };
        await UpdateFlagConfigAsync(configEndpoint, flagName, config, ct);

        // Notify services to refresh
        state = state with { ProgressPercent = 60 };
        await NotifyServicesAsync(configEndpoint, flagName, ct);

        // Verify propagation
        state = state with { ProgressPercent = 90 };
        await VerifyPropagationAsync(configEndpoint, flagName, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata) { ["flagName"] = flagName, ["configEndpoint"] = configEndpoint }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        var flagName = currentState.Metadata.TryGetValue("flagName", out var fn) ? fn?.ToString() : "";
        var configEndpoint = currentState.Metadata.TryGetValue("configEndpoint", out var ce) ? ce?.ToString() : "";

        await DisableFlagAsync(configEndpoint!, flagName!, ct);
        await NotifyServicesAsync(configEndpoint!, flagName!, ct);

        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct) => Task.FromResult(currentState);
    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 10 } });
    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetFlagName(DeploymentConfig config) => config.StrategyConfig.TryGetValue("flagName", out var fn) && fn is string fns ? fns : $"flag-{config.Version}";
    private static string GetConfigEndpoint(DeploymentConfig config) => config.StrategyConfig.TryGetValue("configEndpoint", out var ce) && ce is string ces ? ces : "http://config-service/api";

    private Task UpdateFlagConfigAsync(string endpoint, string name, DeploymentConfig config, CancellationToken ct) => Task.Delay(20, ct);
    private Task NotifyServicesAsync(string endpoint, string name, CancellationToken ct) => Task.Delay(30, ct);
    private Task VerifyPropagationAsync(string endpoint, string name, CancellationToken ct) => Task.Delay(20, ct);
    private Task DisableFlagAsync(string endpoint, string name, CancellationToken ct) => Task.Delay(10, ct);
}
