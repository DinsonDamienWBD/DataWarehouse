using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

// ─────────────────────────────────────────────────────────────────────────────
// Supporting value types
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Recovery status of an individual data block.
/// </summary>
public enum RecoveryStatus
{
    /// <summary>Block was read and its XxHash64 checksum matches the TRLR record.</summary>
    Verified,

    /// <summary>Block was read but its checksum does not match the TRLR record.</summary>
    Corrupt,

    /// <summary>
    /// The TRLR record has a zero generation number or zero checksum, indicating the
    /// slot was never written (suspicious but not definitively corrupt).
    /// </summary>
    Suspect,

    /// <summary>
    /// The TRLR block that should cover this data block was not found at the expected stride
    /// position (device too short, I/O error reading the TRLR block, etc.).
    /// </summary>
    TrlrMissing
}

/// <summary>
/// Describes a single validated TRLR block discovered during the stride scan.
/// </summary>
public readonly struct TrlrBlockInfo
{
    /// <summary>Physical block number of the TRLR block.</summary>
    public long BlockNumber { get; }

    /// <summary>Number of TRLR records in this block that contain non-zero checksums.</summary>
    public int ValidRecordCount { get; }

    /// <summary>Lowest generation number seen across all records in this TRLR block.</summary>
    public uint MinGeneration { get; }

    /// <summary>Highest generation number seen across all records in this TRLR block.</summary>
    public uint MaxGeneration { get; }

    /// <summary>Initialises a new <see cref="TrlrBlockInfo"/>.</summary>
    public TrlrBlockInfo(long blockNumber, int validRecordCount, uint minGeneration, uint maxGeneration)
    {
        BlockNumber = blockNumber;
        ValidRecordCount = validRecordCount;
        MinGeneration = minGeneration;
        MaxGeneration = maxGeneration;
    }
}

/// <summary>
/// Summary of the TRLR stride scan phase.
/// </summary>
public readonly struct TrlrScanResult
{
    /// <summary>Total number of valid TRLR blocks found.</summary>
    public int TrlrBlocksFound { get; }

    /// <summary>Total trailer records parsed across all valid TRLR blocks.</summary>
    public long TotalTrailerRecords { get; }

    /// <summary>All valid TRLR blocks, in ascending block order.</summary>
    public IReadOnlyList<TrlrBlockInfo> ValidBlocks { get; }

    /// <summary>Initialises a new <see cref="TrlrScanResult"/>.</summary>
    public TrlrScanResult(int trlrBlocksFound, long totalTrailerRecords, IReadOnlyList<TrlrBlockInfo> validBlocks)
    {
        TrlrBlocksFound = trlrBlocksFound;
        TotalTrailerRecords = totalTrailerRecords;
        ValidBlocks = validBlocks;
    }
}

/// <summary>
/// Describes a single recovered data block with its recovery status.
/// </summary>
public readonly struct RecoveredBlock
{
    /// <summary>Physical block number on the device.</summary>
    public long PhysicalBlockNumber { get; }

    /// <summary>
    /// Zero-based logical data index within the data region (position within the stride group,
    /// 0-254 relative to the TRLR block's group).
    /// </summary>
    public long LogicalDataIndex { get; }

    /// <summary>Generation number from the TRLR record.</summary>
    public uint GenerationNumber { get; }

    /// <summary>XxHash64 checksum stored in the TRLR record.</summary>
    public ulong Checksum { get; }

    /// <summary>Recovery status for this block.</summary>
    public RecoveryStatus Status { get; }

    /// <summary>Initialises a new <see cref="RecoveredBlock"/>.</summary>
    public RecoveredBlock(long physicalBlockNumber, long logicalDataIndex,
        uint generationNumber, ulong checksum, RecoveryStatus status)
    {
        PhysicalBlockNumber = physicalBlockNumber;
        LogicalDataIndex = logicalDataIndex;
        GenerationNumber = generationNumber;
        Checksum = checksum;
        Status = status;
    }
}

/// <summary>
/// Summary of the metadata block recovery phase.
/// </summary>
public readonly struct MetadataRecoveryResult
{
    /// <summary>Number of metadata blocks found whose type tag is a known DWVD v2.0 tag.</summary>
    public long MetadataBlocksFound { get; }

    /// <summary>Metadata blocks grouped by their BlockTypeTag value.</summary>
    public IReadOnlyDictionary<uint, List<long>> BlocksByType { get; }

    /// <summary>Initialises a new <see cref="MetadataRecoveryResult"/>.</summary>
    public MetadataRecoveryResult(long metadataBlocksFound, IReadOnlyDictionary<uint, List<long>> blocksByType)
    {
        MetadataBlocksFound = metadataBlocksFound;
        BlocksByType = blocksByType;
    }
}

/// <summary>
/// Comprehensive forensic report produced by <see cref="ForensicNecromancyRecovery.RunFullRecoveryAsync"/>.
/// </summary>
public readonly struct ForensicReport
{
    /// <summary>Total blocks examined during the scan.</summary>
    public long TotalBlocksScanned { get; }

    /// <summary>Data blocks whose checksums were verified as intact.</summary>
    public long DataBlocksRecovered { get; }

    /// <summary>Data blocks whose checksums did not match (corrupt).</summary>
    public long DataBlocksCorrupt { get; }

    /// <summary>Metadata blocks found (SUPB, RMAP, INOD, BTRE, etc.).</summary>
    public long MetadataBlocksRecovered { get; }

    /// <summary>Metadata block counts keyed by <see cref="BlockTypeTags"/> uint value.</summary>
    public Dictionary<uint, int> MetadataBlocksByType { get; }

    /// <summary>All recovered data blocks (verified, corrupt, and suspect).</summary>
    public IReadOnlyList<RecoveredBlock> RecoveredBlocks { get; }

    /// <summary>
    /// Fraction of data blocks that were verified intact.
    /// Returns 0 when no data blocks were encountered.
    /// </summary>
    public double RecoveryRate => (DataBlocksRecovered + DataBlocksCorrupt) == 0
        ? 0.0
        : (double)DataBlocksRecovered / (DataBlocksRecovered + DataBlocksCorrupt);

    /// <summary>Wall-clock time taken for the full recovery run.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Initialises a new <see cref="ForensicReport"/>.</summary>
    public ForensicReport(
        long totalBlocksScanned,
        long dataBlocksRecovered,
        long dataBlocksCorrupt,
        long metadataBlocksRecovered,
        Dictionary<uint, int> metadataBlocksByType,
        IReadOnlyList<RecoveredBlock> recoveredBlocks,
        TimeSpan duration)
    {
        TotalBlocksScanned = totalBlocksScanned;
        DataBlocksRecovered = dataBlocksRecovered;
        DataBlocksCorrupt = dataBlocksCorrupt;
        MetadataBlocksRecovered = metadataBlocksRecovered;
        MetadataBlocksByType = metadataBlocksByType;
        RecoveredBlocks = recoveredBlocks;
        Duration = duration;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Main recovery engine
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// VOPT-43: Forensic necromancy recovery tool — reconstructs VDE data from a TRLR
/// stride-scan when all metadata regions (Superblock, Region Directory, Inode Table)
/// are damaged or destroyed.
///
/// <para>Architecture:</para>
/// <list type="bullet">
///   <item>TRLR blocks are placed at every 256th block within the data region (stride 256).
///   Each TRLR block contains 255 trailer records — one per data block in the preceding run.</item>
///   <item>Each trailer record stores: BlockTypeTag (4B), GenerationNumber (4B), XxHash64 (8B).</item>
///   <item>Because TRLR blocks are at known stride offsets they can be found purely by scanning,
///   with no reliance on Superblock, RegionDirectory, or InodeTable.</item>
/// </list>
///
/// <para>Recovery phases:</para>
/// <list type="number">
///   <item>Phase 1 — TRLR stride scan: locate every valid TRLR block.</item>
///   <item>Phase 2 — Data block verification: re-read and checksum-verify the 255 data blocks
///   that precede each TRLR block.</item>
///   <item>Phase 3 — Metadata block recovery: full-device scan for non-DATA typed blocks (optional).</item>
/// </list>
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 (VOPT-43): TRLR stride-scan forensic recovery engine")]
public sealed class ForensicNecromancyRecovery
{
    // TRLR layout within each TRLR block:
    //   [Record 0..N-1] each 16B: [DataBlockTypeTag:4 LE][GenerationNumber:4 LE][XxHash64:8 LE]
    //   [TRLR self-trailer (last 16B)]: [BlockTypeTag=TRLR:4 LE][GenerationNumber:4 LE][XxHash64:8 LE]
    //
    // One TRLR block holds (blockSize - 16) / 16 records; for the default 4096 B block that is
    // (4096 - 16) / 16 = 255 records.  The stride is RecordsPerTrlr + 1 = 256.

    private const int TrailerRecordSize = UniversalBlockTrailer.Size; // 16 bytes

    private readonly IBlockDevice _device;
    private readonly ForensicRecoveryConfig _config;

    /// <summary>Stride in blocks: 255 data blocks followed by 1 TRLR block = 256.</summary>
    private int RecordsPerTrlrBlock => (_config.BlockSize - TrailerRecordSize) / TrailerRecordSize;

    /// <summary>Number of blocks in one stride group (data blocks + 1 TRLR block).</summary>
    private int StrideSize => RecordsPerTrlrBlock + 1;

    /// <summary>
    /// Initialises the recovery engine.
    /// </summary>
    /// <param name="device">Block device to scan. Must not be null.</param>
    /// <param name="config">Recovery configuration. Must not be null.</param>
    public ForensicNecromancyRecovery(IBlockDevice device, ForensicRecoveryConfig config)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(config);
        if (config.BlockSize < TrailerRecordSize)
            throw new ArgumentOutOfRangeException(nameof(config),
                $"BlockSize must be at least {TrailerRecordSize} bytes.");

        _device = device;
        _config = config;
    }

    // ── Phase 1: TRLR stride scan ─────────────────────────────────────────────

    /// <summary>
    /// Scans the device at TRLR stride positions to locate all valid TRLR blocks.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="TrlrScanResult"/> describing all valid TRLR blocks found.</returns>
    public async Task<TrlrScanResult> ScanForTrailersAsync(CancellationToken ct = default)
    {
        long deviceBlocks = _device.BlockCount;
        long start = Math.Max(0, _config.ScanStartBlock);
        long end = Math.Min(deviceBlocks, _config.ScanEndBlock);

        int stride = StrideSize;
        var blockBuf = new byte[_config.BlockSize];
        var validBlocks = new List<TrlrBlockInfo>();
        long totalRecords = 0;

        // TRLR blocks sit at positions: (stride - 1), (2 * stride - 1), (3 * stride - 1) ...
        // i.e. every multiple of stride offset by (stride - 1).
        // We scan candidate positions aligned to stride within [start, end).
        long firstCandidate = ComputeFirstTrlrCandidate(start, stride);

        for (long candidate = firstCandidate; candidate < end; candidate += stride)
        {
            ct.ThrowIfCancellationRequested();

            await _device.ReadBlockAsync(candidate, blockBuf, ct).ConfigureAwait(false);

            // Validate: last 16B of this block must have BlockTypeTag == TRLR
            var selfTrailer = UniversalBlockTrailer.Read(blockBuf, _config.BlockSize);
            if (selfTrailer.BlockTypeTag != BlockTypeTags.TRLR)
                continue;

            // Parse the record area (bytes 0 .. blockSize-16)
            int recordCount = RecordsPerTrlrBlock;
            int validRecords = 0;
            uint minGen = uint.MaxValue;
            uint maxGen = uint.MinValue;

            for (int i = 0; i < recordCount; i++)
            {
                int offset = i * TrailerRecordSize;
                uint tag = BinaryPrimitives.ReadUInt32LittleEndian(blockBuf.AsSpan(offset, 4));
                uint gen  = BinaryPrimitives.ReadUInt32LittleEndian(blockBuf.AsSpan(offset + 4, 4));
                ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(blockBuf.AsSpan(offset + 8, 8));

                // A non-zero checksum means the slot was used
                if (hash != 0)
                {
                    validRecords++;
                    if (gen < minGen) minGen = gen;
                    if (gen > maxGen) maxGen = gen;
                }

                _ = tag; // tag is returned in RecoveredBlock during Phase 2
            }

            if (minGen == uint.MaxValue) { minGen = 0; maxGen = 0; }

            validBlocks.Add(new TrlrBlockInfo(candidate, validRecords, minGen, maxGen));
            totalRecords += validRecords;
        }

        return new TrlrScanResult(validBlocks.Count, totalRecords, validBlocks);
    }

    // ── Phase 2: Data block verification ─────────────────────────────────────

    /// <summary>
    /// For each TRLR block found in <paramref name="scanResult"/>, reads the 255 preceding
    /// data blocks and verifies their XxHash64 checksums against the TRLR records.
    /// </summary>
    /// <param name="scanResult">Result from <see cref="ScanForTrailersAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="RecoveryResult"/> containing status for every data block examined.</returns>
    public async Task<RecoveryResult> RecoverDataBlocksAsync(TrlrScanResult scanResult, CancellationToken ct = default)
    {
        int recordsPerTrlr = RecordsPerTrlrBlock;
        var blockBuf = new byte[_config.BlockSize];
        var trlrBuf  = new byte[_config.BlockSize];
        var recovered = new List<RecoveredBlock>();

        foreach (var trlrInfo in scanResult.ValidBlocks)
        {
            ct.ThrowIfCancellationRequested();

            // Re-read the TRLR block to access its record table
            await _device.ReadBlockAsync(trlrInfo.BlockNumber, trlrBuf, ct).ConfigureAwait(false);

            long groupBase = trlrInfo.BlockNumber - recordsPerTrlr; // first data block in this group

            for (int slot = 0; slot < recordsPerTrlr; slot++)
            {
                ct.ThrowIfCancellationRequested();

                long physBlock = groupBase + slot;

                // Parse TRLR record for this slot
                int recOffset = slot * TrailerRecordSize;
                uint  tag     = BinaryPrimitives.ReadUInt32LittleEndian(trlrBuf.AsSpan(recOffset,     4));
                uint  gen     = BinaryPrimitives.ReadUInt32LittleEndian(trlrBuf.AsSpan(recOffset + 4, 4));
                ulong stored  = BinaryPrimitives.ReadUInt64LittleEndian(trlrBuf.AsSpan(recOffset + 8, 8));

                _ = tag; // type information available but not needed for data-block classification here

                // Determine logical data index globally across groups
                long groupIndex = trlrInfo.BlockNumber / StrideSize;
                long logicalIdx = groupIndex * recordsPerTrlr + slot;

                // Suspect: slot was never written
                if (gen == 0 && stored == 0)
                {
                    recovered.Add(new RecoveredBlock(physBlock, logicalIdx, gen, stored, RecoveryStatus.Suspect));
                    continue;
                }

                // Out-of-range block — can happen when scan range doesn't start at block 0
                if (physBlock < 0 || physBlock >= _device.BlockCount)
                {
                    recovered.Add(new RecoveredBlock(physBlock, logicalIdx, gen, stored, RecoveryStatus.TrlrMissing));
                    continue;
                }

                RecoveryStatus status;

                if (_config.VerifyDataBlockChecksums)
                {
                    await _device.ReadBlockAsync(physBlock, blockBuf, ct).ConfigureAwait(false);
                    int trailerOffset = _config.BlockSize - TrailerRecordSize;
                    ulong computed = XxHash64.HashToUInt64(blockBuf.AsSpan(0, trailerOffset));
                    status = computed == stored ? RecoveryStatus.Verified : RecoveryStatus.Corrupt;
                }
                else
                {
                    // Trust the TRLR record without re-reading
                    status = RecoveryStatus.Verified;
                }

                recovered.Add(new RecoveredBlock(physBlock, logicalIdx, gen, stored, status));
            }
        }

        return new RecoveryResult(recovered);
    }

    // ── Phase 3: Metadata block recovery ─────────────────────────────────────

    /// <summary>
    /// Performs a full-device scan looking for blocks whose trailer carries a known DWVD v2.0
    /// metadata block type tag (SUPB, RMAP, INOD, BTRE, SNAP, etc.).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="MetadataRecoveryResult"/> listing all metadata blocks found.</returns>
    public async Task<MetadataRecoveryResult> RecoverMetadataBlocksAsync(CancellationToken ct = default)
    {
        long deviceBlocks = _device.BlockCount;
        long start = Math.Max(0, _config.ScanStartBlock);
        long end   = Math.Min(deviceBlocks, _config.ScanEndBlock);

        var blockBuf = new byte[_config.BlockSize];
        var byType   = new Dictionary<uint, List<long>>();
        long found   = 0;

        for (long b = start; b < end; b++)
        {
            ct.ThrowIfCancellationRequested();

            await _device.ReadBlockAsync(b, blockBuf, ct).ConfigureAwait(false);

            // Read the trailer from the last 16B
            var trailer = UniversalBlockTrailer.Read(blockBuf, _config.BlockSize);

            // Skip DATA blocks and TRLR blocks — those are handled by Phase 2 / Phase 1
            if (trailer.BlockTypeTag == BlockTypeTags.DATA ||
                trailer.BlockTypeTag == BlockTypeTags.TRLR ||
                trailer.BlockTypeTag == BlockTypeTags.FREE)
                continue;

            if (!BlockTypeTags.IsKnownTag(trailer.BlockTypeTag))
                continue;

            // Validate trailer checksum before accepting the block
            if (!UniversalBlockTrailer.Verify(blockBuf, _config.BlockSize))
                continue;

            if (!byType.TryGetValue(trailer.BlockTypeTag, out var list))
            {
                list = new List<long>();
                byType[trailer.BlockTypeTag] = list;
            }
            list.Add(b);
            found++;
        }

        return new MetadataRecoveryResult(found, byType);
    }

    // ── Full recovery orchestration ───────────────────────────────────────────

    /// <summary>
    /// Executes all three recovery phases and returns a comprehensive <see cref="ForensicReport"/>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ForensicReport"/> describing what was found and recovered.</returns>
    public async Task<ForensicReport> RunFullRecoveryAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // Phase 1: TRLR stride scan
        var scanResult = await ScanForTrailersAsync(ct).ConfigureAwait(false);

        // Phase 2: Data block verification
        var recoveryResult = await RecoverDataBlocksAsync(scanResult, ct).ConfigureAwait(false);

        long dataVerified = recoveryResult.RecoveredBlocks.Count(b => b.Status == RecoveryStatus.Verified);
        long dataCorrupt  = recoveryResult.RecoveredBlocks.Count(b => b.Status == RecoveryStatus.Corrupt);

        // Phase 3: Metadata block recovery (optional)
        long metaFound = 0;
        var metaByType = new Dictionary<uint, int>();

        if (_config.RecoverMetadataBlocks)
        {
            var metaResult = await RecoverMetadataBlocksAsync(ct).ConfigureAwait(false);
            metaFound = metaResult.MetadataBlocksFound;

            foreach (var (tag, blocks) in metaResult.BlocksByType)
                metaByType[tag] = blocks.Count;
        }

        // Compute total blocks scanned (data + TRLR + metadata if phase 3 ran)
        long deviceBlocks = _device.BlockCount;
        long start = Math.Max(0, _config.ScanStartBlock);
        long end   = Math.Min(deviceBlocks, _config.ScanEndBlock);
        long totalScanned = _config.RecoverMetadataBlocks
            ? end - start
            : (long)scanResult.TrlrBlocksFound * StrideSize;

        sw.Stop();

        return new ForensicReport(
            totalBlocksScanned:     totalScanned,
            dataBlocksRecovered:    dataVerified,
            dataBlocksCorrupt:      dataCorrupt,
            metadataBlocksRecovered: metaFound,
            metadataBlocksByType:   metaByType,
            recoveredBlocks:        recoveryResult.RecoveredBlocks,
            duration:               sw.Elapsed);
    }

    // ── Optional export ───────────────────────────────────────────────────────

    /// <summary>
    /// Writes all <see cref="RecoveryStatus.Verified"/> blocks from <paramref name="report"/>
    /// to <paramref name="outputPath"/>, each as a raw binary file named
    /// <c>block-{logicalIndex:D10}.bin</c>.
    /// </summary>
    /// <param name="report">Forensic report from <see cref="RunFullRecoveryAsync"/>.</param>
    /// <param name="outputPath">Directory where block files are written. Created if absent.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ExportRecoveredBlocksAsync(ForensicReport report, string outputPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outputPath);

        Directory.CreateDirectory(outputPath);

        var buf = new byte[_config.BlockSize];

        foreach (var block in report.RecoveredBlocks)
        {
            if (block.Status != RecoveryStatus.Verified)
                continue;

            ct.ThrowIfCancellationRequested();

            await _device.ReadBlockAsync(block.PhysicalBlockNumber, buf, ct).ConfigureAwait(false);

            string fileName = $"block-{block.LogicalDataIndex:D10}.bin";
            string filePath = Path.Combine(outputPath, fileName);

            await File.WriteAllBytesAsync(filePath, buf, ct).ConfigureAwait(false);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private long ComputeFirstTrlrCandidate(long start, int stride)
    {
        // TRLR positions: stride-1, 2*stride-1, 3*stride-1, ...
        // i.e. positions of the form (k * stride + stride - 1) for k >= 0
        // = k * stride + (stride - 1)
        if (start == 0)
            return stride - 1;

        // Find smallest k such that k * stride + (stride - 1) >= start
        long k = (start - (stride - 1) + stride - 1) / stride;
        if (k < 0) k = 0;
        return k * stride + (stride - 1);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// RecoveryResult (internal helper bag returned from Phase 2)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Intermediate container for Phase 2 data block recovery results.
/// </summary>
public sealed class RecoveryResult
{
    /// <summary>All data blocks examined, with their recovery status.</summary>
    public IReadOnlyList<RecoveredBlock> RecoveredBlocks { get; }

    /// <summary>Initialises a new <see cref="RecoveryResult"/>.</summary>
    public RecoveryResult(IReadOnlyList<RecoveredBlock> recoveredBlocks)
    {
        ArgumentNullException.ThrowIfNull(recoveredBlocks);
        RecoveredBlocks = recoveredBlocks;
    }
}
