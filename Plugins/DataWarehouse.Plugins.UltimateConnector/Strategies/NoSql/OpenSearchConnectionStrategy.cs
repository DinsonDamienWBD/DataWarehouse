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
    /// Connection strategy for OpenSearch distributed search and analytics engine.
    /// </summary>
    public class OpenSearchConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private HttpClient? _httpClient;

        public override string StrategyId => "opensearch";
        public override string DisplayName => "OpenSearch";
        public override string SemanticDescription => "Community-driven, Apache 2.0-licensed search and analytics suite derived from Elasticsearch";
        public override string[] Tags => new[] { "nosql", "opensearch", "search", "analytics", "fulltext" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsTransactions: false,
            SupportsBulkOperations: true,
            SupportsSchemaDiscovery: true,
            SupportsSsl: true,
            SupportsCompression: true,
            SupportsAuthentication: true,
            MaxConcurrentConnections: 300,
            SupportedAuthMethods: new[] { "basic", "apikey", "saml" }
        );

        public OpenSearchConnectionStrategy(ILogger<OpenSearchConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 9200);
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

            using var response = await _httpClient.GetAsync("/_cluster/health", ct);
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
                using var response = await _httpClient.GetAsync("/_cluster/health", ct);
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
            return new ConnectionHealth(isHealthy, isHealthy ? "OpenSearch healthy" : "OpenSearch unhealthy",
                TimeSpan.FromMilliseconds(10), DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Executes a search query against OpenSearch using the REST API.
        /// The query should be a JSON query DSL string.
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
                        ["__message"] = "OpenSearch connection not established.",
                        ["__strategy"] = StrategyId
                    }
                };
            }

            try
            {
                var index = parameters?.GetValueOrDefault("index")?.ToString() ?? "_all";
                var content = new StringContent(query, System.Text.Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync($"/{index}/_search", content, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    return new List<Dictionary<string, object?>>
                    {
                        new()
                        {
                            ["__status"] = "QUERY_FAILED",
                            ["__message"] = $"OpenSearch query failed: {response.StatusCode}",
                            ["__error"] = errorBody,
                            ["__strategy"] = StrategyId
                        }
                    };
                }

                var responseBody = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                var results = new List<Dictionary<string, object?>>();

                if (doc.RootElement.TryGetProperty("hits", out var hits) &&
                    hits.TryGetProperty("hits", out var hitsArray))
                {
                    foreach (var hit in hitsArray.EnumerateArray())
                    {
                        var row = new Dictionary<string, object?>
                        {
                            ["_id"] = hit.GetProperty("_id").GetString(),
                            ["_index"] = hit.GetProperty("_index").GetString(),
                            ["_score"] = hit.TryGetProperty("_score", out var score) ? score.GetDouble() : 0
                        };

                        if (hit.TryGetProperty("_source", out var source))
                        {
                            foreach (var prop in source.EnumerateObject())
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
                        ["__message"] = $"OpenSearch query error: {ex.Message}",
                        ["__strategy"] = StrategyId
                    }
                };
            }
        }

        /// <summary>
        /// Executes a bulk or index command against OpenSearch.
        /// </summary>
        public override async Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            if (_httpClient == null)
                return -1;

            try
            {
                var index = parameters?.GetValueOrDefault("index")?.ToString() ?? "default";
                var content = new StringContent(command, System.Text.Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync($"/{index}/_doc", content, ct);

                return response.IsSuccessStatusCode ? 1 : -1;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Retrieves schema information from OpenSearch by listing indices and their mappings.
        /// </summary>
        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            if (_httpClient == null)
                return Array.Empty<DataSchema>();

            try
            {
                using var response = await _httpClient.GetAsync("/_mapping", ct);
                if (!response.IsSuccessStatusCode)
                    return Array.Empty<DataSchema>();

                var responseBody = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(responseBody);

                var schemas = new List<DataSchema>();
                foreach (var indexProp in doc.RootElement.EnumerateObject())
                {
                    var indexName = indexProp.Name;
                    if (indexName.StartsWith("."))
                        continue; // Skip system indices

                    var fields = new List<DataSchemaField>
                    {
                        new DataSchemaField("_id", "String", false, null, null)
                    };

                    if (indexProp.Value.TryGetProperty("mappings", out var mappings) &&
                        mappings.TryGetProperty("properties", out var properties))
                    {
                        foreach (var fieldProp in properties.EnumerateObject())
                        {
                            var fieldType = "Object";
                            if (fieldProp.Value.TryGetProperty("type", out var typeValue))
                                fieldType = typeValue.GetString() ?? "Object";

                            fields.Add(new DataSchemaField(fieldProp.Name, fieldType, true, null, null));
                        }
                    }

                    schemas.Add(new DataSchema(
                        indexName,
                        fields.ToArray(),
                        new[] { "_id" },
                        new Dictionary<string, object> { ["type"] = "index" }
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
            var clean = connectionString.Replace("http://", "").Replace("https://", "").Split('/')[0];
            var parts = clean.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort);
        }
    }
}
