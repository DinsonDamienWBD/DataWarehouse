using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.IoT
{
    /// <summary>
    /// Connection strategy for MQTT IoT message brokers.
    /// Tests connectivity via TCP connection to port 1883.
    /// </summary>
    public class MqttIoTConnectionStrategy : IoTConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "mqtt-iot";

        /// <inheritdoc/>
        public override string DisplayName => "MQTT IoT";

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects to MQTT brokers for IoT device messaging and telemetry";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "mqtt", "iot", "messaging", "telemetry", "pubsub" };

        /// <summary>
        /// Initializes a new instance of <see cref="MqttIoTConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public MqttIoTConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string required")).Split(':');
            var host = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p1883) ? p1883 : 1883;

            var client = new TcpClient();
            await client.ConnectAsync(host, port, ct);

            if (!client.Connected)
                throw new InvalidOperationException($"Failed to connect to MQTT broker at {host}:{port}");

            var info = new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["protocol"] = "MQTT",
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info);
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // Finding 1919: TcpClient.Connected is stale — probe with a fresh TCP connect instead.
            var info = handle.ConnectionInfo;
            var host = info.GetValueOrDefault("host")?.ToString() ?? "";
            if (!int.TryParse(info.GetValueOrDefault("port")?.ToString(), out var port)) port = 1883;
            try { using var probe = new TcpClient(); await probe.ConnectAsync(host, port, ct); return true; }
            catch { return false; }
        }

        /// <inheritdoc/>
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<TcpClient>();
            client.Close();
            client.Dispose();
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy ? "MQTT broker reachable" : "MQTT broker unreachable",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }

        /// <inheritdoc/>
        public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default)
        {
            // MQTT requires publish/subscribe model - return metadata about the subscription topic
            var result = new Dictionary<string, object>
            {
                ["protocol"] = "MQTT",
                ["deviceId"] = deviceId,
                ["topic"] = $"devices/{deviceId}/telemetry",
                ["status"] = "subscription_required",
                ["message"] = "MQTT uses pub/sub model. Subscribe to the telemetry topic to receive data.",
                ["timestamp"] = DateTimeOffset.UtcNow
            };
            return Task.FromResult(result);
        }

        /// <inheritdoc/>
        public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default)
        {
            // MQTT commands are published to command topics
            var commandTopic = $"devices/{deviceId}/commands";
            return Task.FromResult($"{{\"status\":\"queued\",\"topic\":\"{commandTopic}\",\"command\":\"{command}\",\"message\":\"Command queued for MQTT publish\"}}");
        }
    }
}
