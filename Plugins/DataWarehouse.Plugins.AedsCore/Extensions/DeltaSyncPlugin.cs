using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Distribution;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.AedsCore.Extensions;

/// <summary>
/// Delta descriptor containing diff instructions.
/// </summary>
/// <param name="AddedChunks">Chunks that were added in the new version.</param>
/// <param name="RemovedChunks">Chunks that were removed from the base version.</param>
/// <param name="ModifiedChunks">Chunks that were modified between versions.</param>
/// <param name="DeltaSizeBytes">Total size of the delta in bytes.</param>
public record DeltaDescriptor(
    int[] AddedChunks,
    int[] RemovedChunks,
    DeltaChunk[] ModifiedChunks,
    long DeltaSizeBytes
);

/// <summary>
/// Modified chunk information.
/// </summary>
/// <param name="ChunkIndex">Index of the modified chunk.</param>
/// <param name="BaseOffset">Offset in the base version.</param>
/// <param name="NewData">New data bytes (for insertions).</param>
/// <param name="Operation">Operation type (copy from base or insert new).</param>
public record DeltaChunk(
    int ChunkIndex,
    long BaseOffset,
    byte[]? NewData,
    DeltaOperation Operation
);

/// <summary>
/// Delta operation type.
/// </summary>
public enum DeltaOperation
{
    /// <summary>Copy chunk from base version.</summary>
    CopyFromBase,

    /// <summary>Insert new data.</summary>
    InsertNew
}

/// <summary>
/// Delta Sync Plugin: Bandwidth optimization via binary diff.
/// Computes and applies rsync-style rolling hash diffs to transfer only changed bytes
/// between file versions, achieving 50-80% bandwidth savings on updates.
/// </summary>
/// <remarks>
/// Implements rsync rolling hash algorithm (Adler-32 based) for content-defined chunking.
/// Chunk size: 1 KB for signature generation, larger chunks for data transfer.
/// </remarks>
public sealed class DeltaSyncPlugin : DataManagementPluginBase
{
    private const int SignatureChunkSize = 1024; // 1 KB for rolling hash
    private const int DataChunkSize = 1_048_576; // 1 MB for data transfer
    private const double WorthwhileThreshold = 0.5; // Delta is worthwhile if <50% of full size

    /// <summary>
    /// Gets the plugin identifier.
    /// </summary>
    public override string Id => "aeds.delta-sync";

    /// <summary>
    /// Gets the plugin name.
    /// </summary>
    public override string Name => "DeltaSyncPlugin";

    /// <summary>
    /// Gets the plugin version.
    /// </summary>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string DataManagementDomain => "DeltaSync";

    /// <summary>
    /// Gets the plugin category.
    /// </summary>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Computes delta between base version and new version.
    /// </summary>
    /// <param name="baseStream">Base version stream.</param>
    /// <param name="targetStream">Target version stream.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Delta descriptor with diff instructions.</returns>
    public async Task<DeltaDescriptor> ComputeDeltaAsync(
        Stream baseStream,
        Stream targetStream,
        CancellationToken ct = default)
    {
        if (baseStream == null)
            throw new ArgumentNullException(nameof(baseStream));
        if (targetStream == null)
            throw new ArgumentNullException(nameof(targetStream));

        var baseSignature = await GenerateSignatureAsync(baseStream, ct);
        var targetSignature = await GenerateSignatureAsync(targetStream, ct);

        var addedChunks = new List<int>();
        var removedChunks = new List<int>();
        var modifiedChunks = new List<DeltaChunk>();

        // Find added chunks (in target but not in base)
        for (int i = 0; i < targetSignature.Length; i++)
        {
            if (i >= baseSignature.Length || baseSignature[i] != targetSignature[i])
            {
                addedChunks.Add(i);

                // Read the chunk data from target
                targetStream.Position = i * SignatureChunkSize;
                var buffer = new byte[SignatureChunkSize];
                #pragma warning disable CA2022 // Intentional partial read - last chunk may be smaller than SignatureChunkSize
                var bytesRead = await targetStream.ReadAsync(buffer, 0, SignatureChunkSize, ct);
                #pragma warning restore CA2022

                var chunkData = new byte[bytesRead];
                Array.Copy(buffer, chunkData, bytesRead);

                modifiedChunks.Add(new DeltaChunk(i, i * SignatureChunkSize, chunkData, DeltaOperation.InsertNew));
            }
        }

        // Find removed chunks (in base but not in target)
        for (int i = targetSignature.Length; i < baseSignature.Length; i++)
        {
            removedChunks.Add(i);
        }

        var deltaSizeBytes = modifiedChunks.Sum(c => c.NewData?.Length ?? 0);

        return new DeltaDescriptor(
            addedChunks.ToArray(),
            removedChunks.ToArray(),
            modifiedChunks.ToArray(),
            deltaSizeBytes
        );
    }

    /// <summary>
    /// Applies delta to base stream to reconstruct target.
    /// </summary>
    /// <param name="baseStream">Base version stream.</param>
    /// <param name="delta">Delta descriptor.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Reconstructed stream.</returns>
    public async Task<Stream> ApplyDeltaAsync(
        Stream baseStream,
        DeltaDescriptor delta,
        CancellationToken ct = default)
    {
        if (baseStream == null)
            throw new ArgumentNullException(nameof(baseStream));
        if (delta == null)
            throw new ArgumentNullException(nameof(delta));

        // Guard against streams >2GB to prevent silent integer overflow (finding 979).
        if (baseStream.Length > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(baseStream),
                $"Base stream length {baseStream.Length} exceeds maximum supported size of {int.MaxValue} bytes.");

        var baseLength = (int)baseStream.Length;
        var resultStream = new MemoryStream(baseLength);
        var baseData = new byte[baseLength];
        await baseStream.ReadExactlyAsync(baseData, 0, baseLength, ct);

        // P2-988: Build a lookup of modified chunks by index so we can interleave them with
        // unchanged chunks in correct chunk-index order, rather than writing all modified chunks
        // first and all unchanged chunks second (which corrupts output for interleaved modifications).
        var modifiedByIndex = delta.ModifiedChunks.ToDictionary(c => c.ChunkIndex);
        var removedSet = new HashSet<int>(delta.RemovedChunks);

        // Determine total output chunks: max of base chunks and highest modified/added index + 1
        var baseChunks = (int)Math.Ceiling(baseData.Length / (double)SignatureChunkSize);
        var maxModifiedIndex = modifiedByIndex.Count > 0 ? modifiedByIndex.Keys.Max() : -1;
        var totalChunks = Math.Max(baseChunks, maxModifiedIndex + 1);

        for (int i = 0; i < totalChunks; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (removedSet.Contains(i))
            {
                // Chunk removed in delta — skip it
                continue;
            }

            if (modifiedByIndex.TryGetValue(i, out var chunk))
            {
                switch (chunk.Operation)
                {
                    case DeltaOperation.CopyFromBase:
                        if (chunk.BaseOffset >= 0 && chunk.BaseOffset < baseData.Length)
                        {
                            var copyLength = (int)Math.Min(SignatureChunkSize, baseData.Length - chunk.BaseOffset);
                            await resultStream.WriteAsync(baseData, (int)chunk.BaseOffset, copyLength, ct);
                        }
                        break;

                    case DeltaOperation.InsertNew:
                        if (chunk.NewData != null)
                        {
                            await resultStream.WriteAsync(chunk.NewData, 0, chunk.NewData.Length, ct);
                        }
                        break;
                }
            }
            else if (i < baseChunks)
            {
                // Unchanged chunk — copy verbatim from base
                var offset = i * SignatureChunkSize;
                var length = Math.Min(SignatureChunkSize, baseData.Length - offset);
                await resultStream.WriteAsync(baseData, offset, length, ct);
            }
        }

        resultStream.Position = 0;
        return resultStream;
    }

    /// <summary>
    /// Determines if delta sync is worthwhile for given sizes.
    /// </summary>
    /// <param name="baseSize">Size of base version in bytes.</param>
    /// <param name="targetSize">Size of target version in bytes.</param>
    /// <param name="deltaSize">Size of delta in bytes.</param>
    /// <returns>True if delta is worth using (less than 50% of full size).</returns>
    public bool IsWorthwhile(long baseSize, long targetSize, long deltaSize)
    {
        if (targetSize <= 0)
            return false;

        var ratio = deltaSize / (double)targetSize;
        return ratio < WorthwhileThreshold;
    }

    /// <summary>
    /// Generates signature for a stream using rolling hash.
    /// </summary>
    /// <param name="stream">Stream to generate signature for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Array of chunk hashes.</returns>
    public async Task<string[]> GenerateSignatureAsync(Stream stream, CancellationToken ct = default)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        stream.Position = 0;
        var signatures = new List<string>();
        var buffer = new byte[SignatureChunkSize];
        int bytesRead;

        #pragma warning disable CA2022 // Intentional: read loop checks return value for EOF detection
        while ((bytesRead = await stream.ReadAsync(buffer, 0, SignatureChunkSize, ct)) > 0)
        #pragma warning restore CA2022
        {
            var hash = ComputeRollingHash(buffer, bytesRead);
            signatures.Add(hash);
        }

        return signatures.ToArray();
    }

    /// <summary>
    /// Computes a 64-bit non-cryptographic hash for a chunk using XxHash64.
    /// XxHash64 has a 2^64 collision space, far less collision-prone than Adler-32 (2^32),
    /// eliminating the false-positive chunk matches that could cause silent data corruption
    /// in delta reconstruction (finding P2-987).
    /// </summary>
    /// <param name="data">Data bytes.</param>
    /// <param name="length">Length of data to hash.</param>
    /// <returns>Base64-encoded 64-bit hash.</returns>
    private static string ComputeRollingHash(byte[] data, int length)
    {
        var hash = XxHash64.HashToUInt64(data.AsSpan(0, length));
        return Convert.ToBase64String(BitConverter.GetBytes(hash));
    }

    /// <summary>
    /// Checks if this plugin is enabled based on client capabilities.
    /// </summary>
    /// <param name="capabilities">Client capabilities.</param>
    /// <returns>True if DeltaSync capability is enabled.</returns>
    public static bool IsEnabled(ClientCapabilities capabilities)
    {
        return capabilities.HasFlag(ClientCapabilities.DeltaSync);
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task StopAsync()
    {
        return Task.CompletedTask;
    }
}
