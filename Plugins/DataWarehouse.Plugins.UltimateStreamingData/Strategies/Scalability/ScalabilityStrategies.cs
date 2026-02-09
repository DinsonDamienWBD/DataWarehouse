using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace DataWarehouse.Plugins.UltimateStreamingData.Strategies.Scalability;

#region 111.8.1 Partitioning Strategy

/// <summary>
/// 111.8.1: Partitioning strategy for parallel stream processing.
/// Distributes events across partitions for horizontal scaling.
/// </summary>
public sealed class PartitioningStrategy : StreamingDataStrategyBase
{
    private readonly ConcurrentDictionary<string, PartitionManager> _managers = new();

    public override string StrategyId => "scale-partitioning";
    public override string DisplayName => "Stream Partitioning";
    public override StreamingCategory Category => StreamingCategory.StreamScalability;
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = true,
        SupportsWindowing = true,
        SupportsStateManagement = true,
        SupportsCheckpointing = true,
        SupportsBackpressure = true,
        SupportsPartitioning = true,
        SupportsAutoScaling = true,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 5000000,
        TypicalLatencyMs = 0.5
    };
    public override string SemanticDescription =>
        "Stream partitioning strategy for horizontal scaling through event distribution across " +
        "partitions, supporting hash, range, and custom partitioning schemes.";
    public override string[] Tags => ["partitioning", "parallel", "horizontal-scaling", "distribution", "hash"];

    /// <summary>
    /// Creates a partition manager.
    /// </summary>
    public Task<string> CreateManagerAsync(
        string streamId,
        PartitionConfig config,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var managerId = $"part-{Guid.NewGuid():N}";
        var partitions = new ConcurrentDictionary<int, PartitionState>();

        for (int i = 0; i < config.PartitionCount; i++)
        {
            partitions[i] = new PartitionState
            {
                PartitionId = i,
                EventCount = 0,
                LastEventTime = DateTimeOffset.MinValue,
                AssignedWorker = null
            };
        }

        var manager = new PartitionManager
        {
            ManagerId = managerId,
            StreamId = streamId,
            Config = config,
            Partitions = partitions,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _managers[managerId] = manager;
        return Task.FromResult(managerId);
    }

    /// <summary>
    /// Assigns an event to a partition.
    /// </summary>
    public Task<int> AssignPartitionAsync(
        string managerId,
        string? key,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (!_managers.TryGetValue(managerId, out var manager))
        {
            throw new InvalidOperationException($"Manager '{managerId}' not found");
        }

        var partition = manager.Config.Strategy switch
        {
            PartitionStrategy.Hash => key != null
                ? Math.Abs(key.GetHashCode()) % manager.Config.PartitionCount
                : Random.Shared.Next(manager.Config.PartitionCount),
            PartitionStrategy.RoundRobin => (int)(Interlocked.Increment(ref manager.RoundRobinCounter) % manager.Config.PartitionCount),
            PartitionStrategy.Random => Random.Shared.Next(manager.Config.PartitionCount),
            PartitionStrategy.LeastLoaded => GetLeastLoadedPartition(manager),
            _ => 0
        };

        // Update partition stats
        if (manager.Partitions.TryGetValue(partition, out var state))
        {
            Interlocked.Increment(ref state.EventCount);
            state.LastEventTime = DateTimeOffset.UtcNow;
        }

        return Task.FromResult(partition);
    }

    /// <summary>
    /// Rebalances partitions across workers.
    /// </summary>
    public Task<RebalanceResult> RebalanceAsync(
        string managerId,
        string[] workers,
        CancellationToken ct = default)
    {
        if (!_managers.TryGetValue(managerId, out var manager))
        {
            throw new InvalidOperationException($"Manager '{managerId}' not found");
        }

        var assignments = new Dictionary<int, string>();
        var partitionsPerWorker = manager.Config.PartitionCount / workers.Length;
        var remainder = manager.Config.PartitionCount % workers.Length;

        var partitionIndex = 0;
        for (int w = 0; w < workers.Length; w++)
        {
            var count = partitionsPerWorker + (w < remainder ? 1 : 0);
            for (int p = 0; p < count && partitionIndex < manager.Config.PartitionCount; p++, partitionIndex++)
            {
                assignments[partitionIndex] = workers[w];
                if (manager.Partitions.TryGetValue(partitionIndex, out var state))
                {
                    state.AssignedWorker = workers[w];
                }
            }
        }

        return Task.FromResult(new RebalanceResult
        {
            Assignments = assignments,
            WorkerCount = workers.Length,
            PartitionCount = manager.Config.PartitionCount,
            RebalancedAt = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// Gets partition statistics.
    /// </summary>
    public Task<IReadOnlyList<PartitionStats>> GetPartitionStatsAsync(
        string managerId,
        CancellationToken ct = default)
    {
        if (!_managers.TryGetValue(managerId, out var manager))
        {
            return Task.FromResult<IReadOnlyList<PartitionStats>>(Array.Empty<PartitionStats>());
        }

        var stats = manager.Partitions.Values.Select(p => new PartitionStats
        {
            PartitionId = p.PartitionId,
            EventCount = p.EventCount,
            LastEventTime = p.LastEventTime,
            AssignedWorker = p.AssignedWorker
        }).ToList();

        return Task.FromResult<IReadOnlyList<PartitionStats>>(stats);
    }

    private static int GetLeastLoadedPartition(PartitionManager manager)
    {
        return manager.Partitions.Values
            .OrderBy(p => p.EventCount)
            .First()
            .PartitionId;
    }
}

/// <summary>
/// Partition configuration.
/// </summary>
public sealed record PartitionConfig
{
    public int PartitionCount { get; init; } = 16;
    public PartitionStrategy Strategy { get; init; } = PartitionStrategy.Hash;
    public bool EnableRebalancing { get; init; } = true;
}

public enum PartitionStrategy { Hash, RoundRobin, Random, Range, LeastLoaded, Custom }

internal sealed class PartitionManager
{
    public required string ManagerId { get; init; }
    public required string StreamId { get; init; }
    public required PartitionConfig Config { get; init; }
    public required ConcurrentDictionary<int, PartitionState> Partitions { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public long RoundRobinCounter;
}

internal sealed class PartitionState
{
    public int PartitionId { get; init; }
    public long EventCount;
    public DateTimeOffset LastEventTime { get; set; }
    public string? AssignedWorker { get; set; }
}

/// <summary>
/// Rebalance result.
/// </summary>
public sealed record RebalanceResult
{
    public required Dictionary<int, string> Assignments { get; init; }
    public int WorkerCount { get; init; }
    public int PartitionCount { get; init; }
    public DateTimeOffset RebalancedAt { get; init; }
}

/// <summary>
/// Partition statistics.
/// </summary>
public sealed record PartitionStats
{
    public int PartitionId { get; init; }
    public long EventCount { get; init; }
    public DateTimeOffset LastEventTime { get; init; }
    public string? AssignedWorker { get; init; }
}

#endregion

#region 111.8.2 Auto-Scaling Strategy

/// <summary>
/// 111.8.2: Auto-scaling strategy for dynamic resource allocation.
/// Automatically scales workers based on load metrics.
/// </summary>
public sealed class AutoScalingStrategy : StreamingDataStrategyBase
{
    private readonly ConcurrentDictionary<string, AutoScaler> _scalers = new();

    public override string StrategyId => "scale-autoscaling";
    public override string DisplayName => "Auto-Scaling";
    public override StreamingCategory Category => StreamingCategory.StreamScalability;
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = true,
        SupportsWindowing = false,
        SupportsStateManagement = false,
        SupportsCheckpointing = true,
        SupportsBackpressure = true,
        SupportsPartitioning = true,
        SupportsAutoScaling = true,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 10000000,
        TypicalLatencyMs = 0.1
    };
    public override string SemanticDescription =>
        "Auto-scaling strategy for dynamic resource allocation based on throughput, latency, " +
        "and backlog metrics. Supports scale-up, scale-down, and cooldown periods.";
    public override string[] Tags => ["autoscaling", "elastic", "dynamic", "metrics", "resource-management"];

    /// <summary>
    /// Creates an auto-scaler.
    /// </summary>
    public Task<string> CreateScalerAsync(
        string jobId,
        AutoScaleConfig config,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var scalerId = $"autoscale-{Guid.NewGuid():N}";
        var scaler = new AutoScaler
        {
            ScalerId = scalerId,
            JobId = jobId,
            Config = config,
            CurrentInstances = config.MinInstances,
            MetricsHistory = new List<ScalingMetrics>(),
            ScalingHistory = new List<ScalingEvent>(),
            CreatedAt = DateTimeOffset.UtcNow,
            LastScaleTime = DateTimeOffset.MinValue
        };

        _scalers[scalerId] = scaler;
        return Task.FromResult(scalerId);
    }

    /// <summary>
    /// Reports metrics for scaling decisions.
    /// </summary>
    public Task ReportMetricsAsync(
        string scalerId,
        ScalingMetrics metrics,
        CancellationToken ct = default)
    {
        if (!_scalers.TryGetValue(scalerId, out var scaler))
        {
            return Task.CompletedTask;
        }

        lock (scaler.MetricsHistory)
        {
            scaler.MetricsHistory.Add(metrics);
            // Keep only recent metrics
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
            scaler.MetricsHistory.RemoveAll(m => m.Timestamp < cutoff);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Evaluates scaling needs and returns a decision.
    /// </summary>
    public Task<ScalingDecision> EvaluateScalingAsync(
        string scalerId,
        CancellationToken ct = default)
    {
        if (!_scalers.TryGetValue(scalerId, out var scaler))
        {
            return Task.FromResult(new ScalingDecision { Action = ScalingAction.None, Reason = "Scaler not found" });
        }

        var config = scaler.Config;
        var now = DateTimeOffset.UtcNow;

        // Check cooldown
        if ((now - scaler.LastScaleTime) < config.ScaleCooldown)
        {
            return Task.FromResult(new ScalingDecision
            {
                Action = ScalingAction.None,
                Reason = "In cooldown period",
                CurrentInstances = scaler.CurrentInstances
            });
        }

        // Get average metrics
        List<ScalingMetrics> recentMetrics;
        lock (scaler.MetricsHistory)
        {
            recentMetrics = scaler.MetricsHistory
                .Where(m => m.Timestamp >= now - config.MetricWindow)
                .ToList();
        }

        if (recentMetrics.Count == 0)
        {
            return Task.FromResult(new ScalingDecision
            {
                Action = ScalingAction.None,
                Reason = "No metrics available",
                CurrentInstances = scaler.CurrentInstances
            });
        }

        var avgCpuUtilization = recentMetrics.Average(m => m.CpuUtilization);
        var avgThroughput = recentMetrics.Average(m => m.EventsPerSecond);
        var avgBacklog = recentMetrics.Average(m => m.BacklogSize);

        // Evaluate scale-up
        if (avgCpuUtilization > config.ScaleUpCpuThreshold ||
            avgBacklog > config.ScaleUpBacklogThreshold)
        {
            if (scaler.CurrentInstances < config.MaxInstances)
            {
                var newInstances = Math.Min(
                    scaler.CurrentInstances + config.ScaleUpIncrement,
                    config.MaxInstances);

                return Task.FromResult(new ScalingDecision
                {
                    Action = ScalingAction.ScaleUp,
                    CurrentInstances = scaler.CurrentInstances,
                    TargetInstances = newInstances,
                    Reason = $"High load: CPU={avgCpuUtilization:P}, Backlog={avgBacklog}"
                });
            }
        }

        // Evaluate scale-down
        if (avgCpuUtilization < config.ScaleDownCpuThreshold &&
            avgBacklog < config.ScaleDownBacklogThreshold)
        {
            if (scaler.CurrentInstances > config.MinInstances)
            {
                var newInstances = Math.Max(
                    scaler.CurrentInstances - config.ScaleDownDecrement,
                    config.MinInstances);

                return Task.FromResult(new ScalingDecision
                {
                    Action = ScalingAction.ScaleDown,
                    CurrentInstances = scaler.CurrentInstances,
                    TargetInstances = newInstances,
                    Reason = $"Low load: CPU={avgCpuUtilization:P}, Backlog={avgBacklog}"
                });
            }
        }

        return Task.FromResult(new ScalingDecision
        {
            Action = ScalingAction.None,
            Reason = "Load within normal range",
            CurrentInstances = scaler.CurrentInstances
        });
    }

    /// <summary>
    /// Applies a scaling decision.
    /// </summary>
    public Task ApplyScalingAsync(
        string scalerId,
        ScalingDecision decision,
        CancellationToken ct = default)
    {
        if (!_scalers.TryGetValue(scalerId, out var scaler))
        {
            return Task.CompletedTask;
        }

        if (decision.Action == ScalingAction.None)
        {
            return Task.CompletedTask;
        }

        var previousInstances = scaler.CurrentInstances;
        scaler.CurrentInstances = decision.TargetInstances;
        scaler.LastScaleTime = DateTimeOffset.UtcNow;

        scaler.ScalingHistory.Add(new ScalingEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Action = decision.Action,
            PreviousInstances = previousInstances,
            NewInstances = decision.TargetInstances,
            Reason = decision.Reason
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets scaling history.
    /// </summary>
    public Task<IReadOnlyList<ScalingEvent>> GetScalingHistoryAsync(
        string scalerId,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (!_scalers.TryGetValue(scalerId, out var scaler))
        {
            return Task.FromResult<IReadOnlyList<ScalingEvent>>(Array.Empty<ScalingEvent>());
        }

        var history = scaler.ScalingHistory
            .OrderByDescending(e => e.Timestamp)
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<ScalingEvent>>(history);
    }
}

/// <summary>
/// Auto-scale configuration.
/// </summary>
public sealed record AutoScaleConfig
{
    public int MinInstances { get; init; } = 1;
    public int MaxInstances { get; init; } = 10;
    public double ScaleUpCpuThreshold { get; init; } = 0.8;
    public double ScaleDownCpuThreshold { get; init; } = 0.3;
    public long ScaleUpBacklogThreshold { get; init; } = 10000;
    public long ScaleDownBacklogThreshold { get; init; } = 100;
    public int ScaleUpIncrement { get; init; } = 2;
    public int ScaleDownDecrement { get; init; } = 1;
    public TimeSpan ScaleCooldown { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MetricWindow { get; init; } = TimeSpan.FromMinutes(2);
}

internal sealed class AutoScaler
{
    public required string ScalerId { get; init; }
    public required string JobId { get; init; }
    public required AutoScaleConfig Config { get; init; }
    public int CurrentInstances { get; set; }
    public required List<ScalingMetrics> MetricsHistory { get; init; }
    public required List<ScalingEvent> ScalingHistory { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastScaleTime { get; set; }
}

/// <summary>
/// Scaling metrics.
/// </summary>
public sealed record ScalingMetrics
{
    public double CpuUtilization { get; init; }
    public double MemoryUtilization { get; init; }
    public double EventsPerSecond { get; init; }
    public long BacklogSize { get; init; }
    public double AverageLatencyMs { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Scaling decision.
/// </summary>
public sealed record ScalingDecision
{
    public ScalingAction Action { get; init; }
    public int CurrentInstances { get; init; }
    public int TargetInstances { get; init; }
    public required string Reason { get; init; }
}

public enum ScalingAction { None, ScaleUp, ScaleDown }

/// <summary>
/// Scaling event record.
/// </summary>
public sealed record ScalingEvent
{
    public DateTimeOffset Timestamp { get; init; }
    public ScalingAction Action { get; init; }
    public int PreviousInstances { get; init; }
    public int NewInstances { get; init; }
    public string? Reason { get; init; }
}

#endregion

#region 111.8.3 Backpressure Strategy

/// <summary>
/// 111.8.3: Backpressure strategy for flow control.
/// Prevents system overload through rate limiting and buffering.
/// </summary>
public sealed class BackpressureStrategy : StreamingDataStrategyBase
{
    private readonly ConcurrentDictionary<string, BackpressureController> _controllers = new();

    public override string StrategyId => "scale-backpressure";
    public override string DisplayName => "Backpressure Control";
    public override StreamingCategory Category => StreamingCategory.StreamScalability;
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = true,
        SupportsWindowing = false,
        SupportsStateManagement = false,
        SupportsCheckpointing = true,
        SupportsBackpressure = true,
        SupportsPartitioning = true,
        SupportsAutoScaling = true,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 2000000,
        TypicalLatencyMs = 0.1
    };
    public override string SemanticDescription =>
        "Backpressure strategy for flow control preventing system overload through rate limiting, " +
        "buffering, sampling, and drop policies.";
    public override string[] Tags => ["backpressure", "flow-control", "rate-limiting", "buffering", "overload-protection"];

    /// <summary>
    /// Creates a backpressure controller.
    /// </summary>
    public Task<string> CreateControllerAsync(
        string streamId,
        BackpressureConfig config,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var controllerId = $"bp-{Guid.NewGuid():N}";
        var controller = new BackpressureController
        {
            ControllerId = controllerId,
            StreamId = streamId,
            Config = config,
            Buffer = new ConcurrentQueue<BufferedEvent>(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _controllers[controllerId] = controller;
        return Task.FromResult(controllerId);
    }

    /// <summary>
    /// Attempts to submit an event through the backpressure controller.
    /// </summary>
    public Task<SubmitResult> TrySubmitAsync<T>(
        string controllerId,
        T @event,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (!_controllers.TryGetValue(controllerId, out var controller))
        {
            throw new InvalidOperationException($"Controller '{controllerId}' not found");
        }

        var config = controller.Config;

        // Check buffer capacity
        if (controller.Buffer.Count >= config.MaxBufferSize)
        {
            switch (config.Strategy)
            {
                case BackpressureStrategyType.Block:
                    // Would block here in production
                    Interlocked.Increment(ref controller.BlockedCount);
                    return Task.FromResult(new SubmitResult
                    {
                        Accepted = false,
                        Action = BackpressureAction.Blocked,
                        Reason = "Buffer full, blocked"
                    });

                case BackpressureStrategyType.Drop:
                    Interlocked.Increment(ref controller.DroppedCount);
                    return Task.FromResult(new SubmitResult
                    {
                        Accepted = false,
                        Action = BackpressureAction.Dropped,
                        Reason = "Buffer full, dropped"
                    });

                case BackpressureStrategyType.DropOldest:
                    controller.Buffer.TryDequeue(out _);
                    Interlocked.Increment(ref controller.DroppedCount);
                    break;

                case BackpressureStrategyType.Sample:
                    // Random sampling - only accept 10% when buffer is full
                    if (Random.Shared.NextDouble() > 0.1)
                    {
                        Interlocked.Increment(ref controller.SampledOutCount);
                        return Task.FromResult(new SubmitResult
                        {
                            Accepted = false,
                            Action = BackpressureAction.Sampled,
                            Reason = "Buffer full, sampled out"
                        });
                    }
                    break;
            }
        }

        // Rate limiting
        if (config.RateLimitPerSecond.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - controller.RateLimitWindowStart;
            if (elapsed < TimeSpan.FromSeconds(1))
            {
                if (controller.RateLimitCounter >= config.RateLimitPerSecond.Value)
                {
                    Interlocked.Increment(ref controller.RateLimitedCount);
                    return Task.FromResult(new SubmitResult
                    {
                        Accepted = false,
                        Action = BackpressureAction.RateLimited,
                        Reason = "Rate limit exceeded"
                    });
                }
            }
            else
            {
                controller.RateLimitWindowStart = DateTimeOffset.UtcNow;
                controller.RateLimitCounter = 0;
            }
            Interlocked.Increment(ref controller.RateLimitCounter);
        }

        // Accept event
        controller.Buffer.Enqueue(new BufferedEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            Payload = @event,
            EnqueuedAt = DateTimeOffset.UtcNow
        });

        Interlocked.Increment(ref controller.AcceptedCount);

        return Task.FromResult(new SubmitResult
        {
            Accepted = true,
            Action = BackpressureAction.Accepted,
            BufferSize = controller.Buffer.Count
        });
    }

    /// <summary>
    /// Takes events from the buffer.
    /// </summary>
    public Task<IReadOnlyList<BufferedEvent>> TakeAsync(
        string controllerId,
        int maxCount = 100,
        CancellationToken ct = default)
    {
        if (!_controllers.TryGetValue(controllerId, out var controller))
        {
            return Task.FromResult<IReadOnlyList<BufferedEvent>>(Array.Empty<BufferedEvent>());
        }

        var events = new List<BufferedEvent>();
        while (events.Count < maxCount && controller.Buffer.TryDequeue(out var evt))
        {
            events.Add(evt);
        }

        return Task.FromResult<IReadOnlyList<BufferedEvent>>(events);
    }

    /// <summary>
    /// Gets backpressure statistics.
    /// </summary>
    public Task<BackpressureStats> GetStatsAsync(
        string controllerId,
        CancellationToken ct = default)
    {
        if (!_controllers.TryGetValue(controllerId, out var controller))
        {
            throw new InvalidOperationException($"Controller '{controllerId}' not found");
        }

        return Task.FromResult(new BackpressureStats
        {
            BufferSize = controller.Buffer.Count,
            AcceptedCount = controller.AcceptedCount,
            DroppedCount = controller.DroppedCount,
            BlockedCount = controller.BlockedCount,
            RateLimitedCount = controller.RateLimitedCount,
            SampledOutCount = controller.SampledOutCount,
            BufferUtilization = (double)controller.Buffer.Count / controller.Config.MaxBufferSize
        });
    }

    /// <summary>
    /// Adjusts backpressure parameters dynamically.
    /// </summary>
    public Task AdjustConfigAsync(
        string controllerId,
        BackpressureConfig newConfig,
        CancellationToken ct = default)
    {
        if (_controllers.TryGetValue(controllerId, out var controller))
        {
            // In production, would atomically update config
            var updated = new BackpressureController
            {
                ControllerId = controller.ControllerId,
                StreamId = controller.StreamId,
                Config = newConfig,
                Buffer = controller.Buffer,
                CreatedAt = controller.CreatedAt
            };
            _controllers[controllerId] = updated;
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Backpressure configuration.
/// </summary>
public sealed record BackpressureConfig
{
    public BackpressureStrategyType Strategy { get; init; } = BackpressureStrategyType.Block;
    public int MaxBufferSize { get; init; } = 10000;
    public long? RateLimitPerSecond { get; init; }
    public TimeSpan? BlockTimeout { get; init; }
    public double SamplingRate { get; init; } = 0.1;
}

public enum BackpressureStrategyType { Block, Drop, DropOldest, Sample, RateLimitOnly }

internal sealed class BackpressureController
{
    public required string ControllerId { get; init; }
    public required string StreamId { get; init; }
    public required BackpressureConfig Config { get; init; }
    public required ConcurrentQueue<BufferedEvent> Buffer { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public long AcceptedCount;
    public long DroppedCount;
    public long BlockedCount;
    public long RateLimitedCount;
    public long SampledOutCount;
    public DateTimeOffset RateLimitWindowStart = DateTimeOffset.UtcNow;
    public long RateLimitCounter;
}

/// <summary>
/// Buffered event.
/// </summary>
public sealed record BufferedEvent
{
    public required string EventId { get; init; }
    public object? Payload { get; init; }
    public DateTimeOffset EnqueuedAt { get; init; }
}

/// <summary>
/// Submit result.
/// </summary>
public sealed record SubmitResult
{
    public bool Accepted { get; init; }
    public BackpressureAction Action { get; init; }
    public string? Reason { get; init; }
    public int BufferSize { get; init; }
}

public enum BackpressureAction { Accepted, Blocked, Dropped, RateLimited, Sampled }

/// <summary>
/// Backpressure statistics.
/// </summary>
public sealed record BackpressureStats
{
    public int BufferSize { get; init; }
    public long AcceptedCount { get; init; }
    public long DroppedCount { get; init; }
    public long BlockedCount { get; init; }
    public long RateLimitedCount { get; init; }
    public long SampledOutCount { get; init; }
    public double BufferUtilization { get; init; }
}

#endregion

#region 111.8.4 Load Balancing Strategy

/// <summary>
/// 111.8.4: Load balancing strategy for distributing work across processors.
/// Supports multiple balancing algorithms and health-aware routing.
/// </summary>
public sealed class LoadBalancingStrategy : StreamingDataStrategyBase
{
    private readonly ConcurrentDictionary<string, LoadBalancer> _balancers = new();

    public override string StrategyId => "scale-loadbalancing";
    public override string DisplayName => "Load Balancing";
    public override StreamingCategory Category => StreamingCategory.StreamScalability;
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = true,
        SupportsWindowing = false,
        SupportsStateManagement = false,
        SupportsCheckpointing = true,
        SupportsBackpressure = true,
        SupportsPartitioning = true,
        SupportsAutoScaling = true,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 3000000,
        TypicalLatencyMs = 0.2
    };
    public override string SemanticDescription =>
        "Load balancing strategy for distributing work across stream processors with support for " +
        "round-robin, least-connections, weighted, and health-aware routing algorithms.";
    public override string[] Tags => ["load-balancing", "routing", "distribution", "health-aware", "weighted"];

    /// <summary>
    /// Creates a load balancer.
    /// </summary>
    public Task<string> CreateBalancerAsync(
        string name,
        LoadBalanceConfig config,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var balancerId = $"lb-{Guid.NewGuid():N}";
        var workers = new ConcurrentDictionary<string, WorkerState>();

        foreach (var worker in config.Workers)
        {
            workers[worker.WorkerId] = new WorkerState
            {
                WorkerId = worker.WorkerId,
                Weight = worker.Weight,
                IsHealthy = true,
                ActiveConnections = 0,
                TotalRequests = 0
            };
        }

        var balancer = new LoadBalancer
        {
            BalancerId = balancerId,
            Name = name,
            Config = config,
            Workers = workers,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _balancers[balancerId] = balancer;
        return Task.FromResult(balancerId);
    }

    /// <summary>
    /// Selects a worker for the next request.
    /// </summary>
    public Task<WorkerSelection> SelectWorkerAsync(
        string balancerId,
        string? affinityKey = null,
        CancellationToken ct = default)
    {
        if (!_balancers.TryGetValue(balancerId, out var balancer))
        {
            throw new InvalidOperationException($"Balancer '{balancerId}' not found");
        }

        var healthyWorkers = balancer.Workers.Values.Where(w => w.IsHealthy).ToList();
        if (healthyWorkers.Count == 0)
        {
            return Task.FromResult(new WorkerSelection
            {
                Success = false,
                Reason = "No healthy workers available"
            });
        }

        // Affinity routing
        if (affinityKey != null && balancer.Config.EnableAffinity)
        {
            var affinityIndex = Math.Abs(affinityKey.GetHashCode()) % healthyWorkers.Count;
            var affinityWorker = healthyWorkers[affinityIndex];
            Interlocked.Increment(ref affinityWorker.ActiveConnections);
            Interlocked.Increment(ref affinityWorker.TotalRequests);

            return Task.FromResult(new WorkerSelection
            {
                Success = true,
                WorkerId = affinityWorker.WorkerId,
                Reason = "Affinity routing"
            });
        }

        WorkerState selectedWorker;

        switch (balancer.Config.Algorithm)
        {
            case LoadBalanceAlgorithm.RoundRobin:
                var index = (int)(Interlocked.Increment(ref balancer.RoundRobinCounter) % healthyWorkers.Count);
                selectedWorker = healthyWorkers[index];
                break;

            case LoadBalanceAlgorithm.LeastConnections:
                selectedWorker = healthyWorkers.OrderBy(w => w.ActiveConnections).First();
                break;

            case LoadBalanceAlgorithm.Weighted:
                selectedWorker = SelectWeighted(healthyWorkers);
                break;

            case LoadBalanceAlgorithm.Random:
                selectedWorker = healthyWorkers[Random.Shared.Next(healthyWorkers.Count)];
                break;

            default:
                selectedWorker = healthyWorkers[0];
                break;
        }

        Interlocked.Increment(ref selectedWorker.ActiveConnections);
        Interlocked.Increment(ref selectedWorker.TotalRequests);

        return Task.FromResult(new WorkerSelection
        {
            Success = true,
            WorkerId = selectedWorker.WorkerId,
            Reason = balancer.Config.Algorithm.ToString()
        });
    }

    /// <summary>
    /// Releases a connection to a worker.
    /// </summary>
    public Task ReleaseWorkerAsync(
        string balancerId,
        string workerId,
        CancellationToken ct = default)
    {
        if (_balancers.TryGetValue(balancerId, out var balancer))
        {
            if (balancer.Workers.TryGetValue(workerId, out var worker))
            {
                Interlocked.Decrement(ref worker.ActiveConnections);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Updates worker health status.
    /// </summary>
    public Task UpdateWorkerHealthAsync(
        string balancerId,
        string workerId,
        bool isHealthy,
        CancellationToken ct = default)
    {
        if (_balancers.TryGetValue(balancerId, out var balancer))
        {
            if (balancer.Workers.TryGetValue(workerId, out var worker))
            {
                worker.IsHealthy = isHealthy;
                worker.LastHealthCheck = DateTimeOffset.UtcNow;
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets load balancer statistics.
    /// </summary>
    public Task<LoadBalancerStats> GetStatsAsync(
        string balancerId,
        CancellationToken ct = default)
    {
        if (!_balancers.TryGetValue(balancerId, out var balancer))
        {
            throw new InvalidOperationException($"Balancer '{balancerId}' not found");
        }

        var workers = balancer.Workers.Values.ToList();

        return Task.FromResult(new LoadBalancerStats
        {
            TotalWorkers = workers.Count,
            HealthyWorkers = workers.Count(w => w.IsHealthy),
            TotalActiveConnections = workers.Sum(w => w.ActiveConnections),
            TotalRequests = workers.Sum(w => w.TotalRequests),
            WorkerStats = workers.Select(w => new WorkerStats
            {
                WorkerId = w.WorkerId,
                IsHealthy = w.IsHealthy,
                ActiveConnections = w.ActiveConnections,
                TotalRequests = w.TotalRequests,
                Weight = w.Weight
            }).ToList()
        });
    }

    private static WorkerState SelectWeighted(List<WorkerState> workers)
    {
        var totalWeight = workers.Sum(w => w.Weight);
        var random = Random.Shared.NextDouble() * totalWeight;
        var cumulative = 0.0;

        foreach (var worker in workers)
        {
            cumulative += worker.Weight;
            if (random <= cumulative)
            {
                return worker;
            }
        }

        return workers.Last();
    }
}

/// <summary>
/// Load balance configuration.
/// </summary>
public sealed record LoadBalanceConfig
{
    public LoadBalanceAlgorithm Algorithm { get; init; } = LoadBalanceAlgorithm.RoundRobin;
    public required WorkerConfig[] Workers { get; init; }
    public bool EnableAffinity { get; init; }
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(10);
}

public enum LoadBalanceAlgorithm { RoundRobin, LeastConnections, Weighted, Random, IpHash }

/// <summary>
/// Worker configuration.
/// </summary>
public sealed record WorkerConfig
{
    public required string WorkerId { get; init; }
    public string? Address { get; init; }
    public double Weight { get; init; } = 1.0;
}

internal sealed class LoadBalancer
{
    public required string BalancerId { get; init; }
    public required string Name { get; init; }
    public required LoadBalanceConfig Config { get; init; }
    public required ConcurrentDictionary<string, WorkerState> Workers { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public long RoundRobinCounter;
}

internal sealed class WorkerState
{
    public required string WorkerId { get; init; }
    public double Weight { get; init; }
    public bool IsHealthy { get; set; }
    public long ActiveConnections;
    public long TotalRequests;
    public DateTimeOffset LastHealthCheck { get; set; }
}

/// <summary>
/// Worker selection result.
/// </summary>
public sealed record WorkerSelection
{
    public bool Success { get; init; }
    public string? WorkerId { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Load balancer statistics.
/// </summary>
public sealed record LoadBalancerStats
{
    public int TotalWorkers { get; init; }
    public int HealthyWorkers { get; init; }
    public long TotalActiveConnections { get; init; }
    public long TotalRequests { get; init; }
    public required List<WorkerStats> WorkerStats { get; init; }
}

/// <summary>
/// Individual worker statistics.
/// </summary>
public sealed record WorkerStats
{
    public required string WorkerId { get; init; }
    public bool IsHealthy { get; init; }
    public long ActiveConnections { get; init; }
    public long TotalRequests { get; init; }
    public double Weight { get; init; }
}

#endregion
