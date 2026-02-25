using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Container;
using DataWarehouse.SDK.VirtualDiskEngine.CopyOnWrite;
using DataWarehouse.SDK.VirtualDiskEngine.Index;
using DataWarehouse.SDK.VirtualDiskEngine.Integrity;
using DataWarehouse.SDK.VirtualDiskEngine.Concurrency;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;
using DataWarehouse.SDK.VirtualDiskEngine.Metadata;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine;

/// <summary>
/// Main facade for the Virtual Disk Engine.
/// Coordinates all VDE subsystems: container, allocator, WAL, inodes, namespace, B-Tree, CoW, checksums.
/// Provides high-level storage operations: Store, Retrieve, Delete, List, and snapshot management.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE engine facade (VDE-07)")]
public sealed class VirtualDiskEngine : IAsyncDisposable
{
    private readonly VdeOptions _options;
    private readonly StripedWriteLock _stripedLock = new(64);
    private readonly SemaphoreSlim _checkpointLock = new(1, 1);
    private bool _disposed;
    private bool _initialized;

    // Subsystems (initialized in InitializeAsync)
    private ContainerFile? _container;
    private IBlockAllocator? _allocator;
    private IWriteAheadLog? _wal;
    private IBlockChecksummer? _checksummer;
    private CorruptionDetector? _corruptionDetector;
    private IInodeTable? _inodeTable;
    private NamespaceTree? _namespaceTree;
    private IBTreeIndex? _keyIndex;
    private ICowEngine? _cowEngine;
    private CopyOnWrite.SnapshotManager? _snapshotManager;
    private SpaceReclaimer? _spaceReclaimer;

    /// <summary>
    /// Creates a new Virtual Disk Engine with the specified options.
    /// Call <see cref="InitializeAsync"/> to complete initialization.
    /// </summary>
    /// <param name="options">Configuration options.</param>
    public VirtualDiskEngine(VdeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    /// <summary>
    /// Initializes the Virtual Disk Engine: opens or creates the container, loads subsystems.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized)
        {
            return;
        }

        // Step 1: Create or open the container file
        bool containerExists = File.Exists(_options.ContainerPath);

        if (containerExists)
        {
            _container = await ContainerFile.OpenAsync(_options.ContainerPath, ct);

            // Check if WAL needs recovery
            // WAL initialization will handle recovery if needed
        }
        else if (_options.AutoCreateContainer)
        {
            _container = await ContainerFile.CreateAsync(
                _options.ContainerPath,
                _options.BlockSize,
                _options.TotalBlocks,
                ct);
        }
        else
        {
            throw new FileNotFoundException($"Container file '{_options.ContainerPath}' not found and AutoCreateContainer is false.");
        }

        // Step 2: Initialize block allocator (FreeSpaceManager)
        _allocator = await FreeSpaceManager.LoadAsync(
            _container.BlockDevice,
            _container.Layout.BitmapStartBlock,
            _container.Layout.BitmapBlockCount,
            _container.Layout.DataBlockCount,
            ct);

        // Step 3: Initialize Write-Ahead Log
        _wal = await WriteAheadLog.CreateAsync(
            _container.BlockDevice,
            _container.Layout.WalStartBlock,
            _container.Layout.WalBlockCount,
            ct);

        // Run recovery if needed
        if (_wal.NeedsRecovery)
        {
            await RecoverFromWalAsync(ct);
        }

        // Step 4: Initialize checksum subsystem
        var checksumTable = new ChecksumTable(
            _container.BlockDevice,
            _container.Layout.ChecksumTableBlock,
            _container.Layout.ChecksumTableBlockCount,
            _container.Layout.DataBlockCount);

        _checksummer = new BlockChecksummer(checksumTable);

        _corruptionDetector = new CorruptionDetector(
            _checksummer,
            _container.BlockDevice,
            _container.Layout.DataStartBlock,
            _container.Layout.DataBlockCount);

        // Step 5: Initialize inode table
        var inodeTable = new InodeTable(
            _container.BlockDevice,
            _allocator,
            _container.Layout.InodeTableBlock,
            _container.Layout.InodeTableBlockCount);

        await inodeTable.InitializeAsync(ct);
        _inodeTable = inodeTable;

        // Step 6: Initialize namespace tree
        _namespaceTree = new NamespaceTree(_inodeTable);

        // Step 7: Initialize B-Tree key index
        _keyIndex = new BTree(
            _container.BlockDevice,
            _allocator,
            _wal,
            _container.CurrentSuperblock.BTreeRootBlock,
            _options.BlockSize);

        // Step 8: Initialize CoW engine (separate B-Tree for ref counts)
        // Create a separate B-Tree for reference counting
        var refCountBTreeRoot = _container.Layout.DataStartBlock; // Note: should be allocated from metadata region when metadata allocator is available
        var refCountBTree = new BTree(
            _container.BlockDevice,
            _allocator,
            _wal,
            refCountBTreeRoot,
            _options.BlockSize);

        _cowEngine = new CowBlockManager(
            _container.BlockDevice,
            _allocator,
            refCountBTree,
            _wal,
            _checksummer);

        // Step 9: Initialize snapshot manager and space reclaimer
        // Snapshot metadata is stored as extended attributes on a reserved inode (inode 2)
        const long SnapshotMetadataInodeNumber = 2;

        _snapshotManager = new CopyOnWrite.SnapshotManager(
            _inodeTable,
            _cowEngine,
            _allocator,
            _wal,
            SnapshotMetadataInodeNumber);

        await _snapshotManager.LoadSnapshotTableAsync(ct);

        _spaceReclaimer = new SpaceReclaimer(_cowEngine, _allocator);

        _initialized = true;
    }

    #region Store/Retrieve/Delete Operations

    /// <summary>
    /// Stores data with the specified key.
    /// Allocates blocks, writes data via CoW, updates inode, indexes key in B-Tree, all within a WAL transaction.
    /// </summary>
    /// <param name="key">Storage key (mapped to VDE namespace path).</param>
    /// <param name="data">Data stream to store.</param>
    /// <param name="metadata">Optional metadata to associate with the object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Storage object metadata.</returns>
    public async Task<StorageObjectMetadata> StoreAsync(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfNotInitialized();

        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);

        await using var writeRegion = await _stripedLock.AcquireAsync(key, ct);

        // Map key to VDE path (prefix with root directory)
        string vdePath = KeyToVdePath(key);

        // Begin WAL transaction
        await using var txn = await _wal!.BeginTransactionAsync(ct);

        // Create or get existing file inode
        Inode fileInode;
        var existingInode = await _namespaceTree!.ResolvePathAsync(vdePath, ct);

        if (existingInode != null)
        {
            // Update existing file
            fileInode = existingInode;
        }
        else
        {
            // Create new file
            fileInode = await _namespaceTree.CreateFileAsync(vdePath, InodePermissions.OwnerAll, ct);
        }

        // Read data stream in block-sized chunks and write to disk
        var blockPointers = new List<long>();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_options.BlockSize);
        try
        {
            long totalBytesRead = 0;
            int bytesRead;

            while ((bytesRead = await data.ReadAsync(buffer.AsMemory(0, _options.BlockSize), ct)) > 0)
            {
                // Pad last block if needed
                if (bytesRead < _options.BlockSize)
                {
                    Array.Clear(buffer, bytesRead, _options.BlockSize - bytesRead);
                }

                // Allocate block
                long blockNumber = _allocator!.AllocateBlock(ct);

                // Write data via CoW engine
                long actualBlockNumber = await _cowEngine!.WriteBlockCowAsync(blockNumber, buffer.AsMemory(0, _options.BlockSize), ct);

                // Compute and store checksum
                ulong checksum = _checksummer!.ComputeChecksum(buffer.AsSpan(0, _options.BlockSize));
                await _checksummer.StoreChecksumAsync(actualBlockNumber, checksum, ct);

                blockPointers.Add(actualBlockNumber);
                totalBytesRead += bytesRead;
            }

            // Update inode with block pointers
            if (blockPointers.Count > Inode.DirectBlockCount)
            {
                throw new NotSupportedException($"Files exceeding direct block limit require indirect block support. Current limit: {Inode.DirectBlockCount} blocks ({Inode.DirectBlockCount * _options.BlockSize} bytes).");
            }

            for (int i = 0; i < blockPointers.Count && i < Inode.DirectBlockCount; i++)
            {
                fileInode.DirectBlockPointers[i] = blockPointers[i];
            }

            fileInode.Size = totalBytesRead;
            fileInode.ModifiedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await _inodeTable!.UpdateInodeAsync(fileInode, ct);

            // Store metadata as extended attributes
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    byte[] valueBytes = Encoding.UTF8.GetBytes(kvp.Value);
                    await _inodeTable.SetExtendedAttributeAsync(fileInode.InodeNumber, kvp.Key, valueBytes, ct);
                }
            }

            // Index key in B-Tree (key bytes -> inode number)
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            await _keyIndex!.InsertAsync(keyBytes, fileInode.InodeNumber, ct);

            // Commit WAL transaction
            await txn.CommitAsync(ct);

            // Auto-checkpoint if WAL utilization exceeds threshold
            if (_wal.WalUtilization * 100 >= _options.CheckpointWalUtilizationPercent)
            {
                await CheckpointAsync(ct);
            }

            // Return metadata
            return new StorageObjectMetadata
            {
                Key = key,
                Size = totalBytesRead,
                Created = DateTimeOffset.FromUnixTimeSeconds(fileInode.CreatedUtc).UtcDateTime,
                Modified = DateTimeOffset.FromUnixTimeSeconds(fileInode.ModifiedUtc).UtcDateTime,
                ETag = $"inode-{fileInode.InodeNumber}",
                Tier = Contracts.Storage.StorageTier.Hot
            };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Retrieves data for the specified key.
    /// Looks up key in B-Tree, resolves inode, reads blocks, verifies checksums.
    /// </summary>
    /// <param name="key">Storage key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Data stream.</returns>
    public async Task<Stream> RetrieveAsync(string key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfNotInitialized();

        ArgumentNullException.ThrowIfNull(key);

        // Lookup key in B-Tree
        byte[] keyBytes = Encoding.UTF8.GetBytes(key);
        long? inodeNumber = await _keyIndex!.LookupAsync(keyBytes, ct);

        if (inodeNumber == null)
        {
            throw new FileNotFoundException($"Key '{key}' not found.");
        }

        // Get inode
        Inode? inode = await _inodeTable!.GetInodeAsync(inodeNumber.Value, ct);
        if (inode == null)
        {
            throw new InvalidOperationException($"Inode {inodeNumber.Value} not found (index inconsistency).");
        }

        // Read all blocks and build stream
        var resultStream = new MemoryStream((int)inode.Size);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_options.BlockSize);
        try
        {
            long remainingBytes = inode.Size;

            for (int i = 0; i < Inode.DirectBlockCount && remainingBytes > 0; i++)
            {
                long blockNumber = inode.DirectBlockPointers[i];
                if (blockNumber == 0)
                {
                    break;
                }

                // Read block
                await _container!.BlockDevice.ReadBlockAsync(blockNumber, buffer.AsMemory(0, _options.BlockSize), ct);

                // Verify checksum if enabled
                if (_options.EnableChecksumVerification)
                {
                    bool isValid = await _checksummer!.VerifyBlockAsync(blockNumber, buffer.AsMemory(0, _options.BlockSize), ct);
                    if (!isValid)
                    {
                        throw new IOException($"Checksum verification failed for block {blockNumber} (inode {inodeNumber.Value}).");
                    }
                }

                // Append to result stream (only the valid bytes)
                int bytesToWrite = (int)Math.Min(remainingBytes, _options.BlockSize);
                await resultStream.WriteAsync(buffer.AsMemory(0, bytesToWrite), ct);
                remainingBytes -= bytesToWrite;
            }

            if (remainingBytes > 0)
            {
                throw new NotSupportedException($"Files exceeding direct block limit require indirect block support. Current limit: {Inode.DirectBlockCount} blocks ({Inode.DirectBlockCount * _options.BlockSize} bytes).");
            }

            resultStream.Position = 0;
            return resultStream;
        }
        catch
        {
            resultStream.Dispose();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Deletes the object with the specified key.
    /// Removes from B-Tree, deletes from namespace tree, decrements ref counts via SpaceReclaimer.
    /// </summary>
    /// <param name="key">Storage key.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfNotInitialized();

        ArgumentNullException.ThrowIfNull(key);

        await using var writeRegion = await _stripedLock.AcquireAsync(key, ct);

        // Lookup key in B-Tree
        byte[] keyBytes = Encoding.UTF8.GetBytes(key);
        long? inodeNumber = await _keyIndex!.LookupAsync(keyBytes, ct);

        if (inodeNumber == null)
        {
            // Key doesn't exist - idempotent delete
            return;
        }

        // Begin WAL transaction
        await using var txn = await _wal!.BeginTransactionAsync(ct);

        // Get inode to collect block numbers
        Inode? inode = await _inodeTable!.GetInodeAsync(inodeNumber.Value, ct);
        if (inode != null)
        {
            // Collect all block numbers
            var blockNumbers = new List<long>();
            for (int i = 0; i < Inode.DirectBlockCount; i++)
            {
                if (inode.DirectBlockPointers[i] > 0)
                {
                    blockNumbers.Add(inode.DirectBlockPointers[i]);
                }
            }

            // Decrement ref counts (will free blocks with refCount == 0)
            await _spaceReclaimer!.ReclaimBlocksAsync(blockNumbers, ct);
        }

        // Delete from namespace tree
        string vdePath = KeyToVdePath(key);
        await _namespaceTree!.DeleteAsync(vdePath, ct);

        // Remove from B-Tree
        await _keyIndex.DeleteAsync(keyBytes, ct);

        // Commit WAL transaction
        await txn.CommitAsync(ct);

        // Auto-checkpoint if needed
        if (_wal.WalUtilization * 100 >= _options.CheckpointWalUtilizationPercent)
        {
            await CheckpointAsync(ct);
        }
    }

    /// <summary>
    /// Checks if an object with the specified key exists.
    /// </summary>
    /// <param name="key">Storage key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the object exists, false otherwise.</returns>
    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfNotInitialized();

        ArgumentNullException.ThrowIfNull(key);

        byte[] keyBytes = Encoding.UTF8.GetBytes(key);
        long? inodeNumber = await _keyIndex!.LookupAsync(keyBytes, ct);
        return inodeNumber != null;
    }

    /// <summary>
    /// Lists all objects with an optional key prefix.
    /// Performs B-Tree range query.
    /// </summary>
    /// <param name="prefix">Optional key prefix filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of storage object metadata.</returns>
    public async IAsyncEnumerable<StorageObjectMetadata> ListAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfNotInitialized();

        byte[]? prefixBytes = prefix != null ? Encoding.UTF8.GetBytes(prefix) : null;
        byte[]? endBytes = prefixBytes != null ? IncrementBytes(prefixBytes) : null;

        await foreach (var (keyBytes, inodeNumber) in _keyIndex!.RangeQueryAsync(prefixBytes, endBytes, ct))
        {
            string key = Encoding.UTF8.GetString(keyBytes);

            // Get inode for metadata
            Inode? inode = await _inodeTable!.GetInodeAsync(inodeNumber, ct);
            if (inode == null)
            {
                continue; // Skip if inode is missing
            }

            yield return new StorageObjectMetadata
            {
                Key = key,
                Size = inode.Size,
                Created = DateTimeOffset.FromUnixTimeSeconds(inode.CreatedUtc).UtcDateTime,
                Modified = DateTimeOffset.FromUnixTimeSeconds(inode.ModifiedUtc).UtcDateTime,
                ETag = $"inode-{inode.InodeNumber}",
                Tier = Contracts.Storage.StorageTier.Hot
            };
        }
    }

    /// <summary>
    /// Gets metadata for a specific object without retrieving its data.
    /// </summary>
    /// <param name="key">Storage key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Storage object metadata.</returns>
    public async Task<StorageObjectMetadata> GetMetadataAsync(string key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfNotInitialized();

        ArgumentNullException.ThrowIfNull(key);

        byte[] keyBytes = Encoding.UTF8.GetBytes(key);
        long? inodeNumber = await _keyIndex!.LookupAsync(keyBytes, ct);

        if (inodeNumber == null)
        {
            throw new FileNotFoundException($"Key '{key}' not found.");
        }

        Inode? inode = await _inodeTable!.GetInodeAsync(inodeNumber.Value, ct);
        if (inode == null)
        {
            throw new InvalidOperationException($"Inode {inodeNumber.Value} not found (index inconsistency).");
        }

        return new StorageObjectMetadata
        {
            Key = key,
            Size = inode.Size,
            Created = DateTimeOffset.FromUnixTimeSeconds(inode.CreatedUtc).UtcDateTime,
            Modified = DateTimeOffset.FromUnixTimeSeconds(inode.ModifiedUtc).UtcDateTime,
            ETag = $"inode-{inode.InodeNumber}",
            Tier = Contracts.Storage.StorageTier.Hot
        };
    }

    #endregion

    #region Snapshot Operations

    /// <summary>
    /// Creates a read-only snapshot of the current file system state.
    /// </summary>
    /// <param name="name">Snapshot name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created snapshot.</returns>
    public async Task<Snapshot> CreateSnapshotAsync(string name, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfNotInitialized();

        return await _snapshotManager!.CreateSnapshotAsync(name, ct);
    }

    /// <summary>
    /// Lists all existing snapshots.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of snapshots.</returns>
    public async Task<IReadOnlyList<Snapshot>> ListSnapshotsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfNotInitialized();

        return await _snapshotManager!.ListSnapshotsAsync(ct);
    }

    /// <summary>
    /// Deletes a snapshot and reclaims space.
    /// </summary>
    /// <param name="name">Snapshot name.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DeleteSnapshotAsync(string name, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfNotInitialized();

        await _snapshotManager!.DeleteSnapshotAsync(name, ct);
    }

    #endregion

    #region Health and Integrity

    /// <summary>
    /// Gets the current health status of the VDE.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Health report.</returns>
    public async Task<VdeHealthReport> GetHealthReportAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfNotInitialized();

        long totalBlocks = _allocator!.TotalBlockCount;
        long freeBlocks = _allocator.FreeBlockCount;
        long usedBlocks = totalBlocks - freeBlocks;

        double walUtilization = _wal!.WalUtilization * 100.0;

        var snapshots = await _snapshotManager!.ListSnapshotsAsync(ct);

        // Get checksum error count from corruption detector
        long checksumErrorCount = 0; // CorruptionDetector would need to track this

        string healthStatus = VdeHealthReport.DetermineHealthStatus(totalBlocks, freeBlocks, walUtilization, checksumErrorCount);

        return new VdeHealthReport
        {
            TotalBlocks = totalBlocks,
            FreeBlocks = freeBlocks,
            UsedBlocks = usedBlocks,
            TotalInodes = 65536, // Fixed for now; InodeTable should expose this
            AllocatedInodes = _inodeTable!.AllocatedInodeCount,
            WalUtilizationPercent = walUtilization,
            ChecksumErrorCount = checksumErrorCount,
            SnapshotCount = snapshots.Count,
            HealthStatus = healthStatus,
            GeneratedAtUtc = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Scans the entire file system for integrity issues (checksum verification).
    /// </summary>
    /// <param name="progress">Optional progress reporter (0.0 to 1.0).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of corrupt blocks detected.</returns>
    public async Task<IReadOnlyList<long>> ScanIntegrityAsync(IProgress<double>? progress, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfNotInitialized();

        var scanResult = await _corruptionDetector!.ScanAllBlocksAsync(progress, ct);
        return scanResult.Events.Select(e => e.BlockNumber).ToList();
    }

    #endregion

    #region Lifecycle

    /// <summary>
    /// Writes a checkpoint: flushes all dirty data, commits WAL, updates superblock.
    /// Uses a dedicated checkpoint lock (separate from striped write locks) to serialize
    /// checkpoint operations without blocking per-key writes.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task CheckpointAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfNotInitialized();

        await _checkpointLock.WaitAsync(ct);
        try
        {
            // Flush all subsystems
            await _checksummer!.FlushAsync(ct);
            await _allocator!.PersistAsync(_container!.BlockDevice, _container.Layout.BitmapStartBlock, ct);

            // Checkpoint WAL
            await _wal!.CheckpointAsync(ct);

            // Update superblock with current free block count
            await _container.WriteCheckpointAsync(_allocator.FreeBlockCount, ct);
        }
        finally
        {
            _checkpointLock.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Orderly shutdown: checkpoint, flush, dispose subsystems in reverse order
        try
        {
            if (_initialized)
            {
                await CheckpointAsync(CancellationToken.None);
            }
        }
        catch
        {
            // Ignore errors during checkpoint on dispose
        }

        // Dispose subsystems (no explicit dispose needed for most, but good practice)
        if (_container != null)
        {
            await _container.DisposeAsync();
        }

        await _stripedLock.DisposeAsync();
        _checkpointLock.Dispose();
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Converts a storage key to a VDE namespace path.
    /// Keys are prefixed with "/" to make them absolute paths.
    /// </summary>
    private static string KeyToVdePath(string key)
    {
        if (key.StartsWith('/'))
        {
            return key;
        }
        return "/" + key;
    }

    /// <summary>
    /// Increments a byte array by one (for range query end bound).
    /// </summary>
    private static byte[]? IncrementBytes(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return null;
        }

        byte[] result = new byte[bytes.Length];
        Array.Copy(bytes, result, bytes.Length);

        for (int i = result.Length - 1; i >= 0; i--)
        {
            if (result[i] < 255)
            {
                result[i]++;
                return result;
            }
            result[i] = 0;
        }

        // Overflow: return null to indicate "to the end"
        return null;
    }

    /// <summary>
    /// Recovers from WAL after a crash (replays committed transactions).
    /// </summary>
    private async Task RecoverFromWalAsync(CancellationToken ct)
    {
        var entriesToReplay = await _wal!.ReplayAsync(ct);

        foreach (var entry in entriesToReplay)
        {
            if (entry.AfterImage != null && entry.TargetBlockNumber >= 0)
            {
                // Apply after-image to block device
                await _container!.BlockDevice.WriteBlockAsync(entry.TargetBlockNumber, entry.AfterImage, ct);
            }
        }

        // After replay, checkpoint to clear the WAL
        await _wal.CheckpointAsync(ct);
    }

    private void ThrowIfNotInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("VirtualDiskEngine is not initialized. Call InitializeAsync first.");
        }
    }

    #endregion
}
