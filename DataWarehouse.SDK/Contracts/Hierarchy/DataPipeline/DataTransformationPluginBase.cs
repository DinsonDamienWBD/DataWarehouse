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
