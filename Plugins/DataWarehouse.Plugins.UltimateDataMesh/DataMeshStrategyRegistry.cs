using DataWarehouse.SDK.Contracts.DataMesh;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataMesh;

/// <summary>
/// Thread-safe registry for data mesh strategies.
/// </summary>
/// <remarks>
/// This typed registry is superseded by the inherited strategy dispatch provided by
/// <see cref="DataWarehouse.SDK.Contracts.Hierarchy.DataManagementPluginBase"/>.
/// New plugins should use <c>RegisterDataManagementStrategy</c> and
/// <c>DispatchDataManagementStrategyAsync</c> from the base class instead.
/// This class is retained for the typed <see cref="DataWarehouse.SDK.Contracts.DataMesh.IDataMeshStrategy"/>
/// category-query and discovery operations not directly available on the generic base dispatch.
/// </remarks>
[Obsolete("Use the inherited RegisterDataManagementStrategy() and DispatchDataManagementStrategyAsync() " +
          "from DataManagementPluginBase for new dispatch. This class remains for typed IDataMeshStrategy " +
          "category-query operations (GetByCategory, AutoDiscover) not available on the generic base dispatch.")]
public sealed class DataMeshStrategyRegistry
{
    private readonly BoundedDictionary<string, IDataMeshStrategy> _strategies =
        new(1000);

    /// <summary>Registers a strategy.</summary>
    public void Register(IDataMeshStrategy strategy)
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
    public IDataMeshStrategy? Get(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        return _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;
    }

    /// <summary>Gets all registered strategies.</summary>
    public IReadOnlyCollection<IDataMeshStrategy> GetAll() => _strategies.Values.ToList().AsReadOnly();

    /// <summary>Gets strategies by category.</summary>
    public IReadOnlyCollection<IDataMeshStrategy> GetByCategory(DataMeshCategory category) =>
        _strategies.Values.Where(s => s.Category == category).OrderBy(s => s.DisplayName).ToList().AsReadOnly();

    /// <summary>Gets the count of registered strategies.</summary>
    public int Count => _strategies.Count;

    /// <summary>Auto-discovers and registers strategies from assemblies.</summary>
    public int AutoDiscover(params System.Reflection.Assembly[] assemblies)
    {
        var strategyType = typeof(IDataMeshStrategy);
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
                        if (Activator.CreateInstance(type) is IDataMeshStrategy strategy)
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
