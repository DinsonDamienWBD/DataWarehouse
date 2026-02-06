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
    /// Connection strategy for ArangoDB multi-model database.
    /// </summary>
    public class ArangoDbConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private HttpClient? _httpClient;

        public override string StrategyId => "arangodb";
        public override string DisplayName => "ArangoDB";
        public override string SemanticDescription => "Multi-model database supporting documents, graphs, and key-value pairs in one core";
        public override string[] Tags => new[] { "nosql", "arangodb", "multi-model", "graph", "document" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsTransactions: true,
            SupportsBulkOperations: true,
            SupportsSchemaDiscovery: true,
            SupportsSsl: true,
            SupportsCompression: true,
            SupportsAuthentication: true,
            MaxConcurrentConnections: 150,
            SupportedAuthMethods: new[] { "basic", "bearer" }
        );

        public ArangoDbConnectionStrategy(ILogger<ArangoDbConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 8529);
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri($"http://{host}:{port}"),
                Timeout = config.Timeout
            };

            if (config.AuthMethod == "basic" && !string.IsNullOrEmpty(config.AuthCredential))
            {
                var authBytes = System.Text.Encoding.UTF8.GetBytes($"{config.AuthSecondary ?? "root"}:{config.AuthCredential}");
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(authBytes));
            }

            var response = await _httpClient.GetAsync("/_api/version", ct);
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
                var response = await _httpClient.GetAsync("/_api/version", ct);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
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
            return new ConnectionHealth(isHealthy, isHealthy ? "ArangoDB healthy" : "ArangoDB unhealthy",
                TimeSpan.FromMilliseconds(7), DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Executes a query against ArangoDB using the HTTP API.
        /// Returns status information if query execution fails.
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
                        ["__message"] = "ArangoDB connection not established.",
                        ["__strategy"] = StrategyId
                    }
                };
            }

            try
            {
                var requestBody = new Dictionary<string, object> { ["query"] = query };
                if (parameters != null)
                    requestBody["bindVars"] = parameters;

                var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync("/_api/cursor", content, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    return new List<Dictionary<string, object?>>
                    {
                        new()
                        {
                            ["__status"] = "QUERY_FAILED",
                            ["__message"] = $"ArangoDB query failed: {response.StatusCode}",
                            ["__error"] = errorBody,
                            ["__strategy"] = StrategyId
                        }
                    };
                }

                var responseBody = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                var results = new List<Dictionary<string, object?>>();

                if (doc.RootElement.TryGetProperty("result", out var resultArray))
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
                        ["__message"] = $"ArangoDB query error: {ex.Message}",
                        ["__strategy"] = StrategyId
                    }
                };
            }
        }

        /// <summary>
        /// Executes a non-query command against ArangoDB.
        /// Returns -1 if the operation fails, otherwise returns affected count.
        /// </summary>
        public override async Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            if (_httpClient == null)
                return -1;

            try
            {
                var requestBody = new Dictionary<string, object> { ["query"] = command };
                if (parameters != null)
                    requestBody["bindVars"] = parameters;

                var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync("/_api/cursor", content, ct);

                if (!response.IsSuccessStatusCode)
                    return -1;

                var responseBody = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(responseBody);

                if (doc.RootElement.TryGetProperty("extra", out var extra) &&
                    extra.TryGetProperty("stats", out var stats) &&
                    stats.TryGetProperty("writesExecuted", out var writes))
                {
                    return writes.GetInt32();
                }

                return 0;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Retrieves schema information from ArangoDB by listing collections.
        /// </summary>
        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            if (_httpClient == null)
                return Array.Empty<DataSchema>();

            try
            {
                var response = await _httpClient.GetAsync("/_api/collection", ct);
                if (!response.IsSuccessStatusCode)
                    return Array.Empty<DataSchema>();

                var responseBody = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(responseBody);

                var schemas = new List<DataSchema>();
                if (doc.RootElement.TryGetProperty("result", out var collections))
                {
                    foreach (var coll in collections.EnumerateArray())
                    {
                        var name = coll.GetProperty("name").GetString() ?? "unknown";
                        var isSystem = coll.TryGetProperty("isSystem", out var sys) && sys.GetBoolean();

                        if (!isSystem)
                        {
                            schemas.Add(new DataSchema(
                                name,
                                new[]
                                {
                                    new DataSchemaField("_key", "String", false, null, null),
                                    new DataSchemaField("_id", "String", false, null, null),
                                    new DataSchemaField("_rev", "String", false, null, null)
                                },
                                new[] { "_key" },
                                new Dictionary<string, object> { ["type"] = "collection" }
                            ));
                        }
                    }
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
            var clean = connectionString.Replace("http://", "").Replace("https://", "").Split('/')[0];
            var parts = clean.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort);
        }
    }
}
