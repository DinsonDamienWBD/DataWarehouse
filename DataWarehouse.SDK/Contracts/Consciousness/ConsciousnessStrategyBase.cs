using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Contracts.Consciousness
{
    /// <summary>
    /// Categories of consciousness strategies, each addressing a different aspect
    /// of the Data Consciousness Score system.
    /// </summary>
    public enum ConsciousnessCategory
    {
        /// <summary>Strategies that score the value of data objects.</summary>
        ValueScoring,
        /// <summary>Strategies that score the liability of data objects.</summary>
        LiabilityScoring,
        /// <summary>Strategies that combine value and liability into a composite score.</summary>
        CompositeScoring,
        /// <summary>Strategies that automatically archive low-consciousness data.</summary>
        AutoArchive,
        /// <summary>Strategies that automatically purge high-liability, low-value data.</summary>
        AutoPurge,
        /// <summary>Strategies that discover and surface dark (uncharacterized) data.</summary>
        DarkDataDiscovery,
        /// <summary>Strategies that provide dashboard and reporting capabilities.</summary>
        Dashboard,
        /// <summary>Strategies that integrate consciousness scoring into data pipelines.</summary>
        PipelineIntegration
    }

    /// <summary>
    /// Declares the runtime capabilities of a consciousness strategy.
    /// </summary>
    /// <param name="SupportsAsync">Whether the strategy supports asynchronous operation.</param>
    /// <param name="SupportsBatch">Whether the strategy supports batch processing.</param>
    /// <param name="SupportsRealTime">Whether the strategy can score in real-time (sub-second).</param>
    /// <param name="SupportsStreaming">Whether the strategy supports streaming data input.</param>
    /// <param name="SupportsRetroactive">Whether the strategy can re-score historical data.</param>
    public sealed record ConsciousnessCapabilities(
        bool SupportsAsync = true,
        bool SupportsBatch = false,
        bool SupportsRealTime = false,
        bool SupportsStreaming = false,
        bool SupportsRetroactive = false);

    /// <summary>
    /// Interface for consciousness strategies. Implementations provide scoring,
    /// lifecycle management, or integration capabilities for the Data Consciousness system.
    /// </summary>
    public interface IConsciousnessStrategy
    {
        /// <summary>Unique machine-readable identifier for this strategy.</summary>
        string StrategyId { get; }

        /// <summary>Human-readable display name.</summary>
        string DisplayName { get; }

        /// <summary>The category this strategy belongs to.</summary>
        ConsciousnessCategory Category { get; }

        /// <summary>Runtime capabilities of this strategy.</summary>
        ConsciousnessCapabilities Capabilities { get; }

        /// <summary>Natural-language description of what this strategy does and when to use it.</summary>
        string SemanticDescription { get; }

        /// <summary>Tags for discovery, filtering, and grouping.</summary>
        string[] Tags { get; }
    }

    /// <summary>
    /// Base class for consciousness strategies. Provides production infrastructure:
    /// lifecycle management, health checks, counters, and graceful shutdown.
    /// Follows the DataGovernanceStrategyBase pattern (standalone base, does NOT inherit StrategyBase).
    /// </summary>
    public abstract class ConsciousnessStrategyBase : IConsciousnessStrategy
    {
        private readonly BoundedDictionary<string, long> _counters = new BoundedDictionary<string, long>(1000);
        private bool _initialized;
        private DateTime? _healthCacheExpiry;
        private ConsciousnessHealthStatus? _cachedHealth;

        /// <inheritdoc />
        public abstract string StrategyId { get; }

        /// <inheritdoc />
        public abstract string DisplayName { get; }

        /// <inheritdoc />
        public abstract ConsciousnessCategory Category { get; }

        /// <inheritdoc />
        public abstract ConsciousnessCapabilities Capabilities { get; }

        /// <inheritdoc />
        public abstract string SemanticDescription { get; }

        /// <inheritdoc />
        public abstract string[] Tags { get; }

        /// <summary>Gets whether this strategy has been initialized.</summary>
        public bool IsInitialized => _initialized;

        /// <summary>
        /// Initializes the strategy. Idempotent -- calling multiple times has no additional effect.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the initialization.</param>
        public virtual Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_initialized) return Task.CompletedTask;
            _initialized = true;
            IncrementCounter("initialized");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Shuts down the strategy gracefully, releasing any acquired resources.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the shutdown.</param>
        public virtual Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            if (!_initialized) return Task.CompletedTask;
            _initialized = false;
            IncrementCounter("shutdown");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Gets a cached health status, refreshing every 60 seconds.
        /// </summary>
        public ConsciousnessHealthStatus GetHealth()
        {
            if (_cachedHealth.HasValue && _healthCacheExpiry.HasValue && DateTime.UtcNow < _healthCacheExpiry.Value)
                return _cachedHealth.Value;

            _cachedHealth = _initialized ? ConsciousnessHealthStatus.Healthy : ConsciousnessHealthStatus.NotInitialized;
            _healthCacheExpiry = DateTime.UtcNow.AddSeconds(60);
            return _cachedHealth.Value;
        }

        /// <summary>
        /// Increments a named counter. Thread-safe.
        /// </summary>
        /// <param name="name">The counter name.</param>
        protected void IncrementCounter(string name)
        {
            _counters.AddOrUpdate(name, 1, (_, current) => Interlocked.Increment(ref current));
        }

        /// <summary>
        /// Gets all counter values as a read-only snapshot.
        /// </summary>
        public IReadOnlyDictionary<string, long> GetCounters() => new Dictionary<string, long>(_counters);
    }

    /// <summary>
    /// Health status for consciousness strategies.
    /// </summary>
    public enum ConsciousnessHealthStatus
    {
        /// <summary>Strategy is healthy and operational.</summary>
        Healthy,
        /// <summary>Strategy has not been initialized.</summary>
        NotInitialized,
        /// <summary>Strategy is in a degraded state.</summary>
        Degraded,
        /// <summary>Strategy is unhealthy and cannot operate.</summary>
        Unhealthy
    }

    /// <summary>
    /// Registry for consciousness strategies. Supports auto-discovery from assemblies,
    /// manual registration, and lookup by ID or category.
    /// </summary>
    public sealed class ConsciousnessStrategyRegistry
    {
        private readonly BoundedDictionary<string, IConsciousnessStrategy> _strategies = new BoundedDictionary<string, IConsciousnessStrategy>(1000);

        /// <summary>Gets the number of registered strategies.</summary>
        public int Count => _strategies.Count;

        /// <summary>
        /// Auto-discovers and registers all concrete <see cref="IConsciousnessStrategy"/> implementations
        /// from the specified assembly. Skips abstract types and types that fail instantiation.
        /// </summary>
        /// <param name="assembly">The assembly to scan.</param>
        /// <returns>The number of strategies discovered and registered.</returns>
        public int AutoDiscover(Assembly assembly)
        {
            var strategyType = typeof(IConsciousnessStrategy);
            var count = 0;

            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || !strategyType.IsAssignableFrom(type))
                    continue;

                try
                {
                    if (Activator.CreateInstance(type) is IConsciousnessStrategy strategy)
                    {
                        _strategies[strategy.StrategyId] = strategy;
                        count++;
                    }
                }
                catch { /* Non-critical: skip types that require constructor args */ }
            }

            return count;
        }

        /// <summary>
        /// Registers a strategy instance. Overwrites any existing strategy with the same ID.
        /// </summary>
        /// <param name="strategy">The strategy to register.</param>
        public void Register(IConsciousnessStrategy strategy)
        {
            _strategies[strategy.StrategyId] = strategy ?? throw new ArgumentNullException(nameof(strategy));
        }

        /// <summary>
        /// Gets a strategy by its unique identifier.
        /// </summary>
        /// <param name="strategyId">The strategy ID to look up.</param>
        /// <returns>The strategy, or null if not found.</returns>
        public IConsciousnessStrategy? GetById(string strategyId) =>
            _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;

        /// <summary>
        /// Gets all registered strategies in a specific category.
        /// </summary>
        /// <param name="category">The category to filter by.</param>
        /// <returns>A read-only list of matching strategies.</returns>
        public IReadOnlyList<IConsciousnessStrategy> GetByCategory(ConsciousnessCategory category) =>
            _strategies.Values.Where(s => s.Category == category).ToList().AsReadOnly();

        /// <summary>
        /// Gets all registered strategies.
        /// </summary>
        /// <returns>A read-only list of all strategies.</returns>
        public IReadOnlyList<IConsciousnessStrategy> GetAll() =>
            _strategies.Values.ToList().AsReadOnly();
    }
}
