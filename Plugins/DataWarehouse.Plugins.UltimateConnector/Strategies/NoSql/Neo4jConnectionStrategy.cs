using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.NoSql
{
    /// <summary>
    /// Connection strategy for Neo4j graph database.
    /// </summary>
    public class Neo4jConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private volatile HttpClient? _httpClient;
        private TcpClient? _tcpClient;

        public override string StrategyId => "neo4j";
        public override string DisplayName => "Neo4j";
        public override string SemanticDescription => "Leading graph database platform for connected data and relationships";
        public override string[] Tags => new[] { "nosql", "graph", "neo4j", "cypher", "relationships" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsTransactions: true,
            SupportsBulkOperations: true,
            SupportsSchemaDiscovery: true,
            SupportsSsl: true,
            SupportsCompression: true,
            SupportsAuthentication: true,
            MaxConcurrentConnections: 100,
            SupportedAuthMethods: new[] { "basic", "bearer" }
        );

        public Neo4jConnectionStrategy(ILogger<Neo4jConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 7474);

            // Check bolt port (7687) via TCP
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, 7687, ct);

            // Use HTTP REST API
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri($"http://{host}:{port}"),
                Timeout = config.Timeout
            };

            if (config.AuthMethod == "basic" && !string.IsNullOrEmpty(config.AuthCredential))
            {
                var authBytes = System.Text.Encoding.UTF8.GetBytes($"{config.AuthSecondary ?? "neo4j"}:{config.AuthCredential}");
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(authBytes));
            }

            using var response = await _httpClient.GetAsync("/db/data/", ct);
            response.EnsureSuccessStatusCode();

            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object>
            {
                ["host"] = host,
                ["http_port"] = port,
                ["bolt_port"] = 7687
            });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            if (_httpClient == null) return false;
            try
            {
                using var response = await _httpClient.GetAsync("/db/data/", ct);
                return response.IsSuccessStatusCode;
            }
            catch { return false; /* Connection validation - failure acceptable */ }
        }

        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            _httpClient?.Dispose();
            _tcpClient?.Close();
            _tcpClient?.Dispose();
            _httpClient = null;
            _tcpClient = null;
            await Task.CompletedTask;
        }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // P2-2180: Measure actual latency with Stopwatch instead of hardcoded value.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "Neo4j healthy" : "Neo4j unhealthy",
                sw.Elapsed, DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Executes a Cypher query against Neo4j using the HTTP API.
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
                        ["__message"] = "Neo4j connection not established.",
                        ["__strategy"] = StrategyId
                    }
                };
            }

            try
            {
                var requestBody = new Dictionary<string, object>
                {
                    ["statements"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["statement"] = query,
                            ["parameters"] = parameters ?? new Dictionary<string, object?>()
                        }
                    }
                };

                var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync("/db/neo4j/tx/commit", content, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    return new List<Dictionary<string, object?>>
                    {
                        new()
                        {
                            ["__status"] = "QUERY_FAILED",
                            ["__message"] = $"Neo4j query failed: {response.StatusCode}",
                            ["__error"] = errorBody,
                            ["__strategy"] = StrategyId
                        }
                    };
                }

                var responseBody = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                var results = new List<Dictionary<string, object?>>();

                if (doc.RootElement.TryGetProperty("results", out var resultsArray))
                {
                    foreach (var result in resultsArray.EnumerateArray())
                    {
                        if (result.TryGetProperty("columns", out var columns) &&
                            result.TryGetProperty("data", out var data))
                        {
                            var columnNames = columns.EnumerateArray().Select(c => c.GetString() ?? "").ToArray();
                            foreach (var dataRow in data.EnumerateArray())
                            {
                                if (dataRow.TryGetProperty("row", out var row))
                                {
                                    var dict = new Dictionary<string, object?>();
                                    var values = row.EnumerateArray().ToArray();
                                    for (int i = 0; i < Math.Min(columnNames.Length, values.Length); i++)
                                    {
                                        dict[columnNames[i]] = values[i].ValueKind switch
                                        {
                                            System.Text.Json.JsonValueKind.String => values[i].GetString(),
                                            System.Text.Json.JsonValueKind.Number => values[i].GetDouble(),
                                            System.Text.Json.JsonValueKind.True => true,
                                            System.Text.Json.JsonValueKind.False => false,
                                            System.Text.Json.JsonValueKind.Null => null,
                                            _ => values[i].GetRawText()
                                        };
                                    }
                                    results.Add(dict);
                                }
                            }
                        }
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
                        ["__message"] = $"Neo4j query error: {ex.Message}",
                        ["__strategy"] = StrategyId
                    }
                };
            }
        }

        /// <summary>
        /// Executes a Cypher write command against Neo4j.
        /// </summary>
        public override async Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            if (_httpClient == null)
                return -1;

            try
            {
                var requestBody = new Dictionary<string, object>
                {
                    ["statements"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["statement"] = command,
                            ["parameters"] = parameters ?? new Dictionary<string, object?>()
                        }
                    }
                };

                var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync("/db/neo4j/tx/commit", content, ct);

                if (!response.IsSuccessStatusCode)
                    return -1;

                var responseBody = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(responseBody);

                if (doc.RootElement.TryGetProperty("results", out var results) &&
                    results.GetArrayLength() > 0)
                {
                    var firstResult = results[0];
                    if (firstResult.TryGetProperty("stats", out var stats) &&
                        stats.TryGetProperty("nodes_created", out var nodesCreated))
                    {
                        return nodesCreated.GetInt32();
                    }
                }

                return 0;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Retrieves schema information from Neo4j by listing labels and their properties.
        /// </summary>
        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            if (_httpClient == null)
                return Array.Empty<DataSchema>();

            try
            {
                // Query for all labels
                var labelsResult = await ExecuteQueryAsync(handle, "CALL db.labels()", null, ct);
                var schemas = new List<DataSchema>();

                foreach (var row in labelsResult)
                {
                    if (row.TryGetValue("__status", out _))
                        continue; // Skip error responses

                    var labelName = row.Values.FirstOrDefault()?.ToString() ?? "Unknown";

                    schemas.Add(new DataSchema(
                        labelName,
                        new[]
                        {
                            new DataSchemaField("id", "Integer", false, null, null),
                            new DataSchemaField("properties", "Map", true, null, null)
                        },
                        new[] { "id" },
                        new Dictionary<string, object> { ["type"] = "node_label" }
                    ));
                }

                return schemas.Count > 0 ? schemas : Array.Empty<DataSchema>();
            }
            catch
            {
                return Array.Empty<DataSchema>();
            }
        }

        private (string host, int port) ParseHostPort(string connectionString, int defaultPort)
        {
            var clean = connectionString.Replace("neo4j://", "").Replace("bolt://", "").Split('/')[0];
            var parts = clean.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort);
        }
    }
}
