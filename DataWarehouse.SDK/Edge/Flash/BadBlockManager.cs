using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Flash;

/// <summary>
/// Bad-block detection and tracking for flash devices.
/// </summary>
/// <remarks>
/// <para>
/// Manages defective blocks that fail verification or exceed error thresholds. Bad blocks
/// are marked permanently and excluded from allocation to prevent data corruption.
/// </para>
/// <para>
/// <strong>Detection:</strong> Blocks are marked bad when:
/// <list type="bullet">
///   <item><description>Factory-marked bad (detected during initialization scan)</description></item>
///   <item><description>Erase or write operations fail</description></item>
///   <item><description>ECC error rate exceeds threshold (uncorrectable errors)</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Persistence:</strong> Bad-block table should be persisted to flash (e.g., in reserved
/// blocks or out-of-band area) to survive power cycles.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Bad-block manager (EDGE-05)")]
internal sealed class BadBlockManager
{
    private readonly HashSet<long> _badBlocks = new();
    private readonly object _badBlockLock = new();
    private readonly IFlashDevice _flashDevice;

    public BadBlockManager(IFlashDevice flashDevice)
    {
        _flashDevice = flashDevice;
    }

    /// <summary>
    /// Scans all blocks to detect factory-marked bad blocks.
    /// Should be called during FTL initialization.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task ScanBadBlocksAsync(CancellationToken ct = default)
    {
        for (long block = 0; block < _flashDevice.TotalBlocks; block++)
        {
            if (await _flashDevice.IsBlockBadAsync(block, ct))
            {
                lock (_badBlockLock) { _badBlocks.Add(block); }
            }
        }
    }

    /// <summary>
    /// Marks a block as bad and persists the marking to flash.
    /// </summary>
    /// <param name="blockNumber">Block number to mark bad.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task MarkBadAsync(long blockNumber, CancellationToken ct = default)
    {
        lock (_badBlockLock) { _badBlocks.Add(blockNumber); }
        await _flashDevice.MarkBlockBadAsync(blockNumber, ct);
    }

    /// <summary>
    /// Checks if a block is marked as bad.
    /// </summary>
    /// <param name="blockNumber">Block number to check.</param>
    /// <returns>True if block is bad; otherwise false.</returns>
    public bool IsBad(long blockNumber) { lock (_badBlockLock) { return _badBlocks.Contains(blockNumber); } }

    /// <summary>
    /// Gets the list of all bad blocks.
    /// </summary>
    /// <returns>List of bad block numbers.</returns>
    public IReadOnlyList<long> GetBadBlocks() { lock (_badBlockLock) { return _badBlocks.ToList(); } }

    /// <summary>
    /// Gets the total count of bad blocks.
    /// </summary>
    public int BadBlockCount => _badBlocks.Count;
}
