using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.WinFspDriver;

/// <summary>
/// Configuration options for the WinFSP filesystem driver.
/// Provides settings for drive mounting, caching, security, and shell integration.
/// </summary>
public sealed class WinFspConfig
{
    /// <summary>
    /// The drive letter to mount (e.g., "D", "E", "Z"). If null, auto-selects first available.
    /// </summary>
    public string? DriveLetter { get; set; }

    /// <summary>
    /// The mount point path. Alternative to drive letter mounting.
    /// When set, mounts as a directory junction instead of a drive letter.
    /// </summary>
    public string? MountPoint { get; set; }

    /// <summary>
    /// Volume label displayed in Windows Explorer.
    /// </summary>
    public string VolumeLabel { get; set; } = "DataWarehouse";

    /// <summary>
    /// The filesystem type reported to Windows (e.g., "NTFS", "DataWarehouse").
    /// </summary>
    public string FileSystemName { get; set; } = "DataWarehouse";

    /// <summary>
    /// Maximum component length for file/directory names.
    /// </summary>
    public int MaxComponentLength { get; set; } = 255;

    /// <summary>
    /// Sector size in bytes. Must be a power of 2.
    /// </summary>
    public int SectorSize { get; set; } = 4096;

    /// <summary>
    /// Sectors per allocation unit. Affects cluster size.
    /// </summary>
    public int SectorsPerAllocationUnit { get; set; } = 1;

    /// <summary>
    /// Total size of the virtual volume in bytes. 0 = unlimited.
    /// </summary>
    public long TotalSize { get; set; } = 0;

    /// <summary>
    /// Free space to report. 0 = calculate from backend.
    /// </summary>
    public long FreeSize { get; set; } = 0;

    /// <summary>
    /// Whether to enable case-sensitive file names.
    /// </summary>
    public bool CaseSensitive { get; set; } = false;

    /// <summary>
    /// Whether to preserve case in file names.
    /// </summary>
    public bool CasePreserved { get; set; } = true;

    /// <summary>
    /// Whether to support Unicode file names.
    /// </summary>
    public bool UnicodeOnDisk { get; set; } = true;

    /// <summary>
    /// Whether to support persistent ACLs.
    /// </summary>
    public bool PersistentAcls { get; set; } = true;

    /// <summary>
    /// Whether to support extended attributes.
    /// </summary>
    public bool SupportExtendedAttributes { get; set; } = true;

    /// <summary>
    /// Whether to support reparse points (symbolic links, junctions).
    /// </summary>
    public bool SupportReparsePoints { get; set; } = true;

    /// <summary>
    /// Whether to support hard links.
    /// </summary>
    public bool SupportHardLinks { get; set; } = true;

    /// <summary>
    /// Whether to support named streams (alternate data streams).
    /// </summary>
    public bool SupportNamedStreams { get; set; } = true;

    /// <summary>
    /// Whether to support sparse files.
    /// </summary>
    public bool SupportSparseFiles { get; set; } = true;

    /// <summary>
    /// Cache configuration for read operations.
    /// </summary>
    public CacheConfig ReadCache { get; set; } = new();

    /// <summary>
    /// Cache configuration for write operations.
    /// </summary>
    public CacheConfig WriteCache { get; set; } = new();

    /// <summary>
    /// Security configuration for ACL handling.
    /// </summary>
    public SecurityConfig Security { get; set; } = new();

    /// <summary>
    /// Shell integration configuration.
    /// </summary>
    public ShellConfig Shell { get; set; } = new();

    /// <summary>
    /// VSS (Volume Shadow Copy) configuration.
    /// </summary>
    public VssConfig Vss { get; set; } = new();

    /// <summary>
    /// Debug and logging configuration.
    /// </summary>
    public DebugConfig Debug { get; set; } = new();

    /// <summary>
    /// BitLocker encryption configuration.
    /// </summary>
    public BitLockerConfig BitLocker { get; set; } = new();

    /// <summary>
    /// Backend storage provider ID. If null, uses default storage.
    /// </summary>
    public string? StorageProviderId { get; set; }

    /// <summary>
    /// Connection string or path for the backend storage.
    /// </summary>
    public string? StorageConnectionString { get; set; }

    /// <summary>
    /// Maximum number of concurrent file operations.
    /// </summary>
    public int MaxConcurrentOperations { get; set; } = 64;

    /// <summary>
    /// Timeout for individual file operations in milliseconds.
    /// </summary>
    public int OperationTimeoutMs { get; set; } = 30000;

    /// <summary>
    /// Whether to start the filesystem automatically when the plugin loads.
    /// </summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>
    /// Whether to create the mount point if it doesn't exist.
    /// </summary>
    public bool CreateMountPoint { get; set; } = true;

    /// <summary>
    /// Creates a default configuration.
    /// </summary>
    public static WinFspConfig Default => new();

    /// <summary>
    /// Creates configuration for a specific drive letter.
    /// </summary>
    /// <param name="driveLetter">Drive letter without colon (e.g., "D").</param>
    /// <returns>Configured instance.</returns>
    public static WinFspConfig ForDrive(string driveLetter) => new()
    {
        DriveLetter = driveLetter.TrimEnd(':')
    };

    /// <summary>
    /// Creates configuration for a mount point directory.
    /// </summary>
    /// <param name="mountPoint">Full path to mount point directory.</param>
    /// <returns>Configured instance.</returns>
    public static WinFspConfig ForMountPoint(string mountPoint) => new()
    {
        MountPoint = mountPoint
    };
}

/// <summary>
/// Cache configuration for read/write operations.
/// </summary>
public sealed class CacheConfig
{
    /// <summary>
    /// Whether caching is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum cache size in bytes.
    /// </summary>
    public long MaxSizeBytes { get; set; } = 256 * 1024 * 1024; // 256 MB

    /// <summary>
    /// Time-to-live for cached entries in seconds.
    /// </summary>
    public int TtlSeconds { get; set; } = 300; // 5 minutes

    /// <summary>
    /// Prefetch size in bytes for read-ahead.
    /// </summary>
    public int PrefetchSize { get; set; } = 1024 * 1024; // 1 MB

    /// <summary>
    /// Whether to use adaptive prefetch based on access patterns.
    /// </summary>
    public bool AdaptivePrefetch { get; set; } = true;

    /// <summary>
    /// Write buffer size in bytes.
    /// </summary>
    public int WriteBufferSize { get; set; } = 64 * 1024; // 64 KB

    /// <summary>
    /// Flush interval for write buffers in milliseconds.
    /// </summary>
    public int FlushIntervalMs { get; set; } = 5000; // 5 seconds

    /// <summary>
    /// Whether to use write-through (bypass cache for writes).
    /// </summary>
    public bool WriteThrough { get; set; } = false;
}

/// <summary>
/// Security configuration for ACL and permission handling.
/// </summary>
public sealed class SecurityConfig
{
    /// <summary>
    /// Whether to enforce ACLs.
    /// </summary>
    public bool EnforceAcls { get; set; } = true;

    /// <summary>
    /// Default owner SID for new files. If null, uses current user.
    /// </summary>
    public string? DefaultOwnerSid { get; set; }

    /// <summary>
    /// Default group SID for new files. If null, uses current user's primary group.
    /// </summary>
    public string? DefaultGroupSid { get; set; }

    /// <summary>
    /// Default DACL for new files in SDDL format. If null, uses inherited permissions.
    /// </summary>
    public string? DefaultDacl { get; set; }

    /// <summary>
    /// Whether to inherit permissions from parent directories.
    /// </summary>
    public bool InheritPermissions { get; set; } = true;

    /// <summary>
    /// Whether to audit file access.
    /// </summary>
    public bool AuditAccess { get; set; } = false;

    /// <summary>
    /// Path to audit log file.
    /// </summary>
    public string? AuditLogPath { get; set; }
}

/// <summary>
/// Shell integration configuration for Windows Explorer.
/// </summary>
public sealed class ShellConfig
{
    /// <summary>
    /// Whether to register shell overlay icons.
    /// </summary>
    public bool OverlayIcons { get; set; } = true;

    /// <summary>
    /// Whether to register context menu handlers.
    /// </summary>
    public bool ContextMenus { get; set; } = true;

    /// <summary>
    /// Whether to register property sheet handlers.
    /// </summary>
    public bool PropertySheets { get; set; } = true;

    /// <summary>
    /// Whether to register thumbnail providers.
    /// </summary>
    public bool ThumbnailProviders { get; set; } = false;

    /// <summary>
    /// Whether to register for Windows Search indexing.
    /// </summary>
    public bool SearchIndexing { get; set; } = false;

    /// <summary>
    /// Custom icon path for the drive in Explorer.
    /// </summary>
    public string? DriveIconPath { get; set; }
}

/// <summary>
/// VSS (Volume Shadow Copy Service) configuration.
/// </summary>
public sealed class VssConfig
{
    /// <summary>
    /// Whether to enable VSS integration.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum number of shadow copies to retain.
    /// </summary>
    public int MaxShadowCopies { get; set; } = 64;

    /// <summary>
    /// Whether to support transportable shadow copies.
    /// </summary>
    public bool Transportable { get; set; } = false;

    /// <summary>
    /// Provider ID for VSS registration.
    /// </summary>
    public Guid ProviderId { get; set; } = new("3B9B1F50-5A1E-4B8C-9E3F-7D4A6B8C9D0E");
}

/// <summary>
/// Debug and logging configuration.
/// </summary>
public sealed class DebugConfig
{
    /// <summary>
    /// Whether to enable debug logging.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Log level (0=None, 1=Error, 2=Warn, 3=Info, 4=Debug, 5=Trace).
    /// </summary>
    public int LogLevel { get; set; } = 3;

    /// <summary>
    /// Path to log file. If null, logs to standard output.
    /// </summary>
    public string? LogFilePath { get; set; }

    /// <summary>
    /// Whether to log file operation timing.
    /// </summary>
    public bool LogTiming { get; set; } = false;

    /// <summary>
    /// Whether to include stack traces in error logs.
    /// </summary>
    public bool IncludeStackTraces { get; set; } = false;
}
