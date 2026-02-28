// 91.F4: RAID-Aware Deduplication
using System.Security.Cryptography;
using System.Threading;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateRAID.Features;

/// <summary>
/// 91.F4: RAID-Aware Deduplication - Inline and post-RAID deduplication
/// with dedup-aware parity calculation optimization.
/// </summary>
public sealed class RaidDeduplication
{
    private readonly BoundedDictionary<string, DedupIndex> _dedupIndexes = new BoundedDictionary<string, DedupIndex>(1000);
    private readonly BoundedDictionary<byte[], DedupEntry> _hashIndex = new(1000, new ByteArrayComparer());
    private readonly DedupConfiguration _config;
    private long _totalBlocksProcessed;
    private long _duplicateBlocksFound;
    private long _bytesSaved;

    public RaidDeduplication(DedupConfiguration? config = null)
    {
        _config = config ?? new DedupConfiguration();
    }

    /// <summary>
    /// 91.F4.1: Perform inline deduplication before RAID striping.
    /// </summary>
    public InlineDedupResult DeduplicateInline(
        string arrayId,
        byte[] data,
        long logicalAddress,
        DedupMode mode = DedupMode.FixedBlock)
    {
        var index = _dedupIndexes.GetOrAdd(arrayId, _ => new DedupIndex(arrayId));
        var chunks = ChunkData(data, mode);
        var result = new InlineDedupResult { OriginalSize = data.Length };

        var dedupedChunks = new List<DedupChunk>();

        foreach (var chunk in chunks)
        {
            Interlocked.Increment(ref _totalBlocksProcessed);

            var hash = ComputeHash(chunk.Data);
            var entry = index.Lookup(hash);

            if (entry != null)
            {
                // Duplicate found - reference existing block
                Interlocked.Increment(ref _duplicateBlocksFound);
                Interlocked.Add(ref _bytesSaved, chunk.Data.Length);

                entry.ReferenceCount++;
                entry.LastReferenced = DateTime.UtcNow;

                dedupedChunks.Add(new DedupChunk
                {
                    Offset = chunk.Offset,
                    Length = chunk.Length,
                    Hash = hash,
                    IsDuplicate = true,
                    ReferencedAddress = entry.StorageAddress
                });

                result.DuplicateBlocks++;
            }
            else
            {
                // New unique block
                var storageAddress = AllocateStorageAddress(arrayId);

                entry = new DedupEntry
                {
                    Hash = hash,
                    StorageAddress = storageAddress,
                    Size = chunk.Data.Length,
                    ReferenceCount = 1,
                    CreatedTime = DateTime.UtcNow,
                    LastReferenced = DateTime.UtcNow
                };

                index.Add(hash, entry);
                _hashIndex[hash] = entry;

                dedupedChunks.Add(new DedupChunk
                {
                    Offset = chunk.Offset,
                    Length = chunk.Length,
                    Hash = hash,
                    IsDuplicate = false,
                    Data = chunk.Data,
                    StorageAddress = storageAddress
                });

                result.UniqueBlocks++;
            }
        }

        result.DedupedSize = dedupedChunks.Where(c => !c.IsDuplicate).Sum(c => c.Length);
        result.DedupRatio = result.OriginalSize > 0
            ? 1.0 - ((double)result.DedupedSize / result.OriginalSize)
            : 0.0;
        result.Chunks = dedupedChunks;

        return result;
    }

    /// <summary>
    /// 91.F4.2: Perform post-RAID deduplication across stripes.
    /// </summary>
    public async Task<PostRaidDedupResult> DeduplicatePostRaidAsync(
        string arrayId,
        long startAddress,
        long endAddress,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new PostRaidDedupResult
        {
            ArrayId = arrayId,
            StartAddress = startAddress,
            EndAddress = endAddress,
            StartTime = DateTime.UtcNow
        };

        var index = _dedupIndexes.GetOrAdd(arrayId, _ => new DedupIndex(arrayId));
        var totalBlocks = (endAddress - startAddress) / _config.BlockSize;
        var processedBlocks = 0L;

        // Phase 1: Scan and identify duplicates
        var duplicateGroups = new Dictionary<byte[], List<long>>(new ByteArrayComparer());

        for (var address = startAddress; address < endAddress; address += _config.BlockSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var data = await ReadBlockFromRaidAsync(arrayId, address, cancellationToken);
            var hash = ComputeHash(data);

            if (!duplicateGroups.ContainsKey(hash))
            {
                duplicateGroups[hash] = new List<long>();
            }
            duplicateGroups[hash].Add(address);

            processedBlocks++;
            if (processedBlocks % 1000 == 0)
            {
                progress?.Report(0.5 * processedBlocks / totalBlocks);
            }
        }

        // Phase 2: Consolidate duplicates
        foreach (var group in duplicateGroups.Where(g => g.Value.Count > 1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var primaryAddress = group.Value.First();
            var duplicateAddresses = group.Value.Skip(1).ToList();

            // Update references to point to primary
            foreach (var dupAddress in duplicateAddresses)
            {
                await UpdateBlockReferenceAsync(arrayId, dupAddress, primaryAddress, cancellationToken);
                result.BlocksConsolidated++;
            }

            // Update index
            var entry = new DedupEntry
            {
                Hash = group.Key,
                StorageAddress = primaryAddress,
                Size = _config.BlockSize,
                ReferenceCount = group.Value.Count,
                CreatedTime = DateTime.UtcNow,
                LastReferenced = DateTime.UtcNow
            };
            index.Add(group.Key, entry);
        }

        result.DuplicateGroupsFound = duplicateGroups.Count(g => g.Value.Count > 1);
        result.SpaceSaved = result.BlocksConsolidated * _config.BlockSize;
        result.EndTime = DateTime.UtcNow;
        result.Duration = result.EndTime - result.StartTime;

        progress?.Report(1.0);

        return result;
    }

    /// <summary>
    /// 91.F4.3: Calculate dedup-aware parity for optimized storage.
    /// </summary>
    public DedupAwareParity CalculateDedupAwareParity(
        string arrayId,
        IEnumerable<DedupChunk> chunks,
        int parityDisks = 1)
    {
        var chunkList = chunks.ToList();
        var result = new DedupAwareParity
        {
            ArrayId = arrayId,
            TotalChunks = chunkList.Count,
            UniqueChunks = chunkList.Count(c => !c.IsDuplicate)
        };

        // Group chunks by dedup status
        var uniqueChunks = chunkList.Where(c => !c.IsDuplicate).ToList();
        var duplicateChunks = chunkList.Where(c => c.IsDuplicate).ToList();

        // Only unique chunks need parity calculation
        if (uniqueChunks.Count > 0)
        {
            var dataBlocks = uniqueChunks.Select(c => c.Data ?? Array.Empty<byte>()).ToList();

            // Calculate XOR parity for unique blocks
            result.ParityBlocks.Add(CalculateXorParity(dataBlocks));

            // For RAID 6 style, calculate Q parity
            if (parityDisks >= 2)
            {
                result.ParityBlocks.Add(CalculateQParity(dataBlocks));
            }

            result.ParityBytesCalculated = result.ParityBlocks.Sum(p => p.Length);
        }

        // For duplicate chunks, reference existing parity
        foreach (var dupChunk in duplicateChunks)
        {
            result.DuplicateReferences.Add(new DedupParityReference
            {
                ChunkHash = dupChunk.Hash,
                ReferencedAddress = dupChunk.ReferencedAddress,
                ParityAddress = GetParityAddressForBlock(arrayId, dupChunk.ReferencedAddress)
            });
        }

        result.ParityOptimizationRatio = chunkList.Count > 0
            ? (double)uniqueChunks.Count / chunkList.Count
            : 1.0;

        return result;
    }

    /// <summary>
    /// Gets deduplication statistics for an array.
    /// </summary>
    public DedupStatistics GetStatistics(string arrayId)
    {
        var index = _dedupIndexes.GetOrAdd(arrayId, _ => new DedupIndex(arrayId));

        return new DedupStatistics
        {
            ArrayId = arrayId,
            TotalBlocksProcessed = _totalBlocksProcessed,
            DuplicateBlocksFound = _duplicateBlocksFound,
            BytesSaved = _bytesSaved,
            UniqueBlocksStored = index.Count,
            DedupRatio = _totalBlocksProcessed > 0
                ? (double)_duplicateBlocksFound / _totalBlocksProcessed
                : 0.0,
            IndexSizeBytes = index.Count * (32 + 64) // Hash + entry overhead estimate
        };
    }

    /// <summary>
    /// Performs garbage collection on unreferenced dedup entries.
    /// </summary>
    public async Task<GarbageCollectionResult> CollectGarbageAsync(
        string arrayId,
        CancellationToken cancellationToken = default)
    {
        var result = new GarbageCollectionResult { ArrayId = arrayId, StartTime = DateTime.UtcNow };

        if (!_dedupIndexes.TryGetValue(arrayId, out var index))
            return result;

        var entriesToRemove = index.GetEntriesWithZeroReferences().ToList();

        foreach (var entry in entriesToRemove)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Free storage
            await FreeStorageAddressAsync(arrayId, entry.StorageAddress, cancellationToken);

            // Remove from index
            index.Remove(entry.Hash);
            _hashIndex.TryRemove(entry.Hash, out _);

            result.EntriesRemoved++;
            result.BytesReclaimed += entry.Size;
        }

        result.EndTime = DateTime.UtcNow;
        return result;
    }

    private List<DataChunk> ChunkData(byte[] data, DedupMode mode)
    {
        var chunks = new List<DataChunk>();

        switch (mode)
        {
            case DedupMode.FixedBlock:
                for (int i = 0; i < data.Length; i += _config.BlockSize)
                {
                    var length = Math.Min(_config.BlockSize, data.Length - i);
                    var chunk = new byte[length];
                    Array.Copy(data, i, chunk, 0, length);
                    chunks.Add(new DataChunk { Offset = i, Length = length, Data = chunk });
                }
                break;

            case DedupMode.VariableBlock:
                // Content-defined chunking using rolling hash
                chunks = ContentDefinedChunking(data);
                break;

            case DedupMode.WholeFile:
                chunks.Add(new DataChunk { Offset = 0, Length = data.Length, Data = data });
                break;
        }

        return chunks;
    }

    private List<DataChunk> ContentDefinedChunking(byte[] data)
    {
        var chunks = new List<DataChunk>();
        var minSize = _config.MinBlockSize;
        var maxSize = _config.MaxBlockSize;
        var targetSize = _config.BlockSize;

        var start = 0;
        var position = minSize;
        ulong rollingHash = 0;
        const ulong mask = (1UL << 13) - 1; // ~8KB average chunk

        while (position < data.Length)
        {
            // Update rolling hash (Rabin fingerprint simulation)
            rollingHash = (rollingHash << 1) | data[position];
            rollingHash ^= (ulong)data[position] * 31;

            var chunkSize = position - start;

            // Check for chunk boundary
            if ((chunkSize >= minSize && (rollingHash & mask) == 0) || chunkSize >= maxSize)
            {
                var chunk = new byte[chunkSize];
                Array.Copy(data, start, chunk, 0, chunkSize);
                chunks.Add(new DataChunk { Offset = start, Length = chunkSize, Data = chunk });

                start = position;
                rollingHash = 0;
            }

            position++;
        }

        // Add remaining data as final chunk
        if (start < data.Length)
        {
            var remaining = data.Length - start;
            var chunk = new byte[remaining];
            Array.Copy(data, start, chunk, 0, remaining);
            chunks.Add(new DataChunk { Offset = start, Length = remaining, Data = chunk });
        }

        return chunks;
    }

    private byte[] ComputeHash(byte[] data)
    {
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(data);
    }

    private static long _nextStorageAddress = DateTime.UtcNow.Ticks;

    private long AllocateStorageAddress(string arrayId)
    {
        // Atomic counter guarantees unique addresses even under concurrent access.
        // Initialized from clock ticks to avoid collisions across process restarts.
        return Interlocked.Increment(ref _nextStorageAddress);
    }

    private long GetParityAddressForBlock(string arrayId, long blockAddress)
    {
        // Calculate parity address based on stripe layout
        return blockAddress / _config.BlockSize * _config.BlockSize + 0x1000000;
    }

    private Task<byte[]> ReadBlockFromRaidAsync(string arrayId, long address, CancellationToken ct)
    {
        // Simulated read
        return Task.FromResult(new byte[_config.BlockSize]);
    }

    private Task UpdateBlockReferenceAsync(string arrayId, long from, long to, CancellationToken ct)
    {
        // Simulated reference update
        return Task.CompletedTask;
    }

    private Task FreeStorageAddressAsync(string arrayId, long address, CancellationToken ct)
    {
        // Simulated storage free
        return Task.CompletedTask;
    }

    private byte[] CalculateXorParity(List<byte[]> blocks)
    {
        if (blocks.Count == 0) return Array.Empty<byte>();

        var maxLen = blocks.Max(b => b.Length);
        var parity = new byte[maxLen];

        foreach (var block in blocks)
        {
            for (int i = 0; i < block.Length; i++)
            {
                parity[i] ^= block[i];
            }
        }

        return parity;
    }

    private byte[] CalculateQParity(List<byte[]> blocks)
    {
        if (blocks.Count == 0) return Array.Empty<byte>();

        var maxLen = blocks.Max(b => b.Length);
        var qParity = new byte[maxLen];

        for (int i = 0; i < maxLen; i++)
        {
            byte result = 0;
            for (int j = 0; j < blocks.Count; j++)
            {
                if (i < blocks[j].Length)
                {
                    var coeff = (byte)(1 << (j % 8));
                    result ^= GaloisMultiply(blocks[j][i], coeff);
                }
            }
            qParity[i] = result;
        }

        return qParity;
    }

    private static byte GaloisMultiply(byte a, byte b)
    {
        byte result = 0;
        while (b != 0)
        {
            if ((b & 1) != 0)
                result ^= a;
            bool highBit = (a & 0x80) != 0;
            a <<= 1;
            if (highBit)
                a ^= 0x1D;
            b >>= 1;
        }
        return result;
    }
}

/// <summary>
/// Dedup index for an array.
/// </summary>
public sealed class DedupIndex
{
    private readonly string _arrayId;
    private readonly BoundedDictionary<byte[], DedupEntry> _entries = new(1000, new ByteArrayComparer());

    public DedupIndex(string arrayId)
    {
        _arrayId = arrayId;
    }

    public int Count => _entries.Count;

    public DedupEntry? Lookup(byte[] hash) =>
        _entries.TryGetValue(hash, out var entry) ? entry : null;

    public void Add(byte[] hash, DedupEntry entry) => _entries[hash] = entry;

    public void Remove(byte[] hash) => _entries.TryRemove(hash, out _);

    public IEnumerable<DedupEntry> GetEntriesWithZeroReferences() =>
        _entries.Values.Where(e => e.ReferenceCount <= 0);
}

/// <summary>
/// Dedup entry in the index.
/// </summary>
public sealed class DedupEntry
{
    public byte[] Hash { get; set; } = Array.Empty<byte>();
    public long StorageAddress { get; set; }
    public int Size { get; set; }
    public int ReferenceCount { get; set; }
    public DateTime CreatedTime { get; set; }
    public DateTime LastReferenced { get; set; }
}

/// <summary>
/// Deduplication mode.
/// </summary>
public enum DedupMode
{
    FixedBlock,
    VariableBlock,
    WholeFile
}

/// <summary>
/// Configuration for deduplication.
/// </summary>
public sealed class DedupConfiguration
{
    public int BlockSize { get; set; } = 4096;
    public int MinBlockSize { get; set; } = 2048;
    public int MaxBlockSize { get; set; } = 65536;
    public bool EnableCompression { get; set; } = true;
}

/// <summary>
/// Data chunk for processing.
/// </summary>
public sealed class DataChunk
{
    public int Offset { get; set; }
    public int Length { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Dedup chunk result.
/// </summary>
public sealed class DedupChunk
{
    public int Offset { get; set; }
    public int Length { get; set; }
    public byte[] Hash { get; set; } = Array.Empty<byte>();
    public bool IsDuplicate { get; set; }
    public byte[]? Data { get; set; }
    public long StorageAddress { get; set; }
    public long ReferencedAddress { get; set; }
}

/// <summary>
/// Result of inline deduplication.
/// </summary>
public sealed class InlineDedupResult
{
    public int OriginalSize { get; set; }
    public int DedupedSize { get; set; }
    public int UniqueBlocks { get; set; }
    public int DuplicateBlocks { get; set; }
    public double DedupRatio { get; set; }
    public List<DedupChunk> Chunks { get; set; } = new();
}

/// <summary>
/// Result of post-RAID deduplication.
/// </summary>
public sealed class PostRaidDedupResult
{
    public string ArrayId { get; set; } = string.Empty;
    public long StartAddress { get; set; }
    public long EndAddress { get; set; }
    public int DuplicateGroupsFound { get; set; }
    public long BlocksConsolidated { get; set; }
    public long SpaceSaved { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Dedup-aware parity calculation result.
/// </summary>
public sealed class DedupAwareParity
{
    public string ArrayId { get; set; } = string.Empty;
    public int TotalChunks { get; set; }
    public int UniqueChunks { get; set; }
    public List<byte[]> ParityBlocks { get; set; } = new();
    public long ParityBytesCalculated { get; set; }
    public double ParityOptimizationRatio { get; set; }
    public List<DedupParityReference> DuplicateReferences { get; set; } = new();
}

/// <summary>
/// Reference to existing parity for duplicate block.
/// </summary>
public sealed class DedupParityReference
{
    public byte[] ChunkHash { get; set; } = Array.Empty<byte>();
    public long ReferencedAddress { get; set; }
    public long ParityAddress { get; set; }
}

/// <summary>
/// Deduplication statistics.
/// </summary>
public sealed class DedupStatistics
{
    public string ArrayId { get; set; } = string.Empty;
    public long TotalBlocksProcessed { get; set; }
    public long DuplicateBlocksFound { get; set; }
    public long BytesSaved { get; set; }
    public int UniqueBlocksStored { get; set; }
    public double DedupRatio { get; set; }
    public long IndexSizeBytes { get; set; }
}

/// <summary>
/// Result of garbage collection.
/// </summary>
public sealed class GarbageCollectionResult
{
    public string ArrayId { get; set; } = string.Empty;
    public int EntriesRemoved { get; set; }
    public long BytesReclaimed { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
}

/// <summary>
/// Byte array equality comparer.
/// </summary>
internal sealed class ByteArrayComparer : IEqualityComparer<byte[]>
{
    public bool Equals(byte[]? x, byte[]? y)
    {
        if (x == null && y == null) return true;
        if (x == null || y == null) return false;
        return x.SequenceEqual(y);
    }

    public int GetHashCode(byte[] obj)
    {
        if (obj == null || obj.Length == 0) return 0;
        unchecked
        {
            int hash = 17;
            foreach (var b in obj.Take(8))
                hash = hash * 31 + b;
            return hash;
        }
    }
}
