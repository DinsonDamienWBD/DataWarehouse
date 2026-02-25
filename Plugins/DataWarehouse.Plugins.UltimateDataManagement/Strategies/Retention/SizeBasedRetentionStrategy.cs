using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Retention;

/// <summary>
/// Size-based retention strategy that purges data when storage exceeds thresholds.
/// Supports quota-based purging with configurable eviction policies.
/// </summary>
/// <remarks>
/// Features:
/// - Configurable storage quotas
/// - Multiple eviction policies (LRU, FIFO, largest-first, smallest-first)
/// - Per-tenant quotas
/// - Warning thresholds
/// - Priority-based eviction protection
/// </remarks>
public sealed class SizeBasedRetentionStrategy : RetentionStrategyBase
{
    private readonly BoundedDictionary<string, TenantQuota> _quotas = new BoundedDictionary<string, TenantQuota>(1000);
    private readonly long _globalQuotaBytes;
    private readonly double _warningThreshold;
    private readonly double _criticalThreshold;
    private readonly EvictionPolicy _evictionPolicy;
    private long _currentGlobalUsage;

    /// <summary>
    /// Initializes with default 100GB quota.
    /// </summary>
    public SizeBasedRetentionStrategy() : this(100L * 1024 * 1024 * 1024) { }

    /// <summary>
    /// Initializes with specified quota and thresholds.
    /// </summary>
    /// <param name="globalQuotaBytes">Global storage quota in bytes.</param>
    /// <param name="warningThreshold">Warning threshold (0-1, default 0.8).</param>
    /// <param name="criticalThreshold">Critical threshold (0-1, default 0.95).</param>
    /// <param name="evictionPolicy">Policy for selecting items to evict.</param>
    public SizeBasedRetentionStrategy(
        long globalQuotaBytes,
        double warningThreshold = 0.8,
        double criticalThreshold = 0.95,
        EvictionPolicy evictionPolicy = EvictionPolicy.LeastRecentlyUsed)
    {
        if (globalQuotaBytes < 1024)
            throw new ArgumentOutOfRangeException(nameof(globalQuotaBytes), "Quota must be at least 1KB");
        if (warningThreshold < 0 || warningThreshold > 1)
            throw new ArgumentOutOfRangeException(nameof(warningThreshold), "Warning threshold must be between 0 and 1");
        if (criticalThreshold < warningThreshold || criticalThreshold > 1)
            throw new ArgumentOutOfRangeException(nameof(criticalThreshold), "Critical threshold must be between warning and 1");

        _globalQuotaBytes = globalQuotaBytes;
        _warningThreshold = warningThreshold;
        _criticalThreshold = criticalThreshold;
        _evictionPolicy = evictionPolicy;
    }

    /// <inheritdoc/>
    public override string StrategyId => "retention.sizebased";

    /// <inheritdoc/>
    public override string DisplayName => "Size-Based Retention";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 50_000,
        TypicalLatencyMs = 1.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        $"Size-based retention with {FormatBytes(_globalQuotaBytes)} quota. " +
        $"Evicts data using {_evictionPolicy} policy when usage exceeds {_criticalThreshold * 100}%. " +
        "Supports per-tenant quotas and priority-based protection.";

    /// <inheritdoc/>
    public override string[] Tags => ["retention", "quota", "size-based", "eviction", "storage-management"];

    /// <summary>
    /// Gets the global quota in bytes.
    /// </summary>
    public long GlobalQuotaBytes => _globalQuotaBytes;

    /// <summary>
    /// Gets the current global usage in bytes.
    /// </summary>
    public long CurrentUsageBytes => Interlocked.Read(ref _currentGlobalUsage);

    /// <summary>
    /// Gets the current usage ratio (0-1).
    /// </summary>
    public double UsageRatio => (double)CurrentUsageBytes / _globalQuotaBytes;

    /// <summary>
    /// Sets a quota for a specific tenant.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="quotaBytes">Quota in bytes.</param>
    public void SetTenantQuota(string tenantId, long quotaBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        _quotas[tenantId] = new TenantQuota
        {
            TenantId = tenantId,
            QuotaBytes = quotaBytes,
            UsedBytes = 0
        };
    }

    /// <summary>
    /// Gets quota information for a tenant.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <returns>Quota info or null.</returns>
    public TenantQuota? GetTenantQuota(string tenantId)
    {
        return _quotas.TryGetValue(tenantId, out var quota) ? quota : null;
    }

    /// <summary>
    /// Records usage for an object.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="tenantId">Optional tenant ID.</param>
    /// <param name="sizeBytes">Size in bytes.</param>
    public void RecordUsage(string objectId, string? tenantId, long sizeBytes)
    {
        Interlocked.Add(ref _currentGlobalUsage, sizeBytes);

        if (!string.IsNullOrEmpty(tenantId) && _quotas.TryGetValue(tenantId, out var quota))
        {
            Interlocked.Add(ref quota.UsedBytes, sizeBytes);
        }
    }

    /// <summary>
    /// Releases usage for a deleted object.
    /// </summary>
    /// <param name="tenantId">Optional tenant ID.</param>
    /// <param name="sizeBytes">Size in bytes.</param>
    public void ReleaseUsage(string? tenantId, long sizeBytes)
    {
        Interlocked.Add(ref _currentGlobalUsage, -sizeBytes);

        if (!string.IsNullOrEmpty(tenantId) && _quotas.TryGetValue(tenantId, out var quota))
        {
            Interlocked.Add(ref quota.UsedBytes, -sizeBytes);
        }
    }

    /// <inheritdoc/>
    protected override Task<RetentionDecision> EvaluateCoreAsync(DataObject data, CancellationToken ct)
    {
        var currentUsage = Interlocked.Read(ref _currentGlobalUsage);
        var usageRatio = (double)currentUsage / _globalQuotaBytes;

        // Check tenant quota if applicable
        if (!string.IsNullOrEmpty(data.TenantId) && _quotas.TryGetValue(data.TenantId, out var tenantQuota))
        {
            var tenantUsageRatio = (double)tenantQuota.UsedBytes / tenantQuota.QuotaBytes;

            if (tenantUsageRatio >= 1.0)
            {
                // Tenant over quota - evaluate for deletion
                if (ShouldEvict(data, _evictionPolicy))
                {
                    return Task.FromResult(RetentionDecision.Delete(
                        $"Tenant '{data.TenantId}' over quota ({FormatBytes(tenantQuota.UsedBytes)}/{FormatBytes(tenantQuota.QuotaBytes)})"));
                }
            }
        }

        // Check if below warning threshold
        if (usageRatio < _warningThreshold)
        {
            return Task.FromResult(RetentionDecision.Retain(
                $"Storage within limits ({usageRatio * 100:F1}% of quota)",
                DateTime.UtcNow.AddHours(1)));
        }

        // Between warning and critical - check eviction policy
        if (usageRatio < _criticalThreshold)
        {
            if (IsLowPriority(data) && ShouldEvict(data, _evictionPolicy))
            {
                return Task.FromResult(RetentionDecision.Archive(
                    $"Storage at warning level ({usageRatio * 100:F1}%) - archiving low-priority data"));
            }

            return Task.FromResult(RetentionDecision.Retain(
                $"Storage at warning level ({usageRatio * 100:F1}%) - keeping for now",
                DateTime.UtcNow.AddMinutes(30)));
        }

        // At or above critical threshold - must evict
        if (ShouldEvict(data, _evictionPolicy))
        {
            return Task.FromResult(RetentionDecision.Delete(
                $"Storage at critical level ({usageRatio * 100:F1}%) - evicting based on {_evictionPolicy} policy"));
        }

        // Protected from eviction
        return Task.FromResult(RetentionDecision.Retain(
            "Protected from eviction by priority",
            DateTime.UtcNow.AddMinutes(5)));
    }

    /// <inheritdoc/>
    protected override async Task<int> ApplyRetentionCoreAsync(RetentionScope scope, CancellationToken ct)
    {
        var currentUsage = Interlocked.Read(ref _currentGlobalUsage);
        var usageRatio = (double)currentUsage / _globalQuotaBytes;

        // Only evict if above warning threshold
        if (usageRatio < _warningThreshold)
        {
            return 0;
        }

        // Calculate how much to free
        var targetUsage = (long)(_globalQuotaBytes * _warningThreshold * 0.9); // 90% of warning
        var bytesToFree = currentUsage - targetUsage;

        if (bytesToFree <= 0)
        {
            return 0;
        }

        // Get candidates for eviction
        var candidates = GetEvictionCandidates(scope, bytesToFree);
        var affected = 0;
        var freedBytes = 0L;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (freedBytes >= bytesToFree)
                break;

            if (!scope.DryRun)
            {
                if (TrackedObjects.TryRemove(candidate.ObjectId, out _))
                {
                    ReleaseUsage(candidate.TenantId, candidate.Size);
                    freedBytes += candidate.Size;
                    affected++;
                }
            }
            else
            {
                freedBytes += candidate.Size;
                affected++;
            }
        }

        return affected;
    }

    private IEnumerable<DataObject> GetEvictionCandidates(RetentionScope scope, long bytesNeeded)
    {
        var candidates = GetObjectsInScope(scope)
            .Where(o => !IsProtected(o))
            .ToList();

        // Sort based on eviction policy
        return _evictionPolicy switch
        {
            EvictionPolicy.LeastRecentlyUsed => candidates
                .OrderBy(o => o.LastAccessedAt ?? o.CreatedAt),

            EvictionPolicy.FirstInFirstOut => candidates
                .OrderBy(o => o.CreatedAt),

            EvictionPolicy.LargestFirst => candidates
                .OrderByDescending(o => o.Size),

            EvictionPolicy.SmallestFirst => candidates
                .OrderBy(o => o.Size),

            EvictionPolicy.OldestFirst => candidates
                .OrderBy(o => o.LastModifiedAt ?? o.CreatedAt),

            _ => candidates.OrderBy(o => o.CreatedAt)
        };
    }

    private bool ShouldEvict(DataObject data, EvictionPolicy policy)
    {
        if (IsProtected(data))
            return false;

        // Get all candidates
        var candidates = TrackedObjects.Values.Where(o => !IsProtected(o)).ToList();

        if (candidates.Count == 0)
            return false;

        // Check if this object is in the eviction set
        var evictionThreshold = candidates.Count / 4; // Bottom 25%

        var rank = policy switch
        {
            EvictionPolicy.LeastRecentlyUsed =>
                candidates.Count(c => (c.LastAccessedAt ?? c.CreatedAt) < (data.LastAccessedAt ?? data.CreatedAt)),

            EvictionPolicy.FirstInFirstOut =>
                candidates.Count(c => c.CreatedAt < data.CreatedAt),

            EvictionPolicy.LargestFirst =>
                candidates.Count(c => c.Size > data.Size),

            EvictionPolicy.SmallestFirst =>
                candidates.Count(c => c.Size < data.Size),

            EvictionPolicy.OldestFirst =>
                candidates.Count(c => (c.LastModifiedAt ?? c.CreatedAt) < (data.LastModifiedAt ?? data.CreatedAt)),

            _ => 0
        };

        return rank < evictionThreshold;
    }

    private static bool IsLowPriority(DataObject data)
    {
        if (data.Metadata?.TryGetValue("priority", out var priority) == true)
        {
            var priorityStr = priority?.ToString()?.ToLowerInvariant();
            return priorityStr == "low" || priorityStr == "0";
        }

        // Consider old, unaccessed data as low priority
        var lastAccess = data.LastAccessedAt ?? data.CreatedAt;
        return (DateTime.UtcNow - lastAccess).TotalDays > 90;
    }

    private static bool IsProtected(DataObject data)
    {
        if (data.Metadata?.TryGetValue("protected", out var protectedValue) == true)
        {
            return protectedValue is bool b && b;
        }

        if (data.Metadata?.TryGetValue("priority", out var priority) == true)
        {
            var priorityStr = priority?.ToString()?.ToLowerInvariant();
            return priorityStr == "high" || priorityStr == "critical";
        }

        if (data.Tags?.Contains("protected") == true || data.Tags?.Contains("important") == true)
        {
            return true;
        }

        return false;
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        var order = 0;
        double size = bytes;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {sizes[order]}";
    }
}

/// <summary>
/// Eviction policy for size-based retention.
/// </summary>
public enum EvictionPolicy
{
    /// <summary>
    /// Evict least recently used items first (LRU).
    /// </summary>
    LeastRecentlyUsed,

    /// <summary>
    /// Evict oldest items first (FIFO).
    /// </summary>
    FirstInFirstOut,

    /// <summary>
    /// Evict largest items first.
    /// </summary>
    LargestFirst,

    /// <summary>
    /// Evict smallest items first.
    /// </summary>
    SmallestFirst,

    /// <summary>
    /// Evict items with oldest modification time first.
    /// </summary>
    OldestFirst
}

/// <summary>
/// Quota information for a tenant.
/// </summary>
public sealed class TenantQuota
{
    /// <summary>
    /// Tenant identifier.
    /// </summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// Quota in bytes.
    /// </summary>
    public required long QuotaBytes { get; init; }

    /// <summary>
    /// Current usage in bytes.
    /// </summary>
    public long UsedBytes;

    /// <summary>
    /// Usage ratio (0-1).
    /// </summary>
    public double UsageRatio => (double)UsedBytes / QuotaBytes;

    /// <summary>
    /// Remaining bytes.
    /// </summary>
    public long RemainingBytes => Math.Max(0, QuotaBytes - UsedBytes);
}
