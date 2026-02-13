using System.Collections.Concurrent;
using System.Diagnostics;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataProtection
{
    /// <summary>
    /// Abstract base class for data protection strategies.
    /// Inherits from <see cref="StrategyBase"/> for unified strategy hierarchy (AD-05).
    /// Provides common functionality including statistics tracking, progress reporting,
    /// and standardized operation management.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived classes must implement the core backup and restore operations.
    /// This base class provides:
    /// </para>
    /// <list type="bullet">
    ///   <item>Statistics tracking with thread-safe updates</item>
    ///   <item>Progress reporting infrastructure</item>
    ///   <item>MessageBus-based event publishing (guarded by IsIntelligenceAvailable)</item>
    ///   <item>AI recommendation and anomaly detection requests</item>
    /// </list>
    /// </remarks>
    public abstract class DataProtectionStrategyBase : StrategyBase, IDataProtectionStrategy
    {
        private readonly DataProtectionStatistics _statistics = new();
        private readonly object _statsLock = new();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeOperations = new();
        private readonly ConcurrentDictionary<string, BackupProgress> _backupProgress = new();
        private readonly ConcurrentDictionary<string, RestoreProgress> _restoreProgress = new();

        /// <summary>
        /// Message bus for Intelligence communication.
        /// Hides StrategyBase.MessageBus to allow local assignment from ConfigureIntelligence.
        /// </summary>
        protected new IMessageBus? MessageBus { get; private set; }

        /// <summary>
        /// Whether Intelligence integration is available.
        /// Hides StrategyBase.IsIntelligenceAvailable to check local MessageBus.
        /// </summary>
        protected new bool IsIntelligenceAvailable => MessageBus != null;

        /// <inheritdoc/>
        public abstract override string StrategyId { get; }

        /// <inheritdoc/>
        public abstract string StrategyName { get; }

        /// <summary>
        /// Bridges StrategyBase.Name to the domain-specific StrategyName property.
        /// </summary>
        public override string Name => StrategyName;

        /// <inheritdoc/>
        public abstract DataProtectionCategory Category { get; }

        /// <inheritdoc/>
        public abstract DataProtectionCapabilities Capabilities { get; }

        #region Abstract Core Operations

        /// <summary>
        /// Core backup implementation to be provided by derived classes.
        /// </summary>
        /// <param name="request">Backup request parameters.</param>
        /// <param name="progressCallback">Callback for progress updates.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Result of the backup operation.</returns>
        protected abstract Task<BackupResult> CreateBackupCoreAsync(
            BackupRequest request,
            Action<BackupProgress> progressCallback,
            CancellationToken ct);

        /// <summary>
        /// Core restore implementation to be provided by derived classes.
        /// </summary>
        /// <param name="request">Restore request parameters.</param>
        /// <param name="progressCallback">Callback for progress updates.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Result of the restore operation.</returns>
        protected abstract Task<RestoreResult> RestoreCoreAsync(
            RestoreRequest request,
            Action<RestoreProgress> progressCallback,
            CancellationToken ct);

        /// <summary>
        /// Core validation implementation for backup integrity.
        /// </summary>
        /// <param name="backupId">Backup ID to validate.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Validation result.</returns>
        protected abstract Task<ValidationResult> ValidateBackupCoreAsync(string backupId, CancellationToken ct);

        /// <summary>
        /// Core catalog listing implementation.
        /// </summary>
        /// <param name="query">Query parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Matching backup entries.</returns>
        protected abstract Task<IEnumerable<BackupCatalogEntry>> ListBackupsCoreAsync(
            BackupListQuery query, CancellationToken ct);

        /// <summary>
        /// Core backup info retrieval.
        /// </summary>
        /// <param name="backupId">Backup ID.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Backup catalog entry or null.</returns>
        protected abstract Task<BackupCatalogEntry?> GetBackupInfoCoreAsync(string backupId, CancellationToken ct);

        /// <summary>
        /// Core backup deletion implementation.
        /// </summary>
        /// <param name="backupId">Backup ID to delete.</param>
        /// <param name="ct">Cancellation token.</param>
        protected abstract Task DeleteBackupCoreAsync(string backupId, CancellationToken ct);

        #endregion

        #region IDataProtectionStrategy Implementation

        /// <inheritdoc/>
        public async Task<BackupResult> CreateBackupAsync(BackupRequest request, CancellationToken ct = default)
        {
            var backupId = Guid.NewGuid().ToString("N");
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeOperations[backupId] = cts;

            var sw = Stopwatch.StartNew();
            try
            {
                void UpdateProgress(BackupProgress progress)
                {
                    _backupProgress[backupId] = progress with { BackupId = backupId };
                    PublishProgress(progress);
                }

                var result = await CreateBackupCoreAsync(request, UpdateProgress, cts.Token);
                sw.Stop();

                UpdateBackupStats(result, sw.Elapsed);
                await PublishBackupCompletedAsync(result);

                return result with { BackupId = backupId };
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                IncrementFailedBackups();
                await PublishBackupCancelledAsync(backupId);
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                IncrementFailedBackups();
                await PublishBackupFailedAsync(backupId, ex.Message);
                throw;
            }
            finally
            {
                _activeOperations.TryRemove(backupId, out _);
                _backupProgress.TryRemove(backupId, out _);
            }
        }

        /// <inheritdoc/>
        public Task<BackupProgress> GetBackupProgressAsync(string backupId, CancellationToken ct = default)
        {
            if (_backupProgress.TryGetValue(backupId, out var progress))
            {
                return Task.FromResult(progress);
            }

            return Task.FromResult(new BackupProgress
            {
                BackupId = backupId,
                Phase = "Unknown",
                PercentComplete = 0
            });
        }

        /// <inheritdoc/>
        public Task CancelBackupAsync(string backupId, CancellationToken ct = default)
        {
            if (_activeOperations.TryGetValue(backupId, out var cts))
            {
                cts.Cancel();
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public async Task<RestoreResult> RestoreAsync(RestoreRequest request, CancellationToken ct = default)
        {
            var restoreId = Guid.NewGuid().ToString("N");
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeOperations[restoreId] = cts;

            var sw = Stopwatch.StartNew();
            try
            {
                void UpdateProgress(RestoreProgress progress)
                {
                    _restoreProgress[restoreId] = progress with { RestoreId = restoreId };
                    PublishRestoreProgress(progress);
                }

                var result = await RestoreCoreAsync(request, UpdateProgress, cts.Token);
                sw.Stop();

                UpdateRestoreStats(result, sw.Elapsed);
                await PublishRestoreCompletedAsync(result);

                return result with { RestoreId = restoreId };
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                IncrementFailedRestores();
                await PublishRestoreCancelledAsync(restoreId);
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                IncrementFailedRestores();
                await PublishRestoreFailedAsync(restoreId, ex.Message);
                throw;
            }
            finally
            {
                _activeOperations.TryRemove(restoreId, out _);
                _restoreProgress.TryRemove(restoreId, out _);
            }
        }

        /// <inheritdoc/>
        public Task<RestoreProgress> GetRestoreProgressAsync(string restoreId, CancellationToken ct = default)
        {
            if (_restoreProgress.TryGetValue(restoreId, out var progress))
            {
                return Task.FromResult(progress);
            }

            return Task.FromResult(new RestoreProgress
            {
                RestoreId = restoreId,
                Phase = "Unknown",
                PercentComplete = 0
            });
        }

        /// <inheritdoc/>
        public Task CancelRestoreAsync(string restoreId, CancellationToken ct = default)
        {
            if (_activeOperations.TryGetValue(restoreId, out var cts))
            {
                cts.Cancel();
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<IEnumerable<BackupCatalogEntry>> ListBackupsAsync(BackupListQuery query, CancellationToken ct = default)
        {
            return ListBackupsCoreAsync(query, ct);
        }

        /// <inheritdoc/>
        public Task<BackupCatalogEntry?> GetBackupInfoAsync(string backupId, CancellationToken ct = default)
        {
            return GetBackupInfoCoreAsync(backupId, ct);
        }

        /// <inheritdoc/>
        public Task DeleteBackupAsync(string backupId, CancellationToken ct = default)
        {
            return DeleteBackupCoreAsync(backupId, ct);
        }

        /// <inheritdoc/>
        public async Task<ValidationResult> ValidateBackupAsync(string backupId, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await ValidateBackupCoreAsync(backupId, ct);
                sw.Stop();

                UpdateValidationStats(result.IsValid);
                return result with { Duration = sw.Elapsed };
            }
            catch
            {
                sw.Stop();
                IncrementFailedValidations();
                throw;
            }
        }

        /// <inheritdoc/>
        public virtual Task<ValidationResult> ValidateRestoreTargetAsync(RestoreRequest request, CancellationToken ct = default)
        {
            // Default implementation - derived classes can override
            var issues = new List<ValidationIssue>();

            if (string.IsNullOrEmpty(request.TargetPath))
            {
                issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Code = "TARGET_PATH_EMPTY",
                    Message = "No target path specified, will restore to original locations"
                });
            }
            else if (!Directory.Exists(Path.GetDirectoryName(request.TargetPath)))
            {
                issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Error,
                    Code = "TARGET_PATH_INVALID",
                    Message = "Target path parent directory does not exist",
                    AffectedItem = request.TargetPath
                });
            }

            return Task.FromResult(new ValidationResult
            {
                IsValid = !issues.Any(i => i.Severity >= ValidationSeverity.Error),
                Errors = issues.Where(i => i.Severity >= ValidationSeverity.Error).ToList(),
                Warnings = issues.Where(i => i.Severity == ValidationSeverity.Warning).ToList(),
                ChecksPerformed = new[] { "TargetPathValidation" }
            });
        }

        /// <inheritdoc/>
        public DataProtectionStatistics GetStatistics()
        {
            lock (_statsLock)
            {
                return new DataProtectionStatistics
                {
                    TotalBackups = _statistics.TotalBackups,
                    SuccessfulBackups = _statistics.SuccessfulBackups,
                    FailedBackups = _statistics.FailedBackups,
                    TotalRestores = _statistics.TotalRestores,
                    SuccessfulRestores = _statistics.SuccessfulRestores,
                    FailedRestores = _statistics.FailedRestores,
                    TotalBytesBackedUp = _statistics.TotalBytesBackedUp,
                    TotalBytesStored = _statistics.TotalBytesStored,
                    TotalBytesRestored = _statistics.TotalBytesRestored,
                    AverageBackupThroughput = _statistics.AverageBackupThroughput,
                    AverageRestoreThroughput = _statistics.AverageRestoreThroughput,
                    LastBackupTime = _statistics.LastBackupTime,
                    LastRestoreTime = _statistics.LastRestoreTime,
                    TotalValidations = _statistics.TotalValidations,
                    SuccessfulValidations = _statistics.SuccessfulValidations,
                    FailedValidations = _statistics.FailedValidations
                };
            }
        }

        #endregion

        // Intelligence boilerplate (ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability)
        // removed per AD-05 (Phase 25b). StrategyBase shim provides backward-compat.
        // MessageBus/IsIntelligenceAvailable preserved locally for domain helpers below.

        #region Intelligence Helpers

        /// <summary>
        /// Gets a description for this strategy.
        /// </summary>
        protected virtual string GetStrategyDescription()
        {
            return $"{StrategyName} data protection strategy providing {Category} capabilities";
        }

        /// <summary>
        /// Gets the semantic description for AI discovery.
        /// </summary>
        protected virtual string GetSemanticDescription()
        {
            return $"Use {StrategyName} for {Category} data protection operations";
        }

        /// <summary>
        /// Gets knowledge payload for this strategy.
        /// </summary>
        protected virtual Dictionary<string, object> GetKnowledgePayload()
        {
            return new Dictionary<string, object>
            {
                ["strategyId"] = StrategyId,
                ["strategyName"] = StrategyName,
                ["category"] = Category.ToString(),
                ["capabilities"] = Capabilities.ToString(),
                ["supportsCompression"] = Capabilities.HasFlag(DataProtectionCapabilities.Compression),
                ["supportsEncryption"] = Capabilities.HasFlag(DataProtectionCapabilities.Encryption),
                ["supportsDeduplication"] = Capabilities.HasFlag(DataProtectionCapabilities.Deduplication),
                ["supportsPointInTime"] = Capabilities.HasFlag(DataProtectionCapabilities.PointInTimeRecovery),
                ["supportsGranularRecovery"] = Capabilities.HasFlag(DataProtectionCapabilities.GranularRecovery),
                ["intelligenceAware"] = Capabilities.HasFlag(DataProtectionCapabilities.IntelligenceAware)
            };
        }

        /// <summary>
        /// Gets capability metadata for this strategy.
        /// </summary>
        protected virtual Dictionary<string, object> GetCapabilityMetadata()
        {
            return new Dictionary<string, object>
            {
                ["strategyId"] = StrategyId,
                ["category"] = Category.ToString(),
                ["capabilities"] = (int)Capabilities
            };
        }

        /// <summary>
        /// Gets tags for this strategy.
        /// </summary>
        protected virtual string[] GetKnowledgeTags()
        {
            var tags = new List<string>
            {
                "dataprotection",
                "strategy",
                Category.ToString().ToLowerInvariant(),
                StrategyId.ToLowerInvariant()
            };

            if (Capabilities.HasFlag(DataProtectionCapabilities.CloudTarget))
                tags.Add("cloud");
            if (Capabilities.HasFlag(DataProtectionCapabilities.DatabaseAware))
                tags.Add("database");
            if (Capabilities.HasFlag(DataProtectionCapabilities.KubernetesIntegration))
                tags.Add("kubernetes");
            if (Capabilities.HasFlag(DataProtectionCapabilities.IntelligenceAware))
                tags.Add("ai-enhanced");

            return tags.ToArray();
        }

        /// <summary>
        /// Requests an AI recommendation for optimal backup parameters.
        /// </summary>
        /// <param name="dataProfile">Profile of the data to be backed up.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Recommended parameters or null if unavailable.</returns>
        protected async Task<Dictionary<string, object>?> RequestBackupRecommendationAsync(
            Dictionary<string, object> dataProfile,
            CancellationToken ct = default)
        {
            if (!IsIntelligenceAvailable) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "dataprotection.recommend.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = StrategyId,
                    Payload = new Dictionary<string, object>
                    {
                        ["strategyId"] = StrategyId,
                        ["category"] = Category.ToString(),
                        ["dataProfile"] = dataProfile
                    }
                };

                await MessageBus!.PublishAsync(DataProtectionTopics.IntelligenceRecommendation, request, ct);
                return null; // Actual implementation would await response
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Requests AI anomaly detection on backup data.
        /// </summary>
        /// <param name="backupId">Backup ID to analyze.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Anomaly detection result or null if unavailable.</returns>
        protected async Task<Dictionary<string, object>?> RequestAnomalyDetectionAsync(
            string backupId,
            CancellationToken ct = default)
        {
            if (!IsIntelligenceAvailable) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "dataprotection.anomaly.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = StrategyId,
                    Payload = new Dictionary<string, object>
                    {
                        ["backupId"] = backupId,
                        ["strategyId"] = StrategyId
                    }
                };

                await MessageBus!.PublishAsync(DataProtectionTopics.IntelligenceAnomalyDetection, request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Statistics Helpers

        private void UpdateBackupStats(BackupResult result, TimeSpan duration)
        {
            lock (_statsLock)
            {
                _statistics.TotalBackups++;
                if (result.Success)
                {
                    _statistics.SuccessfulBackups++;
                    _statistics.TotalBytesBackedUp += result.TotalBytes;
                    _statistics.TotalBytesStored += result.StoredBytes;
                    _statistics.LastBackupTime = DateTimeOffset.UtcNow;

                    // Update running average throughput
                    var throughput = duration.TotalSeconds > 0 ? result.TotalBytes / duration.TotalSeconds : 0;
                    _statistics.AverageBackupThroughput =
                        (_statistics.AverageBackupThroughput * (_statistics.SuccessfulBackups - 1) + throughput)
                        / _statistics.SuccessfulBackups;
                }
                else
                {
                    _statistics.FailedBackups++;
                }
            }
        }

        private void UpdateRestoreStats(RestoreResult result, TimeSpan duration)
        {
            lock (_statsLock)
            {
                _statistics.TotalRestores++;
                if (result.Success)
                {
                    _statistics.SuccessfulRestores++;
                    _statistics.TotalBytesRestored += result.TotalBytes;
                    _statistics.LastRestoreTime = DateTimeOffset.UtcNow;

                    var throughput = duration.TotalSeconds > 0 ? result.TotalBytes / duration.TotalSeconds : 0;
                    _statistics.AverageRestoreThroughput =
                        (_statistics.AverageRestoreThroughput * (_statistics.SuccessfulRestores - 1) + throughput)
                        / _statistics.SuccessfulRestores;
                }
                else
                {
                    _statistics.FailedRestores++;
                }
            }
        }

        private void UpdateValidationStats(bool success)
        {
            lock (_statsLock)
            {
                _statistics.TotalValidations++;
                if (success)
                    _statistics.SuccessfulValidations++;
                else
                    _statistics.FailedValidations++;
            }
        }

        private void IncrementFailedBackups()
        {
            lock (_statsLock)
            {
                _statistics.TotalBackups++;
                _statistics.FailedBackups++;
            }
        }

        private void IncrementFailedRestores()
        {
            lock (_statsLock)
            {
                _statistics.TotalRestores++;
                _statistics.FailedRestores++;
            }
        }

        private void IncrementFailedValidations()
        {
            lock (_statsLock)
            {
                _statistics.TotalValidations++;
                _statistics.FailedValidations++;
            }
        }

        #endregion

        #region Message Publishing

        private void PublishProgress(BackupProgress progress)
        {
            if (!IsIntelligenceAvailable) return;
            try
            {
                MessageBus!.PublishAsync(DataProtectionTopics.BackupProgress, new PluginMessage
                {
                    Type = "backup.progress",
                    Source = StrategyId,
                    Payload = new Dictionary<string, object>
                    {
                        ["backupId"] = progress.BackupId,
                        ["phase"] = progress.Phase,
                        ["percentComplete"] = progress.PercentComplete,
                        ["bytesProcessed"] = progress.BytesProcessed,
                        ["totalBytes"] = progress.TotalBytes
                    }
                }).ConfigureAwait(false);
            }
            catch { /* Best effort */ }
        }

        private void PublishRestoreProgress(RestoreProgress progress)
        {
            if (!IsIntelligenceAvailable) return;
            try
            {
                MessageBus!.PublishAsync(DataProtectionTopics.RestoreProgress, new PluginMessage
                {
                    Type = "restore.progress",
                    Source = StrategyId,
                    Payload = new Dictionary<string, object>
                    {
                        ["restoreId"] = progress.RestoreId,
                        ["phase"] = progress.Phase,
                        ["percentComplete"] = progress.PercentComplete,
                        ["bytesRestored"] = progress.BytesRestored,
                        ["totalBytes"] = progress.TotalBytes
                    }
                }).ConfigureAwait(false);
            }
            catch { /* Best effort */ }
        }

        private Task PublishBackupCompletedAsync(BackupResult result)
        {
            if (!IsIntelligenceAvailable) return Task.CompletedTask;
            return MessageBus!.PublishAsync(DataProtectionTopics.BackupCompleted, new PluginMessage
            {
                Type = "backup.completed",
                Source = StrategyId,
                Payload = new Dictionary<string, object>
                {
                    ["backupId"] = result.BackupId,
                    ["success"] = result.Success,
                    ["totalBytes"] = result.TotalBytes,
                    ["storedBytes"] = result.StoredBytes,
                    ["duration"] = result.Duration.TotalSeconds
                }
            });
        }

        private Task PublishBackupFailedAsync(string backupId, string error)
        {
            if (!IsIntelligenceAvailable) return Task.CompletedTask;
            return MessageBus!.PublishAsync(DataProtectionTopics.BackupFailed, new PluginMessage
            {
                Type = "backup.failed",
                Source = StrategyId,
                Payload = new Dictionary<string, object>
                {
                    ["backupId"] = backupId,
                    ["error"] = error
                }
            });
        }

        private Task PublishBackupCancelledAsync(string backupId)
        {
            if (!IsIntelligenceAvailable) return Task.CompletedTask;
            return MessageBus!.PublishAsync(DataProtectionTopics.BackupCancelled, new PluginMessage
            {
                Type = "backup.cancelled",
                Source = StrategyId,
                Payload = new Dictionary<string, object> { ["backupId"] = backupId }
            });
        }

        private Task PublishRestoreCompletedAsync(RestoreResult result)
        {
            if (!IsIntelligenceAvailable) return Task.CompletedTask;
            return MessageBus!.PublishAsync(DataProtectionTopics.RestoreCompleted, new PluginMessage
            {
                Type = "restore.completed",
                Source = StrategyId,
                Payload = new Dictionary<string, object>
                {
                    ["restoreId"] = result.RestoreId,
                    ["success"] = result.Success,
                    ["totalBytes"] = result.TotalBytes,
                    ["duration"] = result.Duration.TotalSeconds
                }
            });
        }

        private Task PublishRestoreFailedAsync(string restoreId, string error)
        {
            if (!IsIntelligenceAvailable) return Task.CompletedTask;
            return MessageBus!.PublishAsync(DataProtectionTopics.RestoreFailed, new PluginMessage
            {
                Type = "restore.failed",
                Source = StrategyId,
                Payload = new Dictionary<string, object>
                {
                    ["restoreId"] = restoreId,
                    ["error"] = error
                }
            });
        }

        private Task PublishRestoreCancelledAsync(string restoreId)
        {
            if (!IsIntelligenceAvailable) return Task.CompletedTask;
            return MessageBus!.PublishAsync(DataProtectionTopics.RestoreCancelled, new PluginMessage
            {
                Type = "restore.cancelled",
                Source = StrategyId,
                Payload = new Dictionary<string, object> { ["restoreId"] = restoreId }
            });
        }

        #endregion
    }
}
