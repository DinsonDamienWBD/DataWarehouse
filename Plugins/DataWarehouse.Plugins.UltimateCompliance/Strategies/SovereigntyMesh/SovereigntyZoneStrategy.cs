using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Compliance;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.SovereigntyMesh;

// ==================================================================================
// Sovereignty Zone Strategy: ISovereigntyZone implementation with tag-based rules,
// wildcard pattern matching, and passport-aware action escalation/de-escalation.
// ==================================================================================

/// <summary>
/// Implements <see cref="ISovereigntyZone"/> with declarative tag-based rule evaluation.
/// <para>
/// Each sovereignty zone defines jurisdictions, required regulations, and action rules
/// keyed by tag patterns. Evaluation matches data context tags against rules using
/// exact, prefix-wildcard, and suffix-wildcard patterns. When a compliance passport is
/// provided, the final action is adjusted: full passport coverage de-escalates severity
/// by one level, while missing or incomplete passport coverage escalates by one level.
/// </para>
/// </summary>
public sealed class SovereigntyZone : ISovereigntyZone
{
    /// <summary>
    /// Ordered severity levels from least to most restrictive.
    /// Used for escalation/de-escalation and most-restrictive-wins logic.
    /// </summary>
    private static readonly ZoneAction[] SeverityOrder =
    {
        ZoneAction.Allow,
        ZoneAction.RequireApproval,
        ZoneAction.RequireEncryption,
        ZoneAction.RequireAnonymization,
        ZoneAction.Quarantine,
        ZoneAction.Deny
    };

    /// <inheritdoc/>
    public string ZoneId { get; }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public IReadOnlyList<string> Jurisdictions { get; }

    /// <inheritdoc/>
    public IReadOnlyList<string> RequiredRegulations { get; }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, ZoneAction> ActionRules { get; }

    /// <inheritdoc/>
    public bool IsActive { get; internal set; }

    /// <summary>
    /// Creates a new sovereignty zone with the specified parameters.
    /// Use <see cref="SovereigntyZoneBuilder"/> for fluent construction.
    /// </summary>
    public SovereigntyZone(
        string zoneId,
        string name,
        IReadOnlyList<string> jurisdictions,
        IReadOnlyList<string> requiredRegulations,
        IReadOnlyDictionary<string, ZoneAction> actionRules,
        bool isActive = true)
    {
        ZoneId = zoneId ?? throw new ArgumentNullException(nameof(zoneId));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Jurisdictions = jurisdictions ?? throw new ArgumentNullException(nameof(jurisdictions));
        RequiredRegulations = requiredRegulations ?? throw new ArgumentNullException(nameof(requiredRegulations));
        ActionRules = actionRules ?? throw new ArgumentNullException(nameof(actionRules));
        IsActive = isActive;
    }

    /// <inheritdoc/>
    public Task<ZoneAction> EvaluateAsync(
        string objectId,
        CompliancePassport? passport,
        IReadOnlyDictionary<string, object> context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Step 1: Inactive zones don't block
        if (!IsActive)
            return Task.FromResult(ZoneAction.Allow);

        // Step 2-3: Match context tags against action rules, collect matched actions
        var matchedActions = new List<ZoneAction>();

        foreach (var contextEntry in context)
        {
            var contextTag = contextEntry.Key;
            // Also consider value-based tags like "classification:secret"
            var contextTagWithValue = contextEntry.Value is string strVal
                ? $"{contextTag}:{strVal}"
                : contextTag;

            foreach (var rule in ActionRules)
            {
                if (TagPatternMatcher.Matches(rule.Key, contextTag) ||
                    TagPatternMatcher.Matches(rule.Key, contextTagWithValue))
                {
                    matchedActions.Add(rule.Value);
                }
            }
        }

        // If no rules matched, default to Allow
        if (matchedActions.Count == 0)
        {
            var defaultAction = ZoneAction.Allow;
            defaultAction = AdjustForPassport(defaultAction, passport);
            return Task.FromResult(defaultAction);
        }

        // Step 4: Most restrictive action wins
        var finalAction = GetMostRestrictive(matchedActions);

        // Step 5: Passport-aware adjustment
        finalAction = AdjustForPassport(finalAction, passport);

        // Step 6: Return final action
        return Task.FromResult(finalAction);
    }

    /// <summary>
    /// Gets the most restrictive action from a list based on severity ordering.
    /// </summary>
    private static ZoneAction GetMostRestrictive(IEnumerable<ZoneAction> actions)
    {
        var maxIndex = -1;
        foreach (var action in actions)
        {
            var index = Array.IndexOf(SeverityOrder, action);
            if (index > maxIndex)
                maxIndex = index;
        }
        return maxIndex >= 0 ? SeverityOrder[maxIndex] : ZoneAction.Allow;
    }

    /// <summary>
    /// Adjusts the action based on passport coverage.
    /// Full coverage de-escalates by one level; missing/incomplete coverage escalates by one.
    /// </summary>
    private ZoneAction AdjustForPassport(ZoneAction action, CompliancePassport? passport)
    {
        if (RequiredRegulations.Count == 0)
            return action;

        if (passport != null && passport.IsValid())
        {
            var allCovered = RequiredRegulations.All(r => passport.CoversRegulation(r));
            if (allCovered)
            {
                // De-escalate by one severity level
                return DeescalateAction(action);
            }
            else
            {
                // Partial coverage - escalate by one level
                return EscalateAction(action);
            }
        }
        else
        {
            // No passport or invalid - escalate by one level
            return EscalateAction(action);
        }
    }

    /// <summary>
    /// Moves the action one step toward Allow (less restrictive).
    /// </summary>
    private static ZoneAction DeescalateAction(ZoneAction action)
    {
        var index = Array.IndexOf(SeverityOrder, action);
        return index <= 0 ? SeverityOrder[0] : SeverityOrder[index - 1];
    }

    /// <summary>
    /// Moves the action one step toward Deny (more restrictive).
    /// </summary>
    private static ZoneAction EscalateAction(ZoneAction action)
    {
        var index = Array.IndexOf(SeverityOrder, action);
        return index >= SeverityOrder.Length - 1 ? SeverityOrder[^1] : SeverityOrder[index + 1];
    }
}

/// <summary>
/// Provides tag pattern matching for sovereignty zone rule evaluation.
/// <para>
/// Supports three matching modes:
/// <list type="bullet">
///   <item>Exact match: "classification:secret" matches "classification:secret"</item>
///   <item>Prefix wildcard: "pii:*" matches "pii:name", "pii:email", etc.</item>
///   <item>Suffix wildcard: "*:eu" matches "jurisdiction:eu", "region:eu"</item>
/// </list>
/// All matching is case-insensitive.
/// </para>
/// </summary>
internal static class TagPatternMatcher
{
    /// <summary>
    /// Tests whether a tag pattern matches a given tag value.
    /// </summary>
    /// <param name="pattern">The rule pattern (may contain leading or trailing '*').</param>
    /// <param name="tag">The tag value to test.</param>
    /// <returns><c>true</c> if the pattern matches the tag.</returns>
    public static bool Matches(string pattern, string tag)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(tag))
            return false;

        // Suffix wildcard: "*:eu" matches anything ending with ":eu"
        if (pattern.StartsWith('*'))
        {
            var suffix = pattern.AsSpan(1);
            return tag.AsSpan().EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        // Prefix wildcard: "pii:*" matches anything starting with "pii:"
        if (pattern.EndsWith('*'))
        {
            var prefix = pattern.AsSpan(0, pattern.Length - 1);
            return tag.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // Exact match
        return string.Equals(pattern, tag, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Fluent builder for constructing <see cref="SovereigntyZone"/> instances.
/// <para>
/// Example usage:
/// <code>
/// var zone = new SovereigntyZoneBuilder()
///     .WithId("eu-gdpr-zone")
///     .WithName("EU GDPR Zone")
///     .InJurisdictions("DE", "FR", "IT")
///     .Requiring("GDPR")
///     .WithRule("pii:*", ZoneAction.RequireEncryption)
///     .WithRule("classification:secret", ZoneAction.Deny)
///     .Build();
/// </code>
/// </para>
/// </summary>
public sealed class SovereigntyZoneBuilder
{
    private string _zoneId = string.Empty;
    private string _name = string.Empty;
    private readonly List<string> _jurisdictions = new();
    private readonly List<string> _requiredRegulations = new();
    private readonly Dictionary<string, ZoneAction> _actionRules = new();
    private bool _isActive = true;

    /// <summary>Sets the zone identifier.</summary>
    public SovereigntyZoneBuilder WithId(string zoneId)
    {
        _zoneId = zoneId;
        return this;
    }

    /// <summary>Sets the zone display name.</summary>
    public SovereigntyZoneBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    /// <summary>Adds jurisdictions (ISO country codes) to this zone.</summary>
    public SovereigntyZoneBuilder InJurisdictions(params string[] jurisdictions)
    {
        _jurisdictions.AddRange(jurisdictions);
        return this;
    }

    /// <summary>Adds required regulation identifiers that the zone enforces.</summary>
    public SovereigntyZoneBuilder Requiring(params string[] regulations)
    {
        _requiredRegulations.AddRange(regulations);
        return this;
    }

    /// <summary>Adds a tag-pattern-based action rule.</summary>
    public SovereigntyZoneBuilder WithRule(string tagPattern, ZoneAction action)
    {
        _actionRules[tagPattern] = action;
        return this;
    }

    /// <summary>Sets whether the zone starts as active (default: true).</summary>
    public SovereigntyZoneBuilder Active(bool isActive)
    {
        _isActive = isActive;
        return this;
    }

    /// <summary>
    /// Builds and returns a new <see cref="SovereigntyZone"/> from the configured parameters.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when ZoneId or Name is not set.</exception>
    public SovereigntyZone Build()
    {
        if (string.IsNullOrWhiteSpace(_zoneId))
            throw new InvalidOperationException("ZoneId is required.");
        if (string.IsNullOrWhiteSpace(_name))
            throw new InvalidOperationException("Name is required.");
        // LOW-1555: A zone with no jurisdictions is silently unreachable; require at least one.
        if (_jurisdictions.Count == 0)
            throw new InvalidOperationException("At least one jurisdiction is required for a sovereignty zone.");

        return new SovereigntyZone(
            _zoneId,
            _name,
            _jurisdictions.AsReadOnly(),
            _requiredRegulations.AsReadOnly(),
            new Dictionary<string, ZoneAction>(_actionRules),
            _isActive);
    }
}
