using System;
using System.Threading;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Lock-free logical-to-physical page mapping table for the Bw-Tree.
/// </summary>
/// <remarks>
/// <para>
/// The mapping table provides indirection between logical page IDs and their current
/// delta chain heads. This indirection is the key enabler of lock-free operation:
/// all page modifications are performed by CAS-replacing the chain head pointer
/// in the mapping table, rather than modifying page data in-place.
/// </para>
/// <para>
/// The table auto-grows when page IDs exceed the current capacity, using a
/// copy-on-resize strategy with CAS on the array reference itself to ensure
/// thread safety during resizing.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-09 Bw-Tree mapping table")]
public sealed class BwTreeMappingTable
{
    private volatile object?[] _entries;
    private long _nextPageId;
    private const int InitialCapacity = 4096;
    private const int GrowthFactor = 2;

    /// <summary>
    /// Gets the current capacity of the mapping table.
    /// </summary>
    public int Capacity => _entries.Length;

    /// <summary>
    /// Gets the number of allocated page IDs.
    /// </summary>
    public long AllocatedCount => Interlocked.Read(ref _nextPageId);

    /// <summary>
    /// Initializes a new mapping table with the specified initial capacity.
    /// </summary>
    /// <param name="initialCapacity">The initial number of page slots. Defaults to 4096.</param>
    public BwTreeMappingTable(int initialCapacity = InitialCapacity)
    {
        if (initialCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity), "Initial capacity must be positive.");

        _entries = new object?[initialCapacity];
        _nextPageId = 0;
    }

    /// <summary>
    /// Gets the current delta chain head for the specified logical page ID.
    /// </summary>
    /// <param name="pageId">The logical page ID to look up.</param>
    /// <returns>The current chain head object, or null if the page is empty/unallocated.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if pageId is negative.</exception>
    public object? Get(long pageId)
    {
        if (pageId < 0)
            throw new ArgumentOutOfRangeException(nameof(pageId), "Page ID must be non-negative.");

        var entries = _entries; // Snapshot volatile read
        if (pageId >= entries.Length)
            return null;

        return Volatile.Read(ref entries[pageId]);
    }

    /// <summary>
    /// Atomically replaces the chain head for a page if it matches the expected value.
    /// </summary>
    /// <param name="pageId">The logical page ID to update.</param>
    /// <param name="expected">The expected current chain head.</param>
    /// <param name="newValue">The new chain head to install.</param>
    /// <returns>True if the exchange succeeded (expected matched current), false otherwise.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if pageId is negative.</exception>
    public bool CompareExchange(long pageId, object? expected, object? newValue)
    {
        if (pageId < 0)
            throw new ArgumentOutOfRangeException(nameof(pageId), "Page ID must be non-negative.");

        EnsureCapacity(pageId);

        var entries = _entries; // Snapshot after potential resize
        var original = Interlocked.CompareExchange(ref entries[pageId], newValue, expected);
        return ReferenceEquals(original, expected);
    }

    /// <summary>
    /// Allocates and returns the next available logical page ID via atomic increment.
    /// </summary>
    /// <returns>A unique logical page ID.</returns>
    public long AllocatePageId()
    {
        long id = Interlocked.Increment(ref _nextPageId) - 1;
        EnsureCapacity(id);
        return id;
    }

    /// <summary>
    /// Ensures the mapping table has capacity for the specified page ID,
    /// growing the array if necessary using copy-on-resize with CAS.
    /// </summary>
    /// <param name="pageId">The page ID that must be addressable.</param>
    private void EnsureCapacity(long pageId)
    {
        var currentEntries = _entries;
        if (pageId < currentEntries.Length)
            return;

        // Calculate new size (double until it fits)
        int newSize = currentEntries.Length;
        while (newSize <= pageId)
        {
            newSize = checked(newSize * GrowthFactor);
        }

        // Copy-on-resize with CAS on the array reference
        var newEntries = new object?[newSize];
        Array.Copy(currentEntries, newEntries, currentEntries.Length);

        // CAS the array reference - if another thread already resized, our copy is discarded
        // which is safe because the other thread's array will contain the same data
        Interlocked.CompareExchange(ref _entries, newEntries, currentEntries);

        // If CAS failed, the array was already resized by another thread.
        // Verify the new array is large enough; if not, recurse.
        if (_entries.Length <= pageId)
        {
            EnsureCapacity(pageId);
        }
    }
}
