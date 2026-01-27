// <copyright file="FilesystemCapabilities.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

namespace DataWarehouse.Plugins.FilesystemCore;

/// <summary>
/// Capability flags representing features supported by a filesystem.
/// These flags enable intelligent feature detection and plugin selection
/// based on underlying filesystem capabilities.
/// </summary>
/// <remarks>
/// Use bitwise operations to combine or check multiple capabilities:
/// <code>
/// var caps = FilesystemCapabilities.HardLinks | FilesystemCapabilities.SymbolicLinks;
/// bool hasLinks = (caps &amp; FilesystemCapabilities.HardLinks) != 0;
/// </code>
/// </remarks>
[Flags]
public enum FilesystemCapabilities
{
    /// <summary>
    /// No special capabilities detected or supported.
    /// </summary>
    None = 0,

    /// <summary>
    /// Filesystem supports hard links (multiple directory entries pointing to same inode).
    /// Supported on: NTFS, ext4, Btrfs, APFS.
    /// </summary>
    HardLinks = 1 << 0,

    /// <summary>
    /// Filesystem supports symbolic links (soft links pointing to paths).
    /// Supported on: NTFS (with privileges), ext4, Btrfs, APFS.
    /// </summary>
    SymbolicLinks = 1 << 1,

    /// <summary>
    /// Filesystem supports extended attributes (xattr).
    /// Supported on: NTFS (alternate data streams), ext4, Btrfs, APFS.
    /// </summary>
    ExtendedAttributes = 1 << 2,

    /// <summary>
    /// Filesystem supports access control lists (ACLs).
    /// Supported on: NTFS, ext4 (with ACL mount option), Btrfs, APFS.
    /// </summary>
    ACLs = 1 << 3,

    /// <summary>
    /// Filesystem supports native encryption.
    /// Supported on: NTFS (EFS), ext4 (fscrypt), APFS (FileVault).
    /// </summary>
    Encryption = 1 << 4,

    /// <summary>
    /// Filesystem supports transparent compression.
    /// Supported on: NTFS, Btrfs, ZFS, APFS.
    /// </summary>
    Compression = 1 << 5,

    /// <summary>
    /// Filesystem supports block-level deduplication.
    /// Supported on: Btrfs, ZFS, Windows Server (ReFS).
    /// </summary>
    Deduplication = 1 << 6,

    /// <summary>
    /// Filesystem supports point-in-time snapshots.
    /// Supported on: NTFS (VSS), Btrfs, ZFS, APFS.
    /// </summary>
    Snapshots = 1 << 7,

    /// <summary>
    /// Filesystem supports disk quotas.
    /// Supported on: NTFS, ext4, Btrfs, APFS.
    /// </summary>
    Quotas = 1 << 8,

    /// <summary>
    /// Filesystem uses journaling for crash recovery.
    /// Supported on: NTFS, ext4, Btrfs, APFS, XFS.
    /// </summary>
    Journaling = 1 << 9,

    /// <summary>
    /// Filesystem uses copy-on-write semantics.
    /// Supported on: Btrfs, ZFS, APFS, ReFS.
    /// </summary>
    CopyOnWrite = 1 << 10,

    /// <summary>
    /// Filesystem supports data integrity checksums.
    /// Supported on: Btrfs, ZFS, ReFS.
    /// </summary>
    Checksums = 1 << 11,

    /// <summary>
    /// Filesystem supports sparse files (files with holes).
    /// Supported on: NTFS, ext4, Btrfs, APFS, XFS.
    /// </summary>
    SparseFiles = 1 << 12,

    /// <summary>
    /// Filesystem supports alternate data streams (Windows) or resource forks (macOS).
    /// Supported on: NTFS, APFS.
    /// </summary>
    AlternateDataStreams = 1 << 13,

    /// <summary>
    /// Filesystem supports case-sensitive file names.
    /// Supported on: ext4, Btrfs, XFS. Optionally on NTFS, APFS.
    /// </summary>
    CaseSensitive = 1 << 14,

    /// <summary>
    /// Filesystem supports Unicode file names.
    /// Supported on: NTFS, ext4, Btrfs, APFS, XFS.
    /// </summary>
    Unicode = 1 << 15,

    /// <summary>
    /// Filesystem supports large files (greater than 4GB).
    /// Supported on: NTFS, ext4, Btrfs, APFS, XFS.
    /// </summary>
    LargeFiles = 1 << 16,

    /// <summary>
    /// Filesystem supports reflinks (lightweight file copies).
    /// Supported on: Btrfs, XFS (4.16+), APFS.
    /// </summary>
    Reflinks = 1 << 17,

    /// <summary>
    /// Filesystem supports file change notifications.
    /// Supported on: NTFS (ReadDirectoryChangesW), ext4 (inotify), APFS (FSEvents).
    /// </summary>
    ChangeNotifications = 1 << 18,

    /// <summary>
    /// Filesystem supports mandatory file locking.
    /// Supported on: NTFS, ext4 (with mount option).
    /// </summary>
    MandatoryLocking = 1 << 19,

    /// <summary>
    /// Filesystem supports advisory file locking.
    /// Supported on: All POSIX filesystems, NTFS.
    /// </summary>
    AdvisoryLocking = 1 << 20,

    /// <summary>
    /// Filesystem supports atomic rename operations.
    /// Supported on: Most modern filesystems.
    /// </summary>
    AtomicRename = 1 << 21,

    /// <summary>
    /// Filesystem supports memory-mapped files.
    /// Supported on: All modern filesystems.
    /// </summary>
    MemoryMappedFiles = 1 << 22,

    /// <summary>
    /// Filesystem supports asynchronous I/O.
    /// Supported on: NTFS, ext4 (with io_uring), XFS.
    /// </summary>
    AsyncIO = 1 << 23,

    /// <summary>
    /// Filesystem supports direct I/O (bypassing page cache).
    /// Supported on: ext4, XFS, Btrfs (with restrictions).
    /// </summary>
    DirectIO = 1 << 24,

    /// <summary>
    /// Common capabilities for Windows NTFS.
    /// </summary>
    NtfsCommon = HardLinks | ExtendedAttributes | ACLs | Encryption | Compression |
                 Snapshots | Quotas | Journaling | SparseFiles | AlternateDataStreams |
                 Unicode | LargeFiles | ChangeNotifications | MandatoryLocking |
                 AdvisoryLocking | AtomicRename | MemoryMappedFiles | AsyncIO,

    /// <summary>
    /// Common capabilities for Linux ext4.
    /// </summary>
    Ext4Common = HardLinks | SymbolicLinks | ExtendedAttributes | ACLs | Encryption |
                 Quotas | Journaling | SparseFiles | CaseSensitive | Unicode |
                 LargeFiles | ChangeNotifications | AdvisoryLocking | AtomicRename |
                 MemoryMappedFiles | AsyncIO | DirectIO,

    /// <summary>
    /// Common capabilities for macOS APFS.
    /// </summary>
    ApfsCommon = HardLinks | SymbolicLinks | ExtendedAttributes | ACLs | Encryption |
                 Compression | Snapshots | Quotas | CopyOnWrite | SparseFiles |
                 AlternateDataStreams | Unicode | LargeFiles | Reflinks |
                 ChangeNotifications | AdvisoryLocking | AtomicRename | MemoryMappedFiles,

    /// <summary>
    /// Common capabilities for Linux Btrfs.
    /// </summary>
    BtrfsCommon = HardLinks | SymbolicLinks | ExtendedAttributes | ACLs | Compression |
                  Deduplication | Snapshots | Quotas | CopyOnWrite | Checksums |
                  SparseFiles | CaseSensitive | Unicode | LargeFiles | Reflinks |
                  ChangeNotifications | AdvisoryLocking | AtomicRename | MemoryMappedFiles |
                  AsyncIO
}

/// <summary>
/// Represents a specific filesystem feature that can be queried at runtime.
/// </summary>
public enum FilesystemFeature
{
    /// <summary>
    /// Hard link support.
    /// </summary>
    HardLinks,

    /// <summary>
    /// Symbolic link support.
    /// </summary>
    SymbolicLinks,

    /// <summary>
    /// Extended attributes support.
    /// </summary>
    ExtendedAttributes,

    /// <summary>
    /// Access control lists support.
    /// </summary>
    ACLs,

    /// <summary>
    /// Native encryption support.
    /// </summary>
    Encryption,

    /// <summary>
    /// Transparent compression support.
    /// </summary>
    Compression,

    /// <summary>
    /// Block-level deduplication support.
    /// </summary>
    Deduplication,

    /// <summary>
    /// Point-in-time snapshot support.
    /// </summary>
    Snapshots,

    /// <summary>
    /// Disk quota support.
    /// </summary>
    Quotas,

    /// <summary>
    /// Journaling support.
    /// </summary>
    Journaling,

    /// <summary>
    /// Copy-on-write support.
    /// </summary>
    CopyOnWrite,

    /// <summary>
    /// Data integrity checksum support.
    /// </summary>
    Checksums,

    /// <summary>
    /// Sparse file support.
    /// </summary>
    SparseFiles,

    /// <summary>
    /// Alternate data streams support.
    /// </summary>
    AlternateDataStreams,

    /// <summary>
    /// Case-sensitive file names.
    /// </summary>
    CaseSensitive,

    /// <summary>
    /// Unicode file names.
    /// </summary>
    Unicode,

    /// <summary>
    /// Large file support (greater than 4GB).
    /// </summary>
    LargeFiles,

    /// <summary>
    /// Reflink (lightweight copy) support.
    /// </summary>
    Reflinks,

    /// <summary>
    /// File change notification support.
    /// </summary>
    ChangeNotifications,

    /// <summary>
    /// Mandatory file locking support.
    /// </summary>
    MandatoryLocking,

    /// <summary>
    /// Advisory file locking support.
    /// </summary>
    AdvisoryLocking,

    /// <summary>
    /// Atomic rename operation support.
    /// </summary>
    AtomicRename,

    /// <summary>
    /// Memory-mapped file support.
    /// </summary>
    MemoryMappedFiles,

    /// <summary>
    /// Asynchronous I/O support.
    /// </summary>
    AsyncIO,

    /// <summary>
    /// Direct I/O (bypass page cache) support.
    /// </summary>
    DirectIO
}
