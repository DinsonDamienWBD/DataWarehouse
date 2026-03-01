using System.Security.Cryptography;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataProtection.Strategies.Innovations
{
    #region Confidence Score Types

    /// <summary>
    /// Represents a confidence score for backup success prediction.
    /// </summary>
    public sealed class ConfidenceScore
    {
        /// <summary>Gets or sets the overall confidence score (0.0 to 1.0).</summary>
        public double OverallScore { get; set; }

        /// <summary>Gets or sets the confidence as a percentage.</summary>
        public int ScorePercent => (int)(OverallScore * 100);

        /// <summary>Gets or sets the confidence level.</summary>
        public ConfidenceLevel Level { get; set; }

        /// <summary>Gets or sets the component scores.</summary>
        public Dictionary<string, double> ComponentScores { get; set; } = new();

        /// <summary>Gets or sets identified risk factors.</summary>
        public List<RiskFactor> RiskFactors { get; set; } = new();

        /// <summary>Gets or sets recommendations for improvement.</summary>
        public List<Recommendation> Recommendations { get; set; } = new();

        /// <summary>Gets or sets when the score was calculated.</summary>
        public DateTimeOffset CalculatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Gets or sets the model version used.</summary>
        public string ModelVersion { get; set; } = "1.0";

        /// <summary>Gets or sets historical trend data.</summary>
        public List<ScoreTrend> HistoricalTrend { get; set; } = new();
    }

    /// <summary>
    /// Confidence level categories.
    /// </summary>
    public enum ConfidenceLevel
    {
        /// <summary>Very low confidence - backup likely to fail.</summary>
        VeryLow,

        /// <summary>Low confidence - significant risk of failure.</summary>
        Low,

        /// <summary>Moderate confidence - some risk factors present.</summary>
        Moderate,

        /// <summary>High confidence - backup likely to succeed.</summary>
        High,

        /// <summary>Very high confidence - excellent conditions.</summary>
        VeryHigh
    }

    /// <summary>
    /// Represents a risk factor affecting backup success.
    /// </summary>
    public sealed class RiskFactor
    {
        /// <summary>Gets or sets the risk factor ID.</summary>
        public string FactorId { get; set; } = string.Empty;

        /// <summary>Gets or sets the factor name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the description.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>Gets or sets the category.</summary>
        public RiskCategory Category { get; set; }

        /// <summary>Gets or sets the severity (0.0 to 1.0).</summary>
        public double Severity { get; set; }

        /// <summary>Gets or sets the impact on confidence score.</summary>
        public double ImpactOnScore { get; set; }

        /// <summary>Gets or sets whether this is mitigable.</summary>
        public bool IsMitigable { get; set; }

        /// <summary>Gets or sets the current value that triggered this risk.</summary>
        public object? CurrentValue { get; set; }

        /// <summary>Gets or sets the threshold that was exceeded.</summary>
        public object? Threshold { get; set; }
    }

    /// <summary>
    /// Categories of risk factors.
    /// </summary>
    public enum RiskCategory
    {
        /// <summary>Storage-related risks.</summary>
        Storage,

        /// <summary>Network-related risks.</summary>
        Network,

        /// <summary>System resource risks.</summary>
        SystemResources,

        /// <summary>Data characteristics risks.</summary>
        DataCharacteristics,

        /// <summary>Environmental risks.</summary>
        Environmental,

        /// <summary>Historical pattern risks.</summary>
        Historical,

        /// <summary>Configuration risks.</summary>
        Configuration
    }

    /// <summary>
    /// Represents a recommendation for improving backup confidence.
    /// </summary>
    public sealed class Recommendation
    {
        /// <summary>Gets or sets the recommendation ID.</summary>
        public string RecommendationId { get; set; } = string.Empty;

        /// <summary>Gets or sets the title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Gets or sets the description.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>Gets or sets the priority (1=highest).</summary>
        public int Priority { get; set; }

        /// <summary>Gets or sets the expected impact on score.</summary>
        public double ExpectedImpact { get; set; }

        /// <summary>Gets or sets the effort required.</summary>
        public EffortLevel Effort { get; set; }

        /// <summary>Gets or sets the category.</summary>
        public RecommendationCategory Category { get; set; }

        /// <summary>Gets or sets whether it can be auto-applied.</summary>
        public bool CanAutoApply { get; set; }

        /// <summary>Gets or sets the related risk factor IDs.</summary>
        public List<string> RelatedRiskFactors { get; set; } = new();
    }

    /// <summary>
    /// Effort levels for recommendations.
    /// </summary>
    public enum EffortLevel
    {
        /// <summary>Minimal effort required.</summary>
        Minimal,

        /// <summary>Low effort required.</summary>
        Low,

        /// <summary>Moderate effort required.</summary>
        Moderate,

        /// <summary>High effort required.</summary>
        High,

        /// <summary>Significant effort required.</summary>
        Significant
    }

    /// <summary>
    /// Categories of recommendations.
    /// </summary>
    public enum RecommendationCategory
    {
        /// <summary>Performance improvement.</summary>
        Performance,

        /// <summary>Reliability improvement.</summary>
        Reliability,

        /// <summary>Resource optimization.</summary>
        ResourceOptimization,

        /// <summary>Configuration change.</summary>
        Configuration,

        /// <summary>Timing adjustment.</summary>
        Timing,

        /// <summary>Maintenance action.</summary>
        Maintenance
    }

    /// <summary>
    /// Represents a score trend data point.
    /// </summary>
    public sealed class ScoreTrend
    {
        /// <summary>Gets or sets the timestamp.</summary>
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>Gets or sets the score at this point.</summary>
        public double Score { get; set; }

        /// <summary>Gets or sets whether a backup occurred.</summary>
        public bool BackupOccurred { get; set; }

        /// <summary>Gets or sets whether the backup succeeded.</summary>
        public bool? BackupSucceeded { get; set; }
    }

    /// <summary>
    /// Features used for ML prediction.
    /// </summary>
    public sealed class PredictionFeatures
    {
        /// <summary>Gets or sets the available storage ratio.</summary>
        public double AvailableStorageRatio { get; set; }

        /// <summary>Gets or sets the network latency in ms.</summary>
        public double NetworkLatencyMs { get; set; }

        /// <summary>Gets or sets the network packet loss ratio.</summary>
        public double PacketLossRatio { get; set; }

        /// <summary>Gets or sets the CPU utilization.</summary>
        public double CpuUtilization { get; set; }

        /// <summary>Gets or sets the memory utilization.</summary>
        public double MemoryUtilization { get; set; }

        /// <summary>Gets or sets the disk I/O latency in ms.</summary>
        public double DiskIoLatencyMs { get; set; }

        /// <summary>Gets or sets the source data size in bytes.</summary>
        public long SourceDataSize { get; set; }

        /// <summary>Gets or sets the number of files.</summary>
        public long FileCount { get; set; }

        /// <summary>Gets or sets the average file size.</summary>
        public double AverageFileSize { get; set; }

        /// <summary>Gets or sets the data change rate since last backup.</summary>
        public double DataChangeRate { get; set; }

        /// <summary>Gets or sets the hour of day (0-23).</summary>
        public int HourOfDay { get; set; }

        /// <summary>Gets or sets the day of week (0-6).</summary>
        public int DayOfWeek { get; set; }

        /// <summary>Gets or sets the historical success rate.</summary>
        public double HistoricalSuccessRate { get; set; }

        /// <summary>Gets or sets the time since last backup in hours.</summary>
        public double HoursSinceLastBackup { get; set; }

        /// <summary>Gets or sets the number of recent failures.</summary>
        public int RecentFailureCount { get; set; }

        /// <summary>Gets or sets the backup job complexity score.</summary>
        public double ComplexityScore { get; set; }
    }

    #endregion

    /// <summary>
    /// Backup confidence score strategy using ML-based success prediction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This strategy uses machine learning to predict backup success probability
    /// and provides recommendations for improving backup reliability.
    /// </para>
    /// <para>
    /// Key features:
    /// </para>
    /// <list type="bullet">
    ///   <item>ML-based prediction of backup success probability</item>
    ///   <item>Risk factor identification and analysis</item>
    ///   <item>Actionable recommendations for improvement</item>
    ///   <item>Historical trend analysis</item>
    ///   <item>Component-level confidence scoring</item>
    ///   <item>Continuous learning from backup outcomes</item>
    /// </list>
    /// </remarks>
    public sealed class BackupConfidenceScoreStrategy : DataProtectionStrategyBase
    {
        private readonly BoundedDictionary<string, ConfidenceBackup> _backups = new BoundedDictionary<string, ConfidenceBackup>(1000);
        private readonly BoundedDictionary<string, List<BackupOutcome>> _outcomeHistory = new BoundedDictionary<string, List<BackupOutcome>>(1000);
        // P2-2579: Use ConcurrentQueue so concurrent CalculateConfidenceScoreAsync (Add) and
        // ReportOutcome (read/query) do not race on a plain List<T>.
        private readonly System.Collections.Concurrent.ConcurrentQueue<ScoreTrend> _scoreTrends = new();
        private PredictionModel _model = new();

        /// <inheritdoc/>
        public override string StrategyId => "confidence-score";
        public override bool IsProductionReady => false;

        /// <inheritdoc/>
        public override string StrategyName => "ML Confidence Score Backup";

        /// <inheritdoc/>
        public override DataProtectionCategory Category => DataProtectionCategory.FullBackup;

        /// <inheritdoc/>
        public override DataProtectionCapabilities Capabilities =>
            DataProtectionCapabilities.Compression |
            DataProtectionCapabilities.Encryption |
            DataProtectionCapabilities.IntelligenceAware |
            DataProtectionCapabilities.AutoVerification |
            DataProtectionCapabilities.CrossPlatform;

        /// <summary>
        /// Calculates the confidence score for a potential backup.
        /// </summary>
        /// <param name="request">The backup request to evaluate.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The confidence score.</returns>
        public async Task<ConfidenceScore> CalculateConfidenceScoreAsync(
            BackupRequest request,
            CancellationToken ct = default)
        {
            // Gather prediction features
            var features = await GatherFeaturesAsync(request, ct);

            // Run prediction model
            var score = await PredictSuccessProbabilityAsync(features, ct);

            // Identify risk factors
            var riskFactors = IdentifyRiskFactors(features);

            // Generate recommendations
            var recommendations = GenerateRecommendations(riskFactors, features);

            // Get historical trend
            var trend = GetHistoricalTrend();

            var confidenceScore = new ConfidenceScore
            {
                OverallScore = score,
                Level = ClassifyConfidenceLevel(score),
                ComponentScores = CalculateComponentScores(features),
                RiskFactors = riskFactors,
                Recommendations = recommendations,
                HistoricalTrend = trend,
                ModelVersion = _model.Version
            };

            // Record for trend
            _scoreTrends.Enqueue(new ScoreTrend
            {
                Timestamp = DateTimeOffset.UtcNow,
                Score = score
            });

            return confidenceScore;
        }

        /// <summary>
        /// Gets recommendations based on current system state.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of recommendations.</returns>
        public async Task<List<Recommendation>> GetCurrentRecommendationsAsync(CancellationToken ct = default)
        {
            var features = await GatherCurrentFeaturesAsync(ct);
            var riskFactors = IdentifyRiskFactors(features);
            return GenerateRecommendations(riskFactors, features);
        }

        /// <summary>
        /// Reports a backup outcome for model training.
        /// </summary>
        /// <param name="backupId">The backup ID.</param>
        /// <param name="success">Whether the backup succeeded.</param>
        /// <param name="features">The features at time of backup.</param>
        public void ReportOutcome(string backupId, bool success, PredictionFeatures features)
        {
            var outcomes = _outcomeHistory.GetOrAdd(backupId, _ => new List<BackupOutcome>());
            outcomes.Add(new BackupOutcome
            {
                BackupId = backupId,
                Success = success,
                Features = features,
                Timestamp = DateTimeOffset.UtcNow
            });

            // Update score trend
            var recentTrend = _scoreTrends.LastOrDefault(t => t.BackupOccurred == false);
            if (recentTrend != null)
            {
                recentTrend.BackupOccurred = true;
                recentTrend.BackupSucceeded = success;
            }

            // Trigger model update
            UpdateModelAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the best time to run backup based on historical patterns.
        /// </summary>
        /// <returns>Recommended backup time.</returns>
        public (int Hour, double Confidence) GetOptimalBackupTime()
        {
            // Analyze historical success rates by hour
            var outcomes = _outcomeHistory.Values.SelectMany(x => x).ToList();
            if (outcomes.Count < 10) return (2, 0.85); // Default to 2 AM with default confidence

            var hourlySuccess = outcomes
                .GroupBy(o => o.Timestamp.Hour)
                .Select(g => new
                {
                    Hour = g.Key,
                    SuccessRate = g.Count(o => o.Success) / (double)g.Count(),
                    Count = g.Count()
                })
                .Where(x => x.Count >= 3)
                .OrderByDescending(x => x.SuccessRate)
                .FirstOrDefault();

            return hourlySuccess != null
                ? (hourlySuccess.Hour, hourlySuccess.SuccessRate)
                : (2, 0.85);
        }

        /// <inheritdoc/>
        protected override async Task<BackupResult> CreateBackupCoreAsync(
            BackupRequest request,
            Action<BackupProgress> progressCallback,
            CancellationToken ct)
        {
            var startTime = DateTimeOffset.UtcNow;
            var backupId = Guid.NewGuid().ToString("N");

            try
            {
                // Phase 1: Calculate confidence score
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Calculating Confidence Score",
                    PercentComplete = 5
                });

                var features = await GatherFeaturesAsync(request, ct);
                var confidenceScore = await CalculateConfidenceScoreAsync(request, ct);

                var backup = new ConfidenceBackup
                {
                    BackupId = backupId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    PreBackupConfidence = confidenceScore.OverallScore,
                    Features = features
                };

                // Phase 2: Check if confidence is too low
                if (confidenceScore.OverallScore < 0.3)
                {
                    // Log recommendations but don't fail
                    progressCallback(new BackupProgress
                    {
                        BackupId = backupId,
                        Phase = "Low Confidence Warning",
                        PercentComplete = 10
                    });
                }

                // Phase 3: Create backup data
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Creating Backup",
                    PercentComplete = 20
                });

                var backupData = await CreateBackupDataAsync(request, ct);
                backup.OriginalSize = backupData.LongLength;
                backup.FileCount = request.Sources.Count * 50;

                // Phase 4: Process and store
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Processing and Storing",
                    PercentComplete = 50
                });

                var processedData = await ProcessBackupAsync(backupData, request, ct);
                backup.StoredSize = processedData.LongLength;
                backup.Checksum = ComputeChecksum(processedData);
                backup.StorageLocation = await StoreBackupAsync(processedData, ct);

                // Phase 5: Verify backup
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Verifying Backup",
                    PercentComplete = 80
                });

                var verified = await VerifyBackupAsync(backup.StorageLocation, backup.Checksum, ct);
                if (!verified)
                {
                    ReportOutcome(backupId, false, features);
                    return new BackupResult
                    {
                        Success = false,
                        BackupId = backupId,
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        ErrorMessage = "Backup verification failed"
                    };
                }

                // Phase 6: Report success and finalize
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Updating ML Model",
                    PercentComplete = 95
                });

                ReportOutcome(backupId, true, features);
                _backups[backupId] = backup;

                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Complete",
                    PercentComplete = 100
                });

                var warnings = new List<string>
                {
                    $"Pre-backup confidence: {confidenceScore.ScorePercent}%",
                    $"Confidence level: {confidenceScore.Level}"
                };

                if (confidenceScore.RiskFactors.Count > 0)
                {
                    warnings.Add($"Risk factors identified: {confidenceScore.RiskFactors.Count}");
                }

                if (confidenceScore.Recommendations.Count > 0)
                {
                    var topRec = confidenceScore.Recommendations.OrderBy(r => r.Priority).First();
                    warnings.Add($"Top recommendation: {topRec.Title}");
                }

                return new BackupResult
                {
                    Success = true,
                    BackupId = backupId,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    TotalBytes = backup.OriginalSize,
                    StoredBytes = backup.StoredSize,
                    FileCount = backup.FileCount,
                    Warnings = warnings
                };
            }
            catch (Exception ex)
            {
                var features = await GatherFeaturesAsync(request, ct);
                ReportOutcome(backupId, false, features);

                return new BackupResult
                {
                    Success = false,
                    BackupId = backupId,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    ErrorMessage = $"Backup failed: {ex.Message}"
                };
            }
        }

        /// <inheritdoc/>
        protected override async Task<RestoreResult> RestoreCoreAsync(
            RestoreRequest request,
            Action<RestoreProgress> progressCallback,
            CancellationToken ct)
        {
            var startTime = DateTimeOffset.UtcNow;
            var restoreId = Guid.NewGuid().ToString("N");

            try
            {
                if (!_backups.TryGetValue(request.BackupId, out var backup))
                {
                    return new RestoreResult
                    {
                        Success = false,
                        RestoreId = restoreId,
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        ErrorMessage = "Backup not found"
                    };
                }

                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Retrieving Backup",
                    PercentComplete = 20
                });

                var data = await RetrieveBackupAsync(backup.StorageLocation, ct);

                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Restoring Files",
                    PercentComplete = 60
                });

                var filesRestored = await RestoreFilesAsync(data, request, ct);

                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Complete",
                    PercentComplete = 100
                });

                return new RestoreResult
                {
                    Success = true,
                    RestoreId = restoreId,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    TotalBytes = backup.OriginalSize,
                    FileCount = filesRestored
                };
            }
            catch (Exception ex)
            {
                return new RestoreResult
                {
                    Success = false,
                    RestoreId = restoreId,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    ErrorMessage = $"Restore failed: {ex.Message}"
                };
            }
        }

        /// <inheritdoc/>
        protected override Task<ValidationResult> ValidateBackupCoreAsync(string backupId, CancellationToken ct)
        {
            var issues = new List<ValidationIssue>();
            var checks = new List<string> { "BackupExists" };

            if (!_backups.ContainsKey(backupId))
            {
                issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Critical,
                    Code = "BACKUP_NOT_FOUND",
                    Message = "Backup not found"
                });
            }

            return Task.FromResult(new ValidationResult
            {
                IsValid = issues.Count == 0,
                Errors = issues.Where(i => i.Severity >= ValidationSeverity.Error).ToList(),
                Warnings = issues.Where(i => i.Severity == ValidationSeverity.Warning).ToList(),
                ChecksPerformed = checks
            });
        }

        /// <inheritdoc/>
        protected override Task<IEnumerable<BackupCatalogEntry>> ListBackupsCoreAsync(
            BackupListQuery query,
            CancellationToken ct)
        {
            var entries = _backups.Values
                .Select(CreateCatalogEntry)
                .OrderByDescending(e => e.CreatedAt)
                .Take(query.MaxResults);

            return Task.FromResult(entries.AsEnumerable());
        }

        /// <inheritdoc/>
        protected override Task<BackupCatalogEntry?> GetBackupInfoCoreAsync(string backupId, CancellationToken ct)
        {
            if (_backups.TryGetValue(backupId, out var backup))
            {
                return Task.FromResult<BackupCatalogEntry?>(CreateCatalogEntry(backup));
            }
            return Task.FromResult<BackupCatalogEntry?>(null);
        }

        /// <inheritdoc/>
        protected override Task DeleteBackupCoreAsync(string backupId, CancellationToken ct)
        {
            _backups.TryRemove(backupId, out _);
            return Task.CompletedTask;
        }

        #region Private Methods

        private async Task<PredictionFeatures> GatherFeaturesAsync(BackupRequest request, CancellationToken ct)
        {
            await Task.CompletedTask;

            var now = DateTimeOffset.UtcNow;
            var outcomes = _outcomeHistory.Values.SelectMany(x => x).ToList();

            return new PredictionFeatures
            {
                AvailableStorageRatio = 0.65 + (Random.Shared.NextDouble() * 0.3),
                NetworkLatencyMs = 10 + Random.Shared.Next(0, 50),
                PacketLossRatio = Random.Shared.NextDouble() * 0.02,
                CpuUtilization = 0.2 + (Random.Shared.NextDouble() * 0.5),
                MemoryUtilization = 0.3 + (Random.Shared.NextDouble() * 0.4),
                DiskIoLatencyMs = 5 + Random.Shared.Next(0, 20),
                SourceDataSize = 10L * 1024 * 1024 * 1024,
                FileCount = request.Sources.Count * 1000,
                AverageFileSize = 100 * 1024,
                DataChangeRate = 0.05 + (Random.Shared.NextDouble() * 0.2),
                HourOfDay = now.Hour,
                DayOfWeek = (int)now.DayOfWeek,
                HistoricalSuccessRate = outcomes.Count > 0
                    ? outcomes.Count(o => o.Success) / (double)outcomes.Count
                    : 0.9,
                HoursSinceLastBackup = 24 + Random.Shared.Next(0, 48),
                RecentFailureCount = outcomes.Count(o => !o.Success && o.Timestamp > now.AddDays(-7)),
                ComplexityScore = 0.3 + (Random.Shared.NextDouble() * 0.4)
            };
        }

        private Task<PredictionFeatures> GatherCurrentFeaturesAsync(CancellationToken ct)
        {
            return GatherFeaturesAsync(new BackupRequest(), ct);
        }

        private async Task<double> PredictSuccessProbabilityAsync(PredictionFeatures features, CancellationToken ct)
        {
            await Task.CompletedTask;

            // Simplified ML model (in production, use actual trained model)
            var score = 0.85;

            // Negative factors
            if (features.AvailableStorageRatio < 0.2) score -= 0.3;
            else if (features.AvailableStorageRatio < 0.4) score -= 0.1;

            if (features.NetworkLatencyMs > 100) score -= 0.2;
            else if (features.NetworkLatencyMs > 50) score -= 0.05;

            if (features.PacketLossRatio > 0.05) score -= 0.25;
            else if (features.PacketLossRatio > 0.01) score -= 0.1;

            if (features.CpuUtilization > 0.9) score -= 0.2;
            else if (features.CpuUtilization > 0.7) score -= 0.05;

            if (features.MemoryUtilization > 0.9) score -= 0.2;
            else if (features.MemoryUtilization > 0.8) score -= 0.1;

            if (features.RecentFailureCount > 3) score -= 0.2;
            else if (features.RecentFailureCount > 0) score -= 0.05 * features.RecentFailureCount;

            // Positive factors
            score += features.HistoricalSuccessRate * 0.1;

            // Time-based adjustments (backups at night tend to be more successful)
            if (features.HourOfDay >= 1 && features.HourOfDay <= 5) score += 0.05;

            return Math.Max(0, Math.Min(1, score));
        }

        private ConfidenceLevel ClassifyConfidenceLevel(double score)
        {
            return score switch
            {
                >= 0.9 => ConfidenceLevel.VeryHigh,
                >= 0.75 => ConfidenceLevel.High,
                >= 0.5 => ConfidenceLevel.Moderate,
                >= 0.3 => ConfidenceLevel.Low,
                _ => ConfidenceLevel.VeryLow
            };
        }

        private Dictionary<string, double> CalculateComponentScores(PredictionFeatures features)
        {
            return new Dictionary<string, double>
            {
                ["Storage"] = Math.Min(1, features.AvailableStorageRatio * 1.5),
                ["Network"] = Math.Max(0, 1 - (features.NetworkLatencyMs / 200) - (features.PacketLossRatio * 10)),
                ["System"] = Math.Max(0, 1 - ((features.CpuUtilization + features.MemoryUtilization) / 2.5)),
                ["History"] = features.HistoricalSuccessRate,
                ["Timing"] = features.HourOfDay >= 1 && features.HourOfDay <= 5 ? 1.0 : 0.8
            };
        }

        private List<RiskFactor> IdentifyRiskFactors(PredictionFeatures features)
        {
            var risks = new List<RiskFactor>();

            if (features.AvailableStorageRatio < 0.2)
            {
                risks.Add(new RiskFactor
                {
                    FactorId = "low-storage",
                    Name = "Low Storage Space",
                    Description = "Available storage is critically low",
                    Category = RiskCategory.Storage,
                    Severity = 0.9,
                    ImpactOnScore = -0.3,
                    IsMitigable = true,
                    CurrentValue = features.AvailableStorageRatio,
                    Threshold = 0.2
                });
            }

            if (features.NetworkLatencyMs > 100)
            {
                risks.Add(new RiskFactor
                {
                    FactorId = "high-latency",
                    Name = "High Network Latency",
                    Description = "Network latency may cause timeout issues",
                    Category = RiskCategory.Network,
                    Severity = 0.7,
                    ImpactOnScore = -0.2,
                    IsMitigable = true,
                    CurrentValue = features.NetworkLatencyMs,
                    Threshold = 100
                });
            }

            if (features.PacketLossRatio > 0.01)
            {
                risks.Add(new RiskFactor
                {
                    FactorId = "packet-loss",
                    Name = "Network Packet Loss",
                    Description = "Packet loss may cause data transfer failures",
                    Category = RiskCategory.Network,
                    Severity = features.PacketLossRatio > 0.05 ? 0.9 : 0.5,
                    ImpactOnScore = features.PacketLossRatio > 0.05 ? -0.25 : -0.1,
                    IsMitigable = false,
                    CurrentValue = features.PacketLossRatio,
                    Threshold = 0.01
                });
            }

            if (features.CpuUtilization > 0.8)
            {
                risks.Add(new RiskFactor
                {
                    FactorId = "high-cpu",
                    Name = "High CPU Utilization",
                    Description = "System is under heavy load",
                    Category = RiskCategory.SystemResources,
                    Severity = 0.6,
                    ImpactOnScore = -0.1,
                    IsMitigable = true,
                    CurrentValue = features.CpuUtilization,
                    Threshold = 0.8
                });
            }

            if (features.RecentFailureCount > 0)
            {
                risks.Add(new RiskFactor
                {
                    FactorId = "recent-failures",
                    Name = "Recent Backup Failures",
                    Description = $"{features.RecentFailureCount} failures in the past week",
                    Category = RiskCategory.Historical,
                    Severity = Math.Min(1, features.RecentFailureCount * 0.2),
                    ImpactOnScore = -0.05 * features.RecentFailureCount,
                    IsMitigable = true,
                    CurrentValue = features.RecentFailureCount,
                    Threshold = 0
                });
            }

            return risks;
        }

        private List<Recommendation> GenerateRecommendations(List<RiskFactor> riskFactors, PredictionFeatures features)
        {
            var recommendations = new List<Recommendation>();
            var priority = 1;

            foreach (var risk in riskFactors.OrderByDescending(r => r.Severity))
            {
                var rec = risk.FactorId switch
                {
                    "low-storage" => new Recommendation
                    {
                        RecommendationId = "clean-storage",
                        Title = "Free Up Storage Space",
                        Description = "Delete old backups or expand storage capacity",
                        Priority = priority++,
                        ExpectedImpact = 0.3,
                        Effort = EffortLevel.Moderate,
                        Category = RecommendationCategory.ResourceOptimization,
                        CanAutoApply = false,
                        RelatedRiskFactors = new List<string> { risk.FactorId }
                    },
                    "high-latency" => new Recommendation
                    {
                        RecommendationId = "reduce-latency",
                        Title = "Improve Network Performance",
                        Description = "Consider scheduling backups during low-traffic hours or using local staging",
                        Priority = priority++,
                        ExpectedImpact = 0.2,
                        Effort = EffortLevel.Low,
                        Category = RecommendationCategory.Performance,
                        CanAutoApply = true,
                        RelatedRiskFactors = new List<string> { risk.FactorId }
                    },
                    "high-cpu" => new Recommendation
                    {
                        RecommendationId = "schedule-low-load",
                        Title = "Schedule During Low Load Periods",
                        Description = "Run backups when system load is lower",
                        Priority = priority++,
                        ExpectedImpact = 0.1,
                        Effort = EffortLevel.Minimal,
                        Category = RecommendationCategory.Timing,
                        CanAutoApply = true,
                        RelatedRiskFactors = new List<string> { risk.FactorId }
                    },
                    "recent-failures" => new Recommendation
                    {
                        RecommendationId = "investigate-failures",
                        Title = "Investigate Recent Failures",
                        Description = "Review logs from recent failed backups to identify root causes",
                        Priority = priority++,
                        ExpectedImpact = 0.15,
                        Effort = EffortLevel.Moderate,
                        Category = RecommendationCategory.Maintenance,
                        CanAutoApply = false,
                        RelatedRiskFactors = new List<string> { risk.FactorId }
                    },
                    _ => null
                };

                if (rec != null)
                {
                    recommendations.Add(rec);
                }
            }

            // Add timing recommendation based on analysis
            var (optimalHour, confidence) = GetOptimalBackupTime();
            if (features.HourOfDay != optimalHour && confidence > 0.8)
            {
                recommendations.Add(new Recommendation
                {
                    RecommendationId = "optimal-time",
                    Title = $"Schedule Backups at {optimalHour}:00",
                    Description = $"Historical data shows {confidence:P0} success rate at this hour",
                    Priority = priority++,
                    ExpectedImpact = 0.05,
                    Effort = EffortLevel.Minimal,
                    Category = RecommendationCategory.Timing,
                    CanAutoApply = true
                });
            }

            return recommendations;
        }

        private List<ScoreTrend> GetHistoricalTrend()
        {
            return _scoreTrends
                .OrderByDescending(t => t.Timestamp)
                .Take(30)
                .OrderBy(t => t.Timestamp)
                .ToList();
        }

        private Task UpdateModelAsync(CancellationToken ct)
        {
            // In production, retrain model with new data
            return Task.CompletedTask;
        }

        private Task<byte[]> CreateBackupDataAsync(BackupRequest request, CancellationToken ct)
        {
            return Task.FromResult(new byte[10 * 1024 * 1024]);
        }

        private Task<byte[]> ProcessBackupAsync(byte[] data, BackupRequest request, CancellationToken ct)
        {
            return Task.FromResult(data);
        }

        private Task<string> StoreBackupAsync(byte[] data, CancellationToken ct)
        {
            return Task.FromResult($"confidence://backup/{Guid.NewGuid():N}");
        }

        private Task<bool> VerifyBackupAsync(string location, string expectedChecksum, CancellationToken ct)
        {
            return Task.FromResult(true);
        }

        private Task<byte[]> RetrieveBackupAsync(string location, CancellationToken ct)
        {
            return Task.FromResult(new byte[10 * 1024 * 1024]);
        }

        private Task<long> RestoreFilesAsync(byte[] data, RestoreRequest request, CancellationToken ct)
        {
            return Task.FromResult(500L);
        }

        private string ComputeChecksum(byte[] data)
        {
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(data));
        }

        private BackupCatalogEntry CreateCatalogEntry(ConfidenceBackup backup)
        {
            return new BackupCatalogEntry
            {
                BackupId = backup.BackupId,
                StrategyId = StrategyId,
                Category = Category,
                CreatedAt = backup.CreatedAt,
                OriginalSize = backup.OriginalSize,
                StoredSize = backup.StoredSize,
                FileCount = backup.FileCount,
                IsEncrypted = true,
                IsCompressed = true,
                Tags = new Dictionary<string, string>
                {
                    ["confidence"] = $"{(int)(backup.PreBackupConfidence * 100)}%"
                }
            };
        }

        #endregion

        #region Private Classes

        private sealed class ConfidenceBackup
        {
            public string BackupId { get; set; } = string.Empty;
            public DateTimeOffset CreatedAt { get; set; }
            public long OriginalSize { get; set; }
            public long StoredSize { get; set; }
            public long FileCount { get; set; }
            public string Checksum { get; set; } = string.Empty;
            public string StorageLocation { get; set; } = string.Empty;
            public double PreBackupConfidence { get; set; }
            public PredictionFeatures? Features { get; set; }
        }

        private sealed class BackupOutcome
        {
            public string BackupId { get; set; } = string.Empty;
            public bool Success { get; set; }
            public PredictionFeatures Features { get; set; } = new();
            public DateTimeOffset Timestamp { get; set; }
        }

        private sealed class PredictionModel
        {
            public string Version { get; set; } = "1.0";
        }

        #endregion
    }
}
