using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Pipeline.Stages;

/// <summary>
/// Write Stage 4: applies data transformations (compression, encryption, delta encoding)
/// to the data buffer. This stage always runs (no module gate) and checks individual
/// transformation flags on the pipeline context to decide which transforms to apply.
/// </summary>
/// <remarks>
/// <para>
/// Transformation implementations are injected via the property bag to avoid hard SDK
/// dependencies on specific compression or encryption libraries. The stage reads
/// delegate functions from well-known property keys and applies them to
/// <see cref="VdePipelineContext.DataBuffer"/>.
/// </para>
/// <para>
/// Transform order: compression first, then encryption, then delta encoding.
/// This ensures encrypted data is always compressed (better ratio on plaintext)
/// and delta encoding operates on the final byte stream.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VDE write pipeline data transformer stage (VOPT-90)")]
public sealed class DataTransformerStage : IVdeWriteStage
{
    /// <summary>Property key for the compression delegate: <c>Func&lt;ReadOnlyMemory&lt;byte&gt;, Memory&lt;byte&gt;&gt;</c>.</summary>
    public const string CompressionDelegateKey = "CompressionDelegate";

    /// <summary>Property key for the encryption delegate: <c>Func&lt;ReadOnlyMemory&lt;byte&gt;, ReadOnlyMemory&lt;byte&gt;, Memory&lt;byte&gt;&gt;</c> (data, IV) -> encrypted.</summary>
    public const string EncryptionDelegateKey = "EncryptionDelegate";

    /// <summary>Property key for the delta computation delegate: <c>Func&lt;ReadOnlyMemory&lt;byte&gt;, ReadOnlyMemory&lt;byte&gt;, Memory&lt;byte&gt;&gt;</c> (current, base) -> delta.</summary>
    public const string DeltaDelegateKey = "DeltaDelegate";

    /// <summary>Property key for the delta base data.</summary>
    public const string DeltaBaseKey = "DeltaBase";

    /// <summary>Property key for the encryption initialization vector.</summary>
    public const string EncryptionIVKey = "EncryptionIV";

    /// <summary>Property key for the compression algorithm name (informational).</summary>
    public const string CompressionAlgorithmKey = "CompressionAlgorithm";

    /// <inheritdoc />
    public string StageName => "DataTransformer";

    /// <inheritdoc />
    /// <remarks>Always null: this stage always runs and checks individual transform flags.</remarks>
    public ModuleId? ModuleGate => null;

    /// <inheritdoc />
    public Task ExecuteAsync(VdePipelineContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (context.DataBuffer.Length == 0)
            return Task.CompletedTask;

        // Step 1: Compression (if flagged)
        if (context.IsCompressed &&
            context.TryGetProperty<Func<ReadOnlyMemory<byte>, Memory<byte>>>(CompressionDelegateKey, out var compressor))
        {
            var compressed = compressor(context.DataBuffer);
            context.DataBuffer = compressed;
        }

        // Step 2: Encryption (if flagged)
        if (context.IsEncrypted &&
            context.TryGetProperty<Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, Memory<byte>>>(EncryptionDelegateKey, out var encryptor))
        {
            ReadOnlyMemory<byte> iv = default;
            if (context.TryGetProperty<byte[]>(EncryptionIVKey, out var ivBytes))
            {
                iv = ivBytes;
            }

            var encrypted = encryptor(context.DataBuffer, iv);
            context.DataBuffer = encrypted;
        }

        // Step 3: Delta encoding (if flagged)
        if (context.IsDelta &&
            context.TryGetProperty<Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, Memory<byte>>>(DeltaDelegateKey, out var deltaEncoder) &&
            context.TryGetProperty<byte[]>(DeltaBaseKey, out var deltaBase))
        {
            var delta = deltaEncoder(context.DataBuffer, deltaBase);
            context.DataBuffer = delta;
        }

        return Task.CompletedTask;
    }
}
