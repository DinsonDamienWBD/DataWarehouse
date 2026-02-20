using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Carbon;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.GreenTiering;

/// <summary>
/// Represents a data object identified as cold (infrequently accessed) and currently
/// stored on a backend with a green score below the tenant's target.
/// </summary>
public sealed record GreenMigrationCandidate
{
    /// <summary>
    /// Storage key identifying the data object.
    /// </summary>
    public required string ObjectKey { get; init; }

    /// <summary>
    /// Identifier of the backend currently hosting this object.
    /// </summary>
    public required string CurrentBackendId { get; init; }

    /// <summary>
    /// Current backend's green sustainability score (0-100).
    /// </summary>
    public required double CurrentGreenScore { get; init; }

    /// <summary>
    /// Size of the object in bytes.
    /// </summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Timestamp of the last access to this object.
    /// </summary>
    public required DateTimeOffset LastAccessed { get; init; }

    /// <summary>
    /// Tenant that owns this data object.
    /// </summary>
    public required string TenantId { get; init; }
}

/// <summary>
/// A planned batch of migrations from high-carbon to low-carbon backends,
/// with estimated carbon and energy costs.
/// </summary>
public sealed record GreenMigrationBatch
{
    /// <summary>
    /// Migration candidates included in this batch.
    /// </summary>
    public required IReadOnlyList<GreenMigrationCandidate> Candidates { get; init; }

    /// <summary>
    /// Target backend to migrate data to.
    /// </summary>
    public required string TargetBackendId { get; init; }

    /// <summary>
    /// Green score of the target backend (0-100).
    /// </summary>
    public required double TargetGreenScore { get; init; }

    /// <summary>
    /// Estimated carbon cost of performing the migration itself (gCO2e).
    /// Transferring data consumes energy, which has a carbon cost.
    /// </summary>
    public required double EstimatedCarbonCostGrams { get; init; }

    /// <summary>
    /// Estimated energy cost of the migration (Wh).
    /// </summary>
    public required double EstimatedEnergyWh { get; init; }

    /// <summary>
    /// When this batch is scheduled to execute.
    /// </summary>
    public required DateTimeOffset ScheduledFor { get; init; }

    /// <summary>
    /// Tenant that owns the data being migrated.
    /// </summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// Total size of all candidates in bytes.
    /// </summary>
    public long TotalSizeBytes => Candidates.Sum(c => c.SizeBytes);
}

/// <summary>
/// Core green tiering orchestrator. Identifies cold data on high-carbon backends,
/// scores available green backends, plans migration batches respecting carbon budgets,
/// and schedules migrations during low-carbon windows.
///
/// Communication:
/// - Requests object listings via <c>storage.list</c> message bus topic
/// - Queries green scores via <c>sustainability.placement.scores</c>
/// - Checks carbon budgets via <c>sustainability.carbon.budget.evaluate</c>
/// - Publishes migration batches to <c>sustainability.green-tiering.batch.planned</c>
///
/// Background timer runs every 15 minutes to scan enabled tenants.
/// </summary>
public sealed class GreenTieringStrategy : SustainabilityStrategyBase
{
    private readonly GreenTieringPolicyEngine _policyEngine;
    private readonly ConcurrentQueue<GreenMigrationBatch> _pendingBatches = new();
    private readonly BoundedDictionary<string, DateTimeOffset> _lastScanTimes = new BoundedDictionary<string, DateTimeOffset>(1000);
    private Timer? _scanTimer;
    private volatile bool _scanning;

    // Energy cost of network transfer: ~0.0000006 kWh per MB (approximate for modern data centers)
    private const double NetworkEnergyKwhPerByte = 0.0000006 / (1024.0 * 1024.0);

    // Default carbon intensity for migration energy estimation (gCO2e/kWh)
    private const double DefaultMigrationCarbonIntensity = 400.0;

    private const string PluginId = "com.datawarehouse.sustainability.ultimate";

    /// <inheritdoc/>
    public override string StrategyId => "green-tiering";

    /// <inheritdoc/>
    public override string DisplayName => "Green Tiering";

    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.Scheduling;

    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.Scheduling |
        SustainabilityCapabilities.CarbonCalculation |
        SustainabilityCapabilities.ActiveControl;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Automatically identifies cold (infrequently accessed) data and plans migration " +
        "to the lowest-carbon storage backends available. Respects per-tenant carbon budgets " +
        "and schedules migrations during renewable energy windows for maximum sustainability impact.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "green", "tiering", "cold-data", "migration", "carbon", "scheduling", "placement", "sustainability"
    };

    /// <summary>
    /// Creates a new green tiering strategy.
    /// </summary>
    /// <param name="policyEngine">Policy engine managing per-tenant tiering configuration.</param>
    public GreenTieringStrategy(GreenTieringPolicyEngine policyEngine)
    {
        ArgumentNullException.ThrowIfNull(policyEngine);
        _policyEngine = policyEngine;
    }

    /// <summary>
    /// Gets the policy engine used by this strategy.
    /// </summary>
    public GreenTieringPolicyEngine PolicyEngine => _policyEngine;

    /// <summary>
    /// Gets the number of pending migration batches queued for execution.
    /// </summary>
    public int PendingBatchCount => _pendingBatches.Count;

    /// <summary>
    /// Gets all pending migration batches.
    /// </summary>
    public IReadOnlyList<GreenMigrationBatch> GetPendingBatches()
    {
        return _pendingBatches.ToArray().ToList().AsReadOnly();
    }

    /// <summary>
    /// Dequeues the next pending migration batch for execution.
    /// </summary>
    /// <returns>The next batch, or null if no batches are pending.</returns>
    public GreenMigrationBatch? DequeueBatch()
    {
        return _pendingBatches.TryDequeue(out var batch) ? batch : null;
    }

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        // Background scan every 15 minutes
        _scanTimer = new Timer(
            async _ => await RunScanCycleAsync(),
            null,
            TimeSpan.FromMinutes(1),   // Initial delay: 1 minute after startup
            TimeSpan.FromMinutes(15)); // Repeat every 15 minutes

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _scanTimer?.Dispose();
        _scanTimer = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Identifies cold data for a tenant that is currently stored on high-carbon backends.
    /// Queries the storage layer via message bus for object metadata including last-access times.
    /// </summary>
    /// <param name="tenantId">Tenant to scan for cold data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of migration candidates ordered by size (largest first).</returns>
    public async Task<IReadOnlyList<GreenMigrationCandidate>> IdentifyColdDataAsync(
        string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ThrowIfNotInitialized();

        var policy = _policyEngine.GetPolicy(tenantId);
        if (!policy.Enabled)
            return Array.Empty<GreenMigrationCandidate>();

        var candidates = new List<GreenMigrationCandidate>();
        var coldCutoff = DateTimeOffset.UtcNow - policy.ColdThreshold;

        // Request all objects for tenant with last-access metadata via storage.list
        if (MessageBus == null)
            return candidates.AsReadOnly();

        try
        {
            var listResponse = await MessageBus.SendAsync(
                "storage.list",
                new PluginMessage
                {
                    Type = "storage.list",
                    SourcePluginId = PluginId,
                    Payload = new Dictionary<string, object>
                    {
                        ["tenantId"] = tenantId,
                        ["includeMetadata"] = true,
                        ["metadataFields"] = "lastAccessed,size,backendId"
                    },
                    Description = $"List all objects for tenant {tenantId} with access metadata for green tiering scan"
                },
                TimeSpan.FromSeconds(30),
                ct);

            if (!listResponse.Success || listResponse.Payload is not IEnumerable<Dictionary<string, object>> objects)
                return candidates.AsReadOnly();

            // Request current green scores for all backends
            var greenScores = await GetBackendGreenScoresAsync(ct);

            foreach (var obj in objects)
            {
                if (!TryExtractObjectInfo(obj, out var objectKey, out var backendId, out var sizeBytes, out var lastAccessed))
                    continue;

                // Check if data is cold
                if (lastAccessed >= coldCutoff)
                    continue;

                // Check current backend's green score
                var currentGreenScore = greenScores.TryGetValue(backendId, out var score) ? score : 0.0;

                // Only consider migration if current green score is below target
                if (currentGreenScore >= policy.TargetGreenScore)
                    continue;

                candidates.Add(new GreenMigrationCandidate
                {
                    ObjectKey = objectKey,
                    CurrentBackendId = backendId,
                    CurrentGreenScore = currentGreenScore,
                    SizeBytes = sizeBytes,
                    LastAccessed = lastAccessed,
                    TenantId = tenantId
                });
            }

            RecordSample(0, 0); // Record scan activity
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Storage listing failed -- return empty candidates rather than crashing
        }

        return candidates.OrderByDescending(c => c.SizeBytes).ToList().AsReadOnly();
    }

    /// <summary>
    /// Plans a migration batch from the given candidates, selecting the best green backend
    /// and respecting carbon budget constraints.
    /// </summary>
    /// <param name="candidates">Cold data candidates to consider for migration.</param>
    /// <param name="tenantId">Tenant owning the data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A planned migration batch, or null if no viable migration exists.</returns>
    public async Task<GreenMigrationBatch?> PlanMigrationBatchAsync(
        IReadOnlyList<GreenMigrationCandidate> candidates,
        string tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ThrowIfNotInitialized();

        if (candidates.Count == 0)
            return null;

        var policy = _policyEngine.GetPolicy(tenantId);

        // Get available green backends via bus
        var greenScores = await GetBackendGreenScoresAsync(ct);

        // Select the best target backend (highest score meeting threshold, excluding current backends)
        var currentBackendIds = candidates.Select(c => c.CurrentBackendId).Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bestTarget = greenScores
            .Where(kv => kv.Value >= policy.TargetGreenScore && !currentBackendIds.Contains(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .FirstOrDefault();

        if (bestTarget.Key == null)
            return null; // No suitable green backend available

        // Batch candidates up to MaxMigrationBatchSizeBytes
        var batchCandidates = new List<GreenMigrationCandidate>();
        long batchSize = 0;

        foreach (var candidate in candidates)
        {
            if (batchSize + candidate.SizeBytes > policy.MaxMigrationBatchSizeBytes)
                break;

            batchCandidates.Add(candidate);
            batchSize += candidate.SizeBytes;
        }

        if (batchCandidates.Count == 0)
            return null;

        // Estimate carbon cost of the migration itself
        var migrationEnergyWh = batchSize * NetworkEnergyKwhPerByte * 1000.0; // Convert kWh to Wh
        var migrationCarbonGrams = migrationEnergyWh / 1000.0 * DefaultMigrationCarbonIntensity;

        // Check carbon budget if required
        if (policy.RespectCarbonBudget && MessageBus != null)
        {
            try
            {
                var budgetResponse = await MessageBus.SendAsync(
                    "sustainability.carbon.budget.evaluate",
                    new PluginMessage
                    {
                        Type = "sustainability.carbon.budget.evaluate",
                        SourcePluginId = PluginId,
                        Payload = new Dictionary<string, object>
                        {
                            ["tenantId"] = tenantId,
                            ["estimatedCarbonGramsCO2e"] = migrationCarbonGrams,
                            ["operationType"] = "green-tiering-migration"
                        },
                        Description = $"Check if tenant {tenantId} has sufficient carbon budget for green tiering migration"
                    },
                    TimeSpan.FromSeconds(10),
                    ct);

                if (budgetResponse.Success)
                {
                    // Check if the budget evaluation returned a throttle decision
                    if (budgetResponse.Payload is CarbonThrottleDecision throttleDecision)
                    {
                        if (throttleDecision.ThrottleLevel == ThrottleLevel.Hard)
                            return null; // Budget exhausted, skip migration
                    }
                    else if (budgetResponse.Payload is Dictionary<string, object> payloadDict)
                    {
                        if (payloadDict.TryGetValue("throttleLevel", out var level) &&
                            level?.ToString() == "Hard")
                            return null;
                    }
                }
            }
            catch
            {
                // Budget check failed -- proceed conservatively (skip migration)
                return null;
            }
        }

        // Determine schedule time
        var scheduledFor = await DetermineScheduleTimeAsync(policy.MigrationSchedule, ct);

        var batch = new GreenMigrationBatch
        {
            Candidates = batchCandidates.AsReadOnly(),
            TargetBackendId = bestTarget.Key,
            TargetGreenScore = bestTarget.Value,
            EstimatedCarbonCostGrams = migrationCarbonGrams,
            EstimatedEnergyWh = migrationEnergyWh,
            ScheduledFor = scheduledFor,
            TenantId = tenantId
        };

        RecordWorkloadScheduled();
        return batch;
    }

    /// <summary>
    /// Determines whether a migration should execute now based on the configured schedule.
    /// </summary>
    /// <param name="schedule">The migration schedule to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if migration should proceed now; false if it should be deferred.</returns>
    public async Task<bool> ShouldMigrateNowAsync(GreenMigrationSchedule schedule, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        switch (schedule)
        {
            case GreenMigrationSchedule.Immediate:
                return true;

            case GreenMigrationSchedule.LowCarbonWindowOnly:
                return await IsLowCarbonWindowAsync(ct);

            case GreenMigrationSchedule.DailyBatch:
                // Default batch hour: 02:00 UTC -- check if within the batch window (2 hour window)
                return now.Hour >= 2 && now.Hour < 4;

            case GreenMigrationSchedule.WeeklyBatch:
                // Default: Sunday 02:00-04:00 UTC
                return now.DayOfWeek == DayOfWeek.Sunday && now.Hour >= 2 && now.Hour < 4;

            default:
                return false;
        }
    }

    /// <inheritdoc/>
    public override Task<IReadOnlyList<SustainabilityRecommendation>> GetRecommendationsAsync(
        CancellationToken ct = default)
    {
        var recommendations = new List<SustainabilityRecommendation>();

        var pendingCount = _pendingBatches.Count;
        if (pendingCount > 0)
        {
            var totalSize = _pendingBatches.ToArray().Sum(b => b.TotalSizeBytes);
            recommendations.Add(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-pending-batches",
                Type = "PendingMigrations",
                Priority = 6,
                Description = $"{pendingCount} migration batches pending, totaling {totalSize / (1024.0 * 1024.0 * 1024.0):F2} GB of cold data to migrate to green backends.",
                EstimatedCarbonReductionGrams = _pendingBatches.ToArray().Sum(b => b.EstimatedCarbonCostGrams),
                CanAutoApply = true,
                Action = "execute-pending-batches"
            });
        }

        var enabledTenants = _policyEngine.GetEnabledTenants();
        if (enabledTenants.Count == 0)
        {
            recommendations.Add(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-no-tenants",
                Type = "Configuration",
                Priority = 4,
                Description = "No tenants have green tiering enabled. Configure policies to automatically move cold data to low-carbon backends.",
                CanAutoApply = false,
                Action = "configure-green-tiering"
            });
        }

        return Task.FromResult<IReadOnlyList<SustainabilityRecommendation>>(recommendations.AsReadOnly());
    }

    private async Task RunScanCycleAsync()
    {
        if (_scanning || MessageBus == null) return;

        _scanning = true;
        try
        {
            var enabledTenants = _policyEngine.GetEnabledTenants();

            foreach (var tenantId in enabledTenants)
            {
                try
                {
                    var policy = _policyEngine.GetPolicy(tenantId);

                    // Check if we should migrate now based on schedule
                    if (!await ShouldMigrateNowAsync(policy.MigrationSchedule, CancellationToken.None))
                        continue;

                    // Identify cold data
                    var candidates = await IdentifyColdDataAsync(tenantId, CancellationToken.None);
                    if (candidates.Count == 0)
                        continue;

                    // Plan migration batch
                    var batch = await PlanMigrationBatchAsync(candidates, tenantId, CancellationToken.None);
                    if (batch == null)
                        continue;

                    // Queue batch for execution
                    _pendingBatches.Enqueue(batch);

                    // Publish batch planned event
                    await MessageBus.PublishAsync(
                        "sustainability.green-tiering.batch.planned",
                        new PluginMessage
                        {
                            Type = "sustainability.green-tiering.batch.planned",
                            SourcePluginId = PluginId,
                            Payload = new Dictionary<string, object>
                            {
                                ["tenantId"] = tenantId,
                                ["candidateCount"] = batch.Candidates.Count,
                                ["totalSizeBytes"] = batch.TotalSizeBytes,
                                ["targetBackendId"] = batch.TargetBackendId,
                                ["targetGreenScore"] = batch.TargetGreenScore,
                                ["estimatedCarbonCostGrams"] = batch.EstimatedCarbonCostGrams,
                                ["scheduledFor"] = batch.ScheduledFor.ToString("O")
                            },
                            Description = $"Green tiering batch planned for tenant {tenantId}: " +
                                          $"{batch.Candidates.Count} objects ({batch.TotalSizeBytes / (1024.0 * 1024.0):F1} MB) " +
                                          $"to backend {batch.TargetBackendId} (score {batch.TargetGreenScore:F1})"
                        },
                        CancellationToken.None);

                    _lastScanTimes[tenantId] = DateTimeOffset.UtcNow;
                    RecordOptimizationAction();
                }
                catch
                {
                    // Individual tenant scan failure should not stop other tenants
                }
            }

            // Prune pending batches queue (keep max 100)
            while (_pendingBatches.Count > 100)
                _pendingBatches.TryDequeue(out _);
        }
        finally
        {
            _scanning = false;
        }
    }

    private async Task<Dictionary<string, double>> GetBackendGreenScoresAsync(CancellationToken ct)
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (MessageBus == null)
            return scores;

        try
        {
            var response = await MessageBus.SendAsync(
                "sustainability.placement.scores",
                new PluginMessage
                {
                    Type = "sustainability.placement.scores",
                    SourcePluginId = PluginId,
                    Payload = new Dictionary<string, object>
                    {
                        ["requestType"] = "all-backends"
                    },
                    Description = "Request green sustainability scores for all storage backends"
                },
                TimeSpan.FromSeconds(15),
                ct);

            if (response.Success && response.Payload is Dictionary<string, object> payload)
            {
                foreach (var kvp in payload)
                {
                    if (kvp.Value is double score)
                        scores[kvp.Key] = score;
                    else if (kvp.Value is GreenScore greenScore)
                        scores[kvp.Key] = greenScore.Score;
                    else if (double.TryParse(kvp.Value?.ToString(), out var parsed))
                        scores[kvp.Key] = parsed;
                }
            }
        }
        catch
        {
            // Scores unavailable -- return empty
        }

        return scores;
    }

    private async Task<bool> IsLowCarbonWindowAsync(CancellationToken ct)
    {
        if (MessageBus == null)
        {
            // Fallback: estimate based on time of day (solar generation peaks midday)
            var hour = DateTimeOffset.UtcNow.Hour;
            return hour >= 10 && hour <= 16; // Daytime = typically more renewable
        }

        try
        {
            var response = await MessageBus.SendAsync(
                "sustainability.carbon.intensity.current",
                new PluginMessage
                {
                    Type = "sustainability.carbon.intensity.current",
                    SourcePluginId = PluginId,
                    Payload = new Dictionary<string, object>
                    {
                        ["includeAverage"] = true
                    },
                    Description = "Get current carbon intensity vs 24h average for low-carbon window detection"
                },
                TimeSpan.FromSeconds(10),
                ct);

            if (response.Success && response.Payload is Dictionary<string, object> data)
            {
                if (data.TryGetValue("currentIntensity", out var currentObj) &&
                    data.TryGetValue("averageIntensity24h", out var avgObj) &&
                    currentObj is double current &&
                    avgObj is double average)
                {
                    // Low carbon window: current intensity is below 24h average
                    return current < average;
                }
            }
        }
        catch
        {
            // Fall through to time-of-day estimation
        }

        var fallbackHour = DateTimeOffset.UtcNow.Hour;
        return fallbackHour >= 10 && fallbackHour <= 16;
    }

    private async Task<DateTimeOffset> DetermineScheduleTimeAsync(
        GreenMigrationSchedule schedule, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        return schedule switch
        {
            GreenMigrationSchedule.Immediate => now,
            GreenMigrationSchedule.LowCarbonWindowOnly => await IsLowCarbonWindowAsync(ct) ? now : now.AddHours(1),
            GreenMigrationSchedule.DailyBatch => GetNextBatchTime(now, 2, null),
            GreenMigrationSchedule.WeeklyBatch => GetNextBatchTime(now, 2, DayOfWeek.Sunday),
            _ => now
        };
    }

    private static DateTimeOffset GetNextBatchTime(DateTimeOffset now, int batchHour, DayOfWeek? batchDay)
    {
        var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, batchHour, 0, 0, TimeSpan.Zero);

        if (batchDay.HasValue)
        {
            // Find next matching day of week
            while (candidate.DayOfWeek != batchDay.Value || candidate <= now)
                candidate = candidate.AddDays(1);
        }
        else
        {
            // Daily: next occurrence at batch hour
            if (candidate <= now)
                candidate = candidate.AddDays(1);
        }

        return candidate;
    }

    private static bool TryExtractObjectInfo(
        Dictionary<string, object> obj,
        out string objectKey,
        out string backendId,
        out long sizeBytes,
        out DateTimeOffset lastAccessed)
    {
        objectKey = string.Empty;
        backendId = string.Empty;
        sizeBytes = 0;
        lastAccessed = DateTimeOffset.MinValue;

        if (!obj.TryGetValue("objectKey", out var keyObj) || keyObj is not string key || string.IsNullOrEmpty(key))
            return false;

        if (!obj.TryGetValue("backendId", out var bObj) || bObj is not string backend || string.IsNullOrEmpty(backend))
            return false;

        objectKey = key;
        backendId = backend;

        if (obj.TryGetValue("sizeBytes", out var sizeObj))
        {
            if (sizeObj is long l) sizeBytes = l;
            else if (sizeObj is int i) sizeBytes = i;
            else if (sizeObj is double d) sizeBytes = (long)d;
            else if (long.TryParse(sizeObj?.ToString(), out var parsed)) sizeBytes = parsed;
        }

        if (obj.TryGetValue("lastAccessed", out var accessObj))
        {
            if (accessObj is DateTimeOffset dto) lastAccessed = dto;
            else if (accessObj is DateTime dt) lastAccessed = new DateTimeOffset(dt, TimeSpan.Zero);
            else if (DateTimeOffset.TryParse(accessObj?.ToString(), out var parsedDto)) lastAccessed = parsedDto;
        }

        return sizeBytes > 0 && lastAccessed != DateTimeOffset.MinValue;
    }
}
