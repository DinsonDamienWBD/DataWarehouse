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
    /// Connection strategy for InfluxDB time series database.
    /// </summary>
    public class InfluxDbConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private HttpClient? _httpClient;

        public override string StrategyId => "influxdb";
        public override string DisplayName => "InfluxDB";
        public override string SemanticDescription => "Purpose-built time series database optimized for high write and query loads";
        public override string[] Tags => new[] { "nosql", "influxdb", "timeseries", "metrics", "iot" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsTransactions: false,
            SupportsBulkOperations: true,
            SupportsSchemaDiscovery: true,
            SupportsSsl: true,
            SupportsCompression: true,
            SupportsAuthentication: true,
            MaxConcurrentConnections: 200,
            SupportedAuthMethods: new[] { "bearer", "basic" }
        );

        public InfluxDbConnectionStrategy(ILogger<InfluxDbConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 8086);
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri($"http://{host}:{port}"),
                Timeout = config.Timeout
            };

            if (config.AuthMethod == "bearer" && !string.IsNullOrEmpty(config.AuthCredential))
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Token", config.AuthCredential);

            var response = await _httpClient.GetAsync("/ping", ct);
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
                var response = await _httpClient.GetAsync("/ping", ct);
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
            return new ConnectionHealth(isHealthy, isHealthy ? "InfluxDB healthy" : "InfluxDB unhealthy",
                TimeSpan.FromMilliseconds(5), DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Executes a Flux or InfluxQL query against InfluxDB using the REST API.
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
                        ["__message"] = "InfluxDB connection not established.",
                        ["__strategy"] = StrategyId
                    }
                };
            }

            try
            {
                var org = parameters?.GetValueOrDefault("org")?.ToString() ?? "default";
                var content = new StringContent(query, System.Text.Encoding.UTF8, "application/vnd.flux");
                var response = await _httpClient.PostAsync($"/api/v2/query?org={org}", content, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    return new List<Dictionary<string, object?>>
                    {
                        new()
                        {
                            ["__status"] = "QUERY_FAILED",
                            ["__message"] = $"InfluxDB query failed: {response.StatusCode}",
                            ["__error"] = errorBody,
                            ["__strategy"] = StrategyId
                        }
                    };
                }

                // InfluxDB returns CSV-formatted data for Flux queries
                var responseBody = await response.Content.ReadAsStringAsync(ct);
                var results = new List<Dictionary<string, object?>>();

                var lines = responseBody.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0)
                {
                    string[]? headers = null;
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line))
                            continue;

                        var values = line.Split(',');
                        if (headers == null)
                        {
                            headers = values;
                            continue;
                        }

                        var row = new Dictionary<string, object?>();
                        for (int i = 0; i < Math.Min(headers.Length, values.Length); i++)
                        {
                            row[headers[i]] = values[i];
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
                        ["__message"] = $"InfluxDB query error: {ex.Message}",
                        ["__strategy"] = StrategyId
                    }
                };
            }
        }

        /// <summary>
        /// Writes data points to InfluxDB using Line Protocol.
        /// </summary>
        public override async Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            if (_httpClient == null)
                return -1;

            try
            {
                var bucket = parameters?.GetValueOrDefault("bucket")?.ToString() ?? "default";
                var org = parameters?.GetValueOrDefault("org")?.ToString() ?? "default";

                var content = new StringContent(command, System.Text.Encoding.UTF8, "text/plain");
                var response = await _httpClient.PostAsync($"/api/v2/write?bucket={bucket}&org={org}", content, ct);

                return response.IsSuccessStatusCode ? 1 : -1;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Retrieves schema information from InfluxDB by listing buckets.
        /// </summary>
        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            if (_httpClient == null)
                return Array.Empty<DataSchema>();

            try
            {
                var response = await _httpClient.GetAsync("/api/v2/buckets", ct);
                if (!response.IsSuccessStatusCode)
                    return Array.Empty<DataSchema>();

                var responseBody = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(responseBody);

                var schemas = new List<DataSchema>();
                if (doc.RootElement.TryGetProperty("buckets", out var buckets))
                {
                    foreach (var bucket in buckets.EnumerateArray())
                    {
                        var name = bucket.GetProperty("name").GetString() ?? "unknown";
                        if (name.StartsWith("_"))
                            continue; // Skip system buckets

                        schemas.Add(new DataSchema(
                            name,
                            new[]
                            {
                                new DataSchemaField("_time", "Timestamp", false, null, null),
                                new DataSchemaField("_measurement", "String", false, null, null),
                                new DataSchemaField("_field", "String", false, null, null),
                                new DataSchemaField("_value", "Float64", true, null, null)
                            },
                            new[] { "_time", "_measurement", "_field" },
                            new Dictionary<string, object> { ["type"] = "bucket" }
                        ));
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
