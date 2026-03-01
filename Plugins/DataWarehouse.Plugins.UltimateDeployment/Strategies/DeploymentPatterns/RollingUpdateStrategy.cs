using DataWarehouse.SDK.Utilities;
namespace DataWarehouse.Plugins.UltimateDeployment.Strategies.DeploymentPatterns;

/// <summary>
/// Rolling update strategy that gradually replaces instances of the old version with the new version.
/// Ensures that a minimum number of instances are always available during the update.
/// </summary>
public sealed class RollingUpdateStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Rolling Update",
        DeploymentType = DeploymentType.RollingUpdate,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = false,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 15,
        ResourceOverheadPercent = 25,
        ComplexityLevel = 3,
        RequiredInfrastructure = [],
        Description = "Rolling update with configurable batch size and health check gates"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        var state = initialState;
        var batchSize = GetBatchSize(config);
        var totalInstances = config.TargetInstances;
        var updatedInstances = 0;

        state = state with
        {
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["batchSize"] = batchSize,
                ["totalInstances"] = totalInstances,
                ["phase"] = "rolling-update"
            }
        };

        // Process instances in batches
        while (updatedInstances < totalInstances)
        {
            ct.ThrowIfCancellationRequested();

            var batchEnd = Math.Min(updatedInstances + batchSize, totalInstances);
            var currentBatch = Enumerable.Range(updatedInstances, batchEnd - updatedInstances).ToArray();

            // Update instances in this batch
            foreach (var instanceIndex in currentBatch)
            {
                // Drain instance
                await DrainInstanceAsync(state.DeploymentId, instanceIndex, ct);

                // Update instance
                await UpdateInstanceAsync(state.DeploymentId, instanceIndex, config, ct);

                // Wait for instance to be ready
                await WaitForInstanceReadyAsync(state.DeploymentId, instanceIndex, config, ct);

                // Health check
                var healthResult = await HealthCheckInstanceAsync(state.DeploymentId, instanceIndex, config, ct);
                if (!healthResult.IsHealthy)
                {
                    return state with
                    {
                        Health = DeploymentHealth.Failed,
                        ErrorMessage = $"Instance {instanceIndex} failed health check after update",
                        CompletedAt = DateTimeOffset.UtcNow,
                        Metadata = new Dictionary<string, object>(state.Metadata)
                        {
                            ["failedInstance"] = instanceIndex,
                            ["updatedInstances"] = updatedInstances
                        }
                    };
                }

                updatedInstances++;
            }

            // Update progress
            var progress = (int)((double)updatedInstances / totalInstances * 100);
            state = state with
            {
                ProgressPercent = progress,
                DeployedInstances = updatedInstances,
                HealthyInstances = updatedInstances,
                Metadata = new Dictionary<string, object>(state.Metadata)
                {
                    ["updatedInstances"] = updatedInstances
                }
            };
        }

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = totalInstances,
            HealthyInstances = totalInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["phase"] = "complete"
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        // Rolling rollback - update instances back to previous version
        var totalInstances = currentState.DeployedInstances;
        var rolledBackInstances = 0;
        var batchSize = GetBatchSizeFromState(currentState);

        while (rolledBackInstances < totalInstances)
        {
            ct.ThrowIfCancellationRequested();

            var batchEnd = Math.Min(rolledBackInstances + batchSize, totalInstances);

            for (var i = rolledBackInstances; i < batchEnd; i++)
            {
                await DrainInstanceAsync(deploymentId, i, ct);
                await RollbackInstanceAsync(deploymentId, i, targetVersion, ct);
                await WaitForInstanceReadyAsync(deploymentId, i, null, ct);
                rolledBackInstances++;
            }
        }

        return currentState with
        {
            Health = DeploymentHealth.Healthy,
            Version = targetVersion,
            PreviousVersion = currentState.Version,
            ProgressPercent = 100,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    protected override async Task<DeploymentState> ScaleCoreAsync(
        string deploymentId,
        int targetInstances,
        DeploymentState currentState,
        CancellationToken ct)
    {
        var currentInstances = currentState.DeployedInstances;

        if (targetInstances > currentInstances)
        {
            // Scale up - add new instances with current version
            for (var i = currentInstances; i < targetInstances; i++)
            {
                await AddInstanceAsync(deploymentId, i, currentState.Version, ct);
            }
        }
        else if (targetInstances < currentInstances)
        {
            // Scale down - remove instances
            for (var i = currentInstances - 1; i >= targetInstances; i--)
            {
                await DrainInstanceAsync(deploymentId, i, ct);
                await RemoveInstanceAsync(deploymentId, i, ct);
            }
        }

        return currentState with
        {
            TargetInstances = targetInstances,
            DeployedInstances = targetInstances,
            HealthyInstances = targetInstances
        };
    }

    protected override async Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId,
        DeploymentState currentState,
        CancellationToken ct)
    {
        // P2-2893: Parallelise health checks with Task.WhenAll instead of serial await per instance
        var tasks = Enumerable.Range(0, currentState.DeployedInstances)
            .Select(i => HealthCheckInstanceAsync(deploymentId, i, null, ct));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
    {
        return Task.FromResult(new DeploymentState
        {
            DeploymentId = deploymentId,
            Version = "unknown",
            Health = DeploymentHealth.Unknown
        });
    }

    private static int GetBatchSize(DeploymentConfig config)
    {
        if (config.StrategyConfig.TryGetValue("batchSize", out var bs) && bs is int batchSize)
            return batchSize;

        // Default: 25% of instances or at least 1
        return Math.Max(1, config.TargetInstances / 4);
    }

    private static int GetBatchSizeFromState(DeploymentState state)
    {
        if (state.Metadata.TryGetValue("batchSize", out var bs) && bs is int batchSize)
            return batchSize;
        return Math.Max(1, state.DeployedInstances / 4);
    }

    // Production deployment operations via message bus orchestration
    private async Task DrainInstanceAsync(string deploymentId, int instanceIndex, CancellationToken ct)
    {
        if (MessageBus != null)
        {
            var message = new DataWarehouse.SDK.Utilities.PluginMessage
            {
                Type = "deployment.instance.drain",
                Payload = new Dictionary<string, object>
                {
                    ["DeploymentId"] = deploymentId,
                    ["InstanceIndex"] = instanceIndex,
                    ["RequestedAt"] = DateTime.UtcNow
                }
            };
            await MessageBus.PublishAsync("deployment.instance.drain", message, ct);
        }
        await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
    }

    private async Task UpdateInstanceAsync(string deploymentId, int instanceIndex, DeploymentConfig config, CancellationToken ct)
    {
        if (MessageBus != null)
        {
            var message = new DataWarehouse.SDK.Utilities.PluginMessage
            {
                Type = "deployment.instance.restart",
                Payload = new Dictionary<string, object>
                {
                    ["DeploymentId"] = deploymentId,
                    ["InstanceIndex"] = instanceIndex,
                    ["NewVersion"] = config.Version,
                    ["ArtifactUri"] = config.ArtifactUri,
                    ["RequestedAt"] = DateTime.UtcNow
                }
            };
            await MessageBus.PublishAsync("deployment.instance.restart", message, ct);
        }
        await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
    }

    private async Task WaitForInstanceReadyAsync(string deploymentId, int instanceIndex, DeploymentConfig? config, CancellationToken ct)
    {
        if (MessageBus != null)
        {
            var message = new DataWarehouse.SDK.Utilities.PluginMessage
            {
                Type = "deployment.instance.wait-ready",
                Payload = new Dictionary<string, object>
                {
                    ["DeploymentId"] = deploymentId,
                    ["InstanceIndex"] = instanceIndex,
                    ["RequestedAt"] = DateTime.UtcNow
                }
            };
            await MessageBus.PublishAsync("deployment.instance.wait-ready", message, ct);
        }
        await Task.Delay(TimeSpan.FromMilliseconds(30), ct);
    }

    private async Task<HealthCheckResult> HealthCheckInstanceAsync(string deploymentId, int instanceIndex, DeploymentConfig? config, CancellationToken ct)
    {
        if (MessageBus != null)
        {
            var message = new DataWarehouse.SDK.Utilities.PluginMessage
            {
                Type = "deployment.health.check",
                Payload = new Dictionary<string, object>
                {
                    ["DeploymentId"] = deploymentId,
                    ["InstanceIndex"] = instanceIndex,
                    ["HealthCheckPath"] = config?.HealthCheckPath ?? "/health",
                    ["RequestedAt"] = DateTime.UtcNow
                }
            };
            await MessageBus.SendAsync("deployment.health.check", message, ct);

            // Extract health check result from message
            var isHealthy = message.Payload.TryGetValue("IsHealthy", out var healthyObj) && (bool)healthyObj;
            var statusCode = message.Payload.TryGetValue("StatusCode", out var codeObj) ? Convert.ToInt32(codeObj) : 200;
            var responseTime = message.Payload.TryGetValue("ResponseTimeMs", out var timeObj) ? Convert.ToDouble(timeObj) : 10.0;

            return new HealthCheckResult
            {
                InstanceId = $"{deploymentId}-{instanceIndex}",
                IsHealthy = isHealthy,
                StatusCode = statusCode,
                ResponseTimeMs = responseTime
            };
        }

        return new HealthCheckResult
        {
            InstanceId = $"{deploymentId}-{instanceIndex}",
            IsHealthy = true,
            StatusCode = 200,
            ResponseTimeMs = 10
        };
    }

    private async Task RollbackInstanceAsync(string deploymentId, int instanceIndex, string targetVersion, CancellationToken ct)
    {
        if (MessageBus != null)
        {
            var message = new DataWarehouse.SDK.Utilities.PluginMessage
            {
                Type = "deployment.instance.rollback",
                Payload = new Dictionary<string, object>
                {
                    ["DeploymentId"] = deploymentId,
                    ["InstanceIndex"] = instanceIndex,
                    ["TargetVersion"] = targetVersion,
                    ["RequestedAt"] = DateTime.UtcNow
                }
            };
            await MessageBus.PublishAsync("deployment.instance.rollback", message, ct);
        }
        await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
    }

    private async Task AddInstanceAsync(string deploymentId, int instanceIndex, string version, CancellationToken ct)
    {
        if (MessageBus != null)
        {
            var message = new DataWarehouse.SDK.Utilities.PluginMessage
            {
                Type = "deployment.instance.add",
                Payload = new Dictionary<string, object>
                {
                    ["DeploymentId"] = deploymentId,
                    ["InstanceIndex"] = instanceIndex,
                    ["Version"] = version,
                    ["RequestedAt"] = DateTime.UtcNow
                }
            };
            await MessageBus.PublishAsync("deployment.instance.add", message, ct);
        }
        await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
    }

    private async Task RemoveInstanceAsync(string deploymentId, int instanceIndex, CancellationToken ct)
    {
        if (MessageBus != null)
        {
            var message = new DataWarehouse.SDK.Utilities.PluginMessage
            {
                Type = "deployment.instance.remove",
                Payload = new Dictionary<string, object>
                {
                    ["DeploymentId"] = deploymentId,
                    ["InstanceIndex"] = instanceIndex,
                    ["RequestedAt"] = DateTime.UtcNow
                }
            };
            await MessageBus.PublishAsync("deployment.instance.remove", message, ct);
        }
        await Task.Delay(TimeSpan.FromMilliseconds(30), ct);
    }
}

/// <summary>
/// Recreate deployment strategy that terminates all old instances before creating new ones.
/// Simpler but involves downtime during the transition.
/// </summary>
public sealed class RecreateStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Recreate",
        DeploymentType = DeploymentType.Recreate,
        SupportsZeroDowntime = false,
        SupportsInstantRollback = false,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 5,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 1,
        RequiredInfrastructure = [],
        Description = "Simple recreate strategy - terminates old before creating new (involves downtime)"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        var state = initialState with { ProgressPercent = 10 };

        // Phase 1: Terminate all existing instances
        await TerminateAllInstancesAsync(state.DeploymentId, ct);
        state = state with { ProgressPercent = 40, DeployedInstances = 0, HealthyInstances = 0 };

        // Phase 2: Create new instances
        await CreateInstancesAsync(config, ct);
        state = state with { ProgressPercent = 80 };

        // Phase 3: Health check
        var healthy = await WaitForHealthyAsync(state.DeploymentId, config, ct);
        if (!healthy)
        {
            return state with
            {
                Health = DeploymentHealth.Failed,
                ErrorMessage = "New instances failed to become healthy",
                CompletedAt = DateTimeOffset.UtcNow
            };
        }

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        // Same process - terminate current, create with old version
        await TerminateAllInstancesAsync(deploymentId, ct);
        await CreateInstancesWithVersionAsync(deploymentId, targetVersion, currentState.TargetInstances, ct);

        return currentState with
        {
            Health = DeploymentHealth.Healthy,
            Version = targetVersion,
            PreviousVersion = currentState.Version,
            ProgressPercent = 100,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    protected override async Task<DeploymentState> ScaleCoreAsync(
        string deploymentId,
        int targetInstances,
        DeploymentState currentState,
        CancellationToken ct)
    {
        await ScaleInstancesAsync(deploymentId, targetInstances, ct);

        return currentState with
        {
            TargetInstances = targetInstances,
            DeployedInstances = targetInstances,
            HealthyInstances = targetInstances
        };
    }

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId,
        DeploymentState currentState,
        CancellationToken ct)
    {
        var results = Enumerable.Range(0, currentState.DeployedInstances)
            .Select(i => new HealthCheckResult
            {
                InstanceId = $"{deploymentId}-{i}",
                IsHealthy = true,
                StatusCode = 200,
                ResponseTimeMs = 10
            })
            .ToArray();

        return Task.FromResult(results);
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
    {
        return Task.FromResult(new DeploymentState
        {
            DeploymentId = deploymentId,
            Version = "unknown",
            Health = DeploymentHealth.Unknown
        });
    }

    private Task TerminateAllInstancesAsync(string deploymentId, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(100), ct);

    private Task CreateInstancesAsync(DeploymentConfig config, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(100), ct);

    private Task CreateInstancesWithVersionAsync(string deploymentId, string version, int count, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(100), ct);

    private Task<bool> WaitForHealthyAsync(string deploymentId, DeploymentConfig config, CancellationToken ct)
        => Task.FromResult(true);

    private Task ScaleInstancesAsync(string deploymentId, int count, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);
}

/// <summary>
/// A/B Testing deployment strategy for testing different versions with different user segments.
/// </summary>
public sealed class ABTestingStrategy : DeploymentStrategyBase
{
    private readonly BoundedDictionary<string, long> _counters = new BoundedDictionary<string, long>(1000);
    private readonly BoundedDictionary<string, DeploymentState> _experimentStates = new BoundedDictionary<string, DeploymentState>(1000);
    // Per-deploymentId health cache to avoid single slot shared across concurrent experiments
    private readonly BoundedDictionary<string, (long TicksUtc, bool IsHealthy)> _healthCache = new BoundedDictionary<string, (long, bool)>(1000);

    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "A/B Testing",
        DeploymentType = DeploymentType.ABTesting,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 20,
        ResourceOverheadPercent = 50,
        ComplexityLevel = 7,
        RequiredInfrastructure = ["LoadBalancer", "UserSegmentation"],
        Description = "A/B testing deployment for controlled experiments with user segmentation"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        // Validate experiment configuration
        ValidateExperimentConfig(config);

        var state = initialState;

        // Get A/B split configuration
        var splitPercent = GetSplitPercent(config);

        // Deploy variant B
        state = state with { ProgressPercent = 20 };
        await DeployVariantAsync("B", config, ct);

        // Configure traffic split
        state = state with { ProgressPercent = 50 };
        await ConfigureTrafficSplitAsync(state.DeploymentId, splitPercent, ct);

        // Run experiment
        state = state with
        {
            ProgressPercent = 80,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["variantA"] = 100 - splitPercent,
                ["variantB"] = splitPercent,
                ["experimentStatus"] = "running"
            }
        };

        var finalState = state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow
        };

        _experimentStates[state.DeploymentId] = finalState;
        IncrementCounter("abtest.assignment");

        return finalState;
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        // Route all traffic to variant A
        await ConfigureTrafficSplitAsync(deploymentId, 0, ct);
        await RemoveVariantAsync("B", deploymentId, ct);

        return currentState with
        {
            Health = DeploymentHealth.Healthy,
            Version = targetVersion,
            ProgressPercent = 100,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    protected override async Task<DeploymentState> ScaleCoreAsync(
        string deploymentId,
        int targetInstances,
        DeploymentState currentState,
        CancellationToken ct)
    {
        await ScaleVariantsAsync(deploymentId, targetInstances, ct);
        return currentState with
        {
            TargetInstances = targetInstances,
            DeployedInstances = targetInstances
        };
    }

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId,
        DeploymentState currentState,
        CancellationToken ct)
    {
        // Per-deploymentId 60-second health cache
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        if (_healthCache.TryGetValue(deploymentId, out var cached)
            && (nowTicks - cached.TicksUtc) < TimeSpan.FromSeconds(60).Ticks)
        {
            return Task.FromResult(new[]
            {
                new HealthCheckResult { InstanceId = $"{deploymentId}-A", IsHealthy = cached.IsHealthy, StatusCode = 200, ResponseTimeMs = 10 },
                new HealthCheckResult { InstanceId = $"{deploymentId}-B", IsHealthy = cached.IsHealthy, StatusCode = 200, ResponseTimeMs = 12 }
            });
        }

        // Validate experiment state
        var isRunning = _experimentStates.TryGetValue(deploymentId, out var expState)
            && expState.Metadata.TryGetValue("experimentStatus", out var status)
            && status?.ToString() == "running";

        _healthCache[deploymentId] = (nowTicks, isRunning);

        return Task.FromResult(new[]
        {
            new HealthCheckResult { InstanceId = $"{deploymentId}-A", IsHealthy = isRunning, StatusCode = 200, ResponseTimeMs = 10 },
            new HealthCheckResult { InstanceId = $"{deploymentId}-B", IsHealthy = isRunning, StatusCode = 200, ResponseTimeMs = 12 }
        });
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
    {
        return Task.FromResult(new DeploymentState
        {
            DeploymentId = deploymentId,
            Version = "unknown",
            Health = DeploymentHealth.Unknown
        });
    }

    private void ValidateExperimentConfig(DeploymentConfig config)
    {
        // Validate experiment ID format (if provided)
        if (config.StrategyConfig.TryGetValue("experimentId", out var expId) && expId != null)
        {
            var expIdStr = expId.ToString();
            if (string.IsNullOrWhiteSpace(expIdStr))
                throw new ArgumentException("Experiment ID cannot be empty.");
        }

        // Validate traffic split percentages
        var splitPercent = GetSplitPercent(config);
        if (splitPercent < 0 || splitPercent > 100)
            throw new ArgumentException($"Traffic split percentage must be between 0 and 100. Got: {splitPercent}");

        // Validate variant definitions (at least 2)
        if (config.StrategyConfig.TryGetValue("variants", out var variants) && variants is Array variantArray)
        {
            if (variantArray.Length < 2)
                throw new ArgumentException("A/B testing requires at least 2 variants.");
        }

        // Validate measurement duration (1 hour to 90 days)
        if (config.StrategyConfig.TryGetValue("measurementDurationHours", out var duration) && duration is int hours)
        {
            if (hours < 1 || hours > 2160) // 90 days = 2160 hours
                throw new ArgumentException($"Measurement duration must be between 1 hour and 90 days (2160 hours). Got: {hours} hours");
        }

        // Validate statistical significance threshold (0.01-0.1, i.e., 1%-10%)
        if (config.StrategyConfig.TryGetValue("significanceThreshold", out var threshold) && threshold is double thresholdVal)
        {
            if (thresholdVal < 0.01 || thresholdVal > 0.1)
                throw new ArgumentException($"Statistical significance threshold must be between 0.01 and 0.1. Got: {thresholdVal}");
        }
    }

    private static int GetSplitPercent(DeploymentConfig config)
    {
        if (config.StrategyConfig.TryGetValue("splitPercent", out var sp) && sp is int split)
            return split;
        return 50;
    }

    private new void IncrementCounter(string name)
    {
        _counters.AddOrUpdate(name, 1, (_, current) => System.Threading.Interlocked.Increment(ref current));
    }

    private Task DeployVariantAsync(string variant, DeploymentConfig config, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(100), ct);

    private Task ConfigureTrafficSplitAsync(string deploymentId, int variantBPercent, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);

    private Task RemoveVariantAsync(string variant, string deploymentId, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);

    private Task ScaleVariantsAsync(string deploymentId, int instances, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);
}

/// <summary>
/// Shadow deployment strategy that deploys new version alongside production and mirrors traffic
/// without affecting actual users. Used for testing in production environment.
/// </summary>
public sealed class ShadowDeploymentStrategy : DeploymentStrategyBase
{
    private readonly BoundedDictionary<string, long> _counters = new BoundedDictionary<string, long>(1000);
    private readonly BoundedDictionary<string, DeploymentState> _shadowStates = new BoundedDictionary<string, DeploymentState>(1000);
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingRequests = new();
    // Per-deploymentId health cache (thread-safe, no shared single-slot race)
    private readonly BoundedDictionary<string, (long TicksUtc, bool IsHealthy)> _healthCache = new BoundedDictionary<string, (long, bool)>(1000);

    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Shadow",
        DeploymentType = DeploymentType.Shadow,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 10,
        ResourceOverheadPercent = 100,
        ComplexityLevel = 5,
        RequiredInfrastructure = ["TrafficMirroring"],
        Description = "Shadow (dark launch) deployment with traffic mirroring for production testing"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        // Validate shadow deployment configuration
        ValidateShadowConfig(config);

        var state = initialState;

        // Deploy shadow instances
        state = state with { ProgressPercent = 30 };
        await DeployShadowInstancesAsync(config, ct);

        // Configure traffic mirroring
        state = state with { ProgressPercent = 60 };
        await ConfigureTrafficMirroringAsync(state.DeploymentId, ct);

        // Verify shadow is receiving mirrored traffic
        state = state with { ProgressPercent = 80 };
        await VerifyShadowTrafficAsync(state.DeploymentId, ct);

        var finalState = state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["shadowMode"] = true,
                ["mirroringEnabled"] = true
            }
        };

        _shadowStates[state.DeploymentId] = finalState;
        IncrementCounter("shadow.request_mirrored");

        return finalState;
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        await DisableTrafficMirroringAsync(deploymentId, ct);
        await RemoveShadowInstancesAsync(deploymentId, ct);

        // Cancel pending shadow requests
        while (_pendingRequests.TryDequeue(out _)) { }

        return currentState with
        {
            Health = DeploymentHealth.Healthy,
            Version = targetVersion,
            ProgressPercent = 100,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    protected override async Task<DeploymentState> ScaleCoreAsync(
        string deploymentId,
        int targetInstances,
        DeploymentState currentState,
        CancellationToken ct)
    {
        await ScaleShadowInstancesAsync(deploymentId, targetInstances, ct);
        return currentState with
        {
            TargetInstances = targetInstances,
            DeployedInstances = targetInstances
        };
    }

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId,
        DeploymentState currentState,
        CancellationToken ct)
    {
        // Per-deploymentId 60-second health cache
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        if (_healthCache.TryGetValue(deploymentId, out var cached)
            && (nowTicks - cached.TicksUtc) < TimeSpan.FromSeconds(60).Ticks)
        {
            return Task.FromResult(new[]
            {
                new HealthCheckResult
                {
                    InstanceId = $"{deploymentId}-shadow",
                    IsHealthy = cached.IsHealthy,
                    StatusCode = 200,
                    ResponseTimeMs = 15,
                    Details = new Dictionary<string, object> { ["type"] = "shadow", ["cached"] = true }
                }
            });
        }

        // Check shadow endpoint reachability
        var isReachable = _shadowStates.TryGetValue(deploymentId, out var shadowState)
            && shadowState.Metadata.TryGetValue("mirroringEnabled", out var mirroring)
            && mirroring is bool enabled && enabled;

        _healthCache[deploymentId] = (nowTicks, isReachable);

        return Task.FromResult(new[]
        {
            new HealthCheckResult
            {
                InstanceId = $"{deploymentId}-shadow",
                IsHealthy = isReachable,
                StatusCode = isReachable ? 200 : 503,
                ResponseTimeMs = 15,
                Details = new Dictionary<string, object>
                {
                    ["type"] = "shadow",
                    ["cached"] = false,
                    ["mirroringEnabled"] = isReachable
                }
            }
        });
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
    {
        return Task.FromResult(new DeploymentState
        {
            DeploymentId = deploymentId,
            Version = "unknown",
            Health = DeploymentHealth.Unknown
        });
    }

    private void ValidateShadowConfig(DeploymentConfig config)
    {
        // Validate shadow endpoint
        if (!config.StrategyConfig.TryGetValue("shadowEndpoint", out var endpoint) || endpoint == null)
            throw new InvalidOperationException("Shadow endpoint is required for shadow deployment.");

        var endpointStr = endpoint.ToString();
        if (string.IsNullOrWhiteSpace(endpointStr))
            throw new InvalidOperationException("Shadow endpoint cannot be empty.");

        if (!Uri.TryCreate(endpointStr, UriKind.Absolute, out _))
            throw new InvalidOperationException($"Invalid shadow endpoint URI: '{endpointStr}'");

        // Validate traffic percentage (0-100)
        if (config.StrategyConfig.TryGetValue("trafficPercentage", out var traffic) && traffic is int trafficPercent)
        {
            if (trafficPercent < 0 || trafficPercent > 100)
                throw new ArgumentException($"Traffic percentage must be between 0 and 100. Got: {trafficPercent}");
        }

        // Validate comparison mode
        if (config.StrategyConfig.TryGetValue("comparisonMode", out var mode) && mode != null)
        {
            var modeStr = mode.ToString();
            if (modeStr != "sync" && modeStr != "async")
                throw new ArgumentException($"Comparison mode must be 'sync' or 'async'. Got: '{modeStr}'");
        }

        // Validate timeout multiplier (1.0-10.0)
        if (config.StrategyConfig.TryGetValue("timeoutMultiplier", out var multiplier) && multiplier is double mult)
        {
            if (mult < 1.0 || mult > 10.0)
                throw new ArgumentException($"Timeout multiplier must be between 1.0 and 10.0. Got: {mult}");
        }
    }

    private new void IncrementCounter(string name)
    {
        _counters.AddOrUpdate(name, 1, (_, current) => System.Threading.Interlocked.Increment(ref current));
    }

    private Task DeployShadowInstancesAsync(DeploymentConfig config, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(100), ct);

    private Task ConfigureTrafficMirroringAsync(string deploymentId, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);

    private Task VerifyShadowTrafficAsync(string deploymentId, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(30), ct);

    private Task DisableTrafficMirroringAsync(string deploymentId, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(30), ct);

    private Task RemoveShadowInstancesAsync(string deploymentId, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);

    private Task ScaleShadowInstancesAsync(string deploymentId, int instances, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);
}
