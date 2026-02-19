using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.ChaosVaccination;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.ChaosVaccination.Storage;

/// <summary>
/// In-memory implementation of <see cref="IChaosResultsDatabase"/> for single-node deployments.
///
/// Features:
/// - Thread-safe storage using ConcurrentDictionary keyed by experiment ID
/// - Bounded capacity with FIFO eviction (configurable, default 100,000 records)
/// - Filtered queries with date range, fault type, status, and plugin ID filters
/// - Aggregate summary statistics (success rate, avg recovery, fault distribution, blast radius distribution)
/// - Purge by age for data lifecycle management
///
/// Note: This is the in-memory single-node implementation following the SDK pattern from Phase 26.
/// Distributed/persistent implementations follow the same IChaosResultsDatabase interface in separate storage plugins.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Vaccination scheduler")]
public sealed class InMemoryChaosResultsDatabase : IChaosResultsDatabase
{
    private readonly ConcurrentDictionary<string, ExperimentRecord> _records = new();
    private readonly ConcurrentQueue<string> _insertionOrder = new();
    private readonly IMessageBus? _messageBus;
    private readonly int _maxRecords;
    private readonly object _evictionLock = new();

    /// <summary>
    /// Initializes a new <see cref="InMemoryChaosResultsDatabase"/>.
    /// </summary>
    /// <param name="messageBus">Optional message bus for result event notifications.</param>
    /// <param name="maxRecords">Maximum number of records to store before FIFO eviction. Default: 100,000.</param>
    public InMemoryChaosResultsDatabase(IMessageBus? messageBus = null, int maxRecords = 100_000)
    {
        _messageBus = messageBus;
        _maxRecords = maxRecords > 0 ? maxRecords : throw new ArgumentOutOfRangeException(nameof(maxRecords), "Max records must be positive.");
    }

    /// <summary>
    /// Gets the current number of stored records.
    /// </summary>
    public int Count => _records.Count;

    /// <inheritdoc/>
    public async Task StoreAsync(ExperimentRecord record, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);

        if (record.Result == null)
            throw new ArgumentException("ExperimentRecord.Result cannot be null.", nameof(record));

        var key = record.Result.ExperimentId;

        // Add record and track insertion order
        _records[key] = record;

        lock (_evictionLock)
        {
            _insertionOrder.Enqueue(key);

            // Evict oldest records if over capacity
            while (_records.Count > _maxRecords && _insertionOrder.TryDequeue(out var oldestKey))
            {
                _records.TryRemove(oldestKey, out _);
            }
        }

        // Publish storage event
        if (_messageBus != null)
        {
            await _messageBus.PublishAsync("chaos.results.stored", new PluginMessage
            {
                Type = "chaos.results.stored",
                SourcePluginId = "com.datawarehouse.chaos.vaccination",
                Payload = new Dictionary<string, object>
                {
                    ["experimentId"] = key,
                    ["status"] = record.Result.Status.ToString(),
                    ["createdAt"] = record.CreatedAt.ToString("O")
                }
            }, ct);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ExperimentRecord>> QueryAsync(ExperimentQuery query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(query);

        var results = ApplyFilters(_records.Values, query);

        // Order by CreatedAt
        results = query.OrderByDescending
            ? results.OrderByDescending(r => r.CreatedAt)
            : results.OrderBy(r => r.CreatedAt);

        // Apply MaxResults limit
        var list = results.Take(query.MaxResults).ToList();

        return Task.FromResult<IReadOnlyList<ExperimentRecord>>(list);
    }

    /// <inheritdoc/>
    public Task<ExperimentRecord?> GetByIdAsync(string experimentId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        _records.TryGetValue(experimentId, out var record);
        return Task.FromResult(record);
    }

    /// <inheritdoc/>
    public Task<ExperimentSummary> GetSummaryAsync(ExperimentQuery? filter, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        IEnumerable<ExperimentRecord> records = _records.Values;

        if (filter != null)
        {
            records = ApplyFilters(records, filter);
        }

        var materializedRecords = records.ToList();
        var totalExperiments = materializedRecords.Count;

        if (totalExperiments == 0)
        {
            return Task.FromResult(new ExperimentSummary
            {
                TotalExperiments = 0,
                SuccessRate = 0.0,
                AverageRecoveryMs = 0.0,
                MostCommonFaults = new Dictionary<FaultType, int>(),
                BlastRadiusDistribution = new Dictionary<BlastRadiusLevel, int>()
            });
        }

        // Success rate: Completed / Total
        var completedCount = materializedRecords.Count(r => r.Result.Status == ExperimentStatus.Completed);
        var successRate = (double)completedCount / totalExperiments;

        // Average recovery time (only for records with measured recovery)
        var recoveryTimes = materializedRecords
            .Where(r => r.Result.RecoveryTimeMs.HasValue)
            .Select(r => (double)r.Result.RecoveryTimeMs!.Value)
            .ToList();
        var averageRecoveryMs = recoveryTimes.Count > 0 ? recoveryTimes.Average() : 0.0;

        // Most common fault types -- we need the FaultType from the experiment,
        // but ExperimentRecord only has ChaosExperimentResult which doesn't carry FaultType.
        // We use the FaultSignature.FaultType if available, otherwise skip.
        var faultDistribution = materializedRecords
            .Where(r => r.Result.FaultSignature != null)
            .GroupBy(r => r.Result.FaultSignature!.FaultType)
            .ToDictionary(g => g.Key, g => g.Count());

        // Blast radius distribution
        var blastRadiusDistribution = materializedRecords
            .GroupBy(r => r.Result.ActualBlastRadius)
            .ToDictionary(g => g.Key, g => g.Count());

        return Task.FromResult(new ExperimentSummary
        {
            TotalExperiments = totalExperiments,
            SuccessRate = successRate,
            AverageRecoveryMs = averageRecoveryMs,
            MostCommonFaults = faultDistribution,
            BlastRadiusDistribution = blastRadiusDistribution
        });
    }

    /// <inheritdoc/>
    public Task PurgeAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Remove all records with CreatedAt < olderThan
        var keysToRemove = _records
            .Where(kvp => kvp.Value.CreatedAt < olderThan)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _records.TryRemove(key, out _);
        }

        // Rebuild the insertion order queue to exclude purged keys
        lock (_evictionLock)
        {
            var remaining = new List<string>();
            while (_insertionOrder.TryDequeue(out var key))
            {
                if (_records.ContainsKey(key))
                    remaining.Add(key);
            }

            foreach (var key in remaining)
            {
                _insertionOrder.Enqueue(key);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies query filters to a collection of records.
    /// Pre-filters on date range first for performance, then applies remaining filters.
    /// </summary>
    private static IEnumerable<ExperimentRecord> ApplyFilters(IEnumerable<ExperimentRecord> records, ExperimentQuery query)
    {
        // Pre-filter on date range first (most selective for large datasets)
        if (query.Since.HasValue)
            records = records.Where(r => r.CreatedAt >= query.Since.Value);

        if (query.Until.HasValue)
            records = records.Where(r => r.CreatedAt < query.Until.Value);

        // Filter by fault types (using FaultSignature if available)
        if (query.FaultTypes is { Length: > 0 })
        {
            var faultTypeSet = new HashSet<FaultType>(query.FaultTypes);
            records = records.Where(r =>
                r.Result.FaultSignature != null &&
                faultTypeSet.Contains(r.Result.FaultSignature.FaultType));
        }

        // Filter by statuses
        if (query.Statuses is { Length: > 0 })
        {
            var statusSet = new HashSet<ExperimentStatus>(query.Statuses);
            records = records.Where(r => statusSet.Contains(r.Result.Status));
        }

        // Filter by plugin IDs (intersection with AffectedPlugins)
        if (query.PluginIds is { Length: > 0 })
        {
            var pluginIdSet = new HashSet<string>(query.PluginIds, StringComparer.OrdinalIgnoreCase);
            records = records.Where(r =>
                r.Result.AffectedPlugins.Any(p => pluginIdSet.Contains(p)));
        }

        return records;
    }
}
