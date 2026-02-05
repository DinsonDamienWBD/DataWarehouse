using System;
using System.Collections.Generic;
using System.Net.Sockets;
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
        public override Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default) => throw new NotImplementedException("ZeroMQ publish requires full client library");
        public override IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, CancellationToken ct = default) => throw new NotImplementedException("ZeroMQ subscribe requires full client library");
    }
}
