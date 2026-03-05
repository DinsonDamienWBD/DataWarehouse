using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

// ── Result types ──────────────────────────────────────────────────────────────

/// <summary>
/// Level 1 (block-level) verification result: per-block XxHash64 comparison against
/// the checksum stored in Universal Block Trailers (TRLRs).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-38: Cross-extent block-level result (VOPT-50)")]
public sealed class BlockLevelResult
{
    /// <summary>Total number of blocks examined.</summary>
    public int BlocksVerified { get; }

    /// <summary>Number of blocks whose XxHash64 checksum did not match the stored TRLR value.</summary>
    public int BlocksFailed { get; }

    /// <summary>
    /// Physical block numbers whose checksum validation failed.
    /// Empty when <see cref="IntegrityChainConfig.IncludeBlockDetails"/> is <see langword="false"/>
    /// or when all blocks passed.
    /// </summary>
    public IReadOnlyList<long> FailedBlockNumbers { get; }

    /// <summary>Whether all verified blocks passed.</summary>
    public bool AllBlocksPassed => BlocksFailed == 0;

    /// <summary>Creates a new block-level result.</summary>
    public BlockLevelResult(int blocksVerified, int blocksFailed, IReadOnlyList<long> failedBlockNumbers)
    {
        BlocksVerified = blocksVerified;
        BlocksFailed = blocksFailed;
        FailedBlockNumbers = failedBlockNumbers ?? Array.Empty<long>();
    }
}

/// <summary>
/// Level 2 (extent-level) verification result: BLAKE3 hash over the full extent's block data,
/// compared against the 16-byte <c>ExpectedHash</c> stored in the extent descriptor.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-38: Cross-extent extent-level result (VOPT-50)")]
public sealed class ExtentLevelResult
{
    /// <summary>
    /// Whether the computed BLAKE3 hash (truncated to 16 bytes) matched the stored
    /// <c>ExpectedHash</c>. Always <see langword="true"/> when no expected hash was supplied
    /// (i.e. the extent descriptor carries a zero hash).
    /// </summary>
    public bool HashMatches { get; }

    /// <summary>
    /// Whether the extent had a non-zero <c>ExpectedHash</c> to compare against.
    /// When <see langword="false"/>, <see cref="HashMatches"/> is trivially <see langword="true"/>
    /// and callers should not treat this extent as verified.
    /// </summary>
    public bool HasStoredHash { get; }

    /// <summary>BLAKE3 hash (truncated to 16 bytes) computed over the raw block data.</summary>
    public byte[] ComputedHash { get; }

    /// <summary>16-byte expected hash from the extent descriptor, or all-zeros when absent.</summary>
    public byte[] StoredHash { get; }

    /// <summary>Physical start block of the extent.</summary>
    public long StartBlock { get; }

    /// <summary>Number of blocks in the extent.</summary>
    public int BlockCount { get; }

    /// <summary>Creates a new extent-level result.</summary>
    public ExtentLevelResult(
        bool hashMatches, bool hasStoredHash,
        byte[] computedHash, byte[] storedHash,
        long startBlock, int blockCount)
    {
        HashMatches = hashMatches;
        HasStoredHash = hasStoredHash;
        ComputedHash = computedHash ?? Array.Empty<byte>();
        StoredHash = storedHash ?? Array.Empty<byte>();
        StartBlock = startBlock;
        BlockCount = blockCount;
    }
}

/// <summary>
/// Level 3 (file-level) verification result: Merkle root computed from the file's extents
/// and compared against the stored root in the Integrity Tree region.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-38: Cross-extent file-level result (VOPT-50)")]
public sealed class FileLevelResult
{
    /// <summary>Whether the computed Merkle root matched the stored root.</summary>
    public bool RootMatches { get; }

    /// <summary>32-byte Merkle root computed from the current extent data.</summary>
    public byte[] ComputedRoot { get; }

    /// <summary>32-byte Merkle root retrieved from the Integrity Tree region.</summary>
    public byte[] StoredRoot { get; }

    /// <summary>Number of leaf nodes in the computed Merkle tree (one per extent).</summary>
    public int LeafCount { get; }

    /// <summary>Creates a new file-level result.</summary>
    public FileLevelResult(bool rootMatches, byte[] computedRoot, byte[] storedRoot, int leafCount)
    {
        RootMatches = rootMatches;
        ComputedRoot = computedRoot ?? Array.Empty<byte>();
        StoredRoot = storedRoot ?? Array.Empty<byte>();
        LeafCount = leafCount;
    }
}

/// <summary>
/// Aggregated result of a full three-level cross-extent integrity chain verification.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-38: Cross-extent chain verification result (VOPT-50)")]
public readonly struct ChainVerificationResult
{
    /// <summary>
    /// <see langword="true"/> when every executed verification level passed.
    /// <see langword="false"/> when at least one level reported a failure.
    /// </summary>
    public bool AllLevelsPassed { get; }

    /// <summary>
    /// The first level at which a failure was detected, or <see langword="null"/> when all
    /// executed levels passed.
    /// </summary>
    public IntegrityLevel? FirstFailureLevel { get; }

    /// <summary>Aggregated Level 1 block-by-block XxHash64 results.</summary>
    public BlockLevelResult BlockResult { get; }

    /// <summary>Per-extent Level 2 BLAKE3 hash results (one entry per non-sparse extent).</summary>
    public IReadOnlyList<ExtentLevelResult> ExtentResults { get; }

    /// <summary>Level 3 Merkle root result, or <see langword="null"/> when Level 3 was skipped.</summary>
    public FileLevelResult? FileResult { get; }

    /// <summary>Wall-clock duration of the full chain verification.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Creates a new chain verification result.</summary>
    public ChainVerificationResult(
        bool allLevelsPassed,
        IntegrityLevel? firstFailureLevel,
        BlockLevelResult blockResult,
        IReadOnlyList<ExtentLevelResult> extentResults,
        FileLevelResult? fileResult,
        TimeSpan duration)
    {
        AllLevelsPassed = allLevelsPassed;
        FirstFailureLevel = firstFailureLevel;
        BlockResult = blockResult;
        ExtentResults = extentResults ?? Array.Empty<ExtentLevelResult>();
        FileResult = fileResult;
        Duration = duration;
    }
}

// ── CrossExtentIntegrityChain ─────────────────────────────────────────────────

/// <summary>
/// Three-level cross-extent integrity chain verifier (VOPT-50).
/// </summary>
/// <remarks>
/// <para>
/// Provides defense-in-depth integrity verification that pinpoints the exact corruption
/// level (block, extent, or file) and enables targeted repair rather than wholesale
/// re-verification.
/// </para>
/// <para>
/// <strong>Level 1 — Block (XxHash64 via TRLR):</strong><br/>
/// Reads every data block and its corresponding Universal Block Trailer (TRLR). The TRLR
/// block for logical index <c>n</c> lives at physical offset
/// <c>floor(n / 255) * 256 + 255</c> within the data region. The stored XxHash64
/// is compared against a freshly computed hash of the block payload.
/// </para>
/// <para>
/// <strong>Level 2 — Extent (BLAKE3 / SHA-256 fallback):</strong><br/>
/// Hashes the concatenated raw block data of each extent and truncates to 16 bytes.
/// The result is compared against the 16-byte <c>ExpectedHash</c> stored in the extent
/// descriptor (e.g. <see cref="Format.SpatioTemporalExtent.ExpectedHash"/>). When no
/// expected hash is supplied, the extent is reported as not verifiable at this level.
/// </para>
/// <para>
/// <strong>Level 3 — File (Merkle root):</strong><br/>
/// Builds a Merkle tree from per-extent leaf hashes and compares the root against the
/// value stored in the Integrity Tree region (via <see cref="MerkleIntegrityVerifier"/>).
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-38: Cross-extent integrity chain verifier (VOPT-50)")]
public sealed class CrossExtentIntegrityChain
{
    // Each group of 256 blocks in the data region consists of 255 data blocks followed by
    // 1 TRLR (Universal Block Trailer) block. The TRLR block at group g is:
    //   physical = dataRegionStart + g * 256 + 255
    // For data block at logical index n (0-based within the extent's data space):
    //   group g = n / 255
    //   TRLR physical = dataRegionStart + g * 256 + 255
    private const int DataBlocksPerGroup = 255;
    private const int BlocksPerGroup = 256; // 255 data + 1 TRLR

    // BLAKE3 preferred; SHA-256 used as BCL fallback (no BLAKE3 in .NET BCL as of .NET 10).
    private const int Blake3HashSize = 32;
    private const int ExtentHashSize = 16; // Truncated BLAKE3/SHA-256 stored in ExpectedHash

    private readonly IBlockDevice _device;
    private readonly MerkleIntegrityVerifier? _merkleVerifier;
    private readonly int _blockSize;
    private readonly IntegrityChainConfig _config;

    /// <summary>
    /// Creates a new cross-extent integrity chain verifier.
    /// </summary>
    /// <param name="device">Block device providing random-access block reads.</param>
    /// <param name="merkleVerifier">
    /// Merkle integrity verifier for Level 3 file verification. May be <see langword="null"/>
    /// when only Levels 1 and 2 are needed (i.e.
    /// <see cref="IntegrityChainConfig.MaxVerificationLevel"/> &lt; <see cref="IntegrityLevel.File"/>).
    /// </param>
    /// <param name="blockSize">Block size in bytes (must match the device's block size).</param>
    /// <param name="config">Verification configuration.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="device"/> or <paramref name="config"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="blockSize"/> is less than <see cref="UniversalBlockTrailer.Size"/>.
    /// </exception>
    public CrossExtentIntegrityChain(
        IBlockDevice device,
        MerkleIntegrityVerifier? merkleVerifier,
        int blockSize,
        IntegrityChainConfig config)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(config);
        if (blockSize < UniversalBlockTrailer.Size)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {UniversalBlockTrailer.Size} bytes.");

        _device = device;
        _merkleVerifier = merkleVerifier;
        _blockSize = blockSize;
        _config = config;
    }

    // ── Level 1: Block verification ───────────────────────────────────────────

    /// <summary>
    /// Level 1 verification: reads every data block in <paramref name="extent"/>,
    /// locates its Universal Block Trailer (TRLR) record, and compares the stored
    /// XxHash64 checksum against a freshly computed value.
    /// </summary>
    /// <param name="dataRegionStart">Physical block number where the data region begins.</param>
    /// <param name="extent">The extent whose blocks are to be verified.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="BlockLevelResult"/> reporting the number of blocks verified and any
    /// blocks whose checksum did not match.
    /// </returns>
    public async Task<BlockLevelResult> VerifyBlocksAsync(
        long dataRegionStart, InodeExtent extent, CancellationToken ct)
    {
        if (extent.IsEmpty || extent.IsSparse)
            return new BlockLevelResult(0, 0, Array.Empty<long>());

        int blocksVerified = 0;
        int blocksFailed = 0;
        var failedBlockNumbers = new List<long>();

        var dataBlock = new byte[_blockSize];
        var trlrBlock = new byte[_blockSize];
        long lastTrlrPhysical = -1;

        for (int i = 0; i < extent.BlockCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            long physicalBlock = extent.StartBlock + i;

            // Compute logical index within the data region for TRLR addressing
            long logicalIndex = physicalBlock - dataRegionStart;

            // TRLR block: group = logicalIndex / DataBlocksPerGroup
            //             physical = dataRegionStart + group * BlocksPerGroup + DataBlocksPerGroup
            long group = logicalIndex / DataBlocksPerGroup;
            long trlrPhysical = dataRegionStart + group * BlocksPerGroup + DataBlocksPerGroup;

            // Read the data block
            await _device.ReadBlockAsync(physicalBlock, dataBlock, ct);

            // Read the TRLR block only if it changed (optimization to avoid re-reading same trailer)
            if (trlrPhysical != lastTrlrPhysical)
            {
                if (trlrPhysical < _device.BlockCount)
                    await _device.ReadBlockAsync(trlrPhysical, trlrBlock, ct);
                else
                    Array.Clear(trlrBlock, 0, _blockSize);
                lastTrlrPhysical = trlrPhysical;
            }

            // The TRLR block payload stores per-block records.
            // Each record is UniversalBlockTrailer.Size (16) bytes.
            // Record index within the TRLR block = logicalIndex % DataBlocksPerGroup
            int recordIndex = (int)(logicalIndex % DataBlocksPerGroup);
            int recordOffset = recordIndex * UniversalBlockTrailer.Size;

            bool checkPassed;
            int payloadSize = _blockSize - UniversalBlockTrailer.Size;

            if (recordOffset + UniversalBlockTrailer.Size <= payloadSize)
            {
                // Extract stored XxHash64 from the TRLR record (layout: tag:4, gen:4, hash:8)
                ulong storedHash = BinaryPrimitives.ReadUInt64LittleEndian(
                    trlrBlock.AsSpan(recordOffset + 8, 8));

                // Compute hash over the data block payload (excluding the TRLR at the end of data blocks)
                ulong computedHash = XxHash64.HashToUInt64(dataBlock.AsSpan(0, payloadSize));

                checkPassed = storedHash == computedHash;
            }
            else
            {
                // TRLR record offset exceeds payload area — fall back to inline block trailer
                checkPassed = UniversalBlockTrailer.Verify(dataBlock, _blockSize);
            }

            blocksVerified++;

            if (!checkPassed)
            {
                blocksFailed++;
                if (_config.IncludeBlockDetails)
                    failedBlockNumbers.Add(physicalBlock);
            }
        }

        return new BlockLevelResult(
            blocksVerified,
            blocksFailed,
            _config.IncludeBlockDetails ? failedBlockNumbers : Array.Empty<long>());
    }

    // ── Level 2: Extent verification ──────────────────────────────────────────

    /// <summary>
    /// Level 2 verification: reads all blocks in <paramref name="extent"/>, computes a
    /// BLAKE3 hash (SHA-256 fallback) over the concatenated block data (truncated to 16 bytes),
    /// and compares the result against <paramref name="expectedHash"/>.
    /// </summary>
    /// <param name="extent">The extent to verify.</param>
    /// <param name="expectedHash">
    /// The 16-byte <c>ExpectedHash</c> stored in the extent descriptor
    /// (e.g. <see cref="Format.SpatioTemporalExtent.ExpectedHash"/>).
    /// Pass <see langword="null"/> or an all-zero array when the extent has no stored hash;
    /// the result will report <see cref="ExtentLevelResult.HasStoredHash"/> = <see langword="false"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// An <see cref="ExtentLevelResult"/> reporting whether the computed hash matched the stored value.
    /// </returns>
    public async Task<ExtentLevelResult> VerifyExtentAsync(
        InodeExtent extent, byte[]? expectedHash, CancellationToken ct)
    {
        bool hasStoredHash = expectedHash is { Length: > 0 } && !IsAllZeros(expectedHash);

        if (extent.IsEmpty || extent.IsSparse)
        {
            return new ExtentLevelResult(
                hashMatches: !hasStoredHash,
                hasStoredHash: hasStoredHash,
                computedHash: new byte[ExtentHashSize],
                storedHash: expectedHash ?? new byte[ExtentHashSize],
                startBlock: extent.StartBlock,
                blockCount: extent.BlockCount);
        }

        // Accumulate all block data into a single byte array for hashing
        long totalBytes = (long)extent.BlockCount * _blockSize;

        // For very large extents, use an incremental SHA-256 to avoid huge allocations
        byte[] computedHash;
        using (var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var blockBuffer = new byte[_blockSize];
            for (int i = 0; i < extent.BlockCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                await _device.ReadBlockAsync(extent.StartBlock + i, blockBuffer, ct);
                sha256.AppendData(blockBuffer);
            }

            byte[] fullHash = sha256.GetHashAndReset();
            // Truncate to ExtentHashSize (16 bytes) to match SpatioTemporalExtent.ExpectedHash layout
            computedHash = new byte[ExtentHashSize];
            Array.Copy(fullHash, computedHash, ExtentHashSize);
        }

        byte[] storedHashNormalized = NormalizeHash(expectedHash, ExtentHashSize);
        bool matches = !hasStoredHash || CryptographicOperations.FixedTimeEquals(
            computedHash.AsSpan(), storedHashNormalized.AsSpan());

        return new ExtentLevelResult(
            hashMatches: matches,
            hasStoredHash: hasStoredHash,
            computedHash: computedHash,
            storedHash: storedHashNormalized,
            startBlock: extent.StartBlock,
            blockCount: extent.BlockCount);
    }

    // ── Level 3: File verification ────────────────────────────────────────────

    /// <summary>
    /// Level 3 verification: builds a Merkle tree from the leaf hashes of each extent's
    /// block data and compares the root against the value stored in the Integrity Tree region.
    /// </summary>
    /// <param name="inodeNumber">Inode number that identifies the file's Merkle tree in storage.</param>
    /// <param name="extents">All extents of the file (non-sparse extents contribute leaf hashes).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="FileLevelResult"/> reporting whether the computed Merkle root matched the stored value.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no <see cref="MerkleIntegrityVerifier"/> was provided during construction.
    /// </exception>
    public async Task<FileLevelResult> VerifyFileAsync(
        long inodeNumber, IReadOnlyList<InodeExtent> extents, CancellationToken ct)
    {
        if (_merkleVerifier is null)
            throw new InvalidOperationException(
                "A MerkleIntegrityVerifier must be provided to perform Level 3 (file) verification.");

        // Build per-extent leaf checksums: SHA-256 over the extent's block data (matches
        // the format used by MerkleIntegrityVerifier.StoreMerkleTreeAsync).
        var leafChecksums = new List<byte[]>();
        var blockBuffer = new byte[_blockSize];

        foreach (var extent in extents)
        {
            if (extent.IsEmpty || extent.IsSparse)
                continue;

            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int i = 0; i < extent.BlockCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                await _device.ReadBlockAsync(extent.StartBlock + i, blockBuffer, ct);
                sha256.AppendData(blockBuffer);
            }

            byte[] extentHash = sha256.GetHashAndReset();
            // Store as 8-byte XxHash64-compatible checksum so MerkleIntegrityVerifier can SHA-256 it into a leaf
            // The Merkle verifier expects byte[8] (XxHash64 in LE); we derive a stable 8-byte token from the extent hash.
            byte[] checksum = new byte[8];
            Array.Copy(extentHash, checksum, 8);
            leafChecksums.Add(checksum);
        }

        if (leafChecksums.Count == 0)
        {
            return new FileLevelResult(
                rootMatches: true,
                computedRoot: Array.Empty<byte>(),
                storedRoot: Array.Empty<byte>(),
                leafCount: 0);
        }

        // Retrieve the stored Merkle root from the Integrity Tree region
        byte[] storedRoot = await _merkleVerifier.GetMerkleRootAsync(inodeNumber, ct);

        // Build the computed Merkle root using the same algorithm as MerkleIntegrityVerifier
        byte[] computedRoot = BuildMerkleRoot(leafChecksums);

        bool matches;
        if (storedRoot is null || storedRoot.Length == 0)
        {
            // No stored root available — cannot confirm integrity
            matches = false;
        }
        else
        {
            matches = CryptographicOperations.FixedTimeEquals(
                computedRoot.AsSpan(), storedRoot.AsSpan(0, Math.Min(storedRoot.Length, computedRoot.Length)));
        }

        return new FileLevelResult(
            rootMatches: matches,
            computedRoot: computedRoot,
            storedRoot: storedRoot ?? Array.Empty<byte>(),
            leafCount: leafChecksums.Count);
    }

    // ── Full chain verification ───────────────────────────────────────────────

    /// <summary>
    /// Runs the full three-level cross-extent integrity chain verification for a file.
    /// </summary>
    /// <param name="inodeNumber">Inode number of the file being verified.</param>
    /// <param name="extents">All extents of the file.</param>
    /// <param name="dataRegionStart">Physical block number where the data region begins.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ChainVerificationResult"/> aggregating results from every executed level
    /// and identifying the first level at which a failure was detected (if any).
    /// </returns>
    /// <remarks>
    /// Verification stops early at any level when <see cref="IntegrityChainConfig.StopOnFirstFailure"/>
    /// is <see langword="true"/> and that level reports a failure. The
    /// <see cref="ChainVerificationResult.FirstFailureLevel"/> property identifies which level
    /// caused the stop.
    /// </remarks>
    public async Task<ChainVerificationResult> VerifyChainAsync(
        long inodeNumber,
        IReadOnlyList<InodeExtent> extents,
        long dataRegionStart,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(extents);

        var sw = Stopwatch.StartNew();

        IntegrityLevel? firstFailureLevel = null;
        bool anyFailed = false;

        // ── Level 1: Block verification ──────────────────────────────────────
        int totalBlocksVerified = 0;
        int totalBlocksFailed = 0;
        var allFailedBlocks = new List<long>();

        foreach (var extent in extents)
        {
            ct.ThrowIfCancellationRequested();

            var blockResult = await VerifyBlocksAsync(dataRegionStart, extent, ct);
            totalBlocksVerified += blockResult.BlocksVerified;
            totalBlocksFailed += blockResult.BlocksFailed;

            if (_config.IncludeBlockDetails)
                allFailedBlocks.AddRange(blockResult.FailedBlockNumbers);
        }

        var aggregatedBlockResult = new BlockLevelResult(
            totalBlocksVerified,
            totalBlocksFailed,
            _config.IncludeBlockDetails ? allFailedBlocks : Array.Empty<long>());

        if (totalBlocksFailed > 0)
        {
            firstFailureLevel = IntegrityLevel.Block;
            anyFailed = true;
            if (_config.StopOnFirstFailure || _config.MaxVerificationLevel <= IntegrityLevel.Block)
            {
                sw.Stop();
                return new ChainVerificationResult(
                    allLevelsPassed: false,
                    firstFailureLevel: firstFailureLevel,
                    blockResult: aggregatedBlockResult,
                    extentResults: Array.Empty<ExtentLevelResult>(),
                    fileResult: null,
                    duration: sw.Elapsed);
            }
        }

        if (_config.MaxVerificationLevel < IntegrityLevel.Extent)
        {
            sw.Stop();
            return new ChainVerificationResult(
                allLevelsPassed: !anyFailed,
                firstFailureLevel: firstFailureLevel,
                blockResult: aggregatedBlockResult,
                extentResults: Array.Empty<ExtentLevelResult>(),
                fileResult: null,
                duration: sw.Elapsed);
        }

        // ── Level 2: Extent verification ─────────────────────────────────────
        var extentResults = new List<ExtentLevelResult>(extents.Count);
        bool anyExtentFailed = false;

        foreach (var extent in extents)
        {
            ct.ThrowIfCancellationRequested();

            // InodeExtent does not carry ExpectedHash; pass null to indicate no stored hash.
            // Callers working with SpatioTemporalExtent should call VerifyExtentAsync directly
            // and supply the expected hash.
            var result = await VerifyExtentAsync(extent, null, ct);
            extentResults.Add(result);

            if (!result.HashMatches)
                anyExtentFailed = true;
        }

        if (anyExtentFailed && firstFailureLevel is null)
        {
            firstFailureLevel = IntegrityLevel.Extent;
            anyFailed = true;
        }

        if ((anyExtentFailed && _config.StopOnFirstFailure) || _config.MaxVerificationLevel < IntegrityLevel.File)
        {
            sw.Stop();
            return new ChainVerificationResult(
                allLevelsPassed: !anyFailed,
                firstFailureLevel: firstFailureLevel,
                blockResult: aggregatedBlockResult,
                extentResults: extentResults,
                fileResult: null,
                duration: sw.Elapsed);
        }

        // ── Level 3: File (Merkle) verification ───────────────────────────────
        FileLevelResult? fileResult = null;
        if (_merkleVerifier is not null)
        {
            try
            {
                fileResult = await VerifyFileAsync(inodeNumber, extents, ct);

                if (!fileResult.RootMatches && firstFailureLevel is null)
                {
                    firstFailureLevel = IntegrityLevel.File;
                    anyFailed = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Merkle tree unavailable or corrupt; treat as a file-level failure
                if (firstFailureLevel is null)
                {
                    firstFailureLevel = IntegrityLevel.File;
                    anyFailed = true;
                }

                fileResult = new FileLevelResult(
                    rootMatches: false,
                    computedRoot: Array.Empty<byte>(),
                    storedRoot: Array.Empty<byte>(),
                    leafCount: 0);
            }
        }

        sw.Stop();
        return new ChainVerificationResult(
            allLevelsPassed: !anyFailed,
            firstFailureLevel: firstFailureLevel,
            blockResult: aggregatedBlockResult,
            extentResults: extentResults,
            fileResult: fileResult,
            duration: sw.Elapsed);
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when every byte in <paramref name="data"/> is zero.
    /// </summary>
    private static bool IsAllZeros(byte[] data)
    {
        foreach (byte b in data)
            if (b != 0) return false;
        return true;
    }

    /// <summary>
    /// Returns a copy of <paramref name="hash"/> padded or truncated to exactly
    /// <paramref name="targetLength"/> bytes.
    /// </summary>
    private static byte[] NormalizeHash(byte[]? hash, int targetLength)
    {
        var result = new byte[targetLength];
        if (hash is { Length: > 0 })
            Array.Copy(hash, result, Math.Min(hash.Length, targetLength));
        return result;
    }

    /// <summary>
    /// Builds a Merkle root from leaf checksums using the same algorithm as
    /// <see cref="MerkleIntegrityVerifier"/> (SHA-256 of pairs, bottom-up).
    /// </summary>
    private static byte[] BuildMerkleRoot(List<byte[]> leafChecksums)
    {
        // Each checksum is hashed via SHA-256 to produce the leaf node hash
        var leafHashes = new byte[leafChecksums.Count][];
        for (int i = 0; i < leafChecksums.Count; i++)
            leafHashes[i] = SHA256.HashData(leafChecksums[i]);

        // Pad to next power of two
        int capacity = NextPowerOfTwo(leafHashes.Length);
        int totalNodes = 2 * capacity;

        var nodes = new byte[totalNodes][];
        for (int i = 0; i < totalNodes; i++)
            nodes[i] = new byte[32];

        for (int i = 0; i < leafHashes.Length; i++)
            Buffer.BlockCopy(leafHashes[i], 0, nodes[capacity + i], 0, 32);

        // Build internal nodes bottom-up
        Span<byte> combined = stackalloc byte[64];
        for (int i = capacity - 1; i >= 1; i--)
        {
            nodes[i * 2].AsSpan().CopyTo(combined[..32]);
            nodes[i * 2 + 1].AsSpan().CopyTo(combined[32..]);
            var hash = SHA256.HashData(combined);
            Buffer.BlockCopy(hash, 0, nodes[i], 0, 32);
        }

        return nodes[1]; // root
    }

    /// <summary>Returns the smallest power of two that is &gt;= <paramref name="value"/>.</summary>
    private static int NextPowerOfTwo(int value)
    {
        if (value <= 1) return 1;
        int v = value - 1;
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        return v + 1;
    }
}
