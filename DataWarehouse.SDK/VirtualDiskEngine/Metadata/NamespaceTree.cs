using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Metadata;

/// <summary>
/// Hierarchical namespace tree with path resolution and file/directory operations.
/// Provides POSIX-like filesystem semantics over the inode table.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE Inode system (VDE-02)")]
public sealed class NamespaceTree
{
    private const int MaxPathLength = 4096;
    private const int MaxComponentLength = 255;

    private readonly IInodeTable _inodeTable;

    /// <summary>
    /// Creates a new namespace tree.
    /// </summary>
    /// <param name="inodeTable">Inode table for metadata operations.</param>
    public NamespaceTree(IInodeTable inodeTable)
    {
        ArgumentNullException.ThrowIfNull(inodeTable);
        _inodeTable = inodeTable;
    }

    /// <summary>
    /// Resolves a path to its inode.
    /// </summary>
    /// <param name="path">Path to resolve (absolute, starting with '/').</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The inode, or null if the path doesn't exist.</returns>
    public async Task<Inode?> ResolvePathAsync(string path, CancellationToken ct = default)
    {
        ValidatePath(path);

        if (path == "/")
        {
            return _inodeTable.RootInode;
        }

        var components = SplitPath(path);
        var currentInode = _inodeTable.RootInode;

        foreach (var component in components)
        {
            if (currentInode.Type != InodeType.Directory)
            {
                return null; // Not a directory
            }

            var entries = await _inodeTable.ReadDirectoryAsync(currentInode.InodeNumber, ct);
            var entry = entries.FirstOrDefault(e => e.Name == component);

            if (entry == null)
            {
                return null; // Component not found
            }

            // Follow symlinks
            var nextInode = await _inodeTable.GetInodeAsync(entry.InodeNumber, ct);
            if (nextInode == null)
            {
                return null;
            }

            if (nextInode.Type == InodeType.SymLink)
            {
                if (string.IsNullOrEmpty(nextInode.SymLinkTarget))
                {
                    return null; // Invalid symlink
                }

                // Resolve symlink recursively
                nextInode = await ResolvePathAsync(nextInode.SymLinkTarget, ct);
                if (nextInode == null)
                {
                    return null; // Broken symlink
                }
            }

            currentInode = nextInode;
        }

        return currentInode;
    }

    /// <summary>
    /// Creates a new file at the specified path.
    /// </summary>
    /// <param name="path">Absolute path for the new file.</param>
    /// <param name="permissions">File permissions.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly created file inode.</returns>
    public async Task<Inode> CreateFileAsync(string path, InodePermissions permissions, CancellationToken ct = default)
    {
        ValidatePath(path);

        var (parentPath, fileName) = SplitParentAndName(path);
        var parentInode = await ResolvePathAsync(parentPath, ct);

        if (parentInode == null)
        {
            throw new DirectoryNotFoundException($"Parent directory '{parentPath}' not found.");
        }

        if (parentInode.Type != InodeType.Directory)
        {
            throw new InvalidOperationException($"'{parentPath}' is not a directory.");
        }

        // Check if file already exists
        var existing = await ResolvePathAsync(path, ct);
        if (existing != null)
        {
            throw new IOException($"File '{path}' already exists.");
        }

        // Create file inode
        var fileInode = await _inodeTable.AllocateInodeAsync(InodeType.File, ct);
        fileInode.Permissions = permissions;

        // Add directory entry
        var entry = new DirectoryEntry
        {
            InodeNumber = fileInode.InodeNumber,
            Type = InodeType.File,
            Name = fileName
        };

        await _inodeTable.AddDirectoryEntryAsync(parentInode.InodeNumber, entry, ct);

        return fileInode;
    }

    /// <summary>
    /// Creates a new directory at the specified path.
    /// </summary>
    /// <param name="path">Absolute path for the new directory.</param>
    /// <param name="permissions">Directory permissions.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly created directory inode.</returns>
    public async Task<Inode> CreateDirectoryAsync(string path, InodePermissions permissions, CancellationToken ct = default)
    {
        ValidatePath(path);

        var (parentPath, dirName) = SplitParentAndName(path);
        var parentInode = await ResolvePathAsync(parentPath, ct);

        if (parentInode == null)
        {
            throw new DirectoryNotFoundException($"Parent directory '{parentPath}' not found.");
        }

        if (parentInode.Type != InodeType.Directory)
        {
            throw new InvalidOperationException($"'{parentPath}' is not a directory.");
        }

        // Check if directory already exists
        var existing = await ResolvePathAsync(path, ct);
        if (existing != null)
        {
            throw new IOException($"Directory '{path}' already exists.");
        }

        // Create directory inode
        var dirInode = await _inodeTable.AllocateInodeAsync(InodeType.Directory, ct);
        dirInode.Permissions = permissions;
        dirInode.LinkCount = 2; // . and ..

        // Add . and .. entries
        var dotEntry = new DirectoryEntry
        {
            InodeNumber = dirInode.InodeNumber,
            Type = InodeType.Directory,
            Name = "."
        };

        var dotDotEntry = new DirectoryEntry
        {
            InodeNumber = parentInode.InodeNumber,
            Type = InodeType.Directory,
            Name = ".."
        };

        await _inodeTable.AddDirectoryEntryAsync(dirInode.InodeNumber, dotEntry, ct);
        await _inodeTable.AddDirectoryEntryAsync(dirInode.InodeNumber, dotDotEntry, ct);

        // Add entry to parent directory
        var entry = new DirectoryEntry
        {
            InodeNumber = dirInode.InodeNumber,
            Type = InodeType.Directory,
            Name = dirName
        };

        await _inodeTable.AddDirectoryEntryAsync(parentInode.InodeNumber, entry, ct);

        // Increment parent link count (for .. reference)
        parentInode.LinkCount++;
        await _inodeTable.UpdateInodeAsync(parentInode, ct);

        return dirInode;
    }

    /// <summary>
    /// Deletes a file or directory at the specified path.
    /// </summary>
    /// <param name="path">Absolute path to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        ValidatePath(path);

        if (path == "/")
        {
            throw new InvalidOperationException("Cannot delete root directory.");
        }

        var inode = await ResolvePathAsync(path, ct);
        if (inode == null)
        {
            throw new FileNotFoundException($"Path '{path}' not found.");
        }

        var (parentPath, name) = SplitParentAndName(path);
        var parentInode = await ResolvePathAsync(parentPath, ct);

        if (parentInode == null)
        {
            throw new DirectoryNotFoundException($"Parent directory '{parentPath}' not found.");
        }

        // Check if directory is empty (except . and ..)
        if (inode.Type == InodeType.Directory)
        {
            var entries = await _inodeTable.ReadDirectoryAsync(inode.InodeNumber, ct);
            var nonDotEntries = entries.Where(e => e.Name != "." && e.Name != "..").ToList();

            if (nonDotEntries.Any())
            {
                throw new IOException($"Directory '{path}' is not empty.");
            }

            // Decrement parent link count
            parentInode.LinkCount--;
            await _inodeTable.UpdateInodeAsync(parentInode, ct);
        }

        // Remove directory entry from parent
        await _inodeTable.RemoveDirectoryEntryAsync(parentInode.InodeNumber, name, ct);

        // Decrement link count
        inode.LinkCount--;
        if (inode.LinkCount == 0)
        {
            // Free inode (data blocks would be freed here in a full implementation)
            await _inodeTable.FreeInodeAsync(inode.InodeNumber, ct);
        }
        else
        {
            await _inodeTable.UpdateInodeAsync(inode, ct);
        }
    }

    /// <summary>
    /// Creates a hard link to an existing file.
    /// </summary>
    /// <param name="existingPath">Path to the existing file.</param>
    /// <param name="newPath">Path for the new link.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task HardLinkAsync(string existingPath, string newPath, CancellationToken ct = default)
    {
        ValidatePath(existingPath);
        ValidatePath(newPath);

        var existingInode = await ResolvePathAsync(existingPath, ct);
        if (existingInode == null)
        {
            throw new FileNotFoundException($"Source path '{existingPath}' not found.");
        }

        if (existingInode.Type == InodeType.Directory)
        {
            throw new InvalidOperationException("Cannot hard link directories.");
        }

        var (parentPath, linkName) = SplitParentAndName(newPath);
        var parentInode = await ResolvePathAsync(parentPath, ct);

        if (parentInode == null)
        {
            throw new DirectoryNotFoundException($"Parent directory '{parentPath}' not found.");
        }

        if (parentInode.Type != InodeType.Directory)
        {
            throw new InvalidOperationException($"'{parentPath}' is not a directory.");
        }

        // Check if target already exists
        var existing = await ResolvePathAsync(newPath, ct);
        if (existing != null)
        {
            throw new IOException($"Path '{newPath}' already exists.");
        }

        // Add directory entry pointing to existing inode
        var entry = new DirectoryEntry
        {
            InodeNumber = existingInode.InodeNumber,
            Type = existingInode.Type,
            Name = linkName
        };

        await _inodeTable.AddDirectoryEntryAsync(parentInode.InodeNumber, entry, ct);

        // Increment link count
        existingInode.LinkCount++;
        await _inodeTable.UpdateInodeAsync(existingInode, ct);
    }

    /// <summary>
    /// Creates a symbolic link.
    /// </summary>
    /// <param name="targetPath">Target path for the symlink.</param>
    /// <param name="linkPath">Path where the symlink will be created.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The symlink inode.</returns>
    public async Task<Inode> SymLinkAsync(string targetPath, string linkPath, CancellationToken ct = default)
    {
        ValidatePath(linkPath);
        ArgumentNullException.ThrowIfNull(targetPath);

        var (parentPath, linkName) = SplitParentAndName(linkPath);
        var parentInode = await ResolvePathAsync(parentPath, ct);

        if (parentInode == null)
        {
            throw new DirectoryNotFoundException($"Parent directory '{parentPath}' not found.");
        }

        if (parentInode.Type != InodeType.Directory)
        {
            throw new InvalidOperationException($"'{parentPath}' is not a directory.");
        }

        // Check if link already exists
        var existing = await ResolvePathAsync(linkPath, ct);
        if (existing != null)
        {
            throw new IOException($"Path '{linkPath}' already exists.");
        }

        // Create symlink inode
        var symlinkInode = await _inodeTable.AllocateInodeAsync(InodeType.SymLink, ct);
        symlinkInode.SymLinkTarget = targetPath;
        symlinkInode.Size = Encoding.UTF8.GetByteCount(targetPath);
        await _inodeTable.UpdateInodeAsync(symlinkInode, ct);

        // Add directory entry
        var entry = new DirectoryEntry
        {
            InodeNumber = symlinkInode.InodeNumber,
            Type = InodeType.SymLink,
            Name = linkName
        };

        await _inodeTable.AddDirectoryEntryAsync(parentInode.InodeNumber, entry, ct);

        return symlinkInode;
    }

    /// <summary>
    /// Renames a file or directory (inode-stable operation).
    /// </summary>
    /// <param name="oldPath">Current path.</param>
    /// <param name="newPath">New path.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
    {
        ValidatePath(oldPath);
        ValidatePath(newPath);

        if (oldPath == "/")
        {
            throw new InvalidOperationException("Cannot rename root directory.");
        }

        var inode = await ResolvePathAsync(oldPath, ct);
        if (inode == null)
        {
            throw new FileNotFoundException($"Source path '{oldPath}' not found.");
        }

        var (oldParentPath, oldName) = SplitParentAndName(oldPath);
        var (newParentPath, newName) = SplitParentAndName(newPath);

        var oldParentInode = await ResolvePathAsync(oldParentPath, ct);
        var newParentInode = await ResolvePathAsync(newParentPath, ct);

        if (oldParentInode == null || newParentInode == null)
        {
            throw new DirectoryNotFoundException("Parent directory not found.");
        }

        // Check if target already exists
        var existingTarget = await ResolvePathAsync(newPath, ct);
        if (existingTarget != null)
        {
            throw new IOException($"Target path '{newPath}' already exists.");
        }

        // Remove old entry
        await _inodeTable.RemoveDirectoryEntryAsync(oldParentInode.InodeNumber, oldName, ct);

        // Add new entry (inode number stays the same)
        var entry = new DirectoryEntry
        {
            InodeNumber = inode.InodeNumber,
            Type = inode.Type,
            Name = newName
        };

        await _inodeTable.AddDirectoryEntryAsync(newParentInode.InodeNumber, entry, ct);

        // Update .. reference if directory moved to different parent
        if (inode.Type == InodeType.Directory && oldParentInode.InodeNumber != newParentInode.InodeNumber)
        {
            var entries = await _inodeTable.ReadDirectoryAsync(inode.InodeNumber, ct);
            await _inodeTable.RemoveDirectoryEntryAsync(inode.InodeNumber, "..", ct);

            var dotDotEntry = new DirectoryEntry
            {
                InodeNumber = newParentInode.InodeNumber,
                Type = InodeType.Directory,
                Name = ".."
            };
            await _inodeTable.AddDirectoryEntryAsync(inode.InodeNumber, dotDotEntry, ct);

            // Update parent link counts
            oldParentInode.LinkCount--;
            newParentInode.LinkCount++;
            await _inodeTable.UpdateInodeAsync(oldParentInode, ct);
            await _inodeTable.UpdateInodeAsync(newParentInode, ct);
        }
    }

    /// <summary>
    /// Gets file/directory metadata (stat).
    /// </summary>
    /// <param name="path">Path to stat.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The inode, or null if the path doesn't exist.</returns>
    public async Task<Inode?> StatAsync(string path, CancellationToken ct = default)
    {
        return await ResolvePathAsync(path, ct);
    }

    /// <summary>
    /// Lists directory contents.
    /// </summary>
    /// <param name="path">Directory path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of directory entries.</returns>
    public async Task<IReadOnlyList<DirectoryEntry>> ListDirectoryAsync(string path, CancellationToken ct = default)
    {
        var inode = await ResolvePathAsync(path, ct);
        if (inode == null)
        {
            throw new DirectoryNotFoundException($"Directory '{path}' not found.");
        }

        if (inode.Type != InodeType.Directory)
        {
            throw new InvalidOperationException($"'{path}' is not a directory.");
        }

        return await _inodeTable.ReadDirectoryAsync(inode.InodeNumber, ct);
    }

    /// <summary>
    /// Validates a path.
    /// </summary>
    private static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("Path cannot be null or empty.", nameof(path));
        }

        if (!path.StartsWith('/'))
        {
            throw new ArgumentException("Path must be absolute (start with '/').", nameof(path));
        }

        if (Encoding.UTF8.GetByteCount(path) > MaxPathLength)
        {
            throw new ArgumentException($"Path exceeds maximum length of {MaxPathLength} bytes.", nameof(path));
        }

        var components = SplitPath(path);
        foreach (var component in components)
        {
            if (Encoding.UTF8.GetByteCount(component) > MaxComponentLength)
            {
                throw new ArgumentException($"Path component '{component}' exceeds maximum length of {MaxComponentLength} bytes.", nameof(path));
            }

            if (component.Contains('\0'))
            {
                throw new ArgumentException("Path components cannot contain null characters.", nameof(path));
            }
        }
    }

    /// <summary>
    /// Splits a path into components.
    /// </summary>
    private static string[] SplitPath(string path)
    {
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Splits a path into parent path and file/directory name.
    /// </summary>
    private static (string parentPath, string name) SplitParentAndName(string path)
    {
        int lastSlash = path.LastIndexOf('/');
        if (lastSlash == 0)
        {
            return ("/", path.Substring(1));
        }

        return (path.Substring(0, lastSlash), path.Substring(lastSlash + 1));
    }
}
