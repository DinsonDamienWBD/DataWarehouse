using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Sql;

/// <summary>
/// Comparison operator for zone map predicate evaluation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Zone map index (VOPT-17)")]
public enum ComparisonOp : byte
{
    /// <summary>Equality comparison (value == X).</summary>
    Equal = 0,

    /// <summary>Inequality comparison (value != X).</summary>
    NotEqual = 1,

    /// <summary>Less-than comparison (value &lt; X).</summary>
    LessThan = 2,

    /// <summary>Less-or-equal comparison (value &lt;= X).</summary>
    LessOrEqual = 3,

    /// <summary>Greater-than comparison (value &gt; X).</summary>
    GreaterThan = 4,

    /// <summary>Greater-or-equal comparison (value &gt;= X).</summary>
    GreaterOrEqual = 5,

    /// <summary>IS NULL check (value is null).</summary>
    IsNull = 6,

    /// <summary>IS NOT NULL check (value is not null).</summary>
    IsNotNull = 7,
}

/// <summary>
/// A comparison predicate for zone map evaluation.
/// Carries the comparison operator and the scalar value to compare against.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Zone map index (VOPT-17)")]
public readonly struct ComparisonPredicate
{
    /// <summary>The comparison operator.</summary>
    public ComparisonOp Op { get; }

    /// <summary>The scalar value to compare against (unused for IsNull/IsNotNull).</summary>
    public long Value { get; }

    /// <summary>Creates a new comparison predicate.</summary>
    public ComparisonPredicate(ComparisonOp op, long value = 0)
    {
        Op = op;
        Value = value;
    }
}

/// <summary>
/// A 40-byte zone map entry storing per-extent min/max/null_count metadata
/// for O(1) predicate evaluation at extent granularity.
/// </summary>
/// <remarks>
/// Wire format (40 bytes, little-endian):
/// <code>
///   [0..8)   MinValue          (long)  - minimum value (or hash for strings)
///   [8..16)  MaxValue          (long)  - maximum value (or hash for strings)
///   [16..20) NullCount         (int)   - number of null values in this extent
///   [20..24) RowCount          (int)   - total number of rows in this extent
///   [24..32) ExtentStartBlock  (long)  - physical start block of the extent
///   [32..36) ExtentBlockCount  (int)   - number of blocks in the extent
///   [36..40) Reserved          (int)   - reserved for future use
/// </code>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Zone map index (VOPT-17)")]
public readonly struct ZoneMapEntry : IEquatable<ZoneMapEntry>
{
    /// <summary>Serialized size of a zone map entry in bytes.</summary>
    public const int SerializedSize = 40;

    /// <summary>Minimum value in this extent (or hash for variable-length types).</summary>
    public long MinValue { get; }

    /// <summary>Maximum value in this extent (or hash for variable-length types).</summary>
    public long MaxValue { get; }

    /// <summary>Number of null values in this extent.</summary>
    public int NullCount { get; }

    /// <summary>Total number of rows in this extent.</summary>
    public int RowCount { get; }

    /// <summary>Physical start block of the extent.</summary>
    public long ExtentStartBlock { get; }

    /// <summary>Number of blocks in the extent.</summary>
    public int ExtentBlockCount { get; }

    /// <summary>Creates a new zone map entry.</summary>
    public ZoneMapEntry(long minValue, long maxValue, int nullCount, int rowCount, long extentStartBlock, int extentBlockCount)
    {
        MinValue = minValue;
        MaxValue = maxValue;
        NullCount = nullCount;
        RowCount = rowCount;
        ExtentStartBlock = extentStartBlock;
        ExtentBlockCount = extentBlockCount;
    }

    /// <summary>Serializes this entry to exactly 40 bytes in little-endian format.</summary>
    public static void Serialize(in ZoneMapEntry entry, Span<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        BinaryPrimitives.WriteInt64LittleEndian(buffer[..8], entry.MinValue);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[8..16], entry.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[16..20], entry.NullCount);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[20..24], entry.RowCount);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[24..32], entry.ExtentStartBlock);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[32..36], entry.ExtentBlockCount);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[36..40], 0); // Reserved
    }

    /// <summary>Deserializes a zone map entry from exactly 40 bytes in little-endian format.</summary>
    public static ZoneMapEntry Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        return new ZoneMapEntry(
            minValue: BinaryPrimitives.ReadInt64LittleEndian(buffer[..8]),
            maxValue: BinaryPrimitives.ReadInt64LittleEndian(buffer[8..16]),
            nullCount: BinaryPrimitives.ReadInt32LittleEndian(buffer[16..20]),
            rowCount: BinaryPrimitives.ReadInt32LittleEndian(buffer[20..24]),
            extentStartBlock: BinaryPrimitives.ReadInt64LittleEndian(buffer[24..32]),
            extentBlockCount: BinaryPrimitives.ReadInt32LittleEndian(buffer[32..36]));
    }

    /// <inheritdoc />
    public bool Equals(ZoneMapEntry other)
        => MinValue == other.MinValue
        && MaxValue == other.MaxValue
        && NullCount == other.NullCount
        && RowCount == other.RowCount
        && ExtentStartBlock == other.ExtentStartBlock
        && ExtentBlockCount == other.ExtentBlockCount;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ZoneMapEntry other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(MinValue, MaxValue, NullCount, RowCount, ExtentStartBlock, ExtentBlockCount);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ZoneMapEntry left, ZoneMapEntry right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ZoneMapEntry left, ZoneMapEntry right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString()
        => $"ZoneMap(Min={MinValue}, Max={MaxValue}, Nulls={NullCount}, Rows={RowCount}, Block={ExtentStartBlock}, Count={ExtentBlockCount})";
}

/// <summary>
/// Per-extent zone map metadata index enabling predicate pushdown to skip
/// entire extents without reading data. Zone map entries are stored in a
/// dedicated ZMAP index block per table, packed sequentially.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Zone map index (VOPT-17)")]
public sealed class ZoneMapIndex
{
    private readonly IBlockDevice _device;
    private readonly long _zoneMapRegionStart;
    private readonly int _blockSize;

    // In-memory cache: extent start block -> zone map entry (ConcurrentDictionary for thread safety)
    private readonly ConcurrentDictionary<long, ZoneMapEntry> _entries = new();

    /// <summary>Number of entries that fit in a single block.</summary>
    private int EntriesPerBlock => (_blockSize - 4) / ZoneMapEntry.SerializedSize; // 4 bytes for entry count header

    /// <summary>
    /// Creates a new zone map index.
    /// </summary>
    /// <param name="device">Block device for I/O.</param>
    /// <param name="zoneMapRegionStart">Start block of the zone map region.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public ZoneMapIndex(IBlockDevice device, long zoneMapRegionStart, int blockSize)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _zoneMapRegionStart = zoneMapRegionStart;
        _blockSize = blockSize > 0 ? blockSize : throw new ArgumentOutOfRangeException(nameof(blockSize));
    }

    /// <summary>
    /// Adds or updates a zone map entry for an extent.
    /// Persists the entry to the zone map index block on disk.
    /// </summary>
    /// <param name="extentStartBlock">Physical start block of the extent.</param>
    /// <param name="extentBlockCount">Number of blocks in the extent.</param>
    /// <param name="entry">The zone map entry to store.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AddEntryAsync(long extentStartBlock, int extentBlockCount, ZoneMapEntry entry, CancellationToken ct = default)
    {
        _entries[extentStartBlock] = entry;
        await FlushToDiskAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves the zone map entry for a specific extent.
    /// Returns null if no entry exists for the given extent.
    /// </summary>
    /// <param name="extentStartBlock">Physical start block of the extent.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The zone map entry, or null if not found.</returns>
    public async Task<ZoneMapEntry?> GetEntryAsync(long extentStartBlock, CancellationToken ct = default)
    {
        // Try cache first
        if (_entries.TryGetValue(extentStartBlock, out var cached))
            return cached;

        // Try loading from disk
        await LoadFromDiskAsync(ct).ConfigureAwait(false);

        return _entries.TryGetValue(extentStartBlock, out var entry) ? entry : null;
    }

    /// <summary>
    /// Evaluates whether an extent can be skipped based on zone map metadata and a comparison predicate.
    /// Returns true if the entire extent can be safely skipped (no rows can match).
    /// </summary>
    /// <param name="entry">Zone map entry for the extent.</param>
    /// <param name="predicate">The comparison predicate to evaluate.</param>
    /// <returns>True if the extent can be skipped; false if it must be scanned.</returns>
    public static bool CanSkipExtent(ZoneMapEntry entry, ComparisonPredicate predicate)
    {
        return predicate.Op switch
        {
            // value == X: skip if X is outside [min, max]
            ComparisonOp.Equal => predicate.Value < entry.MinValue || predicate.Value > entry.MaxValue,

            // value != X: skip if all rows have value X (min == max == X)
            ComparisonOp.NotEqual => entry.MinValue == entry.MaxValue && entry.MinValue == predicate.Value,

            // value < X: skip if min >= X (all values are >= X, so none < X)
            ComparisonOp.LessThan => entry.MinValue >= predicate.Value,

            // value <= X: skip if min > X
            ComparisonOp.LessOrEqual => entry.MinValue > predicate.Value,

            // value > X: skip if max <= X (all values are <= X, so none > X)
            ComparisonOp.GreaterThan => entry.MaxValue <= predicate.Value,

            // value >= X: skip if max < X
            ComparisonOp.GreaterOrEqual => entry.MaxValue < predicate.Value,

            // IS NULL: skip if no nulls in extent
            ComparisonOp.IsNull => entry.NullCount == 0,

            // IS NOT NULL: skip if all values are null
            ComparisonOp.IsNotNull => entry.NullCount == entry.RowCount,

            _ => false,
        };
    }

    /// <summary>
    /// Filters a list of extents, returning only those that might contain matching rows.
    /// Extents whose zone maps prove no match are skipped.
    /// </summary>
    /// <param name="extents">List of (StartBlock, BlockCount) extent descriptors.</param>
    /// <param name="predicate">The comparison predicate to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async sequence of extent start blocks that cannot be skipped.</returns>
    public async IAsyncEnumerable<long> FilterExtentsAsync(
        IReadOnlyList<(long StartBlock, int BlockCount)> extents,
        ComparisonPredicate predicate,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(extents);

        // Ensure all entries are loaded
        await LoadFromDiskAsync(ct).ConfigureAwait(false);

        foreach (var (startBlock, blockCount) in extents)
        {
            ct.ThrowIfCancellationRequested();

            if (_entries.TryGetValue(startBlock, out var entry))
            {
                if (CanSkipExtent(entry, predicate))
                    continue; // Zone map proves no match -- skip this extent
            }

            // No zone map entry or cannot prove skip -- must scan
            yield return startBlock;
        }
    }

    // ── Persistence ─────────────────────────────────────────────────────

    private async Task FlushToDiskAsync(CancellationToken ct)
    {
        var entries = _entries.Values.ToArray();
        int entriesPerBlock = EntriesPerBlock;
        int blocksNeeded = (entries.Length + entriesPerBlock - 1) / Math.Max(entriesPerBlock, 1);
        if (blocksNeeded == 0) blocksNeeded = 1;

        for (int blockIdx = 0; blockIdx < blocksNeeded; blockIdx++)
        {
            var blockData = new byte[_blockSize];
            int startEntry = blockIdx * entriesPerBlock;
            int entryCount = Math.Min(entriesPerBlock, entries.Length - startEntry);

            // Header: [entryCount:4LE]
            BinaryPrimitives.WriteInt32LittleEndian(blockData.AsSpan(0, 4), entryCount);

            // Pack entries sequentially
            for (int i = 0; i < entryCount; i++)
            {
                int offset = 4 + i * ZoneMapEntry.SerializedSize;
                ZoneMapEntry.Serialize(entries[startEntry + i], blockData.AsSpan(offset));
            }

            await _device.WriteBlockAsync(_zoneMapRegionStart + blockIdx, blockData, ct).ConfigureAwait(false);
        }
    }

    private async Task LoadFromDiskAsync(CancellationToken ct)
    {
        var blockData = new byte[_blockSize];
        await _device.ReadBlockAsync(_zoneMapRegionStart, blockData, ct).ConfigureAwait(false);

        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(blockData.AsSpan(0, 4));
        if (entryCount <= 0) return;

        // Cap entryCount to a reasonable upper bound to prevent corrupt-disk runaway I/O
        const int MaxEntryCount = 1_000_000;
        if (entryCount > MaxEntryCount)
            throw new InvalidDataException($"Zone map entry count {entryCount} exceeds maximum {MaxEntryCount}. Possible data corruption.");

        int entriesPerBlock = EntriesPerBlock;
        int blocksNeeded = (entryCount + entriesPerBlock - 1) / Math.Max(entriesPerBlock, 1);

        int totalLoaded = 0;
        for (int blockIdx = 0; blockIdx < blocksNeeded; blockIdx++)
        {
            if (blockIdx > 0)
            {
                await _device.ReadBlockAsync(_zoneMapRegionStart + blockIdx, blockData, ct).ConfigureAwait(false);
            }

            int blockEntryCount = BinaryPrimitives.ReadInt32LittleEndian(blockData.AsSpan(0, 4));

            for (int i = 0; i < blockEntryCount && totalLoaded < entryCount; i++)
            {
                int offset = 4 + i * ZoneMapEntry.SerializedSize;
                if (offset + ZoneMapEntry.SerializedSize > _blockSize) break;

                var entry = ZoneMapEntry.Deserialize(blockData.AsSpan(offset));
                _entries[entry.ExtentStartBlock] = entry;
                totalLoaded++;
            }
        }
    }
}
