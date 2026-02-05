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
    /// Connection strategy for OPC UA industrial automation servers.
    /// Tests connectivity via TCP connection to port 4840.
    /// </summary>
    public class OpcUaConnectionStrategy : IoTConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "opcua";

        /// <inheritdoc/>
        public override string DisplayName => "OPC UA";

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects to OPC UA servers for industrial automation data exchange";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "opcua", "industrial", "automation", "scada", "iot" };

        /// <summary>
        /// Initializes a new instance of <see cref="OpcUaConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public OpcUaConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = config.ConnectionString.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 ? int.Parse(parts[1]) : 4840;

            var client = new TcpClient();
            await client.ConnectAsync(host, port, ct);

            if (!client.Connected)
                throw new InvalidOperationException($"Failed to connect to OPC UA server at {host}:{port}");

            var info = new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["protocol"] = "OPC UA",
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info);
        }

        /// <inheritdoc/>
        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<TcpClient>();
            return Task.FromResult(client.Connected);
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
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<TcpClient>();
            var isHealthy = client.Connected;

            return Task.FromResult(new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy ? "OPC UA server connected" : "OPC UA server disconnected",
                Latency: TimeSpan.Zero,
                CheckedAt: DateTimeOffset.UtcNow));
        }

        /// <inheritdoc/>
        public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default)
        {
            throw new NotSupportedException("ReadTelemetryAsync requires OPC UA client library implementation");
        }

        /// <inheritdoc/>
        public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default)
        {
            throw new NotSupportedException("SendCommandAsync requires OPC UA client library implementation");
        }
    }
}
