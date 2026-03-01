using System.Buffers.Binary;
using System.IO.Hashing;
using System.Numerics;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Describes the calculated layout of all regions within a DWVD v2.0 file.
/// Used for previewing the disk layout before creation or for diagnostic display.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 layout info (VDE2-06)")]
public readonly record struct VdeLayoutInfo
{
    /// <summary>Block size in bytes.</summary>
    public int BlockSize { get; init; }

    /// <summary>Total number of logical blocks.</summary>
    public long TotalBlocks { get; init; }

    /// <summary>Total blocks used by metadata (superblocks, directories, WAL, bitmaps, etc.).</summary>
    public long MetadataBlocks { get; init; }

    /// <summary>Blocks available for user data.</summary>
    public long DataBlocks { get; init; }

    /// <summary>Metadata overhead as a percentage of total blocks.</summary>
    public double OverheadPercent { get; init; }

    /// <summary>Inode size in bytes for this profile.</summary>
    public int InodeSize { get; init; }

    /// <summary>
    /// Map of region names to their block ranges (start block, block count).
    /// </summary>
    public IReadOnlyDictionary<string, (long StartBlock, long BlockCount)> Regions { get; init; }
}

/// <summary>
/// The main entry point for creating complete DWVD v2.0 files. Takes a <see cref="VdeCreationProfile"/>
/// and produces a valid VDE file with all metadata regions, superblock groups, region directory,
/// allocation bitmap, inode table, WAL headers, and module-specific regions properly initialized.
/// </summary>
/// <remarks>
/// Block layout:
/// <code>
///   Blocks 0-3:   Primary Superblock Group
///   Blocks 4-7:   Mirror Superblock Group
///   Blocks 8-9:   Region Directory (2 blocks)
///   Blocks 10-11: Policy Vault (if SEC module active)
///   Blocks 12-13: Encryption Header (if SEC module active)
///   Block 14+:    Allocation Bitmap
///   After bitmap: Inode Table
///   After inodes: Metadata WAL
///   After MWAL:   Data WAL (if Streaming module active)
///   After WALs:   Module-specific regions
///   Remaining:    Data Region
/// </code>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 creator (VDE2-06)")]
public static class VdeCreator
{
    /// <summary>Minimum number of blocks required for a valid VDE.</summary>
    private const long MinimumBlocks = 64;

    /// <summary>
    /// Minimum (floor) inode table size in blocks. Used via <c>Math.Max</c> to ensure
    /// the inode table is at least this large even on small volumes.
    /// Cat 15 (finding 824): the name "DefaultBlocks" is a minimum/floor, not a hard maximum.
    /// </summary>
    private const long InodeTableDefaultBlocks = 1024;

    /// <summary>Inode table fraction of total blocks (1/256).</summary>
    private const long InodeTableFractionDivisor = 256;

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates a complete DWVD v2.0 file at the specified path using the given profile.
    /// Writes all metadata blocks (superblocks, region directory, bitmap, inode table,
    /// WAL headers, module regions) and leaves data blocks as sparse holes.
    /// </summary>
    /// <param name="filePath">Full path for the new VDE file.</param>
    /// <param name="profile">Creation profile defining modules, block size, and settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file path of the created VDE.</returns>
    /// <exception cref="ArgumentException">Profile has invalid parameters.</exception>
    public static async Task<string> CreateVdeAsync(
        string filePath,
        VdeCreationProfile profile,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profile);

        var layout = CalculateLayout(profile);
        var blockSize = profile.BlockSize;
        long logicalSize = profile.TotalBlocks * blockSize;

        // Create file (sparse or pre-allocated)
        FileStream stream;
        if (profile.ThinProvisioned)
        {
            stream = ThinProvisioning.CreateSparseFile(filePath, logicalSize);
        }
        else
        {
            stream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite,
                FileShare.None, bufferSize: 4096, FileOptions.Asynchronous);
            stream.SetLength(logicalSize);
        }

        await using (stream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var volumeUuid = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow.Ticks;
            var moduleConfig = profile.GetConfig();
            var inodeLayout = InodeLayoutDescriptor.Create(profile.ModuleManifest);

            // Build region pointer table and region directory
            var rpt = new RegionPointerTable();
            var regionDir = new RegionDirectory();
            int slotIndex = 0;

            foreach (var (name, (startBlock, blockCount)) in layout.Regions)
            {
                var tag = ResolveBlockTypeTag(name);
                var pointer = new RegionPointer(tag, RegionFlags.Active, startBlock, blockCount, 0);
                rpt.SetSlot(slotIndex, pointer);
                regionDir.AddRegion(tag, RegionFlags.None, startBlock, blockCount);
                slotIndex++;
            }

            // Build superblock
            var superblock = new SuperblockV2(
                magic: MagicSignature.CreateDefault(),
                versionInfo: new FormatVersionInfo(
                    minReaderVersion: FormatConstants.FormatMajorVersion,
                    minWriterVersion: FormatConstants.FormatMajorVersion,
                    incompatibleFeatures: IncompatibleFeatureFlags.None,
                    readOnlyCompatibleFeatures: ReadOnlyCompatibleFeatureFlags.None,
                    compatibleFeatures: CompatibleFeatureFlags.None),
                moduleManifest: profile.ModuleManifest,
                moduleConfig: moduleConfig.ConfigPrimary,
                moduleConfigExt: moduleConfig.ConfigExtended,
                blockSize: blockSize,
                totalBlocks: profile.TotalBlocks,
                freeBlocks: layout.DataBlocks,
                expectedFileSize: logicalSize,
                totalAllocatedBlocks: layout.MetadataBlocks,
                volumeUuid: volumeUuid,
                clusterNodeId: Guid.Empty,
                defaultCompressionAlgo: 0,
                defaultEncryptionAlgo: 0,
                defaultChecksumAlgo: 1, // XxHash64
                inodeSize: (ushort)inodeLayout.InodeSize,
                policyVersion: 1,
                replicationEpoch: 0,
                wormHighWaterMark: 0,
                encryptionKeyFingerprint: 0,
                sovereigntyZoneId: 0,
                volumeLabel: new byte[SuperblockV2.VolumeLabelSize],
                createdTimestampUtc: now,
                modifiedTimestampUtc: now,
                lastScrubTimestamp: 0,
                checkpointSequence: 1,
                errorMapBlockCount: 0,
                lastWriterSessionId: Guid.Empty,
                lastWriterTimestamp: now,
                lastWriterNodeId: Guid.Empty,
                physicalAllocatedBlocks: 0,
                headerIntegritySeal: new byte[SuperblockV2.IntegritySealSize]);

            var sbGroup = new SuperblockGroup(
                superblock, rpt,
                ExtendedMetadata.CreateDefault(),
                IntegrityAnchor.CreateDefault());

            // Write superblock group (blocks 0-3) and mirror (blocks 4-7)
            var sbData = sbGroup.SerializeWithMirror(blockSize);
            stream.Position = 0;
            await stream.WriteAsync(sbData, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // Write Region Directory (blocks 8-9)
            var rdBuffer = new byte[FormatConstants.RegionDirectoryBlocks * blockSize];
            regionDir.Generation = 1;
            regionDir.Serialize(rdBuffer, blockSize);
            stream.Position = FormatConstants.RegionDirectoryStartBlock * blockSize;
            await stream.WriteAsync(rdBuffer, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // Write allocation bitmap
            if (layout.Regions.TryGetValue("AllocationBitmap", out var bitmapRegion))
            {
                await WriteAllocationBitmapAsync(stream, bitmapRegion.StartBlock,
                    bitmapRegion.BlockCount, layout.MetadataBlocks, blockSize, cancellationToken);
            }

            // Write inode table header
            if (layout.Regions.TryGetValue("InodeTable", out var inodeRegion))
            {
                await WriteInodeTableHeaderAsync(stream, inodeRegion.StartBlock,
                    inodeLayout, blockSize, cancellationToken);
            }

            // Write Metadata WAL header
            if (layout.Regions.TryGetValue("MetadataWAL", out var mwalRegion))
            {
                var mwalHeader = WalHeader.CreateMetadataWal(mwalRegion.BlockCount);
                await WriteWalHeaderBlockAsync(stream, mwalRegion.StartBlock,
                    BlockTypeTags.MWAL, mwalHeader, blockSize, cancellationToken);
            }

            // Write Data WAL header (if Streaming module is active)
            if (layout.Regions.TryGetValue("DataWAL", out var dwalRegion))
            {
                var dwalHeader = WalHeader.CreateDataWal(dwalRegion.BlockCount);
                await WriteWalHeaderBlockAsync(stream, dwalRegion.StartBlock,
                    BlockTypeTags.DWAL, dwalHeader, blockSize, cancellationToken);
            }

            // Write module-specific region headers
            foreach (var (name, (startBlock, blockCount)) in layout.Regions)
            {
                if (IsModuleRegion(name))
                {
                    await WriteModuleRegionHeaderAsync(stream, startBlock,
                        ResolveBlockTypeTag(name), blockSize, cancellationToken);
                }
            }

            await stream.FlushAsync(cancellationToken);
        }

        return filePath;
    }

    /// <summary>
    /// Calculates the region layout for a VDE creation profile without creating a file.
    /// Useful for previewing disk layout, overhead, and region placement.
    /// </summary>
    /// <param name="profile">Creation profile to calculate layout for.</param>
    /// <returns>Complete layout information including all region positions.</returns>
    public static VdeLayoutInfo CalculateLayout(VdeCreationProfile profile)
    {
        ValidateProfile(profile);

        var blockSize = profile.BlockSize;
        var totalBlocks = profile.TotalBlocks;
        var regions = new Dictionary<string, (long StartBlock, long BlockCount)>();
        long nextBlock = 0;

        // Blocks 0-3: Primary Superblock Group
        regions["PrimarySuperblock"] = (0, FormatConstants.SuperblockGroupBlocks);
        nextBlock = FormatConstants.SuperblockGroupBlocks;

        // Blocks 4-7: Mirror Superblock Group
        regions["MirrorSuperblock"] = (FormatConstants.SuperblockMirrorStartBlock, FormatConstants.SuperblockGroupBlocks);
        nextBlock = FormatConstants.SuperblockMirrorStartBlock + FormatConstants.SuperblockGroupBlocks;

        // Blocks 8-9: Region Directory
        regions["RegionDirectory"] = (FormatConstants.RegionDirectoryStartBlock, FormatConstants.RegionDirectoryBlocks);
        nextBlock = FormatConstants.RegionDirectoryStartBlock + FormatConstants.RegionDirectoryBlocks;

        // Blocks 10-11: Policy Vault (if SEC module active)
        bool securityActive = ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.Security);
        if (securityActive)
        {
            regions["PolicyVault"] = (nextBlock, 2);
            nextBlock += 2;

            // Blocks 12-13: Encryption Header
            regions["EncryptionHeader"] = (nextBlock, 2);
            nextBlock += 2;
        }

        // Allocation Bitmap: 1 bit per block, ceil(totalBlocks / (payloadBits))
        int payloadBytes = blockSize - FormatConstants.UniversalBlockTrailerSize;
        long bitsPerBlock = payloadBytes * 8L;
        long bitmapBlocks = Math.Max(1, (totalBlocks + bitsPerBlock - 1) / bitsPerBlock);
        regions["AllocationBitmap"] = (nextBlock, bitmapBlocks);
        nextBlock += bitmapBlocks;

        // Inode Table
        // Scale inode table with volume size: use fraction of total blocks, with InodeTableDefaultBlocks as minimum
        long inodeBlocks = Math.Max(InodeTableDefaultBlocks, totalBlocks / InodeTableFractionDivisor);
        inodeBlocks = Math.Max(1, inodeBlocks);
        regions["InodeTable"] = (nextBlock, inodeBlocks);
        nextBlock += inodeBlocks;

        // Metadata WAL
        long mwalBlocks = WalHeader.CalculateMetadataWalBlocks(totalBlocks);
        regions["MetadataWAL"] = (nextBlock, mwalBlocks);
        nextBlock += mwalBlocks;

        // Data WAL (only if Streaming module is active)
        bool streamingActive = ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.Streaming);
        if (streamingActive)
        {
            long dwalBlocks = WalHeader.CalculateDataWalBlocks(totalBlocks);
            regions["DataWAL"] = (nextBlock, dwalBlocks);
            nextBlock += dwalBlocks;
        }

        // Module-specific regions (from ModuleRegistry)
        var requiredRegions = ModuleRegistry.GetRequiredRegions(profile.ModuleManifest);
        foreach (var regionName in requiredRegions)
        {
            // Skip regions we already placed explicitly
            if (regionName is "PolicyVault" or "EncryptionHeader" or "DataWAL" or "StreamingAppend")
                continue;

            // Default module region size: 64 blocks (configurable in future)
            long moduleRegionBlocks = 64;
            regions[regionName] = (nextBlock, moduleRegionBlocks);
            nextBlock += moduleRegionBlocks;
        }

        // Streaming append region (separate from Data WAL)
        if (streamingActive)
        {
            long streamBlocks = Math.Max(128, totalBlocks / 100);
            regions["StreamingAppend"] = (nextBlock, streamBlocks);
            nextBlock += streamBlocks;
        }

        // Data Region: everything remaining
        long dataBlocks = Math.Max(0, totalBlocks - nextBlock);
        if (dataBlocks > 0)
        {
            regions["DataRegion"] = (nextBlock, dataBlocks);
        }

        long metadataBlocks = nextBlock;
        var inodeResult = InodeSizeCalculator.Calculate(profile.ModuleManifest);

        return new VdeLayoutInfo
        {
            BlockSize = blockSize,
            TotalBlocks = totalBlocks,
            MetadataBlocks = metadataBlocks,
            DataBlocks = dataBlocks,
            OverheadPercent = totalBlocks > 0 ? (double)metadataBlocks / totalBlocks * 100.0 : 0,
            InodeSize = inodeResult.InodeSize,
            Regions = regions,
        };
    }

    /// <summary>
    /// Opens and validates a DWVD v2.0 file by checking its magic signature and
    /// metadata block trailers.
    /// </summary>
    /// <param name="filePath">Path to the VDE file to validate.</param>
    /// <returns>True if the file is a valid DWVD v2.0 file with intact metadata.</returns>
    public static bool ValidateVde(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Must be at least large enough for primary + mirror superblock groups + region directory
            // Minimum: 10 blocks. Detect block size from superblock.
            if (stream.Length < FormatConstants.MinBlockSize * 10)
                return false;

            // Read first block to detect magic and block size
            var firstBlock = new byte[FormatConstants.MaxBlockSize];
            int bytesRead = stream.Read(firstBlock, 0, Math.Min((int)stream.Length, FormatConstants.MaxBlockSize));
            if (bytesRead < FormatConstants.MinBlockSize)
                return false;

            // Validate magic signature at offset 0
            var magic = MagicSignature.Deserialize(firstBlock);
            if (!magic.Validate())
                return false;

            // Read block size from superblock at offset 0x34
            int blockSize = BinaryPrimitives.ReadInt32LittleEndian(firstBlock.AsSpan(0x34));
            if (!IsValidBlockSize(blockSize))
                return false;

            // Validate primary superblock group trailers (blocks 0-3)
            long groupSize = FormatConstants.SuperblockGroupBlocks * blockSize;
            if (stream.Length < groupSize * 2 + FormatConstants.RegionDirectoryBlocks * blockSize)
                return false;

            stream.Position = 0;
            var groupBuffer = new byte[groupSize];
            stream.ReadExactly(groupBuffer);

            if (!SuperblockGroup.ValidateTrailers(groupBuffer, blockSize))
                return false;

            // Validate region directory trailers (blocks 8-9)
            long rdOffset = FormatConstants.RegionDirectoryStartBlock * blockSize;
            stream.Position = rdOffset;
            var rdBuffer = new byte[FormatConstants.RegionDirectoryBlocks * blockSize];
            stream.ReadExactly(rdBuffer);

            // Check block 0 of region directory
            if (!UniversalBlockTrailer.Verify(rdBuffer.AsSpan(0, blockSize), blockSize))
                return false;

            // Check block 1 of region directory
            if (!UniversalBlockTrailer.Verify(rdBuffer.AsSpan(blockSize, blockSize), blockSize))
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Validation ──────────────────────────────────────────────────────

    private static void ValidateProfile(VdeCreationProfile profile)
    {
        if (!IsValidBlockSize(profile.BlockSize))
            throw new ArgumentException(
                $"Block size must be a power of 2 between {FormatConstants.MinBlockSize} and {FormatConstants.MaxBlockSize}. Got: {profile.BlockSize}",
                nameof(profile));

        if (profile.TotalBlocks < MinimumBlocks)
            throw new ArgumentException(
                $"Total blocks must be at least {MinimumBlocks}. Got: {profile.TotalBlocks}",
                nameof(profile));
    }

    private static bool IsValidBlockSize(int blockSize)
        => blockSize >= FormatConstants.MinBlockSize
        && blockSize <= FormatConstants.MaxBlockSize
        && BitOperations.IsPow2(blockSize);

    // ── Block Writing Helpers ───────────────────────────────────────────

    private static async Task WriteAllocationBitmapAsync(
        FileStream stream, long startBlock, long blockCount,
        long metadataBlocks, int blockSize, CancellationToken ct)
    {
        // First bitmap block: mark metadata blocks as allocated
        var block = new byte[blockSize];

        // Set bits for allocated blocks (metadata blocks)
        for (long i = 0; i < metadataBlocks && i < blockCount * (blockSize - FormatConstants.UniversalBlockTrailerSize) * 8; i++)
        {
            int byteIndex = (int)(i / 8);
            int bitIndex = (int)(i % 8);
            if (byteIndex < blockSize - FormatConstants.UniversalBlockTrailerSize)
            {
                block[byteIndex] |= (byte)(1 << bitIndex);
            }
        }

        // Write trailer
        UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.BMAP, 1);

        stream.Position = startBlock * blockSize;
        await stream.WriteAsync(block, ct);

        // Write remaining bitmap blocks (all zeros = free, plus trailer)
        if (blockCount > 1)
        {
            var emptyBlock = new byte[blockSize];
            UniversalBlockTrailer.Write(emptyBlock, blockSize, BlockTypeTags.BMAP, 1);

            for (long b = 1; b < blockCount; b++)
            {
                ct.ThrowIfCancellationRequested();
                stream.Position = (startBlock + b) * blockSize;
                await stream.WriteAsync(emptyBlock, ct);
            }
        }
    }

    private static async Task WriteInodeTableHeaderAsync(
        FileStream stream, long startBlock,
        InodeLayoutDescriptor inodeLayout, int blockSize, CancellationToken ct)
    {
        var block = new byte[blockSize];

        // Write inode layout descriptor at the start of the first inode block
        InodeLayoutDescriptor.Serialize(inodeLayout, block);

        // Write trailer
        UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.INOD, 1);

        stream.Position = startBlock * blockSize;
        await stream.WriteAsync(block, ct);
    }

    private static async Task WriteWalHeaderBlockAsync(
        FileStream stream, long startBlock, uint blockTypeTag,
        WalHeader walHeader, int blockSize, CancellationToken ct)
    {
        var block = new byte[blockSize];

        // Serialize WAL header into the block payload
        WalHeader.Serialize(walHeader, block);

        // Write trailer
        UniversalBlockTrailer.Write(block, blockSize, blockTypeTag, 1);

        stream.Position = startBlock * blockSize;
        await stream.WriteAsync(block, ct);
    }

    private static async Task WriteModuleRegionHeaderAsync(
        FileStream stream, long startBlock, uint blockTypeTag,
        int blockSize, CancellationToken ct)
    {
        // Write a header block for the module region (zeroed payload + trailer)
        var block = new byte[blockSize];
        UniversalBlockTrailer.Write(block, blockSize, blockTypeTag, 1);

        stream.Position = startBlock * blockSize;
        await stream.WriteAsync(block, ct);
    }

    // ── Tag Resolution ──────────────────────────────────────────────────

    private static uint ResolveBlockTypeTag(string regionName) => regionName switch
    {
        "PrimarySuperblock" or "MirrorSuperblock" => BlockTypeTags.SUPB,
        "RegionDirectory" => BlockTypeTags.RMAP,
        "PolicyVault" => BlockTypeTags.POLV,
        "EncryptionHeader" => BlockTypeTags.ENCR,
        "AllocationBitmap" => BlockTypeTags.BMAP,
        "InodeTable" => BlockTypeTags.INOD,
        "MetadataWAL" => BlockTypeTags.MWAL,
        "DataWAL" => BlockTypeTags.DWAL,
        "DataRegion" => BlockTypeTags.DATA,
        "TagIndexRegion" => BlockTypeTags.TAGI,
        "IntegrityTree" => BlockTypeTags.MTRK,
        "SnapshotTable" => BlockTypeTags.SNAP,
        "BTreeIndexForest" => BlockTypeTags.BTRE,
        "ReplicationState" => BlockTypeTags.REPL,
        "RAIDMetadata" => BlockTypeTags.RAID,
        "DictionaryRegion" => BlockTypeTags.DICT,
        "IntelligenceCache" => BlockTypeTags.INTE,
        "StreamingAppend" => BlockTypeTags.STRE,
        "CrossVDEReferenceTable" => BlockTypeTags.XREF,
        "ComputeCodeCache" => BlockTypeTags.CODE,
        "ConsensusLogRegion" => BlockTypeTags.CLOG,
        "ComplianceVault" => BlockTypeTags.CMVT,
        "AuditLog" or "AuditLogRegion" => BlockTypeTags.ALOG,
        "AnonymizationTable" => BlockTypeTags.ANON,
        "MetricsLogRegion" => BlockTypeTags.MLOG,
        _ => BlockTypeTags.DATA,
    };

    private static bool IsModuleRegion(string regionName) => regionName switch
    {
        "PrimarySuperblock" or "MirrorSuperblock" or "RegionDirectory"
            or "PolicyVault" or "EncryptionHeader" or "AllocationBitmap"
            or "InodeTable" or "MetadataWAL" or "DataWAL" or "DataRegion"
            or "StreamingAppend" => false,
        _ => true,
    };
}
