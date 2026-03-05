using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.ModuleManagement;

/// <summary>
/// Progress report for an extent-aware VDE copy operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Copy progress (OMA-03)")]
public readonly record struct CopyProgress
{
    /// <summary>Total blocks in the source VDE.</summary>
    public long TotalBlocks { get; init; }

    /// <summary>Blocks copied so far.</summary>
    public long CopiedBlocks { get; init; }

    /// <summary>Blocks skipped (unallocated) so far.</summary>
    public long SkippedBlocks { get; init; }

    /// <summary>Percentage of copy complete (0.0 to 100.0).</summary>
    public double PercentComplete { get; init; }

    /// <summary>Current copy speed in bytes per second.</summary>
    public long BytesPerSecond { get; init; }
}

/// <summary>
/// Result of an extent-aware VDE copy operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Copy result (OMA-03)")]
public readonly record struct CopyResult
{
    /// <summary>True if copy completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Error message if copy failed, null on success.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Full path of the destination VDE file.</summary>
    public string DestinationPath { get; init; }

    /// <summary>Number of allocated blocks copied.</summary>
    public long BlocksCopied { get; init; }

    /// <summary>Number of unallocated blocks skipped.</summary>
    public long BlocksSkipped { get; init; }

    /// <summary>Total duration of the copy.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Ratio of total blocks to blocks actually copied. A 1TB VDE with 10GB data
    /// would have SpeedupOverNaive of ~100x since only ~1% of blocks are copied.
    /// </summary>
    public double SpeedupOverNaive { get; init; }

    internal static CopyResult Succeeded(string destinationPath, long copied, long skipped, TimeSpan duration)
    {
        double speedup = copied > 0 ? (double)(copied + skipped) / copied : 1.0;
        return new CopyResult
        {
            Success = true,
            DestinationPath = destinationPath,
            BlocksCopied = copied,
            BlocksSkipped = skipped,
            Duration = duration,
            SpeedupOverNaive = speedup,
        };
    }

    internal static CopyResult Failed(string error) =>
        new() { Success = false, ErrorMessage = error, DestinationPath = string.Empty };
}

/// <summary>
/// Extent-aware bulk VDE copy for creating a new VDE with a desired module configuration
/// (Option 4 of online module addition). Instead of copying the entire file byte-by-byte,
/// this engine reads the source allocation bitmap and only copies allocated blocks,
/// skipping sparse/unallocated regions entirely.
///
/// Key optimization: A 1TB VDE with 10GB of actual data copies ~10GB rather than 1TB.
/// The <see cref="CopyResult.SpeedupOverNaive"/> quantifies the improvement.
///
/// Thread safety: NOT thread-safe. One copy operation at a time per instance.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Extent-aware VDE copy engine (OMA-03)")]
public sealed class ExtentAwareVdeCopy
{
    private readonly Stream _sourceStream;
    private readonly int _blockSize;

    /// <summary>Progress callback invoked during copy.</summary>
    public Action<CopyProgress>? OnProgress { get; set; }

    /// <summary>
    /// Maximum I/O operations per second. 0 = unlimited, positive = throttle to prevent
    /// I/O saturation on production systems during background copy.
    /// </summary>
    public int MaxIopsPerSecond { get; set; }

    /// <summary>Number of blocks to copy in a single I/O batch for sequential efficiency.</summary>
    private const int BatchBlockCount = 16;

    /// <summary>
    /// Creates a new ExtentAwareVdeCopy engine.
    /// </summary>
    /// <param name="sourceStream">The source VDE stream to copy from.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public ExtentAwareVdeCopy(Stream sourceStream, int blockSize)
    {
        _sourceStream = sourceStream ?? throw new ArgumentNullException(nameof(sourceStream));
        if (blockSize < FormatConstants.MinBlockSize || blockSize > FormatConstants.MaxBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        _blockSize = blockSize;
    }

    /// <summary>
    /// Creates a new VDE at the destination path with the given module added, then copies
    /// only the allocated data blocks from the source. Metadata blocks are recreated by
    /// <see cref="VdeCreator"/> with the new module configuration. Inodes are migrated
    /// from old to new layout with the new module's fields zero-filled.
    /// </summary>
    /// <param name="destinationPath">Full path for the new VDE file.</param>
    /// <param name="moduleToAdd">The module to add to the VDE.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Copy result with statistics.</returns>
    public async Task<CopyResult> CopyToNewVdeAsync(string destinationPath, ModuleId moduleToAdd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            return CopyResult.Failed("Destination path cannot be empty.");

        var sw = Stopwatch.StartNew();

        // Step 1: Read source superblock
        var sourceSuperblock = await ReadSuperblockAsync(_sourceStream, ct);
        uint currentManifest = sourceSuperblock.ModuleManifest;

        if (ModuleRegistry.IsModuleActive(currentManifest, moduleToAdd))
            return CopyResult.Failed($"Module {moduleToAdd} is already active in the source VDE.");

        uint newManifest = currentManifest | (1u << (int)moduleToAdd);
        long totalBlocks = sourceSuperblock.TotalBlocks;
        var oldLayout = InodeLayoutDescriptor.Create(currentManifest);
        var newLayout = InodeLayoutDescriptor.Create(newManifest);

        // Step 2: Create new VDE at destination with the new manifest
        var profile = new VdeCreationProfile
        {
            BlockSize = _blockSize,
            TotalBlocks = totalBlocks,
            ModuleManifest = newManifest,
            ThinProvisioned = true,
        };

        await VdeCreator.CreateVdeAsync(destinationPath, profile, ct);

        // Step 3: Open destination stream
        await using var destStream = new FileStream(destinationPath, FileMode.Open, FileAccess.ReadWrite,
            FileShare.None, bufferSize: _blockSize * BatchBlockCount, FileOptions.Asynchronous);

        // Step 4: Read source allocation bitmap to identify allocated blocks
        var sourceRegionDir = await ReadRegionDirectoryAsync(_sourceStream, ct);
        int bitmapSlot = sourceRegionDir.FindRegion(BlockTypeTags.BMAP);
        if (bitmapSlot < 0)
            return CopyResult.Failed("Could not locate allocation bitmap in source VDE.");

        var bitmapPointer = sourceRegionDir.GetSlot(bitmapSlot);
        var scanner = new FreeSpaceScanner(_sourceStream, _blockSize,
            bitmapPointer.StartBlock, bitmapPointer.BlockCount);

        // Get the set of metadata block ranges that were recreated by VdeCreator
        var destRegionDir = await ReadRegionDirectoryAsync(destStream, ct);
        var metadataBlocks = CollectMetadataBlockRanges(destRegionDir);

        // Step 5: Copy allocated data blocks (skipping metadata and unallocated)
        // P2-866: Use async overload to avoid blocking thread-pool on network-backed streams.
        var allocatedBitmap = await ReadBitmapFlagsAsync(scanner, totalBlocks, ct).ConfigureAwait(false);
        long blocksCopied = 0;
        long blocksSkipped = 0;
        long opsCompleted = 0;

        var copyBuffer = ArrayPool<byte>.Shared.Rent(_blockSize * BatchBlockCount);
        try
        {
            long blockIndex = 0;
            while (blockIndex < totalBlocks)
            {
                ct.ThrowIfCancellationRequested();

                // Determine how many contiguous allocated blocks we can batch
                if (!allocatedBitmap[blockIndex] || IsMetadataBlock(blockIndex, metadataBlocks))
                {
                    blocksSkipped++;
                    blockIndex++;
                    continue;
                }

                // Find batch of contiguous allocated non-metadata blocks
                int batchSize = 0;
                while (blockIndex + batchSize < totalBlocks
                    && batchSize < BatchBlockCount
                    && allocatedBitmap[blockIndex + batchSize]
                    && !IsMetadataBlock(blockIndex + batchSize, metadataBlocks))
                {
                    batchSize++;
                }

                // Copy batch
                int bytesToCopy = batchSize * _blockSize;
                _sourceStream.Seek(blockIndex * _blockSize, SeekOrigin.Begin);
                await ReadExactAsync(_sourceStream, copyBuffer, bytesToCopy, ct);

                destStream.Seek(blockIndex * _blockSize, SeekOrigin.Begin);
                await destStream.WriteAsync(copyBuffer.AsMemory(0, bytesToCopy), ct);

                blocksCopied += batchSize;
                opsCompleted++;
                blockIndex += batchSize;

                // Throttle if needed
                await ThrottleIfNeeded(sw, opsCompleted, ct);

                // Report progress periodically
                if (opsCompleted % 100 == 0 || blockIndex >= totalBlocks)
                {
                    long elapsedMs = Math.Max(1, sw.ElapsedMilliseconds);
                    long bytesPerSecond = blocksCopied * _blockSize * 1000L / elapsedMs;

                    OnProgress?.Invoke(new CopyProgress
                    {
                        TotalBlocks = totalBlocks,
                        CopiedBlocks = blocksCopied,
                        SkippedBlocks = blocksSkipped,
                        PercentComplete = totalBlocks > 0
                            ? (double)(blocksCopied + blocksSkipped) / totalBlocks * 100.0
                            : 100.0,
                        BytesPerSecond = bytesPerSecond,
                    });
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copyBuffer);
        }

        // Step 6: Migrate inodes from source to destination
        await MigrateInodesAsync(sourceRegionDir, destRegionDir, oldLayout, newLayout, destStream, ct);

        // Step 7: Update destination superblock with source metadata
        await UpdateDestinationSuperblockAsync(destStream, sourceSuperblock, newManifest,
            (ushort)newLayout.InodeSize, ct);

        await destStream.FlushAsync(ct);

        return CopyResult.Succeeded(destinationPath, blocksCopied, blocksSkipped, sw.Elapsed);
    }

    /// <summary>
    /// Estimates the number of allocated blocks that would be copied.
    /// Reads the bitmap without performing any writes.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Count of allocated blocks.</returns>
    public async Task<long> EstimateCopyBlocksAsync(CancellationToken ct)
    {
        var superblock = await ReadSuperblockAsync(_sourceStream, ct);
        long totalBlocks = superblock.TotalBlocks;

        var regionDir = await ReadRegionDirectoryAsync(_sourceStream, ct);
        int bitmapSlot = regionDir.FindRegion(BlockTypeTags.BMAP);
        if (bitmapSlot < 0) return totalBlocks; // Can't read bitmap, assume full copy

        var bitmapPointer = regionDir.GetSlot(bitmapSlot);
        var scanner = new FreeSpaceScanner(_sourceStream, _blockSize,
            bitmapPointer.StartBlock, bitmapPointer.BlockCount);

        // P2-866: Use async overload to avoid blocking thread-pool on network-backed streams.
        long freeBlocks = await scanner.GetTotalFreeBlocksAsync(ct).ConfigureAwait(false);
        return totalBlocks - freeBlocks;
    }

    // ── Private Helpers ────────────────────────────────────────────────────

    /// <summary>
    /// IOPS throttling: if MaxIopsPerSecond is set, delay to maintain target rate.
    /// </summary>
    private async Task ThrottleIfNeeded(Stopwatch sw, long opsCompleted, CancellationToken ct)
    {
        if (MaxIopsPerSecond <= 0) return;
        double targetElapsedMs = (double)opsCompleted / MaxIopsPerSecond * 1000;
        double actualElapsedMs = sw.Elapsed.TotalMilliseconds;
        if (actualElapsedMs < targetElapsedMs)
            await Task.Delay(TimeSpan.FromMilliseconds(targetElapsedMs - actualElapsedMs), ct);
    }

    /// <summary>
    /// Reads the allocation bitmap into a bool array indicating per-block allocation status.
    /// Async to avoid blocking thread-pool on network-backed streams (P2-866).
    /// </summary>
    private async Task<bool[]> ReadBitmapFlagsAsync(FreeSpaceScanner scanner, long totalBlocks, CancellationToken ct)
    {
        // Start with all blocks allocated, then mark free ranges
        var flags = new bool[totalBlocks];
        Array.Fill(flags, true);

        var freeRanges = await scanner.FindAllFreeRangesAsync(1, ct).ConfigureAwait(false);
        foreach (var range in freeRanges)
        {
            for (long i = 0; i < range.BlockCount && range.StartBlock + i < totalBlocks; i++)
            {
                flags[range.StartBlock + i] = false;
            }
        }

        return flags;
    }

    /// <summary>
    /// Collects metadata block ranges from the destination region directory.
    /// These blocks were already created by VdeCreator and should not be overwritten.
    /// </summary>
    private static List<(long Start, long Count)> CollectMetadataBlockRanges(RegionDirectory regionDir)
    {
        var ranges = new List<(long Start, long Count)>();

        // Always include superblock groups and region directory
        ranges.Add((0, FormatConstants.SuperblockGroupBlocks)); // Primary SB group
        ranges.Add((FormatConstants.SuperblockMirrorStartBlock, FormatConstants.SuperblockGroupBlocks)); // Mirror SB group
        ranges.Add((FormatConstants.RegionDirectoryStartBlock, FormatConstants.RegionDirectoryBlocks)); // Region directory

        // Add all active regions from the directory (bitmap, inode table, WAL, module regions)
        var activeRegions = regionDir.GetActiveRegions();
        foreach (var (_, pointer) in activeRegions)
        {
            // Skip data region
            if (pointer.RegionTypeId == BlockTypeTags.DATA)
                continue;

            ranges.Add((pointer.StartBlock, pointer.BlockCount));
        }

        return ranges;
    }

    /// <summary>
    /// Checks if a block falls within any metadata block range.
    /// </summary>
    private static bool IsMetadataBlock(long blockNumber, List<(long Start, long Count)> ranges)
    {
        foreach (var (start, count) in ranges)
        {
            if (blockNumber >= start && blockNumber < start + count)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Migrates all inodes from the source to destination with the new layout.
    /// Source inodes are read with the old layout descriptor and written to destination
    /// with the new layout descriptor. New module fields are zero-filled.
    /// Extent references (block numbers) remain valid since data blocks are at the same positions.
    /// </summary>
    private async Task MigrateInodesAsync(
        RegionDirectory sourceRegionDir, RegionDirectory destRegionDir,
        InodeLayoutDescriptor oldLayout, InodeLayoutDescriptor newLayout,
        Stream destStream, CancellationToken ct)
    {
        int sourceInodeSlot = sourceRegionDir.FindRegion(BlockTypeTags.INOD);
        int destInodeSlot = destRegionDir.FindRegion(BlockTypeTags.INOD);
        if (sourceInodeSlot < 0 || destInodeSlot < 0) return;

        var sourceInodePointer = sourceRegionDir.GetSlot(sourceInodeSlot);
        var destInodePointer = destRegionDir.GetSlot(destInodeSlot);

        long totalInodes = (sourceInodePointer.BlockCount * _blockSize) / oldLayout.InodeSize;

        var readBuffer = ArrayPool<byte>.Shared.Rent(oldLayout.InodeSize);
        var writeBuffer = ArrayPool<byte>.Shared.Rent(newLayout.InodeSize);
        try
        {
            for (long i = 0; i < totalInodes; i++)
            {
                ct.ThrowIfCancellationRequested();

                // Read source inode
                long srcOffset = sourceInodePointer.StartBlock * _blockSize + i * oldLayout.InodeSize;
                _sourceStream.Seek(srcOffset, SeekOrigin.Begin);
                await ReadExactAsync(_sourceStream, readBuffer, oldLayout.InodeSize, ct);

                // Deserialize with old layout
                var inode = InodeV2.Deserialize(readBuffer.AsSpan(0, oldLayout.InodeSize), oldLayout);

                // Skip empty inodes (inode number 0 and no data)
                if (inode.InodeNumber == 0 && inode.Size == 0 && inode.Type == 0)
                    continue;

                // Serialize with new layout (new module fields zero-filled)
                Array.Clear(writeBuffer, 0, newLayout.InodeSize);
                InodeV2.SerializeToSpan(inode, newLayout, writeBuffer.AsSpan(0, newLayout.InodeSize));

                // Write to destination
                long dstOffset = destInodePointer.StartBlock * _blockSize + i * newLayout.InodeSize;
                destStream.Seek(dstOffset, SeekOrigin.Begin);
                await destStream.WriteAsync(writeBuffer.AsMemory(0, newLayout.InodeSize), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            ArrayPool<byte>.Shared.Return(writeBuffer);
        }
    }

    /// <summary>
    /// Updates the destination superblock with source metadata (checkpoint sequence, timestamps, etc.)
    /// while preserving the new manifest and inode size.
    /// </summary>
    private async Task UpdateDestinationSuperblockAsync(
        Stream destStream, SuperblockV2 sourceSb, uint newManifest, ushort newInodeSize,
        CancellationToken ct)
    {
        // Read destination's current superblock (created by VdeCreator)
        var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            destStream.Seek(0, SeekOrigin.Begin);
            await ReadExactAsync(destStream, buffer, _blockSize, ct);
            var destSb = SuperblockV2.Deserialize(buffer.AsSpan(0, _blockSize), _blockSize);

            // Create updated superblock preserving source metadata
            var updatedSb = new SuperblockV2(
                magic: destSb.Magic,
                versionInfo: destSb.VersionInfo,
                moduleManifest: newManifest,
                moduleConfig: destSb.ModuleConfig,
                moduleConfigExt: destSb.ModuleConfigExt,
                blockSize: _blockSize,
                totalBlocks: destSb.TotalBlocks,
                freeBlocks: destSb.FreeBlocks,
                expectedFileSize: destSb.ExpectedFileSize,
                totalAllocatedBlocks: destSb.TotalAllocatedBlocks,
                volumeUuid: destSb.VolumeUuid,
                clusterNodeId: sourceSb.ClusterNodeId,
                defaultCompressionAlgo: sourceSb.DefaultCompressionAlgo,
                defaultEncryptionAlgo: sourceSb.DefaultEncryptionAlgo,
                defaultChecksumAlgo: sourceSb.DefaultChecksumAlgo,
                inodeSize: newInodeSize,
                policyVersion: sourceSb.PolicyVersion,
                replicationEpoch: sourceSb.ReplicationEpoch,
                wormHighWaterMark: sourceSb.WormHighWaterMark,
                encryptionKeyFingerprint: sourceSb.EncryptionKeyFingerprint,
                sovereigntyZoneId: sourceSb.SovereigntyZoneId,
                volumeLabel: sourceSb.VolumeLabel,
                createdTimestampUtc: sourceSb.CreatedTimestampUtc,
                modifiedTimestampUtc: DateTimeOffset.UtcNow.Ticks,
                lastScrubTimestamp: sourceSb.LastScrubTimestamp,
                checkpointSequence: sourceSb.CheckpointSequence + 1,
                errorMapBlockCount: sourceSb.ErrorMapBlockCount,
                lastWriterSessionId: sourceSb.LastWriterSessionId,
                lastWriterTimestamp: DateTimeOffset.UtcNow.Ticks,
                lastWriterNodeId: sourceSb.LastWriterNodeId,
                physicalAllocatedBlocks: sourceSb.PhysicalAllocatedBlocks,
                headerIntegritySeal: sourceSb.HeaderIntegritySeal);

            // Write to both primary and mirror superblock blocks
            Array.Clear(buffer, 0, _blockSize);
            SuperblockV2.Serialize(updatedSb, buffer.AsSpan(0, _blockSize), _blockSize);
            UniversalBlockTrailer.Write(buffer.AsSpan(0, _blockSize), _blockSize, BlockTypeTags.SUPB, 1);

            destStream.Seek(0, SeekOrigin.Begin);
            await destStream.WriteAsync(buffer.AsMemory(0, _blockSize), ct);

            destStream.Seek(FormatConstants.SuperblockMirrorStartBlock * _blockSize, SeekOrigin.Begin);
            await destStream.WriteAsync(buffer.AsMemory(0, _blockSize), ct);

            await destStream.FlushAsync(ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<SuperblockV2> ReadSuperblockAsync(Stream stream, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            stream.Seek(0, SeekOrigin.Begin);
            await ReadExactAsync(stream, buffer, _blockSize, ct);
            return SuperblockV2.Deserialize(buffer.AsSpan(0, _blockSize), _blockSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<RegionDirectory> ReadRegionDirectoryAsync(Stream stream, CancellationToken ct)
    {
        int totalSize = FormatConstants.RegionDirectoryBlocks * _blockSize;
        var buffer = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            stream.Seek(FormatConstants.RegionDirectoryStartBlock * _blockSize, SeekOrigin.Begin);
            await ReadExactAsync(stream, buffer, totalSize, ct);
            return RegionDirectory.Deserialize(buffer.AsSpan(0, totalSize), _blockSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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
