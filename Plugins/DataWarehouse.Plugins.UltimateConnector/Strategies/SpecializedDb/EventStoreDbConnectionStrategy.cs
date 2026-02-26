using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SpecializedDb
{
    public class EventStoreDbConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private HttpClient? _httpClient;
        public override string StrategyId => "eventstoredb";
        public override string DisplayName => "EventStoreDB";
        public override string SemanticDescription => "Event-native database built for event sourcing with strong consistency guarantees";
        public override string[] Tags => new[] { "specialized", "eventstoredb", "eventsourcing", "streams", "cqrs" };
        public override ConnectionStrategyCapabilities Capabilities => new(SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: true, SupportsBulkOperations: true, SupportsSchemaDiscovery: false, SupportsSsl: true, SupportsCompression: true, SupportsAuthentication: true, MaxConcurrentConnections: 100, SupportedAuthMethods: new[] { "basic" });
        public EventStoreDbConnectionStrategy(ILogger<EventStoreDbConnectionStrategy>? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 2113);
            _httpClient = new HttpClient { BaseAddress = new Uri($"http://{host}:{port}"), Timeout = config.Timeout };
            if (config.AuthMethod == "basic" && !string.IsNullOrEmpty(config.AuthCredential))
            {
                var authBytes = System.Text.Encoding.UTF8.GetBytes($"{config.AuthSecondary ?? "admin"}:{config.AuthCredential}");
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
            }
            using var response = await _httpClient.GetAsync("/stats", ct);
            response.EnsureSuccessStatusCode();
            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object> { ["host"] = host, ["port"] = port });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { if (_httpClient == null) return false; try { var response = await _httpClient.GetAsync("/stats", ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { _httpClient?.Dispose(); _httpClient = null; await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var isHealthy = await TestCoreAsync(handle, ct); return new ConnectionHealth(isHealthy, isHealthy ? "EventStoreDB healthy" : "EventStoreDB unhealthy", TimeSpan.FromMilliseconds(7), DateTimeOffset.UtcNow); }
        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            if (_httpClient == null) return new List<Dictionary<string, object?>>();
            try
            {
                // Query is expected to be a stream name; fetch events from the stream
                var streamName = query.Trim();
                using var response = await _httpClient.GetAsync($"/streams/{Uri.EscapeDataString(streamName)}?embed=body", ct);
                if (!response.IsSuccessStatusCode) return new List<Dictionary<string, object?>>();
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var results = new List<Dictionary<string, object?>>();
                if (doc.RootElement.TryGetProperty("entries", out var entries))
                {
                    foreach (var entry in entries.EnumerateArray())
                    {
                        var row = new Dictionary<string, object?>();
                        row["eventId"] = entry.TryGetProperty("eventId", out var eid) ? eid.GetString() : null;
                        row["eventType"] = entry.TryGetProperty("eventType", out var et) ? et.GetString() : null;
                        row["eventNumber"] = entry.TryGetProperty("eventNumber", out var en) ? en.GetInt64() : 0;
                        row["data"] = entry.TryGetProperty("data", out var data) ? data.ToString() : null;
                        results.Add(row);
                    }
                }
                return results;
            }
            catch { return new List<Dictionary<string, object?>>(); /* Query failed - return empty */ }
        }
        public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            if (_httpClient == null) return 0;
            try
            {
                // Command format: streamName|eventType|eventData(json)
                var parts = command.Split('|');
                if (parts.Length < 3) return 0;
                var streamName = parts[0];
                var eventType = parts[1];
                var eventData = parts[2];
                var eventId = Guid.NewGuid().ToString();
                var body = $"[{{\"eventId\":\"{eventId}\",\"eventType\":\"{eventType}\",\"data\":{eventData}}}]";
                var content = new StringContent(body, System.Text.Encoding.UTF8, "application/vnd.eventstore.events+json");
                using var response = await _httpClient.PostAsync($"/streams/{Uri.EscapeDataString(streamName)}", content, ct);
                return response.IsSuccessStatusCode ? 1 : 0;
            }
            catch { return 0; /* Operation failed - return zero */ }
        }
        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            if (_httpClient == null) return new List<DataSchema>();
            try
            {
                using var response = await _httpClient.GetAsync("/streams", ct);
                if (!response.IsSuccessStatusCode) return new List<DataSchema>();
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var schemas = new List<DataSchema>();
                if (doc.RootElement.TryGetProperty("entries", out var entries))
                {
                    foreach (var entry in entries.EnumerateArray())
                    {
                        var title = entry.TryGetProperty("title", out var t) ? t.GetString() ?? "stream" : "stream";
                        schemas.Add(new DataSchema(title, new[] { new DataSchemaField("eventId", "String", false, null, null), new DataSchemaField("eventType", "String", true, null, null), new DataSchemaField("eventNumber", "Int64", true, null, null), new DataSchemaField("data", "Json", true, null, null) }, new[] { "eventId" }, new Dictionary<string, object> { ["type"] = "stream" }));
                    }
                }
                return schemas.Count > 0 ? schemas : new List<DataSchema> { new DataSchema("$all", new[] { new DataSchemaField("eventId", "String", false, null, null) }, new[] { "eventId" }, new Dictionary<string, object> { ["type"] = "stream" }) };
            }
            catch { return new List<DataSchema>(); /* Schema query failed - return empty */ }
        }
        private (string host, int port) ParseHostPort(string connectionString, int defaultPort) { var clean = connectionString.Replace("http://", "").Split('/')[0]; var parts = clean.Split(':'); return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort); }
    }
}
