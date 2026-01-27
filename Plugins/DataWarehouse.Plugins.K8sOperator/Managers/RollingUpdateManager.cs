using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.K8sOperator.Managers;

/// <summary>
/// Manages rolling updates for DataWarehouse Kubernetes deployments.
/// Provides RollingUpdate deployment strategy, MaxSurge/MaxUnavailable configuration,
/// PodDisruptionBudget creation, readiness gate handling, and canary deployment support.
/// </summary>
public sealed class RollingUpdateManager
{
    private readonly IKubernetesClient _client;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Initializes a new instance of the RollingUpdateManager.
    /// </summary>
    /// <param name="client">Kubernetes client for API interactions.</param>
    public RollingUpdateManager(IKubernetesClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Configures rolling update strategy for a deployment.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="deploymentName">Name of the deployment.</param>
    /// <param name="config">Rolling update configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the configuration.</returns>
    public async Task<RollingUpdateResult> ConfigureRollingUpdateAsync(
        string namespaceName,
        string deploymentName,
        RollingUpdateConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);
        ArgumentNullException.ThrowIfNull(config);

        var patch = new
        {
            spec = new
            {
                strategy = new
                {
                    type = "RollingUpdate",
                    rollingUpdate = new
                    {
                        maxSurge = config.MaxSurge,
                        maxUnavailable = config.MaxUnavailable
                    }
                },
                minReadySeconds = config.MinReadySeconds,
                progressDeadlineSeconds = config.ProgressDeadlineSeconds,
                revisionHistoryLimit = config.RevisionHistoryLimit
            }
        };

        var patchJson = JsonSerializer.Serialize(patch, _jsonOptions);
        var result = await _client.PatchResourceAsync(
            "apps/v1",
            "deployments",
            namespaceName,
            deploymentName,
            patchJson,
            ct: ct);

        return new RollingUpdateResult
        {
            Success = result.Success,
            DeploymentName = deploymentName,
            Message = result.Message
        };
    }

    /// <summary>
    /// Creates a PodDisruptionBudget for the specified deployment.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="deploymentName">Name of the deployment.</param>
    /// <param name="config">PDB configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the PDB creation.</returns>
    public async Task<RollingUpdateResult> CreatePodDisruptionBudgetAsync(
        string namespaceName,
        string deploymentName,
        PodDisruptionBudgetConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);
        ArgumentNullException.ThrowIfNull(config);

        var pdb = new PodDisruptionBudget
        {
            ApiVersion = "policy/v1",
            Kind = "PodDisruptionBudget",
            Metadata = new ObjectMeta
            {
                Name = $"{deploymentName}-pdb",
                Namespace = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator",
                    ["app.kubernetes.io/name"] = deploymentName
                }
            },
            Spec = new PdbSpec
            {
                MinAvailable = config.MinAvailable,
                MaxUnavailable = config.MaxUnavailable,
                Selector = new LabelSelector
                {
                    MatchLabels = config.Selector ?? new Dictionary<string, string>
                    {
                        ["app"] = deploymentName
                    }
                },
                UnhealthyPodEvictionPolicy = config.UnhealthyPodEvictionPolicy
            }
        };

        var json = JsonSerializer.Serialize(pdb, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "policy/v1",
            "poddisruptionbudgets",
            namespaceName,
            $"{deploymentName}-pdb",
            json,
            ct);

        return new RollingUpdateResult
        {
            Success = result.Success,
            DeploymentName = deploymentName,
            ResourceName = $"{deploymentName}-pdb",
            Message = result.Message
        };
    }

    /// <summary>
    /// Configures readiness gates for pods in a deployment.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="deploymentName">Name of the deployment.</param>
    /// <param name="readinessGates">List of readiness gate conditions.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the configuration.</returns>
    public async Task<RollingUpdateResult> ConfigureReadinessGatesAsync(
        string namespaceName,
        string deploymentName,
        List<string> readinessGates,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);
        ArgumentNullException.ThrowIfNull(readinessGates);

        var gates = readinessGates.Select(g => new { conditionType = g }).ToList();

        var patch = new
        {
            spec = new
            {
                template = new
                {
                    spec = new
                    {
                        readinessGates = gates
                    }
                }
            }
        };

        var patchJson = JsonSerializer.Serialize(patch, _jsonOptions);
        var result = await _client.PatchResourceAsync(
            "apps/v1",
            "deployments",
            namespaceName,
            deploymentName,
            patchJson,
            ct: ct);

        return new RollingUpdateResult
        {
            Success = result.Success,
            DeploymentName = deploymentName,
            Message = result.Message
        };
    }

    /// <summary>
    /// Initiates a canary deployment.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="deploymentName">Name of the base deployment.</param>
    /// <param name="config">Canary deployment configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the canary deployment initiation.</returns>
    public async Task<CanaryDeploymentResult> InitiateCanaryDeploymentAsync(
        string namespaceName,
        string deploymentName,
        CanaryDeploymentConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);
        ArgumentNullException.ThrowIfNull(config);

        var canaryDeploymentName = $"{deploymentName}-canary";

        // Get the current deployment
        var currentDeployment = await _client.GetResourceAsync(
            "apps/v1",
            "deployments",
            namespaceName,
            deploymentName,
            ct);

        if (!currentDeployment.Found || string.IsNullOrEmpty(currentDeployment.Json))
        {
            return new CanaryDeploymentResult
            {
                Success = false,
                Message = $"Deployment {deploymentName} not found"
            };
        }

        // Create canary deployment based on the current one
        var deployment = JsonSerializer.Deserialize<JsonElement>(currentDeployment.Json);
        var canaryDeployment = BuildCanaryDeployment(deployment, canaryDeploymentName, config);

        var json = JsonSerializer.Serialize(canaryDeployment, _jsonOptions);
        var createResult = await _client.ApplyResourceAsync(
            "apps/v1",
            "deployments",
            namespaceName,
            canaryDeploymentName,
            json,
            ct);

        if (!createResult.Success)
        {
            return new CanaryDeploymentResult
            {
                Success = false,
                Message = $"Failed to create canary deployment: {createResult.Message}"
            };
        }

        // If using Istio, create VirtualService for traffic splitting
        if (config.UseIstio)
        {
            var vsResult = await CreateIstioVirtualServiceAsync(
                namespaceName,
                deploymentName,
                canaryDeploymentName,
                config.TrafficPercentage,
                ct);

            if (!vsResult.Success)
            {
                return new CanaryDeploymentResult
                {
                    Success = false,
                    CanaryDeploymentName = canaryDeploymentName,
                    Message = $"Canary deployment created but traffic routing failed: {vsResult.Message}"
                };
            }
        }

        return new CanaryDeploymentResult
        {
            Success = true,
            CanaryDeploymentName = canaryDeploymentName,
            TrafficPercentage = config.TrafficPercentage,
            Message = "Canary deployment initiated successfully"
        };
    }

    /// <summary>
    /// Promotes a canary deployment to production.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="deploymentName">Name of the base deployment.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the promotion.</returns>
    public async Task<RollingUpdateResult> PromoteCanaryAsync(
        string namespaceName,
        string deploymentName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        var canaryDeploymentName = $"{deploymentName}-canary";

        // Get canary deployment
        var canaryDeployment = await _client.GetResourceAsync(
            "apps/v1",
            "deployments",
            namespaceName,
            canaryDeploymentName,
            ct);

        if (!canaryDeployment.Found)
        {
            return new RollingUpdateResult
            {
                Success = false,
                DeploymentName = deploymentName,
                Message = "Canary deployment not found"
            };
        }

        // Extract the image from canary deployment
        var canaryJson = JsonSerializer.Deserialize<JsonElement>(canaryDeployment.Json!);
        var containers = canaryJson.GetProperty("spec")
            .GetProperty("template")
            .GetProperty("spec")
            .GetProperty("containers");

        var imageUpdates = new List<object>();
        foreach (var container in containers.EnumerateArray())
        {
            var name = container.GetProperty("name").GetString();
            var image = container.GetProperty("image").GetString();
            imageUpdates.Add(new { name, image });
        }

        // Update the main deployment with canary's image
        var patch = new
        {
            spec = new
            {
                template = new
                {
                    spec = new
                    {
                        containers = imageUpdates
                    }
                }
            }
        };

        var patchJson = JsonSerializer.Serialize(patch, _jsonOptions);
        var updateResult = await _client.PatchResourceAsync(
            "apps/v1",
            "deployments",
            namespaceName,
            deploymentName,
            patchJson,
            ct: ct);

        if (!updateResult.Success)
        {
            return new RollingUpdateResult
            {
                Success = false,
                DeploymentName = deploymentName,
                Message = $"Failed to update main deployment: {updateResult.Message}"
            };
        }

        // Delete canary deployment
        await _client.DeleteResourceAsync(
            "apps/v1",
            "deployments",
            namespaceName,
            canaryDeploymentName,
            ct);

        // Delete VirtualService if exists
        await _client.DeleteResourceAsync(
            "networking.istio.io/v1beta1",
            "virtualservices",
            namespaceName,
            $"{deploymentName}-canary-vs",
            ct);

        return new RollingUpdateResult
        {
            Success = true,
            DeploymentName = deploymentName,
            Message = "Canary promoted successfully"
        };
    }

    /// <summary>
    /// Rolls back a canary deployment.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="deploymentName">Name of the base deployment.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the rollback.</returns>
    public async Task<RollingUpdateResult> RollbackCanaryAsync(
        string namespaceName,
        string deploymentName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        var canaryDeploymentName = $"{deploymentName}-canary";

        // Delete canary deployment
        var deleteResult = await _client.DeleteResourceAsync(
            "apps/v1",
            "deployments",
            namespaceName,
            canaryDeploymentName,
            ct);

        // Delete VirtualService if exists
        await _client.DeleteResourceAsync(
            "networking.istio.io/v1beta1",
            "virtualservices",
            namespaceName,
            $"{deploymentName}-canary-vs",
            ct);

        return new RollingUpdateResult
        {
            Success = true,
            DeploymentName = deploymentName,
            Message = "Canary deployment rolled back"
        };
    }

    /// <summary>
    /// Monitors rollout status of a deployment.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="deploymentName">Name of the deployment.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Rollout status information.</returns>
    public async Task<RolloutStatus> GetRolloutStatusAsync(
        string namespaceName,
        string deploymentName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        var deployment = await _client.GetResourceAsync(
            "apps/v1",
            "deployments",
            namespaceName,
            deploymentName,
            ct);

        if (!deployment.Found)
        {
            return new RolloutStatus
            {
                Phase = RolloutPhase.Unknown,
                Message = "Deployment not found"
            };
        }

        var json = JsonSerializer.Deserialize<JsonElement>(deployment.Json!);
        var status = json.GetProperty("status");

        var replicas = GetIntProperty(status, "replicas", 0);
        var updatedReplicas = GetIntProperty(status, "updatedReplicas", 0);
        var readyReplicas = GetIntProperty(status, "readyReplicas", 0);
        var availableReplicas = GetIntProperty(status, "availableReplicas", 0);
        var unavailableReplicas = GetIntProperty(status, "unavailableReplicas", 0);
        var observedGeneration = GetLongProperty(status, "observedGeneration", 0);

        var metadata = json.GetProperty("metadata");
        var generation = GetLongProperty(metadata, "generation", 0);

        var phase = DetermineRolloutPhase(
            replicas, updatedReplicas, readyReplicas,
            availableReplicas, unavailableReplicas,
            generation, observedGeneration);

        var conditions = ParseConditions(status);

        return new RolloutStatus
        {
            Phase = phase,
            Replicas = replicas,
            UpdatedReplicas = updatedReplicas,
            ReadyReplicas = readyReplicas,
            AvailableReplicas = availableReplicas,
            UnavailableReplicas = unavailableReplicas,
            Generation = generation,
            ObservedGeneration = observedGeneration,
            Conditions = conditions,
            Message = GetRolloutMessage(phase, updatedReplicas, replicas)
        };
    }

    /// <summary>
    /// Pauses a deployment rollout.
    /// </summary>
    public async Task<RollingUpdateResult> PauseRolloutAsync(
        string namespaceName,
        string deploymentName,
        CancellationToken ct = default)
    {
        var patch = new { spec = new { paused = true } };
        var patchJson = JsonSerializer.Serialize(patch, _jsonOptions);

        var result = await _client.PatchResourceAsync(
            "apps/v1",
            "deployments",
            namespaceName,
            deploymentName,
            patchJson,
            ct: ct);

        return new RollingUpdateResult
        {
            Success = result.Success,
            DeploymentName = deploymentName,
            Message = result.Success ? "Rollout paused" : result.Message
        };
    }

    /// <summary>
    /// Resumes a paused deployment rollout.
    /// </summary>
    public async Task<RollingUpdateResult> ResumeRolloutAsync(
        string namespaceName,
        string deploymentName,
        CancellationToken ct = default)
    {
        var patch = new { spec = new { paused = false } };
        var patchJson = JsonSerializer.Serialize(patch, _jsonOptions);

        var result = await _client.PatchResourceAsync(
            "apps/v1",
            "deployments",
            namespaceName,
            deploymentName,
            patchJson,
            ct: ct);

        return new RollingUpdateResult
        {
            Success = result.Success,
            DeploymentName = deploymentName,
            Message = result.Success ? "Rollout resumed" : result.Message
        };
    }

    /// <summary>
    /// Restarts a deployment by triggering a rollout.
    /// </summary>
    public async Task<RollingUpdateResult> RestartDeploymentAsync(
        string namespaceName,
        string deploymentName,
        CancellationToken ct = default)
    {
        var patch = new
        {
            spec = new
            {
                template = new
                {
                    metadata = new
                    {
                        annotations = new Dictionary<string, string>
                        {
                            ["kubectl.kubernetes.io/restartedAt"] = DateTime.UtcNow.ToString("O")
                        }
                    }
                }
            }
        };

        var patchJson = JsonSerializer.Serialize(patch, _jsonOptions);
        var result = await _client.PatchResourceAsync(
            "apps/v1",
            "deployments",
            namespaceName,
            deploymentName,
            patchJson,
            ct: ct);

        return new RollingUpdateResult
        {
            Success = result.Success,
            DeploymentName = deploymentName,
            Message = result.Success ? "Deployment restart initiated" : result.Message
        };
    }

    private object BuildCanaryDeployment(JsonElement source, string canaryName, CanaryDeploymentConfig config)
    {
        return new
        {
            apiVersion = "apps/v1",
            kind = "Deployment",
            metadata = new
            {
                name = canaryName,
                @namespace = source.GetProperty("metadata").GetProperty("namespace").GetString(),
                labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator",
                    ["app.kubernetes.io/version"] = "canary",
                    ["track"] = "canary"
                }
            },
            spec = new
            {
                replicas = config.CanaryReplicas,
                selector = new
                {
                    matchLabels = new Dictionary<string, string>
                    {
                        ["app"] = canaryName,
                        ["track"] = "canary"
                    }
                },
                template = new
                {
                    metadata = new
                    {
                        labels = new Dictionary<string, string>
                        {
                            ["app"] = canaryName,
                            ["track"] = "canary"
                        }
                    },
                    spec = new
                    {
                        containers = new[]
                        {
                            new
                            {
                                name = "main",
                                image = config.NewImage,
                                resources = source.TryGetProperty("spec", out var spec) &&
                                           spec.TryGetProperty("template", out var template) &&
                                           template.TryGetProperty("spec", out var podSpec) &&
                                           podSpec.TryGetProperty("containers", out var containers) &&
                                           containers.GetArrayLength() > 0
                                    ? containers[0].TryGetProperty("resources", out var res) ? res : (object?)null
                                    : null
                            }
                        }
                    }
                }
            }
        };
    }

    private async Task<RollingUpdateResult> CreateIstioVirtualServiceAsync(
        string namespaceName,
        string baseDeploymentName,
        string canaryDeploymentName,
        int canaryTrafficPercentage,
        CancellationToken ct)
    {
        var vs = new
        {
            apiVersion = "networking.istio.io/v1beta1",
            kind = "VirtualService",
            metadata = new
            {
                name = $"{baseDeploymentName}-canary-vs",
                @namespace = namespaceName
            },
            spec = new
            {
                hosts = new[] { baseDeploymentName },
                http = new[]
                {
                    new
                    {
                        route = new[]
                        {
                            new
                            {
                                destination = new { host = baseDeploymentName },
                                weight = 100 - canaryTrafficPercentage
                            },
                            new
                            {
                                destination = new { host = canaryDeploymentName },
                                weight = canaryTrafficPercentage
                            }
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(vs, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "networking.istio.io/v1beta1",
            "virtualservices",
            namespaceName,
            $"{baseDeploymentName}-canary-vs",
            json,
            ct);

        return new RollingUpdateResult
        {
            Success = result.Success,
            DeploymentName = baseDeploymentName,
            Message = result.Message
        };
    }

    private static int GetIntProperty(JsonElement element, string name, int defaultValue)
    {
        return element.TryGetProperty(name, out var prop) && prop.TryGetInt32(out var value)
            ? value
            : defaultValue;
    }

    private static long GetLongProperty(JsonElement element, string name, long defaultValue)
    {
        return element.TryGetProperty(name, out var prop) && prop.TryGetInt64(out var value)
            ? value
            : defaultValue;
    }

    private static RolloutPhase DetermineRolloutPhase(
        int replicas, int updatedReplicas, int readyReplicas,
        int availableReplicas, int unavailableReplicas,
        long generation, long observedGeneration)
    {
        if (generation > observedGeneration)
            return RolloutPhase.Progressing;

        if (updatedReplicas < replicas)
            return RolloutPhase.Progressing;

        if (readyReplicas < updatedReplicas)
            return RolloutPhase.Progressing;

        if (availableReplicas < updatedReplicas)
            return RolloutPhase.Progressing;

        if (unavailableReplicas > 0)
            return RolloutPhase.Degraded;

        return RolloutPhase.Complete;
    }

    private static List<RolloutCondition> ParseConditions(JsonElement status)
    {
        var conditions = new List<RolloutCondition>();

        if (status.TryGetProperty("conditions", out var conds))
        {
            foreach (var cond in conds.EnumerateArray())
            {
                conditions.Add(new RolloutCondition
                {
                    Type = cond.GetProperty("type").GetString() ?? "",
                    Status = cond.GetProperty("status").GetString() ?? "",
                    Reason = cond.TryGetProperty("reason", out var r) ? r.GetString() : null,
                    Message = cond.TryGetProperty("message", out var m) ? m.GetString() : null
                });
            }
        }

        return conditions;
    }

    private static string GetRolloutMessage(RolloutPhase phase, int updatedReplicas, int totalReplicas)
    {
        return phase switch
        {
            RolloutPhase.Complete => "Deployment successfully rolled out",
            RolloutPhase.Progressing => $"Rollout in progress: {updatedReplicas}/{totalReplicas} replicas updated",
            RolloutPhase.Degraded => "Deployment is degraded",
            RolloutPhase.Failed => "Rollout failed",
            _ => "Unknown rollout status"
        };
    }
}

#region Configuration Classes

/// <summary>Configuration for rolling updates.</summary>
public sealed class RollingUpdateConfig
{
    /// <summary>Maximum number of pods that can be created above desired during update.</summary>
    public string MaxSurge { get; set; } = "25%";

    /// <summary>Maximum number of pods that can be unavailable during update.</summary>
    public string MaxUnavailable { get; set; } = "25%";

    /// <summary>Minimum seconds a pod must be ready before considered available.</summary>
    public int MinReadySeconds { get; set; } = 0;

    /// <summary>Maximum time in seconds for deployment to make progress.</summary>
    public int ProgressDeadlineSeconds { get; set; } = 600;

    /// <summary>Number of old ReplicaSets to retain.</summary>
    public int RevisionHistoryLimit { get; set; } = 10;
}

/// <summary>Configuration for PodDisruptionBudget.</summary>
public sealed class PodDisruptionBudgetConfig
{
    /// <summary>Minimum number/percentage of pods that must be available.</summary>
    public string? MinAvailable { get; set; }

    /// <summary>Maximum number/percentage of pods that can be unavailable.</summary>
    public string? MaxUnavailable { get; set; }

    /// <summary>Label selector for pods covered by this PDB.</summary>
    public Dictionary<string, string>? Selector { get; set; }

    /// <summary>Policy for evicting unhealthy pods.</summary>
    public string? UnhealthyPodEvictionPolicy { get; set; }
}

/// <summary>Configuration for canary deployments.</summary>
public sealed class CanaryDeploymentConfig
{
    /// <summary>New container image for canary.</summary>
    public string NewImage { get; set; } = string.Empty;

    /// <summary>Number of canary replicas.</summary>
    public int CanaryReplicas { get; set; } = 1;

    /// <summary>Percentage of traffic to route to canary.</summary>
    public int TrafficPercentage { get; set; } = 10;

    /// <summary>Whether to use Istio for traffic routing.</summary>
    public bool UseIstio { get; set; } = true;

    /// <summary>Analysis template name for automated canary analysis.</summary>
    public string? AnalysisTemplate { get; set; }
}

/// <summary>Result of a rolling update operation.</summary>
public sealed class RollingUpdateResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Name of the deployment.</summary>
    public string DeploymentName { get; set; } = string.Empty;

    /// <summary>Name of the created resource (if applicable).</summary>
    public string? ResourceName { get; set; }

    /// <summary>Result message.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Result of canary deployment operation.</summary>
public sealed class CanaryDeploymentResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Name of the canary deployment.</summary>
    public string? CanaryDeploymentName { get; set; }

    /// <summary>Traffic percentage routed to canary.</summary>
    public int TrafficPercentage { get; set; }

    /// <summary>Result message.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Status of a deployment rollout.</summary>
public sealed class RolloutStatus
{
    /// <summary>Current phase of the rollout.</summary>
    public RolloutPhase Phase { get; set; }

    /// <summary>Total number of replicas.</summary>
    public int Replicas { get; set; }

    /// <summary>Number of updated replicas.</summary>
    public int UpdatedReplicas { get; set; }

    /// <summary>Number of ready replicas.</summary>
    public int ReadyReplicas { get; set; }

    /// <summary>Number of available replicas.</summary>
    public int AvailableReplicas { get; set; }

    /// <summary>Number of unavailable replicas.</summary>
    public int UnavailableReplicas { get; set; }

    /// <summary>Current generation.</summary>
    public long Generation { get; set; }

    /// <summary>Observed generation.</summary>
    public long ObservedGeneration { get; set; }

    /// <summary>Deployment conditions.</summary>
    public List<RolloutCondition> Conditions { get; set; } = new();

    /// <summary>Human-readable status message.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Rollout condition.</summary>
public sealed class RolloutCondition
{
    /// <summary>Condition type.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Condition status (True, False, Unknown).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Reason for the condition.</summary>
    public string? Reason { get; set; }

    /// <summary>Human-readable message.</summary>
    public string? Message { get; set; }
}

/// <summary>Phases of a deployment rollout.</summary>
public enum RolloutPhase
{
    /// <summary>Unknown status.</summary>
    Unknown,
    /// <summary>Rollout is in progress.</summary>
    Progressing,
    /// <summary>Rollout completed successfully.</summary>
    Complete,
    /// <summary>Deployment is degraded.</summary>
    Degraded,
    /// <summary>Rollout failed.</summary>
    Failed
}

#endregion

#region Kubernetes Resource Types

internal sealed class PodDisruptionBudget
{
    public string ApiVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public ObjectMeta Metadata { get; set; } = new();
    public PdbSpec Spec { get; set; } = new();
}

internal sealed class PdbSpec
{
    public string? MinAvailable { get; set; }
    public string? MaxUnavailable { get; set; }
    public LabelSelector Selector { get; set; } = new();
    public string? UnhealthyPodEvictionPolicy { get; set; }
}

#endregion
