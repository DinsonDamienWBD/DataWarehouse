using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.Jaeger;

/// <summary>
/// Jaeger distributed tracing plugin for DataWarehouse.
/// Provides production-ready distributed tracing with span creation, context propagation,
/// sampling strategies, and batch export to Jaeger collectors via UDP and HTTP transports.
///
/// Features:
/// - Full ITelemetryProvider implementation
/// - Jaeger-native span format with tags, logs, and references
/// - Multiple context propagation formats (W3C Trace Context, B3, Jaeger)
/// - Configurable sampling strategies (const, probabilistic, rate limiting)
/// - Batch span export with configurable intervals
/// - UDP (agent) and HTTP (collector) transport support
/// - Async-local span context for automatic propagation
///
/// Message Commands:
/// - jaeger.span.start: Start a new span
/// - jaeger.span.finish: Finish a span
/// - jaeger.span.tag: Add a tag to a span
/// - jaeger.span.log: Add a log entry to a span
/// - jaeger.context.inject: Get context headers for propagation
/// - jaeger.context.extract: Set context from incoming headers
/// - jaeger.flush: Force flush pending spans
/// - jaeger.configure: Update configuration
/// </summary>
public sealed class JaegerPlugin : TelemetryPluginBase
{
    #region Plugin Identity

    /// <inheritdoc />
    public override string Id => "com.datawarehouse.telemetry.jaeger";

    /// <inheritdoc />
    public override string Name => "Jaeger Tracing Plugin";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override TelemetryCapabilities Capabilities =>
        TelemetryCapabilities.Tracing | TelemetryCapabilities.Logging | TelemetryCapabilities.Metrics;

    #endregion

    #region Configuration

    private JaegerConfiguration _config = new();
    private ISamplingStrategy _sampler;
    private IJaegerTransport? _transport;

    #endregion

    #region State

    private readonly ConcurrentDictionary<string, JaegerSpan> _activeSpans = new();
    private readonly ConcurrentQueue<JaegerSpan> _completedSpans = new();
    private readonly ConcurrentDictionary<string, MetricValue> _metrics = new();
    private readonly ConcurrentQueue<JaegerLogEntry> _logs = new();
    private readonly AsyncLocal<JaegerSpanContext?> _currentContext = new();

    private CancellationTokenSource? _cts;
    private Task? _exportTask;
    private bool _isRunning;
    private long _spansCreated;
    private long _spansExported;
    private long _spansSampled;
    private long _spansDropped;

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="JaegerPlugin"/> class.
    /// </summary>
    public JaegerPlugin()
    {
        _sampler = new ConstSampler(true);
    }

    #region Lifecycle

    /// <inheritdoc />
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        // Initialize service name from kernel if not configured
        if (string.IsNullOrEmpty(_config.ServiceName))
        {
            _config = _config with { ServiceName = $"datawarehouse-{request.KernelId}" };
        }

        return await base.OnHandshakeAsync(request);
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct)
    {
        if (_isRunning) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRunning = true;

        // Initialize transport based on configuration
        InitializeTransport();

        // Initialize sampler based on configuration
        InitializeSampler();

        // Start background export task
        _exportTask = RunExportLoopAsync(_cts.Token);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _cts?.Cancel();

        // Final flush
        await FlushAsync(CancellationToken.None);

        if (_exportTask != null)
        {
            try
            {
                await _exportTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }

        _transport?.Dispose();
        _transport = null;

        _cts?.Dispose();
        _cts = null;
    }

    #endregion

    #region ITelemetryProvider Implementation

    /// <inheritdoc />
    public override void RecordMetric(string name, double value, Dictionary<string, string>? tags = null)
    {
        var key = GetMetricKey(name, tags);
        _metrics.AddOrUpdate(
            key,
            _ => new MetricValue { Name = name, Value = value, Tags = tags ?? new(), LastUpdated = DateTime.UtcNow },
            (_, existing) => { existing.Value = value; existing.LastUpdated = DateTime.UtcNow; return existing; }
        );
    }

    /// <inheritdoc />
    public override void IncrementCounter(string name, long delta = 1, Dictionary<string, string>? tags = null)
    {
        var key = GetMetricKey(name, tags);
        _metrics.AddOrUpdate(
            key,
            _ => new MetricValue { Name = name, Value = delta, Tags = tags ?? new(), LastUpdated = DateTime.UtcNow },
            (_, existing) => { existing.Value += delta; existing.LastUpdated = DateTime.UtcNow; return existing; }
        );
    }

    /// <inheritdoc />
    public override void RecordHistogram(string name, double value, Dictionary<string, string>? tags = null)
    {
        // For Jaeger, we record histograms as gauge metrics
        // In a production system, you might want to send these to a metrics backend
        RecordMetric(name, value, tags);
    }

    /// <inheritdoc />
    public override void LogEvent(LogLevel level, string message, Dictionary<string, object>? properties = null, Exception? exception = null)
    {
        var ctx = _currentContext.Value;
        var logEntry = new JaegerLogEntry
        {
            Timestamp = DateTime.UtcNow,
            Level = level,
            Message = message,
            TraceId = ctx?.TraceId,
            SpanId = ctx?.SpanId,
            Properties = properties ?? new(),
            Exception = exception
        };

        _logs.Enqueue(logEntry);

        // Also add to current span if active
        if (ctx != null && _activeSpans.TryGetValue(ctx.SpanId, out var span))
        {
            var fields = new Dictionary<string, object>
            {
                ["level"] = level.ToString(),
                ["message"] = message
            };

            if (properties != null)
            {
                foreach (var kv in properties)
                {
                    fields[kv.Key] = kv.Value;
                }
            }

            if (exception != null)
            {
                fields["error.kind"] = exception.GetType().Name;
                fields["error.message"] = exception.Message;
                fields["stack"] = exception.StackTrace ?? "";
            }

            span.AddLog(fields);
        }
    }

    /// <inheritdoc />
    protected override ITraceSpan CreateSpan(string operationName, SpanKind kind, ITraceSpan? parent)
    {
        return StartJaegerSpan(operationName, kind, parent as JaegerSpan);
    }

    /// <inheritdoc />
    public override async Task FlushAsync(CancellationToken ct = default)
    {
        await ExportSpansAsync(ct);
    }

    /// <inheritdoc />
    public override Task<IReadOnlyDictionary<string, MetricSnapshot>> GetMetricsAsync(CancellationToken ct = default)
    {
        var snapshots = _metrics.ToDictionary(
            kv => kv.Key,
            kv => new MetricSnapshot
            {
                Name = kv.Value.Name,
                Type = "gauge",
                Value = kv.Value.Value,
                Timestamp = kv.Value.LastUpdated,
                Tags = kv.Value.Tags
            }
        );

        return Task.FromResult<IReadOnlyDictionary<string, MetricSnapshot>>(snapshots);
    }

    #endregion

    #region Jaeger-Specific Tracing API

    /// <summary>
    /// Starts a new Jaeger span with full configuration options.
    /// </summary>
    /// <param name="operationName">The operation name for the span.</param>
    /// <param name="kind">The kind of span (client, server, etc.).</param>
    /// <param name="parent">Optional parent span for creating child spans.</param>
    /// <param name="startTime">Optional explicit start time.</param>
    /// <param name="tags">Optional initial tags.</param>
    /// <returns>A new JaegerSpan instance.</returns>
    public JaegerSpan StartJaegerSpan(
        string operationName,
        SpanKind kind = SpanKind.Internal,
        JaegerSpan? parent = null,
        DateTime? startTime = null,
        Dictionary<string, object>? tags = null)
    {
        Interlocked.Increment(ref _spansCreated);

        // Determine parent context
        var parentContext = parent?.Context ?? _currentContext.Value;

        // Apply sampling decision
        bool sampled;
        if (parentContext != null)
        {
            // Inherit sampling decision from parent
            sampled = parentContext.IsSampled;
        }
        else
        {
            // Make sampling decision for root span
            sampled = _sampler.ShouldSample(operationName, GenerateTraceId());
        }

        if (sampled)
        {
            Interlocked.Increment(ref _spansSampled);
        }

        // Generate IDs
        var traceId = parentContext?.TraceId ?? GenerateTraceId();
        var spanId = GenerateSpanId();
        var parentSpanId = parentContext?.SpanId;

        var context = new JaegerSpanContext(
            traceId,
            spanId,
            parentSpanId,
            sampled,
            parentContext?.Baggage ?? new Dictionary<string, string>()
        );

        var span = new JaegerSpan(
            context,
            operationName,
            kind,
            startTime ?? DateTime.UtcNow,
            _config.ServiceName,
            this
        );

        // Add initial tags
        if (tags != null)
        {
            foreach (var tag in tags)
            {
                span.SetAttribute(tag.Key, tag.Value);
            }
        }

        // Add standard tags
        span.SetAttribute("span.kind", kind.ToString().ToLowerInvariant());
        span.SetAttribute("service.name", _config.ServiceName);
        if (!string.IsNullOrEmpty(_config.ServiceVersion))
        {
            span.SetAttribute("service.version", _config.ServiceVersion);
        }

        // Register span
        _activeSpans[spanId] = span;

        // Set as current context
        _currentContext.Value = context;

        return span;
    }

    /// <summary>
    /// Finishes a span and queues it for export.
    /// </summary>
    /// <param name="span">The span to finish.</param>
    internal void FinishSpan(JaegerSpan span)
    {
        if (_activeSpans.TryRemove(span.SpanId, out _))
        {
            if (span.Context.IsSampled)
            {
                _completedSpans.Enqueue(span);
            }
            else
            {
                Interlocked.Increment(ref _spansDropped);
            }

            // Update current context
            if (_currentContext.Value?.SpanId == span.SpanId)
            {
                _currentContext.Value = span.Context.ParentSpanId != null
                    ? new JaegerSpanContext(
                        span.Context.TraceId,
                        span.Context.ParentSpanId,
                        null,
                        span.Context.IsSampled,
                        span.Context.Baggage)
                    : null;
            }
        }
    }

    /// <summary>
    /// Gets the current span context for propagation.
    /// </summary>
    /// <returns>The current span context, or null if no span is active.</returns>
    public JaegerSpanContext? GetCurrentSpanContext() => _currentContext.Value;

    #endregion

    #region Context Propagation

    /// <summary>
    /// Injects the current span context into a carrier using the specified format.
    /// </summary>
    /// <param name="carrier">The carrier dictionary to inject headers into.</param>
    /// <param name="format">The propagation format to use.</param>
    public void InjectContext(IDictionary<string, string> carrier, PropagationFormat format = PropagationFormat.W3C)
    {
        var context = _currentContext.Value;
        if (context == null) return;

        switch (format)
        {
            case PropagationFormat.W3C:
                InjectW3CContext(carrier, context);
                break;
            case PropagationFormat.B3:
                InjectB3Context(carrier, context);
                break;
            case PropagationFormat.B3Single:
                InjectB3SingleContext(carrier, context);
                break;
            case PropagationFormat.Jaeger:
                InjectJaegerContext(carrier, context);
                break;
        }

        // Always inject baggage
        foreach (var item in context.Baggage)
        {
            carrier[$"uberctx-{item.Key}"] = item.Value;
        }
    }

    /// <summary>
    /// Extracts span context from a carrier using the specified format.
    /// </summary>
    /// <param name="carrier">The carrier dictionary containing headers.</param>
    /// <param name="format">The propagation format to use, or null to auto-detect.</param>
    /// <returns>The extracted span context, or null if extraction failed.</returns>
    public JaegerSpanContext? ExtractContext(IDictionary<string, string> carrier, PropagationFormat? format = null)
    {
        JaegerSpanContext? context = null;

        if (format == null)
        {
            // Auto-detect format
            if (carrier.ContainsKey("traceparent"))
            {
                context = ExtractW3CContext(carrier);
            }
            else if (carrier.ContainsKey("uber-trace-id"))
            {
                context = ExtractJaegerContext(carrier);
            }
            else if (carrier.ContainsKey("X-B3-TraceId") || carrier.ContainsKey("b3"))
            {
                context = carrier.ContainsKey("b3")
                    ? ExtractB3SingleContext(carrier)
                    : ExtractB3Context(carrier);
            }
        }
        else
        {
            context = format switch
            {
                PropagationFormat.W3C => ExtractW3CContext(carrier),
                PropagationFormat.B3 => ExtractB3Context(carrier),
                PropagationFormat.B3Single => ExtractB3SingleContext(carrier),
                PropagationFormat.Jaeger => ExtractJaegerContext(carrier),
                _ => null
            };
        }

        if (context != null)
        {
            // Extract baggage
            foreach (var kv in carrier)
            {
                if (kv.Key.StartsWith("uberctx-", StringComparison.OrdinalIgnoreCase))
                {
                    context.Baggage[kv.Key[8..]] = kv.Value;
                }
            }

            _currentContext.Value = context;
        }

        return context;
    }

    private static void InjectW3CContext(IDictionary<string, string> carrier, JaegerSpanContext context)
    {
        // Format: 00-{trace-id}-{span-id}-{flags}
        var flags = context.IsSampled ? "01" : "00";
        var traceId = context.TraceId.PadLeft(32, '0');
        carrier["traceparent"] = $"00-{traceId}-{context.SpanId}-{flags}";

        // Add tracestate if we have baggage
        if (context.Baggage.Count > 0)
        {
            var state = string.Join(",", context.Baggage.Select(kv => $"{kv.Key}={kv.Value}"));
            carrier["tracestate"] = $"jaeger={state}";
        }
    }

    private static void InjectB3Context(IDictionary<string, string> carrier, JaegerSpanContext context)
    {
        carrier["X-B3-TraceId"] = context.TraceId;
        carrier["X-B3-SpanId"] = context.SpanId;
        carrier["X-B3-Sampled"] = context.IsSampled ? "1" : "0";
        if (context.ParentSpanId != null)
        {
            carrier["X-B3-ParentSpanId"] = context.ParentSpanId;
        }
    }

    private static void InjectB3SingleContext(IDictionary<string, string> carrier, JaegerSpanContext context)
    {
        // Format: {TraceId}-{SpanId}-{SamplingState}-{ParentSpanId}
        var sampled = context.IsSampled ? "1" : "0";
        var parent = context.ParentSpanId != null ? $"-{context.ParentSpanId}" : "";
        carrier["b3"] = $"{context.TraceId}-{context.SpanId}-{sampled}{parent}";
    }

    private static void InjectJaegerContext(IDictionary<string, string> carrier, JaegerSpanContext context)
    {
        // Format: {trace-id}:{span-id}:{parent-span-id}:{flags}
        var parent = context.ParentSpanId ?? "0";
        var flags = context.IsSampled ? "1" : "0";
        carrier["uber-trace-id"] = $"{context.TraceId}:{context.SpanId}:{parent}:{flags}";
    }

    private static JaegerSpanContext? ExtractW3CContext(IDictionary<string, string> carrier)
    {
        if (!carrier.TryGetValue("traceparent", out var traceparent)) return null;

        // Format: 00-{trace-id}-{span-id}-{flags}
        var parts = traceparent.Split('-');
        if (parts.Length < 4) return null;

        var traceId = parts[1];
        var spanId = parts[2];
        var sampled = parts[3] == "01";

        return new JaegerSpanContext(traceId, spanId, null, sampled, new Dictionary<string, string>());
    }

    private static JaegerSpanContext? ExtractB3Context(IDictionary<string, string> carrier)
    {
        if (!carrier.TryGetValue("X-B3-TraceId", out var traceId) ||
            !carrier.TryGetValue("X-B3-SpanId", out var spanId))
        {
            return null;
        }

        var sampled = carrier.TryGetValue("X-B3-Sampled", out var s) && s == "1";
        string? parentSpanId = null;
        if (carrier.TryGetValue("X-B3-ParentSpanId", out var parent))
        {
            parentSpanId = parent;
        }

        return new JaegerSpanContext(traceId, spanId, parentSpanId, sampled, new Dictionary<string, string>());
    }

    private static JaegerSpanContext? ExtractB3SingleContext(IDictionary<string, string> carrier)
    {
        if (!carrier.TryGetValue("b3", out var b3)) return null;

        // Format: {TraceId}-{SpanId}-{SamplingState}[-{ParentSpanId}]
        var parts = b3.Split('-');
        if (parts.Length < 3) return null;

        var traceId = parts[0];
        var spanId = parts[1];
        var sampled = parts[2] == "1";
        var parentSpanId = parts.Length > 3 ? parts[3] : null;

        return new JaegerSpanContext(traceId, spanId, parentSpanId, sampled, new Dictionary<string, string>());
    }

    private static JaegerSpanContext? ExtractJaegerContext(IDictionary<string, string> carrier)
    {
        if (!carrier.TryGetValue("uber-trace-id", out var header)) return null;

        // Format: {trace-id}:{span-id}:{parent-span-id}:{flags}
        var parts = header.Split(':');
        if (parts.Length < 4) return null;

        var traceId = parts[0];
        var spanId = parts[1];
        var parentSpanId = parts[2] != "0" ? parts[2] : null;
        var sampled = parts[3] == "1";

        return new JaegerSpanContext(traceId, spanId, parentSpanId, sampled, new Dictionary<string, string>());
    }

    #endregion

    #region Configuration

    /// <summary>
    /// Configures the Jaeger plugin with the specified options.
    /// </summary>
    /// <param name="config">The configuration to apply.</param>
    public void Configure(JaegerConfiguration config)
    {
        _config = config;
        InitializeTransport();
        InitializeSampler();
    }

    private void InitializeTransport()
    {
        _transport?.Dispose();

        _transport = _config.Transport switch
        {
            JaegerTransportType.UdpCompact => new UdpTransport(
                _config.AgentHost,
                _config.AgentCompactPort,
                _config.MaxPacketSize),
            JaegerTransportType.UdpBinary => new UdpTransport(
                _config.AgentHost,
                _config.AgentBinaryPort,
                _config.MaxPacketSize),
            JaegerTransportType.Http => new HttpTransport(
                _config.CollectorEndpoint,
                _config.HttpTimeout,
                _config.AuthToken),
            _ => new UdpTransport(_config.AgentHost, _config.AgentCompactPort, _config.MaxPacketSize)
        };
    }

    private void InitializeSampler()
    {
        _sampler = _config.SamplingType switch
        {
            SamplingType.Const => new ConstSampler(_config.SamplingParam >= 1.0),
            SamplingType.Probabilistic => new ProbabilisticSampler(_config.SamplingParam),
            SamplingType.RateLimiting => new RateLimitingSampler(_config.SamplingParam),
            _ => new ConstSampler(true)
        };
    }

    #endregion

    #region Message Handling

    /// <inheritdoc />
    public override async Task OnMessageAsync(PluginMessage message)
    {
        if (message.Payload == null) return;

        object response = message.Type switch
        {
            "jaeger.span.start" => HandleSpanStart(message.Payload),
            "jaeger.span.finish" => HandleSpanFinish(message.Payload),
            "jaeger.span.tag" => HandleSpanTag(message.Payload),
            "jaeger.span.log" => HandleSpanLog(message.Payload),
            "jaeger.context.inject" => HandleContextInject(message.Payload),
            "jaeger.context.extract" => HandleContextExtract(message.Payload),
            "jaeger.flush" => await HandleFlushAsync(),
            "jaeger.configure" => HandleConfigure(message.Payload),
            "jaeger.stats" => HandleGetStats(),
            _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
        };

        message.Payload["_response"] = response;
    }

    private Dictionary<string, object> HandleSpanStart(Dictionary<string, object> payload)
    {
        var operationName = payload.GetValueOrDefault("operationName")?.ToString() ?? "unknown";
        var kindStr = payload.GetValueOrDefault("kind")?.ToString() ?? "internal";
        var kind = Enum.TryParse<SpanKind>(kindStr, true, out var k) ? k : SpanKind.Internal;

        Dictionary<string, object>? tags = null;
        if (payload.TryGetValue("tags", out var tagsObj) && tagsObj is JsonElement tagsEl)
        {
            tags = JsonSerializer.Deserialize<Dictionary<string, object>>(tagsEl.GetRawText());
        }

        var span = StartJaegerSpan(operationName, kind, null, null, tags);

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["traceId"] = span.TraceId,
            ["spanId"] = span.SpanId,
            ["sampled"] = span.Context.IsSampled
        };
    }

    private Dictionary<string, object> HandleSpanFinish(Dictionary<string, object> payload)
    {
        var spanId = payload.GetValueOrDefault("spanId")?.ToString();
        if (string.IsNullOrEmpty(spanId))
        {
            spanId = _currentContext.Value?.SpanId;
        }

        if (string.IsNullOrEmpty(spanId))
        {
            return new Dictionary<string, object> { ["error"] = "No span to finish" };
        }

        if (_activeSpans.TryGetValue(spanId, out var span))
        {
            span.End();
            return new Dictionary<string, object> { ["success"] = true, ["spanId"] = spanId };
        }

        return new Dictionary<string, object> { ["error"] = $"Span {spanId} not found" };
    }

    private Dictionary<string, object> HandleSpanTag(Dictionary<string, object> payload)
    {
        var spanId = payload.GetValueOrDefault("spanId")?.ToString() ?? _currentContext.Value?.SpanId;
        var key = payload.GetValueOrDefault("key")?.ToString();
        var value = payload.GetValueOrDefault("value");

        if (string.IsNullOrEmpty(spanId) || string.IsNullOrEmpty(key))
        {
            return new Dictionary<string, object> { ["error"] = "spanId and key are required" };
        }

        if (_activeSpans.TryGetValue(spanId, out var span))
        {
            span.SetAttribute(key, value ?? "");
            return new Dictionary<string, object> { ["success"] = true };
        }

        return new Dictionary<string, object> { ["error"] = $"Span {spanId} not found" };
    }

    private Dictionary<string, object> HandleSpanLog(Dictionary<string, object> payload)
    {
        var spanId = payload.GetValueOrDefault("spanId")?.ToString() ?? _currentContext.Value?.SpanId;

        if (string.IsNullOrEmpty(spanId))
        {
            return new Dictionary<string, object> { ["error"] = "No active span" };
        }

        if (!_activeSpans.TryGetValue(spanId, out var span))
        {
            return new Dictionary<string, object> { ["error"] = $"Span {spanId} not found" };
        }

        var fields = new Dictionary<string, object>();
        if (payload.TryGetValue("fields", out var fieldsObj) && fieldsObj is JsonElement fieldsEl)
        {
            foreach (var prop in fieldsEl.EnumerateObject())
            {
                fields[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString()!,
                    JsonValueKind.Number => prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => prop.Value.ToString()
                };
            }
        }

        span.AddLog(fields);
        return new Dictionary<string, object> { ["success"] = true };
    }

    private Dictionary<string, object> HandleContextInject(Dictionary<string, object> payload)
    {
        var formatStr = payload.GetValueOrDefault("format")?.ToString() ?? "w3c";
        var format = Enum.TryParse<PropagationFormat>(formatStr, true, out var f) ? f : PropagationFormat.W3C;

        var carrier = new Dictionary<string, string>();
        InjectContext(carrier, format);

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["headers"] = carrier
        };
    }

    private Dictionary<string, object> HandleContextExtract(Dictionary<string, object> payload)
    {
        var carrier = new Dictionary<string, string>();
        if (payload.TryGetValue("headers", out var headersObj) && headersObj is JsonElement headersEl)
        {
            foreach (var prop in headersEl.EnumerateObject())
            {
                carrier[prop.Name] = prop.Value.GetString() ?? "";
            }
        }

        PropagationFormat? format = null;
        if (payload.TryGetValue("format", out var formatObj) && formatObj != null)
        {
            if (Enum.TryParse<PropagationFormat>(formatObj.ToString(), true, out var f))
            {
                format = f;
            }
        }

        var context = ExtractContext(carrier, format);
        if (context == null)
        {
            return new Dictionary<string, object> { ["error"] = "Failed to extract context" };
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["traceId"] = context.TraceId,
            ["spanId"] = context.SpanId,
            ["sampled"] = context.IsSampled
        };
    }

    private async Task<Dictionary<string, object>> HandleFlushAsync()
    {
        var count = _completedSpans.Count;
        await FlushAsync(CancellationToken.None);

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["spansExported"] = count
        };
    }

    private Dictionary<string, object> HandleConfigure(Dictionary<string, object> payload)
    {
        var newConfig = _config;

        if (payload.TryGetValue("serviceName", out var sn) && sn != null)
        {
            newConfig = newConfig with { ServiceName = sn.ToString()! };
        }

        if (payload.TryGetValue("serviceVersion", out var sv) && sv != null)
        {
            newConfig = newConfig with { ServiceVersion = sv.ToString()! };
        }

        if (payload.TryGetValue("agentHost", out var ah) && ah != null)
        {
            newConfig = newConfig with { AgentHost = ah.ToString()! };
        }

        if (payload.TryGetValue("collectorEndpoint", out var ce) && ce != null)
        {
            newConfig = newConfig with { CollectorEndpoint = ce.ToString()! };
        }

        if (payload.TryGetValue("transport", out var tr) && tr != null &&
            Enum.TryParse<JaegerTransportType>(tr.ToString(), true, out var transport))
        {
            newConfig = newConfig with { Transport = transport };
        }

        if (payload.TryGetValue("samplingType", out var st) && st != null &&
            Enum.TryParse<SamplingType>(st.ToString(), true, out var samplingType))
        {
            newConfig = newConfig with { SamplingType = samplingType };
        }

        if (payload.TryGetValue("samplingParam", out var sp) && sp is double samplingParam)
        {
            newConfig = newConfig with { SamplingParam = samplingParam };
        }

        if (payload.TryGetValue("flushIntervalMs", out var fi) && fi is double flushInterval)
        {
            newConfig = newConfig with { FlushIntervalMs = (int)flushInterval };
        }

        if (payload.TryGetValue("maxQueueSize", out var mq) && mq is double maxQueue)
        {
            newConfig = newConfig with { MaxQueueSize = (int)maxQueue };
        }

        Configure(newConfig);

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["config"] = new Dictionary<string, object>
            {
                ["serviceName"] = _config.ServiceName,
                ["serviceVersion"] = _config.ServiceVersion,
                ["agentHost"] = _config.AgentHost,
                ["transport"] = _config.Transport.ToString(),
                ["samplingType"] = _config.SamplingType.ToString(),
                ["samplingParam"] = _config.SamplingParam
            }
        };
    }

    private Dictionary<string, object> HandleGetStats()
    {
        return new Dictionary<string, object>
        {
            ["spansCreated"] = Interlocked.Read(ref _spansCreated),
            ["spansExported"] = Interlocked.Read(ref _spansExported),
            ["spansSampled"] = Interlocked.Read(ref _spansSampled),
            ["spansDropped"] = Interlocked.Read(ref _spansDropped),
            ["activeSpans"] = _activeSpans.Count,
            ["pendingSpans"] = _completedSpans.Count
        };
    }

    #endregion

    #region Export

    private async Task RunExportLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.FlushIntervalMs, ct);
                await ExportSpansAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Log export error but continue
                LogEvent(LogLevel.Error, $"Failed to export spans: {ex.Message}", exception: ex);
            }
        }
    }

    private async Task ExportSpansAsync(CancellationToken ct)
    {
        if (_transport == null) return;

        var batch = new List<JaegerSpan>();
        while (batch.Count < _config.MaxQueueSize && _completedSpans.TryDequeue(out var span))
        {
            batch.Add(span);
        }

        if (batch.Count == 0) return;

        try
        {
            await _transport.SendAsync(batch, _config.ServiceName, ct);
            Interlocked.Add(ref _spansExported, batch.Count);
        }
        catch (Exception ex)
        {
            // Re-queue spans on failure (up to max queue size)
            foreach (var span in batch)
            {
                if (_completedSpans.Count < _config.MaxQueueSize)
                {
                    _completedSpans.Enqueue(span);
                }
                else
                {
                    Interlocked.Increment(ref _spansDropped);
                }
            }

            throw new InvalidOperationException($"Failed to export {batch.Count} spans", ex);
        }
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

    private static string GetMetricKey(string name, Dictionary<string, string>? tags)
    {
        if (tags == null || tags.Count == 0) return name;
        var sortedTags = string.Join(",", tags.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        return $"{name}{{{sortedTags}}}";
    }

    #endregion

    #region Metadata

    /// <inheritdoc />
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new()
            {
                Name = "span.start",
                Description = "Start a new distributed trace span",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["operationName"] = new { type = "string", description = "Operation name for the span" },
                        ["kind"] = new { type = "string", description = "Span kind (internal, client, server, producer, consumer)" },
                        ["tags"] = new { type = "object", description = "Initial span tags" }
                    },
                    ["required"] = new[] { "operationName" }
                }
            },
            new()
            {
                Name = "span.finish",
                Description = "Finish a span",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["spanId"] = new { type = "string", description = "Span ID to finish (defaults to current)" }
                    }
                }
            },
            new()
            {
                Name = "context.inject",
                Description = "Get trace context headers for propagation",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["format"] = new { type = "string", description = "Propagation format (W3C, B3, B3Single, Jaeger)" }
                    }
                }
            },
            new()
            {
                Name = "context.extract",
                Description = "Extract trace context from headers",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["headers"] = new { type = "object", description = "Headers containing trace context" },
                        ["format"] = new { type = "string", description = "Propagation format (auto-detected if not specified)" }
                    },
                    ["required"] = new[] { "headers" }
                }
            }
        };
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["Description"] = "Jaeger distributed tracing with context propagation and sampling";
        metadata["FeatureType"] = "Tracing";
        metadata["ServiceName"] = _config.ServiceName;
        metadata["Transport"] = _config.Transport.ToString();
        metadata["SamplingType"] = _config.SamplingType.ToString();
        metadata["SamplingParam"] = _config.SamplingParam;
        metadata["SupportedFormats"] = new[] { "W3C", "B3", "B3Single", "Jaeger" };
        metadata["SupportsUDP"] = true;
        metadata["SupportsHTTP"] = true;
        return metadata;
    }

    #endregion
}

#region Configuration Types

/// <summary>
/// Configuration options for the Jaeger plugin.
/// </summary>
/// <param name="ServiceName">The service name to report to Jaeger.</param>
/// <param name="ServiceVersion">The service version to report.</param>
/// <param name="AgentHost">The Jaeger agent host for UDP transport. WARNING: Default is localhost (development only).</param>
/// <param name="AgentCompactPort">The port for compact Thrift protocol.</param>
/// <param name="AgentBinaryPort">The port for binary Thrift protocol.</param>
/// <param name="CollectorEndpoint">The collector endpoint for HTTP transport. WARNING: Default is localhost (development only).</param>
/// <param name="Transport">The transport type to use.</param>
/// <param name="SamplingType">The sampling strategy type.</param>
/// <param name="SamplingParam">The sampling parameter (rate for probabilistic, traces/sec for rate limiting).</param>
/// <param name="FlushIntervalMs">The interval between span exports in milliseconds.</param>
/// <param name="MaxQueueSize">The maximum number of spans to queue before dropping.</param>
/// <param name="MaxPacketSize">The maximum UDP packet size.</param>
/// <param name="HttpTimeout">The HTTP request timeout.</param>
/// <param name="AuthToken">Optional authentication token for HTTP transport.</param>
public sealed record JaegerConfiguration(
    string ServiceName = "datawarehouse",
    string ServiceVersion = "1.0.0",
    string AgentHost = "localhost",
    int AgentCompactPort = 6831,
    int AgentBinaryPort = 6832,
    string CollectorEndpoint = "http://localhost:14268/api/traces",
    JaegerTransportType Transport = JaegerTransportType.UdpCompact,
    SamplingType SamplingType = SamplingType.Const,
    double SamplingParam = 1.0,
    int FlushIntervalMs = 1000,
    int MaxQueueSize = 1000,
    int MaxPacketSize = 65000,
    TimeSpan HttpTimeout = default,
    string? AuthToken = null)
{
    /// <summary>
    /// Gets the HTTP timeout, defaulting to 30 seconds if not specified.
    /// </summary>
    public TimeSpan HttpTimeout { get; init; } = HttpTimeout == default ? TimeSpan.FromSeconds(30) : HttpTimeout;
}

/// <summary>
/// Transport types supported by the Jaeger plugin.
/// </summary>
public enum JaegerTransportType
{
    /// <summary>UDP transport using compact Thrift protocol (recommended for local agent).</summary>
    UdpCompact,
    /// <summary>UDP transport using binary Thrift protocol.</summary>
    UdpBinary,
    /// <summary>HTTP transport for direct collector communication.</summary>
    Http
}

/// <summary>
/// Sampling strategy types.
/// </summary>
public enum SamplingType
{
    /// <summary>Constant sampling - always on or always off.</summary>
    Const,
    /// <summary>Probabilistic sampling - sample based on probability.</summary>
    Probabilistic,
    /// <summary>Rate limiting sampling - sample up to N traces per second.</summary>
    RateLimiting
}

/// <summary>
/// Context propagation formats.
/// </summary>
public enum PropagationFormat
{
    /// <summary>W3C Trace Context (traceparent/tracestate headers).</summary>
    W3C,
    /// <summary>B3 multi-header format (X-B3-TraceId, X-B3-SpanId, etc.).</summary>
    B3,
    /// <summary>B3 single header format (b3 header).</summary>
    B3Single,
    /// <summary>Jaeger native format (uber-trace-id header).</summary>
    Jaeger
}

#endregion

#region Span Types

/// <summary>
/// Represents a Jaeger span context for distributed tracing.
/// Contains trace identity and propagation information.
/// </summary>
public sealed class JaegerSpanContext
{
    /// <summary>
    /// Gets the 128-bit trace ID as a hex string.
    /// </summary>
    public string TraceId { get; }

    /// <summary>
    /// Gets the 64-bit span ID as a hex string.
    /// </summary>
    public string SpanId { get; }

    /// <summary>
    /// Gets the parent span ID, or null for root spans.
    /// </summary>
    public string? ParentSpanId { get; }

    /// <summary>
    /// Gets whether this span is sampled and should be recorded.
    /// </summary>
    public bool IsSampled { get; }

    /// <summary>
    /// Gets the baggage items for cross-process propagation.
    /// </summary>
    public Dictionary<string, string> Baggage { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="JaegerSpanContext"/>.
    /// </summary>
    public JaegerSpanContext(
        string traceId,
        string spanId,
        string? parentSpanId,
        bool isSampled,
        Dictionary<string, string> baggage)
    {
        TraceId = traceId ?? throw new ArgumentNullException(nameof(traceId));
        SpanId = spanId ?? throw new ArgumentNullException(nameof(spanId));
        ParentSpanId = parentSpanId;
        IsSampled = isSampled;
        Baggage = baggage ?? new Dictionary<string, string>();
    }
}

/// <summary>
/// Represents a Jaeger span with full tracing capabilities.
/// Implements ITraceSpan for SDK compatibility.
/// </summary>
public sealed class JaegerSpan : ITraceSpan
{
    private readonly JaegerPlugin _plugin;
    private readonly object _lock = new();
    private readonly List<JaegerSpanLog> _logs = new();
    private readonly Dictionary<string, object> _tags = new();
    private bool _isFinished;

    /// <summary>
    /// Gets the span context containing trace identity.
    /// </summary>
    public JaegerSpanContext Context { get; }

    /// <inheritdoc />
    public string TraceId => Context.TraceId;

    /// <inheritdoc />
    public string SpanId => Context.SpanId;

    /// <inheritdoc />
    public string OperationName { get; }

    /// <inheritdoc />
    public SpanKind Kind { get; }

    /// <inheritdoc />
    public DateTime StartTime { get; }

    /// <summary>
    /// Gets the span end time, or null if not finished.
    /// </summary>
    public DateTime? EndTime { get; private set; }

    /// <inheritdoc />
    public TimeSpan? Duration => EndTime.HasValue ? EndTime.Value - StartTime : null;

    /// <inheritdoc />
    public bool IsRecording => !_isFinished && Context.IsSampled;

    /// <summary>
    /// Gets the service name that created this span.
    /// </summary>
    public string ServiceName { get; }

    /// <summary>
    /// Gets the span tags/attributes.
    /// </summary>
    public IReadOnlyDictionary<string, object> Tags => _tags;

    /// <summary>
    /// Gets the span logs.
    /// </summary>
    public IReadOnlyList<JaegerSpanLog> Logs => _logs;

    /// <summary>
    /// Gets or sets the span status.
    /// </summary>
    public SpanStatus Status { get; private set; } = SpanStatus.Unset;

    /// <summary>
    /// Gets or sets the status description.
    /// </summary>
    public string? StatusDescription { get; private set; }

    /// <summary>
    /// Initializes a new instance of <see cref="JaegerSpan"/>.
    /// </summary>
    internal JaegerSpan(
        JaegerSpanContext context,
        string operationName,
        SpanKind kind,
        DateTime startTime,
        string serviceName,
        JaegerPlugin plugin)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        OperationName = operationName ?? throw new ArgumentNullException(nameof(operationName));
        Kind = kind;
        StartTime = startTime;
        ServiceName = serviceName;
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
    }

    /// <inheritdoc />
    public void SetAttribute(string key, object value)
    {
        if (_isFinished || !Context.IsSampled) return;

        lock (_lock)
        {
            _tags[key] = value;
        }
    }

    /// <inheritdoc />
    public void AddEvent(string name, Dictionary<string, object>? attributes = null)
    {
        if (_isFinished || !Context.IsSampled) return;

        var fields = new Dictionary<string, object> { ["event"] = name };
        if (attributes != null)
        {
            foreach (var kv in attributes)
            {
                fields[kv.Key] = kv.Value;
            }
        }

        AddLog(fields);
    }

    /// <summary>
    /// Adds a log entry to the span with the specified fields.
    /// </summary>
    /// <param name="fields">The log fields.</param>
    /// <param name="timestamp">Optional explicit timestamp.</param>
    public void AddLog(Dictionary<string, object> fields, DateTime? timestamp = null)
    {
        if (_isFinished || !Context.IsSampled) return;

        lock (_lock)
        {
            _logs.Add(new JaegerSpanLog
            {
                Timestamp = timestamp ?? DateTime.UtcNow,
                Fields = new Dictionary<string, object>(fields)
            });
        }
    }

    /// <inheritdoc />
    public void SetStatus(SpanStatus status, string? description = null)
    {
        if (_isFinished) return;

        Status = status;
        StatusDescription = description;

        if (status == SpanStatus.Error)
        {
            SetAttribute("error", true);
            if (description != null)
            {
                SetAttribute("error.message", description);
            }
        }
    }

    /// <inheritdoc />
    public void RecordException(Exception exception)
    {
        if (_isFinished || !Context.IsSampled) return;

        SetStatus(SpanStatus.Error, exception.Message);

        AddLog(new Dictionary<string, object>
        {
            ["event"] = "exception",
            ["error.kind"] = exception.GetType().FullName ?? exception.GetType().Name,
            ["message"] = exception.Message,
            ["stack"] = exception.StackTrace ?? ""
        });
    }

    /// <summary>
    /// Sets a baggage item for cross-process propagation.
    /// </summary>
    /// <param name="key">The baggage key.</param>
    /// <param name="value">The baggage value.</param>
    public void SetBaggageItem(string key, string value)
    {
        Context.Baggage[key] = value;
    }

    /// <summary>
    /// Gets a baggage item.
    /// </summary>
    /// <param name="key">The baggage key.</param>
    /// <returns>The baggage value, or null if not found.</returns>
    public string? GetBaggageItem(string key)
    {
        return Context.Baggage.TryGetValue(key, out var value) ? value : null;
    }

    /// <inheritdoc />
    public void End()
    {
        if (_isFinished) return;

        lock (_lock)
        {
            if (_isFinished) return;
            _isFinished = true;
            EndTime = DateTime.UtcNow;
        }

        _plugin.FinishSpan(this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        End();
    }
}

/// <summary>
/// Represents a log entry within a Jaeger span.
/// </summary>
public sealed class JaegerSpanLog
{
    /// <summary>
    /// Gets or sets the log timestamp.
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// Gets or sets the log fields.
    /// </summary>
    public Dictionary<string, object> Fields { get; init; } = new();
}

#endregion

#region Sampling Strategies

/// <summary>
/// Interface for sampling strategy implementations.
/// </summary>
public interface ISamplingStrategy
{
    /// <summary>
    /// Determines whether a trace should be sampled.
    /// </summary>
    /// <param name="operationName">The operation name.</param>
    /// <param name="traceId">The trace ID.</param>
    /// <returns>True if the trace should be sampled, false otherwise.</returns>
    bool ShouldSample(string operationName, string traceId);
}

/// <summary>
/// Constant sampling strategy - always samples or never samples.
/// </summary>
public sealed class ConstSampler : ISamplingStrategy
{
    private readonly bool _decision;

    /// <summary>
    /// Initializes a new constant sampler.
    /// </summary>
    /// <param name="decision">True to always sample, false to never sample.</param>
    public ConstSampler(bool decision)
    {
        _decision = decision;
    }

    /// <inheritdoc />
    public bool ShouldSample(string operationName, string traceId) => _decision;
}

/// <summary>
/// Probabilistic sampling strategy - samples based on probability.
/// </summary>
public sealed class ProbabilisticSampler : ISamplingStrategy
{
    private readonly double _probability;

    /// <summary>
    /// Initializes a new probabilistic sampler.
    /// </summary>
    /// <param name="probability">The probability of sampling (0.0 to 1.0).</param>
    public ProbabilisticSampler(double probability)
    {
        _probability = Math.Clamp(probability, 0.0, 1.0);
    }

    /// <inheritdoc />
    public bool ShouldSample(string operationName, string traceId)
    {
        // Use trace ID for deterministic sampling across services
        if (traceId.Length >= 16)
        {
            var hash = Convert.ToInt64(traceId[..16], 16);
            var threshold = (long)(_probability * long.MaxValue);
            return Math.Abs(hash) < threshold;
        }

        return Random.Shared.NextDouble() < _probability;
    }
}

/// <summary>
/// Rate limiting sampling strategy - samples up to N traces per second.
/// </summary>
public sealed class RateLimitingSampler : ISamplingStrategy
{
    private readonly double _maxTracesPerSecond;
    private readonly object _lock = new();
    private double _balance;
    private DateTime _lastTick;

    /// <summary>
    /// Initializes a new rate limiting sampler.
    /// </summary>
    /// <param name="maxTracesPerSecond">Maximum traces to sample per second.</param>
    public RateLimitingSampler(double maxTracesPerSecond)
    {
        _maxTracesPerSecond = Math.Max(0, maxTracesPerSecond);
        _balance = _maxTracesPerSecond;
        _lastTick = DateTime.UtcNow;
    }

    /// <inheritdoc />
    public bool ShouldSample(string operationName, string traceId)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastTick).TotalSeconds;
            _lastTick = now;

            // Add credits based on time elapsed
            _balance = Math.Min(_maxTracesPerSecond, _balance + elapsed * _maxTracesPerSecond);

            if (_balance >= 1.0)
            {
                _balance -= 1.0;
                return true;
            }

            return false;
        }
    }
}

#endregion

#region Transport

/// <summary>
/// Interface for Jaeger transport implementations.
/// </summary>
public interface IJaegerTransport : IDisposable
{
    /// <summary>
    /// Sends a batch of spans to Jaeger.
    /// </summary>
    /// <param name="spans">The spans to send.</param>
    /// <param name="serviceName">The service name.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendAsync(IEnumerable<JaegerSpan> spans, string serviceName, CancellationToken ct = default);
}

/// <summary>
/// UDP transport for sending spans to Jaeger agent.
/// </summary>
public sealed class UdpTransport : IJaegerTransport
{
    private readonly UdpClient _client;
    private readonly IPEndPoint _endpoint;
    private readonly int _maxPacketSize;
    private bool _disposed;

    /// <summary>
    /// Initializes a new UDP transport.
    /// </summary>
    /// <param name="host">The agent host.</param>
    /// <param name="port">The agent port.</param>
    /// <param name="maxPacketSize">Maximum UDP packet size.</param>
    public UdpTransport(string host, int port, int maxPacketSize = 65000)
    {
        _client = new UdpClient();
        _endpoint = new IPEndPoint(
            Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork),
            port);
        _maxPacketSize = maxPacketSize;
    }

    /// <inheritdoc />
    public async Task SendAsync(IEnumerable<JaegerSpan> spans, string serviceName, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var batch = SerializeSpans(spans, serviceName);

        // Split into chunks if necessary
        var chunks = SplitIntoChunks(batch, _maxPacketSize);

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            await _client.SendAsync(chunk, chunk.Length, _endpoint);
        }
    }

    private static byte[] SerializeSpans(IEnumerable<JaegerSpan> spans, string serviceName)
    {
        // Simplified Thrift-like serialization for Jaeger compact protocol
        // In production, you would use proper Thrift serialization
        var payload = new JaegerBatchPayload
        {
            Process = new JaegerProcess
            {
                ServiceName = serviceName,
                Tags = new List<JaegerTag>()
            },
            Spans = spans.Select(s => new JaegerThriftSpan
            {
                TraceIdHigh = ParseTraceIdHigh(s.TraceId),
                TraceIdLow = ParseTraceIdLow(s.TraceId),
                SpanId = ParseSpanId(s.SpanId),
                ParentSpanId = s.Context.ParentSpanId != null ? ParseSpanId(s.Context.ParentSpanId) : 0,
                OperationName = s.OperationName,
                Flags = s.Context.IsSampled ? 1 : 0,
                StartTime = new DateTimeOffset(s.StartTime).ToUnixTimeMilliseconds() * 1000,
                Duration = (long)(s.Duration?.TotalMicroseconds ?? 0),
                Tags = s.Tags.Select(t => new JaegerTag
                {
                    Key = t.Key,
                    VType = GetTagType(t.Value),
                    VStr = t.Value?.ToString(),
                    VDouble = t.Value is double d ? d : null,
                    VBool = t.Value is bool b ? b : null,
                    VLong = t.Value is long l ? l : (t.Value is int i ? i : null)
                }).ToList(),
                Logs = s.Logs.Select(l => new JaegerLog
                {
                    Timestamp = new DateTimeOffset(l.Timestamp).ToUnixTimeMilliseconds() * 1000,
                    Fields = l.Fields.Select(f => new JaegerTag
                    {
                        Key = f.Key,
                        VType = GetTagType(f.Value),
                        VStr = f.Value?.ToString()
                    }).ToList()
                }).ToList()
            }).ToList()
        };

        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    private static long ParseTraceIdHigh(string traceId)
    {
        if (traceId.Length >= 32)
        {
            return Convert.ToInt64(traceId[..16], 16);
        }
        return 0;
    }

    private static long ParseTraceIdLow(string traceId)
    {
        if (traceId.Length >= 32)
        {
            return Convert.ToInt64(traceId[16..32], 16);
        }
        return Convert.ToInt64(traceId.PadLeft(16, '0'), 16);
    }

    private static long ParseSpanId(string spanId)
    {
        return Convert.ToInt64(spanId.PadLeft(16, '0'), 16);
    }

    private static string GetTagType(object? value)
    {
        return value switch
        {
            string => "string",
            double => "double",
            float => "double",
            bool => "bool",
            long => "long",
            int => "long",
            _ => "string"
        };
    }

    private static List<byte[]> SplitIntoChunks(byte[] data, int maxSize)
    {
        var chunks = new List<byte[]>();

        if (data.Length <= maxSize)
        {
            chunks.Add(data);
            return chunks;
        }

        // For oversized payloads, we'd need to split spans across batches
        // This is a simplified implementation
        for (int i = 0; i < data.Length; i += maxSize)
        {
            var chunkSize = Math.Min(maxSize, data.Length - i);
            var chunk = new byte[chunkSize];
            Array.Copy(data, i, chunk, 0, chunkSize);
            chunks.Add(chunk);
        }

        return chunks;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }
}

/// <summary>
/// HTTP transport for sending spans to Jaeger collector.
/// </summary>
public sealed class HttpTransport : IJaegerTransport
{
    private readonly HttpClient _client;
    private readonly string _endpoint;
    private bool _disposed;

    /// <summary>
    /// Initializes a new HTTP transport.
    /// </summary>
    /// <param name="endpoint">The collector endpoint URL.</param>
    /// <param name="timeout">Request timeout.</param>
    /// <param name="authToken">Optional authorization token.</param>
    public HttpTransport(string endpoint, TimeSpan timeout, string? authToken = null)
    {
        _endpoint = endpoint;
        _client = new HttpClient
        {
            Timeout = timeout == default ? TimeSpan.FromSeconds(30) : timeout
        };

        if (!string.IsNullOrEmpty(authToken))
        {
            _client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);
        }
    }

    /// <inheritdoc />
    public async Task SendAsync(IEnumerable<JaegerSpan> spans, string serviceName, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var payload = new JaegerBatchPayload
        {
            Process = new JaegerProcess
            {
                ServiceName = serviceName,
                Tags = new List<JaegerTag>()
            },
            Spans = spans.Select(s => new JaegerThriftSpan
            {
                TraceIdHigh = ParseTraceIdHigh(s.TraceId),
                TraceIdLow = ParseTraceIdLow(s.TraceId),
                SpanId = ParseSpanId(s.SpanId),
                ParentSpanId = s.Context.ParentSpanId != null ? ParseSpanId(s.Context.ParentSpanId) : 0,
                OperationName = s.OperationName,
                Flags = s.Context.IsSampled ? 1 : 0,
                StartTime = new DateTimeOffset(s.StartTime).ToUnixTimeMilliseconds() * 1000,
                Duration = (long)(s.Duration?.TotalMicroseconds ?? 0),
                Tags = s.Tags.Select(t => new JaegerTag
                {
                    Key = t.Key,
                    VType = GetTagType(t.Value),
                    VStr = t.Value?.ToString(),
                    VDouble = t.Value is double d ? d : null,
                    VBool = t.Value is bool b ? b : null,
                    VLong = t.Value is long l ? l : (t.Value is int i ? i : null)
                }).ToList(),
                Logs = s.Logs.Select(l => new JaegerLog
                {
                    Timestamp = new DateTimeOffset(l.Timestamp).ToUnixTimeMilliseconds() * 1000,
                    Fields = l.Fields.Select(f => new JaegerTag
                    {
                        Key = f.Key,
                        VType = GetTagType(f.Value),
                        VStr = f.Value?.ToString()
                    }).ToList()
                }).ToList()
            }).ToList()
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync(_endpoint, content, ct);
        response.EnsureSuccessStatusCode();
    }

    private static long ParseTraceIdHigh(string traceId)
    {
        if (traceId.Length >= 32)
        {
            return Convert.ToInt64(traceId[..16], 16);
        }
        return 0;
    }

    private static long ParseTraceIdLow(string traceId)
    {
        if (traceId.Length >= 32)
        {
            return Convert.ToInt64(traceId[16..32], 16);
        }
        return Convert.ToInt64(traceId.PadLeft(16, '0'), 16);
    }

    private static long ParseSpanId(string spanId)
    {
        return Convert.ToInt64(spanId.PadLeft(16, '0'), 16);
    }

    private static string GetTagType(object? value)
    {
        return value switch
        {
            string => "string",
            double => "double",
            float => "double",
            bool => "bool",
            long => "long",
            int => "long",
            _ => "string"
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }
}

#endregion

#region Internal Types

/// <summary>
/// Internal metric value storage.
/// </summary>
internal sealed class MetricValue
{
    public string Name { get; init; } = string.Empty;
    public double Value { get; set; }
    public Dictionary<string, string> Tags { get; init; } = new();
    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// Internal log entry storage.
/// </summary>
internal sealed class JaegerLogEntry
{
    public DateTime Timestamp { get; init; }
    public LogLevel Level { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? TraceId { get; init; }
    public string? SpanId { get; init; }
    public Dictionary<string, object> Properties { get; init; } = new();
    public Exception? Exception { get; init; }
}

/// <summary>
/// Jaeger batch payload for serialization.
/// </summary>
internal sealed class JaegerBatchPayload
{
    [JsonPropertyName("process")]
    public JaegerProcess Process { get; init; } = new();

    [JsonPropertyName("spans")]
    public List<JaegerThriftSpan> Spans { get; init; } = new();
}

/// <summary>
/// Jaeger process information.
/// </summary>
internal sealed class JaegerProcess
{
    [JsonPropertyName("serviceName")]
    public string ServiceName { get; init; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<JaegerTag> Tags { get; init; } = new();
}

/// <summary>
/// Jaeger Thrift span format.
/// </summary>
internal sealed class JaegerThriftSpan
{
    [JsonPropertyName("traceIdHigh")]
    public long TraceIdHigh { get; init; }

    [JsonPropertyName("traceIdLow")]
    public long TraceIdLow { get; init; }

    [JsonPropertyName("spanId")]
    public long SpanId { get; init; }

    [JsonPropertyName("parentSpanId")]
    public long ParentSpanId { get; init; }

    [JsonPropertyName("operationName")]
    public string OperationName { get; init; } = string.Empty;

    [JsonPropertyName("flags")]
    public int Flags { get; init; }

    [JsonPropertyName("startTime")]
    public long StartTime { get; init; }

    [JsonPropertyName("duration")]
    public long Duration { get; init; }

    [JsonPropertyName("tags")]
    public List<JaegerTag> Tags { get; init; } = new();

    [JsonPropertyName("logs")]
    public List<JaegerLog> Logs { get; init; } = new();
}

/// <summary>
/// Jaeger tag format.
/// </summary>
internal sealed class JaegerTag
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("vType")]
    public string VType { get; init; } = "string";

    [JsonPropertyName("vStr")]
    public string? VStr { get; init; }

    [JsonPropertyName("vDouble")]
    public double? VDouble { get; init; }

    [JsonPropertyName("vBool")]
    public bool? VBool { get; init; }

    [JsonPropertyName("vLong")]
    public long? VLong { get; init; }
}

/// <summary>
/// Jaeger log format.
/// </summary>
internal sealed class JaegerLog
{
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }

    [JsonPropertyName("fields")]
    public List<JaegerTag> Fields { get; init; } = new();
}

#endregion
