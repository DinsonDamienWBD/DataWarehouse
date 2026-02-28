using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy.Performance
{
    /// <summary>
    /// Per-VDE bloom filter providing O(1) "has override?" checks with zero false negatives.
    /// <para>
    /// Most VDE paths have no policy overrides. This bloom filter confirms "definitely no override"
    /// in O(1) without touching the policy store. False positives (~1%) fall through to the real
    /// store -- safe but slightly slower. Zero false negatives are guaranteed because bits are
    /// never removed (only Add and Clear).
    /// </para>
    /// <para>
    /// Uses XxHash64 double-hashing: h1 (seed 0) + i * h2 (seed golden ratio) produces
    /// _hashCount independent bit positions from just 2 hash computations.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PERF-02)")]
    public sealed class BloomFilterSkipIndex
    {
        /// <summary>Golden ratio constant used as seed for the second hash function.</summary>
        private const ulong GoldenRatioSeed = 0x9E3779B97F4A7C15;

        private readonly long[] _bits;
        private readonly int _bitCount;
        private readonly int _hashCount;
        private volatile int _itemCount;

        /// <summary>
        /// Initializes a new bloom filter sized for the expected number of override paths.
        /// </summary>
        /// <param name="expectedItems">Expected number of distinct (level, path) override entries.</param>
        /// <param name="falsePositiveRate">Target false positive rate (default 1%).</param>
        public BloomFilterSkipIndex(int expectedItems = 10_000, double falsePositiveRate = 0.01)
        {
            if (expectedItems <= 0)
                throw new ArgumentOutOfRangeException(nameof(expectedItems), "Expected items must be positive.");
            if (falsePositiveRate is <= 0.0 or >= 1.0)
                throw new ArgumentOutOfRangeException(nameof(falsePositiveRate), "False positive rate must be between 0 and 1 exclusive.");

            // Optimal bit array size: m = -n * ln(p) / (ln(2))^2
            int rawBitCount = (int)Math.Ceiling(-expectedItems * Math.Log(falsePositiveRate) / (Math.Log(2) * Math.Log(2)));

            // Round up to next multiple of 64 for cache-line alignment
            _bitCount = ((rawBitCount + 63) / 64) * 64;

            // Optimal hash count: k = (m/n) * ln(2)
            _hashCount = Math.Max(1, (int)Math.Ceiling((_bitCount / (double)expectedItems) * Math.Log(2)));

            _bits = new long[_bitCount / 64];
        }

        /// <summary>Total number of bits in the filter (always a multiple of 64).</summary>
        public int BitCount => _bitCount;

        /// <summary>Number of hash functions used per item.</summary>
        public int HashCount => _hashCount;

        /// <summary>Number of items that have been added to the filter.</summary>
        public int ItemCount => _itemCount;

        /// <summary>
        /// Adds a policy override entry to the bloom filter.
        /// Thread-safe via Interlocked operations on the bit array.
        /// </summary>
        /// <param name="level">The policy hierarchy level.</param>
        /// <param name="path">The VDE path at the specified level.</param>
        public void Add(PolicyLevel level, string path)
        {
            ComputeHashPairFromLevelPath(level, path, out ulong h1, out ulong h2);

            for (int i = 0; i < _hashCount; i++)
            {
                ulong pos = (h1 + (ulong)i * h2) % (ulong)_bitCount;
                int arrayIndex = (int)(pos / 64);
                long bitMask = 1L << (int)(pos % 64);
                Interlocked.Or(ref _bits[arrayIndex], bitMask);
            }

            Interlocked.Increment(ref _itemCount);
        }

        /// <summary>
        /// Checks whether a policy override might exist at the given level and path.
        /// <para>
        /// Returns false if definitely no override exists (zero false negatives).
        /// Returns true if an override might exist (possible false positive at ~1% rate).
        /// </para>
        /// </summary>
        /// <param name="level">The policy hierarchy level to check.</param>
        /// <param name="path">The VDE path to check.</param>
        /// <returns>False if definitely no override; true if an override might exist.</returns>
        public bool MayContain(PolicyLevel level, string path)
        {
            ComputeHashPairFromLevelPath(level, path, out ulong h1, out ulong h2);

            for (int i = 0; i < _hashCount; i++)
            {
                ulong pos = (h1 + (ulong)i * h2) % (ulong)_bitCount;
                int arrayIndex = (int)(pos / 64);
                long bitMask = 1L << (int)(pos % 64);

                // Volatile read for visibility of concurrent Add operations
                long currentBits = Volatile.Read(ref _bits[arrayIndex]);
                if ((currentBits & bitMask) == 0)
                    return false; // Definitely not present -- zero false negatives
            }

            return true; // All bits set -- might be present (possible false positive)
        }

        /// <summary>
        /// Clears all bits and resets the item count. Used before rebuilding the filter
        /// from the current policy store state.
        /// </summary>
        public void Clear()
        {
            Array.Clear(_bits, 0, _bits.Length);
            Interlocked.Exchange(ref _itemCount, 0);
        }

        /// <summary>
        /// Builds a fully populated bloom filter from the current policy store state.
        /// Queries the store for all overrides across the given feature IDs and adds
        /// every (level, path) pair to the filter.
        /// </summary>
        /// <param name="store">The policy store to query for existing overrides.</param>
        /// <param name="featureIds">Feature IDs to scan for overrides.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A new bloom filter populated with all current override entries.</returns>
        public static async Task<BloomFilterSkipIndex> BuildFromStoreAsync(
            IPolicyStore store,
            IReadOnlyList<string> featureIds,
            CancellationToken ct = default)
        {
            if (store is null)
                throw new ArgumentNullException(nameof(store));
            if (featureIds is null)
                throw new ArgumentNullException(nameof(featureIds));

            // First pass: count total overrides to size the filter optimally
            int totalOverrides = 0;
            var allOverrides = new List<(PolicyLevel Level, string Path)>();

            foreach (string featureId in featureIds)
            {
                ct.ThrowIfCancellationRequested();
                var overrides = await store.ListOverridesAsync(featureId, ct).ConfigureAwait(false);
                foreach (var (level, path, _) in overrides)
                {
                    allOverrides.Add((level, path));
                    totalOverrides++;
                }
            }

            // Size filter for at least 100 items to avoid degenerate sizing
            var filter = new BloomFilterSkipIndex(expectedItems: Math.Max(totalOverrides, 100));

            foreach (var (level, path) in allOverrides)
            {
                filter.Add(level, path);
            }

            return filter;
        }

        /// <summary>
        /// Computes the two XxHash64 values used for double-hashing.
        /// h1 uses seed 0, h2 uses the golden ratio constant.
        /// </summary>
        private static void ComputeHashPair(byte[] keyBytes, out ulong h1, out ulong h2)
        {
            h1 = XxHash64.HashToUInt64(keyBytes, seed: 0);
            h2 = XxHash64.HashToUInt64(keyBytes, seed: unchecked((long)GoldenRatioSeed));
        }

        /// <summary>
        /// Allocation-free variant that hashes (level, path) directly using a pooled byte buffer,
        /// avoiding the per-call string interpolation and byte[] allocation (finding P2-498).
        /// </summary>
        private static void ComputeHashPairFromLevelPath(PolicyLevel level, string path,
            out ulong h1, out ulong h2)
        {
            // Key layout: 1-byte level prefix + ':' + UTF-8 path bytes
            int pathByteCount = Encoding.UTF8.GetByteCount(path);
            int totalBytes = 2 + pathByteCount; // level digit + ':' + path bytes

            // Use stackalloc for small keys (≤256 bytes); rent from pool for larger paths.
            const int StackThreshold = 256;
            if (totalBytes <= StackThreshold)
            {
                Span<byte> buf = stackalloc byte[totalBytes];
                buf[0] = (byte)('0' + (int)level); // level is a small int (0-5)
                buf[1] = (byte)':';
                Encoding.UTF8.GetBytes(path, buf[2..]);
                h1 = XxHash64.HashToUInt64(buf, seed: 0);
                h2 = XxHash64.HashToUInt64(buf, seed: unchecked((long)GoldenRatioSeed));
            }
            else
            {
                var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(totalBytes);
                try
                {
                    rented[0] = (byte)('0' + (int)level);
                    rented[1] = (byte)':';
                    Encoding.UTF8.GetBytes(path, 0, path.Length, rented, 2);
                    var span = rented.AsSpan(0, totalBytes);
                    h1 = XxHash64.HashToUInt64(span, seed: 0);
                    h2 = XxHash64.HashToUInt64(span, seed: unchecked((long)GoldenRatioSeed));
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }
    }
}
