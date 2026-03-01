using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SpecializedDb
{
    public class RethinkDbConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private volatile TcpClient? _tcpClient;
        public override string StrategyId => "rethinkdb";
        public override string DisplayName => "RethinkDB";
        public override string SemanticDescription => "Real-time push database with live query updates and changefeeds";
        public override string[] Tags => new[] { "specialized", "rethinkdb", "realtime", "document", "changefeeds" };
        public override ConnectionStrategyCapabilities Capabilities => new(SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: false, SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: true, SupportsCompression: false, SupportsAuthentication: true, MaxConcurrentConnections: 100, SupportedAuthMethods: new[] { "password" });
        public RethinkDbConnectionStrategy(ILogger<RethinkDbConnectionStrategy>? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 28015);
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port, ct);
            return new DefaultConnectionHandle(_tcpClient, new Dictionary<string, object> { ["host"] = host, ["port"] = port });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { var client = handle.GetConnection<TcpClient>(); if (!client.Connected) return false; try { await client.GetStream().WriteAsync(Array.Empty<byte>(), 0, 0, ct).ConfigureAwait(false); return true; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { if (_tcpClient != null) { _tcpClient.Close(); _tcpClient.Dispose(); _tcpClient = null; } await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // P2-2180: Measure actual latency with Stopwatch instead of hardcoded value.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "RethinkDB healthy" : "RethinkDB unhealthy", sw.Elapsed, DateTimeOffset.UtcNow);
        }
        public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            // RethinkDB uses a custom JSON-over-TCP protocol (port 28015). Requires the RethinkDB C# driver (RethinkDb.Driver).
            // This connector provides TCP connectivity; integrate the RethinkDb.Driver for query execution.
            throw new InvalidOperationException("RethinkDB query execution requires the RethinkDb.Driver NuGet package. This connector provides TCP connectivity only.");
        }
        public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            throw new InvalidOperationException("RethinkDB non-query execution requires the RethinkDb.Driver NuGet package. This connector provides TCP connectivity only.");
        }
        public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            throw new InvalidOperationException("RethinkDB schema discovery requires the RethinkDb.Driver NuGet package. This connector provides TCP connectivity only.");
        }
        // P2-2203: Guard null/empty connectionString before Split to prevent empty-hostname SocketException.
        private static (string host, int port) ParseHostPort(string connectionString, int defaultPort)
        {
            if (string.IsNullOrEmpty(connectionString))
                throw new ArgumentException("ConnectionString must not be null or empty.", nameof(connectionString));
            var parts = connectionString.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort);
        }
    }
}
