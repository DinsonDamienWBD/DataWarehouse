using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// The entry point for all tag CRUD operations on stored objects.
/// Every mutation emits events on the message bus for downstream propagation
/// and policy enforcement.
/// </summary>
/// <remarks>
/// <para>
/// Implementations should:
/// <list type="bullet">
/// <item><description>Validate tag values against schemas when <see cref="Tag.SchemaId"/> is set</description></item>
/// <item><description>Enforce ACL permissions before mutations</description></item>
/// <item><description>Publish <see cref="TagEvent"/> instances to the message bus after each mutation</description></item>
/// <item><description>Increment <see cref="Tag.Version"/> on updates for optimistic concurrency</description></item>
/// </list>
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag attachment service contract")]
public interface ITagAttachmentService
{
    /// <summary>
    /// Attaches a new tag to the specified object.
    /// If a tag with the same key already exists, the operation should fail
    /// (use <see cref="UpdateAsync"/> to modify existing tags).
    /// </summary>
    /// <param name="objectKey">The storage key of the target object.</param>
    /// <param name="tagKey">The qualified tag key.</param>
    /// <param name="value">The strongly-typed tag value.</param>
    /// <param name="source">Provenance information for audit trails.</param>
    /// <param name="acl">Optional per-tag access control. Defaults to <see cref="TagAcl.FullAccess"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly created tag with version 1.</returns>
    Task<Tag> AttachAsync(
        string objectKey,
        TagKey tagKey,
        TagValue value,
        TagSourceInfo source,
        TagAcl? acl = null,
        CancellationToken ct = default);

    /// <summary>
    /// Detaches (removes) a tag from the specified object.
    /// No-op if the tag does not exist.
    /// </summary>
    /// <param name="objectKey">The storage key of the target object.</param>
    /// <param name="tagKey">The qualified tag key to remove.</param>
    /// <param name="source">Provenance information for the detachment audit trail.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DetachAsync(
        string objectKey,
        TagKey tagKey,
        TagSourceInfo source,
        CancellationToken ct = default);

    /// <summary>
    /// Updates the value of an existing tag on the specified object.
    /// Increments the tag version for optimistic concurrency.
    /// </summary>
    /// <param name="objectKey">The storage key of the target object.</param>
    /// <param name="tagKey">The qualified tag key to update.</param>
    /// <param name="newValue">The new strongly-typed value.</param>
    /// <param name="source">Provenance information for the update audit trail.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated tag with incremented version.</returns>
    Task<Tag> UpdateAsync(
        string objectKey,
        TagKey tagKey,
        TagValue newValue,
        TagSourceInfo source,
        CancellationToken ct = default);

    /// <summary>
    /// Gets a specific tag attached to the specified object, or null if not found.
    /// </summary>
    /// <param name="objectKey">The storage key of the target object.</param>
    /// <param name="tagKey">The qualified tag key to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tag if found; otherwise null.</returns>
    Task<Tag?> GetAsync(
        string objectKey,
        TagKey tagKey,
        CancellationToken ct = default);

    /// <summary>
    /// Gets all tags attached to the specified object.
    /// Returns <see cref="TagCollection.Empty"/> if the object has no tags.
    /// </summary>
    /// <param name="objectKey">The storage key of the target object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An immutable collection of all tags on the object.</returns>
    Task<TagCollection> GetAllAsync(
        string objectKey,
        CancellationToken ct = default);

    /// <summary>
    /// Attaches multiple tags to the specified object in a single batch operation.
    /// Emits a <see cref="TagsBulkUpdatedEvent"/> containing all individual events.
    /// </summary>
    /// <param name="objectKey">The storage key of the target object.</param>
    /// <param name="tags">The tags to attach, each with key, value, and source.</param>
    /// <param name="ct">Cancellation token.</param>
    Task BulkAttachAsync(
        string objectKey,
        IEnumerable<(TagKey Key, TagValue Value, TagSourceInfo Source)> tags,
        CancellationToken ct = default);

    /// <summary>
    /// Finds objects that have a tag matching the specified key and optional value.
    /// Supports reverse-index lookups for tag-based querying.
    /// </summary>
    /// <param name="tagKey">The qualified tag key to search for.</param>
    /// <param name="value">Optional value filter. When null, matches any value for the key.</param>
    /// <param name="limit">Maximum number of object keys to return. Defaults to 100.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of storage object keys that have the matching tag.</returns>
    Task<IReadOnlyList<string>> FindObjectsByTagAsync(
        TagKey tagKey,
        TagValue? value = null,
        int limit = 100,
        CancellationToken ct = default);
}
