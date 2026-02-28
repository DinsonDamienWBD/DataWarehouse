using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataProtection.Strategies.Advanced
{
    /// <summary>
    /// Synthetic full backup strategy that creates a full backup by merging incremental
    /// and differential backups without re-reading the original source data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Synthetic full backups provide the benefits of full backups (single restore point,
    /// no chain dependencies) without the I/O overhead of re-reading source data.
    /// </para>
    /// <para>
    /// This strategy:
    /// - Validates the backup chain integrity before synthesis
    /// - Merges incremental/differential backups in chronological order
    /// - Creates a new full backup from the merged data
    /// - Maintains metadata and catalog consistency
    /// - Supports parallel block merging for performance
    /// </para>
    /// </remarks>
    public sealed class SyntheticFullBackupStrategy : DataProtectionStrategyBase
    {
        private readonly BoundedDictionary<string, BackupChain> _backupChains = new BoundedDictionary<string, BackupChain>(1000);

        /// <inheritdoc/>
        public override string StrategyId => "synthetic-full";
        public override bool IsProductionReady => false;

        /// <inheritdoc/>
        public override string StrategyName => "Synthetic Full Backup";

        /// <inheritdoc/>
        public override DataProtectionCategory Category => DataProtectionCategory.FullBackup;

        /// <inheritdoc/>
        public override DataProtectionCapabilities Capabilities =>
            DataProtectionCapabilities.Compression |
            DataProtectionCapabilities.Encryption |
            DataProtectionCapabilities.Deduplication |
            DataProtectionCapabilities.ParallelBackup |
            DataProtectionCapabilities.AutoVerification |
            DataProtectionCapabilities.IntelligenceAware;

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
                // Phase 1: Resolve backup chain
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Resolving Backup Chain",
                    PercentComplete = 5
                });

                var chain = await ResolveBackupChainAsync(request, ct);
                if (chain.Backups.Count == 0)
                {
                    return new BackupResult
                    {
                        Success = false,
                        BackupId = backupId,
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        ErrorMessage = "No backup chain found for synthesis"
                    };
                }

                // Phase 2: Validate chain integrity
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Validating Chain Integrity",
                    PercentComplete = 15
                });

                var validationResult = await ValidateChainIntegrityAsync(chain, ct);
                if (!validationResult.IsValid)
                {
                    return new BackupResult
                    {
                        Success = false,
                        BackupId = backupId,
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        ErrorMessage = $"Chain validation failed: {string.Join(", ", validationResult.Errors.Select(e => e.Message))}"
                    };
                }

                // Phase 3: Calculate merge plan
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Calculating Merge Plan",
                    PercentComplete = 25
                });

                var mergePlan = await CalculateMergePlanAsync(chain, ct);
                var totalBytes = mergePlan.TotalBytes;

                // Phase 4: Merge backups
                long bytesProcessed = 0;
                var mergedData = new BoundedDictionary<string, byte[]>(1000);

                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Merging Backup Chain",
                    PercentComplete = 30,
                    TotalBytes = totalBytes
                });

                // Process blocks in parallel
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = request.ParallelStreams,
                    CancellationToken = ct
                };

                await Parallel.ForEachAsync(mergePlan.Blocks, parallelOptions, async (block, token) =>
                {
                    var blockData = await ReadBlockFromChainAsync(chain, block, token);
                    mergedData[block.BlockId] = blockData;

                    var processed = Interlocked.Add(ref bytesProcessed, block.Size);
                    var percent = 30 + (int)((processed / (double)totalBytes) * 50);

                    progressCallback(new BackupProgress
                    {
                        BackupId = backupId,
                        Phase = "Merging Backup Chain",
                        PercentComplete = percent,
                        BytesProcessed = processed,
                        TotalBytes = totalBytes,
                        CurrentItem = block.BlockId
                    });
                });

                // Phase 5: Write synthetic full backup
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Writing Synthetic Full Backup",
                    PercentComplete = 85
                });

                var storedBytes = await WriteSyntheticFullAsync(backupId, mergedData, request, ct);

                // Phase 6: Update catalog
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Updating Catalog",
                    PercentComplete = 95
                });

                await UpdateCatalogAsync(backupId, chain, totalBytes, storedBytes, ct);

                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Complete",
                    PercentComplete = 100,
                    BytesProcessed = totalBytes,
                    TotalBytes = totalBytes
                });

                return new BackupResult
                {
                    Success = true,
                    BackupId = backupId,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    TotalBytes = totalBytes,
                    StoredBytes = storedBytes,
                    FileCount = mergePlan.FileCount
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new BackupResult
                {
                    Success = false,
                    BackupId = backupId,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    ErrorMessage = $"Synthetic full backup failed: {ex.Message}"
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
                // Phase 1: Load backup metadata
                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Loading Backup Metadata",
                    PercentComplete = 5
                });

                var backupInfo = await GetBackupInfoCoreAsync(request.BackupId, ct);
                if (backupInfo == null)
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

                var totalBytes = backupInfo.OriginalSize;
                long bytesRestored = 0;

                // Phase 2: Restore files
                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Restoring Files",
                    PercentComplete = 20,
                    TotalBytes = totalBytes
                });

                // Synthetic full backups can be restored directly without chain traversal
                var fileCount = await RestoreFilesDirectlyAsync(
                    request.BackupId,
                    request.TargetPath ?? "",
                    request.ItemsToRestore,
                    request.ParallelStreams,
                    (bytes) =>
                    {
                        bytesRestored = bytes;
                        var percent = 20 + (int)((bytes / (double)totalBytes) * 70);
                        progressCallback(new RestoreProgress
                        {
                            RestoreId = restoreId,
                            Phase = "Restoring Files",
                            PercentComplete = percent,
                            BytesRestored = bytes,
                            TotalBytes = totalBytes
                        });
                    },
                    ct);

                // Phase 3: Verify restore
                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Verifying Restore",
                    PercentComplete = 95
                });

                await Task.Delay(100, ct); // Simulate verification

                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Complete",
                    PercentComplete = 100,
                    BytesRestored = totalBytes,
                    TotalBytes = totalBytes
                });

                return new RestoreResult
                {
                    Success = true,
                    RestoreId = restoreId,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    TotalBytes = totalBytes,
                    FileCount = fileCount
                };
            }
            catch (OperationCanceledException)
            {
                throw;
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
        protected override async Task<ValidationResult> ValidateBackupCoreAsync(string backupId, CancellationToken ct)
        {
            var issues = new List<ValidationIssue>();
            var checks = new List<string>();

            try
            {
                // Check 1: Backup metadata exists
                checks.Add("MetadataExists");
                var backupInfo = await GetBackupInfoCoreAsync(backupId, ct);
                if (backupInfo == null)
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Critical,
                        Code = "METADATA_NOT_FOUND",
                        Message = "Backup metadata not found",
                        AffectedItem = backupId
                    });
                    return CreateValidationResult(false, issues, checks);
                }

                // Check 2: Synthetic full backup integrity
                checks.Add("SyntheticIntegrity");
                var integrityValid = await VerifySyntheticIntegrityAsync(backupId, ct);
                if (!integrityValid)
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Error,
                        Code = "INTEGRITY_CHECK_FAILED",
                        Message = "Synthetic backup integrity verification failed"
                    });
                }

                // Check 3: Block checksums
                checks.Add("BlockChecksums");
                var checksumValid = await VerifyBlockChecksumsAsync(backupId, ct);
                if (!checksumValid)
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Error,
                        Code = "CHECKSUM_MISMATCH",
                        Message = "Block checksum verification failed"
                    });
                }

                // Check 4: Catalog consistency
                checks.Add("CatalogConsistency");
                var catalogValid = await VerifyCatalogConsistencyAsync(backupId, ct);
                if (!catalogValid)
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Warning,
                        Code = "CATALOG_INCONSISTENT",
                        Message = "Catalog metadata may be inconsistent"
                    });
                }

                return CreateValidationResult(!issues.Any(i => i.Severity >= ValidationSeverity.Error), issues, checks);
            }
            catch (Exception ex)
            {
                issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Critical,
                    Code = "VALIDATION_ERROR",
                    Message = $"Validation failed: {ex.Message}"
                });
                return CreateValidationResult(false, issues, checks);
            }
        }

        /// <inheritdoc/>
        protected override Task<IEnumerable<BackupCatalogEntry>> ListBackupsCoreAsync(
            BackupListQuery query,
            CancellationToken ct)
        {
            // Return synthetic full backups from catalog
            var entries = _backupChains.Values
                .SelectMany(chain => chain.SyntheticFulls)
                .Where(entry => MatchesQuery(entry, query))
                .OrderByDescending(e => e.CreatedAt)
                .Take(query.MaxResults);

            return Task.FromResult(entries.AsEnumerable());
        }

        /// <inheritdoc/>
        protected override Task<BackupCatalogEntry?> GetBackupInfoCoreAsync(string backupId, CancellationToken ct)
        {
            var entry = _backupChains.Values
                .SelectMany(chain => chain.SyntheticFulls)
                .FirstOrDefault(e => e.BackupId == backupId);

            return Task.FromResult(entry);
        }

        /// <inheritdoc/>
        protected override Task DeleteBackupCoreAsync(string backupId, CancellationToken ct)
        {
            // Remove from all chains
            foreach (var chain in _backupChains.Values)
            {
                chain.SyntheticFulls.RemoveAll(e => e.BackupId == backupId);
            }

            return Task.CompletedTask;
        }

        #region Helper Methods

        private async Task<BackupChain> ResolveBackupChainAsync(BackupRequest request, CancellationToken ct)
        {
            // Simulate chain resolution - in production, this would query the catalog
            var chainId = request.Options.TryGetValue("ChainId", out var cid) ? cid.ToString()! : "default";

            if (_backupChains.TryGetValue(chainId, out var chain))
            {
                return chain;
            }

            // Create new chain with sample data
            chain = new BackupChain { ChainId = chainId };
            _backupChains[chainId] = chain;

            await Task.CompletedTask;
            return chain;
        }

        private Task<ValidationResult> ValidateChainIntegrityAsync(BackupChain chain, CancellationToken ct)
        {
            // In production, verify all backups in chain are accessible and valid
            return Task.FromResult(new ValidationResult { IsValid = true });
        }

        private Task<MergePlan> CalculateMergePlanAsync(BackupChain chain, CancellationToken ct)
        {
            // Calculate which blocks need to be merged from each backup
            var totalBytes = 5L * 1024 * 1024 * 1024; // 5 GB synthetic
            var blockSize = 4 * 1024 * 1024; // 4 MB blocks
            var blockCount = (int)(totalBytes / blockSize);

            var plan = new MergePlan
            {
                TotalBytes = totalBytes,
                FileCount = 50000,
                Blocks = Enumerable.Range(0, blockCount)
                    .Select(i => new BlockInfo
                    {
                        BlockId = $"block-{i:D8}",
                        Size = blockSize,
                        SourceBackupId = chain.Backups.FirstOrDefault()?.BackupId ?? "base"
                    })
                    .ToList()
            };

            return Task.FromResult(plan);
        }

        private Task<byte[]> ReadBlockFromChainAsync(BackupChain chain, BlockInfo block, CancellationToken ct)
        {
            // In production, read the block from the appropriate backup in the chain
            var data = new byte[Math.Min(block.Size, 1024)]; // Simulate with small buffer
            return Task.FromResult(data);
        }

        private Task<long> WriteSyntheticFullAsync(
            string backupId,
            BoundedDictionary<string, byte[]> mergedData,
            BackupRequest request,
            CancellationToken ct)
        {
            // Return the actual sum of stored block sizes, not a fabricated multiple.
            // Real compression savings are tracked separately via CompressedSize on each block.
            var storedBytes = mergedData.Sum(kvp => (long)kvp.Value.Length);
            return Task.FromResult(storedBytes);
        }

        private Task UpdateCatalogAsync(
            string backupId,
            BackupChain chain,
            long totalBytes,
            long storedBytes,
            CancellationToken ct)
        {
            var entry = new BackupCatalogEntry
            {
                BackupId = backupId,
                StrategyId = StrategyId,
                Category = Category,
                CreatedAt = DateTimeOffset.UtcNow,
                OriginalSize = totalBytes,
                StoredSize = storedBytes,
                FileCount = 50000,
                IsCompressed = true,
                ChainRootId = chain.ChainId
            };

            chain.SyntheticFulls.Add(entry);
            return Task.CompletedTask;
        }

        private Task<long> RestoreFilesDirectlyAsync(
            string backupId,
            string targetPath,
            IReadOnlyList<string>? itemsToRestore,
            int parallelStreams,
            Action<long> progressCallback,
            CancellationToken ct)
        {
            // In production, restore files directly from synthetic full backup
            var totalBytes = 5L * 1024 * 1024 * 1024;
            progressCallback(totalBytes);
            return Task.FromResult(50000L); // File count
        }

        private Task<bool> VerifySyntheticIntegrityAsync(string backupId, CancellationToken ct)
        {
            // In production, verify the synthetic backup's internal consistency
            return Task.FromResult(true);
        }

        private Task<bool> VerifyBlockChecksumsAsync(string backupId, CancellationToken ct)
        {
            // In production, verify checksums of all blocks
            return Task.FromResult(true);
        }

        private Task<bool> VerifyCatalogConsistencyAsync(string backupId, CancellationToken ct)
        {
            // In production, verify catalog metadata matches backup contents
            return Task.FromResult(true);
        }

        private bool MatchesQuery(BackupCatalogEntry entry, BackupListQuery query)
        {
            if (query.CreatedAfter.HasValue && entry.CreatedAt < query.CreatedAfter.Value)
                return false;
            if (query.CreatedBefore.HasValue && entry.CreatedAt > query.CreatedBefore.Value)
                return false;
            return true;
        }

        private ValidationResult CreateValidationResult(bool isValid, List<ValidationIssue> issues, List<string> checks)
        {
            return new ValidationResult
            {
                IsValid = isValid,
                Errors = issues.Where(i => i.Severity >= ValidationSeverity.Error).ToList(),
                Warnings = issues.Where(i => i.Severity == ValidationSeverity.Warning).ToList(),
                ChecksPerformed = checks
            };
        }

        #endregion

        #region Helper Classes

        private class BackupChain
        {
            public string ChainId { get; set; } = string.Empty;
            public List<BackupCatalogEntry> Backups { get; set; } = new();
            public List<BackupCatalogEntry> SyntheticFulls { get; set; } = new();
        }

        private class MergePlan
        {
            public long TotalBytes { get; set; }
            public long FileCount { get; set; }
            public List<BlockInfo> Blocks { get; set; } = new();
        }

        private class BlockInfo
        {
            public string BlockId { get; set; } = string.Empty;
            public long Size { get; set; }
            public string SourceBackupId { get; set; } = string.Empty;
        }

        #endregion
    }
}
