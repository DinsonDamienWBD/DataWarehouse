using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for data management plugins (governance, catalog, quality, lineage, lake, mesh, privacy).
/// Provides tenant-scoped storage with ConcurrentDictionary cache and virtual persistence hooks.
/// </summary>
public abstract class DataManagementPluginBase : FeaturePluginBase
{
    private readonly BoundedDictionary<string, BoundedDictionary<string, object>> _tenantStorage = new BoundedDictionary<string, BoundedDictionary<string, object>>(1000);

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
        var tenantCache = _tenantStorage.GetOrAdd(tenantId, _ => new BoundedDictionary<string, object>(1000));

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
        var tenantCache = _tenantStorage.GetOrAdd(tenantId, _ => new BoundedDictionary<string, object>(1000));

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

    #region Data Management Strategy Dispatch

    /// <summary>
    /// Registers an <see cref="IStrategy"/> as a data management strategy in the PluginBase IStrategy registry.
    /// </summary>
    /// <param name="strategy">The data management strategy to register.</param>
    protected void RegisterDataManagementStrategy(IStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        RegisterStrategy(strategy);
    }

    /// <summary>
    /// Executes a data management operation using the resolved strategy from the PluginBase registry.
    /// Dispatches to <paramref name="strategyId"/> or the plugin default when not specified.
    /// </summary>
    /// <typeparam name="TStrategy">Concrete strategy type implementing <see cref="IStrategy"/>.</typeparam>
    /// <typeparam name="TResult">The operation result type.</typeparam>
    /// <param name="strategyId">Explicit strategy ID (null = use GetDefaultStrategyId).</param>
    /// <param name="identity">Optional CommandIdentity for ACL checks.</param>
    /// <param name="operation">The operation to execute on the resolved strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    protected Task<TResult> DispatchDataManagementStrategyAsync<TStrategy, TResult>(
        string? strategyId,
        CommandIdentity? identity,
        Func<TStrategy, Task<TResult>> operation,
        CancellationToken ct = default)
        where TStrategy : class, IStrategy
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteWithStrategyAsync<TStrategy, TResult>(strategyId, identity, operation, ct);
    }

    /// <summary>
    /// Executes a data management operation using the specified or default strategy.
    /// The operation dictionary allows strategy implementations to inspect the operation type and parameters.
    /// </summary>
    /// <typeparam name="TStrategy">Concrete strategy type implementing <see cref="IStrategy"/>.</typeparam>
    /// <param name="operation">Operation parameters (e.g., type, target, payload).</param>
    /// <param name="strategyId">Optional strategy ID. Null uses the plugin's domain-based default.</param>
    /// <param name="identity">Optional CommandIdentity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation result dictionary.</returns>
    protected Task<Dictionary<string, object>> ManageDataWithStrategyAsync<TStrategy>(
        Dictionary<string, object> operation,
        string? strategyId,
        CommandIdentity? identity,
        CancellationToken ct = default)
        where TStrategy : class, IStrategy
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteWithStrategyAsync<TStrategy, Dictionary<string, object>>(
            strategyId,
            identity,
            _ => Task.FromResult(new Dictionary<string, object>(operation)),
            ct);
    }

    /// <inheritdoc/>
    /// <remarks>Returns <see cref="DataManagementDomain"/> to route dispatches to domain-specific strategies.</remarks>
    protected override string? GetDefaultStrategyId() => DataManagementDomain;

    #endregion

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["DataManagementDomain"] = DataManagementDomain;
        return metadata;
    }
}
