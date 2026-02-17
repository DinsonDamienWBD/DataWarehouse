using DataWarehouse.SDK.Primitives;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for plugins that mutate data in the pipeline (encryption, compression).
/// Provides OnWrite/OnRead stream transformation with async support.
/// </summary>
public abstract class DataTransformationPluginBase : DataPipelinePluginBase
{
    /// <inheritdoc/>
    public override bool MutatesData => true;

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.DataTransformationProvider;

    /// <summary>Sub-category (e.g., "Encryption", "Compression").</summary>
    public abstract string SubCategory { get; }

    /// <summary>Quality level (1-100) for sorting and selection.</summary>
    public virtual int QualityLevel => 50;

    /// <summary>
    /// AI hook: Select optimal algorithm based on data characteristics and context.
    /// Override in derived classes to provide algorithm-specific selection logic.
    /// Default returns SubCategory as a generic fallback.
    /// </summary>
    /// <param name="context">Context information for algorithm selection (data type, size, performance hints).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The selected algorithm identifier.</returns>
    protected virtual Task<string> SelectOptimalAlgorithmAsync(Dictionary<string, object> context, CancellationToken ct = default)
        => Task.FromResult(SubCategory);

    /// <summary>Transform data during write operations.</summary>
    public virtual Task<Stream> OnWriteAsync(Stream input, IKernelContext context, Dictionary<string, object> args, CancellationToken ct = default)
        => Task.FromResult(input);

    /// <summary>Transform data during read operations.</summary>
    public virtual Task<Stream> OnReadAsync(Stream stored, IKernelContext context, Dictionary<string, object> args, CancellationToken ct = default)
        => Task.FromResult(stored);

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["SubCategory"] = SubCategory;
        metadata["QualityLevel"] = QualityLevel;
        return metadata;
    }
}
