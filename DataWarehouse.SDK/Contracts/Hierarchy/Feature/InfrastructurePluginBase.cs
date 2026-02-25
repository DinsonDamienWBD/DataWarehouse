using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for infrastructure service plugins (deployment, resilience, sustainability, multi-cloud).
/// </summary>
public abstract class InfrastructurePluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "Infrastructure";

    /// <summary>Infrastructure domain (e.g., "Deployment", "Resilience", "Sustainability").</summary>
    public abstract string InfrastructureDomain { get; }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["InfrastructureDomain"] = InfrastructureDomain;
        return metadata;
    }
}
