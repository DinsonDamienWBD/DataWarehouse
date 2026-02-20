using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Tags
{
    /// <summary>
    /// Persistence interface for tag storage. Provides CRUD and search operations
    /// for tag collections keyed by object storage key.
    /// </summary>
    /// <remarks>
    /// Implementations may back this with in-memory dictionaries, databases,
    /// or distributed stores. The <see cref="InMemoryTagStore"/> is the
    /// default SDK-level implementation for tests and single-node deployments.
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Tag store interface")]
    public interface ITagStore
    {
        /// <summary>
        /// Gets a specific tag for an object by its key, or null if not found.
        /// </summary>
        /// <param name="objectKey">The storage key of the object.</param>
        /// <param name="tagKey">The tag key to retrieve.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The tag if found; otherwise null.</returns>
        Task<Tag?> GetTagAsync(string objectKey, TagKey tagKey, CancellationToken ct = default);

        /// <summary>
        /// Gets all tags for an object as an immutable collection.
        /// Returns <see cref="TagCollection.Empty"/> if the object has no tags.
        /// </summary>
        /// <param name="objectKey">The storage key of the object.</param>
        /// <param name="ct">Cancellation token.</param>
        Task<TagCollection> GetAllTagsAsync(string objectKey, CancellationToken ct = default);

        /// <summary>
        /// Sets (adds or replaces) a tag on an object.
        /// </summary>
        /// <param name="objectKey">The storage key of the object.</param>
        /// <param name="tag">The tag to store.</param>
        /// <param name="ct">Cancellation token.</param>
        Task SetTagAsync(string objectKey, Tag tag, CancellationToken ct = default);

        /// <summary>
        /// Removes a specific tag from an object.
        /// No-op if the tag does not exist.
        /// </summary>
        /// <param name="objectKey">The storage key of the object.</param>
        /// <param name="tagKey">The tag key to remove.</param>
        /// <param name="ct">Cancellation token.</param>
        Task RemoveTagAsync(string objectKey, TagKey tagKey, CancellationToken ct = default);

        /// <summary>
        /// Sets multiple tags on an object in a single operation.
        /// Existing tags with the same keys are replaced.
        /// </summary>
        /// <param name="objectKey">The storage key of the object.</param>
        /// <param name="tags">The tags to store.</param>
        /// <param name="ct">Cancellation token.</param>
        Task SetTagsAsync(string objectKey, IEnumerable<Tag> tags, CancellationToken ct = default);

        /// <summary>
        /// Finds objects that have a tag matching the specified key and optional value.
        /// </summary>
        /// <param name="tagKey">The tag key to search for.</param>
        /// <param name="value">Optional value filter. When null, matches any value.</param>
        /// <param name="limit">Maximum number of results to return.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Object keys that have the matching tag.</returns>
        Task<IReadOnlyList<string>> FindObjectsByTagAsync(
            TagKey tagKey, TagValue? value, int limit, CancellationToken ct = default);
    }

    /// <summary>
    /// In-memory implementation of <see cref="ITagStore"/> backed by <see cref="ConcurrentDictionary{TKey,TValue}"/>.
    /// Thread-safe and suitable for tests and single-node deployments.
    /// Plan 08 builds an inverted index for efficient <see cref="FindObjectsByTagAsync"/>;
    /// this implementation uses linear scan.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Tag store and attachment service")]
    public sealed class InMemoryTagStore : ITagStore
    {
        private readonly BoundedDictionary<string, BoundedDictionary<TagKey, Tag>> _store = new BoundedDictionary<string, BoundedDictionary<TagKey, Tag>>(1000);

        /// <inheritdoc />
        public Task<Tag?> GetTagAsync(string objectKey, TagKey tagKey, CancellationToken ct = default)
        {
            if (_store.TryGetValue(objectKey, out var tags) &&
                tags.TryGetValue(tagKey, out var tag))
            {
                return Task.FromResult<Tag?>(tag);
            }

            return Task.FromResult<Tag?>(null);
        }

        /// <inheritdoc />
        public Task<TagCollection> GetAllTagsAsync(string objectKey, CancellationToken ct = default)
        {
            if (!_store.TryGetValue(objectKey, out var tags) || tags.IsEmpty)
            {
                return Task.FromResult(TagCollection.Empty);
            }

            var dict = new Dictionary<TagKey, Tag>(tags.Count);
            foreach (var kvp in tags)
            {
                dict[kvp.Key] = kvp.Value;
            }

            return Task.FromResult(new TagCollection(dict));
        }

        /// <inheritdoc />
        public Task SetTagAsync(string objectKey, Tag tag, CancellationToken ct = default)
        {
            var objectTags = _store.GetOrAdd(objectKey, _ => new BoundedDictionary<TagKey, Tag>(1000));
            objectTags[tag.Key] = tag;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task RemoveTagAsync(string objectKey, TagKey tagKey, CancellationToken ct = default)
        {
            if (_store.TryGetValue(objectKey, out var tags))
            {
                tags.TryRemove(tagKey, out _);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task SetTagsAsync(string objectKey, IEnumerable<Tag> tags, CancellationToken ct = default)
        {
            var objectTags = _store.GetOrAdd(objectKey, _ => new BoundedDictionary<TagKey, Tag>(1000));
            foreach (var tag in tags)
            {
                objectTags[tag.Key] = tag;
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<string>> FindObjectsByTagAsync(
            TagKey tagKey, TagValue? value, int limit, CancellationToken ct = default)
        {
            var results = new List<string>();

            foreach (var kvp in _store)
            {
                if (results.Count >= limit)
                    break;

                ct.ThrowIfCancellationRequested();

                if (kvp.Value.TryGetValue(tagKey, out var tag))
                {
                    if (value is null || tag.Value.Equals(value))
                    {
                        results.Add(kvp.Key);
                    }
                }
            }

            return Task.FromResult<IReadOnlyList<string>>(results);
        }
    }
}
