using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;

/// <summary>
/// Static SIMD helper for word-at-a-time bitmap scanning.
/// Uses hardware-accelerated BitOperations (BSF/TZCNT/POPCNT) to process
/// 64 bits per operation instead of bit-by-bit iteration.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: SIMD-accelerated bitmap scanning")]
public static class SimdBitmapScanner
{
    /// <summary>
    /// Finds first zero bit in bitmap starting from startBit.
    /// Returns -1 if no zero bit found.
    /// Uses 64-bit word scanning with BitOperations.TrailingZeroCount.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long FindFirstZeroBit(byte[] bitmap, long totalBits, long startBit)
    {
        // Phase 1: Handle partial first byte (align to byte boundary)
        long byteIndex = startBit / 8;
        int bitOffset = (int)(startBit % 8);
        long totalBytes = (totalBits + 7) / 8;

        if (bitOffset != 0 && byteIndex < totalBytes)
        {
            // Check remaining bits in the starting byte
            byte b = bitmap[byteIndex];
            byte masked = (byte)(b | ((1 << bitOffset) - 1)); // mask out bits before startBit
            if (masked != 0xFF)
            {
                int zeroBit = BitOperations.TrailingZeroCount((byte)~masked);
                long result = byteIndex * 8 + zeroBit;
                if (result < totalBits) return result;
            }
            byteIndex++;
        }

        // Phase 2: Scan 8 bytes (ulong) at a time
        long ulongStart = byteIndex;
        // Align to 8-byte boundary
        while (ulongStart % 8 != 0 && ulongStart < totalBytes)
        {
            if (bitmap[ulongStart] != 0xFF)
            {
                int zeroBit = BitOperations.TrailingZeroCount((byte)~bitmap[ulongStart]);
                long result = ulongStart * 8 + zeroBit;
                if (result < totalBits) return result;
            }
            ulongStart++;
        }

        // Scan as ulong (64 bits at a time)
        ReadOnlySpan<byte> span = bitmap.AsSpan();
        long ulongEnd = totalBytes - (totalBytes % 8);
        for (long i = ulongStart; i < ulongEnd; i += 8)
        {
            ulong word = MemoryMarshal.Read<ulong>(span.Slice((int)i, 8));
            if (word != ulong.MaxValue) // has at least one zero bit
            {
                ulong inverted = ~word;
                int trailingZeros = BitOperations.TrailingZeroCount(inverted);
                long result = i * 8 + trailingZeros;
                if (result < totalBits) return result;
            }
        }

        // Phase 3: Handle remaining bytes
        for (long i = Math.Max(ulongEnd, ulongStart); i < totalBytes; i++)
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

    /// <summary>
    /// Finds first contiguous run of zero bits of length >= minRun.
    /// Returns start bit index, or -1 if not found.
    /// Uses ulong fast-skip for fully-allocated 64-bit words.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long FindContiguousZeroBits(byte[] bitmap, long totalBits, int minRun)
    {
        long runStart = -1;
        int currentRun = 0;
        long totalBytes = (totalBits + 7) / 8;

        // Fast skip: scan ulong-at-a-time for fully-allocated words
        ReadOnlySpan<byte> span = bitmap.AsSpan();

        for (long byteIdx = 0; byteIdx < totalBytes;)
        {
            // Try ulong fast path
            if (currentRun == 0 && byteIdx % 8 == 0 && byteIdx + 8 <= totalBytes)
            {
                ulong word = MemoryMarshal.Read<ulong>(span.Slice((int)byteIdx, 8));
                if (word == ulong.MaxValue)
                {
                    byteIdx += 8; // skip 64 fully-allocated bits
                    continue;
                }
            }

            byte b = bitmap[byteIdx];
            for (int bit = 0; bit < 8; bit++)
            {
                long bitIndex = byteIdx * 8 + bit;
                if (bitIndex >= totalBits) break;

                if ((b & (1 << bit)) == 0) // free
                {
                    if (currentRun == 0) runStart = bitIndex;
                    currentRun++;
                    if (currentRun >= minRun) return runStart;
                }
                else
                {
                    currentRun = 0;
                    runStart = -1;
                }
            }
            byteIdx++;
        }

        return -1;
    }

    /// <summary>
    /// Counts total zero bits (free blocks) using PopCount.
    /// Processes 64 bits per operation via hardware POPCNT instruction.
    /// </summary>
    public static long CountZeroBits(byte[] bitmap, long totalBits)
    {
        long totalBytes = (totalBits + 7) / 8;
        long setBits = 0;
        ReadOnlySpan<byte> span = bitmap.AsSpan();
        long ulongEnd = totalBytes - (totalBytes % 8);

        for (long i = 0; i < ulongEnd; i += 8)
        {
            ulong word = MemoryMarshal.Read<ulong>(span.Slice((int)i, 8));
            setBits += BitOperations.PopCount(word);
        }

        for (long i = ulongEnd; i < totalBytes; i++)
        {
            setBits += BitOperations.PopCount(bitmap[i]);
        }

        // Subtract extra bits in last byte beyond totalBits
        int extraBits = (int)((totalBytes * 8) - totalBits);
        if (extraBits > 0)
        {
            byte lastByte = bitmap[totalBytes - 1];
            for (int bit = (int)(totalBits % 8); bit < 8; bit++)
            {
                if ((lastByte & (1 << bit)) != 0) setBits--;
            }
        }

        return totalBits - setBits;
    }
}
