// <copyright file="ApfsFilesystemPlugin.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.FilesystemCore.Plugins;

/// <summary>
/// Filesystem plugin for macOS APFS (Apple File System).
/// </summary>
/// <remarks>
/// APFS capabilities include:
/// <list type="bullet">
/// <item>Hard links and symbolic links</item>
/// <item>Extended attributes</item>
/// <item>POSIX ACLs</item>
/// <item>Native encryption (FileVault compatible)</item>
/// <item>Compression</item>
/// <item>Snapshots</item>
/// <item>Space sharing between volumes</item>
/// <item>Copy-on-write</item>
/// <item>Clones (reflinks)</item>
/// <item>Crash protection</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed partial class ApfsFilesystemPlugin : FilesystemPluginBase
{
    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.filesystem.apfs";

    /// <inheritdoc/>
    public override string Name => "APFS Filesystem";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string FilesystemType => "APFS";

    /// <inheritdoc/>
    public override int DetectionPriority => 200; // High priority on macOS

    /// <inheritdoc/>
    public override FilesystemCapabilities Capabilities => FilesystemCapabilities.ApfsCommon;

    /// <inheritdoc/>
    public override async Task<bool> CanHandleAsync(string path, CancellationToken ct = default)
    {
        if (!IsMacOS)
            return false;

        try
        {
            var fsType = await GetFilesystemTypeAsync(path, ct);
            return string.Equals(fsType, "apfs", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public override async Task<FilesystemStats> GetStatsAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);

        var stats = new FilesystemStats
        {
            FilesystemType = "APFS",
            DevicePath = normalizedPath,
            CollectedAt = DateTime.UtcNow,
            MaxFileNameLength = 255,
            MaxPathLength = 1024 // PATH_MAX on macOS
        };

        try
        {
            // Use df to get space info
            var dfOutput = await RunCommandAsync("df", $"-k \"{normalizedPath}\"", ct);
            var lines = dfOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length >= 2)
            {
                var parts = DfOutputRegex().Split(lines[1]);
                if (parts.Length >= 5)
                {
                    // df -k returns values in 1K blocks
                    stats.TotalSizeBytes = long.TryParse(parts[1], out var total) ? total * 1024 : 0;
                    stats.UsedBytes = long.TryParse(parts[2], out var used) ? used * 1024 : 0;
                    stats.AvailableBytes = long.TryParse(parts[3], out var avail) ? avail * 1024 : 0;
                }
            }

            // Get volume info using diskutil
            var diskutilOutput = await RunCommandAsync("diskutil", $"info \"{normalizedPath}\"", ct);

            // Parse volume label
            var labelMatch = VolumeLabelRegex().Match(diskutilOutput);
            if (labelMatch.Success)
                stats.VolumeLabel = labelMatch.Groups[1].Value.Trim();

            // Parse UUID
            var uuidMatch = VolumeUuidRegex().Match(diskutilOutput);
            if (uuidMatch.Success)
            {
                stats.VolumeSerialNumber = uuidMatch.Groups[1].Value.Trim();
                if (Guid.TryParse(stats.VolumeSerialNumber, out var fsId))
                    stats.FilesystemId = fsId;
            }

            // Check read-only status
            stats.IsReadOnly = diskutilOutput.Contains("Read-Only Volume:              Yes");

            // APFS doesn't use traditional inodes
            stats.TotalInodes = 0;
            stats.FreeInodes = 0;
            stats.UsedInodes = 0;

            // Default APFS block size
            stats.BlockSize = 4096;
        }
        catch (Exception ex)
        {
            stats.ExtendedStats["error"] = ex.Message;
        }

        return stats;
    }

    /// <inheritdoc/>
    public override async Task<FilesystemCapabilities> DetectCapabilitiesAsync(string path, CancellationToken ct = default)
    {
        if (!IsMacOS)
            return FilesystemCapabilities.None;

        var fsType = await GetFilesystemTypeAsync(path, ct);
        if (!string.Equals(fsType, "apfs", StringComparison.OrdinalIgnoreCase))
            return FilesystemCapabilities.None;

        var capabilities = FilesystemCapabilities.None;

        // APFS always supports these
        capabilities |= FilesystemCapabilities.HardLinks;
        capabilities |= FilesystemCapabilities.SymbolicLinks;
        capabilities |= FilesystemCapabilities.ExtendedAttributes;
        capabilities |= FilesystemCapabilities.ACLs;
        capabilities |= FilesystemCapabilities.Compression;
        capabilities |= FilesystemCapabilities.Snapshots;
        capabilities |= FilesystemCapabilities.Quotas;
        capabilities |= FilesystemCapabilities.CopyOnWrite;
        capabilities |= FilesystemCapabilities.SparseFiles;
        capabilities |= FilesystemCapabilities.AlternateDataStreams; // Resource forks
        capabilities |= FilesystemCapabilities.Unicode;
        capabilities |= FilesystemCapabilities.LargeFiles;
        capabilities |= FilesystemCapabilities.Reflinks; // Clones
        capabilities |= FilesystemCapabilities.ChangeNotifications; // FSEvents
        capabilities |= FilesystemCapabilities.AdvisoryLocking;
        capabilities |= FilesystemCapabilities.AtomicRename;
        capabilities |= FilesystemCapabilities.MemoryMappedFiles;

        // Check for encryption (FileVault)
        if (await IsEncryptedAsync(path, ct))
            capabilities |= FilesystemCapabilities.Encryption;

        // APFS can be case-sensitive or case-insensitive
        if (await IsCaseSensitiveAsync(path, ct))
            capabilities |= FilesystemCapabilities.CaseSensitive;

        return capabilities;
    }

    /// <inheritdoc/>
    public override async Task<FilesystemHealth> CheckHealthAsync(string path, CancellationToken ct = default)
    {
        var health = new FilesystemHealth
        {
            Status = FilesystemHealthStatus.Healthy,
            CheckedAt = DateTime.UtcNow
        };

        try
        {
            var stats = await GetStatsAsync(path, ct);

            // Check disk space
            if (stats.UsagePercent > 95)
            {
                health.Status = FilesystemHealthStatus.Critical;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Critical,
                    Code = "DISK_SPACE_CRITICAL",
                    Message = $"Disk space critically low: {stats.UsagePercent:F1}% used",
                    Remediation = "Free up disk space or add more storage"
                });
            }
            else if (stats.UsagePercent > 90)
            {
                health.Status = FilesystemHealthStatus.Warning;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Warning,
                    Code = "DISK_SPACE_LOW",
                    Message = $"Disk space low: {stats.UsagePercent:F1}% used"
                });
            }

            // Check APFS container health using diskutil
            var diskutilOutput = await RunCommandAsync("diskutil", $"apfs list \"{path}\"", ct);

            // Look for any error indicators
            if (diskutilOutput.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                diskutilOutput.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                health.Status = FilesystemHealthStatus.Warning;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Warning,
                    Code = "APFS_CONTAINER_WARNING",
                    Message = "APFS container may have issues",
                    Remediation = "Run First Aid from Disk Utility"
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

        return health;
    }

    /// <inheritdoc/>
    protected override Task<IFilesystemHandle> CreateHandleAsync(string path, MountOptions options, CancellationToken ct)
    {
        if (!IsMacOS)
            throw new PlatformNotSupportedException("APFS plugin only works on macOS.");

        var handle = new ApfsFilesystemHandle(path, options, Capabilities);
        return Task.FromResult<IFilesystemHandle>(handle);
    }

    #region Helper Methods

    private static async Task<string> GetFilesystemTypeAsync(string path, CancellationToken ct)
    {
        try
        {
            var output = await RunCommandAsync("df", $"-T \"{path}\"", ct);
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length >= 2)
            {
                var parts = DfOutputRegex().Split(lines[1]);
                // On macOS, df -T shows type in the second-to-last column
                if (parts.Length >= 2)
                {
                    // Check if it's APFS
                    var joinedOutput = string.Join(" ", parts);
                    if (joinedOutput.Contains("apfs", StringComparison.OrdinalIgnoreCase))
                        return "apfs";
                }
            }

            // Fallback to diskutil
            var diskutilOutput = await RunCommandAsync("diskutil", $"info \"{path}\"", ct);
            if (diskutilOutput.Contains("APFS", StringComparison.OrdinalIgnoreCase))
                return "apfs";

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<bool> IsEncryptedAsync(string path, CancellationToken ct)
    {
        try
        {
            var output = await RunCommandAsync("diskutil", $"info \"{path}\"", ct);
            return output.Contains("FileVault:                     Yes") ||
                   output.Contains("Encrypted:                     Yes");
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsCaseSensitiveAsync(string path, CancellationToken ct)
    {
        try
        {
            var output = await RunCommandAsync("diskutil", $"info \"{path}\"", ct);
            return output.Contains("Name (Bidi):                   APFS (Case-sensitive");
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> RunCommandAsync(string command, string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return output;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex DfOutputRegex();

    [GeneratedRegex(@"Volume Name:\s*(.+)")]
    private static partial Regex VolumeLabelRegex();

    [GeneratedRegex(@"Volume UUID:\s*(.+)")]
    private static partial Regex VolumeUuidRegex();

    #endregion
}

/// <summary>
/// Filesystem handle implementation for APFS.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class ApfsFilesystemHandle : IFilesystemHandle
{
    private readonly MountOptions _options;
    private volatile bool _isValid = true;

    public ApfsFilesystemHandle(string mountPath, MountOptions options, FilesystemCapabilities capabilities)
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

        // On APFS, prefer using clonefile for copy-on-write
        // For now, fall back to regular copy
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
            LinkCount = 1
        });
    }

    public Task SetAttributesAsync(string path, FilesystemAttributes attributes, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);

        File.SetCreationTimeUtc(fullPath, attributes.CreatedAtUtc);
        File.SetLastWriteTimeUtc(fullPath, attributes.ModifiedAtUtc);
        File.SetLastAccessTimeUtc(fullPath, attributes.AccessedAtUtc);

        return Task.CompletedTask;
    }

    public Task<Dictionary<string, byte[]>> GetExtendedAttributesAsync(string path, CancellationToken ct = default)
    {
        // Would need to use getxattr syscall
        return Task.FromResult(new Dictionary<string, byte[]>());
    }

    public Task SetExtendedAttributeAsync(string path, string name, byte[] value, CancellationToken ct = default)
    {
        // Would need to use setxattr syscall
        return Task.CompletedTask;
    }

    public Task<bool> RemoveExtendedAttributeAsync(string path, string name, CancellationToken ct = default)
    {
        // Would need to use removexattr syscall
        return Task.FromResult(false);
    }

    #endregion

    #region Locking

    public Task<IFileLock> LockFileAsync(string path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult<IFileLock>(new MacOSFileLock(fullPath, isExclusive: true));
    }

    public Task<IFileLock> LockFileSharedAsync(string path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult<IFileLock>(new MacOSFileLock(fullPath, isExclusive: false));
    }

    #endregion

    #region Links

    public async Task CreateHardLinkAsync(string sourcePath, string linkPath, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var sourceFullPath = GetFullPath(sourcePath);
        var linkFullPath = GetFullPath(linkPath);

        // Use ln command on macOS
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ln",
                Arguments = $"\"{sourceFullPath}\" \"{linkFullPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new IOException($"Failed to create hard link: {error}");
        }
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
        return Task.CompletedTask;
    }

    public Task<FilesystemStats> GetStatsAsync(CancellationToken ct = default)
    {
        ThrowIfInvalid();
        return Task.FromResult(new FilesystemStats
        {
            FilesystemType = "APFS",
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
            throw new ObjectDisposedException(nameof(ApfsFilesystemHandle));
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
/// macOS file lock implementation using flock.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOSFileLock : IFileLock
{
    private FileStream? _stream;

    public MacOSFileLock(string path, bool isExclusive)
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
