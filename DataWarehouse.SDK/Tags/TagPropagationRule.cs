using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// Enumerates the stages of the data processing pipeline through which
/// objects (and their tags) flow. Used by <see cref="TagPropagationRule"/>
/// to define when propagation actions apply.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag propagation")]
public enum PipelineStage
{
    /// <summary>Data ingestion from external sources.</summary>
    Ingest = 0,

    /// <summary>Data validation and quality checks.</summary>
    Validate = 1,

    /// <summary>Data transformation and enrichment.</summary>
    Transform = 2,

    /// <summary>Data encryption at rest or in transit.</summary>
    Encrypt = 3,

    /// <summary>Data compression for storage efficiency.</summary>
    Compress = 4,

    /// <summary>Data written to persistent storage.</summary>
    Store = 5,

    /// <summary>Data replicated across nodes or sites.</summary>
    Replicate = 6,

    /// <summary>Data moved to long-term archival storage.</summary>
    Archive = 7,

    /// <summary>Data marked for deletion or permanently removed.</summary>
    Delete = 8
}

/// <summary>
/// Defines what happens to a tag when it transitions between pipeline stages.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag propagation")]
public enum PropagationAction
{
    /// <summary>Tag carries forward unchanged to the next stage.</summary>
    Copy = 0,

    /// <summary>Tag is removed at this stage.</summary>
    Drop = 1,

    /// <summary>Tag value is modified by a transform function before propagation.</summary>
    Transform = 2,

    /// <summary>When multiple sources converge, merge tag values using LWW or custom logic.</summary>
    Merge = 3,

    /// <summary>Inherit tag from the container or parent object.</summary>
    InheritFromParent = 4
}

/// <summary>
/// A rule that governs how a specific tag (or set of tags) propagates between
/// pipeline stages. Rules are evaluated in priority order; lower values execute first.
/// </summary>
/// <remarks>
/// <para>
/// Rules can target specific tags via <see cref="TagKeyFilter"/> or entire namespaces
/// via <see cref="NamespaceFilter"/>. When both are null, the rule applies to all tags.
/// </para>
/// <para>
/// For <see cref="PropagationAction.Transform"/>, the <see cref="TransformFunc"/> delegate
/// is invoked to produce the new tag. The delegate receives the source tag and must return
/// a transformed copy (tags are immutable records).
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag propagation")]
public sealed record TagPropagationRule
{
    /// <summary>Unique identifier for this rule.</summary>
    public required string RuleId { get; init; }

    /// <summary>
    /// Optional tag key filter. When set, this rule only applies to tags matching this key.
    /// Null means the rule may apply to any tag (subject to <see cref="NamespaceFilter"/>).
    /// </summary>
    public TagKey? TagKeyFilter { get; init; }

    /// <summary>
    /// Optional namespace filter. When set, this rule only applies to tags whose namespace
    /// matches (case-insensitive). Null means any namespace.
    /// </summary>
    public string? NamespaceFilter { get; init; }

    /// <summary>The pipeline stage this rule applies from.</summary>
    public required PipelineStage FromStage { get; init; }

    /// <summary>The pipeline stage this rule applies to.</summary>
    public required PipelineStage ToStage { get; init; }

    /// <summary>The action to take on the tag during propagation.</summary>
    public required PropagationAction Action { get; init; }

    /// <summary>
    /// Transform function invoked when <see cref="Action"/> is <see cref="PropagationAction.Transform"/>.
    /// Receives the source tag and returns the transformed tag. Ignored for other actions.
    /// </summary>
    public Func<Tag, Tag>? TransformFunc { get; init; }

    /// <summary>
    /// Priority for rule evaluation. Lower values are evaluated first.
    /// Default is 100. When two rules have equal priority, they are ordered by <see cref="RuleId"/>.
    /// </summary>
    public int Priority { get; init; } = 100;

    /// <summary>
    /// When true, no further rules are evaluated for the matched tag after this rule executes.
    /// Default is false, allowing subsequent rules to also apply.
    /// </summary>
    public bool StopOnMatch { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        $"Rule({RuleId}: {FromStage}->{ToStage}, {Action}, priority={Priority})";
}

/// <summary>
/// Context for a tag propagation operation, carrying the source and target object
/// identifiers, the current pipeline stage, and the tags to propagate.
/// </summary>
/// <param name="SourceObjectKey">The storage key of the source object.</param>
/// <param name="TargetObjectKey">The storage key of the target object receiving propagated tags.</param>
/// <param name="CurrentStage">The pipeline stage at which propagation is occurring.</param>
/// <param name="SourceTags">The tags attached to the source object.</param>
/// <param name="PipelineMetadata">Optional metadata from the pipeline for rule evaluation context.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag propagation")]
public sealed record TagPropagationContext(
    string SourceObjectKey,
    string TargetObjectKey,
    PipelineStage CurrentStage,
    TagCollection SourceTags,
    IReadOnlyDictionary<string, string>? PipelineMetadata = null);

/// <summary>
/// Result of a tag propagation operation, listing which tags were propagated,
/// which were dropped (by rule), and which failed (due to errors).
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag propagation")]
public sealed record TagPropagationResult
{
    /// <summary>Tags that were successfully propagated to the target object.</summary>
    public required IReadOnlyList<Tag> PropagatedTags { get; init; }

    /// <summary>Tags that were explicitly dropped by a propagation rule, with reasons.</summary>
    public required IReadOnlyList<(TagKey Key, string Reason)> DroppedTags { get; init; }

    /// <summary>Tags that failed to propagate due to errors (schema validation, transform failure, etc.).</summary>
    public required IReadOnlyList<(TagKey Key, string Error)> FailedTags { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        $"PropagationResult(propagated={PropagatedTags.Count}, dropped={DroppedTags.Count}, failed={FailedTags.Count})";
}
