using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags
{
    /// <summary>
    /// Governs what version changes are permitted when evolving a tag schema.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Tag schema types")]
    public enum TagSchemaEvolutionRule
    {
        /// <summary>No evolution allowed; schema is frozen.</summary>
        None,

        /// <summary>Only additive changes (new optional fields, widened constraints).</summary>
        Additive,

        /// <summary>Compatible changes that don't break existing tag values.</summary>
        Compatible,

        /// <summary>Breaking changes allowed (migration required).</summary>
        Breaking
    }

    /// <summary>
    /// Defines constraints that a <see cref="TagValue"/> must satisfy
    /// when governed by a <see cref="TagSchema"/>.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Tag schema types")]
    public sealed record TagConstraint
    {
        /// <summary>Minimum numeric value (for <see cref="NumberTagValue"/>).</summary>
        public decimal? MinValue { get; init; }

        /// <summary>Maximum numeric value (for <see cref="NumberTagValue"/>).</summary>
        public decimal? MaxValue { get; init; }

        /// <summary>Minimum string length (for <see cref="StringTagValue"/>/<see cref="ParagraphTagValue"/>).</summary>
        public int? MinLength { get; init; }

        /// <summary>Maximum string length (for <see cref="StringTagValue"/>/<see cref="ParagraphTagValue"/>).</summary>
        public int? MaxLength { get; init; }

        /// <summary>Regex pattern that string values must match.</summary>
        public string? Pattern { get; init; }

        /// <summary>Allowed string values (enum-style restriction).</summary>
        public IReadOnlySet<string>? AllowedValues { get; init; }

        /// <summary>Allowed item kinds within a <see cref="ListTagValue"/>.</summary>
        public IReadOnlySet<TagValueKind>? AllowedItemKinds { get; init; }

        /// <summary>Maximum items for <see cref="ListTagValue"/> or <see cref="ObjectTagValue"/>.</summary>
        public int? MaxItems { get; init; }

        /// <summary>Maximum depth for <see cref="TreeTagValue"/>.</summary>
        public int? MaxDepth { get; init; }

        /// <summary>If true, the tag is mandatory on objects matching its scope.</summary>
        public bool Required { get; init; }

        /// <summary>Default value when tag is not explicitly provided.</summary>
        public TagValue? DefaultValue { get; init; }

        /// <summary>An empty constraint set with no restrictions.</summary>
        public static TagConstraint None { get; } = new();
    }

    /// <summary>
    /// A single version of a tag schema, capturing the required value kind
    /// and constraints at a point in time.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Tag schema types")]
    public sealed record TagSchemaVersion
    {
        /// <summary>Semantic version string (e.g., "1.0.0").</summary>
        public required string Version { get; init; }

        /// <summary>When this schema version was created.</summary>
        public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>Optional description of what changed in this version.</summary>
        public string? ChangeDescription { get; init; }

        /// <summary>Constraints that tag values must satisfy under this version.</summary>
        public TagConstraint Constraints { get; init; } = TagConstraint.None;

        /// <summary>The required <see cref="TagValueKind"/> for tags under this schema.</summary>
        public required TagValueKind RequiredKind { get; init; }
    }

    /// <summary>
    /// A tag schema defines governance rules for a specific tag key:
    /// what type it must be, what constraints apply, who can set it,
    /// and how it evolves over time through versioned schema changes.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Tag schema types")]
    public sealed record TagSchema
    {
        /// <summary>Unique schema identifier (e.g., "compliance.data-classification").</summary>
        public required string SchemaId { get; init; }

        /// <summary>The tag key this schema governs.</summary>
        public required TagKey TagKey { get; init; }

        /// <summary>Human-readable display name.</summary>
        public required string DisplayName { get; init; }

        /// <summary>Optional description of the schema's purpose.</summary>
        public string? Description { get; init; }

        /// <summary>
        /// Ordered list of schema versions (oldest to newest).
        /// Must contain at least one version.
        /// </summary>
        public required IReadOnlyList<TagSchemaVersion> Versions { get; init; }

        /// <summary>Gets the current (latest) schema version.</summary>
        public TagSchemaVersion CurrentVersion => Versions[^1];

        /// <summary>
        /// Flags indicating which <see cref="TagSource"/> values are permitted
        /// to set tags under this schema. Defaults to <see cref="TagSource.All"/>.
        /// </summary>
        public TagSource AllowedSources { get; init; } = TagSource.All;

        /// <summary>If true, the tag cannot be modified once set.</summary>
        public bool Immutable { get; init; }

        /// <summary>If true, only <see cref="TagSource.System"/> can write this tag.</summary>
        public bool SystemOnly { get; init; }
    }
}
