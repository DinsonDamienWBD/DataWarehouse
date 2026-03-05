using System;
using System.IO.Hashing;
using System.Text;

namespace DataWarehouse.SDK.Utilities;

/// <summary>
/// Provides deterministic, process-stable hash functions for routing, sharding,
/// and any persistent use case. Unlike <see cref="string.GetHashCode()"/> which is
/// randomized per-process in .NET 5+, these methods produce the same value across
/// restarts, machines, and runtime versions.
/// </summary>
public static class StableHash
{
    /// <summary>
    /// Computes a deterministic 32-bit hash of the given string using XxHash32.
    /// Safe for: partition assignment, consistent hashing, shard routing, bloom filters.
    /// </summary>
    public static int Compute(string value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        return unchecked((int)XxHash32.HashToUInt32(Encoding.UTF8.GetBytes(value)));
    }

    /// <summary>
    /// Computes a deterministic 64-bit hash of the given string using XxHash64.
    /// Safe for: trace IDs, span IDs, high-cardinality routing.
    /// </summary>
    public static long ComputeLong(string value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        return unchecked((long)XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(value)));
    }

    /// <summary>
    /// Computes a deterministic 32-bit hash of the given byte array using XxHash32.
    /// </summary>
    public static int Compute(ReadOnlySpan<byte> data)
    {
        return unchecked((int)XxHash32.HashToUInt32(data));
    }

    /// <summary>
    /// Computes a deterministic 64-bit hash of the given byte array using XxHash64.
    /// </summary>
    public static long ComputeLong(ReadOnlySpan<byte> data)
    {
        return unchecked((long)XxHash64.HashToUInt64(data));
    }

    /// <summary>
    /// Computes a non-negative modulo suitable for partition/bucket assignment.
    /// Equivalent to <c>Math.Abs(Compute(value)) % bucketCount</c> but avoids
    /// <see cref="int.MinValue"/> edge case.
    /// </summary>
    public static int Partition(string value, int bucketCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketCount);
        return (int)((uint)Compute(value) % (uint)bucketCount);
    }
}
