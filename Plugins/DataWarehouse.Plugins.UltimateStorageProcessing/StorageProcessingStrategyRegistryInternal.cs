using System.Reflection;
using DataWarehouse.SDK.Contracts.StorageProcessing;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStorageProcessing;

/// <summary>
/// Thread-safe registry for discovering and managing <see cref="IStorageProcessingStrategy"/> implementations.
/// Supports assembly scanning for auto-discovery and provides lookup by strategy ID.
/// </summary>
/// <remarks>
/// <para>
/// The registry scans assemblies for concrete classes extending <see cref="StorageProcessingStrategyBase"/>
/// and maintains a thread-safe dictionary for O(1) lookup. Strategies are keyed by their
/// <see cref="StorageProcessingStrategyBase.StrategyId"/> for unique identification.
/// </para>
/// </remarks>
internal sealed class StorageProcessingStrategyRegistryInternal
{
    private readonly BoundedDictionary<string, IStorageProcessingStrategy> _strategies = new BoundedDictionary<string, IStorageProcessingStrategy>(1000);
    private readonly BoundedDictionary<string, List<IStorageProcessingStrategy>> _byCategory = new BoundedDictionary<string, List<IStorageProcessingStrategy>>(1000);

    /// <summary>
    /// Gets the total number of registered strategies.
    /// </summary>
    public int Count => _strategies.Count;

    /// <summary>
    /// Discovers and registers all <see cref="StorageProcessingStrategyBase"/> implementations
    /// found in the specified assemblies via reflection.
    /// </summary>
    /// <param name="assemblies">One or more assemblies to scan for strategy implementations.</param>
    /// <returns>The number of newly discovered strategies.</returns>
    public int DiscoverStrategies(params Assembly[] assemblies)
    {
        var discovered = 0;
        var strategyType = typeof(IStorageProcessingStrategy);
        var baseType = typeof(StorageProcessingStrategyBase);

        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface || !baseType.IsAssignableFrom(type))
                    continue;

                try
                {
                    if (Activator.CreateInstance(type) is not StorageProcessingStrategyBase strategy)
                        continue;

                    if (_strategies.TryAdd(strategy.StrategyId, strategy))
                    {
                        // Index by category (derived from strategy ID prefix)
                        var category = ExtractCategory(strategy.StrategyId);
                        _byCategory.AddOrUpdate(
                            category,
                            _ => new List<IStorageProcessingStrategy> { strategy },
                            (_, list) => { lock (list) { list.Add(strategy); } return list; });

                        discovered++;
                    }
                }
                catch
                {

                    // Skip types that cannot be instantiated
                    System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                }
            }
        }

        return discovered;
    }

    /// <summary>
    /// Registers a strategy instance directly.
    /// </summary>
    /// <param name="strategy">The strategy to register.</param>
    /// <returns>True if the strategy was registered; false if a strategy with the same ID already exists.</returns>
    public bool Register(StorageProcessingStrategyBase strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        if (_strategies.TryAdd(strategy.StrategyId, strategy))
        {
            var category = ExtractCategory(strategy.StrategyId);
            _byCategory.AddOrUpdate(
                category,
                _ => new List<IStorageProcessingStrategy> { strategy },
                (_, list) => { lock (list) { list.Add(strategy); } return list; });
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets a strategy by its unique string identifier.
    /// </summary>
    /// <param name="strategyId">The strategy identifier (case-insensitive).</param>
    /// <returns>The strategy, or null if not found.</returns>
    public IStorageProcessingStrategy? GetStrategy(string strategyId)
    {
        ArgumentNullException.ThrowIfNull(strategyId);
        _strategies.TryGetValue(strategyId, out var strategy);
        return strategy;
    }

    /// <summary>
    /// Tries to get a strategy by its unique string identifier.
    /// </summary>
    /// <param name="strategyId">The strategy identifier (case-insensitive).</param>
    /// <param name="strategy">The found strategy, or null.</param>
    /// <returns>True if found; false otherwise.</returns>
    public bool TryGetStrategy(string strategyId, out IStorageProcessingStrategy? strategy)
    {
        ArgumentNullException.ThrowIfNull(strategyId);
        return _strategies.TryGetValue(strategyId, out strategy);
    }

    /// <summary>
    /// Gets all strategies registered for a specific category.
    /// </summary>
    /// <param name="category">The category name (e.g., "compression", "build").</param>
    /// <returns>A read-only collection of strategies for the category.</returns>
    public IReadOnlyCollection<IStorageProcessingStrategy> GetStrategiesByCategory(string category)
    {
        ArgumentNullException.ThrowIfNull(category);
        if (_byCategory.TryGetValue(category, out var list))
        {
            lock (list)
            {
                return list.ToArray();
            }
        }
        return Array.Empty<IStorageProcessingStrategy>();
    }

    /// <summary>
    /// Gets all registered strategies.
    /// </summary>
    /// <returns>A read-only collection of all registered strategies.</returns>
    public IReadOnlyCollection<IStorageProcessingStrategy> GetAllStrategies()
    {
        return _strategies.Values.ToArray();
    }

    /// <summary>
    /// Gets all registered strategy identifiers.
    /// </summary>
    /// <returns>A read-only collection of strategy IDs.</returns>
    public IReadOnlyCollection<string> GetAllStrategyIds()
    {
        return _strategies.Keys.ToArray();
    }

    /// <summary>
    /// Gets all category names that have registered strategies.
    /// </summary>
    /// <returns>A read-only collection of category names.</returns>
    public IReadOnlyCollection<string> GetAllCategories()
    {
        return _byCategory.Keys.ToArray();
    }

    /// <summary>
    /// Extracts the category prefix from a strategy ID.
    /// For example, "compression-zstd" returns "compression".
    /// </summary>
    /// <param name="strategyId">The strategy identifier.</param>
    /// <returns>The category prefix.</returns>
    private static string ExtractCategory(string strategyId)
    {
        var dashIndex = strategyId.IndexOf('-');
        return dashIndex > 0 ? strategyId[..dashIndex] : strategyId;
    }
}
