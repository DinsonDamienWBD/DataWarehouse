using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.GreenTiering;

/// <summary>
/// Record of a completed (or failed) data migration from a high-carbon to a low-carbon backend.
/// </summary>
public sealed record MigrationRecord
{
    /// <summary>
    /// Storage key of the migrated object.
    /// </summary>
    public required string ObjectKey { get; init; }

    /// <summary>
    /// Backend the object was migrated from.
    /// </summary>
    public required string SourceBackendId { get; init; }

    /// <summary>
    /// Backend the object was migrated to.
    /// </summary>
    public required string TargetBackendId { get; init; }

    /// <summary>
    /// Size of the migrated object in bytes.
    /// </summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Carbon emissions avoided by moving to the greener backend (gCO2e).
    /// Accounts for the ongoing storage carbon difference over estimated lifetime.
    /// </summary>
    public required double CarbonSavedGrams { get; init; }

    /// <summary>
    /// Energy saved by moving to the greener backend (Wh).
    /// </summary>
    public required double EnergySavedWh { get; init; }

    /// <summary>
    /// UTC timestamp when the migration completed.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Whether the migration succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Error message if the migration failed; null on success.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Tenant that owns the migrated object.
    /// </summary>
    public required string TenantId { get; init; }
}

/// <summary>
/// Executes data migration batches planned by <see cref="GreenTieringStrategy"/>,
/// moving cold data from high-carbon to low-carbon storage backends.
///
/// Communication:
/// - Validates target backend health via <c>storage.health</c> message bus topic
/// - Triggers migrations via <c>storage.migrate.request</c> / <c>storage.migrate.response</c>
/// - Publishes batch completion to <c>sustainability.green-tiering.batch.complete</c>
///
/// Carbon savings calculation:
/// Uses the difference in carbon intensity between source and target backends,
/// multiplied by storage energy per byte and estimated data lifetime.
/// </summary>
public sealed class ColdDataCarbonMigrationStrategy : SustainabilityStrategyBase
{
    private readonly ConcurrentQueue<MigrationRecord> _migrationHistory = new();
    private readonly ConcurrentQueue<GreenMigrationCandidate> _retryQueue = new();
    private long _totalDataMigratedBytes;
    private double _totalCarbonSavedGrams;
    private long _totalMigrationErrors;
    private long _totalMigrationsCompleted;
    private readonly object _statsLock2 = new();

    // Storage energy: ~6 nWh per byte per year for SSD at rest
    private const double StorageEnergyKwhPerByteYear = 0.000000006;

    // Estimated lifetime of cold data in years for savings projection
    private const double EstimatedLifetimeYears = 3.0;

    // Maximum migration history entries to retain
    private const int MaxHistoryEntries = 10_000;

    private const string PluginId = "com.datawarehouse.sustainability.ultimate";

    /// <inheritdoc/>
    public override string StrategyId => "cold-data-carbon-migration";

    /// <inheritdoc/>
    public override string DisplayName => "Cold Data Carbon Migration";

    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.Scheduling;

    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.ActiveControl |
        SustainabilityCapabilities.CarbonCalculation |
        SustainabilityCapabilities.Reporting;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Executes data migration from high-carbon to low-carbon storage backends. " +
        "Manages concurrent migrations with retry logic, tracks carbon savings per migration, " +
        "and generates recommendations based on migration patterns and queue depth.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "migration", "cold-data", "carbon", "storage", "green", "tiering", "reporting"
    };

    /// <summary>
    /// Gets the total amount of data migrated in bytes.
    /// </summary>
    public long TotalDataMigratedBytes
    {
        get { lock (_statsLock2) return _totalDataMigratedBytes; }
    }

    /// <summary>
    /// Gets the total carbon saved across all migrations (gCO2e).
    /// </summary>
    public double TotalCarbonSavedGrams
    {
        get { lock (_statsLock2) return _totalCarbonSavedGrams; }
    }

    /// <summary>
    /// Gets the total number of migration errors.
    /// </summary>
    public long TotalMigrationErrors
    {
        get { lock (_statsLock2) return _totalMigrationErrors; }
    }

    /// <summary>
    /// Gets the total number of completed migrations.
    /// </summary>
    public long TotalMigrationsCompleted
    {
        get { lock (_statsLock2) return _totalMigrationsCompleted; }
    }

    /// <summary>
    /// Gets the number of items in the retry queue.
    /// </summary>
    public int RetryQueueCount => _retryQueue.Count;

    /// <summary>
    /// Executes a migration batch, transferring data objects from their current backends
    /// to the specified green target backend with concurrency control.
    /// </summary>
    /// <param name="batch">The migration batch to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Summary of the batch execution including counts and carbon savings.</returns>
    public async Task<BatchExecutionResult> ExecuteBatchAsync(
        GreenMigrationBatch batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ThrowIfNotInitialized();

        if (MessageBus == null)
            return new BatchExecutionResult
            {
                TenantId = batch.TenantId,
                TargetBackendId = batch.TargetBackendId,
                TotalCandidates = batch.Candidates.Count,
                SuccessfulMigrations = 0,
                FailedMigrations = batch.Candidates.Count,
                TotalCarbonSavedGrams = 0,
                TotalEnergySavedWh = 0,
                TotalBytesMigrated = 0,
                ErrorMessage = "Message bus not available"
            };

        // Step 1: Validate target backend health
        var healthOk = await ValidateTargetHealthAsync(batch.TargetBackendId, ct);
        if (!healthOk)
        {
            return new BatchExecutionResult
            {
                TenantId = batch.TenantId,
                TargetBackendId = batch.TargetBackendId,
                TotalCandidates = batch.Candidates.Count,
                SuccessfulMigrations = 0,
                FailedMigrations = batch.Candidates.Count,
                TotalCarbonSavedGrams = 0,
                TotalEnergySavedWh = 0,
                TotalBytesMigrated = 0,
                ErrorMessage = $"Target backend {batch.TargetBackendId} health check failed"
            };
        }

        // Step 2: Execute migrations with concurrency control
        var maxConcurrent = 5; // Default; could be from policy
        using var semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);

        var migrationTasks = batch.Candidates.Select(async candidate =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                return await MigrateSingleObjectAsync(
                    candidate, batch.TargetBackendId, batch.TargetGreenScore, ct);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(migrationTasks);

        // Step 3: Aggregate results
        var successful = results.Count(r => r.Success);
        var failed = results.Count(r => !r.Success);
        var totalCarbonSaved = results.Where(r => r.Success).Sum(r => r.CarbonSavedGrams);
        var totalEnergySaved = results.Where(r => r.Success).Sum(r => r.EnergySavedWh);
        var totalBytes = results.Where(r => r.Success).Sum(r => r.SizeBytes);

        // Update aggregate statistics
        lock (_statsLock2)
        {
            _totalDataMigratedBytes += totalBytes;
            _totalCarbonSavedGrams += totalCarbonSaved;
            _totalMigrationErrors += failed;
            _totalMigrationsCompleted += successful;
        }

        RecordCarbonAvoided(totalCarbonSaved);
        RecordEnergySaved(totalEnergySaved);

        // Step 4: Publish batch completion event
        await PublishBatchCompletionAsync(batch, successful, failed, totalCarbonSaved, totalEnergySaved, totalBytes, ct);

        // Update recommendations
        await UpdateRecommendationsAsync();

        return new BatchExecutionResult
        {
            TenantId = batch.TenantId,
            TargetBackendId = batch.TargetBackendId,
            TotalCandidates = batch.Candidates.Count,
            SuccessfulMigrations = successful,
            FailedMigrations = failed,
            TotalCarbonSavedGrams = totalCarbonSaved,
            TotalEnergySavedWh = totalEnergySaved,
            TotalBytesMigrated = totalBytes
        };
    }

    /// <summary>
    /// Gets recent migration history, optionally filtered by tenant.
    /// </summary>
    /// <param name="tenantId">Optional tenant filter. Null returns all tenants.</param>
    /// <param name="maxEntries">Maximum entries to return.</param>
    /// <returns>Recent migration records ordered by timestamp descending.</returns>
    public IReadOnlyList<MigrationRecord> GetMigrationHistory(string? tenantId = null, int maxEntries = 100)
    {
        var records = _migrationHistory.ToArray().AsEnumerable();

        if (!string.IsNullOrWhiteSpace(tenantId))
            records = records.Where(r => string.Equals(r.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));

        return records
            .OrderByDescending(r => r.Timestamp)
            .Take(maxEntries)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Gets migration statistics for reporting.
    /// </summary>
    public MigrationStatistics GetMigrationStatistics()
    {
        lock (_statsLock2)
        {
            return new MigrationStatistics
            {
                TotalMigrationsCompleted = _totalMigrationsCompleted,
                TotalMigrationErrors = _totalMigrationErrors,
                TotalDataMigratedBytes = _totalDataMigratedBytes,
                TotalCarbonSavedGrams = _totalCarbonSavedGrams,
                RetryQueueSize = _retryQueue.Count,
                HistorySize = _migrationHistory.Count,
                EstimatedAnnualCarbonSavingsGrams = _totalCarbonSavedGrams / Math.Max(1, EstimatedLifetimeYears)
            };
        }
    }

    /// <inheritdoc/>
    public override Task<IReadOnlyList<SustainabilityRecommendation>> GetRecommendationsAsync(
        CancellationToken ct = default)
    {
        var recommendations = new List<SustainabilityRecommendation>();
        GenerateRecommendations(recommendations);
        return Task.FromResult<IReadOnlyList<SustainabilityRecommendation>>(recommendations.AsReadOnly());
    }

    private async Task<MigrationObjectResult> MigrateSingleObjectAsync(
        GreenMigrationCandidate candidate,
        string targetBackendId,
        double targetGreenScore,
        CancellationToken ct)
    {
        try
        {
            // Send migration request via message bus
            var response = await MessageBus!.SendAsync(
                "storage.migrate.request",
                new PluginMessage
                {
                    Type = "storage.migrate.request",
                    SourcePluginId = PluginId,
                    Payload = new Dictionary<string, object>
                    {
                        ["sourceBackendId"] = candidate.CurrentBackendId,
                        ["targetBackendId"] = targetBackendId,
                        ["objectKey"] = candidate.ObjectKey,
                        ["tenantId"] = candidate.TenantId,
                        ["sizeBytes"] = candidate.SizeBytes,
                        ["reason"] = "green-tiering"
                    },
                    Description = $"Migrate object {candidate.ObjectKey} from {candidate.CurrentBackendId} " +
                                  $"to {targetBackendId} for green tiering (tenant: {candidate.TenantId})"
                },
                TimeSpan.FromMinutes(5), // Large objects may take time
                ct);

            if (response.Success)
            {
                // Calculate carbon savings
                var (carbonSaved, energySaved) = CalculateCarbonSavings(
                    candidate.SizeBytes, candidate.CurrentGreenScore, targetGreenScore);

                var record = new MigrationRecord
                {
                    ObjectKey = candidate.ObjectKey,
                    SourceBackendId = candidate.CurrentBackendId,
                    TargetBackendId = targetBackendId,
                    SizeBytes = candidate.SizeBytes,
                    CarbonSavedGrams = carbonSaved,
                    EnergySavedWh = energySaved,
                    Timestamp = DateTimeOffset.UtcNow,
                    Success = true,
                    TenantId = candidate.TenantId
                };

                EnqueueHistory(record);
                RecordOptimizationAction();

                return new MigrationObjectResult
                {
                    Success = true,
                    SizeBytes = candidate.SizeBytes,
                    CarbonSavedGrams = carbonSaved,
                    EnergySavedWh = energySaved
                };
            }
            else
            {
                // Migration failed -- add to retry queue
                _retryQueue.Enqueue(candidate);

                var failRecord = new MigrationRecord
                {
                    ObjectKey = candidate.ObjectKey,
                    SourceBackendId = candidate.CurrentBackendId,
                    TargetBackendId = targetBackendId,
                    SizeBytes = candidate.SizeBytes,
                    CarbonSavedGrams = 0,
                    EnergySavedWh = 0,
                    Timestamp = DateTimeOffset.UtcNow,
                    Success = false,
                    ErrorMessage = response.ErrorMessage ?? "Migration request failed",
                    TenantId = candidate.TenantId
                };

                EnqueueHistory(failRecord);

                return new MigrationObjectResult
                {
                    Success = false,
                    SizeBytes = 0,
                    CarbonSavedGrams = 0,
                    EnergySavedWh = 0,
                    ErrorMessage = response.ErrorMessage
                };
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _retryQueue.Enqueue(candidate);

            var errorRecord = new MigrationRecord
            {
                ObjectKey = candidate.ObjectKey,
                SourceBackendId = candidate.CurrentBackendId,
                TargetBackendId = targetBackendId,
                SizeBytes = candidate.SizeBytes,
                CarbonSavedGrams = 0,
                EnergySavedWh = 0,
                Timestamp = DateTimeOffset.UtcNow,
                Success = false,
                ErrorMessage = ex.Message,
                TenantId = candidate.TenantId
            };

            EnqueueHistory(errorRecord);

            return new MigrationObjectResult
            {
                Success = false,
                SizeBytes = 0,
                CarbonSavedGrams = 0,
                EnergySavedWh = 0,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Calculates estimated carbon and energy savings from migrating data
    /// from a backend with the source green score to one with the target green score.
    /// </summary>
    /// <remarks>
    /// Formula:
    /// - sourceCarbonPerByte = (100 - sourceGreenScore) * carbonIntensityFactor * storageEnergyPerByte
    /// - targetCarbonPerByte = (100 - targetGreenScore) * carbonIntensityFactor * storageEnergyPerByte
    /// - savings = (sourceCarbonPerByte - targetCarbonPerByte) * sizeBytes * estimatedLifetimeYears
    ///
    /// The green score inversely correlates with carbon intensity:
    /// score 0 = 100% fossil, score 100 = 100% renewable.
    /// </remarks>
    private static (double carbonSavedGrams, double energySavedWh) CalculateCarbonSavings(
        long sizeBytes, double sourceGreenScore, double targetGreenScore)
    {
        // Carbon intensity factor: gCO2e per kWh (global average ~400)
        const double baseCarbonIntensity = 400.0;

        // Convert green score to effective carbon intensity
        // Score 100 = 0 carbon (all renewable), Score 0 = full carbon
        var sourceCarbonFraction = (100.0 - sourceGreenScore) / 100.0;
        var targetCarbonFraction = (100.0 - targetGreenScore) / 100.0;

        var sourceEffectiveIntensity = baseCarbonIntensity * sourceCarbonFraction;
        var targetEffectiveIntensity = baseCarbonIntensity * targetCarbonFraction;

        // Annual storage energy per byte (kWh)
        var annualEnergyKwh = StorageEnergyKwhPerByteYear * sizeBytes;

        // Carbon saved per year (gCO2e)
        var annualCarbonSaved = annualEnergyKwh * (sourceEffectiveIntensity - targetEffectiveIntensity);

        // Project over estimated lifetime
        var totalCarbonSaved = annualCarbonSaved * EstimatedLifetimeYears;
        var totalEnergySaved = annualEnergyKwh * EstimatedLifetimeYears * 1000.0; // Convert to Wh

        // Energy savings from lower PUE on green backends (rough estimate: 10% more efficient)
        var efficiencyBonus = totalEnergySaved * 0.1 * (targetGreenScore - sourceGreenScore) / 100.0;
        totalEnergySaved += efficiencyBonus;

        return (Math.Max(0, totalCarbonSaved), Math.Max(0, totalEnergySaved));
    }

    private async Task<bool> ValidateTargetHealthAsync(string targetBackendId, CancellationToken ct)
    {
        if (MessageBus == null) return false;

        try
        {
            var response = await MessageBus.SendAsync(
                "storage.health",
                new PluginMessage
                {
                    Type = "storage.health",
                    SourcePluginId = PluginId,
                    Payload = new Dictionary<string, object>
                    {
                        ["backendId"] = targetBackendId,
                        ["checkCapacity"] = true
                    },
                    Description = $"Health check for target backend {targetBackendId} before green tiering migration"
                },
                TimeSpan.FromSeconds(10),
                ct);

            return response.Success;
        }
        catch
        {
            return false;
        }
    }

    private async Task PublishBatchCompletionAsync(
        GreenMigrationBatch batch,
        int successful,
        int failed,
        double carbonSaved,
        double energySaved,
        long bytesMigrated,
        CancellationToken ct)
    {
        if (MessageBus == null) return;

        try
        {
            await MessageBus.PublishAsync(
                "sustainability.green-tiering.batch.complete",
                new PluginMessage
                {
                    Type = "sustainability.green-tiering.batch.complete",
                    SourcePluginId = PluginId,
                    Payload = new Dictionary<string, object>
                    {
                        ["tenantId"] = batch.TenantId,
                        ["targetBackendId"] = batch.TargetBackendId,
                        ["successfulMigrations"] = successful,
                        ["failedMigrations"] = failed,
                        ["totalCarbonSavedGrams"] = carbonSaved,
                        ["totalEnergySavedWh"] = energySaved,
                        ["totalBytesMigrated"] = bytesMigrated,
                        ["candidateCount"] = batch.Candidates.Count,
                        ["completedAt"] = DateTimeOffset.UtcNow.ToString("O")
                    },
                    Description = $"Green tiering batch complete for tenant {batch.TenantId}: " +
                                  $"{successful} migrated, {failed} failed, " +
                                  $"{carbonSaved:F1}g CO2e saved"
                },
                ct);
        }
        catch
        {

            // Publishing completion event is best-effort
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }

    private void EnqueueHistory(MigrationRecord record)
    {
        _migrationHistory.Enqueue(record);

        // Prune history if over limit
        while (_migrationHistory.Count > MaxHistoryEntries)
            _migrationHistory.TryDequeue(out _);
    }

    private void GenerateRecommendations(List<SustainabilityRecommendation> recommendations)
    {
        var retryCount = _retryQueue.Count;
        long completedCount;
        double carbonSaved;

        lock (_statsLock2)
        {
            completedCount = _totalMigrationsCompleted;
            carbonSaved = _totalCarbonSavedGrams;
        }

        // If retry queue is growing faster than processing
        if (retryCount > 10)
        {
            recommendations.Add(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-retry-queue-growing",
                Type = "PerformanceTuning",
                Priority = 7,
                Description = $"{retryCount} migrations in retry queue. Consider increasing MaxConcurrentMigrations " +
                              "or investigating backend connectivity issues.",
                CanAutoApply = false,
                Action = "increase-concurrent-migrations"
            });
        }

        // Finding 4435: single snapshot from the bounded queue (at most MaxHistoryEntries entries),
        // then take the last 100 in-place. The trailing .ToArray() was redundant; TakeLast returns
        // an IEnumerable and callers below use Count() + indexer — use ToList() once.
        var recentHistory = _migrationHistory.ToArray().TakeLast(100).ToList();
        if (recentHistory.Count >= 50)
        {
            var successRate = recentHistory.Count(r => r.Success) / (double)recentHistory.Count;
            if (successRate < 0.8)
            {
                recommendations.Add(new SustainabilityRecommendation
                {
                    RecommendationId = $"{StrategyId}-low-success-rate",
                    Type = "Reliability",
                    Priority = 8,
                    Description = $"Migration success rate is {successRate:P0}. " +
                                  "Consider checking backend health and network connectivity.",
                    CanAutoApply = false,
                    Action = "investigate-migration-failures"
                });
            }
        }

        // Report carbon savings if significant
        if (carbonSaved > 1000) // More than 1 kg CO2e saved
        {
            var annualProjection = carbonSaved / Math.Max(1, EstimatedLifetimeYears);
            recommendations.Add(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-carbon-savings-report",
                Type = "CarbonSavings",
                Priority = 3,
                Description = $"Green tiering has saved an estimated {carbonSaved / 1000.0:F2} kg CO2e " +
                              $"across {completedCount} migrations. Projected annual savings: {annualProjection / 1000.0:F2} kg CO2e.",
                EstimatedCarbonReductionGrams = annualProjection,
                CanAutoApply = false
            });
        }

        // If >50% of migrated data was from high-carbon backends
        if (completedCount > 20)
        {
            recommendations.Add(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-default-placement",
                Type = "PlacementOptimization",
                Priority = 5,
                Description = "Consider changing default data placement to green backends to reduce " +
                              "the volume of cold data migration needed.",
                CanAutoApply = false,
                Action = "configure-default-green-placement"
            });
        }
    }

    private Task UpdateRecommendationsAsync()
    {
        ClearRecommendations();
        var recommendations = new List<SustainabilityRecommendation>();
        GenerateRecommendations(recommendations);
        foreach (var rec in recommendations)
            AddRecommendation(rec);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Result of migrating a single object.
    /// </summary>
    private sealed record MigrationObjectResult
    {
        public required bool Success { get; init; }
        public required long SizeBytes { get; init; }
        public required double CarbonSavedGrams { get; init; }
        public required double EnergySavedWh { get; init; }
        public string? ErrorMessage { get; init; }
    }
}

/// <summary>
/// Result of executing a migration batch.
/// </summary>
public sealed record BatchExecutionResult
{
    /// <summary>Tenant that owns the migrated data.</summary>
    public required string TenantId { get; init; }

    /// <summary>Target backend that received the data.</summary>
    public required string TargetBackendId { get; init; }

    /// <summary>Total candidates in the batch.</summary>
    public required int TotalCandidates { get; init; }

    /// <summary>Number of successfully migrated objects.</summary>
    public required int SuccessfulMigrations { get; init; }

    /// <summary>Number of failed migration attempts.</summary>
    public required int FailedMigrations { get; init; }

    /// <summary>Total carbon saved across successful migrations (gCO2e).</summary>
    public required double TotalCarbonSavedGrams { get; init; }

    /// <summary>Total energy saved across successful migrations (Wh).</summary>
    public required double TotalEnergySavedWh { get; init; }

    /// <summary>Total bytes successfully migrated.</summary>
    public required long TotalBytesMigrated { get; init; }

    /// <summary>Error message if the entire batch failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Whether all migrations in the batch succeeded.</summary>
    public bool AllSucceeded => FailedMigrations == 0 && SuccessfulMigrations == TotalCandidates;
}

/// <summary>
/// Aggregate migration statistics for reporting.
/// </summary>
public sealed record MigrationStatistics
{
    /// <summary>Total migrations completed successfully.</summary>
    public required long TotalMigrationsCompleted { get; init; }

    /// <summary>Total migration errors.</summary>
    public required long TotalMigrationErrors { get; init; }

    /// <summary>Total data migrated in bytes.</summary>
    public required long TotalDataMigratedBytes { get; init; }

    /// <summary>Total carbon saved in gCO2e.</summary>
    public required double TotalCarbonSavedGrams { get; init; }

    /// <summary>Current retry queue size.</summary>
    public required int RetryQueueSize { get; init; }

    /// <summary>Current history buffer size.</summary>
    public required int HistorySize { get; init; }

    /// <summary>Estimated annual carbon savings based on lifetime projection (gCO2e).</summary>
    public required double EstimatedAnnualCarbonSavingsGrams { get; init; }

    /// <summary>Total data migrated in human-readable format.</summary>
    public string TotalDataMigratedFormatted
    {
        get
        {
            if (TotalDataMigratedBytes >= 1L << 40) return $"{TotalDataMigratedBytes / (double)(1L << 40):F2} TB";
            if (TotalDataMigratedBytes >= 1L << 30) return $"{TotalDataMigratedBytes / (double)(1L << 30):F2} GB";
            if (TotalDataMigratedBytes >= 1L << 20) return $"{TotalDataMigratedBytes / (double)(1L << 20):F2} MB";
            return $"{TotalDataMigratedBytes / 1024.0:F2} KB";
        }
    }
}
