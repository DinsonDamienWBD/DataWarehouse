using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
        protected bool _initialized;
        private readonly object _lifecycleLock = new();

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
        /// Gets the strategy characteristics metadata (performance profile, hardware
        /// requirements, supported features). Override to provide domain-specific
        /// characteristics. The parent plugin reads these to register capabilities
        /// on the strategy's behalf. Default returns an empty dictionary.
        /// </summary>
        public virtual IReadOnlyDictionary<string, object> Characteristics { get; }
            = new Dictionary<string, object>();

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
            }
            await InitializeAsyncCore(cancellationToken).ConfigureAwait(false);
            _initialized = true;
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
                // Subclasses override to dispose managed resources
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
}
