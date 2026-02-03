namespace DataWarehouse.SDK.Contracts.Interface;

/// <summary>
/// Strategy interface for API protocol implementations.
/// Defines the contract for handling different interface protocols (REST, gRPC, GraphQL, WebSocket, etc.)
/// </summary>
/// <remarks>
/// <para>
/// This interface enables pluggable API protocol strategies within the Ultimate DataWarehouse system.
/// Implementations can provide support for various communication protocols while maintaining a consistent
/// abstraction layer for the warehouse infrastructure.
/// </para>
/// <para>
/// The interface supports lifecycle management (start/stop), request handling, and capability discovery
/// to allow the system to adapt to different protocol requirements dynamically.
/// </para>
/// </remarks>
public interface IInterfaceStrategy
{
    /// <summary>
    /// Gets the protocol type this strategy implements.
    /// </summary>
    /// <value>
    /// The protocol type (REST, gRPC, GraphQL, WebSocket, etc.) that this strategy handles.
    /// </value>
    InterfaceProtocol Protocol { get; }

    /// <summary>
    /// Gets the capabilities supported by this interface strategy.
    /// </summary>
    /// <value>
    /// An <see cref="InterfaceCapabilities"/> object describing what features this strategy supports,
    /// including streaming, authentication, content types, and size limits.
    /// </value>
    InterfaceCapabilities Capabilities { get; }

    /// <summary>
    /// Asynchronously starts the interface strategy and initializes any required resources.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token to cancel the start operation if needed.
    /// </param>
    /// <returns>
    /// A task representing the asynchronous start operation.
    /// </returns>
    /// <remarks>
    /// This method should initialize listeners, establish connections, and prepare the strategy
    /// to handle incoming requests. Implementations should be idempotent - multiple calls to
    /// StartAsync should not cause errors.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the strategy cannot be started due to configuration issues.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown if the operation is cancelled via the <paramref name="cancellationToken"/>.
    /// </exception>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously stops the interface strategy and releases any held resources.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token to cancel the stop operation if needed.
    /// </param>
    /// <returns>
    /// A task representing the asynchronous stop operation.
    /// </returns>
    /// <remarks>
    /// This method should gracefully shut down listeners, close connections, and clean up resources.
    /// Implementations should handle in-flight requests appropriately, either completing or canceling them.
    /// The method should be idempotent - multiple calls should not cause errors.
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// Thrown if the operation is cancelled via the <paramref name="cancellationToken"/>.
    /// </exception>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously handles an incoming interface request and produces a response.
    /// </summary>
    /// <param name="request">
    /// The <see cref="InterfaceRequest"/> containing the request details including method, path, headers, and body.
    /// </param>
    /// <param name="cancellationToken">
    /// A token to cancel the request handling if needed.
    /// </param>
    /// <returns>
    /// A task representing the asynchronous operation, containing an <see cref="InterfaceResponse"/> with the result.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the core method for processing requests in the strategy. Implementations should:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Parse and validate the request according to the protocol</description></item>
    /// <item><description>Route the request to appropriate handlers</description></item>
    /// <item><description>Execute the requested operation</description></item>
    /// <item><description>Format and return the response according to the protocol</description></item>
    /// <item><description>Handle errors gracefully and return appropriate error responses</description></item>
    /// </list>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="request"/> is null.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown if the operation is cancelled via the <paramref name="cancellationToken"/>.
    /// </exception>
    Task<InterfaceResponse> HandleRequestAsync(InterfaceRequest request, CancellationToken cancellationToken = default);
}
