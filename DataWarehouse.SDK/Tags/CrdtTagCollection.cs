using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Infrastructure.Distributed;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// CRDT-backed tag collection that supports conflict-free multi-node concurrent writes.
/// Uses an <see cref="SdkORSet"/> for tracking tag key presence (add/remove with OR-Set semantics)
/// and per-key version vectors with configurable merge strategies for value conflict resolution.
/// </summary>
/// <remarks>
/// <para>The merge operation is commutative, associative, and idempotent:</para>
/// <list type="bullet">
/// <item><description><b>Key presence:</b> OR-Set semantics (concurrent add+remove: add wins)</description></item>
/// <item><description><b>Value conflicts:</b> Resolved by per-tag merge strategy (default: LWW)</description></item>
/// <item><description><b>Version vectors:</b> Merged per-node max to maintain causal ordering</description></item>
/// </list>
/// <para>Integrates with <see cref="OrSetPruner"/> for garbage-collecting tombstones.</para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 55: CRDT tag collection")]
public sealed class CrdtTagCollection
{
    private readonly SdkORSet _tagKeys;
    private readonly BoundedDictionary<TagKey, (Tag Tag, TagVersionVector Version)> _tagValues;
    private readonly string _nodeId;
    private readonly TagMergeMode _defaultMode;
    private readonly BoundedDictionary<TagKey, TagMergeMode> _mergeOverrides;
    private TagVersionVector _currentVersion;

    /// <summary>
    /// Creates a new CRDT tag collection for the specified node.
    /// </summary>
    /// <param name="nodeId">This node's unique identifier, used for OR-Set tags and version vector increments.</param>
    /// <param name="defaultMode">The default merge mode for concurrent value conflicts. Defaults to <see cref="TagMergeMode.LastWriterWins"/>.</param>
    public CrdtTagCollection(string nodeId, TagMergeMode defaultMode = TagMergeMode.LastWriterWins)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        _nodeId = nodeId;
        _defaultMode = defaultMode;
        _tagKeys = new SdkORSet();
        _tagValues = new BoundedDictionary<TagKey, (Tag, TagVersionVector)>(1000);
        _mergeOverrides = new BoundedDictionary<TagKey, TagMergeMode>(1000);
        _currentVersion = new TagVersionVector();
    }

    // Private constructor for deserialization and merge results
    private CrdtTagCollection(
        string nodeId,
        TagMergeMode defaultMode,
        SdkORSet tagKeys,
        BoundedDictionary<TagKey, (Tag Tag, TagVersionVector Version)> tagValues,
        BoundedDictionary<TagKey, TagMergeMode> mergeOverrides,
        TagVersionVector currentVersion)
    {
        _nodeId = nodeId;
        _defaultMode = defaultMode;
        _tagKeys = tagKeys;
        _tagValues = tagValues;
        _mergeOverrides = mergeOverrides;
        _currentVersion = currentVersion;
    }

    /// <summary>
    /// Gets this node's identifier.
    /// </summary>
    public string NodeId => _nodeId;

    /// <summary>
    /// Gets the current version vector for this collection.
    /// </summary>
    public TagVersionVector CurrentVersion => _currentVersion;

    // ── Write Operations ────────────────────────────────────────────

    /// <summary>
    /// Sets (adds or updates) a tag in the collection. Adds the key to the OR-Set
    /// and stores the tag with an incremented version vector.
    /// </summary>
    /// <param name="key">The tag key.</param>
    /// <param name="tag">The tag to store.</param>
    public void Set(TagKey key, Tag tag)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(tag);

        var keyStr = key.ToString();
        // Only add to OR-Set if not already present
        if (!_tagKeys.Elements.Contains(keyStr))
            _tagKeys.Add(keyStr, _nodeId);

        _currentVersion = _currentVersion.Increment(_nodeId);
        _tagValues[key] = (tag, _currentVersion);
    }

    /// <summary>
    /// Removes a tag from the collection using OR-Set tombstone semantics.
    /// The key is removed from the OR-Set; if concurrently re-added on another node, the add wins.
    /// </summary>
    /// <param name="key">The tag key to remove.</param>
    public void Remove(TagKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var keyStr = key.ToString();
        _tagKeys.Remove(keyStr);
        _tagValues.TryRemove(key, out _);
        _currentVersion = _currentVersion.Increment(_nodeId);
    }

    /// <summary>
    /// Overrides the merge strategy for a specific tag key.
    /// </summary>
    /// <param name="key">The tag key to configure.</param>
    /// <param name="mode">The merge mode to use for this key.</param>
    public void SetMergeMode(TagKey key, TagMergeMode mode)
    {
        ArgumentNullException.ThrowIfNull(key);
        _mergeOverrides[key] = mode;
    }

    // ── Read Operations ─────────────────────────────────────────────

    /// <summary>
    /// Gets the tag with the specified key, or <c>null</c> if the key is not present in the OR-Set.
    /// </summary>
    /// <param name="key">The tag key to look up.</param>
    /// <returns>The tag if present; otherwise <c>null</c>.</returns>
    public Tag? Get(TagKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var keyStr = key.ToString();
        if (!_tagKeys.Elements.Contains(keyStr))
            return null;

        return _tagValues.TryGetValue(key, out var entry) ? entry.Tag : null;
    }

    /// <summary>
    /// Gets the set of currently present tag keys (from OR-Set Elements).
    /// </summary>
    public IReadOnlySet<TagKey> Keys
    {
        get
        {
            var keys = new HashSet<TagKey>();
            foreach (var element in _tagKeys.Elements)
            {
                if (TagKey.TryParse(element, out var tagKey) && tagKey is not null)
                    keys.Add(tagKey);
            }
            return keys;
        }
    }

    /// <summary>
    /// Materializes the current CRDT state as an immutable <see cref="TagCollection"/>.
    /// Only includes tags whose keys are present in the OR-Set (not tombstoned).
    /// </summary>
    /// <returns>A snapshot of the current tag collection state.</returns>
    public TagCollection ToTagCollection()
    {
        var builder = new TagCollectionBuilder();
        foreach (var element in _tagKeys.Elements)
        {
            if (TagKey.TryParse(element, out var tagKey) &&
                tagKey is not null &&
                _tagValues.TryGetValue(tagKey, out var entry))
            {
                builder.Add(entry.Tag);
            }
        }
        return builder.Build();
    }

    // ── Merge (Core CRDT Operation) ─────────────────────────────────

    /// <summary>
    /// Merges this collection with another, producing a new merged <see cref="CrdtTagCollection"/>.
    /// <list type="number">
    /// <item><description>Merges OR-Sets: handles add/remove conflicts (add wins if concurrent)</description></item>
    /// <item><description>For each key in merged OR-Set: resolves values using version vectors and merge strategies</description></item>
    /// <item><description>Merges version vectors: takes max per node</description></item>
    /// </list>
    /// </summary>
    /// <param name="other">The remote collection to merge with.</param>
    /// <returns>A new <see cref="CrdtTagCollection"/> containing the merged state.</returns>
    public CrdtTagCollection Merge(CrdtTagCollection other)
    {
        ArgumentNullException.ThrowIfNull(other);

        // Step 1: Merge OR-Sets for key presence
        var mergedOrSet = (SdkORSet)_tagKeys.Merge(other._tagKeys);

        // Step 2: Merge tag values for each key present in merged OR-Set
        var mergedValues = new BoundedDictionary<TagKey, (Tag Tag, TagVersionVector Version)>(1000);

        foreach (var element in mergedOrSet.Elements)
        {
            if (!TagKey.TryParse(element, out var tagKey) || tagKey is null)
                continue;

            var hasLocal = _tagValues.TryGetValue(tagKey, out var localEntry);
            var hasRemote = other._tagValues.TryGetValue(tagKey, out var remoteEntry);

            if (hasLocal && !hasRemote)
            {
                mergedValues[tagKey] = localEntry;
            }
            else if (!hasLocal && hasRemote)
            {
                mergedValues[tagKey] = remoteEntry;
            }
            else if (hasLocal && hasRemote)
            {
                // Both sides have the value -- check version vectors
                if (localEntry.Version.Dominates(remoteEntry.Version))
                {
                    mergedValues[tagKey] = localEntry;
                }
                else if (remoteEntry.Version.Dominates(localEntry.Version))
                {
                    mergedValues[tagKey] = remoteEntry;
                }
                else
                {
                    // Concurrent: apply merge strategy
                    var strategy = GetMergeStrategy(tagKey, other);
                    var mergedTag = strategy.Merge(
                        localEntry.Tag, remoteEntry.Tag,
                        localEntry.Version, remoteEntry.Version);
                    var mergedTagVersion = TagVersionVector.Merge(localEntry.Version, remoteEntry.Version);
                    mergedValues[tagKey] = (mergedTag, mergedTagVersion);
                }
            }
        }

        // Step 3: Merge version vectors
        var mergedVersion = TagVersionVector.Merge(_currentVersion, other._currentVersion);

        // Step 4: Merge merge-overrides (union, prefer local if conflict)
        var mergedOverrides = new BoundedDictionary<TagKey, TagMergeMode>(1000);
        foreach (var (key, mode) in other._mergeOverrides)
        {
            mergedOverrides.TryAdd(key, mode);
        }

        return new CrdtTagCollection(
            _nodeId,
            _defaultMode,
            mergedOrSet,
            mergedValues,
            mergedOverrides,
            mergedVersion);
    }

    // ── Serialization ───────────────────────────────────────────────

    /// <summary>
    /// Serializes this CRDT tag collection to a byte array for network transport or persistence.
    /// </summary>
    /// <returns>The serialized byte array.</returns>
    public byte[] Serialize()
    {
        var tagEntries = new Dictionary<string, CrdtTagEntry>();
        foreach (var (key, (tag, version)) in _tagValues)
        {
            tagEntries[key.ToString()] = new CrdtTagEntry
            {
                TagJson = JsonSerializer.SerializeToUtf8Bytes(TagToSerializable(tag)),
                VersionJson = version.Serialize()
            };
        }

        var overrides = new Dictionary<string, int>();
        foreach (var (key, mode) in _mergeOverrides)
        {
            overrides[key.ToString()] = (int)mode;
        }

        var envelope = new CrdtTagCollectionData
        {
            NodeId = _nodeId,
            DefaultMode = (int)_defaultMode,
            OrSetData = _tagKeys.Serialize(),
            TagEntries = tagEntries,
            MergeOverrides = overrides,
            CurrentVersion = _currentVersion.Serialize()
        };

        return JsonSerializer.SerializeToUtf8Bytes(envelope);
    }

    /// <summary>
    /// Deserializes a CRDT tag collection from a byte array.
    /// </summary>
    /// <param name="data">The byte array to deserialize.</param>
    /// <param name="nodeId">The node ID for the deserialized collection (may differ from original).</param>
    /// <returns>The deserialized <see cref="CrdtTagCollection"/>.</returns>
    public static CrdtTagCollection Deserialize(byte[] data, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        var envelope = JsonSerializer.Deserialize<CrdtTagCollectionData>(data)
                       ?? throw new InvalidOperationException("Failed to deserialize CrdtTagCollection envelope.");

        var orSet = SdkORSet.Deserialize(envelope.OrSetData ?? Array.Empty<byte>());
        var defaultMode = (TagMergeMode)(envelope.DefaultMode);
        var currentVersion = TagVersionVector.Deserialize(envelope.CurrentVersion ?? Array.Empty<byte>());

        var tagValues = new BoundedDictionary<TagKey, (Tag, TagVersionVector)>(1000);
        if (envelope.TagEntries != null)
        {
            foreach (var (keyStr, entry) in envelope.TagEntries)
            {
                if (TagKey.TryParse(keyStr, out var tagKey) && tagKey is not null && entry.TagJson != null)
                {
                    var serializable = JsonSerializer.Deserialize<SerializableTag>(entry.TagJson);
                    if (serializable != null)
                    {
                        var tag = TagFromSerializable(serializable);
                        var version = TagVersionVector.Deserialize(entry.VersionJson ?? Array.Empty<byte>());
                        tagValues[tagKey] = (tag, version);
                    }
                }
            }
        }

        var mergeOverrides = new BoundedDictionary<TagKey, TagMergeMode>(1000);
        if (envelope.MergeOverrides != null)
        {
            foreach (var (keyStr, modeInt) in envelope.MergeOverrides)
            {
                if (TagKey.TryParse(keyStr, out var tagKey) && tagKey is not null)
                {
                    mergeOverrides[tagKey] = (TagMergeMode)modeInt;
                }
            }
        }

        return new CrdtTagCollection(nodeId, defaultMode, orSet, tagValues, mergeOverrides, currentVersion);
    }

    // ── Pruning Integration ─────────────────────────────────────────

    /// <summary>
    /// Prunes tombstones from the underlying OR-Set and removes orphaned tag values.
    /// Delegates to <see cref="OrSetPruner"/> for the actual OR-Set garbage collection.
    /// </summary>
    /// <param name="options">Pruning configuration options.</param>
    /// <param name="activeNodes">Set of known active node IDs (reserved for future use).</param>
    /// <returns>Metrics describing what was pruned.</returns>
    internal OrSetPruneResult Prune(OrSetPruneOptions options, IReadOnlySet<string>? activeNodes = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var result = OrSetPruner.Prune(_tagKeys, options, activeNodes);

        // Remove orphaned tag values (keys no longer in OR-Set and not in add-set at all)
        var currentElements = _tagKeys.Elements;
        var orphanedKeys = new List<TagKey>();
        foreach (var (key, _) in _tagValues)
        {
            if (!currentElements.Contains(key.ToString()))
            {
                // Check if it's truly gone (not in any set)
                var addTags = _tagKeys.GetAddTags(key.ToString());
                if (addTags == null || addTags.Count == 0)
                    orphanedKeys.Add(key);
            }
        }

        foreach (var key in orphanedKeys)
        {
            _tagValues.TryRemove(key, out _);
        }

        return result;
    }

    // ── Private Helpers ─────────────────────────────────────────────

    private ITagMergeStrategy GetMergeStrategy(TagKey key, CrdtTagCollection other)
    {
        // Check local overrides first, then remote overrides, then default
        if (_mergeOverrides.TryGetValue(key, out var localMode))
            return TagMergeStrategyFactory.Create(localMode);
        if (other._mergeOverrides.TryGetValue(key, out var remoteMode))
            return TagMergeStrategyFactory.Create(remoteMode);
        return TagMergeStrategyFactory.Create(_defaultMode);
    }

    // ── Serialization DTOs ──────────────────────────────────────────

    private static SerializableTag TagToSerializable(Tag tag) => new()
    {
        Namespace = tag.Key.Namespace,
        Name = tag.Key.Name,
        ValueKind = (int)tag.Value.Kind,
        ValueStr = tag.Value.ToString() ?? string.Empty,
        SourceType = (int)tag.Source.Source,
        SourceId = tag.Source.SourceId,
        SourceName = tag.Source.SourceName,
        Version = tag.Version,
        CreatedUtc = tag.CreatedUtc,
        ModifiedUtc = tag.ModifiedUtc,
        SchemaId = tag.SchemaId
    };

    private static Tag TagFromSerializable(SerializableTag s)
    {
        var key = new TagKey(s.Namespace ?? "default", s.Name ?? "unknown");
        var value = ReconstructTagValue((TagValueKind)(s.ValueKind), s.ValueStr ?? string.Empty);

        return new Tag
        {
            Key = key,
            Value = value,
            Source = new TagSourceInfo((TagSource)(s.SourceType), s.SourceId, s.SourceName),
            Version = s.Version,
            CreatedUtc = s.CreatedUtc,
            ModifiedUtc = s.ModifiedUtc,
            SchemaId = s.SchemaId
        };
    }

    private static TagValue ReconstructTagValue(TagValueKind kind, string valueStr) => kind switch
    {
        TagValueKind.String => TagValue.String(valueStr),
        TagValueKind.Number => decimal.TryParse(valueStr, out var num) ? TagValue.Number(num) : TagValue.String(valueStr),
        TagValueKind.Bool => TagValue.Bool(bool.TryParse(valueStr, out var b) && b),
        _ => TagValue.String(valueStr) // Fallback: preserve as string
    };

    private sealed class CrdtTagCollectionData
    {
        [JsonPropertyName("nodeId")]
        public string? NodeId { get; set; }

        [JsonPropertyName("defaultMode")]
        public int DefaultMode { get; set; }

        [JsonPropertyName("orSetData")]
        public byte[]? OrSetData { get; set; }

        [JsonPropertyName("tagEntries")]
        public Dictionary<string, CrdtTagEntry>? TagEntries { get; set; }

        [JsonPropertyName("mergeOverrides")]
        public Dictionary<string, int>? MergeOverrides { get; set; }

        [JsonPropertyName("currentVersion")]
        public byte[]? CurrentVersion { get; set; }
    }

    private sealed class CrdtTagEntry
    {
        [JsonPropertyName("tag")]
        public byte[]? TagJson { get; set; }

        [JsonPropertyName("version")]
        public byte[]? VersionJson { get; set; }
    }

    private sealed class SerializableTag
    {
        [JsonPropertyName("ns")]
        public string? Namespace { get; set; }

        [JsonPropertyName("n")]
        public string? Name { get; set; }

        [JsonPropertyName("vk")]
        public int ValueKind { get; set; }

        [JsonPropertyName("vs")]
        public string? ValueStr { get; set; }

        [JsonPropertyName("st")]
        public int SourceType { get; set; }

        [JsonPropertyName("si")]
        public string? SourceId { get; set; }

        [JsonPropertyName("sn")]
        public string? SourceName { get; set; }

        [JsonPropertyName("v")]
        public long Version { get; set; }

        [JsonPropertyName("c")]
        public DateTimeOffset CreatedUtc { get; set; }

        [JsonPropertyName("m")]
        public DateTimeOffset ModifiedUtc { get; set; }

        [JsonPropertyName("sc")]
        public string? SchemaId { get; set; }
    }
}
