using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for integrity pipeline plugins. Verifies data at boundaries.
/// </summary>
public abstract class IntegrityPluginBase : DataPipelinePluginBase
{
    /// <inheritdoc/>
    public override bool MutatesData => false;

    /// <summary>Verify integrity of an object.</summary>
    public abstract Task<Dictionary<string, object>> VerifyAsync(string key, CancellationToken ct = default);

    /// <summary>Compute hash of a data stream.</summary>
    public abstract Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);

    /// <summary>Validate integrity chain between two keys.</summary>
    public virtual Task<bool> ValidateChainAsync(string startKey, string endKey, CancellationToken ct = default)
        => Task.FromResult(true);

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["IntegrityVerification"] = true;
        return metadata;
    }
}
