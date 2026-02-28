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
    /// Connection strategy for Couchbase NoSQL database.
    /// </summary>
    public class CouchbaseConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private volatile HttpClient? _httpClient;

        public override string StrategyId => "couchbase";
        public override string DisplayName => "Couchbase";
        public override string SemanticDescription => "Distributed NoSQL cloud database with mobile and edge capabilities";
        public override string[] Tags => new[] { "nosql", "couchbase", "document", "distributed", "mobile" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsTransactions: true,
            SupportsBulkOperations: true,
            SupportsSchemaDiscovery: true,
            SupportsSsl: true,
            SupportsCompression: true,
            SupportsAuthentication: true,
            MaxConcurrentConnections: 200,
            SupportedAuthMethods: new[] { "basic", "cert" }
        );

        public CouchbaseConnectionStrategy(ILogger<CouchbaseConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 8091);
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri($"http://{host}:{port}"),
                Timeout = config.Timeout
            };

            if (config.AuthMethod == "basic" && !string.IsNullOrEmpty(config.AuthCredential))
            {
                var authBytes = System.Text.Encoding.UTF8.GetBytes($"{config.AuthSecondary ?? "admin"}:{config.AuthCredential}");
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(authBytes));
            }

            using var response = await _httpClient.GetAsync("/pools", ct);
            response.EnsureSuccessStatusCode();

            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port
            });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            if (_httpClient == null) return false;
            try
            {
                using var response = await _httpClient.GetAsync("/pools", ct);
                return response.IsSuccessStatusCode;
            }
            catch { return false; /* Connection validation - failure acceptable */ }
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
            return new ConnectionHealth(isHealthy, isHealthy ? "Couchbase healthy" : "Couchbase unhealthy",
                TimeSpan.FromMilliseconds(10), DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Executes an N1QL query against Couchbase using the REST API.
        /// </summary>
        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            if (_httpClient == null)
            {
                return new List<Dictionary<string, object?>>
                {
                    new()
                    {
                        ["__status"] = "NOT_CONNECTED",
                        ["__message"] = "Couchbase connection not established.",
                        ["__strategy"] = StrategyId
                    }
                };
            }

            try
            {
                // Couchbase N1QL query endpoint
                var requestBody = new Dictionary<string, object> { ["statement"] = query };
                if (parameters != null)
                {
                    foreach (var (key, value) in parameters)
                        requestBody[$"${key}"] = value ?? "";
                }

                var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync("/query/service", content, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    return new List<Dictionary<string, object?>>
                    {
                        new()
                        {
                            ["__status"] = "QUERY_FAILED",
                            ["__message"] = $"Couchbase query failed: {response.StatusCode}",
                            ["__error"] = errorBody,
                            ["__strategy"] = StrategyId
                        }
                    };
                }

                var responseBody = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                var results = new List<Dictionary<string, object?>>();

                if (doc.RootElement.TryGetProperty("results", out var resultArray))
                {
                    foreach (var item in resultArray.EnumerateArray())
                    {
                        var row = new Dictionary<string, object?>();
                        foreach (var prop in item.EnumerateObject())
                        {
                            row[prop.Name] = prop.Value.ValueKind switch
                            {
                                System.Text.Json.JsonValueKind.String => prop.Value.GetString(),
                                System.Text.Json.JsonValueKind.Number => prop.Value.GetDouble(),
                                System.Text.Json.JsonValueKind.True => true,
                                System.Text.Json.JsonValueKind.False => false,
                                System.Text.Json.JsonValueKind.Null => null,
                                _ => prop.Value.GetRawText()
                            };
                        }
                        results.Add(row);
                    }
                }

                return results;
            }
            catch (Exception ex)
            {
                return new List<Dictionary<string, object?>>
                {
                    new()
                    {
                        ["__status"] = "ERROR",
                        ["__message"] = $"Couchbase query error: {ex.Message}",
                        ["__strategy"] = StrategyId
                    }
                };
            }
        }

        /// <summary>
        /// Executes a non-query N1QL command against Couchbase.
        /// </summary>
        public override async Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            if (_httpClient == null)
                return -1;

            try
            {
                var requestBody = new Dictionary<string, object> { ["statement"] = command };
                if (parameters != null)
                {
                    foreach (var (key, value) in parameters)
                        requestBody[$"${key}"] = value ?? "";
                }

                var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync("/query/service", content, ct);

                if (!response.IsSuccessStatusCode)
                    return -1;

                var responseBody = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(responseBody);

                if (doc.RootElement.TryGetProperty("metrics", out var metrics) &&
                    metrics.TryGetProperty("mutationCount", out var mutations))
                {
                    return mutations.GetInt32();
                }

                return 0;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Retrieves schema information from Couchbase by listing buckets.
        /// </summary>
        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            if (_httpClient == null)
                return Array.Empty<DataSchema>();

            try
            {
                using var response = await _httpClient.GetAsync("/pools/default/buckets", ct);
                if (!response.IsSuccessStatusCode)
                    return Array.Empty<DataSchema>();

                var responseBody = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(responseBody);

                var schemas = new List<DataSchema>();
                foreach (var bucket in doc.RootElement.EnumerateArray())
                {
                    var name = bucket.GetProperty("name").GetString() ?? "unknown";
                    schemas.Add(new DataSchema(
                        name,
                        new[]
                        {
                            new DataSchemaField("id", "String", false, null, null),
                            new DataSchemaField("type", "String", true, null, null),
                            new DataSchemaField("_cas", "UInt64", false, null, null)
                        },
                        new[] { "id" },
                        new Dictionary<string, object> { ["type"] = "bucket" }
                    ));
                }

                return schemas;
            }
            catch
            {
                return Array.Empty<DataSchema>();
            }
        }

        private (string host, int port) ParseHostPort(string connectionString, int defaultPort)
        {
            var clean = connectionString.Replace("couchbase://", "").Split('/')[0];
            var parts = clean.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort);
        }
    }
}
