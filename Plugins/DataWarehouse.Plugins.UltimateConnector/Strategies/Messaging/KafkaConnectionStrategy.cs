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
    public class KafkaConnectionStrategy : MessagingConnectionStrategyBase
    {
        public override string StrategyId => "kafka";
        public override string DisplayName => "Apache Kafka";
        public override ConnectorCategory Category => ConnectorCategory.Messaging;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Apache Kafka using TCP wire protocol on port 9092 for distributed streaming platform.";
        public override string[] Tags => new[] { "kafka", "messaging", "streaming", "tcp", "distributed" };
        public KafkaConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var brokers = config.ConnectionString.Split(',');
            var tcpClient = new TcpClient();
            var parts = brokers[0].Split(':');
            var host = parts[0];
            var port = parts.Length > 1 ? int.Parse(parts[1]) : 9092;
            await tcpClient.ConnectAsync(host, port, ct);
            return new DefaultConnectionHandle(tcpClient, new Dictionary<string, object> { ["Brokers"] = config.ConnectionString, ["Host"] = host, ["Port"] = port });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var client = handle.GetConnection<TcpClient>(); return client?.Connected ?? false; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>()?.Close(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Kafka broker is connected" : "Kafka broker is disconnected", sw.Elapsed, DateTimeOffset.UtcNow); }

        public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            if (client == null || !client.Connected)
                throw new InvalidOperationException("Kafka connection is not established");
            var stream = client.GetStream();
            // Kafka Produce Request (simplified wire protocol v0)
            // API Key: 0 (Produce), API Version: 0, Correlation ID, Client ID, Required Acks, Timeout, Topic, Partition, Message
            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);
            var clientId = "datawarehouse-connector";
            var correlationId = Environment.TickCount;
            // Write request header
            writer.Write((short)0); // API Key: Produce
            writer.Write((short)0); // API Version
            writer.Write(correlationId);
            WriteKafkaString(writer, clientId);
            // Produce request body
            writer.Write((short)-1); // Required Acks: no ack
            writer.Write(1000); // Timeout ms
            writer.Write(1); // Topic array count
            WriteKafkaString(writer, topic);
            writer.Write(1); // Partition array count
            writer.Write(0); // Partition index
            // Message set
            var messageSet = BuildKafkaMessageSet(message);
            writer.Write(messageSet.Length);
            writer.Write(messageSet);
            // Send with length prefix
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
                throw new InvalidOperationException("Kafka connection is not established");
            var stream = client.GetStream();
            // Send Fetch request and yield messages
            while (!ct.IsCancellationRequested && client.Connected)
            {
                using var ms = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(ms);
                var clientId = "datawarehouse-connector";
                var correlationId = Environment.TickCount;
                writer.Write((short)1); // API Key: Fetch
                writer.Write((short)0); // API Version
                writer.Write(correlationId);
                WriteKafkaString(writer, clientId);
                writer.Write(-1); // Replica ID
                writer.Write(100); // Max wait ms
                writer.Write(1); // Min bytes
                writer.Write(1); // Topic count
                WriteKafkaString(writer, topic);
                writer.Write(1); // Partition count
                writer.Write(0); // Partition
                writer.Write(0L); // Fetch offset
                writer.Write(1024 * 1024); // Max bytes
                var payload = ms.ToArray();
                var lengthPrefix = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(payload.Length));
                await stream.WriteAsync(lengthPrefix, 0, 4, ct);
                await stream.WriteAsync(payload, 0, payload.Length, ct);
                await stream.FlushAsync(ct);
                // Read response
                var respLengthBuf = new byte[4];
                var read = await stream.ReadAsync(respLengthBuf, 0, 4, ct);
                if (read < 4) break;
                var respLength = System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt32(respLengthBuf, 0));
                if (respLength > 0 && respLength < 10 * 1024 * 1024)
                {
                    var respBuf = new byte[respLength];
                    var totalRead = 0;
                    while (totalRead < respLength)
                    {
                        var chunk = await stream.ReadAsync(respBuf, totalRead, respLength - totalRead, ct);
                        if (chunk == 0) break;
                        totalRead += chunk;
                    }
                    // Parse and yield messages (simplified - real impl would parse message set)
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
            // Message: CRC, Magic, Attributes, Key, Value
            using var msgMs = new System.IO.MemoryStream();
            using var msgWriter = new System.IO.BinaryWriter(msgMs);
            msgWriter.Write((byte)0); // Magic
            msgWriter.Write((byte)0); // Attributes
            msgWriter.Write(-1); // Key (null)
            msgWriter.Write(System.Net.IPAddress.HostToNetworkOrder(value.Length));
            msgWriter.Write(value);
            var msgBytes = msgMs.ToArray();
            var crc = ComputeCrc32(msgBytes);
            writer.Write(System.Net.IPAddress.HostToNetworkOrder(4 + msgBytes.Length)); // Message size
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
            for (uint i = 0; i < 256; i++)
            {
                var crc = i;
                for (var j = 0; j < 8; j++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
                table[i] = crc;
            }
            return table;
        }
    }
}
