using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine;

namespace DataWarehouse.SDK.Edge.Flash;

/// <summary>
/// Flash Translation Layer interface extending IBlockDevice with flash-specific operations.
/// </summary>
/// <remarks>
/// <para>
/// FTL provides wear-leveling, bad-block management, and garbage collection for raw NAND/NOR flash storage.
/// Extends IBlockDevice to enable use with VDE while exposing flash-specific capabilities.
/// </para>
/// <para>
/// <strong>Wear Leveling:</strong> Distributes erase cycles evenly across physical blocks to maximize
/// flash device lifespan (typically 10K-100K erase cycles per block).
/// </para>
/// <para>
/// <strong>Bad Blocks:</strong> Tracks and skips defective blocks that fail verification or exceed
/// error thresholds. Bad blocks are marked permanently and excluded from allocation.
/// </para>
/// <para>
/// <strong>Garbage Collection:</strong> Reclaims blocks containing invalidated pages (overwritten data).
/// GC erases dirty blocks and returns them to the free pool. Triggered when free space is low.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Flash translation layer interface (EDGE-05)")]
public interface IFlashTranslationLayer : IBlockDevice
{
    /// <summary>
    /// Gets the physical erase block size in bytes (e.g., 128KB, 256KB).
    /// Flash devices must be erased at erase-block granularity before rewriting.
    /// </summary>
    int EraseBlockSize { get; }

    /// <summary>
    /// Gets the total number of physical erase blocks in the flash device.
    /// </summary>
    long TotalBlocks { get; }

    /// <summary>
    /// Gets the number of usable blocks (total blocks minus bad blocks).
    /// </summary>
    long UsableBlocks { get; }

    /// <summary>
    /// Gets the count of bad blocks detected and marked.
    /// </summary>
    int BadBlockCount { get; }

    /// <summary>
    /// Gets the write amplification factor (total writes / user writes).
    /// Ideal is 1.0; typical FTL achieves &lt;2.0 under mixed workloads.
    /// </summary>
    double WriteAmplificationFactor { get; }

    /// <summary>
    /// Triggers garbage collection to reclaim invalidated blocks.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task GarbageCollectAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the list of bad block numbers.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of physical block numbers marked as bad.</returns>
    Task<IReadOnlyList<int>> GetBadBlocksAsync(CancellationToken ct = default);
}
