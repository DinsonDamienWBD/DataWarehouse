using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Flash;

/// <summary>
/// Raw flash device abstraction for direct NAND/NOR access.
/// </summary>
/// <remarks>
/// Provides low-level access to flash erase blocks and pages. Production implementations
/// require platform-specific integration with Linux MTD (/dev/mtdX) or vendor SDKs.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Raw flash device abstraction (EDGE-05)")]
public interface IFlashDevice : IAsyncDisposable
{
    /// <summary>
    /// Gets the physical erase block size in bytes.
    /// </summary>
    int EraseBlockSize { get; }

    /// <summary>
    /// Gets the total number of physical blocks in the flash device.
    /// </summary>
    long TotalBlocks { get; }

    /// <summary>
    /// Erases a physical block, setting all bits to 1 (0xFF pattern).
    /// Required before writing to a block in NAND flash.
    /// </summary>
    /// <param name="blockNumber">Physical block number to erase.</param>
    /// <param name="ct">Cancellation token.</param>
    Task EraseBlockAsync(long blockNumber, CancellationToken ct = default);

    /// <summary>
    /// Reads a page from a physical block.
    /// </summary>
    /// <param name="blockNumber">Physical block number.</param>
    /// <param name="pageOffset">Page offset within the block.</param>
    /// <param name="buffer">Buffer to receive page data.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReadPageAsync(long blockNumber, int pageOffset, Memory<byte> buffer, CancellationToken ct = default);

    /// <summary>
    /// Writes a page to a physical block. Block must be erased before writing.
    /// </summary>
    /// <param name="blockNumber">Physical block number.</param>
    /// <param name="pageOffset">Page offset within the block.</param>
    /// <param name="data">Data to write.</param>
    /// <param name="ct">Cancellation token.</param>
    Task WritePageAsync(long blockNumber, int pageOffset, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>
    /// Checks if a block is marked as bad.
    /// </summary>
    /// <param name="blockNumber">Physical block number to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if block is bad; otherwise false.</returns>
    Task<bool> IsBlockBadAsync(long blockNumber, CancellationToken ct = default);

    /// <summary>
    /// Marks a block as bad (permanently defective).
    /// </summary>
    /// <param name="blockNumber">Physical block number to mark bad.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MarkBlockBadAsync(long blockNumber, CancellationToken ct = default);
}

/// <summary>
/// Linux MTD flash device implementation (stub).
/// </summary>
/// <remarks>
/// Stub implementation demonstrating API contract. Production use requires Linux MTD ioctl bindings
/// (MEMGETINFO, MEMERASE, MEMGETBADBLOCK, MEMSETBADBLOCK via &lt;mtd/mtd-user.h&gt;).
/// Full implementation requires platform-specific P/Invoke for ioctl system calls.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Linux MTD flash device stub (EDGE-05)")]
internal sealed class LinuxMtdFlashDevice : IFlashDevice
{
    private readonly string _devicePath; // /dev/mtd0
    private readonly int _eraseBlockSize;
    private readonly long _totalBlocks;

    public LinuxMtdFlashDevice(string devicePath, int eraseBlockSize = 131072, long totalBlocks = 1024)
    {
        _devicePath = devicePath;
        _eraseBlockSize = eraseBlockSize;
        _totalBlocks = totalBlocks;

        // Stub: Open /dev/mtdX with O_RDWR | O_SYNC
        // Query MEMGETINFO ioctl for erase block size, total size
        // Implementation requires Linux-specific P/Invoke
        // For stub, accept parameters directly
    }

    public int EraseBlockSize => _eraseBlockSize;
    public long TotalBlocks => _totalBlocks;

    public Task EraseBlockAsync(long blockNumber, CancellationToken ct = default)
    {
        // Stub: MEMERASE ioctl
        // Real: ioctl(_handle, MEMERASE, &erase_info)
        return Task.CompletedTask;
    }

    public Task ReadPageAsync(long blockNumber, int pageOffset, Memory<byte> buffer, CancellationToken ct = default)
    {
        // Stub: pread at block offset + page offset
        // Real: pread(_handle, buffer, size, block * _eraseBlockSize + pageOffset)
        buffer.Span.Clear();
        return Task.CompletedTask;
    }

    public Task WritePageAsync(long blockNumber, int pageOffset, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        // Stub: pwrite at block offset + page offset
        // Real: pwrite(_handle, data, size, block * _eraseBlockSize + pageOffset)
        return Task.CompletedTask;
    }

    public Task<bool> IsBlockBadAsync(long blockNumber, CancellationToken ct = default)
    {
        // Stub: MEMGETBADBLOCK ioctl
        // Real: ioctl(_handle, MEMGETBADBLOCK, &block_offset) returns 0 (good) or 1 (bad)
        return Task.FromResult(false);
    }

    public Task MarkBlockBadAsync(long blockNumber, CancellationToken ct = default)
    {
        // Stub: MEMSETBADBLOCK ioctl
        // Real: ioctl(_handle, MEMSETBADBLOCK, &block_offset)
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        // Stub: real implementation would close the MTD file handle here
        return ValueTask.CompletedTask;
    }
}
