using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for real-time data streaming plugins.
/// </summary>
public abstract class StreamingPluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "Streaming";

    /// <summary>Publish data to a topic.</summary>
    public abstract Task PublishAsync(string topic, Stream data, CancellationToken ct = default);

    /// <summary>Subscribe to a topic.</summary>
    public abstract IAsyncEnumerable<Dictionary<string, object>> SubscribeAsync(string topic, CancellationToken ct = default);

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["SupportsStreaming"] = true;
        return metadata;
    }
}
