// <copyright file="IFilesystemHandle.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

namespace DataWarehouse.Plugins.FilesystemCore;

/// <summary>
/// Represents a handle to a mounted filesystem, providing file and directory operations.
/// </summary>
/// <remarks>
/// This interface provides a unified API for filesystem operations across different
/// filesystem types (NTFS, ext4, APFS, Btrfs, etc.). Implementations should be thread-safe.
/// </remarks>
public interface IFilesystemHandle : IAsyncDisposable
{
    /// <summary>
    /// Gets the mount path or root path of this filesystem handle.
    /// </summary>
    string MountPath { get; }

    /// <summary>
    /// Gets the detected capabilities of the mounted filesystem.
    /// </summary>
    FilesystemCapabilities Capabilities { get; }

    /// <summary>
    /// Gets whether the handle is valid and the filesystem is mounted.
    /// </summary>
    bool IsValid { get; }

    /// <summary>
    /// Gets whether the filesystem is mounted read-only.
    /// </summary>
    bool IsReadOnly { get; }

    #region File Operations

    /// <summary>
    /// Creates a new file at the specified path.
    /// </summary>
    /// <param name="path">The path of the file to create.</param>
    /// <param name="options">Optional file creation options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A stream for writing to the new file.</returns>
    /// <exception cref="IOException">Thrown if the file already exists or cannot be created.</exception>
    Task<Stream> CreateFileAsync(string path, FileCreationOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Opens an existing file for reading.
    /// </summary>
    /// <param name="path">The path of the file to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A stream for reading the file.</returns>
    /// <exception cref="FileNotFoundException">Thrown if the file does not exist.</exception>
    Task<Stream> ReadFileAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Opens an existing file for writing (overwrite mode).
    /// </summary>
    /// <param name="path">The path of the file to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A stream for writing to the file.</returns>
    /// <exception cref="FileNotFoundException">Thrown if the file does not exist.</exception>
    Task<Stream> WriteFileAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Opens an existing file for appending.
    /// </summary>
    /// <param name="path">The path of the file to append to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A stream positioned at the end of the file.</returns>
    Task<Stream> AppendFileAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Deletes a file at the specified path.
    /// </summary>
    /// <param name="path">The path of the file to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the file was deleted, false if it did not exist.</returns>
    Task<bool> DeleteFileAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Checks if a file exists at the specified path.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the file exists, false otherwise.</returns>
    Task<bool> FileExistsAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Copies a file from source to destination.
    /// </summary>
    /// <param name="sourcePath">The source file path.</param>
    /// <param name="destinationPath">The destination file path.</param>
    /// <param name="overwrite">Whether to overwrite if destination exists.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite = false, CancellationToken ct = default);

    /// <summary>
    /// Moves a file from source to destination.
    /// </summary>
    /// <param name="sourcePath">The source file path.</param>
    /// <param name="destinationPath">The destination file path.</param>
    /// <param name="overwrite">Whether to overwrite if destination exists.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MoveFileAsync(string sourcePath, string destinationPath, bool overwrite = false, CancellationToken ct = default);

    #endregion

    #region Directory Operations

    /// <summary>
    /// Creates a directory at the specified path.
    /// </summary>
    /// <param name="path">The path of the directory to create.</param>
    /// <param name="recursive">Whether to create parent directories if they don't exist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the directory was created, false if it already existed.</returns>
    Task<bool> CreateDirectoryAsync(string path, bool recursive = true, CancellationToken ct = default);

    /// <summary>
    /// Deletes a directory at the specified path.
    /// </summary>
    /// <param name="path">The path of the directory to delete.</param>
    /// <param name="recursive">Whether to delete contents recursively.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the directory was deleted, false if it did not exist.</returns>
    Task<bool> DeleteDirectoryAsync(string path, bool recursive = false, CancellationToken ct = default);

    /// <summary>
    /// Lists entries in a directory.
    /// </summary>
    /// <param name="path">The directory path to list.</param>
    /// <param name="pattern">Optional search pattern (e.g., "*.txt").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of directory entries.</returns>
    IAsyncEnumerable<FilesystemEntry> ListDirectoryAsync(string path, string? pattern = null, CancellationToken ct = default);

    /// <summary>
    /// Checks if a directory exists at the specified path.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the directory exists, false otherwise.</returns>
    Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default);

    #endregion

    #region Attributes and Metadata

    /// <summary>
    /// Gets the attributes of a file or directory.
    /// </summary>
    /// <param name="path">The path to get attributes for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The file/directory attributes.</returns>
    Task<FilesystemAttributes> GetAttributesAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Sets the attributes of a file or directory.
    /// </summary>
    /// <param name="path">The path to set attributes for.</param>
    /// <param name="attributes">The attributes to set.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SetAttributesAsync(string path, FilesystemAttributes attributes, CancellationToken ct = default);

    /// <summary>
    /// Gets extended attributes for a file or directory.
    /// </summary>
    /// <param name="path">The path to get extended attributes for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary of extended attribute names and values.</returns>
    Task<Dictionary<string, byte[]>> GetExtendedAttributesAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Sets an extended attribute for a file or directory.
    /// </summary>
    /// <param name="path">The path to set the extended attribute for.</param>
    /// <param name="name">The attribute name.</param>
    /// <param name="value">The attribute value.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SetExtendedAttributeAsync(string path, string name, byte[] value, CancellationToken ct = default);

    /// <summary>
    /// Removes an extended attribute from a file or directory.
    /// </summary>
    /// <param name="path">The path to remove the extended attribute from.</param>
    /// <param name="name">The attribute name to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> RemoveExtendedAttributeAsync(string path, string name, CancellationToken ct = default);

    #endregion

    #region Locking

    /// <summary>
    /// Acquires an exclusive lock on a file.
    /// </summary>
    /// <param name="path">The path of the file to lock.</param>
    /// <param name="timeout">Maximum time to wait for the lock.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A lock handle that releases the lock when disposed.</returns>
    Task<IFileLock> LockFileAsync(string path, TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>
    /// Acquires a shared (read) lock on a file.
    /// </summary>
    /// <param name="path">The path of the file to lock.</param>
    /// <param name="timeout">Maximum time to wait for the lock.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A lock handle that releases the lock when disposed.</returns>
    Task<IFileLock> LockFileSharedAsync(string path, TimeSpan? timeout = null, CancellationToken ct = default);

    #endregion

    #region Links

    /// <summary>
    /// Creates a hard link to an existing file.
    /// </summary>
    /// <param name="sourcePath">The existing file to link to.</param>
    /// <param name="linkPath">The path for the new hard link.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="NotSupportedException">Thrown if the filesystem doesn't support hard links.</exception>
    Task CreateHardLinkAsync(string sourcePath, string linkPath, CancellationToken ct = default);

    /// <summary>
    /// Creates a symbolic link to a file or directory.
    /// </summary>
    /// <param name="targetPath">The target path the link points to.</param>
    /// <param name="linkPath">The path for the new symbolic link.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="NotSupportedException">Thrown if the filesystem doesn't support symbolic links.</exception>
    Task CreateSymbolicLinkAsync(string targetPath, string linkPath, CancellationToken ct = default);

    /// <summary>
    /// Gets the target of a symbolic link.
    /// </summary>
    /// <param name="linkPath">The path of the symbolic link.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The target path of the symbolic link.</returns>
    Task<string> GetSymbolicLinkTargetAsync(string linkPath, CancellationToken ct = default);

    #endregion

    #region Filesystem Operations

    /// <summary>
    /// Flushes all pending writes to disk.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets current filesystem statistics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current filesystem statistics.</returns>
    Task<FilesystemStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Performs a health check on the filesystem.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Health status of the filesystem.</returns>
    Task<FilesystemHealth> CheckHealthAsync(CancellationToken ct = default);

    #endregion
}

/// <summary>
/// Represents a file lock that can be released.
/// </summary>
public interface IFileLock : IAsyncDisposable
{
    /// <summary>
    /// Gets the path of the locked file.
    /// </summary>
    string Path { get; }

    /// <summary>
    /// Gets whether this is an exclusive lock.
    /// </summary>
    bool IsExclusive { get; }

    /// <summary>
    /// Gets whether the lock is still held.
    /// </summary>
    bool IsHeld { get; }

    /// <summary>
    /// Releases the lock immediately.
    /// </summary>
    Task ReleaseAsync();
}

/// <summary>
/// Represents an entry in a directory listing.
/// </summary>
public sealed class FilesystemEntry
{
    /// <summary>
    /// Gets or sets the name of the entry.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the full path of the entry.
    /// </summary>
    public string FullPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether this entry is a directory.
    /// </summary>
    public bool IsDirectory { get; set; }

    /// <summary>
    /// Gets or sets whether this entry is a symbolic link.
    /// </summary>
    public bool IsSymbolicLink { get; set; }

    /// <summary>
    /// Gets or sets the size in bytes (0 for directories).
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the creation time.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the last modification time.
    /// </summary>
    public DateTime ModifiedAt { get; set; }

    /// <summary>
    /// Gets or sets the last access time.
    /// </summary>
    public DateTime AccessedAt { get; set; }

    /// <summary>
    /// Gets or sets the file attributes.
    /// </summary>
    public FileAttributes Attributes { get; set; }
}

/// <summary>
/// Extended attributes for a file or directory.
/// </summary>
public sealed class FilesystemAttributes
{
    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the creation time (UTC).
    /// </summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the last modification time (UTC).
    /// </summary>
    public DateTime ModifiedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the last access time (UTC).
    /// </summary>
    public DateTime AccessedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the system file attributes.
    /// </summary>
    public FileAttributes FileAttributes { get; set; }

    /// <summary>
    /// Gets or sets whether the entry is a directory.
    /// </summary>
    public bool IsDirectory { get; set; }

    /// <summary>
    /// Gets or sets whether the entry is a symbolic link.
    /// </summary>
    public bool IsSymbolicLink { get; set; }

    /// <summary>
    /// Gets or sets whether the entry is a hard link (link count > 1).
    /// </summary>
    public bool IsHardLink { get; set; }

    /// <summary>
    /// Gets or sets the number of hard links to this file.
    /// </summary>
    public int LinkCount { get; set; } = 1;

    /// <summary>
    /// Gets or sets the owner user ID (Unix) or SID (Windows).
    /// </summary>
    public string? OwnerId { get; set; }

    /// <summary>
    /// Gets or sets the owner group ID (Unix) or SID (Windows).
    /// </summary>
    public string? GroupId { get; set; }

    /// <summary>
    /// Gets or sets the Unix-style permissions (e.g., 0644).
    /// </summary>
    public int? UnixPermissions { get; set; }

    /// <summary>
    /// Gets or sets the inode number (Unix filesystems).
    /// </summary>
    public long? Inode { get; set; }

    /// <summary>
    /// Gets or sets the device ID (Unix filesystems).
    /// </summary>
    public long? DeviceId { get; set; }

    /// <summary>
    /// Gets or sets additional filesystem-specific attributes.
    /// </summary>
    public Dictionary<string, object> ExtendedAttributes { get; set; } = new();
}

/// <summary>
/// Options for creating a new file.
/// </summary>
public sealed class FileCreationOptions
{
    /// <summary>
    /// Gets or sets whether to overwrite if the file exists.
    /// </summary>
    public bool Overwrite { get; set; }

    /// <summary>
    /// Gets or sets the initial file attributes.
    /// </summary>
    public FileAttributes? Attributes { get; set; }

    /// <summary>
    /// Gets or sets the Unix-style permissions (e.g., 0644).
    /// </summary>
    public int? UnixPermissions { get; set; }

    /// <summary>
    /// Gets or sets whether to create the file as sparse.
    /// </summary>
    public bool CreateSparse { get; set; }

    /// <summary>
    /// Gets or sets the initial file size for pre-allocation.
    /// </summary>
    public long? PreallocateSize { get; set; }

    /// <summary>
    /// Gets or sets initial extended attributes to set.
    /// </summary>
    public Dictionary<string, byte[]>? ExtendedAttributes { get; set; }
}
