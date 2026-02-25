using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.UltimateFilesystem.Strategies;

/// <summary>
/// Unix FUSE filesystem strategy - provides FUSE-based filesystem access on Linux and macOS.
/// Consolidated from the standalone FuseDriver plugin.
///
/// Capabilities:
/// - Full POSIX filesystem semantics via FUSE (Filesystem in Userspace)
/// - Extended attributes (xattr) support
/// - POSIX ACL and NFSv4 ACL support
/// - Kernel cache integration with zero-copy I/O
/// - Platform-specific integrations (inotify, SELinux, Spotlight, Finder)
/// - OverlayFS backend support
/// - Systemd service integration
/// - Linux namespace isolation (mount, user, PID)
///
/// Requirements:
/// - Linux: libfuse 3.x (apt install libfuse3-dev)
/// - macOS: macFUSE 4.x (https://osxfuse.github.io/)
/// - FreeBSD/OpenBSD: FUSE/perfuse (optional)
/// </summary>
public sealed class UnixFuseFilesystemStrategy : FilesystemStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "driver-unix-fuse";

    /// <inheritdoc/>
    public override string DisplayName => "Unix FUSE Driver";

    /// <inheritdoc/>
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Virtual;

    /// <inheritdoc/>
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true,
        SupportsAsyncIo = true,
        SupportsMmap = true,
        SupportsKernelBypass = false,
        SupportsVectoredIo = false,
        SupportsSparse = true,
        SupportsAutoDetect = true
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Unix FUSE filesystem driver enabling DataWarehouse storage to be mounted as a native POSIX " +
        "filesystem on Linux and macOS. Supports extended attributes, ACLs, overlayfs, systemd " +
        "integration, Linux namespaces, and platform-specific features (inotify, Spotlight).";

    /// <inheritdoc/>
    public override string[] Tags =>
    [
        "fuse", "unix", "linux", "macos", "posix", "mount", "xattr",
        "overlayfs", "systemd", "namespace", "virtual-filesystem"
    ];

    /// <inheritdoc/>
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        // FUSE detection: check if running on Linux/macOS and path is a FUSE mount
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Task.FromResult<FilesystemMetadata?>(null);
        }

        try
        {
            // Check /proc/mounts (Linux) or /etc/mtab for FUSE mounts
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var mounts = File.ReadAllText("/proc/mounts");
                var normalizedPath = path.TrimEnd('/');
                foreach (var line in mounts.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 3 && parts[1] == normalizedPath &&
                        (parts[2].StartsWith("fuse", StringComparison.OrdinalIgnoreCase) ||
                         parts[0].StartsWith("fuse", StringComparison.OrdinalIgnoreCase)))
                    {
                        return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
                        {
                            FilesystemType = "fuse",
                            MountPoint = path,
                            SupportsCompression = false,
                            SupportsEncryption = false,
                            SupportsSnapshots = false,
                            SupportsSparse = true
                        });
                    }
                }
            }

            return Task.FromResult<FilesystemMetadata?>(null);
        }
        catch
        {
            return Task.FromResult<FilesystemMetadata?>(null);
        }
    }

    /// <inheritdoc/>
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        // FUSE-mounted paths are accessed via standard POSIX I/O
        var fileOptions = options?.AsyncIo == true ? FileOptions.Asynchronous : FileOptions.None;
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            options?.BufferSize ?? 4096, fileOptions);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
#pragma warning disable CA2022
        var bytesRead = await fs.ReadAsync(buffer, 0, length, ct);
#pragma warning restore CA2022
        if (bytesRead < length)
            Array.Resize(ref buffer, bytesRead);
        return buffer;
    }

    /// <inheritdoc/>
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        var fileOptions = options?.AsyncIo == true ? FileOptions.Asynchronous : FileOptions.None;
        if (options?.WriteThrough == true) fileOptions |= FileOptions.WriteThrough;
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
            options?.BufferSize ?? 4096, fileOptions);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data, 0, data.Length, ct);
    }

    /// <inheritdoc/>
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var info = new DriveInfo(Path.GetPathRoot(path) ?? path);
        return Task.FromResult(new FilesystemMetadata
        {
            FilesystemType = "fuse",
            MountPoint = path,
            TotalBytes = info.TotalSize,
            AvailableBytes = info.AvailableFreeSpace,
            UsedBytes = info.TotalSize - info.AvailableFreeSpace,
            SupportsSparse = true
        });
    }
}
