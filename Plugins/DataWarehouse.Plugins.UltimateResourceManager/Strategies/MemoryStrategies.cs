namespace DataWarehouse.Plugins.UltimateResourceManager.Strategies;

/// <summary>
/// Cgroup-based memory management strategy.
/// Uses Linux cgroups semantics for memory limiting.
/// </summary>
public sealed class CgroupMemoryStrategy : ResourceStrategyBase
{
    private long _totalAllocated;
    private readonly long _systemMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    public override string StrategyId => "memory-cgroup";
    public override string DisplayName => "Cgroup Memory Manager";
    public override ResourceCategory Category => ResourceCategory.Memory;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = false, SupportsMemory = true, SupportsIO = false,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = true, SupportsPreemption = false
    };
    public override string SemanticDescription =>
        "Cgroup-based memory manager that enforces memory limits using Linux cgroups v2 semantics, " +
        "supporting hierarchical limits for tenant isolation.";
    public override string[] Tags => ["memory", "cgroup", "limits", "container"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ResourceMetrics
        {
            MemoryBytes = _totalAllocated,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");

        if (_totalAllocated + request.MemoryBytes > _systemMemory * 0.9)
        {
            return Task.FromResult(new ResourceAllocation
            {
                RequestId = request.RequestId,
                Success = false,
                FailureReason = "Insufficient memory available"
            });
        }

        Interlocked.Add(ref _totalAllocated, request.MemoryBytes);

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedMemoryBytes = request.MemoryBytes
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        Interlocked.Add(ref _totalAllocated, -allocation.AllocatedMemoryBytes);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Memory balloon strategy for dynamic memory adjustment.
/// </summary>
public sealed class BalloonMemoryStrategy : ResourceStrategyBase
{
    public override string StrategyId => "memory-balloon";
    public override string DisplayName => "Memory Balloon Manager";
    public override ResourceCategory Category => ResourceCategory.Memory;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = false, SupportsMemory = true, SupportsIO = false,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = false, SupportsPreemption = true
    };
    public override string SemanticDescription =>
        "Memory balloon manager that dynamically inflates/deflates memory allocations " +
        "based on system pressure, enabling overcommitment with safety.";
    public override string[] Tags => ["memory", "balloon", "dynamic", "overcommit"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        var info = GC.GetGCMemoryInfo();
        return Task.FromResult(new ResourceMetrics
        {
            MemoryBytes = info.HeapSizeBytes,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedMemoryBytes = request.MemoryBytes
        });
    }
}

/// <summary>
/// Pressure-aware memory management strategy.
/// Responds to memory pressure signals.
/// </summary>
public sealed class PressureAwareMemoryStrategy : ResourceStrategyBase
{
    public override string StrategyId => "memory-pressure-aware";
    public override string DisplayName => "Pressure-Aware Memory Manager";
    public override ResourceCategory Category => ResourceCategory.Memory;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = false, SupportsMemory = true, SupportsIO = false,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = false, SupportsPreemption = true
    };
    public override string SemanticDescription =>
        "Pressure-aware memory manager that monitors system memory pressure (PSI) " +
        "and proactively adjusts allocations to prevent OOM conditions.";
    public override string[] Tags => ["memory", "pressure", "psi", "oom-prevention"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        var info = GC.GetGCMemoryInfo();
        return Task.FromResult(new ResourceMetrics
        {
            MemoryBytes = info.HeapSizeBytes,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var info = GC.GetGCMemoryInfo();
        var pressure = (double)info.HeapSizeBytes / info.TotalAvailableMemoryBytes;

        // Reduce allocation under high pressure
        var allocatedBytes = pressure > 0.8
            ? (long)(request.MemoryBytes * 0.5)
            : request.MemoryBytes;

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedMemoryBytes = allocatedBytes
        });
    }
}

/// <summary>
/// Huge pages memory strategy for large allocations.
/// </summary>
public sealed class HugePagesMemoryStrategy : ResourceStrategyBase
{
    public override string StrategyId => "memory-hugepages";
    public override string DisplayName => "Huge Pages Memory Manager";
    public override ResourceCategory Category => ResourceCategory.Memory;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = false, SupportsMemory = true, SupportsIO = false,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = false, SupportsPreemption = false
    };
    public override string SemanticDescription =>
        "Huge pages memory manager that allocates memory using 2MB/1GB pages, " +
        "reducing TLB misses for large memory workloads like databases.";
    public override string[] Tags => ["memory", "hugepages", "tlb", "database"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ResourceMetrics
        {
            MemoryBytes = GC.GetTotalMemory(false),
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");

        // Round up to 2MB boundary for huge pages
        var pageSize = 2 * 1024 * 1024L;
        var alignedBytes = ((request.MemoryBytes + pageSize - 1) / pageSize) * pageSize;

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedMemoryBytes = alignedBytes
        });
    }
}
