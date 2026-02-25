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
    /// Base class for consciousness strategies. Extends <see cref="StrategyBase"/> for shared
    /// lifecycle management, health checks, counters, and graceful shutdown. Implements
    /// <see cref="IConsciousnessStrategy"/> for domain-specific consciousness members.
    /// </summary>
    public abstract class ConsciousnessStrategyBase : StrategyBase, IConsciousnessStrategy
    {
        // StrategyBase provides: _initialized, _counters, _healthCacheLock,
        // InitializeAsync, ShutdownAsync, Dispose, IncrementCounter, GetCounter, GetAllCounters,
        // GetCachedHealthAsync, EnsureNotDisposed, ThrowIfNotInitialized.

        /// <inheritdoc />
        public abstract override string StrategyId { get; }

        /// <inheritdoc cref="IConsciousnessStrategy.DisplayName" />
        public abstract string DisplayName { get; }

        /// <summary>
        /// Maps <see cref="StrategyBase.Name"/> to <see cref="DisplayName"/> so the
        /// generic strategy infrastructure can read a human-readable label.
        /// </summary>
        public override string Name => DisplayName;

        /// <inheritdoc />
        public abstract ConsciousnessCategory Category { get; }

        /// <inheritdoc />
        public abstract ConsciousnessCapabilities Capabilities { get; }

        /// <inheritdoc />
        public abstract string SemanticDescription { get; }

        /// <inheritdoc />
        public abstract string[] Tags { get; }

        /// <summary>Gets whether this strategy has been initialized.</summary>
        public new bool IsInitialized => _initialized;

        /// <summary>
        /// Gets a cached health status for this consciousness strategy, reflecting
        /// whether the strategy has been initialized.
        /// </summary>
        public ConsciousnessHealthStatus GetHealth()
        {
            return _initialized
                ? ConsciousnessHealthStatus.Healthy
                : ConsciousnessHealthStatus.NotInitialized;
        }

        /// <summary>
        /// Gets all counter values as a read-only snapshot.
        /// Delegates to the inherited counter infrastructure from <see cref="StrategyBase"/>.
        /// </summary>
        public IReadOnlyDictionary<string, long> GetCounters() => GetAllCounters();
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
    /// Registry for consciousness strategies. Delegates to the generic
    /// <see cref="StrategyRegistry{TStrategy}"/> internally. Supports auto-discovery
    /// from assemblies, manual registration, and lookup by ID or category.
    /// </summary>
    public sealed class ConsciousnessStrategyRegistry
    {
        private readonly StrategyRegistry<IConsciousnessStrategy> _inner =
            new(s => s.StrategyId);

        /// <summary>Gets the number of registered strategies.</summary>
        public int Count => _inner.Count;

        /// <summary>
        /// Auto-discovers and registers all concrete <see cref="IConsciousnessStrategy"/> implementations
        /// from the specified assembly. Skips abstract types and types that fail instantiation.
        /// </summary>
        /// <param name="assembly">The assembly to scan.</param>
        /// <returns>The number of strategies discovered and registered.</returns>
        public int AutoDiscover(Assembly assembly)
        {
            return _inner.DiscoverFromAssembly(assembly);
        }

        /// <summary>
        /// Registers a strategy instance. Overwrites any existing strategy with the same ID.
        /// </summary>
        /// <param name="strategy">The strategy to register.</param>
        public void Register(IConsciousnessStrategy strategy)
        {
            _inner.Register(strategy);
        }

        /// <summary>
        /// Gets a strategy by its unique identifier.
        /// </summary>
        /// <param name="strategyId">The strategy ID to look up.</param>
        /// <returns>The strategy, or null if not found.</returns>
        public IConsciousnessStrategy? GetById(string strategyId) =>
            _inner.Get(strategyId);

        /// <summary>
        /// Gets all registered strategies in a specific category.
        /// </summary>
        /// <param name="category">The category to filter by.</param>
        /// <returns>A read-only list of matching strategies.</returns>
        public IReadOnlyList<IConsciousnessStrategy> GetByCategory(ConsciousnessCategory category) =>
            _inner.GetByPredicate(s => s.Category == category).ToList().AsReadOnly();

        /// <summary>
        /// Gets all registered strategies.
        /// </summary>
        /// <returns>A read-only list of all strategies.</returns>
        public IReadOnlyList<IConsciousnessStrategy> GetAll() =>
            _inner.GetAll().ToList().AsReadOnly();
    }
}
