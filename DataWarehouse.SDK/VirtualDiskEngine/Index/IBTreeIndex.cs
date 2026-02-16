using DataWarehouse.SDK.Contracts;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Index;

/// <summary>
/// Interface for B-Tree index providing O(log n) key-value operations.
/// </summary>
/// <remarks>
/// The B-Tree is the primary indexing structure for the Virtual Disk Engine.
/// It maps storage object keys to block numbers, enabling efficient lookup, insertion,
/// deletion, and range queries. All structural modifications are WAL-protected for crash safety.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE B-Tree index (VDE-05)")]
public interface IBTreeIndex
{
    /// <summary>
    /// Finds the value associated with an exact key match.
    /// </summary>
    /// <param name="key">The key to search for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The value associated with the key, or null if not found.</returns>
    Task<long?> LookupAsync(byte[] key, CancellationToken ct = default);

    /// <summary>
    /// Inserts a new key-value pair into the index.
    /// </summary>
    /// <param name="key">The key to insert.</param>
    /// <param name="value">The value to associate with the key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="System.InvalidOperationException">Thrown if the key already exists.</exception>
    Task InsertAsync(byte[] key, long value, CancellationToken ct = default);

    /// <summary>
    /// Updates the value for an existing key.
    /// </summary>
    /// <param name="key">The key to update.</param>
    /// <param name="newValue">The new value to associate with the key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the key was found and updated, false if not found.</returns>
    Task<bool> UpdateAsync(byte[] key, long newValue, CancellationToken ct = default);

    /// <summary>
    /// Deletes a key-value pair from the index.
    /// </summary>
    /// <param name="key">The key to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the key was found and deleted, false if not found.</returns>
    Task<bool> DeleteAsync(byte[] key, CancellationToken ct = default);

    /// <summary>
    /// Performs a range query returning all key-value pairs in [startKey, endKey) sorted order.
    /// </summary>
    /// <param name="startKey">The inclusive start key. Null means from the beginning.</param>
    /// <param name="endKey">The exclusive end key. Null means to the end.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of key-value pairs in sorted order.</returns>
    IAsyncEnumerable<(byte[] Key, long Value)> RangeQueryAsync(byte[]? startKey, byte[]? endKey, CancellationToken ct = default);

    /// <summary>
    /// Counts the total number of key-value pairs in the index.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The total count of entries.</returns>
    Task<long> CountAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the block number of the root node (stored in superblock).
    /// </summary>
    long RootBlockNumber { get; }
}
