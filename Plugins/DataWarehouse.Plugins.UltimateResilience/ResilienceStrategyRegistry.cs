using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateResilience;

/// <summary>
/// Registry for discovering and managing resilience strategies.
/// </summary>
public interface IResilienceStrategyRegistry
{
    /// <summary>
    /// Gets a strategy by ID.
    /// </summary>
    IResilienceStrategy? GetStrategy(string strategyId);

    /// <summary>
    /// Gets all registered strategies.
    /// </summary>
    IReadOnlyList<IResilienceStrategy> GetAllStrategies();

    /// <summary>
    /// Gets strategies by category.
    /// </summary>
    IReadOnlyList<IResilienceStrategy> GetStrategiesByCategory(string category);

    /// <summary>
    /// Registers a strategy.
    /// </summary>
    void Register(IResilienceStrategy strategy);

    /// <summary>
    /// Discovers and registers strategies from an assembly.
    /// </summary>
    void DiscoverStrategies(Assembly assembly);
}

/// <summary>
/// Default implementation of the resilience strategy registry.
/// </summary>
/// <remarks>
/// This typed registry is superseded by the inherited strategy dispatch provided by
/// <see cref="DataWarehouse.SDK.Contracts.Hierarchy.ResiliencePluginBase"/>.
/// New plugins should use <c>RegisterResilienceStrategy</c> and <c>DispatchResilienceStrategyAsync</c>
/// from the base class instead. This class is retained for the typed <see cref="IResilienceStrategy"/>
/// category-query operations not available on the generic base dispatch.
/// </remarks>
[Obsolete("Use the inherited RegisterResilienceStrategy() and DispatchResilienceStrategyAsync() " +
          "from ResiliencePluginBase for new dispatch. This class remains for typed IResilienceStrategy lookups.")]
public sealed class ResilienceStrategyRegistry : IResilienceStrategyRegistry
{
    private readonly BoundedDictionary<string, IResilienceStrategy> _strategies = new BoundedDictionary<string, IResilienceStrategy>(1000);

    /// <inheritdoc/>
    public IResilienceStrategy? GetStrategy(string strategyId)
    {
        return _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;
    }

    /// <inheritdoc/>
    public IReadOnlyList<IResilienceStrategy> GetAllStrategies()
    {
        return _strategies.Values.ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<IResilienceStrategy> GetStrategiesByCategory(string category)
    {
        return _strategies.Values
            .Where(s => s.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <inheritdoc/>
    public void Register(IResilienceStrategy strategy)
    {
        _strategies[strategy.StrategyId] = strategy;
    }

    /// <inheritdoc/>
    public void DiscoverStrategies(Assembly assembly)
    {
        var strategyTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IResilienceStrategy).IsAssignableFrom(t));

        foreach (var type in strategyTypes)
        {
            try
            {
                if (Activator.CreateInstance(type) is IResilienceStrategy strategy)
                {
                    Register(strategy);
                }
            }
            catch
            {
                // Skip types that can't be instantiated with parameterless constructor
            }
        }
    }
}
