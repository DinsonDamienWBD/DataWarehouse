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
    /// The bridge uses fire-and-forget publishing to avoid blocking the connection pipeline
    /// on message bus delivery.
    /// </remarks>
    public sealed class MessageBusInterceptorBridge : ConnectionInterceptorBase
    {
        private readonly IMessageBus _messageBus;
        private readonly ILogger? _logger;
        private readonly bool _publishAsync;

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
        public MessageBusInterceptorBridge(
            IMessageBus messageBus,
            ILogger? logger = null,
            bool publishAsync = true)
        {
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
            _logger = logger;
            _publishAsync = publishAsync;
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

            await PublishEventAsync(BeforeRequestTopic, "interceptor.before-request", payload, ct);
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
    }
}
