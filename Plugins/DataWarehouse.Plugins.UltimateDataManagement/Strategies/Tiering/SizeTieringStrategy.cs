namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Tiering;

/// <summary>
/// Size-based tiering strategy that moves large files to colder, more cost-effective storage tiers.
/// </summary>
/// <remarks>
/// Features:
/// - Size thresholds per tier
/// - Large file detection and automatic tiering
/// - Size-based cost optimization
/// - Exception handling for frequently accessed large files
/// - Configurable size bands
/// </remarks>
public sealed class SizeTieringStrategy : TieringStrategyBase
{
    private SizeTierConfig _config = SizeTierConfig.Default;
    private readonly HashSet<string> _excludedContentTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _exclusionLock = new();

    /// <summary>
    /// Configuration for size-based tiering.
    /// </summary>
    public sealed class SizeTierConfig
    {
        /// <summary>
        /// Maximum size for Hot tier (bytes).
        /// </summary>
        public long HotMaxSize { get; init; } = 100 * 1024 * 1024; // 100 MB

        /// <summary>
        /// Maximum size for Warm tier (bytes).
        /// </summary>
        public long WarmMaxSize { get; init; } = 1024L * 1024 * 1024; // 1 GB

        /// <summary>
        /// Maximum size for Cold tier (bytes).
        /// </summary>
        public long ColdMaxSize { get; init; } = 10L * 1024 * 1024 * 1024; // 10 GB

        /// <summary>
        /// Maximum size for Archive tier (bytes). Above this goes to Glacier.
        /// </summary>
        public long ArchiveMaxSize { get; init; } = 100L * 1024 * 1024 * 1024; // 100 GB

        /// <summary>
        /// Minimum access frequency (per week) to override size-based demotion.
        /// </summary>
        public int AccessOverrideThreshold { get; init; } = 5;

        /// <summary>
        /// Whether to consider access patterns when making decisions.
        /// </summary>
        public bool ConsiderAccessPatterns { get; init; } = true;

        /// <summary>
        /// Gets the default configuration.
        /// </summary>
        public static SizeTierConfig Default => new()
        {
            HotMaxSize = 100 * 1024 * 1024,
            WarmMaxSize = 1024L * 1024 * 1024,
            ColdMaxSize = 10L * 1024 * 1024 * 1024,
            ArchiveMaxSize = 100L * 1024 * 1024 * 1024,
            AccessOverrideThreshold = 5,
            ConsiderAccessPatterns = true
        };

        /// <summary>
        /// Gets aggressive configuration for maximum cost savings.
        /// </summary>
        public static SizeTierConfig Aggressive => new()
        {
            HotMaxSize = 10 * 1024 * 1024,
            WarmMaxSize = 100L * 1024 * 1024,
            ColdMaxSize = 1024L * 1024 * 1024,
            ArchiveMaxSize = 10L * 1024 * 1024 * 1024,
            AccessOverrideThreshold = 10,
            ConsiderAccessPatterns = true
        };

        /// <summary>
        /// Gets conservative configuration for performance-sensitive environments.
        /// </summary>
        public static SizeTierConfig Conservative => new()
        {
            HotMaxSize = 1024L * 1024 * 1024,
            WarmMaxSize = 10L * 1024 * 1024 * 1024,
            ColdMaxSize = 100L * 1024 * 1024 * 1024,
            ArchiveMaxSize = 1024L * 1024 * 1024 * 1024,
            AccessOverrideThreshold = 2,
            ConsiderAccessPatterns = true
        };
    }

    /// <inheritdoc/>
    public override string StrategyId => "tiering.size";

    /// <inheritdoc/>
    public override string DisplayName => "Size-Based Tiering";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 100000,
        TypicalLatencyMs = 0.1
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Size-based tiering that moves large files to cost-effective storage tiers. " +
        "Features configurable size thresholds, access pattern overrides, " +
        "and content type exclusions for optimized storage costs.";

    /// <inheritdoc/>
    public override string[] Tags => ["tiering", "size", "cost", "large-files", "optimization"];

    /// <summary>
    /// Gets or sets the tier configuration.
    /// </summary>
    public SizeTierConfig Config
    {
        get => _config;
        set => _config = value ?? SizeTierConfig.Default;
    }

    /// <summary>
    /// Excludes a content type from size-based tiering.
    /// </summary>
    /// <param name="contentType">The content type to exclude (supports wildcards like "video/*").</param>
    public void ExcludeContentType(string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        lock (_exclusionLock)
        {
            _excludedContentTypes.Add(contentType);
        }
    }

    /// <summary>
    /// Removes a content type exclusion.
    /// </summary>
    /// <param name="contentType">The content type to include.</param>
    public void IncludeContentType(string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        lock (_exclusionLock)
        {
            _excludedContentTypes.Remove(contentType);
        }
    }

    /// <summary>
    /// Gets the excluded content types.
    /// </summary>
    public IReadOnlyList<string> GetExcludedContentTypes()
    {
        lock (_exclusionLock)
        {
            return _excludedContentTypes.ToList();
        }
    }

    /// <inheritdoc/>
    protected override Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct)
    {
        // Check content type exclusions
        if (!string.IsNullOrEmpty(data.ContentType))
        {
            lock (_exclusionLock)
            {
                if (IsContentTypeExcluded(data.ContentType))
                {
                    return Task.FromResult(NoChange(data,
                        $"Content type '{data.ContentType}' excluded from size-based tiering"));
                }
            }
        }

        // Check access pattern override
        if (_config.ConsiderAccessPatterns && data.AccessesLast7Days >= _config.AccessOverrideThreshold)
        {
            // Frequently accessed - keep in current tier or promote
            if (data.CurrentTier != StorageTier.Hot)
            {
                return Task.FromResult(Promote(data, StorageTier.Hot,
                    $"High access rate ({data.AccessesLast7Days}/week) overrides size-based demotion",
                    0.8, 0.7));
            }

            return Task.FromResult(NoChange(data,
                $"Keeping in Hot tier due to high access rate ({data.AccessesLast7Days}/week)"));
        }

        // Determine recommended tier based on size
        var recommendedTier = DetermineRecommendedTier(data.SizeBytes);

        if (recommendedTier != data.CurrentTier)
        {
            var sizeStr = FormatSize(data.SizeBytes);
            var reason = BuildReason(data.SizeBytes, data.CurrentTier, recommendedTier);
            var confidence = CalculateConfidence(data.SizeBytes, recommendedTier);

            if (recommendedTier > data.CurrentTier)
            {
                // Demotion - large file should be in colder tier
                return Task.FromResult(Demote(data, recommendedTier, reason, confidence,
                    CalculatePriority(data.SizeBytes),
                    EstimateMonthlySavings(data.SizeBytes, data.CurrentTier, recommendedTier)));
            }
            else
            {
                // Promotion - file is smaller than tier minimum
                return Task.FromResult(Promote(data, recommendedTier, reason, confidence * 0.8,
                    CalculatePriority(data.SizeBytes) * 0.5));
            }
        }

        return Task.FromResult(NoChange(data,
            $"Size ({FormatSize(data.SizeBytes)}) appropriate for {data.CurrentTier} tier"));
    }

    private StorageTier DetermineRecommendedTier(long sizeBytes)
    {
        if (sizeBytes <= _config.HotMaxSize)
            return StorageTier.Hot;

        if (sizeBytes <= _config.WarmMaxSize)
            return StorageTier.Warm;

        if (sizeBytes <= _config.ColdMaxSize)
            return StorageTier.Cold;

        if (sizeBytes <= _config.ArchiveMaxSize)
            return StorageTier.Archive;

        return StorageTier.Glacier;
    }

    private string BuildReason(long sizeBytes, StorageTier currentTier, StorageTier recommendedTier)
    {
        var sizeStr = FormatSize(sizeBytes);
        var thresholdStr = FormatSize(GetThresholdForTier(recommendedTier));

        if (recommendedTier > currentTier)
        {
            return $"Object size ({sizeStr}) exceeds {currentTier} tier maximum ({FormatSize(GetThresholdForTier(currentTier))}). " +
                   $"Recommended: {recommendedTier} tier (max: {thresholdStr})";
        }
        else
        {
            return $"Object size ({sizeStr}) is below threshold for current {currentTier} tier. " +
                   $"Recommended: {recommendedTier} tier for better performance";
        }
    }

    private long GetThresholdForTier(StorageTier tier)
    {
        return tier switch
        {
            StorageTier.Hot => _config.HotMaxSize,
            StorageTier.Warm => _config.WarmMaxSize,
            StorageTier.Cold => _config.ColdMaxSize,
            StorageTier.Archive => _config.ArchiveMaxSize,
            StorageTier.Glacier => long.MaxValue,
            _ => _config.HotMaxSize
        };
    }

    private bool IsContentTypeExcluded(string contentType)
    {
        foreach (var excluded in _excludedContentTypes)
        {
            if (excluded.EndsWith("/*"))
            {
                var prefix = excluded[..^2];
                if (contentType.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (contentType.Equals(excluded, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private double CalculateConfidence(long sizeBytes, StorageTier recommendedTier)
    {
        // Higher confidence when size significantly exceeds threshold
        var threshold = GetThresholdForTier(recommendedTier);
        if (threshold == 0 || threshold == long.MaxValue)
            return 0.9;

        var ratio = (double)sizeBytes / threshold;

        // Confidence increases as we move further from threshold
        return Math.Min(1.0, 0.7 + Math.Abs(1.0 - ratio) * 0.2);
    }

    private double CalculatePriority(long sizeBytes)
    {
        // Larger files = higher priority for cost savings
        var sizeMb = sizeBytes / (1024.0 * 1024.0);
        return Math.Min(1.0, sizeMb / 1000.0); // Cap at 1GB
    }

    private static string FormatSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB", "PB"];
        var order = 0;
        var size = (double)bytes;

        while (size >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:F1} {suffixes[order]}";
    }
}
