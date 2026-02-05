using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DataWarehouse.SDK.Connectors
{
    /// <summary>
    /// Thread-safe registry for <see cref="IConnectionStrategy"/> instances.
    /// Supports registration, lookup by ID or category, and auto-discovery from assemblies.
    /// </summary>
    public sealed class ConnectionStrategyRegistry
    {
        private readonly ConcurrentDictionary<string, IConnectionStrategy> _strategies =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Registers a connection strategy. Overwrites any existing registration with the same ID.
        /// </summary>
        /// <param name="strategy">The strategy to register.</param>
        /// <exception cref="ArgumentNullException">If strategy is null.</exception>
        public void Register(IConnectionStrategy strategy)
        {
            ArgumentNullException.ThrowIfNull(strategy);
            _strategies[strategy.StrategyId] = strategy;
        }

        /// <summary>
        /// Unregisters a connection strategy by its ID.
        /// </summary>
        /// <param name="strategyId">The strategy ID to remove.</param>
        /// <returns>True if the strategy was removed, false if it was not found.</returns>
        public bool Unregister(string strategyId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
            return _strategies.TryRemove(strategyId, out _);
        }

        /// <summary>
        /// Gets a strategy by its unique identifier.
        /// </summary>
        /// <param name="strategyId">The strategy identifier (case-insensitive).</param>
        /// <returns>The strategy, or null if not found.</returns>
        public IConnectionStrategy? Get(string strategyId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
            return _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;
        }

        /// <summary>
        /// Gets all registered strategies.
        /// </summary>
        /// <returns>Read-only collection of all registered strategies.</returns>
        public IReadOnlyCollection<IConnectionStrategy> GetAll()
        {
            return _strategies.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets all strategies belonging to a specific connector category.
        /// </summary>
        /// <param name="category">The connector category to filter by.</param>
        /// <returns>Read-only collection of matching strategies.</returns>
        public IReadOnlyCollection<IConnectionStrategy> GetByCategory(ConnectorCategory category)
        {
            return _strategies.Values
                .Where(s => s.Category == category)
                .OrderBy(s => s.DisplayName)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Gets the total number of registered strategies.
        /// </summary>
        public int Count => _strategies.Count;

        /// <summary>
        /// Auto-discovers and registers all concrete <see cref="IConnectionStrategy"/> implementations
        /// found in the specified assemblies. Types must have a parameterless constructor.
        /// </summary>
        /// <param name="assemblies">Assemblies to scan for strategy implementations.</param>
        /// <returns>The number of newly discovered and registered strategies.</returns>
        public int AutoDiscover(params Assembly[] assemblies)
        {
            var strategyType = typeof(IConnectionStrategy);
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
                            if (Activator.CreateInstance(type) is IConnectionStrategy strategy)
                            {
                                Register(strategy);
                                discovered++;
                            }
                        }
                        catch
                        {
                            // Skip types that cannot be instantiated with a parameterless constructor
                        }
                    }
                }
                catch
                {
                    // Skip assemblies that cannot be scanned (e.g., dynamic or unloadable assemblies)
                }
            }

            return discovered;
        }
    }
}
