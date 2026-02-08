using System.Collections.Concurrent;
using DataWarehouse.Plugins.UltimateDataProtection.Catalog;

namespace DataWarehouse.Plugins.UltimateDataProtection.Retention
{
    /// <summary>
    /// Retention policy engine supporting GFS policies, compliance requirements, and space-aware pruning.
    /// </summary>
    public sealed class RetentionPolicyEngine
    {
        private readonly ConcurrentDictionary<string, RetentionPolicy> _policies = new();
        private readonly BackupCatalog _catalog;
        private readonly DataProtectionStrategyRegistry _registry;

        /// <summary>
        /// Event raised when backups are marked for deletion.
        /// </summary>
        public event EventHandler<IEnumerable<string>>? BackupsExpired;

        /// <summary>
        /// Creates a new retention policy engine.
        /// </summary>
        /// <param name="catalog">Backup catalog.</param>
        /// <param name="registry">Strategy registry.</param>
        public RetentionPolicyEngine(BackupCatalog catalog, DataProtectionStrategyRegistry registry)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        /// <summary>
        /// Registers a retention policy.
        /// </summary>
        /// <param name="policy">The policy to register.</param>
        public void RegisterPolicy(RetentionPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);
            _policies[policy.Name] = policy;
        }

        /// <summary>
        /// Gets a policy by name.
        /// </summary>
        public RetentionPolicy? GetPolicy(string name)
        {
            _policies.TryGetValue(name, out var policy);
            return policy;
        }

        /// <summary>
        /// Gets all registered policies.
        /// </summary>
        public IEnumerable<RetentionPolicy> GetAllPolicies()
        {
            return _policies.Values;
        }

        /// <summary>
        /// Removes a policy.
        /// </summary>
        public bool RemovePolicy(string name)
        {
            return _policies.TryRemove(name, out _);
        }

        /// <summary>
        /// Applies a retention policy to determine which backups to keep.
        /// </summary>
        /// <param name="policyName">Policy name.</param>
        /// <param name="backups">Backups to evaluate.</param>
        /// <returns>Backups to delete.</returns>
        public IEnumerable<BackupCatalogEntry> ApplyPolicy(string policyName, IEnumerable<BackupCatalogEntry> backups)
        {
            if (!_policies.TryGetValue(policyName, out var policy))
            {
                throw new ArgumentException($"Policy '{policyName}' not found");
            }

            return ApplyPolicy(policy, backups);
        }

        /// <summary>
        /// Applies a retention policy to determine which backups to keep.
        /// </summary>
        /// <param name="policy">The policy.</param>
        /// <param name="backups">Backups to evaluate.</param>
        /// <returns>Backups to delete.</returns>
        public IEnumerable<BackupCatalogEntry> ApplyPolicy(RetentionPolicy policy, IEnumerable<BackupCatalogEntry> backups)
        {
            var allBackups = backups.OrderByDescending(b => b.CreatedAt).ToList();
            var toKeep = new HashSet<string>();

            switch (policy.Type)
            {
                case RetentionType.Simple:
                    ApplySimpleRetention(policy, allBackups, toKeep);
                    break;

                case RetentionType.GFS:
                    ApplyGFSRetention(policy, allBackups, toKeep);
                    break;

                case RetentionType.TimeBased:
                    ApplyTimeBasedRetention(policy, allBackups, toKeep);
                    break;

                case RetentionType.CountBased:
                    ApplyCountBasedRetention(policy, allBackups, toKeep);
                    break;

                case RetentionType.Compliance:
                    ApplyComplianceRetention(policy, allBackups, toKeep);
                    break;
            }

            // Return backups not in the keep set
            return allBackups.Where(b => !toKeep.Contains(b.BackupId));
        }

        /// <summary>
        /// Enforces retention policies and deletes expired backups.
        /// </summary>
        /// <param name="dryRun">If true, don't actually delete.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Enforcement result.</returns>
        public async Task<RetentionEnforcementResult> EnforceAsync(bool dryRun = false, CancellationToken ct = default)
        {
            var result = new RetentionEnforcementResult();
            var toDelete = new List<string>();

            foreach (var policy in _policies.Values)
            {
                if (!policy.IsEnabled) continue;

                var backups = _catalog.Query(new BackupListQuery
                {
                    MaxResults = int.MaxValue
                }).Where(b => MatchesPolicy(b, policy));

                var expired = ApplyPolicy(policy, backups).ToList();
                result.EvaluatedBackups += backups.Count();
                result.ExpiredBackups += expired.Count;

                foreach (var backup in expired)
                {
                    toDelete.Add(backup.BackupId);
                    result.SpaceToReclaim += backup.StoredSize;
                }
            }

            if (!dryRun && toDelete.Count > 0)
            {
                foreach (var backupId in toDelete)
                {
                    ct.ThrowIfCancellationRequested();

                    var backup = _catalog.Get(backupId);
                    if (backup != null)
                    {
                        try
                        {
                            var strategy = _registry.GetStrategy(backup.StrategyId);
                            if (strategy != null)
                            {
                                await strategy.DeleteBackupAsync(backupId, ct);
                            }
                            _catalog.Remove(backupId);
                            result.DeletedBackups++;
                            result.SpaceReclaimed += backup.StoredSize;
                        }
                        catch
                        {
                            result.FailedDeletions++;
                        }
                    }
                }

                BackupsExpired?.Invoke(this, toDelete);
            }

            return result;
        }

        private static void ApplySimpleRetention(RetentionPolicy policy, List<BackupCatalogEntry> backups, HashSet<string> toKeep)
        {
            // Keep N most recent backups
            var count = policy.RetainCount ?? 10;
            foreach (var backup in backups.Take(count))
            {
                toKeep.Add(backup.BackupId);
            }
        }

        private static void ApplyGFSRetention(RetentionPolicy policy, List<BackupCatalogEntry> backups, HashSet<string> toKeep)
        {
            var now = DateTimeOffset.UtcNow;

            // Daily (Son) - keep N daily backups
            var dailyCount = policy.DailyRetention ?? 7;
            var dailyBackups = backups
                .Where(b => b.CreatedAt >= now.AddDays(-dailyCount))
                .GroupBy(b => b.CreatedAt.Date)
                .SelectMany(g => g.OrderByDescending(b => b.CreatedAt).Take(1))
                .Take(dailyCount);

            foreach (var b in dailyBackups) toKeep.Add(b.BackupId);

            // Weekly (Father) - keep N weekly backups
            var weeklyCount = policy.WeeklyRetention ?? 4;
            var weeklyBackups = backups
                .Where(b => b.CreatedAt >= now.AddDays(-weeklyCount * 7))
                .GroupBy(b => GetWeekOfYear(b.CreatedAt))
                .SelectMany(g => g.OrderByDescending(b => b.CreatedAt).Take(1))
                .Take(weeklyCount);

            foreach (var b in weeklyBackups) toKeep.Add(b.BackupId);

            // Monthly (Grandfather) - keep N monthly backups
            var monthlyCount = policy.MonthlyRetention ?? 12;
            var monthlyBackups = backups
                .Where(b => b.CreatedAt >= now.AddMonths(-monthlyCount))
                .GroupBy(b => new { b.CreatedAt.Year, b.CreatedAt.Month })
                .SelectMany(g => g.OrderByDescending(b => b.CreatedAt).Take(1))
                .Take(monthlyCount);

            foreach (var b in monthlyBackups) toKeep.Add(b.BackupId);

            // Yearly - keep N yearly backups
            var yearlyCount = policy.YearlyRetention ?? 7;
            var yearlyBackups = backups
                .GroupBy(b => b.CreatedAt.Year)
                .SelectMany(g => g.OrderByDescending(b => b.CreatedAt).Take(1))
                .Take(yearlyCount);

            foreach (var b in yearlyBackups) toKeep.Add(b.BackupId);
        }

        private static void ApplyTimeBasedRetention(RetentionPolicy policy, List<BackupCatalogEntry> backups, HashSet<string> toKeep)
        {
            var cutoff = DateTimeOffset.UtcNow - (policy.RetainDuration ?? TimeSpan.FromDays(30));
            foreach (var backup in backups.Where(b => b.CreatedAt >= cutoff))
            {
                toKeep.Add(backup.BackupId);
            }
        }

        private static void ApplyCountBasedRetention(RetentionPolicy policy, List<BackupCatalogEntry> backups, HashSet<string> toKeep)
        {
            var count = policy.RetainCount ?? 10;
            foreach (var backup in backups.Take(count))
            {
                toKeep.Add(backup.BackupId);
            }
        }

        private static void ApplyComplianceRetention(RetentionPolicy policy, List<BackupCatalogEntry> backups, HashSet<string> toKeep)
        {
            // Keep all backups within compliance window
            var complianceDays = policy.ComplianceRetentionDays ?? 2555; // 7 years default
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(complianceDays);

            foreach (var backup in backups.Where(b => b.CreatedAt >= cutoff))
            {
                toKeep.Add(backup.BackupId);
            }

            // Also keep any with legal hold
            foreach (var backup in backups.Where(b => b.Tags.ContainsKey("legal_hold")))
            {
                toKeep.Add(backup.BackupId);
            }
        }

        private static bool MatchesPolicy(BackupCatalogEntry backup, RetentionPolicy policy)
        {
            if (policy.StrategyFilter != null && backup.StrategyId != policy.StrategyFilter)
                return false;

            if (policy.CategoryFilter.HasValue && backup.Category != policy.CategoryFilter.Value)
                return false;

            if (policy.TagFilter != null && !policy.TagFilter.All(t =>
                backup.Tags.TryGetValue(t.Key, out var v) && v == t.Value))
                return false;

            return true;
        }

        private static int GetWeekOfYear(DateTimeOffset date)
        {
            var cal = System.Globalization.CultureInfo.InvariantCulture.Calendar;
            return cal.GetWeekOfYear(date.DateTime, System.Globalization.CalendarWeekRule.FirstDay, DayOfWeek.Monday);
        }
    }

    /// <summary>
    /// Retention policy configuration.
    /// </summary>
    public sealed record RetentionPolicy
    {
        /// <summary>Policy name.</summary>
        public required string Name { get; init; }

        /// <summary>Retention type.</summary>
        public required RetentionType Type { get; init; }

        /// <summary>Whether the policy is enabled.</summary>
        public bool IsEnabled { get; init; } = true;

        /// <summary>Number of backups to retain (for count-based).</summary>
        public int? RetainCount { get; init; }

        /// <summary>Duration to retain backups (for time-based).</summary>
        public TimeSpan? RetainDuration { get; init; }

        /// <summary>Daily retention count (for GFS).</summary>
        public int? DailyRetention { get; init; }

        /// <summary>Weekly retention count (for GFS).</summary>
        public int? WeeklyRetention { get; init; }

        /// <summary>Monthly retention count (for GFS).</summary>
        public int? MonthlyRetention { get; init; }

        /// <summary>Yearly retention count (for GFS).</summary>
        public int? YearlyRetention { get; init; }

        /// <summary>Compliance retention in days.</summary>
        public int? ComplianceRetentionDays { get; init; }

        /// <summary>Filter by strategy ID.</summary>
        public string? StrategyFilter { get; init; }

        /// <summary>Filter by category.</summary>
        public DataProtectionCategory? CategoryFilter { get; init; }

        /// <summary>Filter by tags.</summary>
        public IReadOnlyDictionary<string, string>? TagFilter { get; init; }
    }

    /// <summary>
    /// Type of retention policy.
    /// </summary>
    public enum RetentionType
    {
        /// <summary>Keep N most recent backups.</summary>
        Simple,

        /// <summary>Grandfather-Father-Son rotation.</summary>
        GFS,

        /// <summary>Keep backups within a time window.</summary>
        TimeBased,

        /// <summary>Keep a specific count of backups.</summary>
        CountBased,

        /// <summary>Compliance-driven retention.</summary>
        Compliance
    }

    /// <summary>
    /// Result of retention enforcement.
    /// </summary>
    public sealed class RetentionEnforcementResult
    {
        /// <summary>Backups evaluated.</summary>
        public int EvaluatedBackups { get; set; }

        /// <summary>Backups marked as expired.</summary>
        public int ExpiredBackups { get; set; }

        /// <summary>Backups actually deleted.</summary>
        public int DeletedBackups { get; set; }

        /// <summary>Failed deletion attempts.</summary>
        public int FailedDeletions { get; set; }

        /// <summary>Space to reclaim if all expired backups deleted.</summary>
        public long SpaceToReclaim { get; set; }

        /// <summary>Actual space reclaimed.</summary>
        public long SpaceReclaimed { get; set; }
    }
}
