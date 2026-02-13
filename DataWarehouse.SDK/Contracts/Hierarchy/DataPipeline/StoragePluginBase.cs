using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Primitives;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for storage pipeline plugins (AD-04 key-based model).
/// Provides object/key-based CRUD, metadata, health, and AI-driven placement.
/// </summary>
public abstract class StoragePluginBase : DataPipelinePluginBase
{
    /// <inheritdoc/>
    public override bool MutatesData => false;

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.StorageProvider;

    /// <summary>Store data with the specified key.</summary>
    public abstract Task<StorageObjectMetadata> StoreAsync(string key, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default);

    /// <summary>Retrieve data for the specified key.</summary>
    public abstract Task<Stream> RetrieveAsync(string key, CancellationToken ct = default);

    /// <summary>Delete the object with the specified key.</summary>
    public abstract Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>Check if an object exists.</summary>
    public abstract Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    /// <summary>List objects matching prefix.</summary>
    public abstract IAsyncEnumerable<StorageObjectMetadata> ListAsync(string? prefix, CancellationToken ct = default);

    /// <summary>Get object metadata without retrieving data.</summary>
    public abstract Task<StorageObjectMetadata> GetMetadataAsync(string key, CancellationToken ct = default);

    /// <summary>Get storage health status.</summary>
    public abstract Task<StorageHealthInfo> GetHealthAsync(CancellationToken ct = default);

    /// <summary>AI hook: Optimize storage placement.</summary>
    protected virtual Task<Dictionary<string, object>> OptimizeStoragePlacementAsync(string key, Dictionary<string, object> context, CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, object>());

    /// <summary>AI hook: Predict access pattern for tiering.</summary>
    protected virtual Task<string> PredictAccessPatternAsync(string key, CancellationToken ct = default)
        => Task.FromResult("unknown");

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["StorageModel"] = "ObjectKeyBased";
        return metadata;
    }
}
