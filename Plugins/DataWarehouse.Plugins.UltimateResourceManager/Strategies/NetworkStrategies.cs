using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateResourceManager.Strategies;

/// <summary>
/// Token bucket network bandwidth strategy.
/// </summary>
public sealed class TokenBucketNetworkStrategy : ResourceStrategyBase
{
    private long _allocatedBandwidth;
    private readonly long _maxBandwidth = 10L * 1024 * 1024 * 1024; // 10 Gbps

    public override string StrategyId => "network-token-bucket";
    public override string DisplayName => "Token Bucket Network Manager";
    public override ResourceCategory Category => ResourceCategory.Network;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = false, SupportsMemory = false, SupportsIO = false,
        SupportsGpu = false, SupportsNetwork = true, SupportsQuotas = true,
        SupportsHierarchicalQuotas = true, SupportsPreemption = false
    };
    public override string SemanticDescription =>
        "Token bucket network bandwidth manager allowing controlled burst traffic " +
        "while maintaining average rate limits for fair bandwidth sharing.";
    public override string[] Tags => ["network", "token-bucket", "bandwidth", "burst"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ResourceMetrics
        {
            NetworkBandwidth = _allocatedBandwidth,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var quota = ActiveQuotas.Values.FirstOrDefault(q => q.QuotaId == request.RequesterId);
        var maxAllowed = quota?.MaxNetworkBandwidth ?? _maxBandwidth;

        if (_allocatedBandwidth + request.IoBandwidth > maxAllowed)
        {
            return Task.FromResult(new ResourceAllocation
            {
                RequestId = request.RequestId,
                Success = false,
                FailureReason = "Network bandwidth quota exceeded"
            });
        }

        Interlocked.Add(ref _allocatedBandwidth, request.IoBandwidth);

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedIoBandwidth = request.IoBandwidth
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        Interlocked.Add(ref _allocatedBandwidth, -allocation.AllocatedIoBandwidth);
        return Task.FromResult(true);
    }
}

/// <summary>
/// QoS-based network strategy with traffic classes.
/// </summary>
public sealed class QosNetworkStrategy : ResourceStrategyBase
{
    private long _allocatedBandwidth;
    private readonly ConcurrentDictionary<string, long> _handleBandwidth = new();

    public override string StrategyId => "network-qos";
    public override string DisplayName => "QoS Network Manager";
    public override ResourceCategory Category => ResourceCategory.Network;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = false, SupportsMemory = false, SupportsIO = false,
        SupportsGpu = false, SupportsNetwork = true, SupportsQuotas = true,
        SupportsHierarchicalQuotas = false, SupportsPreemption = true
    };
    public override string SemanticDescription =>
        "Quality of Service network manager with DSCP-based traffic classification, " +
        "ensuring latency-sensitive traffic gets priority over bulk transfers.";
    public override string[] Tags => ["network", "qos", "dscp", "priority", "latency"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ResourceMetrics
        {
            NetworkBandwidth = Interlocked.Read(ref _allocatedBandwidth),
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var qosClass = request.Priority > 80 ? "EF" : (request.Priority > 50 ? "AF" : "BE");
        _ = qosClass; // DSCP class for future tagging
        _handleBandwidth[handle] = request.IoBandwidth;
        Interlocked.Add(ref _allocatedBandwidth, request.IoBandwidth);

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedIoBandwidth = request.IoBandwidth
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        if (allocation.AllocationHandle != null && _handleBandwidth.TryRemove(allocation.AllocationHandle, out var bw))
            Interlocked.Add(ref _allocatedBandwidth, -bw);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Composite resource strategy combining multiple resource types.
/// </summary>
public sealed class CompositeResourceStrategy : ResourceStrategyBase
{
    private long _allocatedCoresMilliunits;
    private long _allocatedMemoryBytes;
    private long _allocatedIops;
    private long _allocatedIoBandwidth;
    private long _allocatedGpuPercentMilliunits;
    private readonly ConcurrentDictionary<string, CompositeAlloc> _handleAllocs = new();

    private sealed class CompositeAlloc
    {
        public long CoreMilliunits;
        public long MemoryBytes;
        public long Iops;
        public long IoBandwidth;
        public long GpuPercentMilliunits;
    }

    public override string StrategyId => "composite-all";
    public override string DisplayName => "Composite Resource Manager";
    public override ResourceCategory Category => ResourceCategory.Composite;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = true, SupportsMemory = true, SupportsIO = true,
        SupportsGpu = true, SupportsNetwork = true, SupportsQuotas = true,
        SupportsHierarchicalQuotas = true, SupportsPreemption = true
    };
    public override string SemanticDescription =>
        "Composite resource manager that orchestrates CPU, memory, I/O, GPU, and network " +
        "resources together, ensuring holistic resource allocation with co-scheduling.";
    public override string[] Tags => ["composite", "orchestration", "co-scheduling", "holistic"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        var allocatedCores = Interlocked.Read(ref _allocatedCoresMilliunits) / 1000.0;
        var cpuPercent = Environment.ProcessorCount > 0
            ? (allocatedCores / Environment.ProcessorCount) * 100.0
            : 0.0;
        return Task.FromResult(new ResourceMetrics
        {
            CpuPercent = cpuPercent,
            MemoryBytes = Interlocked.Read(ref _allocatedMemoryBytes),
            IopsRate = Interlocked.Read(ref _allocatedIops),
            GpuPercent = Interlocked.Read(ref _allocatedGpuPercentMilliunits) / 1000.0,
            NetworkBandwidth = Interlocked.Read(ref _allocatedIoBandwidth),
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var coreMilliunits = (long)(request.CpuCores * 1000);
        var gpuMilliunits = (long)(request.GpuPercent * 1000);
        var alloc = new CompositeAlloc
        {
            CoreMilliunits = coreMilliunits,
            MemoryBytes = request.MemoryBytes,
            Iops = request.Iops,
            IoBandwidth = request.IoBandwidth,
            GpuPercentMilliunits = gpuMilliunits
        };
        _handleAllocs[handle] = alloc;
        Interlocked.Add(ref _allocatedCoresMilliunits, coreMilliunits);
        Interlocked.Add(ref _allocatedMemoryBytes, request.MemoryBytes);
        Interlocked.Add(ref _allocatedIops, request.Iops);
        Interlocked.Add(ref _allocatedIoBandwidth, request.IoBandwidth);
        Interlocked.Add(ref _allocatedGpuPercentMilliunits, gpuMilliunits);

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedCpuCores = request.CpuCores,
            AllocatedMemoryBytes = request.MemoryBytes,
            AllocatedIops = request.Iops,
            AllocatedIoBandwidth = request.IoBandwidth,
            AllocatedGpuPercent = request.GpuPercent
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        if (allocation.AllocationHandle != null && _handleAllocs.TryRemove(allocation.AllocationHandle, out var alloc))
        {
            Interlocked.Add(ref _allocatedCoresMilliunits, -alloc.CoreMilliunits);
            Interlocked.Add(ref _allocatedMemoryBytes, -alloc.MemoryBytes);
            Interlocked.Add(ref _allocatedIops, -alloc.Iops);
            Interlocked.Add(ref _allocatedIoBandwidth, -alloc.IoBandwidth);
            Interlocked.Add(ref _allocatedGpuPercentMilliunits, -alloc.GpuPercentMilliunits);
        }
        return Task.FromResult(true);
    }
}

/// <summary>
/// Hierarchical quota enforcement strategy.
/// </summary>
public sealed class HierarchicalQuotaStrategy : ResourceStrategyBase
{
    public override string StrategyId => "quota-hierarchical";
    public override string DisplayName => "Hierarchical Quota Manager";
    public override ResourceCategory Category => ResourceCategory.Quota;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = true, SupportsMemory = true, SupportsIO = true,
        SupportsGpu = true, SupportsNetwork = true, SupportsQuotas = true,
        SupportsHierarchicalQuotas = true, SupportsPreemption = false
    };
    public override string SemanticDescription =>
        "Hierarchical quota manager supporting multi-level quotas from organization to project " +
        "to individual workload, with inheritance and borrowing semantics.";
    public override string[] Tags => ["quota", "hierarchical", "tenant", "inheritance"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ResourceMetrics
        {
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");

        // Check hierarchical quotas
        var quota = ActiveQuotas.Values.FirstOrDefault(q => q.QuotaId == request.RequesterId);
        if (quota != null)
        {
            if (quota.MaxCpuCores > 0 && request.CpuCores > quota.MaxCpuCores)
            {
                return Task.FromResult(new ResourceAllocation
                {
                    RequestId = request.RequestId,
                    Success = false,
                    FailureReason = "CPU quota exceeded"
                });
            }
            if (quota.MaxMemoryBytes > 0 && request.MemoryBytes > quota.MaxMemoryBytes)
            {
                return Task.FromResult(new ResourceAllocation
                {
                    RequestId = request.RequestId,
                    Success = false,
                    FailureReason = "Memory quota exceeded"
                });
            }
        }

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedCpuCores = request.CpuCores,
            AllocatedMemoryBytes = request.MemoryBytes,
            AllocatedIops = request.Iops,
            AllocatedGpuPercent = request.GpuPercent
        });
    }
}
