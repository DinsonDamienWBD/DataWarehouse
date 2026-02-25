using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CrossCutting
{
    /// <summary>
    /// Configuration mode for intelligence integration.
    /// </summary>
    public enum IntelligenceIntegrationMode
    {
        /// <summary>Fire-and-forget only (current behavior).</summary>
        AsyncOnly = 0,

        /// <summary>Request transformation from intelligence plugin and wait for response.</summary>
        SyncTransform = 1,

        /// <summary>Both async observation AND sync transformation.</summary>
        Hybrid = 2
    }

    /// <summary>
    /// Response payload from intelligence transformation requests.
    /// </summary>
    public record TransformResponse
    {
        public bool Success { get; init; }
        public Dictionary<string, object> Payload { get; init; } = new();
        public string? ErrorMessage { get; init; }
        public TimeSpan ProcessingTime { get; init; }
    }

    /// <summary>
    /// Bridges the connection interceptor pipeline to the message bus, publishing interceptor
    /// events to well-known topics. This allows external plugins (e.g., T90 UltimateIntelligence)
    /// to subscribe to connection lifecycle events and inject intelligence features such as
    /// query optimization, anomaly detection, and adaptive caching.
    /// </summary>
    /// <remarks>
    /// The bridge registers itself as an interceptor in the pipeline and publishes events to:
    /// <list type="bullet">
    ///   <item><c>connector.interceptor.before-request</c> - Before each connection operation</item>
    ///   <item><c>connector.interceptor.after-response</c> - After each connection operation</item>
    ///   <item><c>connector.interceptor.on-schema</c> - When schema is discovered</item>
    ///   <item><c>connector.interceptor.on-error</c> - When errors occur</item>
    ///   <item><c>connector.interceptor.on-connect</c> - When connections are established</item>
    /// </list>
    /// The bridge supports both fire-and-forget publishing and bidirectional request/response
    /// patterns for synchronous transformation with the intelligence plugin.
    /// </remarks>
    public sealed class MessageBusInterceptorBridge : ConnectionInterceptorBase
    {
        private readonly IMessageBus _messageBus;
        private readonly ILogger? _logger;
        private readonly bool _publishAsync;
        private readonly IntelligenceIntegrationMode _integrationMode;
        private readonly TimeSpan _transformTimeout;

        /// <summary>Topic for before-request events.</summary>
        public const string BeforeRequestTopic = "connector.interceptor.before-request";

        /// <summary>Topic for after-response events.</summary>
        public const string AfterResponseTopic = "connector.interceptor.after-response";

        /// <summary>Topic for schema discovery events.</summary>
        public const string SchemaDiscoveredTopic = "connector.interceptor.on-schema";

        /// <summary>Topic for error events.</summary>
        public const string ErrorTopic = "connector.interceptor.on-error";

        /// <summary>Topic for connection established events.</summary>
        public const string ConnectionEstablishedTopic = "connector.interceptor.on-connect";

        /// <summary>Request topic for transformation requests sent to intelligence.</summary>
        public const string TransformRequestTopic = "intelligence.connector.transform-request";

        /// <summary>Request topic for query optimization requests.</summary>
        public const string OptimizeQueryTopic = "intelligence.connector.optimize-query";

        /// <summary>Response topic for transformation responses from intelligence.</summary>
        public const string TransformResponseTopic = "intelligence.connector.transform-response";

        /// <summary>Response topic for query optimization responses.</summary>
        public const string OptimizeQueryResponseTopic = "intelligence.connector.optimize-query.response";

        /// <inheritdoc/>
        public override string Name => "MessageBusInterceptorBridge";

        /// <inheritdoc/>
        public override int Priority => 900;

        /// <summary>
        /// Initializes a new instance of <see cref="MessageBusInterceptorBridge"/>.
        /// </summary>
        /// <param name="messageBus">Message bus for publishing interceptor events.</param>
        /// <param name="logger">Optional logger for diagnostics.</param>
        /// <param name="publishAsync">
        /// If true (default), uses fire-and-forget publishing. If false, awaits delivery.
        /// </param>
        /// <param name="integrationMode">
        /// Intelligence integration mode (AsyncOnly, SyncTransform, or Hybrid).
        /// </param>
        /// <param name="transformTimeout">
        /// Timeout for synchronous transformation requests. Defaults to 5 seconds.
        /// </param>
        public MessageBusInterceptorBridge(
            IMessageBus messageBus,
            ILogger? logger = null,
            bool publishAsync = true,
            IntelligenceIntegrationMode integrationMode = IntelligenceIntegrationMode.AsyncOnly,
            TimeSpan? transformTimeout = null)
        {
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
            _logger = logger;
            _publishAsync = publishAsync;
            _integrationMode = integrationMode;
            _transformTimeout = transformTimeout ?? TimeSpan.FromSeconds(5);
        }

        /// <inheritdoc/>
        public override async Task<BeforeRequestContext> OnBeforeRequestAsync(
            BeforeRequestContext context, CancellationToken ct = default)
        {
            var payload = new Dictionary<string, object>
            {
                ["strategy_id"] = context.StrategyId,
                ["connection_id"] = context.ConnectionId,
                ["timestamp"] = context.Timestamp.ToString("O"),
                ["operation_type"] = context.OperationType,
                ["request_payload_keys"] = string.Join(",", context.RequestPayload.Keys)
            };

            // Always publish async observation
            await PublishEventAsync(BeforeRequestTopic, "interceptor.before-request", payload, ct);

            // If sync mode enabled, request transformation from intelligence
            if (_integrationMode is IntelligenceIntegrationMode.SyncTransform or IntelligenceIntegrationMode.Hybrid)
            {
                var transformed = await RequestTransformationAsync(context, ct);
                if (transformed != null && transformed.Success)
                {
                    _logger?.LogDebug(
                        "Intelligence transformation successful for strategy '{StrategyId}' in {Duration}ms",
                        context.StrategyId, transformed.ProcessingTime.TotalMilliseconds);

                    return context with { RequestPayload = transformed.Payload };
                }
            }

            return context;
        }

        /// <inheritdoc/>
        public override async Task<AfterResponseContext> OnAfterResponseAsync(
            AfterResponseContext context, CancellationToken ct = default)
        {
            var payload = new Dictionary<string, object>
            {
                ["strategy_id"] = context.StrategyId,
                ["connection_id"] = context.ConnectionId,
                ["timestamp"] = context.Timestamp.ToString("O"),
                ["operation_type"] = context.OperationType,
                ["duration_ms"] = context.Duration.TotalMilliseconds,
                ["success"] = context.Success
            };

            if (context.ErrorMessage != null)
                payload["error_message"] = context.ErrorMessage;

            await PublishEventAsync(AfterResponseTopic, "interceptor.after-response", payload, ct);
            return context;
        }

        /// <inheritdoc/>
        public override async Task OnSchemaDiscoveredAsync(
            SchemaDiscoveryContext context, CancellationToken ct = default)
        {
            var payload = new Dictionary<string, object>
            {
                ["strategy_id"] = context.StrategyId,
                ["connection_id"] = context.ConnectionId,
                ["timestamp"] = context.Timestamp.ToString("O"),
                ["schema_name"] = context.SchemaName,
                ["field_count"] = context.Fields.Count,
                ["field_names"] = string.Join(",", context.Fields.Select(f => f.Name)),
                ["discovery_method"] = context.DiscoveryMethod
            };

            await PublishEventAsync(SchemaDiscoveredTopic, "interceptor.on-schema", payload, ct);
        }

        /// <inheritdoc/>
        public override async Task<ErrorContext> OnErrorAsync(
            ErrorContext context, CancellationToken ct = default)
        {
            var payload = new Dictionary<string, object>
            {
                ["strategy_id"] = context.StrategyId,
                ["connection_id"] = context.ConnectionId,
                ["timestamp"] = context.Timestamp.ToString("O"),
                ["operation_type"] = context.OperationType,
                ["exception_type"] = context.Exception.GetType().FullName ?? "Unknown",
                ["exception_message"] = context.Exception.Message,
                ["is_handled"] = context.IsHandled
            };

            await PublishEventAsync(ErrorTopic, "interceptor.on-error", payload, ct);
            return context;
        }

        /// <inheritdoc/>
        public override async Task OnConnectionEstablishedAsync(
            ConnectionEstablishedContext context, CancellationToken ct = default)
        {
            var payload = new Dictionary<string, object>
            {
                ["strategy_id"] = context.StrategyId,
                ["connection_id"] = context.ConnectionId,
                ["timestamp"] = context.Timestamp.ToString("O"),
                ["latency_ms"] = context.Latency.TotalMilliseconds,
                ["connection_info_keys"] = string.Join(",", context.ConnectionInfo.Keys)
            };

            await PublishEventAsync(ConnectionEstablishedTopic, "interceptor.on-connect", payload, ct);
        }

        /// <summary>
        /// Publishes an event to the message bus, optionally as fire-and-forget.
        /// </summary>
        private async Task PublishEventAsync(
            string topic, string messageType, Dictionary<string, object> payload, CancellationToken ct)
        {
            try
            {
                var message = PluginMessage.Create(messageType, payload);

                if (_publishAsync)
                {
                    _ = _messageBus.PublishAsync(topic, message, ct);
                }
                else
                {
                    await _messageBus.PublishAndWaitAsync(topic, message, ct);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "Failed to publish interceptor event to topic '{Topic}'", topic);
            }
        }

        /// <summary>
        /// Requests transformation from the intelligence plugin with timeout and graceful degradation.
        /// </summary>
        private async Task<TransformResponse?> RequestTransformationAsync(
            BeforeRequestContext context, CancellationToken ct)
        {
            try
            {
                var requestPayload = new Dictionary<string, object>
                {
                    ["strategy_id"] = context.StrategyId,
                    ["operation_type"] = context.OperationType,
                    ["request_payload"] = context.RequestPayload
                };

                var messageResponse = await _messageBus.SendAsync(
                    TransformRequestTopic,
                    PluginMessage.Create("transform-request", requestPayload),
                    _transformTimeout,
                    ct
                );

                if (!messageResponse.Success)
                {
                    _logger?.LogWarning(
                        "Intelligence transformation returned error for strategy '{StrategyId}': {Error}",
                        context.StrategyId, messageResponse.ErrorMessage);
                    return null;
                }

                // Extract TransformResponse from payload
                if (messageResponse.Payload is TransformResponse transformResponse)
                {
                    return transformResponse;
                }

                // If payload is a dictionary, convert it
                if (messageResponse.Payload is Dictionary<string, object> dict)
                {
                    return new TransformResponse
                    {
                        Success = dict.TryGetValue("Success", out var success) && success is bool b && b,
                        Payload = dict.TryGetValue("Payload", out var payload) && payload is Dictionary<string, object> p
                            ? p : new Dictionary<string, object>(),
                        ErrorMessage = dict.TryGetValue("ErrorMessage", out var error) ? error?.ToString() : null,
                        ProcessingTime = dict.TryGetValue("ProcessingTime", out var time) && time is TimeSpan ts
                            ? ts : TimeSpan.Zero
                    };
                }

                _logger?.LogWarning(
                    "Intelligence transformation returned unexpected payload type for strategy '{StrategyId}': {Type}",
                    context.StrategyId, messageResponse.Payload?.GetType().FullName ?? "null");
                return null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger?.LogWarning(
                    "Intelligence transformation timed out after {Timeout}ms for strategy '{StrategyId}', using original payload",
                    _transformTimeout.TotalMilliseconds, context.StrategyId);
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "Intelligence transformation failed for strategy '{StrategyId}', using original payload",
                    context.StrategyId);
                return null;
            }
        }
    }
}
