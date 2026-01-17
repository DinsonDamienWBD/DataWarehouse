using DataWarehouse.SDK.Contracts;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DataWarehouse.Kernel.Telemetry
{
    /// <summary>
    /// Default implementation of distributed tracing.
    /// Provides correlation IDs and span tracking for cross-component operations.
    /// </summary>
    public sealed class DistributedTracing : IDistributedTracing
    {
        private static readonly AsyncLocal<TraceContext?> _currentContext = new();
        private readonly ConcurrentDictionary<string, TraceContext> _activeTraces = new();
        private readonly IKernelContext? _kernelContext;

        // Standard header names for context propagation
        public const string CorrelationIdHeader = "X-Correlation-ID";
        public const string SpanIdHeader = "X-Span-ID";
        public const string ParentSpanIdHeader = "X-Parent-Span-ID";
        public const string BaggageHeaderPrefix = "X-Baggage-";

        /// <summary>
        /// Creates a new DistributedTracing instance.
        /// </summary>
        public DistributedTracing(IKernelContext? kernelContext = null)
        {
            _kernelContext = kernelContext;
        }

        /// <summary>
        /// Gets the current trace context.
        /// </summary>
        public TraceContext? Current => _currentContext.Value;

        /// <summary>
        /// Starts a new trace with a fresh correlation ID.
        /// </summary>
        public ITraceScope StartTrace(string operationName, Dictionary<string, string>? baggage = null)
        {
            var correlationId = GenerateId();
            var spanId = GenerateId();

            var context = new TraceContext
            {
                CorrelationId = correlationId,
                SpanId = spanId,
                ParentSpanId = null,
                OperationName = operationName,
                StartTime = DateTime.UtcNow,
                Baggage = baggage != null ? new Dictionary<string, string>(baggage) : new(),
                Tags = new Dictionary<string, object>()
            };

            _activeTraces[spanId] = context;
            _currentContext.Value = context;

            _kernelContext?.LogDebug($"[Trace] Started trace {correlationId}, span {spanId}: {operationName}");

            return new TraceScope(this, context);
        }

        /// <summary>
        /// Starts a child span within the current trace.
        /// </summary>
        public ITraceScope StartSpan(string operationName, Dictionary<string, string>? baggage = null)
        {
            var parent = _currentContext.Value;
            if (parent == null)
            {
                // No current context, start a new trace
                return StartTrace(operationName, baggage);
            }

            var spanId = GenerateId();

            // Merge baggage from parent
            var mergedBaggage = new Dictionary<string, string>(parent.Baggage);
            if (baggage != null)
            {
                foreach (var kvp in baggage)
                {
                    mergedBaggage[kvp.Key] = kvp.Value;
                }
            }

            var context = new TraceContext
            {
                CorrelationId = parent.CorrelationId,
                SpanId = spanId,
                ParentSpanId = parent.SpanId,
                OperationName = operationName,
                StartTime = DateTime.UtcNow,
                Baggage = mergedBaggage,
                Tags = new Dictionary<string, object>()
            };

            _activeTraces[spanId] = context;
            _currentContext.Value = context;

            _kernelContext?.LogDebug($"[Trace] Started span {spanId} (parent: {parent.SpanId}): {operationName}");

            return new TraceScope(this, context, parent);
        }

        /// <summary>
        /// Continues an existing trace from external correlation ID.
        /// </summary>
        public ITraceScope ContinueTrace(string correlationId, string operationName, Dictionary<string, string>? baggage = null)
        {
            var spanId = GenerateId();

            var context = new TraceContext
            {
                CorrelationId = correlationId,
                SpanId = spanId,
                ParentSpanId = null, // External parent not tracked locally
                OperationName = operationName,
                StartTime = DateTime.UtcNow,
                Baggage = baggage != null ? new Dictionary<string, string>(baggage) : new(),
                Tags = new Dictionary<string, object>
                {
                    ["continued_from_external"] = true
                }
            };

            _activeTraces[spanId] = context;
            _currentContext.Value = context;

            _kernelContext?.LogDebug($"[Trace] Continued trace {correlationId}, span {spanId}: {operationName}");

            return new TraceScope(this, context);
        }

        /// <summary>
        /// Extracts trace context from headers/metadata.
        /// </summary>
        public TraceContext? ExtractContext(IDictionary<string, string> carrier)
        {
            if (!carrier.TryGetValue(CorrelationIdHeader, out var correlationId) || string.IsNullOrEmpty(correlationId))
            {
                return null;
            }

            carrier.TryGetValue(SpanIdHeader, out var spanId);
            carrier.TryGetValue(ParentSpanIdHeader, out var parentSpanId);

            var baggage = new Dictionary<string, string>();
            foreach (var kvp in carrier)
            {
                if (kvp.Key.StartsWith(BaggageHeaderPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var key = kvp.Key.Substring(BaggageHeaderPrefix.Length);
                    baggage[key] = kvp.Value;
                }
            }

            return new TraceContext
            {
                CorrelationId = correlationId,
                SpanId = spanId ?? GenerateId(),
                ParentSpanId = parentSpanId,
                OperationName = "extracted",
                StartTime = DateTime.UtcNow,
                Baggage = baggage,
                Tags = new Dictionary<string, object>()
            };
        }

        /// <summary>
        /// Injects trace context into headers/metadata for propagation.
        /// </summary>
        public void InjectContext(IDictionary<string, string> carrier)
        {
            var context = _currentContext.Value;
            if (context == null)
            {
                return;
            }

            carrier[CorrelationIdHeader] = context.CorrelationId;
            carrier[SpanIdHeader] = context.SpanId;

            if (context.ParentSpanId != null)
            {
                carrier[ParentSpanIdHeader] = context.ParentSpanId;
            }

            foreach (var kvp in context.Baggage)
            {
                carrier[$"{BaggageHeaderPrefix}{kvp.Key}"] = kvp.Value;
            }
        }

        /// <summary>
        /// Gets all active traces (for debugging/monitoring).
        /// </summary>
        public IReadOnlyDictionary<string, TraceContext> GetActiveTraces() => _activeTraces;

        private void EndSpan(TraceScope scope)
        {
            _activeTraces.TryRemove(scope.Context.SpanId, out _);

            // Restore parent context if available
            _currentContext.Value = scope.ParentContext;

            var duration = DateTime.UtcNow - scope.Context.StartTime;
            _kernelContext?.LogDebug($"[Trace] Ended span {scope.Context.SpanId}: {scope.Context.OperationName} ({duration.TotalMilliseconds:F2}ms)");
        }

        private static string GenerateId()
        {
            // Generate a 16-character hex ID (similar to W3C Trace Context span ID)
            return Guid.NewGuid().ToString("N")[..16];
        }

        #region TraceScope Implementation

        private sealed class TraceScope : ITraceScope
        {
            private readonly DistributedTracing _tracer;
            private readonly Stopwatch _stopwatch;
            private bool _disposed;

            public TraceContext Context { get; }
            public TraceContext? ParentContext { get; }

            public TraceScope(DistributedTracing tracer, TraceContext context, TraceContext? parentContext = null)
            {
                _tracer = tracer;
                Context = context;
                ParentContext = parentContext;
                _stopwatch = Stopwatch.StartNew();
            }

            public void SetTag(string key, object value)
            {
                if (_disposed) return;
                Context.Tags[key] = value;
            }

            public void SetBaggage(string key, string value)
            {
                if (_disposed) return;
                Context.Baggage[key] = value;
            }

            public void LogEvent(string eventName, Dictionary<string, object>? fields = null)
            {
                if (_disposed) return;

                var eventKey = $"event.{DateTime.UtcNow:HH:mm:ss.fff}.{eventName}";
                Context.Tags[eventKey] = fields != null
                    ? string.Join(", ", fields.Select(kv => $"{kv.Key}={kv.Value}"))
                    : "{}";
            }

            public void SetError(Exception exception)
            {
                if (_disposed) return;

                Context.Tags["error"] = true;
                Context.Tags["error.type"] = exception.GetType().Name;
                Context.Tags["error.message"] = exception.Message;
                Context.Tags["error.stack"] = exception.StackTrace ?? "";
            }

            public void SetStatus(TraceStatus status, string? message = null)
            {
                if (_disposed) return;

                Context.Tags["status"] = status.ToString();
                if (message != null)
                {
                    Context.Tags["status.message"] = message;
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                _stopwatch.Stop();
                Context.Tags["duration_ms"] = _stopwatch.Elapsed.TotalMilliseconds;

                _tracer.EndSpan(this);
            }
        }

        #endregion
    }

    /// <summary>
    /// Extension methods for distributed tracing.
    /// </summary>
    public static class DistributedTracingExtensions
    {
        /// <summary>
        /// Gets the current correlation ID or generates a new one.
        /// </summary>
        public static string GetCorrelationId(this IDistributedTracing? tracing)
        {
            return tracing?.Current?.CorrelationId ?? Guid.NewGuid().ToString("N")[..16];
        }

        /// <summary>
        /// Executes an action within a traced span.
        /// </summary>
        public static async Task<T> TraceAsync<T>(
            this IDistributedTracing tracing,
            string operationName,
            Func<ITraceScope, Task<T>> action)
        {
            using var scope = tracing.StartSpan(operationName);
            try
            {
                var result = await action(scope);
                scope.SetStatus(TraceStatus.Ok);
                return result;
            }
            catch (Exception ex)
            {
                scope.SetError(ex);
                scope.SetStatus(TraceStatus.Error, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Executes an action within a traced span.
        /// </summary>
        public static async Task TraceAsync(
            this IDistributedTracing tracing,
            string operationName,
            Func<ITraceScope, Task> action)
        {
            using var scope = tracing.StartSpan(operationName);
            try
            {
                await action(scope);
                scope.SetStatus(TraceStatus.Ok);
            }
            catch (Exception ex)
            {
                scope.SetError(ex);
                scope.SetStatus(TraceStatus.Error, ex.Message);
                throw;
            }
        }
    }
}
