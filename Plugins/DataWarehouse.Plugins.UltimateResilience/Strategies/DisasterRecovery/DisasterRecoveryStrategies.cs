using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateResilience.Strategies.DisasterRecovery;

/// <summary>
/// Disaster recovery mode indicating the current operational state.
/// </summary>
public enum DisasterRecoveryMode
{
    /// <summary>Normal operation - primary region active.</summary>
    Normal,
    /// <summary>Failover in progress.</summary>
    FailingOver,
    /// <summary>Running on secondary/backup region.</summary>
    FailedOver,
    /// <summary>Failback in progress - returning to primary.</summary>
    FailingBack,
    /// <summary>Degraded mode - partial functionality.</summary>
    Degraded
}

/// <summary>
/// Recovery point representing a consistent state that can be restored.
/// </summary>
public sealed class RecoveryPoint
{
    /// <summary>Unique identifier for this recovery point.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Timestamp when recovery point was created.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Description of this recovery point.</summary>
    public string? Description { get; init; }

    /// <summary>Type of recovery point (full, incremental, checkpoint).</summary>
    public string Type { get; init; } = "checkpoint";

    /// <summary>Additional metadata about the recovery point.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();

    /// <summary>Whether this recovery point is valid and can be restored.</summary>
    public bool IsValid { get; init; } = true;

    /// <summary>Size in bytes if applicable.</summary>
    public long? SizeBytes { get; init; }
}

/// <summary>
/// Result of a disaster recovery operation.
/// </summary>
public sealed class DisasterRecoveryResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Current disaster recovery mode.</summary>
    public DisasterRecoveryMode Mode { get; init; }

    /// <summary>Description of the result.</summary>
    public string? Description { get; init; }

    /// <summary>Duration of the operation.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Recovery point used if applicable.</summary>
    public RecoveryPoint? RecoveryPoint { get; init; }

    /// <summary>Exception if operation failed.</summary>
    public Exception? Exception { get; init; }

    /// <summary>Additional metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Geo-replication failover strategy for multi-region disaster recovery.
/// </summary>
public sealed class GeoReplicationFailoverStrategy : ResilienceStrategyBase
{
    private DisasterRecoveryMode _mode = DisasterRecoveryMode.Normal;
    private readonly List<(string regionId, string endpoint, int priority, bool isHealthy)> _regions = new();
    private string? _activeRegionId;
    private readonly object _lock = new();
    private readonly TimeSpan _healthCheckInterval;
    private readonly int _failureThreshold;
    private int _consecutiveFailures;
    private DateTimeOffset _lastHealthCheck = DateTimeOffset.MinValue;

    public GeoReplicationFailoverStrategy()
        : this(healthCheckInterval: TimeSpan.FromSeconds(30), failureThreshold: 3)
    {
    }

    public GeoReplicationFailoverStrategy(TimeSpan healthCheckInterval, int failureThreshold)
    {
        _healthCheckInterval = healthCheckInterval;
        _failureThreshold = failureThreshold;
    }

    public override string StrategyId => "disaster-recovery-geo-replication";
    public override string StrategyName => "Geo-Replication Failover";
    public override string Category => "DisasterRecovery";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Geo-Replication Failover",
        Description = "Multi-region failover with automatic health-based region switching for geographic disaster recovery",
        Category = "DisasterRecovery",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = true,
        SupportsDistributedCoordination = true,
        TypicalLatencyOverheadMs = 5.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Gets the current disaster recovery mode.</summary>
    public DisasterRecoveryMode Mode => _mode;

    /// <summary>Gets the active region ID.</summary>
    public string? ActiveRegionId => _activeRegionId;

    /// <summary>
    /// Adds a region to the failover chain.
    /// </summary>
    public GeoReplicationFailoverStrategy AddRegion(string regionId, string endpoint, int priority)
    {
        lock (_lock)
        {
            _regions.Add((regionId, endpoint, priority, true));
            _regions.Sort((a, b) => a.priority.CompareTo(b.priority));

            if (_activeRegionId == null)
            {
                _activeRegionId = _regions.First().regionId;
            }
        }
        return this;
    }

    /// <summary>
    /// Initiates failover to the next healthy region.
    /// </summary>
    public async Task<DisasterRecoveryResult> FailoverAsync(CancellationToken cancellationToken = default)
    {
        var startTime = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            _mode = DisasterRecoveryMode.FailingOver;
        }

        try
        {
            // Mark current region as unhealthy
            lock (_lock)
            {
                for (int i = 0; i < _regions.Count; i++)
                {
                    if (_regions[i].regionId == _activeRegionId)
                    {
                        _regions[i] = (_regions[i].regionId, _regions[i].endpoint, _regions[i].priority, false);
                        break;
                    }
                }

                // Find next healthy region
                var nextRegion = _regions.FirstOrDefault(r => r.isHealthy);
                if (nextRegion.regionId == null)
                {
                    _mode = DisasterRecoveryMode.Degraded;
                    return new DisasterRecoveryResult
                    {
                        Success = false,
                        Mode = _mode,
                        Description = "No healthy regions available for failover",
                        Duration = DateTimeOffset.UtcNow - startTime
                    };
                }

                _activeRegionId = nextRegion.regionId;
                _mode = DisasterRecoveryMode.FailedOver;
            }

            await Task.Delay(10, cancellationToken); // Simulate failover work

            return new DisasterRecoveryResult
            {
                Success = true,
                Mode = _mode,
                Description = $"Failed over to region: {_activeRegionId}",
                Duration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["previousRegion"] = _regions.FirstOrDefault(r => !r.isHealthy).regionId ?? "unknown",
                    ["newRegion"] = _activeRegionId ?? "unknown"
                }
            };
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _mode = DisasterRecoveryMode.Degraded;
            }

            return new DisasterRecoveryResult
            {
                Success = false,
                Mode = _mode,
                Description = "Failover failed",
                Duration = DateTimeOffset.UtcNow - startTime,
                Exception = ex
            };
        }
    }

    /// <summary>
    /// Initiates failback to the primary region.
    /// </summary>
    public async Task<DisasterRecoveryResult> FailbackAsync(CancellationToken cancellationToken = default)
    {
        var startTime = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            _mode = DisasterRecoveryMode.FailingBack;
        }

        try
        {
            // Restore primary region health
            lock (_lock)
            {
                if (_regions.Count > 0)
                {
                    var primary = _regions[0];
                    _regions[0] = (primary.regionId, primary.endpoint, primary.priority, true);
                    _activeRegionId = primary.regionId;
                }

                _mode = DisasterRecoveryMode.Normal;
            }

            await Task.Delay(10, cancellationToken); // Simulate failback work

            return new DisasterRecoveryResult
            {
                Success = true,
                Mode = _mode,
                Description = $"Failed back to primary region: {_activeRegionId}",
                Duration = DateTimeOffset.UtcNow - startTime
            };
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _mode = DisasterRecoveryMode.FailedOver;
            }

            return new DisasterRecoveryResult
            {
                Success = false,
                Mode = _mode,
                Description = "Failback failed",
                Duration = DateTimeOffset.UtcNow - startTime,
                Exception = ex
            };
        }
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        try
        {
            var result = await operation(cancellationToken);

            lock (_lock)
            {
                _consecutiveFailures = 0;
            }

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["activeRegion"] = _activeRegionId ?? "unknown",
                    ["mode"] = _mode.ToString()
                }
            };
        }
        catch (Exception ex)
        {
            bool shouldFailover;
            lock (_lock)
            {
                _consecutiveFailures++;
                shouldFailover = _consecutiveFailures >= _failureThreshold;
            }

            if (shouldFailover)
            {
                var failoverResult = await FailoverAsync(cancellationToken);
                if (failoverResult.Success)
                {
                    // Retry on new region
                    try
                    {
                        var retryResult = await operation(cancellationToken);
                        return new ResilienceResult<T>
                        {
                            Success = true,
                            Value = retryResult,
                            Attempts = 2,
                            TotalDuration = DateTimeOffset.UtcNow - startTime,
                            Metadata =
                            {
                                ["failedOver"] = true,
                                ["activeRegion"] = _activeRegionId ?? "unknown"
                            }
                        };
                    }
                    catch
                    {
                        // Failover retry also failed
                    }
                }
            }

            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["activeRegion"] = _activeRegionId ?? "unknown",
                    ["mode"] = _mode.ToString(),
                    ["consecutiveFailures"] = _consecutiveFailures
                }
            };
        }
    }

    protected override string? GetCurrentState() =>
        $"{_mode} (Active: {_activeRegionId ?? "none"}, Regions: {_regions.Count})";
}

/// <summary>
/// Point-in-time recovery strategy for checkpoint-based disaster recovery.
/// </summary>
public sealed class PointInTimeRecoveryStrategy : ResilienceStrategyBase
{
    private readonly ConcurrentDictionary<string, RecoveryPoint> _recoveryPoints = new();
    private readonly int _maxRecoveryPoints;
    private readonly TimeSpan _retentionPeriod;
    private RecoveryPoint? _lastCheckpoint;
    private readonly object _lock = new();

    public PointInTimeRecoveryStrategy()
        : this(maxRecoveryPoints: 100, retentionPeriod: TimeSpan.FromDays(7))
    {
    }

    public PointInTimeRecoveryStrategy(int maxRecoveryPoints, TimeSpan retentionPeriod)
    {
        _maxRecoveryPoints = maxRecoveryPoints;
        _retentionPeriod = retentionPeriod;
    }

    public override string StrategyId => "disaster-recovery-point-in-time";
    public override string StrategyName => "Point-in-Time Recovery";
    public override string Category => "DisasterRecovery";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Point-in-Time Recovery",
        Description = "Checkpoint-based recovery allowing restoration to any saved point in time",
        Category = "DisasterRecovery",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 1.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>Gets all recovery points.</summary>
    public IReadOnlyList<RecoveryPoint> RecoveryPoints => _recoveryPoints.Values.OrderByDescending(r => r.Timestamp).ToList();

    /// <summary>Gets the last checkpoint.</summary>
    public RecoveryPoint? LastCheckpoint => _lastCheckpoint;

    /// <summary>
    /// Creates a new recovery point (checkpoint).
    /// </summary>
    public RecoveryPoint CreateCheckpoint(string? description = null, Dictionary<string, object>? metadata = null)
    {
        PruneOldRecoveryPoints();

        var checkpoint = new RecoveryPoint
        {
            Description = description ?? $"Checkpoint at {DateTimeOffset.UtcNow:O}",
            Type = "checkpoint",
            Metadata = metadata ?? new Dictionary<string, object>()
        };

        _recoveryPoints[checkpoint.Id] = checkpoint;

        lock (_lock)
        {
            _lastCheckpoint = checkpoint;
        }

        return checkpoint;
    }

    /// <summary>
    /// Creates a full backup recovery point.
    /// </summary>
    public RecoveryPoint CreateFullBackup(string? description = null, long? sizeBytes = null)
    {
        PruneOldRecoveryPoints();

        var backup = new RecoveryPoint
        {
            Description = description ?? $"Full backup at {DateTimeOffset.UtcNow:O}",
            Type = "full",
            SizeBytes = sizeBytes
        };

        _recoveryPoints[backup.Id] = backup;
        return backup;
    }

    /// <summary>
    /// Finds recovery points within a time range.
    /// </summary>
    public IReadOnlyList<RecoveryPoint> FindRecoveryPoints(DateTimeOffset start, DateTimeOffset end)
    {
        return _recoveryPoints.Values
            .Where(r => r.Timestamp >= start && r.Timestamp <= end)
            .OrderByDescending(r => r.Timestamp)
            .ToList();
    }

    /// <summary>
    /// Finds the closest recovery point to a given time.
    /// </summary>
    public RecoveryPoint? FindClosestRecoveryPoint(DateTimeOffset targetTime)
    {
        return _recoveryPoints.Values
            .Where(r => r.IsValid && r.Timestamp <= targetTime)
            .OrderByDescending(r => r.Timestamp)
            .FirstOrDefault();
    }

    /// <summary>
    /// Simulates restoring to a recovery point.
    /// </summary>
    public async Task<DisasterRecoveryResult> RestoreToPointAsync(
        string recoveryPointId,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTimeOffset.UtcNow;

        if (!_recoveryPoints.TryGetValue(recoveryPointId, out var recoveryPoint))
        {
            return new DisasterRecoveryResult
            {
                Success = false,
                Mode = DisasterRecoveryMode.Normal,
                Description = $"Recovery point {recoveryPointId} not found",
                Duration = TimeSpan.Zero
            };
        }

        if (!recoveryPoint.IsValid)
        {
            return new DisasterRecoveryResult
            {
                Success = false,
                Mode = DisasterRecoveryMode.Normal,
                Description = $"Recovery point {recoveryPointId} is invalid",
                Duration = TimeSpan.Zero,
                RecoveryPoint = recoveryPoint
            };
        }

        await Task.Delay(10, cancellationToken); // Simulate restore work

        return new DisasterRecoveryResult
        {
            Success = true,
            Mode = DisasterRecoveryMode.Normal,
            Description = $"Restored to recovery point: {recoveryPoint.Description}",
            Duration = DateTimeOffset.UtcNow - startTime,
            RecoveryPoint = recoveryPoint,
            Metadata =
            {
                ["recoveryPointTimestamp"] = recoveryPoint.Timestamp,
                ["recoveryPointType"] = recoveryPoint.Type
            }
        };
    }

    /// <summary>
    /// Restores to a specific point in time.
    /// </summary>
    public async Task<DisasterRecoveryResult> RestoreToTimeAsync(
        DateTimeOffset targetTime,
        CancellationToken cancellationToken = default)
    {
        var recoveryPoint = FindClosestRecoveryPoint(targetTime);

        if (recoveryPoint == null)
        {
            return new DisasterRecoveryResult
            {
                Success = false,
                Mode = DisasterRecoveryMode.Normal,
                Description = $"No recovery point found for time: {targetTime:O}",
                Duration = TimeSpan.Zero
            };
        }

        return await RestoreToPointAsync(recoveryPoint.Id, cancellationToken);
    }

    private void PruneOldRecoveryPoints()
    {
        var cutoff = DateTimeOffset.UtcNow - _retentionPeriod;

        var toRemove = _recoveryPoints.Values
            .Where(r => r.Timestamp < cutoff)
            .Select(r => r.Id)
            .ToList();

        foreach (var id in toRemove)
        {
            _recoveryPoints.TryRemove(id, out _);
        }

        // Also enforce max count
        while (_recoveryPoints.Count > _maxRecoveryPoints)
        {
            var oldest = _recoveryPoints.Values.MinBy(r => r.Timestamp);
            if (oldest != null)
            {
                _recoveryPoints.TryRemove(oldest.Id, out _);
            }
        }
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["recoveryPointCount"] = _recoveryPoints.Count,
                    ["lastCheckpoint"] = _lastCheckpoint?.Timestamp.ToString("O") ?? "none"
                }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    protected override string? GetCurrentState() =>
        $"Recovery points: {_recoveryPoints.Count}, Last: {_lastCheckpoint?.Timestamp.ToString("O") ?? "none"}";
}

/// <summary>
/// Multi-region disaster recovery strategy with active-passive or active-active modes.
/// </summary>
public sealed class MultiRegionDisasterRecoveryStrategy : ResilienceStrategyBase
{
    private readonly ConcurrentDictionary<string, RegionState> _regions = new();
    private string? _primaryRegionId;
    private DisasterRecoveryMode _mode = DisasterRecoveryMode.Normal;
    private readonly bool _activeActive;
    private readonly TimeSpan _syncInterval;
    private readonly object _lock = new();

    private sealed class RegionState
    {
        public string RegionId { get; init; } = "";
        public string Endpoint { get; init; } = "";
        public bool IsPrimary { get; set; }
        public bool IsHealthy { get; set; } = true;
        public DateTimeOffset LastSyncTime { get; set; } = DateTimeOffset.UtcNow;
        public long ReplicationLagMs { get; set; }
    }

    public MultiRegionDisasterRecoveryStrategy()
        : this(activeActive: false, syncInterval: TimeSpan.FromSeconds(10))
    {
    }

    public MultiRegionDisasterRecoveryStrategy(bool activeActive, TimeSpan syncInterval)
    {
        _activeActive = activeActive;
        _syncInterval = syncInterval;
    }

    public override string StrategyId => "disaster-recovery-multi-region";
    public override string StrategyName => "Multi-Region Disaster Recovery";
    public override string Category => "DisasterRecovery";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Multi-Region Disaster Recovery",
        Description = "Enterprise-grade multi-region DR with active-passive or active-active replication",
        Category = "DisasterRecovery",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = true,
        SupportsDistributedCoordination = true,
        TypicalLatencyOverheadMs = 10.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>Gets the current mode.</summary>
    public DisasterRecoveryMode Mode => _mode;

    /// <summary>Gets the primary region ID.</summary>
    public string? PrimaryRegionId => _primaryRegionId;

    /// <summary>Gets whether active-active mode is enabled.</summary>
    public bool IsActiveActive => _activeActive;

    /// <summary>
    /// Adds a region to the DR configuration.
    /// </summary>
    public MultiRegionDisasterRecoveryStrategy AddRegion(string regionId, string endpoint, bool isPrimary = false)
    {
        var state = new RegionState
        {
            RegionId = regionId,
            Endpoint = endpoint,
            IsPrimary = isPrimary
        };

        _regions[regionId] = state;

        if (isPrimary || _primaryRegionId == null)
        {
            lock (_lock)
            {
                _primaryRegionId = regionId;
            }
        }

        return this;
    }

    /// <summary>
    /// Updates the health status of a region.
    /// </summary>
    public void UpdateRegionHealth(string regionId, bool isHealthy, long? replicationLagMs = null)
    {
        if (_regions.TryGetValue(regionId, out var state))
        {
            state.IsHealthy = isHealthy;
            if (replicationLagMs.HasValue)
            {
                state.ReplicationLagMs = replicationLagMs.Value;
            }
        }
    }

    /// <summary>
    /// Promotes a secondary region to primary.
    /// </summary>
    public async Task<DisasterRecoveryResult> PromoteRegionAsync(
        string regionId,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTimeOffset.UtcNow;

        if (!_regions.TryGetValue(regionId, out var newPrimary))
        {
            return new DisasterRecoveryResult
            {
                Success = false,
                Mode = _mode,
                Description = $"Region {regionId} not found",
                Duration = TimeSpan.Zero
            };
        }

        lock (_lock)
        {
            _mode = DisasterRecoveryMode.FailingOver;
        }

        try
        {
            // Demote current primary
            if (_primaryRegionId != null && _regions.TryGetValue(_primaryRegionId, out var oldPrimary))
            {
                oldPrimary.IsPrimary = false;
            }

            // Promote new primary
            newPrimary.IsPrimary = true;

            lock (_lock)
            {
                _primaryRegionId = regionId;
                _mode = DisasterRecoveryMode.FailedOver;
            }

            await Task.Delay(10, cancellationToken); // Simulate promotion work

            return new DisasterRecoveryResult
            {
                Success = true,
                Mode = _mode,
                Description = $"Promoted region {regionId} to primary",
                Duration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["newPrimaryRegion"] = regionId,
                    ["replicationLag"] = newPrimary.ReplicationLagMs
                }
            };
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _mode = DisasterRecoveryMode.Degraded;
            }

            return new DisasterRecoveryResult
            {
                Success = false,
                Mode = _mode,
                Description = "Region promotion failed",
                Duration = DateTimeOffset.UtcNow - startTime,
                Exception = ex
            };
        }
    }

    /// <summary>
    /// Gets the replication status across all regions.
    /// </summary>
    public Dictionary<string, object> GetReplicationStatus()
    {
        return new Dictionary<string, object>
        {
            ["primaryRegion"] = _primaryRegionId ?? "none",
            ["mode"] = _mode.ToString(),
            ["activeActive"] = _activeActive,
            ["regions"] = _regions.Values.Select(r => new Dictionary<string, object>
            {
                ["regionId"] = r.RegionId,
                ["isPrimary"] = r.IsPrimary,
                ["isHealthy"] = r.IsHealthy,
                ["replicationLagMs"] = r.ReplicationLagMs,
                ["lastSync"] = r.LastSyncTime.ToString("O")
            }).ToList()
        };
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        // In active-active, we could route to any healthy region
        // In active-passive, we only use primary

        var targetRegion = _primaryRegionId;
        if (_activeActive)
        {
            // Find healthiest region
            var healthiest = _regions.Values
                .Where(r => r.IsHealthy)
                .MinBy(r => r.ReplicationLagMs);
            targetRegion = healthiest?.RegionId ?? _primaryRegionId;
        }

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["targetRegion"] = targetRegion ?? "unknown",
                    ["mode"] = _mode.ToString(),
                    ["activeActive"] = _activeActive
                }
            };
        }
        catch (Exception ex)
        {
            // On failure in active-passive, try failover
            if (!_activeActive && _primaryRegionId != null)
            {
                UpdateRegionHealth(_primaryRegionId, false);

                var nextRegion = _regions.Values
                    .Where(r => r.IsHealthy && r.RegionId != _primaryRegionId)
                    .MinBy(r => r.ReplicationLagMs);

                if (nextRegion != null)
                {
                    var promotionResult = await PromoteRegionAsync(nextRegion.RegionId, cancellationToken);
                    if (promotionResult.Success)
                    {
                        // Retry on new primary
                        try
                        {
                            var retryResult = await operation(cancellationToken);
                            return new ResilienceResult<T>
                            {
                                Success = true,
                                Value = retryResult,
                                Attempts = 2,
                                TotalDuration = DateTimeOffset.UtcNow - startTime,
                                Metadata =
                                {
                                    ["failedOver"] = true,
                                    ["newPrimary"] = nextRegion.RegionId
                                }
                            };
                        }
                        catch
                        {
                            // Retry also failed
                        }
                    }
                }
            }

            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["mode"] = _mode.ToString(),
                    ["primaryRegion"] = _primaryRegionId ?? "none"
                }
            };
        }
    }

    protected override string? GetCurrentState() =>
        $"{_mode} (Primary: {_primaryRegionId ?? "none"}, Regions: {_regions.Count}, Mode: {(_activeActive ? "Active-Active" : "Active-Passive")})";
}

/// <summary>
/// State checkpoint and recovery strategy for application state disaster recovery.
/// </summary>
public sealed class StateCheckpointStrategy : ResilienceStrategyBase
{
    private readonly ConcurrentDictionary<string, byte[]> _stateStore = new();
    private readonly ConcurrentQueue<(string checkpointId, DateTimeOffset timestamp)> _checkpointHistory = new();
    private readonly int _maxCheckpoints;
    private readonly Func<CancellationToken, Task<byte[]>>? _stateSerializer;
    private readonly Func<byte[], CancellationToken, Task>? _stateRestorer;
    private readonly object _lock = new();

    public StateCheckpointStrategy()
        : this(maxCheckpoints: 50, stateSerializer: null, stateRestorer: null)
    {
    }

    public StateCheckpointStrategy(
        int maxCheckpoints,
        Func<CancellationToken, Task<byte[]>>? stateSerializer,
        Func<byte[], CancellationToken, Task>? stateRestorer)
    {
        _maxCheckpoints = maxCheckpoints;
        _stateSerializer = stateSerializer;
        _stateRestorer = stateRestorer;
    }

    public override string StrategyId => "disaster-recovery-state-checkpoint";
    public override string StrategyName => "State Checkpoint Recovery";
    public override string Category => "DisasterRecovery";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "State Checkpoint Recovery",
        Description = "Application state checkpointing for fast recovery with minimal data loss",
        Category = "DisasterRecovery",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 2.0,
        MemoryFootprint = "High"
    };

    /// <summary>Gets the number of stored checkpoints.</summary>
    public int CheckpointCount => _stateStore.Count;

    /// <summary>
    /// Creates a state checkpoint.
    /// </summary>
    public async Task<RecoveryPoint> CreateCheckpointAsync(
        byte[]? stateData = null,
        CancellationToken cancellationToken = default)
    {
        var checkpointId = Guid.NewGuid().ToString("N");
        var data = stateData;

        if (data == null && _stateSerializer != null)
        {
            data = await _stateSerializer(cancellationToken);
        }

        data ??= Array.Empty<byte>();

        _stateStore[checkpointId] = data;
        _checkpointHistory.Enqueue((checkpointId, DateTimeOffset.UtcNow));

        // Prune old checkpoints
        while (_checkpointHistory.Count > _maxCheckpoints)
        {
            if (_checkpointHistory.TryDequeue(out var oldest))
            {
                _stateStore.TryRemove(oldest.checkpointId, out _);
            }
        }

        return new RecoveryPoint
        {
            Id = checkpointId,
            Type = "state-checkpoint",
            Description = $"State checkpoint with {data.Length} bytes",
            SizeBytes = data.Length
        };
    }

    /// <summary>
    /// Restores state from a checkpoint.
    /// </summary>
    public async Task<DisasterRecoveryResult> RestoreCheckpointAsync(
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTimeOffset.UtcNow;

        if (!_stateStore.TryGetValue(checkpointId, out var stateData))
        {
            return new DisasterRecoveryResult
            {
                Success = false,
                Mode = DisasterRecoveryMode.Normal,
                Description = $"Checkpoint {checkpointId} not found",
                Duration = TimeSpan.Zero
            };
        }

        if (_stateRestorer != null)
        {
            await _stateRestorer(stateData, cancellationToken);
        }

        return new DisasterRecoveryResult
        {
            Success = true,
            Mode = DisasterRecoveryMode.Normal,
            Description = $"Restored checkpoint {checkpointId}",
            Duration = DateTimeOffset.UtcNow - startTime,
            Metadata =
            {
                ["checkpointSize"] = stateData.Length,
                ["checkpointId"] = checkpointId
            }
        };
    }

    /// <summary>
    /// Gets the most recent checkpoint.
    /// </summary>
    public (string? checkpointId, DateTimeOffset? timestamp) GetLatestCheckpoint()
    {
        var latest = _checkpointHistory.LastOrDefault();
        return latest.checkpointId != null ? (latest.checkpointId, latest.timestamp) : (null, null);
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["checkpointCount"] = _stateStore.Count
                }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    public override void Reset()
    {
        base.Reset();
        _stateStore.Clear();
        while (_checkpointHistory.TryDequeue(out _)) { }
    }

    protected override string? GetCurrentState() => $"Checkpoints: {_stateStore.Count}";
}

/// <summary>
/// Data center failover strategy for site-level disaster recovery.
/// </summary>
public sealed class DataCenterFailoverStrategy : ResilienceStrategyBase
{
    private readonly List<DataCenterInfo> _dataCenters = new();
    private string? _activeDataCenterId;
    private DisasterRecoveryMode _mode = DisasterRecoveryMode.Normal;
    private readonly TimeSpan _failoverTimeout;
    private readonly object _lock = new();

    private sealed class DataCenterInfo
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Location { get; init; } = "";
        public int Priority { get; init; }
        public bool IsHealthy { get; set; } = true;
        public DateTimeOffset LastHealthCheck { get; set; } = DateTimeOffset.UtcNow;
        public Dictionary<string, object> Capabilities { get; init; } = new();
    }

    public DataCenterFailoverStrategy()
        : this(failoverTimeout: TimeSpan.FromMinutes(5))
    {
    }

    public DataCenterFailoverStrategy(TimeSpan failoverTimeout)
    {
        _failoverTimeout = failoverTimeout;
    }

    public override string StrategyId => "disaster-recovery-datacenter-failover";
    public override string StrategyName => "Data Center Failover";
    public override string Category => "DisasterRecovery";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Data Center Failover",
        Description = "Site-level disaster recovery with automatic failover between data centers",
        Category = "DisasterRecovery",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = true,
        SupportsDistributedCoordination = true,
        TypicalLatencyOverheadMs = 20.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Gets the active data center ID.</summary>
    public string? ActiveDataCenterId => _activeDataCenterId;

    /// <summary>Gets the current mode.</summary>
    public DisasterRecoveryMode Mode => _mode;

    /// <summary>
    /// Adds a data center to the failover configuration.
    /// </summary>
    public DataCenterFailoverStrategy AddDataCenter(
        string id, string name, string location, int priority,
        Dictionary<string, object>? capabilities = null)
    {
        lock (_lock)
        {
            _dataCenters.Add(new DataCenterInfo
            {
                Id = id,
                Name = name,
                Location = location,
                Priority = priority,
                Capabilities = capabilities ?? new Dictionary<string, object>()
            });

            _dataCenters.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            if (_activeDataCenterId == null)
            {
                _activeDataCenterId = _dataCenters.First().Id;
            }
        }

        return this;
    }

    /// <summary>
    /// Updates health status of a data center.
    /// </summary>
    public void UpdateHealth(string dataCenterId, bool isHealthy)
    {
        lock (_lock)
        {
            var dc = _dataCenters.FirstOrDefault(d => d.Id == dataCenterId);
            if (dc != null)
            {
                dc.IsHealthy = isHealthy;
                dc.LastHealthCheck = DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>
    /// Initiates failover to the next available data center.
    /// </summary>
    public async Task<DisasterRecoveryResult> FailoverAsync(CancellationToken cancellationToken = default)
    {
        var startTime = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            _mode = DisasterRecoveryMode.FailingOver;

            // Mark current as unhealthy
            var current = _dataCenters.FirstOrDefault(d => d.Id == _activeDataCenterId);
            if (current != null)
            {
                current.IsHealthy = false;
            }

            // Find next healthy data center
            var next = _dataCenters.FirstOrDefault(d => d.IsHealthy);
            if (next == null)
            {
                _mode = DisasterRecoveryMode.Degraded;
                return new DisasterRecoveryResult
                {
                    Success = false,
                    Mode = _mode,
                    Description = "No healthy data centers available",
                    Duration = DateTimeOffset.UtcNow - startTime
                };
            }

            _activeDataCenterId = next.Id;
            _mode = DisasterRecoveryMode.FailedOver;
        }

        await Task.Delay(10, cancellationToken); // Simulate failover

        return new DisasterRecoveryResult
        {
            Success = true,
            Mode = _mode,
            Description = $"Failed over to data center: {_activeDataCenterId}",
            Duration = DateTimeOffset.UtcNow - startTime,
            Metadata = { ["newActiveDataCenter"] = _activeDataCenterId ?? "unknown" }
        };
    }

    /// <summary>
    /// Gets status of all data centers.
    /// </summary>
    public IReadOnlyList<Dictionary<string, object>> GetDataCenterStatus()
    {
        lock (_lock)
        {
            return _dataCenters.Select(dc => new Dictionary<string, object>
            {
                ["id"] = dc.Id,
                ["name"] = dc.Name,
                ["location"] = dc.Location,
                ["priority"] = dc.Priority,
                ["isHealthy"] = dc.IsHealthy,
                ["isActive"] = dc.Id == _activeDataCenterId,
                ["lastHealthCheck"] = dc.LastHealthCheck.ToString("O")
            }).ToList();
        }
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["activeDataCenter"] = _activeDataCenterId ?? "unknown",
                    ["mode"] = _mode.ToString()
                }
            };
        }
        catch (Exception ex)
        {
            // Try failover
            var failoverResult = await FailoverAsync(cancellationToken);
            if (failoverResult.Success)
            {
                try
                {
                    var retryResult = await operation(cancellationToken);
                    return new ResilienceResult<T>
                    {
                        Success = true,
                        Value = retryResult,
                        Attempts = 2,
                        TotalDuration = DateTimeOffset.UtcNow - startTime,
                        Metadata =
                        {
                            ["failedOver"] = true,
                            ["activeDataCenter"] = _activeDataCenterId ?? "unknown"
                        }
                    };
                }
                catch
                {
                    // Failover retry failed
                }
            }

            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["mode"] = _mode.ToString(),
                    ["activeDataCenter"] = _activeDataCenterId ?? "unknown"
                }
            };
        }
    }

    protected override string? GetCurrentState() =>
        $"{_mode} (Active: {_activeDataCenterId ?? "none"}, DCs: {_dataCenters.Count})";
}

/// <summary>
/// Backup coordination strategy for managing backup operations across distributed systems.
/// </summary>
public sealed class BackupCoordinationStrategy : ResilienceStrategyBase
{
    private readonly ConcurrentDictionary<string, BackupJob> _backupJobs = new();
    private readonly TimeSpan _backupInterval;
    private readonly int _retainBackups;
    private DateTimeOffset _lastBackup = DateTimeOffset.MinValue;
    private readonly object _lock = new();

    private sealed class BackupJob
    {
        public string Id { get; init; } = "";
        public DateTimeOffset StartTime { get; init; }
        public DateTimeOffset? EndTime { get; set; }
        public string Status { get; set; } = "pending";
        public string? Error { get; set; }
        public long? SizeBytes { get; set; }
        public string Type { get; init; } = "full";
    }

    public BackupCoordinationStrategy()
        : this(backupInterval: TimeSpan.FromHours(1), retainBackups: 24)
    {
    }

    public BackupCoordinationStrategy(TimeSpan backupInterval, int retainBackups)
    {
        _backupInterval = backupInterval;
        _retainBackups = retainBackups;
    }

    public override string StrategyId => "disaster-recovery-backup-coordination";
    public override string StrategyName => "Backup Coordination";
    public override string Category => "DisasterRecovery";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Backup Coordination",
        Description = "Coordinates backup operations across distributed systems with scheduling and retention policies",
        Category = "DisasterRecovery",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = true,
        TypicalLatencyOverheadMs = 1.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Gets the last backup time.</summary>
    public DateTimeOffset LastBackupTime => _lastBackup;

    /// <summary>Gets the backup count.</summary>
    public int BackupCount => _backupJobs.Count;

    /// <summary>
    /// Starts a backup job.
    /// </summary>
    public string StartBackup(string type = "full")
    {
        var job = new BackupJob
        {
            Id = Guid.NewGuid().ToString("N"),
            StartTime = DateTimeOffset.UtcNow,
            Type = type,
            Status = "running"
        };

        _backupJobs[job.Id] = job;
        return job.Id;
    }

    /// <summary>
    /// Completes a backup job.
    /// </summary>
    public void CompleteBackup(string jobId, long? sizeBytes = null, string? error = null)
    {
        if (_backupJobs.TryGetValue(jobId, out var job))
        {
            job.EndTime = DateTimeOffset.UtcNow;
            job.Status = error != null ? "failed" : "completed";
            job.Error = error;
            job.SizeBytes = sizeBytes;

            if (error == null)
            {
                lock (_lock)
                {
                    _lastBackup = DateTimeOffset.UtcNow;
                }
            }

            PruneOldBackups();
        }
    }

    /// <summary>
    /// Checks if a backup is due.
    /// </summary>
    public bool IsBackupDue()
    {
        return DateTimeOffset.UtcNow - _lastBackup >= _backupInterval;
    }

    /// <summary>
    /// Gets backup status.
    /// </summary>
    public IReadOnlyList<Dictionary<string, object>> GetBackupStatus()
    {
        return _backupJobs.Values
            .OrderByDescending(j => j.StartTime)
            .Take(10)
            .Select(j => new Dictionary<string, object>
            {
                ["id"] = j.Id,
                ["type"] = j.Type,
                ["status"] = j.Status,
                ["startTime"] = j.StartTime.ToString("O"),
                ["endTime"] = j.EndTime?.ToString("O") ?? "",
                ["sizeBytes"] = j.SizeBytes ?? 0,
                ["error"] = j.Error ?? ""
            })
            .ToList();
    }

    private void PruneOldBackups()
    {
        var completed = _backupJobs.Values
            .Where(j => j.Status == "completed")
            .OrderByDescending(j => j.StartTime)
            .Skip(_retainBackups)
            .ToList();

        foreach (var job in completed)
        {
            _backupJobs.TryRemove(job.Id, out _);
        }
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["lastBackup"] = _lastBackup.ToString("O"),
                    ["backupDue"] = IsBackupDue()
                }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    protected override string? GetCurrentState() =>
        $"Backups: {_backupJobs.Count}, Last: {(_lastBackup == DateTimeOffset.MinValue ? "never" : _lastBackup.ToString("O"))}";
}
