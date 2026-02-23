using System.Buffers;
using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.ModuleManagement;

/// <summary>
/// Result of an online region addition operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Online region addition result (OMA-01)")]
public readonly record struct RegionAdditionResult
{
    /// <summary>True if the region addition completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Error message if the operation failed, null on success.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The region directory slot index where the first region was placed.</summary>
    public int RegionSlotIndex { get; init; }

    /// <summary>Start block of the first allocated region.</summary>
    public long StartBlock { get; init; }

    /// <summary>Total block count of the first allocated region.</summary>
    public long BlockCount { get; init; }

    /// <summary>Creates a success result.</summary>
    internal static RegionAdditionResult Succeeded(int slotIndex, long startBlock, long blockCount) =>
        new() { Success = true, RegionSlotIndex = slotIndex, StartBlock = startBlock, BlockCount = blockCount };

    /// <summary>Creates a failure result with an error message.</summary>
    internal static RegionAdditionResult Failed(string error) =>
        new() { Success = false, ErrorMessage = error, RegionSlotIndex = -1 };
}

/// <summary>
/// Orchestrates end-to-end online region addition for a VDE module without dismounting.
/// The operation detects free space in the allocation bitmap, allocates contiguous blocks
/// for the new region(s), and commits the region directory + region pointer table +
/// superblock update within a single WAL transaction so that a crash at any point
/// leaves the VDE in its prior valid state.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Online region addition orchestrator (OMA-01)")]
public sealed class OnlineRegionAddition
{
    private readonly Stream _vdeStream;
    private readonly int _blockSize;

    /// <summary>
    /// Creates a new OnlineRegionAddition orchestrator for the given VDE stream.
    /// </summary>
    /// <param name="vdeStream">The VDE stream (must support read/write/seek).</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public OnlineRegionAddition(Stream vdeStream, int blockSize)
    {
        _vdeStream = vdeStream ?? throw new ArgumentNullException(nameof(vdeStream));
        if (blockSize < FormatConstants.MinBlockSize || blockSize > FormatConstants.MaxBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be between {FormatConstants.MinBlockSize} and {FormatConstants.MaxBlockSize}.");
        if (!vdeStream.CanRead || !vdeStream.CanWrite || !vdeStream.CanSeek)
            throw new ArgumentException("Stream must support read, write, and seek.", nameof(vdeStream));
        _blockSize = blockSize;
    }

    /// <summary>
    /// Adds a module's required regions to a running VDE from free space.
    /// The entire operation is WAL-journaled: crash at any point leaves the VDE
    /// in its previous valid state. Both RegionDirectory and RegionPointerTable
    /// are updated within the same WAL transaction.
    /// </summary>
    /// <param name="module">The module to add regions for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result indicating success/failure with region location details.</returns>
    public async Task<RegionAdditionResult> AddModuleRegionsAsync(ModuleId module, CancellationToken ct)
    {
        // Step 1: Read current superblock group
        var superblockGroup = ReadSuperblockGroup();
        var superblock = superblockGroup.PrimarySuperblock;
        var rpt = superblockGroup.RegionPointers;

        // Step 2: Validate module is not already active
        if (ModuleRegistry.IsModuleActive(superblock.ModuleManifest, module))
            return RegionAdditionResult.Failed(
                $"Module {module} is already active in the VDE (manifest=0x{superblock.ModuleManifest:X8}).");

        // Step 3: Look up module's required regions
        var moduleDef = ModuleRegistry.GetModule(module);
        if (!moduleDef.HasRegion)
            return RegionAdditionResult.Failed(
                $"Module {module} does not require any dedicated regions.");

        // Step 4: Find allocation bitmap and scan for free space
        int bmapSlot = rpt.FindRegion(BlockTypeTags.BMAP);
        if (bmapSlot < 0)
            return RegionAdditionResult.Failed("Allocation bitmap region (BMAP) not found in the VDE.");

        var bmapPointer = rpt.GetSlot(bmapSlot);
        var scanner = new FreeSpaceScanner(_vdeStream, _blockSize,
            bmapPointer.StartBlock, bmapPointer.BlockCount);

        // Calculate required blocks per region and find free space
        var regionAllocations = new List<(string Name, uint Tag, long StartBlock, long BlockCount)>();
        int firstSlotIndex = -1;
        long firstStartBlock = 0;
        long firstBlockCount = 0;

        foreach (var regionName in moduleDef.RegionNames)
        {
            long requiredBlocks = CalculateRegionBlocks(moduleDef, superblock.TotalBlocks);
            var freeRange = scanner.FindContiguousFreeBlocks(requiredBlocks);
            if (freeRange is null)
                return RegionAdditionResult.Failed(
                    $"Insufficient contiguous free space for region '{regionName}' " +
                    $"(need {requiredBlocks} blocks). Consider compacting the VDE first.");

            var tag = ResolveBlockTypeTag(regionName);
            regionAllocations.Add((regionName, tag, freeRange.Value.StartBlock, freeRange.Value.BlockCount));
        }

        // Step 5: Find the metadata WAL for journaling
        int mwalSlot = rpt.FindRegion(BlockTypeTags.MWAL);
        if (mwalSlot < 0)
            return RegionAdditionResult.Failed("Metadata WAL region (MWAL) not found in the VDE.");

        var mwalPointer = rpt.GetSlot(mwalSlot);
        var walWriter = new WalJournaledRegionWriter(_vdeStream, _blockSize,
            mwalPointer.StartBlock, mwalPointer.BlockCount);

        var txn = walWriter.BeginTransaction();

        // Step 6: For each region, update bitmap + directory + RPT
        var regionDirectory = ReadRegionDirectory();

        foreach (var (name, tag, startBlock, blockCount) in regionAllocations)
        {
            ct.ThrowIfCancellationRequested();

            // Mark blocks as allocated in the bitmap (flip bits 0->1)
            await UpdateBitmapAllocationAsync(walWriter, txn, bmapPointer,
                startBlock, blockCount, ct);

            // Add region to RegionDirectory
            int slotIdx = regionDirectory.AddRegion(tag, RegionFlags.None, startBlock, blockCount);
            if (firstSlotIndex < 0)
            {
                firstSlotIndex = slotIdx;
                firstStartBlock = startBlock;
                firstBlockCount = blockCount;
            }

            // Add region to RPT
            int rptSlot = rpt.FindFreeSlot();
            if (rptSlot < 0)
                return RegionAdditionResult.Failed("Region pointer table is full; no free slots available.");

            rpt.SetSlot(rptSlot, new RegionPointer(tag, RegionFlags.Active, startBlock, blockCount, 0));
        }

        // Add directory and RPT updates to WAL transaction
        walWriter.AddRegionDirectoryUpdate(txn, regionDirectory);
        walWriter.AddRegionPointerTableUpdate(txn, rpt);

        // Step 7-8: Update superblock
        uint newManifest = superblock.ModuleManifest | (1u << (int)module);

        // Recalculate inode size with new module
        var inodeSizeResult = InodeSizeCalculator.Calculate(newManifest);

        long totalAllocatedNew = regionAllocations.Sum(r => r.BlockCount);
        long newFreeBlocks = superblock.FreeBlocks - totalAllocatedNew;
        long newTotalAllocated = superblock.TotalAllocatedBlocks + totalAllocatedNew;

        var updatedSuperblock = new SuperblockV2(
            magic: superblock.Magic,
            versionInfo: superblock.VersionInfo,
            moduleManifest: newManifest,
            moduleConfig: superblock.ModuleConfig,
            moduleConfigExt: superblock.ModuleConfigExt,
            blockSize: superblock.BlockSize,
            totalBlocks: superblock.TotalBlocks,
            freeBlocks: newFreeBlocks,
            expectedFileSize: superblock.ExpectedFileSize,
            totalAllocatedBlocks: newTotalAllocated,
            volumeUuid: superblock.VolumeUuid,
            clusterNodeId: superblock.ClusterNodeId,
            defaultCompressionAlgo: superblock.DefaultCompressionAlgo,
            defaultEncryptionAlgo: superblock.DefaultEncryptionAlgo,
            defaultChecksumAlgo: superblock.DefaultChecksumAlgo,
            inodeSize: (ushort)inodeSizeResult.InodeSize,
            policyVersion: superblock.PolicyVersion,
            replicationEpoch: superblock.ReplicationEpoch,
            wormHighWaterMark: superblock.WormHighWaterMark,
            encryptionKeyFingerprint: superblock.EncryptionKeyFingerprint,
            sovereigntyZoneId: superblock.SovereigntyZoneId,
            volumeLabel: superblock.VolumeLabel,
            createdTimestampUtc: superblock.CreatedTimestampUtc,
            modifiedTimestampUtc: DateTimeOffset.UtcNow.Ticks,
            lastScrubTimestamp: superblock.LastScrubTimestamp,
            checkpointSequence: superblock.CheckpointSequence + 1,
            errorMapBlockCount: superblock.ErrorMapBlockCount,
            lastWriterSessionId: superblock.LastWriterSessionId,
            lastWriterTimestamp: DateTimeOffset.UtcNow.Ticks,
            lastWriterNodeId: superblock.LastWriterNodeId,
            physicalAllocatedBlocks: superblock.PhysicalAllocatedBlocks + totalAllocatedNew,
            headerIntegritySeal: superblock.HeaderIntegritySeal);

        // Step 9: Add superblock update to transaction
        walWriter.AddSuperblockUpdate(txn, updatedSuperblock, _blockSize);

        // Step 10: Commit the transaction atomically
        await walWriter.CommitAsync(txn, ct);

        // Step 11: Return success
        return RegionAdditionResult.Succeeded(firstSlotIndex, firstStartBlock, firstBlockCount);
    }

    /// <summary>
    /// Pre-flight check: determines whether a module can be added to the VDE
    /// without actually performing the write. Reads the superblock, verifies the
    /// module is not already active, and checks for sufficient free space.
    /// </summary>
    /// <param name="module">The module to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the module can be added; false otherwise.</returns>
    public async Task<bool> CanAddModuleAsync(ModuleId module, CancellationToken ct)
    {
        var superblockGroup = ReadSuperblockGroup();
        var superblock = superblockGroup.PrimarySuperblock;
        var rpt = superblockGroup.RegionPointers;

        // Module already active
        if (ModuleRegistry.IsModuleActive(superblock.ModuleManifest, module))
            return false;

        var moduleDef = ModuleRegistry.GetModule(module);
        if (!moduleDef.HasRegion)
            return false; // No regions needed; nothing to add online

        // Check for BMAP region
        int bmapSlot = rpt.FindRegion(BlockTypeTags.BMAP);
        if (bmapSlot < 0)
            return false;

        var bmapPointer = rpt.GetSlot(bmapSlot);
        var scanner = new FreeSpaceScanner(_vdeStream, _blockSize,
            bmapPointer.StartBlock, bmapPointer.BlockCount);

        // Check each required region has enough free space
        foreach (var regionName in moduleDef.RegionNames)
        {
            long requiredBlocks = CalculateRegionBlocks(moduleDef, superblock.TotalBlocks);
            var freeRange = scanner.FindContiguousFreeBlocks(requiredBlocks);
            if (freeRange is null)
                return false;
        }

        await Task.CompletedTask; // Satisfy async signature
        return true;
    }

    /// <summary>
    /// Calculates the number of blocks to allocate for a module's region based on
    /// the module's characteristics and the total VDE size.
    /// </summary>
    /// <param name="module">The module definition.</param>
    /// <param name="totalBlocks">Total blocks in the VDE.</param>
    /// <returns>Number of blocks to allocate for the region.</returns>
    private static long CalculateRegionBlocks(VdeModule module, long totalBlocks)
    {
        long calculated;
        bool isLargeRegion = module.RegionNames.Length >= 3
            || module.Id == ModuleId.Streaming;

        if (isLargeRegion)
        {
            // Large regions: max(256, totalBlocks / 256)
            calculated = Math.Max(256, totalBlocks / 256);
        }
        else
        {
            // Small regions: max(64, totalBlocks / 1024)
            calculated = Math.Max(64, totalBlocks / 1024);
        }

        // Cap at totalBlocks / 16 (never use more than 6.25% of VDE per region)
        long cap = totalBlocks / 16;
        if (cap < 64) cap = 64; // Ensure minimum even on tiny VDEs

        return Math.Min(calculated, cap);
    }

    /// <summary>
    /// Updates the allocation bitmap to mark the specified block range as allocated.
    /// Flips bits from 0 (free) to 1 (allocated) for each block in the range.
    /// </summary>
    private async Task UpdateBitmapAllocationAsync(
        WalJournaledRegionWriter walWriter,
        WalTransaction txn,
        RegionPointer bmapPointer,
        long startBlock,
        long blockCount,
        CancellationToken ct)
    {
        int payloadPerBlock = _blockSize - FormatConstants.UniversalBlockTrailerSize;

        // Determine which bitmap blocks are affected
        long firstBit = startBlock;
        long lastBit = startBlock + blockCount - 1;
        long firstBitmapByteIndex = firstBit / 8;
        long lastBitmapByteIndex = lastBit / 8;
        long firstBitmapBlock = firstBitmapByteIndex / payloadPerBlock;
        long lastBitmapBlock = lastBitmapByteIndex / payloadPerBlock;

        for (long bmapBlockIdx = firstBitmapBlock; bmapBlockIdx <= lastBitmapBlock; bmapBlockIdx++)
        {
            long absoluteBlock = bmapPointer.StartBlock + bmapBlockIdx;

            // Read original bitmap block
            var oldBits = ReadBlock(absoluteBlock);
            var newBits = new byte[_blockSize];
            Array.Copy(oldBits, newBits, _blockSize);

            // Flip bits within this block's payload area
            long blockPayloadStartBit = bmapBlockIdx * payloadPerBlock * 8;
            long blockPayloadEndBit = blockPayloadStartBit + (payloadPerBlock * 8) - 1;

            long bitStart = Math.Max(firstBit, blockPayloadStartBit);
            long bitEnd = Math.Min(lastBit, blockPayloadEndBit);

            for (long bit = bitStart; bit <= bitEnd; bit++)
            {
                long relativeBit = bit - blockPayloadStartBit;
                int byteIndex = (int)(relativeBit / 8);
                int bitOffset = (int)(relativeBit % 8);
                newBits[byteIndex] |= (byte)(1 << bitOffset);
            }

            walWriter.AddBitmapUpdate(txn, absoluteBlock, oldBits, newBits);
        }

        await Task.CompletedTask; // All WAL entries added synchronously
    }

    /// <summary>
    /// Reads the current superblock group (blocks 0-3) from the VDE stream.
    /// </summary>
    private SuperblockGroup ReadSuperblockGroup()
    {
        int groupSize = FormatConstants.SuperblockGroupBlocks * _blockSize;
        var buffer = ArrayPool<byte>.Shared.Rent(groupSize);
        try
        {
            _vdeStream.Seek(0, SeekOrigin.Begin);
            int totalRead = 0;
            while (totalRead < groupSize)
            {
                int bytesRead = _vdeStream.Read(buffer, totalRead, groupSize - totalRead);
                if (bytesRead == 0)
                    throw new InvalidDataException("Unexpected end of stream reading superblock group.");
                totalRead += bytesRead;
            }

            return SuperblockGroup.DeserializeFromBlocks(buffer.AsSpan(0, groupSize), _blockSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads the region directory (blocks 8-9) from the VDE stream.
    /// </summary>
    private RegionDirectory ReadRegionDirectory()
    {
        int dirSize = FormatConstants.RegionDirectoryBlocks * _blockSize;
        var buffer = ArrayPool<byte>.Shared.Rent(dirSize);
        try
        {
            long offset = FormatConstants.RegionDirectoryStartBlock * _blockSize;
            _vdeStream.Seek(offset, SeekOrigin.Begin);
            int totalRead = 0;
            while (totalRead < dirSize)
            {
                int bytesRead = _vdeStream.Read(buffer, totalRead, dirSize - totalRead);
                if (bytesRead == 0)
                    throw new InvalidDataException("Unexpected end of stream reading region directory.");
                totalRead += bytesRead;
            }

            return RegionDirectory.Deserialize(buffer.AsSpan(0, dirSize), _blockSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads a single block from the VDE stream.
    /// </summary>
    private byte[] ReadBlock(long blockNumber)
    {
        var buffer = new byte[_blockSize];
        long offset = blockNumber * _blockSize;
        _vdeStream.Seek(offset, SeekOrigin.Begin);

        int totalRead = 0;
        while (totalRead < _blockSize)
        {
            int bytesRead = _vdeStream.Read(buffer, totalRead, _blockSize - totalRead);
            if (bytesRead == 0)
                throw new InvalidDataException($"Unexpected end of stream reading block {blockNumber}.");
            totalRead += bytesRead;
        }

        return buffer;
    }

    /// <summary>
    /// Resolves a region name to its block type tag, following the same pattern as VdeCreator.
    /// </summary>
    private static uint ResolveBlockTypeTag(string regionName) => regionName switch
    {
        "PolicyVault" => BlockTypeTags.POLV,
        "EncryptionHeader" => BlockTypeTags.ENCR,
        "ComplianceVault" => BlockTypeTags.CMVT,
        "AuditLog" or "AuditLogRegion" => BlockTypeTags.ALOG,
        "IntelligenceCache" => BlockTypeTags.INTE,
        "TagIndexRegion" => BlockTypeTags.TAGI,
        "ReplicationState" => BlockTypeTags.REPL,
        "RAIDMetadata" => BlockTypeTags.RAID,
        "StreamingAppend" => BlockTypeTags.STRE,
        "DataWAL" => BlockTypeTags.DWAL,
        "ComputeCodeCache" => BlockTypeTags.CODE,
        "CrossVDEReferenceTable" => BlockTypeTags.XREF,
        "ConsensusLogRegion" => BlockTypeTags.CLOG,
        "DictionaryRegion" => BlockTypeTags.DICT,
        "IntegrityTree" => BlockTypeTags.MTRK,
        "SnapshotTable" => BlockTypeTags.SNAP,
        "BTreeIndexForest" => BlockTypeTags.BTRE,
        "AnonymizationTable" => BlockTypeTags.ANON,
        "MetricsLogRegion" => BlockTypeTags.MLOG,
        _ => BlockTypeTags.DATA,
    };
}
