using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for observability plugins (metrics, tracing, logging, health).
/// </summary>
public abstract class ObservabilityPluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "Observability";

    /// <summary>Observability domain (e.g., "Metrics", "Tracing", "Logging", "Health").</summary>
    public abstract string ObservabilityDomain { get; }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["ObservabilityDomain"] = ObservabilityDomain;
        return metadata;
    }
}
