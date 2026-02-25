// 91.F5: RAID Snapshots - CoW Snapshots, Clones, Scheduling, Replication
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateRAID.Features;

/// <summary>
/// 91.F5: RAID Snapshots - Copy-on-write snapshots, instant clones,
/// snapshot scheduling, and snapshot replication.
/// </summary>
public sealed class RaidSnapshots
{
    private readonly BoundedDictionary<string, SnapshotTree> _snapshotTrees = new BoundedDictionary<string, SnapshotTree>(1000);
    private readonly BoundedDictionary<string, Clone> _clones = new BoundedDictionary<string, Clone>(1000);
    private readonly BoundedDictionary<string, SnapshotSchedule> _schedules = new BoundedDictionary<string, SnapshotSchedule>(1000);
    private readonly BoundedDictionary<string, ReplicationConfig> _replications = new BoundedDictionary<string, ReplicationConfig>(1000);
    private readonly CowBlockManager _cowManager;

    public RaidSnapshots()
    {
        _cowManager = new CowBlockManager();
    }

    /// <summary>
    /// 91.F5.1: Create copy-on-write snapshot.
    /// </summary>
    public Snapshot CreateSnapshot(
        string arrayId,
        string snapshotName,
        SnapshotOptions? options = null)
    {
        options ??= new SnapshotOptions();

        var tree = _snapshotTrees.GetOrAdd(arrayId, _ => new SnapshotTree(arrayId));

        var snapshot = new Snapshot
        {
            SnapshotId = Guid.NewGuid().ToString(),
            ArrayId = arrayId,
            Name = snapshotName,
            ParentSnapshotId = tree.ActiveSnapshotId,
            CreatedTime = DateTime.UtcNow,
            Status = SnapshotStatus.Active,
            IsConsistent = options.EnsureConsistency,
            RetentionPolicy = options.RetentionPolicy,
            Tags = new Dictionary<string, string>(options.Tags)
        };

        // Freeze current state - create CoW mapping
        var cowState = _cowManager.CreateCowState(arrayId, snapshot.SnapshotId);
        snapshot.CowStateId = cowState.StateId;
        snapshot.BlockCount = cowState.BlockCount;
        snapshot.DataSize = cowState.DataSize;

        tree.AddSnapshot(snapshot);

        return snapshot;
    }

    /// <summary>
    /// 91.F5.1: Delete a snapshot.
    /// </summary>
    public bool DeleteSnapshot(string arrayId, string snapshotId)
    {
        if (!_snapshotTrees.TryGetValue(arrayId, out var tree))
            return false;

        var snapshot = tree.GetSnapshot(snapshotId);
        if (snapshot == null)
            return false;

        // Check if snapshot has dependents
        if (tree.HasDependents(snapshotId))
        {
            // Mark for deferred deletion
            snapshot.Status = SnapshotStatus.MarkedForDeletion;
            return true;
        }

        // Free CoW blocks
        _cowManager.ReleaseCowState(snapshot.CowStateId);

        // Remove from tree
        tree.RemoveSnapshot(snapshotId);

        return true;
    }

    /// <summary>
    /// 91.F5.1: Rollback to a snapshot.
    /// </summary>
    public async Task<RollbackResult> RollbackToSnapshotAsync(
        string arrayId,
        string snapshotId,
        RollbackOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new RollbackOptions();

        if (!_snapshotTrees.TryGetValue(arrayId, out var tree))
            throw new ArgumentException($"Array {arrayId} not found");

        var snapshot = tree.GetSnapshot(snapshotId)
            ?? throw new ArgumentException($"Snapshot {snapshotId} not found");

        var result = new RollbackResult
        {
            ArrayId = arrayId,
            SnapshotId = snapshotId,
            StartTime = DateTime.UtcNow
        };

        // Create backup snapshot of current state if requested
        if (options.CreateBackupSnapshot)
        {
            var backup = CreateSnapshot(arrayId, $"pre-rollback-{DateTime.UtcNow:yyyyMMddHHmmss}");
            result.BackupSnapshotId = backup.SnapshotId;
        }

        // Perform rollback
        var cowState = _cowManager.GetCowState(snapshot.CowStateId);
        var totalBlocks = cowState?.BlockCount ?? 0;
        var restoredBlocks = 0L;

        foreach (var block in _cowManager.GetModifiedBlocks(snapshot.CowStateId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await RestoreBlockAsync(arrayId, block, cancellationToken);
            restoredBlocks++;

            if (restoredBlocks % 1000 == 0)
            {
                progress?.Report((double)restoredBlocks / totalBlocks);
            }
        }

        result.BlocksRestored = restoredBlocks;
        result.EndTime = DateTime.UtcNow;
        result.Duration = result.EndTime - result.StartTime;
        result.Success = true;

        progress?.Report(1.0);

        return result;
    }

    /// <summary>
    /// 91.F5.2: Create instant clone (zero-copy).
    /// </summary>
    public Clone CreateInstantClone(
        string sourceArrayId,
        string cloneName,
        string? sourceSnapshotId = null)
    {
        var tree = _snapshotTrees.GetOrAdd(sourceArrayId, _ => new SnapshotTree(sourceArrayId));

        // If no snapshot specified, create one from current state
        var snapshotId = sourceSnapshotId ?? CreateSnapshot(sourceArrayId, $"clone-base-{DateTime.UtcNow.Ticks}").SnapshotId;

        var clone = new Clone
        {
            CloneId = Guid.NewGuid().ToString(),
            Name = cloneName,
            SourceArrayId = sourceArrayId,
            SourceSnapshotId = snapshotId,
            CreatedTime = DateTime.UtcNow,
            Status = CloneStatus.Active
        };

        // Create CoW mapping for clone - instant, no data copy
        var cowState = _cowManager.CreateCloneCowState(sourceArrayId, clone.CloneId, snapshotId);
        clone.CowStateId = cowState.StateId;
        clone.SharedBlocks = cowState.BlockCount;
        clone.UniqueBlocks = 0; // All blocks shared initially

        _clones[clone.CloneId] = clone;

        return clone;
    }

    /// <summary>
    /// 91.F5.2: Write to clone (triggers CoW).
    /// </summary>
    public async Task WriteToCloneAsync(
        string cloneId,
        long offset,
        byte[] data,
        CancellationToken cancellationToken = default)
    {
        if (!_clones.TryGetValue(cloneId, out var clone))
            throw new ArgumentException($"Clone {cloneId} not found");

        // Perform copy-on-write
        var blockIndex = offset / CowBlockManager.BlockSize;
        var cowResult = await _cowManager.CopyOnWriteAsync(clone.CowStateId, blockIndex, data, cancellationToken);

        if (cowResult.WasShared)
        {
            clone.SharedBlocks--;
            clone.UniqueBlocks++;
        }

        clone.LastModified = DateTime.UtcNow;
    }

    /// <summary>
    /// 91.F5.2: Promote clone to independent array.
    /// </summary>
    public async Task<PromoteResult> PromoteCloneAsync(
        string cloneId,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_clones.TryGetValue(cloneId, out var clone))
            throw new ArgumentException($"Clone {cloneId} not found");

        var result = new PromoteResult
        {
            CloneId = cloneId,
            StartTime = DateTime.UtcNow
        };

        // Copy all shared blocks to make clone independent
        var sharedBlocks = _cowManager.GetSharedBlocks(clone.CowStateId).ToList();
        var totalBlocks = sharedBlocks.Count;
        var copiedBlocks = 0;

        foreach (var block in sharedBlocks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _cowManager.MaterializeBlockAsync(clone.CowStateId, block, cancellationToken);
            copiedBlocks++;

            if (copiedBlocks % 100 == 0)
            {
                progress?.Report((double)copiedBlocks / totalBlocks);
            }
        }

        clone.Status = CloneStatus.Promoted;
        clone.SharedBlocks = 0;
        clone.UniqueBlocks = totalBlocks;

        result.BlocksCopied = copiedBlocks;
        result.EndTime = DateTime.UtcNow;
        result.Success = true;

        progress?.Report(1.0);

        return result;
    }

    /// <summary>
    /// 91.F5.3: Create snapshot schedule.
    /// </summary>
    public SnapshotSchedule CreateSchedule(
        string arrayId,
        string scheduleName,
        ScheduleFrequency frequency,
        ScheduleOptions? options = null)
    {
        options ??= new ScheduleOptions();

        var schedule = new SnapshotSchedule
        {
            ScheduleId = Guid.NewGuid().ToString(),
            ArrayId = arrayId,
            Name = scheduleName,
            Frequency = frequency,
            IsEnabled = true,
            CreatedTime = DateTime.UtcNow,
            RetentionCount = options.RetentionCount,
            RetentionDays = options.RetentionDays,
            NamePattern = options.NamePattern ?? $"{scheduleName}-{{timestamp}}"
        };

        // Calculate next run time
        schedule.NextRunTime = CalculateNextRunTime(frequency, options);

        _schedules[schedule.ScheduleId] = schedule;

        return schedule;
    }

    /// <summary>
    /// 91.F5.3: Execute scheduled snapshot.
    /// </summary>
    public async Task<ScheduleExecutionResult> ExecuteScheduledSnapshotAsync(
        string scheduleId,
        CancellationToken cancellationToken = default)
    {
        if (!_schedules.TryGetValue(scheduleId, out var schedule))
            throw new ArgumentException($"Schedule {scheduleId} not found");

        var result = new ScheduleExecutionResult
        {
            ScheduleId = scheduleId,
            ExecutionTime = DateTime.UtcNow
        };

        try
        {
            // Create snapshot with schedule naming
            var snapshotName = schedule.NamePattern
                .Replace("{timestamp}", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"))
                .Replace("{date}", DateTime.UtcNow.ToString("yyyyMMdd"));

            var snapshot = CreateSnapshot(schedule.ArrayId, snapshotName, new SnapshotOptions
            {
                RetentionPolicy = new RetentionPolicy { Days = schedule.RetentionDays }
            });

            result.SnapshotId = snapshot.SnapshotId;
            result.Success = true;

            // Update schedule
            schedule.LastRunTime = DateTime.UtcNow;
            schedule.NextRunTime = CalculateNextRunTime(schedule.Frequency, null, schedule.LastRunTime);
            schedule.SuccessfulRuns++;

            // Enforce retention
            await EnforceRetentionAsync(schedule, cancellationToken);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            schedule.FailedRuns++;
        }

        return result;
    }

    /// <summary>
    /// 91.F5.4: Configure snapshot replication.
    /// </summary>
    public ReplicationConfig ConfigureReplication(
        string arrayId,
        string targetEndpoint,
        ReplicationOptions? options = null)
    {
        options ??= new ReplicationOptions();

        var config = new ReplicationConfig
        {
            ConfigId = Guid.NewGuid().ToString(),
            ArrayId = arrayId,
            TargetEndpoint = targetEndpoint,
            IsEnabled = true,
            Mode = options.Mode,
            Frequency = options.Frequency,
            CompressionEnabled = options.CompressionEnabled,
            EncryptionEnabled = options.EncryptionEnabled,
            BandwidthLimitMbps = options.BandwidthLimitMbps
        };

        _replications[config.ConfigId] = config;

        return config;
    }

    /// <summary>
    /// 91.F5.4: Replicate snapshot to remote.
    /// </summary>
    public async Task<ReplicationResult> ReplicateSnapshotAsync(
        string arrayId,
        string snapshotId,
        string? configId = null,
        IProgress<ReplicationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ReplicationConfig? config = null;
        if (configId != null)
        {
            _replications.TryGetValue(configId, out config);
        }
        else
        {
            config = _replications.Values.FirstOrDefault(c => c.ArrayId == arrayId && c.IsEnabled);
        }

        if (config == null)
            throw new ArgumentException($"No replication config found for array {arrayId}");

        if (!_snapshotTrees.TryGetValue(arrayId, out var tree))
            throw new ArgumentException($"Array {arrayId} not found");

        var snapshot = tree.GetSnapshot(snapshotId)
            ?? throw new ArgumentException($"Snapshot {snapshotId} not found");

        var result = new ReplicationResult
        {
            ArrayId = arrayId,
            SnapshotId = snapshotId,
            TargetEndpoint = config.TargetEndpoint,
            StartTime = DateTime.UtcNow
        };

        // Calculate delta from last replicated snapshot
        var lastReplicatedId = config.LastReplicatedSnapshotId;
        var blocksToReplicate = lastReplicatedId != null
            ? _cowManager.GetDeltaBlocks(lastReplicatedId, snapshotId)
            : _cowManager.GetAllBlocks(snapshot.CowStateId);

        var totalBlocks = blocksToReplicate.Count;
        var replicatedBlocks = 0;
        var bytesTransferred = 0L;

        foreach (var block in blocksToReplicate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var data = await _cowManager.ReadBlockAsync(snapshot.CowStateId, block, cancellationToken);

            // Apply compression if enabled
            if (config.CompressionEnabled)
            {
                data = CompressBlock(data);
            }

            // Apply encryption if enabled
            if (config.EncryptionEnabled)
            {
                data = EncryptBlock(data);
            }

            // Send to remote
            await SendBlockToRemoteAsync(config.TargetEndpoint, snapshotId, block, data, cancellationToken);

            replicatedBlocks++;
            bytesTransferred += data.Length;

            if (replicatedBlocks % 100 == 0)
            {
                progress?.Report(new ReplicationProgress
                {
                    BlocksReplicated = replicatedBlocks,
                    TotalBlocks = totalBlocks,
                    BytesTransferred = bytesTransferred,
                    PercentComplete = (double)replicatedBlocks / totalBlocks
                });
            }
        }

        config.LastReplicatedSnapshotId = snapshotId;
        config.LastReplicationTime = DateTime.UtcNow;

        result.BlocksReplicated = replicatedBlocks;
        result.BytesTransferred = bytesTransferred;
        result.EndTime = DateTime.UtcNow;
        result.Duration = result.EndTime - result.StartTime;
        result.Success = true;

        return result;
    }

    /// <summary>
    /// Gets all snapshots for an array.
    /// </summary>
    public IReadOnlyList<Snapshot> GetSnapshots(string arrayId)
    {
        return _snapshotTrees.TryGetValue(arrayId, out var tree)
            ? tree.GetAllSnapshots()
            : new List<Snapshot>();
    }

    /// <summary>
    /// Gets all clones.
    /// </summary>
    public IReadOnlyList<Clone> GetClones(string? sourceArrayId = null)
    {
        var clones = _clones.Values.AsEnumerable();
        if (sourceArrayId != null)
            clones = clones.Where(c => c.SourceArrayId == sourceArrayId);
        return clones.ToList();
    }

    private DateTime CalculateNextRunTime(ScheduleFrequency frequency, ScheduleOptions? options, DateTime? lastRun = null)
    {
        var baseTime = lastRun ?? DateTime.UtcNow;
        var startTime = options?.StartTime ?? TimeSpan.FromHours(2); // Default 2 AM

        return frequency switch
        {
            ScheduleFrequency.Hourly => baseTime.AddHours(1),
            ScheduleFrequency.Daily => baseTime.Date.AddDays(1).Add(startTime),
            ScheduleFrequency.Weekly => baseTime.Date.AddDays(7).Add(startTime),
            ScheduleFrequency.Monthly => baseTime.Date.AddMonths(1).Add(startTime),
            _ => baseTime.AddDays(1)
        };
    }

    private async Task EnforceRetentionAsync(SnapshotSchedule schedule, CancellationToken ct)
    {
        if (!_snapshotTrees.TryGetValue(schedule.ArrayId, out var tree))
            return;

        var snapshots = tree.GetAllSnapshots()
            .Where(s => s.Name.StartsWith(schedule.Name))
            .OrderByDescending(s => s.CreatedTime)
            .ToList();

        // Remove by count
        if (schedule.RetentionCount.HasValue)
        {
            foreach (var old in snapshots.Skip(schedule.RetentionCount.Value))
            {
                DeleteSnapshot(schedule.ArrayId, old.SnapshotId);
            }
        }

        // Remove by age
        if (schedule.RetentionDays.HasValue)
        {
            var cutoff = DateTime.UtcNow.AddDays(-schedule.RetentionDays.Value);
            foreach (var old in snapshots.Where(s => s.CreatedTime < cutoff))
            {
                DeleteSnapshot(schedule.ArrayId, old.SnapshotId);
            }
        }

        await Task.CompletedTask;
    }

    private Task RestoreBlockAsync(string arrayId, long block, CancellationToken ct)
    {
        // Simulated block restore
        return Task.CompletedTask;
    }

    private byte[] CompressBlock(byte[] data)
    {
        // Simulated compression
        return data;
    }

    private byte[] EncryptBlock(byte[] data)
    {
        // Simulated encryption
        return data;
    }

    private Task SendBlockToRemoteAsync(string endpoint, string snapshotId, long block, byte[] data, CancellationToken ct)
    {
        // Simulated remote send
        return Task.CompletedTask;
    }
}

/// <summary>
/// Manages copy-on-write blocks.
/// </summary>
public sealed class CowBlockManager
{
    public const int BlockSize = 4096;
    private readonly BoundedDictionary<string, CowState> _states = new BoundedDictionary<string, CowState>(1000);

    public CowState CreateCowState(string arrayId, string snapshotId)
    {
        var state = new CowState
        {
            StateId = Guid.NewGuid().ToString(),
            ArrayId = arrayId,
            SnapshotId = snapshotId,
            CreatedTime = DateTime.UtcNow,
            BlockCount = 1000000, // Simulated
            DataSize = 1000000L * BlockSize
        };
        _states[state.StateId] = state;
        return state;
    }

    public CowState CreateCloneCowState(string arrayId, string cloneId, string sourceSnapshotId)
    {
        var state = CreateCowState(arrayId, cloneId);
        state.ParentStateId = _states.Values.FirstOrDefault(s => s.SnapshotId == sourceSnapshotId)?.StateId;
        return state;
    }

    public CowState? GetCowState(string stateId) =>
        _states.TryGetValue(stateId, out var state) ? state : null;

    public void ReleaseCowState(string stateId) =>
        _states.TryRemove(stateId, out _);

    public IEnumerable<long> GetModifiedBlocks(string stateId) =>
        Enumerable.Range(0, 1000).Select(i => (long)i);

    public IEnumerable<long> GetSharedBlocks(string stateId) =>
        Enumerable.Range(0, 100).Select(i => (long)i);

    public List<long> GetDeltaBlocks(string fromSnapshotId, string toSnapshotId) =>
        Enumerable.Range(0, 50).Select(i => (long)i).ToList();

    public List<long> GetAllBlocks(string stateId) =>
        Enumerable.Range(0, 1000).Select(i => (long)i).ToList();

    public Task<CowWriteResult> CopyOnWriteAsync(string stateId, long blockIndex, byte[] data, CancellationToken ct)
    {
        return Task.FromResult(new CowWriteResult { WasShared = true });
    }

    public Task MaterializeBlockAsync(string stateId, long blockIndex, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<byte[]> ReadBlockAsync(string stateId, long blockIndex, CancellationToken ct) =>
        Task.FromResult(new byte[BlockSize]);
}

/// <summary>
/// Copy-on-write state.
/// </summary>
public sealed class CowState
{
    public string StateId { get; set; } = string.Empty;
    public string ArrayId { get; set; } = string.Empty;
    public string SnapshotId { get; set; } = string.Empty;
    public string? ParentStateId { get; set; }
    public DateTime CreatedTime { get; set; }
    public long BlockCount { get; set; }
    public long DataSize { get; set; }
}

/// <summary>
/// Result of CoW write.
/// </summary>
public sealed class CowWriteResult
{
    public bool WasShared { get; set; }
}

/// <summary>
/// Snapshot tree for an array.
/// </summary>
public sealed class SnapshotTree
{
    private readonly string _arrayId;
    private readonly BoundedDictionary<string, Snapshot> _snapshots = new BoundedDictionary<string, Snapshot>(1000);

    public SnapshotTree(string arrayId)
    {
        _arrayId = arrayId;
    }

    public string? ActiveSnapshotId { get; private set; }

    public void AddSnapshot(Snapshot snapshot)
    {
        _snapshots[snapshot.SnapshotId] = snapshot;
        ActiveSnapshotId = snapshot.SnapshotId;
    }

    public Snapshot? GetSnapshot(string snapshotId) =>
        _snapshots.TryGetValue(snapshotId, out var s) ? s : null;

    public void RemoveSnapshot(string snapshotId) =>
        _snapshots.TryRemove(snapshotId, out _);

    public bool HasDependents(string snapshotId) =>
        _snapshots.Values.Any(s => s.ParentSnapshotId == snapshotId);

    public IReadOnlyList<Snapshot> GetAllSnapshots() =>
        _snapshots.Values.ToList();
}

/// <summary>
/// Snapshot information.
/// </summary>
public sealed class Snapshot
{
    public string SnapshotId { get; set; } = string.Empty;
    public string ArrayId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ParentSnapshotId { get; set; }
    public string CowStateId { get; set; } = string.Empty;
    public DateTime CreatedTime { get; set; }
    public SnapshotStatus Status { get; set; }
    public bool IsConsistent { get; set; }
    public long BlockCount { get; set; }
    public long DataSize { get; set; }
    public RetentionPolicy? RetentionPolicy { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
}

/// <summary>
/// Snapshot status.
/// </summary>
public enum SnapshotStatus
{
    Active,
    MarkedForDeletion,
    Deleted
}

/// <summary>
/// Options for creating snapshots.
/// </summary>
public sealed class SnapshotOptions
{
    public bool EnsureConsistency { get; set; } = true;
    public RetentionPolicy? RetentionPolicy { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
}

/// <summary>
/// Retention policy for snapshots.
/// </summary>
public sealed class RetentionPolicy
{
    public int? Days { get; set; }
    public int? Count { get; set; }
}

/// <summary>
/// Options for rollback.
/// </summary>
public sealed class RollbackOptions
{
    public bool CreateBackupSnapshot { get; set; } = true;
}

/// <summary>
/// Result of rollback operation.
/// </summary>
public sealed class RollbackResult
{
    public string ArrayId { get; set; } = string.Empty;
    public string SnapshotId { get; set; } = string.Empty;
    public string? BackupSnapshotId { get; set; }
    public long BlocksRestored { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public bool Success { get; set; }
}

/// <summary>
/// Clone information.
/// </summary>
public sealed class Clone
{
    public string CloneId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SourceArrayId { get; set; } = string.Empty;
    public string SourceSnapshotId { get; set; } = string.Empty;
    public string CowStateId { get; set; } = string.Empty;
    public DateTime CreatedTime { get; set; }
    public DateTime? LastModified { get; set; }
    public CloneStatus Status { get; set; }
    public long SharedBlocks { get; set; }
    public long UniqueBlocks { get; set; }
}

/// <summary>
/// Clone status.
/// </summary>
public enum CloneStatus
{
    Active,
    Promoted,
    Deleted
}

/// <summary>
/// Result of clone promotion.
/// </summary>
public sealed class PromoteResult
{
    public string CloneId { get; set; } = string.Empty;
    public long BlocksCopied { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public bool Success { get; set; }
}

/// <summary>
/// Snapshot schedule.
/// </summary>
public sealed class SnapshotSchedule
{
    public string ScheduleId { get; set; } = string.Empty;
    public string ArrayId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ScheduleFrequency Frequency { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedTime { get; set; }
    public DateTime? LastRunTime { get; set; }
    public DateTime NextRunTime { get; set; }
    public int? RetentionCount { get; set; }
    public int? RetentionDays { get; set; }
    public string NamePattern { get; set; } = string.Empty;
    public int SuccessfulRuns { get; set; }
    public int FailedRuns { get; set; }
}

/// <summary>
/// Schedule frequency.
/// </summary>
public enum ScheduleFrequency
{
    Hourly,
    Daily,
    Weekly,
    Monthly
}

/// <summary>
/// Options for schedule.
/// </summary>
public sealed class ScheduleOptions
{
    public int? RetentionCount { get; set; }
    public int? RetentionDays { get; set; }
    public string? NamePattern { get; set; }
    public TimeSpan? StartTime { get; set; }
}

/// <summary>
/// Result of schedule execution.
/// </summary>
public sealed class ScheduleExecutionResult
{
    public string ScheduleId { get; set; } = string.Empty;
    public string? SnapshotId { get; set; }
    public DateTime ExecutionTime { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Replication configuration.
/// </summary>
public sealed class ReplicationConfig
{
    public string ConfigId { get; set; } = string.Empty;
    public string ArrayId { get; set; } = string.Empty;
    public string TargetEndpoint { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public ReplicationMode Mode { get; set; }
    public ScheduleFrequency Frequency { get; set; }
    public bool CompressionEnabled { get; set; }
    public bool EncryptionEnabled { get; set; }
    public int? BandwidthLimitMbps { get; set; }
    public string? LastReplicatedSnapshotId { get; set; }
    public DateTime? LastReplicationTime { get; set; }
}

/// <summary>
/// Replication mode.
/// </summary>
public enum ReplicationMode
{
    Async,
    Sync,
    Scheduled
}

/// <summary>
/// Replication options.
/// </summary>
public sealed class ReplicationOptions
{
    public ReplicationMode Mode { get; set; } = ReplicationMode.Async;
    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Hourly;
    public bool CompressionEnabled { get; set; } = true;
    public bool EncryptionEnabled { get; set; } = true;
    public int? BandwidthLimitMbps { get; set; }
}

/// <summary>
/// Result of replication.
/// </summary>
public sealed class ReplicationResult
{
    public string ArrayId { get; set; } = string.Empty;
    public string SnapshotId { get; set; } = string.Empty;
    public string TargetEndpoint { get; set; } = string.Empty;
    public long BlocksReplicated { get; set; }
    public long BytesTransferred { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public bool Success { get; set; }
}

/// <summary>
/// Progress of replication.
/// </summary>
public sealed class ReplicationProgress
{
    public long BlocksReplicated { get; set; }
    public long TotalBlocks { get; set; }
    public long BytesTransferred { get; set; }
    public double PercentComplete { get; set; }
}
