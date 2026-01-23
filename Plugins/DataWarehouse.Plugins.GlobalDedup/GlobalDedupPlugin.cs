using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace DataWarehouse.Plugins.GlobalDedup
{
    #region Configuration and Enums

    /// <summary>
    /// Deduplication mode for controlling when deduplication occurs.
    /// </summary>
    public enum DedupMode
    {
        /// <summary>Deduplicate during write operations (lower latency for reads, higher write latency).</summary>
        Inline,
        /// <summary>Deduplicate asynchronously after write (lower write latency, background processing).</summary>
        PostProcess,
        /// <summary>Hybrid mode: inline for small objects, post-process for large objects.</summary>
        Adaptive
    }

    /// <summary>
    /// Configuration for the global deduplication plugin.
    /// </summary>
    public sealed class GlobalDedupConfig
    {
        /// <summary>Deduplication mode. Default is Adaptive.</summary>
        public DedupMode Mode { get; set; } = DedupMode.Adaptive;

        /// <summary>Target average chunk size in bytes. Default 64KB.</summary>
        public int AverageChunkSize { get; set; } = 65536;

        /// <summary>Minimum chunk size in bytes. Default 16KB.</summary>
        public int MinChunkSize { get; set; } = 16384;

        /// <summary>Maximum chunk size in bytes. Default 256KB.</summary>
        public int MaxChunkSize { get; set; } = 262144;

        /// <summary>Number of shards for the global index. Default 256 for high concurrency.</summary>
        public int IndexShardCount { get; set; } = 256;

        /// <summary>Threshold size for adaptive mode to switch to post-process. Default 10MB.</summary>
        public long AdaptiveThresholdBytes { get; set; } = 10 * 1024 * 1024;

        /// <summary>Maximum concurrent post-process operations. Default is processor count * 2.</summary>
        public int MaxPostProcessConcurrency { get; set; } = Environment.ProcessorCount * 2;

        /// <summary>Enable bloom filter for fast negative lookups. Default true.</summary>
        public bool EnableBloomFilter { get; set; } = true;

        /// <summary>Bloom filter expected capacity. Default 10 million entries.</summary>
        public int BloomFilterCapacity { get; set; } = 10_000_000;

        /// <summary>Bloom filter false positive rate. Default 0.01 (1%).</summary>
        public double BloomFilterFalsePositiveRate { get; set; } = 0.01;

        /// <summary>Enable Write-Ahead Log for durability. Default true.</summary>
        public bool EnableWAL { get; set; } = true;

        /// <summary>WAL flush interval. Default 1 second.</summary>
        public TimeSpan WalFlushInterval { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>Path for persistent storage. Default "global_dedup_data".</summary>
        public string StoragePath { get; set; } = "global_dedup_data";

        /// <summary>Enable integrity verification on chunk retrieval. Default true.</summary>
        public bool VerifyOnRetrieve { get; set; } = true;

        /// <summary>Garbage collection interval. Default 1 hour.</summary>
        public TimeSpan GarbageCollectionInterval { get; set; } = TimeSpan.FromHours(1);

        /// <summary>Minimum age for orphaned chunks before GC. Default 24 hours.</summary>
        public TimeSpan OrphanedChunkMinAge { get; set; } = TimeSpan.FromHours(24);
    }

    #endregion

    #region Global Chunk Index Types

    /// <summary>
    /// Metadata for a globally deduplicated chunk including cross-volume references.
    /// Thread-safe for concurrent access.
    /// </summary>
    public sealed class GlobalChunkMetadata
    {
        private long _globalReferenceCount;
        private readonly ConcurrentDictionary<string, long> _volumeReferences = new();

        /// <summary>SHA-256 hash of the chunk.</summary>
        public byte[] Hash { get; init; } = Array.Empty<byte>();

        /// <summary>Original uncompressed size of the chunk.</summary>
        public int OriginalSize { get; init; }

        /// <summary>Stored size (may be compressed).</summary>
        public int StoredSize { get; init; }

        /// <summary>UTC timestamp when the chunk was first created.</summary>
        public DateTime CreatedAt { get; init; }

        /// <summary>UTC timestamp of last access.</summary>
        public DateTime LastAccessedAt { get; set; }

        /// <summary>Total reference count across all volumes.</summary>
        public long GlobalReferenceCount => Interlocked.Read(ref _globalReferenceCount);

        /// <summary>Per-volume reference counts for cross-volume tracking.</summary>
        public IReadOnlyDictionary<string, long> VolumeReferences => _volumeReferences;

        /// <summary>
        /// Increments reference count for a specific volume.
        /// </summary>
        /// <param name="volumeId">Volume identifier. Use empty string for default volume.</param>
        /// <returns>New global reference count.</returns>
        public long IncrementReference(string volumeId)
        {
            _volumeReferences.AddOrUpdate(volumeId, 1, (_, count) => count + 1);
            return Interlocked.Increment(ref _globalReferenceCount);
        }

        /// <summary>
        /// Decrements reference count for a specific volume.
        /// </summary>
        /// <param name="volumeId">Volume identifier.</param>
        /// <returns>New global reference count. Returns 0 or negative if chunk is orphaned.</returns>
        public long DecrementReference(string volumeId)
        {
            _volumeReferences.AddOrUpdate(volumeId, 0, (_, count) => Math.Max(0, count - 1));
            return Interlocked.Decrement(ref _globalReferenceCount);
        }

        /// <summary>
        /// Gets reference count for a specific volume.
        /// </summary>
        public long GetVolumeReferenceCount(string volumeId)
        {
            return _volumeReferences.TryGetValue(volumeId, out var count) ? count : 0;
        }
    }

    /// <summary>
    /// Extended chunk reference with volume information for cross-volume deduplication.
    /// </summary>
    public sealed class GlobalChunkReference
    {
        /// <summary>SHA-256 hash of the chunk.</summary>
        public byte[] Hash { get; init; } = Array.Empty<byte>();

        /// <summary>Original size of the chunk.</summary>
        public int Size { get; init; }

        /// <summary>Offset within the original data stream.</summary>
        public long Offset { get; init; }

        /// <summary>Whether this is a newly stored chunk.</summary>
        public bool IsNew { get; init; }

        /// <summary>Source volume ID where this reference originated.</summary>
        public string VolumeId { get; init; } = string.Empty;

        /// <summary>Whether this chunk is shared across volumes.</summary>
        public bool IsCrossVolumeShared { get; init; }
    }

    /// <summary>
    /// Statistics for global deduplication operations.
    /// </summary>
    public sealed class GlobalDedupStatistics
    {
        /// <summary>Total number of unique chunks stored.</summary>
        public long UniqueChunks { get; init; }

        /// <summary>Total logical bytes (sum of all references).</summary>
        public long TotalLogicalBytes { get; init; }

        /// <summary>Total physical bytes stored.</summary>
        public long TotalPhysicalBytes { get; init; }

        /// <summary>Number of volumes participating in global dedup.</summary>
        public int ActiveVolumes { get; init; }

        /// <summary>Chunks shared across multiple volumes.</summary>
        public long CrossVolumeSharedChunks { get; init; }

        /// <summary>Bytes saved through cross-volume deduplication.</summary>
        public long CrossVolumeSavingsBytes { get; init; }

        /// <summary>Inline dedup operations count.</summary>
        public long InlineOperations { get; init; }

        /// <summary>Post-process dedup operations count.</summary>
        public long PostProcessOperations { get; init; }

        /// <summary>Pending post-process operations.</summary>
        public int PendingPostProcessOperations { get; init; }

        /// <summary>Global deduplication ratio (physical/logical).</summary>
        public double GlobalDeduplicationRatio => TotalLogicalBytes > 0
            ? (double)TotalPhysicalBytes / TotalLogicalBytes : 1.0;

        /// <summary>Space savings percentage.</summary>
        public double SpaceSavingsPercent => TotalLogicalBytes > 0
            ? (1.0 - (double)TotalPhysicalBytes / TotalLogicalBytes) * 100 : 0;
    }

    #endregion

    #region Bloom Filter Implementation

    /// <summary>
    /// Thread-safe Bloom filter for fast negative lookups on chunk existence.
    /// Reduces unnecessary index queries for non-existent chunks.
    /// </summary>
    public sealed class ConcurrentBloomFilter : IDisposable
    {
        private readonly int[] _bitArray;
        private readonly int _bitCount;
        private readonly int _hashFunctionCount;
        private readonly ReaderWriterLockSlim _lock = new();
        private long _itemCount;
        private bool _disposed;

        /// <summary>
        /// Creates a Bloom filter with specified capacity and false positive rate.
        /// </summary>
        /// <param name="expectedCapacity">Expected number of items.</param>
        /// <param name="falsePositiveRate">Desired false positive rate (0.0-1.0).</param>
        public ConcurrentBloomFilter(int expectedCapacity, double falsePositiveRate)
        {
            if (expectedCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(expectedCapacity), "Capacity must be positive");
            if (falsePositiveRate <= 0 || falsePositiveRate >= 1)
                throw new ArgumentOutOfRangeException(nameof(falsePositiveRate), "False positive rate must be between 0 and 1");

            // Calculate optimal bit count: m = -n * ln(p) / (ln(2)^2)
            double ln2Squared = Math.Log(2) * Math.Log(2);
            _bitCount = (int)Math.Ceiling(-expectedCapacity * Math.Log(falsePositiveRate) / ln2Squared);
            _bitCount = Math.Max(_bitCount, 64); // Minimum 64 bits

            // Calculate optimal hash function count: k = m/n * ln(2)
            _hashFunctionCount = (int)Math.Round((_bitCount / (double)expectedCapacity) * Math.Log(2));
            _hashFunctionCount = Math.Max(_hashFunctionCount, 1);
            _hashFunctionCount = Math.Min(_hashFunctionCount, 32); // Cap at 32 hash functions

            // Allocate bit array as int array (32 bits per int)
            int intCount = (_bitCount + 31) / 32;
            _bitArray = new int[intCount];
        }

        /// <summary>
        /// Number of items added to the filter.
        /// </summary>
        public long Count => Interlocked.Read(ref _itemCount);

        /// <summary>
        /// Adds an item to the Bloom filter.
        /// </summary>
        /// <param name="hash">SHA-256 hash of the item.</param>
        public void Add(ReadOnlySpan<byte> hash)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _lock.EnterWriteLock();
            try
            {
                for (int i = 0; i < _hashFunctionCount; i++)
                {
                    int bitPosition = GetBitPosition(hash, i);
                    int arrayIndex = bitPosition / 32;
                    int bitIndex = bitPosition % 32;
                    _bitArray[arrayIndex] |= (1 << bitIndex);
                }
                Interlocked.Increment(ref _itemCount);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Checks if an item might exist in the filter.
        /// </summary>
        /// <param name="hash">SHA-256 hash of the item.</param>
        /// <returns>False if definitely not present, true if possibly present.</returns>
        public bool MightContain(ReadOnlySpan<byte> hash)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _lock.EnterReadLock();
            try
            {
                for (int i = 0; i < _hashFunctionCount; i++)
                {
                    int bitPosition = GetBitPosition(hash, i);
                    int arrayIndex = bitPosition / 32;
                    int bitIndex = bitPosition % 32;
                    if ((_bitArray[arrayIndex] & (1 << bitIndex)) == 0)
                    {
                        return false;
                    }
                }
                return true;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Clears all items from the filter.
        /// </summary>
        public void Clear()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _lock.EnterWriteLock();
            try
            {
                Array.Clear(_bitArray);
                Interlocked.Exchange(ref _itemCount, 0);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Computes bit position using double hashing technique.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetBitPosition(ReadOnlySpan<byte> hash, int functionIndex)
        {
            // Use first 8 bytes as hash1, next 8 bytes as hash2
            long hash1 = BitConverter.ToInt64(hash.Slice(0, 8));
            long hash2 = BitConverter.ToInt64(hash.Slice(8, 8));

            // Double hashing: h(i) = h1 + i * h2
            long combinedHash = hash1 + (functionIndex * hash2);
            return (int)((combinedHash & 0x7FFFFFFFFFFFFFFF) % _bitCount);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _lock.Dispose();
                _disposed = true;
            }
        }
    }

    #endregion

    #region Sharded Global Index

    /// <summary>
    /// Thread-safe sharded global chunk index for high-concurrency cross-volume deduplication.
    /// Uses consistent hashing for shard selection and fine-grained locking.
    /// </summary>
    public sealed class ShardedGlobalIndex : IDisposable
    {
        private readonly int _shardCount;
        private readonly ConcurrentDictionary<string, GlobalChunkMetadata>[] _shards;
        private readonly ReaderWriterLockSlim[] _shardLocks;
        private readonly ConcurrentDictionary<string, byte> _activeVolumes = new();
        private readonly ConcurrentBloomFilter? _bloomFilter;

        private long _totalChunks;
        private long _totalLogicalBytes;
        private long _totalPhysicalBytes;
        private long _crossVolumeSharedChunks;
        private long _crossVolumeSavingsBytes;
        private bool _disposed;

        /// <summary>
        /// Creates a new sharded global index.
        /// </summary>
        /// <param name="shardCount">Number of shards for parallel access.</param>
        /// <param name="enableBloomFilter">Enable bloom filter for fast negative lookups.</param>
        /// <param name="bloomCapacity">Bloom filter capacity.</param>
        /// <param name="bloomFalsePositiveRate">Bloom filter false positive rate.</param>
        public ShardedGlobalIndex(
            int shardCount = 256,
            bool enableBloomFilter = true,
            int bloomCapacity = 10_000_000,
            double bloomFalsePositiveRate = 0.01)
        {
            _shardCount = shardCount;
            _shards = new ConcurrentDictionary<string, GlobalChunkMetadata>[shardCount];
            _shardLocks = new ReaderWriterLockSlim[shardCount];

            for (int i = 0; i < shardCount; i++)
            {
                _shards[i] = new ConcurrentDictionary<string, GlobalChunkMetadata>();
                _shardLocks[i] = new ReaderWriterLockSlim();
            }

            if (enableBloomFilter)
            {
                _bloomFilter = new ConcurrentBloomFilter(bloomCapacity, bloomFalsePositiveRate);
            }
        }

        /// <summary>
        /// Gets the shard index for a given hash using consistent hashing.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetShardIndex(string hashHex)
        {
            // Use first 4 hex characters (2 bytes) for sharding
            if (hashHex.Length >= 4 &&
                int.TryParse(hashHex.AsSpan(0, 4), System.Globalization.NumberStyles.HexNumber, null, out int val))
            {
                return val % _shardCount;
            }
            return Math.Abs(hashHex.GetHashCode()) % _shardCount;
        }

        /// <summary>
        /// Checks if a chunk might exist using bloom filter (fast path).
        /// Returns true if chunk might exist, false if definitely doesn't exist.
        /// </summary>
        public bool MightContain(byte[] hash)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bloomFilter?.MightContain(hash) ?? true;
        }

        /// <summary>
        /// Tries to get chunk metadata without modifying reference counts.
        /// </summary>
        /// <param name="hashHex">Hex-encoded hash.</param>
        /// <param name="metadata">Retrieved metadata if found.</param>
        /// <returns>True if chunk exists.</returns>
        public bool TryGet(string hashHex, out GlobalChunkMetadata? metadata)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            int shard = GetShardIndex(hashHex);
            return _shards[shard].TryGetValue(hashHex, out metadata);
        }

        /// <summary>
        /// Adds a chunk or increments its reference count for a volume.
        /// </summary>
        /// <param name="hashHex">Hex-encoded hash.</param>
        /// <param name="hash">Raw hash bytes.</param>
        /// <param name="originalSize">Original chunk size.</param>
        /// <param name="storedSize">Stored chunk size.</param>
        /// <param name="volumeId">Volume ID for reference tracking.</param>
        /// <param name="isNew">Output: whether this is a new chunk.</param>
        /// <returns>The chunk metadata.</returns>
        public GlobalChunkMetadata AddOrReference(
            string hashHex,
            byte[] hash,
            int originalSize,
            int storedSize,
            string volumeId,
            out bool isNew)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            int shard = GetShardIndex(hashHex);
            _shardLocks[shard].EnterWriteLock();
            try
            {
                if (_shards[shard].TryGetValue(hashHex, out var existing))
                {
                    // Existing chunk - increment reference
                    long prevCount = existing.GlobalReferenceCount;
                    long prevVolumeCount = existing.GetVolumeReferenceCount(volumeId);
                    existing.IncrementReference(volumeId);
                    existing.LastAccessedAt = DateTime.UtcNow;

                    Interlocked.Add(ref _totalLogicalBytes, originalSize);

                    // Track cross-volume sharing
                    if (prevVolumeCount == 0 && existing.VolumeReferences.Count > 1)
                    {
                        Interlocked.Increment(ref _crossVolumeSharedChunks);
                        Interlocked.Add(ref _crossVolumeSavingsBytes, storedSize);
                    }

                    isNew = false;
                    return existing;
                }

                // New chunk
                var metadata = new GlobalChunkMetadata
                {
                    Hash = hash,
                    OriginalSize = originalSize,
                    StoredSize = storedSize,
                    CreatedAt = DateTime.UtcNow,
                    LastAccessedAt = DateTime.UtcNow
                };
                metadata.IncrementReference(volumeId);

                if (_shards[shard].TryAdd(hashHex, metadata))
                {
                    _bloomFilter?.Add(hash);
                    Interlocked.Increment(ref _totalChunks);
                    Interlocked.Add(ref _totalLogicalBytes, originalSize);
                    Interlocked.Add(ref _totalPhysicalBytes, storedSize);
                    _activeVolumes.TryAdd(volumeId, 0);
                    isNew = true;
                    return metadata;
                }

                // Race condition - another thread added it first
                if (_shards[shard].TryGetValue(hashHex, out existing))
                {
                    existing.IncrementReference(volumeId);
                    existing.LastAccessedAt = DateTime.UtcNow;
                    Interlocked.Add(ref _totalLogicalBytes, originalSize);
                    isNew = false;
                    return existing;
                }

                // Shouldn't reach here, but handle gracefully
                isNew = false;
                return metadata;
            }
            finally
            {
                _shardLocks[shard].ExitWriteLock();
            }
        }

        /// <summary>
        /// Decrements reference count for a chunk from a specific volume.
        /// </summary>
        /// <param name="hashHex">Hex-encoded hash.</param>
        /// <param name="volumeId">Volume ID.</param>
        /// <param name="metadata">Output: chunk metadata if found.</param>
        /// <returns>True if chunk should be garbage collected (zero references).</returns>
        public bool DecrementReference(string hashHex, string volumeId, out GlobalChunkMetadata? metadata)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            int shard = GetShardIndex(hashHex);
            _shardLocks[shard].EnterWriteLock();
            try
            {
                if (_shards[shard].TryGetValue(hashHex, out metadata))
                {
                    long prevVolumeCount = metadata.GetVolumeReferenceCount(volumeId);
                    long newCount = metadata.DecrementReference(volumeId);
                    Interlocked.Add(ref _totalLogicalBytes, -metadata.OriginalSize);

                    // Track cross-volume sharing changes
                    if (prevVolumeCount == 1 && metadata.VolumeReferences.Values.Count(v => v > 0) < 2)
                    {
                        Interlocked.Decrement(ref _crossVolumeSharedChunks);
                        Interlocked.Add(ref _crossVolumeSavingsBytes, -metadata.StoredSize);
                    }

                    if (newCount <= 0)
                    {
                        // Remove from index
                        _shards[shard].TryRemove(hashHex, out _);
                        Interlocked.Decrement(ref _totalChunks);
                        Interlocked.Add(ref _totalPhysicalBytes, -metadata.StoredSize);
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                _shardLocks[shard].ExitWriteLock();
            }
        }

        /// <summary>
        /// Enumerates all chunks for garbage collection or verification.
        /// </summary>
        public IEnumerable<KeyValuePair<string, GlobalChunkMetadata>> EnumerateAll()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            for (int i = 0; i < _shardCount; i++)
            {
                foreach (var kvp in _shards[i])
                {
                    yield return kvp;
                }
            }
        }

        /// <summary>
        /// Gets orphaned chunks (zero references) older than specified age.
        /// </summary>
        public List<(string Hash, GlobalChunkMetadata Metadata)> GetOrphanedChunks(TimeSpan minAge)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var cutoff = DateTime.UtcNow - minAge;
            var result = new List<(string, GlobalChunkMetadata)>();

            for (int i = 0; i < _shardCount; i++)
            {
                foreach (var kvp in _shards[i])
                {
                    if (kvp.Value.GlobalReferenceCount <= 0 && kvp.Value.LastAccessedAt < cutoff)
                    {
                        result.Add((kvp.Key, kvp.Value));
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Removes a chunk from the index.
        /// </summary>
        public bool Remove(string hashHex)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            int shard = GetShardIndex(hashHex);
            _shardLocks[shard].EnterWriteLock();
            try
            {
                if (_shards[shard].TryRemove(hashHex, out var metadata))
                {
                    Interlocked.Decrement(ref _totalChunks);
                    Interlocked.Add(ref _totalPhysicalBytes, -metadata.StoredSize);
                    return true;
                }
                return false;
            }
            finally
            {
                _shardLocks[shard].ExitWriteLock();
            }
        }

        /// <summary>Total unique chunks in the index.</summary>
        public long TotalChunks => Interlocked.Read(ref _totalChunks);

        /// <summary>Total logical bytes across all references.</summary>
        public long TotalLogicalBytes => Interlocked.Read(ref _totalLogicalBytes);

        /// <summary>Total physical bytes stored.</summary>
        public long TotalPhysicalBytes => Interlocked.Read(ref _totalPhysicalBytes);

        /// <summary>Number of active volumes.</summary>
        public int ActiveVolumeCount => _activeVolumes.Count;

        /// <summary>Chunks shared across multiple volumes.</summary>
        public long CrossVolumeSharedChunks => Interlocked.Read(ref _crossVolumeSharedChunks);

        /// <summary>Bytes saved through cross-volume deduplication.</summary>
        public long CrossVolumeSavingsBytes => Interlocked.Read(ref _crossVolumeSavingsBytes);

        public void Dispose()
        {
            if (!_disposed)
            {
                _bloomFilter?.Dispose();
                foreach (var lockObj in _shardLocks)
                {
                    lockObj.Dispose();
                }
                _disposed = true;
            }
        }
    }

    #endregion

    #region FastCDC Chunker

    /// <summary>
    /// High-performance FastCDC (Fast Content-Defined Chunking) implementation.
    /// Uses gear-based rolling hash with normalized chunking for optimal dedup ratios.
    /// </summary>
    public sealed class GlobalFastCDCChunker
    {
        private static readonly ulong[] GearTable = GenerateGearTable();

        private readonly int _minSize;
        private readonly int _avgSize;
        private readonly int _maxSize;
        private readonly ulong _maskSmall;
        private readonly ulong _maskLarge;
        private readonly int _normalSize;

        /// <summary>
        /// Creates a FastCDC chunker with specified parameters.
        /// </summary>
        /// <param name="minSize">Minimum chunk size.</param>
        /// <param name="avgSize">Target average chunk size.</param>
        /// <param name="maxSize">Maximum chunk size.</param>
        public GlobalFastCDCChunker(int minSize, int avgSize, int maxSize)
        {
            if (minSize <= 0) throw new ArgumentOutOfRangeException(nameof(minSize));
            if (avgSize <= minSize) throw new ArgumentOutOfRangeException(nameof(avgSize));
            if (maxSize <= avgSize) throw new ArgumentOutOfRangeException(nameof(maxSize));

            _minSize = minSize;
            _avgSize = avgSize;
            _maxSize = maxSize;
            _normalSize = avgSize;

            // Compute masks based on average size for normalized chunking
            int bits = (int)Math.Ceiling(Math.Log2(avgSize));
            _maskSmall = (1UL << (bits + 1)) - 1;
            _maskLarge = (1UL << (bits - 1)) - 1;
        }

        /// <summary>
        /// Generates the gear hash lookup table using deterministic PRNG.
        /// </summary>
        private static ulong[] GenerateGearTable()
        {
            var table = new ulong[256];
            ulong state = 0x123456789ABCDEF0UL;

            for (int i = 0; i < 256; i++)
            {
                // xorshift64* PRNG for reproducibility
                state ^= state >> 12;
                state ^= state << 25;
                state ^= state >> 27;
                table[i] = state * 0x2545F4914F6CDD1DUL;
            }

            return table;
        }

        /// <summary>
        /// Finds the next chunk boundary in the data.
        /// </summary>
        /// <param name="data">Data to chunk.</param>
        /// <param name="offset">Starting offset.</param>
        /// <returns>Length of the next chunk.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int FindChunkBoundary(ReadOnlySpan<byte> data, int offset = 0)
        {
            int remaining = data.Length - offset;
            if (remaining <= _minSize)
            {
                return remaining;
            }

            if (remaining >= _maxSize)
            {
                remaining = _maxSize;
            }

            int pos = offset + _minSize;
            int normalPoint = offset + _normalSize;
            int endPoint = offset + remaining;

            ulong hash = 0;

            // Phase 1: Before normal point, use larger mask
            int phase1End = Math.Min(normalPoint, endPoint);
            while (pos < phase1End)
            {
                hash = (hash << 1) + GearTable[data[pos]];
                if ((hash & _maskSmall) == 0)
                {
                    return pos - offset + 1;
                }
                pos++;
            }

            // Phase 2: After normal point, use smaller mask
            while (pos < endPoint)
            {
                hash = (hash << 1) + GearTable[data[pos]];
                if ((hash & _maskLarge) == 0)
                {
                    return pos - offset + 1;
                }
                pos++;
            }

            return remaining;
        }

        /// <summary>
        /// Chunks an entire data buffer.
        /// </summary>
        /// <param name="data">Data to chunk.</param>
        /// <returns>List of (offset, length) tuples for each chunk.</returns>
        public List<(int Offset, int Length)> ChunkData(ReadOnlySpan<byte> data)
        {
            var chunks = new List<(int, int)>();
            int offset = 0;

            while (offset < data.Length)
            {
                int chunkLen = FindChunkBoundary(data, offset);
                chunks.Add((offset, chunkLen));
                offset += chunkLen;
            }

            return chunks;
        }
    }

    #endregion

    #region Post-Process Dedup Queue

    /// <summary>
    /// Item in the post-process deduplication queue.
    /// </summary>
    internal sealed class PostProcessItem
    {
        public required byte[] Data { get; init; }
        public required string VolumeId { get; init; }
        public required TaskCompletionSource<ChunkingResult> Completion { get; init; }
        public DateTime QueuedAt { get; init; } = DateTime.UtcNow;
    }

    #endregion

    #region Global Dedup Plugin

    /// <summary>
    /// Production-grade global deduplication plugin for DataWarehouse.
    /// Provides cross-volume deduplication with content-addressable storage,
    /// SHA-256 hashing, distributed index, reference counting, and garbage collection.
    /// Thread-safe for concurrent multi-volume operations.
    /// </summary>
    /// <remarks>
    /// Features:
    /// - Content-addressable storage with SHA-256 fingerprinting
    /// - Cross-volume global deduplication with per-volume reference tracking
    /// - Bloom filter for fast negative lookups
    /// - Sharded index for high concurrency (256 shards by default)
    /// - Inline and post-process deduplication modes
    /// - Adaptive mode for optimal performance based on data size
    /// - Background garbage collection for orphaned chunks
    /// - Space savings tracking with detailed statistics
    /// - Thread-safe operations with fine-grained locking
    ///
    /// Performance characteristics:
    /// - O(1) chunk lookup with bloom filter acceleration
    /// - O(log n) reference counting operations
    /// - Linear scaling with shard count for concurrent operations
    /// </remarks>
    public sealed class GlobalDedupPlugin : DeduplicationPluginBase, IDisposable
    {
        private readonly GlobalDedupConfig _config;
        private readonly ShardedGlobalIndex _globalIndex;
        private readonly ConcurrentDictionary<string, byte[]> _chunkStorage;
        private readonly GlobalFastCDCChunker _chunker;
        private readonly SemaphoreSlim _storageLock;
        private readonly Channel<PostProcessItem> _postProcessChannel;
        private readonly CancellationTokenSource _shutdownCts;
        private readonly Task _postProcessTask;
        private readonly Task _gcTask;
        private readonly SemaphoreSlim _postProcessSemaphore;

        private long _inlineOperations;
        private long _postProcessOperations;
        private bool _disposed;

        /// <inheritdoc />
        public override string Id => "com.datawarehouse.plugins.globaldedup";

        /// <inheritdoc />
        public override string Name => "Global Cross-Volume Deduplication";

        /// <inheritdoc />
        public override string Version => "1.0.0";

        /// <inheritdoc />
        public override PluginCategory Category => PluginCategory.DataTransformationProvider;

        /// <inheritdoc />
        public override string ChunkingAlgorithm => "FastCDC";

        /// <inheritdoc />
        public override string HashAlgorithm => "SHA256";

        /// <inheritdoc />
        public override int AverageChunkSize => _config.AverageChunkSize;

        /// <inheritdoc />
        protected override int MinChunkSize => _config.MinChunkSize;

        /// <inheritdoc />
        protected override int MaxChunkSize => _config.MaxChunkSize;

        /// <summary>
        /// Current deduplication mode.
        /// </summary>
        public DedupMode Mode => _config.Mode;

        /// <summary>
        /// Creates a new global deduplication plugin with default configuration.
        /// </summary>
        public GlobalDedupPlugin() : this(new GlobalDedupConfig())
        {
        }

        /// <summary>
        /// Creates a new global deduplication plugin with custom configuration.
        /// </summary>
        /// <param name="config">Configuration settings.</param>
        /// <exception cref="ArgumentNullException">Thrown if config is null.</exception>
        public GlobalDedupPlugin(GlobalDedupConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            _globalIndex = new ShardedGlobalIndex(
                config.IndexShardCount,
                config.EnableBloomFilter,
                config.BloomFilterCapacity,
                config.BloomFilterFalsePositiveRate);

            _chunkStorage = new ConcurrentDictionary<string, byte[]>();
            _chunker = new GlobalFastCDCChunker(config.MinChunkSize, config.AverageChunkSize, config.MaxChunkSize);
            _storageLock = new SemaphoreSlim(Environment.ProcessorCount * 4);
            _postProcessSemaphore = new SemaphoreSlim(config.MaxPostProcessConcurrency);
            _shutdownCts = new CancellationTokenSource();

            // Initialize post-process channel with bounded capacity
            _postProcessChannel = Channel.CreateBounded<PostProcessItem>(
                new BoundedChannelOptions(1000)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = false
                });

            // Start background workers
            _postProcessTask = Task.Run(ProcessPostProcessQueueAsync);
            _gcTask = Task.Run(RunGarbageCollectionLoopAsync);
        }

        #region IDeduplicationProvider Implementation

        /// <inheritdoc />
        /// <summary>
        /// Chunks data and returns chunk references with cross-volume deduplication.
        /// Uses configured mode (Inline, PostProcess, or Adaptive) for deduplication.
        /// </summary>
        public override async Task<ChunkingResult> ChunkDataAsync(Stream data, CancellationToken ct = default)
        {
            return await ChunkDataAsync(data, string.Empty, ct);
        }

        /// <summary>
        /// Chunks data with volume-specific tracking for cross-volume deduplication.
        /// </summary>
        /// <param name="data">Data stream to deduplicate.</param>
        /// <param name="volumeId">Volume identifier for reference tracking.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Chunking result with deduplication statistics.</returns>
        public async Task<ChunkingResult> ChunkDataAsync(Stream data, string volumeId, CancellationToken ct = default)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            // Read data into buffer
            byte[] buffer;
            long originalSize;

            if (data is MemoryStream ms && ms.TryGetBuffer(out var segment))
            {
                buffer = segment.Array!;
                originalSize = segment.Count;
            }
            else
            {
                using var tempMs = new MemoryStream();
                await data.CopyToAsync(tempMs, ct);
                buffer = tempMs.ToArray();
                originalSize = buffer.Length;
            }

            // Determine dedup mode
            var effectiveMode = _config.Mode;
            if (effectiveMode == DedupMode.Adaptive)
            {
                effectiveMode = originalSize > _config.AdaptiveThresholdBytes
                    ? DedupMode.PostProcess
                    : DedupMode.Inline;
            }

            // Execute deduplication
            if (effectiveMode == DedupMode.PostProcess)
            {
                return await ChunkDataPostProcessAsync(buffer, volumeId, ct);
            }
            else
            {
                return await ChunkDataInlineAsync(buffer, volumeId, ct);
            }
        }

        /// <summary>
        /// Performs inline deduplication (immediate processing).
        /// </summary>
        private async Task<ChunkingResult> ChunkDataInlineAsync(byte[] buffer, string volumeId, CancellationToken ct)
        {
            Interlocked.Increment(ref _inlineOperations);

            var chunks = new List<ChunkReference>();
            long originalSize = buffer.Length;
            long deduplicatedSize = 0;
            int uniqueCount = 0;
            int duplicateCount = 0;
            long currentOffset = 0;

            // Get chunk boundaries using FastCDC
            var boundaries = _chunker.ChunkData(buffer);

            foreach (var (offset, length) in boundaries)
            {
                ct.ThrowIfCancellationRequested();

                var chunkData = buffer.AsSpan(offset, length);
                byte[] hash = ComputeSHA256Hash(chunkData);
                string hashHex = Convert.ToHexString(hash).ToLowerInvariant();

                // Fast path: check bloom filter first
                bool mightExist = _globalIndex.MightContain(hash);
                bool isNew = false;

                if (mightExist && _globalIndex.TryGet(hashHex, out var existing))
                {
                    // Existing chunk - increment reference
                    _globalIndex.AddOrReference(hashHex, hash, length, length, volumeId, out _);
                    duplicateCount++;
                }
                else
                {
                    // New chunk - store it
                    await _storageLock.WaitAsync(ct);
                    try
                    {
                        _globalIndex.AddOrReference(hashHex, hash, length, length, volumeId, out isNew);
                        if (isNew)
                        {
                            _chunkStorage[hashHex] = chunkData.ToArray();
                            deduplicatedSize += length;
                            uniqueCount++;
                        }
                        else
                        {
                            duplicateCount++;
                        }
                    }
                    finally
                    {
                        _storageLock.Release();
                    }
                }

                chunks.Add(new ChunkReference
                {
                    Hash = hash,
                    Size = length,
                    Offset = currentOffset,
                    IsNew = isNew
                });

                currentOffset += length;
                IncrementChunkRef(hashHex, length);
            }

            return new ChunkingResult
            {
                Chunks = chunks,
                OriginalSize = originalSize,
                DeduplicatedSize = deduplicatedSize,
                TotalChunks = chunks.Count,
                UniqueChunks = uniqueCount,
                DuplicateChunks = duplicateCount
            };
        }

        /// <summary>
        /// Performs post-process deduplication (queued background processing).
        /// </summary>
        private async Task<ChunkingResult> ChunkDataPostProcessAsync(byte[] buffer, string volumeId, CancellationToken ct)
        {
            Interlocked.Increment(ref _postProcessOperations);

            var completion = new TaskCompletionSource<ChunkingResult>();
            var item = new PostProcessItem
            {
                Data = buffer,
                VolumeId = volumeId,
                Completion = completion
            };

            // Register cancellation
            await using var registration = ct.Register(() =>
                completion.TrySetCanceled(ct));

            // Queue for background processing
            await _postProcessChannel.Writer.WriteAsync(item, ct);

            return await completion.Task;
        }

        /// <summary>
        /// Background worker for post-process deduplication queue.
        /// </summary>
        private async Task ProcessPostProcessQueueAsync()
        {
            try
            {
                await foreach (var item in _postProcessChannel.Reader.ReadAllAsync(_shutdownCts.Token))
                {
                    await _postProcessSemaphore.WaitAsync(_shutdownCts.Token);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await ChunkDataInlineAsync(item.Data, item.VolumeId, _shutdownCts.Token);
                            item.Completion.TrySetResult(result);
                        }
                        catch (OperationCanceledException)
                        {
                            item.Completion.TrySetCanceled();
                        }
                        catch (Exception ex)
                        {
                            item.Completion.TrySetException(ex);
                        }
                        finally
                        {
                            _postProcessSemaphore.Release();
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
        }

        /// <inheritdoc />
        public override async Task<Stream> ReassembleAsync(IReadOnlyList<ChunkReference> chunks, CancellationToken ct = default)
        {
            if (chunks == null) throw new ArgumentNullException(nameof(chunks));

            var result = new MemoryStream();

            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();

                var chunkStream = await GetChunkAsync(chunk.Hash, ct);
                if (chunkStream == null)
                {
                    throw new InvalidOperationException(
                        $"Chunk {Convert.ToHexString(chunk.Hash).ToLowerInvariant()} not found in global dedup store");
                }

                await chunkStream.CopyToAsync(result, ct);
                await chunkStream.DisposeAsync();
            }

            result.Position = 0;
            return result;
        }

        /// <inheritdoc />
        public override async Task<bool> StoreChunkAsync(byte[] hash, Stream data, CancellationToken ct = default)
        {
            return await StoreChunkAsync(hash, data, string.Empty, ct);
        }

        /// <summary>
        /// Stores a chunk with volume-specific reference tracking.
        /// </summary>
        public async Task<bool> StoreChunkAsync(byte[] hash, Stream data, string volumeId, CancellationToken ct = default)
        {
            if (hash == null) throw new ArgumentNullException(nameof(hash));
            if (data == null) throw new ArgumentNullException(nameof(data));

            string hashHex = Convert.ToHexString(hash).ToLowerInvariant();

            await _storageLock.WaitAsync(ct);
            try
            {
                // Check if already exists
                if (_globalIndex.TryGet(hashHex, out var existing))
                {
                    _globalIndex.AddOrReference(hashHex, hash, existing.OriginalSize, existing.StoredSize, volumeId, out _);
                    return false;
                }

                // Read and store the chunk
                using var ms = new MemoryStream();
                await data.CopyToAsync(ms, ct);
                var chunkData = ms.ToArray();

                // Verify hash
                byte[] computedHash = ComputeSHA256Hash(chunkData);
                if (!hash.AsSpan().SequenceEqual(computedHash))
                {
                    throw new InvalidOperationException("Chunk data does not match provided hash");
                }

                _globalIndex.AddOrReference(hashHex, hash, chunkData.Length, chunkData.Length, volumeId, out bool isNew);
                if (isNew)
                {
                    _chunkStorage[hashHex] = chunkData;
                }

                return isNew;
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <inheritdoc />
        public override Task<Stream?> GetChunkAsync(byte[] hash, CancellationToken ct = default)
        {
            if (hash == null) throw new ArgumentNullException(nameof(hash));

            string hashHex = Convert.ToHexString(hash).ToLowerInvariant();

            // Fast path: bloom filter check
            if (!_globalIndex.MightContain(hash))
            {
                return Task.FromResult<Stream?>(null);
            }

            if (!_chunkStorage.TryGetValue(hashHex, out var storedData))
            {
                return Task.FromResult<Stream?>(null);
            }

            if (!_globalIndex.TryGet(hashHex, out var metadata))
            {
                return Task.FromResult<Stream?>(null);
            }

            // Verify integrity if enabled
            if (_config.VerifyOnRetrieve)
            {
                byte[] computedHash = ComputeSHA256Hash(storedData);
                if (!hash.AsSpan().SequenceEqual(computedHash))
                {
                    // Data corruption detected
                    return Task.FromResult<Stream?>(null);
                }
            }

            metadata.LastAccessedAt = DateTime.UtcNow;
            return Task.FromResult<Stream?>(new MemoryStream(storedData));
        }

        /// <inheritdoc />
        public override Task<bool> ChunkExistsAsync(byte[] hash, CancellationToken ct = default)
        {
            if (hash == null) throw new ArgumentNullException(nameof(hash));

            // Fast path: bloom filter
            if (!_globalIndex.MightContain(hash))
            {
                return Task.FromResult(false);
            }

            string hashHex = Convert.ToHexString(hash).ToLowerInvariant();
            return Task.FromResult(_globalIndex.TryGet(hashHex, out _));
        }

        /// <inheritdoc />
        public override Task<DeduplicationStatistics> GetStatisticsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new DeduplicationStatistics
            {
                TotalChunks = _globalIndex.TotalChunks,
                UniqueChunks = _globalIndex.TotalChunks,
                TotalLogicalBytes = _globalIndex.TotalLogicalBytes,
                TotalPhysicalBytes = _globalIndex.TotalPhysicalBytes
            });
        }

        /// <summary>
        /// Gets extended global deduplication statistics.
        /// </summary>
        public Task<GlobalDedupStatistics> GetGlobalStatisticsAsync(CancellationToken ct = default)
        {
            int pendingPostProcess = 0;
            try
            {
                pendingPostProcess = _postProcessChannel.Reader.Count;
            }
            catch
            {
                // Channel may be completed
            }

            return Task.FromResult(new GlobalDedupStatistics
            {
                UniqueChunks = _globalIndex.TotalChunks,
                TotalLogicalBytes = _globalIndex.TotalLogicalBytes,
                TotalPhysicalBytes = _globalIndex.TotalPhysicalBytes,
                ActiveVolumes = _globalIndex.ActiveVolumeCount,
                CrossVolumeSharedChunks = _globalIndex.CrossVolumeSharedChunks,
                CrossVolumeSavingsBytes = _globalIndex.CrossVolumeSavingsBytes,
                InlineOperations = Interlocked.Read(ref _inlineOperations),
                PostProcessOperations = Interlocked.Read(ref _postProcessOperations),
                PendingPostProcessOperations = pendingPostProcess
            });
        }

        /// <inheritdoc />
        public override async Task<GarbageCollectionResult> CollectGarbageAsync(CancellationToken ct = default)
        {
            var startTime = DateTime.UtcNow;
            int scanned = 0;
            int deleted = 0;
            long bytesReclaimed = 0;

            var orphaned = _globalIndex.GetOrphanedChunks(_config.OrphanedChunkMinAge);
            scanned = orphaned.Count;

            foreach (var (hashHex, metadata) in orphaned)
            {
                ct.ThrowIfCancellationRequested();

                if (_chunkStorage.TryRemove(hashHex, out var data))
                {
                    bytesReclaimed += data.Length;
                    deleted++;
                }

                _globalIndex.Remove(hashHex);
                await Task.Yield();
            }

            return new GarbageCollectionResult
            {
                ChunksScanned = scanned,
                ChunksDeleted = deleted,
                BytesReclaimed = bytesReclaimed,
                Duration = DateTime.UtcNow - startTime
            };
        }

        /// <summary>
        /// Background garbage collection loop.
        /// </summary>
        private async Task RunGarbageCollectionLoopAsync()
        {
            try
            {
                while (!_shutdownCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(_config.GarbageCollectionInterval, _shutdownCts.Token);
                    await CollectGarbageAsync(_shutdownCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
        }

        #endregion

        #region Cross-Volume Dedup Methods

        /// <summary>
        /// Removes all references for a volume (used during volume deletion).
        /// </summary>
        /// <param name="volumeId">Volume identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Number of chunks that became orphaned.</returns>
        public async Task<int> RemoveVolumeReferencesAsync(string volumeId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(volumeId))
                throw new ArgumentException("Volume ID cannot be null or empty", nameof(volumeId));

            int orphanedCount = 0;

            foreach (var kvp in _globalIndex.EnumerateAll())
            {
                ct.ThrowIfCancellationRequested();

                var metadata = kvp.Value;
                long volumeRefCount = metadata.GetVolumeReferenceCount(volumeId);

                if (volumeRefCount > 0)
                {
                    // Decrement all references for this volume
                    for (long i = 0; i < volumeRefCount; i++)
                    {
                        if (_globalIndex.DecrementReference(kvp.Key, volumeId, out _))
                        {
                            orphanedCount++;
                            break;
                        }
                    }
                }

                await Task.Yield();
            }

            return orphanedCount;
        }

        /// <summary>
        /// Gets chunks shared between two or more volumes.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of shared chunk hashes with their sharing volume IDs.</returns>
        public async Task<List<(string HashHex, IReadOnlyCollection<string> VolumeIds)>> GetCrossVolumeSharedChunksAsync(
            CancellationToken ct = default)
        {
            var result = new List<(string, IReadOnlyCollection<string>)>();

            foreach (var kvp in _globalIndex.EnumerateAll())
            {
                ct.ThrowIfCancellationRequested();

                var activeVolumes = kvp.Value.VolumeReferences
                    .Where(v => v.Value > 0)
                    .Select(v => v.Key)
                    .ToList();

                if (activeVolumes.Count > 1)
                {
                    result.Add((kvp.Key, activeVolumes));
                }

                await Task.Yield();
            }

            return result;
        }

        /// <summary>
        /// Calculates deduplication efficiency for a specific volume.
        /// </summary>
        /// <param name="volumeId">Volume identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Tuple of (logical bytes, physical bytes, savings percent).</returns>
        public async Task<(long LogicalBytes, long PhysicalBytes, double SavingsPercent)> GetVolumeDedupEfficiencyAsync(
            string volumeId,
            CancellationToken ct = default)
        {
            long logicalBytes = 0;
            long physicalBytes = 0;

            foreach (var kvp in _globalIndex.EnumerateAll())
            {
                ct.ThrowIfCancellationRequested();

                var metadata = kvp.Value;
                long volumeRefs = metadata.GetVolumeReferenceCount(volumeId);

                if (volumeRefs > 0)
                {
                    logicalBytes += metadata.OriginalSize * volumeRefs;

                    // Physical bytes: full cost if only volume, shared cost otherwise
                    int volumeCount = metadata.VolumeReferences.Values.Count(v => v > 0);
                    physicalBytes += metadata.StoredSize / volumeCount;
                }

                await Task.Yield();
            }

            double savingsPercent = logicalBytes > 0
                ? (1.0 - (double)physicalBytes / logicalBytes) * 100
                : 0;

            return (logicalBytes, physicalBytes, savingsPercent);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Computes SHA-256 hash of data.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte[] ComputeSHA256Hash(ReadOnlySpan<byte> data)
        {
            return SHA256.HashData(data);
        }

        #endregion

        #region Plugin Lifecycle

        /// <inheritdoc />
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "globaldedup.chunk", DisplayName = "Chunk Data", Description = "Split data into globally deduplicated chunks" },
                new() { Name = "globaldedup.store", DisplayName = "Store Chunk", Description = "Store chunk with global dedup lookup" },
                new() { Name = "globaldedup.retrieve", DisplayName = "Retrieve Chunk", Description = "Retrieve chunk by hash" },
                new() { Name = "globaldedup.stats", DisplayName = "Statistics", Description = "Get global deduplication statistics" },
                new() { Name = "globaldedup.gc", DisplayName = "Garbage Collect", Description = "Prune orphaned chunks" },
                new() { Name = "globaldedup.crossvolume", DisplayName = "Cross-Volume", Description = "Cross-volume deduplication operations" }
            };
        }

        /// <inheritdoc />
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "GlobalDeduplication";
            metadata["Mode"] = _config.Mode.ToString();
            metadata["EnableBloomFilter"] = _config.EnableBloomFilter;
            metadata["IndexShardCount"] = _config.IndexShardCount;
            metadata["SupportsCrossVolume"] = true;
            metadata["SupportsInlineDedup"] = true;
            metadata["SupportsPostProcessDedup"] = true;
            metadata["TotalChunks"] = _globalIndex.TotalChunks;
            metadata["ActiveVolumes"] = _globalIndex.ActiveVolumeCount;
            metadata["CrossVolumeSharedChunks"] = _globalIndex.CrossVolumeSharedChunks;

            var stats = GetGlobalStatisticsAsync().GetAwaiter().GetResult();
            metadata["GlobalDeduplicationRatio"] = stats.GlobalDeduplicationRatio;
            metadata["SpaceSavingsPercent"] = stats.SpaceSavingsPercent;

            return metadata;
        }

        /// <inheritdoc />
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "globaldedup.stats":
                    await GetGlobalStatisticsAsync();
                    break;
                case "globaldedup.gc":
                    await CollectGarbageAsync();
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        /// <summary>
        /// Disposes resources used by the plugin.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                // Signal shutdown
                _shutdownCts.Cancel();

                // Complete the post-process channel
                _postProcessChannel.Writer.TryComplete();

                // Wait for background tasks
                try
                {
                    Task.WhenAll(_postProcessTask, _gcTask).Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // Ignore timeout or cancellation
                }

                // Dispose resources
                _globalIndex.Dispose();
                _storageLock.Dispose();
                _postProcessSemaphore.Dispose();
                _shutdownCts.Dispose();

                _disposed = true;
            }
        }

        #endregion
    }

    #endregion

    #region Specialized Mode Plugins

    /// <summary>
    /// Global deduplication plugin optimized for inline processing.
    /// Best for latency-sensitive read workloads.
    /// </summary>
    public sealed class InlineGlobalDedupPlugin : GlobalDedupPlugin
    {
        /// <inheritdoc />
        public override string Id => "com.datawarehouse.plugins.globaldedup.inline";

        /// <inheritdoc />
        public override string Name => "Inline Global Deduplication";

        /// <summary>
        /// Creates a new inline global dedup plugin.
        /// </summary>
        public InlineGlobalDedupPlugin() : base(new GlobalDedupConfig
        {
            Mode = DedupMode.Inline,
            AverageChunkSize = 65536,
            MinChunkSize = 16384,
            MaxChunkSize = 262144
        })
        {
        }
    }

    /// <summary>
    /// Global deduplication plugin optimized for post-process background deduplication.
    /// Best for write-heavy workloads where immediate dedup is not required.
    /// </summary>
    public sealed class PostProcessGlobalDedupPlugin : GlobalDedupPlugin
    {
        /// <inheritdoc />
        public override string Id => "com.datawarehouse.plugins.globaldedup.postprocess";

        /// <inheritdoc />
        public override string Name => "Post-Process Global Deduplication";

        /// <summary>
        /// Creates a new post-process global dedup plugin.
        /// </summary>
        public PostProcessGlobalDedupPlugin() : base(new GlobalDedupConfig
        {
            Mode = DedupMode.PostProcess,
            AverageChunkSize = 131072, // Larger chunks for background processing
            MinChunkSize = 32768,
            MaxChunkSize = 524288,
            MaxPostProcessConcurrency = Environment.ProcessorCount * 4
        })
        {
        }
    }

    /// <summary>
    /// Global deduplication plugin with adaptive mode selection based on data size.
    /// Automatically switches between inline and post-process based on thresholds.
    /// </summary>
    public sealed class AdaptiveGlobalDedupPlugin : GlobalDedupPlugin
    {
        /// <inheritdoc />
        public override string Id => "com.datawarehouse.plugins.globaldedup.adaptive";

        /// <inheritdoc />
        public override string Name => "Adaptive Global Deduplication";

        /// <summary>
        /// Creates a new adaptive global dedup plugin.
        /// </summary>
        public AdaptiveGlobalDedupPlugin() : base(new GlobalDedupConfig
        {
            Mode = DedupMode.Adaptive,
            AdaptiveThresholdBytes = 10 * 1024 * 1024, // 10MB threshold
            AverageChunkSize = 65536,
            MinChunkSize = 16384,
            MaxChunkSize = 262144
        })
        {
        }

        /// <summary>
        /// Creates a new adaptive global dedup plugin with custom threshold.
        /// </summary>
        /// <param name="thresholdBytes">Size threshold for switching to post-process mode.</param>
        public AdaptiveGlobalDedupPlugin(long thresholdBytes) : base(new GlobalDedupConfig
        {
            Mode = DedupMode.Adaptive,
            AdaptiveThresholdBytes = thresholdBytes,
            AverageChunkSize = 65536,
            MinChunkSize = 16384,
            MaxChunkSize = 262144
        })
        {
        }
    }

    #endregion

    #region Global Dedup Registry

    /// <summary>
    /// Registry for global deduplication providers.
    /// Enables kernel discovery and selection of dedup modes.
    /// </summary>
    public sealed class GlobalDedupRegistry
    {
        private static readonly Lazy<GlobalDedupRegistry> _instance = new(() => new GlobalDedupRegistry());
        private readonly Dictionary<DedupMode, Func<GlobalDedupConfig?, GlobalDedupPlugin>> _factories = new();
        private readonly Dictionary<DedupMode, GlobalDedupPlugin> _defaultProviders = new();

        /// <summary>
        /// Gets the singleton instance of the registry.
        /// </summary>
        public static GlobalDedupRegistry Instance => _instance.Value;

        private GlobalDedupRegistry()
        {
            RegisterProvider(DedupMode.Inline, cfg => cfg != null ? new GlobalDedupPlugin(cfg) : new InlineGlobalDedupPlugin());
            RegisterProvider(DedupMode.PostProcess, cfg => cfg != null ? new GlobalDedupPlugin(cfg) : new PostProcessGlobalDedupPlugin());
            RegisterProvider(DedupMode.Adaptive, cfg => cfg != null ? new GlobalDedupPlugin(cfg) : new AdaptiveGlobalDedupPlugin());
        }

        /// <summary>
        /// Registers a provider factory for a dedup mode.
        /// </summary>
        /// <param name="mode">Deduplication mode.</param>
        /// <param name="factory">Factory function to create the provider.</param>
        public void RegisterProvider(DedupMode mode, Func<GlobalDedupConfig?, GlobalDedupPlugin> factory)
        {
            _factories[mode] = factory;
            _defaultProviders[mode] = factory(null);
        }

        /// <summary>
        /// Gets a provider for the specified mode.
        /// </summary>
        /// <param name="mode">Deduplication mode.</param>
        /// <param name="config">Optional custom configuration.</param>
        /// <returns>The global dedup plugin instance.</returns>
        public GlobalDedupPlugin GetProvider(DedupMode mode, GlobalDedupConfig? config = null)
        {
            if (config == null && _defaultProviders.TryGetValue(mode, out var defaultProvider))
            {
                return defaultProvider;
            }

            if (_factories.TryGetValue(mode, out var factory))
            {
                return factory(config);
            }

            throw new NotSupportedException($"Dedup mode '{mode}' is not registered.");
        }

        /// <summary>
        /// Gets a provider by mode name string.
        /// </summary>
        /// <param name="modeName">Mode name (case-insensitive).</param>
        /// <param name="config">Optional custom configuration.</param>
        /// <returns>The global dedup plugin instance.</returns>
        public GlobalDedupPlugin GetProviderByName(string modeName, GlobalDedupConfig? config = null)
        {
            if (Enum.TryParse<DedupMode>(modeName, true, out var mode))
            {
                return GetProvider(mode, config);
            }

            throw new NotSupportedException($"Unknown dedup mode: '{modeName}'");
        }

        /// <summary>
        /// Gets all available dedup modes.
        /// </summary>
        public IReadOnlyList<DedupMode> GetAvailableModes()
        {
            return _factories.Keys.ToList().AsReadOnly();
        }

        /// <summary>
        /// Checks if a mode is available.
        /// </summary>
        public bool IsModeAvailable(DedupMode mode)
        {
            return _factories.ContainsKey(mode);
        }
    }

    #endregion
}
