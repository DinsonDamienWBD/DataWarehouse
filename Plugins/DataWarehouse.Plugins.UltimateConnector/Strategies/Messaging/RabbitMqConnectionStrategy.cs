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
    public class RabbitMqConnectionStrategy : MessagingConnectionStrategyBase
    {
        public override string StrategyId => "rabbitmq";
        public override string DisplayName => "RabbitMQ";
        public override ConnectorCategory Category => ConnectorCategory.Messaging;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to RabbitMQ using AMQP TCP protocol on port 5672 for message broker.";
        public override string[] Tags => new[] { "rabbitmq", "amqp", "messaging", "queue", "tcp" };
        private ushort _channelId = 1;
        public RabbitMqConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = config.ConnectionString.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 ? int.Parse(parts[1]) : 5672;
            var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(host, port, ct);
            // Send AMQP protocol header
            var stream = tcpClient.GetStream();
            var protocolHeader = new byte[] { 0x41, 0x4D, 0x51, 0x50, 0x00, 0x00, 0x09, 0x01 }; // "AMQP" + version 0.9.1
            await stream.WriteAsync(protocolHeader, 0, protocolHeader.Length, ct);
            await stream.FlushAsync(ct);
            return new DefaultConnectionHandle(tcpClient, new Dictionary<string, object> { ["Host"] = host, ["Port"] = port });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { return handle.GetConnection<TcpClient>()?.Connected ?? false; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>()?.Close(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "RabbitMQ is connected" : "RabbitMQ is disconnected", sw.Elapsed, DateTimeOffset.UtcNow); }

        public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            if (client == null || !client.Connected)
                throw new InvalidOperationException("RabbitMQ connection is not established");
            var stream = client.GetStream();
            // AMQP Basic.Publish frame (simplified)
            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);
            // Method frame for Basic.Publish (class 60, method 40)
            var exchange = "";
            var routingKey = topic;
            WriteAmqpMethodFrame(writer, _channelId, 60, 40, w =>
            {
                w.Write((short)0); // Reserved
                WriteAmqpShortString(w, exchange);
                WriteAmqpShortString(w, routingKey);
                w.Write((byte)0); // Mandatory=false, Immediate=false
            });
            // Content header frame
            WriteAmqpContentHeader(writer, _channelId, 60, message.Length);
            // Content body frame
            WriteAmqpContentBody(writer, _channelId, message);
            var payload = ms.ToArray();
            await stream.WriteAsync(payload, 0, payload.Length, ct);
            await stream.FlushAsync(ct);
        }

        public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            if (client == null || !client.Connected)
                throw new InvalidOperationException("RabbitMQ connection is not established");
            var stream = client.GetStream();
            // Send Basic.Consume frame
            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);
            var queue = topic;
            var consumerTag = consumerGroup ?? Guid.NewGuid().ToString("N");
            WriteAmqpMethodFrame(writer, _channelId, 60, 20, w =>
            {
                w.Write((short)0); // Reserved
                WriteAmqpShortString(w, queue);
                WriteAmqpShortString(w, consumerTag);
                w.Write((byte)1); // No-local=false, No-ack=true, Exclusive=false, No-wait=false
                w.Write((int)0); // Arguments table (empty)
            });
            var consumeFrame = ms.ToArray();
            await stream.WriteAsync(consumeFrame, 0, consumeFrame.Length, ct);
            await stream.FlushAsync(ct);
            // Read messages
            var frameBuffer = new byte[8];
            while (!ct.IsCancellationRequested && client.Connected)
            {
                byte[]? messageBody = null;
                bool shouldBreak = false;
                try
                {
                    var read = await stream.ReadAsync(frameBuffer, 0, 7, ct);
                    if (read < 7) { shouldBreak = true; }
                    else
                    {
                        var frameType = frameBuffer[0];
                        var channel = (ushort)((frameBuffer[1] << 8) | frameBuffer[2]);
                        var size = (frameBuffer[3] << 24) | (frameBuffer[4] << 16) | (frameBuffer[5] << 8) | frameBuffer[6];
                        if (size > 0 && size < 10 * 1024 * 1024)
                        {
                            var body = new byte[size + 1]; // +1 for frame-end
                            var totalRead = 0;
                            while (totalRead < size + 1)
                            {
                                var chunk = await stream.ReadAsync(body, totalRead, size + 1 - totalRead, ct);
                                if (chunk == 0) break;
                                totalRead += chunk;
                            }
                            if (frameType == 3) // Body frame
                                messageBody = body[..size];
                        }
                    }
                }
                catch (Exception) when (ct.IsCancellationRequested) { shouldBreak = true; }
                if (shouldBreak) break;
                if (messageBody != null) yield return messageBody;
            }
        }

        private static void WriteAmqpMethodFrame(System.IO.BinaryWriter writer, ushort channel, ushort classId, ushort methodId, Action<System.IO.BinaryWriter> writeArgs)
        {
            using var argMs = new System.IO.MemoryStream();
            using var argWriter = new System.IO.BinaryWriter(argMs);
            argWriter.Write((byte)(classId >> 8)); argWriter.Write((byte)classId);
            argWriter.Write((byte)(methodId >> 8)); argWriter.Write((byte)methodId);
            writeArgs(argWriter);
            var args = argMs.ToArray();
            writer.Write((byte)1); // Method frame
            writer.Write((byte)(channel >> 8)); writer.Write((byte)channel);
            writer.Write((byte)(args.Length >> 24)); writer.Write((byte)(args.Length >> 16));
            writer.Write((byte)(args.Length >> 8)); writer.Write((byte)args.Length);
            writer.Write(args);
            writer.Write((byte)0xCE); // Frame end
        }

        private static void WriteAmqpContentHeader(System.IO.BinaryWriter writer, ushort channel, ushort classId, long bodySize)
        {
            var headerSize = 14; // class(2) + weight(2) + bodySize(8) + propertyFlags(2)
            writer.Write((byte)2); // Header frame
            writer.Write((byte)(channel >> 8)); writer.Write((byte)channel);
            writer.Write((byte)(headerSize >> 24)); writer.Write((byte)(headerSize >> 16));
            writer.Write((byte)(headerSize >> 8)); writer.Write((byte)headerSize);
            writer.Write((byte)(classId >> 8)); writer.Write((byte)classId);
            writer.Write((short)0); // Weight
            writer.Write((byte)(bodySize >> 56)); writer.Write((byte)(bodySize >> 48));
            writer.Write((byte)(bodySize >> 40)); writer.Write((byte)(bodySize >> 32));
            writer.Write((byte)(bodySize >> 24)); writer.Write((byte)(bodySize >> 16));
            writer.Write((byte)(bodySize >> 8)); writer.Write((byte)bodySize);
            writer.Write((short)0); // Property flags
            writer.Write((byte)0xCE);
        }

        private static void WriteAmqpContentBody(System.IO.BinaryWriter writer, ushort channel, byte[] body)
        {
            writer.Write((byte)3); // Body frame
            writer.Write((byte)(channel >> 8)); writer.Write((byte)channel);
            writer.Write((byte)(body.Length >> 24)); writer.Write((byte)(body.Length >> 16));
            writer.Write((byte)(body.Length >> 8)); writer.Write((byte)body.Length);
            writer.Write(body);
            writer.Write((byte)0xCE);
        }

        private static void WriteAmqpShortString(System.IO.BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }
    }
}
