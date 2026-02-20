using System.Diagnostics;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.AiEnhanced;

/// <summary>
/// Data management goal type.
/// </summary>
public enum DataGoalType
{
    /// <summary>
    /// Optimize for fast access latency.
    /// </summary>
    FastAccess,

    /// <summary>
    /// Optimize for low storage cost.
    /// </summary>
    LowCost,

    /// <summary>
    /// Optimize for high durability.
    /// </summary>
    HighDurability,

    /// <summary>
    /// Optimize for geographic availability.
    /// </summary>
    GeoAvailability,

    /// <summary>
    /// Optimize for compliance requirements.
    /// </summary>
    Compliance,

    /// <summary>
    /// Optimize for security.
    /// </summary>
    Security,

    /// <summary>
    /// Balance multiple objectives.
    /// </summary>
    Balanced
}

/// <summary>
/// Declarative goal for data management.
/// </summary>
public sealed class DataGoal
{
    /// <summary>
    /// Goal identifier.
    /// </summary>
    public required string GoalId { get; init; }

    /// <summary>
    /// Goal type.
    /// </summary>
    public required DataGoalType Type { get; init; }

    /// <summary>
    /// Priority (1-100, higher is more important).
    /// </summary>
    public int Priority { get; init; } = 50;

    /// <summary>
    /// Target metric value (type-specific).
    /// </summary>
    public double? TargetValue { get; init; }

    /// <summary>
    /// Target metric unit (e.g., "ms", "USD", "nines").
    /// </summary>
    public string? TargetUnit { get; init; }

    /// <summary>
    /// Constraints that must be satisfied.
    /// </summary>
    public Dictionary<string, object>? Constraints { get; init; }

    /// <summary>
    /// When the goal was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Natural language description of the goal.
    /// </summary>
    public string? Description { get; init; }
}

/// <summary>
/// Action to take to satisfy a goal.
/// </summary>
public sealed class GoalAction
{
    /// <summary>
    /// Action type.
    /// </summary>
    public required string ActionType { get; init; }

    /// <summary>
    /// Action description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Expected impact on goal satisfaction (0.0-1.0).
    /// </summary>
    public double ExpectedImpact { get; init; }

    /// <summary>
    /// Estimated cost of the action.
    /// </summary>
    public decimal? EstimatedCost { get; init; }

    /// <summary>
    /// Estimated time to complete.
    /// </summary>
    public TimeSpan? EstimatedDuration { get; init; }

    /// <summary>
    /// Action parameters.
    /// </summary>
    public Dictionary<string, object>? Parameters { get; init; }

    /// <summary>
    /// Risk level (0.0-1.0).
    /// </summary>
    public double RiskLevel { get; init; }
}

/// <summary>
/// Goal satisfaction status.
/// </summary>
public sealed class GoalSatisfaction
{
    /// <summary>
    /// Goal being measured.
    /// </summary>
    public required DataGoal Goal { get; init; }

    /// <summary>
    /// Current satisfaction level (0.0-1.0).
    /// </summary>
    public double SatisfactionLevel { get; init; }

    /// <summary>
    /// Current measured value.
    /// </summary>
    public double? CurrentValue { get; init; }

    /// <summary>
    /// Gap to target.
    /// </summary>
    public double? GapToTarget { get; init; }

    /// <summary>
    /// Trend direction (-1: worsening, 0: stable, 1: improving).
    /// </summary>
    public int Trend { get; init; }

    /// <summary>
    /// Recommended actions to improve satisfaction.
    /// </summary>
    public IReadOnlyList<GoalAction> RecommendedActions { get; init; } = Array.Empty<GoalAction>();

    /// <summary>
    /// When this was measured.
    /// </summary>
    public DateTime MeasuredAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Whether goal is currently satisfied.
    /// </summary>
    public bool IsSatisfied => SatisfactionLevel >= 0.8;
}

/// <summary>
/// Intent-based data management strategy using declarative goals.
/// Define what you want, and the system figures out how to achieve it.
/// </summary>
/// <remarks>
/// Features:
/// - Declarative goal specification
/// - AI-powered goal interpretation
/// - Automatic action planning
/// - Goal satisfaction monitoring
/// - Multi-objective optimization
/// </remarks>
public sealed class IntentBasedDataManagementStrategy : AiEnhancedStrategyBase
{
    private readonly BoundedDictionary<string, DataGoal> _goals = new BoundedDictionary<string, DataGoal>(1000);
    private readonly BoundedDictionary<string, GoalSatisfaction> _satisfactionCache = new BoundedDictionary<string, GoalSatisfaction>(1000);
    private readonly BoundedDictionary<string, List<double>> _metricsHistory = new BoundedDictionary<string, List<double>>(1000);
    private readonly object _historyLock = new();

    /// <inheritdoc/>
    public override string StrategyId => "ai.intent-based";

    /// <inheritdoc/>
    public override string DisplayName => "Intent-Based Data Management";

    /// <inheritdoc/>
    public override AiEnhancedCategory AiCategory => AiEnhancedCategory.IntentBased;

    /// <inheritdoc/>
    public override IntelligenceCapabilities RequiredCapabilities =>
        IntelligenceCapabilities.IntentRecognition | IntelligenceCapabilities.Prediction;

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 1_000,
        TypicalLatencyMs = 20.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Intent-based data management strategy using declarative goals. " +
        "Define what you want (fast access, low cost, high durability) and the system determines how to achieve it.";

    /// <inheritdoc/>
    public override string[] Tags => ["ai", "intent", "declarative", "goals", "optimization", "slo"];

    /// <summary>
    /// Sets a data management goal.
    /// </summary>
    /// <param name="goal">Goal to set.</param>
    public void SetGoal(DataGoal goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal.GoalId);

        _goals[goal.GoalId] = goal;
        _satisfactionCache.TryRemove(goal.GoalId, out _);
    }

    /// <summary>
    /// Parses a natural language goal and creates a structured goal.
    /// </summary>
    /// <param name="description">Natural language description of the goal.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Parsed goal or null if parsing failed.</returns>
    public async Task<DataGoal?> ParseGoalAsync(string description, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var sw = Stopwatch.StartNew();

        if (IsAiAvailable)
        {
            var goal = await ParseGoalWithAiAsync(description, ct);
            if (goal != null)
            {
                RecordAiOperation(true, false, 1.0, sw.Elapsed.TotalMilliseconds);
                return goal;
            }
        }

        // Fallback: simple keyword matching
        var fallbackGoal = ParseGoalWithFallback(description);
        RecordAiOperation(false, false, 0, sw.Elapsed.TotalMilliseconds);
        return fallbackGoal;
    }

    /// <summary>
    /// Gets a goal by ID.
    /// </summary>
    /// <param name="goalId">Goal identifier.</param>
    /// <returns>Goal or null if not found.</returns>
    public DataGoal? GetGoal(string goalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);
        return _goals.TryGetValue(goalId, out var goal) ? goal : null;
    }

    /// <summary>
    /// Gets all goals.
    /// </summary>
    /// <returns>All registered goals.</returns>
    public IReadOnlyList<DataGoal> GetAllGoals()
    {
        return _goals.Values.OrderByDescending(g => g.Priority).ToList();
    }

    /// <summary>
    /// Removes a goal.
    /// </summary>
    /// <param name="goalId">Goal identifier.</param>
    /// <returns>True if removed.</returns>
    public bool RemoveGoal(string goalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);
        _satisfactionCache.TryRemove(goalId, out _);
        return _goals.TryRemove(goalId, out _);
    }

    /// <summary>
    /// Reports a metric value for goal satisfaction calculation.
    /// </summary>
    /// <param name="goalId">Goal identifier.</param>
    /// <param name="value">Current metric value.</param>
    public void ReportMetric(string goalId, double value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);

        lock (_historyLock)
        {
            if (!_metricsHistory.TryGetValue(goalId, out var history))
            {
                history = new List<double>();
                _metricsHistory[goalId] = history;
            }

            history.Add(value);

            // Keep last 100 values
            if (history.Count > 100)
            {
                history.RemoveAt(0);
            }
        }

        // Invalidate satisfaction cache
        _satisfactionCache.TryRemove(goalId, out _);
    }

    /// <summary>
    /// Measures current goal satisfaction.
    /// </summary>
    /// <param name="goalId">Goal identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Goal satisfaction status.</returns>
    public async Task<GoalSatisfaction?> MeasureSatisfactionAsync(string goalId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);

        if (!_goals.TryGetValue(goalId, out var goal))
            return null;

        // Check cache
        if (_satisfactionCache.TryGetValue(goalId, out var cached) &&
            DateTime.UtcNow - cached.MeasuredAt < TimeSpan.FromMinutes(5))
        {
            return cached;
        }

        var sw = Stopwatch.StartNew();
        GoalSatisfaction satisfaction;

        if (IsAiAvailable)
        {
            satisfaction = await MeasureWithAiAsync(goal, ct) ?? MeasureWithFallback(goal);
            RecordAiOperation(true, false, satisfaction.SatisfactionLevel, sw.Elapsed.TotalMilliseconds);
        }
        else
        {
            satisfaction = MeasureWithFallback(goal);
            RecordAiOperation(false, false, 0, sw.Elapsed.TotalMilliseconds);
        }

        _satisfactionCache[goalId] = satisfaction;
        return satisfaction;
    }

    /// <summary>
    /// Gets overall goal satisfaction across all goals.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary of goal IDs to satisfaction status.</returns>
    public async Task<IReadOnlyDictionary<string, GoalSatisfaction>> GetOverallSatisfactionAsync(CancellationToken ct = default)
    {
        var results = new Dictionary<string, GoalSatisfaction>();

        foreach (var goalId in _goals.Keys)
        {
            var satisfaction = await MeasureSatisfactionAsync(goalId, ct);
            if (satisfaction != null)
            {
                results[goalId] = satisfaction;
            }
        }

        return results;
    }

    /// <summary>
    /// Gets recommended actions to improve goal satisfaction.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Prioritized list of actions.</returns>
    public async Task<IReadOnlyList<(DataGoal Goal, GoalAction Action)>> GetRecommendedActionsAsync(CancellationToken ct = default)
    {
        var satisfactions = await GetOverallSatisfactionAsync(ct);
        var actions = new List<(DataGoal Goal, GoalAction Action, double Priority)>();

        foreach (var (goalId, satisfaction) in satisfactions)
        {
            if (!satisfaction.IsSatisfied)
            {
                foreach (var action in satisfaction.RecommendedActions)
                {
                    var priority = satisfaction.Goal.Priority * (1 - satisfaction.SatisfactionLevel) * action.ExpectedImpact;
                    actions.Add((satisfaction.Goal, action, priority));
                }
            }
        }

        return actions
            .OrderByDescending(a => a.Priority)
            .Select(a => (a.Goal, a.Action))
            .ToList();
    }

    private async Task<DataGoal?> ParseGoalWithAiAsync(string description, CancellationToken ct)
    {
        var prompt = $"Parse this data management goal and extract: goal_type (FastAccess, LowCost, HighDurability, GeoAvailability, Compliance, Security, Balanced), target_value, target_unit, priority (1-100). Goal: {description}";

        var response = await RequestCompletionAsync(prompt, "You are a data management goal parser. Output JSON only.", DefaultContext, ct);

        if (response != null)
        {
            try
            {
                // Try to parse JSON response - simplified for robustness
                var lower = response.ToLowerInvariant();
                var goalType = DataGoalType.Balanced;
                double? targetValue = null;
                string? targetUnit = null;
                int priority = 50;

                if (lower.Contains("fastaccess") || lower.Contains("fast_access") || lower.Contains("\"fast"))
                    goalType = DataGoalType.FastAccess;
                else if (lower.Contains("lowcost") || lower.Contains("low_cost") || lower.Contains("\"cost"))
                    goalType = DataGoalType.LowCost;
                else if (lower.Contains("highdurability") || lower.Contains("durability"))
                    goalType = DataGoalType.HighDurability;
                else if (lower.Contains("geoavailability") || lower.Contains("geo") || lower.Contains("region"))
                    goalType = DataGoalType.GeoAvailability;
                else if (lower.Contains("compliance"))
                    goalType = DataGoalType.Compliance;
                else if (lower.Contains("security"))
                    goalType = DataGoalType.Security;

                return new DataGoal
                {
                    GoalId = Guid.NewGuid().ToString("N"),
                    Type = goalType,
                    Priority = priority,
                    TargetValue = targetValue,
                    TargetUnit = targetUnit,
                    Description = description
                };
            }
            catch
            {
                // Parsing failed, return null to trigger fallback
            }
        }

        return null;
    }

    private DataGoal ParseGoalWithFallback(string description)
    {
        var lower = description.ToLowerInvariant();

        DataGoalType goalType;
        double? targetValue = null;
        string? targetUnit = null;

        if (lower.Contains("fast") || lower.Contains("latency") || lower.Contains("quick") || lower.Contains("speed"))
        {
            goalType = DataGoalType.FastAccess;
            targetUnit = "ms";
        }
        else if (lower.Contains("cost") || lower.Contains("cheap") || lower.Contains("budget") || lower.Contains("save"))
        {
            goalType = DataGoalType.LowCost;
            targetUnit = "USD";
        }
        else if (lower.Contains("durability") || lower.Contains("reliable") || lower.Contains("never lose"))
        {
            goalType = DataGoalType.HighDurability;
            targetUnit = "nines";
            targetValue = 99.999;
        }
        else if (lower.Contains("region") || lower.Contains("geographic") || lower.Contains("global") || lower.Contains("worldwide"))
        {
            goalType = DataGoalType.GeoAvailability;
        }
        else if (lower.Contains("compliance") || lower.Contains("gdpr") || lower.Contains("hipaa") || lower.Contains("regulation"))
        {
            goalType = DataGoalType.Compliance;
        }
        else if (lower.Contains("security") || lower.Contains("secure") || lower.Contains("encrypt") || lower.Contains("protect"))
        {
            goalType = DataGoalType.Security;
        }
        else
        {
            goalType = DataGoalType.Balanced;
        }

        // Try to extract numbers
        var numbers = System.Text.RegularExpressions.Regex.Matches(lower, @"\d+\.?\d*");
        if (numbers.Count > 0 && double.TryParse(numbers[0].Value, out var num))
        {
            targetValue = num;
        }

        return new DataGoal
        {
            GoalId = Guid.NewGuid().ToString("N"),
            Type = goalType,
            Priority = 50,
            TargetValue = targetValue,
            TargetUnit = targetUnit,
            Description = description
        };
    }

    private async Task<GoalSatisfaction?> MeasureWithAiAsync(DataGoal goal, CancellationToken ct)
    {
        List<double>? history = null;
        lock (_historyLock)
        {
            _metricsHistory.TryGetValue(goal.GoalId, out history);
        }

        var inputData = new Dictionary<string, object>
        {
            ["goalType"] = goal.Type.ToString(),
            ["targetValue"] = goal.TargetValue ?? 0,
            ["targetUnit"] = goal.TargetUnit ?? "",
            ["priority"] = goal.Priority,
            ["history"] = history?.ToArray() ?? Array.Empty<double>()
        };

        var prediction = await RequestPredictionAsync("goal_satisfaction", inputData, DefaultContext, ct);

        if (prediction.HasValue)
        {
            var satisfaction = prediction.Value.Confidence;
            var metadata = prediction.Value.Metadata;

            var actions = new List<GoalAction>();
            if (metadata.TryGetValue("actions", out var actionsObj) && actionsObj is object[] actionsList)
            {
                foreach (var actionObj in actionsList.OfType<Dictionary<string, object>>())
                {
                    actions.Add(new GoalAction
                    {
                        ActionType = actionObj.TryGetValue("type", out var t) ? t?.ToString() ?? "" : "optimize",
                        Description = actionObj.TryGetValue("description", out var d) ? d?.ToString() ?? "" : "Optimize configuration",
                        ExpectedImpact = actionObj.TryGetValue("impact", out var i) && i is double imp ? imp : 0.5
                    });
                }
            }

            return new GoalSatisfaction
            {
                Goal = goal,
                SatisfactionLevel = satisfaction,
                CurrentValue = history?.LastOrDefault(),
                GapToTarget = goal.TargetValue.HasValue && history?.Count > 0
                    ? Math.Abs(goal.TargetValue.Value - history.Last())
                    : null,
                Trend = CalculateTrend(history),
                RecommendedActions = actions
            };
        }

        return null;
    }

    private GoalSatisfaction MeasureWithFallback(DataGoal goal)
    {
        List<double>? history = null;
        lock (_historyLock)
        {
            _metricsHistory.TryGetValue(goal.GoalId, out history);
        }

        var currentValue = history?.LastOrDefault();
        double satisfaction = 0.5;
        double? gap = null;

        if (goal.TargetValue.HasValue && currentValue.HasValue)
        {
            // Calculate satisfaction based on target
            switch (goal.Type)
            {
                case DataGoalType.FastAccess:
                    // Lower is better for latency
                    satisfaction = currentValue <= goal.TargetValue ? 1.0 : Math.Max(0, 1 - (currentValue.Value - goal.TargetValue.Value) / goal.TargetValue.Value);
                    break;
                case DataGoalType.LowCost:
                    // Lower is better for cost
                    satisfaction = currentValue <= goal.TargetValue ? 1.0 : Math.Max(0, 1 - (currentValue.Value - goal.TargetValue.Value) / goal.TargetValue.Value);
                    break;
                case DataGoalType.HighDurability:
                    // Higher is better for durability
                    satisfaction = currentValue >= goal.TargetValue ? 1.0 : currentValue.Value / goal.TargetValue.Value;
                    break;
                default:
                    satisfaction = 0.5;
                    break;
            }

            gap = Math.Abs(goal.TargetValue.Value - currentValue.Value);
        }

        var actions = new List<GoalAction>();
        if (satisfaction < 0.8)
        {
            actions.Add(GetDefaultAction(goal.Type, satisfaction));
        }

        return new GoalSatisfaction
        {
            Goal = goal,
            SatisfactionLevel = satisfaction,
            CurrentValue = currentValue,
            GapToTarget = gap,
            Trend = CalculateTrend(history),
            RecommendedActions = actions
        };
    }

    private static int CalculateTrend(List<double>? history)
    {
        if (history == null || history.Count < 3)
            return 0;

        var recent = history.TakeLast(5).ToList();
        var first = recent.First();
        var last = recent.Last();
        var diff = last - first;

        if (Math.Abs(diff) < first * 0.05)
            return 0; // Stable
        return diff > 0 ? 1 : -1;
    }

    private static GoalAction GetDefaultAction(DataGoalType goalType, double currentSatisfaction)
    {
        return goalType switch
        {
            DataGoalType.FastAccess => new GoalAction
            {
                ActionType = "cache",
                Description = "Add caching layer or move data to faster storage tier",
                ExpectedImpact = 0.3,
                RiskLevel = 0.1
            },
            DataGoalType.LowCost => new GoalAction
            {
                ActionType = "tier",
                Description = "Move infrequently accessed data to cheaper storage tier",
                ExpectedImpact = 0.4,
                RiskLevel = 0.2
            },
            DataGoalType.HighDurability => new GoalAction
            {
                ActionType = "replicate",
                Description = "Increase replication factor across regions",
                ExpectedImpact = 0.5,
                RiskLevel = 0.1
            },
            DataGoalType.GeoAvailability => new GoalAction
            {
                ActionType = "distribute",
                Description = "Enable geo-replication to additional regions",
                ExpectedImpact = 0.6,
                RiskLevel = 0.2
            },
            DataGoalType.Compliance => new GoalAction
            {
                ActionType = "audit",
                Description = "Enable comprehensive audit logging and retention policies",
                ExpectedImpact = 0.4,
                RiskLevel = 0.1
            },
            DataGoalType.Security => new GoalAction
            {
                ActionType = "encrypt",
                Description = "Enable encryption at rest and in transit",
                ExpectedImpact = 0.5,
                RiskLevel = 0.1
            },
            _ => new GoalAction
            {
                ActionType = "review",
                Description = "Review configuration and optimize for current workload",
                ExpectedImpact = 0.3,
                RiskLevel = 0.1
            }
        };
    }
}
