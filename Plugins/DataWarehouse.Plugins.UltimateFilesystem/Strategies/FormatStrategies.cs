using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.UltimateFilesystem.Strategies;

/// <summary>
/// FAT32 filesystem detection strategy.
/// </summary>
public sealed class Fat32Strategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-fat32";
    public override string DisplayName => "FAT32";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Format;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = false, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = false, SupportsSparse = false,
        SupportsAutoDetect = true, MaxFileSize = 4L * 1024 * 1024 * 1024 - 1 // 4GB - 1 byte limit
    };
    public override string SemanticDescription =>
        "Detects FAT32 filesystem with 4GB file size limit, 8.3 filename compatibility, " +
        "and universal device support.";
    public override string[] Tags => ["fat32", "legacy", "compatibility", "removable", "4gb-limit"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(path) ?? path);
            if (!driveInfo.DriveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<FilesystemMetadata?>(null);

            return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
            {
                FilesystemType = "FAT32",
                TotalBytes = driveInfo.TotalSize,
                AvailableBytes = driveInfo.AvailableFreeSpace,
                UsedBytes = driveInfo.TotalSize - driveInfo.AvailableFreeSpace,
                BlockSize = 4096,
                IsReadOnly = !driveInfo.IsReady,
                SupportsSparse = false,
                SupportsCompression = false,
                SupportsEncryption = false,
                MountPoint = driveInfo.RootDirectory.FullName
            });
        }
        catch
        {
            return Task.FromResult<FilesystemMetadata?>(null);
        }
    }

    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options?.BufferSize ?? 4096, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        var bytesRead = await fs.ReadAsync(buffer, 0, length, ct);
        if (bytesRead < length)
            Array.Resize(ref buffer, bytesRead);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        // Enforce 4GB file size limit
        if (offset + data.Length >= 4L * 1024 * 1024 * 1024)
            throw new IOException("FAT32 4GB file size limit exceeded");

        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, options?.BufferSize ?? 4096, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data, 0, data.Length, ct);
    }

    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = await DetectAsync(path, ct);
        return result ?? new FilesystemMetadata { FilesystemType = "FAT32" };
    }
}

/// <summary>
/// exFAT filesystem detection strategy.
/// </summary>
public sealed class ExFatStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-exfat";
    public override string DisplayName => "exFAT";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Format;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = false, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = false, SupportsSparse = false,
        SupportsAutoDetect = true, MaxFileSize = 16L * 1024 * 1024 * 1024 * 1024 * 1024 // 16 exabytes
    };
    public override string SemanticDescription =>
        "Detects exFAT filesystem optimized for flash drives with large file support, " +
        "no 4GB limit, and minimal overhead.";
    public override string[] Tags => ["exfat", "flash", "large-files", "removable", "modern"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(path) ?? path);
            if (!driveInfo.DriveFormat.Equals("exFAT", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<FilesystemMetadata?>(null);

            return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
            {
                FilesystemType = "exFAT",
                TotalBytes = driveInfo.TotalSize,
                AvailableBytes = driveInfo.AvailableFreeSpace,
                UsedBytes = driveInfo.TotalSize - driveInfo.AvailableFreeSpace,
                BlockSize = 128 * 1024, // exFAT cluster size can be large
                IsReadOnly = !driveInfo.IsReady,
                SupportsSparse = false,
                SupportsCompression = false,
                SupportsEncryption = false,
                MountPoint = driveInfo.RootDirectory.FullName
            });
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
        var bytesRead = await fs.ReadAsync(buffer, 0, length, ct);
        if (bytesRead < length)
            Array.Resize(ref buffer, bytesRead);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, options?.BufferSize ?? 128 * 1024, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data, 0, data.Length, ct);
    }

    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = await DetectAsync(path, ct);
        return result ?? new FilesystemMetadata { FilesystemType = "exFAT" };
    }
}

/// <summary>
/// F2FS (Flash-Friendly File System) detection strategy.
/// </summary>
public sealed class F2FsStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-f2fs";
    public override string DisplayName => "F2FS (Flash-Friendly File System)";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Format;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = true, SupportsSparse = true,
        SupportsAutoDetect = true, MaxFileSize = 16L * 1024 * 1024 * 1024 * 1024
    };
    public override string SemanticDescription =>
        "Detects F2FS log-structured filesystem optimized for flash storage with " +
        "wear leveling awareness and multi-head logging.";
    public override string[] Tags => ["f2fs", "flash", "ssd", "log-structured", "wear-leveling"];

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

                if (fsType == "f2fs" && normalizedPath.StartsWith(mountPoint, StringComparison.Ordinal))
                {
                    var driveInfo = new DriveInfo(mountPoint);
                    return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
                    {
                        FilesystemType = "f2fs",
                        TotalBytes = driveInfo.IsReady ? driveInfo.TotalSize : 0,
                        AvailableBytes = driveInfo.IsReady ? driveInfo.AvailableFreeSpace : 0,
                        UsedBytes = driveInfo.IsReady ? driveInfo.TotalSize - driveInfo.AvailableFreeSpace : 0,
                        BlockSize = 4096,
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
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options?.BufferSize ?? 4096, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        await fs.ReadExactlyAsync(buffer.AsMemory(0, length), ct);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, options?.BufferSize ?? 4096, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data.AsMemory(), ct);
    }

    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = await DetectAsync(path, ct);
        return result ?? new FilesystemMetadata { FilesystemType = "f2fs" };
    }
}

/// <summary>
/// ext3 filesystem detection strategy.
/// </summary>
public sealed class Ext3Strategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-ext3";
    public override string DisplayName => "ext3 Filesystem";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Format;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = true, SupportsSparse = true,
        SupportsAutoDetect = true, MaxFileSize = 2L * 1024 * 1024 * 1024 * 1024 // 2TB
    };
    public override string SemanticDescription =>
        "Detects ext3 journaling filesystem with ordered, writeback, and journal modes, " +
        "providing data consistency with performance tradeoffs.";
    public override string[] Tags => ["ext3", "linux", "journaling", "legacy", "stable"];

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

                if (fsType == "ext3" && normalizedPath.StartsWith(mountPoint, StringComparison.Ordinal))
                {
                    var driveInfo = new DriveInfo(mountPoint);
                    return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
                    {
                        FilesystemType = "ext3",
                        TotalBytes = driveInfo.IsReady ? driveInfo.TotalSize : 0,
                        AvailableBytes = driveInfo.IsReady ? driveInfo.AvailableFreeSpace : 0,
                        UsedBytes = driveInfo.IsReady ? driveInfo.TotalSize - driveInfo.AvailableFreeSpace : 0,
                        BlockSize = 4096,
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
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options?.BufferSize ?? 4096, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        await fs.ReadExactlyAsync(buffer.AsMemory(0, length), ct);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, options?.BufferSize ?? 4096, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data.AsMemory(), ct);
    }

    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = await DetectAsync(path, ct);
        return result ?? new FilesystemMetadata { FilesystemType = "ext3" };
    }
}

/// <summary>
/// ext2 filesystem detection strategy.
/// </summary>
public sealed class Ext2Strategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-ext2";
    public override string DisplayName => "ext2 Filesystem";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Format;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = true, SupportsSparse = true,
        SupportsAutoDetect = true, MaxFileSize = 2L * 1024 * 1024 * 1024 * 1024
    };
    public override string SemanticDescription =>
        "Detects ext2 simple block-based filesystem without journaling, offering " +
        "simplicity and minimal overhead for read-mostly workloads.";
    public override string[] Tags => ["ext2", "linux", "no-journal", "simple", "block-groups"];

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

                if (fsType == "ext2" && normalizedPath.StartsWith(mountPoint, StringComparison.Ordinal))
                {
                    var driveInfo = new DriveInfo(mountPoint);
                    return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
                    {
                        FilesystemType = "ext2",
                        TotalBytes = driveInfo.IsReady ? driveInfo.TotalSize : 0,
                        AvailableBytes = driveInfo.IsReady ? driveInfo.AvailableFreeSpace : 0,
                        UsedBytes = driveInfo.IsReady ? driveInfo.TotalSize - driveInfo.AvailableFreeSpace : 0,
                        BlockSize = 4096,
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
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options?.BufferSize ?? 4096, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        await fs.ReadExactlyAsync(buffer.AsMemory(0, length), ct);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, options?.BufferSize ?? 4096, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data.AsMemory(), ct);
    }

    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = await DetectAsync(path, ct);
        return result ?? new FilesystemMetadata { FilesystemType = "ext2" };
    }
}

/// <summary>
/// HFS+ (Hierarchical File System Plus) detection strategy.
/// </summary>
public sealed class HfsStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-hfs";
    public override string DisplayName => "HFS+ (Mac OS Extended)";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Format;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = false, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = false, SupportsSparse = false,
        SupportsAutoDetect = true, MaxFileSize = 8L * 1024 * 1024 * 1024 * 1024
    };
    public override string SemanticDescription =>
        "Detects HFS+ (Mac OS Extended) legacy macOS filesystem with journaling, " +
        "case-insensitive options, and resource fork support.";
    public override string[] Tags => ["hfs", "hfs+", "macos", "legacy", "apple", "journaling"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Task.FromResult<FilesystemMetadata?>(null);

        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(path) ?? path);
            if (!driveInfo.DriveFormat.Equals("HFS+", StringComparison.OrdinalIgnoreCase) &&
                !driveInfo.DriveFormat.Equals("HFS", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<FilesystemMetadata?>(null);

            return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
            {
                FilesystemType = "HFS+",
                TotalBytes = driveInfo.TotalSize,
                AvailableBytes = driveInfo.AvailableFreeSpace,
                UsedBytes = driveInfo.TotalSize - driveInfo.AvailableFreeSpace,
                BlockSize = 4096,
                IsReadOnly = !driveInfo.IsReady,
                SupportsSparse = false,
                SupportsCompression = true,
                MountPoint = driveInfo.RootDirectory.FullName
            });
        }
        catch
        {
            return Task.FromResult<FilesystemMetadata?>(null);
        }
    }

    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options?.BufferSize ?? 4096, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        await fs.ReadExactlyAsync(buffer.AsMemory(0, length), ct);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, options?.BufferSize ?? 4096, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data.AsMemory(), ct);
    }

    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = await DetectAsync(path, ct);
        return result ?? new FilesystemMetadata { FilesystemType = "HFS+" };
    }
}

/// <summary>
/// HAMMER2 filesystem detection strategy (DragonFly BSD).
/// </summary>
public sealed class Hammer2Strategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-hammer2";
    public override string DisplayName => "HAMMER2";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Format;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = true, SupportsSparse = true,
        SupportsAutoDetect = true, MaxFileSize = 64L * 1024 * 1024 * 1024 * 1024 * 1024
    };
    public override string SemanticDescription =>
        "Detects DragonFly BSD HAMMER2 multi-volume clustered filesystem with " +
        "snapshots, deduplication, and advanced compression.";
    public override string[] Tags => ["hammer2", "dragonfly-bsd", "clustered", "snapshot", "deduplication"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        // HAMMER2 is DragonFly BSD specific — NOT FreeBSD (finding 3025).
        // .NET does not expose a DragonFly BSD OSPlatform constant. Detect via /proc/version
        // content or /kern/ostype sysctl string. If neither confirms DragonFly BSD, return null.
        var isDragonFly = false;
        try
        {
            if (System.IO.File.Exists("/kern/ostype"))
            {
                var ostype = System.IO.File.ReadAllText("/kern/ostype").Trim();
                isDragonFly = ostype.Contains("DragonFly", StringComparison.OrdinalIgnoreCase);
            }
            else if (System.IO.File.Exists("/proc/version"))
            {
                var version = System.IO.File.ReadAllText("/proc/version");
                isDragonFly = version.Contains("DragonFly", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { /* Best-effort detection */ }

        if (!isDragonFly)
            return Task.FromResult<FilesystemMetadata?>(null);

        return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
        {
            FilesystemType = "hammer2",
            BlockSize = 64 * 1024,
            SupportsSparse = true,
            SupportsCompression = true,
            SupportsDeduplication = true,
            SupportsSnapshots = true,
            MountPoint = path
        });
    }

    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options?.BufferSize ?? 64 * 1024, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        await fs.ReadExactlyAsync(buffer.AsMemory(0, length), ct);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, options?.BufferSize ?? 64 * 1024, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data.AsMemory(), ct);
    }

    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = await DetectAsync(path, ct);
        return result ?? new FilesystemMetadata { FilesystemType = "hammer2" };
    }
}

/// <summary>
/// OCFS2 (Oracle Cluster File System) detection strategy.
/// </summary>
public sealed class Ocfs2Strategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-ocfs2";
    public override string DisplayName => "OCFS2 (Oracle Cluster FS)";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Format;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = true, SupportsSparse = true,
        SupportsAutoDetect = true, MaxFileSize = 4L * 1024 * 1024 * 1024 * 1024
    };
    public override string SemanticDescription =>
        "Detects Oracle Cluster File System (OCFS2) with distributed lock manager, " +
        "journal recovery, and concurrent access from multiple nodes.";
    public override string[] Tags => ["ocfs2", "oracle", "cluster", "dlm", "shared-disk"];

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

                if (fsType == "ocfs2" && normalizedPath.StartsWith(mountPoint, StringComparison.Ordinal))
                {
                    var driveInfo = new DriveInfo(mountPoint);
                    return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
                    {
                        FilesystemType = "ocfs2",
                        TotalBytes = driveInfo.IsReady ? driveInfo.TotalSize : 0,
                        AvailableBytes = driveInfo.IsReady ? driveInfo.AvailableFreeSpace : 0,
                        UsedBytes = driveInfo.IsReady ? driveInfo.TotalSize - driveInfo.AvailableFreeSpace : 0,
                        BlockSize = 4096,
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
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options?.BufferSize ?? 4096, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        await fs.ReadExactlyAsync(buffer.AsMemory(0, length), ct);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, options?.BufferSize ?? 4096, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data.AsMemory(), ct);
    }

    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = await DetectAsync(path, ct);
        return result ?? new FilesystemMetadata { FilesystemType = "ocfs2" };
    }
}

/// <summary>
/// tmpfs (temporary in-memory filesystem) detection strategy.
/// </summary>
public sealed class TmpfsStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-tmpfs";
    public override string DisplayName => "tmpfs (Memory-backed)";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Virtual;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = false, SupportsAsyncIo = false, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = false, SupportsSparse = false,
        SupportsAutoDetect = true, MaxFileSize = long.MaxValue
    };
    public override string SemanticDescription =>
        "Detects tmpfs memory-backed filesystem with configurable size limits, " +
        "swap integration, and ultra-fast access for temporary data.";
    public override string[] Tags => ["tmpfs", "memory", "ramdisk", "temporary", "volatile"];

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

                if (fsType == "tmpfs" && normalizedPath.StartsWith(mountPoint, StringComparison.Ordinal))
                {
                    var driveInfo = new DriveInfo(mountPoint);
                    return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
                    {
                        FilesystemType = "tmpfs",
                        TotalBytes = driveInfo.IsReady ? driveInfo.TotalSize : 0,
                        AvailableBytes = driveInfo.IsReady ? driveInfo.AvailableFreeSpace : 0,
                        UsedBytes = driveInfo.IsReady ? driveInfo.TotalSize - driveInfo.AvailableFreeSpace : 0,
                        BlockSize = 4096,
                        SupportsSparse = false,
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

    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        // tmpfs: synchronous operations (already in memory)
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        var bytesRead = fs.Read(buffer, 0, length);
        if (bytesRead < length)
            Array.Resize(ref buffer, bytesRead);
        return Task.FromResult(buffer);
    }

    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = await DetectAsync(path, ct);
        return result ?? new FilesystemMetadata { FilesystemType = "tmpfs" };
    }
}

/// <summary>
/// procfs (process information filesystem) detection strategy.
/// </summary>
public sealed class ProcfsStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-procfs";
    public override string DisplayName => "procfs (Process Information)";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Virtual;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = false, SupportsAsyncIo = false, SupportsMmap = false,
        SupportsKernelBypass = false, SupportsVectoredIo = false, SupportsSparse = false,
        SupportsAutoDetect = true, MaxFileSize = 0
    };
    public override string SemanticDescription =>
        "Detects procfs virtual filesystem exposing process and kernel information " +
        "through pseudo-files at /proc.";
    public override string[] Tags => ["procfs", "virtual", "process", "kernel", "proc"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return Task.FromResult<FilesystemMetadata?>(null);

        var normalizedPath = Path.GetFullPath(path);
        if (!normalizedPath.StartsWith("/proc", StringComparison.Ordinal))
            return Task.FromResult<FilesystemMetadata?>(null);

        return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
        {
            FilesystemType = "proc",
            TotalBytes = 0,
            AvailableBytes = 0,
            UsedBytes = 0,
            BlockSize = 0,
            IsReadOnly = false,
            SupportsSparse = false,
            MountPoint = "/proc"
        });
    }

    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        // procfs files are dynamically generated
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        var bytesRead = fs.Read(buffer, 0, length);
        if (bytesRead < length)
            Array.Resize(ref buffer, bytesRead);
        return Task.FromResult(buffer);
    }

    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        // Most procfs files are read-only; some support writes (e.g., sysctl)
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = await DetectAsync(path, ct);
        return result ?? new FilesystemMetadata { FilesystemType = "proc" };
    }
}

/// <summary>
/// sysfs (kernel object filesystem) detection strategy.
/// </summary>
public sealed class SysfsStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-sysfs";
    public override string DisplayName => "sysfs (Kernel Object FS)";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Virtual;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = false, SupportsAsyncIo = false, SupportsMmap = false,
        SupportsKernelBypass = false, SupportsVectoredIo = false, SupportsSparse = false,
        SupportsAutoDetect = true, MaxFileSize = 0
    };
    public override string SemanticDescription =>
        "Detects sysfs virtual filesystem exposing kernel devices, drivers, and " +
        "subsystems through hierarchical pseudo-files at /sys.";
    public override string[] Tags => ["sysfs", "virtual", "kernel", "devices", "sys"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return Task.FromResult<FilesystemMetadata?>(null);

        var normalizedPath = Path.GetFullPath(path);
        if (!normalizedPath.StartsWith("/sys", StringComparison.Ordinal))
            return Task.FromResult<FilesystemMetadata?>(null);

        return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
        {
            FilesystemType = "sysfs",
            TotalBytes = 0,
            AvailableBytes = 0,
            UsedBytes = 0,
            BlockSize = 0,
            IsReadOnly = false,
            SupportsSparse = false,
            MountPoint = "/sys"
        });
    }

    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        var bytesRead = fs.Read(buffer, 0, length);
        if (bytesRead < length)
            Array.Resize(ref buffer, bytesRead);
        return Task.FromResult(buffer);
    }

    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = await DetectAsync(path, ct);
        return result ?? new FilesystemMetadata { FilesystemType = "sysfs" };
    }
}
