using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for compute runtime plugins (WASM, container, sandbox, GPU).
/// </summary>
public abstract class ComputePluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "Compute";

    /// <summary>Runtime type (e.g., "WASM", "Container", "Sandbox", "GPU").</summary>
    public abstract string RuntimeType { get; }

    /// <summary>Execute a compute workload.</summary>
    public abstract Task<Dictionary<string, object>> ExecuteWorkloadAsync(Dictionary<string, object> workload, CancellationToken ct = default);

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["RuntimeType"] = RuntimeType;
        return metadata;
    }
}
