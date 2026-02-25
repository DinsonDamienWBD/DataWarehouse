namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Tiering;

/// <summary>
/// Age-based tiering strategy that moves data to colder tiers based on how old it is.
/// </summary>
/// <remarks>
/// Features:
/// - Configurable age thresholds per tier
/// - Option to use creation date or last modified date
/// - Age-based policies with exceptions for classifications
/// - Grace periods for new data
/// - Override capability for critical data
/// </remarks>
public sealed class AgeTieringStrategy : TieringStrategyBase
{
    private AgeTierConfig _config = AgeTierConfig.Default;
    private readonly HashSet<string> _excludedClassifications = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excludedObjectIds = new();
    private readonly object _exclusionLock = new();

    /// <summary>
    /// Configuration for age-based tiering.
    /// </summary>
    public sealed class AgeTierConfig
    {
        /// <summary>
        /// Age threshold to move from Hot to Warm.
        /// </summary>
        public TimeSpan HotToWarmAge { get; init; } = TimeSpan.FromDays(30);

        /// <summary>
        /// Age threshold to move from Warm to Cold.
        /// </summary>
        public TimeSpan WarmToColdAge { get; init; } = TimeSpan.FromDays(90);

        /// <summary>
        /// Age threshold to move from Cold to Archive.
        /// </summary>
        public TimeSpan ColdToArchiveAge { get; init; } = TimeSpan.FromDays(365);

        /// <summary>
        /// Age threshold to move from Archive to Glacier.
        /// </summary>
        public TimeSpan ArchiveToGlacierAge { get; init; } = TimeSpan.FromDays(730);

        /// <summary>
        /// Whether to use last modified date instead of creation date.
        /// </summary>
        public bool UseLastModifiedDate { get; init; } = false;

        /// <summary>
        /// Grace period for newly created objects before tiering rules apply.
        /// </summary>
        public TimeSpan GracePeriod { get; init; } = TimeSpan.FromHours(24);

        /// <summary>
        /// Gets the default configuration.
        /// </summary>
        public static AgeTierConfig Default => new()
        {
            HotToWarmAge = TimeSpan.FromDays(30),
            WarmToColdAge = TimeSpan.FromDays(90),
            ColdToArchiveAge = TimeSpan.FromDays(365),
            ArchiveToGlacierAge = TimeSpan.FromDays(730),
            UseLastModifiedDate = false,
            GracePeriod = TimeSpan.FromHours(24)
        };

        /// <summary>
        /// Gets an aggressive configuration for faster tiering.
        /// </summary>
        public static AgeTierConfig Aggressive => new()
        {
            HotToWarmAge = TimeSpan.FromDays(7),
            WarmToColdAge = TimeSpan.FromDays(30),
            ColdToArchiveAge = TimeSpan.FromDays(90),
            ArchiveToGlacierAge = TimeSpan.FromDays(180),
            UseLastModifiedDate = false,
            GracePeriod = TimeSpan.FromHours(1)
        };

        /// <summary>
        /// Gets a conservative configuration for slower tiering.
        /// </summary>
        public static AgeTierConfig Conservative => new()
        {
            HotToWarmAge = TimeSpan.FromDays(90),
            WarmToColdAge = TimeSpan.FromDays(180),
            ColdToArchiveAge = TimeSpan.FromDays(730),
            ArchiveToGlacierAge = TimeSpan.FromDays(1825),
            UseLastModifiedDate = false,
            GracePeriod = TimeSpan.FromDays(7)
        };

        /// <summary>
        /// Gets configuration based on last modified date.
        /// </summary>
        public static AgeTierConfig ModifiedDateBased => new()
        {
            HotToWarmAge = TimeSpan.FromDays(30),
            WarmToColdAge = TimeSpan.FromDays(90),
            ColdToArchiveAge = TimeSpan.FromDays(365),
            ArchiveToGlacierAge = TimeSpan.FromDays(730),
            UseLastModifiedDate = true,
            GracePeriod = TimeSpan.FromHours(24)
        };
    }

    /// <inheritdoc/>
    public override string StrategyId => "tiering.age";

    /// <inheritdoc/>
    public override string DisplayName => "Age-Based Tiering";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = true,
        MaxThroughput = 100000,
        TypicalLatencyMs = 0.1
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Age-based tiering that moves data to colder tiers based on age. " +
        "Supports configurable thresholds, creation vs. modified date options, " +
        "grace periods, and classification-based exclusions.";

    /// <inheritdoc/>
    public override string[] Tags => ["tiering", "age", "lifecycle", "retention", "time-based"];

    /// <summary>
    /// Gets or sets the tier configuration.
    /// </summary>
    public AgeTierConfig Config
    {
        get => _config;
        set => _config = value ?? AgeTierConfig.Default;
    }

    /// <summary>
    /// Excludes a classification from automatic tiering.
    /// </summary>
    /// <param name="classification">The classification to exclude.</param>
    public void ExcludeClassification(string classification)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(classification);

        lock (_exclusionLock)
        {
            _excludedClassifications.Add(classification);
        }
    }

    /// <summary>
    /// Removes a classification exclusion.
    /// </summary>
    /// <param name="classification">The classification to include.</param>
    public void IncludeClassification(string classification)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(classification);

        lock (_exclusionLock)
        {
            _excludedClassifications.Remove(classification);
        }
    }

    /// <summary>
    /// Excludes a specific object from automatic tiering.
    /// </summary>
    /// <param name="objectId">The object identifier to exclude.</param>
    public void ExcludeObject(string objectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        lock (_exclusionLock)
        {
            _excludedObjectIds.Add(objectId);
        }
    }

    /// <summary>
    /// Removes an object exclusion.
    /// </summary>
    /// <param name="objectId">The object identifier to include.</param>
    public void IncludeObject(string objectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        lock (_exclusionLock)
        {
            _excludedObjectIds.Remove(objectId);
        }
    }

    /// <summary>
    /// Gets the current exclusions.
    /// </summary>
    /// <returns>Lists of excluded classifications and object IDs.</returns>
    public (IReadOnlyList<string> Classifications, IReadOnlyList<string> ObjectIds) GetExclusions()
    {
        lock (_exclusionLock)
        {
            return (_excludedClassifications.ToList(), _excludedObjectIds.ToList());
        }
    }

    /// <inheritdoc/>
    protected override Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct)
    {
        // Check exclusions
        lock (_exclusionLock)
        {
            if (_excludedObjectIds.Contains(data.ObjectId))
            {
                return Task.FromResult(NoChange(data, "Object excluded from age-based tiering"));
            }

            if (!string.IsNullOrEmpty(data.Classification) &&
                _excludedClassifications.Contains(data.Classification))
            {
                return Task.FromResult(NoChange(data,
                    $"Classification '{data.Classification}' excluded from age-based tiering"));
            }
        }

        // Calculate effective age
        var effectiveAge = _config.UseLastModifiedDate
            ? data.TimeSinceLastModified
            : data.Age;

        // Check grace period
        if (effectiveAge < _config.GracePeriod)
        {
            return Task.FromResult(NoChange(data,
                $"Within grace period ({_config.GracePeriod.TotalHours:F0}h remaining)"));
        }

        // Determine recommended tier based on age
        var recommendedTier = DetermineRecommendedTier(effectiveAge);

        if (recommendedTier != data.CurrentTier)
        {
            // Only allow demotion (moving to colder tiers) based on age
            // Promotion should be handled by access-based strategies
            if (recommendedTier > data.CurrentTier)
            {
                var reason = BuildDemotionReason(effectiveAge, data.CurrentTier, recommendedTier);
                var confidence = CalculateConfidence(effectiveAge, recommendedTier);

                return Task.FromResult(Demote(data, recommendedTier, reason, confidence,
                    CalculatePriority(effectiveAge, data.SizeBytes),
                    EstimateMonthlySavings(data.SizeBytes, data.CurrentTier, recommendedTier)));
            }

            // Data is older than expected for current tier but in a colder tier
            // This could happen if it was previously demoted
            return Task.FromResult(NoChange(data,
                $"Age ({effectiveAge.TotalDays:F0} days) suggests tier {recommendedTier}, but already in colder tier"));
        }

        return Task.FromResult(NoChange(data,
            $"At correct tier for age ({effectiveAge.TotalDays:F0} days)"));
    }

    private StorageTier DetermineRecommendedTier(TimeSpan age)
    {
        if (age >= _config.ArchiveToGlacierAge)
            return StorageTier.Glacier;

        if (age >= _config.ColdToArchiveAge)
            return StorageTier.Archive;

        if (age >= _config.WarmToColdAge)
            return StorageTier.Cold;

        if (age >= _config.HotToWarmAge)
            return StorageTier.Warm;

        return StorageTier.Hot;
    }

    private string BuildDemotionReason(TimeSpan age, StorageTier currentTier, StorageTier recommendedTier)
    {
        var dateType = _config.UseLastModifiedDate ? "last modified" : "creation";
        var threshold = GetThresholdForTransition(currentTier, recommendedTier);

        return $"Object age ({age.TotalDays:F0} days since {dateType}) exceeds " +
               $"{currentTier} to {recommendedTier} threshold ({threshold.TotalDays:F0} days)";
    }

    private TimeSpan GetThresholdForTransition(StorageTier from, StorageTier to)
    {
        return (from, to) switch
        {
            (StorageTier.Hot, StorageTier.Warm) => _config.HotToWarmAge,
            (StorageTier.Hot, StorageTier.Cold) => _config.WarmToColdAge,
            (StorageTier.Hot, StorageTier.Archive) => _config.ColdToArchiveAge,
            (StorageTier.Hot, StorageTier.Glacier) => _config.ArchiveToGlacierAge,
            (StorageTier.Warm, StorageTier.Cold) => _config.WarmToColdAge,
            (StorageTier.Warm, StorageTier.Archive) => _config.ColdToArchiveAge,
            (StorageTier.Warm, StorageTier.Glacier) => _config.ArchiveToGlacierAge,
            (StorageTier.Cold, StorageTier.Archive) => _config.ColdToArchiveAge,
            (StorageTier.Cold, StorageTier.Glacier) => _config.ArchiveToGlacierAge,
            (StorageTier.Archive, StorageTier.Glacier) => _config.ArchiveToGlacierAge,
            _ => TimeSpan.FromDays(30)
        };
    }

    private double CalculateConfidence(TimeSpan age, StorageTier recommendedTier)
    {
        // Higher confidence when age significantly exceeds threshold
        var threshold = recommendedTier switch
        {
            StorageTier.Warm => _config.HotToWarmAge,
            StorageTier.Cold => _config.WarmToColdAge,
            StorageTier.Archive => _config.ColdToArchiveAge,
            StorageTier.Glacier => _config.ArchiveToGlacierAge,
            _ => TimeSpan.FromDays(30)
        };

        var ratio = age.TotalDays / threshold.TotalDays;
        return Math.Min(1.0, 0.7 + (ratio - 1.0) * 0.15);
    }

    private double CalculatePriority(TimeSpan age, long sizeBytes)
    {
        // Higher priority for older objects and larger objects
        var ageFactor = Math.Min(1.0, age.TotalDays / 365.0);
        var sizeFactor = Math.Min(1.0, sizeBytes / (1024.0 * 1024.0 * 1024.0));

        return ageFactor * 0.6 + sizeFactor * 0.4;
    }
}
