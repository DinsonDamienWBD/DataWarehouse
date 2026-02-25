using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Messaging;

/// <summary>
/// NATS connection strategy using the official NATS.Client.Core driver.
/// Provides production-ready connectivity with pub/sub, request/reply,
/// and JetStream persistence support.
/// </summary>
public sealed class NatsConnectionStrategy : MessagingConnectionStrategyBase
{
    public override string StrategyId => "nats";
    public override string DisplayName => "NATS";
    public override ConnectorCategory Category => ConnectorCategory.Messaging;
    public override ConnectionStrategyCapabilities Capabilities => new();
    public override string SemanticDescription =>
        "NATS messaging system using official NATS.Client.Core driver. Supports pub/sub, " +
        "request/reply, queue groups, and JetStream for persistence.";
    public override string[] Tags => ["nats", "messaging", "pubsub", "lightweight", "cloud-native"];

    public NatsConnectionStrategy(ILogger? logger = null) : base(logger) { }

    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var url = config.ConnectionString;
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Connection URL is required for NATS.");

        // Ensure nats:// prefix
        if (!url.StartsWith("nats://") && !url.StartsWith("tls://"))
            url = $"nats://{url}";

        var opts = new NatsOpts
        {
            Url = url,
            Name = "DataWarehouse.UltimateConnector",
            ConnectTimeout = config.Timeout,
            RequestTimeout = config.Timeout
        };

        var connection = new NatsConnection(opts);
        await connection.ConnectAsync();

        var connectionInfo = new Dictionary<string, object>
        {
            ["Provider"] = "NATS.Client.Core",
            ["Url"] = url,
            ["State"] = "Connected"
        };

        return new DefaultConnectionHandle(connection, connectionInfo);
    }

    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        try
        {
            var connection = handle.GetConnection<NatsConnection>();
            return connection.ConnectionState == NatsConnectionState.Open;
        }
        catch
        {
            return false;
        }
    }

    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var connection = handle.GetConnection<NatsConnection>();
        await connection.DisposeAsync();

        if (handle is DefaultConnectionHandle defaultHandle)
            defaultHandle.MarkDisconnected();
    }

    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var connection = handle.GetConnection<NatsConnection>();
            var isOpen = connection.ConnectionState == NatsConnectionState.Open;
            sw.Stop();

            return Task.FromResult(new ConnectionHealth(
                IsHealthy: isOpen,
                StatusMessage: isOpen ? $"NATS connected" : "NATS disconnected",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(new ConnectionHealth(
                IsHealthy: false,
                StatusMessage: $"Health check failed: {ex.Message}",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow));
        }
    }

    public override async Task PublishAsync(
        IConnectionHandle handle,
        string topic,
        byte[] message,
        Dictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        var connection = handle.GetConnection<NatsConnection>();

        NatsHeaders? natsHeaders = null;
        if (headers != null)
        {
            natsHeaders = new NatsHeaders();
            foreach (var (key, value) in headers)
            {
                natsHeaders.Add(key, value);
            }
        }

        await connection.PublishAsync(
            subject: topic,
            data: message,
            headers: natsHeaders,
            cancellationToken: ct);
    }

    public override async IAsyncEnumerable<byte[]> SubscribeAsync(
        IConnectionHandle handle,
        string topic,
        string? consumerGroup = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var connection = handle.GetConnection<NatsConnection>();

        await foreach (var msg in connection.SubscribeAsync<byte[]>(
            subject: topic,
            queueGroup: consumerGroup,
            cancellationToken: ct))
        {
            if (msg.Data != null)
                yield return msg.Data;
        }
    }

    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(
        ConnectionConfig config, CancellationToken ct = default)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.ConnectionString))
            errors.Add("Connection URL is required for NATS.");

        if (config.Timeout <= TimeSpan.Zero)
            errors.Add("Timeout must be a positive duration.");

        return Task.FromResult((errors.Count == 0, errors.ToArray()));
    }
}
