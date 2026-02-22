using DataWarehouse.Plugins.UltimateDataProtection.Catalog;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataProtection.Subsystems
{
    /// <summary>
    /// Implementation of the restore subsystem providing comprehensive recovery operations.
    /// Supports restoring from backups, versions, and point-in-time recovery.
    /// </summary>
    public sealed class RestoreSubsystem : IRestoreSubsystem
    {
        private readonly DataProtectionStrategyRegistry _registry;
        private readonly BackupCatalog _catalog;
        private readonly BoundedDictionary<string, CancellationTokenSource> _activeOperations = new BoundedDictionary<string, CancellationTokenSource>(1000);

        /// <summary>
        /// Creates a new restore subsystem.
        /// </summary>
        /// <param name="registry">Strategy registry.</param>
        /// <param name="catalog">Backup catalog.</param>
        public RestoreSubsystem(
            DataProtectionStrategyRegistry registry,
            BackupCatalog catalog)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        /// <inheritdoc/>
        public async Task<RestoreResult> RestoreAsync(RestoreRequest request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            // Get backup info from catalog
            var backupEntry = _catalog.Get(request.BackupId);
            if (backupEntry == null)
            {
                return new RestoreResult
                {
                    Success = false,
                    RestoreId = request.RequestId,
                    ErrorMessage = $"Backup '{request.BackupId}' not found in catalog.",
                    StartTime = DateTimeOffset.UtcNow,
                    EndTime = DateTimeOffset.UtcNow
                };
            }

            // Get strategy
            var strategy = _registry.GetStrategy(backupEntry.StrategyId);
            if (strategy == null)
            {
                return new RestoreResult
                {
                    Success = false,
                    RestoreId = request.RequestId,
                    ErrorMessage = $"Strategy '{backupEntry.StrategyId}' not found.",
                    StartTime = DateTimeOffset.UtcNow,
                    EndTime = DateTimeOffset.UtcNow
                };
            }

            // Resolve backup chain if this is an incremental
            var restoreChain = await ResolveBackupChainAsync(backupEntry, ct);

            // Create cancellable operation tracking
            using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var restoreId = request.RequestId;
            _activeOperations.TryAdd(restoreId, operationCts);

            try
            {
                RestoreResult? result = null;

                // If this is part of a chain, restore in order (full first, then incrementals)
                if (restoreChain.Count > 1)
                {
                    // Restore chain in order
                    foreach (var chainEntry in restoreChain)
                    {
                        var chainStrategy = _registry.GetRequiredStrategy(chainEntry.StrategyId);
                        var chainRequest = new RestoreRequest
                        {
                            RequestId = $"{request.RequestId}_chain_{chainEntry.BackupId}",
                            BackupId = chainEntry.BackupId,
                            TargetPath = request.TargetPath,
                            PointInTime = request.PointInTime,
                            ItemsToRestore = request.ItemsToRestore,
                            OverwriteExisting = request.OverwriteExisting,
                            PreservePermissions = request.PreservePermissions,
                            ParallelStreams = request.ParallelStreams,
                            BandwidthLimit = request.BandwidthLimit,
                            Options = request.Options
                        };

                        result = await chainStrategy.RestoreAsync(chainRequest, operationCts.Token);

                        if (!result.Success)
                        {
                            return new RestoreResult
                            {
                                Success = false,
                                RestoreId = restoreId,
                                ErrorMessage = $"Failed to restore chain member '{chainEntry.BackupId}': {result.ErrorMessage}",
                                StartTime = result.StartTime,
                                EndTime = result.EndTime
                            };
                        }
                    }
                }
                else
                {
                    // Single backup restore
                    result = await strategy.RestoreAsync(request, operationCts.Token);
                }

                return result ?? new RestoreResult
                {
                    Success = false,
                    RestoreId = restoreId,
                    ErrorMessage = "Restore operation produced no result.",
                    StartTime = DateTimeOffset.UtcNow,
                    EndTime = DateTimeOffset.UtcNow
                };
            }
            finally
            {
                _activeOperations.TryRemove(restoreId, out _);
                operationCts.Dispose();
            }
        }

        /// <inheritdoc/>
        public async Task<RestoreProgress> GetProgressAsync(string restoreId, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(restoreId);

            // Check if it's an active operation
            if (!_activeOperations.ContainsKey(restoreId))
            {
                return new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Not Found",
                    PercentComplete = 0
                };
            }

            // Try to get progress from strategies (simplified - would need to track which strategy is being used)
            // For now, return a basic progress indicator
            return new RestoreProgress
            {
                RestoreId = restoreId,
                Phase = "In Progress",
                PercentComplete = 50
            };
        }

        /// <inheritdoc/>
        public async Task CancelAsync(string restoreId, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(restoreId);

            // Cancel via tracked operation
            if (_activeOperations.TryGetValue(restoreId, out var cts))
            {
                cts.Cancel();
            }
        }

        /// <inheritdoc/>
        public Task<ValidationResult> ValidateTargetAsync(RestoreRequest request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var errors = new List<ValidationIssue>();
            var warnings = new List<ValidationIssue>();

            // Validate backup exists
            var backupEntry = _catalog.Get(request.BackupId);
            if (backupEntry == null)
            {
                errors.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Critical,
                    Code = "BACKUP_NOT_FOUND",
                    Message = $"Backup '{request.BackupId}' not found in catalog.",
                    AffectedItem = request.BackupId
                });
            }

            // Validate strategy exists
            if (backupEntry != null)
            {
                var strategy = _registry.GetStrategy(backupEntry.StrategyId);
                if (strategy == null)
                {
                    errors.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Critical,
                        Code = "STRATEGY_NOT_FOUND",
                        Message = $"Strategy '{backupEntry.StrategyId}' not found.",
                        AffectedItem = backupEntry.StrategyId
                    });
                }
            }

            // Validate target path is specified for file restores
            if (string.IsNullOrWhiteSpace(request.TargetPath))
            {
                warnings.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Code = "NO_TARGET_PATH",
                    Message = "No target path specified. Restore may use default location."
                });
            }

            // Validate backup is not expired
            if (backupEntry?.ExpiresAt.HasValue == true && backupEntry.ExpiresAt.Value <= DateTimeOffset.UtcNow)
            {
                warnings.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Code = "BACKUP_EXPIRED",
                    Message = "Backup has expired. Data may no longer be available.",
                    AffectedItem = request.BackupId
                });
            }

            // Validate backup integrity
            if (backupEntry?.IsValid == false)
            {
                errors.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Error,
                    Code = "BACKUP_INVALID",
                    Message = "Backup failed validation. Restore may be incomplete or corrupted.",
                    AffectedItem = request.BackupId
                });
            }

            return Task.FromResult(new ValidationResult
            {
                IsValid = errors.Count == 0,
                Errors = errors,
                Warnings = warnings,
                ChecksPerformed = new[]
                {
                    "BackupExistence",
                    "StrategyAvailability",
                    "TargetPath",
                    "BackupExpiration",
                    "BackupIntegrity"
                }
            });
        }

        /// <inheritdoc/>
        public Task<IEnumerable<RecoveryPoint>> ListRecoveryPointsAsync(string itemId, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

            // Find all backups that contain this item
            var allBackups = _catalog.GetAll();
            var relevantBackups = allBackups.Where(b =>
                b.Sources.Any(s => s.Contains(itemId, StringComparison.OrdinalIgnoreCase)));

            var recoveryPoints = relevantBackups.Select(b => new RecoveryPoint
            {
                RecoveryPointId = $"rp_{b.BackupId}",
                BackupId = b.BackupId,
                Timestamp = b.CreatedAt,
                Type = b.Category switch
                {
                    DataProtectionCategory.FullBackup => RecoveryPointType.FullBackup,
                    DataProtectionCategory.IncrementalBackup => RecoveryPointType.IncrementalBackup,
                    DataProtectionCategory.Snapshot => RecoveryPointType.Snapshot,
                    DataProtectionCategory.ContinuousProtection => RecoveryPointType.CdpJournal,
                    _ => RecoveryPointType.FullBackup
                },
                IsVerified = b.IsValid == true,
                EstimatedRecoveryTime = EstimateRecoveryTime(b),
                Description = b.Name ?? $"Backup created at {b.CreatedAt:yyyy-MM-dd HH:mm:ss}"
            }).OrderByDescending(rp => rp.Timestamp);

            return Task.FromResult<IEnumerable<RecoveryPoint>>(recoveryPoints.ToList());
        }

        /// <summary>
        /// Resolves the backup chain for an incremental backup.
        /// </summary>
        private async Task<List<BackupCatalogEntry>> ResolveBackupChainAsync(
            BackupCatalogEntry backupEntry,
            CancellationToken ct)
        {
            var chain = new List<BackupCatalogEntry>();

            // If this is part of a chain, get all chain members
            if (!string.IsNullOrEmpty(backupEntry.ChainRootId))
            {
                var chainEntries = _catalog.GetChain(backupEntry.ChainRootId).ToList();

                // Only include backups up to and including the target
                foreach (var entry in chainEntries)
                {
                    chain.Add(entry);
                    if (entry.BackupId == backupEntry.BackupId)
                    {
                        break;
                    }
                }
            }
            else
            {
                // Single backup, not part of a chain
                chain.Add(backupEntry);
            }

            return chain;
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

        /// <summary>
        /// Gets the number of active restore operations.
        /// </summary>
        public int ActiveOperationCount => _activeOperations.Count;
    }
}
