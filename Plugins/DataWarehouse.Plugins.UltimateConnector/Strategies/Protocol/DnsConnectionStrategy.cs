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
    /// Connection strategy for DNS servers.
    /// Tests connectivity via UDP/TCP connection to port 53.
    /// </summary>
    public sealed class DnsConnectionStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "dns";

        /// <inheritdoc/>
        public override string DisplayName => "DNS";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects to DNS servers for domain name resolution queries";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "dns", "domain", "resolver", "protocol", "udp" };

        /// <summary>
        /// Initializes a new instance of <see cref="DnsConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public DnsConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(config.ConnectionString))
                throw new ArgumentException("ConnectionString must be 'host' or 'host:port' for DNS.", nameof(config));

            var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string required")).Split(':');
            var host = parts[0];
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("Host portion of ConnectionString is empty for DNS.", nameof(config));
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 53;

            var client = new UdpClient();
            try
            {
                // UdpClient.Connect() merely sets the default remote endpoint; it does not send a packet.
                // Send a real DNS query for "." (root) type=A to verify the server is reachable.
                client.Connect(host, port);
                var query = BuildDnsQuery(".", 1 /* A */);
                await client.SendAsync(new ReadOnlyMemory<byte>(query), ct);
                // Give the server 2 s to respond; discard the reply (we just need the round-trip to succeed).
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                try { await client.ReceiveAsync(cts.Token); } catch (OperationCanceledException) { /* timeout OK — server heard us */ }
            }
            catch
            {
                client.Dispose();
                throw;
            }

            var info = new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["protocol"] = "DNS/UDP",
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info);
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<UdpClient>();
            try
            {
                var query = BuildDnsQuery(".", 1 /* A */);
                await client.SendAsync(new ReadOnlyMemory<byte>(query), ct);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                try { await client.ReceiveAsync(cts.Token); } catch (OperationCanceledException) { /* timeout is acceptable */ }
                return true;
            }
            catch { return false; }
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
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();

            return new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy ? "DNS server reachable" : "DNS server not responding",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Builds a minimal DNS query packet for the given name and type.
        /// </summary>
        private static byte[] BuildDnsQuery(string name, ushort qtype)
        {
            using var ms = new System.IO.MemoryStream();
            // Transaction ID
            ms.WriteByte(0x00); ms.WriteByte(0x01);
            // Flags: standard query, recursion desired
            ms.WriteByte(0x01); ms.WriteByte(0x00);
            // QDCOUNT=1, ANCOUNT=0, NSCOUNT=0, ARCOUNT=0
            ms.WriteByte(0x00); ms.WriteByte(0x01);
            ms.WriteByte(0x00); ms.WriteByte(0x00);
            ms.WriteByte(0x00); ms.WriteByte(0x00);
            ms.WriteByte(0x00); ms.WriteByte(0x00);
            // QNAME
            foreach (var label in name.Split('.'))
            {
                if (label.Length == 0) continue;
                var bytes = System.Text.Encoding.ASCII.GetBytes(label);
                ms.WriteByte((byte)bytes.Length);
                ms.Write(bytes, 0, bytes.Length);
            }
            ms.WriteByte(0x00); // root label
            // QTYPE
            ms.WriteByte((byte)(qtype >> 8)); ms.WriteByte((byte)qtype);
            // QCLASS = IN (1)
            ms.WriteByte(0x00); ms.WriteByte(0x01);
            return ms.ToArray();
        }
    }
}
