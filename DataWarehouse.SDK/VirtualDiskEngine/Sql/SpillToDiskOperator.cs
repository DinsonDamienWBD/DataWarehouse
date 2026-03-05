using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;

namespace DataWarehouse.SDK.VirtualDiskEngine.Sql;

/// <summary>
/// Statistics from a spill-to-disk aggregation operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Spill-to-disk aggregation (VOPT-19)")]
public readonly struct SpillStats
{
    /// <summary>Total rows processed.</summary>
    public long RowsProcessed { get; init; }

    /// <summary>Rows that were spilled to disk.</summary>
    public long RowsSpilled { get; init; }

    /// <summary>Number of hash partitions created during spill.</summary>
    public int PartitionsCreated { get; init; }

    /// <summary>Number of temporary blocks used for spill storage.</summary>
    public long TempBlocksUsed { get; init; }

    /// <inheritdoc />
    public override string ToString()
        => $"SpillStats(Processed={RowsProcessed}, Spilled={RowsSpilled}, Partitions={PartitionsCreated}, TempBlocks={TempBlocksUsed})";
}

/// <summary>
/// Abstract base class for aggregate functions used by the spill-to-disk operator.
/// Subclasses implement accumulation and merge for distributed aggregation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Spill-to-disk aggregation (VOPT-19)")]
public abstract class AggregateFunction
{
    /// <summary>Accumulates a single value into the aggregate state.</summary>
    /// <param name="value">The raw value bytes to accumulate.</param>
    public abstract void Accumulate(ReadOnlySpan<byte> value);

    /// <summary>Returns the current aggregate result as a byte array.</summary>
    /// <returns>Serialized aggregate state.</returns>
    public abstract byte[] GetResult();

    /// <summary>Merges another aggregate state into this one (for partition merge).</summary>
    /// <param name="other">Serialized aggregate state from another partition.</param>
    public abstract void Merge(byte[] other);

    /// <summary>Resets this aggregate to its initial state.</summary>
    public abstract void Reset();

    /// <summary>Creates a fresh instance of the same aggregate type.</summary>
    public abstract AggregateFunction Clone();
}

/// <summary>Sum aggregate: accumulates int64 sum of 8-byte little-endian values.</summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Spill-to-disk aggregation (VOPT-19)")]
public sealed class SumAggregate : AggregateFunction
{
    private long _sum;

    /// <inheritdoc />
    public override void Accumulate(ReadOnlySpan<byte> value)
    {
        if (value.Length >= 8)
            _sum += BinaryPrimitives.ReadInt64LittleEndian(value);
        else if (value.Length >= 4)
            _sum += BinaryPrimitives.ReadInt32LittleEndian(value);
    }

    /// <inheritdoc />
    public override byte[] GetResult()
    {
        var result = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(result, _sum);
        return result;
    }

    /// <inheritdoc />
    public override void Merge(byte[] other)
    {
        if (other.Length >= 8)
            _sum += BinaryPrimitives.ReadInt64LittleEndian(other);
    }

    /// <inheritdoc />
    public override void Reset() => _sum = 0;

    /// <inheritdoc />
    public override AggregateFunction Clone() => new SumAggregate();
}

/// <summary>Count aggregate: counts the number of accumulated values.</summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Spill-to-disk aggregation (VOPT-19)")]
public sealed class CountAggregate : AggregateFunction
{
    private long _count;

    /// <inheritdoc />
    public override void Accumulate(ReadOnlySpan<byte> value) => _count++;

    /// <inheritdoc />
    public override byte[] GetResult()
    {
        var result = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(result, _count);
        return result;
    }

    /// <inheritdoc />
    public override void Merge(byte[] other)
    {
        if (other.Length >= 8)
            _count += BinaryPrimitives.ReadInt64LittleEndian(other);
    }

    /// <inheritdoc />
    public override void Reset() => _count = 0;

    /// <inheritdoc />
    public override AggregateFunction Clone() => new CountAggregate();
}

/// <summary>Min aggregate: tracks the minimum int64 value seen.</summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Spill-to-disk aggregation (VOPT-19)")]
public sealed class MinAggregate : AggregateFunction
{
    private long _min = long.MaxValue;
    private bool _hasValue;

    /// <inheritdoc />
    public override void Accumulate(ReadOnlySpan<byte> value)
    {
        long v = value.Length >= 8
            ? BinaryPrimitives.ReadInt64LittleEndian(value)
            : value.Length >= 4
                ? BinaryPrimitives.ReadInt32LittleEndian(value)
                : 0;

        if (!_hasValue || v < _min)
        {
            _min = v;
            _hasValue = true;
        }
    }

    /// <inheritdoc />
    public override byte[] GetResult()
    {
        var result = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(result, _hasValue ? _min : 0);
        return result;
    }

    /// <inheritdoc />
    public override void Merge(byte[] other)
    {
        if (other.Length >= 8)
        {
            long v = BinaryPrimitives.ReadInt64LittleEndian(other);
            if (!_hasValue || v < _min)
            {
                _min = v;
                _hasValue = true;
            }
        }
    }

    /// <inheritdoc />
    public override void Reset() { _min = long.MaxValue; _hasValue = false; }

    /// <inheritdoc />
    public override AggregateFunction Clone() => new MinAggregate();
}

/// <summary>Max aggregate: tracks the maximum int64 value seen.</summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Spill-to-disk aggregation (VOPT-19)")]
public sealed class MaxAggregate : AggregateFunction
{
    private long _max = long.MinValue;
    private bool _hasValue;

    /// <inheritdoc />
    public override void Accumulate(ReadOnlySpan<byte> value)
    {
        long v = value.Length >= 8
            ? BinaryPrimitives.ReadInt64LittleEndian(value)
            : value.Length >= 4
                ? BinaryPrimitives.ReadInt32LittleEndian(value)
                : 0;

        if (!_hasValue || v > _max)
        {
            _max = v;
            _hasValue = true;
        }
    }

    /// <inheritdoc />
    public override byte[] GetResult()
    {
        var result = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(result, _hasValue ? _max : 0);
        return result;
    }

    /// <inheritdoc />
    public override void Merge(byte[] other)
    {
        if (other.Length >= 8)
        {
            long v = BinaryPrimitives.ReadInt64LittleEndian(other);
            if (!_hasValue || v > _max)
            {
                _max = v;
                _hasValue = true;
            }
        }
    }

    /// <inheritdoc />
    public override void Reset() { _max = long.MinValue; _hasValue = false; }

    /// <inheritdoc />
    public override AggregateFunction Clone() => new MaxAggregate();
}

/// <summary>Average aggregate: tracks sum and count for average computation.</summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Spill-to-disk aggregation (VOPT-19)")]
public sealed class AvgAggregate : AggregateFunction
{
    private long _sum;
    private long _count;

    /// <inheritdoc />
    public override void Accumulate(ReadOnlySpan<byte> value)
    {
        if (value.Length >= 8)
            _sum += BinaryPrimitives.ReadInt64LittleEndian(value);
        else if (value.Length >= 4)
            _sum += BinaryPrimitives.ReadInt32LittleEndian(value);

        _count++;
    }

    /// <inheritdoc />
    public override byte[] GetResult()
    {
        // Return [sum:8][count:8]
        var result = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(0, 8), _sum);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(8, 8), _count);
        return result;
    }

    /// <inheritdoc />
    public override void Merge(byte[] other)
    {
        if (other.Length >= 16)
        {
            _sum += BinaryPrimitives.ReadInt64LittleEndian(other.AsSpan(0, 8));
            _count += BinaryPrimitives.ReadInt64LittleEndian(other.AsSpan(8, 8));
        }
    }

    /// <inheritdoc />
    public override void Reset() { _sum = 0; _count = 0; }

    /// <inheritdoc />
    public override AggregateFunction Clone() => new AvgAggregate();

    /// <summary>Gets the computed average value.</summary>
    public double GetAverage() => _count > 0 ? (double)_sum / _count : 0.0;
}

/// <summary>
/// Operator for hash-partitioned spill-to-disk aggregation.
/// When memory budget is exceeded during aggregation, spills in-memory hash table
/// partitions to temporary VDE blocks. Processes each spilled partition individually
/// during the merge phase to stay within memory budget.
/// </summary>
/// <remarks>
/// Implements VOPT-19: Spill-to-disk for large aggregations.
/// Two-phase execution:
/// 1. Build: Hash-partition input rows, accumulate in memory, spill when budget exceeded
/// 2. Probe: Process each spilled partition individually (fits in memory), merge results
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Spill-to-disk aggregation (VOPT-19)")]
public sealed class SpillToDiskOperator : IAsyncDisposable
{
    private readonly IBlockDevice _device;
    private readonly IBlockAllocator _allocator;
    private readonly int _blockSize;
    private readonly long _memoryBudgetBytes;

    /// <summary>Default number of hash partitions for spill.</summary>
    private const int DefaultPartitionCount = 64;

    // Tracks temp blocks for cleanup
    private readonly ConcurrentBag<long> _tempBlocks = new();
    private long _currentMemoryUsage;

    // Cat 13 (finding 923): dictionary keys are byte[] serialized as Base64 strings.
    // Base64 adds ~33% size overhead per key and requires encode/decode on every lookup.
    // For a large-aggregation hot path, replace with a byte[]-keyed dictionary using
    // a span-aware IEqualityComparer<byte[]> (e.g., ByteArrayEqualityComparer) to avoid
    // the string allocation entirely. The current Base64 approach is functionally correct
    // but suboptimal for high-cardinality aggregation workloads.

    /// <summary>Last operation statistics.</summary>
    public SpillStats LastStats { get; private set; }

    /// <summary>
    /// Creates a new spill-to-disk aggregation operator.
    /// </summary>
    /// <param name="device">Block device for temporary spill storage.</param>
    /// <param name="allocator">Block allocator for temporary blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="memoryBudgetBytes">Maximum memory budget before spilling (default 64 MB).</param>
    public SpillToDiskOperator(IBlockDevice device, IBlockAllocator allocator, int blockSize, long memoryBudgetBytes = 64 * 1024 * 1024)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _blockSize = blockSize > 0 ? blockSize : throw new ArgumentOutOfRangeException(nameof(blockSize));
        _memoryBudgetBytes = memoryBudgetBytes > 0 ? memoryBudgetBytes : throw new ArgumentOutOfRangeException(nameof(memoryBudgetBytes));
    }

    /// <summary>
    /// Performs aggregation with automatic spill-to-disk when memory budget is exceeded.
    /// </summary>
    /// <param name="input">Async stream of (GroupKey, RowData) tuples.</param>
    /// <param name="aggregates">Aggregate functions to apply per group.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async stream of (GroupKey, AggregateState) results.</returns>
    public async IAsyncEnumerable<(byte[] Key, byte[] AggregateState)> AggregateWithSpillAsync(
        IAsyncEnumerable<(byte[] Key, byte[] Row)> input,
        AggregateFunction[] aggregates,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(aggregates);

        // Phase 1: Build hash table with spill
        var inMemoryGroups = new Dictionary<string, AggregateFunction[]>(StringComparer.Ordinal);
        var spilledPartitions = new Dictionary<int, List<long>>(); // partition -> list of block numbers
        long rowsProcessed = 0;
        long rowsSpilled = 0;
        int partitionsCreated = 0;
        long tempBlocksUsed = 0;

        _currentMemoryUsage = 0;

        await foreach (var (key, row) in input.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            rowsProcessed++;

            string keyStr = Convert.ToBase64String(key);
            int partition = GetPartition(key, DefaultPartitionCount);

            // Check if we need to spill
            if (_currentMemoryUsage >= _memoryBudgetBytes && !inMemoryGroups.ContainsKey(keyStr))
            {
                // Spill current in-memory state for this partition to disk
                await SpillPartitionAsync(inMemoryGroups, spilledPartitions, partition, aggregates.Length, ct).ConfigureAwait(false);
                rowsSpilled += CountKeysInPartition(inMemoryGroups, DefaultPartitionCount, partition);
                RemovePartitionKeys(inMemoryGroups, DefaultPartitionCount, partition);

                if (!spilledPartitions.ContainsKey(partition))
                {
                    partitionsCreated++;
                    spilledPartitions[partition] = new List<long>();
                }

                tempBlocksUsed = _tempBlocks.Count;
            }

            // Accumulate in memory
            if (!inMemoryGroups.TryGetValue(keyStr, out var groupAggs))
            {
                groupAggs = new AggregateFunction[aggregates.Length];
                for (int i = 0; i < aggregates.Length; i++)
                    groupAggs[i] = aggregates[i].Clone();

                inMemoryGroups[keyStr] = groupAggs;
                _currentMemoryUsage += key.Length + 64 + aggregates.Length * 32; // Approximate
            }

            for (int i = 0; i < groupAggs.Length; i++)
                groupAggs[i].Accumulate(row);
        }

        // Phase 2: Yield in-memory results
        foreach (var (keyStr, groupAggs) in inMemoryGroups)
        {
            ct.ThrowIfCancellationRequested();
            byte[] key = Convert.FromBase64String(keyStr);
            byte[] state = MergeAggregateResults(groupAggs);
            yield return (key, state);
        }

        // Phase 2b: Process spilled partitions one at a time
        foreach (var (partition, blocks) in spilledPartitions)
        {
            ct.ThrowIfCancellationRequested();

            var partitionGroups = new Dictionary<string, AggregateFunction[]>(StringComparer.Ordinal);

            foreach (long blockNum in blocks)
            {
                var blockData = new byte[_blockSize];
                await _device.ReadBlockAsync(blockNum, blockData, ct).ConfigureAwait(false);

                // Deserialize spilled entries from block
                int offset = 0;
                while (offset + 4 < blockData.Length)
                {
                    int entryLen = BinaryPrimitives.ReadInt32LittleEndian(blockData.AsSpan(offset, 4));
                    if (entryLen <= 0) break; // end-of-block sentinel (zero padding)
                    offset += 4;

                    if (offset + entryLen > blockData.Length)
                    {
                        // Malformed block: entry claims more bytes than remain — log and skip block
                        System.Diagnostics.Debug.WriteLine($"[SpillToDiskOperator] Block {blockNum}: malformed entry at offset {offset - 4}, entryLen={entryLen} exceeds block boundary. Skipping remainder of block.");
                        break;
                    }

                    // Entry format: [keyLen:4][key:N][aggState:M]
                    int keyLen = BinaryPrimitives.ReadInt32LittleEndian(blockData.AsSpan(offset, 4));
                    offset += 4;

                    if (offset + keyLen > blockData.Length) break;
                    string spilledKeyStr = Convert.ToBase64String(blockData.AsSpan(offset, keyLen));
                    offset += keyLen;

                    int stateLen = entryLen - 4 - keyLen;
                    if (stateLen <= 0 || offset + stateLen > blockData.Length) break;

                    byte[] spilledState = blockData.AsSpan(offset, stateLen).ToArray();
                    offset += stateLen;

                    // Merge into partition groups
                    if (!partitionGroups.TryGetValue(spilledKeyStr, out var pAggs))
                    {
                        pAggs = new AggregateFunction[aggregates.Length];
                        for (int i = 0; i < aggregates.Length; i++)
                            pAggs[i] = aggregates[i].Clone();
                        partitionGroups[spilledKeyStr] = pAggs;
                    }

                    // Merge the spilled aggregate state
                    int stateOffset = 0;
                    for (int i = 0; i < pAggs.Length && stateOffset < spilledState.Length; i++)
                    {
                        int aggStateLen = Math.Min(spilledState.Length - stateOffset, 16);
                        pAggs[i].Merge(spilledState.AsSpan(stateOffset, aggStateLen).ToArray());
                        stateOffset += aggStateLen;
                    }
                }
            }

            // Yield partition results
            foreach (var (keyStr, pAggs) in partitionGroups)
            {
                byte[] key = Convert.FromBase64String(keyStr);
                byte[] state = MergeAggregateResults(pAggs);
                yield return (key, state);
            }
        }

        LastStats = new SpillStats
        {
            RowsProcessed = rowsProcessed,
            RowsSpilled = rowsSpilled,
            PartitionsCreated = partitionsCreated,
            TempBlocksUsed = tempBlocksUsed,
        };
    }

    /// <summary>
    /// Frees all temporary blocks used during spill operations.
    /// Must be called after query completion to reclaim disk space.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public Task CleanupAsync(CancellationToken ct = default)
    {
        while (_tempBlocks.TryTake(out long blockNum))
        {
            ct.ThrowIfCancellationRequested();
            _allocator.FreeBlock(blockNum);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CleanupAsync().ConfigureAwait(false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static int GetPartition(byte[] key, int partitionCount)
    {
        // Simple hash-based partitioning
        var hash = new HashCode();
        hash.AddBytes(key);
        return (int)((uint)hash.ToHashCode() % (uint)partitionCount);
    }

    private async Task SpillPartitionAsync(
        Dictionary<string, AggregateFunction[]> groups,
        Dictionary<int, List<long>> spilledPartitions,
        int partition,
        int aggCount,
        CancellationToken ct)
    {
        // Collect keys belonging to this partition
        var keysToSpill = new List<(string KeyStr, AggregateFunction[] Aggs)>();

        foreach (var (keyStr, aggs) in groups)
        {
            byte[] keyBytes = Convert.FromBase64String(keyStr);
            if (GetPartition(keyBytes, DefaultPartitionCount) == partition)
                keysToSpill.Add((keyStr, aggs));
        }

        if (keysToSpill.Count == 0) return;

        // Serialize and write to temp blocks
        var blockData = new byte[_blockSize];
        int offset = 0;

        if (!spilledPartitions.TryGetValue(partition, out var blockList))
        {
            blockList = new List<long>();
            spilledPartitions[partition] = blockList;
        }

        foreach (var (keyStr, aggs) in keysToSpill)
        {
            byte[] keyBytes = Convert.FromBase64String(keyStr);
            byte[] aggState = MergeAggregateResults(aggs);

            // Entry: [totalLen:4][keyLen:4][key:N][aggState:M]
            int entryLen = 4 + keyBytes.Length + aggState.Length;

            if (offset + 4 + entryLen > _blockSize)
            {
                // Flush current block
                long blockNum = _allocator.AllocateBlock(ct);
                await _device.WriteBlockAsync(blockNum, blockData, ct).ConfigureAwait(false);
                blockList.Add(blockNum);
                _tempBlocks.Add(blockNum);
                blockData = new byte[_blockSize];
                offset = 0;
            }

            BinaryPrimitives.WriteInt32LittleEndian(blockData.AsSpan(offset, 4), entryLen);
            offset += 4;

            BinaryPrimitives.WriteInt32LittleEndian(blockData.AsSpan(offset, 4), keyBytes.Length);
            offset += 4;

            keyBytes.CopyTo(blockData.AsSpan(offset));
            offset += keyBytes.Length;

            aggState.CopyTo(blockData.AsSpan(offset));
            offset += aggState.Length;
        }

        // Flush remaining data
        if (offset > 0)
        {
            long blockNum = _allocator.AllocateBlock(ct);
            await _device.WriteBlockAsync(blockNum, blockData, ct).ConfigureAwait(false);
            blockList.Add(blockNum);
            _tempBlocks.Add(blockNum);
        }
    }

    private static byte[] MergeAggregateResults(AggregateFunction[] aggs)
    {
        using var ms = new MemoryStream();
        foreach (var agg in aggs)
        {
            byte[] result = agg.GetResult();
            ms.Write(result);
        }
        return ms.ToArray();
    }

    private static int CountKeysInPartition(Dictionary<string, AggregateFunction[]> groups, int partitionCount, int partition)
    {
        int count = 0;
        foreach (var keyStr in groups.Keys)
        {
            byte[] keyBytes = Convert.FromBase64String(keyStr);
            if (GetPartition(keyBytes, partitionCount) == partition)
                count++;
        }
        return count;
    }

    private static void RemovePartitionKeys(Dictionary<string, AggregateFunction[]> groups, int partitionCount, int partition)
    {
        var toRemove = new List<string>();
        foreach (var keyStr in groups.Keys)
        {
            byte[] keyBytes = Convert.FromBase64String(keyStr);
            if (GetPartition(keyBytes, partitionCount) == partition)
                toRemove.Add(keyStr);
        }
        foreach (var key in toRemove)
            groups.Remove(key);
    }
}
