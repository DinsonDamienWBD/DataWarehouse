using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Messaging
{
    public class RedPandaConnectionStrategy : MessagingConnectionStrategyBase
    {
        // Monotonically increasing correlation ID counter — safe for concurrent use.
        private static int _correlationIdCounter;

        public override string StrategyId => "redpanda";
        public override string DisplayName => "RedPanda";
        public override ConnectorCategory Category => ConnectorCategory.Messaging;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to RedPanda using Kafka-compatible TCP protocol on port 9092 for high-performance streaming.";
        public override string[] Tags => new[] { "redpanda", "kafka-compatible", "streaming", "tcp", "performance" };
        public RedPandaConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct) { var parts = config.ConnectionString.Split(':'); var host = parts[0]; var port = parts.Length > 1 ? int.Parse(parts[1]) : 9092; var tcpClient = new TcpClient(); await tcpClient.ConnectAsync(host, port, ct); return new DefaultConnectionHandle(tcpClient, new Dictionary<string, object> { ["Host"] = host, ["Port"] = port }); }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { return handle.GetConnection<TcpClient>()?.Connected ?? false; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>()?.Close(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "RedPanda is connected" : "RedPanda is disconnected", sw.Elapsed, DateTimeOffset.UtcNow); }

        public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            if (client == null || !client.Connected)
                throw new InvalidOperationException("RedPanda connection is not established");
            var stream = client.GetStream();
            // Kafka-compatible wire protocol - Produce Request (API Key 0)
            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);
            var clientId = "datawarehouse-redpanda";
            var correlationId = Interlocked.Increment(ref _correlationIdCounter);
            // Kafka wire protocol uses big-endian (network byte order) for all multi-byte integers.
            writer.Write(System.Net.IPAddress.HostToNetworkOrder((short)0)); // API Key: Produce
            writer.Write(System.Net.IPAddress.HostToNetworkOrder((short)0)); // API Version
            writer.Write(System.Net.IPAddress.HostToNetworkOrder(correlationId));
            WriteKafkaString(writer, clientId);
            writer.Write(System.Net.IPAddress.HostToNetworkOrder((short)-1));         // Required Acks
            writer.Write(System.Net.IPAddress.HostToNetworkOrder(1000));              // Timeout ms
            writer.Write(System.Net.IPAddress.HostToNetworkOrder(1));                 // Topic array count
            WriteKafkaString(writer, topic);
            writer.Write(System.Net.IPAddress.HostToNetworkOrder(1));                 // Partition array count
            writer.Write(System.Net.IPAddress.HostToNetworkOrder(0));                 // Partition index
            var messageSet = BuildKafkaMessageSet(message);
            writer.Write(System.Net.IPAddress.HostToNetworkOrder(messageSet.Length));
            writer.Write(messageSet);
            var payload = ms.ToArray();
            var lengthPrefix = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(payload.Length));
            await stream.WriteAsync(lengthPrefix, 0, 4, ct);
            await stream.WriteAsync(payload, 0, payload.Length, ct);
            await stream.FlushAsync(ct);
        }

        public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            if (client == null || !client.Connected)
                throw new InvalidOperationException("RedPanda connection is not established");
            var stream = client.GetStream();
            while (!ct.IsCancellationRequested && client.Connected)
            {
                using var ms = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(ms);
                var clientId = "datawarehouse-redpanda";
                var correlationId = Interlocked.Increment(ref _correlationIdCounter);
                // Kafka wire protocol requires big-endian byte order throughout.
                writer.Write(System.Net.IPAddress.HostToNetworkOrder((short)1)); // API Key: Fetch
                writer.Write(System.Net.IPAddress.HostToNetworkOrder((short)0)); // API Version
                writer.Write(System.Net.IPAddress.HostToNetworkOrder(correlationId));
                WriteKafkaString(writer, clientId);
                writer.Write(System.Net.IPAddress.HostToNetworkOrder(-1));           // Replica ID
                writer.Write(System.Net.IPAddress.HostToNetworkOrder(100));          // Max wait ms
                writer.Write(System.Net.IPAddress.HostToNetworkOrder(1));            // Min bytes
                writer.Write(System.Net.IPAddress.HostToNetworkOrder(1));            // Topic count
                WriteKafkaString(writer, topic);
                writer.Write(System.Net.IPAddress.HostToNetworkOrder(1));            // Partition count
                writer.Write(System.Net.IPAddress.HostToNetworkOrder(0));            // Partition
                writer.Write(System.Net.IPAddress.HostToNetworkOrder(0L));           // Fetch offset
                writer.Write(System.Net.IPAddress.HostToNetworkOrder(1024 * 1024)); // Max bytes
                var payload = ms.ToArray();
                var lengthPrefix = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(payload.Length));
                await stream.WriteAsync(lengthPrefix, 0, 4, ct);
                await stream.WriteAsync(payload, 0, payload.Length, ct);
                await stream.FlushAsync(ct);
                var respLengthBuf = new byte[4];
                var read = await stream.ReadAsync(respLengthBuf, 0, 4, ct);
                if (read < 4) break;
                var respLength = System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt32(respLengthBuf, 0));
                if (respLength > 0 && respLength < 10 * 1024 * 1024)
                {
                    var respBuf = new byte[respLength];
                    var totalRead = 0;
                    while (totalRead < respLength) { var chunk = await stream.ReadAsync(respBuf, totalRead, respLength - totalRead, ct); if (chunk == 0) break; totalRead += chunk; }
                    if (totalRead > 8) yield return respBuf;
                }
                await Task.Delay(100, ct);
            }
        }

        private static void WriteKafkaString(System.IO.BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write((short)System.Net.IPAddress.HostToNetworkOrder((short)bytes.Length));
            writer.Write(bytes);
        }

        private static byte[] BuildKafkaMessageSet(byte[] value)
        {
            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);
            writer.Write(0L); // Offset
            using var msgMs = new System.IO.MemoryStream();
            using var msgWriter = new System.IO.BinaryWriter(msgMs);
            msgWriter.Write((byte)0); // Magic
            msgWriter.Write((byte)0); // Attributes
            msgWriter.Write(-1); // Key (null)
            msgWriter.Write(System.Net.IPAddress.HostToNetworkOrder(value.Length));
            msgWriter.Write(value);
            var msgBytes = msgMs.ToArray();
            var crc = ComputeCrc32(msgBytes);
            writer.Write(System.Net.IPAddress.HostToNetworkOrder(4 + msgBytes.Length));
            writer.Write(System.Net.IPAddress.HostToNetworkOrder((int)crc));
            writer.Write(msgBytes);
            return ms.ToArray();
        }

        private static uint ComputeCrc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (var b in data) crc = (crc >> 8) ^ Crc32Table[(crc ^ b) & 0xFF];
            return crc ^ 0xFFFFFFFF;
        }

        private static readonly uint[] Crc32Table = GenerateCrc32Table();
        private static uint[] GenerateCrc32Table()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++) { var crc = i; for (var j = 0; j < 8; j++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1; table[i] = crc; }
            return table;
        }
    }
}
