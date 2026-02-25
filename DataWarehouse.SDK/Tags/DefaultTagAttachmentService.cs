using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Tags
{
    /// <summary>
    /// Exception thrown when a tag fails schema validation.
    /// Carries the structured <see cref="TagValidationResult"/> for programmatic handling.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Tag store and attachment service")]
    public sealed class TagValidationException : Exception
    {
        /// <summary>
        /// Gets the structured validation result containing all errors.
        /// </summary>
        public TagValidationResult Result { get; }

        /// <summary>
        /// Initializes a new <see cref="TagValidationException"/> from a validation result.
        /// </summary>
        /// <param name="result">The failed validation result. Must have <see cref="TagValidationResult.IsValid"/> == false.</param>
        public TagValidationException(TagValidationResult result)
            : base(FormatMessage(result))
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }

        /// <summary>
        /// Initializes a new <see cref="TagValidationException"/> with a message and validation result.
        /// </summary>
        /// <param name="message">The error message.</param>
        /// <param name="result">The failed validation result.</param>
        public TagValidationException(string message, TagValidationResult result)
            : base(message)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }

        private static string FormatMessage(TagValidationResult result)
        {
            if (result is null || result.Errors.Count == 0)
                return "Tag validation failed.";

            return result.Errors.Count == 1
                ? $"Tag validation failed: {result.Errors[0].Message}"
                : $"Tag validation failed with {result.Errors.Count} errors: {result.Errors[0].Message}";
        }
    }

    /// <summary>
    /// Default SDK-level implementation of <see cref="ITagAttachmentService"/>.
    /// Validates tags against schemas (when available), respects ACL permissions,
    /// delegates persistence to <see cref="ITagStore"/>, and publishes events on
    /// <see cref="IMessageBus"/> (when available).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Optional dependencies:
    /// <list type="bullet">
    /// <item><description><see cref="ITagSchemaRegistry"/>: when provided, tags are validated against their schemas before storage.</description></item>
    /// <item><description><see cref="IMessageBus"/>: when provided, mutation events are published for downstream consumers.</description></item>
    /// </list>
    /// Both are optional to support minimal deployments and testing scenarios.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Tag store and attachment service")]
    public sealed class DefaultTagAttachmentService : ITagAttachmentService
    {
        private readonly ITagStore _store;
        private readonly ITagSchemaRegistry? _schemaRegistry;
        private readonly IMessageBus? _messageBus;

        /// <summary>
        /// Initializes a new <see cref="DefaultTagAttachmentService"/>.
        /// </summary>
        /// <param name="store">The tag store for persistence. Required.</param>
        /// <param name="schemaRegistry">Optional schema registry for validation.</param>
        /// <param name="messageBus">Optional message bus for event publishing.</param>
        public DefaultTagAttachmentService(
            ITagStore store,
            ITagSchemaRegistry? schemaRegistry = null,
            IMessageBus? messageBus = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _schemaRegistry = schemaRegistry;
            _messageBus = messageBus;
        }

        /// <inheritdoc />
        public async Task<Tag> AttachAsync(
            string objectKey,
            TagKey tagKey,
            TagValue value,
            TagSourceInfo source,
            TagAcl? acl = null,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
            ArgumentNullException.ThrowIfNull(tagKey);
            ArgumentNullException.ThrowIfNull(value);
            ArgumentNullException.ThrowIfNull(source);

            var now = DateTimeOffset.UtcNow;
            var tag = new Tag
            {
                Key = tagKey,
                Value = value,
                Source = source,
                Acl = acl ?? TagAcl.FullAccess,
                Version = 1,
                CreatedUtc = now,
                ModifiedUtc = now
            };

            // Validate against schema if registry is available
            await ValidateAgainstSchemaAsync(tag, ct).ConfigureAwait(false);

            // Check ACL: if an existing tag exists, verify Write permission
            var existing = await _store.GetTagAsync(objectKey, tagKey, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                EnsurePermission(existing.Acl, source, TagPermission.Write);
            }

            // Persist
            await _store.SetTagAsync(objectKey, tag, ct).ConfigureAwait(false);

            // Publish event
            await PublishEventAsync(
                TagTopics.TagAttached,
                new TagAttachedEvent(objectKey, tagKey, now, Guid.NewGuid().ToString("N"), tag),
                ct).ConfigureAwait(false);

            return tag;
        }

        /// <inheritdoc />
        public async Task DetachAsync(
            string objectKey,
            TagKey tagKey,
            TagSourceInfo source,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
            ArgumentNullException.ThrowIfNull(tagKey);
            ArgumentNullException.ThrowIfNull(source);

            var existing = await _store.GetTagAsync(objectKey, tagKey, ct).ConfigureAwait(false);
            if (existing is null)
                return; // No-op if not found

            // Check ACL: Delete permission required
            EnsurePermission(existing.Acl, source, TagPermission.Delete);

            // Remove from store
            await _store.RemoveTagAsync(objectKey, tagKey, ct).ConfigureAwait(false);

            // Publish event
            var now = DateTimeOffset.UtcNow;
            await PublishEventAsync(
                TagTopics.TagDetached,
                new TagDetachedEvent(objectKey, tagKey, now, Guid.NewGuid().ToString("N"), source.Source),
                ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<Tag> UpdateAsync(
            string objectKey,
            TagKey tagKey,
            TagValue newValue,
            TagSourceInfo source,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
            ArgumentNullException.ThrowIfNull(tagKey);
            ArgumentNullException.ThrowIfNull(newValue);
            ArgumentNullException.ThrowIfNull(source);

            var existing = await _store.GetTagAsync(objectKey, tagKey, ct).ConfigureAwait(false);
            if (existing is null)
            {
                throw new InvalidOperationException(
                    $"Cannot update tag '{tagKey}' on object '{objectKey}': tag does not exist.");
            }

            // Check ACL: Write permission required
            EnsurePermission(existing.Acl, source, TagPermission.Write);

            var now = DateTimeOffset.UtcNow;
            var updated = existing with
            {
                Value = newValue,
                Source = source,
                Version = existing.Version + 1,
                ModifiedUtc = now
            };

            // Validate the new value against schema
            await ValidateAgainstSchemaAsync(updated, ct).ConfigureAwait(false);

            // Persist
            await _store.SetTagAsync(objectKey, updated, ct).ConfigureAwait(false);

            // Publish event
            await PublishEventAsync(
                TagTopics.TagUpdated,
                new TagUpdatedEvent(objectKey, tagKey, now, Guid.NewGuid().ToString("N"), existing, updated),
                ct).ConfigureAwait(false);

            return updated;
        }

        /// <inheritdoc />
        public async Task<Tag?> GetAsync(
            string objectKey,
            TagKey tagKey,
            CancellationToken ct = default)
        {
            return await _store.GetTagAsync(objectKey, tagKey, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<TagCollection> GetAllAsync(
            string objectKey,
            CancellationToken ct = default)
        {
            return await _store.GetAllTagsAsync(objectKey, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task BulkAttachAsync(
            string objectKey,
            IEnumerable<(TagKey Key, TagValue Value, TagSourceInfo Source)> tags,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
            ArgumentNullException.ThrowIfNull(tags);

            var now = DateTimeOffset.UtcNow;
            var correlationId = Guid.NewGuid().ToString("N");
            var createdTags = new List<Tag>();
            var events = new List<TagEvent>();

            // Phase 1: Validate all tags first (fail-fast)
            foreach (var (key, value, source) in tags)
            {
                var tag = new Tag
                {
                    Key = key,
                    Value = value,
                    Source = source,
                    Acl = TagAcl.FullAccess,
                    Version = 1,
                    CreatedUtc = now,
                    ModifiedUtc = now
                };

                await ValidateAgainstSchemaAsync(tag, ct).ConfigureAwait(false);
                createdTags.Add(tag);
            }

            // Phase 2: Store all validated tags
            await _store.SetTagsAsync(objectKey, createdTags, ct).ConfigureAwait(false);

            // Phase 3: Build events
            foreach (var tag in createdTags)
            {
                events.Add(new TagAttachedEvent(objectKey, tag.Key, now, correlationId, tag));
            }

            // Phase 4: Publish bulk event
            if (events.Count > 0)
            {
                await PublishEventAsync(
                    TagTopics.TagsBulkUpdated,
                    new TagsBulkUpdatedEvent(objectKey, events, now, correlationId),
                    ct).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<string>> FindObjectsByTagAsync(
            TagKey tagKey,
            TagValue? value = null,
            int limit = 100,
            CancellationToken ct = default)
        {
            return await _store.FindObjectsByTagAsync(tagKey, value, limit, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Validates a tag against its schema (if schema registry is available and a schema exists for the tag key).
        /// Throws <see cref="TagValidationException"/> on validation failure.
        /// </summary>
        private async Task ValidateAgainstSchemaAsync(Tag tag, CancellationToken ct)
        {
            if (_schemaRegistry is null)
                return;

            var schema = await _schemaRegistry.GetByTagKeyAsync(tag.Key, ct).ConfigureAwait(false);
            if (schema is null)
                return; // No schema governance for this tag key

            var result = TagSchemaValidator.Validate(tag, schema);
            if (!result.IsValid)
            {
                throw new TagValidationException(result);
            }
        }

        /// <summary>
        /// Ensures the source has the required permission on the tag's ACL.
        /// Throws <see cref="UnauthorizedAccessException"/> if permission is denied.
        /// </summary>
        private static void EnsurePermission(TagAcl acl, TagSourceInfo source, TagPermission required)
        {
            // Use SourceId as the principal; fall back to source type name
            var principal = source.SourceId ?? source.Source.ToString();
            var effective = acl.GetEffectivePermission(principal);

            if (!effective.HasFlag(required))
            {
                throw new UnauthorizedAccessException(
                    $"Source '{principal}' does not have '{required}' permission. Effective: '{effective}'.");
            }
        }

        /// <summary>
        /// Publishes a tag event to the message bus if available.
        /// </summary>
        private async Task PublishEventAsync<TEvent>(string topic, TEvent @event, CancellationToken ct)
        {
            if (_messageBus is null)
                return;

            var message = new PluginMessage
            {
                Type = topic,
                Source = "TagAttachmentService",
                Payload = new Dictionary<string, object>
                {
                    ["event"] = @event!
                }
            };

            await _messageBus.PublishAsync(topic, message, ct).ConfigureAwait(false);
        }
    }
}
