using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateResourceManager.Strategies;

/// <summary>
/// GPU time-slicing strategy.
/// Shares GPU by time-multiplexing.
/// </summary>
public sealed class TimeSlicingGpuStrategy : ResourceStrategyBase
{
    private long _allocatedPercentMilliunits; // Stored as milliunits for Interlocked (1% = 1000)
    private readonly ConcurrentDictionary<string, long> _handlePercents = new();

    public override string StrategyId => "gpu-time-slicing";
    public override string DisplayName => "GPU Time-Slicing Manager";
    public override ResourceCategory Category => ResourceCategory.Gpu;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = false, SupportsMemory = false, SupportsIO = false,
        SupportsGpu = true, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = false, SupportsPreemption = true
    };
    public override string SemanticDescription =>
        "GPU time-slicing manager that shares GPU resources by time-multiplexing, " +
        "allowing multiple workloads to share a single GPU with context switching.";
    public override string[] Tags => ["gpu", "time-slicing", "sharing", "context-switch"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ResourceMetrics
        {
            GpuPercent = Interlocked.Read(ref _allocatedPercentMilliunits) / 1000.0,
            GpuMemoryBytes = 0,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var requestMilliunits = (long)(request.GpuPercent * 1000);
        const long maxMilliunits = 100_000; // 100%

        // Atomic compare-and-add loop to avoid TOCTOU race
        long current;
        long updated;
        do
        {
            current = Interlocked.Read(ref _allocatedPercentMilliunits);
            if (current + requestMilliunits > maxMilliunits)
            {
                return Task.FromResult(new ResourceAllocation
                {
                    RequestId = request.RequestId,
                    Success = false,
                    FailureReason = "GPU capacity exceeded"
                });
            }
            updated = current + requestMilliunits;
        }
        while (Interlocked.CompareExchange(ref _allocatedPercentMilliunits, updated, current) != current);

        _handlePercents[handle] = requestMilliunits;

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedGpuPercent = request.GpuPercent
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        if (allocation.AllocationHandle != null &&
            _handlePercents.TryRemove(allocation.AllocationHandle, out var milliunits))
        {
            Interlocked.Add(ref _allocatedPercentMilliunits, -milliunits);
            // Clamp to 0 in case of rounding
            if (Interlocked.Read(ref _allocatedPercentMilliunits) < 0)
                Interlocked.Exchange(ref _allocatedPercentMilliunits, 0);
        }
        return Task.FromResult(true);
    }
}

/// <summary>
/// Multi-Instance GPU (MIG) strategy.
/// Partitions GPU into isolated instances.
/// </summary>
public sealed class MigGpuStrategy : ResourceStrategyBase
{
    private readonly int[] _migSlots = new int[7]; // MIG can create up to 7 instances

    public override string StrategyId => "gpu-mig";
    public override string DisplayName => "Multi-Instance GPU (MIG) Manager";
    public override ResourceCategory Category => ResourceCategory.Gpu;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = false, SupportsMemory = false, SupportsIO = false,
        SupportsGpu = true, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = false, SupportsPreemption = false
    };
    public override string SemanticDescription =>
        "Multi-Instance GPU (MIG) manager that partitions NVIDIA GPUs into isolated instances, " +
        "providing hardware-level isolation for multi-tenant workloads.";
    public override string[] Tags => ["gpu", "mig", "partition", "isolation", "nvidia"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        var usedSlots = _migSlots.Count(s => s != 0);
        return Task.FromResult(new ResourceMetrics
        {
            GpuPercent = (usedSlots / 7.0) * 100,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var slotsNeeded = (int)Math.Ceiling(request.GpuPercent / (100.0 / 7));

        var allocatedSlots = 0;
        for (int i = 0; i < _migSlots.Length && allocatedSlots < slotsNeeded; i++)
        {
            if (_migSlots[i] == 0)
            {
                _migSlots[i] = handle.GetHashCode();
                allocatedSlots++;
            }
        }

        if (allocatedSlots == 0)
        {
            return Task.FromResult(new ResourceAllocation
            {
                RequestId = request.RequestId,
                Success = false,
                FailureReason = "No MIG instances available"
            });
        }

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedGpuPercent = (allocatedSlots / 7.0) * 100
        });
    }
}

/// <summary>
/// Multi-Process Service (MPS) GPU strategy.
/// Enables concurrent kernel execution.
/// </summary>
public sealed class MpsGpuStrategy : ResourceStrategyBase
{
    private long _allocatedPercentMilliunits;
    private readonly ConcurrentDictionary<string, long> _handlePercents = new();

    public override string StrategyId => "gpu-mps";
    public override string DisplayName => "Multi-Process Service (MPS) Manager";
    public override ResourceCategory Category => ResourceCategory.Gpu;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = false, SupportsMemory = false, SupportsIO = false,
        SupportsGpu = true, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = false, SupportsPreemption = false
    };
    public override string SemanticDescription =>
        "NVIDIA Multi-Process Service (MPS) manager enabling concurrent kernel execution " +
        "from multiple processes on a single GPU for improved utilization.";
    public override string[] Tags => ["gpu", "mps", "concurrent", "nvidia", "multi-process"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ResourceMetrics
        {
            GpuPercent = Interlocked.Read(ref _allocatedPercentMilliunits) / 1000.0,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var milliunits = (long)(request.GpuPercent * 1000);
        _handlePercents[handle] = milliunits;
        Interlocked.Add(ref _allocatedPercentMilliunits, milliunits);

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedGpuPercent = request.GpuPercent
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        if (allocation.AllocationHandle != null && _handlePercents.TryRemove(allocation.AllocationHandle, out var milliunits))
            Interlocked.Add(ref _allocatedPercentMilliunits, -milliunits);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Virtual GPU (vGPU) strategy for virtualized environments.
/// </summary>
public sealed class VgpuStrategy : ResourceStrategyBase
{
    private long _allocatedPercentMilliunits;
    private readonly ConcurrentDictionary<string, long> _handlePercents = new();

    public override string StrategyId => "gpu-vgpu";
    public override string DisplayName => "Virtual GPU (vGPU) Manager";
    public override ResourceCategory Category => ResourceCategory.Gpu;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = false, SupportsMemory = false, SupportsIO = false,
        SupportsGpu = true, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = true, SupportsPreemption = false
    };
    public override string SemanticDescription =>
        "Virtual GPU (vGPU) manager for virtualized environments, providing hardware-accelerated " +
        "graphics and compute to virtual machines with scheduling guarantees.";
    public override string[] Tags => ["gpu", "vgpu", "virtualization", "vm", "scheduling"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ResourceMetrics
        {
            GpuPercent = Interlocked.Read(ref _allocatedPercentMilliunits) / 1000.0,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var percentMilliunits = (long)(request.GpuPercent * 1000);
        _handlePercents[handle] = percentMilliunits;
        Interlocked.Add(ref _allocatedPercentMilliunits, percentMilliunits);

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedGpuPercent = request.GpuPercent
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        if (allocation.AllocationHandle != null && _handlePercents.TryRemove(allocation.AllocationHandle, out var milliunits))
            Interlocked.Add(ref _allocatedPercentMilliunits, -milliunits);
        return Task.FromResult(true);
    }
}
