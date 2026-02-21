using DataWarehouse.SDK.Contracts.DataLake;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataLake;

/// <summary>Thread-safe registry for data lake strategies.</summary>
/// <remarks>
/// <b>Migration note:</b> This inline registry is superseded by the inherited
/// DataManagementPluginBase.RegisterDataManagementStrategy / PluginBase.StrategyRegistry
/// for unified dispatch. Strategies are dual-registered: this typed registry is retained
/// for category-filtered lookups; the base IStrategy registry is used for cross-plugin dispatch.
/// DataLakeStrategyBase : StrategyBase : IStrategy enables the migration.
/// </remarks>
[Obsolete("Superseded by DataManagementPluginBase.RegisterDataManagementStrategy / PluginBase.StrategyRegistry. Retained for category-typed lookups only.")]
public sealed class DataLakeStrategyRegistry
{
    private readonly BoundedDictionary<string, IDataLakeStrategy> _strategies =
        new(1000);

    /// <summary>Registers a strategy.</summary>
    public void Register(IDataLakeStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies[strategy.StrategyId] = strategy;
    }

    /// <summary>Unregisters a strategy by ID.</summary>
    public bool Unregister(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        return _strategies.TryRemove(strategyId, out _);
    }

    /// <summary>Gets a strategy by ID.</summary>
    public IDataLakeStrategy? Get(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        return _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;
    }

    /// <summary>Gets all registered strategies.</summary>
    public IReadOnlyCollection<IDataLakeStrategy> GetAll() => _strategies.Values.ToList().AsReadOnly();

    /// <summary>Gets strategies by category.</summary>
    public IReadOnlyCollection<IDataLakeStrategy> GetByCategory(DataLakeCategory category) =>
        _strategies.Values.Where(s => s.Category == category).OrderBy(s => s.DisplayName).ToList().AsReadOnly();

    /// <summary>Gets the count of registered strategies.</summary>
    public int Count => _strategies.Count;

    /// <summary>Auto-discovers and registers strategies from assemblies.</summary>
    public int AutoDiscover(params System.Reflection.Assembly[] assemblies)
    {
        var strategyType = typeof(IDataLakeStrategy);
        int discovered = 0;

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
                        if (Activator.CreateInstance(type) is IDataLakeStrategy strategy)
                        {
                            Register(strategy);
                            discovered++;
                        }
                    }
                    catch { /* Skip types that cannot be instantiated */ }
                }
            }
            catch { /* Skip assemblies that cannot be scanned */ }
        }

        return discovered;
    }
}
