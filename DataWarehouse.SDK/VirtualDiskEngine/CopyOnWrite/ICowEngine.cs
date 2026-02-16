using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.CopyOnWrite;

/// <summary>
/// Interface for Copy-on-Write (CoW) operations enabling zero-cost snapshots and efficient cloning.
/// </summary>
/// <remarks>
/// The CoW engine manages reference-counted blocks to enable snapshot semantics:
/// - When a block has refCount == 1, it can be modified in-place.
/// - When a block has refCount > 1 (shared by multiple snapshots), modifying it creates a new block
///   and decrements the reference count on the original.
/// - Snapshots are created by cloning inode trees and incrementing reference counts on all referenced blocks.
/// - Deleting snapshots decrements reference counts; blocks with refCount == 0 are freed.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE CoW engine (VDE-06)")]
public interface ICowEngine
{
    /// <summary>
    /// Performs a Copy-on-Write write operation: if the block is shared (refCount > 1), allocates
    /// a new block and writes data there; otherwise writes in-place.
    /// </summary>
    /// <param name="originalBlockNumber">The block number to write to (may be modified in-place or copied).</param>
    /// <param name="newData">The new data to write (must be exactly BlockSize bytes).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The block number where data was written (same as original if written in-place, new block number if copied).</returns>
    /// <exception cref="InvalidOperationException">Thrown if block allocation fails.</exception>
    Task<long> WriteBlockCowAsync(long originalBlockNumber, ReadOnlyMemory<byte> newData, CancellationToken ct = default);

    /// <summary>
    /// Increments the reference count for a block (called during snapshot creation).
    /// </summary>
    /// <param name="blockNumber">Block number to increment.</param>
    /// <param name="ct">Cancellation token.</param>
    Task IncrementRefAsync(long blockNumber, CancellationToken ct = default);

    /// <summary>
    /// Decrements the reference count for a block (called during snapshot deletion).
    /// If the reference count reaches 0, the block is freed.
    /// </summary>
    /// <param name="blockNumber">Block number to decrement.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DecrementRefAsync(long blockNumber, CancellationToken ct = default);

    /// <summary>
    /// Gets the current reference count for a block.
    /// </summary>
    /// <param name="blockNumber">Block number to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current reference count (default is 1 if not tracked in the B-Tree).</returns>
    Task<int> GetRefCountAsync(long blockNumber, CancellationToken ct = default);

    /// <summary>
    /// Batch increments reference counts for multiple blocks (optimized for snapshot creation).
    /// All operations are wrapped in a single WAL transaction.
    /// </summary>
    /// <param name="blockNumbers">Block numbers to increment.</param>
    /// <param name="ct">Cancellation token.</param>
    Task IncrementRefBatchAsync(IEnumerable<long> blockNumbers, CancellationToken ct = default);

    /// <summary>
    /// Batch decrements reference counts for multiple blocks (optimized for snapshot deletion).
    /// All operations are wrapped in a single WAL transaction.
    /// Blocks with refCount reaching 0 are freed.
    /// </summary>
    /// <param name="blockNumbers">Block numbers to decrement.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DecrementRefBatchAsync(IEnumerable<long> blockNumbers, CancellationToken ct = default);
}
