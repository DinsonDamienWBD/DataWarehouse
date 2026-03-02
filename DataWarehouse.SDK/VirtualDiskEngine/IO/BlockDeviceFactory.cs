using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Runtime.InteropServices;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO;

/// <summary>
/// Factory that auto-detects the best available <see cref="IBlockDevice"/> implementation
/// for the current platform using a 7-step detection cascade (AD-46).
/// </summary>
/// <remarks>
/// <para>
/// The cascade prioritizes platform-specific high-performance backends, falling back
/// to increasingly portable implementations:
/// </para>
/// <list type="number">
/// <item><description>Forced implementation (if specified in options)</description></item>
/// <item><description>SPDK user-space NVMe (future)</description></item>
/// <item><description>io_uring (Linux 5.1+, future: plan 87-57)</description></item>
/// <item><description>IoRing (Windows 11+, future: plan 87-58)</description></item>
/// <item><description>Windows Overlapped I/O (future: plan 87-59)</description></item>
/// <item><description>kqueue (macOS/FreeBSD, future: plan 87-60)</description></item>
/// <item><description>DirectFileBlockDevice (if DirectIo requested)</description></item>
/// <item><description>FileBlockDevice (baseline, always available)</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "VOPT-79: Auto-detecting block device factory (AD-46 cascade)")]
public static class BlockDeviceFactory
{
    /// <summary>
    /// Creates the best available <see cref="IBlockDevice"/> for the current platform.
    /// </summary>
    /// <param name="path">Path to the container file.</param>
    /// <param name="blockSize">Size of each block in bytes.</param>
    /// <param name="blockCount">Total number of blocks.</param>
    /// <param name="createNew">If <c>true</c>, creates a new file and pre-allocates space.</param>
    /// <param name="options">
    /// Optional configuration. When null, uses default auto-detection with standard file I/O.
    /// </param>
    /// <param name="logger">Optional logger for reporting which implementation was selected.</param>
    /// <returns>The best available block device implementation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="path"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when <see cref="BlockDeviceOptions.ForceImplementation"/> specifies a backend
    /// that is not yet implemented.
    /// </exception>
    public static IBlockDevice Create(
        string path,
        int blockSize,
        long blockCount,
        bool createNew,
        BlockDeviceOptions? options = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= new BlockDeviceOptions();

        // Step 1: Forced implementation override
        if (options.ForceImplementation.HasValue)
        {
            logger?.LogDebug("BlockDeviceFactory: forced implementation {Implementation}",
                options.ForceImplementation.Value);

            return options.ForceImplementation.Value switch
            {
                BlockDeviceImplementation.FileBlockDevice =>
                    new FileBlockDevice(path, blockSize, blockCount, createNew),

                BlockDeviceImplementation.DirectFile =>
                    new DirectFileBlockDevice(path, blockSize, blockCount, createNew),

                // TODO: 87-57 - IoUringBlockDevice
                BlockDeviceImplementation.IoUring =>
                    throw new NotSupportedException(
                        "IoUring block device is not yet implemented. See plan 87-57."),

                // TODO: 87-58 - IoRingBlockDevice
                BlockDeviceImplementation.IoRing =>
                    throw new NotSupportedException(
                        "IoRing block device is not yet implemented. See plan 87-58."),

                // TODO: 87-59 - WindowsOverlappedBlockDevice
                BlockDeviceImplementation.WindowsOverlapped =>
                    throw new NotSupportedException(
                        "Windows Overlapped block device is not yet implemented. See plan 87-59."),

                // TODO: 87-60 - KqueueBlockDevice
                BlockDeviceImplementation.Kqueue =>
                    throw new NotSupportedException(
                        "Kqueue block device is not yet implemented. See plan 87-60."),

                BlockDeviceImplementation.Spdk =>
                    throw new NotSupportedException(
                        "SPDK block device is not yet implemented."),

                BlockDeviceImplementation.RawPartition =>
                    throw new NotSupportedException(
                        "Raw partition block device is not yet implemented."),

                _ => throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.ForceImplementation.Value,
                    "Unknown block device implementation.")
            };
        }

        // Step 2: SPDK (user-space NVMe) -- not yet available
        // TODO: Check SPDK library availability via P/Invoke probe
        logger?.LogDebug("BlockDeviceFactory: SPDK not available, continuing cascade");

        // Step 3: io_uring (Linux 5.1+)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // TODO: 87-57 - IoUringBlockDevice: probe for io_uring support via io_uring_queue_init
            logger?.LogDebug("BlockDeviceFactory: Linux detected, io_uring not yet implemented (plan 87-57)");
        }

        // Step 4: IoRing (Windows 11+)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // TODO: 87-58 - IoRingBlockDevice: probe for IoRing via CreateIoRing API
            logger?.LogDebug("BlockDeviceFactory: Windows detected, IoRing not yet implemented (plan 87-58)");

            // Step 5: Windows Overlapped I/O (Windows 10+)
            // TODO: 87-59 - WindowsOverlappedBlockDevice
            logger?.LogDebug("BlockDeviceFactory: Windows Overlapped I/O not yet implemented (plan 87-59)");
        }

        // Step 6: kqueue (macOS/FreeBSD)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
        {
            // TODO: 87-60 - KqueueBlockDevice
            logger?.LogDebug("BlockDeviceFactory: macOS/FreeBSD detected, kqueue not yet implemented (plan 87-60)");
        }

        // Step 7: DirectFileBlockDevice (if DirectIo requested)
        if (options.DirectIo)
        {
            logger?.LogDebug("BlockDeviceFactory: using DirectFileBlockDevice (DirectIo requested)");
            return new DirectFileBlockDevice(path, blockSize, blockCount, createNew);
        }

        // Step 8: FileBlockDevice (baseline fallback, always available)
        logger?.LogDebug("BlockDeviceFactory: using FileBlockDevice (baseline fallback)");
        return new FileBlockDevice(path, blockSize, blockCount, createNew);
    }
}
