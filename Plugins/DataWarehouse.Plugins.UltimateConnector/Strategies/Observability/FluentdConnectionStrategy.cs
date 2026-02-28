using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Observability;

/// <summary>
/// Fluentd connection strategy using TCP forward protocol on port 24224.
/// Unified logging layer for data collection and consumption.
/// </summary>
public sealed class FluentdConnectionStrategy : ObservabilityConnectionStrategyBase
{
    public override string StrategyId => "fluentd";
    public override string DisplayName => "Fluentd";
    public override ConnectionStrategyCapabilities Capabilities => new();
    public override string SemanticDescription => "Fluentd unified logging layer. Collects logs from multiple sources and routes to destinations. Ideal for log aggregation and forwarding.";
    public override string[] Tags => ["fluentd", "logs", "log-forwarding", "unified-logging", "cncf"];

    public FluentdConnectionStrategy(ILogger? logger = null) : base(logger) { }

    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var parts = config.ConnectionString?.Split(':') ?? ["localhost", "24224"];
        var host = parts.Length > 0 ? parts[0] : "localhost";
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p24224) ? p24224 : 24224;

        var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(host, port, ct);

        return new DefaultConnectionHandle(tcpClient, new Dictionary<string, object> { ["Provider"] = "Fluentd", ["Host"] = host, ["Port"] = port });
    }

    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var tcpClient = handle.GetConnection<TcpClient>();
        return Task.FromResult(tcpClient.Connected);
    }

    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        handle.GetConnection<TcpClient>().Close();
        if (handle is DefaultConnectionHandle defaultHandle) defaultHandle.MarkDisconnected();
        return Task.CompletedTask;
    }

    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var tcpClient = handle.GetConnection<TcpClient>();
        return Task.FromResult(new ConnectionHealth(tcpClient.Connected, tcpClient.Connected ? "Connected" : "Disconnected", TimeSpan.Zero, DateTimeOffset.UtcNow));
    }

    public override Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default) => throw new NotSupportedException("Fluentd is for logs.");

    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default)
    {
        var tcpClient = handle.GetConnection<TcpClient>();
        var stream = tcpClient.GetStream();
        foreach (var log in logs)
        {
            var json = JsonSerializer.Serialize(log);
            var data = Encoding.UTF8.GetBytes(json + "\n");
            await stream.WriteAsync(data, ct);
        }
        await stream.FlushAsync(ct);
    }

    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default) => throw new NotSupportedException("Fluentd does not support traces.");
}
