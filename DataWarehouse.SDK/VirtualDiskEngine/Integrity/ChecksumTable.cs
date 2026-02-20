using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

/// <summary>
/// On-disk checksum storage: maps block numbers to XxHash3 checksums.
/// Provides caching and dirty tracking for efficient checksum management.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-04 Checksumming)")]
public sealed class ChecksumTable : IAsyncDisposable
{
    private const int ChecksumSize = 8; // ulong = 8 bytes
    private const int MaxCachedBlocks = 256; // Cache up to 256 checksum table blocks
    private const int MaxCacheEntries = MaxCachedBlocks; // Simplified for now

    private readonly IBlockDevice _device;
    private readonly long _checksumTableStartBlock;
    private readonly long _checksumTableBlockCount;
    private readonly long _totalDataBlocks;
    private readonly int _checksumsPerBlock;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Cache of checksum table blocks (key: checksum table block number, value: block data)
    private readonly BoundedDictionary<long, byte[]> _blockCache = new BoundedDictionary<long, byte[]>(1000);

    // Dirty checksum table blocks that need to be flushed
    private readonly HashSet<long> _dirtyBlocks = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new checksum table.
    /// </summary>
    /// <param name="device">Block device for storage.</param>
    /// <param name="checksumTableStartBlock">Starting block of checksum table.</param>
    /// <param name="checksumTableBlockCount">Number of blocks in checksum table.</param>
    /// <param name="totalDataBlocks">Total number of data blocks to track.</param>
    public ChecksumTable(IBlockDevice device, long checksumTableStartBlock, long checksumTableBlockCount, long totalDataBlocks)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(checksumTableBlockCount, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(totalDataBlocks, 0);

        _device = device;
        _checksumTableStartBlock = checksumTableStartBlock;
        _checksumTableBlockCount = checksumTableBlockCount;
        _totalDataBlocks = totalDataBlocks;
        _checksumsPerBlock = device.BlockSize / ChecksumSize;

        // Validate that checksum table is large enough
        long requiredBlocks = ComputeRequiredBlocks(totalDataBlocks, device.BlockSize);
        if (checksumTableBlockCount < requiredBlocks)
        {
            throw new ArgumentException($"Checksum table too small: need {requiredBlocks} blocks, got {checksumTableBlockCount}");
        }
    }

    /// <summary>
    /// Gets the checksum for a data block.
    /// </summary>
    /// <param name="blockNumber">Data block number (0-based).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Stored checksum value.</returns>
    public async Task<ulong> GetChecksumAsync(long blockNumber, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockNumber, _totalDataBlocks);

        // Calculate which checksum table block contains this entry
        long checksumTableBlock = blockNumber / _checksumsPerBlock;
        int offset = (int)(blockNumber % _checksumsPerBlock) * ChecksumSize;

        // Get checksum table block (from cache or disk)
        byte[] blockData = await GetChecksumTableBlockAsync(checksumTableBlock, ct);

        // Extract checksum
        return BinaryPrimitives.ReadUInt64LittleEndian(blockData.AsSpan(offset, ChecksumSize));
    }

    /// <summary>
    /// Sets the checksum for a data block.
    /// </summary>
    /// <param name="blockNumber">Data block number (0-based).</param>
    /// <param name="checksum">Checksum value to store.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SetChecksumAsync(long blockNumber, ulong checksum, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockNumber, _totalDataBlocks);

        // Calculate which checksum table block contains this entry
        long checksumTableBlock = blockNumber / _checksumsPerBlock;
        int offset = (int)(blockNumber % _checksumsPerBlock) * ChecksumSize;

        await _lock.WaitAsync(ct);
        try
        {
            // Get checksum table block (from cache or disk)
            byte[] blockData = await GetChecksumTableBlockAsync(checksumTableBlock, ct);

            // Update checksum in memory
            BinaryPrimitives.WriteUInt64LittleEndian(blockData.AsSpan(offset, ChecksumSize), checksum);

            // Mark as dirty
            _dirtyBlocks.Add(checksumTableBlock);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Flushes all dirty checksum table blocks to disk.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lock.WaitAsync(ct);
        try
        {
            foreach (long checksumTableBlock in _dirtyBlocks)
            {
                if (_blockCache.TryGetValue(checksumTableBlock, out byte[]? blockData))
                {
                    long absoluteBlock = _checksumTableStartBlock + checksumTableBlock;
                    await _device.WriteBlockAsync(absoluteBlock, blockData, ct);
                }
            }

            _dirtyBlocks.Clear();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Computes the number of checksum table blocks required for a given number of data blocks.
    /// </summary>
    /// <param name="totalDataBlocks">Total number of data blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>Number of checksum table blocks required.</returns>
    public static long ComputeRequiredBlocks(long totalDataBlocks, int blockSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(totalDataBlocks, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, ChecksumSize);

        int checksumsPerBlock = blockSize / ChecksumSize;
        return (totalDataBlocks + checksumsPerBlock - 1) / checksumsPerBlock;
    }

    /// <summary>
    /// Gets a checksum table block from cache or reads from disk.
    /// </summary>
    private async Task<byte[]> GetChecksumTableBlockAsync(long checksumTableBlock, CancellationToken ct)
    {
        // Check cache first
        if (_blockCache.TryGetValue(checksumTableBlock, out byte[]? cached))
        {
            return cached;
        }

        // Read from disk
        byte[] blockData = new byte[_device.BlockSize];
        long absoluteBlock = _checksumTableStartBlock + checksumTableBlock;
        await _device.ReadBlockAsync(absoluteBlock, blockData, ct);

        // Add to cache (with eviction if needed)
        if (_blockCache.Count >= MaxCacheEntries)
        {
            // Evict oldest entry (simplified: just evict first entry)
            // In production, use LRU or similar
            foreach (var key in _blockCache.Keys)
            {
                if (_blockCache.TryRemove(key, out _))
                {
                    break;
                }
            }
        }

        _blockCache[checksumTableBlock] = blockData;
        return blockData;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Flush dirty blocks before disposing
        await FlushAsync();

        _lock.Dispose();
    }
}
