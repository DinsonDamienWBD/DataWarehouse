using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.K8sOperator.Managers;

/// <summary>
/// Manages automatic scaling for DataWarehouse Kubernetes resources.
/// Provides HorizontalPodAutoscaler, VerticalPodAutoscaler creation,
/// custom metrics support, scale-to-zero capability, and KEDA integration readiness.
/// </summary>
public sealed class AutoScalingManager
{
    private readonly IKubernetesClient _client;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Initializes a new instance of the AutoScalingManager.
    /// </summary>
    /// <param name="client">Kubernetes client for API interactions.</param>
    public AutoScalingManager(IKubernetesClient client)
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
    /// Creates a HorizontalPodAutoscaler for the specified deployment.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="deploymentName">Name of the deployment to scale.</param>
    /// <param name="config">HPA configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the HPA creation.</returns>
    public async Task<AutoScalingResult> CreateHorizontalPodAutoscalerAsync(
        string namespaceName,
        string deploymentName,
        HpaConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);
        ArgumentNullException.ThrowIfNull(config);

        var hpa = new HorizontalPodAutoscaler
        {
            ApiVersion = "autoscaling/v2",
            Kind = "HorizontalPodAutoscaler",
            Metadata = new ObjectMeta
            {
                Name = $"{deploymentName}-hpa",
                Namespace = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator",
                    ["app.kubernetes.io/name"] = deploymentName
                }
            },
            Spec = new HpaSpec
            {
                ScaleTargetRef = new CrossVersionObjectReference
                {
                    ApiVersion = "apps/v1",
                    Kind = "Deployment",
                    Name = deploymentName
                },
                MinReplicas = config.MinReplicas,
                MaxReplicas = config.MaxReplicas,
                Metrics = BuildMetrics(config),
                Behavior = BuildScalingBehavior(config)
            }
        };

        var json = JsonSerializer.Serialize(hpa, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "autoscaling/v2",
            "horizontalpodautoscalers",
            namespaceName,
            $"{deploymentName}-hpa",
            json,
            ct);

        return new AutoScalingResult
        {
            Success = result.Success,
            ResourceName = $"{deploymentName}-hpa",
            ResourceKind = "HorizontalPodAutoscaler",
            Message = result.Message
        };
    }

    /// <summary>
    /// Creates a VerticalPodAutoscaler for the specified deployment.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="deploymentName">Name of the deployment to scale.</param>
    /// <param name="config">VPA configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the VPA creation.</returns>
    public async Task<AutoScalingResult> CreateVerticalPodAutoscalerAsync(
        string namespaceName,
        string deploymentName,
        VpaConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);
        ArgumentNullException.ThrowIfNull(config);

        var vpa = new VerticalPodAutoscaler
        {
            ApiVersion = "autoscaling.k8s.io/v1",
            Kind = "VerticalPodAutoscaler",
            Metadata = new ObjectMeta
            {
                Name = $"{deploymentName}-vpa",
                Namespace = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator",
                    ["app.kubernetes.io/name"] = deploymentName
                }
            },
            Spec = new VpaSpec
            {
                TargetRef = new CrossVersionObjectReference
                {
                    ApiVersion = "apps/v1",
                    Kind = "Deployment",
                    Name = deploymentName
                },
                UpdatePolicy = new VpaUpdatePolicy
                {
                    UpdateMode = config.UpdateMode.ToString()
                },
                ResourcePolicy = BuildResourcePolicy(config)
            }
        };

        var json = JsonSerializer.Serialize(vpa, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "autoscaling.k8s.io/v1",
            "verticalpodautoscalers",
            namespaceName,
            $"{deploymentName}-vpa",
            json,
            ct);

        return new AutoScalingResult
        {
            Success = result.Success,
            ResourceName = $"{deploymentName}-vpa",
            ResourceKind = "VerticalPodAutoscaler",
            Message = result.Message
        };
    }

    /// <summary>
    /// Creates a KEDA ScaledObject for event-driven autoscaling.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="deploymentName">Name of the deployment to scale.</param>
    /// <param name="config">KEDA configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the ScaledObject creation.</returns>
    public async Task<AutoScalingResult> CreateKedaScaledObjectAsync(
        string namespaceName,
        string deploymentName,
        KedaConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);
        ArgumentNullException.ThrowIfNull(config);

        var scaledObject = new KedaScaledObject
        {
            ApiVersion = "keda.sh/v1alpha1",
            Kind = "ScaledObject",
            Metadata = new ObjectMeta
            {
                Name = $"{deploymentName}-scaled",
                Namespace = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator",
                    ["app.kubernetes.io/name"] = deploymentName
                }
            },
            Spec = new KedaScaledObjectSpec
            {
                ScaleTargetRef = new ScaleTargetRef
                {
                    Name = deploymentName,
                    Kind = "Deployment"
                },
                MinReplicaCount = config.MinReplicas,
                MaxReplicaCount = config.MaxReplicas,
                PollingInterval = config.PollingIntervalSeconds,
                CooldownPeriod = config.CooldownPeriodSeconds,
                IdleReplicaCount = config.EnableScaleToZero ? 0 : null,
                Triggers = config.Triggers.Select(t => new KedaTrigger
                {
                    Type = t.Type,
                    Metadata = t.Metadata,
                    AuthenticationRef = t.AuthenticationRef != null
                        ? new TriggerAuthenticationRef { Name = t.AuthenticationRef }
                        : null
                }).ToList()
            }
        };

        var json = JsonSerializer.Serialize(scaledObject, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "keda.sh/v1alpha1",
            "scaledobjects",
            namespaceName,
            $"{deploymentName}-scaled",
            json,
            ct);

        return new AutoScalingResult
        {
            Success = result.Success,
            ResourceName = $"{deploymentName}-scaled",
            ResourceKind = "ScaledObject",
            Message = result.Message
        };
    }

    /// <summary>
    /// Configures scale-to-zero capability for serverless workloads.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="deploymentName">Name of the deployment.</param>
    /// <param name="config">Scale-to-zero configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the configuration.</returns>
    public async Task<AutoScalingResult> ConfigureScaleToZeroAsync(
        string namespaceName,
        string deploymentName,
        ScaleToZeroConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);
        ArgumentNullException.ThrowIfNull(config);

        // Create KEDA ScaledObject with idleReplicaCount: 0
        var kedaConfig = new KedaConfig
        {
            MinReplicas = 0,
            MaxReplicas = config.MaxReplicas,
            PollingIntervalSeconds = config.PollingIntervalSeconds,
            CooldownPeriodSeconds = config.ScaleDownDelaySeconds,
            EnableScaleToZero = true,
            Triggers = new List<KedaTriggerConfig>
            {
                new()
                {
                    Type = config.TriggerType,
                    Metadata = config.TriggerMetadata
                }
            }
        };

        return await CreateKedaScaledObjectAsync(namespaceName, deploymentName, kedaConfig, ct);
    }

    /// <summary>
    /// Deletes autoscaling resources for a deployment.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="deploymentName">Name of the deployment.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the deletion.</returns>
    public async Task<AutoScalingResult> DeleteAutoscalingResourcesAsync(
        string namespaceName,
        string deploymentName,
        CancellationToken ct = default)
    {
        var errors = new List<string>();

        // Delete HPA
        var hpaResult = await _client.DeleteResourceAsync(
            "autoscaling/v2",
            "horizontalpodautoscalers",
            namespaceName,
            $"{deploymentName}-hpa",
            ct);
        if (!hpaResult.Success && !hpaResult.NotFound)
            errors.Add($"HPA: {hpaResult.Message}");

        // Delete VPA
        var vpaResult = await _client.DeleteResourceAsync(
            "autoscaling.k8s.io/v1",
            "verticalpodautoscalers",
            namespaceName,
            $"{deploymentName}-vpa",
            ct);
        if (!vpaResult.Success && !vpaResult.NotFound)
            errors.Add($"VPA: {vpaResult.Message}");

        // Delete KEDA ScaledObject
        var kedaResult = await _client.DeleteResourceAsync(
            "keda.sh/v1alpha1",
            "scaledobjects",
            namespaceName,
            $"{deploymentName}-scaled",
            ct);
        if (!kedaResult.Success && !kedaResult.NotFound)
            errors.Add($"ScaledObject: {kedaResult.Message}");

        return new AutoScalingResult
        {
            Success = errors.Count == 0,
            ResourceName = deploymentName,
            ResourceKind = "AutoscalingResources",
            Message = errors.Count > 0 ? string.Join("; ", errors) : "All autoscaling resources deleted"
        };
    }

    private List<MetricSpec> BuildMetrics(HpaConfig config)
    {
        var metrics = new List<MetricSpec>();

        if (config.TargetCpuUtilization > 0)
        {
            metrics.Add(new MetricSpec
            {
                Type = "Resource",
                Resource = new ResourceMetricSource
                {
                    Name = "cpu",
                    Target = new MetricTarget
                    {
                        Type = "Utilization",
                        AverageUtilization = config.TargetCpuUtilization
                    }
                }
            });
        }

        if (config.TargetMemoryUtilization > 0)
        {
            metrics.Add(new MetricSpec
            {
                Type = "Resource",
                Resource = new ResourceMetricSource
                {
                    Name = "memory",
                    Target = new MetricTarget
                    {
                        Type = "Utilization",
                        AverageUtilization = config.TargetMemoryUtilization
                    }
                }
            });
        }

        foreach (var custom in config.CustomMetrics)
        {
            metrics.Add(new MetricSpec
            {
                Type = custom.Type,
                Pods = custom.Type == "Pods" ? new PodsMetricSource
                {
                    Metric = new MetricIdentifier { Name = custom.MetricName },
                    Target = new MetricTarget
                    {
                        Type = "AverageValue",
                        AverageValue = custom.TargetValue
                    }
                } : null,
                External = custom.Type == "External" ? new ExternalMetricSource
                {
                    Metric = new MetricIdentifier
                    {
                        Name = custom.MetricName,
                        Selector = custom.Selector != null ? new LabelSelector
                        {
                            MatchLabels = custom.Selector
                        } : null
                    },
                    Target = new MetricTarget
                    {
                        Type = custom.TargetType,
                        Value = custom.TargetType == "Value" ? custom.TargetValue : null,
                        AverageValue = custom.TargetType == "AverageValue" ? custom.TargetValue : null
                    }
                } : null
            });
        }

        return metrics;
    }

    private HpaScalingBehavior? BuildScalingBehavior(HpaConfig config)
    {
        if (!config.EnableAdvancedScaling)
            return null;

        return new HpaScalingBehavior
        {
            ScaleUp = new HpaScalingRules
            {
                StabilizationWindowSeconds = config.ScaleUpStabilizationWindowSeconds,
                SelectPolicy = "Max",
                Policies = new List<HpaScalingPolicy>
                {
                    new()
                    {
                        Type = "Percent",
                        Value = config.ScaleUpPercentPerPeriod,
                        PeriodSeconds = 60
                    },
                    new()
                    {
                        Type = "Pods",
                        Value = config.ScaleUpPodsPerPeriod,
                        PeriodSeconds = 60
                    }
                }
            },
            ScaleDown = new HpaScalingRules
            {
                StabilizationWindowSeconds = config.ScaleDownStabilizationWindowSeconds,
                SelectPolicy = "Min",
                Policies = new List<HpaScalingPolicy>
                {
                    new()
                    {
                        Type = "Percent",
                        Value = config.ScaleDownPercentPerPeriod,
                        PeriodSeconds = 60
                    }
                }
            }
        };
    }

    private VpaResourcePolicy? BuildResourcePolicy(VpaConfig config)
    {
        if (config.ContainerPolicies.Count == 0)
            return null;

        return new VpaResourcePolicy
        {
            ContainerPolicies = config.ContainerPolicies.Select(cp => new VpaContainerResourcePolicy
            {
                ContainerName = cp.ContainerName,
                Mode = cp.Mode.ToString(),
                MinAllowed = cp.MinCpu != null || cp.MinMemory != null ? new ResourceList
                {
                    Cpu = cp.MinCpu,
                    Memory = cp.MinMemory
                } : null,
                MaxAllowed = cp.MaxCpu != null || cp.MaxMemory != null ? new ResourceList
                {
                    Cpu = cp.MaxCpu,
                    Memory = cp.MaxMemory
                } : null,
                ControlledResources = cp.ControlledResources
            }).ToList()
        };
    }
}

#region Configuration Classes

/// <summary>Configuration for HorizontalPodAutoscaler.</summary>
public sealed class HpaConfig
{
    /// <summary>Minimum number of replicas.</summary>
    public int MinReplicas { get; set; } = 1;

    /// <summary>Maximum number of replicas.</summary>
    public int MaxReplicas { get; set; } = 10;

    /// <summary>Target CPU utilization percentage.</summary>
    public int TargetCpuUtilization { get; set; } = 80;

    /// <summary>Target memory utilization percentage.</summary>
    public int TargetMemoryUtilization { get; set; }

    /// <summary>Custom metrics for scaling.</summary>
    public List<CustomMetricConfig> CustomMetrics { get; set; } = new();

    /// <summary>Enable advanced scaling behavior configuration.</summary>
    public bool EnableAdvancedScaling { get; set; }

    /// <summary>Scale up stabilization window in seconds.</summary>
    public int ScaleUpStabilizationWindowSeconds { get; set; } = 0;

    /// <summary>Scale down stabilization window in seconds.</summary>
    public int ScaleDownStabilizationWindowSeconds { get; set; } = 300;

    /// <summary>Maximum percentage increase per period during scale up.</summary>
    public int ScaleUpPercentPerPeriod { get; set; } = 100;

    /// <summary>Maximum pods to add per period during scale up.</summary>
    public int ScaleUpPodsPerPeriod { get; set; } = 4;

    /// <summary>Maximum percentage decrease per period during scale down.</summary>
    public int ScaleDownPercentPerPeriod { get; set; } = 10;
}

/// <summary>Configuration for custom metrics.</summary>
public sealed class CustomMetricConfig
{
    /// <summary>Type of metric (Pods, External, Object).</summary>
    public string Type { get; set; } = "Pods";

    /// <summary>Name of the metric.</summary>
    public string MetricName { get; set; } = string.Empty;

    /// <summary>Target value for the metric.</summary>
    public string TargetValue { get; set; } = string.Empty;

    /// <summary>Target type (Value, AverageValue, Utilization).</summary>
    public string TargetType { get; set; } = "AverageValue";

    /// <summary>Label selector for external metrics.</summary>
    public Dictionary<string, string>? Selector { get; set; }
}

/// <summary>Configuration for VerticalPodAutoscaler.</summary>
public sealed class VpaConfig
{
    /// <summary>Update mode for VPA.</summary>
    public VpaUpdateMode UpdateMode { get; set; } = VpaUpdateMode.Auto;

    /// <summary>Container-specific policies.</summary>
    public List<ContainerPolicyConfig> ContainerPolicies { get; set; } = new();
}

/// <summary>VPA update modes.</summary>
public enum VpaUpdateMode
{
    /// <summary>Off - recommendations only.</summary>
    Off,
    /// <summary>Initial - set resources only at pod creation.</summary>
    Initial,
    /// <summary>Recreate - restart pods to apply recommendations.</summary>
    Recreate,
    /// <summary>Auto - VPA decides the update strategy.</summary>
    Auto
}

/// <summary>Container-specific resource policy.</summary>
public sealed class ContainerPolicyConfig
{
    /// <summary>Container name (* for all).</summary>
    public string ContainerName { get; set; } = "*";

    /// <summary>Mode for this container.</summary>
    public VpaUpdateMode Mode { get; set; } = VpaUpdateMode.Auto;

    /// <summary>Minimum CPU allowed.</summary>
    public string? MinCpu { get; set; }

    /// <summary>Minimum memory allowed.</summary>
    public string? MinMemory { get; set; }

    /// <summary>Maximum CPU allowed.</summary>
    public string? MaxCpu { get; set; }

    /// <summary>Maximum memory allowed.</summary>
    public string? MaxMemory { get; set; }

    /// <summary>Resources to control (cpu, memory).</summary>
    public List<string>? ControlledResources { get; set; }
}

/// <summary>Configuration for KEDA autoscaling.</summary>
public sealed class KedaConfig
{
    /// <summary>Minimum number of replicas.</summary>
    public int MinReplicas { get; set; } = 1;

    /// <summary>Maximum number of replicas.</summary>
    public int MaxReplicas { get; set; } = 10;

    /// <summary>Polling interval in seconds.</summary>
    public int PollingIntervalSeconds { get; set; } = 30;

    /// <summary>Cooldown period in seconds.</summary>
    public int CooldownPeriodSeconds { get; set; } = 300;

    /// <summary>Enable scale to zero.</summary>
    public bool EnableScaleToZero { get; set; }

    /// <summary>KEDA triggers configuration.</summary>
    public List<KedaTriggerConfig> Triggers { get; set; } = new();
}

/// <summary>KEDA trigger configuration.</summary>
public sealed class KedaTriggerConfig
{
    /// <summary>Trigger type (prometheus, kafka, rabbitmq, etc.).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Trigger-specific metadata.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>Reference to TriggerAuthentication resource.</summary>
    public string? AuthenticationRef { get; set; }
}

/// <summary>Configuration for scale-to-zero.</summary>
public sealed class ScaleToZeroConfig
{
    /// <summary>Maximum number of replicas when scaled up.</summary>
    public int MaxReplicas { get; set; } = 10;

    /// <summary>Polling interval in seconds.</summary>
    public int PollingIntervalSeconds { get; set; } = 30;

    /// <summary>Delay before scaling down to zero in seconds.</summary>
    public int ScaleDownDelaySeconds { get; set; } = 300;

    /// <summary>Trigger type for scale-to-zero.</summary>
    public string TriggerType { get; set; } = "prometheus";

    /// <summary>Trigger metadata.</summary>
    public Dictionary<string, string> TriggerMetadata { get; set; } = new();
}

/// <summary>Result of an autoscaling operation.</summary>
public sealed class AutoScalingResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Name of the resource.</summary>
    public string ResourceName { get; set; } = string.Empty;

    /// <summary>Kind of the resource.</summary>
    public string ResourceKind { get; set; } = string.Empty;

    /// <summary>Result message.</summary>
    public string Message { get; set; } = string.Empty;
}

#endregion

#region Kubernetes Resource Types

internal sealed class HorizontalPodAutoscaler
{
    public string ApiVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public ObjectMeta Metadata { get; set; } = new();
    public HpaSpec Spec { get; set; } = new();
}

internal sealed class HpaSpec
{
    public CrossVersionObjectReference ScaleTargetRef { get; set; } = new();
    public int MinReplicas { get; set; }
    public int MaxReplicas { get; set; }
    public List<MetricSpec> Metrics { get; set; } = new();
    public HpaScalingBehavior? Behavior { get; set; }
}

internal sealed class MetricSpec
{
    public string Type { get; set; } = string.Empty;
    public ResourceMetricSource? Resource { get; set; }
    public PodsMetricSource? Pods { get; set; }
    public ExternalMetricSource? External { get; set; }
}

internal sealed class ResourceMetricSource
{
    public string Name { get; set; } = string.Empty;
    public MetricTarget Target { get; set; } = new();
}

internal sealed class PodsMetricSource
{
    public MetricIdentifier Metric { get; set; } = new();
    public MetricTarget Target { get; set; } = new();
}

internal sealed class ExternalMetricSource
{
    public MetricIdentifier Metric { get; set; } = new();
    public MetricTarget Target { get; set; } = new();
}

internal sealed class MetricIdentifier
{
    public string Name { get; set; } = string.Empty;
    public LabelSelector? Selector { get; set; }
}

internal sealed class MetricTarget
{
    public string Type { get; set; } = string.Empty;
    public int? AverageUtilization { get; set; }
    public string? Value { get; set; }
    public string? AverageValue { get; set; }
}

internal sealed class HpaScalingBehavior
{
    public HpaScalingRules? ScaleUp { get; set; }
    public HpaScalingRules? ScaleDown { get; set; }
}

internal sealed class HpaScalingRules
{
    public int? StabilizationWindowSeconds { get; set; }
    public string? SelectPolicy { get; set; }
    public List<HpaScalingPolicy>? Policies { get; set; }
}

internal sealed class HpaScalingPolicy
{
    public string Type { get; set; } = string.Empty;
    public int Value { get; set; }
    public int PeriodSeconds { get; set; }
}

internal sealed class VerticalPodAutoscaler
{
    public string ApiVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public ObjectMeta Metadata { get; set; } = new();
    public VpaSpec Spec { get; set; } = new();
}

internal sealed class VpaSpec
{
    public CrossVersionObjectReference TargetRef { get; set; } = new();
    public VpaUpdatePolicy UpdatePolicy { get; set; } = new();
    public VpaResourcePolicy? ResourcePolicy { get; set; }
}

internal sealed class VpaUpdatePolicy
{
    public string UpdateMode { get; set; } = "Auto";
}

internal sealed class VpaResourcePolicy
{
    public List<VpaContainerResourcePolicy> ContainerPolicies { get; set; } = new();
}

internal sealed class VpaContainerResourcePolicy
{
    public string ContainerName { get; set; } = "*";
    public string? Mode { get; set; }
    public ResourceList? MinAllowed { get; set; }
    public ResourceList? MaxAllowed { get; set; }
    public List<string>? ControlledResources { get; set; }
}

internal sealed class ResourceList
{
    public string? Cpu { get; set; }
    public string? Memory { get; set; }
}

internal sealed class KedaScaledObject
{
    public string ApiVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public ObjectMeta Metadata { get; set; } = new();
    public KedaScaledObjectSpec Spec { get; set; } = new();
}

internal sealed class KedaScaledObjectSpec
{
    public ScaleTargetRef ScaleTargetRef { get; set; } = new();
    public int? MinReplicaCount { get; set; }
    public int? MaxReplicaCount { get; set; }
    public int? PollingInterval { get; set; }
    public int? CooldownPeriod { get; set; }
    public int? IdleReplicaCount { get; set; }
    public List<KedaTrigger> Triggers { get; set; } = new();
}

internal sealed class ScaleTargetRef
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "Deployment";
}

internal sealed class KedaTrigger
{
    public string Type { get; set; } = string.Empty;
    public Dictionary<string, string> Metadata { get; set; } = new();
    public TriggerAuthenticationRef? AuthenticationRef { get; set; }
}

internal sealed class TriggerAuthenticationRef
{
    public string Name { get; set; } = string.Empty;
}

internal sealed class ObjectMeta
{
    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public Dictionary<string, string>? Labels { get; set; }
    public Dictionary<string, string>? Annotations { get; set; }
}

internal sealed class CrossVersionObjectReference
{
    public string ApiVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

internal sealed class LabelSelector
{
    public Dictionary<string, string>? MatchLabels { get; set; }
}

#endregion

#region Kubernetes Client Interface

/// <summary>
/// Interface for Kubernetes API client operations.
/// </summary>
public interface IKubernetesClient
{
    /// <summary>Applies a resource to the cluster.</summary>
    Task<KubernetesOperationResult> ApplyResourceAsync(
        string apiVersion,
        string resourceType,
        string? namespaceName,
        string name,
        string json,
        CancellationToken ct = default);

    /// <summary>Deletes a resource from the cluster.</summary>
    Task<KubernetesOperationResult> DeleteResourceAsync(
        string apiVersion,
        string resourceType,
        string? namespaceName,
        string name,
        CancellationToken ct = default);

    /// <summary>Gets a resource from the cluster.</summary>
    Task<KubernetesResourceResult> GetResourceAsync(
        string apiVersion,
        string resourceType,
        string? namespaceName,
        string name,
        CancellationToken ct = default);

    /// <summary>Lists resources in the cluster.</summary>
    Task<KubernetesListResult> ListResourcesAsync(
        string apiVersion,
        string resourceType,
        string? namespaceName,
        string? labelSelector = null,
        CancellationToken ct = default);

    /// <summary>Patches a resource in the cluster.</summary>
    Task<KubernetesOperationResult> PatchResourceAsync(
        string apiVersion,
        string resourceType,
        string? namespaceName,
        string name,
        string patchJson,
        string patchType = "application/strategic-merge-patch+json",
        CancellationToken ct = default);
}

/// <summary>Result of a Kubernetes operation.</summary>
public sealed class KubernetesOperationResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Whether the resource was not found (for delete operations).</summary>
    public bool NotFound { get; set; }

    /// <summary>Result message.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Result of getting a Kubernetes resource.</summary>
public sealed class KubernetesResourceResult
{
    /// <summary>Whether the resource exists.</summary>
    public bool Found { get; set; }

    /// <summary>JSON representation of the resource.</summary>
    public string? Json { get; set; }

    /// <summary>Error message if any.</summary>
    public string? Error { get; set; }
}

/// <summary>Result of listing Kubernetes resources.</summary>
public sealed class KubernetesListResult
{
    /// <summary>Whether the list operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>List of resource JSON strings.</summary>
    public List<string> Items { get; set; } = new();

    /// <summary>Error message if any.</summary>
    public string? Error { get; set; }
}

#endregion
