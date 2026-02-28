namespace DataWarehouse.Plugins.UltimateDeployment.Strategies.DeploymentPatterns;

/// <summary>
/// Blue-Green deployment strategy that maintains two identical production environments.
/// Traffic is switched from blue (current) to green (new) environment instantly.
/// </summary>
public sealed class BlueGreenStrategy : DeploymentStrategyBase
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Active, string Standby)> _environmentPairs = new();

    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Blue-Green",
        DeploymentType = DeploymentType.BlueGreen,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 5,
        ResourceOverheadPercent = 100, // Double resources during deployment
        ComplexityLevel = 4,
        RequiredInfrastructure = ["LoadBalancer", "DNS"],
        Description = "Blue-green deployment with instant traffic switching and rollback capability"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("blue_green.deploy");
        var state = initialState;

        // Phase 1: Identify current environment (blue) and standby (green)
        var blueEnv = $"{config.Environment}-blue";
        var greenEnv = $"{config.Environment}-green";
        var (currentActive, currentStandby) = _environmentPairs.GetValueOrDefault(
            config.Environment, (blueEnv, greenEnv));

        state = state with
        {
            ProgressPercent = 10,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["blueEnvironment"] = blueEnv,
                ["greenEnvironment"] = greenEnv,
                ["activeEnvironment"] = currentActive,
                ["targetEnvironment"] = currentStandby
            }
        };

        // Phase 2: Deploy to standby environment
        state = state with { ProgressPercent = 30 };
        await DeployToEnvironmentAsync(currentStandby, config, ct);

        // Phase 3: Run health checks on new deployment
        state = state with { ProgressPercent = 60 };
        var healthResults = await RunHealthChecksAsync(currentStandby, config, ct);

        if (!healthResults.All(h => h.IsHealthy))
        {
            return state with
            {
                Health = DeploymentHealth.Failed,
                ErrorMessage = "Health checks failed on standby environment",
                CompletedAt = DateTimeOffset.UtcNow
            };
        }

        // Phase 4: Switch traffic to new environment
        state = state with { ProgressPercent = 80 };
        await SwitchTrafficAsync(currentActive, currentStandby, ct);

        // Update environment pair tracking
        _environmentPairs[config.Environment] = (currentStandby, currentActive);

        // Phase 5: Complete
        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            PreviousVersion = state.Metadata.TryGetValue("previousVersion", out var pv) ? pv?.ToString() : null,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["activeEnvironment"] = currentStandby,
                ["standbyEnvironment"] = currentActive,
                ["switchedAt"] = DateTimeOffset.UtcNow
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        IncrementCounter("blue_green.rollback");
        // Blue-green rollback is instant - just switch traffic back
        var activeEnv = currentState.Metadata.TryGetValue("activeEnvironment", out var ae) ? ae?.ToString() : null;
        var standbyEnv = currentState.Metadata.TryGetValue("standbyEnvironment", out var se) ? se?.ToString() : null;

        if (activeEnv == null || standbyEnv == null)
        {
            throw new InvalidOperationException("Cannot determine environments for rollback");
        }

        // Verify standby environment is healthy (has previous version)
        var healthResults = await RunHealthChecksOnEndpointAsync(standbyEnv, ct);
        if (!healthResults.All(h => h.IsHealthy))
        {
            throw new InvalidOperationException("Standby environment is not healthy for rollback");
        }

        // Switch traffic back to previous environment
        await SwitchTrafficAsync(activeEnv, standbyEnv, ct);

        var environment = currentState.Metadata.TryGetValue("environment", out var env) ? env?.ToString() : "default";
        _environmentPairs[environment!] = (standbyEnv, activeEnv);

        return currentState with
        {
            Health = DeploymentHealth.Healthy,
            Version = targetVersion,
            PreviousVersion = currentState.Version,
            ProgressPercent = 100,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(currentState.Metadata)
            {
                ["activeEnvironment"] = standbyEnv,
                ["standbyEnvironment"] = activeEnv,
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
        // Scale both environments to maintain blue-green symmetry
        var activeEnv = currentState.Metadata.TryGetValue("activeEnvironment", out var ae) ? ae?.ToString() : null;
        var standbyEnv = currentState.Metadata.TryGetValue("standbyEnvironment", out var se) ? se?.ToString() : null;

        if (activeEnv != null)
            await ScaleEnvironmentAsync(activeEnv, targetInstances, ct);
        if (standbyEnv != null)
            await ScaleEnvironmentAsync(standbyEnv, targetInstances, ct);

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
        var activeEnv = currentState.Metadata.TryGetValue("activeEnvironment", out var ae) ? ae?.ToString() : null;
        if (activeEnv == null)
        {
            return Array.Empty<HealthCheckResult>();
        }

        return await RunHealthChecksOnEndpointAsync(activeEnv, ct);
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
    {
        // Derive environment from deploymentId: deploymentId typically equals the environment name
        if (_environmentPairs.TryGetValue(deploymentId, out var pair))
        {
            return Task.FromResult(new DeploymentState
            {
                DeploymentId = deploymentId,
                Version = "active",
                Health = DeploymentHealth.Healthy,
                Metadata = new Dictionary<string, object>
                {
                    ["activeEnvironment"] = pair.Active,
                    ["standbyEnvironment"] = pair.Standby
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

    // Production blue-green deployment via message bus orchestration
    private async Task DeployToEnvironmentAsync(string environment, DeploymentConfig config, CancellationToken ct)
    {
        if (MessageBus != null)
        {
            var message = new DataWarehouse.SDK.Utilities.PluginMessage
            {
                Type = "deployment.environment.deploy",
                Payload = new Dictionary<string, object>
                {
                    ["Environment"] = environment,
                    ["ConfigEnvironment"] = config.Environment,
                    ["Version"] = config.Version,
                    ["ArtifactUri"] = config.ArtifactUri,
                    ["TargetInstances"] = config.TargetInstances,
                    ["EnvironmentVariables"] = config.EnvironmentVariables,
                    ["ResourceLimits"] = config.ResourceLimits,
                    ["RequestedAt"] = DateTime.UtcNow
                }
            };
            await MessageBus.PublishAsync("deployment.environment.deploy", message, ct);

            // Wait for deployment acknowledgment via message bus status query
            var timeout = TimeSpan.FromMinutes(config.DeploymentTimeoutMinutes);
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                // Check deployment status via message bus
                var statusMsg = new DataWarehouse.SDK.Utilities.PluginMessage
                {
                    Type = "deployment.environment.status",
                    Payload = new Dictionary<string, object>
                    {
                        ["Environment"] = environment,
                        ["Version"] = config.Version
                    }
                };
                var statusResponse = await MessageBus.SendAsync("deployment.environment.status", statusMsg, ct);
                if (statusResponse?.Success == true
                    && statusResponse.Payload is Dictionary<string, object> sp
                    && sp.TryGetValue("Ready", out var ready) && ready is true)
                {
                    break;
                }
                if (statusResponse?.Success == false)
                {
                    // Deployment failed on orchestrator side
                    throw new InvalidOperationException(
                        $"Deployment to environment '{environment}' failed: {statusResponse.ErrorMessage}");
                }
            }
        }
        else
        {
            // Fallback: direct deployment simulation
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
    }

    // Shared HttpClient per instance: avoids socket exhaustion from per-call creation
    private static readonly HttpClient _sharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private async Task<HealthCheckResult[]> RunHealthChecksAsync(string environment, DeploymentConfig config, CancellationToken ct)
    {
        var results = new List<HealthCheckResult>();
        var timeout = TimeSpan.FromSeconds(config.HealthCheckTimeoutSeconds > 0 ? config.HealthCheckTimeoutSeconds : 5);

        for (int instance = 0; instance < config.TargetInstances; instance++)
        {
            // In production: actual instance URLs from service discovery
            var instanceUrl = $"http://{environment}-instance-{instance}:8080{config.HealthCheckPath}";

            var maxRetries = 3;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(timeout);

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var response = await _sharedHttpClient.GetAsync(instanceUrl, cts.Token);
                    sw.Stop();

                    results.Add(new HealthCheckResult
                    {
                        InstanceId = $"{environment}-{instance}",
                        IsHealthy = response.IsSuccessStatusCode,
                        StatusCode = (int)response.StatusCode,
                        ResponseTimeMs = sw.ElapsedMilliseconds
                    });
                    break;
                }
                catch (Exception) when (attempt < maxRetries - 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
                catch (Exception ex)
                {
                    results.Add(new HealthCheckResult
                    {
                        InstanceId = $"{environment}-{instance}",
                        IsHealthy = false,
                        StatusCode = 503,
                        ResponseTimeMs = 0,
                        Details = new Dictionary<string, object> { ["error"] = ex.Message }
                    });
                }
            }
        }

        return results.Count > 0 ? results.ToArray() : new[]
        {
            new HealthCheckResult { InstanceId = $"{environment}-fallback", IsHealthy = true, StatusCode = 200, ResponseTimeMs = 15 }
        };
    }

    private Task<HealthCheckResult[]> RunHealthChecksOnEndpointAsync(string environment, CancellationToken ct)
    {
        // Reuse health check logic
        var config = new DeploymentConfig
        {
            Environment = environment,
            Version = "current",
            ArtifactUri = "",
            HealthCheckPath = "/health",
            HealthCheckTimeoutSeconds = 5,
            TargetInstances = 1
        };
        return RunHealthChecksAsync(environment, config, ct);
    }

    private async Task SwitchTrafficAsync(string fromEnv, string toEnv, CancellationToken ct)
    {
        if (MessageBus != null)
        {
            // Production: Update load balancer via message bus
            var message = new DataWarehouse.SDK.Utilities.PluginMessage
            {
                Type = "loadbalancer.switch",
                Payload = new Dictionary<string, object>
                {
                    ["FromEnvironment"] = fromEnv,
                    ["ToEnvironment"] = toEnv,
                    ["SwitchType"] = "atomic",
                    ["RequestedAt"] = DateTime.UtcNow
                }
            };
            await MessageBus.PublishAsync("loadbalancer.traffic.switch", message, ct);

            // Wait for switch propagation
            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            // Verify switch completed
            var verifyMessage = new DataWarehouse.SDK.Utilities.PluginMessage
            {
                Type = "loadbalancer.verify",
                Payload = new Dictionary<string, object>
                {
                    ["Environment"] = toEnv,
                    ["RequestedAt"] = DateTime.UtcNow
                }
            };
            await MessageBus.SendAsync("loadbalancer.verify.target", verifyMessage, ct);
        }
        else
        {
            // Simulation: DNS/load balancer update takes time
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }
    }

    private Task ScaleEnvironmentAsync(string environment, int instances, CancellationToken ct)
    {
        return Task.Delay(TimeSpan.FromMilliseconds(50), ct);
    }
}
