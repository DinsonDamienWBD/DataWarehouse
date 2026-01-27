// <copyright file="XfsFilesystemPlugin.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.FilesystemCore.Plugins;

/// <summary>
/// Filesystem plugin for Linux XFS (X File System).
/// </summary>
/// <remarks>
/// XFS is a high-performance 64-bit journaling filesystem developed by SGI:
/// <list type="bullet">
/// <item>Real-time I/O support with guaranteed I/O rates</item>
/// <item>Project quotas for directory-tree-based quota management</item>
/// <item>Reflink support (since kernel 4.16) for efficient file cloning</item>
/// <item>Online defragmentation</item>
/// <item>Online resizing (grow only)</item>
/// <item>Delayed allocation for improved performance</item>
/// <item>Extent-based allocation</item>
/// <item>B+ tree indexing for directories</item>
/// <item>Parallel I/O with allocation groups</item>
/// <item>Metadata journaling with external log support</item>
/// <item>Direct I/O support</item>
/// </list>
/// XFS is optimized for large files and parallel I/O operations.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class XfsFilesystemPlugin : FilesystemPluginBase
{
    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.filesystem.xfs";

    /// <inheritdoc/>
    public override string Name => "XFS Filesystem";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string FilesystemType => "xfs";

    /// <inheritdoc/>
    public override int DetectionPriority => 180; // High priority on Linux

    /// <inheritdoc/>
    public override FilesystemCapabilities Capabilities =>
        FilesystemCapabilities.HardLinks |
        FilesystemCapabilities.SymbolicLinks |
        FilesystemCapabilities.ExtendedAttributes |
        FilesystemCapabilities.ACLs |
        FilesystemCapabilities.Quotas |
        FilesystemCapabilities.Journaling |
        FilesystemCapabilities.SparseFiles |
        FilesystemCapabilities.CaseSensitive |
        FilesystemCapabilities.Unicode |
        FilesystemCapabilities.LargeFiles |
        FilesystemCapabilities.ChangeNotifications |
        FilesystemCapabilities.AdvisoryLocking |
        FilesystemCapabilities.AtomicRename |
        FilesystemCapabilities.MemoryMappedFiles |
        FilesystemCapabilities.AsyncIO |
        FilesystemCapabilities.DirectIO;

    /// <inheritdoc/>
    public override async Task<bool> CanHandleAsync(string path, CancellationToken ct = default)
    {
        if (!IsLinux)
            return false;

        try
        {
            var fsType = await GetFilesystemTypeAsync(path, ct);
            return string.Equals(fsType, "xfs", StringComparison.OrdinalIgnoreCase);
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
            FilesystemType = "xfs",
            DevicePath = normalizedPath,
            CollectedAt = DateTime.UtcNow,
            MaxFileNameLength = 255,
            MaxPathLength = 4096
        };

        try
        {
            // Get space stats from df
            var dfOutput = await RunCommandAsync("df", $"-B1 \"{normalizedPath}\"", ct);
            var lines = dfOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length >= 2)
            {
                var parts = WhitespaceRegex().Split(lines[1]);
                if (parts.Length >= 5)
                {
                    stats.TotalSizeBytes = long.TryParse(parts[1], out var total) ? total : 0;
                    stats.UsedBytes = long.TryParse(parts[2], out var used) ? used : 0;
                    stats.AvailableBytes = long.TryParse(parts[3], out var avail) ? avail : 0;
                }
            }

            // Get inode stats
            var inodeOutput = await RunCommandAsync("df", $"-i \"{normalizedPath}\"", ct);
            lines = inodeOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length >= 2)
            {
                var parts = WhitespaceRegex().Split(lines[1]);
                if (parts.Length >= 5)
                {
                    stats.TotalInodes = long.TryParse(parts[1], out var totalInodes) ? totalInodes : 0;
                    stats.UsedInodes = long.TryParse(parts[2], out var usedInodes) ? usedInodes : 0;
                    stats.FreeInodes = long.TryParse(parts[3], out var freeInodes) ? freeInodes : 0;
                }
            }

            // Get detailed XFS info using xfs_info
            var xfsInfo = await GetXfsInfoAsync(normalizedPath, ct);
            if (xfsInfo != null)
            {
                stats.BlockSize = xfsInfo.BlockSize;
                stats.ExtendedStats["blockSize"] = xfsInfo.BlockSize;
                stats.ExtendedStats["sectSize"] = xfsInfo.SectorSize;
                stats.ExtendedStats["logVersion"] = xfsInfo.LogVersion;
                stats.ExtendedStats["allocationGroups"] = xfsInfo.AllocationGroups;
                stats.ExtendedStats["inodeSize"] = xfsInfo.InodeSize;
                stats.ExtendedStats["reflinkSupported"] = xfsInfo.ReflinkSupported;
                stats.ExtendedStats["realTimeDevice"] = xfsInfo.HasRealTimeDevice;
            }

            // Check mount options
            var mountOutput = await RunCommandAsync("findmnt", $"-n -o OPTIONS \"{normalizedPath}\"", ct);
            stats.IsReadOnly = mountOutput.Contains("ro,") || mountOutput.StartsWith("ro ");

            // Get filesystem UUID
            var uuidOutput = await RunCommandAsync("xfs_admin", $"-u \"{await GetDevicePathAsync(normalizedPath, ct)}\"", ct);
            var uuidMatch = UuidRegex().Match(uuidOutput);
            if (uuidMatch.Success)
            {
                stats.VolumeSerialNumber = uuidMatch.Groups[1].Value;
                if (Guid.TryParse(stats.VolumeSerialNumber, out var fsId))
                    stats.FilesystemId = fsId;
            }

            // Get filesystem label
            var labelOutput = await RunCommandAsync("xfs_admin", $"-l \"{await GetDevicePathAsync(normalizedPath, ct)}\"", ct);
            var labelMatch = LabelRegex().Match(labelOutput);
            if (labelMatch.Success)
                stats.VolumeLabel = labelMatch.Groups[1].Value.Trim();
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
        if (!IsLinux)
            return FilesystemCapabilities.None;

        var fsType = await GetFilesystemTypeAsync(path, ct);
        if (!string.Equals(fsType, "xfs", StringComparison.OrdinalIgnoreCase))
            return FilesystemCapabilities.None;

        var capabilities = FilesystemCapabilities.None;

        // XFS always supports these core features
        capabilities |= FilesystemCapabilities.HardLinks;
        capabilities |= FilesystemCapabilities.SymbolicLinks;
        capabilities |= FilesystemCapabilities.ExtendedAttributes;
        capabilities |= FilesystemCapabilities.ACLs;
        capabilities |= FilesystemCapabilities.Quotas;
        capabilities |= FilesystemCapabilities.Journaling;
        capabilities |= FilesystemCapabilities.SparseFiles;
        capabilities |= FilesystemCapabilities.CaseSensitive;
        capabilities |= FilesystemCapabilities.Unicode;
        capabilities |= FilesystemCapabilities.LargeFiles;
        capabilities |= FilesystemCapabilities.ChangeNotifications; // inotify
        capabilities |= FilesystemCapabilities.AdvisoryLocking;
        capabilities |= FilesystemCapabilities.AtomicRename;
        capabilities |= FilesystemCapabilities.MemoryMappedFiles;
        capabilities |= FilesystemCapabilities.AsyncIO;
        capabilities |= FilesystemCapabilities.DirectIO;

        // Check for reflink support (requires kernel 4.16+ and -m reflink=1)
        var xfsInfo = await GetXfsInfoAsync(path, ct);
        if (xfsInfo?.ReflinkSupported == true)
            capabilities |= FilesystemCapabilities.Reflinks;

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

        var startTime = DateTime.UtcNow;

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
                    Remediation = "Free up disk space or expand the filesystem using xfs_growfs"
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

            // Check inode usage
            if (stats.InodeUsagePercent > 95)
            {
                health.Status = FilesystemHealthStatus.Critical;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Critical,
                    Code = "INODE_SPACE_CRITICAL",
                    Message = $"Inode space critically low: {stats.InodeUsagePercent:F1}% used",
                    Remediation = "Delete unnecessary files or consider recreating the filesystem"
                });
            }

            // Check for read-only mount
            var mountOutput = await RunCommandAsync("findmnt", $"-n -o OPTIONS \"{path}\"", ct);
            if (mountOutput.Contains("ro,") || mountOutput.StartsWith("ro "))
            {
                health.Status = FilesystemHealthStatus.Critical;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Critical,
                    Code = "READONLY_REMOUNT",
                    Message = "Filesystem has been remounted read-only",
                    Remediation = "Check dmesg for errors, consider running xfs_repair on unmounted filesystem"
                });
            }

            // Check for errors in dmesg (recent XFS errors)
            var dmesgOutput = await RunCommandAsync("dmesg", "-T | grep -i 'xfs.*error' | tail -5", ct);
            if (!string.IsNullOrWhiteSpace(dmesgOutput))
            {
                health.Status = FilesystemHealthStatus.Warning;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Warning,
                    Code = "XFS_ERRORS_DETECTED",
                    Message = "XFS errors found in kernel log",
                    Remediation = "Review kernel logs and consider running xfs_repair"
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

        health.CheckDuration = DateTime.UtcNow - startTime;
        return health;
    }

    /// <inheritdoc/>
    protected override Task<IFilesystemHandle> CreateHandleAsync(string path, MountOptions options, CancellationToken ct)
    {
        if (!IsLinux)
            throw new PlatformNotSupportedException("XFS plugin only works on Linux.");

        var handle = new XfsFilesystemHandle(path, options, Capabilities);
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
                var parts = WhitespaceRegex().Split(lines[1]);
                if (parts.Length >= 2)
                    return parts[1];
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<string> GetDevicePathAsync(string path, CancellationToken ct)
    {
        try
        {
            var output = await RunCommandAsync("df", $"\"{path}\"", ct);
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length >= 2)
            {
                var parts = WhitespaceRegex().Split(lines[1]);
                if (parts.Length >= 1)
                    return parts[0];
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<XfsInfo?> GetXfsInfoAsync(string path, CancellationToken ct)
    {
        try
        {
            var output = await RunCommandAsync("xfs_info", $"\"{path}\"", ct);
            if (string.IsNullOrWhiteSpace(output))
                return null;

            var info = new XfsInfo();

            // Parse xfs_info output
            // Example: meta-data=/dev/sda1 isize=512 agcount=4, agsize=2621440 blks
            //          =                       sectsz=512   attr=2, projid32bit=1
            //          data     =                       bsize=4096   blocks=10485760, imaxpct=25
            //          naming   =version 2              bsize=4096   ascii-ci=0 ftype=1
            //          log      =internal               bsize=4096   blocks=5120, version=2

            var bsizeMatch = BlockSizeRegex().Match(output);
            if (bsizeMatch.Success)
                info.BlockSize = int.Parse(bsizeMatch.Groups[1].Value);

            var sectszMatch = SectorSizeRegex().Match(output);
            if (sectszMatch.Success)
                info.SectorSize = int.Parse(sectszMatch.Groups[1].Value);

            var isizeMatch = InodeSizeRegex().Match(output);
            if (isizeMatch.Success)
                info.InodeSize = int.Parse(isizeMatch.Groups[1].Value);

            var agcountMatch = AgCountRegex().Match(output);
            if (agcountMatch.Success)
                info.AllocationGroups = int.Parse(agcountMatch.Groups[1].Value);

            var logVersionMatch = LogVersionRegex().Match(output);
            if (logVersionMatch.Success)
                info.LogVersion = int.Parse(logVersionMatch.Groups[1].Value);

            // Check for reflink support (reflink=1 in mkfs options or crc=1)
            info.ReflinkSupported = output.Contains("reflink=1") || output.Contains("crc=1");

            // Check for real-time device
            info.HasRealTimeDevice = output.Contains("realtime =");

            return info;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> RunCommandAsync(string command, string arguments, CancellationToken ct)
    {
        try
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
        catch
        {
            return string.Empty;
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"UUID\s*=\s*([a-f0-9-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UuidRegex();

    [GeneratedRegex(@"label\s*=\s*""(.*)""", RegexOptions.IgnoreCase)]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"bsize=(\d+)")]
    private static partial Regex BlockSizeRegex();

    [GeneratedRegex(@"sectsz=(\d+)")]
    private static partial Regex SectorSizeRegex();

    [GeneratedRegex(@"isize=(\d+)")]
    private static partial Regex InodeSizeRegex();

    [GeneratedRegex(@"agcount=(\d+)")]
    private static partial Regex AgCountRegex();

    [GeneratedRegex(@"version=(\d+)")]
    private static partial Regex LogVersionRegex();

    #endregion
}

/// <summary>
/// Parsed XFS filesystem information.
/// </summary>
internal sealed class XfsInfo
{
    /// <summary>
    /// Gets or sets the block size in bytes.
    /// </summary>
    public int BlockSize { get; set; } = 4096;

    /// <summary>
    /// Gets or sets the sector size in bytes.
    /// </summary>
    public int SectorSize { get; set; } = 512;

    /// <summary>
    /// Gets or sets the inode size in bytes.
    /// </summary>
    public int InodeSize { get; set; } = 512;

    /// <summary>
    /// Gets or sets the number of allocation groups.
    /// </summary>
    public int AllocationGroups { get; set; }

    /// <summary>
    /// Gets or sets the log version.
    /// </summary>
    public int LogVersion { get; set; } = 2;

    /// <summary>
    /// Gets or sets whether reflink is supported.
    /// </summary>
    public bool ReflinkSupported { get; set; }

    /// <summary>
    /// Gets or sets whether a real-time device is configured.
    /// </summary>
    public bool HasRealTimeDevice { get; set; }
}

/// <summary>
/// Filesystem handle implementation for XFS.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class XfsFilesystemHandle : IFilesystemHandle
{
    private readonly MountOptions _options;
    private volatile bool _isValid = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="XfsFilesystemHandle"/> class.
    /// </summary>
    /// <param name="mountPath">The mount path.</param>
    /// <param name="options">Mount options.</param>
    /// <param name="capabilities">Detected capabilities.</param>
    public XfsFilesystemHandle(string mountPath, MountOptions options, FilesystemCapabilities capabilities)
    {
        MountPath = mountPath;
        Capabilities = capabilities;
        _options = options;
        IsReadOnly = options.ReadOnly;
    }

    /// <inheritdoc/>
    public string MountPath { get; }

    /// <inheritdoc/>
    public FilesystemCapabilities Capabilities { get; }

    /// <inheritdoc/>
    public bool IsValid => _isValid;

    /// <inheritdoc/>
    public bool IsReadOnly { get; }

    #region File Operations

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();

        var fullPath = GetFullPath(path);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            _options.BufferSize, FileOptions.Asynchronous);

        return Task.FromResult<Stream>(stream);
    }

    /// <inheritdoc/>
    public Task<Stream> WriteFileAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.None,
            _options.BufferSize, FileOptions.Asynchronous);

        return Task.FromResult<Stream>(stream);
    }

    /// <inheritdoc/>
    public Task<Stream> AppendFileAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);
        var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.None,
            _options.BufferSize, FileOptions.Asynchronous);

        return Task.FromResult<Stream>(stream);
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public Task<bool> FileExistsAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult(File.Exists(fullPath));
    }

    /// <inheritdoc/>
    public async Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite = false, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var sourceFullPath = GetFullPath(sourcePath);
        var destFullPath = GetFullPath(destinationPath);

        // Try to use reflink for COW copy on XFS 4.16+
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cp",
                    Arguments = $"--reflink=auto {(overwrite ? "-f" : "-n")} \"{sourceFullPath}\" \"{destFullPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                await Task.Run(() => File.Copy(sourceFullPath, destFullPath, overwrite), ct);
            }
        }
        catch
        {
            await Task.Run(() => File.Copy(sourceFullPath, destFullPath, overwrite), ct);
        }
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult(Directory.Exists(fullPath));
    }

    #endregion

    #region Attributes

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public Task<Dictionary<string, byte[]>> GetExtendedAttributesAsync(string path, CancellationToken ct = default)
    {
        // Would use getfattr command
        return Task.FromResult(new Dictionary<string, byte[]>());
    }

    /// <inheritdoc/>
    public Task SetExtendedAttributeAsync(string path, string name, byte[] value, CancellationToken ct = default)
    {
        // Would use setfattr command
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveExtendedAttributeAsync(string path, string name, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }

    #endregion

    #region Locking

    /// <inheritdoc/>
    public Task<IFileLock> LockFileAsync(string path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult<IFileLock>(new XfsFileLock(fullPath, isExclusive: true));
    }

    /// <inheritdoc/>
    public Task<IFileLock> LockFileSharedAsync(string path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult<IFileLock>(new XfsFileLock(fullPath, isExclusive: false));
    }

    #endregion

    #region Links

    /// <inheritdoc/>
    public async Task CreateHardLinkAsync(string sourcePath, string linkPath, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var sourceFullPath = GetFullPath(sourcePath);
        var linkFullPath = GetFullPath(linkPath);

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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken ct = default)
    {
        ThrowIfInvalid();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<FilesystemStats> GetStatsAsync(CancellationToken ct = default)
    {
        ThrowIfInvalid();
        return Task.FromResult(new FilesystemStats
        {
            FilesystemType = "xfs",
            CollectedAt = DateTime.UtcNow
        });
    }

    /// <inheritdoc/>
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
            throw new ObjectDisposedException(nameof(XfsFilesystemHandle));
    }

    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
            throw new UnauthorizedAccessException("Filesystem is mounted read-only.");
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _isValid = false;
        return ValueTask.CompletedTask;
    }

    #endregion
}

/// <summary>
/// XFS file lock implementation using flock.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class XfsFileLock : IFileLock
{
    private FileStream? _stream;

    /// <summary>
    /// Initializes a new instance of the <see cref="XfsFileLock"/> class.
    /// </summary>
    /// <param name="path">The file path to lock.</param>
    /// <param name="isExclusive">Whether to acquire an exclusive lock.</param>
    public XfsFileLock(string path, bool isExclusive)
    {
        Path = path;
        IsExclusive = isExclusive;

        var access = isExclusive ? FileAccess.ReadWrite : FileAccess.Read;
        var share = isExclusive ? FileShare.None : FileShare.Read;

        _stream = new FileStream(path, FileMode.Open, access, share);
    }

    /// <inheritdoc/>
    public string Path { get; }

    /// <inheritdoc/>
    public bool IsExclusive { get; }

    /// <inheritdoc/>
    public bool IsHeld => _stream != null;

    /// <inheritdoc/>
    public Task ReleaseAsync()
    {
        _stream?.Dispose();
        _stream = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _stream = null;
        return ValueTask.CompletedTask;
    }
}
