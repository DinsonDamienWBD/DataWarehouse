// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.TamperProof;

namespace DataWarehouse.Plugins.TamperProof.TimeLock;

/// <summary>
/// Rule-based policy engine for auto-assigning time-lock durations based on
/// data classification, compliance requirements, and content type.
/// Evaluates rules in priority order (descending) and returns the first match.
/// Ships with 6 built-in compliance rules covering HIPAA, PCI-DSS, GDPR, SOX,
/// classified data, and a default catch-all.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Time-lock engine")]
public sealed class TimeLockPolicyEngine
{
    private readonly ConcurrentBag<TimeLockRule> _rules = new();

    /// <summary>
    /// Gets all currently registered rules, ordered by priority descending.
    /// </summary>
    public IReadOnlyList<TimeLockRule> Rules =>
        _rules.OrderByDescending(r => r.Priority).ToList().AsReadOnly();

    /// <summary>
    /// Initializes a new TimeLockPolicyEngine with the built-in compliance rules.
    /// </summary>
    public TimeLockPolicyEngine()
    {
        InitializeBuiltInRules();
    }

    /// <summary>
    /// Evaluates all registered rules against the provided classification metadata
    /// and returns the time-lock policy from the highest-priority matching rule.
    /// If no rule matches, returns the default policy (7-day lock, Basic vaccination).
    /// </summary>
    /// <param name="dataClassification">Optional data classification label (e.g., "PHI", "PII", "SECRET").</param>
    /// <param name="complianceFramework">Optional compliance framework identifier (e.g., "HIPAA", "PCI-DSS", "GDPR").</param>
    /// <param name="contentType">Optional content type (e.g., "application/pdf", "text/csv", "image/dicom").</param>
    /// <returns>The time-lock policy from the first matching rule, or the default policy if no match.</returns>
    public TimeLockPolicy EvaluatePolicy(
        string? dataClassification,
        string? complianceFramework,
        string? contentType)
    {
        var orderedRules = _rules.OrderByDescending(r => r.Priority);

        foreach (var rule in orderedRules)
        {
            if (RuleMatches(rule, dataClassification, complianceFramework, contentType))
            {
                return BuildPolicyFromRule(rule);
            }
        }

        // No match: return default policy
        return BuildDefaultPolicy();
    }

    /// <summary>
    /// Gets the effective policy for the given metadata, identical to <see cref="EvaluatePolicy"/>
    /// but also returns the matched rule name for audit purposes.
    /// </summary>
    /// <param name="dataClassification">Optional data classification label.</param>
    /// <param name="complianceFramework">Optional compliance framework identifier.</param>
    /// <param name="contentType">Optional content type.</param>
    /// <returns>Tuple of (policy, matchedRuleName).</returns>
    public (TimeLockPolicy Policy, string RuleName) GetEffectivePolicy(
        string? dataClassification,
        string? complianceFramework,
        string? contentType)
    {
        var orderedRules = _rules.OrderByDescending(r => r.Priority);

        foreach (var rule in orderedRules)
        {
            if (RuleMatches(rule, dataClassification, complianceFramework, contentType))
            {
                return (BuildPolicyFromRule(rule), rule.Name);
            }
        }

        return (BuildDefaultPolicy(), "Default");
    }

    /// <summary>
    /// Adds a new rule to the engine. Rules are evaluated by priority (highest first).
    /// </summary>
    /// <param name="rule">The rule to add. Must have a unique name.</param>
    /// <exception cref="ArgumentNullException">Thrown when rule is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a rule with the same name already exists.</exception>
    public void AddRule(TimeLockRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (_rules.Any(r => r.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"A rule with name '{rule.Name}' already exists.", nameof(rule));
        }

        _rules.Add(rule);
    }

    /// <summary>
    /// Removes a rule by name. Returns true if the rule was found and removed.
    /// </summary>
    /// <param name="ruleName">Name of the rule to remove.</param>
    /// <returns>True if a rule was removed, false if no rule with that name was found.</returns>
    public bool RemoveRule(string ruleName)
    {
        if (string.IsNullOrWhiteSpace(ruleName)) return false;

        // ConcurrentBag does not support removal by predicate, so rebuild
        var snapshot = _rules.ToArray();
        var toKeep = snapshot.Where(r => !r.Name.Equals(ruleName, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (toKeep.Length == snapshot.Length) return false;

        // Clear and re-add
        while (_rules.TryTake(out _)) { }
        foreach (var rule in toKeep)
        {
            _rules.Add(rule);
        }

        return true;
    }

    /// <summary>
    /// Checks whether a rule matches the provided metadata.
    /// A rule matches if ALL of its non-null patterns match the corresponding metadata values.
    /// </summary>
    private static bool RuleMatches(
        TimeLockRule rule,
        string? dataClassification,
        string? complianceFramework,
        string? contentType)
    {
        // Check data classification pattern
        if (!string.IsNullOrEmpty(rule.DataClassificationPattern))
        {
            if (string.IsNullOrEmpty(dataClassification))
                return false;

            if (!Regex.IsMatch(dataClassification, rule.DataClassificationPattern, RegexOptions.IgnoreCase))
                return false;
        }

        // Check compliance frameworks
        if (rule.ComplianceFrameworks.Length > 0)
        {
            if (string.IsNullOrEmpty(complianceFramework))
                return false;

            var matchesAnyFramework = rule.ComplianceFrameworks.Any(f =>
                complianceFramework.Equals(f, StringComparison.OrdinalIgnoreCase));

            if (!matchesAnyFramework)
                return false;
        }

        // Check content type patterns
        if (rule.ContentTypePatterns.Length > 0)
        {
            if (string.IsNullOrEmpty(contentType))
                return false;

            var matchesAnyPattern = rule.ContentTypePatterns.Any(p =>
                Regex.IsMatch(contentType, p, RegexOptions.IgnoreCase));

            if (!matchesAnyPattern)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Builds a TimeLockPolicy from a matched rule.
    /// </summary>
    private static TimeLockPolicy BuildPolicyFromRule(TimeLockRule rule)
    {
        var allowedConditions = new List<UnlockConditionType>
        {
            UnlockConditionType.TimeExpiry
        };

        if (rule.RequireMultiPartyUnlock)
        {
            allowedConditions.Add(UnlockConditionType.MultiPartyApproval);
        }

        // All policies allow emergency break-glass and compliance release
        allowedConditions.Add(UnlockConditionType.EmergencyBreakGlass);
        allowedConditions.Add(UnlockConditionType.ComplianceRelease);

        // Maximum vaccination or classified data gets NeverUnlock option
        if (rule.VaccinationLevel >= VaccinationLevel.Maximum)
        {
            allowedConditions.Add(UnlockConditionType.NeverUnlock);
        }

        return new TimeLockPolicy
        {
            MinLockDuration = rule.MinLockDuration,
            MaxLockDuration = rule.MaxLockDuration,
            DefaultLockDuration = rule.DefaultLockDuration,
            AllowedUnlockConditions = allowedConditions.ToArray(),
            RequireMultiPartyForEarlyUnlock = rule.RequireMultiPartyUnlock,
            VaccinationLevel = rule.VaccinationLevel,
            AutoExtendOnTamperDetection = rule.VaccinationLevel >= VaccinationLevel.Enhanced,
            RequirePqcSignature = rule.VaccinationLevel >= VaccinationLevel.Maximum
        };
    }

    /// <summary>
    /// Builds the default policy used when no rule matches: 7-day lock, Basic vaccination.
    /// </summary>
    private static TimeLockPolicy BuildDefaultPolicy()
    {
        return new TimeLockPolicy
        {
            MinLockDuration = TimeSpan.FromMinutes(1),
            MaxLockDuration = TimeSpan.FromDays(365 * 100),
            DefaultLockDuration = TimeSpan.FromDays(7),
            AllowedUnlockConditions = new[]
            {
                UnlockConditionType.TimeExpiry,
                UnlockConditionType.EmergencyBreakGlass,
                UnlockConditionType.ComplianceRelease
            },
            RequireMultiPartyForEarlyUnlock = false,
            VaccinationLevel = VaccinationLevel.Basic,
            AutoExtendOnTamperDetection = false,
            RequirePqcSignature = false
        };
    }

    /// <summary>
    /// Initializes the 6 built-in compliance rules:
    /// HIPAA-PHI, PCI-DSS, GDPR-Personal, SOX-Financial, Classified-Secret, and Default.
    /// </summary>
    private void InitializeBuiltInRules()
    {
        // Priority 100: Classified/Secret -- highest priority, most restrictive
        _rules.Add(new TimeLockRule
        {
            Name = "Classified-Secret",
            Priority = 100,
            DataClassificationPattern = @"(?i)(secret|top.?secret|classified|restricted)",
            ComplianceFrameworks = Array.Empty<string>(),
            ContentTypePatterns = Array.Empty<string>(),
            MinLockDuration = TimeSpan.FromDays(365 * 25),
            MaxLockDuration = TimeSpan.FromDays(365 * 100),
            DefaultLockDuration = TimeSpan.FromDays(365 * 25),
            VaccinationLevel = VaccinationLevel.Maximum,
            RequireMultiPartyUnlock = true
        });

        // Priority 90: SOX Financial -- 7-year retention with Maximum vaccination
        _rules.Add(new TimeLockRule
        {
            Name = "SOX-Financial",
            Priority = 90,
            DataClassificationPattern = @"(?i)(financial|sox|audit|accounting)",
            ComplianceFrameworks = new[] { "SOX", "SOX-404" },
            ContentTypePatterns = Array.Empty<string>(),
            MinLockDuration = TimeSpan.FromDays(365 * 7),
            MaxLockDuration = TimeSpan.FromDays(365 * 25),
            DefaultLockDuration = TimeSpan.FromDays(365 * 7),
            VaccinationLevel = VaccinationLevel.Maximum,
            RequireMultiPartyUnlock = true
        });

        // Priority 80: HIPAA PHI -- 7-year retention with Enhanced vaccination
        _rules.Add(new TimeLockRule
        {
            Name = "HIPAA-PHI",
            Priority = 80,
            DataClassificationPattern = @"(?i)(phi|protected.?health|medical|patient)",
            ComplianceFrameworks = new[] { "HIPAA", "HITECH" },
            ContentTypePatterns = new[] { @"(?i)(dicom|hl7|fhir|medical)" },
            MinLockDuration = TimeSpan.FromDays(365 * 7),
            MaxLockDuration = TimeSpan.FromDays(365 * 25),
            DefaultLockDuration = TimeSpan.FromDays(365 * 7),
            VaccinationLevel = VaccinationLevel.Enhanced,
            RequireMultiPartyUnlock = true
        });

        // Priority 70: PCI-DSS -- 1-year retention with Enhanced vaccination
        _rules.Add(new TimeLockRule
        {
            Name = "PCI-DSS",
            Priority = 70,
            DataClassificationPattern = @"(?i)(pci|payment|cardholder|pan|credit.?card)",
            ComplianceFrameworks = new[] { "PCI-DSS", "PCI" },
            ContentTypePatterns = Array.Empty<string>(),
            MinLockDuration = TimeSpan.FromDays(365),
            MaxLockDuration = TimeSpan.FromDays(365 * 7),
            DefaultLockDuration = TimeSpan.FromDays(365),
            VaccinationLevel = VaccinationLevel.Enhanced,
            RequireMultiPartyUnlock = false
        });

        // Priority 50: GDPR Personal -- 30-day lock with Basic vaccination
        _rules.Add(new TimeLockRule
        {
            Name = "GDPR-Personal",
            Priority = 50,
            DataClassificationPattern = @"(?i)(personal|pii|gdpr|data.?subject)",
            ComplianceFrameworks = new[] { "GDPR", "CCPA", "LGPD" },
            ContentTypePatterns = Array.Empty<string>(),
            MinLockDuration = TimeSpan.FromDays(1),
            MaxLockDuration = TimeSpan.FromDays(365),
            DefaultLockDuration = TimeSpan.FromDays(30),
            VaccinationLevel = VaccinationLevel.Basic,
            RequireMultiPartyUnlock = false
        });

        // Priority 0: Default -- catch-all with 7-day lock
        _rules.Add(new TimeLockRule
        {
            Name = "Default",
            Priority = 0,
            DataClassificationPattern = null,
            ComplianceFrameworks = Array.Empty<string>(),
            ContentTypePatterns = Array.Empty<string>(),
            MinLockDuration = TimeSpan.FromMinutes(1),
            MaxLockDuration = TimeSpan.FromDays(365 * 100),
            DefaultLockDuration = TimeSpan.FromDays(7),
            VaccinationLevel = VaccinationLevel.Basic,
            RequireMultiPartyUnlock = false
        });
    }
}

/// <summary>
/// Defines a single time-lock policy rule that can be matched against object metadata.
/// Rules are evaluated in priority order (highest first) by the <see cref="TimeLockPolicyEngine"/>.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Time-lock engine")]
public sealed record TimeLockRule
{
    /// <summary>
    /// Human-readable name for this rule (must be unique within the engine).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Evaluation priority. Higher values are evaluated first. Use 0 for default/catch-all rules.
    /// </summary>
    public required int Priority { get; init; }

    /// <summary>
    /// Optional regex pattern to match against data classification labels.
    /// If null or empty, this dimension is not considered for matching.
    /// </summary>
    public string? DataClassificationPattern { get; init; }

    /// <summary>
    /// Compliance framework identifiers this rule applies to (e.g., "HIPAA", "PCI-DSS", "GDPR").
    /// If empty, compliance framework is not considered for matching.
    /// </summary>
    public required string[] ComplianceFrameworks { get; init; }

    /// <summary>
    /// Regex patterns to match against content types (e.g., "application/pdf", "image/dicom").
    /// If empty, content type is not considered for matching.
    /// </summary>
    public required string[] ContentTypePatterns { get; init; }

    /// <summary>
    /// Minimum lock duration enforced by this rule.
    /// </summary>
    public required TimeSpan MinLockDuration { get; init; }

    /// <summary>
    /// Maximum lock duration enforced by this rule.
    /// </summary>
    public required TimeSpan MaxLockDuration { get; init; }

    /// <summary>
    /// Default lock duration assigned when this rule matches.
    /// </summary>
    public required TimeSpan DefaultLockDuration { get; init; }

    /// <summary>
    /// Ransomware vaccination level required by this rule.
    /// </summary>
    public required VaccinationLevel VaccinationLevel { get; init; }

    /// <summary>
    /// Whether early unlock (before time expiry) requires multi-party approval.
    /// </summary>
    public required bool RequireMultiPartyUnlock { get; init; }
}
