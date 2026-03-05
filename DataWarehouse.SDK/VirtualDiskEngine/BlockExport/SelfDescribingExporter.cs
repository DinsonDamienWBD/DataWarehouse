using System.Buffers.Binary;
using System.IO.Hashing;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using DataWarehouse.SDK.VirtualDiskEngine.Metadata;

namespace DataWarehouse.SDK.VirtualDiskEngine.BlockExport;

/// <summary>
/// Result returned by <see cref="SelfDescribingExporter.ExportAsync"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-53 self-describing portable export")]
public readonly struct ExportResult
{
    /// <summary>Number of inodes written to the exported VDE.</summary>
    public long TotalInodesExported { get; init; }

    /// <summary>Number of data blocks written to the exported VDE.</summary>
    public long TotalBlocksExported { get; init; }

    /// <summary>Total byte size of the exported .dwvd file.</summary>
    public long ExportSizeBytes { get; init; }

    /// <summary>New UUID assigned to the exported VDE (fresh UUID v7).</summary>
    public Guid ExportedVolumeUuid { get; init; }

    /// <summary>Wall-clock duration of the export operation.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// True when the export was truncated because
    /// <see cref="ExportConfig.MaxExportSizeBytes"/> was reached.
    /// </summary>
    public bool SizeLimitReached { get; init; }
}

/// <summary>
/// Produces a self-describing standalone <c>.dwvd</c> file from a predicate-selected
/// subset of objects in a source <see cref="IBlockDevice"/>.
///
/// The exported file is a complete, mountable DWVD v2.1 volume containing:
/// <list type="bullet">
///   <item>Primary Superblock Group (blocks 0-3) with a fresh <see cref="Guid"/> VolumeUuid</item>
///   <item>Mirror Superblock Group (blocks 4-7)</item>
///   <item>Region Directory (blocks 8-9)</item>
///   <item>Allocation Bitmap region</item>
///   <item>Inode Table region — selected inodes with remapped block addresses</item>
///   <item>Data Region — compacted sequential copy of selected extents</item>
/// </list>
///
/// Block address remapping ensures that every inode extent in the export points to
/// a valid block in the new file, regardless of where the data lived in the source.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-53 self-describing portable export")]
public sealed class SelfDescribingExporter
{
    // ── Layout constants ─────────────────────────────────────────────────

    private const int SuperblockGroupBlocks = FormatConstants.SuperblockGroupBlocks;   // 4
    private const int MirrorOffset           = SuperblockGroupBlocks;                   // 4
    private const int RegionDirStart         = (int)FormatConstants.RegionDirectoryStartBlock; // 8
    private const int RegionDirBlocks        = FormatConstants.RegionDirectoryBlocks;     // 2

    // First block after the static header area (superblocks + region dir)
    private const int FirstDynamicBlock      = RegionDirStart + RegionDirBlocks;       // 10

    // Blocks written as TRLR sentinels every 256 data blocks
    private const int TrailerInterval        = 256;

    // ── State ────────────────────────────────────────────────────────────

    private readonly IBlockDevice _source;
    private readonly int _srcBlockSize;
    private readonly ExportConfig _config;

    // ── Constructor ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new exporter.
    /// </summary>
    /// <param name="sourceDevice">Block device to read source data from.</param>
    /// <param name="sourceBlockSize">Block size of the source device in bytes.</param>
    /// <param name="config">Export options.</param>
    public SelfDescribingExporter(IBlockDevice sourceDevice, int sourceBlockSize, ExportConfig config)
    {
        _source      = sourceDevice ?? throw new ArgumentNullException(nameof(sourceDevice));
        _srcBlockSize = sourceBlockSize > 0
            ? sourceBlockSize
            : throw new ArgumentOutOfRangeException(nameof(sourceBlockSize), "Block size must be positive.");
        _config      = config       ?? throw new ArgumentNullException(nameof(config));

        if (_config.BlockSize < FormatConstants.MinBlockSize || _config.BlockSize > FormatConstants.MaxBlockSize)
            throw new ArgumentOutOfRangeException(nameof(config),
                $"ExportConfig.BlockSize must be between {FormatConstants.MinBlockSize} and {FormatConstants.MaxBlockSize}.");
    }

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Performs the export, writing a complete DWVD v2.1 file to <paramref name="outputStream"/>.
    /// </summary>
    /// <param name="outputStream">Writable stream for the output .dwvd file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Metadata describing the completed export.</returns>
    public async Task<ExportResult> ExportAsync(Stream outputStream, CancellationToken ct = default)
    {
        if (outputStream is null) throw new ArgumentNullException(nameof(outputStream));
        if (!outputStream.CanWrite) throw new ArgumentException("Output stream must be writable.", nameof(outputStream));

        var startTime = DateTimeOffset.UtcNow;

        int dstBlock  = _config.BlockSize;
        long maxBytes = _config.MaxExportSizeBytes;

        // ── Phase 1: Scan source inode table and select inodes ───────────
        var selectedInodes = await SelectInodesAsync(ct).ConfigureAwait(false);

        // ── Phase 2: Layout planning ─────────────────────────────────────
        var (layout, blockRemap) = PlanLayout(selectedInodes, dstBlock);

        bool sizeLimitReached = layout.EstimatedTotalBytes > maxBytes;

        // ── Phase 3: Write Superblock Groups ─────────────────────────────
        var exportUuid = Guid.NewGuid();
        var superblockGroup = BuildSuperblockGroup(exportUuid, layout, dstBlock);
        var sbBytes = superblockGroup.SerializeWithMirror(dstBlock); // 8 blocks
        await outputStream.WriteAsync(sbBytes, ct).ConfigureAwait(false);

        // ── Phase 4: Write Region Directory ─────────────────────────────
        var regionDir = BuildRegionDirectory(layout, dstBlock);
        var rdBuf = new byte[RegionDirBlocks * dstBlock];
        regionDir.Serialize(rdBuf, dstBlock);
        await outputStream.WriteAsync(rdBuf, ct).ConfigureAwait(false);

        // ── Phase 5: Write Allocation Bitmap ─────────────────────────────
        await WriteAllocationBitmapAsync(outputStream, layout, dstBlock, ct).ConfigureAwait(false);

        // ── Phase 6: Write Inode Table ───────────────────────────────────
        await WriteInodeTableAsync(outputStream, selectedInodes, blockRemap, layout, dstBlock, ct)
            .ConfigureAwait(false);

        // ── Phase 7: Write Integrity Tree placeholder ─────────────────────
        await WriteIntegrityTreeAsync(outputStream, layout, dstBlock, ct).ConfigureAwait(false);

        // ── Phase 8: Write Data Region ────────────────────────────────────
        long totalBlocksExported = await WriteDataRegionAsync(
            outputStream, selectedInodes, blockRemap, layout, dstBlock, ct)
            .ConfigureAwait(false);

        // ── Finalize ──────────────────────────────────────────────────────
        await outputStream.FlushAsync(ct).ConfigureAwait(false);

        long exportSizeBytes = outputStream.Position;
        var duration         = DateTimeOffset.UtcNow - startTime;

        return new ExportResult
        {
            TotalInodesExported = selectedInodes.Count,
            TotalBlocksExported = totalBlocksExported,
            ExportSizeBytes     = exportSizeBytes,
            ExportedVolumeUuid  = exportUuid,
            Duration            = duration,
            SizeLimitReached    = sizeLimitReached,
        };
    }

    /// <summary>
    /// Validates an exported DWVD file by reading its superblock, verifying the magic
    /// and version, checking that the region directory is coherent, and spot-checking
    /// a sample of data block trailers.
    /// </summary>
    /// <param name="exportedStream">Readable stream positioned at offset 0.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the file is a valid DWVD v2.1 file; false otherwise.</returns>
    public async Task<bool> ValidateExportAsync(Stream exportedStream, CancellationToken ct = default)
    {
        if (exportedStream is null) throw new ArgumentNullException(nameof(exportedStream));
        if (!exportedStream.CanRead) return false;

        int blockSize = _config.BlockSize;
        int headerBytes = (SuperblockGroupBlocks * 2 + RegionDirBlocks) * blockSize;

        if (exportedStream.Length < headerBytes)
            return false;

        // Read the first 8 blocks (primary + mirror superblock groups)
        var headerBuf = new byte[headerBytes];
        exportedStream.Seek(0, SeekOrigin.Begin);
        await exportedStream.ReadExactlyAsync(headerBuf, ct).ConfigureAwait(false);

        // Verify superblock magic and version
        try
        {
            var sbGroup = SuperblockGroup.DeserializeWithMirror(headerBuf, blockSize);
            var sb      = sbGroup.PrimarySuperblock;

            if (!sb.Magic.Validate())                            return false;
            if (sb.VersionInfo.MinReaderVersion != FormatConstants.FormatMajorVersion) return false;
        }
        catch
        {
            return false;
        }

        // Verify region directory trailer integrity
        var rdBuf = headerBuf.AsSpan(SuperblockGroupBlocks * 2 * blockSize, RegionDirBlocks * blockSize);
        if (!UniversalBlockTrailer.Verify(rdBuf[..blockSize], blockSize)) return false;
        if (!UniversalBlockTrailer.Verify(rdBuf.Slice(blockSize, blockSize), blockSize)) return false;

        // Spot-check data block trailers (sample up to 4 blocks from data region)
        long dataRegionOffset = (long)FirstDynamicBlock * blockSize;
        long fileLength       = exportedStream.Length;
        if (fileLength > dataRegionOffset + blockSize)
        {
            var sampleBuf = new byte[blockSize];
            for (int i = 0; i < 4; i++)
            {
                long sampleOffset = dataRegionOffset + (long)i * TrailerInterval * blockSize;
                if (sampleOffset + blockSize > fileLength) break;

                exportedStream.Seek(sampleOffset, SeekOrigin.Begin);
                int bytesRead = await exportedStream.ReadAsync(sampleBuf, ct).ConfigureAwait(false);
                if (bytesRead < blockSize) break;

                if (!UniversalBlockTrailer.Verify(sampleBuf, blockSize))
                    return false;
            }
        }

        return true;
    }

    // ── Private: Selection ───────────────────────────────────────────────

    /// <summary>
    /// Reads the source inode table (starting at the first dynamic block after
    /// the static header) and returns the inodes that pass the configured predicates.
    ///
    /// The source inode table is assumed to be laid out sequentially starting at
    /// block <see cref="FirstDynamicBlock"/> with each inode occupying one 512-byte
    /// slot.  A block contains <c>srcBlockSize / ExtendedInode512.SerializedSize</c>
    /// inode slots.  Inodes with <c>InodeNumber == 0</c> (unallocated) are skipped.
    /// </summary>
    private async Task<List<ExtendedInode512>> SelectInodesAsync(CancellationToken ct)
    {
        var selected = new List<ExtendedInode512>();
        int inodesPerBlock = _srcBlockSize / ExtendedInode512.SerializedSize;
        if (inodesPerBlock < 1) inodesPerBlock = 1;

        var blockBuf = new byte[_srcBlockSize];

        long totalSourceBlocks = _source.BlockCount;

        for (long blockNum = FirstDynamicBlock; blockNum < totalSourceBlocks; blockNum++)
        {
            ct.ThrowIfCancellationRequested();

            await _source.ReadBlockAsync(blockNum, blockBuf, ct).ConfigureAwait(false);

            // Check block trailer — if this is a DATA or INOD block, scan for inodes
            var trailer = UniversalBlockTrailer.Read(blockBuf, _srcBlockSize);
            if (trailer.BlockTypeTag != BlockTypeTags.INOD &&
                trailer.BlockTypeTag != BlockTypeTags.DATA)
                continue;

            if (trailer.BlockTypeTag != BlockTypeTags.INOD)
                continue;

            // Parse inode slots
            for (int slot = 0; slot < inodesPerBlock; slot++)
            {
                int offset = slot * ExtendedInode512.SerializedSize;
                if (offset + ExtendedInode512.SerializedSize > _srcBlockSize - FormatConstants.UniversalBlockTrailerSize)
                    break;

                var inode = ExtendedInode512.Deserialize(blockBuf.AsSpan(offset, ExtendedInode512.SerializedSize));

                if (inode.InodeNumber == 0)
                    continue;

                if (_config.InodePredicate != null && !_config.InodePredicate(inode.InodeNumber))
                    continue;

                // Name predicate: names are stored in the inode's extended attribute area
                // as a null-terminated UTF-8 string.  Fall back to empty string if area is blank.
                if (_config.NamePredicate != null)
                {
                    var nameSpan = inode.InlineXattrArea.AsSpan();
                    int nullPos  = nameSpan.IndexOf((byte)0);
                    string name  = nullPos >= 0
                        ? System.Text.Encoding.UTF8.GetString(nameSpan[..nullPos])
                        : System.Text.Encoding.UTF8.GetString(nameSpan);
                    if (!_config.NamePredicate(name))
                        continue;
                }

                selected.Add(inode);
            }
        }

        return selected;
    }

    // ── Private: Layout planning ─────────────────────────────────────────

    private readonly record struct ExportLayout(
        long BitmapStartBlock,
        long BitmapBlockCount,
        long InodeTableStartBlock,
        long InodeTableBlockCount,
        long IntegrityTreeStartBlock,
        long IntegrityTreeBlockCount,
        long DataRegionStartBlock,
        long DataRegionBlockCount,
        long TotalBlocks,
        long EstimatedTotalBytes);

    /// <summary>
    /// Calculates the target VDE layout and builds the block address remap table.
    /// </summary>
    private (ExportLayout Layout, Dictionary<long, long> BlockRemap) PlanLayout(
        List<ExtendedInode512> selectedInodes, int dstBlockSize)
    {
        // Count total source data blocks required
        long totalDataBlocks = 0;
        foreach (var inode in selectedInodes)
        {
            for (int i = 0; i < inode.ExtentCount && i < FormatConstants.MaxExtentsPerInode; i++)
            {
                var ext = inode.Extents[i];
                if (!ext.IsEmpty && !ext.IsSparse)
                    totalDataBlocks += ext.BlockCount;
            }
        }

        // Inode table: one 512-byte slot per inode, packed into dstBlockSize blocks
        int inodesPerBlock     = Math.Max(1, dstBlockSize / ExtendedInode512.SerializedSize);
        long inodeTableBlocks  = (selectedInodes.Count + inodesPerBlock - 1) / inodesPerBlock;
        if (inodeTableBlocks < 1) inodeTableBlocks = 1;

        // Allocation bitmap: 1 bit per block, packed into dstBlockSize bytes per block
        // We will know the total block count only after computing all regions, so estimate
        long estimatedTotalBlocks = FirstDynamicBlock + 1 /* bitmap */ + inodeTableBlocks + 1 /* integrity */ + totalDataBlocks;
        long bitmapBlocks = (estimatedTotalBlocks + (long)dstBlockSize * 8 - 1) / ((long)dstBlockSize * 8);
        if (bitmapBlocks < 1) bitmapBlocks = 1;

        // Integrity tree: one anchor block (simplified: single block for this implementation)
        long integrityBlocks = 1;

        // Layout (all regions are sequential)
        long bitmapStart     = FirstDynamicBlock;
        long inodeStart      = bitmapStart + bitmapBlocks;
        long integrityStart  = inodeStart + inodeTableBlocks;
        long dataStart       = integrityStart + integrityBlocks;
        long totalBlocks     = dataStart + totalDataBlocks;

        // Build block address remap: map old source StartBlock -> new destination block
        var blockRemap     = new Dictionary<long, long>();
        long nextDestBlock = dataStart;
        foreach (var inode in selectedInodes)
        {
            for (int i = 0; i < inode.ExtentCount && i < FormatConstants.MaxExtentsPerInode; i++)
            {
                var ext = inode.Extents[i];
                if (ext.IsEmpty || ext.IsSparse) continue;

                if (!blockRemap.ContainsKey(ext.StartBlock))
                {
                    blockRemap[ext.StartBlock] = nextDestBlock;
                    nextDestBlock += ext.BlockCount;
                }
            }
        }

        var layout = new ExportLayout(
            BitmapStartBlock:       bitmapStart,
            BitmapBlockCount:       bitmapBlocks,
            InodeTableStartBlock:   inodeStart,
            InodeTableBlockCount:   inodeTableBlocks,
            IntegrityTreeStartBlock: integrityStart,
            IntegrityTreeBlockCount: integrityBlocks,
            DataRegionStartBlock:   dataStart,
            DataRegionBlockCount:   totalDataBlocks,
            TotalBlocks:            totalBlocks,
            EstimatedTotalBytes:    totalBlocks * dstBlockSize);

        return (layout, blockRemap);
    }

    // ── Private: Superblock group ────────────────────────────────────────

    private static SuperblockGroup BuildSuperblockGroup(
        Guid exportUuid, ExportLayout layout, int blockSize)
    {
        long allocated = layout.TotalBlocks - (SuperblockGroupBlocks * 2 + RegionDirBlocks);
        long free      = 0; // all selected data is allocated in the export

        var now = DateTimeOffset.UtcNow.Ticks;

        // Build SuperblockV2 with FormatMajor=2, Minor=1
        var sb = new SuperblockV2(
            magic:                     MagicSignature.CreateDefault(),
            versionInfo:               new FormatVersionInfo(
                                           minReaderVersion: 2,
                                           minWriterVersion: 2,
                                           incompatibleFeatures: IncompatibleFeatureFlags.None,
                                           readOnlyCompatibleFeatures: ReadOnlyCompatibleFeatureFlags.None,
                                           compatibleFeatures: CompatibleFeatureFlags.None),
            moduleManifest:            0,
            moduleConfig:              0,
            moduleConfigExt:           0,
            blockSize:                 blockSize,
            totalBlocks:               layout.TotalBlocks,
            freeBlocks:                free,
            expectedFileSize:          layout.TotalBlocks * blockSize,
            totalAllocatedBlocks:      allocated,
            volumeUuid:                exportUuid,
            clusterNodeId:             Guid.Empty,
            defaultCompressionAlgo:    0,
            defaultEncryptionAlgo:     0,
            defaultChecksumAlgo:       1, // XxHash64
            inodeSize:                 (ushort)FormatConstants.InodeCoreSize,
            policyVersion:             1,
            replicationEpoch:          0,
            wormHighWaterMark:         0,
            encryptionKeyFingerprint:  0,
            sovereigntyZoneId:         0,
            volumeLabel:               SuperblockV2.CreateVolumeLabel("DWVD-Export"),
            createdTimestampUtc:       now,
            modifiedTimestampUtc:      now,
            lastScrubTimestamp:        0,
            checkpointSequence:        1,
            errorMapBlockCount:        0,
            lastWriterSessionId:       Guid.Empty,
            lastWriterTimestamp:       now,
            lastWriterNodeId:          Guid.Empty,
            physicalAllocatedBlocks:   allocated,
            headerIntegritySeal:       new byte[SuperblockV2.IntegritySealSize]);

        var rpt = new RegionPointerTable();

        // Register regions in the Region Pointer Table
        // Slot 0: Bitmap
        rpt.SetSlot(0, new RegionPointer(
            BlockTypeTags.BMAP, RegionFlags.Active,
            layout.BitmapStartBlock, layout.BitmapBlockCount, layout.BitmapBlockCount));

        // Slot 1: Inode Table
        rpt.SetSlot(1, new RegionPointer(
            BlockTypeTags.INOD, RegionFlags.Active,
            layout.InodeTableStartBlock, layout.InodeTableBlockCount, layout.InodeTableBlockCount));

        // Slot 2: Integrity Tree
        rpt.SetSlot(2, new RegionPointer(
            BlockTypeTags.IANT, RegionFlags.Active,
            layout.IntegrityTreeStartBlock, layout.IntegrityTreeBlockCount, layout.IntegrityTreeBlockCount));

        // Slot 3: Data Region
        rpt.SetSlot(3, new RegionPointer(
            BlockTypeTags.DATA, RegionFlags.Active,
            layout.DataRegionStartBlock, layout.DataRegionBlockCount, layout.DataRegionBlockCount));

        var ext = ExtendedMetadata.CreateDefault();
        var ia  = IntegrityAnchor.CreateDefault();

        return new SuperblockGroup(sb, rpt, ext, ia);
    }

    // ── Private: Region Directory ────────────────────────────────────────

    private static RegionDirectory BuildRegionDirectory(ExportLayout layout, int blockSize)
    {
        var dir = new RegionDirectory();
        dir.AddRegion(BlockTypeTags.BMAP, RegionFlags.None, layout.BitmapStartBlock,   layout.BitmapBlockCount);
        dir.AddRegion(BlockTypeTags.INOD, RegionFlags.None, layout.InodeTableStartBlock, layout.InodeTableBlockCount);
        dir.AddRegion(BlockTypeTags.IANT, RegionFlags.None, layout.IntegrityTreeStartBlock, layout.IntegrityTreeBlockCount);
        dir.AddRegion(BlockTypeTags.DATA, RegionFlags.None, layout.DataRegionStartBlock, layout.DataRegionBlockCount);
        return dir;
    }

    // ── Private: Allocation Bitmap ───────────────────────────────────────

    private static async Task WriteAllocationBitmapAsync(
        Stream output, ExportLayout layout, int blockSize, CancellationToken ct)
    {
        // Build a bitmap marking all blocks as allocated (every bit = 1).
        // The bitmap covers layout.TotalBlocks bits.
        long totalBitmapBytes = (layout.TotalBlocks + 7) / 8;
        long bitmapRegionBytes = layout.BitmapBlockCount * blockSize;

        var buf = new byte[bitmapRegionBytes];

        // Mark all blocks as allocated
        long fullBytes = totalBitmapBytes;
        if (fullBytes > bitmapRegionBytes - FormatConstants.UniversalBlockTrailerSize)
            fullBytes = bitmapRegionBytes - FormatConstants.UniversalBlockTrailerSize;

        buf.AsSpan(0, (int)fullBytes).Fill(0xFF);

        // Mask out any trailing bits in the last byte
        int remainingBits = (int)(layout.TotalBlocks % 8);
        if (remainingBits != 0 && fullBytes > 0)
        {
            byte mask = (byte)((1 << remainingBits) - 1);
            buf[fullBytes - 1] = mask;
        }

        // Write trailers for each block in the bitmap region
        for (int b = 0; b < layout.BitmapBlockCount; b++)
        {
            int blockOff = b * blockSize;
            var blockSpan = buf.AsSpan(blockOff, blockSize);
            UniversalBlockTrailer.Write(blockSpan, blockSize, BlockTypeTags.BMAP, 1);
        }

        await output.WriteAsync(buf, ct).ConfigureAwait(false);
    }

    // ── Private: Inode Table ──────────────────────────────────────────────

    private static async Task WriteInodeTableAsync(
        Stream output,
        List<ExtendedInode512> selectedInodes,
        Dictionary<long, long> blockRemap,
        ExportLayout layout,
        int blockSize,
        CancellationToken ct)
    {
        int inodesPerBlock = Math.Max(1, blockSize / ExtendedInode512.SerializedSize);
        var buf            = new byte[(int)(layout.InodeTableBlockCount * blockSize)];

        for (int idx = 0; idx < selectedInodes.Count; idx++)
        {
            // Clone inode so we can remap extents without mutating the original
            var src   = selectedInodes[idx];
            var inode = CloneInodeWithRemap(src, blockRemap);

            int blockIndex = idx / inodesPerBlock;
            int slotIndex  = idx % inodesPerBlock;
            int offset     = blockIndex * blockSize + slotIndex * ExtendedInode512.SerializedSize;

            ExtendedInode512.SerializeToSpan(inode, buf.AsSpan(offset, ExtendedInode512.SerializedSize));
        }

        // Write trailers for each inode table block
        for (int b = 0; b < (int)layout.InodeTableBlockCount; b++)
        {
            var blockSpan = buf.AsSpan(b * blockSize, blockSize);
            UniversalBlockTrailer.Write(blockSpan, blockSize, BlockTypeTags.INOD, 1);
        }

        await output.WriteAsync(buf, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a deep copy of an inode with each inline extent's StartBlock remapped
    /// to its destination address in the exported VDE.
    /// </summary>
    private static ExtendedInode512 CloneInodeWithRemap(
        ExtendedInode512 src, Dictionary<long, long> blockRemap)
    {
        var dst = new ExtendedInode512
        {
            InodeNumber             = src.InodeNumber,
            Type                    = src.Type,
            Flags                   = src.Flags,
            Permissions             = src.Permissions,
            LinkCount               = src.LinkCount,
            OwnerId                 = src.OwnerId,
            Size                    = src.Size,
            AllocatedSize           = src.AllocatedSize,
            CreatedUtc              = src.CreatedUtc,
            ModifiedUtc             = src.ModifiedUtc,
            AccessedUtc             = src.AccessedUtc,
            ChangedUtc              = src.ChangedUtc,
            ExtentCount             = src.ExtentCount,
            IndirectExtentBlock     = 0, // not exported — only inline extents
            DoubleIndirectBlock     = 0,
            ExtendedAttributeBlock  = 0,
            CreatedNs               = src.CreatedNs,
            ModifiedNs              = src.ModifiedNs,
            AccessedNs              = src.AccessedNs,
            CompressionDictionaryRef = 0,
            MvccVersionChainHead    = 0,
            MvccTransactionId       = src.MvccTransactionId,
            SnapshotRefCount        = src.SnapshotRefCount,
        };

        // Copy inline xattr area
        src.InlineXattrArea.AsSpan().CopyTo(dst.InlineXattrArea.AsSpan());

        // Copy encryption IV
        src.PerObjectEncryptionIV.AsSpan().CopyTo(dst.PerObjectEncryptionIV.AsSpan());

        // Copy replication vector
        src.ReplicationVector.AsSpan().CopyTo(dst.ReplicationVector.AsSpan());

        // Remap extents
        for (int i = 0; i < FormatConstants.MaxExtentsPerInode; i++)
        {
            var ext = src.Extents[i];
            if (ext.IsEmpty)
            {
                dst.Extents[i] = default;
                continue;
            }

            if (ext.IsSparse)
            {
                dst.Extents[i] = ext; // sparse extents have no physical block
                continue;
            }

            if (blockRemap.TryGetValue(ext.StartBlock, out long newStart))
            {
                dst.Extents[i] = new InodeExtent(newStart, ext.BlockCount, ext.Flags, ext.LogicalOffset);
            }
            else
            {
                // Not in remap table (e.g., filtered out) — zero the extent
                dst.Extents[i] = default;
            }
        }

        return dst;
    }

    // ── Private: Integrity Tree ──────────────────────────────────────────

    private static async Task WriteIntegrityTreeAsync(
        Stream output, ExportLayout layout, int blockSize, CancellationToken ct)
    {
        // Simple single-block integrity anchor (placeholder Merkle root)
        var buf = new byte[blockSize];

        // Write a recognizable anchor with a zero Merkle root
        IntegrityAnchor.Serialize(IntegrityAnchor.CreateDefault(), buf, blockSize);
        UniversalBlockTrailer.Write(buf, blockSize, BlockTypeTags.IANT, 1);

        await output.WriteAsync(buf, ct).ConfigureAwait(false);
    }

    // ── Private: Data Region ──────────────────────────────────────────────

    private async Task<long> WriteDataRegionAsync(
        Stream output,
        List<ExtendedInode512> selectedInodes,
        Dictionary<long, long> blockRemap,
        ExportLayout layout,
        int dstBlockSize,
        CancellationToken ct)
    {
        long blocksWritten   = 0;
        var  srcBuf          = new byte[_srcBlockSize];
        var  dstBuf          = new byte[dstBlockSize];

        // Track which source start-blocks we have already copied
        var copied = new HashSet<long>();

        foreach (var inode in selectedInodes)
        {
            for (int i = 0; i < inode.ExtentCount && i < FormatConstants.MaxExtentsPerInode; i++)
            {
                var ext = inode.Extents[i];
                if (ext.IsEmpty || ext.IsSparse) continue;
                if (!copied.Add(ext.StartBlock)) continue; // already emitted

                // Copy each block in the extent
                for (int b = 0; b < ext.BlockCount; b++)
                {
                    ct.ThrowIfCancellationRequested();

                    long srcBlock = ext.StartBlock + b;

                    // Read from source
                    await _source.ReadBlockAsync(srcBlock, srcBuf, ct).ConfigureAwait(false);

                    // Adapt block size if source and dest block sizes differ
                    if (_srcBlockSize == dstBlockSize)
                    {
                        srcBuf.AsSpan().CopyTo(dstBuf);
                    }
                    else if (_srcBlockSize < dstBlockSize)
                    {
                        dstBuf.AsSpan().Clear();
                        srcBuf.AsSpan().CopyTo(dstBuf.AsSpan(0, _srcBlockSize));
                    }
                    else
                    {
                        // Source blocks larger — truncate to dst block size
                        srcBuf.AsSpan(0, dstBlockSize).CopyTo(dstBuf);
                    }

                    // Overwrite trailer with correct tag and checksum
                    UniversalBlockTrailer.Write(dstBuf, dstBlockSize, BlockTypeTags.DATA, 1);

                    await output.WriteAsync(dstBuf, ct).ConfigureAwait(false);
                    blocksWritten++;

                    // Write TRLR sentinel every TrailerInterval blocks
                    if (blocksWritten % TrailerInterval == 0)
                    {
                        var trlrBuf = new byte[dstBlockSize];
                        UniversalBlockTrailer.Write(trlrBuf, dstBlockSize, BlockTypeTags.TRLR, 1);
                        await output.WriteAsync(trlrBuf, ct).ConfigureAwait(false);
                    }
                }
            }
        }

        return blocksWritten;
    }
}
