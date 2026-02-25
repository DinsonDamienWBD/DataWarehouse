using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Connectors
{
    /// <summary>
    /// Thread-safe registry for <see cref="IConnectionStrategy"/> instances.
    /// Delegates to the generic <see cref="StrategyRegistry{TStrategy}"/> internally.
    /// Supports registration, lookup by ID or category, and auto-discovery from assemblies.
    /// </summary>
    public sealed class ConnectionStrategyRegistry
    {
        private readonly StrategyRegistry<IConnectionStrategy> _inner =
            new(s => s.StrategyId);

        /// <summary>
        /// Registers a connection strategy. Overwrites any existing registration with the same ID.
        /// </summary>
        /// <param name="strategy">The strategy to register.</param>
        /// <exception cref="ArgumentNullException">If strategy is null.</exception>
        public void Register(IConnectionStrategy strategy)
        {
            _inner.Register(strategy);
        }

        /// <summary>
        /// Unregisters a connection strategy by its ID.
        /// </summary>
        /// <param name="strategyId">The strategy ID to remove.</param>
        /// <returns>True if the strategy was removed, false if it was not found.</returns>
        public bool Unregister(string strategyId)
        {
            return _inner.Unregister(strategyId);
        }

        /// <summary>
        /// Gets a strategy by its unique identifier.
        /// </summary>
        /// <param name="strategyId">The strategy identifier.</param>
        /// <returns>The strategy, or null if not found.</returns>
        public IConnectionStrategy? Get(string strategyId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
            return _inner.Get(strategyId);
        }

        /// <summary>
        /// Gets all registered strategies.
        /// </summary>
        /// <returns>Read-only collection of all registered strategies.</returns>
        public IReadOnlyCollection<IConnectionStrategy> GetAll()
        {
            return _inner.GetAll();
        }

        /// <summary>
        /// Gets all strategies belonging to a specific connector category.
        /// </summary>
        /// <param name="category">The connector category to filter by.</param>
        /// <returns>Read-only collection of matching strategies.</returns>
        public IReadOnlyCollection<IConnectionStrategy> GetByCategory(ConnectorCategory category)
        {
            return _inner.GetByPredicate(s => s.Category == category)
                .OrderBy(s => s.DisplayName)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Gets the total number of registered strategies.
        /// </summary>
        public int Count => _inner.Count;

        /// <summary>
        /// Auto-discovers and registers all concrete <see cref="IConnectionStrategy"/> implementations
        /// found in the specified assemblies. Types must have a parameterless constructor.
        /// </summary>
        /// <param name="assemblies">Assemblies to scan for strategy implementations.</param>
        /// <returns>The number of newly discovered and registered strategies.</returns>
        public int AutoDiscover(params Assembly[] assemblies)
        {
            return _inner.DiscoverFromAssembly(assemblies);
        }
    }
}
