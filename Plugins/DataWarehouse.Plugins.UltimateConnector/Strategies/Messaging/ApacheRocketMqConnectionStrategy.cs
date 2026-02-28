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
    public class ApacheRocketMqConnectionStrategy : MessagingConnectionStrategyBase
    {
        public override string StrategyId => "rocketmq";
        public override string DisplayName => "Apache RocketMQ";
        public override ConnectorCategory Category => ConnectorCategory.Messaging;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Apache RocketMQ using TCP protocol on port 9876 for distributed messaging.";
        public override string[] Tags => new[] { "rocketmq", "messaging", "distributed", "tcp", "alibaba" };
        private int _requestId = 0;
        public ApacheRocketMqConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct) { var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string required")).Split(':'); var host = parts[0]; var port = parts.Length > 1 ? int.Parse(parts[1]) : 9876; var tcpClient = new TcpClient(); await tcpClient.ConnectAsync(host, port, ct); return new DefaultConnectionHandle(tcpClient, new Dictionary<string, object> { ["Host"] = host, ["Port"] = port }); }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { return handle.GetConnection<TcpClient>()?.Connected ?? false; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>()?.Close(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "RocketMQ is connected" : "RocketMQ is disconnected", sw.Elapsed, DateTimeOffset.UtcNow); }

        public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            if (client == null || !client.Connected)
                throw new InvalidOperationException("RocketMQ connection is not established");
            var stream = client.GetStream();
            // RocketMQ binary protocol - SEND_MESSAGE request
            var requestId = Interlocked.Increment(ref _requestId);
            var requestPacket = BuildRocketMqSendRequest(requestId, topic, message);
            await stream.WriteAsync(requestPacket, 0, requestPacket.Length, ct);
            await stream.FlushAsync(ct);
        }

        public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            if (client == null || !client.Connected)
                throw new InvalidOperationException("RocketMQ connection is not established");
            var stream = client.GetStream();
            // Send PULL_MESSAGE request
            while (!ct.IsCancellationRequested && client.Connected)
            {
                var requestId = Interlocked.Increment(ref _requestId);
                var pullRequest = BuildRocketMqPullRequest(requestId, topic, consumerGroup ?? "default-group");
                await stream.WriteAsync(pullRequest, 0, pullRequest.Length, ct);
                await stream.FlushAsync(ct);
                byte[]? messageData = null;
                bool shouldBreak = false;
                try
                {
                    var lengthBuf = new byte[4];
                    var read = await stream.ReadAsync(lengthBuf, 0, 4, ct);
                    if (read < 4) { shouldBreak = true; }
                    else
                    {
                        var totalLength = System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBuf, 0));
                        if (totalLength > 0 && totalLength < 10 * 1024 * 1024)
                        {
                            var response = new byte[totalLength];
                            var totalRead = 0;
                            while (totalRead < totalLength) { var chunk = await stream.ReadAsync(response, totalRead, totalLength - totalRead, ct); if (chunk == 0) break; totalRead += chunk; }
                            messageData = ExtractRocketMqMessage(response);
                        }
                    }
                }
                catch (Exception) when (ct.IsCancellationRequested) { shouldBreak = true; }
                if (shouldBreak) break;
                if (messageData != null) yield return messageData;
                await Task.Delay(100, ct);
            }
        }

        private static byte[] BuildRocketMqSendRequest(int requestId, string topic, byte[] body)
        {
            using var ms = new System.IO.MemoryStream();
            // RocketMQ frame: [total length (4)] [header length (4)] [header json] [body]
            var headerJson = $"{{\"code\":10,\"language\":\"JAVA\",\"version\":0,\"opaque\":{requestId},\"flag\":0,\"remark\":\"\",\"extFields\":{{\"topic\":\"{topic}\",\"defaultTopic\":\"TBW102\",\"queueId\":\"0\"}}}}";
            var headerBytes = Encoding.UTF8.GetBytes(headerJson);
            var totalLength = 4 + headerBytes.Length + body.Length;
            var lengthBytes = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(totalLength));
            ms.Write(lengthBytes, 0, 4);
            var headerLengthBytes = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(headerBytes.Length));
            ms.Write(headerLengthBytes, 0, 4);
            ms.Write(headerBytes, 0, headerBytes.Length);
            ms.Write(body, 0, body.Length);
            return ms.ToArray();
        }

        private static byte[] BuildRocketMqPullRequest(int requestId, string topic, string consumerGroup)
        {
            using var ms = new System.IO.MemoryStream();
            var headerJson = $"{{\"code\":11,\"language\":\"JAVA\",\"version\":0,\"opaque\":{requestId},\"flag\":0,\"remark\":\"\",\"extFields\":{{\"topic\":\"{topic}\",\"consumerGroup\":\"{consumerGroup}\",\"queueId\":\"0\",\"queueOffset\":\"0\",\"maxMsgNums\":\"32\"}}}}";
            var headerBytes = Encoding.UTF8.GetBytes(headerJson);
            var totalLength = 4 + headerBytes.Length;
            var lengthBytes = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(totalLength));
            ms.Write(lengthBytes, 0, 4);
            var headerLengthBytes = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(headerBytes.Length));
            ms.Write(headerLengthBytes, 0, 4);
            ms.Write(headerBytes, 0, headerBytes.Length);
            return ms.ToArray();
        }

        private static byte[]? ExtractRocketMqMessage(byte[] response)
        {
            if (response.Length < 8) return null;
            var headerLength = System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt32(response, 0));
            // Body starts at byte offset (4 + headerLength). Condition was inverted: we need
            // 4 + headerLength < response.Length (i.e. at least one body byte exists).
            if (headerLength >= 0 && 4 + headerLength < response.Length)
                return response[(4 + headerLength)..];
            return null;
        }
    }
}
