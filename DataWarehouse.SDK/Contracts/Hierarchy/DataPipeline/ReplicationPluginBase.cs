using DataWarehouse.SDK.Primitives;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for replication pipeline plugins. Distributes data across nodes.
/// </summary>
public abstract class ReplicationPluginBase : DataPipelinePluginBase
{
    /// <inheritdoc/>
    public override bool MutatesData => false;

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.StorageProvider;

    /// <summary>Default replication mode (Sync or Async).</summary>
    public virtual string DefaultReplicationMode => "Async";

    /// <summary>Replicate an object to target nodes.</summary>
    public abstract Task<Dictionary<string, object>> ReplicateAsync(string key, string[] targetNodes, CancellationToken ct = default);

    /// <summary>Get sync status for a key.</summary>
    public abstract Task<Dictionary<string, object>> GetSyncStatusAsync(string key, CancellationToken ct = default);

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["DefaultReplicationMode"] = DefaultReplicationMode;
        return metadata;
    }
}
