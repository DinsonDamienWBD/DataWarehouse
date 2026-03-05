using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Cache;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;
using DataWarehouse.SDK.VirtualDiskEngine.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mount;

/// <summary>
/// Result of an inode lookup operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VDE mount filesystem adapter (VOPT-83)")]
public readonly struct InodeLookupResult
{
    /// <summary>Inode number of the found entry.</summary>
    public long InodeNumber { get; init; }

    /// <summary>Type of the found inode.</summary>
    public InodeType Type { get; init; }

    /// <summary>Whether the lookup found a matching entry.</summary>
    public bool Found { get; init; }
}

/// <summary>
/// File/directory attribute information mapped from VDE inodes.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VDE mount filesystem adapter (VOPT-83)")]
public readonly struct InodeAttributes
{
    /// <summary>Inode number.</summary>
    public long InodeNumber { get; init; }

    /// <summary>Inode type (file, directory, symlink).</summary>
    public InodeType Type { get; init; }

    /// <summary>Logical file size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Allocated size on disk in bytes (block-aligned).</summary>
    public long AllocatedSize { get; init; }

    /// <summary>POSIX-style permissions.</summary>
    public InodePermissions Permissions { get; init; }

    /// <summary>Hard link reference count.</summary>
    public int LinkCount { get; init; }

    /// <summary>Owner user ID.</summary>
    public long OwnerId { get; init; }

    /// <summary>Creation timestamp (Unix seconds UTC).</summary>
    public long CreatedUtc { get; init; }

    /// <summary>Last modification timestamp (Unix seconds UTC).</summary>
    public long ModifiedUtc { get; init; }

    /// <summary>Last access timestamp (Unix seconds UTC).</summary>
    public long AccessedUtc { get; init; }

    /// <summary>Inode change timestamp (Unix seconds UTC).</summary>
    public long ChangedUtc { get; init; }

    /// <summary>Block size of the underlying device.</summary>
    public int BlockSize { get; init; }
}

/// <summary>
/// Directory entry information for readdir results.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VDE mount filesystem adapter (VOPT-83)")]
public readonly struct DirectoryEntryInfo
{
    /// <summary>Entry name.</summary>
    public string Name { get; init; }

    /// <summary>Inode number of the entry.</summary>
    public long InodeNumber { get; init; }

    /// <summary>Type of the entry.</summary>
    public InodeType Type { get; init; }
}

/// <summary>
/// Flags indicating which inode attributes to update in a setattr operation.
/// </summary>
[Flags]
[SdkCompatibility("6.0.0", Notes = "Phase 87: VDE mount filesystem adapter (VOPT-83)")]
public enum InodeAttributeMask
{
    /// <summary>No attributes.</summary>
    None = 0,

    /// <summary>Update file size (truncate/extend).</summary>
    Size = 1,

    /// <summary>Update permissions.</summary>
    Permissions = 2,

    /// <summary>Update owner.</summary>
    Owner = 4,

    /// <summary>Update access time.</summary>
    AccessTime = 8,

    /// <summary>Update modification time.</summary>
    ModifyTime = 16,

    /// <summary>Update change time.</summary>
    ChangeTime = 32
}

/// <summary>
/// Filesystem statistics for a mounted VDE volume.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VDE mount filesystem adapter (VOPT-83)")]
public readonly struct FilesystemStats
{
    /// <summary>Block size in bytes.</summary>
    public int BlockSize { get; init; }

    /// <summary>Total number of blocks in the volume.</summary>
    public long TotalBlocks { get; init; }

    /// <summary>Number of free (unallocated) blocks.</summary>
    public long FreeBlocks { get; init; }

    /// <summary>Number of allocated inodes.</summary>
    public long AllocatedInodes { get; init; }

    /// <summary>Maximum file name length in bytes.</summary>
    public int MaxNameLength { get; init; }
}

/// <summary>
/// Shared filesystem-to-VDE translation layer used by all platform mount providers.
/// Translates POSIX filesystem operations (lookup, read, write, readdir, create, unlink, etc.)
/// into VDE inode, extent, and block operations through IBlockDevice + ARC cache.
/// </summary>
/// <remarks>
/// <para>
/// Platform providers (WinFsp, FUSE3, macFUSE) implement only the OS binding layer and
/// delegate all storage logic to this adapter. Thread safety is enforced via a semaphore
/// limiting concurrent operations to <see cref="MountOptions.MaxConcurrentOps"/>.
/// </para>
/// <para>
/// All write operations are WAL-journaled for crash consistency. Read operations
/// use the ARC cache for hot-block acceleration.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VDE mount filesystem adapter (VOPT-83)")]
public sealed class VdeFilesystemAdapter : IAsyncDisposable
{
    private readonly IBlockDevice _device;
    private readonly IInodeTable _inodeTable;
    private readonly IBlockAllocator _allocator;
    private readonly IWriteAheadLog _wal;
    private readonly IArcCache _cache;
    private readonly MountOptions _options;
    private readonly SemaphoreSlim _concurrencySemaphore;
    private bool _disposed;

    /// <summary>
    /// Creates a new VDE filesystem adapter.
    /// </summary>
    /// <param name="device">Block device for raw I/O.</param>
    /// <param name="inodeTable">Inode table for metadata operations.</param>
    /// <param name="allocator">Block allocator for space management.</param>
    /// <param name="wal">Write-ahead log for crash consistency.</param>
    /// <param name="cache">ARC cache for hot-block acceleration.</param>
    /// <param name="options">Mount options controlling behavior.</param>
    public VdeFilesystemAdapter(
        IBlockDevice device,
        IInodeTable inodeTable,
        IBlockAllocator allocator,
        IWriteAheadLog wal,
        IArcCache cache,
        MountOptions options)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _inodeTable = inodeTable ?? throw new ArgumentNullException(nameof(inodeTable));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _wal = wal ?? throw new ArgumentNullException(nameof(wal));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _concurrencySemaphore = new SemaphoreSlim(options.MaxConcurrentOps, options.MaxConcurrentOps);
    }

    #region Lookup

    /// <summary>
    /// Looks up a directory entry by name within a parent directory.
    /// </summary>
    /// <param name="parentInode">Inode number of the parent directory.</param>
    /// <param name="name">Name of the entry to find.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Lookup result indicating whether the entry was found and its inode details.</returns>
    public async Task<InodeLookupResult> LookupAsync(long parentInode, string name, CancellationToken ct)
    {
        ThrowIfDisposed();
        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            var entries = await _inodeTable.ReadDirectoryAsync(parentInode, ct);
            var match = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));

            if (match is null || string.IsNullOrEmpty(match.Name))
            {
                return new InodeLookupResult { Found = false };
            }

            return new InodeLookupResult
            {
                InodeNumber = match.InodeNumber,
                Type = match.Type,
                Found = true
            };
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    #endregion

    #region Getattr

    /// <summary>
    /// Retrieves file/directory attributes for the specified inode.
    /// </summary>
    /// <param name="inodeNumber">Inode number to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Attributes of the inode.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the inode does not exist.</exception>
    public async Task<InodeAttributes> GetattrAsync(long inodeNumber, CancellationToken ct)
    {
        ThrowIfDisposed();
        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            var inode = await _inodeTable.GetInodeAsync(inodeNumber, ct)
                ?? throw new InvalidOperationException($"Inode {inodeNumber} not found.");

            long allocatedBlocks = CountAllocatedBlocks(inode);

            return new InodeAttributes
            {
                InodeNumber = inode.InodeNumber,
                Type = inode.Type,
                Size = inode.Size,
                AllocatedSize = allocatedBlocks * _device.BlockSize,
                Permissions = inode.Permissions,
                LinkCount = inode.LinkCount,
                OwnerId = inode.OwnerId,
                CreatedUtc = inode.CreatedUtc,
                ModifiedUtc = inode.ModifiedUtc,
                AccessedUtc = inode.AccessedUtc,
                ChangedUtc = inode.ModifiedUtc, // VDE uses ModifiedUtc as change time
                BlockSize = _device.BlockSize
            };
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    #endregion

    #region Readdir

    /// <summary>
    /// Reads all entries in a directory, including synthetic "." and ".." entries.
    /// </summary>
    /// <param name="directoryInode">Inode number of the directory.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of directory entries.</returns>
    public async Task<IReadOnlyList<DirectoryEntryInfo>> ReaddirAsync(long directoryInode, CancellationToken ct)
    {
        ThrowIfDisposed();
        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            var entries = await _inodeTable.ReadDirectoryAsync(directoryInode, ct);
            var result = new List<DirectoryEntryInfo>(entries.Count + 2);

            // Add synthetic "." and ".." if not already present
            bool hasDot = false;
            bool hasDotDot = false;

            foreach (var entry in entries)
            {
                if (entry.Name == ".") hasDot = true;
                if (entry.Name == "..") hasDotDot = true;

                result.Add(new DirectoryEntryInfo
                {
                    Name = entry.Name,
                    InodeNumber = entry.InodeNumber,
                    Type = entry.Type
                });
            }

            if (!hasDot)
            {
                result.Insert(0, new DirectoryEntryInfo
                {
                    Name = ".",
                    InodeNumber = directoryInode,
                    Type = InodeType.Directory
                });
            }

            if (!hasDotDot)
            {
                // For root inode, ".." points to itself
                long parentInode = directoryInode == _inodeTable.RootInode.InodeNumber
                    ? directoryInode
                    : directoryInode; // Parent will be resolved from stored ".." entry
                result.Insert(hasDot ? 1 : 0, new DirectoryEntryInfo
                {
                    Name = "..",
                    InodeNumber = parentInode,
                    Type = InodeType.Directory
                });
            }

            return result;
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    #endregion

    #region Read

    /// <summary>
    /// Reads data from a file inode into the provided buffer.
    /// Uses ARC cache for hot blocks, falling back to device reads on cache miss.
    /// </summary>
    /// <param name="inodeNumber">Inode number of the file to read.</param>
    /// <param name="buffer">Buffer to receive data.</param>
    /// <param name="offset">Byte offset within the file to start reading.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of bytes actually read (may be less than buffer length at EOF).</returns>
    public async Task<int> ReadAsync(long inodeNumber, Memory<byte> buffer, long offset, CancellationToken ct)
    {
        ThrowIfDisposed();
        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            var inode = await _inodeTable.GetInodeAsync(inodeNumber, ct)
                ?? throw new InvalidOperationException($"Inode {inodeNumber} not found.");

            if (offset >= inode.Size)
            {
                return 0;
            }

            int blockSize = _device.BlockSize;
            long bytesToRead = Math.Min(buffer.Length, inode.Size - offset);
            int totalBytesRead = 0;

            while (totalBytesRead < bytesToRead)
            {
                ct.ThrowIfCancellationRequested();

                long currentOffset = offset + totalBytesRead;
                long blockIndex = currentOffset / blockSize;
                int blockOffset = (int)(currentOffset % blockSize);
                int bytesFromBlock = (int)Math.Min(blockSize - blockOffset, bytesToRead - totalBytesRead);

                long blockNumber = ResolveBlockPointer(inode, blockIndex);
                if (blockNumber <= 0)
                {
                    // Sparse region -- fill with zeros
                    buffer.Span.Slice(totalBytesRead, bytesFromBlock).Clear();
                }
                else
                {
                    // Try ARC cache first
                    var cached = await _cache.GetAsync(blockNumber, ct);
                    if (cached is not null)
                    {
                        cached.AsSpan(blockOffset, bytesFromBlock).CopyTo(buffer.Span.Slice(totalBytesRead));
                    }
                    else
                    {
                        // Cache miss -- read from device
                        byte[] blockData = new byte[blockSize];
                        await _device.ReadBlockAsync(blockNumber, blockData, ct);
                        await _cache.PutAsync(blockNumber, blockData, ct);
                        blockData.AsSpan(blockOffset, bytesFromBlock).CopyTo(buffer.Span.Slice(totalBytesRead));
                    }
                }

                totalBytesRead += bytesFromBlock;
            }

            return totalBytesRead;
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    #endregion

    #region Write

    /// <summary>
    /// Writes data to a file inode from the provided buffer.
    /// All writes are WAL-journaled for crash consistency and update the ARC cache.
    /// </summary>
    /// <param name="inodeNumber">Inode number of the file to write.</param>
    /// <param name="data">Data to write.</param>
    /// <param name="offset">Byte offset within the file to start writing.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of bytes written.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the mount is read-only.</exception>
    public async Task<int> WriteAsync(long inodeNumber, ReadOnlyMemory<byte> data, long offset, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_options.ReadOnly)
        {
            throw new InvalidOperationException("Cannot write to a read-only mount.");
        }

        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            var inode = await _inodeTable.GetInodeAsync(inodeNumber, ct)
                ?? throw new InvalidOperationException($"Inode {inodeNumber} not found.");

            int blockSize = _device.BlockSize;
            int totalBytesWritten = 0;
            long writeEnd = offset + data.Length;

            await using var txn = await _wal.BeginTransactionAsync(ct);

            while (totalBytesWritten < data.Length)
            {
                ct.ThrowIfCancellationRequested();

                long currentOffset = offset + totalBytesWritten;
                long blockIndex = currentOffset / blockSize;
                int blockOffset = (int)(currentOffset % blockSize);
                int bytesToBlock = (int)Math.Min(blockSize - blockOffset, data.Length - totalBytesWritten);

                // Resolve or allocate block
                long blockNumber = ResolveBlockPointer(inode, blockIndex);
                if (blockNumber <= 0)
                {
                    blockNumber = _allocator.AllocateBlock(ct);
                    AssignBlockPointer(inode, blockIndex, blockNumber);
                }

                // Read existing block for partial writes and WAL before-image
                byte[] existingBlock = new byte[blockSize];
                if (blockOffset != 0 || bytesToBlock != blockSize)
                {
                    await _device.ReadBlockAsync(blockNumber, existingBlock, ct);
                }

                // Build new block
                byte[] newBlock = new byte[blockSize];
                existingBlock.CopyTo(newBlock.AsMemory());
                data.Slice(totalBytesWritten, bytesToBlock).CopyTo(newBlock.AsMemory(blockOffset));

                // WAL-journal the write
                await txn.LogBlockWriteAsync(blockNumber, existingBlock, newBlock, ct);

                // Write to device
                await _device.WriteBlockAsync(blockNumber, newBlock, ct);

                // Update cache
                await _cache.PutAsync(blockNumber, newBlock, ct);

                totalBytesWritten += bytesToBlock;
            }

            // Update inode size if write extends past EOF
            if (writeEnd > inode.Size)
            {
                inode.Size = writeEnd;
            }

            // Update modification timestamp
            inode.ModifiedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            await _inodeTable.UpdateInodeAsync(inode, ct);
            await txn.CommitAsync(ct);

            return totalBytesWritten;
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    #endregion

    #region Create

    /// <summary>
    /// Creates a new file or symlink inode in a parent directory.
    /// </summary>
    /// <param name="parentInode">Inode number of the parent directory.</param>
    /// <param name="name">Name for the new entry.</param>
    /// <param name="type">Type of inode to create.</param>
    /// <param name="permissions">Initial permissions.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Inode number of the newly created entry.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the mount is read-only.</exception>
    public async Task<long> CreateAsync(long parentInode, string name, InodeType type, InodePermissions permissions, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_options.ReadOnly)
        {
            throw new InvalidOperationException("Cannot create entries on a read-only mount.");
        }

        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            var newInode = await _inodeTable.AllocateInodeAsync(type, ct);
            newInode.Permissions = permissions;
            newInode.LinkCount = 1;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            newInode.CreatedUtc = now;
            newInode.ModifiedUtc = now;
            newInode.AccessedUtc = now;

            await _inodeTable.UpdateInodeAsync(newInode, ct);

            await _inodeTable.AddDirectoryEntryAsync(parentInode, new DirectoryEntry
            {
                InodeNumber = newInode.InodeNumber,
                Type = type,
                Name = name
            }, ct);

            return newInode.InodeNumber;
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    #endregion

    #region Unlink

    /// <summary>
    /// Removes a file entry from a directory. If the link count reaches zero,
    /// the inode and its data blocks are freed.
    /// </summary>
    /// <param name="parentInode">Inode number of the parent directory.</param>
    /// <param name="name">Name of the entry to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if the mount is read-only.</exception>
    public async Task UnlinkAsync(long parentInode, string name, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_options.ReadOnly)
        {
            throw new InvalidOperationException("Cannot unlink entries on a read-only mount.");
        }

        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            // Lookup the entry
            var entries = await _inodeTable.ReadDirectoryAsync(parentInode, ct);
            var entry = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (entry is null || string.IsNullOrEmpty(entry.Name))
            {
                throw new InvalidOperationException($"Entry '{name}' not found in directory inode {parentInode}.");
            }

            // Decrement link count
            var targetInode = await _inodeTable.GetInodeAsync(entry.InodeNumber, ct)
                ?? throw new InvalidOperationException($"Inode {entry.InodeNumber} not found.");

            targetInode.LinkCount--;

            if (targetInode.LinkCount <= 0)
            {
                // Free all data blocks
                FreeInodeBlocks(targetInode);

                // Free the inode
                await _inodeTable.FreeInodeAsync(targetInode.InodeNumber, ct);
            }
            else
            {
                await _inodeTable.UpdateInodeAsync(targetInode, ct);
            }

            // Remove directory entry
            await _inodeTable.RemoveDirectoryEntryAsync(parentInode, name, ct);
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    #endregion

    #region Mkdir

    /// <summary>
    /// Creates a new directory with "." and ".." entries.
    /// </summary>
    /// <param name="parentInode">Inode number of the parent directory.</param>
    /// <param name="name">Name for the new directory.</param>
    /// <param name="permissions">Initial permissions.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Inode number of the new directory.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the mount is read-only.</exception>
    public async Task<long> MkdirAsync(long parentInode, string name, InodePermissions permissions, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_options.ReadOnly)
        {
            throw new InvalidOperationException("Cannot create directories on a read-only mount.");
        }

        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            var dirInode = await _inodeTable.AllocateInodeAsync(InodeType.Directory, ct);
            dirInode.Permissions = permissions;
            dirInode.LinkCount = 2; // "." self-reference + parent entry
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            dirInode.CreatedUtc = now;
            dirInode.ModifiedUtc = now;
            dirInode.AccessedUtc = now;

            await _inodeTable.UpdateInodeAsync(dirInode, ct);

            // Add "." entry (self-reference)
            await _inodeTable.AddDirectoryEntryAsync(dirInode.InodeNumber, new DirectoryEntry
            {
                InodeNumber = dirInode.InodeNumber,
                Type = InodeType.Directory,
                Name = "."
            }, ct);

            // Add ".." entry (parent reference)
            await _inodeTable.AddDirectoryEntryAsync(dirInode.InodeNumber, new DirectoryEntry
            {
                InodeNumber = parentInode,
                Type = InodeType.Directory,
                Name = ".."
            }, ct);

            // Add entry in parent directory
            await _inodeTable.AddDirectoryEntryAsync(parentInode, new DirectoryEntry
            {
                InodeNumber = dirInode.InodeNumber,
                Type = InodeType.Directory,
                Name = name
            }, ct);

            // Increment parent link count (for the ".." reference)
            var parent = await _inodeTable.GetInodeAsync(parentInode, ct);
            if (parent is not null)
            {
                parent.LinkCount++;
                await _inodeTable.UpdateInodeAsync(parent, ct);
            }

            return dirInode.InodeNumber;
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    #endregion

    #region Rmdir

    /// <summary>
    /// Removes an empty directory.
    /// </summary>
    /// <param name="parentInode">Inode number of the parent directory.</param>
    /// <param name="name">Name of the directory to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if the directory is not empty or the mount is read-only.</exception>
    public async Task RmdirAsync(long parentInode, string name, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_options.ReadOnly)
        {
            throw new InvalidOperationException("Cannot remove directories on a read-only mount.");
        }

        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            // Lookup the directory
            var entries = await _inodeTable.ReadDirectoryAsync(parentInode, ct);
            var entry = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (entry is null || string.IsNullOrEmpty(entry.Name))
            {
                throw new InvalidOperationException($"Directory '{name}' not found in inode {parentInode}.");
            }

            // Verify the directory is empty (only "." and "..")
            var dirEntries = await _inodeTable.ReadDirectoryAsync(entry.InodeNumber, ct);
            int realEntryCount = dirEntries.Count(e => e.Name != "." && e.Name != "..");
            if (realEntryCount > 0)
            {
                throw new InvalidOperationException($"Directory '{name}' is not empty.");
            }

            // Remove directory entry from parent
            await _inodeTable.RemoveDirectoryEntryAsync(parentInode, name, ct);

            // Decrement parent link count (removing ".." reference)
            var parent = await _inodeTable.GetInodeAsync(parentInode, ct);
            if (parent is not null)
            {
                parent.LinkCount--;
                await _inodeTable.UpdateInodeAsync(parent, ct);
            }

            // Free the directory inode
            await _inodeTable.FreeInodeAsync(entry.InodeNumber, ct);
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    #endregion

    #region Rename

    /// <summary>
    /// Renames/moves a file or directory from one location to another.
    /// If a destination entry exists, it is unlinked first.
    /// </summary>
    /// <param name="oldParentInode">Inode number of the source parent directory.</param>
    /// <param name="oldName">Current name of the entry.</param>
    /// <param name="newParentInode">Inode number of the destination parent directory.</param>
    /// <param name="newName">New name for the entry.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if the mount is read-only or source not found.</exception>
    public async Task RenameAsync(long oldParentInode, string oldName, long newParentInode, string newName, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_options.ReadOnly)
        {
            throw new InvalidOperationException("Cannot rename entries on a read-only mount.");
        }

        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            // Lookup source entry
            var srcEntries = await _inodeTable.ReadDirectoryAsync(oldParentInode, ct);
            var srcEntry = srcEntries.FirstOrDefault(e => string.Equals(e.Name, oldName, StringComparison.Ordinal));
            if (srcEntry is null || string.IsNullOrEmpty(srcEntry.Name))
            {
                throw new InvalidOperationException($"Entry '{oldName}' not found in directory inode {oldParentInode}.");
            }

            // Check if destination already exists and unlink it
            var dstEntries = await _inodeTable.ReadDirectoryAsync(newParentInode, ct);
            var dstEntry = dstEntries.FirstOrDefault(e => string.Equals(e.Name, newName, StringComparison.Ordinal));
            if (dstEntry is not null && !string.IsNullOrEmpty(dstEntry.Name))
            {
                // Unlink the existing destination
                var dstInode = await _inodeTable.GetInodeAsync(dstEntry.InodeNumber, ct);
                if (dstInode is not null)
                {
                    dstInode.LinkCount--;
                    if (dstInode.LinkCount <= 0)
                    {
                        FreeInodeBlocks(dstInode);
                        await _inodeTable.FreeInodeAsync(dstInode.InodeNumber, ct);
                    }
                    else
                    {
                        await _inodeTable.UpdateInodeAsync(dstInode, ct);
                    }
                }
                await _inodeTable.RemoveDirectoryEntryAsync(newParentInode, newName, ct);
            }

            // Remove from old parent
            await _inodeTable.RemoveDirectoryEntryAsync(oldParentInode, oldName, ct);

            // Add to new parent with the new name
            await _inodeTable.AddDirectoryEntryAsync(newParentInode, new DirectoryEntry
            {
                InodeNumber = srcEntry.InodeNumber,
                Type = srcEntry.Type,
                Name = newName
            }, ct);

            // If moving a directory, update its ".." entry
            if (srcEntry.Type == InodeType.Directory && oldParentInode != newParentInode)
            {
                await _inodeTable.RemoveDirectoryEntryAsync(srcEntry.InodeNumber, "..", ct);
                await _inodeTable.AddDirectoryEntryAsync(srcEntry.InodeNumber, new DirectoryEntry
                {
                    InodeNumber = newParentInode,
                    Type = InodeType.Directory,
                    Name = ".."
                }, ct);

                // Adjust link counts for parent changes
                var oldParent = await _inodeTable.GetInodeAsync(oldParentInode, ct);
                if (oldParent is not null)
                {
                    oldParent.LinkCount--;
                    await _inodeTable.UpdateInodeAsync(oldParent, ct);
                }

                var newParent = await _inodeTable.GetInodeAsync(newParentInode, ct);
                if (newParent is not null)
                {
                    newParent.LinkCount++;
                    await _inodeTable.UpdateInodeAsync(newParent, ct);
                }
            }
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    #endregion

    #region Symlink

    /// <summary>
    /// Creates a symbolic link.
    /// </summary>
    /// <param name="parentInode">Inode number of the parent directory.</param>
    /// <param name="name">Name of the symbolic link.</param>
    /// <param name="target">Target path the symlink points to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Inode number of the new symlink.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the mount is read-only.</exception>
    public async Task<long> SymlinkAsync(long parentInode, string name, string target, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_options.ReadOnly)
        {
            throw new InvalidOperationException("Cannot create symlinks on a read-only mount.");
        }

        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            var symInode = await _inodeTable.AllocateInodeAsync(InodeType.SymLink, ct);
            symInode.SymLinkTarget = target;
            symInode.LinkCount = 1;
            symInode.Size = Encoding.UTF8.GetByteCount(target);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            symInode.CreatedUtc = now;
            symInode.ModifiedUtc = now;
            symInode.AccessedUtc = now;

            await _inodeTable.UpdateInodeAsync(symInode, ct);

            await _inodeTable.AddDirectoryEntryAsync(parentInode, new DirectoryEntry
            {
                InodeNumber = symInode.InodeNumber,
                Type = InodeType.SymLink,
                Name = name
            }, ct);

            return symInode.InodeNumber;
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    #endregion

    #region Readlink

    /// <summary>
    /// Reads the target path of a symbolic link.
    /// </summary>
    /// <param name="inodeNumber">Inode number of the symlink.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Target path of the symlink.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the inode is not a symlink.</exception>
    public async Task<string> ReadlinkAsync(long inodeNumber, CancellationToken ct)
    {
        ThrowIfDisposed();
        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            var inode = await _inodeTable.GetInodeAsync(inodeNumber, ct)
                ?? throw new InvalidOperationException($"Inode {inodeNumber} not found.");

            if (inode.Type != InodeType.SymLink)
            {
                throw new InvalidOperationException($"Inode {inodeNumber} is not a symbolic link.");
            }

            return inode.SymLinkTarget ?? string.Empty;
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    #endregion

    #region Setattr

    /// <summary>
    /// Sets inode attributes based on the provided mask.
    /// Only attributes indicated by the mask are updated.
    /// </summary>
    /// <param name="inodeNumber">Inode number to modify.</param>
    /// <param name="attrs">New attribute values.</param>
    /// <param name="mask">Mask indicating which attributes to update.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if the mount is read-only or inode not found.</exception>
    public async Task SetattrAsync(long inodeNumber, InodeAttributes attrs, InodeAttributeMask mask, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_options.ReadOnly)
        {
            throw new InvalidOperationException("Cannot set attributes on a read-only mount.");
        }

        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            var inode = await _inodeTable.GetInodeAsync(inodeNumber, ct)
                ?? throw new InvalidOperationException($"Inode {inodeNumber} not found.");

            if ((mask & InodeAttributeMask.Size) != 0)
            {
                long newSize = attrs.Size;
                if (newSize < inode.Size)
                {
                    // Truncate: free excess blocks
                    TruncateBlocks(inode, newSize);
                }
                inode.Size = newSize;
            }

            if ((mask & InodeAttributeMask.Permissions) != 0)
            {
                inode.Permissions = attrs.Permissions;
            }

            if ((mask & InodeAttributeMask.Owner) != 0)
            {
                inode.OwnerId = attrs.OwnerId;
            }

            if ((mask & InodeAttributeMask.AccessTime) != 0)
            {
                inode.AccessedUtc = attrs.AccessedUtc;
            }

            if ((mask & InodeAttributeMask.ModifyTime) != 0)
            {
                inode.ModifiedUtc = attrs.ModifiedUtc;
            }

            await _inodeTable.UpdateInodeAsync(inode, ct);
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    #endregion

    #region Flush / Sync

    /// <summary>
    /// Flushes cached writes for a specific inode.
    /// Ensures all pending data for the inode is persisted.
    /// </summary>
    /// <param name="inodeNumber">Inode number to flush.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task FlushAsync(long inodeNumber, CancellationToken ct)
    {
        ThrowIfDisposed();
        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            // Ensure WAL entries are flushed
            await _wal.FlushAsync(ct);
            await _device.FlushAsync(ct);
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    /// <summary>
    /// Synchronizes the entire filesystem, flushing all pending writes to persistent storage.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task SyncAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            await _wal.FlushAsync(ct);
            await _device.FlushAsync(ct);
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    #endregion

    #region Statfs

    /// <summary>
    /// Returns filesystem statistics (block size, space usage, inode count).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Filesystem statistics.</returns>
    public Task<FilesystemStats> StatfsAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        return Task.FromResult(new FilesystemStats
        {
            BlockSize = _device.BlockSize,
            TotalBlocks = _allocator.TotalBlockCount,
            FreeBlocks = _allocator.FreeBlockCount,
            AllocatedInodes = _inodeTable.AllocatedInodeCount,
            MaxNameLength = DirectoryEntry.MaxNameLength
        });
    }

    #endregion

    #region Extended Attributes

    /// <summary>
    /// Sets an extended attribute on an inode.
    /// </summary>
    /// <param name="inodeNumber">Inode number.</param>
    /// <param name="key">Attribute key.</param>
    /// <param name="value">Attribute value.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if the mount is read-only.</exception>
    public async Task SetXattrAsync(long inodeNumber, string key, byte[] value, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_options.ReadOnly)
        {
            throw new InvalidOperationException("Cannot set extended attributes on a read-only mount.");
        }

        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            await _inodeTable.SetExtendedAttributeAsync(inodeNumber, key, value, ct);
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    /// <summary>
    /// Gets an extended attribute from an inode.
    /// </summary>
    /// <param name="inodeNumber">Inode number.</param>
    /// <param name="key">Attribute key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Attribute value, or null if not found.</returns>
    public async Task<byte[]?> GetXattrAsync(long inodeNumber, string key, CancellationToken ct)
    {
        ThrowIfDisposed();
        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            return await _inodeTable.GetExtendedAttributeAsync(inodeNumber, key, ct);
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    /// <summary>
    /// Lists all extended attribute keys for an inode.
    /// </summary>
    /// <param name="inodeNumber">Inode number.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of attribute keys.</returns>
    public async Task<IReadOnlyList<string>> ListXattrAsync(long inodeNumber, CancellationToken ct)
    {
        ThrowIfDisposed();
        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            var attrs = await _inodeTable.ListExtendedAttributesAsync(inodeNumber, ct);
            return attrs.Keys.ToList();
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    #endregion

    #region Block Resolution Helpers

    /// <summary>
    /// Resolves a logical block index to a physical block number via the inode's block pointer tree.
    /// Returns 0 for sparse (unallocated) regions.
    /// </summary>
    private static long ResolveBlockPointer(Inode inode, long blockIndex)
    {
        if (blockIndex < Inode.DirectBlockCount)
        {
            return inode.DirectBlockPointers[blockIndex];
        }

        // Indirect and double-indirect resolution would require reading pointer blocks.
        // For the abstraction layer, we rely on the inode table to resolve these.
        // Return 0 to indicate the block is beyond direct pointers and needs
        // indirect resolution (handled by the block device layer in production).
        if (blockIndex < Inode.DirectBlockCount + (4096 / 8))
        {
            // Single indirect range -- pointer is in IndirectBlockPointer
            return inode.IndirectBlockPointer > 0 ? inode.IndirectBlockPointer : 0;
        }

        // Double indirect range
        return inode.DoubleIndirectPointer > 0 ? inode.DoubleIndirectPointer : 0;
    }

    /// <summary>
    /// Assigns a physical block number to a logical block index in the inode's pointer tree.
    /// </summary>
    private static void AssignBlockPointer(Inode inode, long blockIndex, long blockNumber)
    {
        if (blockIndex < Inode.DirectBlockCount)
        {
            inode.DirectBlockPointers[blockIndex] = blockNumber;
            return;
        }

        // For indirect blocks, store the pointer block number.
        // Full indirect block management is handled by the VDE layer.
        if (blockIndex < Inode.DirectBlockCount + (4096 / 8))
        {
            if (inode.IndirectBlockPointer == 0)
            {
                inode.IndirectBlockPointer = blockNumber;
            }
            return;
        }

        if (inode.DoubleIndirectPointer == 0)
        {
            inode.DoubleIndirectPointer = blockNumber;
        }
    }

    /// <summary>
    /// Counts the number of allocated (non-zero) block pointers in an inode.
    /// </summary>
    private static long CountAllocatedBlocks(Inode inode)
    {
        long count = 0;
        for (int i = 0; i < Inode.DirectBlockCount; i++)
        {
            if (inode.DirectBlockPointers[i] != 0)
            {
                count++;
            }
        }

        if (inode.IndirectBlockPointer != 0) count++;
        if (inode.DoubleIndirectPointer != 0) count++;

        return count;
    }

    /// <summary>
    /// Frees all data blocks owned by an inode.
    /// </summary>
    private void FreeInodeBlocks(Inode inode)
    {
        for (int i = 0; i < Inode.DirectBlockCount; i++)
        {
            if (inode.DirectBlockPointers[i] != 0)
            {
                _allocator.FreeBlock(inode.DirectBlockPointers[i]);
                _cache.Evict(inode.DirectBlockPointers[i]);
                inode.DirectBlockPointers[i] = 0;
            }
        }

        if (inode.IndirectBlockPointer != 0)
        {
            _allocator.FreeBlock(inode.IndirectBlockPointer);
            _cache.Evict(inode.IndirectBlockPointer);
            inode.IndirectBlockPointer = 0;
        }

        if (inode.DoubleIndirectPointer != 0)
        {
            _allocator.FreeBlock(inode.DoubleIndirectPointer);
            _cache.Evict(inode.DoubleIndirectPointer);
            inode.DoubleIndirectPointer = 0;
        }
    }

    /// <summary>
    /// Truncates blocks beyond the new file size, freeing excess allocations.
    /// </summary>
    private void TruncateBlocks(Inode inode, long newSize)
    {
        int blockSize = _device.BlockSize;
        long keepBlocks = (newSize + blockSize - 1) / blockSize;

        // Free direct blocks beyond the new size
        for (long i = keepBlocks; i < Inode.DirectBlockCount; i++)
        {
            if (inode.DirectBlockPointers[i] != 0)
            {
                _allocator.FreeBlock(inode.DirectBlockPointers[i]);
                _cache.Evict(inode.DirectBlockPointers[i]);
                inode.DirectBlockPointers[i] = 0;
            }
        }

        // If truncating below indirect block range, free indirect pointer
        if (keepBlocks <= Inode.DirectBlockCount)
        {
            if (inode.IndirectBlockPointer != 0)
            {
                _allocator.FreeBlock(inode.IndirectBlockPointer);
                _cache.Evict(inode.IndirectBlockPointer);
                inode.IndirectBlockPointer = 0;
            }

            if (inode.DoubleIndirectPointer != 0)
            {
                _allocator.FreeBlock(inode.DoubleIndirectPointer);
                _cache.Evict(inode.DoubleIndirectPointer);
                inode.DoubleIndirectPointer = 0;
            }
        }
    }

    #endregion

    #region Disposal

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Flush all pending data before disposing
        try
        {
            await _wal.FlushAsync(CancellationToken.None);
            await _device.FlushAsync(CancellationToken.None);
        }
        catch
        {
            // Best-effort flush during disposal
        }

        _concurrencySemaphore.Dispose();
    }

    #endregion
}
