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
        throw new PlatformNotSupportedException(
            $"Linux MTD ioctl (MEMERASE) not available for '{_devicePath}'. " +
            "Requires Linux with MTD subsystem and P/Invoke bindings for ioctl syscall.");
    }

    public Task ReadPageAsync(long blockNumber, int pageOffset, Memory<byte> buffer, CancellationToken ct = default)
    {
        throw new PlatformNotSupportedException(
            $"Linux MTD pread not available for '{_devicePath}'. " +
            "Requires Linux with MTD subsystem and P/Invoke bindings.");
    }

    public Task WritePageAsync(long blockNumber, int pageOffset, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        throw new PlatformNotSupportedException(
            $"Linux MTD pwrite not available for '{_devicePath}'. " +
            "Requires Linux with MTD subsystem and P/Invoke bindings.");
    }

    public Task<bool> IsBlockBadAsync(long blockNumber, CancellationToken ct = default)
    {
        throw new PlatformNotSupportedException(
            $"Linux MTD ioctl (MEMGETBADBLOCK) not available for '{_devicePath}'. " +
            "Requires Linux with MTD subsystem and P/Invoke bindings.");
    }

    public Task MarkBlockBadAsync(long blockNumber, CancellationToken ct = default)
    {
        throw new PlatformNotSupportedException(
            $"Linux MTD ioctl (MEMSETBADBLOCK) not available for '{_devicePath}'. " +
            "Requires Linux with MTD subsystem and P/Invoke bindings.");
    }

    public ValueTask DisposeAsync()
    {
        // Stub: real implementation would close the MTD file handle here
        return ValueTask.CompletedTask;
    }
}
