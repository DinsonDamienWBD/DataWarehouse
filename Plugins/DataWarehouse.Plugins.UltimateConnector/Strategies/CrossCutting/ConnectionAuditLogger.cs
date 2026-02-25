using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CrossCutting
{
    /// <summary>
    /// Centralized audit logger for all connection lifecycle events. Logs connect, disconnect,
    /// test, and health check events with timestamps, strategy ID, duration, and outcome.
    /// Publishes events to the message bus under the "connector.audit.*" topic hierarchy
    /// for downstream consumption by compliance and observability systems.
    /// </summary>
    /// <remarks>
    /// Audit entries are buffered in a thread-safe ring buffer and flushed to the message bus
    /// periodically or when the buffer reaches capacity. This prevents audit logging from
    /// becoming a bottleneck on the connection hot path.
    /// </remarks>
    public sealed class ConnectionAuditLogger : IAsyncDisposable
    {
        private readonly IMessageBus? _messageBus;
        private readonly ILogger? _logger;
        private readonly ConcurrentQueue<AuditEntry> _buffer = new();
        private readonly Timer _flushTimer;
        private readonly int _maxBufferSize;
        private volatile bool _disposed;

        /// <summary>
        /// Base topic prefix for all connector audit events.
        /// </summary>
        public const string AuditTopicPrefix = "connector.audit";

        /// <summary>
        /// Initializes a new instance of <see cref="ConnectionAuditLogger"/>.
        /// </summary>
        /// <param name="messageBus">Message bus for publishing audit events. May be null.</param>
        /// <param name="logger">Optional logger for local diagnostics.</param>
        /// <param name="flushInterval">Interval between automatic buffer flushes.</param>
        /// <param name="maxBufferSize">Maximum buffer size before forced flush.</param>
        public ConnectionAuditLogger(
            IMessageBus? messageBus,
            ILogger? logger = null,
            TimeSpan? flushInterval = null,
            int maxBufferSize = 500)
        {
            _messageBus = messageBus;
            _logger = logger;
            _maxBufferSize = maxBufferSize;

            var interval = flushInterval ?? TimeSpan.FromSeconds(10);
            _flushTimer = new Timer(_ => _ = FlushAsync(), null, interval, interval);
        }

        /// <summary>
        /// Logs a connection establishment event.
        /// </summary>
        /// <param name="strategyId">Strategy that established the connection.</param>
        /// <param name="connectionId">ID of the established connection.</param>
        /// <param name="duration">Time taken to establish the connection.</param>
        /// <param name="success">Whether the connection succeeded.</param>
        /// <param name="errorMessage">Error message if the connection failed.</param>
        public void LogConnect(
            string strategyId, string connectionId, TimeSpan duration, bool success, string? errorMessage = null)
        {
            var entry = new AuditEntry(
                EventType: AuditEventType.Connect,
                StrategyId: strategyId,
                ConnectionId: connectionId,
                Timestamp: DateTimeOffset.UtcNow,
                Duration: duration,
                Success: success,
                ErrorMessage: errorMessage,
                Details: null);

            EnqueueAndMaybeFlush(entry);
        }

        /// <summary>
        /// Logs a connection disconnection event.
        /// </summary>
        /// <param name="strategyId">Strategy that owned the connection.</param>
        /// <param name="connectionId">ID of the disconnected connection.</param>
        /// <param name="duration">Time the connection was active.</param>
        /// <param name="reason">Reason for disconnection.</param>
        public void LogDisconnect(
            string strategyId, string connectionId, TimeSpan duration, string? reason = null)
        {
            var entry = new AuditEntry(
                EventType: AuditEventType.Disconnect,
                StrategyId: strategyId,
                ConnectionId: connectionId,
                Timestamp: DateTimeOffset.UtcNow,
                Duration: duration,
                Success: true,
                ErrorMessage: null,
                Details: reason != null ? new Dictionary<string, object> { ["reason"] = reason } : null);

            EnqueueAndMaybeFlush(entry);
        }

        /// <summary>
        /// Logs a connection test event.
        /// </summary>
        /// <param name="strategyId">Strategy that performed the test.</param>
        /// <param name="connectionId">ID of the tested connection.</param>
        /// <param name="duration">Duration of the test operation.</param>
        /// <param name="isAlive">Whether the connection test passed.</param>
        public void LogTest(
            string strategyId, string connectionId, TimeSpan duration, bool isAlive)
        {
            var entry = new AuditEntry(
                EventType: AuditEventType.Test,
                StrategyId: strategyId,
                ConnectionId: connectionId,
                Timestamp: DateTimeOffset.UtcNow,
                Duration: duration,
                Success: isAlive,
                ErrorMessage: isAlive ? null : "Connection test failed",
                Details: null);

            EnqueueAndMaybeFlush(entry);
        }

        /// <summary>
        /// Logs a health check event.
        /// </summary>
        /// <param name="strategyId">Strategy that performed the health check.</param>
        /// <param name="connectionId">ID of the checked connection.</param>
        /// <param name="duration">Duration of the health check.</param>
        /// <param name="isHealthy">Whether the health check passed.</param>
        /// <param name="statusMessage">Health status message.</param>
        public void LogHealthCheck(
            string strategyId, string connectionId, TimeSpan duration, bool isHealthy, string statusMessage)
        {
            var entry = new AuditEntry(
                EventType: AuditEventType.HealthCheck,
                StrategyId: strategyId,
                ConnectionId: connectionId,
                Timestamp: DateTimeOffset.UtcNow,
                Duration: duration,
                Success: isHealthy,
                ErrorMessage: isHealthy ? null : statusMessage,
                Details: new Dictionary<string, object> { ["status_message"] = statusMessage });

            EnqueueAndMaybeFlush(entry);
        }

        /// <summary>
        /// Returns all buffered audit entries without clearing the buffer.
        /// </summary>
        /// <returns>Snapshot of current audit entries.</returns>
        public IReadOnlyList<AuditEntry> GetBufferedEntries() => _buffer.ToArray();

        /// <summary>
        /// Flushes all buffered audit entries to the message bus.
        /// </summary>
        public async Task FlushAsync()
        {
            if (_messageBus == null || _disposed) return;

            var entries = new List<AuditEntry>();
            while (_buffer.TryDequeue(out var entry))
                entries.Add(entry);

            if (entries.Count == 0) return;

            foreach (var entry in entries)
            {
                var topic = $"{AuditTopicPrefix}.{entry.EventType.ToString().ToLowerInvariant()}";

                try
                {
                    var payload = new Dictionary<string, object>
                    {
                        ["event_type"] = entry.EventType.ToString(),
                        ["strategy_id"] = entry.StrategyId,
                        ["connection_id"] = entry.ConnectionId,
                        ["timestamp"] = entry.Timestamp.ToString("O"),
                        ["duration_ms"] = entry.Duration.TotalMilliseconds,
                        ["success"] = entry.Success
                    };

                    if (entry.ErrorMessage != null)
                        payload["error_message"] = entry.ErrorMessage;

                    if (entry.Details != null)
                    {
                        foreach (var (key, value) in entry.Details)
                            payload[$"detail_{key}"] = value;
                    }

                    var message = PluginMessage.Create(topic, payload);
                    await _messageBus.PublishAsync(topic, message);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "Failed to publish audit event {EventType} for strategy {StrategyId}",
                        entry.EventType, entry.StrategyId);
                }
            }

            _logger?.LogDebug("Flushed {Count} audit entries to message bus", entries.Count);
        }

        private void EnqueueAndMaybeFlush(AuditEntry entry)
        {
            _buffer.Enqueue(entry);

            _logger?.LogDebug(
                "Audit: {EventType} strategy={StrategyId} connection={ConnectionId} success={Success} duration={Duration}ms",
                entry.EventType, entry.StrategyId, entry.ConnectionId, entry.Success, entry.Duration.TotalMilliseconds);

            if (_buffer.Count >= _maxBufferSize)
                _ = FlushAsync();
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            await _flushTimer.DisposeAsync();
            await FlushAsync();
        }
    }

    /// <summary>
    /// Type of connection audit event.
    /// </summary>
    public enum AuditEventType
    {
        /// <summary>Connection establishment.</summary>
        Connect,
        /// <summary>Connection termination.</summary>
        Disconnect,
        /// <summary>Connection liveness test.</summary>
        Test,
        /// <summary>Connection health check.</summary>
        HealthCheck
    }

    /// <summary>
    /// A single audit log entry capturing a connection lifecycle event.
    /// </summary>
    /// <param name="EventType">Type of audit event.</param>
    /// <param name="StrategyId">Strategy identifier that triggered the event.</param>
    /// <param name="ConnectionId">Connection identifier involved in the event.</param>
    /// <param name="Timestamp">When the event occurred.</param>
    /// <param name="Duration">Duration of the operation.</param>
    /// <param name="Success">Whether the operation succeeded.</param>
    /// <param name="ErrorMessage">Error message if the operation failed.</param>
    /// <param name="Details">Optional additional details about the event.</param>
    public sealed record AuditEntry(
        AuditEventType EventType,
        string StrategyId,
        string ConnectionId,
        DateTimeOffset Timestamp,
        TimeSpan Duration,
        bool Success,
        string? ErrorMessage,
        Dictionary<string, object>? Details);
}
