using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataProtection.Catalog
{
    /// <summary>
    /// Manages the backup catalog - tracking all backups, metadata, and dependencies.
    /// Provides search, filter, and retention policy enforcement capabilities.
    /// </summary>
    public sealed class BackupCatalog
    {
        private readonly BoundedDictionary<string, BackupCatalogEntry> _entries = new BoundedDictionary<string, BackupCatalogEntry>(1000);
        private readonly BoundedDictionary<string, List<string>> _chains = new BoundedDictionary<string, List<string>>(1000); // root -> chain members

        /// <summary>
        /// Gets the total number of catalog entries.
        /// </summary>
        public int Count => _entries.Count;

        /// <summary>
        /// Adds or updates a backup entry in the catalog.
        /// </summary>
        /// <param name="entry">The backup catalog entry.</param>
        public void AddOrUpdate(BackupCatalogEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            _entries[entry.BackupId] = entry;

            // Track chain membership
            if (!string.IsNullOrEmpty(entry.ChainRootId))
            {
                _chains.AddOrUpdate(
                    entry.ChainRootId,
                    _ => new List<string> { entry.BackupId },
                    (_, list) => { lock (list) { if (!list.Contains(entry.BackupId)) list.Add(entry.BackupId); } return list; }
                );
            }
        }

        /// <summary>
        /// Gets a backup entry by ID.
        /// </summary>
        /// <param name="backupId">The backup ID.</param>
        /// <returns>The catalog entry, or null if not found.</returns>
        public BackupCatalogEntry? Get(string backupId)
        {
            _entries.TryGetValue(backupId, out var entry);
            return entry;
        }

        /// <summary>
        /// Removes a backup entry from the catalog.
        /// </summary>
        /// <param name="backupId">The backup ID to remove.</param>
        /// <returns>True if removed; false if not found.</returns>
        public bool Remove(string backupId)
        {
            if (_entries.TryRemove(backupId, out var entry))
            {
                // Remove from chain tracking
                if (!string.IsNullOrEmpty(entry.ChainRootId) && _chains.TryGetValue(entry.ChainRootId, out var chain))
                {
                    lock (chain)
                    {
                        chain.Remove(backupId);
                        if (chain.Count == 0)
                            _chains.TryRemove(entry.ChainRootId, out _);
                    }
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Queries backups matching the specified criteria.
        /// </summary>
        /// <param name="query">Query parameters.</param>
        /// <returns>Matching backup entries.</returns>
        public IEnumerable<BackupCatalogEntry> Query(BackupListQuery query)
        {
            var results = _entries.Values.AsEnumerable();

            // Apply filters
            if (!string.IsNullOrEmpty(query.NamePattern))
            {
                results = results.Where(e => e.Name?.Contains(query.NamePattern, StringComparison.OrdinalIgnoreCase) == true);
            }

            if (!string.IsNullOrEmpty(query.SourcePath))
            {
                results = results.Where(e => e.Sources.Any(s => s.Contains(query.SourcePath, StringComparison.OrdinalIgnoreCase)));
            }

            if (query.CreatedAfter.HasValue)
            {
                results = results.Where(e => e.CreatedAt >= query.CreatedAfter.Value);
            }

            if (query.CreatedBefore.HasValue)
            {
                results = results.Where(e => e.CreatedAt <= query.CreatedBefore.Value);
            }

            if (query.Tags != null && query.Tags.Count > 0)
            {
                results = results.Where(e => query.Tags.All(qt =>
                    e.Tags.TryGetValue(qt.Key, out var value) && value == qt.Value));
            }

            // Apply ordering and limits
            return results
                .OrderByDescending(e => e.CreatedAt)
                .Take(query.MaxResults);
        }

        /// <summary>
        /// Gets all backups in a backup chain.
        /// </summary>
        /// <param name="chainRootId">The chain root backup ID.</param>
        /// <returns>All backups in the chain, ordered by creation time.</returns>
        public IEnumerable<BackupCatalogEntry> GetChain(string chainRootId)
        {
            if (!_chains.TryGetValue(chainRootId, out var chainIds))
                return Enumerable.Empty<BackupCatalogEntry>();

            List<string> idsCopy;
            lock (chainIds)
            {
                idsCopy = chainIds.ToList();
            }

            return idsCopy
                .Select(id => _entries.TryGetValue(id, out var e) ? e : null)
                .Where(e => e != null)
                .OrderBy(e => e!.CreatedAt)!;
        }

        /// <summary>
        /// Gets backups by category.
        /// </summary>
        /// <param name="category">The protection category.</param>
        /// <returns>Backups in the specified category.</returns>
        public IEnumerable<BackupCatalogEntry> GetByCategory(DataProtectionCategory category)
        {
            return _entries.Values.Where(e => e.Category == category);
        }

        /// <summary>
        /// Gets backups by strategy.
        /// </summary>
        /// <param name="strategyId">The strategy ID.</param>
        /// <returns>Backups created by the specified strategy.</returns>
        public IEnumerable<BackupCatalogEntry> GetByStrategy(string strategyId)
        {
            return _entries.Values.Where(e => e.StrategyId == strategyId);
        }

        /// <summary>
        /// Gets expired backups based on their expiration time.
        /// </summary>
        /// <param name="asOf">Reference time for expiration check.</param>
        /// <returns>Expired backup entries.</returns>
        public IEnumerable<BackupCatalogEntry> GetExpired(DateTimeOffset? asOf = null)
        {
            var referenceTime = asOf ?? DateTimeOffset.UtcNow;
            return _entries.Values.Where(e => e.ExpiresAt.HasValue && e.ExpiresAt.Value <= referenceTime);
        }

        /// <summary>
        /// Gets backups that need validation (never validated or validated long ago).
        /// </summary>
        /// <param name="maxAge">Maximum age since last validation.</param>
        /// <returns>Backups needing validation.</returns>
        public IEnumerable<BackupCatalogEntry> GetNeedingValidation(TimeSpan maxAge)
        {
            var cutoff = DateTimeOffset.UtcNow - maxAge;
            return _entries.Values.Where(e =>
                !e.LastValidatedAt.HasValue || e.LastValidatedAt.Value < cutoff);
        }

        /// <summary>
        /// Gets backup storage statistics.
        /// </summary>
        public CatalogStatistics GetStatistics()
        {
            var entries = _entries.Values.ToList();
            return new CatalogStatistics
            {
                TotalBackups = entries.Count,
                TotalOriginalSize = entries.Sum(e => e.OriginalSize),
                TotalStoredSize = entries.Sum(e => e.StoredSize),
                TotalFiles = entries.Sum(e => e.FileCount),
                OldestBackup = entries.Min(e => e.CreatedAt),
                NewestBackup = entries.Max(e => e.CreatedAt),
                BackupsByCategory = entries.GroupBy(e => e.Category).ToDictionary(g => g.Key, g => g.Count()),
                BackupsByStrategy = entries.GroupBy(e => e.StrategyId).ToDictionary(g => g.Key, g => g.Count()),
                EncryptedCount = entries.Count(e => e.IsEncrypted),
                CompressedCount = entries.Count(e => e.IsCompressed),
                ValidatedCount = entries.Count(e => e.IsValid == true),
                ExpiredCount = entries.Count(e => e.ExpiresAt.HasValue && e.ExpiresAt.Value <= DateTimeOffset.UtcNow)
            };
        }

        /// <summary>
        /// Updates the validation status for a backup.
        /// </summary>
        /// <param name="backupId">The backup ID.</param>
        /// <param name="isValid">Whether the backup passed validation.</param>
        public void UpdateValidationStatus(string backupId, bool isValid)
        {
            if (_entries.TryGetValue(backupId, out var entry))
            {
                _entries[backupId] = entry with
                {
                    LastValidatedAt = DateTimeOffset.UtcNow,
                    IsValid = isValid
                };
            }
        }

        /// <summary>
        /// Gets all catalog entries.
        /// </summary>
        public IEnumerable<BackupCatalogEntry> GetAll()
        {
            return _entries.Values.OrderByDescending(e => e.CreatedAt);
        }
    }

    /// <summary>
    /// Statistics about the backup catalog.
    /// </summary>
    public sealed class CatalogStatistics
    {
        /// <summary>Total number of backups.</summary>
        public int TotalBackups { get; init; }

        /// <summary>Total original data size across all backups.</summary>
        public long TotalOriginalSize { get; init; }

        /// <summary>Total stored size across all backups.</summary>
        public long TotalStoredSize { get; init; }

        /// <summary>Total file count across all backups.</summary>
        public long TotalFiles { get; init; }

        /// <summary>Oldest backup creation time.</summary>
        public DateTimeOffset? OldestBackup { get; init; }

        /// <summary>Newest backup creation time.</summary>
        public DateTimeOffset? NewestBackup { get; init; }

        /// <summary>Backup count by category.</summary>
        public Dictionary<DataProtectionCategory, int> BackupsByCategory { get; init; } = new();

        /// <summary>Backup count by strategy.</summary>
        public Dictionary<string, int> BackupsByStrategy { get; init; } = new();

        /// <summary>Number of encrypted backups.</summary>
        public int EncryptedCount { get; init; }

        /// <summary>Number of compressed backups.</summary>
        public int CompressedCount { get; init; }

        /// <summary>Number of validated backups.</summary>
        public int ValidatedCount { get; init; }

        /// <summary>Number of expired backups.</summary>
        public int ExpiredCount { get; init; }

        /// <summary>Overall deduplication ratio.</summary>
        public double DeduplicationRatio => TotalOriginalSize > 0 ? (double)TotalStoredSize / TotalOriginalSize : 1.0;

        /// <summary>Space savings percentage.</summary>
        public double SpaceSavingsPercent => TotalOriginalSize > 0 ? (1.0 - DeduplicationRatio) * 100 : 0;
    }
}
