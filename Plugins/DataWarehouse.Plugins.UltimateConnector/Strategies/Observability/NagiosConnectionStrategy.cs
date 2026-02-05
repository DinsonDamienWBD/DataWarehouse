using System;using System.Collections.Generic;using System.Net.Sockets;using System.Text;using System.Threading;using System.Threading.Tasks;using DataWarehouse.SDK.Connectors;using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Observability;

/// <summary>Nagios connection strategy. TCP NSCA port 5667. Classic infrastructure monitoring.</summary>
public sealed class NagiosConnectionStrategy : ObservabilityConnectionStrategyBase
{
    public override string StrategyId => "nagios";public override string DisplayName => "Nagios";public override ConnectionStrategyCapabilities Capabilities => new();public override string SemanticDescription => "Nagios infrastructure monitoring. Check-based monitoring with alerting. Industry-standard for decades.";public override string[] Tags => ["nagios", "monitoring", "infrastructure", "alerting", "open-source"];
    public NagiosConnectionStrategy(ILogger? logger = null) : base(logger) { }
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct){var parts = config.ConnectionString?.Split(':') ?? ["localhost", "5667"];var host = parts[0];var port = parts.Length > 1 ? int.Parse(parts[1]) : 5667;var tcpClient = new TcpClient();await tcpClient.ConnectAsync(host, port, ct);return new DefaultConnectionHandle(tcpClient, new Dictionary<string, object> { ["Provider"] = "Nagios", ["Host"] = host, ["Port"] = port });}
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(handle.GetConnection<TcpClient>().Connected);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct){handle.GetConnection<TcpClient>().Close();if (handle is DefaultConnectionHandle defaultHandle) defaultHandle.MarkDisconnected();return Task.CompletedTask;}
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct){var connected = handle.GetConnection<TcpClient>().Connected;return Task.FromResult(new ConnectionHealth(connected, connected ? "Connected" : "Disconnected", TimeSpan.Zero, DateTimeOffset.UtcNow));}
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default){var tcpClient = handle.GetConnection<TcpClient>();var stream = tcpClient.GetStream();foreach (var metric in metrics){var data = Encoding.ASCII.GetBytes($"{metric["host"]}\t{metric["service"]}\t{metric["status"]}\t{metric["message"]}\n");await stream.WriteAsync(data, ct);}await stream.FlushAsync(ct);}
    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default) => throw new NotSupportedException("Nagios is for checks/metrics.");
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default) => throw new NotSupportedException("Nagios does not support traces.");
}
