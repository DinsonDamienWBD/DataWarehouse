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
    public class ZeroMqConnectionStrategy : MessagingConnectionStrategyBase
    {
        public override string StrategyId => "zeromq";
        public override string DisplayName => "ZeroMQ";
        public override ConnectorCategory Category => ConnectorCategory.Messaging;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to ZeroMQ using TCP socket for high-performance asynchronous messaging.";
        public override string[] Tags => new[] { "zeromq", "zmq", "messaging", "tcp", "high-performance" };
        public ZeroMqConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct) { var parts = config.ConnectionString.Split(':'); var host = parts[0]; var port = parts.Length > 1 ? int.Parse(parts[1]) : 5555; var tcpClient = new TcpClient(); await tcpClient.ConnectAsync(host, port, ct); return new DefaultConnectionHandle(tcpClient, new Dictionary<string, object> { ["Host"] = host, ["Port"] = port }); }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { return handle.GetConnection<TcpClient>()?.Connected ?? false; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>()?.Close(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "ZeroMQ is connected" : "ZeroMQ is disconnected", sw.Elapsed, DateTimeOffset.UtcNow); }

        public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            if (client == null || !client.Connected)
                throw new InvalidOperationException("ZeroMQ connection is not established");
            var stream = client.GetStream();
            // ZeroMQ DEALER/ROUTER pattern with multipart message: [topic, message]
            // Frame format: [more flag (1 byte)] [size (8 bytes)] [data]
            var topicBytes = Encoding.UTF8.GetBytes(topic);
            await WriteZmqFrame(stream, topicBytes, true, ct);
            await WriteZmqFrame(stream, message, false, ct);
            await stream.FlushAsync(ct);
        }

        public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            if (client == null || !client.Connected)
                throw new InvalidOperationException("ZeroMQ connection is not established");
            var stream = client.GetStream();
            // Send SUB filter for topic
            var filterBytes = Encoding.UTF8.GetBytes($"\x01{topic}");
            await stream.WriteAsync(filterBytes, 0, filterBytes.Length, ct);
            await stream.FlushAsync(ct);
            // Read frames
            while (!ct.IsCancellationRequested && client.Connected)
            {
                byte[]? messageData = null;
                bool shouldBreak = false;
                try
                {
                    var (frame, hasMore) = await ReadZmqFrame(stream, ct);
                    if (frame == null) { shouldBreak = true; }
                    else
                    {
                        // Skip topic frame, get message frame
                        if (hasMore)
                        {
                            var (msgFrame, _) = await ReadZmqFrame(stream, ct);
                            if (msgFrame != null) messageData = msgFrame;
                        }
                    }
                }
                catch (Exception) when (ct.IsCancellationRequested) { shouldBreak = true; }
                if (shouldBreak) break;
                if (messageData != null) yield return messageData;
            }
        }

        private static async Task WriteZmqFrame(NetworkStream stream, byte[] data, bool hasMore, CancellationToken ct)
        {
            var moreFlag = (byte)(hasMore ? 0x01 : 0x00);
            var size = (ulong)data.Length;
            await stream.WriteAsync(new[] { moreFlag }, 0, 1, ct);
            var sizeBytes = BitConverter.GetBytes(size);
            if (BitConverter.IsLittleEndian) Array.Reverse(sizeBytes);
            await stream.WriteAsync(sizeBytes, 0, 8, ct);
            await stream.WriteAsync(data, 0, data.Length, ct);
        }

        private static async Task<(byte[]? Frame, bool HasMore)> ReadZmqFrame(NetworkStream stream, CancellationToken ct)
        {
            var header = new byte[9];
            var read = await stream.ReadAsync(header, 0, 9, ct);
            if (read < 9) return (null, false);
            var hasMore = header[0] == 0x01;
            var sizeBytes = header[1..9];
            if (BitConverter.IsLittleEndian) Array.Reverse(sizeBytes);
            var size = BitConverter.ToUInt64(sizeBytes, 0);
            if (size > 10 * 1024 * 1024) return (null, false);
            var frame = new byte[size];
            var totalRead = 0;
            while (totalRead < (int)size)
            {
                var chunk = await stream.ReadAsync(frame, totalRead, (int)size - totalRead, ct);
                if (chunk == 0) break;
                totalRead += chunk;
            }
            return (frame, hasMore);
        }
    }
}
