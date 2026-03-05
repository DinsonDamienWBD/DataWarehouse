using System.IO.Hashing;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Pipeline.Stages;

/// <summary>
/// Write Stage 6: computes integrity hashes for each allocated extent and queues
/// Merkle tree updates. This stage is gated by the <see cref="ModuleId.Integrity"/>
/// module bit and only executes when integrity verification is active.
/// </summary>
/// <remarks>
/// <para>
/// For each extent in <see cref="VdePipelineContext.Extents"/>, the stage computes
/// a hash of the corresponding data slice from the data buffer. The current
/// implementation uses XxHash64 as a fast placeholder; the hash algorithm will be
/// wired to a BLAKE3 provider when the cryptographic subsystem is integrated.
/// </para>
/// <para>
/// Hash results are stored as <c>context.Properties["ExtentHashes"]</c> and the
/// TRLR generation number is set to the current epoch for on-disk trailer records.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VDE write pipeline integrity calculator stage (VOPT-90)")]
public sealed class IntegrityCalculatorStage : IVdeWriteStage
{
    /// <summary>Property key for the computed extent hashes.</summary>
    public const string ExtentHashesKey = "ExtentHashes";

    /// <summary>Property key for the TRLR generation number.</summary>
    public const string TrlrGenerationKey = "TrlrGeneration";

    /// <summary>Property key for the queued Merkle tree update.</summary>
    public const string MerkleUpdateKey = "MerkleTreeUpdate";

    /// <inheritdoc />
    public string StageName => "IntegrityCalculator";

    /// <inheritdoc />
    public ModuleId? ModuleGate => ModuleId.Integrity;

    /// <inheritdoc />
    public Task ExecuteAsync(VdePipelineContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        int blockSize = context.BlockSize > 0 ? context.BlockSize : 4096;
        var dataSpan = context.DataBuffer.Span;
        var hashes = new Dictionary<long, ulong>(context.Extents.Count);

        for (int i = 0; i < context.Extents.Count; i++)
        {
            var extent = context.Extents[i];
            int dataOffset = (int)extent.LogicalOffset;
            int dataLength = extent.BlockCount * blockSize;

            // Clamp to actual buffer length
            if (dataOffset >= dataSpan.Length)
                break;

            int actualLength = Math.Min(dataLength, dataSpan.Length - dataOffset);
            var slice = dataSpan.Slice(dataOffset, actualLength);

            // Compute hash using XxHash64 (placeholder for BLAKE3 provider integration)
            // TODO: wire to BLAKE3 provider when cryptographic subsystem is available
            ulong hash = XxHash64.HashToUInt64(slice);
            hashes[extent.StartBlock] = hash;
        }

        // Store extent hashes for downstream consumption (WAL writer, trailer records)
        context.SetProperty(ExtentHashesKey, hashes);

        // Set TRLR generation to current epoch
        long generation = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        context.SetProperty(TrlrGenerationKey, generation);

        // Queue Merkle tree update for background processing
        context.SetProperty(MerkleUpdateKey, new MerkleTreeUpdateRequest
        {
            InodeNumber = context.Inode?.InodeNumber ?? context.InodeNumber,
            ExtentHashes = hashes,
            Generation = generation,
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Describes a pending Merkle tree update to be processed after the write completes.
    /// </summary>
    public sealed class MerkleTreeUpdateRequest
    {
        /// <summary>Inode number whose Merkle tree needs updating.</summary>
        public long InodeNumber { get; init; }

        /// <summary>Per-extent hashes keyed by start block number.</summary>
        public Dictionary<long, ulong> ExtentHashes { get; init; } = new();

        /// <summary>Generation number for this update.</summary>
        public long Generation { get; init; }
    }
}
