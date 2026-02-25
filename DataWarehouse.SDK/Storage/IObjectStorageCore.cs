using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Storage;

/// <summary>
/// The canonical object/key-based storage contract (AD-04).
/// All storage operations in the SDK ultimately resolve to this interface.
/// URI-based access is provided via <see cref="PathStorageAdapter"/> which translates
/// URI paths to keys and delegates to this interface.
/// </summary>
/// <remarks>
/// <para>
/// Keys follow a hierarchical convention: "namespace/category/name" (e.g., "data/users/profile.json").
/// No leading slash. Forward slashes only. No path traversal sequences.
/// </para>
/// </remarks>
public interface IObjectStorageCore
{
    /// <summary>Stores data with the specified key and optional metadata.</summary>
    Task<StorageObjectMetadata> StoreAsync(string key, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default);

    /// <summary>Retrieves data for the specified key.</summary>
    Task<Stream> RetrieveAsync(string key, CancellationToken ct = default);

    /// <summary>Deletes the object with the specified key.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>Checks if an object with the specified key exists.</summary>
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    /// <summary>Lists objects matching an optional key prefix.</summary>
    IAsyncEnumerable<StorageObjectMetadata> ListAsync(string? prefix = null, CancellationToken ct = default);

    /// <summary>Gets metadata for a specific object without retrieving its data.</summary>
    Task<StorageObjectMetadata> GetMetadataAsync(string key, CancellationToken ct = default);

    /// <summary>Gets metadata including tags for a specific object.</summary>
    Task<StorageObjectMetadata> GetMetadataWithTagsAsync(string key, CancellationToken ct = default)
        => GetMetadataAsync(key, ct); // Default: delegates to non-tag version (backward compat)

    /// <summary>Gets the health status of the underlying storage backend.</summary>
    Task<StorageHealthInfo> GetHealthAsync(CancellationToken ct = default);

    /// <summary>Gets available storage capacity in bytes.</summary>
    Task<long?> GetAvailableCapacityAsync(CancellationToken ct = default);

    #region StorageAddress Overloads (HAL-05)

    /// <summary>Stores data using a StorageAddress. Default: delegates via ToKey().</summary>
    Task<StorageObjectMetadata> StoreAsync(StorageAddress address, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default)
        => StoreAsync(address.ToKey(), data, metadata, ct);

    /// <summary>Retrieves data using a StorageAddress. Default: delegates via ToKey().</summary>
    Task<Stream> RetrieveAsync(StorageAddress address, CancellationToken ct = default)
        => RetrieveAsync(address.ToKey(), ct);

    /// <summary>Deletes an object using a StorageAddress. Default: delegates via ToKey().</summary>
    Task DeleteAsync(StorageAddress address, CancellationToken ct = default)
        => DeleteAsync(address.ToKey(), ct);

    /// <summary>Checks existence using a StorageAddress. Default: delegates via ToKey().</summary>
    Task<bool> ExistsAsync(StorageAddress address, CancellationToken ct = default)
        => ExistsAsync(address.ToKey(), ct);

    /// <summary>Gets metadata using a StorageAddress. Default: delegates via ToKey().</summary>
    Task<StorageObjectMetadata> GetMetadataAsync(StorageAddress address, CancellationToken ct = default)
        => GetMetadataAsync(address.ToKey(), ct);

    #endregion
}
