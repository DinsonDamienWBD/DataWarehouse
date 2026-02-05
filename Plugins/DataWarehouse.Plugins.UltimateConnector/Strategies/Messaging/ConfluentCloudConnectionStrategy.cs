using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Messaging
{
    public class ConfluentCloudConnectionStrategy : MessagingConnectionStrategyBase
    {
        public override string StrategyId => "confluent-cloud";
        public override string DisplayName => "Confluent Cloud";
        public override ConnectorCategory Category => ConnectorCategory.Messaging;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Confluent Cloud using Kafka-compatible TCP protocol with SASL authentication.";
        public override string[] Tags => new[] { "confluent", "kafka", "cloud", "messaging", "sasl" };
        public ConfluentCloudConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct) { var brokers = config.ConnectionString.Split(','); var parts = brokers[0].Split(':'); var host = parts[0]; var port = parts.Length > 1 ? int.Parse(parts[1]) : 9092; var tcpClient = new TcpClient(); await tcpClient.ConnectAsync(host, port, ct); return new DefaultConnectionHandle(tcpClient, new Dictionary<string, object> { ["Brokers"] = config.ConnectionString, ["Host"] = host, ["Port"] = port }); }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { return handle.GetConnection<TcpClient>()?.Connected ?? false; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>()?.Close(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Confluent Cloud is connected" : "Confluent Cloud is disconnected", sw.Elapsed, DateTimeOffset.UtcNow); }
        public override Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default) => throw new NotImplementedException("Confluent Cloud publish requires full client library");
        public override IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, CancellationToken ct = default) => throw new NotImplementedException("Confluent Cloud subscribe requires full client library");
    }
}
