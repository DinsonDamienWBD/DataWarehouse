using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Retention;

/// <summary>
/// GFS-style (Grandfather-Father-Son) cascading retention strategy.
/// Maintains backups at different time granularities with automatic promotion.
/// </summary>
/// <remarks>
/// Features:
/// - Daily, weekly, monthly, yearly retention tiers
/// - Configurable counts per tier
/// - Automatic promotion through tiers
/// - Point-in-time recovery support
/// - Space-efficient versioning
/// </remarks>
public sealed class CascadingRetentionStrategy : RetentionStrategyBase
{
    private readonly BoundedDictionary<string, RetentionTierInfo> _tierAssignments = new BoundedDictionary<string, RetentionTierInfo>(1000);
    private readonly int _dailyCount;
    private readonly int _weeklyCount;
    private readonly int _monthlyCount;
    private readonly int _yearlyCount;

    /// <summary>
    /// Initializes with default GFS settings (7 daily, 4 weekly, 12 monthly, 7 yearly).
    /// </summary>
    public CascadingRetentionStrategy() : this(7, 4, 12, 7) { }

    /// <summary>
    /// Initializes with specified retention counts per tier.
    /// </summary>
    /// <param name="dailyCount">Number of daily versions to keep.</param>
    /// <param name="weeklyCount">Number of weekly versions to keep.</param>
    /// <param name="monthlyCount">Number of monthly versions to keep.</param>
    /// <param name="yearlyCount">Number of yearly versions to keep.</param>
    public CascadingRetentionStrategy(int dailyCount, int weeklyCount, int monthlyCount, int yearlyCount)
    {
        if (dailyCount < 0)
            throw new ArgumentOutOfRangeException(nameof(dailyCount), "Daily count cannot be negative");
        if (weeklyCount < 0)
            throw new ArgumentOutOfRangeException(nameof(weeklyCount), "Weekly count cannot be negative");
        if (monthlyCount < 0)
            throw new ArgumentOutOfRangeException(nameof(monthlyCount), "Monthly count cannot be negative");
        if (yearlyCount < 0)
            throw new ArgumentOutOfRangeException(nameof(yearlyCount), "Yearly count cannot be negative");

        _dailyCount = dailyCount;
        _weeklyCount = weeklyCount;
        _monthlyCount = monthlyCount;
        _yearlyCount = yearlyCount;
    }

    /// <inheritdoc/>
    public override string StrategyId => "retention.cascading";

    /// <inheritdoc/>
    public override string DisplayName => "Cascading Retention (GFS)";

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
        $"Grandfather-Father-Son (GFS) cascading retention: {_dailyCount} daily, {_weeklyCount} weekly, " +
        $"{_monthlyCount} monthly, {_yearlyCount} yearly. Provides long-term retention with graduated granularity. " +
        "Ideal for backup systems requiring point-in-time recovery at various time horizons.";

    /// <inheritdoc/>
    public override string[] Tags => ["retention", "gfs", "cascading", "backup", "grandfather-father-son", "tiered"];

    /// <summary>
    /// Gets the configured tier counts.
    /// </summary>
    public (int Daily, int Weekly, int Monthly, int Yearly) TierCounts =>
        (_dailyCount, _weeklyCount, _monthlyCount, _yearlyCount);

    /// <summary>
    /// Assigns an object to a retention tier.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="tier">Retention tier.</param>
    /// <param name="tierPosition">Position within the tier (1 = most recent).</param>
    /// <param name="backupTime">When this backup was created.</param>
    public void AssignTier(string objectId, RetentionTier tier, int tierPosition, DateTime backupTime)
    {
        _tierAssignments[objectId] = new RetentionTierInfo
        {
            ObjectId = objectId,
            Tier = tier,
            TierPosition = tierPosition,
            BackupTime = backupTime,
            AssignedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Gets the tier assignment for an object.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <returns>Tier info or null.</returns>
    public RetentionTierInfo? GetTierAssignment(string objectId)
    {
        return _tierAssignments.TryGetValue(objectId, out var info) ? info : null;
    }

    /// <summary>
    /// Promotes an object to a higher tier.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="newTier">New tier to promote to.</param>
    /// <returns>True if promoted.</returns>
    public bool PromoteToTier(string objectId, RetentionTier newTier)
    {
        if (!_tierAssignments.TryGetValue(objectId, out var current))
            return false;

        if (newTier <= current.Tier)
            return false;

        _tierAssignments[objectId] = current with
        {
            Tier = newTier,
            TierPosition = GetNextPositionForTier(newTier),
            AssignedAt = DateTime.UtcNow
        };

        return true;
    }

    /// <summary>
    /// Gets all objects in a specific tier.
    /// </summary>
    /// <param name="tier">Retention tier.</param>
    /// <returns>Objects in the tier ordered by position.</returns>
    public IReadOnlyList<RetentionTierInfo> GetObjectsInTier(RetentionTier tier)
    {
        return _tierAssignments.Values
            .Where(t => t.Tier == tier)
            .OrderBy(t => t.TierPosition)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc/>
    protected override Task<RetentionDecision> EvaluateCoreAsync(DataObject data, CancellationToken ct)
    {
        // Check if object has a tier assignment
        if (!_tierAssignments.TryGetValue(data.ObjectId, out var tierInfo))
        {
            // Auto-assign based on age
            tierInfo = AutoAssignTier(data);
        }

        var maxForTier = GetMaxForTier(tierInfo.Tier);

        // Check if within tier limits
        if (tierInfo.TierPosition <= maxForTier)
        {
            var promotionEligibility = CheckPromotionEligibility(tierInfo);

            if (promotionEligibility.HasValue)
            {
                return Task.FromResult(new RetentionDecision
                {
                    Action = RetentionAction.Retain,
                    Reason = $"In {tierInfo.Tier} tier (position {tierInfo.TierPosition}/{maxForTier}), eligible for {promotionEligibility.Value} promotion",
                    NextEvaluationDate = GetNextPromotionDate(tierInfo),
                    Metadata = new Dictionary<string, object>
                    {
                        ["Tier"] = tierInfo.Tier.ToString(),
                        ["Position"] = tierInfo.TierPosition,
                        ["EligibleForPromotion"] = promotionEligibility.Value.ToString()
                    }
                });
            }

            return Task.FromResult(RetentionDecision.Retain(
                $"In {tierInfo.Tier} tier (position {tierInfo.TierPosition}/{maxForTier})",
                GetNextEvaluationForTier(tierInfo.Tier)));
        }

        // Exceeds tier limit - check if can be promoted
        var nextTier = GetNextTier(tierInfo.Tier);

        if (nextTier.HasValue && ShouldPromote(tierInfo, nextTier.Value))
        {
            PromoteToTier(data.ObjectId, nextTier.Value);
            return Task.FromResult(RetentionDecision.Retain(
                $"Promoted from {tierInfo.Tier} to {nextTier.Value}",
                GetNextEvaluationForTier(nextTier.Value)));
        }

        // Cannot be promoted and exceeds limit - delete
        return Task.FromResult(RetentionDecision.Delete(
            $"Exceeds {tierInfo.Tier} tier limit ({tierInfo.TierPosition}/{maxForTier}) and not eligible for promotion"));
    }

    /// <inheritdoc/>
    protected override async Task<int> ApplyRetentionCoreAsync(RetentionScope scope, CancellationToken ct)
    {
        var affected = 0;

        // Process each tier
        foreach (RetentionTier tier in Enum.GetValues<RetentionTier>())
        {
            ct.ThrowIfCancellationRequested();

            var maxForTier = GetMaxForTier(tier);
            var objectsInTier = GetObjectsInTier(tier).ToList();

            // Process objects beyond the limit
            for (int i = maxForTier; i < objectsInTier.Count; i++)
            {
                var obj = objectsInTier[i];
                var nextTier = GetNextTier(tier);

                if (nextTier.HasValue && ShouldPromote(obj, nextTier.Value))
                {
                    if (!scope.DryRun)
                    {
                        PromoteToTier(obj.ObjectId, nextTier.Value);
                    }
                    affected++;
                }
                else
                {
                    if (!scope.DryRun)
                    {
                        _tierAssignments.TryRemove(obj.ObjectId, out _);
                        TrackedObjects.TryRemove(obj.ObjectId, out _);
                    }
                    affected++;
                }
            }

            // Renumber positions
            if (!scope.DryRun)
            {
                RenumberTierPositions(tier);
            }
        }

        return affected;
    }

    private RetentionTierInfo AutoAssignTier(DataObject data)
    {
        var age = data.Age;
        RetentionTier tier;
        int position;

        if (age.TotalDays < 7)
        {
            tier = RetentionTier.Daily;
            position = (int)age.TotalDays + 1;
        }
        else if (age.TotalDays < 28)
        {
            tier = RetentionTier.Weekly;
            position = (int)(age.TotalDays / 7) + 1;
        }
        else if (age.TotalDays < 365)
        {
            tier = RetentionTier.Monthly;
            position = (int)(age.TotalDays / 30) + 1;
        }
        else
        {
            tier = RetentionTier.Yearly;
            position = (int)(age.TotalDays / 365) + 1;
        }

        var info = new RetentionTierInfo
        {
            ObjectId = data.ObjectId,
            Tier = tier,
            TierPosition = position,
            BackupTime = data.CreatedAt,
            AssignedAt = DateTime.UtcNow
        };

        _tierAssignments[data.ObjectId] = info;
        return info;
    }

    private int GetMaxForTier(RetentionTier tier) => tier switch
    {
        RetentionTier.Daily => _dailyCount,
        RetentionTier.Weekly => _weeklyCount,
        RetentionTier.Monthly => _monthlyCount,
        RetentionTier.Yearly => _yearlyCount,
        _ => 0
    };

    private static RetentionTier? GetNextTier(RetentionTier tier) => tier switch
    {
        RetentionTier.Daily => RetentionTier.Weekly,
        RetentionTier.Weekly => RetentionTier.Monthly,
        RetentionTier.Monthly => RetentionTier.Yearly,
        RetentionTier.Yearly => null,
        _ => null
    };

    private RetentionTier? CheckPromotionEligibility(RetentionTierInfo info)
    {
        var age = DateTime.UtcNow - info.BackupTime;

        return info.Tier switch
        {
            RetentionTier.Daily when age.TotalDays >= 7 && IsWeekBoundary(info.BackupTime) => RetentionTier.Weekly,
            RetentionTier.Weekly when age.TotalDays >= 28 && IsMonthBoundary(info.BackupTime) => RetentionTier.Monthly,
            RetentionTier.Monthly when age.TotalDays >= 365 && IsYearBoundary(info.BackupTime) => RetentionTier.Yearly,
            _ => null
        };
    }

    private bool ShouldPromote(RetentionTierInfo info, RetentionTier targetTier)
    {
        var objectsInTarget = GetObjectsInTier(targetTier).Count;
        var maxInTarget = GetMaxForTier(targetTier);

        if (objectsInTarget >= maxInTarget)
            return false;

        // Check if this backup represents a boundary
        return targetTier switch
        {
            RetentionTier.Weekly => IsWeekBoundary(info.BackupTime),
            RetentionTier.Monthly => IsMonthBoundary(info.BackupTime),
            RetentionTier.Yearly => IsYearBoundary(info.BackupTime),
            _ => false
        };
    }

    private static bool IsWeekBoundary(DateTime time)
    {
        // Last backup of the week (Sunday)
        return time.DayOfWeek == DayOfWeek.Sunday;
    }

    private static bool IsMonthBoundary(DateTime time)
    {
        // Last day of month
        var lastDayOfMonth = DateTime.DaysInMonth(time.Year, time.Month);
        return time.Day == lastDayOfMonth;
    }

    private static bool IsYearBoundary(DateTime time)
    {
        // December 31st
        return time.Month == 12 && time.Day == 31;
    }

    private static DateTime GetNextEvaluationForTier(RetentionTier tier) => tier switch
    {
        RetentionTier.Daily => DateTime.UtcNow.AddDays(1),
        RetentionTier.Weekly => DateTime.UtcNow.AddDays(7),
        RetentionTier.Monthly => DateTime.UtcNow.AddDays(30),
        RetentionTier.Yearly => DateTime.UtcNow.AddDays(365),
        _ => DateTime.UtcNow.AddDays(1)
    };

    private DateTime? GetNextPromotionDate(RetentionTierInfo info)
    {
        return info.Tier switch
        {
            RetentionTier.Daily => info.BackupTime.AddDays(7),
            RetentionTier.Weekly => info.BackupTime.AddDays(28),
            RetentionTier.Monthly => info.BackupTime.AddDays(365),
            _ => null
        };
    }

    private int GetNextPositionForTier(RetentionTier tier)
    {
        var existing = GetObjectsInTier(tier);
        return existing.Count > 0 ? existing.Max(t => t.TierPosition) + 1 : 1;
    }

    private void RenumberTierPositions(RetentionTier tier)
    {
        var objects = _tierAssignments.Values
            .Where(t => t.Tier == tier)
            .OrderBy(t => t.BackupTime)
            .ToList();

        for (int i = 0; i < objects.Count; i++)
        {
            var obj = objects[i];
            _tierAssignments[obj.ObjectId] = obj with { TierPosition = i + 1 };
        }
    }
}

/// <summary>
/// Retention tiers for GFS strategy.
/// </summary>
public enum RetentionTier
{
    /// <summary>
    /// Daily backup tier (Son).
    /// </summary>
    Daily = 1,

    /// <summary>
    /// Weekly backup tier (Father).
    /// </summary>
    Weekly = 2,

    /// <summary>
    /// Monthly backup tier.
    /// </summary>
    Monthly = 3,

    /// <summary>
    /// Yearly backup tier (Grandfather).
    /// </summary>
    Yearly = 4
}

/// <summary>
/// Information about an object's tier assignment.
/// </summary>
public sealed record RetentionTierInfo
{
    /// <summary>
    /// Object identifier.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Current tier.
    /// </summary>
    public required RetentionTier Tier { get; init; }

    /// <summary>
    /// Position within the tier (1 = most recent).
    /// </summary>
    public required int TierPosition { get; init; }

    /// <summary>
    /// When this backup was created.
    /// </summary>
    public required DateTime BackupTime { get; init; }

    /// <summary>
    /// When this tier was assigned.
    /// </summary>
    public required DateTime AssignedAt { get; init; }
}
