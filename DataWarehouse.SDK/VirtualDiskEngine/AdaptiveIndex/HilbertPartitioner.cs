using System;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Hilbert space-filling curve implementation for locality-preserving shard partitioning.
/// </summary>
/// <remarks>
/// <para>
/// Hilbert curves provide better spatial locality than Z-order (Morton) curves because
/// neighboring points in multi-dimensional space remain close on the 1D curve. This
/// property is critical for range queries across shards: a Hilbert-partitioned forest
/// needs to touch fewer shards for spatial range queries than Z-order partitioning.
/// </para>
/// <para>
/// All methods are static and pure (no state). Hot-path methods are inlined for
/// performance in the tight shard-routing loop.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-04 Hilbert curve shard partitioner")]
public static class HilbertPartitioner
{
    /// <summary>
    /// Converts a multi-dimensional key to a 1D Hilbert index value.
    /// For single-dimension keys, the first N bytes are interpreted as coordinates.
    /// </summary>
    /// <param name="key">The key bytes to convert.</param>
    /// <param name="dimensions">Number of dimensions (2 or 3 typical; up to 8 supported).</param>
    /// <param name="bitsPerDimension">Bits per dimension (determines curve resolution). Max 20 for 2D, 14 for 3D.</param>
    /// <returns>The Hilbert index value.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when dimensions or bitsPerDimension are out of range.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long KeyToHilbertValue(byte[] key, int dimensions, int bitsPerDimension)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (dimensions < 1 || dimensions > 8)
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Dimensions must be 1-8.");
        if (bitsPerDimension < 1 || bitsPerDimension > 20)
            throw new ArgumentOutOfRangeException(nameof(bitsPerDimension), bitsPerDimension, "BitsPerDimension must be 1-20.");

        // Extract coordinates from key bytes
        var coords = ExtractCoordinates(key, dimensions, bitsPerDimension);

        // Compute Hilbert value using the Butz algorithm (generalized)
        return CoordsToHilbert(coords, dimensions, bitsPerDimension);
    }

    /// <summary>
    /// Divides the Hilbert space into equal-sized ranges for N shards.
    /// </summary>
    /// <param name="numShards">Number of shards to partition into.</param>
    /// <param name="bitsPerDimension">Bits per dimension (determines total Hilbert space size).</param>
    /// <returns>Array of (Start, End) inclusive ranges, one per shard.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when numShards is less than 1.</exception>
    public static (long Start, long End)[] PartitionHilbertSpace(int numShards, int bitsPerDimension)
    {
        if (numShards < 1)
            throw new ArgumentOutOfRangeException(nameof(numShards), numShards, "Must have at least 1 shard.");

        // Total Hilbert space is 2^(dimensions * bitsPerDimension), but for partitioning
        // we use a simplified 1D range based on bitsPerDimension alone
        long totalRange = 1L << (bitsPerDimension * 2); // Assume 2D for range calculation
        long rangePerShard = totalRange / numShards;

        var partitions = new (long Start, long End)[numShards];
        for (int i = 0; i < numShards; i++)
        {
            long start = i * rangePerShard;
            long end = (i == numShards - 1) ? totalRange - 1 : (i + 1) * rangePerShard - 1;
            partitions[i] = (start, end);
        }

        return partitions;
    }

    /// <summary>
    /// Maps a key to a shard ID via Hilbert value and range lookup.
    /// </summary>
    /// <param name="key">The key to route.</param>
    /// <param name="numShards">Total number of shards.</param>
    /// <returns>The shard ID (0-based).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetShardId(byte[] key, int numShards)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (numShards < 1)
            throw new ArgumentOutOfRangeException(nameof(numShards), numShards, "Must have at least 1 shard.");

        if (numShards == 1) return 0;

        // Use 2D Hilbert with 16 bits per dimension for good distribution
        const int dimensions = 2;
        const int bitsPerDimension = 16;

        long hilbertValue = KeyToHilbertValue(key, dimensions, bitsPerDimension);
        long totalRange = 1L << (dimensions * bitsPerDimension);
        long rangePerShard = totalRange / numShards;

        int shardId = (int)(hilbertValue / rangePerShard);
        // Clamp to valid range (last shard may get slightly more due to integer division)
        return Math.Min(shardId, numShards - 1);
    }

    /// <summary>
    /// Inverse mapping: converts a Hilbert index value back to multi-dimensional coordinates encoded as key bytes.
    /// Useful for range queries to determine which shards overlap a query range.
    /// </summary>
    /// <param name="hilbertValue">The Hilbert index value.</param>
    /// <param name="dimensions">Number of dimensions.</param>
    /// <param name="bitsPerDimension">Bits per dimension.</param>
    /// <returns>A key byte array representing the coordinates.</returns>
    public static byte[] HilbertValueToKey(long hilbertValue, int dimensions, int bitsPerDimension)
    {
        if (dimensions < 1 || dimensions > 8)
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Dimensions must be 1-8.");
        if (bitsPerDimension < 1 || bitsPerDimension > 20)
            throw new ArgumentOutOfRangeException(nameof(bitsPerDimension), bitsPerDimension, "BitsPerDimension must be 1-20.");

        var coords = HilbertToCoords(hilbertValue, dimensions, bitsPerDimension);

        // Encode coordinates back to key bytes (2 bytes per coordinate, big-endian)
        var key = new byte[dimensions * 2];
        for (int d = 0; d < dimensions; d++)
        {
            key[d * 2] = (byte)(coords[d] >> 8);
            key[d * 2 + 1] = (byte)(coords[d] & 0xFF);
        }

        return key;
    }

    /// <summary>
    /// Extracts N-dimensional coordinates from key bytes.
    /// Each dimension gets 2 bytes (big-endian), masked to bitsPerDimension bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int[] ExtractCoordinates(byte[] key, int dimensions, int bitsPerDimension)
    {
        var coords = new int[dimensions];
        int mask = (1 << bitsPerDimension) - 1;

        for (int d = 0; d < dimensions; d++)
        {
            int byteOffset = d * 2;
            int value = 0;

            if (byteOffset < key.Length)
                value = key[byteOffset] << 8;
            if (byteOffset + 1 < key.Length)
                value |= key[byteOffset + 1];

            coords[d] = value & mask;
        }

        return coords;
    }

    /// <summary>
    /// Converts N-dimensional coordinates to a Hilbert index using the generalized Butz algorithm.
    /// Uses bit-interleaving with Gray code transforms for arbitrary dimensions.
    /// </summary>
    private static long CoordsToHilbert(int[] coords, int dimensions, int bitsPerDimension)
    {
        // Transpose: convert coordinates to transposed form
        // This is the standard algorithm from "Programming the Hilbert Curve" by John Skilling (2004)
        int maxCoord = (1 << bitsPerDimension) - 1;
        var x = new int[dimensions];
        for (int i = 0; i < dimensions; i++)
            x[i] = Math.Min(coords[i], maxCoord);

        int m = 1 << (bitsPerDimension - 1);

        // Inverse undo: Gray code to Hilbert
        for (int q = m; q > 1; q >>= 1)
        {
            int p = q - 1;
            for (int i = 0; i < dimensions; i++)
            {
                if ((x[i] & q) != 0)
                {
                    x[0] ^= p; // Invert
                }
                else
                {
                    int t = (x[0] ^ x[i]) & p;
                    x[0] ^= t;
                    x[i] ^= t;
                }
            }
        }

        // Gray encode
        for (int i = 1; i < dimensions; i++)
            x[i] ^= x[i - 1];

        int t2 = 0;
        for (int q = m; q > 1; q >>= 1)
        {
            if ((x[dimensions - 1] & q) != 0)
                t2 ^= q - 1;
        }

        for (int i = 0; i < dimensions; i++)
            x[i] ^= t2;

        // Transpose bits to compute Hilbert index
        return TransposeToHilbertIndex(x, dimensions, bitsPerDimension);
    }

    /// <summary>
    /// Converts a Hilbert index back to N-dimensional coordinates.
    /// Inverse of <see cref="CoordsToHilbert"/>.
    /// </summary>
    private static int[] HilbertToCoords(long hilbertValue, int dimensions, int bitsPerDimension)
    {
        // Convert Hilbert index to transposed form
        var x = HilbertIndexToTranspose(hilbertValue, dimensions, bitsPerDimension);

        // Undo Gray encode
        int n = 1 << bitsPerDimension;

        int t = x[dimensions - 1] >> 1;
        for (int i = dimensions - 1; i > 0; i--)
            x[i] ^= x[i - 1];
        x[0] ^= t;

        // Undo transpose
        for (int q = 2; q < n; q <<= 1)
        {
            int p = q - 1;
            for (int i = dimensions - 1; i >= 0; i--)
            {
                if ((x[i] & q) != 0)
                {
                    x[0] ^= p;
                }
                else
                {
                    int tt = (x[0] ^ x[i]) & p;
                    x[0] ^= tt;
                    x[i] ^= tt;
                }
            }
        }

        return x;
    }

    /// <summary>
    /// Transposes N coordinate values into a single Hilbert index by interleaving bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long TransposeToHilbertIndex(int[] x, int dimensions, int bitsPerDimension)
    {
        long result = 0;
        int totalBits = dimensions * bitsPerDimension;

        for (int b = bitsPerDimension - 1; b >= 0; b--)
        {
            for (int d = 0; d < dimensions; d++)
            {
                int bitPosition = (bitsPerDimension - 1 - b) * dimensions + d;
                if (bitPosition < totalBits && (x[d] & (1 << b)) != 0)
                {
                    result |= 1L << (totalBits - 1 - bitPosition);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Converts a Hilbert index into transposed coordinate values by de-interleaving bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int[] HilbertIndexToTranspose(long hilbertValue, int dimensions, int bitsPerDimension)
    {
        var x = new int[dimensions];
        int totalBits = dimensions * bitsPerDimension;

        for (int b = bitsPerDimension - 1; b >= 0; b--)
        {
            for (int d = 0; d < dimensions; d++)
            {
                int bitPosition = (bitsPerDimension - 1 - b) * dimensions + d;
                if (bitPosition < totalBits && (hilbertValue & (1L << (totalBits - 1 - bitPosition))) != 0)
                {
                    x[d] |= 1 << b;
                }
            }
        }

        return x;
    }
}
