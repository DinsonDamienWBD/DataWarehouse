using DataWarehouse.SDK.Primitives.Configuration;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;
namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Abstract root class for all strategy implementations. Provides lifecycle management,
    /// dispose pattern, metadata, and structured logging hooks. All domain strategy bases
    /// (Encryption, Storage, Compression, etc.) inherit from this class.
    /// <para>
    /// Per AD-05: Strategies are workers, NOT orchestrators. They do NOT have intelligence,
    /// capability registry, knowledge bank access, or message bus. The parent plugin handles
    /// all of that.
    /// </para>
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Root strategy base class -- AD-05 flat hierarchy, no intelligence")]
    public abstract class StrategyBase : IStrategy
    {
        private bool _disposed;
        protected volatile bool _initialized;
        private readonly object _lifecycleLock = new();
        private readonly BoundedDictionary<string, long> _counters = new BoundedDictionary<string, long>(1000);
        private readonly SemaphoreSlim _healthCacheLock = new(1, 1);
        private StrategyHealthCheckResult? _cachedHealth;
        private DateTime? _healthCacheExpiry;

        /// <summary>
        /// Gets the unique machine-readable identifier for this strategy.
        /// Each domain base or concrete strategy must provide this.
        /// </summary>
        public abstract string StrategyId { get; }

        /// <summary>
        /// Gets the human-readable display name of this strategy.
        /// Each domain base or concrete strategy must provide this.
        /// </summary>
        public abstract string Name { get; }

        /// <summary>
        /// Gets a description of what this strategy does and when to use it.
        /// Override to provide a custom description. Default returns "{Name} strategy".
        /// </summary>
        public virtual string Description => $"{Name} strategy";

        /// <summary>
        /// Gets whether this strategy is production-ready.
        /// Strategies that use in-memory simulation or lack real backend integration
        /// should override this to return false, preventing accidental production use.
        /// Default is true.
        /// </summary>
        public virtual bool IsProductionReady => true;

        /// <summary>
        /// Gets the strategy characteristics metadata (performance profile, hardware
        /// requirements, supported features). Override to provide domain-specific
        /// characteristics. The parent plugin reads these to register capabilities
        /// on the strategy's behalf. Default returns an empty dictionary.
        /// </summary>
        public virtual IReadOnlyDictionary<string, object> Characteristics { get; }
            = new Dictionary<string, object>();

        /// <summary>
        /// Gets the unified system-wide configuration for this strategy instance.
        /// Injected by the owning plugin during strategy initialization.
        /// Strategies can read configuration values but do not own them.
        /// </summary>
        protected DataWarehouseConfiguration SystemConfiguration { get; private set; } = ConfigurationPresets.CreateStandard();

        /// <summary>
        /// Injects the unified system configuration into this strategy.
        /// Called by the owning plugin during strategy initialization.
        /// </summary>
        internal void InjectConfiguration(DataWarehouseConfiguration config)
        {
            SystemConfiguration = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// Initializes the strategy, acquiring any resources needed for operation.
        /// This method is idempotent -- calling it multiple times has no additional effect.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the initialization.</param>
        /// <returns>A task representing the asynchronous initialization operation.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the strategy has been disposed.</exception>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();
            if (_initialized) return;
            lock (_lifecycleLock)
            {
                if (_initialized) return;
                _initialized = true;
            }
            try
            {
                await InitializeAsyncCore(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (_lifecycleLock) { _initialized = false; }
                throw;
            }
        }

        /// <summary>
        /// Shuts down the strategy, releasing all acquired resources.
        /// This method is safe to call even if the strategy was never initialized.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the shutdown.</param>
        /// <returns>A task representing the asynchronous shutdown operation.</returns>
        public async Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            if (!_initialized) return;
            await ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
            _initialized = false;
        }

        /// <summary>
        /// Override to perform domain-specific initialization logic.
        /// Called by <see cref="InitializeAsync"/> after guard checks.
        /// Default implementation does nothing.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the initialization.</param>
        /// <returns>A task representing the asynchronous initialization operation.</returns>
        protected virtual Task InitializeAsyncCore(CancellationToken cancellationToken)
            => Task.CompletedTask;

        /// <summary>
        /// Override to perform domain-specific shutdown logic.
        /// Called by <see cref="ShutdownAsync"/> after guard checks.
        /// Default implementation does nothing.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the shutdown.</param>
        /// <returns>A task representing the asynchronous shutdown operation.</returns>
        protected virtual Task ShutdownAsyncCore(CancellationToken cancellationToken)
            => Task.CompletedTask;

        /// <summary>
        /// Gets whether this strategy has been initialized and is ready for use.
        /// </summary>
        protected bool IsInitialized => _initialized;

        /// <summary>
        /// Releases all resources used by this strategy synchronously.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases all resources used by this strategy asynchronously.
        /// </summary>
        /// <returns>A value task representing the asynchronous dispose operation.</returns>
        public async ValueTask DisposeAsync()
        {
            await DisposeAsyncCore().ConfigureAwait(false);
            Dispose(disposing: false);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Override to dispose managed and unmanaged resources.
        /// Call <c>base.Dispose(disposing)</c> in overrides to ensure proper cleanup.
        /// </summary>
        /// <param name="disposing">True if disposing managed resources; false if finalizing.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                _healthCacheLock.Dispose();
            }
            _disposed = true;
        }

        /// <summary>
        /// Override to perform asynchronous resource cleanup.
        /// Called by <see cref="DisposeAsync"/> before the synchronous dispose.
        /// Default implementation does nothing.
        /// </summary>
        /// <returns>A value task representing the asynchronous dispose operation.</returns>
        protected virtual ValueTask DisposeAsyncCore()
            => ValueTask.CompletedTask;

        /// <summary>
        /// Throws <see cref="ObjectDisposedException"/> if this strategy has been disposed.
        /// Call this at the start of public methods that require the strategy to be alive.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown if the strategy has been disposed.</exception>
        protected void EnsureNotDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        #region Common Infrastructure Helpers

        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> if the strategy has not been initialized.
        /// Use at the start of methods that require initialization.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if the strategy has not been initialized.</exception>
        protected void ThrowIfNotInitialized()
        {
            if (!_initialized)
                throw new InvalidOperationException($"Strategy '{StrategyId}' has not been initialized. Call InitializeAsync first.");
        }

        /// <summary>
        /// Ensures the strategy is initialized using a double-check lock pattern.
        /// If not yet initialized, acquires the lifecycle lock and calls the provided initialization function.
        /// Thread-safe and idempotent.
        /// </summary>
        /// <param name="initCore">The initialization function to call if not yet initialized.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        protected async Task EnsureInitializedAsync(Func<Task> initCore)
        {
            if (_initialized) return;
            // Use a simple async-safe pattern with the lifecycle lock
            bool needsInit = false;
            lock (_lifecycleLock)
            {
                if (!_initialized)
                    needsInit = true;
            }
            if (needsInit)
            {
                await initCore().ConfigureAwait(false);
                _initialized = true;
            }
        }

        /// <summary>
        /// Increments a named counter by 1. Thread-safe using <see cref="Interlocked"/>.
        /// Use for tracking operation counts (e.g., reads, writes, encryptions).
        /// </summary>
        /// <param name="name">The counter name.</param>
        protected void IncrementCounter(string name)
        {
            _counters.AddOrUpdate(name, 1, (_, current) => Interlocked.Increment(ref current));
        }

        /// <summary>
        /// Increments a named counter by <paramref name="amount"/> in a single atomic operation.
        /// Prefer this overload over calling <see cref="IncrementCounter(string)"/> in a loop.
        /// Thread-safe.
        /// </summary>
        /// <param name="name">The counter name.</param>
        /// <param name="amount">The amount to add (must be &gt;= 0).</param>
        protected void IncrementCounter(string name, long amount)
        {
            if (amount <= 0) return;
            // P2-2274: Use AddOrUpdate with the full amount to avoid O(n) loop over Interlocked.Increment.
            _counters.AddOrUpdate(name, amount, (_, current) => current + amount);
        }

        /// <summary>
        /// Gets the current value of a named counter.
        /// Returns 0 if the counter does not exist.
        /// </summary>
        /// <param name="name">The counter name.</param>
        /// <returns>The current counter value.</returns>
        protected long GetCounter(string name)
        {
            return _counters.GetValueOrDefault(name, 0);
        }

        /// <summary>
        /// Gets a snapshot of all counters as a read-only dictionary.
        /// </summary>
        /// <returns>A read-only dictionary of counter names to values.</returns>
        protected IReadOnlyDictionary<string, long> GetAllCounters()
        {
            return new Dictionary<string, long>(_counters);
        }

        /// <summary>
        /// Resets all counters to zero by clearing the counter dictionary.
        /// </summary>
        protected void ResetCounters()
        {
            _counters.Clear();
        }

        /// <summary>
        /// Executes an operation with retry using exponential backoff and jitter.
        /// Retries only on transient exceptions as determined by <see cref="IsTransientException"/>.
        /// </summary>
        /// <typeparam name="T">The return type of the operation.</typeparam>
        /// <param name="operation">The async operation to execute.</param>
        /// <param name="maxRetries">Maximum number of retry attempts (default: 3).</param>
        /// <param name="baseDelay">Base delay between retries (default: 100ms). Actual delay increases exponentially.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The result of the operation.</returns>
        /// <exception cref="AggregateException">Thrown with all collected exceptions if all retries are exhausted.</exception>
        protected async Task<T> ExecuteWithRetryAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            int maxRetries = 3,
            TimeSpan? baseDelay = null,
            CancellationToken ct = default)
        {
            var delay = baseDelay ?? TimeSpan.FromMilliseconds(100);
            var exceptions = new List<Exception>();

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    return await operation(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < maxRetries && IsTransientException(ex))
                {
                    exceptions.Add(ex);
                    // Exponential backoff with jitter
                    var jitter = TimeSpan.FromMilliseconds(
                        System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 50));
                    var backoff = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * Math.Pow(2, attempt)) + jitter;
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
            }

            throw new AggregateException($"Operation failed after {maxRetries + 1} attempts", exceptions);
        }

        /// <summary>
        /// Determines whether an exception is transient and the operation should be retried.
        /// Override in domain strategy bases to classify domain-specific transient errors.
        /// Default returns false (no retries).
        /// </summary>
        /// <param name="ex">The exception to evaluate.</param>
        /// <returns>True if the exception is transient; false otherwise.</returns>
        protected virtual bool IsTransientException(Exception ex) => false;

        /// <summary>
        /// Gets a cached health check result, refreshing it if expired.
        /// Thread-safe with configurable cache duration.
        /// </summary>
        /// <param name="healthCheck">The health check function to call when cache is expired.</param>
        /// <param name="cacheDuration">How long to cache the result (default: 30 seconds).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The cached or freshly-computed health check result.</returns>
        protected async Task<StrategyHealthCheckResult> GetCachedHealthAsync(
            Func<CancellationToken, Task<StrategyHealthCheckResult>> healthCheck,
            TimeSpan? cacheDuration = null,
            CancellationToken ct = default)
        {
            var duration = cacheDuration ?? TimeSpan.FromSeconds(30);

            if (_cachedHealth != null && _healthCacheExpiry.HasValue && DateTime.UtcNow < _healthCacheExpiry.Value)
                return _cachedHealth;

            await _healthCacheLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Double-check after acquiring lock
                if (_cachedHealth != null && _healthCacheExpiry.HasValue && DateTime.UtcNow < _healthCacheExpiry.Value)
                    return _cachedHealth;

                _cachedHealth = await healthCheck(ct).ConfigureAwait(false);
                _healthCacheExpiry = DateTime.UtcNow.Add(duration);
                return _cachedHealth;
            }
            finally
            {
                _healthCacheLock.Release();
            }
        }

        #endregion

        #region Legacy MessageBus Compatibility

        // Intelligence interface methods (GetStrategyKnowledge, GetStrategyCapability) removed
        // per AD-05 (Phase 25b). ConfigureIntelligence, MessageBus, and IsIntelligenceAvailable
        // are PRESERVED because ~55 strategy files and 9 ConfigureIntelligence overrides
        // actively reference them. Phase 27 migrates MessageBus to plugin-level injection.

        /// <summary>
        /// Configures the message bus for strategy-level event publishing.
        /// Override in domain bases or concrete strategies that need MessageBus access.
        /// </summary>
        /// <param name="messageBus">The message bus instance, or null to disconnect.</param>
        public virtual void ConfigureIntelligence(IMessageBus? messageBus)
        {
            MessageBus = messageBus;
        }

        /// <summary>
        /// Message bus for strategy-level event publishing. Always null unless explicitly
        /// configured via <see cref="ConfigureIntelligence"/>. Strategies that use this
        /// should guard access with <see cref="IsIntelligenceAvailable"/>.
        /// Phase 27 will migrate this to plugin-level dependency injection.
        /// </summary>
        protected IMessageBus? MessageBus { get; private set; }

        /// <summary>
        /// Gets whether a message bus is available for event publishing.
        /// Returns false unless <see cref="ConfigureIntelligence"/> has been called with a non-null bus.
        /// Phase 27 will migrate this to plugin-level dependency injection.
        /// </summary>
        protected bool IsIntelligenceAvailable => MessageBus != null;

        #endregion
    }

    /// <summary>
    /// Represents the result of a strategy-level health check.
    /// Used by <see cref="StrategyBase.GetCachedHealthAsync"/> for cached health monitoring.
    /// </summary>
    /// <param name="IsHealthy">Whether the strategy is healthy.</param>
    /// <param name="Message">Optional message describing the health status.</param>
    /// <param name="Details">Optional dictionary of detailed health information.</param>
    public record StrategyHealthCheckResult(
        bool IsHealthy,
        string? Message = null,
        IReadOnlyDictionary<string, object>? Details = null);
}
