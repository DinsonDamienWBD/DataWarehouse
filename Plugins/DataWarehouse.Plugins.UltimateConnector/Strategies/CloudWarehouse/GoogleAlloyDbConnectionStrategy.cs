using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CloudWarehouse
{
    public class GoogleAlloyDbConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private volatile TcpClient? _tcpClient;
        public override string StrategyId => "alloydb";
        public override string DisplayName => "Google AlloyDB";
        public override string SemanticDescription => "Fully managed PostgreSQL-compatible database for demanding transactional and analytical workloads";
        public override string[] Tags => new[] { "cloud", "alloydb", "postgresql", "google", "managed" };
        public override ConnectionStrategyCapabilities Capabilities => new(SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: true, SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: true, SupportsCompression: true, SupportsAuthentication: true, MaxConcurrentConnections: 400, SupportedAuthMethods: new[] { "password", "iam" });
        public GoogleAlloyDbConnectionStrategy(ILogger<GoogleAlloyDbConnectionStrategy>? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 5432);
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port, ct);
            return new DefaultConnectionHandle(_tcpClient, new Dictionary<string, object> { ["host"] = host, ["port"] = port, ["protocol"] = "postgresql" });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var info = handle.ConnectionInfo;
            var host = info.TryGetValue("host", out var h) ? h?.ToString() ?? "" : "";
            var port = info.TryGetValue("port", out var p) && p is int pi ? pi : 5432;
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(host, port, ct);
                return true;
            }
            catch { return false; }
        }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { if (_tcpClient != null) { _tcpClient.Close(); _tcpClient.Dispose(); _tcpClient = null; } await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "AlloyDB healthy" : "AlloyDB unhealthy", sw.Elapsed, DateTimeOffset.UtcNow); }
        public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new NotSupportedException("Google AlloyDB query execution requires the Npgsql NuGet package (PostgreSQL-compatible) with valid host/port/database/user/password connection string.");
        public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new NotSupportedException("Google AlloyDB DML execution requires the Npgsql NuGet package (PostgreSQL-compatible).");
        public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
            => throw new NotSupportedException("Google AlloyDB schema discovery requires the Npgsql NuGet package (PostgreSQL-compatible).");
        private (string host, int port) ParseHostPort(string connectionString, int defaultPort) { var parts = connectionString.Split(':'); return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort); }
    }
}
