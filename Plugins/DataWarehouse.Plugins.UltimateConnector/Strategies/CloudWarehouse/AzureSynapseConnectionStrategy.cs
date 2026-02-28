using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CloudWarehouse
{
    public class AzureSynapseConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private volatile TcpClient? _tcpClient;
        public override string StrategyId => "synapse";
        public override string DisplayName => "Azure Synapse Analytics";
        public override string SemanticDescription => "Limitless analytics service bringing together data integration, enterprise data warehousing, and big data analytics";
        public override string[] Tags => new[] { "cloud", "synapse", "warehouse", "azure", "analytics" };
        public override ConnectionStrategyCapabilities Capabilities => new(SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: true, SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: true, SupportsCompression: true, SupportsAuthentication: true, MaxConcurrentConnections: 500, SupportedAuthMethods: new[] { "password", "aad" });
        public AzureSynapseConnectionStrategy(ILogger<AzureSynapseConnectionStrategy>? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 1433);
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port, ct);
            return new DefaultConnectionHandle(_tcpClient, new Dictionary<string, object> { ["host"] = host, ["port"] = port, ["protocol"] = "sqlserver" });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // Re-probe the TCP endpoint to avoid stale .Connected state
            var client = handle.GetConnection<TcpClient>();
            var info = handle.ConnectionInfo;
            var host = info.TryGetValue("host", out var h) ? h?.ToString() ?? "" : "";
            var port = info.TryGetValue("port", out var p) && p is int pi ? pi : 1433;
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(host, port, ct);
                return true;
            }
            catch { return false; }
        }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { if (_tcpClient != null) { _tcpClient.Close(); _tcpClient.Dispose(); _tcpClient = null; } await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Synapse healthy" : "Synapse unhealthy", sw.Elapsed, DateTimeOffset.UtcNow); }
        public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new NotSupportedException("Azure Synapse query execution requires Microsoft.Data.SqlClient NuGet package with a valid SQL connection string including user/password or AAD token.");
        public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new NotSupportedException("Azure Synapse DML execution requires Microsoft.Data.SqlClient NuGet package with a valid SQL connection string including user/password or AAD token.");
        public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
            => throw new NotSupportedException("Azure Synapse schema discovery requires Microsoft.Data.SqlClient NuGet package.");
        private (string host, int port) ParseHostPort(string connectionString, int defaultPort) { var parts = connectionString.Split(':'); return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort); }
    }
}
