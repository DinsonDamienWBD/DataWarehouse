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
    public class ApachePulsarConnectionStrategy : MessagingConnectionStrategyBase
    {
        public override string StrategyId => "pulsar";
        public override string DisplayName => "Apache Pulsar";
        public override ConnectorCategory Category => ConnectorCategory.Messaging;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Apache Pulsar using TCP binary protocol on port 6650 for distributed messaging and streaming.";
        public override string[] Tags => new[] { "pulsar", "messaging", "streaming", "tcp", "distributed" };
        private long _requestId = 1;
        private long _producerId = 1;
        private long _consumerId = 1;
        private long _sequenceId = 1;
        public ApachePulsarConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string required")).Split(':');
            var host = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p6650) ? p6650 : 6650;
            var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(host, port, ct);
            var stream = tcpClient.GetStream();
            // Send CONNECT command
            var connectCmd = BuildPulsarCommand(2, BuildConnectPayload()); // CommandType.CONNECT = 2
            await stream.WriteAsync(connectCmd, 0, connectCmd.Length, ct);
            await stream.FlushAsync(ct);
            return new DefaultConnectionHandle(tcpClient, new Dictionary<string, object> { ["Host"] = host, ["Port"] = port });
        }
        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { return Task.FromResult(handle.GetConnection<TcpClient>()?.Connected ?? false); } catch { return Task.FromResult(false); } }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>()?.Close(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Pulsar is connected" : "Pulsar is disconnected", sw.Elapsed, DateTimeOffset.UtcNow); }

        public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            if (client == null || !client.Connected)
                throw new InvalidOperationException("Pulsar connection is not established");
            var stream = client.GetStream();
            // Send PRODUCER command first
            var requestId = Interlocked.Increment(ref _requestId);
            var producerId = Interlocked.Increment(ref _producerId);
            var producerCmd = BuildPulsarCommand(12, BuildProducerPayload(requestId, producerId, topic)); // PRODUCER = 12
            await stream.WriteAsync(producerCmd, 0, producerCmd.Length, ct);
            // Send SEND command with message
            var sequenceId = Interlocked.Increment(ref _sequenceId);
            var sendCmd = BuildPulsarSendCommand(producerId, sequenceId, message);
            await stream.WriteAsync(sendCmd, 0, sendCmd.Length, ct);
            await stream.FlushAsync(ct);
        }

        public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            if (client == null || !client.Connected)
                throw new InvalidOperationException("Pulsar connection is not established");
            var stream = client.GetStream();
            // Send SUBSCRIBE command
            var requestId = Interlocked.Increment(ref _requestId);
            var consumerId = Interlocked.Increment(ref _consumerId);
            var subscribeCmd = BuildPulsarCommand(10, BuildSubscribePayload(requestId, consumerId, topic, consumerGroup)); // SUBSCRIBE = 10
            await stream.WriteAsync(subscribeCmd, 0, subscribeCmd.Length, ct);
            // Send FLOW command to allow messages
            var flowCmd = BuildPulsarCommand(15, BuildFlowPayload(consumerId, 1000)); // FLOW = 15
            await stream.WriteAsync(flowCmd, 0, flowCmd.Length, ct);
            await stream.FlushAsync(ct);
            // Read messages
            var frameHeader = new byte[4];
            while (!ct.IsCancellationRequested && client.Connected)
            {
                byte[]? messageBody = null;
                bool shouldBreak = false;
                try
                {
                    var read = await stream.ReadAsync(frameHeader, 0, 4, ct);
                    if (read < 4) { shouldBreak = true; }
                    else
                    {
                        var totalSize = (frameHeader[0] << 24) | (frameHeader[1] << 16) | (frameHeader[2] << 8) | frameHeader[3];
                        if (totalSize > 0 && totalSize < 10 * 1024 * 1024)
                        {
                            var frame = new byte[totalSize];
                            var totalRead = 0;
                            while (totalRead < totalSize) { var chunk = await stream.ReadAsync(frame, totalRead, totalSize - totalRead, ct); if (chunk == 0) break; totalRead += chunk; }
                            // Extract message payload from MESSAGE command
                            if (frame.Length > 8 && frame[4] == 4) // MESSAGE command type
                                messageBody = ExtractPulsarMessagePayload(frame);
                        }
                    }
                }
                catch (Exception) when (ct.IsCancellationRequested) { shouldBreak = true; }
                if (shouldBreak) break;
                if (messageBody != null) yield return messageBody;
            }
        }

        private static byte[] BuildPulsarCommand(int commandType, byte[] payload)
        {
            var cmdSize = 4 + payload.Length; // commandType(4) + payload
            var totalSize = 4 + cmdSize; // cmdSize(4) + command
            var result = new byte[4 + totalSize];
            // Total size (big-endian)
            result[0] = (byte)(totalSize >> 24); result[1] = (byte)(totalSize >> 16);
            result[2] = (byte)(totalSize >> 8); result[3] = (byte)totalSize;
            // Command size
            result[4] = (byte)(cmdSize >> 24); result[5] = (byte)(cmdSize >> 16);
            result[6] = (byte)(cmdSize >> 8); result[7] = (byte)cmdSize;
            // Command type (varint encoded as 4 bytes for simplicity)
            result[8] = (byte)commandType;
            Array.Copy(payload, 0, result, 9, payload.Length);
            return result;
        }

        private static byte[] BuildPulsarSendCommand(long producerId, long sequenceId, byte[] message)
        {
            using var ms = new System.IO.MemoryStream();
            // Build simplified SEND command
            WriteVarint(ms, 7); // SEND = 7
            WriteVarint(ms, producerId);
            WriteVarint(ms, sequenceId);
            var cmdBytes = ms.ToArray();
            var cmdSize = cmdBytes.Length;
            var totalSize = 4 + cmdSize + message.Length;
            var result = new byte[4 + totalSize];
            result[0] = (byte)(totalSize >> 24); result[1] = (byte)(totalSize >> 16);
            result[2] = (byte)(totalSize >> 8); result[3] = (byte)totalSize;
            result[4] = (byte)(cmdSize >> 24); result[5] = (byte)(cmdSize >> 16);
            result[6] = (byte)(cmdSize >> 8); result[7] = (byte)cmdSize;
            Array.Copy(cmdBytes, 0, result, 8, cmdBytes.Length);
            Array.Copy(message, 0, result, 8 + cmdBytes.Length, message.Length);
            return result;
        }

        private static byte[] BuildConnectPayload() { using var ms = new System.IO.MemoryStream(); WriteVarint(ms, 15); WriteString(ms, "datawarehouse-connector"); return ms.ToArray(); }
        private static byte[] BuildProducerPayload(long requestId, long producerId, string topic) { using var ms = new System.IO.MemoryStream(); WriteVarint(ms, requestId); WriteVarint(ms, producerId); WriteString(ms, topic); return ms.ToArray(); }
        private static byte[] BuildSubscribePayload(long requestId, long consumerId, string topic, string? subscription) { using var ms = new System.IO.MemoryStream(); WriteVarint(ms, requestId); WriteVarint(ms, consumerId); WriteString(ms, topic); WriteString(ms, subscription ?? "default-sub"); WriteVarint(ms, 0); return ms.ToArray(); }
        private static byte[] BuildFlowPayload(long consumerId, int permits) { using var ms = new System.IO.MemoryStream(); WriteVarint(ms, consumerId); WriteVarint(ms, permits); return ms.ToArray(); }

        private static void WriteVarint(System.IO.Stream s, long value) { while (value > 127) { s.WriteByte((byte)((value & 0x7F) | 0x80)); value >>= 7; } s.WriteByte((byte)value); }
        private static void WriteString(System.IO.Stream s, string value) { var bytes = Encoding.UTF8.GetBytes(value); WriteVarint(s, bytes.Length); s.Write(bytes, 0, bytes.Length); }
        private static byte[] ExtractPulsarMessagePayload(byte[] frame) { var payloadStart = Math.Min(20, frame.Length - 1); return frame[payloadStart..]; }
    }
}
