using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// Identifies who or what created or last modified a tag.
/// Used for provenance tracking, audit trails, and schema enforcement.
/// Defined as flags to support filtering by multiple sources simultaneously.
/// </summary>
[Flags]
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag source enumeration")]
public enum TagSource
{
    /// <summary>No source specified.</summary>
    None = 0,

    /// <summary>Tag was set by an end user through direct interaction.</summary>
    User = 1,

    /// <summary>Tag was set by a plugin during processing.</summary>
    Plugin = 2,

    /// <summary>Tag was set by an AI/ML model (classification, inference, etc.).</summary>
    AI = 4,

    /// <summary>Tag was set by the system infrastructure (automatic tagging, replication, etc.).</summary>
    System = 8,

    /// <summary>All sources combined -- useful for filtering/ACL that permits any source.</summary>
    All = User | Plugin | AI | System
}

/// <summary>
/// Detailed provenance information about the source of a tag.
/// Captures the broad source category plus specific identifiers for audit trails.
/// </summary>
/// <param name="Source">The broad category of who/what created the tag.</param>
/// <param name="SourceId">
/// The specific identifier of the source: plugin ID for <see cref="TagSource.Plugin"/>,
/// user principal for <see cref="TagSource.User"/>, AI model ID for <see cref="TagSource.AI"/>,
/// or "system" for <see cref="TagSource.System"/>.
/// </param>
/// <param name="SourceName">Optional human-readable display name for the source.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag source provenance")]
public sealed record TagSourceInfo(
    TagSource Source,
    string? SourceId = null,
    string? SourceName = null)
{
    /// <inheritdoc />
    public override string ToString() =>
        SourceName is not null ? $"{Source}({SourceName})" : $"{Source}({SourceId ?? "unknown"})";
}
