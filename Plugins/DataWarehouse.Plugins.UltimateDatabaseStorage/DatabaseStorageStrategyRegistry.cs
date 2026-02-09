using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using System.Collections.Concurrent;
using System.Reflection;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage;

/// <summary>
/// Interface for database storage strategy registry.
/// Provides auto-discovery and lookup of database storage strategies.
/// </summary>
public interface IDatabaseStorageStrategyRegistry
{
    /// <summary>Registers a database storage strategy.</summary>
    void Register(DatabaseStorageStrategyBase strategy);

    /// <summary>Gets a strategy by its ID.</summary>
    DatabaseStorageStrategyBase? GetStrategy(string strategyId);

    /// <summary>Gets all registered strategies.</summary>
    IReadOnlyCollection<DatabaseStorageStrategyBase> GetAllStrategies();

    /// <summary>Gets strategies by database category.</summary>
    IReadOnlyCollection<DatabaseStorageStrategyBase> GetStrategiesByCategory(DatabaseCategory category);

    /// <summary>Gets strategies by engine name.</summary>
    IReadOnlyCollection<DatabaseStorageStrategyBase> GetStrategiesByEngine(string engine);

    /// <summary>Gets strategies by tier.</summary>
    IReadOnlyCollection<DatabaseStorageStrategyBase> GetStrategiesByTier(StorageTier tier);

    /// <summary>Gets the default strategy.</summary>
    DatabaseStorageStrategyBase GetDefaultStrategy();

    /// <summary>Sets the default strategy.</summary>
    void SetDefaultStrategy(string strategyId);

    /// <summary>Discovers and registers strategies from assemblies.</summary>
    void DiscoverStrategies(params Assembly[] assemblies);
}

/// <summary>
/// Default implementation of database storage strategy registry.
/// Provides thread-safe registration and lookup of strategies.
/// </summary>
public sealed class DatabaseStorageStrategyRegistry : IDatabaseStorageStrategyRegistry
{
    private readonly ConcurrentDictionary<string, DatabaseStorageStrategyBase> _strategies = new(StringComparer.OrdinalIgnoreCase);
    private volatile string _defaultStrategyId = "postgresql";

    /// <inheritdoc/>
    public void Register(DatabaseStorageStrategyBase strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies[strategy.StrategyId] = strategy;
    }

    /// <inheritdoc/>
    public DatabaseStorageStrategyBase? GetStrategy(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        return _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<DatabaseStorageStrategyBase> GetAllStrategies()
    {
        return _strategies.Values.ToList().AsReadOnly();
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<DatabaseStorageStrategyBase> GetStrategiesByCategory(DatabaseCategory category)
    {
        return _strategies.Values
            .Where(s => s.DatabaseCategory == category)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<DatabaseStorageStrategyBase> GetStrategiesByEngine(string engine)
    {
        return _strategies.Values
            .Where(s => s.Engine.Equals(engine, StringComparison.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<DatabaseStorageStrategyBase> GetStrategiesByTier(StorageTier tier)
    {
        return _strategies.Values
            .Where(s => s.Tier == tier)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc/>
    public DatabaseStorageStrategyBase GetDefaultStrategy()
    {
        return GetStrategy(_defaultStrategyId)
            ?? throw new InvalidOperationException($"Default strategy '{_defaultStrategyId}' not found");
    }

    /// <inheritdoc/>
    public void SetDefaultStrategy(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        if (!_strategies.ContainsKey(strategyId))
        {
            throw new ArgumentException($"Strategy '{strategyId}' not registered", nameof(strategyId));
        }
        _defaultStrategyId = strategyId;
    }

    /// <inheritdoc/>
    public void DiscoverStrategies(params Assembly[] assemblies)
    {
        var strategyType = typeof(DatabaseStorageStrategyBase);

        foreach (var assembly in assemblies)
        {
            try
            {
                var types = assembly.GetTypes()
                    .Where(t => !t.IsAbstract && !t.IsInterface && strategyType.IsAssignableFrom(t));

                foreach (var type in types)
                {
                    try
                    {
                        if (Activator.CreateInstance(type) is DatabaseStorageStrategyBase strategy)
                        {
                            Register(strategy);
                        }
                    }
                    catch
                    {
                        // Skip types that can't be instantiated
                    }
                }
            }
            catch
            {
                // Skip assemblies that can't be scanned
            }
        }
    }

    /// <summary>
    /// Creates a pre-populated registry with common strategies.
    /// </summary>
    public static DatabaseStorageStrategyRegistry CreateDefault()
    {
        return new DatabaseStorageStrategyRegistry();
    }

    /// <summary>
    /// Gets statistics for all registered strategies.
    /// </summary>
    public IReadOnlyDictionary<string, DatabaseStorageStatistics> GetAllStatistics()
    {
        return _strategies.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.GetDatabaseStatistics());
    }

    /// <summary>
    /// Gets the count of registered strategies.
    /// </summary>
    public int Count => _strategies.Count;

    /// <summary>
    /// Gets registered strategy IDs.
    /// </summary>
    public IReadOnlyCollection<string> StrategyIds => _strategies.Keys.ToList().AsReadOnly();

    /// <summary>
    /// Removes a strategy from the registry.
    /// </summary>
    public bool Unregister(string strategyId)
    {
        return _strategies.TryRemove(strategyId, out _);
    }

    /// <summary>
    /// Clears all registered strategies.
    /// </summary>
    public void Clear()
    {
        _strategies.Clear();
    }
}
