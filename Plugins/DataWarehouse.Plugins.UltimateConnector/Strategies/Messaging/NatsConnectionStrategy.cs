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
    public class NatsConnectionStrategy : MessagingConnectionStrategyBase
    {
        public override string StrategyId => "nats";
        public override string DisplayName => "NATS";
        public override ConnectorCategory Category => ConnectorCategory.Messaging;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to NATS using TCP text protocol on port 4222 for lightweight messaging.";
        public override string[] Tags => new[] { "nats", "messaging", "pubsub", "tcp", "lightweight" };
        public NatsConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = config.ConnectionString.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 ? int.Parse(parts[1]) : 4222;
            var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(host, port, ct);
            var stream = tcpClient.GetStream();
            // Read server INFO
            var buffer = new byte[1024];
            await stream.ReadAsync(buffer, 0, buffer.Length, ct);
            // Send CONNECT
            var connectCmd = "CONNECT {\"verbose\":false,\"pedantic\":false,\"name\":\"datawarehouse\"}\r\n";
            var connectBytes = Encoding.UTF8.GetBytes(connectCmd);
            await stream.WriteAsync(connectBytes, 0, connectBytes.Length, ct);
            await stream.FlushAsync(ct);
            return new DefaultConnectionHandle(tcpClient, new Dictionary<string, object> { ["Host"] = host, ["Port"] = port });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { return handle.GetConnection<TcpClient>()?.Connected ?? false; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>()?.Close(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "NATS is connected" : "NATS is disconnected", sw.Elapsed, DateTimeOffset.UtcNow); }

        public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            if (client == null || !client.Connected)
                throw new InvalidOperationException("NATS connection is not established");
            var stream = client.GetStream();
            // NATS text protocol: PUB <subject> [reply-to] <#bytes>\r\n<payload>\r\n
            var pubCmd = $"PUB {topic} {message.Length}\r\n";
            var cmdBytes = Encoding.UTF8.GetBytes(pubCmd);
            await stream.WriteAsync(cmdBytes, 0, cmdBytes.Length, ct);
            await stream.WriteAsync(message, 0, message.Length, ct);
            var crlf = Encoding.UTF8.GetBytes("\r\n");
            await stream.WriteAsync(crlf, 0, crlf.Length, ct);
            await stream.FlushAsync(ct);
        }

        public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            if (client == null || !client.Connected)
                throw new InvalidOperationException("NATS connection is not established");
            var stream = client.GetStream();
            // Send SUB command: SUB <subject> [queue group] <sid>\r\n
            var sid = "1";
            var subCmd = consumerGroup != null ? $"SUB {topic} {consumerGroup} {sid}\r\n" : $"SUB {topic} {sid}\r\n";
            var subBytes = Encoding.UTF8.GetBytes(subCmd);
            await stream.WriteAsync(subBytes, 0, subBytes.Length, ct);
            await stream.FlushAsync(ct);
            // Read messages: MSG <subject> <sid> [reply-to] <#bytes>\r\n<payload>\r\n
            var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
            while (!ct.IsCancellationRequested && client.Connected)
            {
                byte[]? messageData = null;
                bool shouldBreak = false;
                try
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) { shouldBreak = true; }
                    else if (line.StartsWith("MSG "))
                    {
                        var parts = line.Split(' ');
                        var size = int.Parse(parts[^1]);
                        if (size > 0 && size < 10 * 1024 * 1024)
                        {
                            var payload = new char[size];
                            var read = await reader.ReadAsync(payload, 0, size);
                            if (read > 0) messageData = Encoding.UTF8.GetBytes(payload, 0, read);
                            await reader.ReadLineAsync(); // consume trailing CRLF
                        }
                    }
                }
                catch (Exception) when (ct.IsCancellationRequested) { shouldBreak = true; }
                if (shouldBreak) break;
                if (messageData != null) yield return messageData;
            }
        }
    }
}
