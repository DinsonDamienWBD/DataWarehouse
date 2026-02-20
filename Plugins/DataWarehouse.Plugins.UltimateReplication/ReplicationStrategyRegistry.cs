using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DataWarehouse.SDK.Contracts.Replication;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateReplication
{
    /// <summary>
    /// Registry for discovering and managing replication strategies.
    /// Provides strategy discovery, selection, and lifecycle management.
    /// </summary>
    public sealed class ReplicationStrategyRegistry
    {
        private readonly BoundedDictionary<string, EnhancedReplicationStrategyBase> _strategies = new BoundedDictionary<string, EnhancedReplicationStrategyBase>(1000);
        private readonly BoundedDictionary<ConsistencyModel, List<string>> _byConsistencyModel = new BoundedDictionary<ConsistencyModel, List<string>>(1000);
        private readonly BoundedDictionary<string, List<string>> _byCapability = new BoundedDictionary<string, List<string>>(1000);

        /// <summary>
        /// Gets all registered strategy names.
        /// </summary>
        public IReadOnlyCollection<string> RegisteredStrategies => _strategies.Keys.ToArray();

        /// <summary>
        /// Gets the count of registered strategies.
        /// </summary>
        public int Count => _strategies.Count;

        /// <summary>
        /// Registers a replication strategy.
        /// </summary>
        /// <param name="strategy">The strategy to register.</param>
        /// <exception cref="ArgumentNullException">If strategy is null.</exception>
        public void Register(EnhancedReplicationStrategyBase strategy)
        {
            ArgumentNullException.ThrowIfNull(strategy);

            var name = strategy.Characteristics.StrategyName;
            _strategies[name] = strategy;

            // Index by consistency model
            var model = strategy.Characteristics.ConsistencyModel;
            _byConsistencyModel.AddOrUpdate(model,
                _ => new List<string> { name },
                (_, list) => { if (!list.Contains(name)) list.Add(name); return list; });

            // Index by capabilities
            IndexByCapabilities(strategy);
        }

        private void IndexByCapabilities(EnhancedReplicationStrategyBase strategy)
        {
            var name = strategy.Characteristics.StrategyName;
            var caps = strategy.Characteristics.Capabilities;

            if (caps.SupportsMultiMaster)
                AddToCapabilityIndex("multi-master", name);
            if (caps.SupportsAsyncReplication)
                AddToCapabilityIndex("async", name);
            if (caps.SupportsSyncReplication)
                AddToCapabilityIndex("sync", name);
            if (caps.IsGeoAware)
                AddToCapabilityIndex("geo-aware", name);
            if (strategy.Characteristics.SupportsVectorClocks)
                AddToCapabilityIndex("vector-clocks", name);
            if (strategy.Characteristics.SupportsDeltaSync)
                AddToCapabilityIndex("delta-sync", name);
            if (strategy.Characteristics.SupportsStreaming)
                AddToCapabilityIndex("streaming", name);
            if (strategy.Characteristics.SupportsAutoConflictResolution)
                AddToCapabilityIndex("auto-conflict-resolution", name);
        }

        private void AddToCapabilityIndex(string capability, string strategyName)
        {
            _byCapability.AddOrUpdate(capability,
                _ => new List<string> { strategyName },
                (_, list) => { if (!list.Contains(strategyName)) list.Add(strategyName); return list; });
        }

        /// <summary>
        /// Unregisters a strategy by name.
        /// </summary>
        /// <param name="strategyName">The name of the strategy to unregister.</param>
        /// <returns>True if the strategy was unregistered.</returns>
        public bool Unregister(string strategyName)
        {
            return _strategies.TryRemove(strategyName, out _);
        }

        /// <summary>
        /// Gets a strategy by name.
        /// </summary>
        /// <param name="strategyName">The name of the strategy.</param>
        /// <returns>The strategy, or null if not found.</returns>
        public EnhancedReplicationStrategyBase? Get(string strategyName)
        {
            return _strategies.GetValueOrDefault(strategyName);
        }

        /// <summary>
        /// Gets all strategies as key-value pairs.
        /// </summary>
        public IEnumerable<KeyValuePair<string, EnhancedReplicationStrategyBase>> GetAll()
        {
            return _strategies.ToArray();
        }

        /// <summary>
        /// Gets strategies by consistency model.
        /// </summary>
        /// <param name="model">The consistency model to filter by.</param>
        /// <returns>Strategies matching the consistency model.</returns>
        public IEnumerable<EnhancedReplicationStrategyBase> GetByConsistencyModel(ConsistencyModel model)
        {
            if (_byConsistencyModel.TryGetValue(model, out var names))
            {
                foreach (var name in names)
                {
                    if (_strategies.TryGetValue(name, out var strategy))
                        yield return strategy;
                }
            }
        }

        /// <summary>
        /// Gets strategies by capability.
        /// </summary>
        /// <param name="capability">The capability to filter by.</param>
        /// <returns>Strategies with the specified capability.</returns>
        public IEnumerable<EnhancedReplicationStrategyBase> GetByCapability(string capability)
        {
            if (_byCapability.TryGetValue(capability.ToLowerInvariant(), out var names))
            {
                foreach (var name in names)
                {
                    if (_strategies.TryGetValue(name, out var strategy))
                        yield return strategy;
                }
            }
        }

        /// <summary>
        /// Selects the best strategy for given requirements.
        /// </summary>
        /// <param name="preferredConsistency">Preferred consistency model.</param>
        /// <param name="maxLagMs">Maximum acceptable lag in milliseconds.</param>
        /// <param name="requireMultiMaster">Whether multi-master is required.</param>
        /// <param name="requireGeoAware">Whether geo-awareness is required.</param>
        /// <returns>The best matching strategy, or null if none match.</returns>
        public EnhancedReplicationStrategyBase? SelectBestStrategy(
            ConsistencyModel? preferredConsistency = null,
            long? maxLagMs = null,
            bool requireMultiMaster = false,
            bool requireGeoAware = false)
        {
            var candidates = _strategies.Values.AsEnumerable();

            if (preferredConsistency.HasValue)
                candidates = candidates.Where(s => s.Characteristics.ConsistencyModel == preferredConsistency.Value);

            if (maxLagMs.HasValue)
                candidates = candidates.Where(s => s.Characteristics.TypicalLagMs <= maxLagMs.Value);

            if (requireMultiMaster)
                candidates = candidates.Where(s => s.Characteristics.Capabilities.SupportsMultiMaster);

            if (requireGeoAware)
                candidates = candidates.Where(s => s.Characteristics.Capabilities.IsGeoAware);

            // Sort by: consistency strength, then lower lag, then more capabilities
            return candidates
                .OrderByDescending(s => (int)s.Characteristics.ConsistencyModel)
                .ThenBy(s => s.Characteristics.TypicalLagMs)
                .ThenByDescending(s => CountCapabilities(s))
                .FirstOrDefault();
        }

        private static int CountCapabilities(EnhancedReplicationStrategyBase strategy)
        {
            int count = 0;
            var caps = strategy.Characteristics.Capabilities;
            if (caps.SupportsMultiMaster) count++;
            if (caps.SupportsAsyncReplication) count++;
            if (caps.SupportsSyncReplication) count++;
            if (caps.IsGeoAware) count++;
            if (strategy.Characteristics.SupportsVectorClocks) count++;
            if (strategy.Characteristics.SupportsDeltaSync) count++;
            if (strategy.Characteristics.SupportsStreaming) count++;
            return count;
        }

        /// <summary>
        /// Discovers and registers all strategies from an assembly.
        /// </summary>
        /// <param name="assembly">The assembly to scan.</param>
        /// <returns>Number of strategies discovered and registered.</returns>
        public int DiscoverFromAssembly(Assembly? assembly = null)
        {
            assembly ??= Assembly.GetExecutingAssembly();
            int count = 0;

            var strategyTypes = assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(EnhancedReplicationStrategyBase).IsAssignableFrom(t));

            foreach (var type in strategyTypes)
            {
                try
                {
                    if (Activator.CreateInstance(type) is EnhancedReplicationStrategyBase strategy)
                    {
                        Register(strategy);
                        count++;
                    }
                }
                catch
                {
                    // Strategy failed to instantiate, skip
                }
            }

            return count;
        }

        /// <summary>
        /// Gets a summary of all registered strategies.
        /// </summary>
        public IEnumerable<StrategySummary> GetSummary()
        {
            return _strategies.Values.Select(s => new StrategySummary
            {
                Name = s.Characteristics.StrategyName,
                Description = s.Characteristics.Description,
                ConsistencyModel = s.Characteristics.ConsistencyModel,
                TypicalLagMs = s.Characteristics.TypicalLagMs,
                SupportsMultiMaster = s.Characteristics.Capabilities.SupportsMultiMaster,
                IsGeoAware = s.Characteristics.Capabilities.IsGeoAware,
                SupportsAutoConflictResolution = s.Characteristics.SupportsAutoConflictResolution,
                ConflictResolutionMethods = s.Characteristics.Capabilities.ConflictResolutionMethods
            });
        }
    }

    /// <summary>
    /// Summary information about a replication strategy.
    /// </summary>
    public sealed class StrategySummary
    {
        /// <summary>
        /// Strategy name.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Strategy description.
        /// </summary>
        public required string Description { get; init; }

        /// <summary>
        /// Consistency model.
        /// </summary>
        public required ConsistencyModel ConsistencyModel { get; init; }

        /// <summary>
        /// Typical replication lag in milliseconds.
        /// </summary>
        public required long TypicalLagMs { get; init; }

        /// <summary>
        /// Whether multi-master is supported.
        /// </summary>
        public required bool SupportsMultiMaster { get; init; }

        /// <summary>
        /// Whether geo-awareness is supported.
        /// </summary>
        public required bool IsGeoAware { get; init; }

        /// <summary>
        /// Whether auto conflict resolution is supported.
        /// </summary>
        public required bool SupportsAutoConflictResolution { get; init; }

        /// <summary>
        /// Supported conflict resolution methods.
        /// </summary>
        public required ConflictResolutionMethod[] ConflictResolutionMethods { get; init; }
    }
}
