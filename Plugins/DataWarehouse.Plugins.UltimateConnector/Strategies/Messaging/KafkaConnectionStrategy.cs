using System;
using System.Collections.Generic;
using System.Net.Sockets;
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
        public override Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default) => throw new NotImplementedException("Kafka publish requires full client library");
        public override IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, CancellationToken ct = default) => throw new NotImplementedException("Kafka subscribe requires full client library");
    }
}
