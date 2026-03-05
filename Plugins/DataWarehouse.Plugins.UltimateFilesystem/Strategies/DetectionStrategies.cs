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

    // LOW-3039: Use async I/O with FileOptions.Asynchronous to avoid blocking the threadpool thread.
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        var bytesRead = await fs.ReadAsync(buffer, 0, length, ct).ConfigureAwait(false);
        if (bytesRead < length)
            Array.Resize(ref buffer, bytesRead);
        return buffer;
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
        var result = await DetectAsync(path, ct).ConfigureAwait(false);
        return result ?? new FilesystemMetadata { FilesystemType = "unknown" };
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

    // LOW-3039: Use async I/O to avoid blocking the threadpool thread.
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        var fileOptions = (options?.DirectIo == true ? FileOptions.WriteThrough : FileOptions.None) | FileOptions.Asynchronous;
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options?.BufferSize ?? 4096, fileOptions);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        var bytesRead = await fs.ReadAsync(buffer, 0, length, ct).ConfigureAwait(false);
        if (bytesRead < length)
            Array.Resize(ref buffer, bytesRead);
        return buffer;
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

    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var result = await DetectAsync(path, ct).ConfigureAwait(false);
        return result ?? new FilesystemMetadata { FilesystemType = "NTFS" };
    }
}

// ext4, btrfs, ZFS, and APFS strategies have been moved to SuperblockDetectionStrategies.cs
// with production-grade superblock parsing implementations.
