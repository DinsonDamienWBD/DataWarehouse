using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for data management plugins (governance, catalog, quality, lineage, lake, mesh, privacy).
/// </summary>
public abstract class DataManagementPluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "DataManagement";

    /// <summary>Data management domain (e.g., "Governance", "Catalog", "Quality", "Lineage").</summary>
    public abstract string DataManagementDomain { get; }

    /// <summary>AI hook: Classify data for governance.</summary>
    protected virtual Task<Dictionary<string, object>> ClassifyDataAsync(Dictionary<string, object> context, CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, object>());

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["DataManagementDomain"] = DataManagementDomain;
        return metadata;
    }
}
