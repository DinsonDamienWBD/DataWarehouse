#pragma warning disable CS0618 // DataProtectionStrategyRegistry obsolete -- retained as typed lookup thin wrapper
using DataWarehouse.Plugins.UltimateDataProtection.Catalog;
using DataWarehouse.Plugins.UltimateDataProtection.Validation;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataProtection.Subsystems
{
    /// <summary>
    /// Implementation of the backup subsystem providing comprehensive backup operations.
    /// Manages backup strategies, progress tracking, and validation.
    /// </summary>
    public sealed class BackupSubsystem : IBackupSubsystem
    {
        private readonly DataProtectionStrategyRegistry _registry;
        private readonly BackupCatalog _catalog;
        private readonly BackupValidator _validator;
        private readonly BoundedDictionary<string, CancellationTokenSource> _activeOperations = new BoundedDictionary<string, CancellationTokenSource>(1000);

        /// <summary>
        /// Creates a new backup subsystem.
        /// </summary>
        /// <param name="registry">Strategy registry.</param>
        /// <param name="catalog">Backup catalog.</param>
        /// <param name="validator">Backup validator.</param>
        public BackupSubsystem(
            DataProtectionStrategyRegistry registry,
            BackupCatalog catalog,
            BackupValidator validator)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        }

        /// <inheritdoc/>
        public IReadOnlyCollection<IDataProtectionStrategy> Strategies => _registry.Strategies;

        /// <inheritdoc/>
        public async Task<BackupResult> CreateBackupAsync(
            string strategyId,
            BackupRequest request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var strategy = _registry.GetRequiredStrategy(strategyId);

            // Create cancellable operation tracking
            using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var backupId = request.RequestId;
            _activeOperations.TryAdd(backupId, operationCts);

            try
            {
                // Perform the backup
                var result = await strategy.CreateBackupAsync(request, operationCts.Token);

                // Add to catalog if successful
                if (result.Success)
                {
                    var catalogEntry = new BackupCatalogEntry
                    {
                        BackupId = result.BackupId,
                        Name = request.BackupName,
                        StrategyId = strategyId,
                        Category = strategy.Category,
                        CreatedAt = result.StartTime,
                        Sources = request.Sources,
                        Destination = request.Destination ?? string.Empty,
                        OriginalSize = result.TotalBytes,
                        StoredSize = result.StoredBytes,
                        FileCount = result.FileCount,
                        IsEncrypted = request.EnableEncryption,
                        IsCompressed = request.EnableCompression,
                        Tags = request.Tags
                    };

                    _catalog.AddOrUpdate(catalogEntry);
                }

                return result;
            }
            finally
            {
                _activeOperations.TryRemove(backupId, out _);
                operationCts.Dispose();
            }
        }

        /// <inheritdoc/>
        public async Task<BackupProgress> GetProgressAsync(string backupId, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(backupId);

            // Find the backup in catalog to determine strategy
            var catalogEntry = _catalog.Get(backupId);
            if (catalogEntry == null)
            {
                // Check active operations
                if (!_activeOperations.ContainsKey(backupId))
                {
                    throw new InvalidOperationException($"Backup '{backupId}' not found in active operations or catalog.");
                }

                // Return minimal progress for unknown operations
                return new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Unknown",
                    PercentComplete = 0
                };
            }

            var strategy = _registry.GetStrategy(catalogEntry.StrategyId);
            if (strategy == null)
            {
                throw new InvalidOperationException($"Strategy '{catalogEntry.StrategyId}' not found.");
            }

            return await strategy.GetBackupProgressAsync(backupId, ct);
        }

        /// <inheritdoc/>
        public async Task CancelAsync(string backupId, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(backupId);

            // Cancel via tracked operation
            if (_activeOperations.TryGetValue(backupId, out var cts))
            {
                cts.Cancel();
            }

            // Also notify the strategy
            var catalogEntry = _catalog.Get(backupId);
            if (catalogEntry != null)
            {
                var strategy = _registry.GetStrategy(catalogEntry.StrategyId);
                if (strategy != null)
                {
                    await strategy.CancelBackupAsync(backupId, ct);
                }
            }
        }

        /// <inheritdoc/>
        public Task<IEnumerable<BackupCatalogEntry>> ListBackupsAsync(
            BackupListQuery query,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(query);

            var results = _catalog.Query(query);
            return Task.FromResult(results);
        }

        /// <inheritdoc/>
        public async Task<ValidationResult> ValidateAsync(string backupId, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(backupId);

            // Get backup info from catalog
            var catalogEntry = _catalog.Get(backupId);
            if (catalogEntry == null)
            {
                return new ValidationResult
                {
                    IsValid = false,
                    Errors = new[]
                    {
                        new ValidationIssue
                        {
                            Severity = ValidationSeverity.Critical,
                            Code = "BACKUP_NOT_FOUND",
                            Message = $"Backup '{backupId}' not found in catalog"
                        }
                    }
                };
            }

            // Use validator to perform comprehensive validation
            var result = await _validator.ValidateAsync(
                catalogEntry.StrategyId,
                backupId,
                new ValidationOptions
                {
                    VerifyChecksums = true,
                    VerifyChainIntegrity = !string.IsNullOrEmpty(catalogEntry.ParentBackupId)
                },
                ct);

            // Update catalog validation status
            _catalog.UpdateValidationStatus(backupId, result.IsValid);

            return result;
        }

        /// <summary>
        /// Gets the number of active backup operations.
        /// </summary>
        public int ActiveOperationCount => _activeOperations.Count;

        /// <summary>
        /// Gets backup statistics from the catalog.
        /// </summary>
        public CatalogStatistics GetCatalogStatistics() => _catalog.GetStatistics();
    }
}
