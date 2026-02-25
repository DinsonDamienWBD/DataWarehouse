namespace DataWarehouse.SDK.Contracts.Observability;

/// <summary>
/// Represents a span context for distributed tracing, containing all information needed to trace a single operation.
/// </summary>
/// <param name="TraceId">Unique identifier for the entire trace (across all services).</param>
/// <param name="SpanId">Unique identifier for this specific span.</param>
/// <param name="ParentSpanId">Identifier of the parent span, if any (null for root spans).</param>
/// <param name="OperationName">Name describing the operation being traced (e.g., "GET /api/users", "database.query").</param>
/// <param name="StartTime">When the operation started.</param>
/// <param name="Duration">How long the operation took.</param>
/// <param name="Kind">The kind of span (Client, Server, Producer, Consumer, Internal).</param>
/// <param name="Status">Status of the span operation.</param>
/// <param name="Attributes">Additional attributes describing the operation.</param>
/// <param name="Events">Time-stamped events that occurred during the span.</param>
/// <remarks>
/// <para>
/// Spans are the building blocks of distributed traces. A trace represents the entire journey of a request
/// through a system, composed of multiple spans representing individual operations.
/// </para>
/// <para>
/// Example trace hierarchy:
/// <code>
/// [Root Span: HTTP Request]
///   ├─ [Child: Database Query]
///   ├─ [Child: Cache Lookup]
///   └─ [Child: External API Call]
///        └─ [Grandchild: HTTP Request to external service]
/// </code>
/// </para>
/// </remarks>
public record SpanContext(
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string OperationName,
    DateTimeOffset StartTime,
    TimeSpan Duration,
    SpanKind Kind,
    SpanStatus Status,
    IReadOnlyDictionary<string, object> Attributes,
    IReadOnlyList<SpanEvent>? Events = null)
{
    /// <summary>
    /// Creates a new root span (no parent).
    /// </summary>
    /// <param name="operationName">Name of the operation.</param>
    /// <param name="kind">Kind of span.</param>
    /// <returns>A new span context with generated IDs.</returns>
    public static SpanContext CreateRoot(string operationName, SpanKind kind = SpanKind.Internal) =>
        new(
            TraceId: GenerateTraceId(),
            SpanId: GenerateSpanId(),
            ParentSpanId: null,
            OperationName: operationName,
            StartTime: DateTimeOffset.UtcNow,
            Duration: TimeSpan.Zero,
            Kind: kind,
            Status: SpanStatus.Ok,
            Attributes: new Dictionary<string, object>(),
            Events: null);

    /// <summary>
    /// Creates a child span from this span context.
    /// </summary>
    /// <param name="operationName">Name of the child operation.</param>
    /// <param name="kind">Kind of child span.</param>
    /// <returns>A new child span context.</returns>
    public SpanContext CreateChild(string operationName, SpanKind kind = SpanKind.Internal) =>
        new(
            TraceId: TraceId,
            SpanId: GenerateSpanId(),
            ParentSpanId: SpanId,
            OperationName: operationName,
            StartTime: DateTimeOffset.UtcNow,
            Duration: TimeSpan.Zero,
            Kind: kind,
            Status: SpanStatus.Ok,
            Attributes: new Dictionary<string, object>(),
            Events: null);

    private static string GenerateTraceId() => Guid.NewGuid().ToString("N");
    private static string GenerateSpanId() => Guid.NewGuid().ToString("N")[..16];
}

/// <summary>
/// Defines the kind of span, indicating its role in the trace.
/// </summary>
public enum SpanKind
{
    /// <summary>
    /// Internal span: Operation within a single process (default).
    /// </summary>
    Internal = 0,

    /// <summary>
    /// Server span: Represents handling of a request from a remote client.
    /// </summary>
    Server = 1,

    /// <summary>
    /// Client span: Represents making a request to a remote service.
    /// </summary>
    Client = 2,

    /// <summary>
    /// Producer span: Represents sending a message to a message broker.
    /// </summary>
    Producer = 3,

    /// <summary>
    /// Consumer span: Represents receiving a message from a message broker.
    /// </summary>
    Consumer = 4
}

/// <summary>
/// Represents the status of a span operation.
/// </summary>
public enum SpanStatus
{
    /// <summary>The operation completed successfully.</summary>
    Ok = 0,

    /// <summary>The operation encountered an error.</summary>
    Error = 1,

    /// <summary>The operation status is not set (used during span lifecycle).</summary>
    Unset = 2
}

/// <summary>
/// Represents a time-stamped event that occurred during a span.
/// </summary>
/// <param name="Name">Name of the event (e.g., "exception", "cache.miss", "retry").</param>
/// <param name="Timestamp">When the event occurred.</param>
/// <param name="Attributes">Additional attributes describing the event.</param>
/// <remarks>
/// Events allow recording significant moments within a span's lifetime, such as exceptions,
/// retries, cache hits/misses, or any other notable occurrences.
/// </remarks>
public record SpanEvent(
    string Name,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, object>? Attributes = null);

/// <summary>
/// Represents a trace context that can be propagated across service boundaries.
/// </summary>
/// <param name="TraceId">Unique identifier for the trace.</param>
/// <param name="SpanId">Current span identifier.</param>
/// <param name="TraceFlags">Flags indicating trace behavior (e.g., sampled, debug).</param>
/// <param name="TraceState">Vendor-specific trace state information.</param>
/// <remarks>
/// <para>
/// TraceContext follows the W3C Trace Context specification for propagating trace information
/// across process and network boundaries via HTTP headers or message metadata.
/// </para>
/// <para>
/// Standard HTTP header format:
/// <code>
/// traceparent: 00-{TraceId}-{SpanId}-{TraceFlags}
/// tracestate: {TraceState}
/// </code>
/// </para>
/// </remarks>
public record TraceContext(
    string TraceId,
    string SpanId,
    byte TraceFlags = 0x01,
    string? TraceState = null)
{
    /// <summary>
    /// Gets a value indicating whether this trace is sampled (should be recorded).
    /// </summary>
    public bool IsSampled => (TraceFlags & 0x01) == 0x01;

    /// <summary>
    /// Parses a W3C traceparent header value.
    /// </summary>
    /// <param name="traceparent">The traceparent header value (format: "00-{traceId}-{spanId}-{flags}").</param>
    /// <returns>A parsed trace context.</returns>
    /// <exception cref="ArgumentException">Thrown when the traceparent format is invalid.</exception>
    public static TraceContext ParseTraceparent(string traceparent)
    {
        var parts = traceparent.Split('-');
        if (parts.Length != 4 || parts[0] != "00")
            throw new ArgumentException("Invalid traceparent format", nameof(traceparent));

        return new TraceContext(
            TraceId: parts[1],
            SpanId: parts[2],
            TraceFlags: Convert.ToByte(parts[3], 16));
    }

    /// <summary>
    /// Converts this trace context to a W3C traceparent header value.
    /// </summary>
    /// <returns>A traceparent header value string.</returns>
    public string ToTraceparent() => $"00-{TraceId}-{SpanId}-{TraceFlags:x2}";
}
