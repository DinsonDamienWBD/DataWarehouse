using System.Reflection;
using DataWarehouse.SDK.Contracts.Transit;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataTransit;

/// <summary>
/// Thread-safe registry for data transit strategies.
/// Provides registration, lookup by ID/protocol/capability, and reflection-based auto-discovery.
/// </summary>
internal sealed class TransitStrategyRegistry
{
    private readonly BoundedDictionary<string, IDataTransitStrategy> _strategies = new BoundedDictionary<string, IDataTransitStrategy>(1000);

    /// <summary>
    /// Registers a transit strategy. Replaces any existing strategy with the same ID.
    /// </summary>
    /// <param name="strategy">The strategy to register.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="strategy"/> is null.</exception>
    public void Register(IDataTransitStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies[strategy.StrategyId] = strategy;
    }

    /// <summary>
    /// Gets a strategy by its unique identifier.
    /// </summary>
    /// <param name="strategyId">The strategy identifier to look up.</param>
    /// <returns>The strategy if found; null otherwise.</returns>
    public IDataTransitStrategy? GetStrategy(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        return _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;
    }

    /// <summary>
    /// Gets all registered strategies.
    /// </summary>
    /// <returns>A read-only collection of all strategies.</returns>
    public IReadOnlyCollection<IDataTransitStrategy> GetAll()
    {
        return _strategies.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets strategies that support a specific protocol.
    /// </summary>
    /// <param name="protocol">The protocol to filter by (e.g., "http2", "sftp", "grpc").</param>
    /// <returns>Strategies supporting the specified protocol.</returns>
    public IReadOnlyCollection<IDataTransitStrategy> GetByProtocol(string protocol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        return _strategies.Values
            .Where(s => s.Capabilities.SupportedProtocols
                .Any(p => p.Equals(protocol, StringComparison.OrdinalIgnoreCase)))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Gets strategies matching a capability predicate.
    /// </summary>
    /// <param name="predicate">The capability filter function.</param>
    /// <returns>Strategies matching the predicate.</returns>
    public IReadOnlyCollection<IDataTransitStrategy> GetByCapability(Func<TransitCapabilities, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return _strategies.Values
            .Where(s => predicate(s.Capabilities))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Gets the number of registered strategies.
    /// </summary>
    public int Count => _strategies.Count;

    /// <summary>
    /// Discovers and registers all <see cref="IDataTransitStrategy"/> implementations
    /// from the specified assemblies using reflection.
    /// </summary>
    /// <param name="assemblies">Assemblies to scan for strategy implementations.</param>
    /// <remarks>
    /// Only non-abstract, non-interface types with a public parameterless constructor
    /// that implement <see cref="IDataTransitStrategy"/> are instantiated and registered.
    /// Types that fail to instantiate are silently skipped.
    /// </remarks>
    public void DiscoverStrategies(params Assembly[] assemblies)
    {
        var strategyType = typeof(IDataTransitStrategy);

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
                        if (Activator.CreateInstance(type) is IDataTransitStrategy strategy)
                        {
                            Register(strategy);
                        }
                    }
                    catch
                    {
                        // Skip types that cannot be instantiated (no parameterless ctor, etc.)
                    }
                }
            }
            catch
            {
                // Skip assemblies that cannot be scanned (security, loading issues)
            }
        }
    }
}
