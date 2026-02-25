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
    public class MqttConnectionStrategy : MessagingConnectionStrategyBase
    {
        public override string StrategyId => "mqtt";
        public override string DisplayName => "MQTT";
        public override ConnectorCategory Category => ConnectorCategory.Messaging;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to MQTT broker using TCP protocol on port 1883 (or 8883 for TLS) for IoT messaging.";
        public override string[] Tags => new[] { "mqtt", "iot", "messaging", "tcp", "pubsub" };
        private ushort _packetId = 1;
        public MqttConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = config.ConnectionString.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 ? int.Parse(parts[1]) : 1883;
            var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(host, port, ct);
            var stream = tcpClient.GetStream();
            // Send MQTT CONNECT packet
            var clientId = "datawarehouse-" + Guid.NewGuid().ToString("N")[..8];
            var connectPacket = BuildMqttConnectPacket(clientId);
            await stream.WriteAsync(connectPacket, 0, connectPacket.Length, ct);
            await stream.FlushAsync(ct);
            // Read CONNACK
            var ackBuffer = new byte[4];
            await stream.ReadExactlyAsync(ackBuffer, 0, 4, ct);
            return new DefaultConnectionHandle(tcpClient, new Dictionary<string, object> { ["Host"] = host, ["Port"] = port });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { return handle.GetConnection<TcpClient>()?.Connected ?? false; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>()?.Close(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "MQTT broker is connected" : "MQTT broker is disconnected", sw.Elapsed, DateTimeOffset.UtcNow); }

        public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            if (client == null || !client.Connected)
                throw new InvalidOperationException("MQTT connection is not established");
            var stream = client.GetStream();
            // MQTT PUBLISH packet: Fixed header + Variable header + Payload
            var publishPacket = BuildMqttPublishPacket(topic, message, 0); // QoS 0
            await stream.WriteAsync(publishPacket, 0, publishPacket.Length, ct);
            await stream.FlushAsync(ct);
        }

        public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            if (client == null || !client.Connected)
                throw new InvalidOperationException("MQTT connection is not established");
            var stream = client.GetStream();
            // Send SUBSCRIBE packet
            var subscribePacket = BuildMqttSubscribePacket(topic, _packetId++);
            await stream.WriteAsync(subscribePacket, 0, subscribePacket.Length, ct);
            await stream.FlushAsync(ct);
            // Read SUBACK
            var subackBuffer = new byte[5];
            await stream.ReadExactlyAsync(subackBuffer, 0, 5, ct);
            // Read PUBLISH messages
            while (!ct.IsCancellationRequested && client.Connected)
            {
                byte[]? messageData = null;
                bool shouldBreak = false;
                try
                {
                    var header = new byte[2];
                    var read = await stream.ReadAsync(header, 0, 2, ct);
                    if (read < 2) { shouldBreak = true; }
                    else
                    {
                        var packetType = (header[0] >> 4) & 0x0F;
                        var remainingLength = await ReadMqttRemainingLength(stream, ct);
                        if (remainingLength > 0 && remainingLength < 10 * 1024 * 1024)
                        {
                            var packet = new byte[remainingLength];
                            var totalRead = 0;
                            while (totalRead < remainingLength) { var chunk = await stream.ReadAsync(packet, totalRead, remainingLength - totalRead, ct); if (chunk == 0) break; totalRead += chunk; }
                            if (packetType == 3) // PUBLISH
                            {
                                var topicLen = (packet[0] << 8) | packet[1];
                                var payloadStart = 2 + topicLen;
                                if (payloadStart < packet.Length) messageData = packet[payloadStart..];
                            }
                        }
                    }
                }
                catch (Exception) when (ct.IsCancellationRequested) { shouldBreak = true; }
                if (shouldBreak) break;
                if (messageData != null) yield return messageData;
            }
        }

        private static byte[] BuildMqttConnectPacket(string clientId)
        {
            using var ms = new System.IO.MemoryStream();
            // Fixed header
            ms.WriteByte(0x10); // CONNECT
            // Variable header
            var protocolName = Encoding.UTF8.GetBytes("MQTT");
            var clientIdBytes = Encoding.UTF8.GetBytes(clientId);
            var variableHeaderLength = 2 + protocolName.Length + 1 + 1 + 2 + 2 + clientIdBytes.Length;
            WriteMqttRemainingLength(ms, variableHeaderLength);
            ms.WriteByte((byte)(protocolName.Length >> 8)); ms.WriteByte((byte)protocolName.Length);
            ms.Write(protocolName, 0, protocolName.Length);
            ms.WriteByte(0x04); // Protocol level (MQTT 3.1.1)
            ms.WriteByte(0x02); // Connect flags (Clean session)
            ms.WriteByte(0x00); ms.WriteByte(0x3C); // Keep alive (60 seconds)
            ms.WriteByte((byte)(clientIdBytes.Length >> 8)); ms.WriteByte((byte)clientIdBytes.Length);
            ms.Write(clientIdBytes, 0, clientIdBytes.Length);
            return ms.ToArray();
        }

        private static byte[] BuildMqttPublishPacket(string topic, byte[] payload, int qos)
        {
            using var ms = new System.IO.MemoryStream();
            ms.WriteByte((byte)(0x30 | (qos << 1))); // PUBLISH with QoS
            var topicBytes = Encoding.UTF8.GetBytes(topic);
            var variableHeaderLength = 2 + topicBytes.Length + payload.Length;
            WriteMqttRemainingLength(ms, variableHeaderLength);
            ms.WriteByte((byte)(topicBytes.Length >> 8)); ms.WriteByte((byte)topicBytes.Length);
            ms.Write(topicBytes, 0, topicBytes.Length);
            ms.Write(payload, 0, payload.Length);
            return ms.ToArray();
        }

        private byte[] BuildMqttSubscribePacket(string topic, ushort packetId)
        {
            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(0x82); // SUBSCRIBE with QoS 1
            var topicBytes = Encoding.UTF8.GetBytes(topic);
            var variableHeaderLength = 2 + 2 + topicBytes.Length + 1;
            WriteMqttRemainingLength(ms, variableHeaderLength);
            ms.WriteByte((byte)(packetId >> 8)); ms.WriteByte((byte)packetId);
            ms.WriteByte((byte)(topicBytes.Length >> 8)); ms.WriteByte((byte)topicBytes.Length);
            ms.Write(topicBytes, 0, topicBytes.Length);
            ms.WriteByte(0x00); // QoS 0
            return ms.ToArray();
        }

        private static void WriteMqttRemainingLength(System.IO.Stream stream, int length)
        {
            do { var encodedByte = (byte)(length % 128); length /= 128; if (length > 0) encodedByte |= 128; stream.WriteByte(encodedByte); } while (length > 0);
        }

        private static async Task<int> ReadMqttRemainingLength(NetworkStream stream, CancellationToken ct)
        {
            var multiplier = 1;
            var value = 0;
            byte encodedByte;
            do { var buffer = new byte[1]; await stream.ReadExactlyAsync(buffer, 0, 1, ct); encodedByte = buffer[0]; value += (encodedByte & 127) * multiplier; multiplier *= 128; } while ((encodedByte & 128) != 0);
            return value;
        }
    }
}
