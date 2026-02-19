using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// Enumerates available merge modes for resolving concurrent tag updates
/// when two nodes modify the same tag key simultaneously.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: CRDT tag versioning")]
public enum TagMergeMode
{
    /// <summary>
    /// Last-writer-wins: the tag with the higher <see cref="Tag.ModifiedUtc"/> timestamp wins.
    /// Ties are broken by lexicographic nodeId comparison (deterministic).
    /// </summary>
    LastWriterWins = 0,

    /// <summary>
    /// Multi-value: keep all concurrent values wrapped in a <see cref="ListTagValue"/>.
    /// Allows manual resolution by a downstream consumer or user.
    /// </summary>
    MultiValue = 1,

    /// <summary>
    /// Union: for <see cref="ListTagValue"/> tags, union the list items (set union, preserving order of first seen).
    /// For all other tag value types, falls back to LWW semantics.
    /// </summary>
    Union = 2,

    /// <summary>
    /// Custom: use a user-provided merge function via <see cref="ITagMergeStrategy"/>.
    /// This mode is for extension; the factory cannot create it without an explicit implementation.
    /// </summary>
    Custom = 3
}

/// <summary>
/// Strategy interface for merging two concurrent versions of the same tag.
/// Implementations must be deterministic: given the same inputs, they must produce the same output.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: CRDT tag versioning")]
public interface ITagMergeStrategy
{
    /// <summary>
    /// Merges a local tag with a remote tag when their version vectors are concurrent.
    /// </summary>
    /// <param name="local">The local node's version of the tag.</param>
    /// <param name="remote">The remote node's version of the tag.</param>
    /// <param name="localVersion">The version vector from the local node.</param>
    /// <param name="remoteVersion">The version vector from the remote node.</param>
    /// <returns>The merged tag.</returns>
    Tag Merge(Tag local, Tag remote, TagVersionVector localVersion, TagVersionVector remoteVersion);
}

/// <summary>
/// Last-writer-wins merge strategy. Compares <see cref="Tag.ModifiedUtc"/> timestamps;
/// the tag with the higher timestamp wins. Equal timestamps use a deterministic
/// tiebreaker based on the source node identifier.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: CRDT tag versioning")]
public sealed class LwwTagMergeStrategy : ITagMergeStrategy
{
    /// <inheritdoc />
    public Tag Merge(Tag local, Tag remote, TagVersionVector localVersion, TagVersionVector remoteVersion)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);

        if (remote.ModifiedUtc > local.ModifiedUtc)
            return remote;

        if (remote.ModifiedUtc == local.ModifiedUtc)
        {
            // Deterministic tiebreaker: compare source IDs lexicographically
            var localSourceId = local.Source.SourceId ?? string.Empty;
            var remoteSourceId = remote.Source.SourceId ?? string.Empty;
            if (string.Compare(remoteSourceId, localSourceId, StringComparison.Ordinal) > 0)
                return remote;
        }

        return local;
    }
}

/// <summary>
/// Multi-value merge strategy. Wraps both concurrent values in a <see cref="ListTagValue"/>,
/// preserving both for manual resolution. If either value is already a list, the items
/// are flattened into the result list to avoid nesting.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: CRDT tag versioning")]
public sealed class MultiValueTagMergeStrategy : ITagMergeStrategy
{
    /// <inheritdoc />
    public Tag Merge(Tag local, Tag remote, TagVersionVector localVersion, TagVersionVector remoteVersion)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);

        var items = new List<TagValue>();

        // Flatten if already a list, otherwise add as single item
        if (local.Value is ListTagValue localList)
            items.AddRange(localList.Items);
        else
            items.Add(local.Value);

        if (remote.Value is ListTagValue remoteList)
            items.AddRange(remoteList.Items);
        else
            items.Add(remote.Value);

        var mergedVersion = TagVersionVector.Merge(localVersion, remoteVersion);
        var newerTag = remote.ModifiedUtc >= local.ModifiedUtc ? remote : local;

        return newerTag with
        {
            Value = TagValue.List(items),
            Version = Math.Max(local.Version, remote.Version) + 1,
            ModifiedUtc = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// Union merge strategy. For <see cref="ListTagValue"/> tags, computes the set union of items
/// (preserving first-seen order). For all other tag value types, falls back to LWW semantics.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: CRDT tag versioning")]
public sealed class UnionTagMergeStrategy : ITagMergeStrategy
{
    private static readonly LwwTagMergeStrategy LwwFallback = new();

    /// <inheritdoc />
    public Tag Merge(Tag local, Tag remote, TagVersionVector localVersion, TagVersionVector remoteVersion)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);

        // Only apply union semantics for ListTagValue; fall back to LWW for others
        if (local.Value is not ListTagValue localList || remote.Value is not ListTagValue remoteList)
            return LwwFallback.Merge(local, remote, localVersion, remoteVersion);

        // Set union preserving order: local items first, then remote items not already present
        var seen = new HashSet<TagValue>(localList.Items);
        var unionItems = new List<TagValue>(localList.Items);
        foreach (var item in remoteList.Items)
        {
            if (seen.Add(item))
                unionItems.Add(item);
        }

        var newerTag = remote.ModifiedUtc >= local.ModifiedUtc ? remote : local;
        return newerTag with
        {
            Value = TagValue.List(unionItems),
            Version = Math.Max(local.Version, remote.Version) + 1,
            ModifiedUtc = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// Factory for creating <see cref="ITagMergeStrategy"/> instances from <see cref="TagMergeMode"/> values.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: CRDT tag versioning")]
public static class TagMergeStrategyFactory
{
    private static readonly LwwTagMergeStrategy Lww = new();
    private static readonly MultiValueTagMergeStrategy MultiValue = new();
    private static readonly UnionTagMergeStrategy Union = new();

    /// <summary>
    /// Gets the default merge strategy (Last-Writer-Wins).
    /// </summary>
    public static ITagMergeStrategy Default => Lww;

    /// <summary>
    /// Creates a merge strategy for the given mode.
    /// </summary>
    /// <param name="mode">The merge mode.</param>
    /// <returns>The corresponding <see cref="ITagMergeStrategy"/> implementation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="mode"/> is <see cref="TagMergeMode.Custom"/>
    /// (custom strategies must be provided directly, not created via factory).</exception>
    public static ITagMergeStrategy Create(TagMergeMode mode) => mode switch
    {
        TagMergeMode.LastWriterWins => Lww,
        TagMergeMode.MultiValue => MultiValue,
        TagMergeMode.Union => Union,
        TagMergeMode.Custom => throw new ArgumentOutOfRangeException(nameof(mode),
            "Custom merge mode requires providing an ITagMergeStrategy implementation directly."),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), $"Unknown merge mode: {mode}")
    };
}
