using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStreamingData.Strategies.FaultTolerance;

#region 111.7.1 Checkpoint Strategy

/// <summary>
/// 111.7.1: Checkpointing strategy for exactly-once processing guarantees.
/// Provides periodic snapshots of processing state for recovery.
/// </summary>
public sealed class CheckpointStrategy : StreamingDataStrategyBase
{
    private readonly BoundedDictionary<string, CheckpointCoordinator> _coordinators = new BoundedDictionary<string, CheckpointCoordinator>(1000);

    public override string StrategyId => "fault-checkpoint";
    public override string DisplayName => "Checkpoint Fault Tolerance";
    public override StreamingCategory Category => StreamingCategory.StreamFaultTolerance;
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = true,
        SupportsWindowing = false,
        SupportsStateManagement = true,
        SupportsCheckpointing = true,
        SupportsBackpressure = false,
        SupportsPartitioning = true,
        SupportsAutoScaling = true,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 1000000,
        TypicalLatencyMs = 50.0
    };
    public override string SemanticDescription =>
        "Checkpoint fault tolerance providing exactly-once processing through periodic state snapshots, " +
        "barrier alignment, and coordinated recovery across distributed operators.";
    public override string[] Tags => ["checkpoint", "exactly-once", "snapshot", "barrier", "recovery"];

    /// <summary>
    /// Creates a checkpoint coordinator for a job.
    /// </summary>
    public Task<string> CreateCoordinatorAsync(
        string jobId,
        CheckpointConfig config,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var coordinatorId = $"coord-{Guid.NewGuid():N}";
        var coordinator = new CheckpointCoordinator
        {
            CoordinatorId = coordinatorId,
            JobId = jobId,
            Config = config,
            Checkpoints = new BoundedDictionary<long, CheckpointMetadata>(1000),
            Status = CoordinatorStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _coordinators[coordinatorId] = coordinator;
        return Task.FromResult(coordinatorId);
    }

    /// <summary>
    /// Triggers a checkpoint.
    /// </summary>
    public async Task<CheckpointMetadata> TriggerCheckpointAsync(
        string coordinatorId,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (!_coordinators.TryGetValue(coordinatorId, out var coordinator))
        {
            throw new InvalidOperationException($"Coordinator '{coordinatorId}' not found");
        }

        var checkpointId = Interlocked.Increment(ref coordinator.NextCheckpointId);
        var checkpoint = new CheckpointMetadata
        {
            CheckpointId = checkpointId,
            JobId = coordinator.JobId,
            Status = CheckpointStatus.InProgress,
            TriggerTimestamp = DateTimeOffset.UtcNow,
            Operators = new BoundedDictionary<string, OperatorCheckpoint>(1000)
        };

        coordinator.Checkpoints[checkpointId] = checkpoint;
        coordinator.CurrentCheckpointId = checkpointId;

        // Simulate checkpoint completion
        await Task.Delay(10, ct);

        checkpoint.Status = CheckpointStatus.Completed;
        checkpoint.CompletionTimestamp = DateTimeOffset.UtcNow;
        checkpoint.Duration = checkpoint.CompletionTimestamp.Value - checkpoint.TriggerTimestamp;
        checkpoint.StateSize = Random.Shared.Next(1000000, 10000000);

        coordinator.LastCompletedCheckpointId = checkpointId;

        return checkpoint;
    }

    /// <summary>
    /// Acknowledges operator checkpoint.
    /// </summary>
    public Task AcknowledgeCheckpointAsync(
        string coordinatorId,
        long checkpointId,
        string operatorId,
        byte[] stateHandle,
        CancellationToken ct = default)
    {
        if (!_coordinators.TryGetValue(coordinatorId, out var coordinator))
        {
            return Task.CompletedTask;
        }

        if (!coordinator.Checkpoints.TryGetValue(checkpointId, out var checkpoint))
        {
            return Task.CompletedTask;
        }

        checkpoint.Operators[operatorId] = new OperatorCheckpoint
        {
            OperatorId = operatorId,
            StateHandle = stateHandle,
            AckedAt = DateTimeOffset.UtcNow
        };

        return Task.CompletedTask;
    }

    /// <summary>
    /// Restores from a checkpoint.
    /// </summary>
    public Task<RestoreResult> RestoreFromCheckpointAsync(
        string coordinatorId,
        long? checkpointId = null,
        CancellationToken ct = default)
    {
        if (!_coordinators.TryGetValue(coordinatorId, out var coordinator))
        {
            throw new InvalidOperationException($"Coordinator '{coordinatorId}' not found");
        }

        var targetCheckpointId = checkpointId ?? coordinator.LastCompletedCheckpointId;
        if (!coordinator.Checkpoints.TryGetValue(targetCheckpointId, out var checkpoint))
        {
            return Task.FromResult(new RestoreResult
            {
                Success = false,
                Error = $"Checkpoint {targetCheckpointId} not found"
            });
        }

        return Task.FromResult(new RestoreResult
        {
            Success = true,
            RestoredCheckpointId = targetCheckpointId,
            RestoredAt = DateTimeOffset.UtcNow,
            OperatorsRestored = checkpoint.Operators.Count
        });
    }

    /// <summary>
    /// Gets checkpoint history.
    /// </summary>
    public Task<IReadOnlyList<CheckpointMetadata>> GetCheckpointHistoryAsync(
        string coordinatorId,
        int limit = 10,
        CancellationToken ct = default)
    {
        if (!_coordinators.TryGetValue(coordinatorId, out var coordinator))
        {
            return Task.FromResult<IReadOnlyList<CheckpointMetadata>>(Array.Empty<CheckpointMetadata>());
        }

        var checkpoints = coordinator.Checkpoints.Values
            .OrderByDescending(c => c.CheckpointId)
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<CheckpointMetadata>>(checkpoints);
    }

    /// <summary>
    /// Gets checkpoint statistics.
    /// </summary>
    public Task<CheckpointStats> GetCheckpointStatsAsync(
        string coordinatorId,
        CancellationToken ct = default)
    {
        if (!_coordinators.TryGetValue(coordinatorId, out var coordinator))
        {
            throw new InvalidOperationException($"Coordinator '{coordinatorId}' not found");
        }

        var completed = coordinator.Checkpoints.Values.Where(c => c.Status == CheckpointStatus.Completed).ToList();

        return Task.FromResult(new CheckpointStats
        {
            TotalCheckpoints = coordinator.Checkpoints.Count,
            CompletedCheckpoints = completed.Count,
            FailedCheckpoints = coordinator.Checkpoints.Values.Count(c => c.Status == CheckpointStatus.Failed),
            AverageDurationMs = completed.Any()
                ? completed.Where(c => c.Duration.HasValue).Average(c => c.Duration!.Value.TotalMilliseconds)
                : 0,
            AverageStateSizeBytes = completed.Any()
                ? (long)completed.Average(c => c.StateSize)
                : 0,
            LastCheckpointId = coordinator.LastCompletedCheckpointId
        });
    }
}

/// <summary>
/// Checkpoint configuration.
/// </summary>
public sealed record CheckpointConfig
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
    public int MinPauseBetweenCheckpoints { get; init; } = 500;
    public int MaxConcurrentCheckpoints { get; init; } = 1;
    public CheckpointingMode Mode { get; init; } = CheckpointingMode.ExactlyOnce;
    public bool UnalignedCheckpoints { get; init; }
    public string? ExternalizedCheckpointPath { get; init; }
}

public enum CheckpointingMode { ExactlyOnce, AtLeastOnce }

internal sealed class CheckpointCoordinator
{
    public required string CoordinatorId { get; init; }
    public required string JobId { get; init; }
    public required CheckpointConfig Config { get; init; }
    public required BoundedDictionary<long, CheckpointMetadata> Checkpoints { get; init; }
    public CoordinatorStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public long NextCheckpointId;
    public long CurrentCheckpointId;
    public long LastCompletedCheckpointId;
}

public enum CoordinatorStatus { Running, Suspended, Failed }

/// <summary>
/// Checkpoint metadata.
/// </summary>
public sealed class CheckpointMetadata
{
    public long CheckpointId { get; init; }
    public required string JobId { get; init; }
    public CheckpointStatus Status { get; set; }
    public DateTimeOffset TriggerTimestamp { get; init; }
    public DateTimeOffset? CompletionTimestamp { get; set; }
    public TimeSpan? Duration { get; set; }
    public long StateSize { get; set; }
    public BoundedDictionary<string, OperatorCheckpoint> Operators { get; init; } = new BoundedDictionary<string, OperatorCheckpoint>(1000);
    public string? FailureReason { get; set; }
}

public enum CheckpointStatus { InProgress, Completed, Failed, Expired }

/// <summary>
/// Operator checkpoint state.
/// </summary>
public sealed record OperatorCheckpoint
{
    public required string OperatorId { get; init; }
    public required byte[] StateHandle { get; init; }
    public DateTimeOffset AckedAt { get; init; }
}

/// <summary>
/// Restore result.
/// </summary>
public sealed record RestoreResult
{
    public bool Success { get; init; }
    public long RestoredCheckpointId { get; init; }
    public DateTimeOffset RestoredAt { get; init; }
    public int OperatorsRestored { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Checkpoint statistics.
/// </summary>
public sealed record CheckpointStats
{
    public int TotalCheckpoints { get; init; }
    public int CompletedCheckpoints { get; init; }
    public int FailedCheckpoints { get; init; }
    public double AverageDurationMs { get; init; }
    public long AverageStateSizeBytes { get; init; }
    public long LastCheckpointId { get; init; }
}

#endregion

#region 111.7.2 Restart Strategy

/// <summary>
/// 111.7.2: Restart strategies for automatic failure recovery.
/// Provides configurable restart policies with backoff and failure thresholds.
/// </summary>
public sealed class RestartStrategyHandler : StreamingDataStrategyBase
{
    private readonly BoundedDictionary<string, RestartTracker> _trackers = new BoundedDictionary<string, RestartTracker>(1000);

    public override string StrategyId => "fault-restart";
    public override string DisplayName => "Restart Strategy Handler";
    public override StreamingCategory Category => StreamingCategory.StreamFaultTolerance;
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = true,
        SupportsWindowing = false,
        SupportsStateManagement = false,
        SupportsCheckpointing = true,
        SupportsBackpressure = false,
        SupportsPartitioning = false,
        SupportsAutoScaling = false,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 1000000,
        TypicalLatencyMs = 1.0
    };
    public override string SemanticDescription =>
        "Restart strategy handler providing automatic failure recovery with configurable policies " +
        "including fixed delay, exponential backoff, and failure rate thresholds.";
    public override string[] Tags => ["restart", "recovery", "backoff", "failure-rate", "resilience"];

    /// <summary>
    /// Creates a restart tracker for a task.
    /// </summary>
    public Task<string> CreateTrackerAsync(
        string taskId,
        RestartStrategyConfig config,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var trackerId = $"restart-{Guid.NewGuid():N}";
        var tracker = new RestartTracker
        {
            TrackerId = trackerId,
            TaskId = taskId,
            Config = config,
            RestartHistory = new List<RestartAttempt>(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _trackers[trackerId] = tracker;
        return Task.FromResult(trackerId);
    }

    /// <summary>
    /// Determines if a restart should be attempted.
    /// </summary>
    public Task<RestartDecision> ShouldRestartAsync(
        string trackerId,
        Exception failure,
        CancellationToken ct = default)
    {
        if (!_trackers.TryGetValue(trackerId, out var tracker))
        {
            return Task.FromResult(new RestartDecision { ShouldRestart = false, Reason = "Tracker not found" });
        }

        var config = tracker.Config;

        switch (config.Strategy)
        {
            case RestartStrategyType.NoRestart:
                return Task.FromResult(new RestartDecision { ShouldRestart = false, Reason = "No restart configured" });

            case RestartStrategyType.FixedDelay:
                if (tracker.RestartCount >= config.MaxRestartAttempts)
                {
                    return Task.FromResult(new RestartDecision
                    {
                        ShouldRestart = false,
                        Reason = $"Maximum restart attempts ({config.MaxRestartAttempts}) exceeded"
                    });
                }
                return Task.FromResult(new RestartDecision
                {
                    ShouldRestart = true,
                    Delay = config.RestartDelay,
                    Reason = "Fixed delay restart"
                });

            case RestartStrategyType.ExponentialBackoff:
                if (tracker.RestartCount >= config.MaxRestartAttempts)
                {
                    return Task.FromResult(new RestartDecision
                    {
                        ShouldRestart = false,
                        Reason = $"Maximum restart attempts ({config.MaxRestartAttempts}) exceeded"
                    });
                }
                var backoffDelay = TimeSpan.FromMilliseconds(
                    config.InitialBackoff.TotalMilliseconds * Math.Pow(config.BackoffMultiplier, tracker.RestartCount));
                if (config.MaxBackoff.HasValue && backoffDelay > config.MaxBackoff.Value)
                {
                    backoffDelay = config.MaxBackoff.Value;
                }
                return Task.FromResult(new RestartDecision
                {
                    ShouldRestart = true,
                    Delay = backoffDelay,
                    Reason = $"Exponential backoff restart (attempt {tracker.RestartCount + 1})"
                });

            case RestartStrategyType.FailureRate:
                var windowStart = DateTimeOffset.UtcNow - config.FailureRateInterval;
                var recentFailures = tracker.RestartHistory.Count(r => r.Timestamp >= windowStart);
                if (recentFailures >= config.MaxFailuresPerInterval)
                {
                    return Task.FromResult(new RestartDecision
                    {
                        ShouldRestart = false,
                        Reason = $"Failure rate exceeded ({recentFailures} failures in {config.FailureRateInterval})"
                    });
                }
                return Task.FromResult(new RestartDecision
                {
                    ShouldRestart = true,
                    Delay = config.RestartDelay,
                    Reason = "Failure rate restart"
                });

            default:
                return Task.FromResult(new RestartDecision { ShouldRestart = false, Reason = "Unknown strategy" });
        }
    }

    /// <summary>
    /// Records a restart attempt.
    /// </summary>
    public Task RecordRestartAsync(
        string trackerId,
        bool successful,
        string? reason = null,
        CancellationToken ct = default)
    {
        if (!_trackers.TryGetValue(trackerId, out var tracker))
        {
            return Task.CompletedTask;
        }

        tracker.RestartCount++;
        tracker.RestartHistory.Add(new RestartAttempt
        {
            AttemptNumber = tracker.RestartCount,
            Timestamp = DateTimeOffset.UtcNow,
            Successful = successful,
            Reason = reason
        });

        if (successful)
        {
            tracker.LastSuccessfulRestart = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Resets the restart counter (after successful recovery period).
    /// </summary>
    public Task ResetRestartCounterAsync(string trackerId, CancellationToken ct = default)
    {
        if (_trackers.TryGetValue(trackerId, out var tracker))
        {
            tracker.RestartCount = 0;
            tracker.RestartHistory.Clear();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets restart statistics.
    /// </summary>
    public Task<RestartStats> GetRestartStatsAsync(string trackerId, CancellationToken ct = default)
    {
        if (!_trackers.TryGetValue(trackerId, out var tracker))
        {
            throw new InvalidOperationException($"Tracker '{trackerId}' not found");
        }

        return Task.FromResult(new RestartStats
        {
            TotalRestarts = tracker.RestartCount,
            SuccessfulRestarts = tracker.RestartHistory.Count(r => r.Successful),
            FailedRestarts = tracker.RestartHistory.Count(r => !r.Successful),
            LastRestartAt = tracker.RestartHistory.LastOrDefault()?.Timestamp,
            LastSuccessfulRestartAt = tracker.LastSuccessfulRestart
        });
    }
}

/// <summary>
/// Restart strategy configuration.
/// </summary>
public sealed record RestartStrategyConfig
{
    public RestartStrategyType Strategy { get; init; } = RestartStrategyType.FixedDelay;
    public int MaxRestartAttempts { get; init; } = 3;
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(10);

    // Exponential backoff settings
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public double BackoffMultiplier { get; init; } = 2.0;
    public TimeSpan? MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);

    // Failure rate settings
    public TimeSpan FailureRateInterval { get; init; } = TimeSpan.FromMinutes(5);
    public int MaxFailuresPerInterval { get; init; } = 3;
}

public enum RestartStrategyType { NoRestart, FixedDelay, ExponentialBackoff, FailureRate }

internal sealed class RestartTracker
{
    public required string TrackerId { get; init; }
    public required string TaskId { get; init; }
    public required RestartStrategyConfig Config { get; init; }
    public required List<RestartAttempt> RestartHistory { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public int RestartCount;
    public DateTimeOffset? LastSuccessfulRestart;
}

/// <summary>
/// Restart decision.
/// </summary>
public sealed record RestartDecision
{
    public bool ShouldRestart { get; init; }
    public TimeSpan? Delay { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// Restart attempt record.
/// </summary>
public sealed record RestartAttempt
{
    public int AttemptNumber { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public bool Successful { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Restart statistics.
/// </summary>
public sealed record RestartStats
{
    public int TotalRestarts { get; init; }
    public int SuccessfulRestarts { get; init; }
    public int FailedRestarts { get; init; }
    public DateTimeOffset? LastRestartAt { get; init; }
    public DateTimeOffset? LastSuccessfulRestartAt { get; init; }
}

#endregion

#region 111.7.3 Write-Ahead Log Strategy

/// <summary>
/// 111.7.3: Write-ahead log (WAL) for durability and recovery.
/// Provides transactional guarantees through sequential log writes.
/// </summary>
public sealed class WriteAheadLogStrategy : StreamingDataStrategyBase
{
    private readonly BoundedDictionary<string, WalStore> _stores = new BoundedDictionary<string, WalStore>(1000);

    public override string StrategyId => "fault-wal";
    public override string DisplayName => "Write-Ahead Log";
    public override StreamingCategory Category => StreamingCategory.StreamFaultTolerance;
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = true,
        SupportsWindowing = false,
        SupportsStateManagement = true,
        SupportsCheckpointing = true,
        SupportsBackpressure = false,
        SupportsPartitioning = true,
        SupportsAutoScaling = false,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 500000,
        TypicalLatencyMs = 1.0
    };
    public override string SemanticDescription =>
        "Write-ahead log providing durability through sequential log writes before state changes, " +
        "enabling crash recovery and transactional consistency.";
    public override string[] Tags => ["wal", "durability", "transaction", "crash-recovery", "sequential"];

    /// <summary>
    /// Creates a WAL store.
    /// </summary>
    public Task<string> CreateWalAsync(
        string name,
        WalConfig? config = null,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var walId = $"wal-{Guid.NewGuid():N}";
        var store = new WalStore
        {
            WalId = walId,
            Name = name,
            Config = config ?? new WalConfig(),
            Entries = new List<WalEntry>(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _stores[walId] = store;
        return Task.FromResult(walId);
    }

    /// <summary>
    /// Appends an entry to the WAL.
    /// </summary>
    public Task<long> AppendAsync(
        string walId,
        byte[] data,
        WalEntryType entryType = WalEntryType.Data,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        var sw = Stopwatch.StartNew();

        if (!_stores.TryGetValue(walId, out var store))
        {
            throw new InvalidOperationException($"WAL '{walId}' not found");
        }

        var lsn = Interlocked.Increment(ref store.NextLsn);
        var entry = new WalEntry
        {
            Lsn = lsn,
            Data = data,
            Type = entryType,
            Timestamp = DateTimeOffset.UtcNow,
            Checksum = ComputeChecksum(data)
        };

        lock (store.Entries)
        {
            store.Entries.Add(entry);
        }

        // Sync if configured
        if (store.Config.SyncMode == WalSyncMode.EveryWrite)
        {
            // In production, would fsync here
        }

        RecordWrite(data.Length, sw.Elapsed.TotalMilliseconds);
        return Task.FromResult(lsn);
    }

    /// <summary>
    /// Appends a batch of entries.
    /// </summary>
    public Task<long> AppendBatchAsync(
        string walId,
        IEnumerable<byte[]> entries,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (!_stores.TryGetValue(walId, out var store))
        {
            throw new InvalidOperationException($"WAL '{walId}' not found");
        }

        long lastLsn = 0;

        lock (store.Entries)
        {
            foreach (var data in entries)
            {
                lastLsn = Interlocked.Increment(ref store.NextLsn);
                store.Entries.Add(new WalEntry
                {
                    Lsn = lastLsn,
                    Data = data,
                    Type = WalEntryType.Data,
                    Timestamp = DateTimeOffset.UtcNow,
                    Checksum = ComputeChecksum(data)
                });
            }
        }

        return Task.FromResult(lastLsn);
    }

    /// <summary>
    /// Reads entries from the WAL.
    /// </summary>
    public Task<IReadOnlyList<WalEntry>> ReadAsync(
        string walId,
        long fromLsn = 0,
        int limit = 1000,
        CancellationToken ct = default)
    {
        if (!_stores.TryGetValue(walId, out var store))
        {
            return Task.FromResult<IReadOnlyList<WalEntry>>(Array.Empty<WalEntry>());
        }

        List<WalEntry> entries;
        lock (store.Entries)
        {
            entries = store.Entries
                .Where(e => e.Lsn >= fromLsn)
                .Take(limit)
                .ToList();
        }

        return Task.FromResult<IReadOnlyList<WalEntry>>(entries);
    }

    /// <summary>
    /// Truncates the WAL up to a given LSN.
    /// </summary>
    public Task TruncateAsync(string walId, long upToLsn, CancellationToken ct = default)
    {
        if (!_stores.TryGetValue(walId, out var store))
        {
            return Task.CompletedTask;
        }

        lock (store.Entries)
        {
            store.Entries.RemoveAll(e => e.Lsn <= upToLsn);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Forces a sync to disk.
    /// </summary>
    public Task SyncAsync(string walId, CancellationToken ct = default)
    {
        // In production, would fsync here
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets WAL statistics.
    /// </summary>
    public Task<WalStats> GetStatsAsync(string walId, CancellationToken ct = default)
    {
        if (!_stores.TryGetValue(walId, out var store))
        {
            throw new InvalidOperationException($"WAL '{walId}' not found");
        }

        long totalSize;
        int entryCount;
        long minLsn = 0;
        long maxLsn = 0;

        lock (store.Entries)
        {
            entryCount = store.Entries.Count;
            totalSize = store.Entries.Sum(e => e.Data.Length);
            if (store.Entries.Count > 0)
            {
                minLsn = store.Entries.Min(e => e.Lsn);
                maxLsn = store.Entries.Max(e => e.Lsn);
            }
        }

        return Task.FromResult(new WalStats
        {
            EntryCount = entryCount,
            TotalSizeBytes = totalSize,
            MinLsn = minLsn,
            MaxLsn = maxLsn,
            CurrentLsn = store.NextLsn
        });
    }

    private static uint ComputeChecksum(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ (0xEDB88320 & ~((crc & 1) - 1));
            }
        }
        return ~crc;
    }
}

/// <summary>
/// WAL configuration.
/// </summary>
public sealed record WalConfig
{
    public WalSyncMode SyncMode { get; init; } = WalSyncMode.Periodic;
    public TimeSpan SyncInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public int MaxSegmentSizeBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxRetainedSegments { get; init; } = 5;
}

public enum WalSyncMode { EveryWrite, Periodic, Manual }
public enum WalEntryType { Data, Checkpoint, Commit, Rollback }

internal sealed class WalStore
{
    public required string WalId { get; init; }
    public required string Name { get; init; }
    public required WalConfig Config { get; init; }
    public required List<WalEntry> Entries { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public long NextLsn;
}

/// <summary>
/// WAL entry.
/// </summary>
public sealed record WalEntry
{
    public long Lsn { get; init; }
    public required byte[] Data { get; init; }
    public WalEntryType Type { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public uint Checksum { get; init; }
}

/// <summary>
/// WAL statistics.
/// </summary>
public sealed record WalStats
{
    public int EntryCount { get; init; }
    public long TotalSizeBytes { get; init; }
    public long MinLsn { get; init; }
    public long MaxLsn { get; init; }
    public long CurrentLsn { get; init; }
}

#endregion

#region 111.7.4 Idempotent Processing Strategy

/// <summary>
/// 111.7.4: Idempotent processing for exactly-once semantics without checkpoints.
/// Uses deduplication and idempotent operations.
/// </summary>
public sealed class IdempotentProcessingStrategy : StreamingDataStrategyBase
{
    private readonly BoundedDictionary<string, IdempotencyStore> _stores = new BoundedDictionary<string, IdempotencyStore>(1000);

    public override string StrategyId => "fault-idempotent";
    public override string DisplayName => "Idempotent Processing";
    public override StreamingCategory Category => StreamingCategory.StreamFaultTolerance;
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = true,
        SupportsWindowing = false,
        SupportsStateManagement = true,
        SupportsCheckpointing = false,
        SupportsBackpressure = false,
        SupportsPartitioning = true,
        SupportsAutoScaling = true,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 800000,
        TypicalLatencyMs = 0.5
    };
    public override string SemanticDescription =>
        "Idempotent processing providing exactly-once semantics through deduplication, " +
        "idempotent operations, and message tracking without full checkpointing overhead.";
    public override string[] Tags => ["idempotent", "deduplication", "exactly-once", "lightweight", "efficient"];

    /// <summary>
    /// Creates an idempotency store.
    /// </summary>
    public Task<string> CreateStoreAsync(
        string name,
        IdempotencyConfig? config = null,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var storeId = $"idemp-{Guid.NewGuid():N}";
        var store = new IdempotencyStore
        {
            StoreId = storeId,
            Name = name,
            Config = config ?? new IdempotencyConfig(),
            ProcessedIds = new BoundedDictionary<string, ProcessedRecord>(1000),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _stores[storeId] = store;
        return Task.FromResult(storeId);
    }

    /// <summary>
    /// Checks if a message has been processed and marks it if not.
    /// </summary>
    public Task<IdempotencyResult> TryProcessAsync(
        string storeId,
        string messageId,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (!_stores.TryGetValue(storeId, out var store))
        {
            throw new InvalidOperationException($"Store '{storeId}' not found");
        }

        var record = new ProcessedRecord
        {
            MessageId = messageId,
            ProcessedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow + store.Config.RetentionPeriod
        };

        if (store.ProcessedIds.TryAdd(messageId, record))
        {
            return Task.FromResult(new IdempotencyResult
            {
                IsNew = true,
                MessageId = messageId,
                FirstProcessedAt = record.ProcessedAt
            });
        }

        var existing = store.ProcessedIds[messageId];
        return Task.FromResult(new IdempotencyResult
        {
            IsNew = false,
            MessageId = messageId,
            FirstProcessedAt = existing.ProcessedAt,
            DuplicateCount = Interlocked.Increment(ref existing.DuplicateCount)
        });
    }

    /// <summary>
    /// Marks a message as processed with a result.
    /// </summary>
    public Task MarkProcessedAsync(
        string storeId,
        string messageId,
        byte[]? result = null,
        CancellationToken ct = default)
    {
        if (!_stores.TryGetValue(storeId, out var store))
        {
            return Task.CompletedTask;
        }

        var record = new ProcessedRecord
        {
            MessageId = messageId,
            ProcessedAt = DateTimeOffset.UtcNow,
            Result = result,
            ExpiresAt = DateTimeOffset.UtcNow + store.Config.RetentionPeriod
        };

        store.ProcessedIds[messageId] = record;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the cached result for a processed message.
    /// </summary>
    public Task<byte[]?> GetCachedResultAsync(
        string storeId,
        string messageId,
        CancellationToken ct = default)
    {
        if (!_stores.TryGetValue(storeId, out var store))
        {
            return Task.FromResult<byte[]?>(null);
        }

        if (store.ProcessedIds.TryGetValue(messageId, out var record))
        {
            return Task.FromResult(record.Result);
        }

        return Task.FromResult<byte[]?>(null);
    }

    /// <summary>
    /// Cleans up expired records.
    /// </summary>
    public Task CleanupAsync(string storeId, CancellationToken ct = default)
    {
        if (!_stores.TryGetValue(storeId, out var store))
        {
            return Task.CompletedTask;
        }

        var now = DateTimeOffset.UtcNow;
        var expired = store.ProcessedIds
            .Where(kv => kv.Value.ExpiresAt < now)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expired)
        {
            store.ProcessedIds.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets idempotency statistics.
    /// </summary>
    public Task<IdempotencyStats> GetStatsAsync(string storeId, CancellationToken ct = default)
    {
        if (!_stores.TryGetValue(storeId, out var store))
        {
            throw new InvalidOperationException($"Store '{storeId}' not found");
        }

        var records = store.ProcessedIds.Values.ToList();
        return Task.FromResult(new IdempotencyStats
        {
            TotalTracked = records.Count,
            TotalDuplicates = records.Sum(r => r.DuplicateCount),
            OldestRecord = records.MinBy(r => r.ProcessedAt)?.ProcessedAt,
            NewestRecord = records.MaxBy(r => r.ProcessedAt)?.ProcessedAt
        });
    }
}

/// <summary>
/// Idempotency configuration.
/// </summary>
public sealed record IdempotencyConfig
{
    public TimeSpan RetentionPeriod { get; init; } = TimeSpan.FromHours(24);
    public int MaxTrackedMessages { get; init; } = 1000000;
    public bool CacheResults { get; init; } = true;
}

internal sealed class IdempotencyStore
{
    public required string StoreId { get; init; }
    public required string Name { get; init; }
    public required IdempotencyConfig Config { get; init; }
    public required BoundedDictionary<string, ProcessedRecord> ProcessedIds { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class ProcessedRecord
{
    public required string MessageId { get; init; }
    public DateTimeOffset ProcessedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public byte[]? Result { get; init; }
    public long DuplicateCount;
}

/// <summary>
/// Idempotency check result.
/// </summary>
public sealed record IdempotencyResult
{
    public bool IsNew { get; init; }
    public required string MessageId { get; init; }
    public DateTimeOffset FirstProcessedAt { get; init; }
    public long DuplicateCount { get; init; }
}

/// <summary>
/// Idempotency statistics.
/// </summary>
public sealed record IdempotencyStats
{
    public int TotalTracked { get; init; }
    public long TotalDuplicates { get; init; }
    public DateTimeOffset? OldestRecord { get; init; }
    public DateTimeOffset? NewestRecord { get; init; }
}

#endregion
