using System.Collections.Concurrent;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Tiering;

/// <summary>
/// Defines the available storage tiers in the system.
/// </summary>
public enum StorageTier
{
    /// <summary>
    /// Hot tier - fastest storage for frequently accessed data.
    /// </summary>
    Hot = 0,

    /// <summary>
    /// Warm tier - balanced storage for moderately accessed data.
    /// </summary>
    Warm = 1,

    /// <summary>
    /// Cold tier - cost-optimized storage for infrequently accessed data.
    /// </summary>
    Cold = 2,

    /// <summary>
    /// Archive tier - lowest cost storage for rarely accessed data.
    /// </summary>
    Archive = 3,

    /// <summary>
    /// Glacier tier - deep archive for compliance/long-term retention.
    /// </summary>
    Glacier = 4
}

/// <summary>
/// Represents a data object for tiering evaluation.
/// </summary>
public sealed class DataObject
{
    /// <summary>
    /// Unique identifier of the object.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Current storage tier of the object.
    /// </summary>
    public StorageTier CurrentTier { get; init; } = StorageTier.Hot;

    /// <summary>
    /// Size of the object in bytes.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Creation timestamp.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Last modified timestamp.
    /// </summary>
    public DateTime LastModifiedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Last accessed timestamp.
    /// </summary>
    public DateTime LastAccessedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Total number of accesses.
    /// </summary>
    public long AccessCount { get; init; }

    /// <summary>
    /// Number of accesses in the last 24 hours.
    /// </summary>
    public int AccessesLast24Hours { get; init; }

    /// <summary>
    /// Number of accesses in the last 7 days.
    /// </summary>
    public int AccessesLast7Days { get; init; }

    /// <summary>
    /// Number of accesses in the last 30 days.
    /// </summary>
    public int AccessesLast30Days { get; init; }

    /// <summary>
    /// Content type (MIME type).
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Object classification or category.
    /// </summary>
    public string? Classification { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Gets the age of the object since creation.
    /// </summary>
    public TimeSpan Age => DateTime.UtcNow - CreatedAt;

    /// <summary>
    /// Gets the time since last access.
    /// </summary>
    public TimeSpan TimeSinceLastAccess => DateTime.UtcNow - LastAccessedAt;

    /// <summary>
    /// Gets the time since last modification.
    /// </summary>
    public TimeSpan TimeSinceLastModified => DateTime.UtcNow - LastModifiedAt;
}

/// <summary>
/// Represents a tier movement recommendation.
/// </summary>
/// <param name="ObjectId">The object identifier.</param>
/// <param name="CurrentTier">The current storage tier.</param>
/// <param name="RecommendedTier">The recommended target tier.</param>
/// <param name="Reason">Human-readable reason for the recommendation.</param>
/// <param name="Confidence">Confidence score from 0.0 to 1.0.</param>
public record TierRecommendation(
    string ObjectId,
    StorageTier CurrentTier,
    StorageTier RecommendedTier,
    string Reason,
    double Confidence)
{
    /// <summary>
    /// Whether the recommendation suggests a tier change.
    /// </summary>
    public bool RequiresMove => CurrentTier != RecommendedTier;

    /// <summary>
    /// Whether the recommendation moves data to a colder tier.
    /// </summary>
    public bool IsDemotion => RecommendedTier > CurrentTier;

    /// <summary>
    /// Whether the recommendation moves data to a hotter tier.
    /// </summary>
    public bool IsPromotion => RecommendedTier < CurrentTier;

    /// <summary>
    /// Priority score for movement (higher = more urgent).
    /// </summary>
    public double Priority { get; init; }

    /// <summary>
    /// Estimated cost savings per month if the move is executed.
    /// </summary>
    public decimal? EstimatedMonthlySavings { get; init; }
}

/// <summary>
/// Result of a tier movement operation.
/// </summary>
public sealed class TierMoveResult
{
    /// <summary>
    /// Whether the move operation succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Object identifier.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Source tier before the move.
    /// </summary>
    public StorageTier SourceTier { get; init; }

    /// <summary>
    /// Target tier after the move.
    /// </summary>
    public StorageTier TargetTier { get; init; }

    /// <summary>
    /// Duration of the move operation.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Bytes transferred during the move.
    /// </summary>
    public long BytesTransferred { get; init; }

    /// <summary>
    /// Error message if the move failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static TierMoveResult Succeeded(string objectId, StorageTier source, StorageTier target, TimeSpan duration, long bytes) =>
        new()
        {
            Success = true,
            ObjectId = objectId,
            SourceTier = source,
            TargetTier = target,
            Duration = duration,
            BytesTransferred = bytes
        };

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static TierMoveResult Failed(string objectId, StorageTier source, StorageTier target, string error) =>
        new()
        {
            Success = false,
            ObjectId = objectId,
            SourceTier = source,
            TargetTier = target,
            ErrorMessage = error
        };
}

/// <summary>
/// Statistics for tiering operations.
/// </summary>
public sealed class TieringStats
{
    /// <summary>
    /// Total objects evaluated.
    /// </summary>
    public long TotalObjectsEvaluated { get; set; }

    /// <summary>
    /// Total objects moved.
    /// </summary>
    public long TotalObjectsMoved { get; set; }

    /// <summary>
    /// Total bytes moved.
    /// </summary>
    public long TotalBytesMoved { get; set; }

    /// <summary>
    /// Objects per tier.
    /// </summary>
    public Dictionary<StorageTier, long> ObjectsPerTier { get; set; } = new();

    /// <summary>
    /// Bytes per tier.
    /// </summary>
    public Dictionary<StorageTier, long> BytesPerTier { get; set; } = new();

    /// <summary>
    /// Pending recommendations count.
    /// </summary>
    public long PendingRecommendations { get; set; }

    /// <summary>
    /// Failed moves count.
    /// </summary>
    public long FailedMoves { get; set; }

    /// <summary>
    /// Total estimated savings per month.
    /// </summary>
    public decimal TotalEstimatedMonthlySavings { get; set; }

    /// <summary>
    /// Last evaluation timestamp.
    /// </summary>
    public DateTime? LastEvaluationAt { get; set; }

    /// <summary>
    /// Average evaluation time in milliseconds.
    /// </summary>
    public double AverageEvaluationTimeMs { get; set; }
}

/// <summary>
/// Interface for tiering strategies.
/// </summary>
public interface ITieringStrategy : IDataManagementStrategy
{
    /// <summary>
    /// Evaluates a single data object and recommends a tier.
    /// </summary>
    /// <param name="data">The data object to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tier recommendation.</returns>
    Task<TierRecommendation> EvaluateAsync(DataObject data, CancellationToken ct = default);

    /// <summary>
    /// Moves an object to a target tier.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <param name="targetTier">The target storage tier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result of the move operation.</returns>
    Task<TierMoveResult> MoveToTierAsync(string objectId, StorageTier targetTier, CancellationToken ct = default);

    /// <summary>
    /// Evaluates multiple data objects in a batch.
    /// </summary>
    /// <param name="data">The data objects to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tier recommendations.</returns>
    Task<IEnumerable<TierRecommendation>> BatchEvaluateAsync(IEnumerable<DataObject> data, CancellationToken ct = default);

    /// <summary>
    /// Gets tiering statistics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tiering statistics.</returns>
    Task<TieringStats> GetStatsAsync(CancellationToken ct = default);
}

/// <summary>
/// Abstract base class for tiering strategies.
/// Provides common functionality for tier evaluation, movement, and statistics tracking.
/// </summary>
public abstract class TieringStrategyBase : DataManagementStrategyBase, ITieringStrategy
{
    private readonly ConcurrentDictionary<string, TierRecommendation> _pendingRecommendations = new();
    private readonly ConcurrentDictionary<StorageTier, long> _objectsPerTier = new();
    private readonly ConcurrentDictionary<StorageTier, long> _bytesPerTier = new();
    private long _totalObjectsEvaluated;
    private long _totalObjectsMoved;
    private long _totalBytesMoved;
    private long _failedMoves;
    private double _totalEvaluationTimeMs;
    private DateTime? _lastEvaluationAt;
    private readonly object _statsLock = new();

    /// <summary>
    /// Storage tier handler for executing actual tier movements.
    /// </summary>
    protected IStorageTierHandler? TierHandler { get; set; }

    /// <inheritdoc/>
    public override DataManagementCategory Category => DataManagementCategory.Lifecycle;

    /// <inheritdoc/>
    public async Task<TierRecommendation> EvaluateAsync(DataObject data, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(data);

        var sw = Stopwatch.StartNew();
        try
        {
            var recommendation = await EvaluateCoreAsync(data, ct);
            sw.Stop();

            lock (_statsLock)
            {
                _totalObjectsEvaluated++;
                _totalEvaluationTimeMs += sw.Elapsed.TotalMilliseconds;
                _lastEvaluationAt = DateTime.UtcNow;
            }

            if (recommendation.RequiresMove)
            {
                _pendingRecommendations[data.ObjectId] = recommendation;
            }

            RecordRead(0, sw.Elapsed.TotalMilliseconds);
            return recommendation;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<TierMoveResult> MoveToTierAsync(string objectId, StorageTier targetTier, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await MoveCoreAsync(objectId, targetTier, ct);
            sw.Stop();

            if (result.Success)
            {
                lock (_statsLock)
                {
                    _totalObjectsMoved++;
                    _totalBytesMoved += result.BytesTransferred;
                }

                _pendingRecommendations.TryRemove(objectId, out _);
                UpdateTierCounts(result.SourceTier, result.TargetTier, result.BytesTransferred);
            }
            else
            {
                Interlocked.Increment(ref _failedMoves);
            }

            RecordWrite(result.BytesTransferred, sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Interlocked.Increment(ref _failedMoves);
            RecordFailure();
            return TierMoveResult.Failed(objectId, StorageTier.Hot, targetTier, ex.Message);
        }
    }

    /// <inheritdoc/>
    public virtual async Task<IEnumerable<TierRecommendation>> BatchEvaluateAsync(
        IEnumerable<DataObject> data,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(data);

        var recommendations = new List<TierRecommendation>();
        var sw = Stopwatch.StartNew();

        foreach (var item in data)
        {
            ct.ThrowIfCancellationRequested();
            var recommendation = await EvaluateCoreAsync(item, ct);
            recommendations.Add(recommendation);

            if (recommendation.RequiresMove)
            {
                _pendingRecommendations[item.ObjectId] = recommendation;
            }
        }

        sw.Stop();

        lock (_statsLock)
        {
            _totalObjectsEvaluated += recommendations.Count;
            _totalEvaluationTimeMs += sw.Elapsed.TotalMilliseconds;
            _lastEvaluationAt = DateTime.UtcNow;
        }

        return recommendations;
    }

    /// <inheritdoc/>
    public Task<TieringStats> GetStatsAsync(CancellationToken ct = default)
    {
        var evaluationCount = Interlocked.Read(ref _totalObjectsEvaluated);

        var stats = new TieringStats
        {
            TotalObjectsEvaluated = evaluationCount,
            TotalObjectsMoved = Interlocked.Read(ref _totalObjectsMoved),
            TotalBytesMoved = Interlocked.Read(ref _totalBytesMoved),
            ObjectsPerTier = _objectsPerTier.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            BytesPerTier = _bytesPerTier.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            PendingRecommendations = _pendingRecommendations.Count,
            FailedMoves = Interlocked.Read(ref _failedMoves),
            LastEvaluationAt = _lastEvaluationAt,
            AverageEvaluationTimeMs = evaluationCount > 0 ? _totalEvaluationTimeMs / evaluationCount : 0
        };

        // Calculate estimated savings from pending recommendations
        stats.TotalEstimatedMonthlySavings = _pendingRecommendations.Values
            .Where(r => r.EstimatedMonthlySavings.HasValue)
            .Sum(r => r.EstimatedMonthlySavings!.Value);

        return Task.FromResult(stats);
    }

    /// <summary>
    /// Core evaluation logic. Must be implemented by derived classes.
    /// </summary>
    /// <param name="data">The data object to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tier recommendation.</returns>
    protected abstract Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct);

    /// <summary>
    /// Core move logic. Override to customize tier movement behavior.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <param name="targetTier">The target storage tier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result of the move operation.</returns>
    protected virtual async Task<TierMoveResult> MoveCoreAsync(string objectId, StorageTier targetTier, CancellationToken ct)
    {
        if (TierHandler == null)
        {
            return TierMoveResult.Failed(objectId, StorageTier.Hot, targetTier, "No tier handler configured");
        }

        return await TierHandler.MoveToTierAsync(objectId, targetTier, ct);
    }

    /// <summary>
    /// Creates a recommendation with no tier change.
    /// </summary>
    protected TierRecommendation NoChange(DataObject data, string reason, double confidence = 1.0) =>
        new(data.ObjectId, data.CurrentTier, data.CurrentTier, reason, confidence);

    /// <summary>
    /// Creates a recommendation for tier promotion (moving to hotter tier).
    /// </summary>
    protected TierRecommendation Promote(DataObject data, StorageTier targetTier, string reason, double confidence, double priority = 0.5) =>
        new(data.ObjectId, data.CurrentTier, targetTier, reason, confidence)
        {
            Priority = priority
        };

    /// <summary>
    /// Creates a recommendation for tier demotion (moving to colder tier).
    /// </summary>
    protected TierRecommendation Demote(DataObject data, StorageTier targetTier, string reason, double confidence, double priority = 0.5, decimal? savings = null) =>
        new(data.ObjectId, data.CurrentTier, targetTier, reason, confidence)
        {
            Priority = priority,
            EstimatedMonthlySavings = savings
        };

    /// <summary>
    /// Updates the tier counts when an object is moved.
    /// </summary>
    private void UpdateTierCounts(StorageTier sourceTier, StorageTier targetTier, long bytes)
    {
        _objectsPerTier.AddOrUpdate(sourceTier, -1, (_, v) => v - 1);
        _objectsPerTier.AddOrUpdate(targetTier, 1, (_, v) => v + 1);
        _bytesPerTier.AddOrUpdate(sourceTier, -bytes, (_, v) => v - bytes);
        _bytesPerTier.AddOrUpdate(targetTier, bytes, (_, v) => v + bytes);
    }

    /// <summary>
    /// Registers an object in the tier tracking system.
    /// </summary>
    protected void RegisterObject(DataObject data)
    {
        _objectsPerTier.AddOrUpdate(data.CurrentTier, 1, (_, v) => v + 1);
        _bytesPerTier.AddOrUpdate(data.CurrentTier, data.SizeBytes, (_, v) => v + data.SizeBytes);
    }

    /// <summary>
    /// Gets the pending recommendation for an object.
    /// </summary>
    protected TierRecommendation? GetPendingRecommendation(string objectId) =>
        _pendingRecommendations.TryGetValue(objectId, out var rec) ? rec : null;

    /// <summary>
    /// Estimates monthly cost savings for moving from source to target tier.
    /// </summary>
    protected static decimal EstimateMonthlySavings(long sizeBytes, StorageTier sourceTier, StorageTier targetTier)
    {
        // Estimated costs per GB per month
        var costPerGb = new Dictionary<StorageTier, decimal>
        {
            [StorageTier.Hot] = 0.023m,
            [StorageTier.Warm] = 0.0125m,
            [StorageTier.Cold] = 0.004m,
            [StorageTier.Archive] = 0.00099m,
            [StorageTier.Glacier] = 0.0004m
        };

        var sizeGb = sizeBytes / (1024m * 1024m * 1024m);
        var sourceCost = sizeGb * costPerGb.GetValueOrDefault(sourceTier, 0.023m);
        var targetCost = sizeGb * costPerGb.GetValueOrDefault(targetTier, 0.023m);

        return Math.Max(0, sourceCost - targetCost);
    }
}

/// <summary>
/// Interface for handling actual storage tier operations.
/// </summary>
public interface IStorageTierHandler
{
    /// <summary>
    /// Gets the current tier of an object.
    /// </summary>
    Task<StorageTier> GetCurrentTierAsync(string objectId, CancellationToken ct = default);

    /// <summary>
    /// Moves an object to a target tier.
    /// </summary>
    Task<TierMoveResult> MoveToTierAsync(string objectId, StorageTier targetTier, CancellationToken ct = default);

    /// <summary>
    /// Gets metadata for an object.
    /// </summary>
    Task<DataObject?> GetObjectMetadataAsync(string objectId, CancellationToken ct = default);
}

/// <summary>
/// In-memory storage tier handler for testing and simulation.
/// </summary>
public sealed class InMemoryStorageTierHandler : IStorageTierHandler
{
    private readonly ConcurrentDictionary<string, DataObject> _objects = new();
    private readonly ConcurrentDictionary<string, StorageTier> _tiers = new();

    /// <summary>
    /// Registers an object with its initial tier.
    /// </summary>
    public void RegisterObject(DataObject obj)
    {
        _objects[obj.ObjectId] = obj;
        _tiers[obj.ObjectId] = obj.CurrentTier;
    }

    /// <inheritdoc/>
    public Task<StorageTier> GetCurrentTierAsync(string objectId, CancellationToken ct = default)
    {
        if (_tiers.TryGetValue(objectId, out var tier))
        {
            return Task.FromResult(tier);
        }
        return Task.FromResult(StorageTier.Hot);
    }

    /// <inheritdoc/>
    public Task<TierMoveResult> MoveToTierAsync(string objectId, StorageTier targetTier, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        if (!_tiers.TryGetValue(objectId, out var sourceTier))
        {
            return Task.FromResult(TierMoveResult.Failed(objectId, StorageTier.Hot, targetTier, "Object not found"));
        }

        _tiers[objectId] = targetTier;
        var bytesTransferred = _objects.TryGetValue(objectId, out var obj) ? obj.SizeBytes : 0;

        sw.Stop();
        return Task.FromResult(TierMoveResult.Succeeded(objectId, sourceTier, targetTier, sw.Elapsed, bytesTransferred));
    }

    /// <inheritdoc/>
    public Task<DataObject?> GetObjectMetadataAsync(string objectId, CancellationToken ct = default)
    {
        _objects.TryGetValue(objectId, out var obj);
        return Task.FromResult(obj);
    }
}
