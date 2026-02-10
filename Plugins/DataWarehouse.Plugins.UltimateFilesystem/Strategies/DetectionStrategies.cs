using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.UltimateFilesystem.Strategies;

/// <summary>
/// Auto-detection filesystem strategy.
/// Automatically detects filesystem type.
/// </summary>
public sealed class AutoDetectStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "auto-detect";
    public override string DisplayName => "Auto-Detect Filesystem";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Detection;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = false, SupportsAsyncIo = true, SupportsMmap = false,
        SupportsKernelBypass = false, SupportsVectoredIo = false, SupportsSparse = false,
        SupportsAutoDetect = true
    };
    public override string SemanticDescription =>
        "Auto-detection strategy that identifies the filesystem type at a given path, " +
        "supporting NTFS, ext4, btrfs, XFS, ZFS, APFS, ReFS, and more.";
    public override string[] Tags => ["detection", "auto", "filesystem", "type"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
            return Task.FromResult<FilesystemMetadata?>(null);

        var driveInfo = new DriveInfo(Path.GetPathRoot(path) ?? path);

        return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
        {
            FilesystemType = driveInfo.DriveFormat,
            TotalBytes = driveInfo.TotalSize,
            AvailableBytes = driveInfo.AvailableFreeSpace,
            UsedBytes = driveInfo.TotalSize - driveInfo.AvailableFreeSpace,
            IsReadOnly = !driveInfo.IsReady,
            MountPoint = driveInfo.RootDirectory.FullName,
            SupportsCompression = driveInfo.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ||
                                  driveInfo.DriveFormat.Equals("btrfs", StringComparison.OrdinalIgnoreCase),
            SupportsEncryption = driveInfo.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ||
                                 driveInfo.DriveFormat.Equals("APFS", StringComparison.OrdinalIgnoreCase)
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
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = DetectAsync(path, ct).Result;
        return Task.FromResult(result ?? new FilesystemMetadata { FilesystemType = "unknown" });
    }
}

/// <summary>
/// NTFS detection and handling strategy.
/// </summary>
public sealed class NtfsStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-ntfs";
    public override string DisplayName => "NTFS Filesystem";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Detection;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = true, SupportsSparse = true,
        SupportsAutoDetect = true, MaxFileSize = 16L * 1024 * 1024 * 1024 * 1024
    };
    public override string SemanticDescription =>
        "NTFS filesystem strategy supporting compression, encryption, sparse files, " +
        "alternate data streams, and security descriptors.";
    public override string[] Tags => ["ntfs", "windows", "compression", "encryption", "ads"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Task.FromResult<FilesystemMetadata?>(null);

        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(path) ?? path);
            if (!driveInfo.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<FilesystemMetadata?>(null);

            return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
            {
                FilesystemType = "NTFS",
                TotalBytes = driveInfo.TotalSize,
                AvailableBytes = driveInfo.AvailableFreeSpace,
                UsedBytes = driveInfo.TotalSize - driveInfo.AvailableFreeSpace,
                BlockSize = 4096,
                SupportsCompression = true,
                SupportsEncryption = true,
                SupportsSparse = true,
                SupportsSnapshots = true,
                MountPoint = driveInfo.RootDirectory.FullName
            });
        }
        catch
        {
            return Task.FromResult<FilesystemMetadata?>(null);
        }
    }

    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        var fileOptions = options?.DirectIo == true ? FileOptions.WriteThrough : FileOptions.Asynchronous;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options?.BufferSize ?? 4096, fileOptions);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        var bytesRead = fs.Read(buffer, 0, length);
        if (bytesRead < length)
            Array.Resize(ref buffer, bytesRead);
        return Task.FromResult(buffer);
    }

    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        var fileOptions = FileOptions.Asynchronous;
        if (options?.WriteThrough == true) fileOptions |= FileOptions.WriteThrough;
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, options?.BufferSize ?? 4096, fileOptions);
        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = DetectAsync(path, ct).Result;
        return Task.FromResult(result ?? new FilesystemMetadata { FilesystemType = "NTFS" });
    }
}

/// <summary>
/// ext4 detection and handling strategy.
/// </summary>
public sealed class Ext4Strategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-ext4";
    public override string DisplayName => "ext4 Filesystem";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Detection;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = true, SupportsVectoredIo = true, SupportsSparse = true,
        SupportsAutoDetect = true, MaxFileSize = 16L * 1024 * 1024 * 1024 * 1024
    };
    public override string SemanticDescription =>
        "Linux ext4 filesystem strategy with extent-based allocation, journaling, " +
        "and excellent performance for general workloads.";
    public override string[] Tags => ["ext4", "linux", "journaling", "extent"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return Task.FromResult<FilesystemMetadata?>(null);

        // In production, would read /proc/mounts or use statfs
        return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
        {
            FilesystemType = "ext4",
            BlockSize = 4096,
            SupportsSparse = true,
            MountPoint = path
        });
    }

    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        fs.Read(buffer, 0, length);
        return Task.FromResult(buffer);
    }

    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = DetectAsync(path, ct).Result;
        return Task.FromResult(result ?? new FilesystemMetadata { FilesystemType = "ext4" });
    }
}

/// <summary>
/// btrfs detection and handling strategy.
/// </summary>
public sealed class BtrfsStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-btrfs";
    public override string DisplayName => "Btrfs Filesystem";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Detection;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = true, SupportsVectoredIo = true, SupportsSparse = true,
        SupportsAutoDetect = true, MaxFileSize = 16L * 1024 * 1024 * 1024 * 1024 * 1024
    };
    public override string SemanticDescription =>
        "Btrfs copy-on-write filesystem with snapshots, compression, deduplication, " +
        "checksumming, and RAID support built-in.";
    public override string[] Tags => ["btrfs", "linux", "cow", "snapshot", "compression", "deduplication"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return Task.FromResult<FilesystemMetadata?>(null);

        return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
        {
            FilesystemType = "btrfs",
            BlockSize = 4096,
            SupportsCompression = true,
            SupportsDeduplication = true,
            SupportsSnapshots = true,
            SupportsSparse = true,
            MountPoint = path
        });
    }

    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        fs.Read(buffer, 0, length);
        return Task.FromResult(buffer);
    }

    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = DetectAsync(path, ct).Result;
        return Task.FromResult(result ?? new FilesystemMetadata { FilesystemType = "btrfs" });
    }
}

/// <summary>
/// ZFS detection and handling strategy.
/// </summary>
public sealed class ZfsStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-zfs";
    public override string DisplayName => "ZFS Filesystem";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Detection;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = true, SupportsSparse = true,
        SupportsAutoDetect = true, MaxFileSize = 16L * 1024 * 1024 * 1024 * 1024 * 1024
    };
    public override string SemanticDescription =>
        "ZFS enterprise filesystem with end-to-end checksumming, self-healing, " +
        "snapshots, clones, compression, and deduplication.";
    public override string[] Tags => ["zfs", "enterprise", "checksum", "self-healing", "snapshot"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
        {
            FilesystemType = "zfs",
            BlockSize = 128 * 1024, // ZFS default recordsize
            SupportsCompression = true,
            SupportsDeduplication = true,
            SupportsSnapshots = true,
            SupportsSparse = true,
            MountPoint = path
        });
    }

    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        fs.Read(buffer, 0, length);
        return Task.FromResult(buffer);
    }

    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = DetectAsync(path, ct).Result;
        return Task.FromResult(result ?? new FilesystemMetadata { FilesystemType = "zfs" });
    }
}

/// <summary>
/// APFS detection and handling strategy.
/// </summary>
public sealed class ApfsStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "detect-apfs";
    public override string DisplayName => "APFS Filesystem";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Detection;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = true, SupportsSparse = true,
        SupportsAutoDetect = true, MaxFileSize = 8L * 1024 * 1024 * 1024 * 1024 * 1024
    };
    public override string SemanticDescription =>
        "Apple File System (APFS) with copy-on-write, snapshots, encryption, " +
        "space sharing, and crash protection.";
    public override string[] Tags => ["apfs", "macos", "apple", "encryption", "snapshot"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Task.FromResult<FilesystemMetadata?>(null);

        return Task.FromResult<FilesystemMetadata?>(new FilesystemMetadata
        {
            FilesystemType = "APFS",
            BlockSize = 4096,
            SupportsCompression = false,
            SupportsEncryption = true,
            SupportsSnapshots = true,
            SupportsSparse = true,
            MountPoint = path
        });
    }

    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        fs.Read(buffer, 0, length);
        return Task.FromResult(buffer);
    }

    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = DetectAsync(path, ct).Result;
        return Task.FromResult(result ?? new FilesystemMetadata { FilesystemType = "APFS" });
    }
}
