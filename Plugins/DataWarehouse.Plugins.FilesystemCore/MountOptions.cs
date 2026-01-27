// <copyright file="MountOptions.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

namespace DataWarehouse.Plugins.FilesystemCore;

/// <summary>
/// Configuration options for mounting a filesystem.
/// </summary>
/// <remarks>
/// These options control how a filesystem handle behaves after mounting,
/// including read/write access, caching behavior, and security settings.
/// </remarks>
public sealed class MountOptions
{
    /// <summary>
    /// Gets or sets whether the filesystem should be mounted read-only.
    /// Default is false (read-write).
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Gets or sets whether to enable caching for file operations.
    /// Default is true.
    /// </summary>
    public bool EnableCaching { get; set; } = true;

    /// <summary>
    /// Gets or sets the cache size in bytes. Zero means use system default.
    /// Default is 0 (system default).
    /// </summary>
    public long CacheSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the cache expiration time for cached entries.
    /// Default is 5 minutes.
    /// </summary>
    public TimeSpan CacheExpiration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets whether to follow symbolic links.
    /// Default is true.
    /// </summary>
    public bool FollowSymlinks { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to enable case-sensitive operations.
    /// Default is null (use filesystem default).
    /// </summary>
    public bool? CaseSensitive { get; set; }

    /// <summary>
    /// Gets or sets the default file permissions for newly created files (Unix-style octal).
    /// Default is 0644 (rw-r--r--).
    /// </summary>
    public int DefaultFileMode { get; set; } = 0x1A4; // 0644 in octal

    /// <summary>
    /// Gets or sets the default directory permissions for newly created directories (Unix-style octal).
    /// Default is 0755 (rwxr-xr-x).
    /// </summary>
    public int DefaultDirectoryMode { get; set; } = 0x1ED; // 0755 in octal

    /// <summary>
    /// Gets or sets whether to preserve timestamps when copying/moving files.
    /// Default is true.
    /// </summary>
    public bool PreserveTimestamps { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to preserve extended attributes when copying/moving files.
    /// Default is true.
    /// </summary>
    public bool PreserveExtendedAttributes { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to preserve ACLs when copying/moving files.
    /// Default is true.
    /// </summary>
    public bool PreserveAcls { get; set; } = true;

    /// <summary>
    /// Gets or sets the timeout for filesystem operations.
    /// Default is 30 seconds.
    /// </summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the maximum number of concurrent operations.
    /// Default is 0 (unlimited).
    /// </summary>
    public int MaxConcurrentOperations { get; set; }

    /// <summary>
    /// Gets or sets whether to enable write-ahead logging for crash recovery.
    /// Default is false.
    /// </summary>
    public bool EnableWriteAheadLog { get; set; }

    /// <summary>
    /// Gets or sets whether to sync writes immediately to disk.
    /// Default is false (buffered writes).
    /// </summary>
    public bool SyncWrites { get; set; }

    /// <summary>
    /// Gets or sets whether to use direct I/O (bypass page cache).
    /// Default is false.
    /// </summary>
    public bool UseDirectIO { get; set; }

    /// <summary>
    /// Gets or sets the buffer size for read/write operations.
    /// Default is 64KB.
    /// </summary>
    public int BufferSize { get; set; } = 64 * 1024;

    /// <summary>
    /// Gets or sets custom mount options as key-value pairs.
    /// These are filesystem-specific options.
    /// </summary>
    public Dictionary<string, string> CustomOptions { get; set; } = new();

    /// <summary>
    /// Gets or sets the encryption key for encrypted filesystems.
    /// Should be handled securely and cleared after use.
    /// </summary>
    public byte[]? EncryptionKey { get; set; }

    /// <summary>
    /// Gets or sets whether to verify checksums on read operations.
    /// Default is false.
    /// </summary>
    public bool VerifyChecksums { get; set; }

    /// <summary>
    /// Gets or sets whether to compute checksums on write operations.
    /// Default is false.
    /// </summary>
    public bool ComputeChecksums { get; set; }

    /// <summary>
    /// Gets or sets the user identifier to use for operations.
    /// Default is null (use current user).
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Gets or sets the group identifier to use for operations.
    /// Default is null (use current group).
    /// </summary>
    public string? GroupId { get; set; }

    /// <summary>
    /// Creates a default read-only mount configuration.
    /// </summary>
    /// <returns>A new <see cref="MountOptions"/> configured for read-only access.</returns>
    public static MountOptions ReadOnlyMount() => new()
    {
        ReadOnly = true,
        EnableCaching = true
    };

    /// <summary>
    /// Creates a default high-performance mount configuration.
    /// </summary>
    /// <returns>A new <see cref="MountOptions"/> optimized for performance.</returns>
    public static MountOptions HighPerformance() => new()
    {
        EnableCaching = true,
        CacheSizeBytes = 256 * 1024 * 1024, // 256MB cache
        UseDirectIO = true,
        BufferSize = 1024 * 1024, // 1MB buffer
        SyncWrites = false
    };

    /// <summary>
    /// Creates a default high-integrity mount configuration.
    /// </summary>
    /// <returns>A new <see cref="MountOptions"/> optimized for data integrity.</returns>
    public static MountOptions HighIntegrity() => new()
    {
        EnableCaching = false,
        SyncWrites = true,
        EnableWriteAheadLog = true,
        VerifyChecksums = true,
        ComputeChecksums = true
    };
}

/// <summary>
/// Result of an unmount operation.
/// </summary>
public sealed class UnmountResult
{
    /// <summary>
    /// Gets or sets whether the unmount was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the error message if unmount failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets whether the unmount was forced (with potential data loss).
    /// </summary>
    public bool WasForced { get; set; }

    /// <summary>
    /// Gets or sets the number of files that were still open during unmount.
    /// </summary>
    public int OpenFileCount { get; set; }

    /// <summary>
    /// Gets or sets the number of pending write operations that were flushed.
    /// </summary>
    public int FlushedOperations { get; set; }

    /// <summary>
    /// Creates a successful unmount result.
    /// </summary>
    public static UnmountResult Successful() => new() { Success = true };

    /// <summary>
    /// Creates a failed unmount result.
    /// </summary>
    /// <param name="errorMessage">The error message describing the failure.</param>
    public static UnmountResult Failed(string errorMessage) => new()
    {
        Success = false,
        ErrorMessage = errorMessage
    };
}
