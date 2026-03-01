using DataWarehouse.SDK.Contracts.Storage;
using IStorageStrategy = DataWarehouse.SDK.Contracts.Storage.IStorageStrategy;
using StorageTier = DataWarehouse.SDK.Contracts.Storage.StorageTier;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStorage.Features
{
    /// <summary>
    /// Auto-Tiering Feature (C2) - Automatic tiering between storage backends based on access patterns.
    ///
    /// Features:
    /// - Access frequency tracking per object
    /// - Temperature scoring: Hot (frequent access), Warm (moderate), Cold (rare), Archive (inactive)
    /// - Automatic migration between tiers based on configurable policies
    /// - Policy types: Access count, Age, Size, Content type
    /// - Background tiering worker
    /// - Tiering history and audit trail
    /// - Manual tier override support
    /// - Cost-aware tiering decisions
    /// </summary>
    public sealed class AutoTieringFeature : IDisposable
    {
        private readonly StrategyRegistry<IStorageStrategy> _registry;
        private readonly BoundedDictionary<string, ObjectAccessMetrics> _accessMetrics = new BoundedDictionary<string, ObjectAccessMetrics>(1000);
        private readonly BoundedDictionary<string, ObjectTieringInfo> _tieringInfo = new BoundedDictionary<string, ObjectTieringInfo>(1000);
        private readonly BoundedDictionary<string, TieringPolicy> _policies = new BoundedDictionary<string, TieringPolicy>(1000);
        private readonly Timer _tieringTimer;
        private bool _disposed;

        // Configuration
        private TimeSpan _tieringInterval = TimeSpan.FromHours(1);
        private bool _enableAutoTiering = true;

        // Statistics
        private long _totalTieringOperations;
        private long _totalHotToCold;
        private long _totalColdToHot;
        private long _totalToArchive;
        private long _totalFromArchive;

        /// <summary>
        /// Initializes a new instance of the AutoTieringFeature.
        /// </summary>
        /// <param name="registry">The storage strategy registry.</param>
        public AutoTieringFeature(StrategyRegistry<IStorageStrategy> registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));

            // Initialize default tiering policies
            InitializeDefaultPolicies();

            // Start background tiering worker
            _tieringTimer = new Timer(
                callback: async _ => await RunTieringCycleAsync(),
                state: null,
                dueTime: _tieringInterval,
                period: _tieringInterval);
        }

        /// <summary>
        /// Gets the total number of tiering operations performed.
        /// </summary>
        public long TotalTieringOperations => Interlocked.Read(ref _totalTieringOperations);

        /// <summary>
        /// Gets or sets whether automatic tiering is enabled.
        /// </summary>
        public bool EnableAutoTiering
        {
            get => _enableAutoTiering;
            set => _enableAutoTiering = value;
        }

        /// <summary>
        /// Gets or sets the tiering interval for background worker.
        /// </summary>
        public TimeSpan TieringInterval
        {
            get => _tieringInterval;
            set
            {
                _tieringInterval = value;
                _tieringTimer.Change(value, value);
            }
        }

        /// <summary>
        /// Records an access to an object (for tracking access frequency).
        /// </summary>
        /// <param name="key">The object key.</param>
        /// <param name="accessType">Type of access (read/write).</param>
        public void RecordAccess(string key, AccessType accessType)
        {
            if (string.IsNullOrWhiteSpace(key)) return;

            var metrics = _accessMetrics.GetOrAdd(key, _ => new ObjectAccessMetrics
            {
                Key = key,
                FirstAccess = DateTime.UtcNow,
                LastAccess = DateTime.UtcNow
            });

            var now = DateTime.UtcNow;
            metrics.LastAccess = now;
            Interlocked.Increment(ref metrics.TotalAccesses);

            // Track recent accesses for time-windowed frequency evaluation (24-hour window)
            metrics.RecentAccessTicks.Enqueue(now.Ticks);
            // Prune entries older than 24 hours; limit queue size to avoid unbounded growth
            var cutoff = now.AddHours(-24).Ticks;
            while (metrics.RecentAccessTicks.TryPeek(out var oldest) && oldest < cutoff)
                metrics.RecentAccessTicks.TryDequeue(out _);
            // Safety cap: drop oldest if queue exceeds 10000 entries
            while (metrics.RecentAccessTicks.Count > 10000)
                metrics.RecentAccessTicks.TryDequeue(out _);

            if (accessType == AccessType.Read)
            {
                Interlocked.Increment(ref metrics.ReadCount);
            }
            else if (accessType == AccessType.Write)
            {
                Interlocked.Increment(ref metrics.WriteCount);
            }

            // Update temperature score
            UpdateTemperatureScore(metrics);
        }

        /// <summary>
        /// Gets the current temperature (tier classification) for an object.
        /// </summary>
        /// <param name="key">The object key.</param>
        /// <returns>The object's temperature classification.</returns>
        public ObjectTemperature GetObjectTemperature(string key)
        {
            if (!_accessMetrics.TryGetValue(key, out var metrics))
            {
                return ObjectTemperature.Unknown;
            }

            return metrics.Temperature;
        }

        /// <summary>
        /// Manually triggers tiering for a specific object.
        /// </summary>
        /// <param name="key">The object key.</param>
        /// <param name="sourceBackendId">Source backend ID.</param>
        /// <param name="targetTier">Target tier to move to.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Tiering result.</returns>
        public async Task<TieringResult> TierObjectAsync(
            string key,
            string sourceBackendId,
            StorageTier targetTier,
            CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var sourceBackend = _registry.Get(sourceBackendId);
            if (sourceBackend == null)
            {
                return new TieringResult
                {
                    Key = key,
                    Success = false,
                    ErrorMessage = $"Source backend '{sourceBackendId}' not found"
                };
            }

            // Find target backend in the desired tier
            var targetBackend = _registry.GetByPredicate(s => s.Tier == targetTier).FirstOrDefault();
            if (targetBackend == null)
            {
                return new TieringResult
                {
                    Key = key,
                    Success = false,
                    ErrorMessage = $"No backend found for tier '{targetTier}'"
                };
            }

            try
            {
                // Read from source
                var data = await sourceBackend.RetrieveAsync(key, ct);

                // Get metadata
                var metadata = await sourceBackend.GetMetadataAsync(key, ct);

                // Convert readonly dictionary to mutable
                var metadataDict = metadata.CustomMetadata != null
                    ? new Dictionary<string, string>(metadata.CustomMetadata)
                    : null;

                // Write to target
                await targetBackend.StoreAsync(key, data, metadataDict, ct);

                // Delete from source
                await sourceBackend.DeleteAsync(key, ct);

                // Update tiering info
                var tieringInfo = _tieringInfo.GetOrAdd(key, _ => new ObjectTieringInfo { Key = key });
                tieringInfo.CurrentTier = targetTier;
                tieringInfo.CurrentBackend = targetBackend.StrategyId;
                tieringInfo.LastTieringDate = DateTime.UtcNow;
                tieringInfo.TieringCount++;
                tieringInfo.TieringHistory.Add(new TieringHistoryEntry
                {
                    FromTier = sourceBackend.Tier,
                    ToTier = targetTier,
                    FromBackend = sourceBackendId,
                    ToBackend = targetBackend.StrategyId,
                    Timestamp = DateTime.UtcNow
                });

                Interlocked.Increment(ref _totalTieringOperations);

                // Update statistics
                UpdateTieringStats(sourceBackend.Tier, targetTier);

                return new TieringResult
                {
                    Key = key,
                    Success = true,
                    SourceBackend = sourceBackendId,
                    TargetBackend = targetBackend.StrategyId,
                    SourceTier = sourceBackend.Tier,
                    TargetTier = targetTier
                };
            }
            catch (Exception ex)
            {
                return new TieringResult
                {
                    Key = key,
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// Adds or updates a tiering policy.
        /// </summary>
        /// <param name="policyName">Policy name.</param>
        /// <param name="policy">Policy configuration.</param>
        public void SetTieringPolicy(string policyName, TieringPolicy policy)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
            ArgumentNullException.ThrowIfNull(policy);

            _policies[policyName] = policy;
        }

        /// <summary>
        /// Gets all defined tiering policies.
        /// </summary>
        /// <returns>Dictionary of policy names to policies.</returns>
        public IReadOnlyDictionary<string, TieringPolicy> GetPolicies()
        {
            return _policies.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        /// <summary>
        /// Gets access metrics for an object.
        /// </summary>
        /// <param name="key">The object key.</param>
        /// <returns>Access metrics or null if not found.</returns>
        public ObjectAccessMetrics? GetAccessMetrics(string key)
        {
            return _accessMetrics.TryGetValue(key, out var metrics) ? metrics : null;
        }

        /// <summary>
        /// Gets tiering information for an object.
        /// </summary>
        /// <param name="key">The object key.</param>
        /// <returns>Tiering info or null if not found.</returns>
        public ObjectTieringInfo? GetTieringInfo(string key)
        {
            return _tieringInfo.TryGetValue(key, out var info) ? info : null;
        }

        #region Private Methods

        private void InitializeDefaultPolicies()
        {
            // Hot to Warm: If not accessed in 7 days
            _policies["HotToWarm"] = new TieringPolicy
            {
                Name = "HotToWarm",
                SourceTier = StorageTier.Hot,
                TargetTier = StorageTier.Warm,
                Conditions = new List<TieringCondition>
                {
                    new() { Type = ConditionType.DaysSinceLastAccess, Threshold = 7 }
                }
            };

            // Warm to Cold: If not accessed in 30 days
            _policies["WarmToCold"] = new TieringPolicy
            {
                Name = "WarmToCold",
                SourceTier = StorageTier.Warm,
                TargetTier = StorageTier.Cold,
                Conditions = new List<TieringCondition>
                {
                    new() { Type = ConditionType.DaysSinceLastAccess, Threshold = 30 }
                }
            };

            // Cold to Archive: If not accessed in 90 days
            _policies["ColdToArchive"] = new TieringPolicy
            {
                Name = "ColdToArchive",
                SourceTier = StorageTier.Cold,
                TargetTier = StorageTier.Archive,
                Conditions = new List<TieringCondition>
                {
                    new() { Type = ConditionType.DaysSinceLastAccess, Threshold = 90 }
                }
            };

            // Cold to Hot: If access count > 10 in last 24 hours
            _policies["ColdToHot"] = new TieringPolicy
            {
                Name = "ColdToHot",
                SourceTier = StorageTier.Cold,
                TargetTier = StorageTier.Hot,
                Conditions = new List<TieringCondition>
                {
                    new() { Type = ConditionType.AccessesInPeriod, Threshold = 10 }
                }
            };
        }

        private async Task RunTieringCycleAsync()
        {
            if (!_enableAutoTiering || _disposed)
            {
                return;
            }

            try
            {
                foreach (var policy in _policies.Values.Where(p => p.Enabled))
                {
                    await ApplyPolicyAsync(policy);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AutoTieringFeature] Tiering evaluation failed: {ex.Message}");
            }
        }

        private async Task ApplyPolicyAsync(TieringPolicy policy)
        {
            var candidateObjects = _accessMetrics.Values
                .Where(metrics => ShouldApplyPolicy(metrics, policy))
                .Take(100) // Process in batches
                .ToList();

            foreach (var metrics in candidateObjects)
            {
                if (!_tieringInfo.TryGetValue(metrics.Key, out var tieringInfo))
                {
                    continue;
                }

                if (tieringInfo.CurrentTier == policy.SourceTier)
                {
                    await TierObjectAsync(
                        metrics.Key,
                        tieringInfo.CurrentBackend ?? string.Empty,
                        policy.TargetTier,
                        CancellationToken.None);
                }
            }
        }

        private bool ShouldApplyPolicy(ObjectAccessMetrics metrics, TieringPolicy policy)
        {
            foreach (var condition in policy.Conditions)
            {
                switch (condition.Type)
                {
                    case ConditionType.DaysSinceLastAccess:
                        var daysSinceAccess = (DateTime.UtcNow - metrics.LastAccess).TotalDays;
                        if (daysSinceAccess < condition.Threshold)
                            return false;
                        break;

                    case ConditionType.AccessesInPeriod:
                        // Use time-windowed access count (24-hour rolling window)
                        var recentCount = metrics.RecentAccessTicks.Count;
                        if (recentCount < condition.Threshold)
                            return false;
                        break;

                    case ConditionType.SizeGreaterThan:
                        // Size information is not tracked in access metrics; skip condition
                        // (object size would need to be supplied at RecordAccess time)
                        // Return false to avoid false positives when size is unknown
                        return false;

                    case ConditionType.AgeDays:
                        var ageDays = (DateTime.UtcNow - metrics.FirstAccess).TotalDays;
                        if (ageDays < condition.Threshold)
                            return false;
                        break;
                }
            }

            return true;
        }

        private void UpdateTemperatureScore(ObjectAccessMetrics metrics)
        {
            var daysSinceLastAccess = (DateTime.UtcNow - metrics.LastAccess).TotalDays;
            var totalAccesses = Interlocked.Read(ref metrics.TotalAccesses);

            // Calculate temperature based on access frequency and recency
            if (daysSinceLastAccess < 1 && totalAccesses > 10)
            {
                metrics.Temperature = ObjectTemperature.Hot;
            }
            else if (daysSinceLastAccess < 7 && totalAccesses > 3)
            {
                metrics.Temperature = ObjectTemperature.Warm;
            }
            else if (daysSinceLastAccess < 30)
            {
                metrics.Temperature = ObjectTemperature.Cold;
            }
            else if (daysSinceLastAccess >= 90)
            {
                metrics.Temperature = ObjectTemperature.Archive;
            }
            else
            {
                metrics.Temperature = ObjectTemperature.Cold;
            }
        }

        private void UpdateTieringStats(StorageTier sourceTier, StorageTier targetTier)
        {
            if (sourceTier == StorageTier.Hot && (targetTier == StorageTier.Cold || targetTier == StorageTier.Archive))
            {
                Interlocked.Increment(ref _totalHotToCold);
            }
            else if ((sourceTier == StorageTier.Cold || sourceTier == StorageTier.Archive) && targetTier == StorageTier.Hot)
            {
                Interlocked.Increment(ref _totalColdToHot);
            }

            if (targetTier == StorageTier.Archive)
            {
                Interlocked.Increment(ref _totalToArchive);
            }

            if (sourceTier == StorageTier.Archive)
            {
                Interlocked.Increment(ref _totalFromArchive);
            }
        }

        #endregion

        /// <summary>
        /// Disposes resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _tieringTimer.Dispose();
            _accessMetrics.Clear();
            _tieringInfo.Clear();
        }
    }

    #region Supporting Types

    /// <summary>
    /// Access type enumeration.
    /// </summary>
    public enum AccessType
    {
        /// <summary>Read access.</summary>
        Read,

        /// <summary>Write access.</summary>
        Write
    }

    /// <summary>
    /// Object temperature classification based on access patterns.
    /// </summary>
    public enum ObjectTemperature
    {
        /// <summary>Unknown temperature.</summary>
        Unknown,

        /// <summary>Hot - frequently accessed (sub-second latency needed).</summary>
        Hot,

        /// <summary>Warm - moderate access (seconds latency acceptable).</summary>
        Warm,

        /// <summary>Cold - rarely accessed (minutes latency acceptable).</summary>
        Cold,

        /// <summary>Archive - inactive (hours latency acceptable).</summary>
        Archive
    }

    /// <summary>
    /// Access metrics for an object.
    /// </summary>
    public sealed class ObjectAccessMetrics
    {
        // Backing fields for lock-free atomic operations on timestamps
        private long _lastAccessTicks = DateTime.UtcNow.Ticks;
        private int _temperature = (int)ObjectTemperature.Unknown;

        /// <summary>Object key.</summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>First access timestamp.</summary>
        public DateTime FirstAccess { get; set; }

        /// <summary>Last access timestamp (thread-safe via Interlocked on backing ticks).</summary>
        public DateTime LastAccess
        {
            get => new DateTime(Interlocked.Read(ref _lastAccessTicks), DateTimeKind.Utc);
            set => Interlocked.Exchange(ref _lastAccessTicks, value.Ticks);
        }

        /// <summary>Total number of accesses.</summary>
        public long TotalAccesses;

        /// <summary>Number of read accesses.</summary>
        public long ReadCount;

        /// <summary>Number of write accesses.</summary>
        public long WriteCount;

        /// <summary>Current temperature classification (thread-safe via Interlocked).</summary>
        public ObjectTemperature Temperature
        {
            get => (ObjectTemperature)Interlocked.CompareExchange(ref _temperature, 0, 0);
            set => Interlocked.Exchange(ref _temperature, (int)value);
        }

        /// <summary>Recent access window entries (ticks) for time-windowed frequency checks.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<long> RecentAccessTicks { get; } = new();
    }

    /// <summary>
    /// Tiering information for an object.
    /// </summary>
    public sealed class ObjectTieringInfo
    {
        /// <summary>Object key.</summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>Current storage tier.</summary>
        public StorageTier CurrentTier { get; set; }

        /// <summary>Current backend ID.</summary>
        public string? CurrentBackend { get; set; }

        /// <summary>Last tiering date.</summary>
        public DateTime? LastTieringDate { get; set; }

        /// <summary>Number of times object has been tiered.</summary>
        public int TieringCount { get; set; }

        /// <summary>History of tiering operations.</summary>
        public List<TieringHistoryEntry> TieringHistory { get; } = new();
    }

    /// <summary>
    /// Tiering history entry.
    /// </summary>
    public sealed class TieringHistoryEntry
    {
        /// <summary>Source tier.</summary>
        public StorageTier FromTier { get; init; }

        /// <summary>Target tier.</summary>
        public StorageTier ToTier { get; init; }

        /// <summary>Source backend.</summary>
        public string? FromBackend { get; init; }

        /// <summary>Target backend.</summary>
        public string? ToBackend { get; init; }

        /// <summary>Timestamp of tiering operation.</summary>
        public DateTime Timestamp { get; init; }
    }

    /// <summary>
    /// Tiering policy configuration.
    /// </summary>
    public sealed class TieringPolicy
    {
        /// <summary>Policy name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Source tier to tier from.</summary>
        public StorageTier SourceTier { get; init; }

        /// <summary>Target tier to tier to.</summary>
        public StorageTier TargetTier { get; init; }

        /// <summary>Conditions that must be met to trigger tiering.</summary>
        public List<TieringCondition> Conditions { get; init; } = new();

        /// <summary>Whether this policy is enabled.</summary>
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// Tiering condition.
    /// </summary>
    public sealed class TieringCondition
    {
        /// <summary>Condition type.</summary>
        public ConditionType Type { get; init; }

        /// <summary>Threshold value for the condition.</summary>
        public double Threshold { get; init; }
    }

    /// <summary>
    /// Condition type for tiering policies.
    /// </summary>
    public enum ConditionType
    {
        /// <summary>Days since last access.</summary>
        DaysSinceLastAccess,

        /// <summary>Number of accesses in a period.</summary>
        AccessesInPeriod,

        /// <summary>Object size greater than threshold.</summary>
        SizeGreaterThan,

        /// <summary>Object age in days.</summary>
        AgeDays
    }

    /// <summary>
    /// Result of a tiering operation.
    /// </summary>
    public sealed class TieringResult
    {
        /// <summary>Object key.</summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>Whether the operation succeeded.</summary>
        public bool Success { get; init; }

        /// <summary>Source backend ID.</summary>
        public string? SourceBackend { get; init; }

        /// <summary>Target backend ID.</summary>
        public string? TargetBackend { get; init; }

        /// <summary>Source tier.</summary>
        public StorageTier SourceTier { get; init; }

        /// <summary>Target tier.</summary>
        public StorageTier TargetTier { get; init; }

        /// <summary>Error message if operation failed.</summary>
        public string? ErrorMessage { get; init; }
    }

    #endregion
}
