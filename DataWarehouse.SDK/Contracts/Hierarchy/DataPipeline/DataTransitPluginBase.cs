using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for data transit pipeline plugins. Moves data between nodes/sites.
/// </summary>
public abstract class DataTransitPluginBase : DataPipelinePluginBase
{
    /// <inheritdoc/>
    public override bool MutatesData => false;

    /// <summary>Transport protocol (e.g., "tcp", "quic", "rdma").</summary>
    public virtual string TransportProtocol => "tcp";

    /// <summary>Transfer data to a target.</summary>
    public abstract Task<Dictionary<string, object>> TransferAsync(string key, Dictionary<string, object> target, CancellationToken ct = default);

    /// <summary>Get transfer status.</summary>
    public abstract Task<Dictionary<string, object>> GetTransferStatusAsync(string transferId, CancellationToken ct = default);

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["TransportProtocol"] = TransportProtocol;
        return metadata;
    }
}
