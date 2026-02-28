using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SpecializedDb
{
    public class TigerGraphConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private volatile HttpClient? _httpClient;

        public override string StrategyId => "tigergraph";
        public override string DisplayName => "TigerGraph";
        public override string SemanticDescription => "Native parallel graph database for real-time deep link analytics on large-scale datasets";
        public override string[] Tags => new[] { "specialized", "tigergraph", "graph", "parallel", "analytics" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: true,
            SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: true,
            SupportsCompression: true, SupportsAuthentication: true, MaxConcurrentConnections: 100,
            SupportedAuthMethods: new[] { "basic", "bearer" }
        );

        public TigerGraphConnectionStrategy(ILogger<TigerGraphConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 9000);
            _httpClient = new HttpClient { BaseAddress = new Uri($"http://{host}:{port}"), Timeout = config.Timeout };
            if (!string.IsNullOrEmpty(config.AuthCredential))
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AuthCredential);
            using var response = await _httpClient.GetAsync("/echo", ct);
            response.EnsureSuccessStatusCode();
            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object> { ["host"] = host, ["port"] = port });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            if (_httpClient == null) return false;
            try { using var response = await _httpClient.GetAsync("/echo", ct); return response.IsSuccessStatusCode; }
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
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "TigerGraph healthy" : "TigerGraph unhealthy", sw.Elapsed, DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Executes a GSQL query via TigerGraph REST API.
        /// Supports inline GSQL statements submitted to /gsqlserver/gsql/file.
        /// </summary>
        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            // TigerGraph REST++ API: POST /gsqlserver/gsql/file with GSQL body
            using var content = new StringContent(query, Encoding.UTF8, "text/plain");
            using var response = await client.PostAsync("/gsqlserver/gsql/file", content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var results = new List<Dictionary<string, object?>>();

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                // TigerGraph responses vary; attempt to parse "results" array
                if (root.TryGetProperty("results", out var resultsProp) &&
                    resultsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in resultsProp.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            var dict = new Dictionary<string, object?>();
                            foreach (var prop in item.EnumerateObject())
                                dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.ToString();
                            results.Add(dict);
                        }
                    }
                }
            }
            catch { /* Return empty on parse failure */ }

            return results;
        }

        /// <summary>
        /// Executes a GSQL DDL/DML statement.
        /// </summary>
        public override async Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            using var content = new StringContent(command, Encoding.UTF8, "text/plain");
            using var response = await client.PostAsync("/gsqlserver/gsql/file", content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return 1;
        }

        /// <summary>
        /// Retrieves graph schema via TigerGraph's /gsqlserver/gsql/schema endpoint.
        /// </summary>
        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            try
            {
                using var response = await client.GetAsync("/gsqlserver/gsql/schema", ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return Array.Empty<DataSchema>();

                using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(ct).ConfigureAwait(false);
                var root = doc!.RootElement;
                var schemas = new List<DataSchema>();

                if (root.TryGetProperty("VertexTypes", out var vertexTypes))
                {
                    foreach (var vt in vertexTypes.EnumerateArray())
                    {
                        var typeName = vt.TryGetProperty("Name", out var n) ? n.GetString() ?? "Vertex" : "Vertex";
                        var fields = new List<DataSchemaField>();
                        if (vt.TryGetProperty("Attributes", out var attrs))
                        {
                            foreach (var attr in attrs.EnumerateArray())
                            {
                                var attrName = attr.TryGetProperty("AttributeName", out var an) ? an.GetString() ?? "attr" : "attr";
                                var attrType = attr.TryGetProperty("AttributeType", out var at) && at.TryGetProperty("Name", out var atn) ? atn.GetString() ?? "STRING" : "STRING";
                                fields.Add(new DataSchemaField(attrName, attrType, true, null, null));
                            }
                        }
                        schemas.Add(new DataSchema(typeName, fields.ToArray(), new[] { "id" },
                            new Dictionary<string, object> { ["type"] = "vertex" }));
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
            var clean = connectionString.Replace("http://", "").Split('/')[0];
            var parts = clean.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort);
        }
    }
}
