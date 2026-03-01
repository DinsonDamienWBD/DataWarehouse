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
    public class ActiveMqConnectionStrategy : MessagingConnectionStrategyBase
    {
        public override string StrategyId => "activemq";
        public override string DisplayName => "Apache ActiveMQ";
        public override ConnectorCategory Category => ConnectorCategory.Messaging;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Apache ActiveMQ using TCP OpenWire protocol on port 61616 for JMS messaging.";
        public override string[] Tags => new[] { "activemq", "jms", "messaging", "tcp", "openwire" };
        private int _commandId = 1;
        public ActiveMqConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string required")).Split(':');
            var host = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p61616) ? p61616 : 61616;
            var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(host, port, ct);
            var stream = tcpClient.GetStream();
            // Send OpenWire WireFormatInfo command
            var wireFormat = BuildOpenWireWireFormatInfo();
            await stream.WriteAsync(wireFormat, 0, wireFormat.Length, ct);
            await stream.FlushAsync(ct);
            return new DefaultConnectionHandle(tcpClient, new Dictionary<string, object> { ["Host"] = host, ["Port"] = port });
        }
        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { return Task.FromResult(handle.GetConnection<TcpClient>()?.Connected ?? false); } catch { return Task.FromResult(false); } }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>()?.Close(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "ActiveMQ is connected" : "ActiveMQ is disconnected", sw.Elapsed, DateTimeOffset.UtcNow); }

        public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            if (client == null || !client.Connected)
                throw new InvalidOperationException("ActiveMQ connection is not established");
            var stream = client.GetStream();
            // Build OpenWire MESSAGE command (simplified)
            var commandId = Interlocked.Increment(ref _commandId);
            var messageCmd = BuildOpenWireMessage(commandId, topic, message, headers);
            await stream.WriteAsync(messageCmd, 0, messageCmd.Length, ct);
            await stream.FlushAsync(ct);
        }

        public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            if (client == null || !client.Connected)
                throw new InvalidOperationException("ActiveMQ connection is not established");
            var stream = client.GetStream();
            // Send ConsumerInfo command to subscribe
            var commandId = Interlocked.Increment(ref _commandId);
            var consumerCmd = BuildOpenWireConsumerInfo(commandId, topic, consumerGroup);
            await stream.WriteAsync(consumerCmd, 0, consumerCmd.Length, ct);
            await stream.FlushAsync(ct);
            // Read message commands
            var lengthBuffer = new byte[4];
            while (!ct.IsCancellationRequested && client.Connected)
            {
                byte[]? messageBody = null;
                bool shouldBreak = false;
                try
                {
                    var read = await stream.ReadAsync(lengthBuffer, 0, 4, ct);
                    if (read < 4) { shouldBreak = true; }
                    else
                    {
                        var length = (lengthBuffer[0] << 24) | (lengthBuffer[1] << 16) | (lengthBuffer[2] << 8) | lengthBuffer[3];
                        if (length > 0 && length < 10 * 1024 * 1024)
                        {
                            var payload = new byte[length];
                            var totalRead = 0;
                            while (totalRead < length)
                            {
                                var chunk = await stream.ReadAsync(payload, totalRead, length - totalRead, ct);
                                if (chunk == 0) break;
                                totalRead += chunk;
                            }
                            // Check if this is a MESSAGE command (type 23)
                            if (payload.Length > 1 && payload[0] == 23)
                                messageBody = ExtractMessageBody(payload);
                        }
                    }
                }
                catch (Exception) when (ct.IsCancellationRequested) { shouldBreak = true; }
                if (shouldBreak) break;
                if (messageBody != null) yield return messageBody;
            }
        }

        private byte[] BuildOpenWireWireFormatInfo()
        {
            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(1); // WireFormatInfo type
            // Simplified wire format - real impl would include version, options
            WriteOpenWireString(ms, "TightUnmarshalledCachedEnabled");
            ms.WriteByte(0);
            return WrapWithLength(ms.ToArray());
        }

        private byte[] BuildOpenWireMessage(int commandId, string destination, byte[] body, Dictionary<string, string>? headers)
        {
            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(23); // ActiveMQBytesMessage type
            WriteOpenWireInt(ms, commandId);
            ms.WriteByte(1); // ResponseRequired
            ms.WriteByte(0); // CorrelationId (null)
            // Destination
            ms.WriteByte(1); // Topic type
            WriteOpenWireString(ms, destination);
            // Message body
            WriteOpenWireInt(ms, body.Length);
            ms.Write(body, 0, body.Length);
            return WrapWithLength(ms.ToArray());
        }

        private byte[] BuildOpenWireConsumerInfo(int commandId, string destination, string? selector)
        {
            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(5); // ConsumerInfo type
            WriteOpenWireInt(ms, commandId);
            ms.WriteByte(1); // ResponseRequired
            // ConsumerId
            WriteOpenWireString(ms, Guid.NewGuid().ToString("N"));
            // Destination
            ms.WriteByte(1); // Topic type
            WriteOpenWireString(ms, destination);
            ms.WriteByte(0); // Selector (null)
            return WrapWithLength(ms.ToArray());
        }

        private static byte[] WrapWithLength(byte[] data)
        {
            var result = new byte[4 + data.Length];
            result[0] = (byte)(data.Length >> 24);
            result[1] = (byte)(data.Length >> 16);
            result[2] = (byte)(data.Length >> 8);
            result[3] = (byte)data.Length;
            Array.Copy(data, 0, result, 4, data.Length);
            return result;
        }

        private static void WriteOpenWireString(System.IO.Stream ms, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            ms.WriteByte((byte)(bytes.Length >> 8));
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }

        private static void WriteOpenWireInt(System.IO.Stream ms, int value)
        {
            ms.WriteByte((byte)(value >> 24));
            ms.WriteByte((byte)(value >> 16));
            ms.WriteByte((byte)(value >> 8));
            ms.WriteByte((byte)value);
        }

        private static byte[] ExtractMessageBody(byte[] payload)
        {
            // Full OpenWire frame parsing is not feasible without the ActiveMQ client library.
            // The magic offset=20 approach silently returns garbage body bytes.
            // Callers should use Apache.NMS.ActiveMQ (NuGet) for production message consumption.
            throw new NotSupportedException(
                "OpenWire binary frame parsing requires the Apache.NMS.ActiveMQ NuGet package. " +
                "This raw TCP subscriber cannot reliably extract message body bytes without the full OpenWire codec.");
        }
    }
}
