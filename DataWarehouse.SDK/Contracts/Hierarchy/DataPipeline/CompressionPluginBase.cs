using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for compression pipeline plugins. Provides algorithm metadata,
/// compression level configuration, and AI-driven algorithm selection.
/// </summary>
public abstract class CompressionPluginBase : DataTransformationPluginBase
{
    /// <inheritdoc/>
    public override string SubCategory => "Compression";

    /// <summary>Compression algorithm identifier (e.g., "LZ4", "Zstd", "Brotli").</summary>
    public abstract string CompressionAlgorithm { get; }

    /// <summary>Compression level (1-22, higher = better compression, slower).</summary>
    public virtual int CompressionLevel => 6;

    /// <summary>AI hook: Select optimal compression algorithm for data type.</summary>
    protected virtual Task<string> SelectOptimalAlgorithmAsync(Dictionary<string, object> context, CancellationToken ct = default)
        => Task.FromResult(CompressionAlgorithm);

    /// <summary>AI hook: Predict compression ratio before compressing.</summary>
    protected virtual Task<double> PredictCompressionRatioAsync(Dictionary<string, object> dataProfile, CancellationToken ct = default)
        => Task.FromResult(0.5);

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["CompressionAlgorithm"] = CompressionAlgorithm;
        metadata["CompressionLevel"] = CompressionLevel;
        return metadata;
    }
}
