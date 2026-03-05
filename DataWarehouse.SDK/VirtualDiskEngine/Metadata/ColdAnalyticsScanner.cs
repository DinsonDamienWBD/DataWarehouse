using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Metadata;

// ── Analytics result types ───────────────────────────────────────────────────

/// <summary>
/// Aggregated statistics from a sequential inode table scan.
/// All counts are derived purely from inode metadata — no data blocks are read.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-45 inode analytics")]
public readonly struct InodeAnalytics
{
    /// <summary>Total number of file inodes encountered.</summary>
    public long TotalFiles { get; init; }

    /// <summary>Total number of directory inodes encountered.</summary>
    public long TotalDirectories { get; init; }

    /// <summary>Total number of symbolic link inodes encountered.</summary>
    public long TotalSymlinks { get; init; }

    /// <summary>Sum of logical sizes (<see cref="ExtendedInode512.Size"/>) across all inodes.</summary>
    public long TotalLogicalBytes { get; init; }

    /// <summary>Sum of allocated sizes (<see cref="ExtendedInode512.AllocatedSize"/>) across all inodes.</summary>
    public long TotalAllocatedBytes { get; init; }

    /// <summary>Average number of extents per file inode (0.0 when no files present).</summary>
    public double AverageExtentsPerFile { get; init; }

    /// <summary>
    /// Ratio of inodes using indirect extents (i.e. <see cref="ExtendedInode512.IndirectExtentBlock"/> != 0).
    /// Range [0.0, 1.0]. A higher value indicates greater fragmentation.
    /// </summary>
    public double FragmentationRatio { get; init; }

    /// <summary>
    /// Count of inodes that have each active-module flag set, keyed by module name.
    /// For example, "Encrypted" counts inodes with <see cref="InodeFlags.Encrypted"/>.
    /// </summary>
    public Dictionary<string, long> ModuleUsageCounts { get; init; }

    /// <summary>Oldest creation timestamp encountered (UTC ticks, 0 when no inodes scanned).</summary>
    public long OldestFileEpoch { get; init; }

    /// <summary>Newest creation timestamp encountered (UTC ticks, 0 when no inodes scanned).</summary>
    public long NewestFileEpoch { get; init; }
}

/// <summary>
/// Aggregated statistics from a scan of Universal Block Trailer (TRLR) blocks.
/// Only TRLR-tagged blocks are read; data blocks are skipped entirely.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-45 trailer analytics")]
public readonly struct TrailerAnalytics
{
    /// <summary>Total number of trailer blocks read during the scan.</summary>
    public long TrailerBlocksScanned { get; init; }

    /// <summary>Total number of individual trailer records examined.</summary>
    public long TrailerRecordsRead { get; init; }

    /// <summary>
    /// Number of trailer records that are suspect for corruption:
    /// records where <see cref="UniversalBlockTrailer.GenerationNumber"/> == 0
    /// or <see cref="UniversalBlockTrailer.XxHash64Checksum"/> == 0.
    /// </summary>
    public long SuspectCorruptions { get; init; }

    /// <summary>Smallest generation number observed across all trailer records.</summary>
    public uint MinGeneration { get; init; }

    /// <summary>Largest generation number observed across all trailer records.</summary>
    public uint MaxGeneration { get; init; }
}

/// <summary>
/// Aggregated statistics from a scan of inline tag areas within inode blocks.
/// Tag data is extracted from <see cref="ExtendedInode512.InlineXattrArea"/> without
/// reading any data blocks.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-45 tag analytics")]
public readonly struct TagAnalytics
{
    /// <summary>Number of inodes that have at least one inline tag.</summary>
    public long InodesWithTags { get; init; }

    /// <summary>Total number of valid compact inline tags found across all inodes.</summary>
    public long TotalInlineTags { get; init; }

    /// <summary>
    /// Number of inodes with a non-zero extended attribute block pointer,
    /// indicating tag overflow beyond the inline area.
    /// </summary>
    public long InodesWithOverflow { get; init; }

    /// <summary>
    /// Distribution of tags by namespace hash.
    /// Key: 4-byte namespace hash (little-endian uint32). Value: tag count.
    /// </summary>
    public Dictionary<uint, long> NamespaceHashCounts { get; init; }
}

/// <summary>
/// Combined results from a full cold analytics scan of a VDE volume.
/// Individual scanner results are null when the corresponding scan was disabled
/// via <see cref="ColdAnalyticsConfig"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-45 combined cold analytics report")]
public readonly struct ColdAnalyticsReport
{
    /// <summary>Inode analytics, or null if <see cref="ColdAnalyticsConfig.ScanInodes"/> was false.</summary>
    public InodeAnalytics? Inodes { get; init; }

    /// <summary>Trailer analytics, or null if <see cref="ColdAnalyticsConfig.ScanTrailers"/> was false.</summary>
    public TrailerAnalytics? Trailers { get; init; }

    /// <summary>Tag analytics, or null if <see cref="ColdAnalyticsConfig.ScanTags"/> was false.</summary>
    public TagAnalytics? Tags { get; init; }

    /// <summary>Wall-clock duration of the full scan.</summary>
    public TimeSpan Duration { get; init; }
}

// ── Scanner ──────────────────────────────────────────────────────────────────

/// <summary>
/// Metadata-only cold analytics scanner for DWVD v2.0 volumes (VOPT-45).
///
/// <para>
/// All three scanners (inode, TRLR, tag) operate exclusively on metadata blocks.
/// No DATA-tagged blocks are ever read, making this safe and efficient for
/// cold/archival volumes where data reads would be expensive.
/// </para>
///
/// <para>
/// Typical usage:
/// <code>
/// var scanner = new ColdAnalyticsScanner(device, blockSize, new ColdAnalyticsConfig());
/// var report = await scanner.RunFullScanAsync(inodeStart, inodeCount, dataStart, dataBlocks, ct);
/// </code>
/// </para>
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-45 metadata-only cold analytics scanner")]
public sealed class ColdAnalyticsScanner
{
    // Each ExtendedInode512 is exactly 512 bytes; a 4 KiB block holds 8.
    private const int InodesPerBlock = 4096 / ExtendedInode512.SerializedSize; // 8

    // Compact inline tag record size: [NamespaceHash:4][NameHash:4][ValueType:1][ValueLen:1][Value:<=22] = 32 bytes.
    private const int CompactTagSize = 32;
    private const int CompactTagNamespaceHashOffset = 0;  // uint32
    private const int CompactTagNameHashOffset = 4;        // uint32
    private const int CompactTagValueTypeOffset = 8;       // byte
    private const int CompactTagValueLenOffset = 9;        // byte
    private const int CompactTagValueOffset = 10;          // up to 22 bytes
    private const int CompactTagMaxValueLen = 22;

    // TRLR blocks appear every 256 blocks: positions 255, 511, 767, ...
    // (i.e. the block at index (n+1)*256 - 1 within the data region).
    private const int TrailerBlockInterval = 256;

    private readonly IBlockDevice _device;
    private readonly int _blockSize;
    private readonly ColdAnalyticsConfig _config;

    /// <summary>
    /// Initialises the scanner.
    /// </summary>
    /// <param name="device">The underlying block device to read metadata from.</param>
    /// <param name="blockSize">Block size in bytes (must be &gt;= <see cref="ExtendedInode512.SerializedSize"/>).</param>
    /// <param name="config">Scan configuration.</param>
    /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="blockSize"/> is less than <see cref="ExtendedInode512.SerializedSize"/>.
    /// </exception>
    public ColdAnalyticsScanner(IBlockDevice device, int blockSize, ColdAnalyticsConfig config)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(config);
        if (blockSize < ExtendedInode512.SerializedSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {ExtendedInode512.SerializedSize} bytes.");

        _device = device;
        _blockSize = blockSize;
        _config = config;
    }

    // ── Inode scanner ────────────────────────────────────────────────────────

    /// <summary>
    /// Scans the inode table sequentially, reading only INOD-tagged blocks.
    /// No data blocks are touched.
    /// </summary>
    /// <param name="inodeTableStartBlock">Absolute block number of the first inode table block.</param>
    /// <param name="inodeCount">Total number of inodes in the table.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregated inode statistics.</returns>
    public async Task<InodeAnalytics> ScanInodesAsync(
        long inodeTableStartBlock,
        long inodeCount,
        CancellationToken ct = default)
    {
        long totalFiles = 0L;
        long totalDirs = 0L;
        long totalSymlinks = 0L;
        long totalLogicalBytes = 0L;
        long totalAllocatedBytes = 0L;
        long totalExtents = 0L;
        long fragmentedInodes = 0L;
        long oldestEpoch = long.MaxValue;
        long newestEpoch = long.MinValue;
        long inodesWithFlags = 0L;
        long encryptedCount = 0L;
        long compressedCount = 0L;
        long wormCount = 0L;
        long inlineDataCount = 0L;

        long inodesPerBlock = Math.Max(1L, _blockSize / ExtendedInode512.SerializedSize);
        long limit = Math.Min(inodeCount, _config.MaxInodesPerScan);
        long batchSizeAligned = Math.Max(1, (_config.BatchSize / inodesPerBlock) * inodesPerBlock);
        long inodesProcessed = 0L;

        var blockBuffer = new byte[_blockSize];

        for (long i = 0; i < limit && !ct.IsCancellationRequested; i += batchSizeAligned)
        {
            long batchInodes = Math.Min(batchSizeAligned, limit - i);
            long batchBlocks = (batchInodes + inodesPerBlock - 1) / inodesPerBlock;

            for (long b = 0; b < batchBlocks && !ct.IsCancellationRequested; b++)
            {
                long blockNumber = inodeTableStartBlock + (i / inodesPerBlock) + b;
                await _device.ReadBlockAsync(blockNumber, blockBuffer, ct).ConfigureAwait(false);

                // Determine how many inodes are in this block.
                long blockInodeOffset = (i / inodesPerBlock + b) * inodesPerBlock;
                int inodesInThisBlock = (int)Math.Min(inodesPerBlock, limit - blockInodeOffset);

                for (int j = 0; j < inodesInThisBlock; j++)
                {
                    int inodeOffset = j * ExtendedInode512.SerializedSize;
                    if (inodeOffset + ExtendedInode512.SerializedSize > blockBuffer.Length)
                        break;

                    var inodeSpan = blockBuffer.AsSpan(inodeOffset, ExtendedInode512.SerializedSize);
                    var inode = ExtendedInode512.Deserialize(inodeSpan);

                    // Skip zero/unallocated inodes (inode number 0 is reserved).
                    if (inode.InodeNumber == 0 && inode.Size == 0 && inode.Type == InodeType.File)
                        continue;

                    // Classify by type.
                    switch (inode.Type)
                    {
                        case InodeType.File:
                        case InodeType.HardLinkTarget:
                            totalFiles++;
                            totalExtents += inode.ExtentCount;
                            if (inode.IndirectExtentBlock != 0)
                                fragmentedInodes++;
                            break;
                        case InodeType.Directory:
                            totalDirs++;
                            break;
                        case InodeType.SymLink:
                            totalSymlinks++;
                            break;
                    }

                    totalLogicalBytes += inode.Size;
                    totalAllocatedBytes += inode.AllocatedSize;

                    // Track age.
                    if (inode.CreatedUtc > 0)
                    {
                        if (inode.CreatedUtc < oldestEpoch) oldestEpoch = inode.CreatedUtc;
                        if (inode.CreatedUtc > newestEpoch) newestEpoch = inode.CreatedUtc;
                    }

                    // Module / flag usage.
                    if (inode.Flags != InodeFlags.None)
                    {
                        inodesWithFlags++;
                        if ((inode.Flags & InodeFlags.Encrypted) != 0) encryptedCount++;
                        if ((inode.Flags & InodeFlags.Compressed) != 0) compressedCount++;
                        if ((inode.Flags & InodeFlags.Worm) != 0) wormCount++;
                        if ((inode.Flags & InodeFlags.InlineData) != 0) inlineDataCount++;
                    }

                    inodesProcessed++;
                }
            }
        }

        long totalInodes = totalFiles + totalDirs + totalSymlinks;

        return new InodeAnalytics
        {
            TotalFiles = totalFiles,
            TotalDirectories = totalDirs,
            TotalSymlinks = totalSymlinks,
            TotalLogicalBytes = totalLogicalBytes,
            TotalAllocatedBytes = totalAllocatedBytes,
            AverageExtentsPerFile = totalFiles > 0 ? (double)totalExtents / totalFiles : 0.0,
            FragmentationRatio = totalFiles > 0 ? (double)fragmentedInodes / totalFiles : 0.0,
            ModuleUsageCounts = new Dictionary<string, long>
            {
                { nameof(InodeFlags.Encrypted), encryptedCount },
                { nameof(InodeFlags.Compressed), compressedCount },
                { nameof(InodeFlags.Worm), wormCount },
                { nameof(InodeFlags.InlineData), inlineDataCount },
            },
            OldestFileEpoch = oldestEpoch == long.MaxValue ? 0L : oldestEpoch,
            NewestFileEpoch = newestEpoch == long.MinValue ? 0L : newestEpoch,
        };
    }

    // ── TRLR scanner ─────────────────────────────────────────────────────────

    /// <summary>
    /// Scans the data region for Universal Block Trailer (TRLR) blocks.
    /// TRLR blocks occur at every 256th position within the data region
    /// (absolute positions: dataRegionStartBlock + 255, + 511, + 767, …).
    /// Data blocks between trailers are never read.
    /// </summary>
    /// <param name="dataRegionStartBlock">Absolute block number of the first block of the data region.</param>
    /// <param name="dataRegionBlockCount">Total number of blocks in the data region.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregated trailer statistics.</returns>
    public async Task<TrailerAnalytics> ScanTrailersAsync(
        long dataRegionStartBlock,
        long dataRegionBlockCount,
        CancellationToken ct = default)
    {
        long trailerBlocksScanned = 0L;
        long trailerRecordsRead = 0L;
        long suspectCorruptions = 0L;
        uint minGeneration = uint.MaxValue;
        uint maxGeneration = 0U;

        var blockBuffer = new byte[_blockSize];

        // TRLR positions are every TrailerBlockInterval-th block (0-indexed within the region):
        // positions 255, 511, 767, ... (i.e. (n * TrailerBlockInterval) + TrailerBlockInterval - 1)
        for (long offset = TrailerBlockInterval - 1;
             offset < dataRegionBlockCount && !ct.IsCancellationRequested;
             offset += TrailerBlockInterval)
        {
            long absoluteBlock = dataRegionStartBlock + offset;
            await _device.ReadBlockAsync(absoluteBlock, blockBuffer, ct).ConfigureAwait(false);

            // Read the Universal Block Trailer from the last 16 bytes.
            var trailer = UniversalBlockTrailer.Read(blockBuffer, _blockSize);

            trailerBlocksScanned++;
            trailerRecordsRead++;

            // Suspect corruption: zero generation or zero checksum.
            if (trailer.GenerationNumber == 0 || trailer.XxHash64Checksum == 0)
                suspectCorruptions++;

            if (trailer.GenerationNumber < minGeneration)
                minGeneration = trailer.GenerationNumber;
            if (trailer.GenerationNumber > maxGeneration)
                maxGeneration = trailer.GenerationNumber;
        }

        return new TrailerAnalytics
        {
            TrailerBlocksScanned = trailerBlocksScanned,
            TrailerRecordsRead = trailerRecordsRead,
            SuspectCorruptions = suspectCorruptions,
            MinGeneration = trailerBlocksScanned > 0 ? minGeneration : 0U,
            MaxGeneration = maxGeneration,
        };
    }

    // ── Tag scanner ──────────────────────────────────────────────────────────

    /// <summary>
    /// Scans the inode table to extract inline tag analytics from each inode's
    /// <see cref="ExtendedInode512.InlineXattrArea"/>.
    ///
    /// <para>
    /// Compact inline tag format (32 bytes per entry):
    /// <code>
    ///   [NamespaceHash : 4 bytes LE uint32]
    ///   [NameHash      : 4 bytes LE uint32]
    ///   [ValueType     : 1 byte]
    ///   [ValueLen      : 1 byte]   (0-22 bytes)
    ///   [Value         : 22 bytes] (padded with zeroes)
    /// </code>
    /// A tag entry is considered present when NamespaceHash != 0 or NameHash != 0.
    /// </para>
    /// </summary>
    /// <param name="inodeTableStartBlock">Absolute block number of the first inode table block.</param>
    /// <param name="inodeCount">Total number of inodes in the table.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregated inline tag statistics.</returns>
    public async Task<TagAnalytics> ScanInlineTagsAsync(
        long inodeTableStartBlock,
        long inodeCount,
        CancellationToken ct = default)
    {
        long inodesWithTags = 0L;
        long totalInlineTags = 0L;
        long inodesWithOverflow = 0L;
        var namespaceHashCounts = new Dictionary<uint, long>();

        long inodesPerBlock = Math.Max(1L, _blockSize / ExtendedInode512.SerializedSize);
        long limit = Math.Min(inodeCount, _config.MaxInodesPerScan);
        long batchSizeAligned = Math.Max(1, (_config.BatchSize / inodesPerBlock) * inodesPerBlock);

        var blockBuffer = new byte[_blockSize];

        // Maximum number of compact tags that fit in the inline xattr area.
        int maxTagsPerInode = ExtendedInode512.MaxInlineXattrSize / CompactTagSize; // 64 / 32 = 2

        for (long i = 0; i < limit && !ct.IsCancellationRequested; i += batchSizeAligned)
        {
            long batchInodes = Math.Min(batchSizeAligned, limit - i);
            long batchBlocks = (batchInodes + inodesPerBlock - 1) / inodesPerBlock;

            for (long b = 0; b < batchBlocks && !ct.IsCancellationRequested; b++)
            {
                long blockNumber = inodeTableStartBlock + (i / inodesPerBlock) + b;
                await _device.ReadBlockAsync(blockNumber, blockBuffer, ct).ConfigureAwait(false);

                long blockInodeOffset = (i / inodesPerBlock + b) * inodesPerBlock;
                int inodesInThisBlock = (int)Math.Min(inodesPerBlock, limit - blockInodeOffset);

                for (int j = 0; j < inodesInThisBlock; j++)
                {
                    int inodeOffset = j * ExtendedInode512.SerializedSize;
                    if (inodeOffset + ExtendedInode512.SerializedSize > blockBuffer.Length)
                        break;

                    var inodeSpan = blockBuffer.AsSpan(inodeOffset, ExtendedInode512.SerializedSize);
                    var inode = ExtendedInode512.Deserialize(inodeSpan);

                    // Skip zero/unallocated inodes.
                    if (inode.InodeNumber == 0 && inode.Size == 0)
                        continue;

                    // Track overflow (ext attr block indicates tag overflow beyond inline area).
                    if (inode.ExtendedAttributeBlock != 0)
                        inodesWithOverflow++;

                    // Parse compact inline tags from InlineXattrArea.
                    var xattrArea = inode.GetInlineXattrs();
                    bool inodeHasTag = false;
                    int tagCount = 0;

                    for (int t = 0; t < maxTagsPerInode; t++)
                    {
                        int tagStart = t * CompactTagSize;
                        if (tagStart + CompactTagSize > xattrArea.Length)
                            break;

                        var tagSlot = xattrArea.Slice(tagStart, CompactTagSize);
                        uint nsHash = BinaryPrimitives.ReadUInt32LittleEndian(tagSlot.Slice(CompactTagNamespaceHashOffset, 4));
                        uint nameHash = BinaryPrimitives.ReadUInt32LittleEndian(tagSlot.Slice(CompactTagNameHashOffset, 4));
                        byte valueLen = tagSlot[CompactTagValueLenOffset];

                        // A tag is present when either hash is non-zero (avoids counting all-zero padding).
                        if (nsHash == 0 && nameHash == 0)
                            continue;

                        // Sanity: value length must be within bounds.
                        if (valueLen > CompactTagMaxValueLen)
                            continue;

                        tagCount++;
                        inodeHasTag = true;
                        totalInlineTags++;

                        namespaceHashCounts.TryGetValue(nsHash, out long existing);
                        namespaceHashCounts[nsHash] = existing + 1;
                    }

                    if (inodeHasTag)
                        inodesWithTags++;
                }
            }
        }

        return new TagAnalytics
        {
            InodesWithTags = inodesWithTags,
            TotalInlineTags = totalInlineTags,
            InodesWithOverflow = inodesWithOverflow,
            NamespaceHashCounts = namespaceHashCounts,
        };
    }

    // ── Combined scan ────────────────────────────────────────────────────────

    /// <summary>
    /// Runs all enabled analytics scanners in sequence and returns a combined report.
    /// Scanners are enabled/disabled via <see cref="ColdAnalyticsConfig"/>.
    /// No DATA-tagged blocks are ever read during any scanner pass.
    /// </summary>
    /// <param name="inodeTableStart">Absolute block number of the first inode table block.</param>
    /// <param name="inodeCount">Total number of inodes in the table.</param>
    /// <param name="dataRegionStart">Absolute block number of the first data region block.</param>
    /// <param name="dataRegionBlocks">Total number of blocks in the data region.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ColdAnalyticsReport"/> with results from all enabled scanners.</returns>
    public async Task<ColdAnalyticsReport> RunFullScanAsync(
        long inodeTableStart,
        long inodeCount,
        long dataRegionStart,
        long dataRegionBlocks,
        CancellationToken ct = default)
    {
        var start = DateTimeOffset.UtcNow;

        InodeAnalytics? inodeAnalytics = null;
        if (_config.ScanInodes)
            inodeAnalytics = await ScanInodesAsync(inodeTableStart, inodeCount, ct).ConfigureAwait(false);

        TrailerAnalytics? trailerAnalytics = null;
        if (_config.ScanTrailers && !ct.IsCancellationRequested)
            trailerAnalytics = await ScanTrailersAsync(dataRegionStart, dataRegionBlocks, ct).ConfigureAwait(false);

        TagAnalytics? tagAnalytics = null;
        if (_config.ScanTags && !ct.IsCancellationRequested)
            tagAnalytics = await ScanInlineTagsAsync(inodeTableStart, inodeCount, ct).ConfigureAwait(false);

        return new ColdAnalyticsReport
        {
            Inodes = inodeAnalytics,
            Trailers = trailerAnalytics,
            Tags = tagAnalytics,
            Duration = DateTimeOffset.UtcNow - start,
        };
    }
}
