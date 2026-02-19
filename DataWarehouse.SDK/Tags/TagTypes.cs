using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags
{
    /// <summary>
    /// A qualified tag key composed of a namespace and name.
    /// String representation is "Namespace:Name".
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag types")]
    public sealed record TagKey(string Namespace, string Name)
    {
        /// <summary>Returns the qualified key as "Namespace:Name".</summary>
        public override string ToString() => $"{Namespace}:{Name}";

        /// <summary>Parses a "Namespace:Name" string into a <see cref="TagKey"/>.</summary>
        public static TagKey Parse(string input)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(input);
            var idx = input.IndexOf(':');
            if (idx <= 0 || idx >= input.Length - 1)
                throw new FormatException($"Invalid TagKey format: '{input}'. Expected 'Namespace:Name'.");
            return new TagKey(input[..idx], input[(idx + 1)..]);
        }
    }

    /// <summary>
    /// A fully qualified tag with key, typed value, source provenance,
    /// access control, version, and timestamps.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag types")]
    public sealed record Tag
    {
        public required TagKey Key { get; init; }
        public required TagValue Value { get; init; }
        public required TagSourceInfo Source { get; init; }
        public TagAcl Acl { get; init; } = TagAcl.FullAccess;
        public long Version { get; init; } = 1;
        public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset ModifiedUtc { get; init; } = DateTimeOffset.UtcNow;
        public string? SchemaId { get; init; }
    }

    /// <summary>
    /// An immutable collection of tags indexed by <see cref="TagKey"/>.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag types")]
    public sealed record TagCollection : IEnumerable<Tag>
    {
        private readonly IReadOnlyDictionary<TagKey, Tag> _tags;

        public TagCollection(IReadOnlyDictionary<TagKey, Tag> tags)
        {
            _tags = tags ?? throw new ArgumentNullException(nameof(tags));
        }

        public static TagCollection Empty { get; } = new(new Dictionary<TagKey, Tag>());

        public Tag? this[TagKey key] => _tags.TryGetValue(key, out var tag) ? tag : null;

        public Tag? Get(string ns, string name) => this[new TagKey(ns, name)];

        public IEnumerable<Tag> GetByNamespace(string ns) =>
            _tags.Values.Where(t => t.Key.Namespace.Equals(ns, StringComparison.OrdinalIgnoreCase));

        public IEnumerable<Tag> GetBySource(TagSource source) =>
            _tags.Values.Where(t => t.Source.Source.HasFlag(source));

        public int Count => _tags.Count;

        public bool ContainsKey(TagKey key) => _tags.ContainsKey(key);

        public IEnumerator<Tag> GetEnumerator() => _tags.Values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Mutable builder for constructing <see cref="TagCollection"/> instances.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag types")]
    public sealed class TagCollectionBuilder
    {
        private readonly Dictionary<TagKey, Tag> _tags = new();

        public TagCollectionBuilder Add(Tag tag)
        {
            _tags[tag.Key] = tag;
            return this;
        }

        public TagCollectionBuilder Remove(TagKey key)
        {
            _tags.Remove(key);
            return this;
        }

        public TagCollection Build() => new(new Dictionary<TagKey, Tag>(_tags));
    }
}
