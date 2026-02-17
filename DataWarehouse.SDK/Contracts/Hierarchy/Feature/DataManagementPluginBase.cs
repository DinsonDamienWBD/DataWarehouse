using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for data management plugins (governance, catalog, quality, lineage, lake, mesh, privacy).
/// Provides tenant-scoped storage with ConcurrentDictionary cache and virtual persistence hooks.
/// </summary>
public abstract class DataManagementPluginBase : FeaturePluginBase
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, object>> _tenantStorage = new();

    /// <inheritdoc/>
    public override string FeatureCategory => "DataManagement";

    /// <summary>Data management domain (e.g., "Governance", "Catalog", "Quality", "Lineage").</summary>
    public abstract string DataManagementDomain { get; }

    /// <summary>
    /// Gets the current tenant ID for scoping storage operations.
    /// Override to provide tenant context from ISecurityContext or other sources.
    /// Defaults to "default" when no tenant context is available.
    /// </summary>
    protected virtual string GetCurrentTenantId() => "default";

    /// <summary>
    /// Gets a value from tenant-scoped storage. Checks in-memory cache first,
    /// then falls back to <see cref="LoadFromStorageAsync{T}"/> for persistent retrieval.
    /// </summary>
    /// <typeparam name="T">The type of value to retrieve.</typeparam>
    /// <param name="key">The storage key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The value if found; otherwise null/default.</returns>
    protected async Task<T?> GetDataAsync<T>(string key, CancellationToken ct = default)
    {
        var tenantId = GetCurrentTenantId();
        var tenantCache = _tenantStorage.GetOrAdd(tenantId, _ => new ConcurrentDictionary<string, object>());

        if (tenantCache.TryGetValue(key, out var cached) && cached is T typedValue)
            return typedValue;

        // Fall back to persistent storage
        var loaded = await LoadFromStorageAsync<T>(tenantId, key, ct);
        if (loaded is not null)
        {
            tenantCache[key] = loaded;
        }

        return loaded;
    }

    /// <summary>
    /// Sets a value in tenant-scoped storage. Updates both in-memory cache
    /// and calls <see cref="SaveToStorageAsync{T}"/> for persistent storage.
    /// </summary>
    /// <typeparam name="T">The type of value to store.</typeparam>
    /// <param name="key">The storage key.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="ct">Cancellation token.</param>
    protected async Task SetDataAsync<T>(string key, T value, CancellationToken ct = default)
    {
        var tenantId = GetCurrentTenantId();
        var tenantCache = _tenantStorage.GetOrAdd(tenantId, _ => new ConcurrentDictionary<string, object>());

        tenantCache[key] = value!;
        await SaveToStorageAsync(tenantId, key, value, ct);
    }

    /// <summary>
    /// Virtual persistence hook for loading data from external storage.
    /// Override to delegate to IStorageEngine via message bus or other persistence mechanism.
    /// Default: returns default (no persistent storage).
    /// </summary>
    protected virtual Task<T?> LoadFromStorageAsync<T>(string tenantId, string key, CancellationToken ct)
        => Task.FromResult<T?>(default);

    /// <summary>
    /// Virtual persistence hook for saving data to external storage.
    /// Override to delegate to IStorageEngine via message bus or other persistence mechanism.
    /// Default: no-op (in-memory only).
    /// </summary>
    protected virtual Task SaveToStorageAsync<T>(string tenantId, string key, T value, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Gets all storage keys for the current tenant.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only collection of keys stored for the current tenant.</returns>
    protected Task<IReadOnlyCollection<string>> GetTenantDataKeysAsync(CancellationToken ct = default)
    {
        var tenantId = GetCurrentTenantId();
        if (_tenantStorage.TryGetValue(tenantId, out var tenantCache))
        {
            IReadOnlyCollection<string> keys = tenantCache.Keys.ToList().AsReadOnly();
            return Task.FromResult(keys);
        }

        IReadOnlyCollection<string> empty = Array.Empty<string>();
        return Task.FromResult(empty);
    }

    /// <summary>
    /// Clears all data for the current tenant from the in-memory cache.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    protected Task ClearTenantDataAsync(CancellationToken ct = default)
    {
        var tenantId = GetCurrentTenantId();
        if (_tenantStorage.TryGetValue(tenantId, out var tenantCache))
        {
            tenantCache.Clear();
        }

        return Task.CompletedTask;
    }

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
