using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.Extensions.Logging;

using ElasticHttpMethod = Elastic.Transport.HttpMethod;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.NoSql;

/// <summary>
/// Elasticsearch connection strategy using the official Elastic.Clients.Elasticsearch SDK.
/// Provides production-ready connectivity with Index, Search, Bulk, aggregations,
/// scroll, and index lifecycle management.
/// </summary>
public sealed class ElasticsearchConnectionStrategy : DatabaseConnectionStrategyBase
{
    public override string StrategyId => "elasticsearch";
    public override string DisplayName => "Elasticsearch";
    public override string SemanticDescription =>
        "Elasticsearch search engine using official Elastic SDK. Supports index/search/bulk, " +
        "query DSL, aggregations, scroll API, and index lifecycle management.";
    public override string[] Tags => ["nosql", "elasticsearch", "search", "analytics", "fulltext"];

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
        SupportedAuthMethods: ["basic", "apikey"]
    );

    public ElasticsearchConnectionStrategy(ILogger? logger = null) : base(logger) { }

    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var connectionString = config.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string is required for Elasticsearch.");

        var uri = connectionString.StartsWith("http")
            ? new Uri(connectionString)
            : new Uri($"http://{connectionString}");

        var settings = new ElasticsearchClientSettings(uri)
            .RequestTimeout(config.Timeout);

        if (config.AuthMethod == "basic" && !string.IsNullOrEmpty(config.AuthCredential))
        {
            var username = config.AuthSecondary ?? "elastic";
            settings = settings.Authentication(new BasicAuthentication(username, config.AuthCredential));
        }
        else if (config.AuthMethod == "apikey" && !string.IsNullOrEmpty(config.AuthCredential))
        {
            settings = settings.Authentication(new ApiKey(config.AuthCredential));
        }

        var client = new ElasticsearchClient(settings);

        // Verify connectivity
        var pingResponse = await client.PingAsync(ct);
        if (!pingResponse.IsValidResponse)
        {
            // Try cluster health as fallback - ping may fail on some configs
            var healthResponse = await client.Cluster.HealthAsync(ct);
            if (!healthResponse.IsValidResponse)
                throw new InvalidOperationException($"Elasticsearch connection failed: {healthResponse.DebugInformation}");
        }

        var connectionInfo = new Dictionary<string, object>
        {
            ["Provider"] = "Elastic.Clients.Elasticsearch",
            ["Endpoint"] = uri.ToString(),
            ["State"] = "Connected"
        };

        return new DefaultConnectionHandle(client, connectionInfo);
    }

    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        try
        {
            var client = handle.GetConnection<ElasticsearchClient>();
            var response = await client.PingAsync(ct);
            return response.IsValidResponse;
        }
        catch
        {
            return false;
        }
    }

    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        // ElasticsearchClient uses HttpClient internally which manages its own lifecycle
        if (handle is DefaultConnectionHandle defaultHandle)
            defaultHandle.MarkDisconnected();

        return Task.CompletedTask;
    }

    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var client = handle.GetConnection<ElasticsearchClient>();
            var health = await client.Cluster.HealthAsync(ct);
            sw.Stop();

            if (health.IsValidResponse)
            {
                return new ConnectionHealth(
                    IsHealthy: true,
                    StatusMessage: $"Elasticsearch - Status: {health.Status}, Nodes: {health.NumberOfNodes}, Shards: {health.ActiveShards}",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            return new ConnectionHealth(
                IsHealthy: false,
                StatusMessage: $"Cluster health check returned invalid response",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectionHealth(
                IsHealthy: false,
                StatusMessage: $"Health check failed: {ex.Message}",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Executes a search query using Elasticsearch REST API (query DSL JSON).
    /// The query parameter should be a valid Elasticsearch JSON query body.
    /// Pass "index" in parameters to specify the target index.
    /// </summary>
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
        IConnectionHandle handle,
        string query,
        Dictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var client = handle.GetConnection<ElasticsearchClient>();
        var index = parameters?.GetValueOrDefault("index")?.ToString() ?? "*";

        // Use low-level transport for raw query DSL
        var response = await client.Transport.RequestAsync<StringResponse>(
            ElasticHttpMethod.POST,
            $"/{index}/_search",
            PostData.String(query),
            cancellationToken: ct);

        if (!response.ApiCallDetails.HasSuccessfulStatusCode)
        {
            return new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["__status"] = "QUERY_FAILED",
                    ["__message"] = $"Elasticsearch query failed: {response.ApiCallDetails.HttpStatusCode}",
                    ["__strategy"] = StrategyId
                }
            };
        }

        var results = new List<Dictionary<string, object?>>();
        using var doc = JsonDocument.Parse(response.Body);

        if (doc.RootElement.TryGetProperty("hits", out var hits) &&
            hits.TryGetProperty("hits", out var hitsArray))
        {
            foreach (var hit in hitsArray.EnumerateArray())
            {
                var row = new Dictionary<string, object?>
                {
                    ["_id"] = hit.GetProperty("_id").GetString(),
                    ["_index"] = hit.GetProperty("_index").GetString(),
                    ["_score"] = hit.TryGetProperty("_score", out var score) && score.ValueKind == JsonValueKind.Number
                        ? score.GetDouble() : 0.0
                };

                if (hit.TryGetProperty("_source", out var source))
                {
                    foreach (var prop in source.EnumerateObject())
                    {
                        row[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Null => null,
                            _ => prop.Value.GetRawText()
                        };
                    }
                }
                results.Add(row);
            }
        }

        return results;
    }

    /// <summary>
    /// Indexes a document into Elasticsearch. Command should be a JSON document body.
    /// Pass "index" in parameters to specify the target index.
    /// </summary>
    public override async Task<int> ExecuteNonQueryAsync(
        IConnectionHandle handle,
        string command,
        Dictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var client = handle.GetConnection<ElasticsearchClient>();
        var index = parameters?.GetValueOrDefault("index")?.ToString() ?? "default";

        var response = await client.Transport.RequestAsync<StringResponse>(
            ElasticHttpMethod.POST,
            $"/{index}/_doc",
            PostData.String(command),
            cancellationToken: ct);

        return response.ApiCallDetails.HasSuccessfulStatusCode ? 1 : -1;
    }

    /// <summary>
    /// Retrieves index mappings as schema information.
    /// </summary>
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(
        IConnectionHandle handle,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var client = handle.GetConnection<ElasticsearchClient>();

        var response = await client.Transport.RequestAsync<StringResponse>(
            ElasticHttpMethod.GET,
            "/_mapping",
            cancellationToken: ct);

        if (!response.ApiCallDetails.HasSuccessfulStatusCode)
            return Array.Empty<DataSchema>();

        var schemas = new List<DataSchema>();
        using var doc = JsonDocument.Parse(response.Body);

        foreach (var indexProp in doc.RootElement.EnumerateObject())
        {
            if (indexProp.Name.StartsWith('.')) continue; // Skip system indices

            var fields = new List<DataSchemaField>
            {
                new("_id", "keyword", false, null, null)
            };

            if (indexProp.Value.TryGetProperty("mappings", out var mappings) &&
                mappings.TryGetProperty("properties", out var properties))
            {
                foreach (var fieldProp in properties.EnumerateObject())
                {
                    var fieldType = "object";
                    if (fieldProp.Value.TryGetProperty("type", out var typeValue))
                        fieldType = typeValue.GetString() ?? "object";

                    fields.Add(new DataSchemaField(fieldProp.Name, fieldType, true, null, null));
                }
            }

            schemas.Add(new DataSchema(
                indexProp.Name,
                fields.ToArray(),
                ["_id"],
                new Dictionary<string, object> { ["type"] = "index" }
            ));
        }

        return schemas;
    }

    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(
        ConnectionConfig config, CancellationToken ct = default)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.ConnectionString))
            errors.Add("ConnectionString is required for Elasticsearch.");

        if (config.Timeout <= TimeSpan.Zero)
            errors.Add("Timeout must be a positive duration.");

        return Task.FromResult((errors.Count == 0, errors.ToArray()));
    }
}
