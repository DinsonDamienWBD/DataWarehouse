using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.SDK.Contracts.Interface;

/// <summary>
/// Abstract base class for interface strategy implementations providing common infrastructure
/// for protocol handling, lifecycle management, and capability validation.
/// </summary>
/// <remarks>
/// <para>
/// This base class handles:
/// <list type="bullet">
/// <item><description>Lifecycle management (start/stop with idempotent semantics)</description></item>
/// <item><description>Request validation before dispatching to core handler</description></item>
/// <item><description>Thread-safe state management for running state</description></item>
/// <item><description>Intelligence integration for AI-enhanced protocol handling</description></item>
/// </list>
/// </para>
/// <para>
/// Derived classes must implement the abstract methods for starting, stopping, and handling
/// requests, and provide their specific protocol and capabilities via the constructor or properties.
/// </para>
/// </remarks>
public abstract class InterfaceStrategyBase : IInterfaceStrategy, IDisposable
{
    private int _isRunning;
    private bool _disposed;

    /// <inheritdoc/>
    public abstract InterfaceProtocol Protocol { get; }

    /// <inheritdoc/>
    public abstract InterfaceCapabilities Capabilities { get; }

    /// <summary>
    /// Gets a value indicating whether this strategy is currently running and accepting requests.
    /// </summary>
    protected bool IsRunning => Interlocked.CompareExchange(ref _isRunning, 0, 0) == 1;

    #region Lifecycle

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();

        // Idempotent: if already running, return
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) == 1)
            return;

        try
        {
            await StartAsyncCore(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Reset state on failure
            Interlocked.Exchange(ref _isRunning, 0);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Idempotent: if not running, return
        if (Interlocked.CompareExchange(ref _isRunning, 0, 1) == 0)
            return;

        await StopAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<InterfaceResponse> HandleRequestAsync(InterfaceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureNotDisposed();

        if (!IsRunning)
        {
            return InterfaceResponse.Error(503, "Interface strategy is not running. Call StartAsync first.");
        }

        try
        {
            return await HandleRequestAsyncCore(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return InterfaceResponse.InternalServerError($"Request processing failed: {ex.Message}");
        }
    }

    #endregion

    #region Abstract Core Methods

    /// <summary>
    /// Core implementation of strategy startup. Called after state validation.
    /// Initialize listeners, establish connections, and prepare to handle requests.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected abstract Task StartAsyncCore(CancellationToken cancellationToken);

    /// <summary>
    /// Core implementation of strategy shutdown. Called after state validation.
    /// Gracefully shut down listeners, close connections, and clean up resources.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected abstract Task StopAsyncCore(CancellationToken cancellationToken);

    /// <summary>
    /// Core implementation of request handling. Called after validation and running-state checks.
    /// Parse the request according to the protocol, route it, and return the response.
    /// </summary>
    /// <param name="request">The validated interface request.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The interface response.</returns>
    protected abstract Task<InterfaceResponse> HandleRequestAsyncCore(InterfaceRequest request, CancellationToken cancellationToken);

    #endregion

    #region Intelligence Integration

    /// <summary>
    /// Gets the message bus for Intelligence communication.
    /// </summary>
    protected IMessageBus? MessageBus { get; private set; }

    /// <summary>
    /// Configures Intelligence integration for this interface strategy.
    /// </summary>
    /// <param name="messageBus">Optional message bus for Intelligence communication.</param>
    public virtual void ConfigureIntelligence(IMessageBus? messageBus)
    {
        MessageBus = messageBus;
    }

    /// <summary>
    /// Gets a value indicating whether Intelligence integration is available.
    /// </summary>
    protected bool IsIntelligenceAvailable => MessageBus != null;

    /// <summary>
    /// Gets static knowledge about this interface strategy for Intelligence registration.
    /// </summary>
    /// <returns>A KnowledgeObject describing this strategy's capabilities.</returns>
    public virtual KnowledgeObject GetStrategyKnowledge()
    {
        return new KnowledgeObject
        {
            Id = $"interface.{Protocol.ToString().ToLowerInvariant()}",
            Topic = "interface.strategy",
            SourcePluginId = "sdk.interface",
            SourcePluginName = Protocol.ToString(),
            KnowledgeType = "capability",
            Description = $"{Protocol} interface strategy for API protocol handling",
            Payload = new Dictionary<string, object>
            {
                ["protocol"] = Protocol.ToString(),
                ["supportsStreaming"] = Capabilities.SupportsStreaming,
                ["supportsAuthentication"] = Capabilities.SupportsAuthentication,
                ["supportsBidirectional"] = Capabilities.SupportsBidirectionalStreaming
            },
            Tags = new[] { "interface", "protocol", "strategy", Protocol.ToString().ToLowerInvariant() }
        };
    }

    /// <summary>
    /// Gets the registered capability for this interface strategy.
    /// </summary>
    /// <returns>A RegisteredCapability describing this strategy.</returns>
    public virtual RegisteredCapability GetStrategyCapability()
    {
        return new RegisteredCapability
        {
            CapabilityId = $"interface.{Protocol.ToString().ToLowerInvariant()}",
            DisplayName = $"{Protocol} Interface",
            Description = $"{Protocol} interface strategy for API protocol handling",
            Category = CapabilityCategory.Transport,
            SubCategory = "Interface",
            PluginId = "sdk.interface",
            PluginName = Protocol.ToString(),
            PluginVersion = "1.0.0",
            Tags = new[] { "interface", "protocol", Protocol.ToString().ToLowerInvariant() },
            SemanticDescription = $"Use {Protocol} interface for API communication"
        };
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes resources used by this interface strategy.
    /// </summary>
    /// <param name="disposing">True if disposing managed resources; false if finalizing.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            // Stop if running (best effort, no async in dispose)
            Interlocked.Exchange(ref _isRunning, 0);
        }

        _disposed = true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);
    }

    #endregion
}
