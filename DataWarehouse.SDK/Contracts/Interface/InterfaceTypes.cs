namespace DataWarehouse.SDK.Contracts.Interface;

/// <summary>
/// Represents an incoming interface request to be processed by an interface strategy.
/// </summary>
/// <remarks>
/// <para>
/// This record encapsulates all information needed to process a request in a protocol-agnostic way.
/// Different interface strategies can map protocol-specific request formats to this common structure.
/// </para>
/// </remarks>
/// <param name="Method">
/// The HTTP method or equivalent operation type (GET, POST, PUT, DELETE, etc.).
/// For protocols without HTTP semantics, this can be mapped to an appropriate equivalent.
/// </param>
/// <param name="Path">
/// The request path or resource identifier. For REST, this is the URL path.
/// For gRPC, this might be the service and method name. For GraphQL, this might be the query endpoint.
/// </param>
/// <param name="Headers">
/// A dictionary of request headers. Protocol-specific metadata should be mapped to appropriate header entries.
/// Keys are case-insensitive. Common headers include Content-Type, Authorization, Accept, etc.
/// </param>
/// <param name="Body">
/// The request body as a read-only memory span of bytes.
/// For requests without a body (e.g., GET requests), this should be empty.
/// </param>
/// <param name="QueryParameters">
/// A dictionary of query string parameters extracted from the request URL or equivalent.
/// For protocols without query strings, this can represent filter or option parameters.
/// </param>
/// <param name="Protocol">
/// The protocol this request originated from. Useful for protocol-specific processing.
/// </param>
/// <param name="Metadata">
/// Additional protocol-specific metadata that doesn't fit into standard properties.
/// This allows strategies to pass custom data without breaking the common abstraction.
/// </param>
public record InterfaceRequest(
    HttpMethod Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> Body,
    IReadOnlyDictionary<string, string> QueryParameters,
    InterfaceProtocol Protocol,
    IReadOnlyDictionary<string, object>? Metadata = null
)
{
    /// <summary>
    /// Gets the Content-Type header value, if present.
    /// </summary>
    public string? ContentType => Headers.TryGetValue("Content-Type", out var value) ? value : null;

    /// <summary>
    /// Gets the Authorization header value, if present.
    /// </summary>
    public string? Authorization => Headers.TryGetValue("Authorization", out var value) ? value : null;

    /// <summary>
    /// Attempts to get a header value by key (case-insensitive).
    /// </summary>
    /// <param name="key">The header key to retrieve.</param>
    /// <param name="value">The header value if found, otherwise null.</param>
    /// <returns>True if the header was found, false otherwise.</returns>
    public bool TryGetHeader(string key, out string? value) =>
        Headers.TryGetValue(key, out value);

    /// <summary>
    /// Attempts to get a query parameter value by key (case-sensitive).
    /// </summary>
    /// <param name="key">The query parameter key to retrieve.</param>
    /// <param name="value">The parameter value if found, otherwise null.</param>
    /// <returns>True if the parameter was found, false otherwise.</returns>
    public bool TryGetQueryParameter(string key, out string? value) =>
        QueryParameters.TryGetValue(key, out value);
}

/// <summary>
/// Represents an outgoing interface response produced by an interface strategy.
/// </summary>
/// <remarks>
/// <para>
/// This record encapsulates the response data in a protocol-agnostic way.
/// Interface strategies map this structure to protocol-specific response formats.
/// </para>
/// </remarks>
/// <param name="StatusCode">
/// The HTTP status code or equivalent response status (200, 404, 500, etc.).
/// For protocols without HTTP semantics, standard codes should be mapped appropriately.
/// </param>
/// <param name="Headers">
/// A dictionary of response headers. Protocol-specific metadata should be mapped to appropriate header entries.
/// Common headers include Content-Type, Content-Length, Cache-Control, etc.
/// </param>
/// <param name="Body">
/// The response body as a read-only memory span of bytes.
/// For responses without a body (e.g., 204 No Content), this should be empty.
/// </param>
/// <param name="Metadata">
/// Additional protocol-specific metadata that doesn't fit into standard properties.
/// This allows strategies to return custom data without breaking the common abstraction.
/// </param>
public record InterfaceResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> Body,
    IReadOnlyDictionary<string, object>? Metadata = null
)
{
    /// <summary>
    /// Gets whether the response indicates success (status code 200-299).
    /// </summary>
    public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;

    /// <summary>
    /// Gets whether the response indicates a client error (status code 400-499).
    /// </summary>
    public bool IsClientError => StatusCode >= 400 && StatusCode < 500;

    /// <summary>
    /// Gets whether the response indicates a server error (status code 500-599).
    /// </summary>
    public bool IsServerError => StatusCode >= 500 && StatusCode < 600;

    /// <summary>
    /// Creates a success response with the given body and content type.
    /// </summary>
    /// <param name="body">The response body bytes.</param>
    /// <param name="contentType">The MIME type of the response body.</param>
    /// <returns>An <see cref="InterfaceResponse"/> with status code 200 (OK).</returns>
    public static InterfaceResponse Ok(ReadOnlyMemory<byte> body, string contentType = "application/json") => new(
        StatusCode: 200,
        Headers: new Dictionary<string, string> { ["Content-Type"] = contentType },
        Body: body
    );

    /// <summary>
    /// Creates a 201 Created response with the given body and location.
    /// </summary>
    /// <param name="body">The response body bytes.</param>
    /// <param name="location">The URI of the created resource.</param>
    /// <param name="contentType">The MIME type of the response body.</param>
    /// <returns>An <see cref="InterfaceResponse"/> with status code 201 (Created).</returns>
    public static InterfaceResponse Created(ReadOnlyMemory<byte> body, string location, string contentType = "application/json") => new(
        StatusCode: 201,
        Headers: new Dictionary<string, string>
        {
            ["Content-Type"] = contentType,
            ["Location"] = location
        },
        Body: body
    );

    /// <summary>
    /// Creates a 204 No Content response.
    /// </summary>
    /// <returns>An <see cref="InterfaceResponse"/> with status code 204 (No Content) and empty body.</returns>
    public static InterfaceResponse NoContent() => new(
        StatusCode: 204,
        Headers: new Dictionary<string, string>(),
        Body: ReadOnlyMemory<byte>.Empty
    );

    /// <summary>
    /// Creates a 400 Bad Request error response.
    /// </summary>
    /// <param name="message">The error message to include in the response body.</param>
    /// <returns>An <see cref="InterfaceResponse"/> with status code 400 (Bad Request).</returns>
    public static InterfaceResponse BadRequest(string message) => Error(400, message);

    /// <summary>
    /// Creates a 401 Unauthorized error response.
    /// </summary>
    /// <param name="message">The error message to include in the response body.</param>
    /// <returns>An <see cref="InterfaceResponse"/> with status code 401 (Unauthorized).</returns>
    public static InterfaceResponse Unauthorized(string message = "Unauthorized") => Error(401, message);

    /// <summary>
    /// Creates a 403 Forbidden error response.
    /// </summary>
    /// <param name="message">The error message to include in the response body.</param>
    /// <returns>An <see cref="InterfaceResponse"/> with status code 403 (Forbidden).</returns>
    public static InterfaceResponse Forbidden(string message = "Forbidden") => Error(403, message);

    /// <summary>
    /// Creates a 404 Not Found error response.
    /// </summary>
    /// <param name="message">The error message to include in the response body.</param>
    /// <returns>An <see cref="InterfaceResponse"/> with status code 404 (Not Found).</returns>
    public static InterfaceResponse NotFound(string message = "Not Found") => Error(404, message);

    /// <summary>
    /// Creates a 500 Internal Server Error response.
    /// </summary>
    /// <param name="message">The error message to include in the response body.</param>
    /// <returns>An <see cref="InterfaceResponse"/> with status code 500 (Internal Server Error).</returns>
    public static InterfaceResponse InternalServerError(string message = "Internal Server Error") => Error(500, message);

    /// <summary>
    /// Creates an error response with the specified status code and message.
    /// </summary>
    /// <param name="statusCode">The HTTP status code for the error.</param>
    /// <param name="message">The error message to include in the response body.</param>
    /// <returns>An <see cref="InterfaceResponse"/> with the specified error details.</returns>
    public static InterfaceResponse Error(int statusCode, string message)
    {
        var body = System.Text.Encoding.UTF8.GetBytes($"{{\"error\":\"{message}\"}}");
        return new InterfaceResponse(
            StatusCode: statusCode,
            Headers: new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Body: body
        );
    }
}

/// <summary>
/// Enumerates the supported interface protocols.
/// </summary>
/// <remarks>
/// This enumeration allows the system to identify and route requests to appropriate interface strategies.
/// Additional protocols can be added as new strategies are implemented.
/// </remarks>
public enum InterfaceProtocol
{
    /// <summary>
    /// Unknown or unspecified protocol.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// REST (Representational State Transfer) over HTTP/HTTPS.
    /// Standard web API protocol using HTTP methods and JSON/XML payloads.
    /// </summary>
    REST = 1,

    /// <summary>
    /// gRPC (Google Remote Procedure Call) protocol.
    /// High-performance RPC framework using HTTP/2 and Protocol Buffers.
    /// </summary>
    gRPC = 2,

    /// <summary>
    /// GraphQL query language protocol.
    /// Flexible API query language allowing clients to request specific data structures.
    /// </summary>
    GraphQL = 3,

    /// <summary>
    /// WebSocket protocol for bidirectional communication.
    /// Full-duplex communication channels over a single TCP connection.
    /// </summary>
    WebSocket = 4,

    /// <summary>
    /// Server-Sent Events (SSE) protocol for server-to-client streaming.
    /// One-way event stream from server to client over HTTP.
    /// </summary>
    ServerSentEvents = 5,

    /// <summary>
    /// MQTT (Message Queuing Telemetry Transport) protocol.
    /// Lightweight publish-subscribe messaging protocol, often used for IoT.
    /// </summary>
    MQTT = 6,

    /// <summary>
    /// AMQP (Advanced Message Queuing Protocol).
    /// Enterprise message-oriented middleware protocol for message queuing.
    /// </summary>
    AMQP = 7,

    /// <summary>
    /// Apache Thrift RPC protocol.
    /// Cross-language RPC framework with interface definition language.
    /// </summary>
    Thrift = 8,

    /// <summary>
    /// JSON-RPC protocol.
    /// Lightweight remote procedure call protocol encoded in JSON.
    /// </summary>
    JsonRpc = 9,

    /// <summary>
    /// XML-RPC protocol.
    /// Remote procedure call protocol using XML to encode calls and HTTP as transport.
    /// </summary>
    XmlRpc = 10,

    /// <summary>
    /// Apache Kafka protocol.
    /// Distributed event streaming platform protocol for high-throughput data pipelines.
    /// </summary>
    Kafka = 11,

    /// <summary>
    /// NATS (Neural Autonomic Transport System) messaging protocol.
    /// High-performance cloud-native messaging system.
    /// </summary>
    NATS = 12,

    /// <summary>
    /// Redis Serialization Protocol (RESP).
    /// Protocol for communicating with Redis servers and compatible systems.
    /// </summary>
    Redis = 13,

    /// <summary>
    /// Custom or proprietary protocol.
    /// Used when the protocol doesn't match any standard types.
    /// </summary>
    Custom = 99
}

/// <summary>
/// Standard HTTP methods used in interface requests.
/// </summary>
/// <remarks>
/// This enumeration represents common HTTP methods. For non-HTTP protocols,
/// these can be mapped to equivalent operations (e.g., GET = Read, POST = Create).
/// </remarks>
public enum HttpMethod
{
    /// <summary>
    /// GET method - retrieve a resource.
    /// </summary>
    GET,

    /// <summary>
    /// POST method - create a new resource or execute an operation.
    /// </summary>
    POST,

    /// <summary>
    /// PUT method - update or replace a resource.
    /// </summary>
    PUT,

    /// <summary>
    /// DELETE method - remove a resource.
    /// </summary>
    DELETE,

    /// <summary>
    /// PATCH method - partially update a resource.
    /// </summary>
    PATCH,

    /// <summary>
    /// HEAD method - retrieve headers only, no body.
    /// </summary>
    HEAD,

    /// <summary>
    /// OPTIONS method - query supported methods and capabilities.
    /// </summary>
    OPTIONS,

    /// <summary>
    /// TRACE method - echo the received request for debugging.
    /// </summary>
    TRACE,

    /// <summary>
    /// CONNECT method - establish a tunnel through a proxy.
    /// </summary>
    CONNECT
}
