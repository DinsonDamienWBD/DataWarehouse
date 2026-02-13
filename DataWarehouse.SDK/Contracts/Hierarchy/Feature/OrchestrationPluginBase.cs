using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for workflow and edge computing orchestration plugins.
/// </summary>
public abstract class OrchestrationPluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "Orchestration";

    /// <summary>Orchestration mode (e.g., "Workflow", "EdgeComputing", "Pipeline").</summary>
    public abstract string OrchestrationMode { get; }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["OrchestrationMode"] = OrchestrationMode;
        return metadata;
    }
}
