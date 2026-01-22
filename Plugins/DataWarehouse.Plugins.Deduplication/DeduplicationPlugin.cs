using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace DataWarehouse.Plugins.Deduplication
{
    #region Chunking Strategy Enums and Configuration

    /// <summary>
    /// Chunking algorithm selection for deduplication.
    /// </summary>
    public enum ChunkingStrategy
    {
        /// <summary>Fixed-size chunks - simple but less effective for dedup.</summary>
        Fixed,
        /// <summary>Rabin fingerprint CDC - classic content-defined chunking.</summary>
        Rabin,
        /// <summary>FastCDC - high-performance gear-based CDC with normalized chunking.</summary>
        FastCDC
    }

    /// <summary>
    /// Configuration for the deduplication plugin.
    /// </summary>
    public sealed class DeduplicationConfig
    {
        /// <summary>Chunking strategy to use.</summary>
        public ChunkingStrategy Strategy { get; set; } = ChunkingStrategy.FastCDC;

        /// <summary>Target average chunk size in bytes. Default 8KB.</summary>
        public int AverageChunkSize { get; set; } = 8192;

        /// <summary>Minimum chunk size in bytes. Default 2KB.</summary>
        public int MinChunkSize { get; set; } = 2048;

        /// <summary>Maximum chunk size in bytes. Default 64KB.</summary>
        public int MaxChunkSize { get; set; } = 65536;

        /// <summary>Sliding window size for Rabin fingerprinting. Default 48 bytes.</summary>
        public int WindowSize { get; set; } = 48;

        /// <summary>Storage path for chunk data.</summary>
        public string ChunkStoragePath { get; set; } = "chunks";

        /// <summary>Enable delta encoding for similar chunks.</summary>
        public bool EnableDeltaEncoding { get; set; } = true;

        /// <summary>Similarity threshold for delta encoding (0.0-1.0).</summary>
        public double DeltaSimilarityThreshold { get; set; } = 0.5;

        /// <summary>Number of shards for chunk index (for parallel access).</summary>
        public int IndexShardCount { get; set; } = 64;

        /// <summary>Enable integrity verification on chunk retrieval.</summary>
        public bool VerifyOnRetrieve { get; set; } = true;
    }

    #endregion

    #region Chunk Storage Types

    /// <summary>
    /// Metadata for a stored chunk including reference count.
    /// </summary>
    public sealed class ChunkMetadata
    {
        public byte[] Hash { get; init; } = Array.Empty<byte>();
        public int OriginalSize { get; init; }
        public int StoredSize { get; init; }
        public long ReferenceCount { get; set; }
        public DateTime CreatedAt { get; init; }
        public DateTime LastAccessedAt { get; set; }
        public bool IsDeltaEncoded { get; init; }
        public byte[]? BaseChunkHash { get; init; }
    }

    /// <summary>
    /// Result of chunk integrity verification.
    /// </summary>
    public sealed class IntegrityVerificationResult
    {
        public int TotalChunks { get; init; }
        public int VerifiedChunks { get; init; }
        public int CorruptedChunks { get; init; }
        public IReadOnlyList<string> CorruptedChunkHashes { get; init; } = Array.Empty<string>();
        public TimeSpan Duration { get; init; }
        public bool AllValid => CorruptedChunks == 0;
    }

    #endregion

    #region Rabin Fingerprint Implementation

    /// <summary>
    /// Production-grade Rabin fingerprint implementation using polynomial arithmetic in GF(2).
    /// Supports rolling hash computation for efficient content-defined chunking.
    /// </summary>
    public sealed class RabinFingerprint
    {
        // Irreducible polynomial for GF(2^64): x^64 + x^4 + x^3 + x + 1
        // Represented as 0xbfe6b8a5bf378d83 (commonly used in dedup systems)
        private const ulong IrreduciblePolynomial = 0xbfe6b8a5bf378d83UL;

        private readonly int _windowSize;
        private readonly ulong[] _pushTable;
        private readonly ulong[] _popTable;
        private readonly byte[] _window;
        private int _windowIndex;
        private ulong _fingerprint;

        /// <summary>
        /// Creates a new Rabin fingerprint calculator.
        /// </summary>
        /// <param name="windowSize">Size of the sliding window in bytes.</param>
        public RabinFingerprint(int windowSize = 48)
        {
            _windowSize = windowSize;
            _window = new byte[windowSize];
            _pushTable = new ulong[256];
            _popTable = new ulong[256];
            _windowIndex = 0;
            _fingerprint = 0;

            InitializeTables();
        }

        /// <summary>
        /// Precomputes lookup tables for polynomial operations.
        /// </summary>
        private void InitializeTables()
        {
            // Push table: precompute polynomial multiplication for each byte value
            for (int i = 0; i < 256; i++)
            {
                ulong fp = 0;
                for (int j = 0; j < 8; j++)
                {
                    if ((i & (1 << j)) != 0)
                    {
                        fp ^= IrreduciblePolynomial >> (7 - j);
                    }
                }
                _pushTable[i] = fp;
            }

            // Pop table: precompute the polynomial for removing old bytes from window
            // This accounts for the window size shift in the polynomial
            ulong shiftedPoly = ComputeShiftedPolynomial(_windowSize);
            for (int i = 0; i < 256; i++)
            {
                _popTable[i] = MultiplyByteByShiftedPoly((byte)i, shiftedPoly);
            }
        }

        /// <summary>
        /// Computes the polynomial shifted by window size positions.
        /// </summary>
        private static ulong ComputeShiftedPolynomial(int windowSize)
        {
            ulong result = 1;
            for (int i = 0; i < windowSize * 8; i++)
            {
                bool highBitSet = (result & 0x8000000000000000UL) != 0;
                result <<= 1;
                if (highBitSet)
                {
                    result ^= IrreduciblePolynomial;
                }
            }
            return result;
        }

        /// <summary>
        /// Multiplies a byte value by the shifted polynomial in GF(2).
        /// </summary>
        private static ulong MultiplyByteByShiftedPoly(byte b, ulong shiftedPoly)
        {
            ulong result = 0;
            ulong poly = shiftedPoly;

            for (int i = 0; i < 8; i++)
            {
                if ((b & (1 << i)) != 0)
                {
                    result ^= poly;
                }
                bool highBit = (poly & 0x8000000000000000UL) != 0;
                poly <<= 1;
                if (highBit)
                {
                    poly ^= IrreduciblePolynomial;
                }
            }

            return result;
        }

        /// <summary>
        /// Resets the fingerprint state for a new stream.
        /// </summary>
        public void Reset()
        {
            _fingerprint = 0;
            _windowIndex = 0;
            Array.Clear(_window, 0, _windowSize);
        }

        /// <summary>
        /// Slides the window by one byte and returns the new fingerprint.
        /// </summary>
        /// <param name="newByte">The new byte entering the window.</param>
        /// <returns>The updated fingerprint value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Slide(byte newByte)
        {
            byte oldByte = _window[_windowIndex];
            _window[_windowIndex] = newByte;
            _windowIndex = (_windowIndex + 1) % _windowSize;

            // Remove contribution of old byte and add new byte
            _fingerprint = (_fingerprint << 8) ^ _pushTable[newByte] ^ _popTable[oldByte];

            return _fingerprint;
        }

        /// <summary>
        /// Gets the current fingerprint value.
        /// </summary>
        public ulong CurrentFingerprint => _fingerprint;

        /// <summary>
        /// Computes a non-rolling fingerprint of an entire buffer.
        /// Used for chunk hashing validation.
        /// </summary>
        public static ulong ComputeFingerprint(ReadOnlySpan<byte> data)
        {
            ulong fp = 0;
            foreach (byte b in data)
            {
                bool highBit = (fp & 0x8000000000000000UL) != 0;
                fp = (fp << 8) ^ b;
                if (highBit)
                {
                    fp ^= IrreduciblePolynomial;
                }
            }
            return fp;
        }
    }

    #endregion

    #region FastCDC Implementation

    /// <summary>
    /// High-performance FastCDC (Fast Content-Defined Chunking) implementation.
    /// Uses gear-based rolling hash with normalized chunking for better dedup ratios.
    /// Based on the FastCDC paper by Xia et al.
    /// </summary>
    public sealed class FastCDCChunker
    {
        // Gear table: random 64-bit values for each byte value
        // These are specifically chosen to have good statistical properties
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
        public FastCDCChunker(int minSize, int avgSize, int maxSize)
        {
            _minSize = minSize;
            _avgSize = avgSize;
            _maxSize = maxSize;

            // Normalized chunking: use different masks before and after normal point
            // This improves chunk size distribution
            _normalSize = avgSize;

            // Compute masks based on average size
            // Mask determines probability of chunk boundary
            int bits = (int)Math.Ceiling(Math.Log2(avgSize));
            _maskSmall = (1UL << (bits + 1)) - 1;  // Larger mask = harder to match = larger chunks before normal
            _maskLarge = (1UL << (bits - 1)) - 1;  // Smaller mask = easier to match = smaller chunks after normal
        }

        /// <summary>
        /// Generates the gear hash lookup table using a deterministic PRNG.
        /// </summary>
        private static ulong[] GenerateGearTable()
        {
            var table = new ulong[256];
            // Use a fixed seed for reproducibility
            ulong state = 0x123456789ABCDEF0UL;

            for (int i = 0; i < 256; i++)
            {
                // Simple xorshift64* PRNG
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
        /// <param name="offset">Starting offset in data.</param>
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

            // Start after minimum size
            int pos = offset + _minSize;
            int normalPoint = offset + _normalSize;
            int endPoint = offset + remaining;

            ulong hash = 0;

            // Phase 1: Before normal point, use larger mask (harder to match)
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

            // Phase 2: After normal point, use smaller mask (easier to match)
            while (pos < endPoint)
            {
                hash = (hash << 1) + GearTable[data[pos]];
                if ((hash & _maskLarge) == 0)
                {
                    return pos - offset + 1;
                }
                pos++;
            }

            // Reached max size without finding boundary
            return remaining;
        }

        /// <summary>
        /// Chunks an entire data buffer using FastCDC.
        /// </summary>
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

    #region Delta Encoding Implementation

    /// <summary>
    /// Delta encoding for storing similar chunks efficiently.
    /// Uses a simple but effective byte-level diff algorithm.
    /// </summary>
    public static class DeltaEncoder
    {
        private const byte OP_COPY = 0x00;   // Copy from base chunk
        private const byte OP_INSERT = 0x01; // Insert new data
        private const byte OP_END = 0xFF;    // End of delta

        /// <summary>
        /// Computes delta between target and base chunks.
        /// Returns null if delta would be larger than target (not worth it).
        /// </summary>
        public static byte[]? ComputeDelta(ReadOnlySpan<byte> target, ReadOnlySpan<byte> baseChunk)
        {
            if (target.Length == 0 || baseChunk.Length == 0)
            {
                return null;
            }

            using var delta = new MemoryStream();
            using var writer = new BinaryWriter(delta);

            int targetPos = 0;
            int insertStart = -1;

            while (targetPos < target.Length)
            {
                // Try to find a match in base chunk
                var (matchOffset, matchLength) = FindBestMatch(target, targetPos, baseChunk);

                if (matchLength >= 4) // Minimum match length to be worthwhile
                {
                    // Flush any pending inserts
                    if (insertStart >= 0)
                    {
                        WriteInsert(writer, target.Slice(insertStart, targetPos - insertStart));
                        insertStart = -1;
                    }

                    // Write copy operation
                    WriteCopy(writer, matchOffset, matchLength);
                    targetPos += matchLength;
                }
                else
                {
                    // Start or continue insert
                    if (insertStart < 0)
                    {
                        insertStart = targetPos;
                    }
                    targetPos++;
                }
            }

            // Flush remaining inserts
            if (insertStart >= 0)
            {
                WriteInsert(writer, target.Slice(insertStart, target.Length - insertStart));
            }

            writer.Write(OP_END);
            writer.Flush();

            var result = delta.ToArray();

            // Only return delta if it's actually smaller
            return result.Length < target.Length ? result : null;
        }

        /// <summary>
        /// Applies delta to base chunk to reconstruct target.
        /// </summary>
        public static byte[] ApplyDelta(ReadOnlySpan<byte> delta, ReadOnlySpan<byte> baseChunk)
        {
            using var result = new MemoryStream();
            int pos = 0;

            while (pos < delta.Length)
            {
                byte op = delta[pos++];

                if (op == OP_END)
                {
                    break;
                }
                else if (op == OP_COPY)
                {
                    // Read offset and length (varint encoded)
                    int offset = ReadVarint(delta, ref pos);
                    int length = ReadVarint(delta, ref pos);

                    if (offset + length <= baseChunk.Length)
                    {
                        result.Write(baseChunk.Slice(offset, length));
                    }
                }
                else if (op == OP_INSERT)
                {
                    int length = ReadVarint(delta, ref pos);
                    if (pos + length <= delta.Length)
                    {
                        result.Write(delta.Slice(pos, length));
                        pos += length;
                    }
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// Calculates similarity ratio between two chunks (0.0-1.0).
        /// </summary>
        public static double CalculateSimilarity(ReadOnlySpan<byte> chunk1, ReadOnlySpan<byte> chunk2)
        {
            if (chunk1.Length == 0 || chunk2.Length == 0)
            {
                return 0.0;
            }

            // Use simple byte histogram comparison for efficiency
            var hist1 = new int[256];
            var hist2 = new int[256];

            foreach (byte b in chunk1) hist1[b]++;
            foreach (byte b in chunk2) hist2[b]++;

            int common = 0;
            int total = 0;

            for (int i = 0; i < 256; i++)
            {
                common += Math.Min(hist1[i], hist2[i]);
                total += Math.Max(hist1[i], hist2[i]);
            }

            return total > 0 ? (double)common / total : 0.0;
        }

        private static (int offset, int length) FindBestMatch(
            ReadOnlySpan<byte> target, int targetPos,
            ReadOnlySpan<byte> baseChunk)
        {
            int bestOffset = 0;
            int bestLength = 0;
            int maxSearch = Math.Min(1000, baseChunk.Length); // Limit search for performance

            for (int i = 0; i < maxSearch; i++)
            {
                int matchLen = 0;
                while (targetPos + matchLen < target.Length &&
                       i + matchLen < baseChunk.Length &&
                       target[targetPos + matchLen] == baseChunk[i + matchLen] &&
                       matchLen < 65535)
                {
                    matchLen++;
                }

                if (matchLen > bestLength)
                {
                    bestOffset = i;
                    bestLength = matchLen;
                }
            }

            return (bestOffset, bestLength);
        }

        private static void WriteCopy(BinaryWriter writer, int offset, int length)
        {
            writer.Write(OP_COPY);
            WriteVarint(writer, offset);
            WriteVarint(writer, length);
        }

        private static void WriteInsert(BinaryWriter writer, ReadOnlySpan<byte> data)
        {
            writer.Write(OP_INSERT);
            WriteVarint(writer, data.Length);
            writer.Write(data);
        }

        private static void WriteVarint(BinaryWriter writer, int value)
        {
            while (value >= 0x80)
            {
                writer.Write((byte)(value | 0x80));
                value >>= 7;
            }
            writer.Write((byte)value);
        }

        private static int ReadVarint(ReadOnlySpan<byte> data, ref int pos)
        {
            int result = 0;
            int shift = 0;

            while (pos < data.Length)
            {
                byte b = data[pos++];
                result |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    break;
                }
                shift += 7;
            }

            return result;
        }
    }

    #endregion

    #region Sharded Chunk Index

    /// <summary>
    /// Thread-safe sharded chunk index for concurrent access.
    /// Sharding reduces lock contention for TB-scale datasets.
    /// </summary>
    public sealed class ShardedChunkIndex : IDisposable
    {
        private readonly int _shardCount;
        private readonly ConcurrentDictionary<string, ChunkMetadata>[] _shards;
        private readonly ReaderWriterLockSlim[] _shardLocks;
        private long _totalChunks;
        private long _totalLogicalBytes;
        private long _totalPhysicalBytes;

        public ShardedChunkIndex(int shardCount = 64)
        {
            _shardCount = shardCount;
            _shards = new ConcurrentDictionary<string, ChunkMetadata>[shardCount];
            _shardLocks = new ReaderWriterLockSlim[shardCount];

            for (int i = 0; i < shardCount; i++)
            {
                _shards[i] = new ConcurrentDictionary<string, ChunkMetadata>();
                _shardLocks[i] = new ReaderWriterLockSlim();
            }
        }

        /// <summary>
        /// Gets the shard index for a given hash.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetShardIndex(string hashHex)
        {
            // Use first bytes of hash for sharding
            if (hashHex.Length >= 2 && int.TryParse(hashHex[..2], System.Globalization.NumberStyles.HexNumber, null, out int val))
            {
                return val % _shardCount;
            }
            return Math.Abs(hashHex.GetHashCode()) % _shardCount;
        }

        /// <summary>
        /// Tries to get chunk metadata, incrementing reference count if found.
        /// </summary>
        public bool TryGetAndReference(string hashHex, out ChunkMetadata? metadata)
        {
            int shard = GetShardIndex(hashHex);
            _shardLocks[shard].EnterUpgradeableReadLock();
            try
            {
                if (_shards[shard].TryGetValue(hashHex, out metadata))
                {
                    _shardLocks[shard].EnterWriteLock();
                    try
                    {
                        Interlocked.Increment(ref metadata.ReferenceCount);
                        metadata.LastAccessedAt = DateTime.UtcNow;
                        Interlocked.Add(ref _totalLogicalBytes, metadata.OriginalSize);
                    }
                    finally
                    {
                        _shardLocks[shard].ExitWriteLock();
                    }
                    return true;
                }
                metadata = null;
                return false;
            }
            finally
            {
                _shardLocks[shard].ExitUpgradeableReadLock();
            }
        }

        /// <summary>
        /// Adds or updates chunk metadata, handling reference counting.
        /// </summary>
        public bool AddOrReference(string hashHex, ChunkMetadata metadata, out bool isNew)
        {
            int shard = GetShardIndex(hashHex);
            _shardLocks[shard].EnterWriteLock();
            try
            {
                if (_shards[shard].TryGetValue(hashHex, out var existing))
                {
                    Interlocked.Increment(ref existing.ReferenceCount);
                    existing.LastAccessedAt = DateTime.UtcNow;
                    Interlocked.Add(ref _totalLogicalBytes, existing.OriginalSize);
                    isNew = false;
                    return true;
                }

                metadata.ReferenceCount = 1;
                if (_shards[shard].TryAdd(hashHex, metadata))
                {
                    Interlocked.Increment(ref _totalChunks);
                    Interlocked.Add(ref _totalLogicalBytes, metadata.OriginalSize);
                    Interlocked.Add(ref _totalPhysicalBytes, metadata.StoredSize);
                    isNew = true;
                    return true;
                }

                isNew = false;
                return false;
            }
            finally
            {
                _shardLocks[shard].ExitWriteLock();
            }
        }

        /// <summary>
        /// Decrements reference count, returns true if chunk should be deleted.
        /// </summary>
        public bool DecrementReference(string hashHex, out ChunkMetadata? metadata)
        {
            int shard = GetShardIndex(hashHex);
            _shardLocks[shard].EnterWriteLock();
            try
            {
                if (_shards[shard].TryGetValue(hashHex, out metadata))
                {
                    var newCount = Interlocked.Decrement(ref metadata.ReferenceCount);
                    Interlocked.Add(ref _totalLogicalBytes, -metadata.OriginalSize);

                    if (newCount <= 0)
                    {
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
        /// Checks if a chunk exists without modifying reference count.
        /// </summary>
        public bool Contains(string hashHex)
        {
            int shard = GetShardIndex(hashHex);
            return _shards[shard].ContainsKey(hashHex);
        }

        /// <summary>
        /// Gets chunk metadata without modifying reference count.
        /// </summary>
        public ChunkMetadata? Get(string hashHex)
        {
            int shard = GetShardIndex(hashHex);
            _shards[shard].TryGetValue(hashHex, out var metadata);
            return metadata;
        }

        /// <summary>
        /// Enumerates all chunks for garbage collection or verification.
        /// </summary>
        public IEnumerable<KeyValuePair<string, ChunkMetadata>> EnumerateAll()
        {
            for (int i = 0; i < _shardCount; i++)
            {
                foreach (var kvp in _shards[i])
                {
                    yield return kvp;
                }
            }
        }

        /// <summary>
        /// Gets chunks with zero reference count for garbage collection.
        /// </summary>
        public List<(string Hash, ChunkMetadata Metadata)> GetUnreferencedChunks()
        {
            var result = new List<(string, ChunkMetadata)>();

            for (int i = 0; i < _shardCount; i++)
            {
                foreach (var kvp in _shards[i])
                {
                    if (Interlocked.Read(ref kvp.Value.ReferenceCount) <= 0)
                    {
                        result.Add((kvp.Key, kvp.Value));
                    }
                }
            }

            return result;
        }

        public long TotalChunks => Interlocked.Read(ref _totalChunks);
        public long TotalLogicalBytes => Interlocked.Read(ref _totalLogicalBytes);
        public long TotalPhysicalBytes => Interlocked.Read(ref _totalPhysicalBytes);

        public void Dispose()
        {
            foreach (var lockObj in _shardLocks)
            {
                lockObj.Dispose();
            }
        }
    }

    #endregion

    #region Main Deduplication Plugin

    /// <summary>
    /// Production-grade deduplication plugin for DataWarehouse.
    /// Supports Rabin fingerprinting, FastCDC, fixed-size chunking, SHA-256 hashing,
    /// reference counting, and delta encoding for TB-scale data deduplication.
    /// </summary>
    public sealed class DeduplicationPlugin : DeduplicationPluginBase, IDisposable
    {
        private readonly DeduplicationConfig _config;
        private readonly ShardedChunkIndex _chunkIndex;
        private readonly ConcurrentDictionary<string, byte[]> _chunkStorage;
        private readonly ConcurrentDictionary<ulong, string> _fingerprintToHash;
        private readonly RabinFingerprint _rabin;
        private readonly FastCDCChunker _fastCdc;
        private readonly SemaphoreSlim _storageLock;
        private bool _disposed;

        public override string Id => "datawarehouse.plugins.deduplication";
        public override string Name => "Enterprise Deduplication";
        public override string Version => "1.0.0";
        public override PluginCategory Category => PluginCategory.DataTransformationProvider;
        public override string ChunkingAlgorithm => _config.Strategy.ToString();
        public override int AverageChunkSize => _config.AverageChunkSize;
        protected override int MinChunkSize => _config.MinChunkSize;
        protected override int MaxChunkSize => _config.MaxChunkSize;

        /// <summary>
        /// Creates a new deduplication plugin with default configuration.
        /// </summary>
        public DeduplicationPlugin() : this(new DeduplicationConfig())
        {
        }

        /// <summary>
        /// Creates a new deduplication plugin with custom configuration.
        /// </summary>
        public DeduplicationPlugin(DeduplicationConfig config)
        {
            _config = config;
            _chunkIndex = new ShardedChunkIndex(config.IndexShardCount);
            _chunkStorage = new ConcurrentDictionary<string, byte[]>();
            _fingerprintToHash = new ConcurrentDictionary<ulong, string>();
            _rabin = new RabinFingerprint(config.WindowSize);
            _fastCdc = new FastCDCChunker(config.MinChunkSize, config.AverageChunkSize, config.MaxChunkSize);
            _storageLock = new SemaphoreSlim(Environment.ProcessorCount * 2);
        }

        #region IDeduplicationProvider Implementation

        /// <summary>
        /// Chunks data using the configured chunking strategy and returns chunk references.
        /// </summary>
        public override async Task<ChunkingResult> ChunkDataAsync(Stream data, CancellationToken ct = default)
        {
            var chunks = new List<ChunkReference>();
            long originalSize = 0;
            long deduplicatedSize = 0;
            int uniqueCount = 0;
            int duplicateCount = 0;

            // Read entire stream for chunking (can be optimized for streaming in future)
            byte[] buffer;
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

            // Get chunk boundaries based on strategy
            var boundaries = GetChunkBoundaries(buffer);
            long currentOffset = 0;

            foreach (var (offset, length) in boundaries)
            {
                ct.ThrowIfCancellationRequested();

                var chunkData = buffer.AsSpan(offset, length);
                byte[] hash = ComputeHash(chunkData.ToArray());
                string hashHex = HashToHex(hash);

                // Check if chunk already exists
                bool isNew;
                if (_chunkIndex.TryGetAndReference(hashHex, out var existing))
                {
                    // Duplicate chunk
                    duplicateCount++;
                    isNew = false;
                }
                else
                {
                    // New chunk - store it
                    byte[]? dataToStore = chunkData.ToArray();
                    int storedSize = length;

                    // Try delta encoding if enabled
                    byte[]? baseHash = null;
                    if (_config.EnableDeltaEncoding)
                    {
                        var (deltaData, baseChunkHash) = TryDeltaEncode(chunkData);
                        if (deltaData != null)
                        {
                            dataToStore = deltaData;
                            storedSize = deltaData.Length;
                            baseHash = baseChunkHash;
                        }
                    }

                    var metadata = new ChunkMetadata
                    {
                        Hash = hash,
                        OriginalSize = length,
                        StoredSize = storedSize,
                        CreatedAt = DateTime.UtcNow,
                        LastAccessedAt = DateTime.UtcNow,
                        IsDeltaEncoded = baseHash != null,
                        BaseChunkHash = baseHash
                    };

                    _chunkIndex.AddOrReference(hashHex, metadata, out isNew);

                    if (isNew)
                    {
                        _chunkStorage[hashHex] = dataToStore;
                        deduplicatedSize += storedSize;
                        uniqueCount++;

                        // Store fingerprint mapping for delta encoding similarity search
                        ulong fingerprint = RabinFingerprint.ComputeFingerprint(chunkData);
                        _fingerprintToHash.TryAdd(fingerprint, hashHex);
                    }
                    else
                    {
                        duplicateCount++;
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

                // Increment reference in base class tracking
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
        /// Reassembles data from chunk references.
        /// </summary>
        public override async Task<Stream> ReassembleAsync(IReadOnlyList<ChunkReference> chunks, CancellationToken ct = default)
        {
            var result = new MemoryStream();

            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();

                var chunkStream = await GetChunkAsync(chunk.Hash, ct);
                if (chunkStream == null)
                {
                    throw new InvalidOperationException($"Chunk {HashToHex(chunk.Hash)} not found");
                }

                await chunkStream.CopyToAsync(result, ct);
                chunkStream.Dispose();
            }

            result.Position = 0;
            return result;
        }

        /// <summary>
        /// Stores a chunk if it doesn't already exist.
        /// </summary>
        public override async Task<bool> StoreChunkAsync(byte[] hash, Stream data, CancellationToken ct = default)
        {
            string hashHex = HashToHex(hash);

            await _storageLock.WaitAsync(ct);
            try
            {
                if (_chunkIndex.Contains(hashHex))
                {
                    // Already exists - just increment reference
                    _chunkIndex.TryGetAndReference(hashHex, out _);
                    return false;
                }

                // Read and store the chunk
                using var ms = new MemoryStream();
                await data.CopyToAsync(ms, ct);
                var chunkData = ms.ToArray();

                var metadata = new ChunkMetadata
                {
                    Hash = hash,
                    OriginalSize = chunkData.Length,
                    StoredSize = chunkData.Length,
                    CreatedAt = DateTime.UtcNow,
                    LastAccessedAt = DateTime.UtcNow
                };

                _chunkIndex.AddOrReference(hashHex, metadata, out bool isNew);
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

        /// <summary>
        /// Retrieves a chunk by its hash.
        /// </summary>
        public override Task<Stream?> GetChunkAsync(byte[] hash, CancellationToken ct = default)
        {
            string hashHex = HashToHex(hash);

            if (!_chunkStorage.TryGetValue(hashHex, out var storedData))
            {
                return Task.FromResult<Stream?>(null);
            }

            var metadata = _chunkIndex.Get(hashHex);
            if (metadata == null)
            {
                return Task.FromResult<Stream?>(null);
            }

            byte[] chunkData;

            // Handle delta-encoded chunks
            if (metadata.IsDeltaEncoded && metadata.BaseChunkHash != null)
            {
                string baseHashHex = HashToHex(metadata.BaseChunkHash);
                if (_chunkStorage.TryGetValue(baseHashHex, out var baseData))
                {
                    chunkData = DeltaEncoder.ApplyDelta(storedData, baseData);
                }
                else
                {
                    // Base chunk missing - data corruption
                    return Task.FromResult<Stream?>(null);
                }
            }
            else
            {
                chunkData = storedData;
            }

            // Verify integrity if enabled
            if (_config.VerifyOnRetrieve)
            {
                byte[] computedHash = ComputeHash(chunkData);
                if (!hash.AsSpan().SequenceEqual(computedHash))
                {
                    // Hash mismatch - data corruption
                    return Task.FromResult<Stream?>(null);
                }
            }

            metadata.LastAccessedAt = DateTime.UtcNow;
            return Task.FromResult<Stream?>(new MemoryStream(chunkData));
        }

        /// <summary>
        /// Checks if a chunk exists.
        /// </summary>
        public override Task<bool> ChunkExistsAsync(byte[] hash, CancellationToken ct = default)
        {
            string hashHex = HashToHex(hash);
            return Task.FromResult(_chunkIndex.Contains(hashHex));
        }

        /// <summary>
        /// Gets deduplication statistics.
        /// </summary>
        public override Task<DeduplicationStatistics> GetStatisticsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new DeduplicationStatistics
            {
                TotalChunks = _chunkIndex.TotalChunks,
                UniqueChunks = _chunkIndex.TotalChunks,
                TotalLogicalBytes = _chunkIndex.TotalLogicalBytes,
                TotalPhysicalBytes = _chunkIndex.TotalPhysicalBytes
            });
        }

        /// <summary>
        /// Performs garbage collection on orphaned chunks.
        /// </summary>
        public override async Task<GarbageCollectionResult> CollectGarbageAsync(CancellationToken ct = default)
        {
            var startTime = DateTime.UtcNow;
            int scanned = 0;
            int deleted = 0;
            long bytesReclaimed = 0;

            var unreferenced = _chunkIndex.GetUnreferencedChunks();
            scanned = unreferenced.Count;

            foreach (var (hashHex, metadata) in unreferenced)
            {
                ct.ThrowIfCancellationRequested();

                if (_chunkStorage.TryRemove(hashHex, out var data))
                {
                    bytesReclaimed += data.Length;
                    deleted++;
                }

                // Remove fingerprint mapping
                ulong fingerprint = RabinFingerprint.ComputeFingerprint(data ?? Array.Empty<byte>());
                _fingerprintToHash.TryRemove(fingerprint, out _);

                await Task.Yield(); // Allow other operations
            }

            return new GarbageCollectionResult
            {
                ChunksScanned = scanned,
                ChunksDeleted = deleted,
                BytesReclaimed = bytesReclaimed,
                Duration = DateTime.UtcNow - startTime
            };
        }

        #endregion

        #region Additional Public Methods

        /// <summary>
        /// Splits data into dedup chunks and returns chunk information.
        /// </summary>
        public async Task<List<ChunkReference>> ChunkData(Stream data, CancellationToken ct = default)
        {
            var result = await ChunkDataAsync(data, ct);
            return result.Chunks.ToList();
        }

        /// <summary>
        /// Stores a chunk with dedup lookup.
        /// </summary>
        public async Task<(bool Stored, bool IsNew)> StoreChunk(byte[] data, CancellationToken ct = default)
        {
            byte[] hash = ComputeHash(data);
            using var stream = new MemoryStream(data);
            bool isNew = await StoreChunkAsync(hash, stream, ct);
            return (true, isNew);
        }

        /// <summary>
        /// Retrieves a chunk by hash string.
        /// </summary>
        public async Task<byte[]?> RetrieveChunk(string hashHex, CancellationToken ct = default)
        {
            byte[] hash = Convert.FromHexString(hashHex);
            var stream = await GetChunkAsync(hash, ct);
            if (stream == null) return null;

            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }

        /// <summary>
        /// Calculates space savings from deduplication.
        /// </summary>
        public double GetDeduplicationRatio()
        {
            long logical = _chunkIndex.TotalLogicalBytes;
            long physical = _chunkIndex.TotalPhysicalBytes;
            return logical > 0 ? (double)physical / logical : 1.0;
        }

        /// <summary>
        /// Calculates space savings percentage.
        /// </summary>
        public double GetSpaceSavingsPercent()
        {
            return (1.0 - GetDeduplicationRatio()) * 100.0;
        }

        /// <summary>
        /// Removes chunks with zero references.
        /// </summary>
        public async Task<int> PruneUnreferencedChunks(CancellationToken ct = default)
        {
            var result = await CollectGarbageAsync(ct);
            return result.ChunksDeleted;
        }

        /// <summary>
        /// Verifies integrity of all stored chunks.
        /// </summary>
        public async Task<IntegrityVerificationResult> VerifyIntegrity(CancellationToken ct = default)
        {
            var startTime = DateTime.UtcNow;
            int total = 0;
            int verified = 0;
            int corrupted = 0;
            var corruptedHashes = new List<string>();

            foreach (var kvp in _chunkIndex.EnumerateAll())
            {
                ct.ThrowIfCancellationRequested();
                total++;

                string hashHex = kvp.Key;
                var metadata = kvp.Value;

                if (!_chunkStorage.TryGetValue(hashHex, out var storedData))
                {
                    corrupted++;
                    corruptedHashes.Add(hashHex);
                    continue;
                }

                byte[] chunkData;
                if (metadata.IsDeltaEncoded && metadata.BaseChunkHash != null)
                {
                    string baseHashHex = HashToHex(metadata.BaseChunkHash);
                    if (_chunkStorage.TryGetValue(baseHashHex, out var baseData))
                    {
                        chunkData = DeltaEncoder.ApplyDelta(storedData, baseData);
                    }
                    else
                    {
                        corrupted++;
                        corruptedHashes.Add(hashHex);
                        continue;
                    }
                }
                else
                {
                    chunkData = storedData;
                }

                byte[] computedHash = ComputeHash(chunkData);
                if (computedHash.AsSpan().SequenceEqual(metadata.Hash))
                {
                    verified++;
                }
                else
                {
                    corrupted++;
                    corruptedHashes.Add(hashHex);
                }

                await Task.Yield();
            }

            return new IntegrityVerificationResult
            {
                TotalChunks = total,
                VerifiedChunks = verified,
                CorruptedChunks = corrupted,
                CorruptedChunkHashes = corruptedHashes,
                Duration = DateTime.UtcNow - startTime
            };
        }

        #endregion

        #region Private Helper Methods

        /// <summary>
        /// Gets chunk boundaries based on the configured strategy.
        /// </summary>
        private List<(int Offset, int Length)> GetChunkBoundaries(byte[] data)
        {
            return _config.Strategy switch
            {
                ChunkingStrategy.Fixed => GetFixedChunkBoundaries(data),
                ChunkingStrategy.Rabin => GetRabinChunkBoundaries(data),
                ChunkingStrategy.FastCDC => _fastCdc.ChunkData(data),
                _ => _fastCdc.ChunkData(data)
            };
        }

        /// <summary>
        /// Fixed-size chunking.
        /// </summary>
        private List<(int Offset, int Length)> GetFixedChunkBoundaries(byte[] data)
        {
            var chunks = new List<(int, int)>();
            int offset = 0;
            int chunkSize = _config.AverageChunkSize;

            while (offset < data.Length)
            {
                int length = Math.Min(chunkSize, data.Length - offset);
                chunks.Add((offset, length));
                offset += length;
            }

            return chunks;
        }

        /// <summary>
        /// Rabin fingerprint-based content-defined chunking.
        /// </summary>
        private List<(int Offset, int Length)> GetRabinChunkBoundaries(byte[] data)
        {
            var chunks = new List<(int, int)>();
            _rabin.Reset();

            int chunkStart = 0;
            int minSize = _config.MinChunkSize;
            int maxSize = _config.MaxChunkSize;

            // Mask for determining chunk boundaries
            // Probability of boundary = 1 / AverageChunkSize
            int bits = (int)Math.Log2(_config.AverageChunkSize);
            ulong mask = (1UL << bits) - 1;

            for (int i = 0; i < data.Length; i++)
            {
                ulong fp = _rabin.Slide(data[i]);
                int currentLength = i - chunkStart + 1;

                // Check for chunk boundary after minimum size
                bool isBoundary = currentLength >= minSize &&
                    ((fp & mask) == 0 || currentLength >= maxSize);

                if (isBoundary || i == data.Length - 1)
                {
                    chunks.Add((chunkStart, currentLength));
                    chunkStart = i + 1;
                    _rabin.Reset();
                }
            }

            return chunks;
        }

        /// <summary>
        /// Attempts to delta-encode a chunk against similar existing chunks.
        /// </summary>
        private (byte[]? DeltaData, byte[]? BaseHash) TryDeltaEncode(ReadOnlySpan<byte> chunkData)
        {
            // Find similar chunk by fingerprint
            ulong fingerprint = RabinFingerprint.ComputeFingerprint(chunkData);

            // Search for similar fingerprints (simple linear search for now)
            foreach (var kvp in _fingerprintToHash)
            {
                // Quick fingerprint similarity check
                int bitDiff = System.Numerics.BitOperations.PopCount(fingerprint ^ kvp.Key);
                if (bitDiff > 32) continue; // Too different

                if (_chunkStorage.TryGetValue(kvp.Value, out var baseData))
                {
                    double similarity = DeltaEncoder.CalculateSimilarity(chunkData, baseData);
                    if (similarity >= _config.DeltaSimilarityThreshold)
                    {
                        var delta = DeltaEncoder.ComputeDelta(chunkData, baseData);
                        if (delta != null)
                        {
                            byte[] baseHash = Convert.FromHexString(kvp.Value);
                            return (delta, baseHash);
                        }
                    }
                }
            }

            return (null, null);
        }

        #endregion

        #region Plugin Lifecycle

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "dedup.chunk", DisplayName = "Chunk Data", Description = "Split data into dedup chunks" },
                new() { Name = "dedup.store", DisplayName = "Store Chunk", Description = "Store chunk with dedup lookup" },
                new() { Name = "dedup.retrieve", DisplayName = "Retrieve Chunk", Description = "Retrieve chunk by hash" },
                new() { Name = "dedup.stats", DisplayName = "Statistics", Description = "Get deduplication statistics" },
                new() { Name = "dedup.gc", DisplayName = "Garbage Collect", Description = "Prune unreferenced chunks" },
                new() { Name = "dedup.verify", DisplayName = "Verify Integrity", Description = "Verify all chunk hashes" }
            };
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Strategy"] = _config.Strategy.ToString();
            metadata["WindowSize"] = _config.WindowSize;
            metadata["EnableDeltaEncoding"] = _config.EnableDeltaEncoding;
            metadata["DeltaSimilarityThreshold"] = _config.DeltaSimilarityThreshold;
            metadata["IndexShardCount"] = _config.IndexShardCount;
            metadata["VerifyOnRetrieve"] = _config.VerifyOnRetrieve;
            metadata["TotalChunks"] = _chunkIndex.TotalChunks;
            metadata["DeduplicationRatio"] = GetDeduplicationRatio();
            metadata["SpaceSavingsPercent"] = GetSpaceSavingsPercent();
            return metadata;
        }

        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "dedup.stats":
                    await GetStatisticsAsync();
                    break;
                case "dedup.gc":
                    await CollectGarbageAsync();
                    break;
                case "dedup.verify":
                    await VerifyIntegrity();
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _chunkIndex.Dispose();
                _storageLock.Dispose();
                _disposed = true;
            }
        }

        #endregion
    }

    #endregion

    #region Specialized Chunking Strategy Plugins

    /// <summary>
    /// Fixed-size chunking plugin for predictable chunk sizes.
    /// Best for archival and streaming scenarios.
    /// </summary>
    public sealed class FixedChunkingPlugin : DeduplicationPlugin
    {
        public override string Id => "datawarehouse.plugins.deduplication.fixed";
        public override string Name => "Fixed-Size Deduplication";

        public FixedChunkingPlugin() : base(new DeduplicationConfig
        {
            Strategy = ChunkingStrategy.Fixed,
            AverageChunkSize = 8192,
            MinChunkSize = 8192,
            MaxChunkSize = 8192
        })
        {
        }

        public FixedChunkingPlugin(int chunkSize) : base(new DeduplicationConfig
        {
            Strategy = ChunkingStrategy.Fixed,
            AverageChunkSize = chunkSize,
            MinChunkSize = chunkSize,
            MaxChunkSize = chunkSize
        })
        {
        }
    }

    /// <summary>
    /// Rabin fingerprint CDC plugin for classic content-defined chunking.
    /// Best compatibility with existing dedup systems.
    /// </summary>
    public sealed class RabinCDCPlugin : DeduplicationPlugin
    {
        public override string Id => "datawarehouse.plugins.deduplication.rabin";
        public override string Name => "Rabin CDC Deduplication";

        public RabinCDCPlugin() : base(new DeduplicationConfig
        {
            Strategy = ChunkingStrategy.Rabin,
            AverageChunkSize = 8192,
            MinChunkSize = 2048,
            MaxChunkSize = 65536,
            WindowSize = 48
        })
        {
        }

        public RabinCDCPlugin(int avgSize, int minSize, int maxSize, int windowSize = 48)
            : base(new DeduplicationConfig
            {
                Strategy = ChunkingStrategy.Rabin,
                AverageChunkSize = avgSize,
                MinChunkSize = minSize,
                MaxChunkSize = maxSize,
                WindowSize = windowSize
            })
        {
        }
    }

    /// <summary>
    /// FastCDC plugin for high-performance content-defined chunking.
    /// Best throughput for TB-scale deduplication.
    /// </summary>
    public sealed class FastCDCPlugin : DeduplicationPlugin
    {
        public override string Id => "datawarehouse.plugins.deduplication.fastcdc";
        public override string Name => "FastCDC Deduplication";

        public FastCDCPlugin() : base(new DeduplicationConfig
        {
            Strategy = ChunkingStrategy.FastCDC,
            AverageChunkSize = 8192,
            MinChunkSize = 2048,
            MaxChunkSize = 65536,
            EnableDeltaEncoding = true
        })
        {
        }

        public FastCDCPlugin(int avgSize, int minSize, int maxSize, bool enableDelta = true)
            : base(new DeduplicationConfig
            {
                Strategy = ChunkingStrategy.FastCDC,
                AverageChunkSize = avgSize,
                MinChunkSize = minSize,
                MaxChunkSize = maxSize,
                EnableDeltaEncoding = enableDelta
            })
        {
        }
    }

    #endregion

    #region Deduplication Registry

    /// <summary>
    /// Registry for deduplication providers.
    /// Enables Kernel discovery and selection of chunking strategies.
    /// </summary>
    public sealed class DeduplicationRegistry
    {
        private static readonly Lazy<DeduplicationRegistry> _instance = new(() => new DeduplicationRegistry());
        private readonly Dictionary<ChunkingStrategy, Func<DeduplicationConfig?, DeduplicationPlugin>> _factories = new();
        private readonly Dictionary<ChunkingStrategy, DeduplicationPlugin> _defaultProviders = new();

        public static DeduplicationRegistry Instance => _instance.Value;

        private DeduplicationRegistry()
        {
            RegisterProvider(ChunkingStrategy.Fixed, cfg => cfg != null ? new DeduplicationPlugin(cfg) : new FixedChunkingPlugin());
            RegisterProvider(ChunkingStrategy.Rabin, cfg => cfg != null ? new DeduplicationPlugin(cfg) : new RabinCDCPlugin());
            RegisterProvider(ChunkingStrategy.FastCDC, cfg => cfg != null ? new DeduplicationPlugin(cfg) : new FastCDCPlugin());
        }

        public void RegisterProvider(ChunkingStrategy strategy, Func<DeduplicationConfig?, DeduplicationPlugin> factory)
        {
            _factories[strategy] = factory;
            _defaultProviders[strategy] = factory(null);
        }

        public DeduplicationPlugin GetProvider(ChunkingStrategy strategy, DeduplicationConfig? config = null)
        {
            if (config == null && _defaultProviders.TryGetValue(strategy, out var defaultProvider))
            {
                return defaultProvider;
            }

            if (_factories.TryGetValue(strategy, out var factory))
            {
                return factory(config);
            }

            throw new NotSupportedException($"Chunking strategy '{strategy}' is not registered.");
        }

        public DeduplicationPlugin GetProviderByName(string strategyName, DeduplicationConfig? config = null)
        {
            if (Enum.TryParse<ChunkingStrategy>(strategyName, true, out var strategy))
            {
                return GetProvider(strategy, config);
            }

            throw new NotSupportedException($"Unknown chunking strategy: '{strategyName}'");
        }

        public IReadOnlyList<ChunkingStrategy> GetAvailableStrategies()
        {
            return _factories.Keys.ToList().AsReadOnly();
        }

        public bool IsStrategyAvailable(ChunkingStrategy strategy)
        {
            return _factories.ContainsKey(strategy);
        }
    }

    #endregion
}
