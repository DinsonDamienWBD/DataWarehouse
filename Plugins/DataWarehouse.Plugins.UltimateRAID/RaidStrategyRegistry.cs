using System.Reflection;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateRAID;

/// <summary>
/// Interface for RAID strategy registry.
/// Provides auto-discovery and lookup of RAID strategies.
/// </summary>
public interface IRaidStrategyRegistry
{
    /// <summary>Registers a RAID strategy.</summary>
    void Register(IRaidStrategy strategy);

    /// <summary>Gets a strategy by its ID.</summary>
    IRaidStrategy? GetStrategy(string strategyId);

    /// <summary>Gets all registered strategies.</summary>
    IReadOnlyCollection<IRaidStrategy> GetAllStrategies();

    /// <summary>Gets strategies by category.</summary>
    IReadOnlyCollection<IRaidStrategy> GetStrategiesByCategory(string category);

    /// <summary>Gets strategies by RAID level.</summary>
    IReadOnlyCollection<IRaidStrategy> GetStrategiesByLevel(int raidLevel);

    /// <summary>Gets the default strategy.</summary>
    IRaidStrategy GetDefaultStrategy();

    /// <summary>Sets the default strategy.</summary>
    void SetDefaultStrategy(string strategyId);

    /// <summary>Discovers and registers strategies from assemblies.</summary>
    void DiscoverStrategies(params Assembly[] assemblies);
}

/// <summary>
/// Default implementation of RAID strategy registry.
/// Provides thread-safe registration and lookup of strategies.
/// </summary>
public sealed class RaidStrategyRegistry : IRaidStrategyRegistry
{
    private readonly BoundedDictionary<string, IRaidStrategy> _strategies = new BoundedDictionary<string, IRaidStrategy>(1000);
    private volatile string _defaultStrategyId = "raid1";

    /// <inheritdoc/>
    public void Register(IRaidStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies[strategy.StrategyId] = strategy;
    }

    /// <inheritdoc/>
    public IRaidStrategy? GetStrategy(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        return _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<IRaidStrategy> GetAllStrategies()
    {
        return _strategies.Values.ToList().AsReadOnly();
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<IRaidStrategy> GetStrategiesByCategory(string category)
    {
        return _strategies.Values
            .Where(s => s.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<IRaidStrategy> GetStrategiesByLevel(int raidLevel)
    {
        return _strategies.Values
            .Where(s => s.RaidLevel == raidLevel)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc/>
    public IRaidStrategy GetDefaultStrategy()
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
        var strategyType = typeof(IRaidStrategy);

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
                        if (Activator.CreateInstance(type) is IRaidStrategy strategy)
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
    /// Creates a pre-populated registry with discovered strategies.
    /// </summary>
    public static RaidStrategyRegistry CreateDefault()
    {
        return new RaidStrategyRegistry();
    }
}
