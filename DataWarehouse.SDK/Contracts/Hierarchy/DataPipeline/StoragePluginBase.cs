using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Storage;
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

    #region StorageAddress Overloads (HAL-05)

    /// <summary>Store data using a StorageAddress. Override for native StorageAddress support.</summary>
    public virtual Task<StorageObjectMetadata> StoreAsync(StorageAddress address, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default)
        => StoreAsync(address.ToKey(), data, metadata, ct);

    /// <summary>Retrieve data using a StorageAddress. Override for native StorageAddress support.</summary>
    public virtual Task<Stream> RetrieveAsync(StorageAddress address, CancellationToken ct = default)
        => RetrieveAsync(address.ToKey(), ct);

    /// <summary>Delete an object using a StorageAddress. Override for native StorageAddress support.</summary>
    public virtual Task DeleteAsync(StorageAddress address, CancellationToken ct = default)
        => DeleteAsync(address.ToKey(), ct);

    /// <summary>Check existence using a StorageAddress. Override for native StorageAddress support.</summary>
    public virtual Task<bool> ExistsAsync(StorageAddress address, CancellationToken ct = default)
        => ExistsAsync(address.ToKey(), ct);

    /// <summary>List objects using a StorageAddress prefix. Override for native StorageAddress support.</summary>
    public virtual IAsyncEnumerable<StorageObjectMetadata> ListAsync(StorageAddress? prefix, CancellationToken ct = default)
        => ListAsync(prefix?.ToKey(), ct);

    /// <summary>Get metadata using a StorageAddress. Override for native StorageAddress support.</summary>
    public virtual Task<StorageObjectMetadata> GetMetadataAsync(StorageAddress address, CancellationToken ct = default)
        => GetMetadataAsync(address.ToKey(), ct);

    #endregion

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
