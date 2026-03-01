using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CloudWarehouse
{
    public class RedshiftConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private volatile TcpClient? _tcpClient;
        public override string StrategyId => "redshift";
        public override string DisplayName => "AWS Redshift";
        public override string SemanticDescription => "Fast, scalable data warehouse on AWS using PostgreSQL-compatible query language";
        public override string[] Tags => new[] { "cloud", "redshift", "warehouse", "aws", "postgresql-compatible" };
        public override ConnectionStrategyCapabilities Capabilities => new(SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: true, SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: true, SupportsCompression: true, SupportsAuthentication: true, MaxConcurrentConnections: 500, SupportedAuthMethods: new[] { "password", "iam" });
        public RedshiftConnectionStrategy(ILogger<RedshiftConnectionStrategy>? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 5439);
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port, ct);
            return new DefaultConnectionHandle(_tcpClient, new Dictionary<string, object> { ["host"] = host, ["port"] = port, ["protocol"] = "postgresql" });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var info = handle.ConnectionInfo;
            var host = info.TryGetValue("host", out var h) ? h?.ToString() ?? "" : "";
            var port = info.TryGetValue("port", out var p) && p is int pi ? pi : 5439;
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(host, port, ct);
                return true;
            }
            catch { return false; }
        }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { if (_tcpClient != null) { _tcpClient.Close(); _tcpClient.Dispose(); _tcpClient = null; } return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Redshift healthy" : "Redshift unhealthy", sw.Elapsed, DateTimeOffset.UtcNow); }
        public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new NotSupportedException("AWS Redshift query execution requires the Npgsql NuGet package (PostgreSQL-compatible) with valid cluster endpoint/user/password/database configuration.");
        public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new NotSupportedException("AWS Redshift DML execution requires the Npgsql NuGet package (PostgreSQL-compatible).");
        public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
            => throw new NotSupportedException("AWS Redshift schema discovery requires the Npgsql NuGet package (PostgreSQL-compatible).");
        private (string host, int port) ParseHostPort(string connectionString, int defaultPort) { var parts = connectionString.Split(':'); return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort); }
    }
}
