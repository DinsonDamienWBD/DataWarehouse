using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for external interface plugins (REST, gRPC, SQL, WebSocket).
/// </summary>
public abstract class InterfacePluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "Interface";

    /// <summary>Protocol this interface serves (e.g., "REST", "gRPC", "SQL", "WebSocket").</summary>
    public abstract string Protocol { get; }

    /// <summary>Port number, if applicable.</summary>
    public virtual int? Port => null;

    /// <summary>Base path for the interface (e.g., "/api/v1").</summary>
    public virtual string? BasePath => null;

    /// <summary>AI hook: Optimize response for a query.</summary>
    protected virtual Task<Dictionary<string, object>> OptimizeResponseAsync(Dictionary<string, object> query, CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, object>());

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["Protocol"] = Protocol;
        if (Port.HasValue) metadata["Port"] = Port.Value;
        if (BasePath != null) metadata["BasePath"] = BasePath;
        return metadata;
    }
}
