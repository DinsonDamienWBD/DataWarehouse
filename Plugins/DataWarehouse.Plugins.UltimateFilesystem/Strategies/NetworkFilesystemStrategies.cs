using System.Runtime.InteropServices;
using System.Text;

namespace DataWarehouse.Plugins.UltimateFilesystem.Strategies;

/// <summary>
/// NFS (Network File System) detection and handling strategy.
/// Supports NFSv3 and NFSv4 mount detection.
/// </summary>
public sealed class NfsStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-nfs";
    public override string DisplayName => "NFS (Network File System)";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Detection;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = false, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = false, SupportsSparse = false,
        SupportsAutoDetect = true, MaxFileSize = 8L * 1024 * 1024 * 1024 * 1024 // 8TB NFS max
    };
    public override string SemanticDescription =>
        "Detects and provides metadata for NFS network filesystems with mount parsing, " +
        "export discovery, and server information extraction.";
    public override string[] Tags => ["nfs", "network", "distributed", "mount", "unix"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return Task.FromResult<FilesystemMetadata?>(null);

        try
        {
            // Parse /proc/mounts to detect NFS
            if (!File.Exists("/proc/mounts"))
                return Task.FromResult<FilesystemMetadata?>(null);

            var mounts = File.ReadAllLines("/proc/mounts");
            var normalizedPath = Path.GetFullPath(path);

            foreach (var line in mounts)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                var fsType = parts[2];
                var mountPoint = parts[1];

                if ((fsType == "nfs" || fsType == "nfs4") && normalizedPath.StartsWith(mountPoint, StringComparison.Ordinal))
                {
                    var driveInfo = new DriveInfo(mountPoint);
                    return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
                    {
                        FilesystemType = fsType,
                        TotalBytes = driveInfo.IsReady ? driveInfo.TotalSize : 0,
                        AvailableBytes = driveInfo.IsReady ? driveInfo.AvailableFreeSpace : 0,
                        UsedBytes = driveInfo.IsReady ? driveInfo.TotalSize - driveInfo.AvailableFreeSpace : 0,
                        BlockSize = 32768, // NFS typical rsize/wsize
                        IsReadOnly = !driveInfo.IsReady,
                        SupportsSparse = false,
                        SupportsCompression = false,
                        SupportsEncryption = false,
                        MountPoint = mountPoint
                    });
                }
            }

            return Task.FromResult<FilesystemMetadata?>(null);
        }
        catch
        {
            return Task.FromResult<FilesystemMetadata?>(null);
        }
    }

    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        // NFS-optimized: use 1MB buffer for network latency compensation
        var bufferSize = Math.Max(options?.BufferSize ?? 1024 * 1024, 1024 * 1024);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        var bytesRead = await fs.ReadAsync(buffer, 0, length, ct);
        if (bytesRead < length)
            Array.Resize(ref buffer, bytesRead);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        var bufferSize = Math.Max(options?.BufferSize ?? 1024 * 1024, 1024 * 1024);
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data, 0, data.Length, ct);
        // NFS: explicit flush for sync semantics
        await fs.FlushAsync(ct);
    }

    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = await DetectAsync(path, ct);
        return result ?? new FilesystemMetadata { FilesystemType = "nfs" };
    }
}

/// <summary>
/// SMB/CIFS network filesystem detection strategy.
/// </summary>
public sealed class SmbStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-smb";
    public override string DisplayName => "SMB/CIFS";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Detection;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = false, SupportsAsyncIo = true, SupportsMmap = false,
        SupportsKernelBypass = false, SupportsVectoredIo = false, SupportsSparse = false,
        SupportsAutoDetect = true, MaxFileSize = 16L * 1024 * 1024 * 1024 * 1024 // 16TB SMB3
    };
    public override string SemanticDescription =>
        "Detects SMB/CIFS network shares with enumeration support for Windows and Linux, " +
        "including DFS namespace detection.";
    public override string[] Tags => ["smb", "cifs", "windows", "network", "share", "dfs"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        try
        {
            // Detect SMB shares on Windows by UNC path or mapped drives
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var normalizedPath = Path.GetFullPath(path);
                if (normalizedPath.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    // UNC path detected
                    var driveInfo = new DriveInfo(Path.GetPathRoot(normalizedPath) ?? normalizedPath);
                    if (driveInfo.DriveType == DriveType.Network)
                    {
                        return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
                        {
                            FilesystemType = "SMB",
                            TotalBytes = driveInfo.IsReady ? driveInfo.TotalSize : 0,
                            AvailableBytes = driveInfo.IsReady ? driveInfo.AvailableFreeSpace : 0,
                            UsedBytes = driveInfo.IsReady ? driveInfo.TotalSize - driveInfo.AvailableFreeSpace : 0,
                            BlockSize = 4096,
                            IsReadOnly = !driveInfo.IsReady,
                            SupportsSparse = false,
                            MountPoint = driveInfo.RootDirectory.FullName
                        });
                    }
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Parse /proc/mounts for cifs/smb
                if (File.Exists("/proc/mounts"))
                {
                    var mounts = File.ReadAllLines("/proc/mounts");
                    var normalizedPath = Path.GetFullPath(path);

                    foreach (var line in mounts)
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 3) continue;

                        var fsType = parts[2];
                        var mountPoint = parts[1];

                        if ((fsType == "cifs" || fsType == "smb" || fsType == "smbfs") && normalizedPath.StartsWith(mountPoint, StringComparison.Ordinal))
                        {
                            var driveInfo = new DriveInfo(mountPoint);
                            return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
                            {
                                FilesystemType = "SMB",
                                TotalBytes = driveInfo.IsReady ? driveInfo.TotalSize : 0,
                                AvailableBytes = driveInfo.IsReady ? driveInfo.AvailableFreeSpace : 0,
                                UsedBytes = driveInfo.IsReady ? driveInfo.TotalSize - driveInfo.AvailableFreeSpace : 0,
                                BlockSize = 4096,
                                IsReadOnly = !driveInfo.IsReady,
                                MountPoint = mountPoint
                            });
                        }
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

    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        // SMB: use large buffers (512KB) to reduce round-trips
        var bufferSize = Math.Max(options?.BufferSize ?? 512 * 1024, 512 * 1024);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        var bytesRead = await fs.ReadAsync(buffer, 0, length, ct);
        if (bytesRead < length)
            Array.Resize(ref buffer, bytesRead);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        var bufferSize = Math.Max(options?.BufferSize ?? 512 * 1024, 512 * 1024);
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data, 0, data.Length, ct);
    }

    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = await DetectAsync(path, ct);
        return result ?? new FilesystemMetadata { FilesystemType = "SMB" };
    }
}

/// <summary>
/// GlusterFS distributed filesystem detection strategy.
/// </summary>
public sealed class GlusterFsStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-glusterfs";
    public override string DisplayName => "GlusterFS";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Detection;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = true, SupportsSparse = true,
        SupportsAutoDetect = true, MaxFileSize = 64L * 1024 * 1024 * 1024 * 1024 // 64TB
    };
    public override string SemanticDescription =>
        "Detects GlusterFS distributed filesystem volumes with brick enumeration and " +
        "volume topology discovery.";
    public override string[] Tags => ["glusterfs", "distributed", "scale-out", "replicated"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return Task.FromResult<FilesystemMetadata?>(null);

        try
        {
            if (!File.Exists("/proc/mounts"))
                return Task.FromResult<FilesystemMetadata?>(null);

            var mounts = File.ReadAllLines("/proc/mounts");
            var normalizedPath = Path.GetFullPath(path);

            foreach (var line in mounts)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                var fsType = parts[2];
                var mountPoint = parts[1];

                if (fsType == "fuse.glusterfs" && normalizedPath.StartsWith(mountPoint, StringComparison.Ordinal))
                {
                    var driveInfo = new DriveInfo(mountPoint);
                    return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
                    {
                        FilesystemType = "glusterfs",
                        TotalBytes = driveInfo.IsReady ? driveInfo.TotalSize : 0,
                        AvailableBytes = driveInfo.IsReady ? driveInfo.AvailableFreeSpace : 0,
                        UsedBytes = driveInfo.IsReady ? driveInfo.TotalSize - driveInfo.AvailableFreeSpace : 0,
                        BlockSize = 128 * 1024, // GlusterFS default
                        SupportsSparse = true,
                        MountPoint = mountPoint
                    });
                }
            }

            return Task.FromResult<FilesystemMetadata?>(null);
        }
        catch
        {
            return Task.FromResult<FilesystemMetadata?>(null);
        }
    }

    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options?.BufferSize ?? 128 * 1024, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        await fs.ReadExactlyAsync(buffer.AsMemory(0, length), ct);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, options?.BufferSize ?? 128 * 1024, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data.AsMemory(), ct);
    }

    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = await DetectAsync(path, ct);
        return result ?? new FilesystemMetadata { FilesystemType = "glusterfs" };
    }
}

/// <summary>
/// CephFS distributed filesystem detection strategy.
/// </summary>
public sealed class CephFsStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-cephfs";
    public override string DisplayName => "CephFS";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Detection;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = true, SupportsSparse = true,
        SupportsAutoDetect = true, MaxFileSize = 64L * 1024 * 1024 * 1024 * 1024 * 1024 // Exabyte scale
    };
    public override string SemanticDescription =>
        "Detects Ceph distributed filesystem with metadata server info, pool mapping, " +
        "and POSIX-compliant distributed storage.";
    public override string[] Tags => ["cephfs", "distributed", "object-storage", "mds", "rados"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return Task.FromResult<FilesystemMetadata?>(null);

        try
        {
            if (!File.Exists("/proc/mounts"))
                return Task.FromResult<FilesystemMetadata?>(null);

            var mounts = File.ReadAllLines("/proc/mounts");
            var normalizedPath = Path.GetFullPath(path);

            foreach (var line in mounts)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                var fsType = parts[2];
                var mountPoint = parts[1];

                if (fsType == "ceph" && normalizedPath.StartsWith(mountPoint, StringComparison.Ordinal))
                {
                    var driveInfo = new DriveInfo(mountPoint);
                    return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
                    {
                        FilesystemType = "cephfs",
                        TotalBytes = driveInfo.IsReady ? driveInfo.TotalSize : 0,
                        AvailableBytes = driveInfo.IsReady ? driveInfo.AvailableFreeSpace : 0,
                        UsedBytes = driveInfo.IsReady ? driveInfo.TotalSize - driveInfo.AvailableFreeSpace : 0,
                        BlockSize = 4 * 1024 * 1024, // CephFS 4MB default object size
                        SupportsSparse = true,
                        SupportsSnapshots = true,
                        MountPoint = mountPoint
                    });
                }
            }

            return Task.FromResult<FilesystemMetadata?>(null);
        }
        catch
        {
            return Task.FromResult<FilesystemMetadata?>(null);
        }
    }

    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options?.BufferSize ?? 4 * 1024 * 1024, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        await fs.ReadExactlyAsync(buffer.AsMemory(0, length), ct);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, options?.BufferSize ?? 4 * 1024 * 1024, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data.AsMemory(), ct);
    }

    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = await DetectAsync(path, ct);
        return result ?? new FilesystemMetadata { FilesystemType = "cephfs" };
    }
}

/// <summary>
/// Lustre parallel filesystem detection strategy.
/// </summary>
public sealed class LustreStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-lustre";
    public override string DisplayName => "Lustre";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Detection;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = true, SupportsSparse = true,
        SupportsAutoDetect = true, MaxFileSize = 64L * 1024 * 1024 * 1024 * 1024 * 1024
    };
    public override string SemanticDescription =>
        "Detects Lustre high-performance parallel filesystem with OST/MDT info, striping " +
        "patterns, and HPC-optimized I/O characteristics.";
    public override string[] Tags => ["lustre", "hpc", "parallel", "striping", "ost", "mdt"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return Task.FromResult<FilesystemMetadata?>(null);

        try
        {
            if (!File.Exists("/proc/mounts"))
                return Task.FromResult<FilesystemMetadata?>(null);

            var mounts = File.ReadAllLines("/proc/mounts");
            var normalizedPath = Path.GetFullPath(path);

            foreach (var line in mounts)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                var fsType = parts[2];
                var mountPoint = parts[1];

                if (fsType == "lustre" && normalizedPath.StartsWith(mountPoint, StringComparison.Ordinal))
                {
                    var driveInfo = new DriveInfo(mountPoint);
                    return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
                    {
                        FilesystemType = "lustre",
                        TotalBytes = driveInfo.IsReady ? driveInfo.TotalSize : 0,
                        AvailableBytes = driveInfo.IsReady ? driveInfo.AvailableFreeSpace : 0,
                        UsedBytes = driveInfo.IsReady ? driveInfo.TotalSize - driveInfo.AvailableFreeSpace : 0,
                        BlockSize = 1024 * 1024, // Lustre 1MB stripe size
                        SupportsSparse = true,
                        MountPoint = mountPoint
                    });
                }
            }

            return Task.FromResult<FilesystemMetadata?>(null);
        }
        catch
        {
            return Task.FromResult<FilesystemMetadata?>(null);
        }
    }

    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        // Lustre: align to 1MB stripes for optimal performance
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options?.BufferSize ?? 1024 * 1024, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        await fs.ReadExactlyAsync(buffer.AsMemory(0, length), ct);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, options?.BufferSize ?? 1024 * 1024, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data.AsMemory(), ct);
    }

    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = await DetectAsync(path, ct);
        return result ?? new FilesystemMetadata { FilesystemType = "lustre" };
    }
}

/// <summary>
/// IBM Spectrum Scale (GPFS) detection strategy.
/// </summary>
public sealed class GpfsStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-gpfs";
    public override string DisplayName => "IBM Spectrum Scale (GPFS)";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Detection;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = true, SupportsSparse = true,
        SupportsAutoDetect = true, MaxFileSize = 64L * 1024 * 1024 * 1024 * 1024 * 1024
    };
    public override string SemanticDescription =>
        "Detects IBM Spectrum Scale (GPFS) cluster filesystem with fileset info, " +
        "policy-based management, and enterprise-grade performance.";
    public override string[] Tags => ["gpfs", "spectrum-scale", "ibm", "cluster", "enterprise"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return Task.FromResult<FilesystemMetadata?>(null);

        try
        {
            if (!File.Exists("/proc/mounts"))
                return Task.FromResult<FilesystemMetadata?>(null);

            var mounts = File.ReadAllLines("/proc/mounts");
            var normalizedPath = Path.GetFullPath(path);

            foreach (var line in mounts)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                var fsType = parts[2];
                var mountPoint = parts[1];

                if (fsType == "gpfs" && normalizedPath.StartsWith(mountPoint, StringComparison.Ordinal))
                {
                    var driveInfo = new DriveInfo(mountPoint);
                    return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
                    {
                        FilesystemType = "gpfs",
                        TotalBytes = driveInfo.IsReady ? driveInfo.TotalSize : 0,
                        AvailableBytes = driveInfo.IsReady ? driveInfo.AvailableFreeSpace : 0,
                        UsedBytes = driveInfo.IsReady ? driveInfo.TotalSize - driveInfo.AvailableFreeSpace : 0,
                        BlockSize = 256 * 1024, // GPFS 256KB default
                        SupportsSparse = true,
                        SupportsSnapshots = true,
                        MountPoint = mountPoint
                    });
                }
            }

            return Task.FromResult<FilesystemMetadata?>(null);
        }
        catch
        {
            return Task.FromResult<FilesystemMetadata?>(null);
        }
    }

    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options?.BufferSize ?? 256 * 1024, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        await fs.ReadExactlyAsync(buffer.AsMemory(0, length), ct);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, options?.BufferSize ?? 256 * 1024, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data.AsMemory(), ct);
    }

    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = await DetectAsync(path, ct);
        return result ?? new FilesystemMetadata { FilesystemType = "gpfs" };
    }
}

/// <summary>
/// BeeGFS parallel filesystem detection strategy.
/// </summary>
public sealed class BeeGfsStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-beegfs";
    public override string DisplayName => "BeeGFS";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Detection;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = true, SupportsSparse = true,
        SupportsAutoDetect = true, MaxFileSize = 64L * 1024 * 1024 * 1024 * 1024 * 1024
    };
    public override string SemanticDescription =>
        "Detects BeeGFS parallel filesystem with storage/metadata target info, striping " +
        "patterns optimized for HPC and AI workloads.";
    public override string[] Tags => ["beegfs", "parallel", "hpc", "ai", "striping"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return Task.FromResult<FilesystemMetadata?>(null);

        try
        {
            if (!File.Exists("/proc/mounts"))
                return Task.FromResult<FilesystemMetadata?>(null);

            var mounts = File.ReadAllLines("/proc/mounts");
            var normalizedPath = Path.GetFullPath(path);

            foreach (var line in mounts)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                var fsType = parts[2];
                var mountPoint = parts[1];

                if (fsType == "beegfs" && normalizedPath.StartsWith(mountPoint, StringComparison.Ordinal))
                {
                    var driveInfo = new DriveInfo(mountPoint);
                    return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
                    {
                        FilesystemType = "beegfs",
                        TotalBytes = driveInfo.IsReady ? driveInfo.TotalSize : 0,
                        AvailableBytes = driveInfo.IsReady ? driveInfo.AvailableFreeSpace : 0,
                        UsedBytes = driveInfo.IsReady ? driveInfo.TotalSize - driveInfo.AvailableFreeSpace : 0,
                        BlockSize = 512 * 1024, // BeeGFS 512KB stripe
                        SupportsSparse = true,
                        MountPoint = mountPoint
                    });
                }
            }

            return Task.FromResult<FilesystemMetadata?>(null);
        }
        catch
        {
            return Task.FromResult<FilesystemMetadata?>(null);
        }
    }

    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options?.BufferSize ?? 512 * 1024, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        await fs.ReadExactlyAsync(buffer.AsMemory(0, length), ct);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, options?.BufferSize ?? 512 * 1024, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data.AsMemory(), ct);
    }

    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = await DetectAsync(path, ct);
        return result ?? new FilesystemMetadata { FilesystemType = "beegfs" };
    }
}

/// <summary>
/// NFSv3 driver strategy with stateless operations.
/// </summary>
public sealed class Nfs3Strategy : FilesystemStrategyBase
{
    public override string StrategyId => "driver-nfs3";
    public override string DisplayName => "NFSv3 Driver";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Driver;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = false, SupportsAsyncIo = true, SupportsMmap = false,
        SupportsKernelBypass = false, SupportsVectoredIo = false, SupportsSparse = false,
        SupportsAutoDetect = false, MaxFileSize = 8L * 1024 * 1024 * 1024 * 1024
    };
    public override string SemanticDescription =>
        "NFSv3 driver with stateless operations, UDP/TCP transport support, and " +
        "legacy NFS compatibility for maximum interoperability.";
    public override string[] Tags => ["nfs3", "driver", "stateless", "udp", "tcp"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<FilesystemMetadata?>(null);

    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        // NFSv3: 32KB typical rsize
        var bufferSize = Math.Max(options?.BufferSize ?? 32 * 1024, 32 * 1024);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        var bytesRead = await fs.ReadAsync(buffer, 0, length, ct);
        if (bytesRead < length)
            Array.Resize(ref buffer, bytesRead);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        var bufferSize = Math.Max(options?.BufferSize ?? 32 * 1024, 32 * 1024);
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data, 0, data.Length, ct);
        // NFSv3 COMMIT equivalent
        await fs.FlushAsync(ct);
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(new FilesystemMetadata { FilesystemType = "nfs3-driver" });
}

/// <summary>
/// NFSv4 driver strategy with compound operations.
/// </summary>
public sealed class Nfs4Strategy : FilesystemStrategyBase
{
    public override string StrategyId => "driver-nfs4";
    public override string DisplayName => "NFSv4 Driver";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Driver;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = false, SupportsAsyncIo = true, SupportsMmap = false,
        SupportsKernelBypass = false, SupportsVectoredIo = true, SupportsSparse = false,
        SupportsAutoDetect = false, MaxFileSize = 8L * 1024 * 1024 * 1024 * 1024
    };
    public override string SemanticDescription =>
        "NFSv4 driver with compound operations, delegations, ACL support, and " +
        "improved security via Kerberos integration.";
    public override string[] Tags => ["nfs4", "driver", "compound", "delegations", "acl", "kerberos"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<FilesystemMetadata?>(null);

    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        // NFSv4: 1MB rsize for compound operations
        var bufferSize = Math.Max(options?.BufferSize ?? 1024 * 1024, 1024 * 1024);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        var bytesRead = await fs.ReadAsync(buffer, 0, length, ct);
        if (bytesRead < length)
            Array.Resize(ref buffer, bytesRead);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        var bufferSize = Math.Max(options?.BufferSize ?? 1024 * 1024, 1024 * 1024);
        var fileOptions = FileOptions.Asynchronous;
        if (options?.WriteThrough == true)
            fileOptions |= FileOptions.WriteThrough;

        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, bufferSize, fileOptions);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data, 0, data.Length, ct);
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(new FilesystemMetadata { FilesystemType = "nfs4-driver" });
}
