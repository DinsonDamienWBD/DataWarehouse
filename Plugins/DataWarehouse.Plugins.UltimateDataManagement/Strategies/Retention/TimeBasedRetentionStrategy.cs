using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Retention;

/// <summary>
/// Time-based retention strategy that retains data for configurable periods.
/// Supports days, months, and years with multiple retention tiers.
/// </summary>
/// <remarks>
/// Features:
/// - Configurable retention periods (days, months, years)
/// - Multiple retention tiers based on content type or tags
/// - Grace period before actual deletion
/// - Archival tier before permanent deletion
/// - Extensible through custom policies
/// </remarks>
public sealed class TimeBasedRetentionStrategy : RetentionStrategyBase
{
    private readonly TimeSpan _defaultRetentionPeriod;
    private readonly TimeSpan _gracePeriod;
    private readonly Dictionary<string, TimeSpan> _contentTypeRetention = new();
    private readonly Dictionary<string, TimeSpan> _tagRetention = new();
    private readonly bool _archiveBeforeDelete;
    private readonly TimeSpan? _archiveAfter;

    /// <summary>
    /// Initializes with default 1-year retention and 30-day grace period.
    /// </summary>
    public TimeBasedRetentionStrategy() : this(TimeSpan.FromDays(365), TimeSpan.FromDays(30)) { }

    /// <summary>
    /// Initializes with specified retention and grace periods.
    /// </summary>
    /// <param name="retentionPeriod">Default retention period.</param>
    /// <param name="gracePeriod">Grace period before deletion.</param>
    /// <param name="archiveBeforeDelete">Whether to archive before deleting.</param>
    /// <param name="archiveAfter">When to move to archive (before deletion).</param>
    public TimeBasedRetentionStrategy(
        TimeSpan retentionPeriod,
        TimeSpan gracePeriod,
        bool archiveBeforeDelete = false,
        TimeSpan? archiveAfter = null)
    {
        if (retentionPeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retentionPeriod), "Retention period must be positive");
        if (gracePeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(gracePeriod), "Grace period cannot be negative");

        _defaultRetentionPeriod = retentionPeriod;
        _gracePeriod = gracePeriod;
        _archiveBeforeDelete = archiveBeforeDelete;
        _archiveAfter = archiveAfter;
    }

    /// <inheritdoc/>
    public override string StrategyId => "retention.timebased";

    /// <inheritdoc/>
    public override string DisplayName => "Time-Based Retention";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = true,
        MaxThroughput = 100_000,
        TypicalLatencyMs = 0.1
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        $"Time-based retention policy that keeps data for {_defaultRetentionPeriod.TotalDays} days " +
        $"with a {_gracePeriod.TotalDays}-day grace period. " +
        "Supports content-type and tag-based retention tiers with optional archival.";

    /// <inheritdoc/>
    public override string[] Tags => ["retention", "time-based", "ttl", "expiration", "lifecycle"];

    /// <summary>
    /// Gets the default retention period.
    /// </summary>
    public TimeSpan RetentionPeriod => _defaultRetentionPeriod;

    /// <summary>
    /// Gets the grace period.
    /// </summary>
    public TimeSpan GracePeriod => _gracePeriod;

    /// <summary>
    /// Sets retention period for a specific content type.
    /// </summary>
    /// <param name="contentType">Content type (MIME type).</param>
    /// <param name="period">Retention period.</param>
    public void SetContentTypeRetention(string contentType, TimeSpan period)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        _contentTypeRetention[contentType.ToLowerInvariant()] = period;
    }

    /// <summary>
    /// Sets retention period for objects with a specific tag.
    /// </summary>
    /// <param name="tag">Tag name.</param>
    /// <param name="period">Retention period.</param>
    public void SetTagRetention(string tag, TimeSpan period)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        _tagRetention[tag.ToLowerInvariant()] = period;
    }

    /// <inheritdoc/>
    protected override Task<RetentionDecision> EvaluateCoreAsync(DataObject data, CancellationToken ct)
    {
        // Get applicable retention period
        var retentionPeriod = GetRetentionPeriod(data);
        var expirationDate = data.CreatedAt + retentionPeriod;
        var graceExpirationDate = expirationDate + _gracePeriod;
        var now = DateTime.UtcNow;

        // Check if within retention period
        if (now < expirationDate)
        {
            var nextEval = expirationDate;
            return Task.FromResult(RetentionDecision.Retain(
                $"Within retention period (expires {expirationDate:yyyy-MM-dd})",
                nextEval));
        }

        // Check archival tier
        if (_archiveBeforeDelete && _archiveAfter.HasValue)
        {
            var archiveDate = data.CreatedAt + _archiveAfter.Value;
            if (now >= archiveDate && now < expirationDate)
            {
                return Task.FromResult(RetentionDecision.Archive(
                    $"Past archive date ({archiveDate:yyyy-MM-dd}), moving to cold storage"));
            }
        }

        // Within grace period
        if (now < graceExpirationDate)
        {
            if (_archiveBeforeDelete)
            {
                return Task.FromResult(RetentionDecision.Archive(
                    $"Retention expired, in grace period until {graceExpirationDate:yyyy-MM-dd}"));
            }

            return Task.FromResult(RetentionDecision.Retain(
                $"In grace period (ends {graceExpirationDate:yyyy-MM-dd})",
                graceExpirationDate));
        }

        // Past grace period - eligible for deletion
        return Task.FromResult(RetentionDecision.Delete(
            $"Retention and grace period expired (created {data.CreatedAt:yyyy-MM-dd})"));
    }

    private TimeSpan GetRetentionPeriod(DataObject data)
    {
        // Check tag-based retention (highest priority)
        if (data.Tags != null)
        {
            foreach (var tag in data.Tags)
            {
                if (_tagRetention.TryGetValue(tag.ToLowerInvariant(), out var tagPeriod))
                {
                    return tagPeriod;
                }
            }
        }

        // Check content-type retention
        if (!string.IsNullOrEmpty(data.ContentType))
        {
            if (_contentTypeRetention.TryGetValue(data.ContentType.ToLowerInvariant(), out var ctPeriod))
            {
                return ctPeriod;
            }

            // Try category match (e.g., "image/*")
            var category = data.ContentType.Split('/')[0] + "/*";
            if (_contentTypeRetention.TryGetValue(category, out var catPeriod))
            {
                return catPeriod;
            }
        }

        return _defaultRetentionPeriod;
    }
}
