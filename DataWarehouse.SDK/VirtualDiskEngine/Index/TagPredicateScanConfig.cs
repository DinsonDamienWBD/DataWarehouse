using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Index;

/// <summary>
/// Operation type for inline tag predicate evaluation.
/// </summary>
public enum TagPredicateOp
{
    /// <summary>Tag must exist (any value accepted).</summary>
    Exists,

    /// <summary>Tag value must equal the predicate value bytes exactly.</summary>
    Equals,

    /// <summary>Tag value must not equal the predicate value bytes.</summary>
    NotEquals,

    /// <summary>Tag value bytes must start with the predicate value bytes.</summary>
    StartsWith,

    /// <summary>Tag value (interpreted as little-endian uint64) must be greater than predicate uint64.</summary>
    GreaterThan,

    /// <summary>Tag value (interpreted as little-endian uint64) must be less than predicate uint64.</summary>
    LessThan,
}

/// <summary>
/// A single tag predicate used during inline tag predicate scanning.
/// </summary>
/// <remarks>
/// Tag slot layout (32 bytes):
/// [NamespaceHash:4][NameHash:4][ValueType:1][ValueLen:1][Value:&lt;=22B][Pad]
/// Predicate matching first checks NamespaceHash + NameHash for fast rejection,
/// then applies Op against the value bytes.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-54 inline tag predicate scanning")]
public sealed record TagPredicate
{
    /// <summary>FNV-1a or XxHash32 of the tag namespace string.</summary>
    public uint NamespaceHash { get; init; }

    /// <summary>FNV-1a or XxHash32 of the tag name string.</summary>
    public uint NameHash { get; init; }

    /// <summary>Comparison operation to apply against the matched tag's value bytes.</summary>
    public TagPredicateOp Op { get; init; }

    /// <summary>
    /// Value bytes to compare against the tag slot value.
    /// Empty for <see cref="TagPredicateOp.Exists"/>.
    /// Maximum 22 bytes (inline tag slot limit).
    /// </summary>
    public ReadOnlyMemory<byte> Value { get; init; }

    /// <summary>
    /// Creates a new predicate that checks tag existence only.
    /// </summary>
    public static TagPredicate Exists(uint namespaceHash, uint nameHash) =>
        new() { NamespaceHash = namespaceHash, NameHash = nameHash, Op = TagPredicateOp.Exists };

    /// <summary>
    /// Creates a new equality predicate.
    /// </summary>
    public static TagPredicate Equal(uint namespaceHash, uint nameHash, ReadOnlyMemory<byte> value) =>
        new() { NamespaceHash = namespaceHash, NameHash = nameHash, Op = TagPredicateOp.Equals, Value = value };
}

/// <summary>
/// Configuration for <see cref="InlineTagPredicateScanner"/>.
/// Controls batch sizing, SIMD acceleration, overflow inclusion, and result limits.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-54 inline tag predicate scan configuration")]
public sealed class TagPredicateScanConfig
{
    /// <summary>
    /// Number of inode-table blocks to read per batch.
    /// Default: 64 (covers 512 inodes at 8 inodes per 4KB block).
    /// </summary>
    public int BatchSizeBlocks { get; set; } = 64;

    /// <summary>
    /// Whether to use SIMD (Vector256) acceleration for hash matching when available.
    /// When false, falls back to scalar comparison loop.
    /// Default: true.
    /// </summary>
    public bool UseSimdAcceleration { get; set; } = true;

    /// <summary>
    /// If true, the scanner also reads overflow tag blocks referenced by inodes
    /// that have more tags than fit inline (beyond MaxInlineSlots).
    /// Default: false (inline tags only for maximum throughput).
    /// </summary>
    public bool IncludeOverflowInodes { get; set; } = false;

    /// <summary>
    /// Maximum number of matching inode numbers to collect before stopping.
    /// Use to cap result memory for very broad queries.
    /// Default: 10,000.
    /// </summary>
    public int MaxResultCount { get; set; } = 10_000;
}
