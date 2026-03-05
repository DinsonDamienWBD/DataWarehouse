using System;
using System.Buffers;
using System.IO.Hashing;
using System.Text;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Deterministic path-to-slot mapping engine using XxHash64 with consistent hashing.
/// Hashes path segments to domain slots in the routing ring, enabling path-embedded routing
/// where the key's path structure directly determines which shard holds the data.
/// </summary>
/// <remarks>
/// Path normalization rules:
/// - Leading and trailing '/' characters are trimmed.
/// - Consecutive '/' characters are collapsed to a single separator.
/// - Case is preserved (no lowering) -- routing is binary-exact.
/// - Empty segments after normalization are skipped.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Federation Router (VFED-04)")]
public sealed class PathHashRouter
{
    /// <summary>
    /// Maximum path segment length (in chars) that uses stackalloc for UTF8 conversion.
    /// Segments longer than this use ArrayPool to avoid stack overflow.
    /// </summary>
    private const int StackAllocCharThreshold = 256;

    /// <summary>
    /// Maximum UTF8 byte count per char (worst case for stackalloc sizing).
    /// </summary>
    private const int MaxUtf8BytesPerChar = 4;

    private readonly int _slotCount;

    /// <summary>
    /// Creates a new path hash router with the specified number of domain slots.
    /// </summary>
    /// <param name="slotCount">Number of slots in the routing ring.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when slotCount is less than 1.</exception>
    public PathHashRouter(int slotCount = FederationConstants.DefaultDomainSlotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);
        _slotCount = slotCount;
    }

    /// <summary>
    /// Number of slots in the routing ring.
    /// </summary>
    public int SlotCount => _slotCount;

    /// <summary>
    /// Computes the slot index for a single path segment (char span).
    /// Converts to UTF8 on the stack for short segments, uses ArrayPool for long ones.
    /// </summary>
    /// <param name="pathSegment">The path segment to hash.</param>
    /// <returns>Slot index in [0, SlotCount).</returns>
    public int ComputeSlot(ReadOnlySpan<char> pathSegment)
    {
        if (pathSegment.IsEmpty)
            return 0;

        int maxByteCount = Encoding.UTF8.GetMaxByteCount(pathSegment.Length);

        if (pathSegment.Length <= StackAllocCharThreshold)
        {
            Span<byte> utf8Bytes = stackalloc byte[maxByteCount];
            int bytesWritten = Encoding.UTF8.GetBytes(pathSegment, utf8Bytes);
            return ComputeSlot(utf8Bytes[..bytesWritten]);
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(maxByteCount);
        try
        {
            int bytesWritten = Encoding.UTF8.GetBytes(pathSegment, rented);
            return ComputeSlot(rented.AsSpan(0, bytesWritten));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Computes the slot index for a single path segment already encoded as UTF8 bytes.
    /// This is the zero-allocation hot path for callers that already have UTF8 data.
    /// </summary>
    /// <param name="utf8PathSegment">UTF8-encoded path segment bytes.</param>
    /// <returns>Slot index in [0, SlotCount).</returns>
    public int ComputeSlot(ReadOnlySpan<byte> utf8PathSegment)
    {
        ulong hash = XxHash64.HashToUInt64(utf8PathSegment, (long)FederationConstants.HashSeed);
        return (int)(hash % (ulong)_slotCount);
    }

    /// <summary>
    /// Splits a full path by '/' separator and computes slot indices for each segment.
    /// The first slot determines the domain, subsequent slots are used for deeper routing.
    /// </summary>
    /// <param name="fullPath">Full path string (e.g., "domain/collection/key").</param>
    /// <returns>Array of slot indices, one per non-empty path segment.</returns>
    /// <exception cref="ArgumentNullException">Thrown when fullPath is null.</exception>
    public int[] ComputeSlots(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);

        ReadOnlySpan<char> normalized = NormalizePath(fullPath.AsSpan());
        if (normalized.IsEmpty)
            return [];

        // Count segments first to allocate exact array
        int segmentCount = 1;
        for (int i = 0; i < normalized.Length; i++)
        {
            if (normalized[i] == '/')
                segmentCount++;
        }

        int[] slots = new int[segmentCount];
        int slotIndex = 0;
        int segmentStart = 0;

        for (int i = 0; i <= normalized.Length; i++)
        {
            if (i == normalized.Length || normalized[i] == '/')
            {
                ReadOnlySpan<char> segment = normalized[segmentStart..i];
                if (!segment.IsEmpty)
                {
                    slots[slotIndex++] = ComputeSlot(segment);
                }
                segmentStart = i + 1;
            }
        }

        // Trim if we skipped any empty segments (shouldn't happen after normalization, but defensive)
        if (slotIndex < slots.Length)
            return slots[..slotIndex];

        return slots;
    }

    /// <summary>
    /// Normalizes a path span: trims leading/trailing '/', collapses consecutive '/'.
    /// Returns a trimmed view or a new span if collapsing was needed.
    /// </summary>
    private static ReadOnlySpan<char> NormalizePath(ReadOnlySpan<char> path)
    {
        // Trim leading and trailing '/'
        path = path.Trim('/');

        if (path.IsEmpty)
            return path;

        // Check if collapsing is needed
        bool hasConsecutive = false;
        for (int i = 1; i < path.Length; i++)
        {
            if (path[i] == '/' && path[i - 1] == '/')
            {
                hasConsecutive = true;
                break;
            }
        }

        if (!hasConsecutive)
            return path;

        // Need to collapse -- allocate and copy
        Span<char> buffer = path.Length <= StackAllocCharThreshold
            ? stackalloc char[path.Length]
            : new char[path.Length];

        int writePos = 0;
        bool lastWasSlash = false;

        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '/')
            {
                if (!lastWasSlash)
                {
                    buffer[writePos++] = '/';
                    lastWasSlash = true;
                }
            }
            else
            {
                buffer[writePos++] = path[i];
                lastWasSlash = false;
            }
        }

        return new string(buffer[..writePos]).AsSpan();
    }
}
