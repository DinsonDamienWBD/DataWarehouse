using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// SIMD-accelerated core operations for the adaptive index engine.
/// Provides bloom filter probe, ART Node16 search, bitmap scanning, and XxHash64
/// with runtime hardware detection and scalar fallback paths.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-15 SIMD operations")]
public static class SimdOperations
{
    // ── Runtime capability detection ──────────────────────────────────────

    /// <summary>Whether AVX2 instruction set is supported on this processor.</summary>
    public static readonly bool HasAvx2 = Avx2.IsSupported;

    /// <summary>Whether SSE2 instruction set is supported on this processor.</summary>
    public static readonly bool HasSse2 = Sse2.IsSupported;

    /// <summary>Whether AVX-512 BW instruction set is supported on this processor (future use).</summary>
    public static readonly bool HasAvx512 = Avx512BW.IsSupported;

    // ── XXH64 constants ──────────────────────────────────────────────────

    private const ulong XXH_PRIME64_1 = 0x9E3779B185EBCA87UL;
    private const ulong XXH_PRIME64_2 = 0x14DEF9DEA2F79CD5UL;
    private const ulong XXH_PRIME64_3 = 0x0165667B19E3779FUL;
    private const ulong XXH_PRIME64_4 = 0xC2B2AE3D27D4EB4FUL;
    private const ulong XXH_PRIME64_5 = 0x27D4EB2F165667C5UL;

    /// <summary>
    /// Returns a human-readable string describing the SIMD capabilities detected at runtime.
    /// </summary>
    /// <returns>"AVX2+SSE2", "SSE2", or "Scalar" depending on hardware support.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetCapabilityString()
    {
        if (HasAvx2 && HasSse2) return "AVX2+SSE2";
        if (HasSse2) return "SSE2";
        return "Scalar";
    }

    // ── 1. Bloom filter SIMD probe ───────────────────────────────────────

    /// <summary>
    /// Probes a bloom filter for possible membership using SIMD-accelerated hash checking.
    /// Computes <paramref name="hashCount"/> hash values via XxHash64 with different seeds,
    /// then checks all corresponding bit positions. When AVX2 is available, processes
    /// 4 hash functions in a single Vector256 operation.
    /// </summary>
    /// <param name="filter">The bloom filter bit array.</param>
    /// <param name="key">The key to probe.</param>
    /// <param name="hashCount">Number of hash functions to evaluate.</param>
    /// <returns>True if all probed bits are set (may contain); false if any bit is zero (definitely absent).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool BloomProbe(byte[] filter, byte[] key, int hashCount)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(key);
        if (filter.Length == 0 || hashCount <= 0) return false;

        long totalBits = (long)filter.Length * 8;
        ReadOnlySpan<byte> keySpan = key.AsSpan();

        if (HasAvx2 && hashCount >= 4)
        {
            return BloomProbeAvx2(filter, keySpan, hashCount, totalBits);
        }

        return BloomProbeScalar(filter, keySpan, hashCount, totalBits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool BloomProbeAvx2(byte[] filter, ReadOnlySpan<byte> key, int hashCount, long totalBits)
    {
        int i = 0;

        // Process 4 hashes at a time using Vector256<int>
        for (; i + 4 <= hashCount; i += 4)
        {
            // Compute 4 hash values with different seeds
            ulong h0 = XxHash64Simd(key, (ulong)i);
            ulong h1 = XxHash64Simd(key, (ulong)(i + 1));
            ulong h2 = XxHash64Simd(key, (ulong)(i + 2));
            ulong h3 = XxHash64Simd(key, (ulong)(i + 3));

            // Derive bit positions
            int pos0 = (int)(h0 % (ulong)totalBits);
            int pos1 = (int)(h1 % (ulong)totalBits);
            int pos2 = (int)(h2 % (ulong)totalBits);
            int pos3 = (int)(h3 % (ulong)totalBits);

            // Pack positions into Vector256<int>
            Vector256<int> positions = Vector256.Create(pos0, pos1, pos2, pos3, 0, 0, 0, 0);

            // Extract byte indices and bit offsets
            Vector256<int> byteIndices = Avx2.ShiftRightLogical(positions, 3); // pos / 8
            Vector256<int> bitOffsets = Avx2.And(positions, Vector256.Create(7, 7, 7, 7, 7, 7, 7, 7)); // pos % 8

            // Load filter bytes at those positions
            int bi0 = byteIndices.GetElement(0);
            int bi1 = byteIndices.GetElement(1);
            int bi2 = byteIndices.GetElement(2);
            int bi3 = byteIndices.GetElement(3);

            Vector256<int> filterBytes = Vector256.Create(
                filter[bi0], filter[bi1], filter[bi2], filter[bi3], 0, 0, 0, 0);

            // Create bit masks (1 << bitOffset)
            Vector256<int> ones = Vector256.Create(1, 1, 1, 1, 1, 1, 1, 1);
            Vector256<int> masks = Avx2.ShiftLeftLogicalVariable(
                ones.AsUInt32(), bitOffsets.AsUInt32()).AsInt32();

            // AND filter bytes with masks
            Vector256<int> result = Avx2.And(filterBytes, masks);

            // Check if all 4 results are non-zero (bits are set)
            // Compare with zero — any zero means bit not set
            Vector256<int> zero = Vector256<int>.Zero;
            Vector256<int> cmp = Avx2.CompareEqual(result, zero);
            int cmpMask = Avx2.MoveMask(cmp.AsByte());

            // Lower 16 bytes (4 ints) — if any int is zero, corresponding 4 bytes will be 0xFF
            if ((cmpMask & 0xFFFF) != 0) return false;
        }

        // Handle remaining hashes with scalar path
        for (; i < hashCount; i++)
        {
            ulong h = XxHash64Simd(key, (ulong)i);
            int pos = (int)(h % (ulong)totalBits);
            int byteIdx = pos / 8;
            int bitOff = pos % 8;
            if ((filter[byteIdx] & (1 << bitOff)) == 0) return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool BloomProbeScalar(byte[] filter, ReadOnlySpan<byte> key, int hashCount, long totalBits)
    {
        for (int i = 0; i < hashCount; i++)
        {
            ulong h = XxHash64Simd(key, (ulong)i);
            int pos = (int)(h % (ulong)totalBits);
            int byteIdx = pos / 8;
            int bitOff = pos % 8;
            if ((filter[byteIdx] & (1 << bitOff)) == 0) return false;
        }

        return true;
    }

    // ── 2. ART Node16 SIMD search ────────────────────────────────────────

    /// <summary>
    /// Searches an ART Node16's key array for a specific byte key using SIMD.
    /// When SSE2 is available, loads all 16 keys into a Vector128 and performs
    /// a single compare-equal operation to find the match.
    /// </summary>
    /// <param name="nodeKeys">Array of up to 16 key bytes in the ART node. Must have at least 16 elements.</param>
    /// <param name="searchKey">The key byte to search for.</param>
    /// <returns>Index of the matching key (0-15), or -1 if not found.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ArtNode16Search(byte[] nodeKeys, byte searchKey)
    {
        ArgumentNullException.ThrowIfNull(nodeKeys);

        if (HasSse2 && nodeKeys.Length >= 16)
        {
            return ArtNode16SearchSse2(nodeKeys, searchKey);
        }

        return ArtNode16SearchScalar(nodeKeys, searchKey);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ArtNode16SearchSse2(byte[] nodeKeys, byte searchKey)
    {
        // Load 16 node keys into Vector128<byte>
        Vector128<byte> keys = Vector128.Create(
            nodeKeys[0], nodeKeys[1], nodeKeys[2], nodeKeys[3],
            nodeKeys[4], nodeKeys[5], nodeKeys[6], nodeKeys[7],
            nodeKeys[8], nodeKeys[9], nodeKeys[10], nodeKeys[11],
            nodeKeys[12], nodeKeys[13], nodeKeys[14], nodeKeys[15]);

        // Broadcast search key to all positions
        Vector128<byte> searchVec = Vector128.Create(searchKey);

        // Compare equal — each byte lane is 0xFF on match, 0x00 otherwise
        Vector128<byte> cmp = Sse2.CompareEqual(keys, searchVec);

        // Extract bitmask (one bit per byte)
        int mask = Sse2.MoveMask(cmp);

        if (mask == 0) return -1;

        // Find first set bit = index of matching key
        return BitOperations.TrailingZeroCount(mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ArtNode16SearchScalar(byte[] nodeKeys, byte searchKey)
    {
        int count = Math.Min(nodeKeys.Length, 16);
        for (int i = 0; i < count; i++)
        {
            if (nodeKeys[i] == searchKey) return i;
        }
        return -1;
    }

    // ── 3. Bitmap SIMD scanning ──────────────────────────────────────────

    /// <summary>
    /// Finds the first zero bit in a bitmap starting from the given position,
    /// processing 256 bits (32 bytes) at a time using AVX2 instructions.
    /// Provides approximately 4x speedup over ulong-at-a-time scanning.
    /// </summary>
    /// <param name="bitmap">The bitmap byte array.</param>
    /// <param name="startBit">The bit index to start scanning from.</param>
    /// <param name="totalBits">Total number of valid bits in the bitmap.</param>
    /// <returns>Index of the first zero bit found, or -1 if all bits are set.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long FindFirstZeroBit256(byte[] bitmap, long startBit, long totalBits)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (startBit >= totalBits) return -1;

        long totalBytes = (totalBits + 7) / 8;
        long byteIndex = startBit / 8;
        int bitOffset = (int)(startBit % 8);

        // Handle partial first byte
        if (bitOffset != 0 && byteIndex < totalBytes)
        {
            byte b = bitmap[byteIndex];
            byte masked = (byte)(b | ((1 << bitOffset) - 1));
            if (masked != 0xFF)
            {
                int zeroBit = BitOperations.TrailingZeroCount((byte)~masked);
                long result = byteIndex * 8 + zeroBit;
                if (result < totalBits) return result;
            }
            byteIndex++;
        }

        if (HasAvx2)
        {
            return FindFirstZeroBit256Avx2(bitmap, byteIndex, totalBits, totalBytes);
        }

        return FindFirstZeroBit256Scalar(bitmap, byteIndex, totalBits, totalBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long FindFirstZeroBit256Avx2(byte[] bitmap, long byteIndex, long totalBits, long totalBytes)
    {
        Vector256<byte> allOnes = Vector256.Create(byte.MaxValue);

        // Align to 32-byte boundary
        while (byteIndex % 32 != 0 && byteIndex < totalBytes)
        {
            if (bitmap[byteIndex] != 0xFF)
            {
                int zeroBit = BitOperations.TrailingZeroCount((byte)~bitmap[byteIndex]);
                long result = byteIndex * 8 + zeroBit;
                if (result < totalBits) return result;
            }
            byteIndex++;
        }

        // Process 32 bytes (256 bits) at a time
        long chunkEnd = totalBytes - (totalBytes % 32);
        ReadOnlySpan<byte> span = bitmap.AsSpan();

        for (long i = byteIndex; i < chunkEnd; i += 32)
        {
            Vector256<byte> chunk = Vector256.Create(span.Slice((int)i, 32));
            Vector256<byte> cmp = Avx2.CompareEqual(chunk, allOnes);
            int mask = Avx2.MoveMask(cmp);

            // If all bytes are 0xFF, mask will be all 1s (0xFFFFFFFF)
            if (mask == unchecked((int)0xFFFFFFFF)) continue;

            // Find the first non-0xFF byte
            int invertedMask = ~mask;
            int bytePos = BitOperations.TrailingZeroCount(invertedMask);
            byte b = bitmap[i + bytePos];
            int zeroBit = BitOperations.TrailingZeroCount((byte)~b);
            long result = (i + bytePos) * 8 + zeroBit;
            if (result < totalBits) return result;
        }

        // Handle remaining bytes
        for (long i = Math.Max(chunkEnd, byteIndex); i < totalBytes; i++)
        {
            if (bitmap[i] != 0xFF)
            {
                int zeroBit = BitOperations.TrailingZeroCount((byte)~bitmap[i]);
                long result = i * 8 + zeroBit;
                if (result < totalBits) return result;
            }
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long FindFirstZeroBit256Scalar(byte[] bitmap, long byteIndex, long totalBits, long totalBytes)
    {
        // Scan ulong at a time (64 bits)
        ReadOnlySpan<byte> span = bitmap.AsSpan();

        // Align to 8-byte boundary
        while (byteIndex % 8 != 0 && byteIndex < totalBytes)
        {
            if (bitmap[byteIndex] != 0xFF)
            {
                int zeroBit = BitOperations.TrailingZeroCount((byte)~bitmap[byteIndex]);
                long result = byteIndex * 8 + zeroBit;
                if (result < totalBits) return result;
            }
            byteIndex++;
        }

        long ulongEnd = totalBytes - (totalBytes % 8);
        for (long i = byteIndex; i < ulongEnd; i += 8)
        {
            ulong word = MemoryMarshal.Read<ulong>(span.Slice((int)i, 8));
            if (word != ulong.MaxValue)
            {
                ulong inverted = ~word;
                int trailingZeros = BitOperations.TrailingZeroCount(inverted);
                long result = i * 8 + trailingZeros;
                if (result < totalBits) return result;
            }
        }

        for (long i = Math.Max(ulongEnd, byteIndex); i < totalBytes; i++)
        {
            if (bitmap[i] != 0xFF)
            {
                int zeroBit = BitOperations.TrailingZeroCount((byte)~bitmap[i]);
                long result = i * 8 + zeroBit;
                if (result < totalBits) return result;
            }
        }

        return -1;
    }

    // ── 4. XxHash64 SIMD ─────────────────────────────────────────────────

    /// <summary>
    /// Computes XXH64 hash of the given data with a seed value.
    /// For data &gt;= 32 bytes, uses 4 parallel accumulators for throughput.
    /// For data &lt; 32 bytes, uses the standard scalar XXH64 finalization path.
    /// </summary>
    /// <param name="data">Data to hash.</param>
    /// <param name="seed">Hash seed for producing different hash values from the same input.</param>
    /// <returns>64-bit hash value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong XxHash64Simd(ReadOnlySpan<byte> data, ulong seed)
    {
        int length = data.Length;

        if (length >= 32)
        {
            return XxHash64Long(data, seed);
        }

        return XxHash64Short(data, seed, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong XxHash64Long(ReadOnlySpan<byte> data, ulong seed)
    {
        int length = data.Length;

        // Initialize 4 accumulators
        ulong v1 = seed + XXH_PRIME64_1 + XXH_PRIME64_2;
        ulong v2 = seed + XXH_PRIME64_2;
        ulong v3 = seed;
        ulong v4 = seed - XXH_PRIME64_1;

        // Process 32-byte stripes
        int offset = 0;
        int stripeEnd = length - 31;

        while (offset < stripeEnd)
        {
            v1 = Xxh64Round(v1, MemoryMarshal.Read<ulong>(data.Slice(offset)));
            v2 = Xxh64Round(v2, MemoryMarshal.Read<ulong>(data.Slice(offset + 8)));
            v3 = Xxh64Round(v3, MemoryMarshal.Read<ulong>(data.Slice(offset + 16)));
            v4 = Xxh64Round(v4, MemoryMarshal.Read<ulong>(data.Slice(offset + 24)));
            offset += 32;
        }

        // Merge accumulators
        ulong h64 = BitOperations.RotateLeft(v1, 1)
                   + BitOperations.RotateLeft(v2, 7)
                   + BitOperations.RotateLeft(v3, 12)
                   + BitOperations.RotateLeft(v4, 18);

        h64 = Xxh64MergeRound(h64, v1);
        h64 = Xxh64MergeRound(h64, v2);
        h64 = Xxh64MergeRound(h64, v3);
        h64 = Xxh64MergeRound(h64, v4);

        h64 += (ulong)length;

        // Finalize remaining bytes
        return Xxh64Finalize(h64, data.Slice(offset));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong XxHash64Short(ReadOnlySpan<byte> data, ulong seed, int length)
    {
        ulong h64 = seed + XXH_PRIME64_5 + (ulong)length;
        return Xxh64Finalize(h64, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Xxh64Round(ulong acc, ulong input)
    {
        acc += input * XXH_PRIME64_2;
        acc = BitOperations.RotateLeft(acc, 31);
        acc *= XXH_PRIME64_1;
        return acc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Xxh64MergeRound(ulong acc, ulong val)
    {
        val = Xxh64Round(0, val);
        acc ^= val;
        acc = acc * XXH_PRIME64_1 + XXH_PRIME64_4;
        return acc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Xxh64Finalize(ulong h64, ReadOnlySpan<byte> remaining)
    {
        int offset = 0;
        int length = remaining.Length;

        // Process 8-byte chunks
        while (offset + 8 <= length)
        {
            ulong k1 = Xxh64Round(0, MemoryMarshal.Read<ulong>(remaining.Slice(offset)));
            h64 ^= k1;
            h64 = BitOperations.RotateLeft(h64, 27) * XXH_PRIME64_1 + XXH_PRIME64_4;
            offset += 8;
        }

        // Process 4-byte chunks
        while (offset + 4 <= length)
        {
            ulong k1 = (ulong)MemoryMarshal.Read<uint>(remaining.Slice(offset));
            h64 ^= k1 * XXH_PRIME64_1;
            h64 = BitOperations.RotateLeft(h64, 23) * XXH_PRIME64_2 + XXH_PRIME64_3;
            offset += 4;
        }

        // Process remaining bytes
        while (offset < length)
        {
            h64 ^= remaining[offset] * XXH_PRIME64_5;
            h64 = BitOperations.RotateLeft(h64, 11) * XXH_PRIME64_1;
            offset++;
        }

        // Final avalanche
        h64 ^= h64 >> 33;
        h64 *= XXH_PRIME64_2;
        h64 ^= h64 >> 29;
        h64 *= XXH_PRIME64_3;
        h64 ^= h64 >> 32;

        return h64;
    }
}
