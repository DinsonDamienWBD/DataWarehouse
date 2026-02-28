using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace DataWarehouse.SDK.Mathematics;

/// <summary>
/// High-performance parity calculation utilities for RAID operations.
/// Provides optimized implementations for XOR, P+Q, and diagonal parity schemes.
/// Uses SIMD instructions when available for maximum throughput.
/// </summary>
public static class ParityCalculation
{
    /// <summary>
    /// Computes XOR parity across multiple data blocks (RAID 5 style).
    /// This is the most basic parity scheme where parity = D1 XOR D2 XOR ... XOR Dn.
    /// </summary>
    /// <param name="dataBlocks">Array of data blocks to compute parity from</param>
    /// <param name="parity">Output buffer for computed parity (must be same length as data blocks)</param>
    /// <exception cref="ArgumentNullException">Thrown when parameters are null</exception>
    /// <exception cref="ArgumentException">Thrown when block sizes don't match</exception>
    public static void ComputeXorParity(ReadOnlySpan<byte[]> dataBlocks, Span<byte> parity)
    {
        if (dataBlocks.Length == 0)
        {
            throw new ArgumentException("Must provide at least one data block", nameof(dataBlocks));
        }

        int blockSize = dataBlocks[0].Length;

        if (parity.Length != blockSize)
        {
            throw new ArgumentException($"Parity buffer must be {blockSize} bytes", nameof(parity));
        }

        // Initialize parity to zeros
        parity.Clear();

        // XOR all data blocks into parity
        foreach (var block in dataBlocks)
        {
            if (block.Length != blockSize)
            {
                throw new ArgumentException("All data blocks must be same size", nameof(dataBlocks));
            }

            XorInto(block, parity);
        }
    }

    /// <summary>
    /// Computes P and Q parity for dual-parity RAID (RAID 6 style).
    /// P parity is XOR of all data blocks.
    /// Q parity is Reed-Solomon syndrome using Galois Field multiplication.
    /// </summary>
    /// <param name="dataBlocks">Array of data blocks</param>
    /// <param name="pParity">Output buffer for P parity (XOR)</param>
    /// <param name="qParity">Output buffer for Q parity (Reed-Solomon)</param>
    /// <exception cref="ArgumentNullException">Thrown when parameters are null</exception>
    /// <exception cref="ArgumentException">Thrown when block sizes don't match</exception>
    public static void ComputePQParity(ReadOnlySpan<byte[]> dataBlocks, Span<byte> pParity, Span<byte> qParity)
    {
        if (dataBlocks.Length == 0)
        {
            throw new ArgumentException("Must provide at least one data block", nameof(dataBlocks));
        }

        int blockSize = dataBlocks[0].Length;

        if (pParity.Length != blockSize)
        {
            throw new ArgumentException($"P parity buffer must be {blockSize} bytes", nameof(pParity));
        }

        if (qParity.Length != blockSize)
        {
            throw new ArgumentException($"Q parity buffer must be {blockSize} bytes", nameof(qParity));
        }

        // Initialize parity buffers
        pParity.Clear();
        qParity.Clear();

        // Compute P (XOR) and Q (Galois Field) parity
        for (int i = 0; i < dataBlocks.Length; i++)
        {
            var block = dataBlocks[i];

            if (block.Length != blockSize)
            {
                throw new ArgumentException("All data blocks must be same size", nameof(dataBlocks));
            }

            // P parity: simple XOR
            XorInto(block, pParity);

            // Q parity: multiply by g^i and XOR
            // g is the generator (2 in GF(2^8))
            byte coefficient = GaloisField.GeneratorPower(i);
            GaloisMultiplyAndXor(block, coefficient, qParity);
        }
    }

    /// <summary>
    /// Computes diagonal parity for advanced RAID schemes.
    /// Diagonal parity provides additional redundancy by XORing elements along diagonals.
    /// </summary>
    /// <param name="dataBlocks">Array of data blocks arranged in a matrix</param>
    /// <param name="diagonalParity">Output buffer for diagonal parity</param>
    /// <param name="stripeSize">Size of each stripe unit within blocks</param>
    /// <exception cref="ArgumentException">Thrown when configuration is invalid</exception>
    public static void ComputeDiagonalParity(ReadOnlySpan<byte[]> dataBlocks, Span<byte> diagonalParity, int stripeSize)
    {
        if (dataBlocks.Length == 0)
        {
            throw new ArgumentException("Must provide at least one data block", nameof(dataBlocks));
        }

        if (stripeSize <= 0)
        {
            throw new ArgumentException("Stripe size must be positive", nameof(stripeSize));
        }

        int blockSize = dataBlocks[0].Length;

        if (blockSize % stripeSize != 0)
        {
            throw new ArgumentException("Block size must be multiple of stripe size", nameof(dataBlocks));
        }

        if (diagonalParity.Length != blockSize)
        {
            throw new ArgumentException($"Diagonal parity buffer must be {blockSize} bytes", nameof(diagonalParity));
        }

        diagonalParity.Clear();

        int stripesPerBlock = blockSize / stripeSize;

        // For each diagonal
        for (int diag = 0; diag < dataBlocks.Length; diag++)
        {
            for (int stripe = 0; stripe < stripesPerBlock; stripe++)
            {
                // Calculate which block and stripe offset for this diagonal position
                int blockIndex = (diag + stripe) % dataBlocks.Length;
                int sourceOffset = stripe * stripeSize;
                int destOffset = stripe * stripeSize;

                // XOR stripe into diagonal parity
                var sourceStripe = dataBlocks[blockIndex].AsSpan(sourceOffset, stripeSize);
                var destStripe = diagonalParity.Slice(destOffset, stripeSize);

                XorInto(sourceStripe, destStripe);
            }
        }
    }

    /// <summary>
    /// XORs source data into destination buffer in-place.
    /// Uses SIMD instructions when available for optimal performance.
    /// </summary>
    /// <param name="source">Source data to XOR</param>
    /// <param name="destination">Destination buffer (modified in-place)</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void XorInto(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length != destination.Length)
        {
            throw new ArgumentException("Source and destination must be same length");
        }

        int length = source.Length;
        int i = 0;

        // Use AVX2 if available (32 bytes at a time)
        if (Avx2.IsSupported && length >= 32)
        {
            ref byte srcRef = ref MemoryMarshal.GetReference(source);
            ref byte dstRef = ref MemoryMarshal.GetReference(destination);

            int vectorEnd = length - 31;
            for (; i < vectorEnd; i += 32)
            {
                var srcVec = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref srcRef, i));
                var dstVec = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref dstRef, i));
                var xorVec = Avx2.Xor(srcVec, dstVec);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, i), xorVec);
            }
        }
        // Use SSE2 if available (16 bytes at a time)
        else if (Sse2.IsSupported && length >= 16)
        {
            ref byte srcRef = ref MemoryMarshal.GetReference(source);
            ref byte dstRef = ref MemoryMarshal.GetReference(destination);

            int vectorEnd = length - 15;
            for (; i < vectorEnd; i += 16)
            {
                var srcVec = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref srcRef, i));
                var dstVec = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref dstRef, i));
                var xorVec = Sse2.Xor(srcVec, dstVec);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, i), xorVec);
            }
        }

        // Handle remaining bytes (scalar)
        for (; i < length; i++)
        {
            destination[i] ^= source[i];
        }
    }

    /// <summary>
    /// Multiplies source data by a Galois Field coefficient and XORs into destination.
    /// Used for Reed-Solomon Q parity calculation.
    /// </summary>
    /// <param name="source">Source data</param>
    /// <param name="coefficient">Galois Field multiplier</param>
    /// <param name="destination">Destination buffer (modified in-place)</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void GaloisMultiplyAndXor(ReadOnlySpan<byte> source, byte coefficient, Span<byte> destination)
    {
        if (source.Length != destination.Length)
        {
            throw new ArgumentException("Source and destination must be same length");
        }

        // Special cases for performance
        if (coefficient == 0)
        {
            return; // Multiply by 0 = no change
        }

        if (coefficient == 1)
        {
            XorInto(source, destination); // Multiply by 1 = simple XOR
            return;
        }

        // General case: GF multiply each byte
        for (int i = 0; i < source.Length; i++)
        {
            byte product = GaloisField.Multiply(source[i], coefficient);
            destination[i] ^= product;
        }
    }

    /// <summary>
    /// Rebuilds a missing data block using XOR parity (RAID 5 reconstruction).
    /// </summary>
    /// <param name="availableBlocks">All available blocks (data + parity)</param>
    /// <param name="missingBlockIndex">Index of the block to rebuild</param>
    /// <param name="output">Output buffer for rebuilt block</param>
    /// <exception cref="ArgumentException">Thrown when parameters are invalid</exception>
    public static void RebuildFromXorParity(ReadOnlySpan<byte[]> availableBlocks, int missingBlockIndex, Span<byte> output)
    {
        if (availableBlocks.Length == 0)
        {
            throw new ArgumentException("Must provide at least one available block", nameof(availableBlocks));
        }

        int blockSize = availableBlocks[0].Length;

        if (output.Length != blockSize)
        {
            throw new ArgumentException($"Output buffer must be {blockSize} bytes", nameof(output));
        }

        // XOR all available blocks (excluding the missing one)
        output.Clear();

        for (int i = 0; i < availableBlocks.Length; i++)
        {
            if (i != missingBlockIndex)
            {
                if (availableBlocks[i].Length != blockSize)
                {
                    throw new ArgumentException("All blocks must be same size", nameof(availableBlocks));
                }

                XorInto(availableBlocks[i], output);
            }
        }
    }

    /// <summary>
    /// Validates that parity is consistent with data blocks.
    /// Useful for integrity checking and verification.
    /// </summary>
    /// <param name="dataBlocks">Data blocks to validate</param>
    /// <param name="parity">Expected parity</param>
    /// <returns>True if parity matches, false otherwise</returns>
    public static bool ValidateXorParity(ReadOnlySpan<byte[]> dataBlocks, ReadOnlySpan<byte> parity)
    {
        if (dataBlocks.Length == 0)
        {
            return false;
        }

        int blockSize = dataBlocks[0].Length;
        if (parity.Length != blockSize)
        {
            return false;
        }

        // Guard against stack overflow for RAID-sized blocks (typical RAID blocks can be MBs).
        // Limit stackalloc to a safe threshold; use heap allocation for larger blocks.
        const int MaxStackAllocBytes = 4096;
        byte[]? heapBuffer = blockSize > MaxStackAllocBytes ? new byte[blockSize] : null;
        Span<byte> computedParity = heapBuffer != null
            ? heapBuffer.AsSpan(0, blockSize)
            : stackalloc byte[blockSize];
        try
        {
            ComputeXorParity(dataBlocks, computedParity);

            // Compare with expected parity
            return computedParity.SequenceEqual(parity);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if the system supports hardware acceleration (SIMD).
    /// </summary>
    /// <returns>Acceleration level: 0=none, 1=SSE2, 2=AVX2</returns>
    public static int GetAccelerationLevel()
    {
        if (Avx2.IsSupported)
        {
            return 2;
        }
        if (Sse2.IsSupported)
        {
            return 1;
        }
        return 0;
    }

    /// <summary>
    /// Gets a human-readable description of the acceleration support.
    /// </summary>
    /// <returns>Description of SIMD support</returns>
    public static string GetAccelerationDescription()
    {
        return GetAccelerationLevel() switch
        {
            2 => "AVX2 (256-bit SIMD)",
            1 => "SSE2 (128-bit SIMD)",
            _ => "Scalar (no SIMD)"
        };
    }
}
