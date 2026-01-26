using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.DistributedTracing;

#region Supporting Types

/// <summary>
/// Represents a W3C Trace Context with traceparent and tracestate.
/// Implements the W3C Trace Context specification (https://www.w3.org/TR/trace-context/).
/// </summary>
public sealed class TraceContext
{
    /// <summary>
    /// W3C Trace Context version. Currently always "00".
    /// </summary>
    public string Version { get; init; } = "00";

    /// <summary>
    /// The trace ID (32 hex characters / 16 bytes).
    /// </summary>
    public string TraceId { get; init; } = string.Empty;

    /// <summary>
    /// The parent span ID (16 hex characters / 8 bytes).
    /// </summary>
    public string ParentSpanId { get; init; } = string.Empty;

    /// <summary>
    /// Trace flags (2 hex characters / 1 byte).
    /// Bit 0: sampled flag.
    /// </summary>
    public string TraceFlags { get; init; } = "01";

    /// <summary>
    /// Gets whether the trace is sampled.
    /// </summary>
    public bool IsSampled => TraceFlags.Length >= 2 &&
        (Convert.ToByte(TraceFlags, 16) & 0x01) == 0x01;

    /// <summary>
    /// Optional tracestate header containing vendor-specific trace data.
    /// </summary>
    public TraceState? TraceState { get; init; }

    /// <summary>
    /// Creates a new root trace context.
    /// </summary>
    /// <param name="sampled">Whether the trace should be sampled.</param>
    /// <returns>A new trace context.</returns>
    public static TraceContext CreateNew(bool sampled = true)
    {
        return new TraceContext
        {
            Version = "00",
            TraceId = GenerateTraceId(),
            ParentSpanId = GenerateSpanId(),
            TraceFlags = sampled ? "01" : "00"
        };
    }

    /// <summary>
    /// Parses a W3C traceparent header.
    /// </summary>
    /// <param name="traceparent">The traceparent header value.</param>
    /// <param name="tracestate">Optional tracestate header value.</param>
    /// <returns>The parsed trace context, or null if invalid.</returns>
    public static TraceContext? Parse(string traceparent, string? tracestate = null)
    {
        if (string.IsNullOrWhiteSpace(traceparent))
            return null;

        // Format: {version}-{trace-id}-{parent-id}-{flags}
        var parts = traceparent.Trim().Split('-');
        if (parts.Length < 4)
            return null;

        var version = parts[0];
        var traceId = parts[1];
        var parentSpanId = parts[2];
        var flags = parts[3];

        // Validate lengths
        if (version.Length != 2 || traceId.Length != 32 ||
            parentSpanId.Length != 16 || flags.Length != 2)
            return null;

        // Validate all-zeros not allowed for trace-id and parent-id
        if (traceId == new string('0', 32) || parentSpanId == new string('0', 16))
            return null;

        return new TraceContext
        {
            Version = version,
            TraceId = traceId.ToLowerInvariant(),
            ParentSpanId = parentSpanId.ToLowerInvariant(),
            TraceFlags = flags.ToLowerInvariant(),
            TraceState = TraceState.Parse(tracestate)
        };
    }

    /// <summary>
    /// Converts this context to a W3C traceparent header value.
    /// </summary>
    /// <returns>The traceparent header value.</returns>
    public string ToTraceparent()
    {
        return $"{Version}-{TraceId}-{ParentSpanId}-{TraceFlags}";
    }

    /// <summary>
    /// Creates a child context with a new span ID.
    /// </summary>
    /// <returns>A new trace context representing a child span.</returns>
    public TraceContext CreateChild()
    {
        return new TraceContext
        {
            Version = Version,
            TraceId = TraceId,
            ParentSpanId = GenerateSpanId(),
            TraceFlags = TraceFlags,
            TraceState = TraceState
        };
    }

    private static string GenerateTraceId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GenerateSpanId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>
/// Represents W3C tracestate header with vendor-specific key-value pairs.
/// </summary>
public sealed class TraceState
{
    private readonly List<KeyValuePair<string, string>> _entries = new();

    /// <summary>
    /// Gets all tracestate entries.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Entries => _entries.AsReadOnly();

    /// <summary>
    /// Maximum number of entries allowed (per W3C spec).
    /// </summary>
    public const int MaxEntries = 32;

    /// <summary>
    /// Gets a value by key.
    /// </summary>
    /// <param name="key">The vendor key.</param>
    /// <returns>The value, or null if not found.</returns>
    public string? Get(string key)
    {
        return _entries.FirstOrDefault(e =>
            e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    /// <summary>
    /// Sets a value, adding or updating the entry.
    /// </summary>
    /// <param name="key">The vendor key.</param>
    /// <param name="value">The value.</param>
    /// <returns>This instance for chaining.</returns>
    public TraceState Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        // Remove existing entry with same key
        _entries.RemoveAll(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

        // Add to front (most recent)
        _entries.Insert(0, new KeyValuePair<string, string>(key, value));

        // Enforce max entries
        while (_entries.Count > MaxEntries)
        {
            _entries.RemoveAt(_entries.Count - 1);
        }

        return this;
    }

    /// <summary>
    /// Removes an entry by key.
    /// </summary>
    /// <param name="key">The vendor key.</param>
    /// <returns>True if removed, false if not found.</returns>
    public bool Remove(string key)
    {
        return _entries.RemoveAll(e =>
            e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    /// <summary>
    /// Parses a tracestate header value.
    /// </summary>
    /// <param name="tracestate">The tracestate header value.</param>
    /// <returns>The parsed tracestate, or null if empty/invalid.</returns>
    public static TraceState? Parse(string? tracestate)
    {
        if (string.IsNullOrWhiteSpace(tracestate))
            return null;

        var result = new TraceState();

        // Format: vendor1=value1,vendor2=value2
        var entries = tracestate.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in entries.Take(MaxEntries))
        {
            var eqIndex = entry.IndexOf('=');
            if (eqIndex > 0 && eqIndex < entry.Length - 1)
            {
                var key = entry[..eqIndex].Trim();
                var value = entry[(eqIndex + 1)..].Trim();
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                {
                    result._entries.Add(new KeyValuePair<string, string>(key, value));
                }
            }
        }

        return result._entries.Count > 0 ? result : null;
    }

    /// <summary>
    /// Converts to tracestate header value.
    /// </summary>
    /// <returns>The tracestate header value.</returns>
    public override string ToString()
    {
        return string.Join(",", _entries.Select(e => $"{e.Key}={e.Value}"));
    }
}

/// <summary>
/// Represents baggage for propagating arbitrary key-value pairs across services.
/// Implements the W3C Baggage specification (https://www.w3.org/TR/baggage/).
/// </summary>
public sealed class Baggage
{
    private readonly ConcurrentDictionary<string, BaggageEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets all baggage entries.
    /// </summary>
    public IReadOnlyDictionary<string, BaggageEntry> Entries => _entries;

    /// <summary>
    /// Maximum total size of baggage header (8192 bytes per W3C spec).
    /// </summary>
    public const int MaxHeaderSize = 8192;

    /// <summary>
    /// Maximum number of entries.
    /// </summary>
    public const int MaxEntries = 180;

    /// <summary>
    /// Gets a baggage value by key.
    /// </summary>
    /// <param name="key">The baggage key.</param>
    /// <returns>The value, or null if not found.</returns>
    public string? Get(string key)
    {
        return _entries.TryGetValue(key, out var entry) ? entry.Value : null;
    }

    /// <summary>
    /// Sets a baggage value.
    /// </summary>
    /// <param name="key">The baggage key.</param>
    /// <param name="value">The baggage value.</param>
    /// <param name="metadata">Optional metadata properties.</param>
    /// <returns>This instance for chaining.</returns>
    public Baggage Set(string key, string value, Dictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        if (_entries.Count >= MaxEntries && !_entries.ContainsKey(key))
        {
            throw new InvalidOperationException($"Baggage entry limit ({MaxEntries}) exceeded.");
        }

        _entries[key] = new BaggageEntry
        {
            Key = key,
            Value = value,
            Metadata = metadata ?? new Dictionary<string, string>()
        };

        return this;
    }

    /// <summary>
    /// Removes a baggage entry.
    /// </summary>
    /// <param name="key">The baggage key.</param>
    /// <returns>True if removed, false if not found.</returns>
    public bool Remove(string key)
    {
        return _entries.TryRemove(key, out _);
    }

    /// <summary>
    /// Clears all baggage entries.
    /// </summary>
    public void Clear()
    {
        _entries.Clear();
    }

    /// <summary>
    /// Parses a baggage header value.
    /// </summary>
    /// <param name="baggageHeader">The baggage header value.</param>
    /// <returns>The parsed baggage.</returns>
    public static Baggage Parse(string? baggageHeader)
    {
        var result = new Baggage();

        if (string.IsNullOrWhiteSpace(baggageHeader))
            return result;

        // Format: key1=value1;prop1=val1,key2=value2
        var members = baggageHeader.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var member in members.Take(MaxEntries))
        {
            var parts = member.Split(';');
            if (parts.Length == 0) continue;

            var kvPart = parts[0];
            var eqIndex = kvPart.IndexOf('=');
            if (eqIndex <= 0) continue;

            var key = Uri.UnescapeDataString(kvPart[..eqIndex].Trim());
            var value = Uri.UnescapeDataString(kvPart[(eqIndex + 1)..].Trim());

            // Parse metadata properties
            var metadata = new Dictionary<string, string>();
            for (int i = 1; i < parts.Length; i++)
            {
                var propEq = parts[i].IndexOf('=');
                if (propEq > 0)
                {
                    var propKey = parts[i][..propEq].Trim();
                    var propVal = parts[i][(propEq + 1)..].Trim();
                    metadata[propKey] = propVal;
                }
            }

            if (!string.IsNullOrEmpty(key))
            {
                result._entries[key] = new BaggageEntry
                {
                    Key = key,
                    Value = value,
                    Metadata = metadata
                };
            }
        }

        return result;
    }

    /// <summary>
    /// Converts to baggage header value.
    /// </summary>
    /// <returns>The baggage header value.</returns>
    public override string ToString()
    {
        var parts = new List<string>();
        foreach (var entry in _entries.Values)
        {
            var sb = new StringBuilder();
            sb.Append(Uri.EscapeDataString(entry.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(entry.Value));

            foreach (var prop in entry.Metadata)
            {
                sb.Append(';');
                sb.Append(prop.Key);
                sb.Append('=');
                sb.Append(prop.Value);
            }

            parts.Add(sb.ToString());
        }

        return string.Join(",", parts);
    }

    /// <summary>
    /// Creates a copy of this baggage.
    /// </summary>
    /// <returns>A new baggage instance with copied entries.</returns>
    public Baggage Clone()
    {
        var result = new Baggage();
        foreach (var entry in _entries)
        {
            result._entries[entry.Key] = new BaggageEntry
            {
                Key = entry.Value.Key,
                Value = entry.Value.Value,
                Metadata = new Dictionary<string, string>(entry.Value.Metadata)
            };
        }
        return result;
    }
}

/// <summary>
/// Represents a single baggage entry with optional metadata.
/// </summary>
public sealed class BaggageEntry
{
    /// <summary>
    /// The baggage key.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// The baggage value.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// Optional metadata properties for this entry.
    /// </summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>
/// Represents a link between spans, enabling correlation of related traces.
/// </summary>
public sealed class SpanLink
{
    /// <summary>
    /// The trace ID of the linked span.
    /// </summary>
    public required string TraceId { get; init; }

    /// <summary>
    /// The span ID of the linked span.
    /// </summary>
    public required string SpanId { get; init; }

    /// <summary>
    /// The tracestate of the linked span.
    /// </summary>
    public string? TraceState { get; init; }

    /// <summary>
    /// Attributes describing the link.
    /// </summary>
    public Dictionary<string, object> Attributes { get; init; } = new();

    /// <summary>
    /// Creates a span link from a trace context.
    /// </summary>
    /// <param name="context">The trace context to link to.</param>
    /// <param name="attributes">Optional link attributes.</param>
    /// <returns>A new span link.</returns>
    public static SpanLink FromContext(TraceContext context, Dictionary<string, object>? attributes = null)
    {
        return new SpanLink
        {
            TraceId = context.TraceId,
            SpanId = context.ParentSpanId,
            TraceState = context.TraceState?.ToString(),
            Attributes = attributes ?? new Dictionary<string, object>()
        };
    }
}

/// <summary>
/// Represents a distributed trace span.
/// </summary>
public sealed class TracingSpan : IDisposable
{
    private readonly DistributedTracingPlugin _plugin;
    private readonly Stopwatch _stopwatch;
    private bool _isEnded;
    private readonly object _lock = new();

    /// <summary>
    /// The trace ID this span belongs to.
    /// </summary>
    public string TraceId { get; }

    /// <summary>
    /// This span's unique ID.
    /// </summary>
    public string SpanId { get; }

    /// <summary>
    /// The parent span ID, if any.
    /// </summary>
    public string? ParentSpanId { get; }

    /// <summary>
    /// The span name/operation name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The span kind.
    /// </summary>
    public TracingSpanKind Kind { get; }

    /// <summary>
    /// When the span started.
    /// </summary>
    public DateTimeOffset StartTime { get; }

    /// <summary>
    /// When the span ended.
    /// </summary>
    public DateTimeOffset? EndTime { get; private set; }

    /// <summary>
    /// The span duration.
    /// </summary>
    public TimeSpan? Duration { get; private set; }

    /// <summary>
    /// The span status.
    /// </summary>
    public TracingSpanStatus Status { get; private set; } = TracingSpanStatus.Unset;

    /// <summary>
    /// Status description message.
    /// </summary>
    public string? StatusMessage { get; private set; }

    /// <summary>
    /// Span attributes.
    /// </summary>
    public ConcurrentDictionary<string, object> Attributes { get; } = new();

    /// <summary>
    /// Span events/logs.
    /// </summary>
    public ConcurrentBag<TracingSpanEvent> Events { get; } = new();

    /// <summary>
    /// Links to other spans.
    /// </summary>
    public ConcurrentBag<SpanLink> Links { get; } = new();

    /// <summary>
    /// Resource attributes (service name, version, etc.).
    /// </summary>
    public Dictionary<string, object> Resource { get; init; } = new();

    /// <summary>
    /// The instrumentation scope/library name.
    /// </summary>
    public string? InstrumentationScope { get; init; }

    /// <summary>
    /// Gets whether this span has ended.
    /// </summary>
    public bool IsEnded => _isEnded;

    /// <summary>
    /// Gets the current trace context for this span.
    /// </summary>
    public TraceContext Context => new()
    {
        TraceId = TraceId,
        ParentSpanId = SpanId,
        TraceFlags = Status == TracingSpanStatus.Error ? "00" : "01"
    };

    internal TracingSpan(
        DistributedTracingPlugin plugin,
        string traceId,
        string spanId,
        string? parentSpanId,
        string name,
        TracingSpanKind kind)
    {
        _plugin = plugin;
        _stopwatch = Stopwatch.StartNew();
        TraceId = traceId;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        Name = name;
        Kind = kind;
        StartTime = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Sets a span attribute.
    /// </summary>
    /// <param name="key">The attribute key.</param>
    /// <param name="value">The attribute value.</param>
    /// <returns>This span for chaining.</returns>
    public TracingSpan SetAttribute(string key, object value)
    {
        Attributes[key] = value;
        return this;
    }

    /// <summary>
    /// Sets multiple attributes.
    /// </summary>
    /// <param name="attributes">The attributes to set.</param>
    /// <returns>This span for chaining.</returns>
    public TracingSpan SetAttributes(IEnumerable<KeyValuePair<string, object>> attributes)
    {
        foreach (var attr in attributes)
        {
            Attributes[attr.Key] = attr.Value;
        }
        return this;
    }

    /// <summary>
    /// Sets the span status.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <param name="message">Optional status message.</param>
    /// <returns>This span for chaining.</returns>
    public TracingSpan SetStatus(TracingSpanStatus status, string? message = null)
    {
        Status = status;
        StatusMessage = message;
        return this;
    }

    /// <summary>
    /// Records an exception on the span.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>This span for chaining.</returns>
    public TracingSpan RecordException(Exception exception)
    {
        AddEvent("exception", new Dictionary<string, object>
        {
            ["exception.type"] = exception.GetType().FullName ?? exception.GetType().Name,
            ["exception.message"] = exception.Message,
            ["exception.stacktrace"] = exception.StackTrace ?? string.Empty
        });

        SetStatus(TracingSpanStatus.Error, exception.Message);
        return this;
    }

    /// <summary>
    /// Adds an event to the span.
    /// </summary>
    /// <param name="name">The event name.</param>
    /// <param name="attributes">Optional event attributes.</param>
    /// <returns>This span for chaining.</returns>
    public TracingSpan AddEvent(string name, Dictionary<string, object>? attributes = null)
    {
        Events.Add(new TracingSpanEvent
        {
            Name = name,
            Timestamp = DateTimeOffset.UtcNow,
            Attributes = attributes ?? new Dictionary<string, object>()
        });
        return this;
    }

    /// <summary>
    /// Adds a link to another span.
    /// </summary>
    /// <param name="link">The span link.</param>
    /// <returns>This span for chaining.</returns>
    public TracingSpan AddLink(SpanLink link)
    {
        Links.Add(link);
        return this;
    }

    /// <summary>
    /// Ends the span.
    /// </summary>
    public void End()
    {
        lock (_lock)
        {
            if (_isEnded) return;

            _stopwatch.Stop();
            EndTime = DateTimeOffset.UtcNow;
            Duration = _stopwatch.Elapsed;
            _isEnded = true;

            _plugin.OnSpanEnded(this);
        }
    }

    /// <summary>
    /// Disposes the span, ending it if not already ended.
    /// </summary>
    public void Dispose()
    {
        End();
    }
}

/// <summary>
/// Span kind indicating the role in a distributed operation.
/// </summary>
public enum TracingSpanKind
{
    /// <summary>
    /// Internal operation within a service.
    /// </summary>
    Internal = 0,

    /// <summary>
    /// Server-side handling of a request.
    /// </summary>
    Server = 1,

    /// <summary>
    /// Client-side request to a remote service.
    /// </summary>
    Client = 2,

    /// <summary>
    /// Producer sending a message to a queue/topic.
    /// </summary>
    Producer = 3,

    /// <summary>
    /// Consumer receiving a message from a queue/topic.
    /// </summary>
    Consumer = 4
}

/// <summary>
/// Span status indicating success, error, or unset.
/// </summary>
public enum TracingSpanStatus
{
    /// <summary>
    /// Status not set.
    /// </summary>
    Unset = 0,

    /// <summary>
    /// Operation completed successfully.
    /// </summary>
    Ok = 1,

    /// <summary>
    /// Operation failed with an error.
    /// </summary>
    Error = 2
}

/// <summary>
/// Represents an event/log within a span.
/// </summary>
public sealed class TracingSpanEvent
{
    /// <summary>
    /// The event name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// When the event occurred.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Event attributes.
    /// </summary>
    public Dictionary<string, object> Attributes { get; init; } = new();
}

/// <summary>
/// Provides automatic instrumentation helpers.
/// </summary>
public sealed class AutoInstrumentation
{
    private readonly DistributedTracingPlugin _plugin;

    internal AutoInstrumentation(DistributedTracingPlugin plugin)
    {
        _plugin = plugin;
    }

    /// <summary>
    /// Creates a span for an HTTP client request.
    /// </summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="url">The request URL.</param>
    /// <returns>A new span.</returns>
    public TracingSpan HttpClient(string method, string url)
    {
        var uri = new Uri(url);
        var span = _plugin.StartSpan($"HTTP {method}", TracingSpanKind.Client);
        span.SetAttribute("http.method", method);
        span.SetAttribute("http.url", url);
        span.SetAttribute("http.scheme", uri.Scheme);
        span.SetAttribute("http.host", uri.Host);
        span.SetAttribute("http.target", uri.PathAndQuery);
        span.SetAttribute("net.peer.name", uri.Host);
        span.SetAttribute("net.peer.port", uri.Port);
        return span;
    }

    /// <summary>
    /// Creates a span for an HTTP server request.
    /// </summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="route">The request route.</param>
    /// <param name="url">The full URL.</param>
    /// <returns>A new span.</returns>
    public TracingSpan HttpServer(string method, string route, string? url = null)
    {
        var span = _plugin.StartSpan($"{method} {route}", TracingSpanKind.Server);
        span.SetAttribute("http.method", method);
        span.SetAttribute("http.route", route);
        if (url != null)
        {
            span.SetAttribute("http.url", url);
        }
        return span;
    }

    /// <summary>
    /// Records HTTP response attributes.
    /// </summary>
    /// <param name="span">The span to update.</param>
    /// <param name="statusCode">The HTTP status code.</param>
    public void RecordHttpResponse(TracingSpan span, int statusCode)
    {
        span.SetAttribute("http.status_code", statusCode);
        if (statusCode >= 400)
        {
            span.SetStatus(TracingSpanStatus.Error, $"HTTP {statusCode}");
        }
        else
        {
            span.SetStatus(TracingSpanStatus.Ok);
        }
    }

    /// <summary>
    /// Creates a span for a database operation.
    /// </summary>
    /// <param name="system">The database system (e.g., "postgresql", "mysql").</param>
    /// <param name="operation">The operation (e.g., "SELECT", "INSERT").</param>
    /// <param name="table">The target table, if applicable.</param>
    /// <returns>A new span.</returns>
    public TracingSpan Database(string system, string operation, string? table = null)
    {
        var name = table != null ? $"{operation} {table}" : operation;
        var span = _plugin.StartSpan(name, TracingSpanKind.Client);
        span.SetAttribute("db.system", system);
        span.SetAttribute("db.operation", operation);
        if (table != null)
        {
            span.SetAttribute("db.sql.table", table);
        }
        return span;
    }

    /// <summary>
    /// Creates a span for a message queue producer.
    /// </summary>
    /// <param name="system">The messaging system (e.g., "kafka", "rabbitmq").</param>
    /// <param name="destination">The destination queue/topic.</param>
    /// <returns>A new span.</returns>
    public TracingSpan MessageProducer(string system, string destination)
    {
        var span = _plugin.StartSpan($"{destination} send", TracingSpanKind.Producer);
        span.SetAttribute("messaging.system", system);
        span.SetAttribute("messaging.destination", destination);
        span.SetAttribute("messaging.destination_kind", "queue");
        return span;
    }

    /// <summary>
    /// Creates a span for a message queue consumer.
    /// </summary>
    /// <param name="system">The messaging system (e.g., "kafka", "rabbitmq").</param>
    /// <param name="destination">The source queue/topic.</param>
    /// <returns>A new span.</returns>
    public TracingSpan MessageConsumer(string system, string destination)
    {
        var span = _plugin.StartSpan($"{destination} receive", TracingSpanKind.Consumer);
        span.SetAttribute("messaging.system", system);
        span.SetAttribute("messaging.destination", destination);
        span.SetAttribute("messaging.destination_kind", "queue");
        span.SetAttribute("messaging.operation", "receive");
        return span;
    }

    /// <summary>
    /// Creates a span for a gRPC client call.
    /// </summary>
    /// <param name="service">The gRPC service name.</param>
    /// <param name="method">The gRPC method name.</param>
    /// <returns>A new span.</returns>
    public TracingSpan GrpcClient(string service, string method)
    {
        var span = _plugin.StartSpan($"{service}/{method}", TracingSpanKind.Client);
        span.SetAttribute("rpc.system", "grpc");
        span.SetAttribute("rpc.service", service);
        span.SetAttribute("rpc.method", method);
        return span;
    }

    /// <summary>
    /// Creates a span for a gRPC server handler.
    /// </summary>
    /// <param name="service">The gRPC service name.</param>
    /// <param name="method">The gRPC method name.</param>
    /// <returns>A new span.</returns>
    public TracingSpan GrpcServer(string service, string method)
    {
        var span = _plugin.StartSpan($"{service}/{method}", TracingSpanKind.Server);
        span.SetAttribute("rpc.system", "grpc");
        span.SetAttribute("rpc.service", service);
        span.SetAttribute("rpc.method", method);
        return span;
    }

    /// <summary>
    /// Creates a span for a generic internal operation.
    /// </summary>
    /// <param name="name">The operation name.</param>
    /// <param name="component">The component name.</param>
    /// <returns>A new span.</returns>
    public TracingSpan Internal(string name, string? component = null)
    {
        var span = _plugin.StartSpan(name, TracingSpanKind.Internal);
        if (component != null)
        {
            span.SetAttribute("component", component);
        }
        return span;
    }
}

/// <summary>
/// Visualization data for a complete trace.
/// </summary>
public sealed class TraceVisualization
{
    /// <summary>
    /// The trace ID.
    /// </summary>
    public required string TraceId { get; init; }

    /// <summary>
    /// All spans in the trace.
    /// </summary>
    public required List<SpanVisualization> Spans { get; init; }

    /// <summary>
    /// Total trace duration.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Number of services involved.
    /// </summary>
    public int ServiceCount { get; init; }

    /// <summary>
    /// Number of errors in the trace.
    /// </summary>
    public int ErrorCount { get; init; }

    /// <summary>
    /// Trace start time.
    /// </summary>
    public DateTimeOffset StartTime { get; init; }

    /// <summary>
    /// Critical path spans (longest sequence).
    /// </summary>
    public List<string>? CriticalPath { get; init; }
}

/// <summary>
/// Visualization data for a single span.
/// </summary>
public sealed class SpanVisualization
{
    /// <summary>
    /// The span ID.
    /// </summary>
    public required string SpanId { get; init; }

    /// <summary>
    /// The parent span ID.
    /// </summary>
    public string? ParentSpanId { get; init; }

    /// <summary>
    /// The span name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The service name.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// The span kind.
    /// </summary>
    public TracingSpanKind Kind { get; init; }

    /// <summary>
    /// Start time offset from trace start.
    /// </summary>
    public TimeSpan StartOffset { get; init; }

    /// <summary>
    /// Span duration.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Whether the span has errors.
    /// </summary>
    public bool HasError { get; init; }

    /// <summary>
    /// Number of child spans.
    /// </summary>
    public int ChildCount { get; init; }

    /// <summary>
    /// Key attributes for display.
    /// </summary>
    public Dictionary<string, object> KeyAttributes { get; init; } = new();
}

/// <summary>
/// Interface for trace exporters.
/// </summary>
public interface ITraceExporter
{
    /// <summary>
    /// Gets the exporter name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Exports completed spans.
    /// </summary>
    /// <param name="spans">The spans to export.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the export operation.</returns>
    Task ExportAsync(IReadOnlyList<TracingSpan> spans, CancellationToken ct = default);
}

/// <summary>
/// Context propagation interface for injecting/extracting trace context.
/// </summary>
public interface IContextPropagator
{
    /// <summary>
    /// Injects trace context into a carrier.
    /// </summary>
    /// <typeparam name="TCarrier">The carrier type.</typeparam>
    /// <param name="context">The trace context.</param>
    /// <param name="baggage">Optional baggage.</param>
    /// <param name="carrier">The carrier to inject into.</param>
    /// <param name="setter">Function to set header values.</param>
    void Inject<TCarrier>(
        TraceContext context,
        Baggage? baggage,
        TCarrier carrier,
        Action<TCarrier, string, string> setter);

    /// <summary>
    /// Extracts trace context from a carrier.
    /// </summary>
    /// <typeparam name="TCarrier">The carrier type.</typeparam>
    /// <param name="carrier">The carrier to extract from.</param>
    /// <param name="getter">Function to get header values.</param>
    /// <returns>The extracted context and baggage.</returns>
    (TraceContext? Context, Baggage? Baggage) Extract<TCarrier>(
        TCarrier carrier,
        Func<TCarrier, string, string?> getter);
}

#endregion

/// <summary>
/// Vendor-neutral distributed tracing plugin with W3C Trace Context propagation.
///
/// Provides comprehensive distributed tracing capabilities:
/// - W3C Trace Context (traceparent/tracestate) propagation
/// - W3C Baggage propagation for cross-service context
/// - Automatic instrumentation helpers for common operations
/// - Trace correlation across services
/// - Span links for relating traces
/// - Trace visualization data generation
/// - Export to multiple backends (OTLP, Jaeger, Zipkin, console)
///
/// Implements ITelemetryProvider for integration with the DataWarehouse telemetry system.
/// </summary>
public sealed class DistributedTracingPlugin : FeaturePluginBase, ITelemetryProvider
{
    #region Plugin Identity

    /// <inheritdoc />
    public override string Id => "com.datawarehouse.telemetry.distributedtracing";

    /// <inheritdoc />
    public override string Name => "Distributed Tracing Plugin";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.MetricsProvider;

    #endregion

    #region Fields

    // Span storage
    private readonly ConcurrentDictionary<string, TracingSpan> _activeSpans = new();
    private readonly ConcurrentQueue<TracingSpan> _completedSpans = new();
    private readonly ConcurrentDictionary<string, List<TracingSpan>> _traceSpans = new();

    // Context storage (async-local for proper async propagation)
    private readonly AsyncLocal<TraceContext?> _currentContext = new();
    private readonly AsyncLocal<Baggage> _currentBaggage = new();

    // Exporters
    private readonly List<ITraceExporter> _exporters = new();
    private readonly object _exporterLock = new();

    // Configuration
    private string _serviceName = "datawarehouse";
    private string _serviceVersion = "1.0.0";
    private string _serviceInstanceId = string.Empty;
    private double _samplingRate = 1.0;
    private int _maxSpansPerTrace = 10000;
    private int _maxAttributes = 128;
    private int _maxEvents = 128;
    private int _maxLinks = 128;
    private TimeSpan _exportInterval = TimeSpan.FromSeconds(30);
    private int _maxBatchSize = 512;

    // Background tasks
    private CancellationTokenSource? _cts;
    private Task? _exportTask;
    private bool _isRunning;

    // Auto instrumentation
    private AutoInstrumentation? _autoInstrumentation;

    // Context propagator
    private readonly W3CContextPropagator _propagator = new();

    // Statistics
    private long _totalSpansCreated;
    private long _totalSpansExported;
    private long _totalSpansDropped;

    #endregion

    #region Properties

    /// <summary>
    /// Gets the auto instrumentation helper.
    /// </summary>
    public AutoInstrumentation Auto => _autoInstrumentation ??= new AutoInstrumentation(this);

    /// <summary>
    /// Gets the current trace context.
    /// </summary>
    public TraceContext? CurrentContext => _currentContext.Value;

    /// <summary>
    /// Gets the current baggage.
    /// </summary>
    public Baggage CurrentBaggage => _currentBaggage.Value ??= new Baggage();

    /// <summary>
    /// Gets the context propagator.
    /// </summary>
    public IContextPropagator Propagator => _propagator;

    /// <summary>
    /// Gets the service name.
    /// </summary>
    public string ServiceName => _serviceName;

    /// <summary>
    /// Gets the service version.
    /// </summary>
    public string ServiceVersion => _serviceVersion;

    /// <summary>
    /// Gets the total number of spans created.
    /// </summary>
    public long TotalSpansCreated => Interlocked.Read(ref _totalSpansCreated);

    /// <summary>
    /// Gets the total number of spans exported.
    /// </summary>
    public long TotalSpansExported => Interlocked.Read(ref _totalSpansExported);

    /// <summary>
    /// Gets the total number of spans dropped.
    /// </summary>
    public long TotalSpansDropped => Interlocked.Read(ref _totalSpansDropped);

    #endregion

    #region Lifecycle

    /// <inheritdoc />
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        _serviceInstanceId = $"{request.KernelId}-{Guid.NewGuid():N}"[..24];

        return await Task.FromResult(new HandshakeResponse
        {
            PluginId = Id,
            Name = Name,
            Version = ParseSemanticVersion(Version),
            Category = Category,
            Success = true,
            ReadyState = PluginReadyState.Ready,
            Capabilities = GetCapabilities(),
            Dependencies = GetDependencies(),
            Metadata = GetMetadata()
        });
    }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken ct)
    {
        if (_isRunning) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRunning = true;

        // Add default console exporter
        AddExporter(new ConsoleTraceExporter());

        // Start export loop
        _exportTask = RunExportLoopAsync(_cts.Token);

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _cts?.Cancel();

        // Final flush
        await FlushAsync();

        if (_exportTask != null)
        {
            try
            {
                await _exportTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            catch (TimeoutException)
            {
                // Export loop didn't finish in time
            }
        }

        _cts?.Dispose();
        _cts = null;
    }

    /// <inheritdoc />
    public override async Task OnMessageAsync(PluginMessage message)
    {
        if (message.Payload == null) return;

        var response = message.Type switch
        {
            "trace.start" => HandleStartSpan(message.Payload),
            "trace.end" => HandleEndSpan(message.Payload),
            "trace.event" => HandleAddEvent(message.Payload),
            "trace.status" => HandleSetStatus(message.Payload),
            "trace.attribute" => HandleSetAttribute(message.Payload),
            "trace.link" => HandleAddLink(message.Payload),
            "context.get" => HandleGetContext(),
            "context.set" => HandleSetContext(message.Payload),
            "context.inject" => HandleInjectContext(message.Payload),
            "context.extract" => HandleExtractContext(message.Payload),
            "baggage.get" => HandleGetBaggage(message.Payload),
            "baggage.set" => HandleSetBaggage(message.Payload),
            "baggage.remove" => HandleRemoveBaggage(message.Payload),
            "baggage.all" => HandleGetAllBaggage(),
            "visualize" => HandleVisualize(message.Payload),
            "configure" => HandleConfigure(message.Payload),
            "flush" => await HandleFlushAsync(),
            "stats" => HandleStats(),
            _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
        };

        message.Payload["_response"] = response;
    }

    #endregion

    #region ITelemetryProvider Implementation

    /// <inheritdoc />
    public void RecordMetric(string name, double value, Dictionary<string, string>? tags = null)
    {
        // Record as a span event on the current span
        var span = GetCurrentSpan();
        if (span != null)
        {
            var attributes = new Dictionary<string, object>
            {
                ["metric.name"] = name,
                ["metric.value"] = value
            };

            if (tags != null)
            {
                foreach (var tag in tags)
                {
                    attributes[$"metric.tag.{tag.Key}"] = tag.Value;
                }
            }

            span.AddEvent("metric", attributes);
        }
    }

    /// <inheritdoc />
    public void RecordEvent(string name, Dictionary<string, object>? properties = null)
    {
        var span = GetCurrentSpan();
        span?.AddEvent(name, properties);
    }

    /// <inheritdoc />
    public IDisposable BeginOperation(string operationName, Dictionary<string, object>? properties = null)
    {
        var span = StartSpan(operationName, TracingSpanKind.Internal);
        if (properties != null)
        {
            span.SetAttributes(properties);
        }
        return span;
    }

    /// <inheritdoc />
    public void SetProperty(string key, object value)
    {
        var span = GetCurrentSpan();
        span?.SetAttribute(key, value);
    }

    /// <inheritdoc />
    public string? GetCorrelationId()
    {
        return _currentContext.Value?.TraceId;
    }

    /// <inheritdoc />
    public void SetCorrelationId(string correlationId)
    {
        if (_currentContext.Value != null)
        {
            _currentContext.Value = new TraceContext
            {
                TraceId = correlationId,
                ParentSpanId = _currentContext.Value.ParentSpanId,
                TraceFlags = _currentContext.Value.TraceFlags,
                TraceState = _currentContext.Value.TraceState
            };
        }
    }

    #endregion

    #region Span Operations

    /// <summary>
    /// Starts a new span.
    /// </summary>
    /// <param name="name">The span name.</param>
    /// <param name="kind">The span kind.</param>
    /// <param name="parentContext">Optional parent context.</param>
    /// <param name="links">Optional span links.</param>
    /// <returns>The new span.</returns>
    public TracingSpan StartSpan(
        string name,
        TracingSpanKind kind = TracingSpanKind.Internal,
        TraceContext? parentContext = null,
        IEnumerable<SpanLink>? links = null)
    {
        // Apply sampling
        var parent = parentContext ?? _currentContext.Value;
        if (parent == null && Random.Shared.NextDouble() > _samplingRate)
        {
            return CreateNoOpSpan(name, kind);
        }

        // Generate IDs
        var traceId = parent?.TraceId ?? GenerateTraceId();
        var spanId = GenerateSpanId();
        var parentSpanId = parent?.ParentSpanId;

        // Check max spans per trace
        if (_traceSpans.TryGetValue(traceId, out var traceSpanList) &&
            traceSpanList.Count >= _maxSpansPerTrace)
        {
            Interlocked.Increment(ref _totalSpansDropped);
            return CreateNoOpSpan(name, kind);
        }

        var span = new TracingSpan(this, traceId, spanId, parentSpanId, name, kind)
        {
            Resource = new Dictionary<string, object>
            {
                ["service.name"] = _serviceName,
                ["service.version"] = _serviceVersion,
                ["service.instance.id"] = _serviceInstanceId
            }
        };

        // Add links
        if (links != null)
        {
            foreach (var link in links.Take(_maxLinks))
            {
                span.AddLink(link);
            }
        }

        // Track span
        _activeSpans[spanId] = span;
        _traceSpans.AddOrUpdate(
            traceId,
            _ => new List<TracingSpan> { span },
            (_, list) => { list.Add(span); return list; });

        Interlocked.Increment(ref _totalSpansCreated);

        // Update current context
        _currentContext.Value = span.Context;

        return span;
    }

    /// <summary>
    /// Gets the current active span.
    /// </summary>
    /// <returns>The current span, or null if none.</returns>
    public TracingSpan? GetCurrentSpan()
    {
        var ctx = _currentContext.Value;
        if (ctx == null) return null;

        // The current context's ParentSpanId is actually the current span's ID
        // (because context represents what to pass to children)
        _activeSpans.TryGetValue(ctx.ParentSpanId, out var span);
        return span;
    }

    /// <summary>
    /// Ends a span by ID.
    /// </summary>
    /// <param name="spanId">The span ID.</param>
    public void EndSpan(string spanId)
    {
        if (_activeSpans.TryGetValue(spanId, out var span))
        {
            span.End();
        }
    }

    /// <summary>
    /// Called when a span ends.
    /// </summary>
    /// <param name="span">The ended span.</param>
    internal void OnSpanEnded(TracingSpan span)
    {
        _activeSpans.TryRemove(span.SpanId, out _);
        _completedSpans.Enqueue(span);

        // Update context to parent if this was the current span
        if (_currentContext.Value?.ParentSpanId == span.SpanId)
        {
            if (span.ParentSpanId != null)
            {
                _currentContext.Value = new TraceContext
                {
                    TraceId = span.TraceId,
                    ParentSpanId = span.ParentSpanId,
                    TraceFlags = span.Status == TracingSpanStatus.Error ? "00" : "01"
                };
            }
            else
            {
                _currentContext.Value = null;
            }
        }
    }

    private TracingSpan CreateNoOpSpan(string name, TracingSpanKind kind)
    {
        // Return a span that does nothing
        var span = new TracingSpan(this, string.Empty, string.Empty, null, name, kind);
        return span;
    }

    #endregion

    #region Context Propagation

    /// <summary>
    /// Sets the current trace context from a traceparent header.
    /// </summary>
    /// <param name="traceparent">The traceparent header value.</param>
    /// <param name="tracestate">Optional tracestate header value.</param>
    public void SetContextFromHeaders(string traceparent, string? tracestate = null)
    {
        _currentContext.Value = TraceContext.Parse(traceparent, tracestate);
    }

    /// <summary>
    /// Gets headers for propagation.
    /// </summary>
    /// <returns>Dictionary of header names to values.</returns>
    public Dictionary<string, string> GetPropagationHeaders()
    {
        var headers = new Dictionary<string, string>();
        var ctx = _currentContext.Value;

        if (ctx != null)
        {
            headers["traceparent"] = ctx.ToTraceparent();
            if (ctx.TraceState != null)
            {
                headers["tracestate"] = ctx.TraceState.ToString();
            }
        }

        var baggage = _currentBaggage.Value;
        if (baggage != null && baggage.Entries.Count > 0)
        {
            headers["baggage"] = baggage.ToString();
        }

        return headers;
    }

    /// <summary>
    /// Injects context into a carrier.
    /// </summary>
    /// <typeparam name="TCarrier">The carrier type.</typeparam>
    /// <param name="carrier">The carrier.</param>
    /// <param name="setter">Function to set values.</param>
    public void InjectContext<TCarrier>(TCarrier carrier, Action<TCarrier, string, string> setter)
    {
        var ctx = _currentContext.Value;
        if (ctx != null)
        {
            _propagator.Inject(ctx, _currentBaggage.Value, carrier, setter);
        }
    }

    /// <summary>
    /// Extracts context from a carrier.
    /// </summary>
    /// <typeparam name="TCarrier">The carrier type.</typeparam>
    /// <param name="carrier">The carrier.</param>
    /// <param name="getter">Function to get values.</param>
    public void ExtractContext<TCarrier>(TCarrier carrier, Func<TCarrier, string, string?> getter)
    {
        var (context, baggage) = _propagator.Extract(carrier, getter);
        _currentContext.Value = context;
        if (baggage != null)
        {
            _currentBaggage.Value = baggage;
        }
    }

    #endregion

    #region Baggage Operations

    /// <summary>
    /// Sets a baggage value.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    public void SetBaggage(string key, string value)
    {
        (_currentBaggage.Value ??= new Baggage()).Set(key, value);
    }

    /// <summary>
    /// Gets a baggage value.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The value, or null if not found.</returns>
    public string? GetBaggage(string key)
    {
        return _currentBaggage.Value?.Get(key);
    }

    /// <summary>
    /// Removes a baggage entry.
    /// </summary>
    /// <param name="key">The key.</param>
    public void RemoveBaggage(string key)
    {
        _currentBaggage.Value?.Remove(key);
    }

    #endregion

    #region Visualization

    /// <summary>
    /// Gets visualization data for a trace.
    /// </summary>
    /// <param name="traceId">The trace ID.</param>
    /// <returns>The trace visualization, or null if not found.</returns>
    public TraceVisualization? GetTraceVisualization(string traceId)
    {
        if (!_traceSpans.TryGetValue(traceId, out var spans) || spans.Count == 0)
        {
            return null;
        }

        var spanList = spans.OrderBy(s => s.StartTime).ToList();
        var traceStart = spanList.First().StartTime;
        var traceEnd = spanList.Where(s => s.EndTime.HasValue)
            .Select(s => s.EndTime!.Value)
            .DefaultIfEmpty(traceStart)
            .Max();

        var services = spanList
            .Select(s => s.Resource.GetValueOrDefault("service.name")?.ToString() ?? "unknown")
            .Distinct()
            .Count();

        var errors = spanList.Count(s => s.Status == TracingSpanStatus.Error);

        // Build child count map
        var childCounts = spanList
            .Where(s => s.ParentSpanId != null)
            .GroupBy(s => s.ParentSpanId!)
            .ToDictionary(g => g.Key, g => g.Count());

        var spanVisualizations = spanList.Select(s => new SpanVisualization
        {
            SpanId = s.SpanId,
            ParentSpanId = s.ParentSpanId,
            Name = s.Name,
            ServiceName = s.Resource.GetValueOrDefault("service.name")?.ToString() ?? "unknown",
            Kind = s.Kind,
            StartOffset = s.StartTime - traceStart,
            Duration = s.Duration ?? TimeSpan.Zero,
            HasError = s.Status == TracingSpanStatus.Error,
            ChildCount = childCounts.GetValueOrDefault(s.SpanId, 0),
            KeyAttributes = ExtractKeyAttributes(s)
        }).ToList();

        return new TraceVisualization
        {
            TraceId = traceId,
            Spans = spanVisualizations,
            Duration = traceEnd - traceStart,
            ServiceCount = services,
            ErrorCount = errors,
            StartTime = traceStart,
            CriticalPath = CalculateCriticalPath(spanList)
        };
    }

    private static Dictionary<string, object> ExtractKeyAttributes(TracingSpan span)
    {
        var result = new Dictionary<string, object>();
        var keyAttrs = new[]
        {
            "http.method", "http.status_code", "http.url", "http.route",
            "db.system", "db.operation", "db.statement",
            "rpc.service", "rpc.method",
            "messaging.system", "messaging.destination"
        };

        foreach (var key in keyAttrs)
        {
            if (span.Attributes.TryGetValue(key, out var value))
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static List<string>? CalculateCriticalPath(List<TracingSpan> spans)
    {
        if (spans.Count == 0) return null;

        // Find the root span
        var root = spans.FirstOrDefault(s => s.ParentSpanId == null);
        if (root == null) return null;

        // Build child map
        var children = spans
            .Where(s => s.ParentSpanId != null)
            .GroupBy(s => s.ParentSpanId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        // DFS to find longest path
        var path = new List<string>();
        FindLongestPath(root, children, path);
        return path;
    }

    private static void FindLongestPath(
        TracingSpan span,
        Dictionary<string, List<TracingSpan>> children,
        List<string> path)
    {
        path.Add(span.SpanId);

        if (!children.TryGetValue(span.SpanId, out var childSpans) || childSpans.Count == 0)
        {
            return;
        }

        // Find child with longest duration
        var longestChild = childSpans
            .OrderByDescending(s => s.Duration ?? TimeSpan.Zero)
            .First();

        FindLongestPath(longestChild, children, path);
    }

    #endregion

    #region Exporters

    /// <summary>
    /// Adds a trace exporter.
    /// </summary>
    /// <param name="exporter">The exporter to add.</param>
    public void AddExporter(ITraceExporter exporter)
    {
        lock (_exporterLock)
        {
            _exporters.Add(exporter);
        }
    }

    /// <summary>
    /// Removes a trace exporter.
    /// </summary>
    /// <param name="exporterName">The exporter name.</param>
    /// <returns>True if removed.</returns>
    public bool RemoveExporter(string exporterName)
    {
        lock (_exporterLock)
        {
            var exporter = _exporters.FirstOrDefault(e => e.Name == exporterName);
            if (exporter != null)
            {
                _exporters.Remove(exporter);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Flushes all completed spans to exporters.
    /// </summary>
    public async Task FlushAsync()
    {
        var spans = new List<TracingSpan>();
        while (_completedSpans.TryDequeue(out var span) && spans.Count < _maxBatchSize)
        {
            spans.Add(span);
        }

        if (spans.Count == 0) return;

        List<ITraceExporter> exportersCopy;
        lock (_exporterLock)
        {
            exportersCopy = _exporters.ToList();
        }

        foreach (var exporter in exportersCopy)
        {
            try
            {
                await exporter.ExportAsync(spans);
                Interlocked.Add(ref _totalSpansExported, spans.Count);
            }
            catch (Exception ex)
            {
                // Log but don't throw
                System.Diagnostics.Debug.WriteLine($"Export to {exporter.Name} failed: {ex.Message}");
            }
        }
    }

    private async Task RunExportLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_exportInterval, ct);
                await FlushAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Export failed, will retry
            }
        }
    }

    #endregion

    #region Message Handlers

    private Dictionary<string, object> HandleStartSpan(Dictionary<string, object> payload)
    {
        var name = payload.GetValueOrDefault("name")?.ToString() ?? "unnamed";
        var kindStr = payload.GetValueOrDefault("kind")?.ToString() ?? "internal";
        var kind = Enum.TryParse<TracingSpanKind>(kindStr, true, out var k) ? k : TracingSpanKind.Internal;

        TraceContext? parent = null;
        if (payload.TryGetValue("traceparent", out var tp) && tp != null)
        {
            var tracestate = payload.GetValueOrDefault("tracestate")?.ToString();
            parent = TraceContext.Parse(tp.ToString()!, tracestate);
        }

        var span = StartSpan(name, kind, parent);

        // Add attributes
        if (payload.TryGetValue("attributes", out var attrs) && attrs is JsonElement el)
        {
            foreach (var prop in el.EnumerateObject())
            {
                span.SetAttribute(prop.Name, ConvertJsonElement(prop.Value));
            }
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["traceId"] = span.TraceId,
            ["spanId"] = span.SpanId,
            ["traceparent"] = span.Context.ToTraceparent()
        };
    }

    private Dictionary<string, object> HandleEndSpan(Dictionary<string, object> payload)
    {
        var spanId = payload.GetValueOrDefault("spanId")?.ToString();
        if (string.IsNullOrEmpty(spanId))
        {
            var current = GetCurrentSpan();
            if (current == null)
            {
                return new Dictionary<string, object> { ["error"] = "No span to end" };
            }
            spanId = current.SpanId;
        }

        EndSpan(spanId);
        return new Dictionary<string, object> { ["success"] = true, ["spanId"] = spanId };
    }

    private Dictionary<string, object> HandleAddEvent(Dictionary<string, object> payload)
    {
        var spanId = payload.GetValueOrDefault("spanId")?.ToString();
        var span = string.IsNullOrEmpty(spanId) ? GetCurrentSpan() :
            (_activeSpans.TryGetValue(spanId, out var s) ? s : null);

        if (span == null)
        {
            return new Dictionary<string, object> { ["error"] = "No active span" };
        }

        var eventName = payload.GetValueOrDefault("name")?.ToString() ?? "event";
        Dictionary<string, object>? attributes = null;

        if (payload.TryGetValue("attributes", out var attrs) && attrs is JsonElement el)
        {
            attributes = new Dictionary<string, object>();
            foreach (var prop in el.EnumerateObject())
            {
                attributes[prop.Name] = ConvertJsonElement(prop.Value);
            }
        }

        span.AddEvent(eventName, attributes);
        return new Dictionary<string, object> { ["success"] = true };
    }

    private Dictionary<string, object> HandleSetStatus(Dictionary<string, object> payload)
    {
        var spanId = payload.GetValueOrDefault("spanId")?.ToString();
        var span = string.IsNullOrEmpty(spanId) ? GetCurrentSpan() :
            (_activeSpans.TryGetValue(spanId, out var s) ? s : null);

        if (span == null)
        {
            return new Dictionary<string, object> { ["error"] = "No active span" };
        }

        var statusStr = payload.GetValueOrDefault("status")?.ToString() ?? "ok";
        var message = payload.GetValueOrDefault("message")?.ToString();

        var status = statusStr.ToLowerInvariant() switch
        {
            "ok" => TracingSpanStatus.Ok,
            "error" => TracingSpanStatus.Error,
            _ => TracingSpanStatus.Unset
        };

        span.SetStatus(status, message);
        return new Dictionary<string, object> { ["success"] = true };
    }

    private Dictionary<string, object> HandleSetAttribute(Dictionary<string, object> payload)
    {
        var spanId = payload.GetValueOrDefault("spanId")?.ToString();
        var span = string.IsNullOrEmpty(spanId) ? GetCurrentSpan() :
            (_activeSpans.TryGetValue(spanId, out var s) ? s : null);

        if (span == null)
        {
            return new Dictionary<string, object> { ["error"] = "No active span" };
        }

        var key = payload.GetValueOrDefault("key")?.ToString();
        if (string.IsNullOrEmpty(key))
        {
            return new Dictionary<string, object> { ["error"] = "key is required" };
        }

        var value = payload.GetValueOrDefault("value");
        span.SetAttribute(key, value ?? string.Empty);
        return new Dictionary<string, object> { ["success"] = true };
    }

    private Dictionary<string, object> HandleAddLink(Dictionary<string, object> payload)
    {
        var spanId = payload.GetValueOrDefault("spanId")?.ToString();
        var span = string.IsNullOrEmpty(spanId) ? GetCurrentSpan() :
            (_activeSpans.TryGetValue(spanId, out var s) ? s : null);

        if (span == null)
        {
            return new Dictionary<string, object> { ["error"] = "No active span" };
        }

        var linkedTraceId = payload.GetValueOrDefault("linkedTraceId")?.ToString();
        var linkedSpanId = payload.GetValueOrDefault("linkedSpanId")?.ToString();

        if (string.IsNullOrEmpty(linkedTraceId) || string.IsNullOrEmpty(linkedSpanId))
        {
            return new Dictionary<string, object> { ["error"] = "linkedTraceId and linkedSpanId are required" };
        }

        span.AddLink(new SpanLink
        {
            TraceId = linkedTraceId,
            SpanId = linkedSpanId,
            TraceState = payload.GetValueOrDefault("linkedTraceState")?.ToString()
        });

        return new Dictionary<string, object> { ["success"] = true };
    }

    private Dictionary<string, object> HandleGetContext()
    {
        var ctx = _currentContext.Value;
        if (ctx == null)
        {
            return new Dictionary<string, object> { ["hasContext"] = false };
        }

        return new Dictionary<string, object>
        {
            ["hasContext"] = true,
            ["traceId"] = ctx.TraceId,
            ["parentSpanId"] = ctx.ParentSpanId,
            ["traceparent"] = ctx.ToTraceparent(),
            ["tracestate"] = ctx.TraceState?.ToString() ?? string.Empty,
            ["sampled"] = ctx.IsSampled
        };
    }

    private Dictionary<string, object> HandleSetContext(Dictionary<string, object> payload)
    {
        var traceparent = payload.GetValueOrDefault("traceparent")?.ToString();
        if (string.IsNullOrEmpty(traceparent))
        {
            return new Dictionary<string, object> { ["error"] = "traceparent is required" };
        }

        var tracestate = payload.GetValueOrDefault("tracestate")?.ToString();
        SetContextFromHeaders(traceparent, tracestate);

        return new Dictionary<string, object> { ["success"] = true };
    }

    private Dictionary<string, object> HandleInjectContext(Dictionary<string, object> payload)
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["headers"] = GetPropagationHeaders()
        };
    }

    private Dictionary<string, object> HandleExtractContext(Dictionary<string, object> payload)
    {
        var traceparent = payload.GetValueOrDefault("traceparent")?.ToString();
        if (string.IsNullOrEmpty(traceparent))
        {
            return new Dictionary<string, object> { ["error"] = "traceparent is required" };
        }

        var tracestate = payload.GetValueOrDefault("tracestate")?.ToString();
        var baggageHeader = payload.GetValueOrDefault("baggage")?.ToString();

        _currentContext.Value = TraceContext.Parse(traceparent, tracestate);
        if (!string.IsNullOrEmpty(baggageHeader))
        {
            _currentBaggage.Value = Baggage.Parse(baggageHeader);
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["hasContext"] = _currentContext.Value != null
        };
    }

    private Dictionary<string, object> HandleGetBaggage(Dictionary<string, object> payload)
    {
        var key = payload.GetValueOrDefault("key")?.ToString();
        if (string.IsNullOrEmpty(key))
        {
            return new Dictionary<string, object> { ["error"] = "key is required" };
        }

        var value = GetBaggage(key);
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["key"] = key,
            ["value"] = value ?? string.Empty,
            ["found"] = value != null
        };
    }

    private Dictionary<string, object> HandleSetBaggage(Dictionary<string, object> payload)
    {
        var key = payload.GetValueOrDefault("key")?.ToString();
        var value = payload.GetValueOrDefault("value")?.ToString();

        if (string.IsNullOrEmpty(key) || value == null)
        {
            return new Dictionary<string, object> { ["error"] = "key and value are required" };
        }

        SetBaggage(key, value);
        return new Dictionary<string, object> { ["success"] = true };
    }

    private Dictionary<string, object> HandleRemoveBaggage(Dictionary<string, object> payload)
    {
        var key = payload.GetValueOrDefault("key")?.ToString();
        if (string.IsNullOrEmpty(key))
        {
            return new Dictionary<string, object> { ["error"] = "key is required" };
        }

        RemoveBaggage(key);
        return new Dictionary<string, object> { ["success"] = true };
    }

    private Dictionary<string, object> HandleGetAllBaggage()
    {
        var baggage = _currentBaggage.Value;
        var entries = baggage?.Entries.ToDictionary(
            e => e.Key,
            e => (object)e.Value.Value) ?? new Dictionary<string, object>();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["entries"] = entries,
            ["header"] = baggage?.ToString() ?? string.Empty
        };
    }

    private Dictionary<string, object> HandleVisualize(Dictionary<string, object> payload)
    {
        var traceId = payload.GetValueOrDefault("traceId")?.ToString();
        if (string.IsNullOrEmpty(traceId))
        {
            traceId = _currentContext.Value?.TraceId;
        }

        if (string.IsNullOrEmpty(traceId))
        {
            return new Dictionary<string, object> { ["error"] = "traceId is required" };
        }

        var viz = GetTraceVisualization(traceId);
        if (viz == null)
        {
            return new Dictionary<string, object> { ["error"] = "Trace not found" };
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["traceId"] = viz.TraceId,
            ["duration"] = viz.Duration.TotalMilliseconds,
            ["serviceCount"] = viz.ServiceCount,
            ["errorCount"] = viz.ErrorCount,
            ["startTime"] = viz.StartTime.ToString("O"),
            ["spanCount"] = viz.Spans.Count,
            ["spans"] = viz.Spans.Select(s => new Dictionary<string, object>
            {
                ["spanId"] = s.SpanId,
                ["parentSpanId"] = s.ParentSpanId ?? string.Empty,
                ["name"] = s.Name,
                ["serviceName"] = s.ServiceName,
                ["kind"] = s.Kind.ToString(),
                ["startOffset"] = s.StartOffset.TotalMilliseconds,
                ["duration"] = s.Duration.TotalMilliseconds,
                ["hasError"] = s.HasError,
                ["childCount"] = s.ChildCount
            }).ToList(),
            ["criticalPath"] = viz.CriticalPath ?? new List<string>()
        };
    }

    private Dictionary<string, object> HandleConfigure(Dictionary<string, object> payload)
    {
        if (payload.TryGetValue("serviceName", out var sn) && sn != null)
        {
            _serviceName = sn.ToString()!;
        }

        if (payload.TryGetValue("serviceVersion", out var sv) && sv != null)
        {
            _serviceVersion = sv.ToString()!;
        }

        if (payload.TryGetValue("samplingRate", out var sr))
        {
            if (sr is double rate)
            {
                _samplingRate = Math.Clamp(rate, 0.0, 1.0);
            }
            else if (sr is JsonElement el && el.TryGetDouble(out var d))
            {
                _samplingRate = Math.Clamp(d, 0.0, 1.0);
            }
        }

        if (payload.TryGetValue("exportIntervalSeconds", out var ei))
        {
            if (ei is double interval)
            {
                _exportInterval = TimeSpan.FromSeconds(interval);
            }
            else if (ei is JsonElement el && el.TryGetDouble(out var d))
            {
                _exportInterval = TimeSpan.FromSeconds(d);
            }
        }

        if (payload.TryGetValue("maxBatchSize", out var mbs))
        {
            if (mbs is int batch)
            {
                _maxBatchSize = batch;
            }
            else if (mbs is JsonElement el && el.TryGetInt32(out var i))
            {
                _maxBatchSize = i;
            }
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["serviceName"] = _serviceName,
            ["serviceVersion"] = _serviceVersion,
            ["samplingRate"] = _samplingRate,
            ["exportIntervalSeconds"] = _exportInterval.TotalSeconds,
            ["maxBatchSize"] = _maxBatchSize
        };
    }

    private async Task<Dictionary<string, object>> HandleFlushAsync()
    {
        await FlushAsync();
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["spansExported"] = TotalSpansExported
        };
    }

    private Dictionary<string, object> HandleStats()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["totalSpansCreated"] = TotalSpansCreated,
            ["totalSpansExported"] = TotalSpansExported,
            ["totalSpansDropped"] = TotalSpansDropped,
            ["activeSpans"] = _activeSpans.Count,
            ["pendingSpans"] = _completedSpans.Count,
            ["tracesTracked"] = _traceSpans.Count,
            ["exporterCount"] = _exporters.Count
        };
    }

    #endregion

    #region Helpers

    private static string GenerateTraceId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GenerateSpanId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static object ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => string.Empty,
            _ => element.ToString()
        };
    }

    #endregion

    #region Plugin Metadata

    /// <inheritdoc />
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new()
            {
                Name = "trace.start",
                Description = "Start a new distributed trace span with W3C context support",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["name"] = new { type = "string", description = "Span name/operation" },
                        ["kind"] = new { type = "string", description = "Span kind (Internal, Server, Client, Producer, Consumer)" },
                        ["traceparent"] = new { type = "string", description = "W3C traceparent header for parent context" },
                        ["tracestate"] = new { type = "string", description = "W3C tracestate header" },
                        ["attributes"] = new { type = "object", description = "Initial span attributes" }
                    },
                    ["required"] = new[] { "name" }
                }
            },
            new()
            {
                Name = "baggage.set",
                Description = "Set a baggage value for cross-service propagation",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["key"] = new { type = "string", description = "Baggage key" },
                        ["value"] = new { type = "string", description = "Baggage value" }
                    },
                    ["required"] = new[] { "key", "value" }
                }
            },
            new()
            {
                Name = "visualize",
                Description = "Get visualization data for a trace",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["traceId"] = new { type = "string", description = "Trace ID to visualize" }
                    }
                }
            }
        };
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        return new Dictionary<string, object>
        {
            ["Description"] = "Vendor-neutral distributed tracing with W3C Trace Context and Baggage propagation",
            ["FeatureType"] = "DistributedTracing",
            ["ServiceName"] = _serviceName,
            ["ServiceVersion"] = _serviceVersion,
            ["ServiceInstanceId"] = _serviceInstanceId,
            ["SupportsW3CTraceContext"] = true,
            ["SupportsW3CBaggage"] = true,
            ["SupportsSpanLinks"] = true,
            ["SupportsAutoInstrumentation"] = true,
            ["SupportsVisualization"] = true,
            ["SamplingRate"] = _samplingRate,
            ["ActiveSpans"] = _activeSpans.Count,
            ["TotalSpansCreated"] = TotalSpansCreated,
            ["ExporterCount"] = _exporters.Count
        };
    }

    #endregion
}

#region Exporters

/// <summary>
/// W3C Trace Context propagator.
/// </summary>
internal sealed class W3CContextPropagator : IContextPropagator
{
    private const string TraceparentHeader = "traceparent";
    private const string TracestateHeader = "tracestate";
    private const string BaggageHeader = "baggage";

    /// <inheritdoc />
    public void Inject<TCarrier>(
        TraceContext context,
        Baggage? baggage,
        TCarrier carrier,
        Action<TCarrier, string, string> setter)
    {
        setter(carrier, TraceparentHeader, context.ToTraceparent());

        if (context.TraceState != null)
        {
            setter(carrier, TracestateHeader, context.TraceState.ToString());
        }

        if (baggage != null && baggage.Entries.Count > 0)
        {
            setter(carrier, BaggageHeader, baggage.ToString());
        }
    }

    /// <inheritdoc />
    public (TraceContext? Context, Baggage? Baggage) Extract<TCarrier>(
        TCarrier carrier,
        Func<TCarrier, string, string?> getter)
    {
        var traceparent = getter(carrier, TraceparentHeader);
        var tracestate = getter(carrier, TracestateHeader);
        var baggageHeader = getter(carrier, BaggageHeader);

        var context = TraceContext.Parse(traceparent ?? string.Empty, tracestate);
        var baggage = Baggage.Parse(baggageHeader);

        return (context, baggage);
    }
}

/// <summary>
/// Console trace exporter for development/debugging.
/// </summary>
public sealed class ConsoleTraceExporter : ITraceExporter
{
    /// <inheritdoc />
    public string Name => "Console";

    /// <inheritdoc />
    public Task ExportAsync(IReadOnlyList<TracingSpan> spans, CancellationToken ct = default)
    {
        foreach (var span in spans)
        {
            var status = span.Status switch
            {
                TracingSpanStatus.Ok => "OK",
                TracingSpanStatus.Error => "ERROR",
                _ => "UNSET"
            };

            var message = $"[TRACE] {span.TraceId}:{span.SpanId} {span.Name} " +
                         $"{span.Duration?.TotalMilliseconds:F2}ms {status}";

#if DEBUG
            Console.Error.WriteLine(message);
#else
            System.Diagnostics.Debug.WriteLine(message);
#endif
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// OTLP (OpenTelemetry Protocol) trace exporter.
/// </summary>
public sealed class OtlpTraceExporter : ITraceExporter
{
    private readonly HttpClient _client;
    private readonly string _endpoint;

    /// <inheritdoc />
    public string Name => "OTLP";

    /// <summary>
    /// Creates a new OTLP trace exporter.
    /// </summary>
    /// <param name="endpoint">The OTLP endpoint URL.</param>
    /// <param name="client">Optional HTTP client.</param>
    public OtlpTraceExporter(string endpoint, HttpClient? client = null)
    {
        _endpoint = endpoint.TrimEnd('/') + "/v1/traces";
        _client = client ?? new HttpClient();
    }

    /// <inheritdoc />
    public async Task ExportAsync(IReadOnlyList<TracingSpan> spans, CancellationToken ct = default)
    {
        var payload = new
        {
            resourceSpans = new[]
            {
                new
                {
                    resource = new
                    {
                        attributes = spans.FirstOrDefault()?.Resource
                            .Select(kv => new { key = kv.Key, value = new { stringValue = kv.Value?.ToString() } })
                            .ToArray() ?? Array.Empty<object>()
                    },
                    scopeSpans = new[]
                    {
                        new
                        {
                            spans = spans.Select(s => new
                            {
                                traceId = Convert.FromHexString(s.TraceId),
                                spanId = Convert.FromHexString(s.SpanId),
                                parentSpanId = s.ParentSpanId != null ? Convert.FromHexString(s.ParentSpanId) : null,
                                name = s.Name,
                                kind = (int)s.Kind + 1,
                                startTimeUnixNano = s.StartTime.ToUnixTimeMilliseconds() * 1_000_000,
                                endTimeUnixNano = (s.EndTime ?? s.StartTime).ToUnixTimeMilliseconds() * 1_000_000,
                                status = new
                                {
                                    code = (int)s.Status,
                                    message = s.StatusMessage ?? string.Empty
                                },
                                attributes = s.Attributes
                                    .Select(kv => new { key = kv.Key, value = new { stringValue = kv.Value?.ToString() } })
                                    .ToArray()
                            }).ToArray()
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        await _client.PostAsync(_endpoint, content, ct);
    }
}

/// <summary>
/// Jaeger trace exporter.
/// </summary>
public sealed class JaegerTraceExporter : ITraceExporter
{
    private readonly HttpClient _client;
    private readonly string _endpoint;
    private readonly string _serviceName;

    /// <inheritdoc />
    public string Name => "Jaeger";

    /// <summary>
    /// Creates a new Jaeger trace exporter.
    /// </summary>
    /// <param name="endpoint">The Jaeger collector endpoint.</param>
    /// <param name="serviceName">The service name.</param>
    /// <param name="client">Optional HTTP client.</param>
    public JaegerTraceExporter(string endpoint, string serviceName, HttpClient? client = null)
    {
        _endpoint = endpoint;
        _serviceName = serviceName;
        _client = client ?? new HttpClient();
    }

    /// <inheritdoc />
    public async Task ExportAsync(IReadOnlyList<TracingSpan> spans, CancellationToken ct = default)
    {
        var batch = new
        {
            process = new { serviceName = _serviceName },
            spans = spans.Select(s => new
            {
                traceIdHigh = Convert.ToInt64(s.TraceId[..16], 16),
                traceIdLow = Convert.ToInt64(s.TraceId[16..], 16),
                spanId = Convert.ToInt64(s.SpanId, 16),
                parentSpanId = string.IsNullOrEmpty(s.ParentSpanId) ? 0L : Convert.ToInt64(s.ParentSpanId, 16),
                operationName = s.Name,
                startTime = s.StartTime.ToUnixTimeMilliseconds() * 1000,
                duration = (long)(s.Duration?.TotalMicroseconds ?? 0)
            }).ToArray()
        };

        var json = JsonSerializer.Serialize(batch);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        await _client.PostAsync(_endpoint, content, ct);
    }
}

/// <summary>
/// Zipkin trace exporter.
/// </summary>
public sealed class ZipkinTraceExporter : ITraceExporter
{
    private readonly HttpClient _client;
    private readonly string _endpoint;
    private readonly string _serviceName;

    /// <inheritdoc />
    public string Name => "Zipkin";

    /// <summary>
    /// Creates a new Zipkin trace exporter.
    /// </summary>
    /// <param name="endpoint">The Zipkin collector endpoint.</param>
    /// <param name="serviceName">The service name.</param>
    /// <param name="client">Optional HTTP client.</param>
    public ZipkinTraceExporter(string endpoint, string serviceName, HttpClient? client = null)
    {
        _endpoint = endpoint;
        _serviceName = serviceName;
        _client = client ?? new HttpClient();
    }

    /// <inheritdoc />
    public async Task ExportAsync(IReadOnlyList<TracingSpan> spans, CancellationToken ct = default)
    {
        var zipkinSpans = spans.Select(s => new
        {
            traceId = s.TraceId,
            id = s.SpanId,
            parentId = s.ParentSpanId,
            name = s.Name,
            timestamp = s.StartTime.ToUnixTimeMilliseconds() * 1000,
            duration = (long)(s.Duration?.TotalMicroseconds ?? 0),
            localEndpoint = new { serviceName = _serviceName },
            kind = s.Kind switch
            {
                TracingSpanKind.Client => "CLIENT",
                TracingSpanKind.Server => "SERVER",
                TracingSpanKind.Producer => "PRODUCER",
                TracingSpanKind.Consumer => "CONSUMER",
                _ => (string?)null
            },
            tags = s.Attributes.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty)
        }).ToArray();

        var json = JsonSerializer.Serialize(zipkinSpans);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        await _client.PostAsync(_endpoint, content, ct);
    }
}

#endregion

#region Telemetry Provider Interface

/// <summary>
/// Interface for telemetry providers.
/// </summary>
public interface ITelemetryProvider
{
    /// <summary>
    /// Records a metric value.
    /// </summary>
    /// <param name="name">The metric name.</param>
    /// <param name="value">The metric value.</param>
    /// <param name="tags">Optional tags.</param>
    void RecordMetric(string name, double value, Dictionary<string, string>? tags = null);

    /// <summary>
    /// Records an event.
    /// </summary>
    /// <param name="name">The event name.</param>
    /// <param name="properties">Optional properties.</param>
    void RecordEvent(string name, Dictionary<string, object>? properties = null);

    /// <summary>
    /// Begins a tracked operation.
    /// </summary>
    /// <param name="operationName">The operation name.</param>
    /// <param name="properties">Optional properties.</param>
    /// <returns>A disposable that ends the operation when disposed.</returns>
    IDisposable BeginOperation(string operationName, Dictionary<string, object>? properties = null);

    /// <summary>
    /// Sets a property on the current operation.
    /// </summary>
    /// <param name="key">The property key.</param>
    /// <param name="value">The property value.</param>
    void SetProperty(string key, object value);

    /// <summary>
    /// Gets the current correlation ID.
    /// </summary>
    /// <returns>The correlation ID, or null if none.</returns>
    string? GetCorrelationId();

    /// <summary>
    /// Sets the correlation ID.
    /// </summary>
    /// <param name="correlationId">The correlation ID.</param>
    void SetCorrelationId(string correlationId);
}

#endregion
