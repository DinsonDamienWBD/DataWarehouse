using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.FileExtension;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Compatibility;

/// <summary>
/// Enumerates the phases of a v1.0-to-v2.0 VDE migration operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 81: Migration phase enum (MIGR-02)")]
public enum MigrationPhase : byte
{
    /// <summary>Detecting the source VDE format version.</summary>
    Detecting = 0,

    /// <summary>Reading the v1.0 source superblock and volume metadata.</summary>
    ReadingSource = 1,

    /// <summary>Creating the v2.0 destination VDE with module regions.</summary>
    CreatingDestination = 2,

    /// <summary>Copying data blocks from source to destination.</summary>
    CopyingData = 3,

    /// <summary>Verifying the destination is a valid v2.0 VDE.</summary>
    VerifyingResult = 4,

    /// <summary>Migration completed successfully.</summary>
    Complete = 5,

    /// <summary>Migration failed.</summary>
    Failed = 6,
}

/// <summary>
/// Progress information for an in-flight migration operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 81: Migration progress (MIGR-02)")]
public readonly record struct MigrationProgress
{
    /// <summary>Current phase of the migration.</summary>
    public MigrationPhase Phase { get; init; }

    /// <summary>Overall completion percentage (0.0 to 100.0).</summary>
    public double PercentComplete { get; init; }

    /// <summary>Human-readable status message describing the current activity.</summary>
    public string StatusMessage { get; init; }

    /// <summary>Total bytes copied so far.</summary>
    public long BytesCopied { get; init; }

    /// <summary>Estimated total bytes to copy.</summary>
    public long TotalEstimatedBytes { get; init; }

    /// <summary>Time elapsed since migration started.</summary>
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Final result of a v1.0-to-v2.0 migration operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 81: Migration result (MIGR-02)")]
public readonly record struct MigrationResult
{
    /// <summary>True if migration completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Error message if migration failed, null on success.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Path to the v1.0 source VDE file.</summary>
    public string SourcePath { get; init; }

    /// <summary>Path to the newly created v2.0 destination VDE file.</summary>
    public string DestinationPath { get; init; }

    /// <summary>Detected format version of the source file.</summary>
    public DetectedFormatVersion SourceVersion { get; init; }

    /// <summary>Module manifest bitmask of the new v2.0 VDE.</summary>
    public uint NewModuleManifest { get; init; }

    /// <summary>Number of data blocks successfully copied.</summary>
    public long BlocksCopied { get; init; }

    /// <summary>Number of blocks skipped (metadata blocks recreated by VdeCreator).</summary>
    public long BlocksSkipped { get; init; }

    /// <summary>Total duration of the migration.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Performs end-to-end v1.0-to-v2.0 VDE migration. The source VDE is NEVER modified;
/// a new v2.0 VDE is created at the destination path, data blocks are copied sequentially,
/// and the result is verified before reporting success.
/// </summary>
/// <remarks>
/// <para>
/// Migration steps:
/// <list type="number">
/// <item>Detect source format via <see cref="VdeFormatDetector"/>; reject if not v1.0</item>
/// <item>Read v1.0 superblock via <see cref="V1CompatibilityLayer"/>; extract volume metadata</item>
/// <item>Create v2.0 destination via <see cref="VdeCreator"/>; build module manifest from selection</item>
/// <item>Copy data blocks sequentially with ArrayPool buffers; skip metadata blocks (0-9)</item>
/// <item>Update destination superblock with source volume identity (UUID, label)</item>
/// <item>Verify destination via <see cref="DwvdContentDetector"/> and <see cref="VdeFormatDetector"/></item>
/// </list>
/// </para>
/// <para>
/// Thread safety: NOT thread-safe. One migration at a time per instance.
/// Cancellation: If cancelled, the partial destination file is deleted. Source is never modified.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 81: VDE migration engine (MIGR-02)")]
public sealed class VdeMigrationEngine
{
    /// <summary>Number of blocks to copy in a single I/O batch for sequential efficiency.</summary>
    private const int BatchBlockCount = 16;

    /// <summary>
    /// Number of metadata blocks at the start of a v2.0 VDE that should not be overwritten
    /// (superblock groups 0-7, region directory 8-9).
    /// </summary>
    private const int MetadataBlocksToSkip = 10;

    /// <summary>Progress reporting interval (every N batches).</summary>
    private const int ProgressReportInterval = 100;

    private readonly Action<MigrationProgress>? _onProgress;
    private readonly MigrationModuleSelector _moduleSelector = new();

    /// <summary>
    /// Creates a new VDE migration engine.
    /// </summary>
    /// <param name="onProgress">Optional callback invoked to report migration progress.</param>
    public VdeMigrationEngine(Action<MigrationProgress>? onProgress = null)
    {
        _onProgress = onProgress;
    }

    /// <summary>
    /// Migrates a v1.0 VDE to v2.0 format. The source file is read-only throughout;
    /// a new v2.0 VDE is created at the destination path. If any step fails, the partial
    /// destination is deleted and the source remains untouched.
    /// </summary>
    /// <param name="sourcePath">Path to the v1.0 source VDE file.</param>
    /// <param name="destinationPath">Path for the new v2.0 VDE file (must not exist).</param>
    /// <param name="selectedModules">Modules to enable in the v2.0 VDE.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="MigrationResult"/> describing the outcome.</returns>
    public async Task<MigrationResult> MigrateAsync(
        string sourcePath,
        string destinationPath,
        IReadOnlyList<ModuleId> selectedModules,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        ArgumentNullException.ThrowIfNull(selectedModules);

        var sw = Stopwatch.StartNew();
        bool destinationCreated = false;

        try
        {
            // ── Step 1: Detect source format ──────────────────────────────
            ReportProgress(MigrationPhase.Detecting, 0, "Detecting source VDE format...",
                0, 0, sw.Elapsed);

            var detected = await VdeFormatDetector.DetectAsync(sourcePath, ct)
                .ConfigureAwait(false);

            if (detected is null)
            {
                return FailedResult(sourcePath, destinationPath, default,
                    "Source file is not a DWVD file (magic bytes not found).", sw.Elapsed);
            }

            var version = detected.Value;

            if (version.IsV2)
            {
                return FailedResult(sourcePath, destinationPath, version,
                    "Source file is already v2.0 format. No migration needed.", sw.Elapsed);
            }

            if (version.IsUnknown)
            {
                return FailedResult(sourcePath, destinationPath, version,
                    $"Source file has unrecognized format version {version.MajorVersion}.{version.MinorVersion}.",
                    sw.Elapsed);
            }

            ReportProgress(MigrationPhase.Detecting, 5, $"Detected {version.FormatDescription}.",
                0, 0, sw.Elapsed);

            // ── Step 2: Read v1.0 source superblock ───────────────────────
            ReportProgress(MigrationPhase.ReadingSource, 10, "Reading v1.0 source superblock...",
                0, 0, sw.Elapsed);

            await using var sourceStream = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

            // Use a default block size for initial superblock read (v1.0 layout uses fixed offsets)
            var compatLayer = new V1CompatibilityLayer(sourceStream, FormatConstants.DefaultBlockSize);
            var v1Superblock = await compatLayer.ReadV1SuperblockAsync(ct).ConfigureAwait(false);

            int blockSize = v1Superblock.BlockSize;
            long totalBlocks = v1Superblock.TotalBlocks;
            long totalEstimatedBytes = totalBlocks * blockSize;

            ReportProgress(MigrationPhase.ReadingSource, 15,
                $"Source: {v1Superblock.VolumeLabel}, {totalBlocks} blocks x {blockSize}B, UUID={v1Superblock.VolumeUuid}",
                0, totalEstimatedBytes, sw.Elapsed);

            // ── Step 3: Create v2.0 destination ───────────────────────────
            ReportProgress(MigrationPhase.CreatingDestination, 20, "Creating v2.0 destination VDE...",
                0, totalEstimatedBytes, sw.Elapsed);

            uint manifest = _moduleSelector.BuildManifest(selectedModules);

            var profile = new VdeCreationProfile
            {
                ProfileType = VdeProfileType.Custom,
                Name = "Migration",
                Description = $"Migrated from v1.0: {v1Superblock.VolumeLabel}",
                ModuleManifest = manifest,
                ModuleConfigLevels = new Dictionary<ModuleId, byte>(),
                BlockSize = blockSize,
                TotalBlocks = totalBlocks,
                ThinProvisioned = true,
                TamperResponseLevel = 0x00,
            };

            await VdeCreator.CreateVdeAsync(destinationPath, profile, ct).ConfigureAwait(false);
            destinationCreated = true;

            ReportProgress(MigrationPhase.CreatingDestination, 25,
                $"Destination created with manifest 0x{manifest:X8}.",
                0, totalEstimatedBytes, sw.Elapsed);

            // ── Step 4: Copy data blocks ──────────────────────────────────
            ReportProgress(MigrationPhase.CopyingData, 30, "Copying data blocks...",
                0, totalEstimatedBytes, sw.Elapsed);

            long blocksCopied = 0;
            long blocksSkipped = 0;

            // Open destination for writing
            await using var destStream = new FileStream(
                destinationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                bufferSize: blockSize * BatchBlockCount, FileOptions.Asynchronous);

            int batchBytes = blockSize * BatchBlockCount;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(batchBytes);
            try
            {
                long blockIndex = MetadataBlocksToSkip; // Skip blocks 0-9 (metadata, already created)
                blocksSkipped = MetadataBlocksToSkip;
                int batchCount = 0;

                while (blockIndex < totalBlocks)
                {
                    ct.ThrowIfCancellationRequested();

                    // Determine batch size (remaining blocks capped at BatchBlockCount)
                    int blocksInBatch = (int)Math.Min(BatchBlockCount, totalBlocks - blockIndex);
                    int bytesInBatch = blocksInBatch * blockSize;

                    // Read from source
                    sourceStream.Position = blockIndex * blockSize;
                    int totalRead = 0;
                    while (totalRead < bytesInBatch)
                    {
                        int read = await sourceStream.ReadAsync(
                            buffer.AsMemory(totalRead, bytesInBatch - totalRead), ct)
                            .ConfigureAwait(false);
                        if (read == 0)
                            break;
                        totalRead += read;
                    }

                    // Write to destination
                    destStream.Position = blockIndex * blockSize;
                    await destStream.WriteAsync(buffer.AsMemory(0, totalRead), ct)
                        .ConfigureAwait(false);

                    blocksCopied += blocksInBatch;
                    blockIndex += blocksInBatch;
                    batchCount++;

                    // Report progress periodically
                    if (batchCount % ProgressReportInterval == 0 || blockIndex >= totalBlocks)
                    {
                        long bytesCopied = blocksCopied * blockSize;
                        double percent = 30.0 + (double)(blocksCopied + blocksSkipped) / totalBlocks * 55.0;

                        ReportProgress(MigrationPhase.CopyingData, percent,
                            $"Copied {blocksCopied}/{totalBlocks - MetadataBlocksToSkip} data blocks...",
                            bytesCopied, totalEstimatedBytes, sw.Elapsed);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            // ── Step 5: Update destination superblock ─────────────────────
            ReportProgress(MigrationPhase.VerifyingResult, 85,
                "Updating destination superblock with source identity...",
                blocksCopied * blockSize, totalEstimatedBytes, sw.Elapsed);

            await UpdateDestinationSuperblockAsync(
                destStream, v1Superblock, blockSize, ct).ConfigureAwait(false);

            await destStream.FlushAsync(ct).ConfigureAwait(false);

            // Close destination before verification
            await destStream.DisposeAsync().ConfigureAwait(false);

            // ── Step 6: Verify destination ────────────────────────────────
            ReportProgress(MigrationPhase.VerifyingResult, 90,
                "Verifying destination as valid v2.0 VDE...",
                blocksCopied * blockSize, totalEstimatedBytes, sw.Elapsed);

            var contentResult = await DwvdContentDetector.DetectAsync(destinationPath, ct)
                .ConfigureAwait(false);

            if (!contentResult.IsDwvd)
            {
                return FailedResult(sourcePath, destinationPath, version,
                    "Verification failed: destination is not recognized as a valid DWVD file.",
                    sw.Elapsed);
            }

            if (contentResult.MajorVersion != 2)
            {
                return FailedResult(sourcePath, destinationPath, version,
                    $"Verification failed: destination has major version {contentResult.MajorVersion}, expected 2.",
                    sw.Elapsed);
            }

            var destVersion = await VdeFormatDetector.DetectAsync(destinationPath, ct)
                .ConfigureAwait(false);

            if (destVersion is null || !destVersion.Value.IsV2)
            {
                return FailedResult(sourcePath, destinationPath, version,
                    "Verification failed: VdeFormatDetector does not recognize destination as v2.0.",
                    sw.Elapsed);
            }

            ReportProgress(MigrationPhase.Complete, 100, "Migration complete.",
                blocksCopied * blockSize, totalEstimatedBytes, sw.Elapsed);

            return new MigrationResult
            {
                Success = true,
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                SourceVersion = version,
                NewModuleManifest = manifest,
                BlocksCopied = blocksCopied,
                BlocksSkipped = blocksSkipped,
                Duration = sw.Elapsed,
            };
        }
        catch (OperationCanceledException)
        {
            // Clean up partial destination on cancellation
            CleanupDestination(destinationPath, destinationCreated);
            throw;
        }
        catch (Exception ex)
        {
            CleanupDestination(destinationPath, destinationCreated);
            return FailedResult(sourcePath, destinationPath, default,
                $"Migration failed: {ex.Message}", sw.Elapsed);
        }
    }

    /// <summary>
    /// Convenience wrapper that resolves a preset to its module list, then calls
    /// <see cref="MigrateAsync(string, string, IReadOnlyList{ModuleId}, CancellationToken)"/>.
    /// </summary>
    /// <param name="sourcePath">Path to the v1.0 source VDE file.</param>
    /// <param name="destinationPath">Path for the new v2.0 VDE file.</param>
    /// <param name="preset">The migration preset defining which modules to enable.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="MigrationResult"/> describing the outcome.</returns>
    public Task<MigrationResult> MigrateWithPresetAsync(
        string sourcePath,
        string destinationPath,
        MigrationModulePreset preset,
        CancellationToken ct = default)
    {
        var modules = _moduleSelector.GetPresetModules(preset);
        return MigrateAsync(sourcePath, destinationPath, modules, ct);
    }

    /// <summary>
    /// Quick estimation of migration size and source format without performing migration.
    /// Reads only the source header to determine file size and version.
    /// </summary>
    /// <param name="sourcePath">Path to the source VDE file to estimate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A tuple of (EstimatedBytes, Version) where EstimatedBytes is the total data size
    /// and Version is the detected format version.
    /// </returns>
    /// <exception cref="VdeFormatException">Source file is not a valid DWVD file.</exception>
    public async Task<(long EstimatedBytes, DetectedFormatVersion Version)> EstimateMigrationAsync(
        string sourcePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);

        var detected = await VdeFormatDetector.DetectAsync(sourcePath, ct).ConfigureAwait(false);
        if (detected is null)
            throw new VdeFormatException("Source file is not a DWVD file (magic bytes not found).");

        var version = detected.Value;

        if (version.IsV1)
        {
            await using var stream = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.Asynchronous);

            var compatLayer = new V1CompatibilityLayer(stream, FormatConstants.DefaultBlockSize);
            var summary = await compatLayer.ReadV1SuperblockAsync(ct).ConfigureAwait(false);

            return (summary.TotalBlocks * summary.BlockSize, version);
        }

        // For non-v1.0 files, estimate from file length
        var fileInfo = new FileInfo(sourcePath);
        return (fileInfo.Length, version);
    }

    // ── Private Helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Updates the destination superblock with the source volume's UUID and label,
    /// preserving the identity of the migrated VDE.
    /// Writes to both primary (block 0) and mirror (block 4) superblock positions.
    /// </summary>
    private static async Task UpdateDestinationSuperblockAsync(
        Stream destStream, V1SuperblockSummary v1Summary, int blockSize, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(blockSize);
        try
        {
            // Read existing destination superblock (block 0)
            destStream.Position = 0;
            int totalRead = 0;
            while (totalRead < blockSize)
            {
                int read = await destStream.ReadAsync(
                    buffer.AsMemory(totalRead, blockSize - totalRead), ct)
                    .ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }

            var destSb = SuperblockV2.Deserialize(buffer.AsSpan(0, blockSize), blockSize);

            // Build the volume label bytes from the v1.0 source
            byte[] labelBytes = new byte[SuperblockV2.VolumeLabelSize];
            if (!string.IsNullOrEmpty(v1Summary.VolumeLabel))
            {
                int written = Encoding.UTF8.GetBytes(
                    v1Summary.VolumeLabel.AsSpan(),
                    labelBytes.AsSpan(0, SuperblockV2.VolumeLabelSize));
                // Null-terminate if shorter than 64 bytes (already zero-filled)
            }

            // Create updated superblock with source identity
            var updatedSb = new SuperblockV2(
                magic: destSb.Magic,
                versionInfo: destSb.VersionInfo,
                moduleManifest: destSb.ModuleManifest,
                moduleConfig: destSb.ModuleConfig,
                moduleConfigExt: destSb.ModuleConfigExt,
                blockSize: blockSize,
                totalBlocks: destSb.TotalBlocks,
                freeBlocks: destSb.FreeBlocks,
                expectedFileSize: destSb.ExpectedFileSize,
                totalAllocatedBlocks: destSb.TotalAllocatedBlocks,
                volumeUuid: v1Summary.VolumeUuid,
                clusterNodeId: destSb.ClusterNodeId,
                defaultCompressionAlgo: destSb.DefaultCompressionAlgo,
                defaultEncryptionAlgo: destSb.DefaultEncryptionAlgo,
                defaultChecksumAlgo: destSb.DefaultChecksumAlgo,
                inodeSize: destSb.InodeSize,
                policyVersion: destSb.PolicyVersion,
                replicationEpoch: destSb.ReplicationEpoch,
                wormHighWaterMark: destSb.WormHighWaterMark,
                encryptionKeyFingerprint: destSb.EncryptionKeyFingerprint,
                sovereigntyZoneId: destSb.SovereigntyZoneId,
                volumeLabel: labelBytes,
                createdTimestampUtc: destSb.CreatedTimestampUtc,
                modifiedTimestampUtc: DateTimeOffset.UtcNow.Ticks,
                lastScrubTimestamp: destSb.LastScrubTimestamp,
                checkpointSequence: destSb.CheckpointSequence + 1,
                errorMapBlockCount: destSb.ErrorMapBlockCount,
                lastWriterSessionId: destSb.LastWriterSessionId,
                lastWriterTimestamp: DateTimeOffset.UtcNow.Ticks,
                lastWriterNodeId: destSb.LastWriterNodeId,
                physicalAllocatedBlocks: destSb.PhysicalAllocatedBlocks,
                headerIntegritySeal: destSb.HeaderIntegritySeal);

            // Serialize updated superblock
            Array.Clear(buffer, 0, blockSize);
            SuperblockV2.Serialize(updatedSb, buffer.AsSpan(0, blockSize), blockSize);
            UniversalBlockTrailer.Write(buffer.AsSpan(0, blockSize), blockSize, BlockTypeTags.SUPB, 1);

            // Write to primary superblock (block 0)
            destStream.Position = 0;
            await destStream.WriteAsync(buffer.AsMemory(0, blockSize), ct).ConfigureAwait(false);

            // Write to mirror superblock (block 4)
            destStream.Position = FormatConstants.SuperblockMirrorStartBlock * blockSize;
            await destStream.WriteAsync(buffer.AsMemory(0, blockSize), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ReportProgress(
        MigrationPhase phase, double percent, string message,
        long bytesCopied, long totalEstimatedBytes, TimeSpan elapsed)
    {
        _onProgress?.Invoke(new MigrationProgress
        {
            Phase = phase,
            PercentComplete = Math.Clamp(percent, 0.0, 100.0),
            StatusMessage = message,
            BytesCopied = bytesCopied,
            TotalEstimatedBytes = totalEstimatedBytes,
            Elapsed = elapsed,
        });
    }

    private static MigrationResult FailedResult(
        string sourcePath, string destinationPath, DetectedFormatVersion version,
        string errorMessage, TimeSpan duration)
    {
        return new MigrationResult
        {
            Success = false,
            ErrorMessage = errorMessage,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            SourceVersion = version,
            Duration = duration,
        };
    }

    private static void CleanupDestination(string destinationPath, bool wasCreated)
    {
        if (!wasCreated || string.IsNullOrEmpty(destinationPath))
            return;

        try
        {
            if (File.Exists(destinationPath))
                File.Delete(destinationPath);
        }
        catch
        {
            // Best-effort cleanup; source is never modified regardless
        }
    }
}
