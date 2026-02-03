namespace DataWarehouse.SDK.Contracts.Interface;

/// <summary>
/// Describes the capabilities supported by an interface strategy implementation.
/// </summary>
/// <remarks>
/// <para>
/// This record provides a declarative way to specify what features an interface strategy supports,
/// allowing the system to make intelligent routing and compatibility decisions at runtime.
/// </para>
/// <para>
/// Capabilities include protocol features (streaming, authentication), content handling (supported MIME types),
/// and operational limits (maximum request size, timeouts).
/// </para>
/// </remarks>
/// <param name="SupportsStreaming">
/// Indicates whether the interface strategy supports streaming requests and responses.
/// If true, the strategy can handle long-lived connections with incremental data transfer.
/// </param>
/// <param name="SupportsAuthentication">
/// Indicates whether the interface strategy supports authentication mechanisms.
/// If true, the strategy can validate credentials and enforce access control.
/// </param>
/// <param name="SupportedContentTypes">
/// A collection of MIME types that this interface strategy can handle.
/// Examples include "application/json", "application/xml", "application/protobuf", etc.
/// An empty collection indicates no specific content type restrictions.
/// </param>
/// <param name="MaxRequestSize">
/// The maximum size (in bytes) of a request that this interface strategy can handle.
/// Null indicates no specific size limit. Zero indicates the strategy does not support request bodies.
/// </param>
/// <param name="MaxResponseSize">
/// The maximum size (in bytes) of a response that this interface strategy can produce.
/// Null indicates no specific size limit. This helps prevent memory exhaustion attacks.
/// </param>
/// <param name="SupportsBidirectionalStreaming">
/// Indicates whether the interface strategy supports bidirectional streaming (e.g., gRPC streaming, WebSockets).
/// If true, both client and server can send multiple messages over a single connection concurrently.
/// </param>
/// <param name="SupportsMultiplexing">
/// Indicates whether the interface strategy supports request multiplexing over a single connection.
/// If true, multiple requests can be in-flight simultaneously over one connection (e.g., HTTP/2, HTTP/3).
/// </param>
/// <param name="DefaultTimeout">
/// The default timeout duration for requests handled by this strategy.
/// Null indicates no default timeout. Individual requests may override this value.
/// </param>
/// <param name="SupportsCancellation">
/// Indicates whether the interface strategy supports request cancellation.
/// If true, clients can cancel in-progress requests, and the strategy will stop processing them.
/// </param>
/// <param name="RequiresTLS">
/// Indicates whether the interface strategy requires TLS/SSL encryption for all communications.
/// If true, unencrypted connections will be rejected.
/// </param>
public record InterfaceCapabilities(
    bool SupportsStreaming,
    bool SupportsAuthentication,
    IReadOnlyList<string> SupportedContentTypes,
    long? MaxRequestSize = null,
    long? MaxResponseSize = null,
    bool SupportsBidirectionalStreaming = false,
    bool SupportsMultiplexing = false,
    TimeSpan? DefaultTimeout = null,
    bool SupportsCancellation = true,
    bool RequiresTLS = false
)
{
    /// <summary>
    /// Creates a default set of capabilities suitable for basic REST APIs.
    /// </summary>
    /// <returns>
    /// An <see cref="InterfaceCapabilities"/> instance with standard REST API capabilities.
    /// </returns>
    public static InterfaceCapabilities CreateRestDefaults() => new(
        SupportsStreaming: false,
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "application/json", "application/xml", "text/plain" },
        MaxRequestSize: 10 * 1024 * 1024, // 10 MB
        MaxResponseSize: 50 * 1024 * 1024, // 50 MB
        DefaultTimeout: TimeSpan.FromSeconds(30)
    );

    /// <summary>
    /// Creates a default set of capabilities suitable for gRPC services.
    /// </summary>
    /// <returns>
    /// An <see cref="InterfaceCapabilities"/> instance with standard gRPC capabilities.
    /// </returns>
    public static InterfaceCapabilities CreateGrpcDefaults() => new(
        SupportsStreaming: true,
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "application/grpc", "application/grpc+proto" },
        MaxRequestSize: 100 * 1024 * 1024, // 100 MB
        MaxResponseSize: 100 * 1024 * 1024, // 100 MB
        SupportsBidirectionalStreaming: true,
        SupportsMultiplexing: true,
        DefaultTimeout: TimeSpan.FromMinutes(5),
        RequiresTLS: true
    );

    /// <summary>
    /// Creates a default set of capabilities suitable for WebSocket connections.
    /// </summary>
    /// <returns>
    /// An <see cref="InterfaceCapabilities"/> instance with standard WebSocket capabilities.
    /// </returns>
    public static InterfaceCapabilities CreateWebSocketDefaults() => new(
        SupportsStreaming: true,
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "application/json", "application/octet-stream", "text/plain" },
        MaxRequestSize: null, // Streaming without specific size limit
        MaxResponseSize: null,
        SupportsBidirectionalStreaming: true,
        SupportsMultiplexing: false,
        DefaultTimeout: null, // Long-lived connections
        SupportsCancellation: true
    );

    /// <summary>
    /// Creates a default set of capabilities suitable for GraphQL endpoints.
    /// </summary>
    /// <returns>
    /// An <see cref="InterfaceCapabilities"/> instance with standard GraphQL capabilities.
    /// </returns>
    public static InterfaceCapabilities CreateGraphQLDefaults() => new(
        SupportsStreaming: true, // GraphQL subscriptions
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "application/json", "application/graphql" },
        MaxRequestSize: 5 * 1024 * 1024, // 5 MB
        MaxResponseSize: 50 * 1024 * 1024, // 50 MB
        DefaultTimeout: TimeSpan.FromSeconds(60)
    );
}
