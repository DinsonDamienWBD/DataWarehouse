using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Extendible hashing inode table providing O(1) amortized lookup that scales
/// from 1K to 1B+ inodes without full table rebuild. The directory doubles only
/// when a bucket split requires a new bit (LocalDepth == GlobalDepth).
/// </summary>
/// <remarks>
/// <para>
/// Structure: a directory of 2^GlobalDepth entries, each pointing to a bucket block number.
/// Multiple directory entries can point to the same bucket when LocalDepth &lt; GlobalDepth.
/// </para>
/// <para>
/// On-disk directory format (starting at regionStartBlock):
/// Block 0: [GlobalDepth:4 LE][BucketCount:8 LE][DirectoryEntries: 8 bytes each * 2^GlobalDepth].
/// If the directory exceeds one block, it spans consecutive blocks.
/// </para>
/// <para>
/// Hash function: XxHash64 for uniform distribution.
/// Thread safety: <see cref="ReaderWriterLockSlim"/> for concurrent reads, single writer.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-11 Extendible hashing")]
public sealed class ExtendibleHashTable : IDisposable
{
    /// <summary>
    /// Directory header size: [GlobalDepth:4][BucketCount:8].
    /// </summary>
    private const int DirectoryHeaderSize = 12;

    private readonly int _blockSize;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<long, ExtendibleHashBucket> _bucketCache = new();

    /// <summary>
    /// Directory array of 2^GlobalDepth entries, each a bucket block number.
    /// </summary>
    private long[] _directory;

    /// <summary>
    /// Gets the number of hash bits used for directory indexing.
    /// Starts at 1 (2 directory slots) and grows when bucket splits require it.
    /// </summary>
    public int GlobalDepth { get; private set; }

    /// <summary>
    /// Gets the total number of inode entries across all buckets.
    /// </summary>
    public long EntryCount { get; private set; }

    /// <summary>
    /// Gets the number of distinct buckets.
    /// </summary>
    public int BucketCount => _bucketCache.Count;

    /// <summary>
    /// Gets the maximum entries per bucket (derived from block size).
    /// </summary>
    public int BucketCapacity { get; }

    /// <summary>
    /// Gets the load factor: ratio of entries to total bucket capacity.
    /// </summary>
    public double LoadFactor => BucketCount == 0
        ? 0.0
        : EntryCount / (double)(BucketCount * BucketCapacity);

    /// <summary>
    /// Gets the directory size in bytes: 8 * 2^GlobalDepth.
    /// </summary>
    public double DirectorySizeBytes => 8.0 * (1L << GlobalDepth);

    /// <summary>
    /// Initializes a new <see cref="ExtendibleHashTable"/> with the specified block size
    /// and initial global depth.
    /// </summary>
    /// <param name="blockSize">VDE block size in bytes.</param>
    /// <param name="globalDepth">Initial global depth (default 1, creating 2 directory slots).</param>
    public ExtendibleHashTable(int blockSize, int globalDepth = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, ExtendibleHashBucket.HeaderSize + ExtendibleHashBucket.EntrySize, nameof(blockSize));
        ArgumentOutOfRangeException.ThrowIfNegative(globalDepth, nameof(globalDepth));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(globalDepth, 30, nameof(globalDepth));

        _blockSize = blockSize;
        GlobalDepth = globalDepth;
        BucketCapacity = (blockSize - ExtendibleHashBucket.HeaderSize) / ExtendibleHashBucket.EntrySize;

        int directorySize = 1 << globalDepth;
        _directory = new long[directorySize];

        // Initialize with one bucket per directory slot (each slot independent).
        // Caller must assign real block numbers before persistence.
        for (int i = 0; i < directorySize; i++)
        {
            _directory[i] = -(i + 1); // Placeholder: negative indicates unassigned
        }
    }

    /// <summary>
    /// Private constructor for deserialization and migration.
    /// </summary>
    private ExtendibleHashTable(int blockSize, int globalDepth, long[] directory)
    {
        _blockSize = blockSize;
        GlobalDepth = globalDepth;
        BucketCapacity = (blockSize - ExtendibleHashBucket.HeaderSize) / ExtendibleHashBucket.EntrySize;
        _directory = directory;
    }

    /// <summary>
    /// Computes the XxHash64 hash of an inode ID for uniform distribution.
    /// </summary>
    /// <param name="inodeId">The inode ID to hash.</param>
    /// <returns>64-bit hash value.</returns>
    internal static ulong HashInodeId(ulong inodeId)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, inodeId);
        return XxHash64.HashToUInt64(buffer);
    }

    /// <summary>
    /// Gets the directory index for a hash value at the current global depth.
    /// </summary>
    private int GetDirectoryIndex(ulong hash)
    {
        // Use the lowest GlobalDepth bits as directory index
        return (int)(hash & ((1UL << GlobalDepth) - 1));
    }

    /// <summary>
    /// Looks up the block number for a given inode ID.
    /// O(1) amortized: single directory lookup + linear scan within one bucket.
    /// </summary>
    /// <param name="inodeId">The inode ID to look up.</param>
    /// <returns>The block number if found, or -1 if not present.</returns>
    public long Lookup(ulong inodeId)
    {
        _lock.EnterReadLock();
        try
        {
            ulong hash = HashInodeId(inodeId);
            int index = GetDirectoryIndex(hash);
            long bucketBlock = _directory[index];

            if (_bucketCache.TryGetValue(bucketBlock, out var bucket))
            {
                foreach (var entry in bucket.Entries)
                {
                    if (entry.InodeId == inodeId)
                        return entry.BlockNumber;
                }
            }

            return -1;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Inserts or updates an inode entry. If the target bucket is full, triggers
    /// a split. If the bucket's local depth equals the global depth, the directory
    /// is doubled first.
    /// </summary>
    /// <param name="inodeId">The inode ID.</param>
    /// <param name="blockNumber">The block number storing the inode data.</param>
    /// <param name="allocator">Block allocator for new bucket blocks (used on split).</param>
    public void Insert(ulong inodeId, long blockNumber, IBlockAllocator? allocator = null)
    {
        _lock.EnterWriteLock();
        try
        {
            InsertInternal(inodeId, blockNumber, allocator);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private void InsertInternal(ulong inodeId, long blockNumber, IBlockAllocator? allocator)
    {
        ulong hash = HashInodeId(inodeId);
        int index = GetDirectoryIndex(hash);
        long bucketBlock = _directory[index];

        if (!_bucketCache.TryGetValue(bucketBlock, out var bucket))
        {
            // Create a new bucket on first access
            bucket = new ExtendibleHashBucket(_blockSize, GlobalDepth > 0 ? 0 : 0)
            {
                BlockNumber = bucketBlock
            };
            _bucketCache[bucketBlock] = bucket;
        }

        // Check for existing entry (update case)
        for (int i = 0; i < bucket.Entries.Count; i++)
        {
            if (bucket.Entries[i].InodeId == inodeId)
            {
                bucket.Entries[i] = (inodeId, blockNumber);
                return;
            }
        }

        // Insert if bucket has room
        if (!bucket.IsFull)
        {
            bucket.Entries.Add((inodeId, blockNumber));
            EntryCount++;
            return;
        }

        // Bucket is full: split
        SplitBucket(bucket, allocator);

        // Retry insert after split (recursive, but bounded by depth growth)
        InsertInternal(inodeId, blockNumber, allocator);
    }

    /// <summary>
    /// Splits a full bucket, doubling the directory if needed.
    /// </summary>
    private void SplitBucket(ExtendibleHashBucket bucket, IBlockAllocator? allocator)
    {
        // If local depth == global depth, double the directory first
        if (bucket.LocalDepth == GlobalDepth)
        {
            DoubleDirectory();
        }

        // Split the bucket
        var (low, high) = bucket.Split(HashInodeId, _blockSize);

        // Assign block numbers to new buckets
        long lowBlock = bucket.BlockNumber; // Reuse old block for low
        long highBlock = allocator?.AllocateBlock() ?? (bucket.BlockNumber + _bucketCache.Count + 1);

        low.BlockNumber = lowBlock;
        high.BlockNumber = highBlock;

        // Remove old bucket from cache
        _bucketCache.Remove(bucket.BlockNumber);

        // Add new buckets
        _bucketCache[lowBlock] = low;
        _bucketCache[highBlock] = high;

        // Update directory: entries that pointed to old bucket now point to
        // appropriate new bucket based on the split bit
        ulong bitMask = 1UL << bucket.LocalDepth; // The bit that distinguishes low/high
        int directorySize = 1 << GlobalDepth;

        for (int i = 0; i < directorySize; i++)
        {
            if (_directory[i] == bucket.BlockNumber)
            {
                _directory[i] = ((ulong)i & bitMask) == 0 ? lowBlock : highBlock;
            }
        }
    }

    /// <summary>
    /// Doubles the directory by incrementing GlobalDepth and duplicating entries.
    /// </summary>
    private void DoubleDirectory()
    {
        int oldSize = 1 << GlobalDepth;
        GlobalDepth++;
        int newSize = 1 << GlobalDepth;

        var newDirectory = new long[newSize];

        // Each old entry i maps to new entries i and i + oldSize
        for (int i = 0; i < oldSize; i++)
        {
            newDirectory[i] = _directory[i];
            newDirectory[i + oldSize] = _directory[i];
        }

        _directory = newDirectory;
    }

    /// <summary>
    /// Deletes an inode entry from the table.
    /// Bucket merging with siblings is deferred (not implemented in this version).
    /// </summary>
    /// <param name="inodeId">The inode ID to remove.</param>
    /// <returns>True if the entry was found and removed; false otherwise.</returns>
    public bool Delete(ulong inodeId)
    {
        _lock.EnterWriteLock();
        try
        {
            ulong hash = HashInodeId(inodeId);
            int index = GetDirectoryIndex(hash);
            long bucketBlock = _directory[index];

            if (!_bucketCache.TryGetValue(bucketBlock, out var bucket))
                return false;

            for (int i = 0; i < bucket.Entries.Count; i++)
            {
                if (bucket.Entries[i].InodeId == inodeId)
                {
                    bucket.Entries.RemoveAt(i);
                    EntryCount--;
                    return true;
                }
            }

            return false;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Persists the directory and all buckets to VDE blocks.
    /// Directory format at regionStartBlock: [GlobalDepth:4 LE][BucketCount:8 LE][DirectoryEntries:8*2^GlobalDepth LE].
    /// If the directory exceeds one block, it spans multiple consecutive blocks.
    /// </summary>
    /// <param name="device">Block device to write to.</param>
    /// <param name="regionStartBlock">First block of the hash table region.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SaveAsync(IBlockDevice device, long regionStartBlock, CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try
        {
            // Serialize directory
            int directorySize = 1 << GlobalDepth;
            int directoryBytes = DirectoryHeaderSize + (directorySize * 8);
            int directoryBlocks = (directoryBytes + _blockSize - 1) / _blockSize;

            var directoryData = new byte[directoryBlocks * _blockSize];
            BinaryPrimitives.WriteInt32LittleEndian(directoryData.AsSpan(0, 4), GlobalDepth);
            BinaryPrimitives.WriteInt64LittleEndian(directoryData.AsSpan(4, 8), _bucketCache.Count);

            int offset = DirectoryHeaderSize;
            for (int i = 0; i < directorySize; i++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(directoryData.AsSpan(offset, 8), _directory[i]);
                offset += 8;
            }

            // Write directory blocks
            for (int b = 0; b < directoryBlocks; b++)
            {
                await device.WriteBlockAsync(
                    regionStartBlock + b,
                    directoryData.AsMemory(b * _blockSize, _blockSize),
                    ct).ConfigureAwait(false);
            }

            // Write each bucket
            foreach (var bucket in _bucketCache.Values)
            {
                var bucketData = bucket.Serialize(_blockSize);
                await device.WriteBlockAsync(bucket.BlockNumber, bucketData, ct).ConfigureAwait(false);
            }

            await device.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Loads the directory and all referenced buckets from VDE blocks.
    /// </summary>
    /// <param name="device">Block device to read from.</param>
    /// <param name="regionStartBlock">First block of the hash table region.</param>
    /// <param name="blockSize">VDE block size in bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A fully populated <see cref="ExtendibleHashTable"/>.</returns>
    public static async Task<ExtendibleHashTable> LoadAsync(
        IBlockDevice device, long regionStartBlock, int blockSize, CancellationToken ct = default)
    {
        // Read first directory block to get GlobalDepth
        var firstBlock = new byte[blockSize];
        await device.ReadBlockAsync(regionStartBlock, firstBlock, ct).ConfigureAwait(false);

        int globalDepth = BinaryPrimitives.ReadInt32LittleEndian(firstBlock.AsSpan(0, 4));
        long bucketCount = BinaryPrimitives.ReadInt64LittleEndian(firstBlock.AsSpan(4, 8));

        int directorySize = 1 << globalDepth;
        int directoryBytes = DirectoryHeaderSize + (directorySize * 8);
        int directoryBlocks = (directoryBytes + blockSize - 1) / blockSize;

        // Read all directory blocks
        var directoryData = new byte[directoryBlocks * blockSize];
        firstBlock.CopyTo(directoryData, 0);

        for (int b = 1; b < directoryBlocks; b++)
        {
            await device.ReadBlockAsync(
                regionStartBlock + b,
                directoryData.AsMemory(b * blockSize, blockSize),
                ct).ConfigureAwait(false);
        }

        // Parse directory entries
        var directory = new long[directorySize];
        int offset = DirectoryHeaderSize;
        for (int i = 0; i < directorySize; i++)
        {
            directory[i] = BinaryPrimitives.ReadInt64LittleEndian(
                directoryData.AsSpan(offset, 8));
            offset += 8;
        }

        var table = new ExtendibleHashTable(blockSize, globalDepth, directory);

        // Load unique buckets (directory entries may share buckets)
        var loadedBlocks = new HashSet<long>();
        long totalEntries = 0;

        for (int i = 0; i < directorySize; i++)
        {
            long bucketBlock = directory[i];
            if (bucketBlock < 0 || !loadedBlocks.Add(bucketBlock))
                continue;

            var bucketData = new byte[blockSize];
            await device.ReadBlockAsync(bucketBlock, bucketData, ct).ConfigureAwait(false);

            var bucket = ExtendibleHashBucket.Deserialize(bucketData, blockSize);
            bucket.BlockNumber = bucketBlock;
            table._bucketCache[bucketBlock] = bucket;
            totalEntries += bucket.Entries.Count;
        }

        table.EntryCount = totalEntries;
        return table;
    }

    /// <summary>
    /// Migrates an existing linear inode array into an extendible hash table.
    /// Bulk-loads all entries with an initial GlobalDepth sized to minimize splits.
    /// </summary>
    /// <param name="entries">Existing inode-to-block mappings.</param>
    /// <param name="device">Block device for persistence (unused during in-memory construction).</param>
    /// <param name="allocator">Block allocator for bucket blocks.</param>
    /// <param name="blockSize">VDE block size in bytes.</param>
    /// <returns>A new <see cref="ExtendibleHashTable"/> populated with all entries.</returns>
    public static ExtendibleHashTable MigrateFromLinearArray(
        IReadOnlyList<(ulong InodeId, long BlockNumber)> entries,
        IBlockDevice device,
        IBlockAllocator allocator,
        int blockSize)
    {
        int bucketCapacity = (blockSize - ExtendibleHashBucket.HeaderSize) / ExtendibleHashBucket.EntrySize;
        int estimatedBuckets = Math.Max(1, entries.Count / Math.Max(1, bucketCapacity));

        // GlobalDepth = ceil(log2(estimatedBuckets)), minimum 1
        int globalDepth = Math.Max(1, (int)Math.Ceiling(Math.Log2(estimatedBuckets)));
        if (globalDepth > 30) globalDepth = 30;

        var table = new ExtendibleHashTable(blockSize, globalDepth);

        // Pre-allocate bucket blocks for each directory slot
        int directorySize = 1 << globalDepth;
        var assignedBuckets = new Dictionary<int, ExtendibleHashBucket>();

        for (int i = 0; i < directorySize; i++)
        {
            if (!assignedBuckets.ContainsKey(i))
            {
                long block = allocator.AllocateBlock();
                var bucket = new ExtendibleHashBucket(blockSize, globalDepth)
                {
                    BlockNumber = block
                };
                table._directory[i] = block;
                table._bucketCache[block] = bucket;
                assignedBuckets[i] = bucket;
            }
        }

        // Insert all entries
        foreach (var (inodeId, blockNumber) in entries)
        {
            table.InsertInternal(inodeId, blockNumber, allocator);
        }

        return table;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _lock.Dispose();
    }
}
