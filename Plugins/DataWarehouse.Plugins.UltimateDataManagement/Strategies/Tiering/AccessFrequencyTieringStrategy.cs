using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Tiering;

/// <summary>
/// Access frequency-based tiering strategy that categorizes data as hot, warm, or cold
/// based on how frequently it is accessed.
/// </summary>
/// <remarks>
/// Features:
/// - Track access frequency per object
/// - Configurable thresholds (hot: >10/day, warm: >1/week, cold: less than 1/month)
/// - Automatic tier transitions based on access patterns
/// - Hysteresis to prevent tier thrashing
/// - Promotion and demotion cooldown periods
/// </remarks>
public sealed class AccessFrequencyTieringStrategy : TieringStrategyBase
{
    private readonly BoundedDictionary<string, AccessTracking> _accessTracking = new BoundedDictionary<string, AccessTracking>(1000);
    private readonly BoundedDictionary<string, TierTransition> _recentTransitions = new BoundedDictionary<string, TierTransition>(1000);
    private TierThresholds _thresholds = TierThresholds.Default;
    private readonly TimeSpan _cooldownPeriod = TimeSpan.FromHours(24);
    private readonly double _hysteresisMultiplier = 1.2;

    /// <summary>
    /// Tracks access patterns for an object.
    /// </summary>
    private sealed class AccessTracking
    {
        public required string ObjectId { get; init; }
        public int AccessesLast24Hours { get; set; }
        public int AccessesLast7Days { get; set; }
        public int AccessesLast30Days { get; set; }
        public DateTime LastAccess { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Records a tier transition for cooldown tracking.
    /// </summary>
    private sealed class TierTransition
    {
        public required string ObjectId { get; init; }
        public StorageTier FromTier { get; init; }
        public StorageTier ToTier { get; init; }
        public DateTime TransitionedAt { get; init; }
    }

    /// <summary>
    /// Configurable thresholds for tier assignment.
    /// </summary>
    public sealed class TierThresholds
    {
        /// <summary>
        /// Minimum daily accesses for hot tier.
        /// </summary>
        public int HotDailyAccessMin { get; init; } = 10;

        /// <summary>
        /// Minimum weekly accesses for warm tier.
        /// </summary>
        public int WarmWeeklyAccessMin { get; init; } = 7;

        /// <summary>
        /// Minimum monthly accesses for cold tier (below this = archive).
        /// </summary>
        public int ColdMonthlyAccessMin { get; init; } = 1;

        /// <summary>
        /// Days without access before considering glacier tier.
        /// </summary>
        public int GlacierInactiveDays { get; init; } = 365;

        /// <summary>
        /// Gets the default thresholds.
        /// </summary>
        public static TierThresholds Default => new()
        {
            HotDailyAccessMin = 10,
            WarmWeeklyAccessMin = 7,
            ColdMonthlyAccessMin = 1,
            GlacierInactiveDays = 365
        };

        /// <summary>
        /// Gets aggressive thresholds for faster tiering.
        /// </summary>
        public static TierThresholds Aggressive => new()
        {
            HotDailyAccessMin = 5,
            WarmWeeklyAccessMin = 3,
            ColdMonthlyAccessMin = 1,
            GlacierInactiveDays = 180
        };

        /// <summary>
        /// Gets conservative thresholds for slower tiering.
        /// </summary>
        public static TierThresholds Conservative => new()
        {
            HotDailyAccessMin = 20,
            WarmWeeklyAccessMin = 14,
            ColdMonthlyAccessMin = 4,
            GlacierInactiveDays = 730
        };
    }

    /// <inheritdoc/>
    public override string StrategyId => "tiering.access-frequency";

    /// <inheritdoc/>
    public override string DisplayName => "Access Frequency Tiering";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 100000,
        TypicalLatencyMs = 0.2
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Access frequency-based tiering that categorizes data into hot, warm, cold, " +
        "archive, and glacier tiers based on access patterns. Features configurable " +
        "thresholds, automatic transitions, hysteresis, and cooldown periods.";

    /// <inheritdoc/>
    public override string[] Tags => ["tiering", "access", "frequency", "hot", "warm", "cold"];

    /// <summary>
    /// Gets or sets the tier thresholds.
    /// </summary>
    public TierThresholds Thresholds
    {
        get => _thresholds;
        set => _thresholds = value ?? TierThresholds.Default;
    }

    /// <summary>
    /// Records an access event for the specified object.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <param name="timestamp">Optional timestamp of the access.</param>
    public void RecordAccess(string objectId, DateTime? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        var accessTime = timestamp ?? DateTime.UtcNow;
        var tracking = _accessTracking.GetOrAdd(objectId, id => new AccessTracking
        {
            ObjectId = id,
            LastUpdated = DateTime.UtcNow
        });

        lock (tracking)
        {
            var now = DateTime.UtcNow;

            // Age out old accesses
            if ((now - tracking.LastUpdated).TotalHours >= 1)
            {
                AgeOutAccesses(tracking, now);
            }

            // Record new access
            tracking.AccessesLast24Hours++;
            tracking.AccessesLast7Days++;
            tracking.AccessesLast30Days++;
            tracking.LastAccess = accessTime;
            tracking.LastUpdated = now;
        }
    }

    /// <summary>
    /// Gets the access statistics for an object.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <returns>Access counts for different time windows.</returns>
    public (int Last24Hours, int Last7Days, int Last30Days, DateTime? LastAccess) GetAccessStats(string objectId)
    {
        if (_accessTracking.TryGetValue(objectId, out var tracking))
        {
            lock (tracking)
            {
                AgeOutAccesses(tracking, DateTime.UtcNow);
                return (tracking.AccessesLast24Hours, tracking.AccessesLast7Days,
                    tracking.AccessesLast30Days, tracking.LastAccess);
            }
        }

        return (0, 0, 0, null);
    }

    /// <summary>
    /// Clears the cooldown for an object, allowing immediate tier changes.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    public void ClearCooldown(string objectId)
    {
        _recentTransitions.TryRemove(objectId, out _);
    }

    /// <inheritdoc/>
    protected override Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct)
    {
        // Get or initialize access tracking
        var tracking = _accessTracking.GetOrAdd(data.ObjectId, id => new AccessTracking
        {
            ObjectId = id,
            AccessesLast24Hours = data.AccessesLast24Hours,
            AccessesLast7Days = data.AccessesLast7Days,
            AccessesLast30Days = data.AccessesLast30Days,
            LastAccess = data.LastAccessedAt,
            LastUpdated = DateTime.UtcNow
        });

        // Age out old accesses
        lock (tracking)
        {
            AgeOutAccesses(tracking, DateTime.UtcNow);
        }

        // Check cooldown
        if (_recentTransitions.TryGetValue(data.ObjectId, out var transition))
        {
            if (DateTime.UtcNow - transition.TransitionedAt < _cooldownPeriod)
            {
                return Task.FromResult(NoChange(data,
                    $"In cooldown period until {transition.TransitionedAt.Add(_cooldownPeriod):u}"));
            }
        }

        // Determine recommended tier based on access frequency
        var recommendedTier = DetermineRecommendedTier(tracking, data.TimeSinceLastAccess);

        // Apply hysteresis - require higher threshold to promote, lower to demote
        var effectiveTier = ApplyHysteresis(data.CurrentTier, recommendedTier, tracking);

        if (effectiveTier != data.CurrentTier)
        {
            var reason = BuildReason(tracking, effectiveTier);
            var confidence = CalculateConfidence(tracking);

            // Record the transition for cooldown
            _recentTransitions[data.ObjectId] = new TierTransition
            {
                ObjectId = data.ObjectId,
                FromTier = data.CurrentTier,
                ToTier = effectiveTier,
                TransitionedAt = DateTime.UtcNow
            };

            return Task.FromResult(effectiveTier < data.CurrentTier
                ? Promote(data, effectiveTier, reason, confidence, CalculatePriority(tracking, effectiveTier))
                : Demote(data, effectiveTier, reason, confidence, CalculatePriority(tracking, effectiveTier),
                    EstimateMonthlySavings(data.SizeBytes, data.CurrentTier, effectiveTier)));
        }

        return Task.FromResult(NoChange(data, BuildReason(tracking, data.CurrentTier)));
    }

    private StorageTier DetermineRecommendedTier(AccessTracking tracking, TimeSpan timeSinceLastAccess)
    {
        // Hot: High daily access rate
        if (tracking.AccessesLast24Hours >= _thresholds.HotDailyAccessMin)
        {
            return StorageTier.Hot;
        }

        // Warm: Moderate weekly access rate
        if (tracking.AccessesLast7Days >= _thresholds.WarmWeeklyAccessMin)
        {
            return StorageTier.Warm;
        }

        // Cold: Low monthly access rate
        if (tracking.AccessesLast30Days >= _thresholds.ColdMonthlyAccessMin)
        {
            return StorageTier.Cold;
        }

        // Archive: Very infrequent access
        if (timeSinceLastAccess.TotalDays < _thresholds.GlacierInactiveDays)
        {
            return StorageTier.Archive;
        }

        // Glacier: No access for extended period
        return StorageTier.Glacier;
    }

    private StorageTier ApplyHysteresis(StorageTier currentTier, StorageTier recommendedTier, AccessTracking tracking)
    {
        // No change needed
        if (recommendedTier == currentTier)
            return currentTier;

        // Promotion (moving to hotter tier) - require higher threshold
        if (recommendedTier < currentTier)
        {
            var effectiveThreshold = recommendedTier switch
            {
                StorageTier.Hot => (int)(_thresholds.HotDailyAccessMin * _hysteresisMultiplier),
                StorageTier.Warm => (int)(_thresholds.WarmWeeklyAccessMin * _hysteresisMultiplier),
                _ => 0
            };

            // Check if access rate exceeds the hysteresis-adjusted threshold
            var meetsThreshold = recommendedTier switch
            {
                StorageTier.Hot => tracking.AccessesLast24Hours >= effectiveThreshold,
                StorageTier.Warm => tracking.AccessesLast7Days >= effectiveThreshold,
                _ => true
            };

            return meetsThreshold ? recommendedTier : currentTier;
        }

        // Demotion (moving to colder tier) - use lower threshold (inverse hysteresis)
        var demotionThreshold = currentTier switch
        {
            StorageTier.Hot => (int)(_thresholds.HotDailyAccessMin / _hysteresisMultiplier),
            StorageTier.Warm => (int)(_thresholds.WarmWeeklyAccessMin / _hysteresisMultiplier),
            StorageTier.Cold => (int)(_thresholds.ColdMonthlyAccessMin / _hysteresisMultiplier),
            _ => 0
        };

        // Check if access rate is below the hysteresis-adjusted threshold
        var belowThreshold = currentTier switch
        {
            StorageTier.Hot => tracking.AccessesLast24Hours < demotionThreshold,
            StorageTier.Warm => tracking.AccessesLast7Days < demotionThreshold,
            StorageTier.Cold => tracking.AccessesLast30Days < demotionThreshold,
            _ => true
        };

        return belowThreshold ? recommendedTier : currentTier;
    }

    private void AgeOutAccesses(AccessTracking tracking, DateTime now)
    {
        var hoursSinceUpdate = (now - tracking.LastUpdated).TotalHours;
        if (hoursSinceUpdate < 1)
            return;

        // Simple decay model - reduce counts based on time elapsed
        var daysSinceUpdate = hoursSinceUpdate / 24;

        if (daysSinceUpdate >= 1)
        {
            // Move 24-hour accesses to 7-day bucket (already counted there, so just reset)
            tracking.AccessesLast24Hours = 0;
        }

        if (daysSinceUpdate >= 7)
        {
            // Decay 7-day count
            tracking.AccessesLast7Days = Math.Max(0, tracking.AccessesLast7Days - (int)(daysSinceUpdate - 6));
        }

        if (daysSinceUpdate >= 30)
        {
            // Decay 30-day count
            tracking.AccessesLast30Days = Math.Max(0, tracking.AccessesLast30Days - (int)(daysSinceUpdate - 29));
        }

        tracking.LastUpdated = now;
    }

    private string BuildReason(AccessTracking tracking, StorageTier tier)
    {
        return tier switch
        {
            StorageTier.Hot => $"High access rate: {tracking.AccessesLast24Hours} accesses/day (threshold: {_thresholds.HotDailyAccessMin})",
            StorageTier.Warm => $"Moderate access rate: {tracking.AccessesLast7Days} accesses/week (threshold: {_thresholds.WarmWeeklyAccessMin})",
            StorageTier.Cold => $"Low access rate: {tracking.AccessesLast30Days} accesses/month (threshold: {_thresholds.ColdMonthlyAccessMin})",
            StorageTier.Archive => $"Very low access rate: {tracking.AccessesLast30Days} accesses/month, last access: {tracking.LastAccess:u}",
            StorageTier.Glacier => $"No significant access for {_thresholds.GlacierInactiveDays}+ days",
            _ => "Unknown access pattern"
        };
    }

    private static double CalculateConfidence(AccessTracking tracking)
    {
        // More data points = higher confidence
        var dataPoints = tracking.AccessesLast30Days;
        return Math.Min(1.0, 0.5 + (dataPoints * 0.05));
    }

    private double CalculatePriority(AccessTracking tracking, StorageTier targetTier)
    {
        // Higher priority for objects with clear access patterns
        var clarity = Math.Min(1.0, Math.Abs(tracking.AccessesLast24Hours - _thresholds.HotDailyAccessMin) / 10.0);
        return 0.3 + clarity * 0.7;
    }
}
