using DataWarehouse.SDK.Contracts;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO;

/// <summary>
/// Extends <see cref="IBlockDevice"/> with batched scatter-gather I/O operations.
/// Enables efficient multi-block reads and writes in a single call, reducing
/// per-operation overhead for workloads that touch many non-contiguous blocks.
/// </summary>
/// <remarks>
/// <para>
/// Scatter-gather I/O allows multiple disjoint block ranges to be read or written
/// in a single operation. Each <see cref="BlockRange"/> or <see cref="WriteRequest"/>
/// specifies its own block number, count, and buffer, enabling the implementation
/// to optimize I/O scheduling (e.g., merging adjacent requests, reordering for
/// sequential access, or submitting to kernel batch APIs).
/// </para>
/// <para>
/// Implementations may process requests in any order and may return partial success
/// if some operations fail. The return value indicates how many operations completed
/// successfully from the beginning of the list.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "VOPT-79: Batched scatter-gather I/O for VDE block devices")]
public interface IBatchBlockDevice : IBlockDevice
{
    /// <summary>
    /// Gets the maximum number of operations that can be submitted in a single batch call.
    /// Callers should partition larger request lists into chunks of this size.
    /// </summary>
    int MaxBatchSize { get; }

    /// <summary>
    /// Reads multiple block ranges in a single batch operation.
    /// </summary>
    /// <param name="requests">
    /// The list of block ranges to read. Each range specifies its own block number,
    /// count, and destination buffer. The list must not exceed <see cref="MaxBatchSize"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The number of operations completed successfully from the beginning of the list.
    /// A return value less than <paramref name="requests"/>.Count indicates partial success.
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// Thrown when <paramref name="requests"/> contains more than <see cref="MaxBatchSize"/> entries.
    /// </exception>
    Task<int> ReadBatchAsync(IReadOnlyList<BlockRange> requests, CancellationToken ct = default);

    /// <summary>
    /// Writes multiple block ranges in a single batch operation.
    /// </summary>
    /// <param name="requests">
    /// The list of write requests. Each request specifies its own block number,
    /// count, and source data. The list must not exceed <see cref="MaxBatchSize"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The number of operations completed successfully from the beginning of the list.
    /// A return value less than <paramref name="requests"/>.Count indicates partial success.
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// Thrown when <paramref name="requests"/> contains more than <see cref="MaxBatchSize"/> entries.
    /// </exception>
    Task<int> WriteBatchAsync(IReadOnlyList<WriteRequest> requests, CancellationToken ct = default);
}
