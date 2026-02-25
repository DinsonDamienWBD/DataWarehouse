using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// Abstract base for all tag mutation events published on the message bus.
/// Every event carries the affected object key, the tag key, a timestamp,
/// and a correlation ID for distributed tracing.
/// </summary>
/// <param name="ObjectKey">The storage key of the object whose tag was mutated.</param>
/// <param name="TagKey">The qualified key of the affected tag.</param>
/// <param name="Timestamp">UTC timestamp of when the event occurred.</param>
/// <param name="CorrelationId">Correlation ID for distributed tracing and event grouping.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag attachment events")]
public abstract record TagEvent(
    string ObjectKey,
    TagKey TagKey,
    DateTimeOffset Timestamp,
    string CorrelationId);

/// <summary>
/// Published when a new tag is attached to an object.
/// </summary>
/// <param name="ObjectKey">The storage key of the object.</param>
/// <param name="TagKey">The qualified key of the attached tag.</param>
/// <param name="Timestamp">UTC timestamp of attachment.</param>
/// <param name="CorrelationId">Correlation ID for tracing.</param>
/// <param name="Tag">The full tag that was attached.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag attachment events")]
public sealed record TagAttachedEvent(
    string ObjectKey,
    TagKey TagKey,
    DateTimeOffset Timestamp,
    string CorrelationId,
    Tag Tag) : TagEvent(ObjectKey, TagKey, Timestamp, CorrelationId);

/// <summary>
/// Published when a tag is detached (removed) from an object.
/// </summary>
/// <param name="ObjectKey">The storage key of the object.</param>
/// <param name="TagKey">The qualified key of the detached tag.</param>
/// <param name="Timestamp">UTC timestamp of detachment.</param>
/// <param name="CorrelationId">Correlation ID for tracing.</param>
/// <param name="DetachedBy">The source that triggered the detachment.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag attachment events")]
public sealed record TagDetachedEvent(
    string ObjectKey,
    TagKey TagKey,
    DateTimeOffset Timestamp,
    string CorrelationId,
    TagSource DetachedBy) : TagEvent(ObjectKey, TagKey, Timestamp, CorrelationId);

/// <summary>
/// Published when an existing tag's value is updated.
/// </summary>
/// <param name="ObjectKey">The storage key of the object.</param>
/// <param name="TagKey">The qualified key of the updated tag.</param>
/// <param name="Timestamp">UTC timestamp of update.</param>
/// <param name="CorrelationId">Correlation ID for tracing.</param>
/// <param name="OldTag">The tag state before the update.</param>
/// <param name="NewTag">The tag state after the update.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag attachment events")]
public sealed record TagUpdatedEvent(
    string ObjectKey,
    TagKey TagKey,
    DateTimeOffset Timestamp,
    string CorrelationId,
    Tag OldTag,
    Tag NewTag) : TagEvent(ObjectKey, TagKey, Timestamp, CorrelationId);

/// <summary>
/// Published when multiple tags are mutated in a single batch operation.
/// Contains the individual events for each tag mutation.
/// </summary>
/// <param name="ObjectKey">The storage key of the object.</param>
/// <param name="Events">The individual tag events within the batch.</param>
/// <param name="Timestamp">UTC timestamp of the batch operation.</param>
/// <param name="CorrelationId">Correlation ID for tracing the batch.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag attachment events")]
public sealed record TagsBulkUpdatedEvent(
    string ObjectKey,
    IReadOnlyList<TagEvent> Events,
    DateTimeOffset Timestamp,
    string CorrelationId)
{
    /// <inheritdoc />
    public override string ToString() => $"TagsBulkUpdated({ObjectKey}, {Events.Count} events)";
}

/// <summary>
/// Message bus topic constants for tag events.
/// Follow the existing bus topic naming convention: "domain.action".
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag event topics")]
public static class TagTopics
{
    /// <summary>Topic for tag-attached events.</summary>
    public const string TagAttached = "tags.attached";

    /// <summary>Topic for tag-detached events.</summary>
    public const string TagDetached = "tags.detached";

    /// <summary>Topic for tag-updated events.</summary>
    public const string TagUpdated = "tags.updated";

    /// <summary>Topic for bulk tag mutation events.</summary>
    public const string TagsBulkUpdated = "tags.bulk-updated";

    /// <summary>Topic for tag policy violation notifications.</summary>
    public const string TagPolicyViolation = "tags.policy.violation";
}
