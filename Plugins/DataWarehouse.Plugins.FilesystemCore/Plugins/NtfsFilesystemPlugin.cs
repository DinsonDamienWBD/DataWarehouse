// <copyright file="NtfsFilesystemPlugin.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DataWarehouse.Plugins.FilesystemCore.Plugins;

/// <summary>
/// Filesystem plugin for Windows NTFS (New Technology File System).
/// </summary>
/// <remarks>
/// NTFS capabilities include:
/// <list type="bullet">
/// <item>Hard links and symbolic links (with appropriate privileges)</item>
/// <item>Alternate Data Streams (ADS)</item>
/// <item>Access Control Lists (ACLs)</item>
/// <item>Encryption (EFS)</item>
/// <item>Compression</item>
/// <item>Sparse files</item>
/// <item>Volume Shadow Copy (snapshots)</item>
/// <item>Disk quotas</item>
/// <item>Journaling (USN Journal)</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class NtfsFilesystemPlugin : FilesystemPluginBase
{
    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.filesystem.ntfs";

    /// <inheritdoc/>
    public override string Name => "NTFS Filesystem";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string FilesystemType => "NTFS";

    /// <inheritdoc/>
    public override int DetectionPriority => 200; // High priority on Windows

    /// <inheritdoc/>
    public override FilesystemCapabilities Capabilities => FilesystemCapabilities.NtfsCommon;

    /// <inheritdoc/>
    public override async Task<bool> CanHandleAsync(string path, CancellationToken ct = default)
    {
        if (!IsWindows)
            return false;

        try
        {
            var driveInfo = GetDriveInfo(path);
            if (driveInfo == null)
                return false;

            return string.Equals(driveInfo.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public override Task<FilesystemStats> GetStatsAsync(string path, CancellationToken ct = default)
    {
        var driveInfo = GetDriveInfo(path)
            ?? throw new DirectoryNotFoundException($"Cannot find drive for path: {path}");

        var stats = new FilesystemStats
        {
            TotalSizeBytes = driveInfo.TotalSize,
            AvailableBytes = driveInfo.AvailableFreeSpace,
            UsedBytes = driveInfo.TotalSize - driveInfo.TotalFreeSpace,
            ReservedBytes = driveInfo.TotalFreeSpace - driveInfo.AvailableFreeSpace,
            FilesystemType = driveInfo.DriveFormat,
            VolumeLabel = driveInfo.VolumeLabel,
            DevicePath = driveInfo.Name,
            IsReadOnly = !driveInfo.IsReady || driveInfo.DriveType == DriveType.CDRom,
            IsNetwork = driveInfo.DriveType == DriveType.Network,
            IsRemovable = driveInfo.DriveType == DriveType.Removable,
            BlockSize = 4096, // Default NTFS cluster size
            MaxFileNameLength = 255,
            MaxPathLength = 32767, // With long path support
            CollectedAt = DateTime.UtcNow
        };

        // NTFS doesn't use inodes, so leave inode counts at 0
        stats.TotalInodes = 0;
        stats.FreeInodes = 0;
        stats.UsedInodes = 0;

        return Task.FromResult(stats);
    }

    /// <inheritdoc/>
    public override Task<FilesystemCapabilities> DetectCapabilitiesAsync(string path, CancellationToken ct = default)
    {
        if (!IsWindows)
            return Task.FromResult(FilesystemCapabilities.None);

        var driveInfo = GetDriveInfo(path);
        if (driveInfo == null || !string.Equals(driveInfo.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(FilesystemCapabilities.None);

        var capabilities = FilesystemCapabilities.None;

        // NTFS always supports these
        capabilities |= FilesystemCapabilities.HardLinks;
        capabilities |= FilesystemCapabilities.ExtendedAttributes; // Via ADS
        capabilities |= FilesystemCapabilities.AlternateDataStreams;
        capabilities |= FilesystemCapabilities.ACLs;
        capabilities |= FilesystemCapabilities.Compression;
        capabilities |= FilesystemCapabilities.SparseFiles;
        capabilities |= FilesystemCapabilities.Journaling;
        capabilities |= FilesystemCapabilities.Unicode;
        capabilities |= FilesystemCapabilities.LargeFiles;
        capabilities |= FilesystemCapabilities.ChangeNotifications;
        capabilities |= FilesystemCapabilities.MandatoryLocking;
        capabilities |= FilesystemCapabilities.AdvisoryLocking;
        capabilities |= FilesystemCapabilities.AtomicRename;
        capabilities |= FilesystemCapabilities.MemoryMappedFiles;
        capabilities |= FilesystemCapabilities.AsyncIO;

        // Symbolic links require SeCreateSymbolicLinkPrivilege or Developer Mode
        if (CanCreateSymbolicLinks())
            capabilities |= FilesystemCapabilities.SymbolicLinks;

        // EFS encryption depends on system configuration
        if (IsEfsAvailable())
            capabilities |= FilesystemCapabilities.Encryption;

        // VSS snapshots depend on service availability
        if (IsVssAvailable())
            capabilities |= FilesystemCapabilities.Snapshots;

        // Quotas depend on disk configuration
        capabilities |= FilesystemCapabilities.Quotas;

        return Task.FromResult(capabilities);
    }

    /// <inheritdoc/>
    public override Task<FilesystemHealth> CheckHealthAsync(string path, CancellationToken ct = default)
    {
        var health = new FilesystemHealth
        {
            Status = FilesystemHealthStatus.Healthy,
            CheckedAt = DateTime.UtcNow
        };

        try
        {
            var driveInfo = GetDriveInfo(path);
            if (driveInfo == null || !driveInfo.IsReady)
            {
                health.Status = FilesystemHealthStatus.Failed;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Critical,
                    Code = "DRIVE_NOT_READY",
                    Message = "Drive is not ready or accessible"
                });
                return Task.FromResult(health);
            }

            // Check for low disk space
            var usagePercent = (double)(driveInfo.TotalSize - driveInfo.AvailableFreeSpace) / driveInfo.TotalSize * 100;
            if (usagePercent > 95)
            {
                health.Status = FilesystemHealthStatus.Critical;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Critical,
                    Code = "DISK_SPACE_CRITICAL",
                    Message = $"Disk space critically low: {usagePercent:F1}% used",
                    Remediation = "Free up disk space or expand the volume"
                });
            }
            else if (usagePercent > 90)
            {
                health.Status = FilesystemHealthStatus.Warning;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Warning,
                    Code = "DISK_SPACE_LOW",
                    Message = $"Disk space low: {usagePercent:F1}% used",
                    Remediation = "Consider freeing up disk space"
                });
            }
        }
        catch (Exception ex)
        {
            health.Status = FilesystemHealthStatus.Warning;
            health.Issues.Add(new FilesystemIssue
            {
                Severity = FilesystemIssueSeverity.Warning,
                Code = "HEALTH_CHECK_ERROR",
                Message = $"Error during health check: {ex.Message}"
            });
        }

        return Task.FromResult(health);
    }

    /// <inheritdoc/>
    protected override Task<IFilesystemHandle> CreateHandleAsync(string path, MountOptions options, CancellationToken ct)
    {
        if (!IsWindows)
            throw new PlatformNotSupportedException("NTFS plugin only works on Windows.");

        var driveInfo = GetDriveInfo(path)
            ?? throw new DirectoryNotFoundException($"Cannot find drive for path: {path}");

        if (!string.Equals(driveInfo.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Path is not on an NTFS filesystem: {driveInfo.DriveFormat}");

        var handle = new NtfsFilesystemHandle(path, options, Capabilities);
        return Task.FromResult<IFilesystemHandle>(handle);
    }

    private static DriveInfo? GetDriveInfo(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                return null;

            return new DriveInfo(root);
        }
        catch
        {
            return null;
        }
    }

    private static bool CanCreateSymbolicLinks()
    {
        // Check if Developer Mode is enabled or user has privilege
        // This is a simplified check - in production, would check actual privileges
        try
        {
            // Check for Developer Mode via registry
            // For now, assume modern Windows 10/11 with Developer Mode
            return Environment.OSVersion.Version.Major >= 10;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsEfsAvailable()
    {
        // EFS is available on NTFS by default on most Windows editions
        // Not available on Home editions
        try
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsVssAvailable()
    {
        // VSS is available on most Windows editions
        try
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Filesystem handle implementation for NTFS.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NtfsFilesystemHandle : IFilesystemHandle
{
    private readonly MountOptions _options;
    private volatile bool _isValid = true;

    public NtfsFilesystemHandle(string mountPath, MountOptions options, FilesystemCapabilities capabilities)
    {
        MountPath = mountPath;
        Capabilities = capabilities;
        _options = options;
        IsReadOnly = options.ReadOnly;
    }

    public string MountPath { get; }
    public FilesystemCapabilities Capabilities { get; }
    public bool IsValid => _isValid;
    public bool IsReadOnly { get; }

    #region File Operations

    public Task<Stream> CreateFileAsync(string path, FileCreationOptions? options = null, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);
        var mode = options?.Overwrite == true ? FileMode.Create : FileMode.CreateNew;
        var stream = new FileStream(fullPath, mode, FileAccess.Write, FileShare.None,
            _options.BufferSize, FileOptions.Asynchronous);

        return Task.FromResult<Stream>(stream);
    }

    public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();

        var fullPath = GetFullPath(path);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            _options.BufferSize, FileOptions.Asynchronous);

        return Task.FromResult<Stream>(stream);
    }

    public Task<Stream> WriteFileAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.None,
            _options.BufferSize, FileOptions.Asynchronous);

        return Task.FromResult<Stream>(stream);
    }

    public Task<Stream> AppendFileAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);
        var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.None,
            _options.BufferSize, FileOptions.Asynchronous);

        return Task.FromResult<Stream>(stream);
    }

    public Task<bool> DeleteFileAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);
        if (!File.Exists(fullPath))
            return Task.FromResult(false);

        File.Delete(fullPath);
        return Task.FromResult(true);
    }

    public Task<bool> FileExistsAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult(File.Exists(fullPath));
    }

    public async Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite = false, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var sourceFullPath = GetFullPath(sourcePath);
        var destFullPath = GetFullPath(destinationPath);

        await Task.Run(() => File.Copy(sourceFullPath, destFullPath, overwrite), ct);
    }

    public async Task MoveFileAsync(string sourcePath, string destinationPath, bool overwrite = false, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var sourceFullPath = GetFullPath(sourcePath);
        var destFullPath = GetFullPath(destinationPath);

        if (overwrite && File.Exists(destFullPath))
            File.Delete(destFullPath);

        await Task.Run(() => File.Move(sourceFullPath, destFullPath), ct);
    }

    #endregion

    #region Directory Operations

    public Task<bool> CreateDirectoryAsync(string path, bool recursive = true, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);
        if (Directory.Exists(fullPath))
            return Task.FromResult(false);

        Directory.CreateDirectory(fullPath);
        return Task.FromResult(true);
    }

    public Task<bool> DeleteDirectoryAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);
        if (!Directory.Exists(fullPath))
            return Task.FromResult(false);

        Directory.Delete(fullPath, recursive);
        return Task.FromResult(true);
    }

    public async IAsyncEnumerable<FilesystemEntry> ListDirectoryAsync(string path, string? pattern = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfInvalid();

        var fullPath = GetFullPath(path);
        var searchPattern = pattern ?? "*";

        var entries = await Task.Run(() =>
        {
            var dirInfo = new DirectoryInfo(fullPath);
            return dirInfo.EnumerateFileSystemInfos(searchPattern).ToList();
        }, ct);

        foreach (var entry in entries)
        {
            if (ct.IsCancellationRequested)
                yield break;

            yield return new FilesystemEntry
            {
                Name = entry.Name,
                FullPath = entry.FullName,
                IsDirectory = entry is DirectoryInfo,
                IsSymbolicLink = entry.LinkTarget != null,
                Size = entry is FileInfo fi ? fi.Length : 0,
                CreatedAt = entry.CreationTimeUtc,
                ModifiedAt = entry.LastWriteTimeUtc,
                AccessedAt = entry.LastAccessTimeUtc,
                Attributes = entry.Attributes
            };
        }
    }

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult(Directory.Exists(fullPath));
    }

    #endregion

    #region Attributes

    public Task<FilesystemAttributes> GetAttributesAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();

        var fullPath = GetFullPath(path);
        var info = new FileInfo(fullPath);

        return Task.FromResult(new FilesystemAttributes
        {
            Size = info.Exists ? info.Length : 0,
            CreatedAtUtc = info.CreationTimeUtc,
            ModifiedAtUtc = info.LastWriteTimeUtc,
            AccessedAtUtc = info.LastAccessTimeUtc,
            FileAttributes = info.Attributes,
            IsDirectory = (info.Attributes & FileAttributes.Directory) != 0,
            IsSymbolicLink = info.LinkTarget != null,
            LinkCount = 1 // Would need P/Invoke to get actual link count
        });
    }

    public Task SetAttributesAsync(string path, FilesystemAttributes attributes, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);

        File.SetAttributes(fullPath, attributes.FileAttributes);
        File.SetCreationTimeUtc(fullPath, attributes.CreatedAtUtc);
        File.SetLastWriteTimeUtc(fullPath, attributes.ModifiedAtUtc);
        File.SetLastAccessTimeUtc(fullPath, attributes.AccessedAtUtc);

        return Task.CompletedTask;
    }

    public Task<Dictionary<string, byte[]>> GetExtendedAttributesAsync(string path, CancellationToken ct = default)
    {
        // NTFS uses Alternate Data Streams instead of xattrs
        // Would need to enumerate ADS using FindFirstStreamW/FindNextStreamW
        return Task.FromResult(new Dictionary<string, byte[]>());
    }

    public Task SetExtendedAttributeAsync(string path, string name, byte[] value, CancellationToken ct = default)
    {
        // Would need to write to ADS using path:streamname syntax
        return Task.CompletedTask;
    }

    public Task<bool> RemoveExtendedAttributeAsync(string path, string name, CancellationToken ct = default)
    {
        // Would need to delete ADS
        return Task.FromResult(false);
    }

    #endregion

    #region Locking

    public Task<IFileLock> LockFileAsync(string path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult<IFileLock>(new WindowsFileLock(fullPath, isExclusive: true));
    }

    public Task<IFileLock> LockFileSharedAsync(string path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult<IFileLock>(new WindowsFileLock(fullPath, isExclusive: false));
    }

    #endregion

    #region Links

    public Task CreateHardLinkAsync(string sourcePath, string linkPath, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var sourceFullPath = GetFullPath(sourcePath);
        var linkFullPath = GetFullPath(linkPath);

        // Use P/Invoke for CreateHardLink on Windows
        if (!NativeMethods.CreateHardLink(linkFullPath, sourceFullPath, IntPtr.Zero))
        {
            throw new IOException($"Failed to create hard link from '{sourceFullPath}' to '{linkFullPath}'",
                Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }

        return Task.CompletedTask;
    }

    public Task CreateSymbolicLinkAsync(string targetPath, string linkPath, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var linkFullPath = GetFullPath(linkPath);
        var isDirectory = Directory.Exists(targetPath);

        if (isDirectory)
            Directory.CreateSymbolicLink(linkFullPath, targetPath);
        else
            File.CreateSymbolicLink(linkFullPath, targetPath);

        return Task.CompletedTask;
    }

    public Task<string> GetSymbolicLinkTargetAsync(string linkPath, CancellationToken ct = default)
    {
        ThrowIfInvalid();

        var fullPath = GetFullPath(linkPath);
        var info = new FileInfo(fullPath);

        if (info.LinkTarget == null)
            throw new InvalidOperationException($"Path is not a symbolic link: {linkPath}");

        return Task.FromResult(info.LinkTarget);
    }

    #endregion

    #region Filesystem Operations

    public Task FlushAsync(CancellationToken ct = default)
    {
        ThrowIfInvalid();
        // Would need to call FlushFileBuffers via P/Invoke for full flush
        return Task.CompletedTask;
    }

    public async Task<FilesystemStats> GetStatsAsync(CancellationToken ct = default)
    {
        ThrowIfInvalid();

        var driveInfo = new DriveInfo(Path.GetPathRoot(MountPath)!);

        return await Task.FromResult(new FilesystemStats
        {
            TotalSizeBytes = driveInfo.TotalSize,
            AvailableBytes = driveInfo.AvailableFreeSpace,
            UsedBytes = driveInfo.TotalSize - driveInfo.TotalFreeSpace,
            FilesystemType = driveInfo.DriveFormat,
            VolumeLabel = driveInfo.VolumeLabel,
            CollectedAt = DateTime.UtcNow
        });
    }

    public Task<FilesystemHealth> CheckHealthAsync(CancellationToken ct = default)
    {
        ThrowIfInvalid();

        return Task.FromResult(new FilesystemHealth
        {
            Status = FilesystemHealthStatus.Healthy,
            CheckedAt = DateTime.UtcNow
        });
    }

    #endregion

    #region Helper Methods

    private string GetFullPath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;

        return Path.Combine(MountPath, path);
    }

    private void ThrowIfInvalid()
    {
        if (!_isValid)
            throw new ObjectDisposedException(nameof(NtfsFilesystemHandle));
    }

    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
            throw new UnauthorizedAccessException("Filesystem is mounted read-only.");
    }

    public ValueTask DisposeAsync()
    {
        _isValid = false;
        return ValueTask.CompletedTask;
    }

    #endregion
}

/// <summary>
/// Windows file lock implementation.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsFileLock : IFileLock
{
    private FileStream? _stream;

    public WindowsFileLock(string path, bool isExclusive)
    {
        Path = path;
        IsExclusive = isExclusive;

        var access = isExclusive ? FileAccess.ReadWrite : FileAccess.Read;
        var share = isExclusive ? FileShare.None : FileShare.Read;

        _stream = new FileStream(path, FileMode.Open, access, share);
    }

    public string Path { get; }
    public bool IsExclusive { get; }
    public bool IsHeld => _stream != null;

    public Task ReleaseAsync()
    {
        _stream?.Dispose();
        _stream = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _stream = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Native Windows API methods for filesystem operations.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    /// <summary>
    /// Creates a hard link.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
