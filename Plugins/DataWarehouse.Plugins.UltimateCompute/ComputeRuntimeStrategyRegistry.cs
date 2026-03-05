using System.Reflection;
using DataWarehouse.SDK.Contracts.Compute;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompute;

/// <summary>
/// Thread-safe registry for discovering and managing <see cref="IComputeRuntimeStrategy"/> implementations.
/// Supports assembly scanning for auto-discovery and provides lookup by runtime type or strategy ID.
/// </summary>
/// <remarks>
/// <para>
/// The registry scans assemblies for concrete classes implementing <see cref="IComputeRuntimeStrategy"/>
/// and maintains a thread-safe dictionary for O(1) lookup. Strategies are keyed by their
/// <see cref="ComputeRuntimeStrategyBase.StrategyId"/> for unique identification.
/// </para>
/// </remarks>
internal sealed class ComputeRuntimeStrategyRegistry
{
    private readonly BoundedDictionary<string, IComputeRuntimeStrategy> _strategies = new BoundedDictionary<string, IComputeRuntimeStrategy>(1000);
    private readonly BoundedDictionary<ComputeRuntime, List<IComputeRuntimeStrategy>> _byRuntime = new BoundedDictionary<ComputeRuntime, List<IComputeRuntimeStrategy>>(1000);

    /// <summary>
    /// Gets the total number of registered strategies.
    /// </summary>
    public int Count => _strategies.Count;

    /// <summary>
    /// Discovers and registers all <see cref="IComputeRuntimeStrategy"/> implementations
    /// found in the specified assemblies via reflection.
    /// </summary>
    /// <param name="assemblies">One or more assemblies to scan for strategy implementations.</param>
    /// <returns>The number of newly discovered strategies.</returns>
    public int DiscoverStrategies(params Assembly[] assemblies)
    {
        var discovered = 0;
        var strategyType = typeof(IComputeRuntimeStrategy);
        var baseType = typeof(ComputeRuntimeStrategyBase);

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
                if (type.IsAbstract || type.IsInterface || !strategyType.IsAssignableFrom(type))
                    continue;

                try
                {
                    if (Activator.CreateInstance(type) is not ComputeRuntimeStrategyBase strategy)
                        continue;

                    if (_strategies.TryAdd(strategy.StrategyId, strategy))
                    {
                        // Index by runtime type
                        _byRuntime.AddOrUpdate(
                            strategy.Runtime,
                            _ => new List<IComputeRuntimeStrategy> { strategy },
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
    public bool Register(ComputeRuntimeStrategyBase strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        if (_strategies.TryAdd(strategy.StrategyId, strategy))
        {
            _byRuntime.AddOrUpdate(
                strategy.Runtime,
                _ => new List<IComputeRuntimeStrategy> { strategy },
                (_, list) => { lock (list) { list.Add(strategy); } return list; });
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets a strategy by its <see cref="ComputeRuntime"/> type.
    /// Returns the first registered strategy for the given runtime.
    /// </summary>
    /// <param name="runtime">The compute runtime type to look up.</param>
    /// <returns>The strategy, or null if none is registered for the runtime.</returns>
    public IComputeRuntimeStrategy? GetStrategy(ComputeRuntime runtime)
    {
        if (_byRuntime.TryGetValue(runtime, out var list))
        {
            lock (list)
            {
                return list.Count > 0 ? list[0] : null;
            }
        }
        return null;
    }

    /// <summary>
    /// Gets a strategy by its unique string identifier.
    /// </summary>
    /// <param name="strategyId">The strategy identifier (case-insensitive).</param>
    /// <returns>The strategy, or null if not found.</returns>
    public IComputeRuntimeStrategy? GetStrategy(string strategyId)
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
    public bool TryGetStrategy(string strategyId, out IComputeRuntimeStrategy? strategy)
    {
        ArgumentNullException.ThrowIfNull(strategyId);
        return _strategies.TryGetValue(strategyId, out strategy);
    }

    /// <summary>
    /// Gets all strategies registered for a specific runtime type.
    /// </summary>
    /// <param name="runtime">The compute runtime type.</param>
    /// <returns>A read-only collection of strategies for the runtime.</returns>
    public IReadOnlyCollection<IComputeRuntimeStrategy> GetStrategiesByRuntime(ComputeRuntime runtime)
    {
        if (_byRuntime.TryGetValue(runtime, out var list))
        {
            lock (list)
            {
                return list.ToArray();
            }
        }
        return Array.Empty<IComputeRuntimeStrategy>();
    }

    /// <summary>
    /// Gets all registered strategies.
    /// </summary>
    /// <returns>A read-only collection of all registered strategies.</returns>
    public IReadOnlyCollection<IComputeRuntimeStrategy> GetAllStrategies()
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
}
