using DataWarehouse.Plugins.UltimateDataProtection.Catalog;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataProtection.Subsystems
{
    /// <summary>
    /// Implementation of the intelligence subsystem providing AI-driven optimization and recommendations.
    /// Integrates with the AI provider via message bus for intelligent backup decisions.
    /// </summary>
    public sealed class IntelligenceSubsystem : IIntelligenceSubsystem
    {
        private readonly DataProtectionStrategyRegistry _registry;
        private readonly BackupCatalog _catalog;
        private readonly IMessageBus? _messageBus;
        private bool _isAvailable;

        /// <summary>
        /// Creates a new intelligence subsystem.
        /// </summary>
        /// <param name="registry">Strategy registry.</param>
        /// <param name="catalog">Backup catalog.</param>
        /// <param name="messageBus">Message bus for AI communication (optional).</param>
        public IntelligenceSubsystem(
            DataProtectionStrategyRegistry registry,
            BackupCatalog catalog,
            IMessageBus? messageBus = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _messageBus = messageBus;
            _isAvailable = messageBus != null;
        }

        /// <inheritdoc/>
        public bool IsAvailable => _isAvailable;

        /// <inheritdoc/>
        public async Task<StrategyRecommendation> RecommendStrategyAsync(
            Dictionary<string, object> context,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            // Try AI-powered recommendation first
            if (_messageBus != null)
            {
                try
                {
                    var aiRecommendation = await GetAIStrategyRecommendationAsync(context, ct);
                    if (aiRecommendation != null)
                    {
                        return aiRecommendation;
                    }
                }
                catch
                {

                    // Fall through to heuristic-based recommendation
                    System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                }
            }

            // Fallback to heuristic-based recommendation
            return GetHeuristicStrategyRecommendation(context);
        }

        /// <inheritdoc/>
        public async Task<RecoveryPointRecommendation> RecommendRecoveryPointAsync(
            string itemId,
            DateTimeOffset? targetTime,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

            // Get all backups containing this item
            var allBackups = _catalog.GetAll();
            var relevantBackups = allBackups
                .Where(b => b.Sources.Any(s => s.Contains(itemId, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(b => b.CreatedAt)
                .ToList();

            if (relevantBackups.Count == 0)
            {
                return new RecoveryPointRecommendation
                {
                    RecoveryPointId = string.Empty,
                    Confidence = 0,
                    Reasoning = "No backups found containing the specified item."
                };
            }

            // Find best recovery point based on target time
            BackupCatalogEntry? bestBackup;
            string reasoning;

            if (targetTime.HasValue)
            {
                // Find closest backup before or at target time
                bestBackup = relevantBackups
                    .Where(b => b.CreatedAt <= targetTime.Value)
                    .OrderByDescending(b => b.CreatedAt)
                    .FirstOrDefault();

                if (bestBackup == null)
                {
                    // No backup before target time, use oldest available
                    bestBackup = relevantBackups.OrderBy(b => b.CreatedAt).First();
                    reasoning = $"No backup found before target time {targetTime.Value:yyyy-MM-dd HH:mm:ss}. Using oldest available backup.";
                }
                else
                {
                    var timeDiff = targetTime.Value - bestBackup.CreatedAt;
                    reasoning = $"Selected backup {timeDiff.TotalMinutes:F1} minutes before target time.";
                }
            }
            else
            {
                // Use most recent validated backup
                bestBackup = relevantBackups
                    .Where(b => b.IsValid == true)
                    .OrderByDescending(b => b.CreatedAt)
                    .FirstOrDefault() ?? relevantBackups.First();

                reasoning = bestBackup.IsValid == true
                    ? "Selected most recent validated backup."
                    : "Selected most recent backup (no validated backups available).";
            }

            var confidence = CalculateRecoveryConfidence(bestBackup, targetTime);
            var estimatedDataLoss = targetTime.HasValue
                ? targetTime.Value - bestBackup.CreatedAt
                : (DateTimeOffset.UtcNow - bestBackup.CreatedAt);

            return new RecoveryPointRecommendation
            {
                RecoveryPointId = $"rp_{bestBackup.BackupId}",
                Confidence = confidence,
                Reasoning = reasoning,
                EstimatedDataLoss = estimatedDataLoss > TimeSpan.Zero ? estimatedDataLoss : null,
                EstimatedRecoveryTime = EstimateRecoveryTime(bestBackup)
            };
        }

        /// <inheritdoc/>
        public Task<AnomalyDetectionResult> DetectAnomaliesAsync(
            string backupId,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(backupId);

            var backup = _catalog.Get(backupId);
            if (backup == null)
            {
                return Task.FromResult(new AnomalyDetectionResult
                {
                    AnomaliesDetected = false,
                    RiskScore = 0,
                    Anomalies = Array.Empty<DetectedAnomaly>()
                });
            }

            var anomalies = new List<DetectedAnomaly>();
            double riskScore = 0;

            // Check for unusual backup size
            var sizeAnomaly = DetectSizeAnomaly(backup);
            if (sizeAnomaly != null)
            {
                anomalies.Add(sizeAnomaly);
                riskScore = Math.Max(riskScore, sizeAnomaly.Severity switch
                {
                    AnomalySeverity.Critical => 0.9,
                    AnomalySeverity.High => 0.7,
                    AnomalySeverity.Medium => 0.5,
                    AnomalySeverity.Low => 0.3,
                    _ => 0.1
                });
            }

            // Check for unusual compression ratio (potential ransomware indicator)
            var compressionAnomaly = DetectCompressionAnomaly(backup);
            if (compressionAnomaly != null)
            {
                anomalies.Add(compressionAnomaly);
                riskScore = Math.Max(riskScore, 0.8);
            }

            // Check for unusual file patterns
            var fileCountAnomaly = DetectFileCountAnomaly(backup);
            if (fileCountAnomaly != null)
            {
                anomalies.Add(fileCountAnomaly);
                riskScore = Math.Max(riskScore, 0.6);
            }

            return Task.FromResult(new AnomalyDetectionResult
            {
                AnomaliesDetected = anomalies.Count > 0,
                Anomalies = anomalies,
                RiskScore = riskScore
            });
        }

        /// <inheritdoc/>
        public Task<StoragePrediction> PredictStorageAsync(
            int daysAhead,
            CancellationToken ct = default)
        {
            if (daysAhead <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(daysAhead), "Days ahead must be positive.");
            }

            var stats = _catalog.GetStatistics();

            if (stats.TotalBackups == 0)
            {
                return Task.FromResult(new StoragePrediction
                {
                    PredictionDate = DateTimeOffset.UtcNow.AddDays(daysAhead),
                    PredictedStorageBytes = 0,
                    PredictedDailyGrowthBytes = 0,
                    Confidence = 0,
                    Recommendations = new[] { "Insufficient backup history for prediction." }
                });
            }

            // Calculate daily growth rate from recent backups
            var recentBackups = _catalog.GetAll()
                .Where(b => b.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-30))
                .OrderBy(b => b.CreatedAt)
                .ToList();

            long dailyGrowthBytes;
            double confidence;

            if (recentBackups.Count >= 2)
            {
                var oldestRecent = recentBackups.First();
                var newestRecent = recentBackups.Last();
                var daysDiff = (newestRecent.CreatedAt - oldestRecent.CreatedAt).TotalDays;

                if (daysDiff > 0)
                {
                    // Actual storage growth = difference between total stored size at newest vs oldest point.
                    var sizeDiff = newestRecent.StoredSize - oldestRecent.StoredSize;
                    dailyGrowthBytes = (long)(sizeDiff / daysDiff);
                    confidence = Math.Min(recentBackups.Count / 30.0, 1.0); // More backups = higher confidence
                }
                else
                {
                    dailyGrowthBytes = stats.TotalStoredSize / 30;
                    confidence = 0.3;
                }
            }
            else
            {
                // Estimate based on average backup size
                dailyGrowthBytes = stats.TotalStoredSize / Math.Max(stats.TotalBackups, 1);
                confidence = 0.2;
            }

            var predictedStorage = stats.TotalStoredSize + (dailyGrowthBytes * daysAhead);

            // Generate recommendations
            var recommendations = new List<string>();
            var growthRate = stats.TotalStoredSize > 0
                ? (double)dailyGrowthBytes / stats.TotalStoredSize * 100
                : 0;

            if (growthRate > 5)
            {
                recommendations.Add("Storage growth rate is high. Consider implementing retention policies.");
            }

            if (stats.DeduplicationRatio > 0.5)
            {
                recommendations.Add("Deduplication ratio is low. Consider enabling or optimizing deduplication.");
            }

            if (stats.CompressedCount < stats.TotalBackups * 0.8)
            {
                recommendations.Add("Many backups are uncompressed. Enable compression to save space.");
            }

            return Task.FromResult(new StoragePrediction
            {
                PredictionDate = DateTimeOffset.UtcNow.AddDays(daysAhead),
                PredictedStorageBytes = predictedStorage,
                PredictedDailyGrowthBytes = dailyGrowthBytes,
                Confidence = confidence,
                Recommendations = recommendations
            });
        }

        #region Private Helper Methods

        /// <summary>
        /// Gets AI-powered strategy recommendation via message bus.
        /// </summary>
        private async Task<StrategyRecommendation?> GetAIStrategyRecommendationAsync(
            Dictionary<string, object> context,
            CancellationToken ct)
        {
            if (_messageBus == null)
            {
                return null;
            }

            try
            {
                var message = new PluginMessage
                {
                    MessageId = Guid.NewGuid().ToString(),
                    Source = "UltimateDataProtection.Intelligence",
                    Type = "ai.strategy.recommend",
                    Payload = context,
                    Timestamp = DateTime.UtcNow
                };

                var response = await _messageBus.SendAsync("ai.strategy.recommend", message, TimeSpan.FromSeconds(5), ct);

                if (response.Success && response.Payload is StrategyRecommendation recommendation)
                {
                    return recommendation;
                }
            }
            catch
            {

                // Graceful degradation
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }

            return null;
        }

        /// <summary>
        /// Gets heuristic-based strategy recommendation.
        /// </summary>
        private StrategyRecommendation GetHeuristicStrategyRecommendation(Dictionary<string, object> context)
        {
            // Extract context parameters
            var dataSize = context.TryGetValue("dataSize", out var size) ? Convert.ToInt64(size) : 0L;
            var changeRate = context.TryGetValue("changeRate", out var rate) ? Convert.ToDouble(rate) : 0.0;
            var criticalityLevel = context.TryGetValue("criticalityLevel", out var crit) ? Convert.ToDouble(crit) : 0.5;
            var requiresEncryption = context.TryGetValue("requiresEncryption", out var enc) && Convert.ToBoolean(enc);

            // Decision logic
            DataProtectionCategory category;
            DataProtectionCapabilities requiredCaps = DataProtectionCapabilities.None;

            if (changeRate > 0.5)
            {
                // High change rate - use incremental or continuous
                category = changeRate > 0.8
                    ? DataProtectionCategory.ContinuousProtection
                    : DataProtectionCategory.IncrementalBackup;
                requiredCaps |= DataProtectionCapabilities.PointInTimeRecovery;
            }
            else if (criticalityLevel > 0.7)
            {
                // High criticality - full backup with verification
                category = DataProtectionCategory.FullBackup;
                requiredCaps |= DataProtectionCapabilities.AutoVerification;
            }
            else
            {
                // Standard backup
                category = DataProtectionCategory.FullBackup;
            }

            if (requiresEncryption)
            {
                requiredCaps |= DataProtectionCapabilities.Encryption;
            }

            if (dataSize > 1_000_000_000) // > 1GB
            {
                requiredCaps |= DataProtectionCapabilities.Compression | DataProtectionCapabilities.Deduplication;
            }

            // Find best matching strategy
            var bestStrategy = _registry.SelectBestStrategy(category, requiredCaps);

            if (bestStrategy == null)
            {
                // Fallback to any full backup strategy
                bestStrategy = _registry.GetByCategory(DataProtectionCategory.FullBackup).FirstOrDefault();
            }

            var alternatives = _registry.GetByCategory(category)
                .Where(s => s.StrategyId != bestStrategy?.StrategyId)
                .Take(3)
                .Select(s => s.StrategyId)
                .ToList();

            return new StrategyRecommendation
            {
                StrategyId = bestStrategy?.StrategyId ?? "full_backup",
                StrategyName = bestStrategy?.StrategyName ?? "Full Backup",
                Confidence = bestStrategy != null ? 0.75 : 0.5,
                Reasoning = $"Selected {category} strategy based on change rate ({changeRate:P0}), " +
                           $"data size ({dataSize / 1_000_000.0:F1} MB), and criticality ({criticalityLevel:P0}).",
                Alternatives = alternatives
            };
        }

        /// <summary>
        /// Detects size anomalies in backup.
        /// </summary>
        private DetectedAnomaly? DetectSizeAnomaly(BackupCatalogEntry backup)
        {
            // Get backups from same strategy for comparison
            var similarBackups = _catalog.GetByStrategy(backup.StrategyId)
                .Where(b => b.BackupId != backup.BackupId)
                .ToList();

            if (similarBackups.Count < 3)
            {
                return null; // Not enough data
            }

            var avgSize = similarBackups.Average(b => b.OriginalSize);
            var stdDev = Math.Sqrt(similarBackups.Average(b => Math.Pow(b.OriginalSize - avgSize, 2)));

            var deviation = Math.Abs(backup.OriginalSize - avgSize);

            if (deviation > stdDev * 3)
            {
                // Significant size anomaly (3 standard deviations)
                var percentDiff = (backup.OriginalSize - avgSize) / avgSize * 100;

                return new DetectedAnomaly
                {
                    AnomalyType = "UnusualBackupSize",
                    Severity = Math.Abs(percentDiff) > 200 ? AnomalySeverity.High : AnomalySeverity.Medium,
                    Description = $"Backup size ({backup.OriginalSize / 1_000_000.0:F1} MB) is {Math.Abs(percentDiff):F0}% " +
                                 $"{(percentDiff > 0 ? "larger" : "smaller")} than average ({avgSize / 1_000_000.0:F1} MB).",
                    AffectedItems = new[] { backup.BackupId },
                    RecommendedAction = percentDiff > 0
                        ? "Investigate for potential ransomware or data corruption."
                        : "Verify backup completeness - data may be missing."
                };
            }

            return null;
        }

        /// <summary>
        /// Detects compression ratio anomalies.
        /// </summary>
        private DetectedAnomaly? DetectCompressionAnomaly(BackupCatalogEntry backup)
        {
            if (!backup.IsCompressed || backup.OriginalSize == 0)
            {
                return null;
            }

            var compressionRatio = (double)backup.StoredSize / backup.OriginalSize;

            // Encrypted or random data compresses poorly (ratio close to 1.0)
            // This can indicate ransomware encryption
            if (compressionRatio > 0.95 && !backup.IsEncrypted)
            {
                return new DetectedAnomaly
                {
                    AnomalyType = "PoorCompression",
                    Severity = AnomalySeverity.High,
                    Description = $"Compression ratio is unusually poor ({compressionRatio:P1}). " +
                                 "Data may be encrypted by ransomware or highly random.",
                    AffectedItems = new[] { backup.BackupId },
                    RecommendedAction = "URGENT: Investigate for potential ransomware activity. Verify data integrity."
                };
            }

            return null;
        }

        /// <summary>
        /// Detects file count anomalies.
        /// </summary>
        private DetectedAnomaly? DetectFileCountAnomaly(BackupCatalogEntry backup)
        {
            var similarBackups = _catalog.GetByStrategy(backup.StrategyId)
                .Where(b => b.BackupId != backup.BackupId && b.Sources.SequenceEqual(backup.Sources))
                .ToList();

            if (similarBackups.Count < 3)
            {
                return null;
            }

            var avgFileCount = similarBackups.Average(b => b.FileCount);
            var percentDiff = (backup.FileCount - avgFileCount) / avgFileCount * 100;

            if (Math.Abs(percentDiff) > 50)
            {
                return new DetectedAnomaly
                {
                    AnomalyType = "UnusualFileCount",
                    Severity = Math.Abs(percentDiff) > 100 ? AnomalySeverity.Medium : AnomalySeverity.Low,
                    Description = $"File count ({backup.FileCount:N0}) is {Math.Abs(percentDiff):F0}% " +
                                 $"{(percentDiff > 0 ? "higher" : "lower")} than average ({avgFileCount:N0}).",
                    AffectedItems = new[] { backup.BackupId },
                    RecommendedAction = "Review backup scope and verify no files were missed or unexpectedly added."
                };
            }

            return null;
        }

        /// <summary>
        /// Calculates recovery confidence based on backup quality.
        /// </summary>
        private static double CalculateRecoveryConfidence(BackupCatalogEntry backup, DateTimeOffset? targetTime)
        {
            var confidence = 1.0;

            // Reduce confidence if not validated
            if (backup.IsValid == false)
            {
                confidence *= 0.3;
            }
            else if (!backup.LastValidatedAt.HasValue)
            {
                confidence *= 0.7;
            }
            else if (backup.LastValidatedAt.Value < DateTimeOffset.UtcNow.AddDays(-7))
            {
                confidence *= 0.9;
            }

            // Reduce confidence if backup is old
            var age = DateTimeOffset.UtcNow - backup.CreatedAt;
            if (age > TimeSpan.FromDays(365))
            {
                confidence *= 0.8;
            }

            // Reduce confidence based on distance from target time
            if (targetTime.HasValue)
            {
                var timeDiff = Math.Abs((targetTime.Value - backup.CreatedAt).TotalHours);
                if (timeDiff > 24)
                {
                    confidence *= 0.9;
                }
            }

            return confidence;
        }

        /// <summary>
        /// Estimates recovery time based on backup size.
        /// </summary>
        private static TimeSpan EstimateRecoveryTime(BackupCatalogEntry backup)
        {
            // Rough estimate: 100 MB/s restore speed
            var bytesPerSecond = 100 * 1024 * 1024;
            var seconds = backup.OriginalSize / bytesPerSecond;
            return TimeSpan.FromSeconds(Math.Max(seconds, 30)); // Minimum 30 seconds
        }

        #endregion
    }
}
