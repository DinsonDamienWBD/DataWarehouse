using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO;

/// <summary>
/// Configuration options for <see cref="BlockDeviceFactory"/> controlling which
/// block device implementation to use and platform-specific tuning parameters.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "VOPT-79: Block device factory configuration")]
public sealed class BlockDeviceOptions
{
    /// <summary>
    /// Gets or sets whether to prefer direct I/O when available.
    /// When <c>true</c> and no platform-optimized backend is available,
    /// the factory will use <see cref="DirectFileBlockDevice"/> instead of
    /// <see cref="FileBlockDevice"/>.
    /// </summary>
    public bool DirectIo { get; set; }

    /// <summary>
    /// Gets or sets a forced implementation override. When non-null, the factory
    /// bypasses auto-detection and uses this implementation directly.
    /// </summary>
    public BlockDeviceImplementation? ForceImplementation { get; set; }

    /// <summary>
    /// Gets or sets the submission queue depth for io_uring, IoRing, or SPDK backends.
    /// Higher values allow more concurrent I/O operations but consume more kernel resources.
    /// </summary>
    public int QueueDepth { get; set; } = 64;

    /// <summary>
    /// Gets or sets whether to enable kernel-side submission queue polling (io_uring SQPOLL).
    /// When <c>true</c>, the kernel polls the submission queue without requiring syscalls,
    /// reducing latency at the cost of dedicated CPU. Only effective with io_uring backend.
    /// </summary>
    public bool UseSqPoll { get; set; }

    /// <summary>
    /// Gets or sets the number of pre-registered buffers for zero-copy I/O.
    /// Registered buffers avoid per-operation page pinning overhead in io_uring and IoRing.
    /// </summary>
    public int RegisteredBufferCount { get; set; } = 256;

    /// <summary>
    /// Gets or sets whether to allow opening raw partitions that do not contain a DWVD signature.
    /// When <c>false</c> (default), <see cref="RawPartitionBlockDevice"/> throws
    /// <see cref="System.InvalidOperationException"/> if the DWVD magic bytes are not found
    /// at the expected superblock location. Set to <c>true</c> to bypass this safety check.
    /// <strong>WARNING:</strong> Setting this to <c>true</c> on a non-DWVD partition risks
    /// irreversible data destruction.
    /// </summary>
    public bool AllowNonDwvd { get; set; }
}

/// <summary>
/// Enumerates the available block device implementations.
/// Used by <see cref="BlockDeviceOptions.ForceImplementation"/> to override auto-detection.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "VOPT-79: Block device implementation selection")]
public enum BlockDeviceImplementation
{
    /// <summary>
    /// Standard file-backed block device using RandomAccess API with OS page cache.
    /// Available on all platforms. The baseline fallback.
    /// </summary>
    FileBlockDevice = 0,

    /// <summary>
    /// Direct file I/O bypassing OS page cache via FILE_FLAG_NO_BUFFERING (Windows)
    /// or WriteThrough (cross-platform best-effort).
    /// </summary>
    DirectFile = 1,

    /// <summary>
    /// Linux io_uring-based block device with kernel-managed async I/O.
    /// Provides the lowest latency on Linux 5.1+ kernels.
    /// </summary>
    IoUring = 2,

    /// <summary>
    /// Windows IoRing-based block device (Windows 11+).
    /// Provides kernel-batched I/O with reduced syscall overhead.
    /// </summary>
    IoRing = 3,

    /// <summary>
    /// Windows overlapped I/O block device using completion ports.
    /// Available on Windows 10+ with good async I/O performance.
    /// </summary>
    WindowsOverlapped = 4,

    /// <summary>
    /// macOS/FreeBSD kqueue-based block device with F_NOCACHE.
    /// </summary>
    Kqueue = 5,

    /// <summary>
    /// SPDK (Storage Performance Development Kit) user-space NVMe driver.
    /// Provides the highest possible throughput by bypassing the kernel entirely.
    /// </summary>
    Spdk = 6,

    /// <summary>
    /// Raw partition access without filesystem overhead.
    /// Requires elevated privileges.
    /// </summary>
    RawPartition = 7
}
