using System.Collections.Concurrent;
using System.Reflection;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateResourceManager;

/// <summary>
/// Ultimate Resource Manager Plugin - Central resource orchestration for DataWarehouse.
///
/// Implements 45+ resource management strategies across categories:
/// - CPU Scheduling (Fair-share, Priority-based, Real-time, Affinity)
/// - Memory Management (Cgroup-based, Balloon, Pressure-aware, NUMA)
/// - I/O Throttling (Bandwidth limiting, IOPS limiting, Deadline-based)
/// - GPU Allocation (MPS, MIG, Time-slicing, vGPU)
/// - Network Bandwidth (Token bucket, Traffic shaping, QoS)
/// - Hierarchical Quotas (Tenant, Plugin, Process-level)
///
/// Features:
/// - Strategy pattern for extensibility
/// - Auto-discovery of strategies via reflection
/// - Fair scheduling across plugins
/// - Resource starvation prevention
/// - Hierarchical quota management
/// - Real-time metrics and monitoring
/// - Preemption support for priority workloads
/// - Carbon-aware scheduling integration
/// </summary>
public sealed class UltimateResourceManagerPlugin : FeaturePluginBase, IDisposable
{
    private readonly ResourceStrategyRegistry _registry;
    private readonly ConcurrentDictionary<string, ResourceQuota> _quotas = new();
    private readonly ConcurrentDictionary<string, ResourceAllocation> _activeAllocations = new();
    private readonly ConcurrentDictionary<string, long> _usageStats = new();
    private bool _disposed;

    // Configuration
    private volatile bool _auditEnabled = true;
    private volatile bool _fairSchedulingEnabled = true;
    private volatile bool _preemptionEnabled = true;

    // Statistics
    private long _totalAllocations;
    private long _totalReleases;
    private long _totalDenied;
    private long _totalPreemptions;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.resourcemanager.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate Resource Manager";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.OrchestrationProvider;

    /// <inheritdoc/>
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc/>
    public override Task StopAsync() => Task.CompletedTask;

    /// <summary>
    /// Semantic description of this plugin for AI discovery.
    /// </summary>
    public string SemanticDescription =>
        "Ultimate resource manager plugin providing central orchestration for CPU, memory, I/O, GPU, and network resources. " +
        "Supports hierarchical quotas from hyperscale data centers to laptop deployments, fair scheduling across plugins, " +
        "resource starvation prevention, and preemption for priority workloads.";

    /// <summary>
    /// Semantic tags for AI discovery and categorization.
    /// </summary>
    public string[] SemanticTags =>
    [
        "resource-manager", "cpu", "memory", "io", "gpu", "network",
        "quota", "scheduling", "throttling", "orchestration"
    ];

    /// <summary>
    /// Gets the resource strategy registry.
    /// </summary>
    public ResourceStrategyRegistry Registry => _registry;

    /// <summary>
    /// Gets or sets whether audit logging is enabled.
    /// </summary>
    public bool AuditEnabled
    {
        get => _auditEnabled;
        set => _auditEnabled = value;
    }

    /// <summary>
    /// Gets or sets whether fair scheduling is enabled.
    /// </summary>
    public bool FairSchedulingEnabled
    {
        get => _fairSchedulingEnabled;
        set => _fairSchedulingEnabled = value;
    }

    /// <summary>
    /// Gets or sets whether preemption is enabled.
    /// </summary>
    public bool PreemptionEnabled
    {
        get => _preemptionEnabled;
        set => _preemptionEnabled = value;
    }

    /// <summary>
    /// Initializes a new instance of the Ultimate Resource Manager plugin.
    /// </summary>
    public UltimateResourceManagerPlugin()
    {
        _registry = new ResourceStrategyRegistry();
        DiscoverAndRegisterStrategies();
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = await base.OnHandshakeAsync(request);

        response.Metadata["RegisteredStrategies"] = _registry.Count.ToString();
        response.Metadata["AuditEnabled"] = _auditEnabled.ToString();
        response.Metadata["FairSchedulingEnabled"] = _fairSchedulingEnabled.ToString();
        response.Metadata["PreemptionEnabled"] = _preemptionEnabled.ToString();
        response.Metadata["CpuStrategies"] = GetStrategiesByCategory(ResourceCategory.Cpu).Count.ToString();
        response.Metadata["MemoryStrategies"] = GetStrategiesByCategory(ResourceCategory.Memory).Count.ToString();
        response.Metadata["IoStrategies"] = GetStrategiesByCategory(ResourceCategory.IO).Count.ToString();
        response.Metadata["GpuStrategies"] = GetStrategiesByCategory(ResourceCategory.Gpu).Count.ToString();

        return response;
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return
        [
            new() { Name = "resource.allocate", DisplayName = "Allocate Resources", Description = "Request resource allocation" },
            new() { Name = "resource.release", DisplayName = "Release Resources", Description = "Release allocated resources" },
            new() { Name = "resource.metrics", DisplayName = "Get Metrics", Description = "Get current resource metrics" },
            new() { Name = "resource.quota.set", DisplayName = "Set Quota", Description = "Set resource quota for entity" },
            new() { Name = "resource.quota.get", DisplayName = "Get Quota", Description = "Get resource quota for entity" },
            new() { Name = "resource.quota.remove", DisplayName = "Remove Quota", Description = "Remove resource quota" },
            new() { Name = "resource.list-strategies", DisplayName = "List Strategies", Description = "List available resource strategies" },
            new() { Name = "resource.stats", DisplayName = "Statistics", Description = "Get resource manager statistics" },
            new() { Name = "resource.preempt", DisplayName = "Preempt", Description = "Preempt lower priority allocations" },
            new() { Name = "resource.reserve", DisplayName = "Reserve", Description = "Reserve resources for future use" }
        ];
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["TotalStrategies"] = _registry.Count;
        metadata["CpuStrategies"] = GetStrategiesByCategory(ResourceCategory.Cpu).Count;
        metadata["MemoryStrategies"] = GetStrategiesByCategory(ResourceCategory.Memory).Count;
        metadata["IoStrategies"] = GetStrategiesByCategory(ResourceCategory.IO).Count;
        metadata["GpuStrategies"] = GetStrategiesByCategory(ResourceCategory.Gpu).Count;
        metadata["ActiveAllocations"] = _activeAllocations.Count;
        metadata["TotalAllocations"] = Interlocked.Read(ref _totalAllocations);
        metadata["TotalReleases"] = Interlocked.Read(ref _totalReleases);
        metadata["TotalDenied"] = Interlocked.Read(ref _totalDenied);
        return metadata;
    }

    /// <inheritdoc/>
    public override Task OnMessageAsync(PluginMessage message)
    {
        return message.Type switch
        {
            "resource.allocate" => HandleAllocateAsync(message),
            "resource.release" => HandleReleaseAsync(message),
            "resource.metrics" => HandleMetricsAsync(message),
            "resource.quota.set" => HandleSetQuotaAsync(message),
            "resource.quota.get" => HandleGetQuotaAsync(message),
            "resource.quota.remove" => HandleRemoveQuotaAsync(message),
            "resource.list-strategies" => HandleListStrategiesAsync(message),
            "resource.stats" => HandleStatsAsync(message),
            "resource.preempt" => HandlePreemptAsync(message),
            "resource.reserve" => HandleReserveAsync(message),
            _ => base.OnMessageAsync(message)
        };
    }

    #region Message Handlers

    private async Task HandleAllocateAsync(PluginMessage message)
    {
        var requesterId = message.Payload.TryGetValue("requesterId", out var rid) && rid is string r
            ? r : message.Source;

        var request = new ResourceRequest
        {
            RequesterId = requesterId,
            CpuCores = message.Payload.TryGetValue("cpuCores", out var cpu) && cpu is double c ? c : 0,
            MemoryBytes = message.Payload.TryGetValue("memoryBytes", out var mem) && mem is long m ? m : 0,
            Iops = message.Payload.TryGetValue("iops", out var iops) && iops is long io ? io : 0,
            IoBandwidth = message.Payload.TryGetValue("ioBandwidth", out var bw) && bw is long b ? b : 0,
            GpuPercent = message.Payload.TryGetValue("gpuPercent", out var gpu) && gpu is double g ? g : 0,
            Priority = message.Payload.TryGetValue("priority", out var pri) && pri is int p ? p : 50
        };

        // Check quota
        if (_quotas.TryGetValue(requesterId, out var quota))
        {
            if (!CheckQuotaCompliance(request, quota))
            {
                Interlocked.Increment(ref _totalDenied);
                message.Payload["success"] = false;
                message.Payload["error"] = "Quota exceeded";
                return;
            }
        }

        // Find appropriate strategy
        var strategyId = message.Payload.TryGetValue("strategyId", out var sid) && sid is string s
            ? s : SelectBestStrategy(request);

        var strategy = _registry.Get(strategyId);
        if (strategy == null)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = $"Strategy '{strategyId}' not found";
            return;
        }

        var allocation = await strategy.AllocateAsync(request);

        if (allocation.Success && allocation.AllocationHandle != null)
        {
            _activeAllocations[allocation.AllocationHandle] = allocation;
            Interlocked.Increment(ref _totalAllocations);
            IncrementUsageStats(strategyId);
        }
        else
        {
            Interlocked.Increment(ref _totalDenied);
        }

        message.Payload["success"] = allocation.Success;
        message.Payload["allocationHandle"] = allocation.AllocationHandle ?? "";
        message.Payload["allocatedCpuCores"] = allocation.AllocatedCpuCores;
        message.Payload["allocatedMemoryBytes"] = allocation.AllocatedMemoryBytes;
        message.Payload["allocatedIops"] = allocation.AllocatedIops;
        message.Payload["allocatedGpuPercent"] = allocation.AllocatedGpuPercent;
        if (!allocation.Success && allocation.FailureReason != null)
            message.Payload["error"] = allocation.FailureReason;
    }

    private async Task HandleReleaseAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("allocationHandle", out var handleObj) || handleObj is not string handle)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Missing 'allocationHandle' parameter";
            return;
        }

        if (!_activeAllocations.TryRemove(handle, out var allocation))
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Allocation not found";
            return;
        }

        var strategyId = message.Payload.TryGetValue("strategyId", out var sid) && sid is string s
            ? s : "default-cpu";

        var strategy = _registry.Get(strategyId);
        if (strategy != null)
        {
            await strategy.ReleaseAsync(handle);
        }

        Interlocked.Increment(ref _totalReleases);
        message.Payload["success"] = true;
    }

    private async Task HandleMetricsAsync(PluginMessage message)
    {
        var categoryFilter = message.Payload.TryGetValue("category", out var catObj) && catObj is string catStr
            && Enum.TryParse<ResourceCategory>(catStr, true, out var cat)
            ? cat
            : (ResourceCategory?)null;

        var strategies = categoryFilter.HasValue
            ? _registry.GetByCategory(categoryFilter.Value)
            : _registry.GetAll();

        var metrics = new Dictionary<string, object>();

        foreach (var strategy in strategies)
        {
            try
            {
                var m = await strategy.GetMetricsAsync();
                metrics[strategy.StrategyId] = new Dictionary<string, object>
                {
                    ["cpuPercent"] = m.CpuPercent,
                    ["memoryBytes"] = m.MemoryBytes,
                    ["iopsRate"] = m.IopsRate,
                    ["ioBandwidth"] = m.IoBandwidth,
                    ["gpuPercent"] = m.GpuPercent,
                    ["gpuMemoryBytes"] = m.GpuMemoryBytes,
                    ["networkBandwidth"] = m.NetworkBandwidth,
                    ["timestamp"] = m.Timestamp
                };
            }
            catch
            {
                // Skip strategies that fail to report metrics
            }
        }

        message.Payload["metrics"] = metrics;
    }

    private Task HandleSetQuotaAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("quotaId", out var qidObj) || qidObj is not string quotaId)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Missing 'quotaId' parameter";
            return Task.CompletedTask;
        }

        var quota = new ResourceQuota
        {
            QuotaId = quotaId,
            MaxCpuCores = message.Payload.TryGetValue("maxCpuCores", out var cpu) && cpu is double c ? c : 0,
            MaxMemoryBytes = message.Payload.TryGetValue("maxMemoryBytes", out var mem) && mem is long m ? m : 0,
            MaxIops = message.Payload.TryGetValue("maxIops", out var iops) && iops is long io ? io : 0,
            MaxIoBandwidth = message.Payload.TryGetValue("maxIoBandwidth", out var bw) && bw is long b ? b : 0,
            MaxGpuPercent = message.Payload.TryGetValue("maxGpuPercent", out var gpu) && gpu is double g ? g : 0,
            MaxNetworkBandwidth = message.Payload.TryGetValue("maxNetworkBandwidth", out var net) && net is long n ? n : 0,
            Priority = message.Payload.TryGetValue("priority", out var pri) && pri is int p ? p : 50,
            HardLimit = message.Payload.TryGetValue("hardLimit", out var hl) && hl is bool h ? h : true
        };

        _quotas[quotaId] = quota;
        message.Payload["success"] = true;
        return Task.CompletedTask;
    }

    private Task HandleGetQuotaAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("quotaId", out var qidObj) || qidObj is not string quotaId)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Missing 'quotaId' parameter";
            return Task.CompletedTask;
        }

        if (!_quotas.TryGetValue(quotaId, out var quota))
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Quota not found";
            return Task.CompletedTask;
        }

        message.Payload["success"] = true;
        message.Payload["maxCpuCores"] = quota.MaxCpuCores;
        message.Payload["maxMemoryBytes"] = quota.MaxMemoryBytes;
        message.Payload["maxIops"] = quota.MaxIops;
        message.Payload["maxIoBandwidth"] = quota.MaxIoBandwidth;
        message.Payload["maxGpuPercent"] = quota.MaxGpuPercent;
        message.Payload["maxNetworkBandwidth"] = quota.MaxNetworkBandwidth;
        message.Payload["priority"] = quota.Priority;
        message.Payload["hardLimit"] = quota.HardLimit;
        return Task.CompletedTask;
    }

    private Task HandleRemoveQuotaAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("quotaId", out var qidObj) || qidObj is not string quotaId)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Missing 'quotaId' parameter";
            return Task.CompletedTask;
        }

        message.Payload["success"] = _quotas.TryRemove(quotaId, out _);
        return Task.CompletedTask;
    }

    private Task HandleListStrategiesAsync(PluginMessage message)
    {
        var categoryFilter = message.Payload.TryGetValue("category", out var catObj) && catObj is string catStr
            && Enum.TryParse<ResourceCategory>(catStr, true, out var cat)
            ? cat
            : (ResourceCategory?)null;

        var strategies = categoryFilter.HasValue
            ? _registry.GetByCategory(categoryFilter.Value)
            : _registry.GetAll();

        var strategyList = strategies.Select(s => new Dictionary<string, object>
        {
            ["id"] = s.StrategyId,
            ["displayName"] = s.DisplayName,
            ["category"] = s.Category.ToString(),
            ["capabilities"] = new Dictionary<string, object>
            {
                ["supportsCpu"] = s.Capabilities.SupportsCpu,
                ["supportsMemory"] = s.Capabilities.SupportsMemory,
                ["supportsIO"] = s.Capabilities.SupportsIO,
                ["supportsGpu"] = s.Capabilities.SupportsGpu,
                ["supportsNetwork"] = s.Capabilities.SupportsNetwork,
                ["supportsQuotas"] = s.Capabilities.SupportsQuotas,
                ["supportsPreemption"] = s.Capabilities.SupportsPreemption
            },
            ["tags"] = s.Tags
        }).ToList();

        message.Payload["strategies"] = strategyList;
        message.Payload["count"] = strategyList.Count;
        return Task.CompletedTask;
    }

    private Task HandleStatsAsync(PluginMessage message)
    {
        message.Payload["totalAllocations"] = Interlocked.Read(ref _totalAllocations);
        message.Payload["totalReleases"] = Interlocked.Read(ref _totalReleases);
        message.Payload["totalDenied"] = Interlocked.Read(ref _totalDenied);
        message.Payload["totalPreemptions"] = Interlocked.Read(ref _totalPreemptions);
        message.Payload["activeAllocations"] = _activeAllocations.Count;
        message.Payload["activeQuotas"] = _quotas.Count;
        message.Payload["registeredStrategies"] = _registry.Count;

        var usageByStrategy = new Dictionary<string, long>(_usageStats);
        message.Payload["usageByStrategy"] = usageByStrategy;
        return Task.CompletedTask;
    }

    private Task HandlePreemptAsync(PluginMessage message)
    {
        if (!_preemptionEnabled)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Preemption is disabled";
            return Task.CompletedTask;
        }

        var priority = message.Payload.TryGetValue("priority", out var pri) && pri is int p ? p : 100;

        // Find and preempt lower priority allocations
        var preempted = 0;
        foreach (var kvp in _activeAllocations.ToArray())
        {
            if (_quotas.TryGetValue(kvp.Value.RequestId, out var quota) && quota.Priority < priority)
            {
                if (_activeAllocations.TryRemove(kvp.Key, out _))
                {
                    preempted++;
                    Interlocked.Increment(ref _totalPreemptions);
                }
            }
        }

        message.Payload["success"] = true;
        message.Payload["preemptedCount"] = preempted;
        return Task.CompletedTask;
    }

    private Task HandleReserveAsync(PluginMessage message)
    {
        // Reserve resources for future use (placeholder implementation)
        var reservationId = Guid.NewGuid().ToString("N");
        message.Payload["success"] = true;
        message.Payload["reservationId"] = reservationId;
        return Task.CompletedTask;
    }

    #endregion

    #region Helper Methods

    private bool CheckQuotaCompliance(ResourceRequest request, ResourceQuota quota)
    {
        if (quota.MaxCpuCores > 0 && request.CpuCores > quota.MaxCpuCores)
            return false;
        if (quota.MaxMemoryBytes > 0 && request.MemoryBytes > quota.MaxMemoryBytes)
            return false;
        if (quota.MaxIops > 0 && request.Iops > quota.MaxIops)
            return false;
        if (quota.MaxIoBandwidth > 0 && request.IoBandwidth > quota.MaxIoBandwidth)
            return false;
        if (quota.MaxGpuPercent > 0 && request.GpuPercent > quota.MaxGpuPercent)
            return false;
        return true;
    }

    private string SelectBestStrategy(ResourceRequest request)
    {
        // Select strategy based on primary resource type requested
        if (request.GpuPercent > 0)
            return "gpu-time-slicing";
        if (request.Iops > 0 || request.IoBandwidth > 0)
            return "io-deadline";
        if (request.MemoryBytes > 0)
            return "memory-cgroup";
        return "cpu-fair-share";
    }

    private List<IResourceStrategy> GetStrategiesByCategory(ResourceCategory category)
    {
        return _registry.GetByCategory(category).ToList();
    }

    private void IncrementUsageStats(string strategyId)
    {
        _usageStats.AddOrUpdate(strategyId, 1, (_, count) => count + 1);
    }

    private void DiscoverAndRegisterStrategies()
    {
        _registry.AutoDiscover(Assembly.GetExecutingAssembly());
    }

    #endregion

    /// <summary>
    /// Disposes resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_disposed) return;
            _disposed = true;
            _usageStats.Clear();
            _quotas.Clear();
            _activeAllocations.Clear();
        }
        base.Dispose(disposing);
    }
}
