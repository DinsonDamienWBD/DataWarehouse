using System.Collections;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// A qualified tag key composed of a namespace and name.
/// String representation is <c>"Namespace:Name"</c>.
/// Equality is ordinal and case-sensitive on both components.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag key")]
public sealed record TagKey
{
    /// <summary>The tag namespace (e.g., "system", "user", "ai.classification").</summary>
    public string Namespace { get; }

    /// <summary>The tag name within the namespace (e.g., "priority", "color").</summary>
    public string Name { get; }

    /// <summary>Creates a validated TagKey with non-empty namespace and name.</summary>
    /// <param name="ns">The tag namespace.</param>
    /// <param name="name">The tag name within the namespace.</param>
    public TagKey(string ns, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns, nameof(ns));
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        this.Namespace = ns;
        this.Name = name;
    }

    /// <summary>Returns the qualified key as <c>"Namespace:Name"</c>.</summary>
    public override string ToString() => $"{Namespace}:{Name}";

    /// <summary>
    /// Parses a <c>"Namespace:Name"</c> string into a <see cref="TagKey"/>.
    /// </summary>
    /// <param name="input">The string to parse. Must contain exactly one colon separating non-empty parts.</param>
    /// <returns>A new <see cref="TagKey"/> instance.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="input"/> is null, empty, or whitespace.</exception>
    /// <exception cref="FormatException">Thrown when <paramref name="input"/> does not match the expected format.</exception>
    public static TagKey Parse(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        var idx = input.IndexOf(':');
        if (idx <= 0 || idx >= input.Length - 1)
            throw new FormatException($"Invalid TagKey format: '{input}'. Expected 'Namespace:Name'.");
        return new TagKey(input[..idx], input[(idx + 1)..]);
    }

    /// <summary>
    /// Attempts to parse a <c>"Namespace:Name"</c> string into a <see cref="TagKey"/>.
    /// </summary>
    /// <param name="input">The string to parse.</param>
    /// <param name="result">When successful, the parsed <see cref="TagKey"/>; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> if parsing succeeded; otherwise <c>false</c>.</returns>
    public static bool TryParse(string? input, out TagKey? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var idx = input.IndexOf(':');
        if (idx <= 0 || idx >= input.Length - 1) return false;
        result = new TagKey(input[..idx], input[(idx + 1)..]);
        return true;
    }
}

/// <summary>
/// A fully qualified tag with key, typed value, source provenance,
/// access control, monotonic version counter, and timestamps.
/// </summary>
/// <remarks>
/// Tags are the core unit of the universal tag system. Every tag is:
/// <list type="bullet">
/// <item><description>Keyed by a <see cref="TagKey"/> (namespace + name)</description></item>
/// <item><description>Strongly typed via <see cref="TagValue"/> discriminated union</description></item>
/// <item><description>Tracked for provenance via <see cref="TagSourceInfo"/></description></item>
/// <item><description>Access-controlled via <see cref="TagAcl"/></description></item>
/// <item><description>Versioned with a monotonic counter (starts at 1, increments on each update)</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag record")]
public sealed record Tag
{
    /// <summary>The qualified tag key (namespace + name).</summary>
    public required TagKey Key { get; init; }

    /// <summary>The strongly-typed tag value.</summary>
    public required TagValue Value { get; init; }

    /// <summary>Provenance information: who or what created/last modified this tag.</summary>
    public required TagSourceInfo Source { get; init; }

    /// <summary>Per-tag access control list. Defaults to <see cref="TagAcl.FullAccess"/>.</summary>
    public TagAcl Acl { get; init; } = TagAcl.FullAccess;

    /// <summary>
    /// Monotonic version counter. Starts at 1 and increments on each update.
    /// Used for optimistic concurrency and conflict detection.
    /// </summary>
    public long Version { get; init; } = 1;

    /// <summary>UTC timestamp of when this tag was first created.</summary>
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>UTC timestamp of when this tag was last modified.</summary>
    public DateTimeOffset ModifiedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Optional link to a schema in the TagSchemaRegistry.
    /// When set, the tag value must conform to the referenced schema.
    /// </summary>
    public string? SchemaId { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"Tag({Key}={Value}, v{Version})";
}

/// <summary>
/// An immutable collection of tags indexed by <see cref="TagKey"/>.
/// Provides query helpers for namespace-based and source-based filtering.
/// </summary>
/// <remarks>
/// Use <see cref="TagCollectionBuilder"/> to construct instances.
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag collection")]
public sealed class TagCollection : IEnumerable<Tag>
{
    private readonly IReadOnlyDictionary<TagKey, Tag> _tags;
    // Cat 13: cache the hash code — collection is immutable, computing OrderBy on every GetHashCode is O(n log n)
    private int _cachedHashCode;
    private bool _hashCodeComputed;

    /// <summary>
    /// Initializes a new <see cref="TagCollection"/> wrapping the given dictionary.
    /// </summary>
    /// <param name="tags">The tag dictionary. Must not be null.</param>
    public TagCollection(IReadOnlyDictionary<TagKey, Tag> tags)
    {
        _tags = tags ?? throw new ArgumentNullException(nameof(tags));
    }

    /// <summary>An empty tag collection singleton.</summary>
    public static TagCollection Empty { get; } = new(new Dictionary<TagKey, Tag>());

    /// <summary>
    /// Gets the tag with the specified key, or <c>null</c> if not present.
    /// </summary>
    /// <param name="key">The tag key to look up.</param>
    public Tag? this[TagKey key] => _tags.TryGetValue(key, out var tag) ? tag : null;

    /// <summary>
    /// Gets a tag by namespace and name, or <c>null</c> if not present.
    /// </summary>
    /// <param name="ns">The tag namespace.</param>
    /// <param name="name">The tag name.</param>
    public Tag? Get(string ns, string name) => this[new TagKey(ns, name)];

    /// <summary>
    /// Returns all tags in the specified namespace (ordinal case-sensitive match,
    /// consistent with <see cref="TagKey"/> equality semantics).
    /// </summary>
    /// <param name="ns">The namespace to filter by.</param>
    public IEnumerable<Tag> GetByNamespace(string ns) =>
        _tags.Values.Where(t => t.Key.Namespace.Equals(ns, StringComparison.Ordinal));

    /// <summary>
    /// Returns all tags whose source matches the specified <see cref="TagSource"/> flags.
    /// </summary>
    /// <param name="source">The source flag(s) to filter by.</param>
    public IEnumerable<Tag> GetBySource(TagSource source) =>
        _tags.Values.Where(t => t.Source.Source.HasFlag(source));

    /// <summary>Gets the number of tags in this collection.</summary>
    public int Count => _tags.Count;

    /// <summary>
    /// Determines whether this collection contains a tag with the specified key.
    /// </summary>
    /// <param name="key">The tag key to check.</param>
    public bool ContainsKey(TagKey key) => _tags.ContainsKey(key);

    /// <inheritdoc />
    public IEnumerator<Tag> GetEnumerator() => _tags.Values.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public override string ToString() => $"TagCollection({Count} tags)";

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is TagCollection other && Count == other.Count &&
        _tags.All(kvp => other._tags.TryGetValue(kvp.Key, out var otherTag) && Equals(kvp.Value, otherTag));

    /// <inheritdoc />
#pragma warning disable S2328 // TagCollection._tags is effectively immutable after construction; lazy hash cache is intentional.
    public override int GetHashCode()
    {
        if (_hashCodeComputed) return _cachedHashCode;
        var hash = new HashCode();
        foreach (var kvp in _tags.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
        {
            hash.Add(kvp.Key);
            hash.Add(kvp.Value);
        }
        _cachedHashCode = hash.ToHashCode();
        _hashCodeComputed = true;
        return _cachedHashCode;
    }
#pragma warning restore S2328
}

/// <summary>
/// Mutable builder for constructing <see cref="TagCollection"/> instances.
/// Supports fluent <c>Add</c>/<c>Remove</c> chaining.
/// </summary>
/// <example>
/// <code>
/// var collection = new TagCollectionBuilder()
///     .Add(myTag1)
///     .Add(myTag2)
///     .Build();
/// </code>
/// </example>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag collection builder")]
public sealed class TagCollectionBuilder
{
    private readonly Dictionary<TagKey, Tag> _tags = new();

    /// <summary>
    /// Adds or replaces a tag in the builder. If a tag with the same key exists, it is overwritten.
    /// </summary>
    /// <param name="tag">The tag to add.</param>
    /// <returns>This builder for fluent chaining.</returns>
    public TagCollectionBuilder Add(Tag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        _tags[tag.Key] = tag;
        return this;
    }

    /// <summary>
    /// Removes the tag with the specified key, if present.
    /// </summary>
    /// <param name="key">The key of the tag to remove.</param>
    /// <returns>This builder for fluent chaining.</returns>
    public TagCollectionBuilder Remove(TagKey key)
    {
        _tags.Remove(key);
        return this;
    }

    /// <summary>
    /// Builds an immutable <see cref="TagCollection"/> from the current state.
    /// The builder can continue to be used after calling <c>Build()</c>.
    /// </summary>
    /// <returns>A new <see cref="TagCollection"/> snapshot.</returns>
    public TagCollection Build() => new(new Dictionary<TagKey, Tag>(_tags));
}
