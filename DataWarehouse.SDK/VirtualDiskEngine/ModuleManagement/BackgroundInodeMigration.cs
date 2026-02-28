using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.ModuleManagement;

/// <summary>
/// Progress report for a background inode migration operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Migration progress (OMA-03)")]
public readonly record struct MigrationProgress
{
    /// <summary>Total inodes to migrate.</summary>
    public long TotalInodes { get; init; }

    /// <summary>Inodes successfully migrated so far.</summary>
    public long MigratedInodes { get; init; }

    /// <summary>Percentage of migration complete (0.0 to 100.0).</summary>
    public double PercentComplete { get; init; }

    /// <summary>Current migration phase.</summary>
    public MigrationPhase Phase { get; init; }
}

/// <summary>
/// Result of a background inode migration operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Migration result (OMA-03)")]
public readonly record struct MigrationResult
{
    /// <summary>True if migration completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Error message if migration failed, null on success.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Number of inodes migrated.</summary>
    public long InodesMigrated { get; init; }

    /// <summary>Total duration of the migration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>The new inode layout descriptor after migration.</summary>
    public InodeLayoutDescriptor NewLayout { get; init; }

    internal static MigrationResult Succeeded(long inodesMigrated, TimeSpan duration, InodeLayoutDescriptor newLayout)
        => new() { Success = true, InodesMigrated = inodesMigrated, Duration = duration, NewLayout = newLayout };

    internal static MigrationResult Failed(string error)
        => new() { Success = false, ErrorMessage = error };
}

/// <summary>
/// Crash-safe background inode table migration engine for when padding bytes are insufficient
/// to accommodate a new module's inode fields. Creates a new inode table region with larger
/// inodes, copies all inodes from old to new with the extended layout, and atomically swaps
/// the region pointers via WAL transaction.
///
/// Progress is checkpointed every <see cref="CheckpointIntervalInodes"/> inodes so that
/// migration can resume after crash without re-doing completed work.
///
/// Thread safety: NOT thread-safe. Only one migration may be in progress at a time.
/// The checkpoint block enforces this by detecting conflicting migrations on resume.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Background inode migration engine (OMA-03)")]
public sealed class BackgroundInodeMigration
{
    private readonly Stream _vdeStream;
    private readonly int _blockSize;

    /// <summary>Progress callback invoked during migration.</summary>
    public Action<MigrationProgress>? OnProgress { get; set; }

    /// <summary>
    /// Number of inodes between checkpoint saves. Lower values increase crash safety
    /// at the cost of more I/O overhead.
    /// </summary>
    public int CheckpointIntervalInodes { get; set; } = 1000;

    /// <summary>
    /// Creates a new BackgroundInodeMigration engine.
    /// </summary>
    /// <param name="vdeStream">The VDE stream for read/write operations.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public BackgroundInodeMigration(Stream vdeStream, int blockSize)
    {
        _vdeStream = vdeStream ?? throw new ArgumentNullException(nameof(vdeStream));
        if (blockSize < FormatConstants.MinBlockSize || blockSize > FormatConstants.MaxBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        _blockSize = blockSize;
    }

    /// <summary>
    /// Migrates all inodes to a new inode table with the given module added.
    /// The migration is crash-safe: progress is checkpointed every N inodes and the
    /// region swap is atomic via WAL transaction.
    /// </summary>
    /// <param name="module">The module to add to the inode layout.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Migration result with statistics.</returns>
    public async Task<MigrationResult> MigrateAsync(ModuleId module, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Step 1: Read superblock to get current layout
        var superblock = await ReadSuperblockAsync(ct);
        uint currentManifest = superblock.ModuleManifest;

        if (ModuleRegistry.IsModuleActive(currentManifest, module))
            return MigrationResult.Failed($"Module {module} is already active in the current manifest.");

        uint newManifest = currentManifest | (1u << (int)module);
        var oldLayout = InodeLayoutDescriptor.Create(currentManifest);
        var newSizeResult = InodeSizeCalculator.Calculate(newManifest);
        var newLayout = InodeLayoutDescriptor.Create(newManifest);

        // Step 2: Find inode table region from region directory
        var (inodeStartBlock, inodeBlockCount) = await FindInodeTableRegionAsync(ct);
        if (inodeBlockCount <= 0)
            return MigrationResult.Failed("Could not locate inode table region in the VDE.");

        // Calculate total inodes in existing table
        long totalInodes = (inodeBlockCount * _blockSize) / oldLayout.InodeSize;

        // Step 3: Check for existing checkpoint
        // Use block after the emergency recovery block (block 9) as checkpoint:
        // we use the last block of the mirror superblock group for WAL backup per 78-02,
        // so we use a block in the metadata WAL area. For simplicity, use the second-to-last
        // block of the old inode table range as checkpoint storage.
        long checkpointBlock = inodeStartBlock + inodeBlockCount - 1;
        var checkpoint = new MigrationCheckpoint(_vdeStream, _blockSize, checkpointBlock);
        var existingCheckpoint = await checkpoint.LoadAsync(ct);

        long startFromInode = 0;
        Guid migrationId;
        long newTableStartBlock;
        long newTableBlockCount;

        if (existingCheckpoint.HasValue)
        {
            var cp = existingCheckpoint.Value;
            // Verify it's for the same migration (same target module and original manifest)
            if (cp.TargetModule == module && cp.OriginalManifest == currentManifest)
            {
                // Resume from checkpoint
                startFromInode = cp.MigratedInodes;
                migrationId = cp.MigrationId;
                newTableStartBlock = cp.NewInodeTableStartBlock;
                newTableBlockCount = cp.NewInodeTableBlockCount;

                if (cp.Phase == MigrationPhase.Complete)
                {
                    await checkpoint.ClearAsync(ct);
                    return MigrationResult.Succeeded(cp.TotalInodes, sw.Elapsed, newLayout);
                }
                if (cp.Phase == MigrationPhase.SwappingRegions)
                {
                    // Swap was interrupted; re-do the swap
                    return await CompleteSwapAsync(superblock, newManifest, newLayout,
                        newTableStartBlock, newTableBlockCount,
                        inodeStartBlock, inodeBlockCount,
                        totalInodes, checkpoint, migrationId, module, sw, ct);
                }
            }
            else
            {
                return MigrationResult.Failed(
                    "A different migration is already in progress. Complete or rollback it first.");
            }
        }
        else
        {
            migrationId = Guid.NewGuid();

            // Step 4: Allocate new inode table region
            long newBlocksNeeded = (totalInodes * newLayout.InodeSize + _blockSize - 1) / _blockSize;

            var bitmapRegion = await FindBitmapRegionAsync(ct);
            var scanner = new FreeSpaceScanner(_vdeStream, _blockSize,
                bitmapRegion.StartBlock, bitmapRegion.BlockCount);
            var freeRange = scanner.FindContiguousFreeBlocks(newBlocksNeeded);

            if (!freeRange.HasValue)
                return MigrationResult.Failed(
                    $"Insufficient contiguous free space for new inode table ({newBlocksNeeded} blocks needed).");

            newTableStartBlock = freeRange.Value.StartBlock;
            newTableBlockCount = freeRange.Value.BlockCount;

            // Mark new blocks as allocated in bitmap via WAL
            var walRegion = await FindMetadataWalRegionAsync(ct);
            var walWriter = new WalJournaledRegionWriter(_vdeStream, _blockSize,
                walRegion.StartBlock, walRegion.BlockCount);
            await MarkBlocksAllocatedAsync(walWriter, bitmapRegion, newTableStartBlock, newBlocksNeeded, ct);

            // Save initial checkpoint
            await checkpoint.SaveAsync(new CheckpointData
            {
                MigrationId = migrationId,
                TargetModule = module,
                OriginalManifest = currentManifest,
                TargetManifest = newManifest,
                TotalInodes = totalInodes,
                MigratedInodes = 0,
                NewInodeTableStartBlock = newTableStartBlock,
                NewInodeTableBlockCount = newTableBlockCount,
                OldInodeTableStartBlock = inodeStartBlock,
                OldInodeTableBlockCount = inodeBlockCount,
                StartedUtc = DateTimeOffset.UtcNow,
                LastCheckpointUtc = DateTimeOffset.UtcNow,
                Phase = MigrationPhase.Allocating,
            }, ct);
        }

        // Step 5: Copy inodes from old to new table
        ReportProgress(totalInodes, startFromInode, MigrationPhase.CopyingInodes);

        var readBuffer = ArrayPool<byte>.Shared.Rent(oldLayout.InodeSize);
        var writeBuffer = ArrayPool<byte>.Shared.Rent(newLayout.InodeSize);
        try
        {
            for (long i = startFromInode; i < totalInodes; i++)
            {
                ct.ThrowIfCancellationRequested();

                // Read old inode
                long oldOffset = inodeStartBlock * _blockSize + i * oldLayout.InodeSize;
                _vdeStream.Seek(oldOffset, SeekOrigin.Begin);
                await ReadExactAsync(_vdeStream, readBuffer, oldLayout.InodeSize, ct);

                // Deserialize with old layout
                var inode = InodeV2.Deserialize(readBuffer.AsSpan(0, oldLayout.InodeSize), oldLayout);

                // Serialize with new layout (new module fields zero-filled by default)
                Array.Clear(writeBuffer, 0, newLayout.InodeSize);
                InodeV2.SerializeToSpan(inode, newLayout, writeBuffer.AsSpan(0, newLayout.InodeSize));

                // Write to new inode table
                long newOffset = newTableStartBlock * _blockSize + i * newLayout.InodeSize;
                _vdeStream.Seek(newOffset, SeekOrigin.Begin);
                await _vdeStream.WriteAsync(writeBuffer.AsMemory(0, newLayout.InodeSize), ct);

                // Checkpoint and progress reporting
                if ((i + 1) % CheckpointIntervalInodes == 0 || i == totalInodes - 1)
                {
                    await _vdeStream.FlushAsync(ct);

                    await checkpoint.SaveAsync(new CheckpointData
                    {
                        MigrationId = migrationId,
                        TargetModule = module,
                        OriginalManifest = currentManifest,
                        TargetManifest = newManifest,
                        TotalInodes = totalInodes,
                        MigratedInodes = i + 1,
                        NewInodeTableStartBlock = newTableStartBlock,
                        NewInodeTableBlockCount = newTableBlockCount,
                        OldInodeTableStartBlock = inodeStartBlock,
                        OldInodeTableBlockCount = inodeBlockCount,
                        StartedUtc = DateTimeOffset.UtcNow,
                        LastCheckpointUtc = DateTimeOffset.UtcNow,
                        Phase = MigrationPhase.CopyingInodes,
                    }, ct);

                    ReportProgress(totalInodes, i + 1, MigrationPhase.CopyingInodes);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            ArrayPool<byte>.Shared.Return(writeBuffer);
        }

        // Step 6: Swap regions atomically
        return await CompleteSwapAsync(superblock, newManifest, newLayout,
            newTableStartBlock, newTableBlockCount,
            inodeStartBlock, inodeBlockCount,
            totalInodes, checkpoint, migrationId, module, sw, ct);
    }

    /// <summary>
    /// Resumes a previously interrupted migration from the last checkpoint.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Migration result with statistics.</returns>
    public async Task<MigrationResult> ResumeAsync(CancellationToken ct)
    {
        // Find current inode table to locate checkpoint
        var (inodeStartBlock, inodeBlockCount) = await FindInodeTableRegionAsync(ct);
        if (inodeBlockCount <= 0)
            return MigrationResult.Failed("Could not locate inode table region in the VDE.");

        long checkpointBlock = inodeStartBlock + inodeBlockCount - 1;
        var checkpoint = new MigrationCheckpoint(_vdeStream, _blockSize, checkpointBlock);
        var existingCheckpoint = await checkpoint.LoadAsync(ct);

        if (!existingCheckpoint.HasValue)
            return MigrationResult.Failed("No pending migration checkpoint found.");

        var cp = existingCheckpoint.Value;

        // Resume by calling MigrateAsync with the same module
        return await MigrateAsync(cp.TargetModule, ct);
    }

    /// <summary>
    /// Rolls back a pending migration. If the migration was in progress (pre-swap),
    /// frees the new inode table blocks. If mid-swap, restores old region pointers.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task RollbackAsync(CancellationToken ct)
    {
        // Find current inode table to locate checkpoint
        var (inodeStartBlock, inodeBlockCount) = await FindInodeTableRegionAsync(ct);
        if (inodeBlockCount <= 0)
            return;

        long checkpointBlock = inodeStartBlock + inodeBlockCount - 1;
        var checkpoint = new MigrationCheckpoint(_vdeStream, _blockSize, checkpointBlock);
        var existingCheckpoint = await checkpoint.LoadAsync(ct);

        if (!existingCheckpoint.HasValue)
            return; // Nothing to rollback

        var cp = existingCheckpoint.Value;

        if (cp.Phase < MigrationPhase.SwappingRegions)
        {
            // Free the new inode table blocks in bitmap
            var bitmapRegion = await FindBitmapRegionAsync(ct);
            var walRegion = await FindMetadataWalRegionAsync(ct);
            var walWriter = new WalJournaledRegionWriter(_vdeStream, _blockSize,
                walRegion.StartBlock, walRegion.BlockCount);
            await MarkBlocksFreeAsync(walWriter, bitmapRegion,
                cp.NewInodeTableStartBlock, cp.NewInodeTableBlockCount, ct);
        }
        else if (cp.Phase == MigrationPhase.SwappingRegions)
        {
            // Mid-swap: restore old region pointers
            var walRegion = await FindMetadataWalRegionAsync(ct);
            var walWriter = new WalJournaledRegionWriter(_vdeStream, _blockSize,
                walRegion.StartBlock, walRegion.BlockCount);

            var superblock = await ReadSuperblockAsync(ct);
            var regionDir = await ReadRegionDirectoryAsync(ct);
            var rpt = await ReadRegionPointerTableAsync(ct);

            // Restore old inode table pointers
            UpdateRegionDirectory(regionDir, BlockTypeTags.INOD, cp.OldInodeTableStartBlock, cp.OldInodeTableBlockCount);
            UpdateRptInodeRegion(rpt, cp.OldInodeTableStartBlock, cp.OldInodeTableBlockCount);

            var restoredSuperblock = CreateUpdatedSuperblock(superblock, cp.OriginalManifest,
                (ushort)InodeSizeCalculator.Calculate(cp.OriginalManifest).InodeSize);

            var txn = walWriter.BeginTransaction();
            walWriter.AddRegionDirectoryUpdate(txn, regionDir);
            walWriter.AddRegionPointerTableUpdate(txn, rpt);
            walWriter.AddSuperblockUpdate(txn, restoredSuperblock, _blockSize);
            await walWriter.CommitAsync(txn, ct);
        }

        await checkpoint.ClearAsync(ct);
    }

    // ── Private Helpers ────────────────────────────────────────────────────

    private async Task<MigrationResult> CompleteSwapAsync(
        SuperblockV2 superblock, uint newManifest, InodeLayoutDescriptor newLayout,
        long newTableStartBlock, long newTableBlockCount,
        long oldTableStartBlock, long oldTableBlockCount,
        long totalInodes, MigrationCheckpoint checkpoint, Guid migrationId,
        ModuleId targetModule, Stopwatch sw, CancellationToken ct)
    {
        ReportProgress(totalInodes, totalInodes, MigrationPhase.SwappingRegions);

        // Save checkpoint marking swap phase
        await checkpoint.SaveAsync(new CheckpointData
        {
            MigrationId = migrationId,
            TargetModule = targetModule,
            OriginalManifest = superblock.ModuleManifest,
            TargetManifest = newManifest,
            TotalInodes = totalInodes,
            MigratedInodes = totalInodes,
            NewInodeTableStartBlock = newTableStartBlock,
            NewInodeTableBlockCount = newTableBlockCount,
            OldInodeTableStartBlock = oldTableStartBlock,
            OldInodeTableBlockCount = oldTableBlockCount,
            StartedUtc = DateTimeOffset.UtcNow,
            LastCheckpointUtc = DateTimeOffset.UtcNow,
            Phase = MigrationPhase.SwappingRegions,
        }, ct);

        // WAL transaction to atomically update all metadata
        var walRegion = await FindMetadataWalRegionAsync(ct);
        var walWriter = new WalJournaledRegionWriter(_vdeStream, _blockSize,
            walRegion.StartBlock, walRegion.BlockCount);

        var regionDir = await ReadRegionDirectoryAsync(ct);
        var rpt = await ReadRegionPointerTableAsync(ct);

        // Update inode table region pointers to new location
        UpdateRegionDirectory(regionDir, BlockTypeTags.INOD, newTableStartBlock, newTableBlockCount);
        UpdateRptInodeRegion(rpt, newTableStartBlock, newTableBlockCount);

        // Update superblock with new manifest and inode size
        var updatedSuperblock = CreateUpdatedSuperblock(superblock, newManifest,
            (ushort)newLayout.InodeSize);

        var txn = walWriter.BeginTransaction();
        walWriter.AddRegionDirectoryUpdate(txn, regionDir);
        walWriter.AddRegionPointerTableUpdate(txn, rpt);
        walWriter.AddSuperblockUpdate(txn, updatedSuperblock, _blockSize);
        await walWriter.CommitAsync(txn, ct);

        // Step 7: Complete
        await checkpoint.ClearAsync(ct);
        ReportProgress(totalInodes, totalInodes, MigrationPhase.Complete);

        return MigrationResult.Succeeded(totalInodes, sw.Elapsed, newLayout);
    }

    private SuperblockV2 CreateUpdatedSuperblock(SuperblockV2 original, uint newManifest, ushort newInodeSize)
    {
        return new SuperblockV2(
            magic: original.Magic,
            versionInfo: original.VersionInfo,
            moduleManifest: newManifest,
            moduleConfig: original.ModuleConfig,
            moduleConfigExt: original.ModuleConfigExt,
            blockSize: original.BlockSize,
            totalBlocks: original.TotalBlocks,
            freeBlocks: original.FreeBlocks,
            expectedFileSize: original.ExpectedFileSize,
            totalAllocatedBlocks: original.TotalAllocatedBlocks,
            volumeUuid: original.VolumeUuid,
            clusterNodeId: original.ClusterNodeId,
            defaultCompressionAlgo: original.DefaultCompressionAlgo,
            defaultEncryptionAlgo: original.DefaultEncryptionAlgo,
            defaultChecksumAlgo: original.DefaultChecksumAlgo,
            inodeSize: newInodeSize,
            policyVersion: original.PolicyVersion,
            replicationEpoch: original.ReplicationEpoch,
            wormHighWaterMark: original.WormHighWaterMark,
            encryptionKeyFingerprint: original.EncryptionKeyFingerprint,
            sovereigntyZoneId: original.SovereigntyZoneId,
            volumeLabel: original.VolumeLabel,
            createdTimestampUtc: original.CreatedTimestampUtc,
            modifiedTimestampUtc: DateTimeOffset.UtcNow.Ticks,
            lastScrubTimestamp: original.LastScrubTimestamp,
            checkpointSequence: original.CheckpointSequence + 1,
            errorMapBlockCount: original.ErrorMapBlockCount,
            lastWriterSessionId: original.LastWriterSessionId,
            lastWriterTimestamp: DateTimeOffset.UtcNow.Ticks,
            lastWriterNodeId: original.LastWriterNodeId,
            physicalAllocatedBlocks: original.PhysicalAllocatedBlocks,
            headerIntegritySeal: original.HeaderIntegritySeal);
    }

    private void ReportProgress(long total, long migrated, MigrationPhase phase)
    {
        OnProgress?.Invoke(new MigrationProgress
        {
            TotalInodes = total,
            MigratedInodes = migrated,
            PercentComplete = total > 0 ? (double)migrated / total * 100.0 : 100.0,
            Phase = phase,
        });
    }

    private async Task<SuperblockV2> ReadSuperblockAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            _vdeStream.Seek(0, SeekOrigin.Begin);
            await ReadExactAsync(_vdeStream, buffer, _blockSize, ct);
            return SuperblockV2.Deserialize(buffer.AsSpan(0, _blockSize), _blockSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<RegionDirectory> ReadRegionDirectoryAsync(CancellationToken ct)
    {
        int totalSize = FormatConstants.RegionDirectoryBlocks * _blockSize;
        var buffer = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            _vdeStream.Seek(FormatConstants.RegionDirectoryStartBlock * _blockSize, SeekOrigin.Begin);
            await ReadExactAsync(_vdeStream, buffer, totalSize, ct);
            return RegionDirectory.Deserialize(buffer.AsSpan(0, totalSize), _blockSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<RegionPointerTable> ReadRegionPointerTableAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            _vdeStream.Seek(1 * _blockSize, SeekOrigin.Begin);
            await ReadExactAsync(_vdeStream, buffer, _blockSize, ct);
            return RegionPointerTable.Deserialize(buffer.AsSpan(0, _blockSize), _blockSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<(long StartBlock, long BlockCount)> FindInodeTableRegionAsync(CancellationToken ct)
    {
        var regionDir = await ReadRegionDirectoryAsync(ct);
        int slot = regionDir.FindRegion(BlockTypeTags.INOD);
        if (slot < 0) return (0, 0);
        var pointer = regionDir.GetSlot(slot);
        return (pointer.StartBlock, pointer.BlockCount);
    }

    private async Task<(long StartBlock, long BlockCount)> FindBitmapRegionAsync(CancellationToken ct)
    {
        var regionDir = await ReadRegionDirectoryAsync(ct);
        int slot = regionDir.FindRegion(BlockTypeTags.BMAP);
        if (slot < 0) return (0, 0);
        var pointer = regionDir.GetSlot(slot);
        return (pointer.StartBlock, pointer.BlockCount);
    }

    private async Task<(long StartBlock, long BlockCount)> FindMetadataWalRegionAsync(CancellationToken ct)
    {
        var regionDir = await ReadRegionDirectoryAsync(ct);
        int slot = regionDir.FindRegion(BlockTypeTags.MWAL);
        if (slot < 0) return (0, 0);
        var pointer = regionDir.GetSlot(slot);
        return (pointer.StartBlock, pointer.BlockCount);
    }

    private async Task MarkBlocksAllocatedAsync(
        WalJournaledRegionWriter walWriter,
        (long StartBlock, long BlockCount) bitmapRegion,
        long blockStart, long count, CancellationToken ct)
    {
        int payloadPerBlock = _blockSize - FormatConstants.UniversalBlockTrailerSize;
        long bitsPerBitmapBlock = payloadPerBlock * 8L;

        var txn = walWriter.BeginTransaction();

        for (long i = 0; i < count; i++)
        {
            long blockNumber = blockStart + i;
            long bitmapBlockIndex = blockNumber / bitsPerBitmapBlock;
            long bitmapBlockNumber = bitmapRegion.StartBlock + bitmapBlockIndex;
            int byteInPayload = (int)((blockNumber % bitsPerBitmapBlock) / 8);
            int bitInByte = (int)(blockNumber % 8);

            // Read current bitmap block
            var oldData = new byte[_blockSize];
            _vdeStream.Seek(bitmapBlockNumber * _blockSize, SeekOrigin.Begin);
            await ReadExactAsync(_vdeStream, oldData, _blockSize, ct);

            var newData = new byte[_blockSize];
            Array.Copy(oldData, newData, _blockSize);

            // Set the allocation bit
            newData[byteInPayload] |= (byte)(1 << bitInByte);

            // Re-write trailer
            UniversalBlockTrailer.Write(newData, _blockSize, BlockTypeTags.BMAP, 1);

            walWriter.AddBitmapUpdate(txn, bitmapBlockNumber, oldData, newData);
        }

        await walWriter.CommitAsync(txn, ct);
    }

    private async Task MarkBlocksFreeAsync(
        WalJournaledRegionWriter walWriter,
        (long StartBlock, long BlockCount) bitmapRegion,
        long blockStart, long count, CancellationToken ct)
    {
        int payloadPerBlock = _blockSize - FormatConstants.UniversalBlockTrailerSize;
        long bitsPerBitmapBlock = payloadPerBlock * 8L;

        var txn = walWriter.BeginTransaction();

        for (long i = 0; i < count; i++)
        {
            long blockNumber = blockStart + i;
            long bitmapBlockIndex = blockNumber / bitsPerBitmapBlock;
            long bitmapBlockNumber = bitmapRegion.StartBlock + bitmapBlockIndex;
            int byteInPayload = (int)((blockNumber % bitsPerBitmapBlock) / 8);
            int bitInByte = (int)(blockNumber % 8);

            var oldData = new byte[_blockSize];
            _vdeStream.Seek(bitmapBlockNumber * _blockSize, SeekOrigin.Begin);
            await ReadExactAsync(_vdeStream, oldData, _blockSize, ct);

            var newData = new byte[_blockSize];
            Array.Copy(oldData, newData, _blockSize);

            // Clear the allocation bit
            newData[byteInPayload] &= (byte)~(1 << bitInByte);

            UniversalBlockTrailer.Write(newData, _blockSize, BlockTypeTags.BMAP, 1);

            walWriter.AddBitmapUpdate(txn, bitmapBlockNumber, oldData, newData);
        }

        await walWriter.CommitAsync(txn, ct);
    }

    private static void UpdateRegionDirectory(RegionDirectory dir, uint regionTypeId, long startBlock, long blockCount)
    {
        dir.RemoveRegion(regionTypeId);
        dir.AddRegion(regionTypeId, RegionFlags.Active, startBlock, blockCount);
    }

    private static void UpdateRptInodeRegion(RegionPointerTable rpt, long startBlock, long blockCount)
    {
        // Find and update the INOD slot in the region pointer table
        for (int i = 0; i < RegionPointerTable.MaxSlots; i++)
        {
            var pointer = rpt.GetSlot(i);
            if (pointer.RegionTypeId == BlockTypeTags.INOD)
            {
                rpt.SetSlot(i, new RegionPointer(
                    BlockTypeTags.INOD, pointer.Flags, startBlock, blockCount, pointer.UsedBlocks));
                return;
            }
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int bytesRead = await stream.ReadAsync(
                buffer.AsMemory(totalRead, count - totalRead), ct);
            if (bytesRead == 0)
                throw new InvalidDataException("Unexpected end of stream.");
            totalRead += bytesRead;
        }
    }
}
