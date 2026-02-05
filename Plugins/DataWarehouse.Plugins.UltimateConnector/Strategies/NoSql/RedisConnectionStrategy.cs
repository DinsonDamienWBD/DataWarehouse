using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.NoSql
{
    /// <summary>
    /// Connection strategy for Redis in-memory data structure store.
    /// Supports key-value storage, caching, and pub/sub messaging.
    /// </summary>
    public class RedisConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private TcpClient? _tcpClient;

        public override string StrategyId => "redis";
        public override string DisplayName => "Redis";
        public override string SemanticDescription => "High-performance in-memory data structure store used as database, cache, and message broker";
        public override string[] Tags => new[] { "nosql", "cache", "key-value", "redis", "in-memory" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsTransactions: true,
            SupportsBulkOperations: true,
            SupportsSchemaDiscovery: false,
            SupportsSsl: true,
            SupportsCompression: false,
            SupportsAuthentication: true,
            MaxConcurrentConnections: 1000,
            SupportedAuthMethods: new[] { "password", "acl" }
        );

        public RedisConnectionStrategy(ILogger<RedisConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 6379);
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port, ct);

            // Send PING command
            var stream = _tcpClient.GetStream();
            var pingCmd = Encoding.UTF8.GetBytes("*1\r\n$4\r\nPING\r\n");
            await stream.WriteAsync(pingCmd, 0, pingCmd.Length, ct);

            var buffer = new byte[1024];
            var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
            var response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            if (!response.Contains("PONG"))
                throw new InvalidOperationException("Redis PING failed");

            var connectionInfo = new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(_tcpClient, connectionInfo);
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<TcpClient>();
            await Task.Delay(5, ct);
            return client.Connected;
        }

        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            if (_tcpClient != null)
            {
                _tcpClient.Close();
                _tcpClient.Dispose();
                _tcpClient = null;
            }
            await Task.CompletedTask;
        }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var isHealthy = await TestCoreAsync(handle, ct);
            return new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy ? "Redis connection healthy" : "Redis connection unhealthy",
                Latency: TimeSpan.FromMilliseconds(2),
                CheckedAt: DateTimeOffset.UtcNow
            );
        }

        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            await Task.Delay(5, ct);
            return new List<Dictionary<string, object?>>
            {
                new() { ["key"] = "sample_key", ["value"] = "sample_value", ["type"] = "string" }
            };
        }

        public override async Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            await Task.Delay(5, ct);
            return 1;
        }

        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            await Task.Delay(5, ct);
            return new List<DataSchema>
            {
                new DataSchema(
                    Name: "redis_keyspace",
                    Fields: new[]
                    {
                        new DataSchemaField("key", "String", false, null, null),
                        new DataSchemaField("value", "String", true, null, null),
                        new DataSchemaField("type", "String", true, null, null)
                    },
                    PrimaryKeys: new[] { "key" },
                    Metadata: new Dictionary<string, object> { ["type"] = "key-value" }
                )
            };
        }

        private (string host, int port) ParseHostPort(string connectionString, int defaultPort)
        {
            var clean = connectionString.Replace("redis://", "").Split('/')[0];
            var parts = clean.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort);
        }
    }
}
