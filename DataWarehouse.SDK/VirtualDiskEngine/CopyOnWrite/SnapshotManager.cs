using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;
using DataWarehouse.SDK.VirtualDiskEngine.Metadata;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.CopyOnWrite;

/// <summary>
/// Represents a snapshot: a read-only or writable point-in-time copy of the file system.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE CoW engine (VDE-06)")]
public sealed record Snapshot
{
    /// <summary>
    /// Unique snapshot identifier.
    /// </summary>
    public long SnapshotId { get; init; }

    /// <summary>
    /// Human-readable snapshot name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Root directory inode number for this snapshot.
    /// </summary>
    public long RootInodeNumber { get; init; }

    /// <summary>
    /// Timestamp when the snapshot was created (UTC).
    /// </summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>
    /// Whether this snapshot is read-only (true) or writable/clone (false).
    /// </summary>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// Number of blocks referenced by this snapshot at creation time.
    /// </summary>
    public long BlockCount { get; init; }
}

/// <summary>
/// Manages snapshot lifecycle: creation, listing, deletion, and cloning.
/// </summary>
/// <remarks>
/// Snapshot creation is O(n) in the number of blocks, but uses batch reference count
/// increments wrapped in a single WAL transaction for efficiency. Snapshots are read-only
/// point-in-time copies. Clones are writable copies that share blocks via CoW semantics.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE CoW engine (VDE-06)")]
public sealed class SnapshotManager
{
    private readonly IInodeTable _inodeTable;
    private readonly ICowEngine _cowEngine;
    private readonly IBlockAllocator _allocator;
    private readonly IWriteAheadLog _wal;
    private readonly long _snapshotMetadataInodeNumber;
    private readonly IBlockDevice? _blockDevice; // Optional: enables full indirect block traversal (finding 775)
    // Finding 777: Use ReaderWriterLockSlim so concurrent reads (ListSnapshots, GetSnapshot)
    // can proceed without blocking each other, while mutations (Create, Delete, Clone, Load)
    // are serialized under write lock.
    private readonly ReaderWriterLockSlim _snapshotsLock = new(LockRecursionPolicy.NoRecursion);
    private List<Snapshot> _snapshots = new();
    // Cat 13 (finding 791): secondary name index for O(1) snapshot lookup by name, replacing
    // O(n) FirstOrDefault scans on the hot CreateSnapshot/DeleteSnapshot/CloneSnapshot paths.
    private Dictionary<string, Snapshot> _snapshotsByName = new(StringComparer.OrdinalIgnoreCase);
    private long _nextSnapshotId = 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotManager"/> class.
    /// </summary>
    /// <param name="inodeTable">Inode table for accessing file system metadata.</param>
    /// <param name="cowEngine">Copy-on-write engine for reference counting.</param>
    /// <param name="allocator">Block allocator for managing free space.</param>
    /// <param name="wal">Write-ahead log for transaction safety.</param>
    /// <param name="snapshotMetadataInodeNumber">Inode number for storing snapshot table metadata.</param>
    /// <param name="blockDevice">
    /// Optional block device used to traverse indirect and double-indirect block pointer chains.
    /// When provided, <see cref="CollectBlockNumbersAsync"/> fully walks the block tree so that
    /// large-file snapshots capture all referenced data blocks (finding 775).
    /// When null the indirect-pointer blocks themselves are still recorded, but their data-block
    /// children are not — correct only for files that fit entirely in direct block pointers.
    /// </param>
    public SnapshotManager(
        IInodeTable inodeTable,
        ICowEngine cowEngine,
        IBlockAllocator allocator,
        IWriteAheadLog wal,
        long snapshotMetadataInodeNumber,
        IBlockDevice? blockDevice = null)
    {
        _inodeTable = inodeTable ?? throw new ArgumentNullException(nameof(inodeTable));
        _cowEngine = cowEngine ?? throw new ArgumentNullException(nameof(cowEngine));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _wal = wal ?? throw new ArgumentNullException(nameof(wal));
        _snapshotMetadataInodeNumber = snapshotMetadataInodeNumber;
        _blockDevice = blockDevice;
    }

    /// <summary>
    /// Creates a read-only snapshot of the current file system state.
    /// </summary>
    /// <param name="name">Human-readable name for the snapshot.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created snapshot.</returns>
    /// <exception cref="InvalidOperationException">Thrown if a snapshot with the same name already exists.</exception>
    public async Task<Snapshot> CreateSnapshotAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Snapshot name cannot be null or whitespace.", nameof(name));
        }

        _snapshotsLock.EnterReadLock();
        bool nameExists;
        try
        {
            // Cat 13 (finding 791): O(1) dictionary lookup instead of O(n) Any() scan.
            nameExists = _snapshotsByName.ContainsKey(name);
        }
        finally
        {
            _snapshotsLock.ExitReadLock();
        }

        if (nameExists)
        {
            throw new InvalidOperationException($"A snapshot with the name '{name}' already exists.");
        }

        await using var txn = await _wal.BeginTransactionAsync(ct);

        // Get current root directory inode
        Inode rootInode = _inodeTable.RootInode;

        // Collect all block numbers referenced by the root inode tree
        var blockNumbers = await CollectBlockNumbersAsync(rootInode.InodeNumber, ct);

        // Batch increment reference counts for all blocks
        await _cowEngine.IncrementRefBatchAsync(blockNumbers, ct);

        // Create snapshot record
        Snapshot snapshot;
        _snapshotsLock.EnterWriteLock();
        try
        {
            snapshot = new Snapshot
            {
                SnapshotId = Interlocked.Increment(ref _nextSnapshotId),
                Name = name,
                RootInodeNumber = rootInode.InodeNumber,
                CreatedUtc = DateTimeOffset.UtcNow,
                IsReadOnly = true,
                BlockCount = blockNumbers.Count
            };

            _snapshots.Add(snapshot);
            _snapshotsByName[snapshot.Name] = snapshot; // Cat 13 (finding 791): maintain name index
        }
        finally
        {
            _snapshotsLock.ExitWriteLock();
        }

        // Persist snapshot table
        await PersistSnapshotTableAsync(ct);

        await txn.CommitAsync(ct);

        return snapshot;
    }

    /// <summary>
    /// Lists all existing snapshots.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Read-only list of snapshots.</returns>
    public Task<IReadOnlyList<Snapshot>> ListSnapshotsAsync(CancellationToken ct = default)
    {
        _snapshotsLock.EnterReadLock();
        try
        {
            // Return a snapshot copy to prevent callers from observing concurrent mutations.
            return Task.FromResult<IReadOnlyList<Snapshot>>(_snapshots.ToList().AsReadOnly());
        }
        finally
        {
            _snapshotsLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets a snapshot by name.
    /// </summary>
    /// <param name="name">Snapshot name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The snapshot, or null if not found.</returns>
    public Task<Snapshot?> GetSnapshotAsync(string name, CancellationToken ct = default)
    {
        _snapshotsLock.EnterReadLock();
        try
        {
            // Cat 13 (finding 791): O(1) dictionary lookup.
            _snapshotsByName.TryGetValue(name, out var snapshot);
            return Task.FromResult(snapshot);
        }
        finally
        {
            _snapshotsLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Deletes a snapshot and reclaims space for unreferenced blocks.
    /// </summary>
    /// <param name="name">Name of the snapshot to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if the snapshot does not exist.</exception>
    public async Task DeleteSnapshotAsync(string name, CancellationToken ct = default)
    {
        Snapshot? snapshot;
        _snapshotsLock.EnterReadLock();
        try
        {
            // Cat 13 (finding 791): O(1) dictionary lookup.
            _snapshotsByName.TryGetValue(name, out snapshot);
        }
        finally
        {
            _snapshotsLock.ExitReadLock();
        }

        if (snapshot == null)
        {
            throw new InvalidOperationException($"Snapshot '{name}' not found.");
        }

        await using var txn = await _wal.BeginTransactionAsync(ct);

        // Collect all block numbers referenced by this snapshot
        var blockNumbers = await CollectBlockNumbersAsync(snapshot.RootInodeNumber, ct);

        // Batch decrement reference counts (blocks with refCount reaching 0 will be freed)
        await _cowEngine.DecrementRefBatchAsync(blockNumbers, ct);

        // Free the snapshot root inode
        await _inodeTable.FreeInodeAsync(snapshot.RootInodeNumber, ct);

        // Remove from snapshot list and name index
        _snapshotsLock.EnterWriteLock();
        try
        {
            _snapshots.Remove(snapshot);
            _snapshotsByName.Remove(snapshot.Name); // Cat 13 (finding 791): maintain name index
        }
        finally
        {
            _snapshotsLock.ExitWriteLock();
        }

        // Persist snapshot table
        await PersistSnapshotTableAsync(ct);

        await txn.CommitAsync(ct);
    }

    /// <summary>
    /// Creates a writable clone from a snapshot.
    /// </summary>
    /// <param name="snapshotName">Name of the snapshot to clone.</param>
    /// <param name="cloneName">Name for the new clone.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created clone snapshot.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the snapshot does not exist or clone name already exists.</exception>
    public async Task<Snapshot> CloneAsync(string snapshotName, string cloneName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cloneName))
        {
            throw new ArgumentException("Clone name cannot be null or whitespace.", nameof(cloneName));
        }

        Snapshot? sourceSnapshot;
        _snapshotsLock.EnterReadLock();
        try
        {
            // Cat 13 (finding 791): O(1) dictionary lookups.
            if (_snapshotsByName.ContainsKey(cloneName))
            {
                throw new InvalidOperationException($"A snapshot with the name '{cloneName}' already exists.");
            }

            _snapshotsByName.TryGetValue(snapshotName, out sourceSnapshot);
        }
        finally
        {
            _snapshotsLock.ExitReadLock();
        }

        if (sourceSnapshot == null)
        {
            throw new InvalidOperationException($"Snapshot '{snapshotName}' not found.");
        }

        await using var txn = await _wal.BeginTransactionAsync(ct);

        // Collect all block numbers referenced by the source snapshot
        var blockNumbers = await CollectBlockNumbersAsync(sourceSnapshot.RootInodeNumber, ct);

        // Batch increment reference counts for all blocks
        await _cowEngine.IncrementRefBatchAsync(blockNumbers, ct);

        // Allocate a new root inode for the clone
        Inode sourceRootInode = await _inodeTable.GetInodeAsync(sourceSnapshot.RootInodeNumber, ct)
            ?? throw new InvalidOperationException($"Source snapshot root inode {sourceSnapshot.RootInodeNumber} not found.");

        Inode cloneRootInode = await _inodeTable.AllocateInodeAsync(InodeType.Directory, ct);

        // Copy metadata from source inode
        cloneRootInode.Permissions = sourceRootInode.Permissions;
        cloneRootInode.OwnerId = sourceRootInode.OwnerId;
        cloneRootInode.GroupId = sourceRootInode.GroupId;
        cloneRootInode.Size = sourceRootInode.Size;
        cloneRootInode.LinkCount = sourceRootInode.LinkCount;
        Array.Copy(sourceRootInode.DirectBlockPointers, cloneRootInode.DirectBlockPointers, Inode.DirectBlockCount);
        cloneRootInode.IndirectBlockPointer = sourceRootInode.IndirectBlockPointer;
        cloneRootInode.DoubleIndirectPointer = sourceRootInode.DoubleIndirectPointer;
        cloneRootInode.ExtendedAttributesBlock = sourceRootInode.ExtendedAttributesBlock;

        await _inodeTable.UpdateInodeAsync(cloneRootInode, ct);

        // Create clone record (writable)
        Snapshot clone;
        _snapshotsLock.EnterWriteLock();
        try
        {
            clone = new Snapshot
            {
                SnapshotId = Interlocked.Increment(ref _nextSnapshotId),
                Name = cloneName,
                RootInodeNumber = cloneRootInode.InodeNumber,
                CreatedUtc = DateTimeOffset.UtcNow,
                IsReadOnly = false,
                BlockCount = blockNumbers.Count
            };

            _snapshots.Add(clone);
            _snapshotsByName[clone.Name] = clone; // Cat 13 (finding 791): maintain name index
        }
        finally
        {
            _snapshotsLock.ExitWriteLock();
        }

        // Persist snapshot table
        await PersistSnapshotTableAsync(ct);

        await txn.CommitAsync(ct);

        return clone;
    }

    /// <summary>
    /// Collects all block numbers referenced by an inode tree (recursive walk).
    /// </summary>
    /// <param name="rootInodeNumber">Root inode number to start from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of all referenced block numbers.</returns>
    private async Task<IReadOnlyList<long>> CollectBlockNumbersAsync(long rootInodeNumber, CancellationToken ct)
    {
        var blockNumbers = new HashSet<long>();
        var inodeQueue = new Queue<long>();
        inodeQueue.Enqueue(rootInodeNumber);

        while (inodeQueue.Count > 0)
        {
            long currentInodeNumber = inodeQueue.Dequeue();
            Inode? inode = await _inodeTable.GetInodeAsync(currentInodeNumber, ct);

            if (inode == null)
            {
                continue;
            }

            // Collect direct block pointers
            foreach (long blockNumber in inode.DirectBlockPointers)
            {
                if (blockNumber > 0)
                {
                    blockNumbers.Add(blockNumber);
                }
            }

            // Collect indirect block pointer and referenced data blocks
            if (inode.IndirectBlockPointer > 0)
            {
                blockNumbers.Add(inode.IndirectBlockPointer);
                // Read indirect block to collect referenced data blocks
                await CollectIndirectBlockReferencesAsync(inode.IndirectBlockPointer, blockNumbers, ct);
            }

            // Collect double indirect block pointer and recursively collect data blocks
            if (inode.DoubleIndirectPointer > 0)
            {
                blockNumbers.Add(inode.DoubleIndirectPointer);
                await CollectDoubleIndirectBlockReferencesAsync(inode.DoubleIndirectPointer, blockNumbers, ct);
            }

            // Collect extended attributes block
            if (inode.ExtendedAttributesBlock > 0)
            {
                blockNumbers.Add(inode.ExtendedAttributesBlock);
            }

            // If this is a directory, recurse into child inodes
            if (inode.Type == InodeType.Directory)
            {
                var entries = await _inodeTable.ReadDirectoryAsync(currentInodeNumber, ct);
                foreach (var entry in entries)
                {
                    if (entry.Name != "." && entry.Name != "..")
                    {
                        inodeQueue.Enqueue(entry.InodeNumber);
                    }
                }
            }
        }

        return blockNumbers.ToList();
    }

    /// <summary>
    /// Persists the snapshot table to the snapshot metadata inode.
    /// </summary>
    private async Task PersistSnapshotTableAsync(CancellationToken ct)
    {
        // Take a read-locked snapshot of the list before serializing to avoid holding the lock
        // across an async await (not supported by ReaderWriterLockSlim).
        List<Snapshot> snapshotsCopy;
        _snapshotsLock.EnterReadLock();
        try
        {
            snapshotsCopy = _snapshots.ToList();
        }
        finally
        {
            _snapshotsLock.ExitReadLock();
        }

        // Serialize snapshot list to JSON
        string json = JsonSerializer.Serialize(snapshotsCopy, new JsonSerializerOptions
        {
            WriteIndented = false
        });

        // Store as extended attribute on the snapshot metadata inode
        byte[] jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
        await _inodeTable.SetExtendedAttributeAsync(_snapshotMetadataInodeNumber, "snapshot-table", jsonBytes, ct);
    }

    /// <summary>
    /// Loads the snapshot table from the snapshot metadata inode.
    /// </summary>
    public async Task LoadSnapshotTableAsync(CancellationToken ct = default)
    {
        byte[]? jsonBytes = await _inodeTable.GetExtendedAttributeAsync(_snapshotMetadataInodeNumber, "snapshot-table", ct);

        List<Snapshot> loaded;
        long nextId;

        if (jsonBytes == null || jsonBytes.Length == 0)
        {
            loaded = new List<Snapshot>();
            nextId = 1;
        }
        else
        {
            string json = System.Text.Encoding.UTF8.GetString(jsonBytes);
            loaded = JsonSerializer.Deserialize<List<Snapshot>>(json) ?? new List<Snapshot>();
            nextId = loaded.Count > 0 ? loaded.Max(s => s.SnapshotId) + 1 : 1;
        }

        _snapshotsLock.EnterWriteLock();
        try
        {
            _snapshots = loaded;
            // Cat 13 (finding 791): rebuild name index from loaded list.
            _snapshotsByName = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in loaded)
                _snapshotsByName[s.Name] = s;
            _nextSnapshotId = nextId;
        }
        finally
        {
            _snapshotsLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Collects data block numbers stored in an indirect block (one level of indirection).
    /// Each 8-byte slot in the block is a pointer to a data block (0 = unused).
    /// Requires a <see cref="IBlockDevice"/> to read the indirect block; when unavailable
    /// the data blocks are not collected (only the pointer block itself has been added by caller).
    /// </summary>
    private async Task CollectIndirectBlockReferencesAsync(long indirectBlockPointer, HashSet<long> blockNumbers, CancellationToken ct)
    {
        if (_blockDevice == null)
            return;

        int blockSize = _blockDevice.BlockSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(blockSize);
        try
        {
            await _blockDevice.ReadBlockAsync(indirectBlockPointer, buffer.AsMemory(0, blockSize), ct).ConfigureAwait(false);

            int pointerCount = blockSize / 8;
            for (int i = 0; i < pointerCount; i++)
            {
                long dataBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(i * 8, 8));
                if (dataBlock > 0)
                    blockNumbers.Add(dataBlock);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Collects data block numbers stored in a double-indirect block (two levels of indirection).
    /// Reads the top-level block to enumerate first-level indirect blocks, then calls
    /// <see cref="CollectIndirectBlockReferencesAsync"/> for each to collect data blocks.
    /// </summary>
    private async Task CollectDoubleIndirectBlockReferencesAsync(long doubleIndirectPointer, HashSet<long> blockNumbers, CancellationToken ct)
    {
        if (_blockDevice == null)
            return;

        int blockSize = _blockDevice.BlockSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(blockSize);
        try
        {
            await _blockDevice.ReadBlockAsync(doubleIndirectPointer, buffer.AsMemory(0, blockSize), ct).ConfigureAwait(false);

            int pointerCount = blockSize / 8;
            for (int i = 0; i < pointerCount; i++)
            {
                long indirectBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(i * 8, 8));
                if (indirectBlock > 0)
                {
                    blockNumbers.Add(indirectBlock);
                    await CollectIndirectBlockReferencesAsync(indirectBlock, blockNumbers, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
