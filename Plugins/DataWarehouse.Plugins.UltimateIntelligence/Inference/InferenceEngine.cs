using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Inference;

// ==================================================================================
// T90 PHASE G: KNOWLEDGE INFERENCE
// G1: Core Inference Engine, G2: Built-in Rules, G3: Inference Management
// ==================================================================================

#region Message Bus Topics

/// <summary>
/// Message bus topics for knowledge inference operations.
/// Provides comprehensive topic definitions for all inference-related messaging.
/// </summary>
public static class InferenceTopics
{
    /// <summary>Base prefix for all inference topics.</summary>
    public const string Prefix = "intelligence.inference";

    #region Inference Operations

    /// <summary>Request inference on a set of facts.</summary>
    public const string Infer = $"{Prefix}.infer";

    /// <summary>Response for inference operation.</summary>
    public const string InferResponse = $"{Prefix}.infer.response";

    /// <summary>Request batch inference on multiple fact sets.</summary>
    public const string InferBatch = $"{Prefix}.infer.batch";

    /// <summary>Response for batch inference operation.</summary>
    public const string InferBatchResponse = $"{Prefix}.infer.batch.response";

    /// <summary>Request explanation for an inference.</summary>
    public const string Explain = $"{Prefix}.explain";

    /// <summary>Response for explanation request.</summary>
    public const string ExplainResponse = $"{Prefix}.explain.response";

    #endregion

    #region Rule Management

    /// <summary>Register a new inference rule.</summary>
    public const string RegisterRule = $"{Prefix}.rule.register";

    /// <summary>Response for rule registration.</summary>
    public const string RegisterRuleResponse = $"{Prefix}.rule.register.response";

    /// <summary>Unregister an existing rule.</summary>
    public const string UnregisterRule = $"{Prefix}.rule.unregister";

    /// <summary>Response for rule unregistration.</summary>
    public const string UnregisterRuleResponse = $"{Prefix}.rule.unregister.response";

    /// <summary>Enable a rule.</summary>
    public const string EnableRule = $"{Prefix}.rule.enable";

    /// <summary>Response for rule enablement.</summary>
    public const string EnableRuleResponse = $"{Prefix}.rule.enable.response";

    /// <summary>Disable a rule.</summary>
    public const string DisableRule = $"{Prefix}.rule.disable";

    /// <summary>Response for rule disablement.</summary>
    public const string DisableRuleResponse = $"{Prefix}.rule.disable.response";

    /// <summary>Get all registered rules.</summary>
    public const string GetRules = $"{Prefix}.rules.get";

    /// <summary>Response for get rules request.</summary>
    public const string GetRulesResponse = $"{Prefix}.rules.get.response";

    #endregion

    #region Cache Management

    /// <summary>Invalidate cached inferences.</summary>
    public const string InvalidateCache = $"{Prefix}.cache.invalidate";

    /// <summary>Response for cache invalidation.</summary>
    public const string InvalidateCacheResponse = $"{Prefix}.cache.invalidate.response";

    /// <summary>Get cache statistics.</summary>
    public const string GetCacheStats = $"{Prefix}.cache.stats";

    /// <summary>Response for cache statistics.</summary>
    public const string GetCacheStatsResponse = $"{Prefix}.cache.stats.response";

    /// <summary>Clear entire cache.</summary>
    public const string ClearCache = $"{Prefix}.cache.clear";

    /// <summary>Response for cache clear operation.</summary>
    public const string ClearCacheResponse = $"{Prefix}.cache.clear.response";

    #endregion

    #region Events

    /// <summary>Event emitted when new knowledge is inferred.</summary>
    public const string KnowledgeInferred = $"{Prefix}.event.inferred";

    /// <summary>Event emitted when an inference is invalidated.</summary>
    public const string InferenceInvalidated = $"{Prefix}.event.invalidated";

    /// <summary>Event emitted when a rule fires.</summary>
    public const string RuleFired = $"{Prefix}.event.rule-fired";

    /// <summary>Event emitted when inference confidence changes.</summary>
    public const string ConfidenceChanged = $"{Prefix}.event.confidence-changed";

    #endregion
}

#endregion

#region G1: Core Inference Engine

/// <summary>
/// Core inference rule definition using "If X and Y then Z" logic.
/// Immutable record for thread-safe rule evaluation.
/// </summary>
/// <param name="RuleId">Unique identifier for this rule.</param>
/// <param name="Name">Human-readable name of the rule.</param>
/// <param name="Description">Detailed description of what this rule infers.</param>
/// <param name="Category">Category for grouping related rules.</param>
/// <param name="Conditions">Conditions that must be met for the rule to fire (the "If X and Y" part).</param>
/// <param name="Conclusion">The conclusion to draw when conditions are met (the "then Z" part).</param>
/// <param name="BaseConfidence">Base confidence level for inferences from this rule (0.0 - 1.0).</param>
/// <param name="Priority">Priority for rule evaluation order (higher = evaluated first).</param>
/// <param name="Tags">Tags for categorizing and filtering rules.</param>
public sealed record InferenceRule(
    string RuleId,
    string Name,
    string Description,
    string Category,
    IReadOnlyList<RuleCondition> Conditions,
    RuleConclusion Conclusion,
    double BaseConfidence = 0.8,
    int Priority = 100,
    IReadOnlyList<string>? Tags = null)
{
    /// <summary>
    /// Gets whether this rule is currently enabled.
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// Gets the minimum number of conditions that must match (for OR-like logic).
    /// Default is all conditions must match (AND logic).
    /// </summary>
    public int MinConditionsToMatch { get; init; } = -1; // -1 means all

    /// <summary>
    /// Creates a new rule with the specified enabled state.
    /// </summary>
    public InferenceRule WithEnabled(bool enabled) => this with { IsEnabled = enabled };
}

/// <summary>
/// Represents a single condition in an inference rule.
/// </summary>
/// <param name="FactType">The type of fact to check (e.g., "file.modified", "backup.timestamp").</param>
/// <param name="Operator">The comparison operator.</param>
/// <param name="Value">The value to compare against (can be null for existence checks).</param>
/// <param name="RelativeToFact">Optional fact to compare against instead of a static value.</param>
public sealed record RuleCondition(
    string FactType,
    ConditionOperator Operator,
    object? Value = null,
    string? RelativeToFact = null)
{
    /// <summary>
    /// Gets the weight of this condition for confidence calculation.
    /// </summary>
    public double Weight { get; init; } = 1.0;

    /// <summary>
    /// Gets optional metadata for the condition.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Operators for condition evaluation.
/// </summary>
public enum ConditionOperator
{
    /// <summary>Fact exists with any value.</summary>
    Exists,
    /// <summary>Fact does not exist.</summary>
    NotExists,
    /// <summary>Fact equals value.</summary>
    Equals,
    /// <summary>Fact does not equal value.</summary>
    NotEquals,
    /// <summary>Fact is greater than value.</summary>
    GreaterThan,
    /// <summary>Fact is less than value.</summary>
    LessThan,
    /// <summary>Fact is greater than or equal to value.</summary>
    GreaterThanOrEqual,
    /// <summary>Fact is less than or equal to value.</summary>
    LessThanOrEqual,
    /// <summary>Fact contains value (for strings/collections).</summary>
    Contains,
    /// <summary>Fact does not contain value.</summary>
    NotContains,
    /// <summary>Fact matches regex pattern.</summary>
    Matches,
    /// <summary>Fact is within range [Value, RelativeToFact].</summary>
    InRange,
    /// <summary>Fact is after another date/time fact.</summary>
    After,
    /// <summary>Fact is before another date/time fact.</summary>
    Before,
    /// <summary>Fact shows deviation from normal pattern.</summary>
    Deviates
}

/// <summary>
/// Represents the conclusion of an inference rule.
/// </summary>
/// <param name="InferredFactType">The type of fact to infer.</param>
/// <param name="InferredValue">The value to assign to the inferred fact.</param>
public sealed record RuleConclusion(
    string InferredFactType,
    object? InferredValue = null)
{
    /// <summary>
    /// Gets the severity level of the inferred knowledge (for risk/alert scenarios).
    /// </summary>
    public InferenceSeverity Severity { get; init; } = InferenceSeverity.Info;

    /// <summary>
    /// Gets the action to recommend based on this inference.
    /// </summary>
    public string? RecommendedAction { get; init; }

    /// <summary>
    /// Gets additional metadata for the conclusion.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Severity levels for inferred knowledge.
/// </summary>
public enum InferenceSeverity
{
    /// <summary>Informational inference.</summary>
    Info,
    /// <summary>Low-priority warning.</summary>
    Low,
    /// <summary>Medium-priority warning.</summary>
    Medium,
    /// <summary>High-priority alert.</summary>
    High,
    /// <summary>Critical alert requiring immediate attention.</summary>
    Critical
}

/// <summary>
/// Represents a fact used as input for inference.
/// </summary>
/// <param name="FactType">The type/category of this fact.</param>
/// <param name="Value">The value of this fact.</param>
/// <param name="SourceId">Identifier of the source that provided this fact.</param>
/// <param name="Timestamp">When this fact was observed.</param>
public sealed record InferenceFact(
    string FactType,
    object? Value,
    string SourceId,
    DateTimeOffset Timestamp)
{
    /// <summary>
    /// Gets the confidence level of this fact (0.0 - 1.0).
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    /// Gets additional metadata about this fact.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Represents inferred knowledge resulting from rule evaluation.
/// </summary>
public sealed class InferredKnowledge
{
    /// <summary>
    /// Gets the unique identifier for this inferred knowledge.
    /// </summary>
    public required string InferenceId { get; init; }

    /// <summary>
    /// Gets the type of inferred fact.
    /// </summary>
    public required string FactType { get; init; }

    /// <summary>
    /// Gets the inferred value.
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    /// Gets the confidence level of this inference (0.0 - 1.0).
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// Gets the severity level.
    /// </summary>
    public InferenceSeverity Severity { get; init; } = InferenceSeverity.Info;

    /// <summary>
    /// Gets the rule that produced this inference.
    /// </summary>
    public required string RuleId { get; init; }

    /// <summary>
    /// Gets the facts that led to this inference.
    /// </summary>
    public required IReadOnlyList<InferenceFact> SourceFacts { get; init; }

    /// <summary>
    /// Gets when this inference was made.
    /// </summary>
    public DateTimeOffset InferredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the expiration time for this inference.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Gets the recommended action, if any.
    /// </summary>
    public string? RecommendedAction { get; init; }

    /// <summary>
    /// Gets the explanation chain for this inference.
    /// </summary>
    public InferenceExplanation? Explanation { get; init; }

    /// <summary>
    /// Gets additional metadata.
    /// </summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Rule engine that manages and evaluates inference rules.
/// Thread-safe implementation for concurrent rule evaluation.
/// </summary>
public sealed class RuleEngine
{
    private readonly BoundedDictionary<string, InferenceRule> _rules = new BoundedDictionary<string, InferenceRule>(1000);
    private readonly SemaphoreSlim _evaluationLock = new(1, 1);
    private readonly object _statsLock = new();
    private long _totalEvaluations;
    private long _totalRulesFired;
    private long _totalInferencesProduced;

    /// <summary>
    /// Event raised when a rule fires.
    /// </summary>
    public event EventHandler<RuleFiredEventArgs>? RuleFired;

    /// <summary>
    /// Registers a new inference rule.
    /// </summary>
    /// <param name="rule">The rule to register.</param>
    /// <exception cref="ArgumentNullException">If rule is null.</exception>
    /// <exception cref="InvalidOperationException">If a rule with the same ID already exists.</exception>
    public void RegisterRule(InferenceRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!_rules.TryAdd(rule.RuleId, rule))
        {
            throw new InvalidOperationException($"Rule with ID '{rule.RuleId}' already exists.");
        }
    }

    /// <summary>
    /// Registers multiple rules at once.
    /// </summary>
    /// <param name="rules">The rules to register.</param>
    public void RegisterRules(IEnumerable<InferenceRule> rules)
    {
        foreach (var rule in rules)
        {
            RegisterRule(rule);
        }
    }

    /// <summary>
    /// Unregisters a rule by ID.
    /// </summary>
    /// <param name="ruleId">The ID of the rule to unregister.</param>
    /// <returns>True if the rule was removed, false if not found.</returns>
    public bool UnregisterRule(string ruleId)
    {
        return _rules.TryRemove(ruleId, out _);
    }

    /// <summary>
    /// Gets a rule by ID.
    /// </summary>
    /// <param name="ruleId">The rule ID.</param>
    /// <returns>The rule, or null if not found.</returns>
    public InferenceRule? GetRule(string ruleId)
    {
        return _rules.TryGetValue(ruleId, out var rule) ? rule : null;
    }

    /// <summary>
    /// Gets all registered rules.
    /// </summary>
    public IReadOnlyCollection<InferenceRule> GetAllRules()
    {
        return _rules.Values.ToArray();
    }

    /// <summary>
    /// Gets rules by category.
    /// </summary>
    /// <param name="category">The category to filter by.</param>
    public IEnumerable<InferenceRule> GetRulesByCategory(string category)
    {
        return _rules.Values.Where(r => r.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Enables a rule.
    /// </summary>
    /// <param name="ruleId">The rule ID.</param>
    /// <returns>True if the rule was enabled, false if not found.</returns>
    public bool EnableRule(string ruleId)
    {
        if (_rules.TryGetValue(ruleId, out var rule))
        {
            _rules[ruleId] = rule.WithEnabled(true);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Disables a rule.
    /// </summary>
    /// <param name="ruleId">The rule ID.</param>
    /// <returns>True if the rule was disabled, false if not found.</returns>
    public bool DisableRule(string ruleId)
    {
        if (_rules.TryGetValue(ruleId, out var rule))
        {
            _rules[ruleId] = rule.WithEnabled(false);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Evaluates all applicable rules against the given facts.
    /// </summary>
    /// <param name="facts">The facts to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All inferred knowledge from matching rules.</returns>
    public async Task<IReadOnlyList<InferredKnowledge>> EvaluateAsync(
        IEnumerable<InferenceFact> facts,
        CancellationToken ct = default)
    {
        var factList = facts.ToList();
        var factLookup = factList
            .GroupBy(f => f.FactType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var inferences = new List<InferredKnowledge>();

        await _evaluationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_statsLock) { _totalEvaluations++; }

            // Evaluate rules in priority order
            var orderedRules = _rules.Values
                .Where(r => r.IsEnabled)
                .OrderByDescending(r => r.Priority);

            foreach (var rule in orderedRules)
            {
                ct.ThrowIfCancellationRequested();

                var matchResult = EvaluateRule(rule, factLookup, factList);
                if (matchResult.IsMatch)
                {
                    var inference = CreateInference(rule, matchResult.MatchedFacts, matchResult.Confidence);
                    inferences.Add(inference);

                    lock (_statsLock)
                    {
                        _totalRulesFired++;
                        _totalInferencesProduced++;
                    }

                    OnRuleFired(new RuleFiredEventArgs
                    {
                        Rule = rule,
                        MatchedFacts = matchResult.MatchedFacts,
                        Inference = inference
                    });
                }
            }
        }
        finally
        {
            _evaluationLock.Release();
        }

        return inferences;
    }

    /// <summary>
    /// Gets engine statistics.
    /// </summary>
    public RuleEngineStatistics GetStatistics()
    {
        lock (_statsLock)
        {
            return new RuleEngineStatistics
            {
                TotalRules = _rules.Count,
                EnabledRules = _rules.Values.Count(r => r.IsEnabled),
                TotalEvaluations = _totalEvaluations,
                TotalRulesFired = _totalRulesFired,
                TotalInferencesProduced = _totalInferencesProduced
            };
        }
    }

    /// <summary>
    /// Resets statistics counters.
    /// </summary>
    public void ResetStatistics()
    {
        lock (_statsLock)
        {
            _totalEvaluations = 0;
            _totalRulesFired = 0;
            _totalInferencesProduced = 0;
        }
    }

    private (bool IsMatch, List<InferenceFact> MatchedFacts, double Confidence) EvaluateRule(
        InferenceRule rule,
        Dictionary<string, List<InferenceFact>> factLookup,
        List<InferenceFact> allFacts)
    {
        var matchedFacts = new List<InferenceFact>();
        var matchedCount = 0;
        var totalWeight = 0.0;
        var weightedConfidence = 0.0;

        foreach (var condition in rule.Conditions)
        {
            var conditionMet = EvaluateCondition(condition, factLookup, allFacts, out var matchedFact);
            if (conditionMet && matchedFact != null)
            {
                matchedFacts.Add(matchedFact);
                matchedCount++;
                totalWeight += condition.Weight;
                weightedConfidence += condition.Weight * matchedFact.Confidence;
            }
        }

        var requiredMatches = rule.MinConditionsToMatch == -1
            ? rule.Conditions.Count
            : rule.MinConditionsToMatch;

        var isMatch = matchedCount >= requiredMatches;
        var confidence = isMatch && totalWeight > 0
            ? (weightedConfidence / totalWeight) * rule.BaseConfidence
            : 0.0;

        return (isMatch, matchedFacts, confidence);
    }

    private bool EvaluateCondition(
        RuleCondition condition,
        Dictionary<string, List<InferenceFact>> factLookup,
        List<InferenceFact> allFacts,
        out InferenceFact? matchedFact)
    {
        matchedFact = null;

        // Handle existence checks
        if (condition.Operator == ConditionOperator.Exists)
        {
            if (factLookup.TryGetValue(condition.FactType, out var facts) && facts.Count > 0)
            {
                matchedFact = facts[0];
                return true;
            }
            return false;
        }

        if (condition.Operator == ConditionOperator.NotExists)
        {
            return !factLookup.ContainsKey(condition.FactType);
        }

        // Get the fact to evaluate
        if (!factLookup.TryGetValue(condition.FactType, out var matchFacts) || matchFacts.Count == 0)
        {
            return false;
        }

        var fact = matchFacts[0];
        var compareValue = condition.Value;

        // Handle relative comparisons
        if (!string.IsNullOrEmpty(condition.RelativeToFact))
        {
            if (!factLookup.TryGetValue(condition.RelativeToFact, out var relativeFacts) || relativeFacts.Count == 0)
            {
                return false;
            }
            compareValue = relativeFacts[0].Value;
        }

        var result = EvaluateOperator(condition.Operator, fact.Value, compareValue);
        if (result)
        {
            matchedFact = fact;
        }

        return result;
    }

    private static bool EvaluateOperator(ConditionOperator op, object? factValue, object? compareValue)
    {
        return op switch
        {
            ConditionOperator.Equals => Equals(factValue, compareValue),
            ConditionOperator.NotEquals => !Equals(factValue, compareValue),
            ConditionOperator.GreaterThan => Compare(factValue, compareValue) > 0,
            ConditionOperator.LessThan => Compare(factValue, compareValue) < 0,
            ConditionOperator.GreaterThanOrEqual => Compare(factValue, compareValue) >= 0,
            ConditionOperator.LessThanOrEqual => Compare(factValue, compareValue) <= 0,
            ConditionOperator.Contains => ContainsValue(factValue, compareValue),
            ConditionOperator.NotContains => !ContainsValue(factValue, compareValue),
            ConditionOperator.Matches => MatchesPattern(factValue, compareValue),
            ConditionOperator.After => CompareDateTime(factValue, compareValue) > 0,
            ConditionOperator.Before => CompareDateTime(factValue, compareValue) < 0,
            ConditionOperator.Deviates => CheckDeviation(factValue, compareValue),
            _ => false
        };
    }

    private static int Compare(object? a, object? b)
    {
        if (a is IComparable comparableA && b is IComparable comparableB)
        {
            try
            {
                return comparableA.CompareTo(Convert.ChangeType(comparableB, comparableA.GetType()));
            }
            catch
            {
                return 0;
            }
        }
        return 0;
    }

    private static int CompareDateTime(object? a, object? b)
    {
        if (TryParseDateTime(a, out var dtA) && TryParseDateTime(b, out var dtB))
        {
            return dtA.CompareTo(dtB);
        }
        return 0;
    }

    private static bool TryParseDateTime(object? value, out DateTimeOffset result)
    {
        result = default;
        if (value is DateTimeOffset dto)
        {
            result = dto;
            return true;
        }
        if (value is DateTime dt)
        {
            result = new DateTimeOffset(dt);
            return true;
        }
        if (value is string s && DateTimeOffset.TryParse(s, out result))
        {
            return true;
        }
        return false;
    }

    private static bool ContainsValue(object? container, object? value)
    {
        if (container is string s && value is string searchStr)
        {
            return s.Contains(searchStr, StringComparison.OrdinalIgnoreCase);
        }
        if (container is IEnumerable<object> enumerable)
        {
            return enumerable.Contains(value);
        }
        return false;
    }

    private static bool MatchesPattern(object? value, object? pattern)
    {
        if (value is string s && pattern is string regex)
        {
            try
            {
                return System.Text.RegularExpressions.Regex.IsMatch(s, regex);
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private static bool CheckDeviation(object? value, object? threshold)
    {
        // Deviation check - value represents how far from normal (e.g., z-score)
        if (value is double deviation && threshold is double thresh)
        {
            return Math.Abs(deviation) > thresh;
        }
        return false;
    }

    private InferredKnowledge CreateInference(
        InferenceRule rule,
        List<InferenceFact> matchedFacts,
        double confidence)
    {
        return new InferredKnowledge
        {
            InferenceId = Guid.NewGuid().ToString(),
            FactType = rule.Conclusion.InferredFactType,
            Value = rule.Conclusion.InferredValue,
            Confidence = Math.Clamp(confidence, 0.0, 1.0),
            Severity = rule.Conclusion.Severity,
            RuleId = rule.RuleId,
            SourceFacts = matchedFacts,
            RecommendedAction = rule.Conclusion.RecommendedAction,
            Explanation = CreateExplanation(rule, matchedFacts)
        };
    }

    private InferenceExplanation CreateExplanation(InferenceRule rule, List<InferenceFact> matchedFacts)
    {
        var steps = new List<ExplanationStep>
        {
            new ExplanationStep
            {
                StepNumber = 1,
                Description = $"Rule '{rule.Name}' was evaluated",
                Details = rule.Description
            }
        };

        for (int i = 0; i < matchedFacts.Count; i++)
        {
            var fact = matchedFacts[i];
            steps.Add(new ExplanationStep
            {
                StepNumber = i + 2,
                Description = $"Condition matched: {fact.FactType}",
                Details = $"Value: {fact.Value}, Confidence: {fact.Confidence:P0}"
            });
        }

        steps.Add(new ExplanationStep
        {
            StepNumber = steps.Count + 1,
            Description = $"Conclusion: {rule.Conclusion.InferredFactType}",
            Details = $"Inferred value: {rule.Conclusion.InferredValue}"
        });

        return new InferenceExplanation
        {
            RuleId = rule.RuleId,
            RuleName = rule.Name,
            Steps = steps,
            NaturalLanguage = GenerateNaturalLanguageExplanation(rule, matchedFacts)
        };
    }

    private string GenerateNaturalLanguageExplanation(InferenceRule rule, List<InferenceFact> matchedFacts)
    {
        var sb = new StringBuilder();
        sb.Append($"Based on the rule '{rule.Name}': ");

        var conditions = matchedFacts.Select(f => $"{f.FactType} = {f.Value}").ToList();
        if (conditions.Count == 1)
        {
            sb.Append($"Given that {conditions[0]}, ");
        }
        else if (conditions.Count > 1)
        {
            sb.Append("Given that ");
            sb.Append(string.Join(" and ", conditions));
            sb.Append(", ");
        }

        sb.Append($"we infer that {rule.Conclusion.InferredFactType}");
        if (rule.Conclusion.InferredValue != null)
        {
            sb.Append($" is {rule.Conclusion.InferredValue}");
        }
        sb.Append('.');

        if (!string.IsNullOrEmpty(rule.Conclusion.RecommendedAction))
        {
            sb.Append($" Recommended action: {rule.Conclusion.RecommendedAction}");
        }

        return sb.ToString();
    }

    private void OnRuleFired(RuleFiredEventArgs e)
    {
        RuleFired?.Invoke(this, e);
    }
}

/// <summary>
/// Event arguments for when a rule fires.
/// </summary>
public sealed class RuleFiredEventArgs : EventArgs
{
    /// <summary>The rule that fired.</summary>
    public required InferenceRule Rule { get; init; }

    /// <summary>The facts that matched the rule.</summary>
    public required IReadOnlyList<InferenceFact> MatchedFacts { get; init; }

    /// <summary>The inference produced.</summary>
    public required InferredKnowledge Inference { get; init; }
}

/// <summary>
/// Statistics for the rule engine.
/// </summary>
public sealed record RuleEngineStatistics
{
    /// <summary>Total number of registered rules.</summary>
    public int TotalRules { get; init; }

    /// <summary>Number of enabled rules.</summary>
    public int EnabledRules { get; init; }

    /// <summary>Total number of evaluations performed.</summary>
    public long TotalEvaluations { get; init; }

    /// <summary>Total number of times rules have fired.</summary>
    public long TotalRulesFired { get; init; }

    /// <summary>Total number of inferences produced.</summary>
    public long TotalInferencesProduced { get; init; }
}

/// <summary>
/// Registry for managing all active inference rules.
/// Provides centralized rule management with persistence support.
/// </summary>
public sealed class RuleRegistry : IDisposable
{
    private readonly BoundedDictionary<string, InferenceRule> _rules = new BoundedDictionary<string, InferenceRule>(1000);
    private readonly BoundedDictionary<string, RuleMetadata> _metadata = new BoundedDictionary<string, RuleMetadata>(1000);
    private readonly SemaphoreSlim _persistLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Event raised when a rule is registered.
    /// </summary>
    public event EventHandler<RuleRegistryEventArgs>? RuleRegistered;

    /// <summary>
    /// Event raised when a rule is unregistered.
    /// </summary>
    public event EventHandler<RuleRegistryEventArgs>? RuleUnregistered;

    /// <summary>
    /// Event raised when a rule is modified.
    /// </summary>
    public event EventHandler<RuleRegistryEventArgs>? RuleModified;

    /// <summary>
    /// Registers a rule in the registry.
    /// </summary>
    /// <param name="rule">The rule to register.</param>
    /// <param name="registeredBy">Who registered the rule.</param>
    public void Register(InferenceRule rule, string registeredBy = "system")
    {
        ArgumentNullException.ThrowIfNull(rule);

        _rules[rule.RuleId] = rule;
        _metadata[rule.RuleId] = new RuleMetadata
        {
            RuleId = rule.RuleId,
            RegisteredAt = DateTimeOffset.UtcNow,
            RegisteredBy = registeredBy,
            LastModifiedAt = DateTimeOffset.UtcNow,
            Version = 1,
            FireCount = 0
        };

        OnRuleRegistered(new RuleRegistryEventArgs { Rule = rule, Action = "Registered" });
    }

    /// <summary>
    /// Unregisters a rule from the registry.
    /// </summary>
    /// <param name="ruleId">The rule ID to unregister.</param>
    /// <returns>True if the rule was removed.</returns>
    public bool Unregister(string ruleId)
    {
        if (_rules.TryRemove(ruleId, out var rule))
        {
            _metadata.TryRemove(ruleId, out _);
            OnRuleUnregistered(new RuleRegistryEventArgs { Rule = rule, Action = "Unregistered" });
            return true;
        }
        return false;
    }

    /// <summary>
    /// Gets a rule by ID.
    /// </summary>
    public InferenceRule? Get(string ruleId)
    {
        return _rules.TryGetValue(ruleId, out var rule) ? rule : null;
    }

    /// <summary>
    /// Gets all registered rules.
    /// </summary>
    public IReadOnlyCollection<InferenceRule> GetAll()
    {
        return _rules.Values.ToArray();
    }

    /// <summary>
    /// Gets rules by category.
    /// </summary>
    public IEnumerable<InferenceRule> GetByCategory(string category)
    {
        return _rules.Values.Where(r => r.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets rules by tag.
    /// </summary>
    public IEnumerable<InferenceRule> GetByTag(string tag)
    {
        return _rules.Values.Where(r => r.Tags?.Contains(tag, StringComparer.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// Updates a rule.
    /// </summary>
    public bool Update(InferenceRule rule)
    {
        if (!_rules.ContainsKey(rule.RuleId))
        {
            return false;
        }

        _rules[rule.RuleId] = rule;

        if (_metadata.TryGetValue(rule.RuleId, out var meta))
        {
            _metadata[rule.RuleId] = meta with
            {
                LastModifiedAt = DateTimeOffset.UtcNow,
                Version = meta.Version + 1
            };
        }

        OnRuleModified(new RuleRegistryEventArgs { Rule = rule, Action = "Modified" });
        return true;
    }

    /// <summary>
    /// Records that a rule has fired.
    /// </summary>
    public void RecordFire(string ruleId)
    {
        if (_metadata.TryGetValue(ruleId, out var meta))
        {
            _metadata[ruleId] = meta with
            {
                FireCount = meta.FireCount + 1,
                LastFiredAt = DateTimeOffset.UtcNow
            };
        }
    }

    /// <summary>
    /// Gets metadata for a rule.
    /// </summary>
    public RuleMetadata? GetMetadata(string ruleId)
    {
        return _metadata.TryGetValue(ruleId, out var meta) ? meta : null;
    }

    /// <summary>
    /// Gets all categories.
    /// </summary>
    public IReadOnlyCollection<string> GetCategories()
    {
        return _rules.Values.Select(r => r.Category).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Gets registry statistics.
    /// </summary>
    public RuleRegistryStatistics GetStatistics()
    {
        var rules = _rules.Values.ToList();
        var metadata = _metadata.Values.ToList();

        return new RuleRegistryStatistics
        {
            TotalRules = rules.Count,
            EnabledRules = rules.Count(r => r.IsEnabled),
            DisabledRules = rules.Count(r => !r.IsEnabled),
            Categories = rules.Select(r => r.Category).Distinct().Count(),
            TotalFireCount = metadata.Sum(m => m.FireCount),
            MostFiredRuleId = metadata.OrderByDescending(m => m.FireCount).FirstOrDefault()?.RuleId
        };
    }

    private void OnRuleRegistered(RuleRegistryEventArgs e) => RuleRegistered?.Invoke(this, e);
    private void OnRuleUnregistered(RuleRegistryEventArgs e) => RuleUnregistered?.Invoke(this, e);
    private void OnRuleModified(RuleRegistryEventArgs e) => RuleModified?.Invoke(this, e);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _persistLock.Dispose();
    }
}

/// <summary>
/// Metadata about a registered rule.
/// </summary>
public sealed record RuleMetadata
{
    /// <summary>The rule ID.</summary>
    public required string RuleId { get; init; }

    /// <summary>When the rule was registered.</summary>
    public DateTimeOffset RegisteredAt { get; init; }

    /// <summary>Who registered the rule.</summary>
    public string RegisteredBy { get; init; } = "system";

    /// <summary>When the rule was last modified.</summary>
    public DateTimeOffset LastModifiedAt { get; init; }

    /// <summary>Rule version number.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Number of times the rule has fired.</summary>
    public long FireCount { get; init; }

    /// <summary>When the rule last fired.</summary>
    public DateTimeOffset? LastFiredAt { get; init; }
}

/// <summary>
/// Event arguments for rule registry events.
/// </summary>
public sealed class RuleRegistryEventArgs : EventArgs
{
    /// <summary>The affected rule.</summary>
    public required InferenceRule Rule { get; init; }

    /// <summary>The action performed.</summary>
    public required string Action { get; init; }
}

/// <summary>
/// Statistics for the rule registry.
/// </summary>
public sealed record RuleRegistryStatistics
{
    /// <summary>Total number of registered rules.</summary>
    public int TotalRules { get; init; }

    /// <summary>Number of enabled rules.</summary>
    public int EnabledRules { get; init; }

    /// <summary>Number of disabled rules.</summary>
    public int DisabledRules { get; init; }

    /// <summary>Number of distinct categories.</summary>
    public int Categories { get; init; }

    /// <summary>Total fire count across all rules.</summary>
    public long TotalFireCount { get; init; }

    /// <summary>ID of the most frequently fired rule.</summary>
    public string? MostFiredRuleId { get; init; }
}

/// <summary>
/// Core inference processor that orchestrates rule evaluation, caching, and explanation.
/// Thread-safe implementation for production use.
/// </summary>
public sealed class InferenceEngine : IDisposable
{
    private readonly RuleEngine _ruleEngine;
    private readonly RuleRegistry _ruleRegistry;
    private readonly InferenceCache _cache;
    private readonly ConfidenceScoring _confidenceScoring;
    private readonly SemaphoreSlim _inferLock = new(1, 1);
    private readonly object _statsLock = new();
    private long _totalInferences;
    private long _cacheHits;
    private long _cacheMisses;
    private bool _disposed;

    /// <summary>
    /// Event raised when knowledge is inferred.
    /// </summary>
    public event EventHandler<KnowledgeInferredEventArgs>? KnowledgeInferred;

    /// <summary>
    /// Event raised when cached inference is invalidated.
    /// </summary>
    public event EventHandler<InferenceInvalidatedEventArgs>? InferenceInvalidated;

    /// <summary>
    /// Creates a new inference engine.
    /// </summary>
    /// <param name="cacheTtl">Default TTL for cached inferences.</param>
    public InferenceEngine(TimeSpan? cacheTtl = null)
    {
        _ruleEngine = new RuleEngine();
        _ruleRegistry = new RuleRegistry();
        _cache = new InferenceCache(cacheTtl ?? TimeSpan.FromMinutes(15));
        _confidenceScoring = new ConfidenceScoring();

        // Wire up rule engine events
        _ruleEngine.RuleFired += OnRuleEngineFired;

        // Register built-in rules
        RegisterBuiltInRules();
    }

    /// <summary>
    /// Gets the rule engine.
    /// </summary>
    public RuleEngine RuleEngine => _ruleEngine;

    /// <summary>
    /// Gets the rule registry.
    /// </summary>
    public RuleRegistry RuleRegistry => _ruleRegistry;

    /// <summary>
    /// Gets the inference cache.
    /// </summary>
    public InferenceCache Cache => _cache;

    /// <summary>
    /// Gets the confidence scoring component.
    /// </summary>
    public ConfidenceScoring ConfidenceScoring => _confidenceScoring;

    /// <summary>
    /// Performs inference on the given facts.
    /// </summary>
    /// <param name="facts">The facts to infer from.</param>
    /// <param name="options">Inference options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Inferred knowledge.</returns>
    public async Task<InferenceResult> InferAsync(
        IEnumerable<InferenceFact> facts,
        InferenceOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new InferenceOptions();
        var factList = facts.ToList();
        var cacheKey = GenerateCacheKey(factList);

        // Check cache first
        if (options.UseCache && _cache.TryGet(cacheKey, out var cached))
        {
            lock (_statsLock) { _cacheHits++; }
            return new InferenceResult
            {
                Inferences = cached!,
                FromCache = true,
                CacheKey = cacheKey
            };
        }

        lock (_statsLock) { _cacheMisses++; }

        await _inferLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_statsLock) { _totalInferences++; }

            // Evaluate rules
            var inferences = await _ruleEngine.EvaluateAsync(factList, ct).ConfigureAwait(false);

            // Apply confidence scoring adjustments
            var adjustedInferences = inferences.Select(i =>
            {
                var adjustment = _confidenceScoring.CalculateAdjustment(i, factList);
                return new InferredKnowledge
                {
                    InferenceId = i.InferenceId,
                    FactType = i.FactType,
                    Value = i.Value,
                    Confidence = Math.Clamp(i.Confidence * adjustment.Factor, 0.0, 1.0),
                    Severity = i.Severity,
                    RuleId = i.RuleId,
                    SourceFacts = i.SourceFacts,
                    InferredAt = i.InferredAt,
                    ExpiresAt = options.InferenceTtl.HasValue
                        ? DateTimeOffset.UtcNow.Add(options.InferenceTtl.Value)
                        : null,
                    RecommendedAction = i.RecommendedAction,
                    Explanation = i.Explanation,
                    Metadata = i.Metadata
                };
            }).ToList();

            // Filter by minimum confidence
            if (options.MinConfidence > 0)
            {
                adjustedInferences = adjustedInferences
                    .Where(i => i.Confidence >= options.MinConfidence)
                    .ToList();
            }

            // Cache results
            if (options.UseCache && adjustedInferences.Count > 0)
            {
                _cache.Set(cacheKey, adjustedInferences, options.InferenceTtl);
            }

            // Record fires in registry
            foreach (var inference in adjustedInferences)
            {
                _ruleRegistry.RecordFire(inference.RuleId);
            }

            // Raise events
            foreach (var inference in adjustedInferences)
            {
                OnKnowledgeInferred(new KnowledgeInferredEventArgs
                {
                    Inference = inference,
                    Facts = factList
                });
            }

            return new InferenceResult
            {
                Inferences = adjustedInferences,
                FromCache = false,
                CacheKey = cacheKey,
                EvaluatedRules = _ruleEngine.GetStatistics().TotalRules
            };
        }
        finally
        {
            _inferLock.Release();
        }
    }

    /// <summary>
    /// Gets an explanation for how specific knowledge was inferred.
    /// </summary>
    /// <param name="inferenceId">The inference ID.</param>
    /// <returns>The explanation, or null if not found.</returns>
    public InferenceExplanation? GetExplanation(string inferenceId)
    {
        // Search cache for the inference
        var cached = _cache.GetAll()
            .SelectMany(kvp => kvp.Value)
            .FirstOrDefault(i => i.InferenceId == inferenceId);

        return cached?.Explanation;
    }

    /// <summary>
    /// Invalidates inferences related to specific source facts.
    /// </summary>
    /// <param name="sourceIds">The source IDs to invalidate.</param>
    public void InvalidateBySource(IEnumerable<string> sourceIds)
    {
        var sourceSet = sourceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invalidated = new List<InferredKnowledge>();

        foreach (var kvp in _cache.GetAll())
        {
            var toInvalidate = kvp.Value
                .Where(i => i.SourceFacts.Any(f => sourceSet.Contains(f.SourceId)))
                .ToList();

            if (toInvalidate.Count > 0)
            {
                invalidated.AddRange(toInvalidate);
                _cache.Invalidate(kvp.Key);
            }
        }

        foreach (var inference in invalidated)
        {
            OnInferenceInvalidated(new InferenceInvalidatedEventArgs
            {
                Inference = inference,
                Reason = "Source data changed"
            });
        }
    }

    /// <summary>
    /// Gets engine statistics.
    /// </summary>
    public InferenceEngineStatistics GetStatistics()
    {
        lock (_statsLock)
        {
            return new InferenceEngineStatistics
            {
                TotalInferences = _totalInferences,
                CacheHits = _cacheHits,
                CacheMisses = _cacheMisses,
                CacheHitRate = _cacheHits + _cacheMisses > 0
                    ? (double)_cacheHits / (_cacheHits + _cacheMisses)
                    : 0,
                RuleEngineStats = _ruleEngine.GetStatistics(),
                RegistryStats = _ruleRegistry.GetStatistics(),
                CacheStats = _cache.GetStatistics()
            };
        }
    }

    private void RegisterBuiltInRules()
    {
        // Register all built-in inference rules
        var builtInRules = BuiltInRules.GetAllRules();
        foreach (var rule in builtInRules)
        {
            _ruleRegistry.Register(rule, "system");
            _ruleEngine.RegisterRule(rule);
        }
    }

    private static string GenerateCacheKey(IEnumerable<InferenceFact> facts)
    {
        var ordered = facts
            .OrderBy(f => f.FactType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.SourceId, StringComparer.OrdinalIgnoreCase);

        var combined = string.Join("|", ordered.Select(f => $"{f.FactType}:{f.Value}:{f.SourceId}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hash)[..32];
    }

    private void OnRuleEngineFired(object? sender, RuleFiredEventArgs e)
    {
        _ruleRegistry.RecordFire(e.Rule.RuleId);
    }

    private void OnKnowledgeInferred(KnowledgeInferredEventArgs e)
    {
        KnowledgeInferred?.Invoke(this, e);
    }

    private void OnInferenceInvalidated(InferenceInvalidatedEventArgs e)
    {
        InferenceInvalidated?.Invoke(this, e);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _ruleEngine.RuleFired -= OnRuleEngineFired;
        _inferLock.Dispose();
        _ruleRegistry.Dispose();
        _cache.Dispose();
    }
}

/// <summary>
/// Options for inference operations.
/// </summary>
public sealed class InferenceOptions
{
    /// <summary>
    /// Gets or sets whether to use the cache.
    /// </summary>
    public bool UseCache { get; init; } = true;

    /// <summary>
    /// Gets or sets the minimum confidence threshold for results.
    /// </summary>
    public double MinConfidence { get; init; } = 0.0;

    /// <summary>
    /// Gets or sets the TTL for inferred knowledge.
    /// </summary>
    public TimeSpan? InferenceTtl { get; init; }

    /// <summary>
    /// Gets or sets whether to include explanations.
    /// </summary>
    public bool IncludeExplanations { get; init; } = true;

    /// <summary>
    /// Gets or sets categories to limit evaluation to.
    /// </summary>
    public IReadOnlyList<string>? Categories { get; init; }
}

/// <summary>
/// Result of an inference operation.
/// </summary>
public sealed class InferenceResult
{
    /// <summary>
    /// Gets the inferred knowledge.
    /// </summary>
    public required IReadOnlyList<InferredKnowledge> Inferences { get; init; }

    /// <summary>
    /// Gets whether the result came from cache.
    /// </summary>
    public bool FromCache { get; init; }

    /// <summary>
    /// Gets the cache key used.
    /// </summary>
    public string? CacheKey { get; init; }

    /// <summary>
    /// Gets the number of rules evaluated.
    /// </summary>
    public int EvaluatedRules { get; init; }
}

/// <summary>
/// Event arguments for knowledge inferred events.
/// </summary>
public sealed class KnowledgeInferredEventArgs : EventArgs
{
    /// <summary>The inferred knowledge.</summary>
    public required InferredKnowledge Inference { get; init; }

    /// <summary>The facts used for inference.</summary>
    public required IReadOnlyList<InferenceFact> Facts { get; init; }
}

/// <summary>
/// Event arguments for inference invalidation events.
/// </summary>
public sealed class InferenceInvalidatedEventArgs : EventArgs
{
    /// <summary>The invalidated inference.</summary>
    public required InferredKnowledge Inference { get; init; }

    /// <summary>The reason for invalidation.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Statistics for the inference engine.
/// </summary>
public sealed record InferenceEngineStatistics
{
    /// <summary>Total inference operations.</summary>
    public long TotalInferences { get; init; }

    /// <summary>Cache hits.</summary>
    public long CacheHits { get; init; }

    /// <summary>Cache misses.</summary>
    public long CacheMisses { get; init; }

    /// <summary>Cache hit rate (0.0 - 1.0).</summary>
    public double CacheHitRate { get; init; }

    /// <summary>Rule engine statistics.</summary>
    public RuleEngineStatistics? RuleEngineStats { get; init; }

    /// <summary>Registry statistics.</summary>
    public RuleRegistryStatistics? RegistryStats { get; init; }

    /// <summary>Cache statistics.</summary>
    public InferenceCacheStatistics? CacheStats { get; init; }
}

#endregion

#region G2: Built-in Rules

/// <summary>
/// Provides built-in inference rules for common scenarios.
/// </summary>
public static class BuiltInRules
{
    /// <summary>
    /// Gets all built-in inference rules.
    /// </summary>
    public static IEnumerable<InferenceRule> GetAllRules()
    {
        yield return StalenessInference.Rule;
        yield return CapacityInference.Rule;
        yield return RiskInference.Rule;
        yield return AnomalyInference.Rule;

        // Additional common rules
        yield return PerformanceDegradationRule;
        yield return DataQualityRule;
        yield return SecurityRiskRule;
        yield return ResourceExhaustionRule;
    }

    /// <summary>
    /// Rule for detecting performance degradation.
    /// </summary>
    public static readonly InferenceRule PerformanceDegradationRule = new(
        RuleId: "builtin.performance.degradation",
        Name: "Performance Degradation Detection",
        Description: "Detects when system performance has degraded beyond acceptable thresholds",
        Category: "Performance",
        Conditions: new[]
        {
            new RuleCondition("performance.latency.current", ConditionOperator.GreaterThan, 0.0)
            {
                RelativeToFact = "performance.latency.baseline",
                Weight = 1.5
            },
            new RuleCondition("performance.throughput.current", ConditionOperator.LessThan, 0.0)
            {
                RelativeToFact = "performance.throughput.baseline",
                Weight = 1.0
            }
        },
        Conclusion: new RuleConclusion("performance.status", "degraded")
        {
            Severity = InferenceSeverity.Medium,
            RecommendedAction = "Investigate system performance and consider scaling resources"
        },
        BaseConfidence: 0.85,
        Priority: 80,
        Tags: new[] { "performance", "monitoring", "health" }
    );

    /// <summary>
    /// Rule for detecting data quality issues.
    /// </summary>
    public static readonly InferenceRule DataQualityRule = new(
        RuleId: "builtin.data.quality",
        Name: "Data Quality Issue Detection",
        Description: "Detects potential data quality issues based on validation failures and anomalies",
        Category: "DataQuality",
        Conditions: new[]
        {
            new RuleCondition("data.validation.failure.count", ConditionOperator.GreaterThan, 5) { Weight = 2.0 },
            new RuleCondition("data.null.ratio", ConditionOperator.GreaterThan, 0.1) { Weight = 1.0 }
        },
        Conclusion: new RuleConclusion("data.quality.status", "compromised")
        {
            Severity = InferenceSeverity.High,
            RecommendedAction = "Review data sources and validation rules"
        },
        BaseConfidence: 0.75,
        Priority: 90,
        Tags: new[] { "data", "quality", "validation" }
    )
    { MinConditionsToMatch = 1 }; // OR logic - either condition triggers

    /// <summary>
    /// Rule for detecting security risks.
    /// </summary>
    public static readonly InferenceRule SecurityRiskRule = new(
        RuleId: "builtin.security.risk",
        Name: "Security Risk Detection",
        Description: "Detects potential security risks based on access patterns and configurations",
        Category: "Security",
        Conditions: new[]
        {
            new RuleCondition("security.failed.auth.count", ConditionOperator.GreaterThan, 10) { Weight = 2.0 },
            new RuleCondition("security.unusual.access.pattern", ConditionOperator.Equals, true) { Weight = 1.5 }
        },
        Conclusion: new RuleConclusion("security.risk.level", "elevated")
        {
            Severity = InferenceSeverity.Critical,
            RecommendedAction = "Review security logs and consider implementing additional controls"
        },
        BaseConfidence: 0.9,
        Priority: 100,
        Tags: new[] { "security", "risk", "compliance" }
    )
    { MinConditionsToMatch = 1 };

    /// <summary>
    /// Rule for detecting resource exhaustion.
    /// </summary>
    public static readonly InferenceRule ResourceExhaustionRule = new(
        RuleId: "builtin.resource.exhaustion",
        Name: "Resource Exhaustion Prediction",
        Description: "Predicts when resources will be exhausted based on usage trends",
        Category: "Capacity",
        Conditions: new[]
        {
            new RuleCondition("resource.usage.percent", ConditionOperator.GreaterThan, 85) { Weight = 2.0 },
            new RuleCondition("resource.usage.trend", ConditionOperator.Equals, "increasing") { Weight = 1.0 }
        },
        Conclusion: new RuleConclusion("resource.exhaustion.imminent", true)
        {
            Severity = InferenceSeverity.High,
            RecommendedAction = "Plan capacity expansion or optimize resource usage"
        },
        BaseConfidence: 0.8,
        Priority: 95,
        Tags: new[] { "resources", "capacity", "prediction" }
    );
}

/// <summary>
/// Staleness inference: "File modified after backup = stale backup"
/// </summary>
public static class StalenessInference
{
    /// <summary>
    /// The staleness inference rule.
    /// </summary>
    public static readonly InferenceRule Rule = new(
        RuleId: "builtin.staleness.backup",
        Name: "Stale Backup Detection",
        Description: "Detects when a file has been modified after its last backup, indicating the backup is stale",
        Category: "Staleness",
        Conditions: new[]
        {
            new RuleCondition("file.modified.timestamp", ConditionOperator.Exists) { Weight = 1.0 },
            new RuleCondition("backup.timestamp", ConditionOperator.Exists) { Weight = 1.0 },
            new RuleCondition("file.modified.timestamp", ConditionOperator.After, null)
            {
                RelativeToFact = "backup.timestamp",
                Weight = 2.0
            }
        },
        Conclusion: new RuleConclusion("backup.status", "stale")
        {
            Severity = InferenceSeverity.Medium,
            RecommendedAction = "Schedule a new backup to ensure data is protected",
            Metadata = new Dictionary<string, object>
            {
                ["risk_factor"] = "data_loss",
                ["category"] = "backup"
            }
        },
        BaseConfidence: 0.95,
        Priority: 85,
        Tags: new[] { "backup", "staleness", "data-protection" }
    );

    /// <summary>
    /// Creates facts for staleness detection.
    /// </summary>
    /// <param name="fileId">The file identifier.</param>
    /// <param name="fileModified">When the file was last modified.</param>
    /// <param name="backupTimestamp">When the last backup was taken.</param>
    /// <returns>Facts for inference.</returns>
    public static IEnumerable<InferenceFact> CreateFacts(
        string fileId,
        DateTimeOffset fileModified,
        DateTimeOffset backupTimestamp)
    {
        yield return new InferenceFact(
            "file.modified.timestamp",
            fileModified,
            fileId,
            DateTimeOffset.UtcNow);

        yield return new InferenceFact(
            "backup.timestamp",
            backupTimestamp,
            fileId,
            DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// Capacity inference: "Usage trend + growth = future capacity"
/// </summary>
public static class CapacityInference
{
    /// <summary>
    /// The capacity inference rule.
    /// </summary>
    public static readonly InferenceRule Rule = new(
        RuleId: "builtin.capacity.prediction",
        Name: "Capacity Exhaustion Prediction",
        Description: "Predicts future capacity issues based on current usage and growth trends",
        Category: "Capacity",
        Conditions: new[]
        {
            new RuleCondition("storage.usage.percent", ConditionOperator.GreaterThan, 70) { Weight = 1.5 },
            new RuleCondition("storage.growth.rate.percent", ConditionOperator.GreaterThan, 5) { Weight = 1.0 },
            new RuleCondition("storage.days.to.full", ConditionOperator.LessThan, 30) { Weight = 2.0 }
        },
        Conclusion: new RuleConclusion("capacity.action.required", true)
        {
            Severity = InferenceSeverity.High,
            RecommendedAction = "Plan storage expansion or implement data retention policies",
            Metadata = new Dictionary<string, object>
            {
                ["planning_horizon_days"] = 30,
                ["category"] = "capacity"
            }
        },
        BaseConfidence: 0.85,
        Priority: 90,
        Tags: new[] { "capacity", "storage", "prediction", "planning" }
    )
    { MinConditionsToMatch = 2 }; // At least 2 of 3 conditions

    /// <summary>
    /// Creates facts for capacity prediction.
    /// </summary>
    /// <param name="sourceId">The source identifier.</param>
    /// <param name="usagePercent">Current usage percentage.</param>
    /// <param name="growthRatePercent">Growth rate percentage per period.</param>
    /// <param name="daysToFull">Estimated days until full.</param>
    /// <returns>Facts for inference.</returns>
    public static IEnumerable<InferenceFact> CreateFacts(
        string sourceId,
        double usagePercent,
        double growthRatePercent,
        int daysToFull)
    {
        yield return new InferenceFact(
            "storage.usage.percent",
            usagePercent,
            sourceId,
            DateTimeOffset.UtcNow);

        yield return new InferenceFact(
            "storage.growth.rate.percent",
            growthRatePercent,
            sourceId,
            DateTimeOffset.UtcNow);

        yield return new InferenceFact(
            "storage.days.to.full",
            daysToFull,
            sourceId,
            DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// Risk inference: "No backup + critical file = high risk"
/// </summary>
public static class RiskInference
{
    /// <summary>
    /// The risk inference rule.
    /// </summary>
    public static readonly InferenceRule Rule = new(
        RuleId: "builtin.risk.unprotected-critical",
        Name: "Unprotected Critical Data Risk",
        Description: "Identifies high-risk situations where critical files have no recent backup",
        Category: "Risk",
        Conditions: new[]
        {
            new RuleCondition("file.criticality", ConditionOperator.Equals, "critical") { Weight = 2.0 },
            new RuleCondition("backup.recent", ConditionOperator.Equals, false) { Weight = 2.0 },
            new RuleCondition("file.modified.recently", ConditionOperator.Equals, true) { Weight = 1.0 }
        },
        Conclusion: new RuleConclusion("data.risk.level", "high")
        {
            Severity = InferenceSeverity.Critical,
            RecommendedAction = "Immediately backup critical files to prevent potential data loss",
            Metadata = new Dictionary<string, object>
            {
                ["risk_type"] = "data_loss",
                ["urgency"] = "immediate",
                ["category"] = "risk"
            }
        },
        BaseConfidence: 0.9,
        Priority: 100,
        Tags: new[] { "risk", "backup", "critical", "data-protection" }
    )
    { MinConditionsToMatch = 2 }; // Critical + no backup is enough

    /// <summary>
    /// Creates facts for risk detection.
    /// </summary>
    /// <param name="fileId">The file identifier.</param>
    /// <param name="criticality">File criticality level.</param>
    /// <param name="hasRecentBackup">Whether file has recent backup.</param>
    /// <param name="modifiedRecently">Whether file was modified recently.</param>
    /// <returns>Facts for inference.</returns>
    public static IEnumerable<InferenceFact> CreateFacts(
        string fileId,
        string criticality,
        bool hasRecentBackup,
        bool modifiedRecently)
    {
        yield return new InferenceFact(
            "file.criticality",
            criticality,
            fileId,
            DateTimeOffset.UtcNow);

        yield return new InferenceFact(
            "backup.recent",
            hasRecentBackup,
            fileId,
            DateTimeOffset.UtcNow);

        yield return new InferenceFact(
            "file.modified.recently",
            modifiedRecently,
            fileId,
            DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// Anomaly inference: "Pattern deviation = potential issue"
/// </summary>
public static class AnomalyInference
{
    /// <summary>
    /// The anomaly inference rule.
    /// </summary>
    public static readonly InferenceRule Rule = new(
        RuleId: "builtin.anomaly.pattern-deviation",
        Name: "Pattern Deviation Detection",
        Description: "Detects when observed patterns deviate significantly from established baselines",
        Category: "Anomaly",
        Conditions: new[]
        {
            new RuleCondition("pattern.deviation.zscore", ConditionOperator.Deviates, 2.0) { Weight = 2.0 },
            new RuleCondition("pattern.baseline.established", ConditionOperator.Equals, true) { Weight = 1.0 }
        },
        Conclusion: new RuleConclusion("anomaly.detected", true)
        {
            Severity = InferenceSeverity.Medium,
            RecommendedAction = "Investigate the deviation to determine if it indicates a problem or new pattern",
            Metadata = new Dictionary<string, object>
            {
                ["detection_method"] = "statistical",
                ["category"] = "anomaly"
            }
        },
        BaseConfidence: 0.8,
        Priority: 75,
        Tags: new[] { "anomaly", "pattern", "detection", "monitoring" }
    );

    /// <summary>
    /// Creates facts for anomaly detection.
    /// </summary>
    /// <param name="sourceId">The source identifier.</param>
    /// <param name="deviationZScore">The z-score of the deviation.</param>
    /// <param name="baselineEstablished">Whether a baseline has been established.</param>
    /// <param name="observedValue">The observed value.</param>
    /// <param name="expectedValue">The expected value.</param>
    /// <returns>Facts for inference.</returns>
    public static IEnumerable<InferenceFact> CreateFacts(
        string sourceId,
        double deviationZScore,
        bool baselineEstablished,
        double? observedValue = null,
        double? expectedValue = null)
    {
        yield return new InferenceFact(
            "pattern.deviation.zscore",
            deviationZScore,
            sourceId,
            DateTimeOffset.UtcNow);

        yield return new InferenceFact(
            "pattern.baseline.established",
            baselineEstablished,
            sourceId,
            DateTimeOffset.UtcNow);

        if (observedValue.HasValue)
        {
            yield return new InferenceFact(
                "pattern.observed.value",
                observedValue.Value,
                sourceId,
                DateTimeOffset.UtcNow);
        }

        if (expectedValue.HasValue)
        {
            yield return new InferenceFact(
                "pattern.expected.value",
                expectedValue.Value,
                sourceId,
                DateTimeOffset.UtcNow);
        }
    }
}

#endregion

#region G3: Inference Management

/// <summary>
/// Cache for inferred knowledge with TTL support.
/// Thread-safe implementation with automatic expiration.
/// </summary>
public sealed class InferenceCache : IDisposable
{
    private readonly BoundedDictionary<string, CacheEntry> _cache = new BoundedDictionary<string, CacheEntry>(1000);
    private readonly TimeSpan _defaultTtl;
    private readonly Timer _cleanupTimer;
    private readonly object _statsLock = new();
    private long _totalSets;
    private long _totalGets;
    private long _totalEvictions;
    private bool _disposed;

    /// <summary>
    /// Creates a new inference cache.
    /// </summary>
    /// <param name="defaultTtl">Default TTL for cache entries.</param>
    public InferenceCache(TimeSpan defaultTtl)
    {
        _defaultTtl = defaultTtl;
        _cleanupTimer = new Timer(
            CleanupExpired,
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Attempts to get cached inferences.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="inferences">The cached inferences, if found.</param>
    /// <returns>True if found and not expired.</returns>
    public bool TryGet(string key, out IReadOnlyList<InferredKnowledge>? inferences)
    {
        lock (_statsLock) { _totalGets++; }

        if (_cache.TryGetValue(key, out var entry) && !entry.IsExpired)
        {
            inferences = entry.Inferences;
            return true;
        }

        inferences = null;
        return false;
    }

    /// <summary>
    /// Sets cached inferences.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="inferences">The inferences to cache.</param>
    /// <param name="ttl">Optional TTL override.</param>
    public void Set(string key, IReadOnlyList<InferredKnowledge> inferences, TimeSpan? ttl = null)
    {
        lock (_statsLock) { _totalSets++; }

        _cache[key] = new CacheEntry
        {
            Key = key,
            Inferences = inferences,
            CachedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(ttl ?? _defaultTtl)
        };
    }

    /// <summary>
    /// Invalidates a cache entry.
    /// </summary>
    /// <param name="key">The cache key to invalidate.</param>
    /// <returns>True if the entry was found and removed.</returns>
    public bool Invalidate(string key)
    {
        if (_cache.TryRemove(key, out _))
        {
            lock (_statsLock) { _totalEvictions++; }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Clears all cache entries.
    /// </summary>
    public void Clear()
    {
        var count = _cache.Count;
        _cache.Clear();
        lock (_statsLock) { _totalEvictions += count; }
    }

    /// <summary>
    /// Gets all cached entries.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<InferredKnowledge>> GetAll()
    {
        return _cache
            .Where(kvp => !kvp.Value.IsExpired)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Inferences);
    }

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    public InferenceCacheStatistics GetStatistics()
    {
        lock (_statsLock)
        {
            return new InferenceCacheStatistics
            {
                EntryCount = _cache.Count,
                TotalSets = _totalSets,
                TotalGets = _totalGets,
                TotalEvictions = _totalEvictions,
                TotalInferences = _cache.Values.Sum(e => e.Inferences.Count)
            };
        }
    }

    private void CleanupExpired(object? state)
    {
        var expired = _cache.Where(kvp => kvp.Value.IsExpired).Select(kvp => kvp.Key).ToList();
        foreach (var key in expired)
        {
            if (_cache.TryRemove(key, out _))
            {
                lock (_statsLock) { _totalEvictions++; }
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanupTimer.Dispose();
    }

    private sealed record CacheEntry
    {
        public required string Key { get; init; }
        public required IReadOnlyList<InferredKnowledge> Inferences { get; init; }
        public DateTimeOffset CachedAt { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;
    }
}

/// <summary>
/// Statistics for the inference cache.
/// </summary>
public sealed record InferenceCacheStatistics
{
    /// <summary>Current number of entries.</summary>
    public int EntryCount { get; init; }

    /// <summary>Total number of set operations.</summary>
    public long TotalSets { get; init; }

    /// <summary>Total number of get operations.</summary>
    public long TotalGets { get; init; }

    /// <summary>Total number of evictions.</summary>
    public long TotalEvictions { get; init; }

    /// <summary>Total number of inferences across all entries.</summary>
    public int TotalInferences { get; init; }
}

/// <summary>
/// Manages invalidation of inferences when source data changes.
/// </summary>
public sealed class InferenceInvalidation
{
    private readonly InferenceCache _cache;
    private readonly BoundedDictionary<string, HashSet<string>> _sourceToKeys = new BoundedDictionary<string, HashSet<string>>(1000);
    private readonly object _mappingLock = new();

    /// <summary>
    /// Event raised when inferences are invalidated.
    /// </summary>
    public event EventHandler<InvalidationEventArgs>? OnInvalidation;

    /// <summary>
    /// Creates a new invalidation manager.
    /// </summary>
    /// <param name="cache">The cache to manage.</param>
    public InferenceInvalidation(InferenceCache cache)
    {
        _cache = cache;
    }

    /// <summary>
    /// Registers a mapping from source IDs to cache key.
    /// </summary>
    /// <param name="cacheKey">The cache key.</param>
    /// <param name="sourceIds">The source IDs that contribute to this cache entry.</param>
    public void RegisterMapping(string cacheKey, IEnumerable<string> sourceIds)
    {
        lock (_mappingLock)
        {
            foreach (var sourceId in sourceIds)
            {
                if (!_sourceToKeys.TryGetValue(sourceId, out var keys))
                {
                    keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _sourceToKeys[sourceId] = keys;
                }
                keys.Add(cacheKey);
            }
        }
    }

    /// <summary>
    /// Invalidates all inferences that depend on the given source.
    /// </summary>
    /// <param name="sourceId">The source ID that changed.</param>
    /// <param name="reason">The reason for invalidation.</param>
    /// <returns>Number of cache entries invalidated.</returns>
    public int InvalidateBySource(string sourceId, string reason = "Source data changed")
    {
        var invalidated = 0;

        lock (_mappingLock)
        {
            if (_sourceToKeys.TryGetValue(sourceId, out var keys))
            {
                foreach (var key in keys)
                {
                    if (_cache.Invalidate(key))
                    {
                        invalidated++;
                    }
                }

                OnInvalidation?.Invoke(this, new InvalidationEventArgs
                {
                    SourceId = sourceId,
                    Reason = reason,
                    InvalidatedCount = invalidated
                });

                _sourceToKeys.TryRemove(sourceId, out _);
            }
        }

        return invalidated;
    }

    /// <summary>
    /// Invalidates all inferences that match a predicate.
    /// </summary>
    /// <param name="predicate">Predicate to match source IDs.</param>
    /// <param name="reason">The reason for invalidation.</param>
    /// <returns>Total number of cache entries invalidated.</returns>
    public int InvalidateWhere(Func<string, bool> predicate, string reason = "Bulk invalidation")
    {
        var total = 0;

        lock (_mappingLock)
        {
            var matching = _sourceToKeys.Keys.Where(predicate).ToList();
            foreach (var sourceId in matching)
            {
                total += InvalidateBySource(sourceId, reason);
            }
        }

        return total;
    }
}

/// <summary>
/// Event arguments for invalidation events.
/// </summary>
public sealed class InvalidationEventArgs : EventArgs
{
    /// <summary>The source ID that triggered invalidation.</summary>
    public required string SourceId { get; init; }

    /// <summary>The reason for invalidation.</summary>
    public required string Reason { get; init; }

    /// <summary>Number of entries invalidated.</summary>
    public int InvalidatedCount { get; init; }
}

/// <summary>
/// Provides explanations for how knowledge was inferred.
/// </summary>
public sealed class InferenceExplanation
{
    /// <summary>
    /// Gets the rule ID that produced this inference.
    /// </summary>
    public required string RuleId { get; init; }

    /// <summary>
    /// Gets the rule name.
    /// </summary>
    public required string RuleName { get; init; }

    /// <summary>
    /// Gets the step-by-step explanation.
    /// </summary>
    public required IReadOnlyList<ExplanationStep> Steps { get; init; }

    /// <summary>
    /// Gets the natural language explanation.
    /// </summary>
    public required string NaturalLanguage { get; init; }

    /// <summary>
    /// Gets the timestamp of the explanation.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A single step in an inference explanation.
/// </summary>
public sealed record ExplanationStep
{
    /// <summary>
    /// Gets the step number.
    /// </summary>
    public required int StepNumber { get; init; }

    /// <summary>
    /// Gets the step description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets additional details.
    /// </summary>
    public string? Details { get; init; }
}

/// <summary>
/// Calculates and adjusts confidence scores for inferences.
/// </summary>
public sealed class ConfidenceScoring
{
    private readonly BoundedDictionary<string, ConfidenceHistory> _history = new BoundedDictionary<string, ConfidenceHistory>(1000);

    /// <summary>
    /// Calculates a confidence adjustment based on various factors.
    /// </summary>
    /// <param name="inference">The inference to adjust.</param>
    /// <param name="sourceFacts">The source facts.</param>
    /// <returns>Confidence adjustment.</returns>
    public ConfidenceAdjustment CalculateAdjustment(
        InferredKnowledge inference,
        IEnumerable<InferenceFact> sourceFacts)
    {
        var factors = new List<(string Name, double Factor)>();
        var totalFactor = 1.0;

        // Factor 1: Source fact confidence (average)
        var avgSourceConfidence = inference.SourceFacts.Average(f => f.Confidence);
        var sourceConfidenceFactor = 0.5 + (avgSourceConfidence * 0.5); // Range 0.5 - 1.0
        factors.Add(("SourceConfidence", sourceConfidenceFactor));
        totalFactor *= sourceConfidenceFactor;

        // Factor 2: Fact freshness (more recent = higher confidence)
        var avgAge = inference.SourceFacts.Average(f => (DateTimeOffset.UtcNow - f.Timestamp).TotalHours);
        var freshnessFactor = avgAge switch
        {
            < 1 => 1.0,
            < 24 => 0.95,
            < 168 => 0.85, // < 1 week
            _ => 0.7
        };
        factors.Add(("Freshness", freshnessFactor));
        totalFactor *= freshnessFactor;

        // Factor 3: Historical accuracy (if available)
        if (_history.TryGetValue(inference.RuleId, out var history))
        {
            var accuracyFactor = history.AccuracyRate;
            factors.Add(("HistoricalAccuracy", accuracyFactor));
            totalFactor *= accuracyFactor;
        }

        // Factor 4: Number of supporting facts
        var factCountFactor = inference.SourceFacts.Count switch
        {
            1 => 0.8,
            2 => 0.9,
            3 => 0.95,
            _ => 1.0
        };
        factors.Add(("FactCount", factCountFactor));
        totalFactor *= factCountFactor;

        return new ConfidenceAdjustment
        {
            Factor = Math.Clamp(totalFactor, 0.1, 1.5),
            Factors = factors
        };
    }

    /// <summary>
    /// Records feedback about an inference's accuracy.
    /// </summary>
    /// <param name="ruleId">The rule ID.</param>
    /// <param name="wasAccurate">Whether the inference was accurate.</param>
    public void RecordFeedback(string ruleId, bool wasAccurate)
    {
        var history = _history.GetOrAdd(ruleId, _ => new ConfidenceHistory { RuleId = ruleId });
        history.TotalInferences++;
        if (wasAccurate) history.AccurateInferences++;
    }

    /// <summary>
    /// Gets the confidence history for a rule.
    /// </summary>
    public ConfidenceHistory? GetHistory(string ruleId)
    {
        return _history.TryGetValue(ruleId, out var history) ? history : null;
    }

    /// <summary>
    /// Resets all confidence history.
    /// </summary>
    public void ResetHistory()
    {
        _history.Clear();
    }
}

/// <summary>
/// Confidence adjustment result.
/// </summary>
public sealed record ConfidenceAdjustment
{
    /// <summary>
    /// Gets the overall adjustment factor to apply.
    /// </summary>
    public double Factor { get; init; }

    /// <summary>
    /// Gets the individual factors that contributed.
    /// </summary>
    public IReadOnlyList<(string Name, double Factor)> Factors { get; init; } = Array.Empty<(string, double)>();
}

/// <summary>
/// Historical accuracy data for a rule.
/// </summary>
public sealed class ConfidenceHistory
{
    /// <summary>
    /// Gets the rule ID.
    /// </summary>
    public required string RuleId { get; init; }

    /// <summary>
    /// Gets or sets the total number of inferences.
    /// </summary>
    public long TotalInferences { get; set; }

    /// <summary>
    /// Gets or sets the number of accurate inferences.
    /// </summary>
    public long AccurateInferences { get; set; }

    /// <summary>
    /// Gets the accuracy rate (0.0 - 1.0).
    /// </summary>
    public double AccuracyRate => TotalInferences > 0 ? (double)AccurateInferences / TotalInferences : 1.0;
}

#endregion

#region Message Bus Payloads

/// <summary>
/// Payload structures for inference message bus operations.
/// </summary>
public static class InferencePayloads
{
    /// <summary>
    /// Payload for inference requests.
    /// </summary>
    public sealed record InferRequest
    {
        /// <summary>Facts to infer from.</summary>
        public required IReadOnlyList<FactDto> Facts { get; init; }

        /// <summary>Whether to use cache.</summary>
        public bool UseCache { get; init; } = true;

        /// <summary>Minimum confidence threshold.</summary>
        public double MinConfidence { get; init; } = 0.0;

        /// <summary>Categories to limit to.</summary>
        public IReadOnlyList<string>? Categories { get; init; }
    }

    /// <summary>
    /// DTO for facts in message payloads.
    /// </summary>
    public sealed record FactDto
    {
        /// <summary>Fact type.</summary>
        public required string FactType { get; init; }

        /// <summary>Fact value.</summary>
        public object? Value { get; init; }

        /// <summary>Source identifier.</summary>
        public required string SourceId { get; init; }

        /// <summary>Confidence level.</summary>
        public double Confidence { get; init; } = 1.0;
    }

    /// <summary>
    /// Response for inference operations.
    /// </summary>
    public sealed record InferResponse
    {
        /// <summary>Whether the operation succeeded.</summary>
        public bool Success { get; init; }

        /// <summary>Inferred knowledge.</summary>
        public IReadOnlyList<InferenceDto>? Inferences { get; init; }

        /// <summary>Whether result was from cache.</summary>
        public bool FromCache { get; init; }

        /// <summary>Error message if failed.</summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// DTO for inferred knowledge in message payloads.
    /// </summary>
    public sealed record InferenceDto
    {
        /// <summary>Inference ID.</summary>
        public required string InferenceId { get; init; }

        /// <summary>Inferred fact type.</summary>
        public required string FactType { get; init; }

        /// <summary>Inferred value.</summary>
        public object? Value { get; init; }

        /// <summary>Confidence level.</summary>
        public double Confidence { get; init; }

        /// <summary>Severity level.</summary>
        public string Severity { get; init; } = "Info";

        /// <summary>Rule that produced this inference.</summary>
        public required string RuleId { get; init; }

        /// <summary>Recommended action.</summary>
        public string? RecommendedAction { get; init; }

        /// <summary>Natural language explanation.</summary>
        public string? Explanation { get; init; }
    }

    /// <summary>
    /// Payload for rule registration.
    /// </summary>
    public sealed record RegisterRuleRequest
    {
        /// <summary>Rule ID.</summary>
        public required string RuleId { get; init; }

        /// <summary>Rule name.</summary>
        public required string Name { get; init; }

        /// <summary>Rule description.</summary>
        public required string Description { get; init; }

        /// <summary>Rule category.</summary>
        public required string Category { get; init; }

        /// <summary>Rule priority.</summary>
        public int Priority { get; init; } = 100;

        /// <summary>Base confidence.</summary>
        public double BaseConfidence { get; init; } = 0.8;

        /// <summary>Rule conditions (JSON).</summary>
        public required string ConditionsJson { get; init; }

        /// <summary>Rule conclusion (JSON).</summary>
        public required string ConclusionJson { get; init; }
    }

    /// <summary>
    /// Response for rule operations.
    /// </summary>
    public sealed record RuleOperationResponse
    {
        /// <summary>Whether the operation succeeded.</summary>
        public bool Success { get; init; }

        /// <summary>The rule ID.</summary>
        public string? RuleId { get; init; }

        /// <summary>Error message if failed.</summary>
        public string? ErrorMessage { get; init; }
    }
}

#endregion
