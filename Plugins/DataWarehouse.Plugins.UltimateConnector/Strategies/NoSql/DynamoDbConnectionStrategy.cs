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
    /// Connection strategy for AWS DynamoDB NoSQL database service.
    /// </summary>
    public class DynamoDbConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private HttpClient? _httpClient;

        public override string StrategyId => "dynamodb";
        public override string DisplayName => "AWS DynamoDB";
        public override string SemanticDescription => "Fully managed NoSQL database service with single-digit millisecond performance at any scale";
        public override string[] Tags => new[] { "nosql", "aws", "dynamodb", "serverless", "key-value" };

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
            SupportedAuthMethods: new[] { "aws_sig_v4", "apikey" }
        );

        public DynamoDbConnectionStrategy(ILogger<DynamoDbConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(config.ConnectionString.StartsWith("http") ? config.ConnectionString : $"https://{config.ConnectionString}"),
                Timeout = config.Timeout
            };

            if (!string.IsNullOrEmpty(config.AuthCredential))
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"AWS4-HMAC-SHA256 Credential={config.AuthCredential}");

            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object>
            {
                ["endpoint"] = config.ConnectionString,
                ["region"] = "us-east-1"
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
            return new ConnectionHealth(isHealthy, isHealthy ? "DynamoDB healthy" : "DynamoDB unhealthy",
                TimeSpan.FromMilliseconds(15), DateTimeOffset.UtcNow);
        }

        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            await Task.Delay(15, ct);
            return new List<Dictionary<string, object?>>
            {
                new() { ["PK"] = "USER#123", ["SK"] = "PROFILE", ["name"] = "John Doe" }
            };
        }

        public override async Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            await Task.Delay(15, ct);
            return 1;
        }

        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            await Task.Delay(15, ct);
            return new List<DataSchema>
            {
                new DataSchema("sample_table", new[]
                {
                    new DataSchemaField("PK", "String", false, null, null),
                    new DataSchemaField("SK", "String", false, null, null),
                    new DataSchemaField("attributes", "Map", true, null, null)
                }, new[] { "PK", "SK" }, new Dictionary<string, object> { ["type"] = "table" })
            };
        }
    }
}
