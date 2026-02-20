using System.Text.RegularExpressions;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Tiering;

/// <summary>
/// Policy-based tiering strategy that uses configurable rules for automatic tier assignment.
/// Supports complex rule definitions with priority ordering and conflict resolution.
/// </summary>
/// <remarks>
/// Features:
/// - Define rules (e.g., "if age > 30d then tier = warm")
/// - Rule priority ordering for conflict resolution
/// - Multiple condition types (age, size, access frequency, content type)
/// - Rule chaining and composition
/// - Real-time rule evaluation with caching
/// </remarks>
public sealed class PolicyBasedTieringStrategy : TieringStrategyBase
{
    private readonly BoundedDictionary<string, TieringRule> _rules = new BoundedDictionary<string, TieringRule>(1000);
    private readonly List<TieringRule> _sortedRules = new();
    private readonly object _rulesLock = new();
    private long _rulesEvaluated;
    private long _rulesMatched;

    /// <summary>
    /// Represents a tiering rule.
    /// </summary>
    public sealed class TieringRule
    {
        /// <summary>
        /// Unique identifier for the rule.
        /// </summary>
        public required string RuleId { get; init; }

        /// <summary>
        /// Human-readable name for the rule.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Description of what the rule does.
        /// </summary>
        public string? Description { get; init; }

        /// <summary>
        /// Priority of the rule (higher = evaluated first).
        /// </summary>
        public int Priority { get; init; }

        /// <summary>
        /// Whether the rule is enabled.
        /// </summary>
        public bool IsEnabled { get; init; } = true;

        /// <summary>
        /// Conditions that must all be met for the rule to apply.
        /// </summary>
        public required IReadOnlyList<RuleCondition> Conditions { get; init; }

        /// <summary>
        /// The target tier when the rule matches.
        /// </summary>
        public StorageTier TargetTier { get; init; }

        /// <summary>
        /// Whether to stop evaluating further rules after this one matches.
        /// </summary>
        public bool StopOnMatch { get; init; } = true;

        /// <summary>
        /// Optional content type filter (regex pattern).
        /// </summary>
        public string? ContentTypeFilter { get; init; }

        /// <summary>
        /// Optional classification filter.
        /// </summary>
        public string? ClassificationFilter { get; init; }

        /// <summary>
        /// Created timestamp.
        /// </summary>
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Represents a condition in a tiering rule.
    /// </summary>
    public sealed class RuleCondition
    {
        /// <summary>
        /// The property to evaluate.
        /// </summary>
        public required ConditionProperty Property { get; init; }

        /// <summary>
        /// The comparison operator.
        /// </summary>
        public required ConditionOperator Operator { get; init; }

        /// <summary>
        /// The value to compare against.
        /// </summary>
        public required object Value { get; init; }
    }

    /// <summary>
    /// Properties that can be used in rule conditions.
    /// </summary>
    public enum ConditionProperty
    {
        /// <summary>Object age since creation.</summary>
        Age,
        /// <summary>Time since last access.</summary>
        TimeSinceLastAccess,
        /// <summary>Time since last modification.</summary>
        TimeSinceLastModified,
        /// <summary>Object size in bytes.</summary>
        SizeBytes,
        /// <summary>Total access count.</summary>
        AccessCount,
        /// <summary>Accesses in last 24 hours.</summary>
        AccessesLast24Hours,
        /// <summary>Accesses in last 7 days.</summary>
        AccessesLast7Days,
        /// <summary>Accesses in last 30 days.</summary>
        AccessesLast30Days,
        /// <summary>Current storage tier.</summary>
        CurrentTier,
        /// <summary>Content type (MIME type).</summary>
        ContentType,
        /// <summary>Object classification.</summary>
        Classification
    }

    /// <summary>
    /// Comparison operators for rule conditions.
    /// </summary>
    public enum ConditionOperator
    {
        /// <summary>Equals.</summary>
        Equals,
        /// <summary>Not equals.</summary>
        NotEquals,
        /// <summary>Greater than.</summary>
        GreaterThan,
        /// <summary>Greater than or equal.</summary>
        GreaterThanOrEqual,
        /// <summary>Less than.</summary>
        LessThan,
        /// <summary>Less than or equal.</summary>
        LessThanOrEqual,
        /// <summary>Contains (for strings).</summary>
        Contains,
        /// <summary>Matches regex pattern.</summary>
        Matches,
        /// <summary>Is one of the specified values.</summary>
        In,
        /// <summary>Is not one of the specified values.</summary>
        NotIn
    }

    /// <inheritdoc/>
    public override string StrategyId => "tiering.policy";

    /// <inheritdoc/>
    public override string DisplayName => "Policy-Based Tiering";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 50000,
        TypicalLatencyMs = 0.5
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Policy-based tiering using configurable rules for automatic tier assignment. " +
        "Supports complex conditions based on age, size, access patterns, and content type " +
        "with priority ordering and conflict resolution.";

    /// <inheritdoc/>
    public override string[] Tags => ["tiering", "policy", "rules", "automatic", "conditions"];

    /// <summary>
    /// Initializes the strategy with default rules.
    /// </summary>
    public PolicyBasedTieringStrategy()
    {
        // Add default tiering policies
        AddDefaultRules();
    }

    /// <summary>
    /// Adds a tiering rule.
    /// </summary>
    /// <param name="rule">The rule to add.</param>
    public void AddRule(TieringRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        lock (_rulesLock)
        {
            _rules[rule.RuleId] = rule;
            RebuildSortedRules();
        }
    }

    /// <summary>
    /// Removes a tiering rule.
    /// </summary>
    /// <param name="ruleId">The rule identifier.</param>
    /// <returns>True if the rule was removed.</returns>
    public bool RemoveRule(string ruleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);

        lock (_rulesLock)
        {
            if (_rules.TryRemove(ruleId, out _))
            {
                RebuildSortedRules();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets all configured rules.
    /// </summary>
    /// <returns>All tiering rules ordered by priority.</returns>
    public IReadOnlyList<TieringRule> GetRules()
    {
        lock (_rulesLock)
        {
            return _sortedRules.ToList();
        }
    }

    /// <summary>
    /// Enables or disables a rule.
    /// </summary>
    /// <param name="ruleId">The rule identifier.</param>
    /// <param name="enabled">Whether to enable the rule.</param>
    /// <returns>True if the rule was found and updated.</returns>
    public bool SetRuleEnabled(string ruleId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);

        lock (_rulesLock)
        {
            if (_rules.TryGetValue(ruleId, out var existingRule))
            {
                var updatedRule = new TieringRule
                {
                    RuleId = existingRule.RuleId,
                    Name = existingRule.Name,
                    Description = existingRule.Description,
                    Priority = existingRule.Priority,
                    IsEnabled = enabled,
                    Conditions = existingRule.Conditions,
                    TargetTier = existingRule.TargetTier,
                    StopOnMatch = existingRule.StopOnMatch,
                    ContentTypeFilter = existingRule.ContentTypeFilter,
                    ClassificationFilter = existingRule.ClassificationFilter,
                    CreatedAt = existingRule.CreatedAt
                };

                _rules[ruleId] = updatedRule;
                RebuildSortedRules();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets rule match statistics.
    /// </summary>
    /// <returns>Total rules evaluated and matched.</returns>
    public (long Evaluated, long Matched) GetRuleStats()
    {
        return (Interlocked.Read(ref _rulesEvaluated), Interlocked.Read(ref _rulesMatched));
    }

    /// <inheritdoc/>
    protected override Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct)
    {
        List<TieringRule> rules;
        lock (_rulesLock)
        {
            rules = _sortedRules.ToList();
        }

        foreach (var rule in rules)
        {
            ct.ThrowIfCancellationRequested();

            if (!rule.IsEnabled)
                continue;

            Interlocked.Increment(ref _rulesEvaluated);

            // Check content type filter
            if (!string.IsNullOrEmpty(rule.ContentTypeFilter) &&
                !string.IsNullOrEmpty(data.ContentType) &&
                !Regex.IsMatch(data.ContentType, rule.ContentTypeFilter, RegexOptions.IgnoreCase))
            {
                continue;
            }

            // Check classification filter
            if (!string.IsNullOrEmpty(rule.ClassificationFilter) &&
                !string.IsNullOrEmpty(data.Classification) &&
                !data.Classification.Equals(rule.ClassificationFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Evaluate all conditions
            if (EvaluateConditions(data, rule.Conditions))
            {
                Interlocked.Increment(ref _rulesMatched);

                if (data.CurrentTier != rule.TargetTier)
                {
                    var reason = $"Rule '{rule.Name}' matched: {rule.Description ?? "conditions met"}";

                    return Task.FromResult(rule.TargetTier < data.CurrentTier
                        ? Promote(data, rule.TargetTier, reason, 0.9, CalculatePriority(data, rule.TargetTier))
                        : Demote(data, rule.TargetTier, reason, 0.9, CalculatePriority(data, rule.TargetTier),
                            EstimateMonthlySavings(data.SizeBytes, data.CurrentTier, rule.TargetTier)));
                }

                if (rule.StopOnMatch)
                {
                    return Task.FromResult(NoChange(data, $"At correct tier per rule '{rule.Name}'"));
                }
            }
        }

        // No rules matched
        return Task.FromResult(NoChange(data, "No policy rules matched"));
    }

    private bool EvaluateConditions(DataObject data, IReadOnlyList<RuleCondition> conditions)
    {
        foreach (var condition in conditions)
        {
            if (!EvaluateCondition(data, condition))
            {
                return false;
            }
        }

        return true;
    }

    private bool EvaluateCondition(DataObject data, RuleCondition condition)
    {
        var propertyValue = GetPropertyValue(data, condition.Property);
        if (propertyValue == null)
            return false;

        return condition.Operator switch
        {
            ConditionOperator.Equals => CompareValues(propertyValue, condition.Value) == 0,
            ConditionOperator.NotEquals => CompareValues(propertyValue, condition.Value) != 0,
            ConditionOperator.GreaterThan => CompareValues(propertyValue, condition.Value) > 0,
            ConditionOperator.GreaterThanOrEqual => CompareValues(propertyValue, condition.Value) >= 0,
            ConditionOperator.LessThan => CompareValues(propertyValue, condition.Value) < 0,
            ConditionOperator.LessThanOrEqual => CompareValues(propertyValue, condition.Value) <= 0,
            ConditionOperator.Contains => propertyValue.ToString()?.Contains(condition.Value.ToString() ?? "", StringComparison.OrdinalIgnoreCase) == true,
            ConditionOperator.Matches => Regex.IsMatch(propertyValue.ToString() ?? "", condition.Value.ToString() ?? "", RegexOptions.IgnoreCase),
            ConditionOperator.In => IsValueIn(propertyValue, condition.Value),
            ConditionOperator.NotIn => !IsValueIn(propertyValue, condition.Value),
            _ => false
        };
    }

    private static object? GetPropertyValue(DataObject data, ConditionProperty property)
    {
        return property switch
        {
            ConditionProperty.Age => data.Age,
            ConditionProperty.TimeSinceLastAccess => data.TimeSinceLastAccess,
            ConditionProperty.TimeSinceLastModified => data.TimeSinceLastModified,
            ConditionProperty.SizeBytes => data.SizeBytes,
            ConditionProperty.AccessCount => data.AccessCount,
            ConditionProperty.AccessesLast24Hours => data.AccessesLast24Hours,
            ConditionProperty.AccessesLast7Days => data.AccessesLast7Days,
            ConditionProperty.AccessesLast30Days => data.AccessesLast30Days,
            ConditionProperty.CurrentTier => data.CurrentTier,
            ConditionProperty.ContentType => data.ContentType,
            ConditionProperty.Classification => data.Classification,
            _ => null
        };
    }

    private static int CompareValues(object left, object right)
    {
        if (left is TimeSpan leftTs)
        {
            var rightTs = right switch
            {
                TimeSpan ts => ts,
                double d => TimeSpan.FromDays(d),
                int i => TimeSpan.FromDays(i),
                long l => TimeSpan.FromDays(l),
                string s when double.TryParse(s.TrimEnd('d', 'h', 'm'), out var val) =>
                    s.EndsWith('h') ? TimeSpan.FromHours(val) :
                    s.EndsWith('m') ? TimeSpan.FromMinutes(val) :
                    TimeSpan.FromDays(val),
                _ => TimeSpan.Zero
            };
            return leftTs.CompareTo(rightTs);
        }

        if (left is long leftLong)
        {
            var rightLong = Convert.ToInt64(right);
            return leftLong.CompareTo(rightLong);
        }

        if (left is int leftInt)
        {
            var rightInt = Convert.ToInt32(right);
            return leftInt.CompareTo(rightInt);
        }

        if (left is StorageTier leftTier)
        {
            var rightTier = right switch
            {
                StorageTier t => t,
                int i => (StorageTier)i,
                string s when Enum.TryParse<StorageTier>(s, true, out var t) => t,
                _ => StorageTier.Hot
            };
            return leftTier.CompareTo(rightTier);
        }

        if (left is string leftStr)
        {
            return string.Compare(leftStr, right?.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        return 0;
    }

    private static bool IsValueIn(object value, object collection)
    {
        if (collection is IEnumerable<object> enumerable)
        {
            return enumerable.Any(v => CompareValues(value, v) == 0);
        }

        if (collection is string str)
        {
            var items = str.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return items.Any(i => CompareValues(value, i) == 0);
        }

        return CompareValues(value, collection) == 0;
    }

    private static double CalculatePriority(DataObject data, StorageTier targetTier)
    {
        // Higher priority for larger objects and bigger tier differences
        var tierDiff = Math.Abs((int)data.CurrentTier - (int)targetTier);
        var sizeFactor = Math.Min(1.0, data.SizeBytes / (1024.0 * 1024.0 * 1024.0)); // Cap at 1GB
        return (tierDiff * 0.3 + sizeFactor * 0.7);
    }

    private void RebuildSortedRules()
    {
        _sortedRules.Clear();
        _sortedRules.AddRange(_rules.Values.OrderByDescending(r => r.Priority).ThenBy(r => r.CreatedAt));
    }

    private void AddDefaultRules()
    {
        // Rule: Move to warm if no access in 30 days
        AddRule(new TieringRule
        {
            RuleId = "default-warm-30d",
            Name = "Move to Warm after 30 days inactivity",
            Description = "Move objects to warm tier if not accessed for 30 days",
            Priority = 100,
            TargetTier = StorageTier.Warm,
            Conditions = new[]
            {
                new RuleCondition { Property = ConditionProperty.TimeSinceLastAccess, Operator = ConditionOperator.GreaterThan, Value = TimeSpan.FromDays(30) },
                new RuleCondition { Property = ConditionProperty.CurrentTier, Operator = ConditionOperator.Equals, Value = StorageTier.Hot }
            }
        });

        // Rule: Move to cold if no access in 90 days
        AddRule(new TieringRule
        {
            RuleId = "default-cold-90d",
            Name = "Move to Cold after 90 days inactivity",
            Description = "Move objects to cold tier if not accessed for 90 days",
            Priority = 90,
            TargetTier = StorageTier.Cold,
            Conditions = new[]
            {
                new RuleCondition { Property = ConditionProperty.TimeSinceLastAccess, Operator = ConditionOperator.GreaterThan, Value = TimeSpan.FromDays(90) },
                new RuleCondition { Property = ConditionProperty.CurrentTier, Operator = ConditionOperator.In, Value = "Hot,Warm" }
            }
        });

        // Rule: Move to archive if no access in 365 days
        AddRule(new TieringRule
        {
            RuleId = "default-archive-365d",
            Name = "Move to Archive after 1 year inactivity",
            Description = "Move objects to archive tier if not accessed for 1 year",
            Priority = 80,
            TargetTier = StorageTier.Archive,
            Conditions = new[]
            {
                new RuleCondition { Property = ConditionProperty.TimeSinceLastAccess, Operator = ConditionOperator.GreaterThan, Value = TimeSpan.FromDays(365) },
                new RuleCondition { Property = ConditionProperty.CurrentTier, Operator = ConditionOperator.In, Value = "Hot,Warm,Cold" }
            }
        });

        // Rule: Large files (>1GB) go to cold immediately
        AddRule(new TieringRule
        {
            RuleId = "default-large-files",
            Name = "Large files to Cold tier",
            Description = "Move files larger than 1GB to cold tier",
            Priority = 110,
            TargetTier = StorageTier.Cold,
            Conditions = new[]
            {
                new RuleCondition { Property = ConditionProperty.SizeBytes, Operator = ConditionOperator.GreaterThan, Value = 1024L * 1024L * 1024L },
                new RuleCondition { Property = ConditionProperty.AccessesLast7Days, Operator = ConditionOperator.LessThan, Value = 2 }
            }
        });

        // Rule: Keep frequently accessed in hot
        AddRule(new TieringRule
        {
            RuleId = "default-hot-frequent",
            Name = "Keep frequent access in Hot",
            Description = "Keep objects accessed frequently in hot tier",
            Priority = 150,
            TargetTier = StorageTier.Hot,
            Conditions = new[]
            {
                new RuleCondition { Property = ConditionProperty.AccessesLast7Days, Operator = ConditionOperator.GreaterThan, Value = 10 }
            }
        });
    }
}
