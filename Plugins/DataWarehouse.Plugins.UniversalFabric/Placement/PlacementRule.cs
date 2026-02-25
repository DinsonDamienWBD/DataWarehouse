using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using StorageTier = DataWarehouse.SDK.Contracts.Storage.StorageTier;

namespace DataWarehouse.Plugins.UniversalFabric.Placement;

/// <summary>
/// Defines the type of action a placement rule performs on backend candidates.
/// </summary>
public enum PlacementRuleType
{
    /// <summary>
    /// Include: backends matching this rule are eligible candidates.
    /// </summary>
    Include,

    /// <summary>
    /// Exclude: backends matching this rule are removed from candidates.
    /// </summary>
    Exclude,

    /// <summary>
    /// Prefer: backends matching this rule receive a score boost but are not required.
    /// </summary>
    Prefer,

    /// <summary>
    /// Require: only backends matching this rule are allowed. If none match, placement fails.
    /// </summary>
    Require
}

/// <summary>
/// Declarative condition for matching objects against placement rules.
/// All specified conditions must match (AND logic). Null properties are ignored.
/// </summary>
public record PlacementCondition
{
    /// <summary>
    /// Gets a content type pattern to match (supports wildcards, e.g., "image/*", "video/*").
    /// Null means no content type restriction.
    /// </summary>
    public string? ContentTypePattern { get; init; }

    /// <summary>
    /// Gets the minimum object size in bytes for this rule to apply.
    /// Null means no minimum size restriction.
    /// </summary>
    public long? MinSizeBytes { get; init; }

    /// <summary>
    /// Gets the maximum object size in bytes for this rule to apply.
    /// Null means no maximum size restriction.
    /// </summary>
    public long? MaxSizeBytes { get; init; }

    /// <summary>
    /// Gets the set of metadata keys that must be present on the object for this rule to apply.
    /// Null means no metadata key restriction.
    /// </summary>
    public IReadOnlySet<string>? MetadataKeys { get; init; }

    /// <summary>
    /// Gets a bucket name pattern to match (supports wildcards, e.g., "archive-*").
    /// Null means no bucket restriction.
    /// </summary>
    public string? BucketPattern { get; init; }

    /// <summary>
    /// Evaluates whether the given placement context matches all specified conditions.
    /// </summary>
    /// <param name="context">The placement context describing the object being placed. May be null.</param>
    /// <returns>True if the context matches all non-null conditions; false otherwise.</returns>
    public bool Matches(PlacementContext? context)
    {
        if (context is null)
        {
            // If no context provided, only match if no conditions are specified
            return ContentTypePattern is null
                && MinSizeBytes is null
                && MaxSizeBytes is null
                && MetadataKeys is null
                && BucketPattern is null;
        }

        if (ContentTypePattern is not null)
        {
            if (context.ContentType is null)
                return false;

            if (!MatchesWildcard(context.ContentType, ContentTypePattern))
                return false;
        }

        if (MinSizeBytes is not null)
        {
            if (context.ObjectSize is null || context.ObjectSize.Value < MinSizeBytes.Value)
                return false;
        }

        if (MaxSizeBytes is not null)
        {
            if (context.ObjectSize is null || context.ObjectSize.Value > MaxSizeBytes.Value)
                return false;
        }

        if (MetadataKeys is not null && MetadataKeys.Count > 0)
        {
            if (context.Metadata is null)
                return false;

            foreach (var key in MetadataKeys)
            {
                if (!context.Metadata.ContainsKey(key))
                    return false;
            }
        }

        if (BucketPattern is not null)
        {
            if (context.BucketName is null)
                return false;

            if (!MatchesWildcard(context.BucketName, BucketPattern))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Matches a value against a simple wildcard pattern (supports * as any characters).
    /// </summary>
    private static bool MatchesWildcard(string value, string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(value, regexPattern, RegexOptions.IgnoreCase);
    }
}

/// <summary>
/// Declarative placement rule for backend selection. Rules are evaluated in priority order
/// (lower priority values are evaluated first) and control which backends are eligible
/// for storing new objects.
/// </summary>
/// <remarks>
/// <para>
/// Rules support four types: Include (make eligible), Exclude (remove from candidates),
/// Prefer (score boost), and Require (mandatory match). Rules can filter by tags, tier,
/// region, or match against object conditions like content type and size.
/// </para>
/// <para>
/// Rules are evaluated against backends in priority order. Require rules act as hard filters;
/// Exclude rules remove candidates; Include and Prefer rules expand or boost candidates.
/// </para>
/// </remarks>
public record PlacementRule
{
    /// <summary>
    /// Gets the unique name for this rule (used for identification and removal).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the type of action this rule performs (Include, Exclude, Prefer, Require).
    /// </summary>
    public PlacementRuleType Type { get; init; }

    /// <summary>
    /// Gets the condition that determines when this rule applies based on object properties.
    /// If null, the rule applies unconditionally.
    /// </summary>
    public PlacementCondition? Condition { get; init; }

    /// <summary>
    /// Gets an explicit backend ID target. When set with Require type, forces placement to this backend.
    /// </summary>
    public string? TargetBackendId { get; init; }

    /// <summary>
    /// Gets the set of tags that a backend must have for this rule to match.
    /// All specified tags must be present (AND logic).
    /// </summary>
    public IReadOnlySet<string>? RequiredTags { get; init; }

    /// <summary>
    /// Gets the set of tags that disqualify a backend from this rule.
    /// If a backend has any of these tags, the rule applies for exclusion.
    /// </summary>
    public IReadOnlySet<string>? ExcludedTags { get; init; }

    /// <summary>
    /// Gets the storage tier that a backend must operate on for this rule to match.
    /// </summary>
    public StorageTier? RequiredTier { get; init; }

    /// <summary>
    /// Gets the geographic region that a backend must be in for this rule to match.
    /// </summary>
    public string? RequiredRegion { get; init; }

    /// <summary>
    /// Gets the evaluation priority. Lower values are evaluated first.
    /// Default is 100. Use values below 100 for high-priority rules.
    /// </summary>
    public int Priority { get; init; } = 100;
}
