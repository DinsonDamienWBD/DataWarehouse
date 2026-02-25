using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Generic thread-safe strategy registry that replaces the three bespoke typed registries
    /// (EncryptionStrategyRegistry, ConnectionStrategyRegistry, ConsciousnessStrategyRegistry).
    ///
    /// The key selector pattern accommodates any strategy type: IStrategy derivatives that have
    /// StrategyId, as well as independent strategy interfaces (IEncryptionStrategy, IConsciousnessStrategy)
    /// that define their own StrategyId property without inheriting IStrategy.
    /// </summary>
    /// <typeparam name="TStrategy">The strategy type. Must be a reference type.</typeparam>
    [SdkCompatibility("5.0.0", Notes = "Generic strategy registry replacing bespoke typed registries")]
    public sealed class StrategyRegistry<TStrategy> where TStrategy : class
    {
        private readonly BoundedDictionary<string, TStrategy> _strategies =
            new BoundedDictionary<string, TStrategy>(1000);

        private readonly Func<TStrategy, string> _keySelector;
        private volatile string? _defaultStrategyId;
        private readonly object _defaultLock = new();

        // -------------------------------------------------------------------------
        // Constructors
        // -------------------------------------------------------------------------

        /// <summary>
        /// Initializes a new <see cref="StrategyRegistry{TStrategy}"/> with an explicit key selector.
        /// </summary>
        /// <param name="keySelector">
        /// Function that extracts the unique string key from a strategy instance.
        /// For IStrategy types, use <c>s => s.StrategyId</c>.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="keySelector"/> is null.</exception>
        public StrategyRegistry(Func<TStrategy, string> keySelector)
        {
            _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        }

        // -------------------------------------------------------------------------
        // Core CRUD
        // -------------------------------------------------------------------------

        /// <summary>
        /// Registers a strategy. Overwrites any existing registration with the same key.
        /// </summary>
        /// <param name="strategy">The strategy instance to register.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="strategy"/> is null.</exception>
        public void Register(TStrategy strategy)
        {
            ArgumentNullException.ThrowIfNull(strategy);
            var key = _keySelector(strategy);
            _strategies[key] = strategy;
        }

        /// <summary>
        /// Unregisters a strategy by its ID.
        /// </summary>
        /// <param name="strategyId">The strategy ID to remove.</param>
        /// <returns>True if removed; false if not found.</returns>
        public bool Unregister(string strategyId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
            var removed = _strategies.TryRemove(strategyId, out _);

            // Clear default if the removed strategy was the default
            if (removed && _defaultStrategyId == strategyId)
            {
                lock (_defaultLock)
                {
                    if (_defaultStrategyId == strategyId)
                        _defaultStrategyId = null;
                }
            }

            return removed;
        }

        /// <summary>
        /// Gets a strategy by its unique identifier.
        /// </summary>
        /// <param name="strategyId">The strategy ID to look up.</param>
        /// <returns>The strategy, or null if not found.</returns>
        public TStrategy? Get(string strategyId)
        {
            if (string.IsNullOrWhiteSpace(strategyId)) return null;
            return _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;
        }

        /// <summary>
        /// Gets all registered strategies as a read-only collection.
        /// </summary>
        public IReadOnlyCollection<TStrategy> GetAll()
        {
            return _strategies.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets strategies matching a predicate.
        /// </summary>
        /// <param name="predicate">Filter function applied to each strategy.</param>
        /// <returns>Read-only collection of matching strategies.</returns>
        public IReadOnlyCollection<TStrategy> GetByPredicate(Func<TStrategy, bool> predicate)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            return _strategies.Values.Where(predicate).ToList().AsReadOnly();
        }

        /// <summary>
        /// Checks whether a strategy with the given ID is registered.
        /// </summary>
        /// <param name="strategyId">The strategy ID to check.</param>
        /// <returns>True if registered; false otherwise.</returns>
        public bool ContainsStrategy(string strategyId)
        {
            if (string.IsNullOrWhiteSpace(strategyId)) return false;
            return _strategies.ContainsKey(strategyId);
        }

        /// <summary>
        /// Gets the total number of registered strategies.
        /// </summary>
        public int Count => _strategies.Count;

        // -------------------------------------------------------------------------
        // Default Strategy
        // -------------------------------------------------------------------------

        /// <summary>
        /// Gets or sets the default strategy ID.
        /// Volatile for thread-safe reads without locking.
        /// Use <see cref="SetDefault"/> to validate before setting.
        /// </summary>
        public string? DefaultStrategyId
        {
            get => _defaultStrategyId;
            set => _defaultStrategyId = value;
        }

        /// <summary>
        /// Sets the default strategy ID after validating it is registered.
        /// </summary>
        /// <param name="strategyId">The strategy ID to set as default.</param>
        /// <exception cref="ArgumentException">Thrown if the strategy is not registered.</exception>
        public void SetDefault(string strategyId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
            if (!ContainsStrategy(strategyId))
                throw new ArgumentException(
                    $"Cannot set default: strategy '{strategyId}' is not registered.", nameof(strategyId));
            _defaultStrategyId = strategyId;
        }

        /// <summary>
        /// Returns the strategy for <see cref="DefaultStrategyId"/>.
        /// </summary>
        /// <returns>The default strategy.</returns>
        /// <exception cref="InvalidOperationException">Thrown if no default is set or the default strategy is not found.</exception>
        public TStrategy GetDefault()
        {
            var id = _defaultStrategyId;
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("No default strategy ID is set.");

            var strategy = Get(id);
            if (strategy is null)
                throw new InvalidOperationException(
                    $"Default strategy '{id}' is not registered.");

            return strategy;
        }

        // -------------------------------------------------------------------------
        // Assembly Discovery
        // -------------------------------------------------------------------------

        /// <summary>
        /// Scans the given assemblies for non-abstract concrete types that implement
        /// <typeparamref name="TStrategy"/>, instantiates them via
        /// <see cref="Activator.CreateInstance(Type)"/>, and registers each one.
        /// Types that cannot be instantiated are silently skipped.
        /// </summary>
        /// <param name="assemblies">Assemblies to scan.</param>
        /// <returns>Number of strategies successfully discovered and registered.</returns>
        public int DiscoverFromAssembly(params Assembly[] assemblies)
        {
            if (assemblies is null || assemblies.Length == 0) return 0;

            var strategyType = typeof(TStrategy);
            int discovered = 0;

            foreach (var assembly in assemblies)
            {
                if (assembly is null) continue;

                IEnumerable<Type> types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // Partial load — process what we can
                    types = ex.Types.Where(t => t is not null)!;
                }

                foreach (var type in types)
                {
                    if (type.IsAbstract || type.IsInterface || !strategyType.IsAssignableFrom(type))
                        continue;

                    try
                    {
                        var instance = (TStrategy?)Activator.CreateInstance(type);
                        if (instance is null) continue;
                        Register(instance);
                        discovered++;
                    }
                    catch
                    {
                        // Silent skip on instantiation failures (matching existing pattern)
                    }
                }
            }

            return discovered;
        }
    }
}
