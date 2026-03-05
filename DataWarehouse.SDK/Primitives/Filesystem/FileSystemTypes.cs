namespace DataWarehouse.SDK.Primitives.Filesystem;

/// <summary>
/// Represents metadata and attributes for a filesystem entry (file or directory).
/// </summary>
/// <param name="Path">The full path to the entry.</param>
/// <param name="Name">The name of the file or directory (without path).</param>
/// <param name="IsDirectory">True if this entry is a directory; false for files.</param>
/// <param name="Size">The size in bytes (0 for directories).</param>
/// <param name="CreatedUtc">The creation timestamp in UTC.</param>
/// <param name="ModifiedUtc">The last modification timestamp in UTC.</param>
/// <param name="AccessedUtc">The last access timestamp in UTC (may be null if not supported).</param>
/// <param name="Attributes">Filesystem attributes flags.</param>
/// <param name="Permissions">Optional permissions information (platform-specific).</param>
public record FileSystemEntry(
    string Path,
    string Name,
    bool IsDirectory,
    long Size,
    DateTime CreatedUtc,
    DateTime ModifiedUtc,
    DateTime? AccessedUtc,
    FilesystemAttributes Attributes,
    FilePermissions? Permissions = null);

/// <summary>
/// Filesystem attributes that can be applied to files and directories.
/// Renamed from <c>FileAttributes</c> to <c>FilesystemAttributes</c> (Cat 15, finding 547)
/// to avoid shadowing <see cref="System.IO.FileAttributes"/> in consuming code.
/// </summary>
[Flags]
public enum FilesystemAttributes
{
    /// <summary>No special attributes.</summary>
    None = 0,

    /// <summary>The file or directory is read-only.</summary>
    ReadOnly = 1 << 0,

    /// <summary>The file or directory is hidden from normal directory listings.</summary>
    Hidden = 1 << 1,

    /// <summary>The file is a system file.</summary>
    System = 1 << 2,

    /// <summary>The file is marked for archival or backup.</summary>
    Archive = 1 << 3,

    /// <summary>The file is compressed.</summary>
    Compressed = 1 << 4,

    /// <summary>The file is encrypted.</summary>
    Encrypted = 1 << 5,

    /// <summary>The file is a symbolic link.</summary>
    SymbolicLink = 1 << 6,

    /// <summary>The file is a temporary file.</summary>
    Temporary = 1 << 7
}

/// <summary>
/// Represents file system permissions (Unix-style).
/// </summary>
/// <param name="Owner">Permissions for the file owner.</param>
/// <param name="Group">Permissions for the group.</param>
/// <param name="Others">Permissions for others.</param>
/// <param name="OwnerName">The name of the owner (optional).</param>
/// <param name="GroupName">The name of the group (optional).</param>
public record FilePermissions(
    UnixPermission Owner,
    UnixPermission Group,
    UnixPermission Others,
    string? OwnerName = null,
    string? GroupName = null);

/// <summary>
/// Represents individual Unix permission flags (read, write, execute).
/// Renamed from <c>Permission</c> to <c>UnixPermission</c> (Cat 15, finding 548)
/// to avoid ambiguous reference with <c>DataWarehouse.SDK.Primitives.Permission</c>.
/// </summary>
[Flags]
public enum UnixPermission
{
    /// <summary>No permissions.</summary>
    None = 0,

    /// <summary>Execute permission.</summary>
    Execute = 1 << 0,

    /// <summary>Write permission.</summary>
    Write = 1 << 1,

    /// <summary>Read permission.</summary>
    Read = 1 << 2,

    /// <summary>All permissions (read, write, execute).</summary>
    All = Read | Write | Execute
}

/// <summary>
/// Represents detailed path information with parsed components.
/// </summary>
/// <param name="FullPath">The complete path.</param>
/// <param name="DirectoryPath">The directory portion of the path.</param>
/// <param name="FileName">The filename without extension.</param>
/// <param name="Extension">The file extension (including the dot, e.g., ".txt").</param>
/// <param name="IsAbsolute">True if the path is absolute; false if relative.</param>
public record PathInfo(
    string FullPath,
    string DirectoryPath,
    string FileName,
    string Extension,
    bool IsAbsolute);

/// <summary>
/// Metadata specific to files.
/// </summary>
/// <param name="Entry">The base filesystem entry information.</param>
/// <param name="MimeType">The MIME type of the file (optional).</param>
/// <param name="Hash">Content hash (optional, e.g., SHA256).</param>
/// <param name="IsSymbolicLink">True if this is a symbolic link.</param>
/// <param name="TargetPath">If symbolic link, the target path.</param>
public record FileMetadata(
    FileSystemEntry Entry,
    string? MimeType = null,
    string? Hash = null,
    bool IsSymbolicLink = false,
    string? TargetPath = null);

/// <summary>
/// Metadata specific to directories.
/// </summary>
/// <param name="Entry">The base filesystem entry information.</param>
/// <param name="FileCount">Number of files in this directory (non-recursive).</param>
/// <param name="DirectoryCount">Number of subdirectories.</param>
/// <param name="TotalSize">Total size of all files (optional, may be expensive to compute).</param>
public record DirectoryMetadata(
    FileSystemEntry Entry,
    int FileCount,
    int DirectoryCount,
    long? TotalSize = null);
