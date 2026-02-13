using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for data format plugins (columnar, graph, scientific, lakehouse).
/// </summary>
public abstract class FormatPluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "Format";

    /// <summary>Format family (e.g., "Columnar", "Graph", "Scientific", "Lakehouse").</summary>
    public abstract string FormatFamily { get; }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FormatFamily"] = FormatFamily;
        return metadata;
    }
}
