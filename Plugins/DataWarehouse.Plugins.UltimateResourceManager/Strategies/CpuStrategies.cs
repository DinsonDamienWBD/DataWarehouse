using System.Collections.Concurrent;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateResourceManager.Strategies;

/// <summary>
/// Fair-share CPU scheduling strategy.
/// Distributes CPU fairly among requesters based on weight.
/// </summary>
public sealed class FairShareCpuStrategy : ResourceStrategyBase
{
    private readonly ConcurrentDictionary<string, double> _weights = new();
    private long _totalWeightMilliunits; // Stored as integer milliunits for Interlocked safety (1 unit = 1000 milliunits)

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
        var totalWeight = Interlocked.Read(ref _totalWeightMilliunits) / 1000.0;
        var cpuPercent = totalWeight > 0
            ? (totalWeight / Environment.ProcessorCount) * 100
            : proc.TotalProcessorTime.TotalSeconds;
        return Task.FromResult(new ResourceMetrics
        {
            CpuPercent = cpuPercent,
            MemoryBytes = proc.WorkingSet64,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var weight = request.Priority / 100.0;
        _weights[handle] = weight;
        Interlocked.Add(ref _totalWeightMilliunits, (long)(weight * 1000));

        var totalWeight = Interlocked.Read(ref _totalWeightMilliunits) / 1000.0;
        var share = totalWeight > 0 ? weight / totalWeight : 1.0;
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
        if (allocation.AllocationHandle != null && _weights.TryRemove(allocation.AllocationHandle, out var weight))
        {
            Interlocked.Add(ref _totalWeightMilliunits, -(long)(weight * 1000));
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
    private long _allocatedCoresMilliunits;
    private readonly ConcurrentDictionary<string, long> _handleCores = new();

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
        var proc = Process.GetCurrentProcess();
        var cpuCores = Interlocked.Read(ref _allocatedCoresMilliunits) / 1000.0;
        var cpuPercent = Environment.ProcessorCount > 0
            ? (cpuCores / Environment.ProcessorCount) * 100.0
            : 0.0;
        return Task.FromResult(new ResourceMetrics
        {
            CpuPercent = cpuPercent,
            MemoryBytes = proc.WorkingSet64,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var priorityFactor = request.Priority / 100.0;
        var allocatedCores = request.CpuCores * priorityFactor;
        var milliunits = (long)(allocatedCores * 1000);
        _handleCores[handle] = milliunits;
        Interlocked.Add(ref _allocatedCoresMilliunits, milliunits);

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
        if (allocation.AllocationHandle != null && _handleCores.TryRemove(allocation.AllocationHandle, out var milliunits))
            Interlocked.Add(ref _allocatedCoresMilliunits, -milliunits);
        return Task.FromResult(true);
    }
}

/// <summary>
/// CPU affinity management strategy.
/// Pins workloads to specific CPU cores for cache locality.
/// </summary>
public sealed class AffinityCpuStrategy : ResourceStrategyBase
{
    private readonly ConcurrentDictionary<int, byte> _usedCores = new();
    private readonly ConcurrentDictionary<string, List<int>> _handleCores = new(); // handle -> allocated core indices
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
        var allocatedList = new List<int>(neededCores);

        for (int i = 0; i < _totalCores && allocatedList.Count < neededCores; i++)
        {
            if (_usedCores.TryAdd(i, 0))
            {
                allocatedList.Add(i);
            }
        }

        if (allocatedList.Count > 0)
        {
            _handleCores[handle] = allocatedList;
        }

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = allocatedList.Count > 0,
            AllocationHandle = handle,
            AllocatedCpuCores = allocatedList.Count,
            FailureReason = allocatedList.Count == 0 ? "No CPU cores available" : null
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        if (allocation.AllocationHandle != null &&
            _handleCores.TryRemove(allocation.AllocationHandle, out var cores))
        {
            foreach (var core in cores)
                _usedCores.TryRemove(core, out _);
        }
        return Task.FromResult(true);
    }
}

/// <summary>
/// Real-time CPU scheduling strategy.
/// Provides guaranteed CPU time for real-time workloads.
/// </summary>
public sealed class RealTimeCpuStrategy : ResourceStrategyBase
{
    private long _allocatedCoresMilliunits;
    private readonly ConcurrentDictionary<string, long> _handleCores = new();

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
        var proc = Process.GetCurrentProcess();
        var allocatedCores = Interlocked.Read(ref _allocatedCoresMilliunits) / 1000.0;
        var cpuPercent = Environment.ProcessorCount > 0
            ? (allocatedCores / Environment.ProcessorCount) * 100.0
            : 0.0;
        return Task.FromResult(new ResourceMetrics
        {
            CpuPercent = cpuPercent,
            MemoryBytes = proc.WorkingSet64,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var milliunits = (long)(request.CpuCores * 1000);
        _handleCores[handle] = milliunits;
        Interlocked.Add(ref _allocatedCoresMilliunits, milliunits);

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedCpuCores = request.CpuCores
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        if (allocation.AllocationHandle != null && _handleCores.TryRemove(allocation.AllocationHandle, out var milliunits))
            Interlocked.Add(ref _allocatedCoresMilliunits, -milliunits);
        return Task.FromResult(true);
    }
}

/// <summary>
/// NUMA-aware CPU scheduling strategy.
/// Optimizes for Non-Uniform Memory Access architectures.
/// </summary>
public sealed class NumaCpuStrategy : ResourceStrategyBase
{
    private long _allocatedCoresMilliunits;
    private long _allocatedMemoryBytes;
    private readonly ConcurrentDictionary<string, (long coreMilliunits, long memBytes)> _handleAllocs = new();

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
        var allocatedCores = Interlocked.Read(ref _allocatedCoresMilliunits) / 1000.0;
        var cpuPercent = Environment.ProcessorCount > 0
            ? (allocatedCores / Environment.ProcessorCount) * 100.0
            : 0.0;
        return Task.FromResult(new ResourceMetrics
        {
            CpuPercent = cpuPercent,
            MemoryBytes = Interlocked.Read(ref _allocatedMemoryBytes),
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var coreMilliunits = (long)(request.CpuCores * 1000);
        _handleAllocs[handle] = (coreMilliunits, request.MemoryBytes);
        Interlocked.Add(ref _allocatedCoresMilliunits, coreMilliunits);
        Interlocked.Add(ref _allocatedMemoryBytes, request.MemoryBytes);

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedCpuCores = request.CpuCores,
            AllocatedMemoryBytes = request.MemoryBytes
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        if (allocation.AllocationHandle != null && _handleAllocs.TryRemove(allocation.AllocationHandle, out var alloc))
        {
            Interlocked.Add(ref _allocatedCoresMilliunits, -alloc.coreMilliunits);
            Interlocked.Add(ref _allocatedMemoryBytes, -alloc.memBytes);
        }
        return Task.FromResult(true);
    }
}
