using System;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags
{
    /// <summary>
    /// Identifies who or what created or last modified a tag.
    /// Used for provenance tracking and schema enforcement.
    /// </summary>
    [Flags]
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag types")]
    public enum TagSource
    {
        /// <summary>No source.</summary>
        None = 0,

        /// <summary>Set by an end user.</summary>
        User = 1,

        /// <summary>Set by a plugin.</summary>
        Plugin = 2,

        /// <summary>Set by an AI/ML model.</summary>
        AI = 4,

        /// <summary>Set by the system infrastructure.</summary>
        System = 8,

        /// <summary>All sources permitted.</summary>
        All = User | Plugin | AI | System
    }

    /// <summary>
    /// Detailed provenance information about the source of a tag.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag types")]
    public sealed record TagSourceInfo(
        TagSource Source,
        string? SourceId = null,
        string? SourceName = null);
}
