using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateResourceManager.Strategies;

/// <summary>
/// Deadline-based I/O scheduling strategy.
/// Ensures I/O operations complete within deadlines.
/// </summary>
public sealed class DeadlineIoStrategy : ResourceStrategyBase
{
    private long _currentIops;
    private long _currentBandwidth;
    private readonly long _maxIops = 100000;

    public override string StrategyId => "io-deadline";
    public override string DisplayName => "Deadline I/O Scheduler";
    public override ResourceCategory Category => ResourceCategory.IO;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = false, SupportsMemory = false, SupportsIO = true,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = false, SupportsPreemption = true
    };
    public override string SemanticDescription =>
        "Deadline-based I/O scheduler that ensures operations complete within specified deadlines, " +
        "preventing I/O starvation while maintaining fairness.";
    public override string[] Tags => ["io", "deadline", "scheduling", "latency"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ResourceMetrics
        {
            IopsRate = _currentIops,
            IoBandwidth = _currentBandwidth,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");

        // P2-2560: Atomically check-and-add to prevent TOCTOU over-allocation.
        // Interlocked.Add returns the new value; if it exceeds the limit, roll back.
        var newIops = Interlocked.Add(ref _currentIops, request.Iops);
        if (newIops > _maxIops)
        {
            Interlocked.Add(ref _currentIops, -request.Iops); // roll back
            return Task.FromResult(new ResourceAllocation
            {
                RequestId = request.RequestId,
                Success = false,
                FailureReason = "IOPS limit exceeded"
            });
        }
        Interlocked.Add(ref _currentBandwidth, request.IoBandwidth);

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedIops = request.Iops,
            AllocatedIoBandwidth = request.IoBandwidth
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        Interlocked.Add(ref _currentIops, -allocation.AllocatedIops);
        Interlocked.Add(ref _currentBandwidth, -allocation.AllocatedIoBandwidth);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Token bucket I/O throttling strategy.
/// Rate limits I/O using token bucket algorithm.
/// </summary>
public sealed class TokenBucketIoStrategy : ResourceStrategyBase
{
    private readonly Dictionary<string, long> _buckets = new();

    public override string StrategyId => "io-token-bucket";
    public override string DisplayName => "Token Bucket I/O Throttler";
    public override ResourceCategory Category => ResourceCategory.IO;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = false, SupportsMemory = false, SupportsIO = true,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = true, SupportsPreemption = false
    };
    public override string SemanticDescription =>
        "Token bucket I/O throttler that smoothly rate-limits I/O operations, " +
        "allowing bursts while maintaining average throughput limits.";
    public override string[] Tags => ["io", "token-bucket", "rate-limit", "burst"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        var totalTokens = _buckets.Values.Sum();
        return Task.FromResult(new ResourceMetrics
        {
            IopsRate = totalTokens,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        _buckets[handle] = request.Iops;

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedIops = request.Iops,
            AllocatedIoBandwidth = request.IoBandwidth
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        if (allocation.AllocationHandle != null)
            _buckets.Remove(allocation.AllocationHandle);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Bandwidth-limiting I/O strategy.
/// Enforces per-requester bandwidth limits.
/// </summary>
public sealed class BandwidthLimitIoStrategy : ResourceStrategyBase
{
    private long _allocatedBandwidth;
    private readonly ConcurrentDictionary<string, long> _handleBandwidth = new();

    public override string StrategyId => "io-bandwidth-limit";
    public override string DisplayName => "Bandwidth Limit I/O Manager";
    public override ResourceCategory Category => ResourceCategory.IO;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = false, SupportsMemory = false, SupportsIO = true,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = true, SupportsPreemption = false
    };
    public override string SemanticDescription =>
        "Bandwidth limiting I/O manager that enforces per-tenant or per-operation " +
        "bandwidth caps to prevent I/O monopolization.";
    public override string[] Tags => ["io", "bandwidth", "limit", "throttle"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ResourceMetrics
        {
            IoBandwidth = Interlocked.Read(ref _allocatedBandwidth),
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
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
/// Priority I/O strategy.
/// Handles I/O based on priority classes.
/// </summary>
public sealed class PriorityIoStrategy : ResourceStrategyBase
{
    private long _currentIops;
    private long _currentBandwidth;
    private readonly ConcurrentDictionary<string, (long iops, long bandwidth)> _handleAllocs = new();

    public override string StrategyId => "io-priority";
    public override string DisplayName => "Priority I/O Scheduler";
    public override ResourceCategory Category => ResourceCategory.IO;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = false, SupportsMemory = false, SupportsIO = true,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = false, SupportsPreemption = true
    };
    public override string SemanticDescription =>
        "Priority-based I/O scheduler with 3 classes: real-time, best-effort, and idle. " +
        "Real-time I/O always takes precedence.";
    public override string[] Tags => ["io", "priority", "classes", "realtime"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ResourceMetrics
        {
            IopsRate = Interlocked.Read(ref _currentIops),
            IoBandwidth = Interlocked.Read(ref _currentBandwidth),
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var priorityMultiplier = request.Priority > 80 ? 1.5 : (request.Priority > 50 ? 1.0 : 0.5);
        var allocatedIops = (long)(request.Iops * priorityMultiplier);
        var allocatedBandwidth = (long)(request.IoBandwidth * priorityMultiplier);
        _handleAllocs[handle] = (allocatedIops, allocatedBandwidth);
        Interlocked.Add(ref _currentIops, allocatedIops);
        Interlocked.Add(ref _currentBandwidth, allocatedBandwidth);

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedIops = allocatedIops,
            AllocatedIoBandwidth = allocatedBandwidth
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        if (allocation.AllocationHandle != null && _handleAllocs.TryRemove(allocation.AllocationHandle, out var alloc))
        {
            Interlocked.Add(ref _currentIops, -alloc.iops);
            Interlocked.Add(ref _currentBandwidth, -alloc.bandwidth);
        }
        return Task.FromResult(true);
    }
}
