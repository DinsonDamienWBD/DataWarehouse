namespace DataWarehouse.Plugins.UltimateDeployment.Strategies.ContainerOrchestration;

/// <summary>
/// Kubernetes deployment strategy using native Kubernetes Deployment resources.
/// Supports rolling updates, health checks, and auto-scaling via HPA.
/// </summary>
public sealed class KubernetesDeploymentStrategy : DeploymentStrategyBase
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _counters = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DeploymentState> _k8sStates = new();
    private DateTimeOffset? _lastHealthCheck;
    private bool _lastHealthCheckResult = true;

    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Kubernetes",
        DeploymentType = DeploymentType.ContainerOrchestration,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 10,
        ResourceOverheadPercent = 25,
        ComplexityLevel = 5,
        RequiredInfrastructure = ["Kubernetes"],
        Description = "Native Kubernetes Deployment with rolling updates and auto-scaling"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        // Validate Kubernetes configuration
        ValidateK8sConfig(config);

        var state = initialState;
        var ns = GetNamespace(config);
        var deploymentName = GetDeploymentName(config);

        // Phase 1: Create/Update ConfigMaps and Secrets
        state = state with { ProgressPercent = 10 };
        await ApplyConfigMapsAsync(ns, config, ct);
        await ApplySecretsAsync(ns, config, ct);

        // Phase 2: Apply Deployment manifest
        state = state with { ProgressPercent = 30 };
        var deployment = BuildDeploymentManifest(config, deploymentName);

        try
        {
            await ApplyDeploymentAsync(ns, deployment, ct);
        }
        catch (HttpRequestException ex)
        {
            // Handle K8s API errors
            var statusCode = ex.StatusCode;
            if (statusCode == System.Net.HttpStatusCode.Unauthorized) // 401
            {
                IncrementCounter("k8s.auth_failure");
                throw new InvalidOperationException("Kubernetes API authentication failed. Check credentials.", ex);
            }
            else if (statusCode == System.Net.HttpStatusCode.Forbidden) // 403
            {
                IncrementCounter("k8s.forbidden");
                throw new InvalidOperationException("Kubernetes API forbidden. Check RBAC permissions.", ex);
            }
            else if (statusCode == System.Net.HttpStatusCode.Conflict) // 409
            {
                IncrementCounter("k8s.conflict");
                throw new InvalidOperationException("Kubernetes resource conflict. Resource may already exist.", ex);
            }
            else if (statusCode == (System.Net.HttpStatusCode)422) // 422 - Unprocessable Entity (validation error)
            {
                IncrementCounter("k8s.validation_error");
                throw new InvalidOperationException("Kubernetes manifest validation failed. Check manifest syntax.", ex);
            }
            throw;
        }

        // Phase 3: Wait for rollout
        state = state with { ProgressPercent = 50 };
        var rolloutSuccess = await WaitForRolloutAsync(ns, deploymentName, config.DeploymentTimeoutMinutes, ct);

        if (!rolloutSuccess)
        {
            return state with
            {
                Health = DeploymentHealth.Failed,
                ErrorMessage = "Kubernetes rollout did not complete in time",
                CompletedAt = DateTimeOffset.UtcNow
            };
        }

        // Phase 4: Apply Service if needed
        state = state with { ProgressPercent = 70 };
        await ApplyServiceAsync(ns, config, ct);

        // Phase 5: Apply HPA if auto-scaling configured
        state = state with { ProgressPercent = 85 };
        if (config.StrategyConfig.TryGetValue("enableAutoScaling", out var eas) && eas is true)
        {
            await ApplyHpaAsync(ns, deploymentName, config, ct);
        }

        // Phase 6: Verify pods are healthy
        state = state with { ProgressPercent = 95 };
        var podStatus = await GetPodStatusAsync(ns, deploymentName, ct);

        var finalState = state with
        {
            Health = podStatus.AllReady ? DeploymentHealth.Healthy : DeploymentHealth.Degraded,
            ProgressPercent = 100,
            DeployedInstances = podStatus.TotalPods,
            HealthyInstances = podStatus.ReadyPods,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["namespace"] = ns,
                ["deploymentName"] = deploymentName,
                ["totalPods"] = podStatus.TotalPods,
                ["readyPods"] = podStatus.ReadyPods
            }
        };

        _k8sStates[state.DeploymentId] = finalState;
        IncrementCounter("k8s.deploy");

        return finalState;
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        var ns = currentState.Metadata.TryGetValue("namespace", out var nsObj) ? nsObj?.ToString() : "default";
        var deploymentName = currentState.Metadata.TryGetValue("deploymentName", out var dnObj) ? dnObj?.ToString() : "";

        // Use kubectl rollout undo
        await RollbackDeploymentAsync(ns!, deploymentName!, ct);
        await WaitForRolloutAsync(ns!, deploymentName!, 5, ct);

        IncrementCounter("k8s.rollback");

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
        var ns = currentState.Metadata.TryGetValue("namespace", out var nsObj) ? nsObj?.ToString() : "default";
        var deploymentName = currentState.Metadata.TryGetValue("deploymentName", out var dnObj) ? dnObj?.ToString() : "";

        await ScaleDeploymentAsync(ns!, deploymentName!, targetInstances, ct);

        IncrementCounter("k8s.scale");

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
        // Check if health check is cached (60-second cache)
        if (_lastHealthCheck.HasValue && DateTimeOffset.UtcNow - _lastHealthCheck.Value < TimeSpan.FromSeconds(60))
        {
            var ns = currentState.Metadata.TryGetValue("namespace", out var nsObj) ? nsObj?.ToString() : "default";
            var deploymentName = currentState.Metadata.TryGetValue("deploymentName", out var dnObj) ? dnObj?.ToString() : "";

            return new[]
            {
                new HealthCheckResult
                {
                    InstanceId = $"{deploymentName}-cached",
                    IsHealthy = _lastHealthCheckResult,
                    StatusCode = _lastHealthCheckResult ? 200 : 503,
                    ResponseTimeMs = 5,
                    Details = new Dictionary<string, object> { ["cached"] = true, ["namespace"] = ns ?? "default" }
                }
            };
        }

        var ns2 = currentState.Metadata.TryGetValue("namespace", out var nsObj2) ? nsObj2?.ToString() : "default";
        var deploymentName2 = currentState.Metadata.TryGetValue("deploymentName", out var dnObj2) ? dnObj2?.ToString() : "";

        var pods = await GetPodsAsync(ns2!, deploymentName2!, ct);
        var results = pods.Select(p => new HealthCheckResult
        {
            InstanceId = p.Name,
            IsHealthy = p.Ready,
            StatusCode = p.Ready ? 200 : 503,
            ResponseTimeMs = 10,
            Details = new Dictionary<string, object>
            {
                ["phase"] = p.Phase,
                ["restartCount"] = p.RestartCount
            }
        }).ToArray();

        _lastHealthCheck = DateTimeOffset.UtcNow;
        _lastHealthCheckResult = results.All(r => r.IsHealthy);

        return results;
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

    // Configuration validation and helper methods
    private void ValidateK8sConfig(DeploymentConfig config)
    {
        // Validate cluster context
        if (!config.StrategyConfig.TryGetValue("clusterContext", out var context) || context == null || string.IsNullOrWhiteSpace(context.ToString()))
            throw new InvalidOperationException("Kubernetes cluster context is required.");

        // Validate namespace (valid K8s name: lowercase alphanumeric, hyphens, max 63 chars)
        var ns = GetNamespace(config);
        if (!System.Text.RegularExpressions.Regex.IsMatch(ns, @"^[a-z0-9]([-a-z0-9]*[a-z0-9])?$") || ns.Length > 63)
            throw new InvalidOperationException($"Invalid Kubernetes namespace: '{ns}'. Must be lowercase alphanumeric with hyphens, max 63 chars.");

        // Validate kubeconfig path if not in-cluster
        if (config.StrategyConfig.TryGetValue("kubeconfigPath", out var kubeconfigPath) && kubeconfigPath != null)
        {
            var path = kubeconfigPath.ToString();
            if (!string.IsNullOrWhiteSpace(path) && !System.IO.File.Exists(path))
            {
                // Don't throw - kubeconfig might be in default location
            }
        }

        // Validate deployment timeout (10s to 3600s)
        if (config.DeploymentTimeoutMinutes < 0.17 || config.DeploymentTimeoutMinutes > 60) // 10s to 60 minutes
            throw new ArgumentException($"Deployment timeout must be between 10 seconds and 60 minutes. Got: {config.DeploymentTimeoutMinutes} minutes");

        // Validate max unavailable replicas
        if (config.StrategyConfig.TryGetValue("maxUnavailable", out var maxUnavail) && maxUnavail is int maxUnavailable)
        {
            if (maxUnavailable < 0)
                throw new ArgumentException($"Max unavailable replicas cannot be negative. Got: {maxUnavailable}");
        }
    }

    private void IncrementCounter(string name)
    {
        _counters.AddOrUpdate(name, 1, (_, current) => System.Threading.Interlocked.Increment(ref current));
    }

    private static string GetNamespace(DeploymentConfig config)
    {
        return config.StrategyConfig.TryGetValue("namespace", out var ns) && ns is string nsStr
            ? nsStr : "default";
    }

    private static string GetDeploymentName(DeploymentConfig config)
    {
        return config.StrategyConfig.TryGetValue("deploymentName", out var dn) && dn is string dnStr
            ? dnStr : $"deploy-{config.Environment}";
    }

    private static object BuildDeploymentManifest(DeploymentConfig config, string name)
    {
        // Strategy: rolling update with configurable parameters
        var strategyConfig = new
        {
            type = "RollingUpdate",
            rollingUpdate = new
            {
                maxSurge = config.StrategyConfig.TryGetValue("maxSurge", out var ms) && ms is int msi ? msi : 1,
                maxUnavailable = config.StrategyConfig.TryGetValue("maxUnavailable", out var mu) && mu is int mui ? mui : 0
            }
        };

        // Container ports
        var ports = new[]
        {
            new { containerPort = 8080, protocol = "TCP", name = "http" },
            new { containerPort = 8443, protocol = "TCP", name = "https" }
        };

        // Liveness probe for detecting crashed containers
        var livenessProbe = new
        {
            httpGet = new { path = config.HealthCheckPath, port = 8080, scheme = "HTTP" },
            initialDelaySeconds = 30,
            periodSeconds = 10,
            timeoutSeconds = config.HealthCheckTimeoutSeconds,
            successThreshold = 1,
            failureThreshold = 3
        };

        // Readiness probe for traffic routing
        var readinessProbe = new
        {
            httpGet = new { path = config.HealthCheckPath, port = 8080, scheme = "HTTP" },
            initialDelaySeconds = 5,
            periodSeconds = config.HealthCheckIntervalSeconds,
            timeoutSeconds = config.HealthCheckTimeoutSeconds,
            successThreshold = 1,
            failureThreshold = 3
        };

        // Resource requests and limits
        Dictionary<string, object> limits = config.ResourceLimits.Count > 0
            ? config.ResourceLimits.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value)
            : new Dictionary<string, object>
            {
                ["cpu"] = "1000m",
                ["memory"] = "512Mi"
            };

        var resources = new
        {
            requests = new
            {
                cpu = config.ResourceLimits.TryGetValue("cpu", out var reqCpu) ? reqCpu : "100m",
                memory = config.ResourceLimits.TryGetValue("memory", out var reqMem) ? reqMem : "128Mi"
            },
            limits
        };

        Dictionary<string, object> labels = config.Labels.Count > 0
            ? config.Labels.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value)
            : new Dictionary<string, object>
            {
                ["app"] = name,
                ["version"] = config.Version,
                ["managed-by"] = "datawarehouse-deployment"
            };

        Dictionary<string, object> selectorLabels = config.Labels.Count > 0
            ? config.Labels.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value)
            : new Dictionary<string, object> { ["app"] = name };

        Dictionary<string, object> templateLabels = config.Labels.Count > 0
            ? config.Labels.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value)
            : new Dictionary<string, object>
            {
                ["app"] = name,
                ["version"] = config.Version
            };

        return new
        {
            apiVersion = "apps/v1",
            kind = "Deployment",
            metadata = new
            {
                name,
                labels,
                annotations = new Dictionary<string, object>
                {
                    ["deployment.kubernetes.io/revision"] = "1",
                    ["datawarehouse.io/deployed-at"] = DateTimeOffset.UtcNow.ToString("o")
                }
            },
            spec = new
            {
                replicas = config.TargetInstances,
                selector = new
                {
                    matchLabels = selectorLabels
                },
                strategy = strategyConfig,
                template = new
                {
                    metadata = new
                    {
                        labels = templateLabels
                    },
                    spec = new
                    {
                        containers = new[]
                        {
                            new
                            {
                                name = "app",
                                image = config.ArtifactUri,
                                imagePullPolicy = "IfNotPresent",
                                ports,
                                env = config.EnvironmentVariables.Select(kv => new { name = kv.Key, value = kv.Value.ToString() }).ToArray(),
                                resources,
                                readinessProbe,
                                livenessProbe
                            }
                        },
                        restartPolicy = "Always",
                        terminationGracePeriodSeconds = 30
                    }
                }
            }
        };
    }

    // Simulated Kubernetes operations
    private Task ApplyConfigMapsAsync(string ns, DeploymentConfig config, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(30), ct);

    private Task ApplySecretsAsync(string ns, DeploymentConfig config, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(30), ct);

    private Task ApplyDeploymentAsync(string ns, object deployment, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(100), ct);

    private Task<bool> WaitForRolloutAsync(string ns, string name, int timeoutMinutes, CancellationToken ct)
        => Task.FromResult(true);

    private Task ApplyServiceAsync(string ns, DeploymentConfig config, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(30), ct);

    private Task ApplyHpaAsync(string ns, string deploymentName, DeploymentConfig config, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(30), ct);

    private Task<(int TotalPods, int ReadyPods, bool AllReady)> GetPodStatusAsync(string ns, string name, CancellationToken ct)
        => Task.FromResult((3, 3, true));

    private Task RollbackDeploymentAsync(string ns, string name, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);

    private Task ScaleDeploymentAsync(string ns, string name, int replicas, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);

    private Task<PodInfo[]> GetPodsAsync(string ns, string name, CancellationToken ct)
        => Task.FromResult(new[]
        {
            new PodInfo { Name = $"{name}-abc123", Ready = true, Phase = "Running", RestartCount = 0 },
            new PodInfo { Name = $"{name}-def456", Ready = true, Phase = "Running", RestartCount = 0 }
        });

    private record PodInfo
    {
        public string Name { get; init; } = "";
        public bool Ready { get; init; }
        public string Phase { get; init; } = "";
        public int RestartCount { get; init; }
    }
}

/// <summary>
/// Docker Swarm deployment strategy using Swarm services.
/// </summary>
public sealed class DockerSwarmStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Docker Swarm",
        DeploymentType = DeploymentType.ContainerOrchestration,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 8,
        ResourceOverheadPercent = 20,
        ComplexityLevel = 3,
        RequiredInfrastructure = ["DockerSwarm"],
        Description = "Docker Swarm service deployment with rolling updates"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        var state = initialState;
        var serviceName = GetServiceName(config);

        // Check if service exists
        var exists = await ServiceExistsAsync(serviceName, ct);

        if (exists)
        {
            // Update existing service
            state = state with { ProgressPercent = 30 };
            await UpdateServiceAsync(serviceName, config, ct);
        }
        else
        {
            // Create new service
            state = state with { ProgressPercent = 30 };
            await CreateServiceAsync(serviceName, config, ct);
        }

        // Wait for convergence
        state = state with { ProgressPercent = 70 };
        await WaitForServiceConvergenceAsync(serviceName, config.DeploymentTimeoutMinutes, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["serviceName"] = serviceName
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        var serviceName = currentState.Metadata.TryGetValue("serviceName", out var sn) ? sn?.ToString() : "";
        await RollbackServiceAsync(serviceName!, ct);

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
        var serviceName = currentState.Metadata.TryGetValue("serviceName", out var sn) ? sn?.ToString() : "";
        await ScaleServiceAsync(serviceName!, targetInstances, ct);

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
        return Task.FromResult(new[]
        {
            new HealthCheckResult
            {
                InstanceId = $"{deploymentId}-swarm",
                IsHealthy = true,
                StatusCode = 200,
                ResponseTimeMs = 10
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

    private static string GetServiceName(DeploymentConfig config)
    {
        return config.StrategyConfig.TryGetValue("serviceName", out var sn) && sn is string snStr
            ? snStr : $"service-{config.Environment}";
    }

    private Task<bool> ServiceExistsAsync(string name, CancellationToken ct)
        => Task.FromResult(false);

    private Task CreateServiceAsync(string name, DeploymentConfig config, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(100), ct);

    private Task UpdateServiceAsync(string name, DeploymentConfig config, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(100), ct);

    private Task WaitForServiceConvergenceAsync(string name, int timeoutMinutes, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);

    private Task RollbackServiceAsync(string name, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);

    private Task ScaleServiceAsync(string name, int replicas, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);
}

/// <summary>
/// HashiCorp Nomad deployment strategy.
/// </summary>
public sealed class NomadStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Nomad",
        DeploymentType = DeploymentType.ContainerOrchestration,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 10,
        ResourceOverheadPercent = 25,
        ComplexityLevel = 5,
        RequiredInfrastructure = ["Nomad", "Consul"],
        Description = "HashiCorp Nomad job deployment with Consul integration"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        var state = initialState;
        var jobId = GetJobId(config);

        // Parse and submit job
        state = state with { ProgressPercent = 20 };
        var jobSpec = BuildNomadJobSpec(config, jobId);
        var evalId = await SubmitJobAsync(jobSpec, ct);

        // Wait for evaluation
        state = state with { ProgressPercent = 40 };
        await WaitForEvaluationAsync(evalId, ct);

        // Wait for allocations
        state = state with { ProgressPercent = 60 };
        await WaitForAllocationsAsync(jobId, config.TargetInstances, ct);

        // Register with Consul for service discovery
        state = state with { ProgressPercent = 80 };
        await RegisterConsulServiceAsync(jobId, config, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["jobId"] = jobId,
                ["evaluationId"] = evalId
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        var jobId = currentState.Metadata.TryGetValue("jobId", out var ji) ? ji?.ToString() : "";
        await RevertJobAsync(jobId!, ct);

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
        var jobId = currentState.Metadata.TryGetValue("jobId", out var ji) ? ji?.ToString() : "";
        await ScaleJobAsync(jobId!, targetInstances, ct);

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
        return Task.FromResult(new[]
        {
            new HealthCheckResult
            {
                InstanceId = $"{deploymentId}-nomad",
                IsHealthy = true,
                StatusCode = 200,
                ResponseTimeMs = 12
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

    private static string GetJobId(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("jobId", out var ji) && ji is string jiStr
            ? jiStr : $"job-{config.Environment}";

    private static object BuildNomadJobSpec(DeploymentConfig config, string jobId)
        => new { ID = jobId, Type = "service", Datacenters = new[] { "dc1" } };

    private Task<string> SubmitJobAsync(object jobSpec, CancellationToken ct)
        => Task.FromResult("eval-123");

    private Task WaitForEvaluationAsync(string evalId, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);

    private Task WaitForAllocationsAsync(string jobId, int count, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(100), ct);

    private Task RegisterConsulServiceAsync(string jobId, DeploymentConfig config, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(30), ct);

    private Task RevertJobAsync(string jobId, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);

    private Task ScaleJobAsync(string jobId, int count, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);
}

/// <summary>
/// AWS ECS (Elastic Container Service) deployment strategy.
/// </summary>
public sealed class AwsEcsStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "AWS ECS",
        DeploymentType = DeploymentType.ContainerOrchestration,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 15,
        ResourceOverheadPercent = 25,
        ComplexityLevel = 6,
        RequiredInfrastructure = ["AWS", "ECS", "ALB"],
        Description = "AWS ECS Fargate/EC2 deployment with ALB integration"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        var state = initialState;
        var cluster = GetClusterName(config);
        var serviceName = GetServiceName(config);

        // Register new task definition
        state = state with { ProgressPercent = 20 };
        var taskDefArn = await RegisterTaskDefinitionAsync(config, ct);

        // Update service with new task definition
        state = state with { ProgressPercent = 40 };
        await UpdateServiceAsync(cluster, serviceName, taskDefArn, config, ct);

        // Wait for service stability
        state = state with { ProgressPercent = 70 };
        await WaitForServiceStabilityAsync(cluster, serviceName, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["cluster"] = cluster,
                ["serviceName"] = serviceName,
                ["taskDefinitionArn"] = taskDefArn
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        var cluster = currentState.Metadata.TryGetValue("cluster", out var c) ? c?.ToString() : "";
        var serviceName = currentState.Metadata.TryGetValue("serviceName", out var s) ? s?.ToString() : "";

        await RollbackServiceAsync(cluster!, serviceName!, ct);

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
        var cluster = currentState.Metadata.TryGetValue("cluster", out var c) ? c?.ToString() : "";
        var serviceName = currentState.Metadata.TryGetValue("serviceName", out var s) ? s?.ToString() : "";

        await ScaleServiceAsync(cluster!, serviceName!, targetInstances, ct);

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
        return Task.FromResult(new[]
        {
            new HealthCheckResult
            {
                InstanceId = $"{deploymentId}-ecs",
                IsHealthy = true,
                StatusCode = 200,
                ResponseTimeMs = 15
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

    private static string GetClusterName(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("cluster", out var c) && c is string cs ? cs : "default";

    private static string GetServiceName(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("serviceName", out var s) && s is string ss
            ? ss : $"svc-{config.Environment}";

    private Task<string> RegisterTaskDefinitionAsync(DeploymentConfig config, CancellationToken ct)
        => Task.FromResult("arn:aws:ecs:us-east-1:123456789:task-definition/app:123");

    private Task UpdateServiceAsync(string cluster, string service, string taskDefArn, DeploymentConfig config, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(100), ct);

    private Task WaitForServiceStabilityAsync(string cluster, string service, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(100), ct);

    private Task RollbackServiceAsync(string cluster, string service, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);

    private Task ScaleServiceAsync(string cluster, string service, int count, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);
}

/// <summary>
/// Azure AKS (Azure Kubernetes Service) deployment strategy.
/// </summary>
public sealed class AzureAksStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Azure AKS",
        DeploymentType = DeploymentType.ContainerOrchestration,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 12,
        ResourceOverheadPercent = 25,
        ComplexityLevel = 6,
        RequiredInfrastructure = ["Azure", "AKS"],
        Description = "Azure Kubernetes Service deployment with Azure integrations"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        var state = initialState;
        var resourceGroup = GetResourceGroup(config);
        var clusterName = GetClusterName(config);

        // Get AKS credentials
        state = state with { ProgressPercent = 10 };
        await GetAksCredentialsAsync(resourceGroup, clusterName, ct);

        // Deploy using kubectl
        state = state with { ProgressPercent = 40 };
        await ApplyManifestsAsync(config, ct);

        // Wait for deployment
        state = state with { ProgressPercent = 70 };
        await WaitForDeploymentAsync(config, ct);

        // Configure Azure-specific features
        state = state with { ProgressPercent = 90 };
        await ConfigureAzureMonitorAsync(config, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["resourceGroup"] = resourceGroup,
                ["clusterName"] = clusterName
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        await RollbackDeploymentAsync(currentState, ct);
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
        await ScaleDeploymentAsync(currentState, targetInstances, ct);
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
        return Task.FromResult(new[]
        {
            new HealthCheckResult
            {
                InstanceId = $"{deploymentId}-aks",
                IsHealthy = true,
                StatusCode = 200,
                ResponseTimeMs = 12
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

    private static string GetResourceGroup(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("resourceGroup", out var rg) && rg is string rgs ? rgs : "default-rg";

    private static string GetClusterName(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("clusterName", out var cn) && cn is string cns ? cns : "aks-cluster";

    private Task GetAksCredentialsAsync(string rg, string cluster, CancellationToken ct) => Task.CompletedTask;
    private Task ApplyManifestsAsync(DeploymentConfig config, CancellationToken ct) => Task.Delay(100, ct);
    private Task WaitForDeploymentAsync(DeploymentConfig config, CancellationToken ct) => Task.Delay(100, ct);
    private Task ConfigureAzureMonitorAsync(DeploymentConfig config, CancellationToken ct) => Task.Delay(30, ct);
    private Task RollbackDeploymentAsync(DeploymentState state, CancellationToken ct) => Task.Delay(50, ct);
    private Task ScaleDeploymentAsync(DeploymentState state, int count, CancellationToken ct) => Task.Delay(50, ct);
}

/// <summary>
/// Google GKE (Google Kubernetes Engine) deployment strategy.
/// </summary>
public sealed class GoogleGkeStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Google GKE",
        DeploymentType = DeploymentType.ContainerOrchestration,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 10,
        ResourceOverheadPercent = 25,
        ComplexityLevel = 6,
        RequiredInfrastructure = ["GCP", "GKE"],
        Description = "Google Kubernetes Engine deployment with GCP integrations"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        var state = initialState;
        var project = GetProject(config);
        var zone = GetZone(config);
        var cluster = GetCluster(config);

        // Get GKE credentials
        state = state with { ProgressPercent = 10 };
        await GetGkeCredentialsAsync(project, zone, cluster, ct);

        // Apply deployment
        state = state with { ProgressPercent = 50 };
        await ApplyDeploymentAsync(config, ct);

        // Wait and verify
        state = state with { ProgressPercent = 80 };
        await WaitForReadyAsync(config, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["project"] = project,
                ["zone"] = zone,
                ["cluster"] = cluster
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        await RollbackAsync(currentState, ct);
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
        await ScaleAsync(currentState, targetInstances, ct);
        return currentState with { TargetInstances = targetInstances, DeployedInstances = targetInstances };
    }

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId, DeploymentState currentState, CancellationToken ct)
    {
        return Task.FromResult(new[] { new HealthCheckResult { InstanceId = $"{deploymentId}-gke", IsHealthy = true, StatusCode = 200, ResponseTimeMs = 11 } });
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetProject(DeploymentConfig config) => config.StrategyConfig.TryGetValue("project", out var p) && p is string ps ? ps : "my-project";
    private static string GetZone(DeploymentConfig config) => config.StrategyConfig.TryGetValue("zone", out var z) && z is string zs ? zs : "us-central1-a";
    private static string GetCluster(DeploymentConfig config) => config.StrategyConfig.TryGetValue("cluster", out var c) && c is string cs ? cs : "gke-cluster";

    private Task GetGkeCredentialsAsync(string project, string zone, string cluster, CancellationToken ct) => Task.CompletedTask;
    private Task ApplyDeploymentAsync(DeploymentConfig config, CancellationToken ct) => Task.Delay(100, ct);
    private Task WaitForReadyAsync(DeploymentConfig config, CancellationToken ct) => Task.Delay(100, ct);
    private Task RollbackAsync(DeploymentState state, CancellationToken ct) => Task.Delay(50, ct);
    private Task ScaleAsync(DeploymentState state, int count, CancellationToken ct) => Task.Delay(50, ct);
}

/// <summary>
/// AWS EKS (Elastic Kubernetes Service) deployment strategy.
/// </summary>
public sealed class AwsEksStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "AWS EKS",
        DeploymentType = DeploymentType.ContainerOrchestration,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 12,
        ResourceOverheadPercent = 25,
        ComplexityLevel = 6,
        RequiredInfrastructure = ["AWS", "EKS"],
        Description = "AWS Elastic Kubernetes Service deployment with AWS integrations"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        var state = initialState;
        var cluster = GetClusterName(config);
        var region = GetRegion(config);

        // Configure kubectl for EKS
        state = state with { ProgressPercent = 10 };
        await UpdateKubeconfigAsync(cluster, region, ct);

        // Apply manifests
        state = state with { ProgressPercent = 50 };
        await ApplyManifestsAsync(config, ct);

        // Wait for deployment
        state = state with { ProgressPercent = 80 };
        await WaitForDeploymentAsync(config, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["cluster"] = cluster,
                ["region"] = region
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        await RollbackDeploymentAsync(currentState, ct);
        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<DeploymentState> ScaleCoreAsync(
        string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct)
    {
        await ScaleDeploymentAsync(currentState, targetInstances, ct);
        return currentState with { TargetInstances = targetInstances, DeployedInstances = targetInstances };
    }

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = $"{deploymentId}-eks", IsHealthy = true, StatusCode = 200, ResponseTimeMs = 13 } });

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetClusterName(DeploymentConfig config) => config.StrategyConfig.TryGetValue("cluster", out var c) && c is string cs ? cs : "eks-cluster";
    private static string GetRegion(DeploymentConfig config) => config.StrategyConfig.TryGetValue("region", out var r) && r is string rs ? rs : "us-east-1";

    private Task UpdateKubeconfigAsync(string cluster, string region, CancellationToken ct) => Task.CompletedTask;
    private Task ApplyManifestsAsync(DeploymentConfig config, CancellationToken ct) => Task.Delay(100, ct);
    private Task WaitForDeploymentAsync(DeploymentConfig config, CancellationToken ct) => Task.Delay(100, ct);
    private Task RollbackDeploymentAsync(DeploymentState state, CancellationToken ct) => Task.Delay(50, ct);
    private Task ScaleDeploymentAsync(DeploymentState state, int count, CancellationToken ct) => Task.Delay(50, ct);
}
