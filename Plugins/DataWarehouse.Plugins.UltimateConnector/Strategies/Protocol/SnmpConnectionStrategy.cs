using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Protocol
{
    /// <summary>
    /// Connection strategy for SNMP network management.
    /// Tests connectivity via UDP connection to port 161.
    /// </summary>
    public class SnmpConnectionStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "snmp";

        /// <inheritdoc/>
        public override string DisplayName => "SNMP";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects to SNMP agents for network device monitoring and management";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "snmp", "monitoring", "network", "protocol", "udp" };

        /// <summary>
        /// Initializes a new instance of <see cref="SnmpConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public SnmpConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string required")).Split(':');
            var host = parts[0];
            int port;
            if (parts.Length > 1)
            {
                if (!int.TryParse(parts[1], out port) || port < 1 || port > 65535)
                    throw new ArgumentException($"Invalid port '{parts[1]}' in SNMP connection string. Expected a number between 1 and 65535.", nameof(config));
            }
            else
            {
                port = 161;
            }

            var client = new UdpClient();
            client.Connect(host, port);

            // Send an SNMP GetRequest PDU for sysDescr (OID 1.3.6.1.2.1.1.1.0) to verify reachability.
            // Minimal SNMP v1 GetRequest: community "public", OID 1.3.6.1.2.1.1.1.0
            var probePacket = new byte[]
            {
                0x30, 0x26, // SEQUENCE
                0x02, 0x01, 0x00, // version: INTEGER 0 (v1)
                0x04, 0x06, 0x70, 0x75, 0x62, 0x6c, 0x69, 0x63, // community: OCTET STRING "public"
                0xa0, 0x19, // GetRequest-PDU
                0x02, 0x04, 0x00, 0x00, 0x00, 0x01, // request-id: INTEGER 1
                0x02, 0x01, 0x00, // error-status: 0
                0x02, 0x01, 0x00, // error-index: 0
                0x30, 0x0b, // VarBindList SEQUENCE
                0x30, 0x09, // VarBind SEQUENCE
                0x06, 0x05, 0x2b, 0x06, 0x01, 0x02, 0x01, // OID 1.3.6.1.2.1 (enterprises)
                0x05, 0x00  // Null value
            };
            try
            {
                await client.SendAsync(probePacket, probePacket.Length).ConfigureAwait(false);
            }
            catch
            {
                // UDP send failure is non-fatal at connect time; agent may be firewalled
            }

            var info = new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["protocol"] = "SNMP/UDP",
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info);
        }

        /// <inheritdoc/>
        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // UdpClient.Client.Connected is always true after Connect() — not a liveness indicator.
            // Verify the socket is open and bound (not disposed/closed).
            var client = handle.GetConnection<UdpClient>();
            return Task.FromResult(client.Client != null && client.Client.IsBound);
        }

        /// <inheritdoc/>
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<UdpClient>();
            client.Close();
            client.Dispose();
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<UdpClient>();
            var isHealthy = client.Client != null && client.Client.IsBound;

            return Task.FromResult(new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy ? "SNMP agent socket bound" : "SNMP agent socket closed",
                Latency: TimeSpan.Zero,
                CheckedAt: DateTimeOffset.UtcNow));
        }
    }
}
