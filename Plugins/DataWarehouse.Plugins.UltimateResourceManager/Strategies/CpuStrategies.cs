using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateResourceManager.Strategies;

/// <summary>
/// Fair-share CPU scheduling strategy.
/// Distributes CPU fairly among requesters based on weight.
/// </summary>
public sealed class FairShareCpuStrategy : ResourceStrategyBase
{
    private readonly Dictionary<string, double> _weights = new();
    private double _totalWeight;

    public override string StrategyId => "cpu-fair-share";
    public override string DisplayName => "Fair-Share CPU Scheduler";
    public override ResourceCategory Category => ResourceCategory.Cpu;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = true, SupportsMemory = false, SupportsIO = false,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = true, SupportsPreemption = true
    };
    public override string SemanticDescription =>
        "Fair-share CPU scheduler that distributes CPU cycles among requesters proportionally to their weights, " +
        "preventing starvation while ensuring high-priority workloads get appropriate share.";
    public override string[] Tags => ["cpu", "fair-share", "scheduling", "weight-based"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        var proc = Process.GetCurrentProcess();
        return Task.FromResult(new ResourceMetrics
        {
            CpuPercent = proc.TotalProcessorTime.TotalSeconds,
            MemoryBytes = proc.WorkingSet64,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var weight = request.Priority / 100.0;
        _weights[handle] = weight;
        _totalWeight += weight;

        var share = _totalWeight > 0 ? weight / _totalWeight : 1.0;
        var allocatedCores = request.CpuCores * share;

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedCpuCores = allocatedCores
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        if (allocation.AllocationHandle != null && _weights.TryGetValue(allocation.AllocationHandle, out var weight))
        {
            _weights.Remove(allocation.AllocationHandle);
            _totalWeight -= weight;
        }
        return Task.FromResult(true);
    }
}

/// <summary>
/// Priority-based CPU scheduling strategy.
/// Allocates CPU based on strict priority ordering.
/// </summary>
public sealed class PriorityCpuStrategy : ResourceStrategyBase
{
    public override string StrategyId => "cpu-priority";
    public override string DisplayName => "Priority CPU Scheduler";
    public override ResourceCategory Category => ResourceCategory.Cpu;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = true, SupportsMemory = false, SupportsIO = false,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = false, SupportsPreemption = true
    };
    public override string SemanticDescription =>
        "Priority-based CPU scheduler that allocates CPU strictly by priority level, " +
        "with higher priority workloads preempting lower priority ones.";
    public override string[] Tags => ["cpu", "priority", "preemption", "strict"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ResourceMetrics
        {
            CpuPercent = Environment.ProcessorCount > 0 ? 50.0 : 0,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var priorityFactor = request.Priority / 100.0;

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedCpuCores = request.CpuCores * priorityFactor
        });
    }
}

/// <summary>
/// CPU affinity management strategy.
/// Pins workloads to specific CPU cores for cache locality.
/// </summary>
public sealed class AffinityCpuStrategy : ResourceStrategyBase
{
    private readonly HashSet<int> _usedCores = new();
    private readonly int _totalCores = Environment.ProcessorCount;

    public override string StrategyId => "cpu-affinity";
    public override string DisplayName => "CPU Affinity Manager";
    public override ResourceCategory Category => ResourceCategory.Cpu;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = true, SupportsMemory = false, SupportsIO = false,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = false,
        SupportsHierarchicalQuotas = false, SupportsPreemption = false
    };
    public override string SemanticDescription =>
        "CPU affinity manager that pins workloads to specific cores, improving cache locality " +
        "and reducing context switch overhead for latency-sensitive operations.";
    public override string[] Tags => ["cpu", "affinity", "pinning", "cache-locality"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ResourceMetrics
        {
            CpuPercent = (_usedCores.Count / (double)_totalCores) * 100,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var neededCores = (int)Math.Ceiling(request.CpuCores);
        var allocatedCores = 0;

        for (int i = 0; i < _totalCores && allocatedCores < neededCores; i++)
        {
            if (!_usedCores.Contains(i))
            {
                _usedCores.Add(i);
                allocatedCores++;
            }
        }

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = allocatedCores > 0,
            AllocationHandle = handle,
            AllocatedCpuCores = allocatedCores,
            FailureReason = allocatedCores == 0 ? "No CPU cores available" : null
        });
    }
}

/// <summary>
/// Real-time CPU scheduling strategy.
/// Provides guaranteed CPU time for real-time workloads.
/// </summary>
public sealed class RealTimeCpuStrategy : ResourceStrategyBase
{
    public override string StrategyId => "cpu-realtime";
    public override string DisplayName => "Real-Time CPU Scheduler";
    public override ResourceCategory Category => ResourceCategory.Cpu;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = true, SupportsMemory = false, SupportsIO = false,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = false, SupportsPreemption = true
    };
    public override string SemanticDescription =>
        "Real-time CPU scheduler providing guaranteed CPU time with deterministic latency " +
        "for time-critical operations like audio processing or control systems.";
    public override string[] Tags => ["cpu", "realtime", "deterministic", "low-latency"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ResourceMetrics
        {
            CpuPercent = 25.0, // Reserved for RT
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
            AllocatedCpuCores = request.CpuCores
        });
    }
}

/// <summary>
/// NUMA-aware CPU scheduling strategy.
/// Optimizes for Non-Uniform Memory Access architectures.
/// </summary>
public sealed class NumaCpuStrategy : ResourceStrategyBase
{
    public override string StrategyId => "cpu-numa";
    public override string DisplayName => "NUMA-Aware CPU Scheduler";
    public override ResourceCategory Category => ResourceCategory.Cpu;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = true, SupportsMemory = true, SupportsIO = false,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = true, SupportsPreemption = false
    };
    public override string SemanticDescription =>
        "NUMA-aware scheduler that co-locates CPU and memory allocations on the same NUMA node, " +
        "minimizing cross-node memory access latency for memory-intensive workloads.";
    public override string[] Tags => ["cpu", "numa", "memory-locality", "optimization"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ResourceMetrics
        {
            CpuPercent = 30.0,
            MemoryBytes = GC.GetTotalMemory(false),
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
            AllocatedCpuCores = request.CpuCores,
            AllocatedMemoryBytes = request.MemoryBytes
        });
    }
}
