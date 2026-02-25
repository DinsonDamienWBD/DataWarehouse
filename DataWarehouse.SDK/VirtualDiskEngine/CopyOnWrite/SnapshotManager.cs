using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;
using DataWarehouse.SDK.VirtualDiskEngine.Metadata;
using System;
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
    private List<Snapshot> _snapshots = new();
    private long _nextSnapshotId = 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotManager"/> class.
    /// </summary>
    /// <param name="inodeTable">Inode table for accessing file system metadata.</param>
    /// <param name="cowEngine">Copy-on-write engine for reference counting.</param>
    /// <param name="allocator">Block allocator for managing free space.</param>
    /// <param name="wal">Write-ahead log for transaction safety.</param>
    /// <param name="snapshotMetadataInodeNumber">Inode number for storing snapshot table metadata.</param>
    public SnapshotManager(
        IInodeTable inodeTable,
        ICowEngine cowEngine,
        IBlockAllocator allocator,
        IWriteAheadLog wal,
        long snapshotMetadataInodeNumber)
    {
        _inodeTable = inodeTable ?? throw new ArgumentNullException(nameof(inodeTable));
        _cowEngine = cowEngine ?? throw new ArgumentNullException(nameof(cowEngine));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _wal = wal ?? throw new ArgumentNullException(nameof(wal));
        _snapshotMetadataInodeNumber = snapshotMetadataInodeNumber;
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

        if (_snapshots.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
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
        var snapshot = new Snapshot
        {
            SnapshotId = _nextSnapshotId++,
            Name = name,
            RootInodeNumber = rootInode.InodeNumber,
            CreatedUtc = DateTimeOffset.UtcNow,
            IsReadOnly = true,
            BlockCount = blockNumbers.Count
        };

        _snapshots.Add(snapshot);

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
        return Task.FromResult<IReadOnlyList<Snapshot>>(_snapshots.AsReadOnly());
    }

    /// <summary>
    /// Gets a snapshot by name.
    /// </summary>
    /// <param name="name">Snapshot name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The snapshot, or null if not found.</returns>
    public Task<Snapshot?> GetSnapshotAsync(string name, CancellationToken ct = default)
    {
        var snapshot = _snapshots.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(snapshot);
    }

    /// <summary>
    /// Deletes a snapshot and reclaims space for unreferenced blocks.
    /// </summary>
    /// <param name="name">Name of the snapshot to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if the snapshot does not exist.</exception>
    public async Task DeleteSnapshotAsync(string name, CancellationToken ct = default)
    {
        var snapshot = _snapshots.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
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

        // Remove from snapshot list
        _snapshots.Remove(snapshot);

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

        if (_snapshots.Any(s => s.Name.Equals(cloneName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A snapshot with the name '{cloneName}' already exists.");
        }

        var sourceSnapshot = _snapshots.FirstOrDefault(s => s.Name.Equals(snapshotName, StringComparison.OrdinalIgnoreCase));
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
        var clone = new Snapshot
        {
            SnapshotId = _nextSnapshotId++,
            Name = cloneName,
            RootInodeNumber = cloneRootInode.InodeNumber,
            CreatedUtc = DateTimeOffset.UtcNow,
            IsReadOnly = false,
            BlockCount = blockNumbers.Count
        };

        _snapshots.Add(clone);

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

            // Collect indirect block pointer (only the pointer block itself)
            // Note: Indirect block support is not yet implemented. When present, only the indirect block pointer
            // itself is collected. Full implementation would require reading and collecting referenced data blocks.
            if (inode.IndirectBlockPointer > 0)
            {
                blockNumbers.Add(inode.IndirectBlockPointer);
            }

            // Collect double indirect block pointer (only the pointer block itself)
            // Note: Double indirect block support is not yet implemented. When present, only the double indirect
            // block pointer itself is collected. Full implementation would require recursive collection.
            if (inode.DoubleIndirectPointer > 0)
            {
                blockNumbers.Add(inode.DoubleIndirectPointer);
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
        // Serialize snapshot list to JSON
        string json = JsonSerializer.Serialize(_snapshots, new JsonSerializerOptions
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

        if (jsonBytes == null || jsonBytes.Length == 0)
        {
            _snapshots = new List<Snapshot>();
            _nextSnapshotId = 1;
            return;
        }

        string json = System.Text.Encoding.UTF8.GetString(jsonBytes);
        _snapshots = JsonSerializer.Deserialize<List<Snapshot>>(json) ?? new List<Snapshot>();

        if (_snapshots.Count > 0)
        {
            _nextSnapshotId = _snapshots.Max(s => s.SnapshotId) + 1;
        }
        else
        {
            _nextSnapshotId = 1;
        }
    }
}
