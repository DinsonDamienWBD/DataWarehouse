using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CloudWarehouse
{
    public class SnowflakeConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private HttpClient? _httpClient;
        public override string StrategyId => "snowflake";
        public override string DisplayName => "Snowflake";
        public override string SemanticDescription => "Cloud data warehouse with elastic scaling and multi-cloud support for analytics workloads";
        public override string[] Tags => new[] { "cloud", "snowflake", "warehouse", "analytics", "elastic" };
        public override ConnectionStrategyCapabilities Capabilities => new(SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: true, SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: true, SupportsCompression: true, SupportsAuthentication: true, MaxConcurrentConnections: 500, SupportedAuthMethods: new[] { "password", "oauth2", "key-pair" });
        public SnowflakeConnectionStrategy(ILogger<SnowflakeConnectionStrategy>? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString.Contains("snowflakecomputing.com") ? config.ConnectionString : $"https://{config.ConnectionString}.snowflakecomputing.com";
            _httpClient = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = config.Timeout };
            if (!string.IsNullOrEmpty(config.AuthCredential))
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AuthCredential);
            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object> { ["account"] = config.ConnectionString, ["warehouse"] = "COMPUTE_WH" });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            try
            {
                using var response = await client.GetAsync("/", ct);
                return response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable;
            }
            catch { return false; }
        }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { _httpClient?.Dispose(); _httpClient = null; await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Snowflake healthy" : "Snowflake unhealthy", sw.Elapsed, DateTimeOffset.UtcNow); }
        public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new NotSupportedException("Snowflake query execution requires the Snowflake.Data NuGet package and a SnowflakeDbConnection with valid account/user/password/role configuration.");
        public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new NotSupportedException("Snowflake DML execution requires the Snowflake.Data NuGet package and a SnowflakeDbConnection with valid account/user/password/role configuration.");
        public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
            => throw new NotSupportedException("Snowflake schema discovery requires the Snowflake.Data NuGet package and a SnowflakeDbConnection.");
    }
}
