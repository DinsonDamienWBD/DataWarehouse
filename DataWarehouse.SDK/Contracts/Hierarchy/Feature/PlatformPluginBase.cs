using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for platform service plugins (SDK ports, microservices, IoT, marketplace).
/// </summary>
public abstract class PlatformPluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "Platform";

    /// <summary>Platform domain (e.g., "SDKPorts", "Microservices", "IoT", "Marketplace").</summary>
    public abstract string PlatformDomain { get; }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["PlatformDomain"] = PlatformDomain;
        return metadata;
    }
}
