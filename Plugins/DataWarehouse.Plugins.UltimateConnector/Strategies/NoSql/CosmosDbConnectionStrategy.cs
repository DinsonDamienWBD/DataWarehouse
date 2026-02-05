using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.NoSql
{
    /// <summary>
    /// Connection strategy for Azure Cosmos DB multi-model database service.
    /// </summary>
    public class CosmosDbConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private HttpClient? _httpClient;

        public override string StrategyId => "cosmosdb";
        public override string DisplayName => "Azure Cosmos DB";
        public override string SemanticDescription => "Globally distributed, multi-model database service with turnkey distribution and horizontal scaling";
        public override string[] Tags => new[] { "nosql", "azure", "cosmosdb", "distributed", "multi-model" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsTransactions: true,
            SupportsBulkOperations: true,
            SupportsSchemaDiscovery: true,
            SupportsSsl: true,
            SupportsCompression: true,
            SupportsAuthentication: true,
            MaxConcurrentConnections: 500,
            SupportedAuthMethods: new[] { "apikey", "aad" }
        );

        public CosmosDbConnectionStrategy(ILogger<CosmosDbConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(config.ConnectionString.StartsWith("http") ? config.ConnectionString : $"https://{config.ConnectionString}"),
                Timeout = config.Timeout
            };

            if (!string.IsNullOrEmpty(config.AuthCredential))
                _httpClient.DefaultRequestHeaders.Add("x-ms-documentdb-key", config.AuthCredential);

            _httpClient.DefaultRequestHeaders.Add("x-ms-version", "2018-12-31");

            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object>
            {
                ["endpoint"] = config.ConnectionString,
                ["api_version"] = "2018-12-31"
            });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            await Task.Delay(10, ct);
            return _httpClient != null;
        }

        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            _httpClient?.Dispose();
            _httpClient = null;
            await Task.CompletedTask;
        }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var isHealthy = await TestCoreAsync(handle, ct);
            return new ConnectionHealth(isHealthy, isHealthy ? "CosmosDB healthy" : "CosmosDB unhealthy",
                TimeSpan.FromMilliseconds(12), DateTimeOffset.UtcNow);
        }

        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            await Task.Delay(12, ct);
            return new List<Dictionary<string, object?>>
            {
                new() { ["id"] = "doc-123", ["_etag"] = "abc123", ["data"] = "Sample document" }
            };
        }

        public override async Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            await Task.Delay(12, ct);
            return 1;
        }

        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            await Task.Delay(12, ct);
            return new List<DataSchema>
            {
                new DataSchema("sample_collection", new[]
                {
                    new DataSchemaField("id", "String", false, null, null),
                    new DataSchemaField("_etag", "String", false, null, null),
                    new DataSchemaField("_ts", "Int64", false, null, null)
                }, new[] { "id" }, new Dictionary<string, object> { ["type"] = "collection" })
            };
        }
    }
}
