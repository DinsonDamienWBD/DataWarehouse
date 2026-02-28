using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Flash;

/// <summary>
/// Flash Translation Layer implementation with wear-leveling, bad-block management, and garbage collection.
/// </summary>
/// <remarks>
/// <para>
/// FTL sits between block device interface and raw flash hardware, managing flash-specific concerns:
/// erase-before-write requirements, wear leveling, bad blocks, and space reclamation.
/// </para>
/// <para>
/// <strong>Write Amplification Factor (WAF):</strong> Calculated as total physical writes / logical writes.
/// Good FTL achieves WAF &lt;2.0 under mixed workloads. High WAF indicates excessive garbage collection
/// or poor wear-leveling efficiency.
/// </para>
/// <para>
/// <strong>Garbage Collection:</strong> Reclaims blocks containing invalidated pages (overwritten data).
/// Triggered manually via GarbageCollectAsync() or automatically when free space falls below threshold
/// (implementation-defined, typically 10-20% of total blocks).
/// </para>
/// <para>
/// <strong>Over-Provisioning:</strong> FTL reserves additional capacity (e.g., 7-28%) to ensure free
/// blocks are always available for writes and GC operations. UsableBlocks reflects this reserve.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: FTL implementation (EDGE-05)")]
public sealed class FlashTranslationLayer : IFlashTranslationLayer
{
    private readonly IFlashDevice _flashDevice;
    private readonly WearLevelingStrategy _wearLeveling;
    private readonly BadBlockManager _badBlockManager;
    private readonly Dictionary<long, long> _logicalToPhysical = new(); // Logical block -> physical block
    private readonly HashSet<long> _freeBlocks = new();
    private readonly HashSet<long> _dirtyBlocks = new(); // Blocks with invalidated pages
    private readonly object _ftlLock = new(); // Guards L2P map, free/dirty sets
    private long _writeCount;
    private long _eraseCount;

    /// <summary>
    /// Gets the logical block size exposed to upper layers.
    /// </summary>
    public int BlockSize => 4096; // 4KB logical blocks

    /// <summary>
    /// Gets the number of logical blocks available to the system.
    /// </summary>
    public long BlockCount { get; }

    /// <summary>
    /// Gets the physical erase block size of the underlying flash device.
    /// </summary>
    public int EraseBlockSize => _flashDevice.EraseBlockSize;

    /// <summary>
    /// Gets the total number of physical blocks in the flash device.
    /// </summary>
    public long TotalBlocks => _flashDevice.TotalBlocks;

    /// <summary>
    /// Gets the number of usable physical blocks (total minus bad blocks).
    /// </summary>
    public long UsableBlocks => TotalBlocks - _badBlockManager.BadBlockCount;

    /// <summary>
    /// Gets the count of bad blocks detected.
    /// </summary>
    public int BadBlockCount => _badBlockManager.BadBlockCount;

    /// <summary>
    /// Gets the write amplification factor (total writes / logical writes).
    /// Lower is better; ideal is 1.0, typical FTL achieves &lt;2.0.
    /// </summary>
    public double WriteAmplificationFactor => _eraseCount == 0 ? 0 : (double)_writeCount / _eraseCount;

    /// <summary>
    /// Initializes a new FTL instance.
    /// </summary>
    /// <param name="flashDevice">Underlying flash device.</param>
    /// <param name="logicalBlockCount">Number of logical blocks to expose.</param>
    public FlashTranslationLayer(IFlashDevice flashDevice, long logicalBlockCount)
    {
        _flashDevice = flashDevice;
        _wearLeveling = new WearLevelingStrategy();
        _badBlockManager = new BadBlockManager(flashDevice);
        BlockCount = logicalBlockCount;
    }

    /// <summary>
    /// Initializes the FTL by scanning bad blocks and building free block pool.
    /// Must be called before performing I/O operations.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _badBlockManager.ScanBadBlocksAsync(ct);

        // Initialize free blocks (all physical blocks minus bad blocks)
        for (long i = 0; i < _flashDevice.TotalBlocks; i++)
        {
            if (!_badBlockManager.IsBad(i))
                _freeBlocks.Add(i);
        }
    }

    /// <summary>
    /// Reads a logical block from the flash device.
    /// </summary>
    /// <param name="blockNumber">Logical block number to read.</param>
    /// <param name="buffer">Buffer to receive block data. Must be at least BlockSize bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ReadBlockAsync(long blockNumber, Memory<byte> buffer, CancellationToken ct = default)
    {
        if (blockNumber < 0 || blockNumber >= BlockCount)
            throw new ArgumentOutOfRangeException(nameof(blockNumber));

        if (buffer.Length < BlockSize)
            throw new ArgumentException("Buffer too small", nameof(buffer));

        long physicalBlock;
        lock (_ftlLock)
        {
            if (!_logicalToPhysical.TryGetValue(blockNumber, out physicalBlock))
            {
                buffer.Span.Clear(); // Unwritten block returns zeros
                return;
            }
        }

        await _flashDevice.ReadPageAsync(physicalBlock, 0, buffer, ct);
    }

    /// <summary>
    /// Writes a logical block to the flash device.
    /// Allocates new physical block, erases it, and writes data.
    /// </summary>
    /// <param name="blockNumber">Logical block number to write.</param>
    /// <param name="data">Data to write. Must be exactly BlockSize bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteBlockAsync(long blockNumber, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (blockNumber < 0 || blockNumber >= BlockCount)
            throw new ArgumentOutOfRangeException(nameof(blockNumber));

        if (data.Length != BlockSize)
            throw new ArgumentException($"Data must be exactly {BlockSize} bytes", nameof(data));

        long newPhysical;
        lock (_ftlLock)
        {
            // Flash write requires erase-before-write. If block is already mapped, mark old physical block dirty.
            if (_logicalToPhysical.TryGetValue(blockNumber, out var oldPhysical))
            {
                _dirtyBlocks.Add(oldPhysical);
            }
        }

        // Check if GC needed (low free space)
        bool needsGc;
        lock (_ftlLock) { needsGc = _freeBlocks.Count < TotalBlocks * 0.1; }
        if (needsGc)
        {
            await GarbageCollectAsync(ct);
        }

        lock (_ftlLock)
        {
            if (_freeBlocks.Count == 0)
            {
                throw new InvalidOperationException("No free blocks available. Garbage collection failed to reclaim space.");
            }

            // Allocate new physical block via wear-leveling
            newPhysical = _wearLeveling.SelectBlockForWrite(_freeBlocks);
            _freeBlocks.Remove(newPhysical);
        }

        try
        {
            // Erase block before write (NAND requirement)
            await _flashDevice.EraseBlockAsync(newPhysical, ct);
            Interlocked.Increment(ref _eraseCount);

            // Write data
            await _flashDevice.WritePageAsync(newPhysical, 0, data, ct);
            Interlocked.Increment(ref _writeCount);

            lock (_ftlLock)
            {
                _logicalToPhysical[blockNumber] = newPhysical;
            }
        }
        catch (Exception)
        {
            // Write failed -- mark block bad
            await _badBlockManager.MarkBadAsync(newPhysical, ct);
            throw;
        }
    }

    /// <summary>
    /// Flushes any buffered writes to persistent storage.
    /// FTL writes are immediately persistent; this is a no-op.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Triggers garbage collection to reclaim dirty blocks.
    /// Erases dirty blocks and returns them to free pool.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task GarbageCollectAsync(CancellationToken ct = default)
    {
        // Reclaim dirty blocks: erase and return to free pool
        foreach (var dirtyBlock in _dirtyBlocks.ToList())
        {
            try
            {
                await _flashDevice.EraseBlockAsync(dirtyBlock, ct);
                _eraseCount++;
                _dirtyBlocks.Remove(dirtyBlock);
                _freeBlocks.Add(dirtyBlock);
            }
            catch (Exception)
            {
                // Erase failed -- mark block bad
                await _badBlockManager.MarkBadAsync(dirtyBlock, ct);
                _dirtyBlocks.Remove(dirtyBlock);
            }
        }
    }

    /// <summary>
    /// Gets the list of bad block numbers.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of physical block numbers marked as bad.</returns>
    public Task<IReadOnlyList<int>> GetBadBlocksAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<int>>(_badBlockManager.GetBadBlocks().Select(b => (int)b).ToList());

    /// <summary>
    /// Disposes the FTL and underlying flash device.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _flashDevice.DisposeAsync();
    }
}
