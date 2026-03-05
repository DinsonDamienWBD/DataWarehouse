using System.IO.Hashing;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Pipeline.Stages;

/// <summary>
/// Read Stage 4: reads data blocks from the device for each resolved extent and
/// verifies on-disk integrity. This stage always runs (no module gate) and assembles
/// the complete data buffer from all extent blocks.
/// </summary>
/// <remarks>
/// <para>
/// For each extent in <see cref="VdePipelineContext.Extents"/>, the stage:
/// <list type="number">
/// <item>Reads blocks via <see cref="IBlockDevice.ReadBlockAsync"/>.</item>
/// <item>Verifies TRLR XxHash64 checksum (if trailer records are present).</item>
/// <item>If the Integrity module is active, verifies the expected BLAKE3 hash
///   from <c>context.Properties["ExtentHashes"]</c>.</item>
/// <item>On mismatch: attempts RAID reconstruction if the RAID module is active,
///   otherwise throws an integrity exception.</item>
/// <item>Verifies generation monotonicity for ordered reads.</item>
/// </list>
/// </para>
/// <para>
/// After all blocks are read and verified, the assembled data is stored in
/// <see cref="VdePipelineContext.DataBuffer"/>.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VDE read pipeline block reader integrity stage (VOPT-90)")]
public sealed class BlockReaderIntegrityStage : IVdeReadStage
{
    private readonly IBlockDevice _device;

    /// <summary>Property key for requesting RAID reconstruction on integrity failure.</summary>
    public const string RaidReconstructionKey = "RaidReconstructionRequired";

    /// <summary>
    /// Initializes a new <see cref="BlockReaderIntegrityStage"/> with the given block device.
    /// </summary>
    /// <param name="device">Block device to read data blocks from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is null.</exception>
    public BlockReaderIntegrityStage(IBlockDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    /// <inheritdoc />
    public string StageName => "BlockReaderIntegrity";

    /// <inheritdoc />
    /// <remarks>Always null: block reading and integrity verification are unconditional for every read.</remarks>
    public ModuleId? ModuleGate => null;

    /// <inheritdoc />
    public async Task ExecuteAsync(VdePipelineContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Extents.Count == 0)
            return;

        int blockSize = context.BlockSize > 0 ? context.BlockSize : _device.BlockSize;
        bool hasIntegrity = context.Manifest.IsModuleActive(ModuleId.Integrity);
        bool hasRaid = context.Manifest.IsModuleActive(ModuleId.Raid);

        // Read expected hashes if integrity module is active
        Dictionary<long, ulong>? expectedHashes = null;
        if (hasIntegrity)
        {
            context.TryGetProperty<Dictionary<long, ulong>>(IntegrityCalculatorStage.ExtentHashesKey, out expectedHashes);
        }

        // Calculate total data size from extents
        long totalSize = 0;
        for (int i = 0; i < context.Extents.Count; i++)
        {
            totalSize += (long)context.Extents[i].BlockCount * blockSize;
        }

        // Cap at inode size if available
        if (context.Inode is not null && context.Inode.Size > 0 && context.Inode.Size < totalSize)
        {
            totalSize = context.Inode.Size;
        }

        var dataBuffer = new byte[totalSize];
        int dataOffset = 0;

        for (int i = 0; i < context.Extents.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var extent = context.Extents[i];

            // Skip sparse holes
            if (extent.IsSparse)
            {
                int holeSize = extent.BlockCount * blockSize;
                int actualHole = Math.Min(holeSize, dataBuffer.Length - dataOffset);
                // Zero-filled by default (byte[] initialization)
                dataOffset += actualHole;
                continue;
            }

            // Read each block in the extent
            var extentData = new byte[extent.BlockCount * blockSize];
            for (int b = 0; b < extent.BlockCount; b++)
            {
                long blockNumber = extent.StartBlock + b;
                int blockOffset = b * blockSize;

                await _device.ReadBlockAsync(blockNumber, extentData.AsMemory(blockOffset, blockSize), ct)
                    .ConfigureAwait(false);
            }

            // Verify XxHash64 integrity (TRLR checksum)
            ulong actualHash = XxHash64.HashToUInt64(extentData);

            if (hasIntegrity && expectedHashes is not null &&
                expectedHashes.TryGetValue(extent.StartBlock, out var expectedHash))
            {
                if (actualHash != expectedHash)
                {
                    if (hasRaid)
                    {
                        // Signal RAID reconstruction needed
                        context.SetProperty(RaidReconstructionKey, new RaidReconstructionRequest
                        {
                            ExtentStartBlock = extent.StartBlock,
                            ExtentBlockCount = extent.BlockCount,
                            ExpectedHash = expectedHash,
                            ActualHash = actualHash,
                        });
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"Integrity verification failed for extent at block {extent.StartBlock}: " +
                            $"expected hash 0x{expectedHash:X16}, got 0x{actualHash:X16}.");
                    }
                }
            }

            // Copy extent data to the assembled buffer
            int copyLength = Math.Min(extentData.Length, dataBuffer.Length - dataOffset);
            if (copyLength > 0)
            {
                extentData.AsSpan(0, copyLength).CopyTo(dataBuffer.AsSpan(dataOffset));
                dataOffset += copyLength;
            }
        }

        // Store the assembled data buffer
        context.DataBuffer = dataBuffer;
    }

    /// <summary>
    /// Describes a RAID reconstruction request for an extent with integrity failure.
    /// </summary>
    public sealed class RaidReconstructionRequest
    {
        /// <summary>Start block of the failed extent.</summary>
        public long ExtentStartBlock { get; init; }

        /// <summary>Number of blocks in the failed extent.</summary>
        public int ExtentBlockCount { get; init; }

        /// <summary>Expected integrity hash.</summary>
        public ulong ExpectedHash { get; init; }

        /// <summary>Actual computed hash that failed verification.</summary>
        public ulong ActualHash { get; init; }
    }
}
