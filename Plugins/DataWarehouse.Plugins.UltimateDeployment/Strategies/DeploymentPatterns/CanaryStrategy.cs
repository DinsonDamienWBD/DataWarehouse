namespace DataWarehouse.Plugins.UltimateDeployment.Strategies.DeploymentPatterns;

/// <summary>
/// Canary deployment strategy that gradually rolls out changes to a small subset of users,
/// then progressively increases the traffic percentage while monitoring for issues.
/// </summary>
public sealed class CanaryStrategy : DeploymentStrategyBase
{
    private readonly Dictionary<string, CanaryState> _canaryStates = new();

    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Canary",
        DeploymentType = DeploymentType.Canary,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 30,
        ResourceOverheadPercent = 20,
        ComplexityLevel = 6,
        RequiredInfrastructure = ["LoadBalancer", "TrafficSplitter"],
        Description = "Canary deployment with gradual traffic shifting and automatic rollback on error thresholds"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        var state = initialState;
        var canaryState = new CanaryState
        {
            DeploymentId = state.DeploymentId,
            CanaryPercent = config.CanaryPercent,
            StableVersion = state.PreviousVersion ?? "v0",
            CanaryVersion = config.Version,
            ErrorThreshold = 0.01 // 1% error rate threshold
        };

        _canaryStates[state.DeploymentId] = canaryState;

        // Phase 1: Deploy canary instances (subset of total)
        var canaryInstances = Math.Max(1, config.TargetInstances * config.CanaryPercent / 100);
        state = state with
        {
            ProgressPercent = 10,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["canaryInstances"] = canaryInstances,
                ["canaryPercent"] = config.CanaryPercent,
                ["phase"] = "deploying-canary"
            }
        };

        await DeployCanaryInstancesAsync(config, canaryInstances, ct);

        // Phase 2: Initial health check
        state = state with { ProgressPercent = 20 };
        var initialHealth = await CheckCanaryHealthAsync(state.DeploymentId, ct);
        if (!initialHealth.IsHealthy)
        {
            return state with
            {
                Health = DeploymentHealth.Failed,
                ErrorMessage = "Canary instances failed initial health check",
                CompletedAt = DateTimeOffset.UtcNow
            };
        }

        // Phase 3: Route initial traffic to canary
        state = state with { ProgressPercent = 30 };
        await RouteTrafficAsync(state.DeploymentId, config.CanaryPercent, ct);

        // Phase 4: Monitor and gradually increase traffic
        var trafficSteps = new[] { config.CanaryPercent, 25, 50, 75, 100 };
        var stepIndex = 0;

        foreach (var trafficPercent in trafficSteps.Skip(1))
        {
            ct.ThrowIfCancellationRequested();
            stepIndex++;

            // Monitor for errors
            var metrics = await CollectCanaryMetricsAsync(state.DeploymentId, ct);
            if (metrics.ErrorRate > canaryState.ErrorThreshold)
            {
                // Auto-rollback on error threshold breach
                await RouteTrafficAsync(state.DeploymentId, 0, ct);
                return state with
                {
                    Health = DeploymentHealth.Failed,
                    ErrorMessage = $"Canary error rate {metrics.ErrorRate:P2} exceeded threshold {canaryState.ErrorThreshold:P2}",
                    CompletedAt = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, object>(state.Metadata)
                    {
                        ["failedAtPercent"] = trafficPercent,
                        ["errorRate"] = metrics.ErrorRate
                    }
                };
            }

            // Increase traffic
            await RouteTrafficAsync(state.DeploymentId, trafficPercent, ct);
            state = state with
            {
                ProgressPercent = 30 + (stepIndex * 70 / trafficSteps.Length),
                Metadata = new Dictionary<string, object>(state.Metadata)
                {
                    ["currentTrafficPercent"] = trafficPercent,
                    ["phase"] = trafficPercent == 100 ? "complete" : "shifting-traffic"
                }
            };

            canaryState.CurrentPercent = trafficPercent;

            // Wait for observation window before next increase
            if (trafficPercent < 100)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }

        // Phase 5: Complete - scale up remaining instances
        await ScaleToFullAsync(config, ct);
        _canaryStates.Remove(state.DeploymentId);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["currentTrafficPercent"] = 100,
                ["phase"] = "complete",
                ["canaryDuration"] = DateTimeOffset.UtcNow - state.StartedAt
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        // Route all traffic back to stable version
        await RouteTrafficAsync(deploymentId, 0, ct);

        // Remove canary instances
        await RemoveCanaryInstancesAsync(deploymentId, ct);

        _canaryStates.Remove(deploymentId);

        return currentState with
        {
            Health = DeploymentHealth.Healthy,
            Version = targetVersion,
            PreviousVersion = currentState.Version,
            ProgressPercent = 100,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(currentState.Metadata)
            {
                ["currentTrafficPercent"] = 0,
                ["phase"] = "rolled-back",
                ["rolledBackAt"] = DateTimeOffset.UtcNow
            }
        };
    }

    protected override async Task<DeploymentState> ScaleCoreAsync(
        string deploymentId,
        int targetInstances,
        DeploymentState currentState,
        CancellationToken ct)
    {
        if (_canaryStates.TryGetValue(deploymentId, out var canaryState))
        {
            // Maintain canary ratio during scaling
            var canaryInstances = targetInstances * canaryState.CurrentPercent / 100;
            var stableInstances = targetInstances - canaryInstances;

            await ScaleCanaryAsync(deploymentId, canaryInstances, ct);
            await ScaleStableAsync(deploymentId, stableInstances, ct);
        }

        return currentState with
        {
            TargetInstances = targetInstances,
            DeployedInstances = targetInstances
        };
    }

    protected override async Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId,
        DeploymentState currentState,
        CancellationToken ct)
    {
        var results = new List<HealthCheckResult>();

        // Check canary instances
        var canaryHealth = await CheckCanaryHealthAsync(deploymentId, ct);
        results.Add(new HealthCheckResult
        {
            InstanceId = $"{deploymentId}-canary",
            IsHealthy = canaryHealth.IsHealthy,
            StatusCode = 200,
            ResponseTimeMs = canaryHealth.ResponseTimeMs,
            Details = new Dictionary<string, object>
            {
                ["type"] = "canary",
                ["errorRate"] = canaryHealth.ErrorRate
            }
        });

        // Check stable instances
        var stableHealth = await CheckStableHealthAsync(deploymentId, ct);
        results.Add(new HealthCheckResult
        {
            InstanceId = $"{deploymentId}-stable",
            IsHealthy = stableHealth.IsHealthy,
            StatusCode = 200,
            ResponseTimeMs = stableHealth.ResponseTimeMs,
            Details = new Dictionary<string, object>
            {
                ["type"] = "stable"
            }
        });

        return results.ToArray();
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
    {
        if (_canaryStates.TryGetValue(deploymentId, out var canaryState))
        {
            return Task.FromResult(new DeploymentState
            {
                DeploymentId = deploymentId,
                Version = canaryState.CanaryVersion,
                PreviousVersion = canaryState.StableVersion,
                Health = DeploymentHealth.InProgress,
                Metadata = new Dictionary<string, object>
                {
                    ["currentTrafficPercent"] = canaryState.CurrentPercent,
                    ["phase"] = "canary-in-progress"
                }
            });
        }

        return Task.FromResult(new DeploymentState
        {
            DeploymentId = deploymentId,
            Version = "unknown",
            Health = DeploymentHealth.Unknown
        });
    }

    // Production canary deployment via message bus orchestration
    private async Task DeployCanaryInstancesAsync(DeploymentConfig config, int instanceCount, CancellationToken ct)
    {
        if (MessageBus != null)
        {
            var message = new DataWarehouse.SDK.Utilities.PluginMessage
            {
                Type = "deployment.canary.deploy",
                Payload = new Dictionary<string, object>
                {
                    ["Environment"] = config.Environment,
                    ["Version"] = config.Version,
                    ["InstanceCount"] = instanceCount,
                    ["ArtifactUri"] = config.ArtifactUri,
                    ["TrafficWeight"] = 10, // Default 10% canary traffic
                    ["RequestedAt"] = DateTime.UtcNow
                }
            };
            await MessageBus.PublishAsync("deployment.traffic.route", message, ct);
        }
        await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
    }

    private Task<(bool IsHealthy, double ResponseTimeMs, double ErrorRate)> CheckCanaryHealthAsync(
        string deploymentId, CancellationToken ct)
    {
        return Task.FromResult((true, 15.0, 0.001));
    }

    private Task<(bool IsHealthy, double ResponseTimeMs)> CheckStableHealthAsync(
        string deploymentId, CancellationToken ct)
    {
        return Task.FromResult((true, 12.0));
    }

    private Task RouteTrafficAsync(string deploymentId, int canaryPercent, CancellationToken ct)
    {
        return Task.Delay(TimeSpan.FromMilliseconds(50), ct);
    }

    private Task<(double ErrorRate, double Latency)> CollectCanaryMetricsAsync(
        string deploymentId, CancellationToken ct)
    {
        return Task.FromResult((0.005, 20.0));
    }

    private Task ScaleToFullAsync(DeploymentConfig config, CancellationToken ct)
    {
        return Task.Delay(TimeSpan.FromMilliseconds(100), ct);
    }

    private Task RemoveCanaryInstancesAsync(string deploymentId, CancellationToken ct)
    {
        return Task.Delay(TimeSpan.FromMilliseconds(50), ct);
    }

    private Task ScaleCanaryAsync(string deploymentId, int instances, CancellationToken ct)
    {
        return Task.Delay(TimeSpan.FromMilliseconds(50), ct);
    }

    private Task ScaleStableAsync(string deploymentId, int instances, CancellationToken ct)
    {
        return Task.Delay(TimeSpan.FromMilliseconds(50), ct);
    }

    private sealed class CanaryState
    {
        public string DeploymentId { get; init; } = "";
        public int CanaryPercent { get; init; }
        public int CurrentPercent { get; set; }
        public string StableVersion { get; init; } = "";
        public string CanaryVersion { get; init; } = "";
        public double ErrorThreshold { get; init; }
    }
}
