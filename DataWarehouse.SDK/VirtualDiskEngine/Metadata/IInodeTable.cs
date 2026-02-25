using DataWarehouse.SDK.Contracts;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Metadata;

/// <summary>
/// Interface for inode table operations.
/// Manages inode CRUD, directory entries, and extended attributes.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE Inode system (VDE-02)")]
public interface IInodeTable
{
    /// <summary>
    /// Allocates a new inode of the specified type.
    /// </summary>
    /// <param name="type">Type of inode to allocate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Newly allocated inode.</returns>
    Task<Inode> AllocateInodeAsync(InodeType type, CancellationToken ct = default);

    /// <summary>
    /// Retrieves an inode by number.
    /// </summary>
    /// <param name="inodeNumber">Inode number to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The inode, or null if it doesn't exist.</returns>
    Task<Inode?> GetInodeAsync(long inodeNumber, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing inode.
    /// </summary>
    /// <param name="inode">Inode to update.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateInodeAsync(Inode inode, CancellationToken ct = default);

    /// <summary>
    /// Frees an inode (marks it as deleted).
    /// </summary>
    /// <param name="inodeNumber">Inode number to free.</param>
    /// <param name="ct">Cancellation token.</param>
    Task FreeInodeAsync(long inodeNumber, CancellationToken ct = default);

    /// <summary>
    /// Reads all directory entries from a directory inode.
    /// </summary>
    /// <param name="directoryInodeNumber">Directory inode number.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of directory entries.</returns>
    Task<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(long directoryInodeNumber, CancellationToken ct = default);

    /// <summary>
    /// Adds a directory entry to a directory inode.
    /// </summary>
    /// <param name="directoryInodeNumber">Directory inode number.</param>
    /// <param name="entry">Entry to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddDirectoryEntryAsync(long directoryInodeNumber, DirectoryEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Removes a directory entry from a directory inode.
    /// </summary>
    /// <param name="directoryInodeNumber">Directory inode number.</param>
    /// <param name="name">Name of the entry to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveDirectoryEntryAsync(long directoryInodeNumber, string name, CancellationToken ct = default);

    /// <summary>
    /// Sets an extended attribute on an inode.
    /// </summary>
    /// <param name="inodeNumber">Inode number.</param>
    /// <param name="key">Attribute key.</param>
    /// <param name="value">Attribute value.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SetExtendedAttributeAsync(long inodeNumber, string key, byte[] value, CancellationToken ct = default);

    /// <summary>
    /// Gets an extended attribute from an inode.
    /// </summary>
    /// <param name="inodeNumber">Inode number.</param>
    /// <param name="key">Attribute key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Attribute value, or null if not found.</returns>
    Task<byte[]?> GetExtendedAttributeAsync(long inodeNumber, string key, CancellationToken ct = default);

    /// <summary>
    /// Lists all extended attributes for an inode.
    /// </summary>
    /// <param name="inodeNumber">Inode number.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary of extended attributes.</returns>
    Task<IReadOnlyDictionary<string, byte[]>> ListExtendedAttributesAsync(long inodeNumber, CancellationToken ct = default);

    /// <summary>
    /// Gets the total number of allocated inodes.
    /// </summary>
    long AllocatedInodeCount { get; }

    /// <summary>
    /// Gets the root directory inode (inode 1).
    /// </summary>
    Inode RootInode { get; }
}
