using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.Search;

/// <summary>
/// Elasticsearch HTTP/REST transport protocol implementation.
/// Supports Elasticsearch 7.x and 8.x APIs including:
/// - Document CRUD operations
/// - Search queries (Query DSL, SQL)
/// - Aggregations
/// - Bulk operations
/// - Index management
/// - Cluster health and statistics
/// - Scroll API for large result sets
/// - Point in time (PIT) for consistent pagination
/// </summary>
public sealed class ElasticsearchProtocolStrategy : DatabaseProtocolStrategyBase
{
    private HttpClient? _httpClient;
    private string _baseUrl = "";
    private string? _apiKey;
    private string _clusterName = "";
    private string _clusterVersion = "";

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <inheritdoc/>
    public override string StrategyId => "elasticsearch-http";

    /// <inheritdoc/>
    public override string StrategyName => "Elasticsearch HTTP Transport";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Elasticsearch HTTP/REST",
        ProtocolVersion = "8.x",
        DefaultPort = 9200,
        Family = ProtocolFamily.Search,
        MaxPacketSize = 100 * 1024 * 1024, // 100 MB
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = false,
            SupportsPreparedStatements = false,
            SupportsCursors = true, // Scroll API
            SupportsStreaming = true,
            SupportsBatch = true, // Bulk API
            SupportsNotifications = false,
            SupportsSsl = true,
            SupportsCompression = true,
            SupportsMultiplexing = true,
            SupportsServerCursors = true,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods =
            [
                AuthenticationMethod.ClearText,
                AuthenticationMethod.Token,
                AuthenticationMethod.Certificate
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var scheme = parameters.UseSsl ? "https" : "http";
        _baseUrl = $"{scheme}://{parameters.Host}:{parameters.Port}";

        var handler = new HttpClientHandler();
        // SECURITY: TLS certificate validation is enabled by default.
        // Only bypass when explicitly configured to false.
        // SECURITY: SSL certificate validation must never be bypassed in production (finding 2729).
        // Removed _verifySsl bypass. Use X509 trust stores for custom CAs.

        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromSeconds(parameters.CommandTimeout > 0 ? parameters.CommandTimeout : 30)
        };

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        if (_httpClient == null) throw new InvalidOperationException("Not connected");

        // Set up authentication
        if (!string.IsNullOrEmpty(parameters.Password))
        {
            if (parameters.Password.StartsWith("ApiKey ", StringComparison.OrdinalIgnoreCase))
            {
                _apiKey = parameters.Password[7..];
                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("ApiKey", _apiKey);
            }
            else
            {
                // Basic auth
                var credentials = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{parameters.Username}:{parameters.Password}"));
                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", credentials);
            }
        }

        // Verify connection and get cluster info
        using var response = await _httpClient.GetAsync("/", ct);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(ct);
        using var info = JsonDocument.Parse(content);

        if (info.RootElement.TryGetProperty("cluster_name", out var clusterName))
            _clusterName = clusterName.GetString() ?? "";

        if (info.RootElement.TryGetProperty("version", out var version) &&
            version.TryGetProperty("number", out var number))
            _clusterVersion = number.GetString() ?? "";
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_httpClient == null) throw new InvalidOperationException("Not connected");

        // Parse the query to determine operation type
        var operation = ParseOperation(query, parameters);

        try
        {
            var response = await ExecuteOperationAsync(operation, ct);
            return response;
        }
        catch (HttpRequestException ex)
        {
            return new QueryResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ErrorCode = ex.StatusCode?.ToString() ?? "HTTP_ERROR"
            };
        }
    }

    private ElasticsearchOperation ParseOperation(string query, IReadOnlyDictionary<string, object?>? parameters)
    {
        var trimmed = query.Trim();

        // Check for SQL query
        if (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("SHOW", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("DESCRIBE", StringComparison.OrdinalIgnoreCase))
        {
            return new ElasticsearchOperation
            {
                Type = OperationType.SqlQuery,
                Endpoint = "/_sql",
                Method = HttpMethod.Post,
                Body = JsonSerializer.Serialize(new { query = trimmed }, _jsonOptions)
            };
        }

        // Check for JSON query DSL
        if (trimmed.StartsWith("{"))
        {
            var index = parameters?.GetValueOrDefault("index")?.ToString() ?? "_all";
            return new ElasticsearchOperation
            {
                Type = OperationType.Search,
                Endpoint = $"/{index}/_search",
                Method = HttpMethod.Post,
                Body = trimmed
            };
        }

        // Parse command-style queries
        var parts = trimmed.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException("Empty query");
        }

        var command = parts[0].ToUpperInvariant();
        return command switch
        {
            "GET" => ParseGetOperation(parts),
            "PUT" => ParsePutOperation(parts, parameters),
            "POST" => ParsePostOperation(parts, parameters),
            "DELETE" => ParseDeleteOperation(parts),
            "HEAD" => ParseHeadOperation(parts),
            "SEARCH" => ParseSearchOperation(parts, parameters),
            "BULK" => ParseBulkOperation(parameters),
            "SCROLL" => ParseScrollOperation(parts, parameters),
            "COUNT" => ParseCountOperation(parts, parameters),
            "AGGREGATE" or "AGG" => ParseAggregateOperation(parts, parameters),
            _ => throw new ArgumentException($"Unknown command: {command}")
        };
    }

    private ElasticsearchOperation ParseGetOperation(string[] parts)
    {
        var endpoint = parts.Length > 1 ? parts[1] : "/";
        return new ElasticsearchOperation
        {
            Type = OperationType.Get,
            Endpoint = endpoint.StartsWith('/') ? endpoint : $"/{endpoint}",
            Method = HttpMethod.Get
        };
    }

    private ElasticsearchOperation ParsePutOperation(string[] parts, IReadOnlyDictionary<string, object?>? parameters)
    {
        var endpoint = parts.Length > 1 ? parts[1] : throw new ArgumentException("PUT requires endpoint");
        var body = parts.Length > 2 ? parts[2] : parameters?.GetValueOrDefault("body")?.ToString();

        return new ElasticsearchOperation
        {
            Type = OperationType.Put,
            Endpoint = endpoint.StartsWith('/') ? endpoint : $"/{endpoint}",
            Method = HttpMethod.Put,
            Body = body
        };
    }

    private ElasticsearchOperation ParsePostOperation(string[] parts, IReadOnlyDictionary<string, object?>? parameters)
    {
        var endpoint = parts.Length > 1 ? parts[1] : throw new ArgumentException("POST requires endpoint");
        var body = parts.Length > 2 ? parts[2] : parameters?.GetValueOrDefault("body")?.ToString();

        return new ElasticsearchOperation
        {
            Type = OperationType.Post,
            Endpoint = endpoint.StartsWith('/') ? endpoint : $"/{endpoint}",
            Method = HttpMethod.Post,
            Body = body
        };
    }

    private ElasticsearchOperation ParseDeleteOperation(string[] parts)
    {
        var endpoint = parts.Length > 1 ? parts[1] : throw new ArgumentException("DELETE requires endpoint");
        return new ElasticsearchOperation
        {
            Type = OperationType.Delete,
            Endpoint = endpoint.StartsWith('/') ? endpoint : $"/{endpoint}",
            Method = HttpMethod.Delete
        };
    }

    private ElasticsearchOperation ParseHeadOperation(string[] parts)
    {
        var endpoint = parts.Length > 1 ? parts[1] : "/";
        return new ElasticsearchOperation
        {
            Type = OperationType.Head,
            Endpoint = endpoint.StartsWith('/') ? endpoint : $"/{endpoint}",
            Method = HttpMethod.Head
        };
    }

    private ElasticsearchOperation ParseSearchOperation(string[] parts, IReadOnlyDictionary<string, object?>? parameters)
    {
        var index = parts.Length > 1 ? parts[1] : "_all";
        var query = parts.Length > 2 ? parts[2] : parameters?.GetValueOrDefault("query")?.ToString();

        string body;
        if (query != null && query.StartsWith("{"))
        {
            body = query;
        }
        else if (query != null)
        {
            // Simple query string
            body = JsonSerializer.Serialize(new
            {
                query = new
                {
                    query_string = new { query }
                }
            }, _jsonOptions);
        }
        else
        {
            body = JsonSerializer.Serialize(new { query = new { match_all = new { } } }, _jsonOptions);
        }

        return new ElasticsearchOperation
        {
            Type = OperationType.Search,
            Endpoint = $"/{index}/_search",
            Method = HttpMethod.Post,
            Body = body
        };
    }

    private ElasticsearchOperation ParseBulkOperation(IReadOnlyDictionary<string, object?>? parameters)
    {
        var index = parameters?.GetValueOrDefault("index")?.ToString();
        var body = parameters?.GetValueOrDefault("body")?.ToString()
            ?? throw new ArgumentException("BULK requires body parameter");

        var endpoint = index != null ? $"/{index}/_bulk" : "/_bulk";
        return new ElasticsearchOperation
        {
            Type = OperationType.Bulk,
            Endpoint = endpoint,
            Method = HttpMethod.Post,
            Body = body,
            ContentType = "application/x-ndjson"
        };
    }

    private ElasticsearchOperation ParseScrollOperation(string[] parts, IReadOnlyDictionary<string, object?>? parameters)
    {
        var scrollId = parts.Length > 1 ? parts[1] : parameters?.GetValueOrDefault("scroll_id")?.ToString();
        var scroll = parameters?.GetValueOrDefault("scroll")?.ToString() ?? "1m";

        if (scrollId == null)
        {
            throw new ArgumentException("SCROLL requires scroll_id");
        }

        return new ElasticsearchOperation
        {
            Type = OperationType.Scroll,
            Endpoint = "/_search/scroll",
            Method = HttpMethod.Post,
            Body = JsonSerializer.Serialize(new { scroll, scroll_id = scrollId }, _jsonOptions)
        };
    }

    private ElasticsearchOperation ParseCountOperation(string[] parts, IReadOnlyDictionary<string, object?>? parameters)
    {
        var index = parts.Length > 1 ? parts[1] : "_all";
        var query = parameters?.GetValueOrDefault("query")?.ToString();

        string? body = null;
        if (query != null)
        {
            body = query.StartsWith("{") ? query : JsonSerializer.Serialize(new
            {
                query = new { query_string = new { query } }
            }, _jsonOptions);
        }

        return new ElasticsearchOperation
        {
            Type = OperationType.Count,
            Endpoint = $"/{index}/_count",
            Method = body != null ? HttpMethod.Post : HttpMethod.Get,
            Body = body
        };
    }

    private ElasticsearchOperation ParseAggregateOperation(string[] parts, IReadOnlyDictionary<string, object?>? parameters)
    {
        var index = parts.Length > 1 ? parts[1] : "_all";
        var aggs = parameters?.GetValueOrDefault("aggs")?.ToString()
            ?? parameters?.GetValueOrDefault("aggregations")?.ToString()
            ?? throw new ArgumentException("AGGREGATE requires aggs parameter");

        var body = JsonSerializer.Serialize(new
        {
            size = 0,
            aggs = JsonDocument.Parse(aggs).RootElement
        }, _jsonOptions);

        return new ElasticsearchOperation
        {
            Type = OperationType.Aggregate,
            Endpoint = $"/{index}/_search",
            Method = HttpMethod.Post,
            Body = body
        };
    }

    private async Task<QueryResult> ExecuteOperationAsync(ElasticsearchOperation operation, CancellationToken ct)
    {
        if (_httpClient == null) throw new InvalidOperationException("Not connected");

        HttpResponseMessage response;
        if (operation.Body != null)
        {
            var content = new StringContent(operation.Body, Encoding.UTF8,
                operation.ContentType ?? "application/json");
            response = await _httpClient.SendAsync(
                new HttpRequestMessage(operation.Method, operation.Endpoint) { Content = content }, ct);
        }
        else
        {
            response = await _httpClient.SendAsync(
                new HttpRequestMessage(operation.Method, operation.Endpoint), ct);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            return new QueryResult
            {
                Success = false,
                ErrorMessage = responseBody,
                ErrorCode = ((int)response.StatusCode).ToString()
            };
        }

        return ParseResponse(responseBody, operation.Type);
    }

    private QueryResult ParseResponse(string responseBody, OperationType operationType)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        long rowsAffected = 0;

        if (string.IsNullOrEmpty(responseBody))
        {
            return new QueryResult { Success = true, RowsAffected = 0, Rows = rows };
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            switch (operationType)
            {
                case OperationType.Search:
                    if (root.TryGetProperty("hits", out var hits))
                    {
                        if (hits.TryGetProperty("total", out var total))
                        {
                            rowsAffected = total.ValueKind == JsonValueKind.Object
                                ? total.GetProperty("value").GetInt64()
                                : total.GetInt64();
                        }

                        if (hits.TryGetProperty("hits", out var hitArray))
                        {
                            foreach (var hit in hitArray.EnumerateArray())
                            {
                                var row = new Dictionary<string, object?>
                                {
                                    ["_index"] = hit.GetProperty("_index").GetString(),
                                    ["_id"] = hit.GetProperty("_id").GetString()
                                };

                                if (hit.TryGetProperty("_score", out var score))
                                    row["_score"] = score.GetDouble();

                                if (hit.TryGetProperty("_source", out var source))
                                {
                                    foreach (var prop in source.EnumerateObject())
                                    {
                                        row[prop.Name] = GetJsonValue(prop.Value);
                                    }
                                }

                                rows.Add(row);
                            }
                        }
                    }
                    break;

                case OperationType.SqlQuery:
                    if (root.TryGetProperty("columns", out var columns) &&
                        root.TryGetProperty("rows", out var sqlRows))
                    {
                        var columnNames = columns.EnumerateArray()
                            .Select(c => c.GetProperty("name").GetString()!)
                            .ToList();

                        foreach (var sqlRow in sqlRows.EnumerateArray())
                        {
                            var row = new Dictionary<string, object?>();
                            int i = 0;
                            foreach (var value in sqlRow.EnumerateArray())
                            {
                                if (i < columnNames.Count)
                                    row[columnNames[i]] = GetJsonValue(value);
                                i++;
                            }
                            rows.Add(row);
                        }
                        rowsAffected = rows.Count;
                    }
                    break;

                case OperationType.Count:
                    if (root.TryGetProperty("count", out var count))
                    {
                        rowsAffected = count.GetInt64();
                        rows.Add(new Dictionary<string, object?> { ["count"] = rowsAffected });
                    }
                    break;

                case OperationType.Bulk:
                    if (root.TryGetProperty("items", out var items))
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            foreach (var action in item.EnumerateObject())
                            {
                                var row = new Dictionary<string, object?>
                                {
                                    ["action"] = action.Name,
                                    ["_index"] = action.Value.GetProperty("_index").GetString(),
                                    ["_id"] = action.Value.GetProperty("_id").GetString(),
                                    ["status"] = action.Value.GetProperty("status").GetInt32()
                                };

                                if (action.Value.TryGetProperty("result", out var result))
                                    row["result"] = result.GetString();

                                if (action.Value.TryGetProperty("error", out var error))
                                    row["error"] = error.ToString();

                                rows.Add(row);
                                rowsAffected++;
                            }
                        }
                    }
                    break;

                case OperationType.Get:
                    if (root.TryGetProperty("_source", out var getSource))
                    {
                        var row = new Dictionary<string, object?>
                        {
                            ["_index"] = root.TryGetProperty("_index", out var idx) ? idx.GetString() : null,
                            ["_id"] = root.TryGetProperty("_id", out var id) ? id.GetString() : null
                        };

                        foreach (var prop in getSource.EnumerateObject())
                        {
                            row[prop.Name] = GetJsonValue(prop.Value);
                        }
                        rows.Add(row);
                        rowsAffected = 1;
                    }
                    else
                    {
                        // Cluster info or other response
                        var row = new Dictionary<string, object?>();
                        foreach (var prop in root.EnumerateObject())
                        {
                            row[prop.Name] = GetJsonValue(prop.Value);
                        }
                        rows.Add(row);
                    }
                    break;

                default:
                    // Generic response parsing
                    if (root.TryGetProperty("acknowledged", out var ack))
                    {
                        rows.Add(new Dictionary<string, object?> { ["acknowledged"] = ack.GetBoolean() });
                        rowsAffected = ack.GetBoolean() ? 1 : 0;
                    }
                    else if (root.TryGetProperty("result", out var res))
                    {
                        rows.Add(new Dictionary<string, object?> { ["result"] = res.GetString() });
                        rowsAffected = 1;
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            rows.Add(new Dictionary<string, object?> { ["raw"] = responseBody });
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = rowsAffected,
            Rows = rows
        };
    }

    private static object? GetJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(GetJsonValue).ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => GetJsonValue(p.Value)),
            _ => element.ToString()
        };
    }

    /// <inheritdoc/>
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(
        string command,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        return ExecuteQueryCoreAsync(command, parameters, ct);
    }

    /// <inheritdoc/>
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        throw new NotSupportedException("Elasticsearch does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("Elasticsearch does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("Elasticsearch does not support transactions");
    }

    /// <inheritdoc/>
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        // No disconnect message for HTTP
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        if (_httpClient == null) return false;

        try
        {
            using var response = await _httpClient.GetAsync("/_cluster/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    protected override async Task CleanupConnectionAsync()
    {
        _httpClient?.Dispose();
        _httpClient = null;
        await base.CleanupConnectionAsync();
    }

    private enum OperationType
    {
        Get, Put, Post, Delete, Head,
        Search, SqlQuery, Bulk, Scroll, Count, Aggregate
    }

    private sealed class ElasticsearchOperation
    {
        public OperationType Type { get; init; }
        public string Endpoint { get; init; } = "";
        public HttpMethod Method { get; init; } = HttpMethod.Get;
        public string? Body { get; init; }
        public string? ContentType { get; init; }
    }
}

/// <summary>
/// OpenSearch HTTP transport protocol implementation.
/// Fork of Elasticsearch with additional features.
/// </summary>
public sealed class OpenSearchProtocolStrategy : DatabaseProtocolStrategyBase
{
    private HttpClient? _httpClient;
    private string _baseUrl = "";
    private string _clusterVersion = "";

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <inheritdoc/>
    public override string StrategyId => "opensearch-http";

    /// <inheritdoc/>
    public override string StrategyName => "OpenSearch HTTP Transport";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "OpenSearch HTTP/REST",
        ProtocolVersion = "2.x",
        DefaultPort = 9200,
        Family = ProtocolFamily.Search,
        MaxPacketSize = 100 * 1024 * 1024,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = false,
            SupportsPreparedStatements = false,
            SupportsCursors = true,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = true, // Alerting
            SupportsSsl = true,
            SupportsCompression = true,
            SupportsMultiplexing = true,
            SupportsServerCursors = true,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods =
            [
                AuthenticationMethod.ClearText,
                AuthenticationMethod.Token,
                AuthenticationMethod.Certificate,
                AuthenticationMethod.SASL
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var scheme = parameters.UseSsl ? "https" : "http";
        _baseUrl = $"{scheme}://{parameters.Host}:{parameters.Port}";

        var handler = new HttpClientHandler();
        // SECURITY: TLS certificate validation is enabled by default.
        // Only bypass when explicitly configured to false.
        // SECURITY: SSL certificate validation must never be bypassed in production (finding 2729).
        // Removed _verifySsl bypass. Use X509 trust stores for custom CAs.

        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromSeconds(parameters.CommandTimeout > 0 ? parameters.CommandTimeout : 30)
        };

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        if (_httpClient == null) throw new InvalidOperationException("Not connected");

        if (!string.IsNullOrEmpty(parameters.Password))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{parameters.Username}:{parameters.Password}"));
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
        }

        using var response = await _httpClient.GetAsync("/", ct);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(ct);
        using var info = JsonDocument.Parse(content);

        if (info.RootElement.TryGetProperty("version", out var version) &&
            version.TryGetProperty("number", out var number))
            _clusterVersion = number.GetString() ?? "";
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_httpClient == null) throw new InvalidOperationException("Not connected");

        // Parse query similar to Elasticsearch
        var trimmed = query.Trim();
        string endpoint;
        HttpMethod method;
        string? body = null;

        if (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            // SQL query
            endpoint = "/_plugins/_sql";
            method = HttpMethod.Post;
            body = JsonSerializer.Serialize(new { query = trimmed }, _jsonOptions);
        }
        else if (trimmed.StartsWith("{"))
        {
            // Query DSL
            var index = parameters?.GetValueOrDefault("index")?.ToString() ?? "_all";
            endpoint = $"/{index}/_search";
            method = HttpMethod.Post;
            body = trimmed;
        }
        else
        {
            // Simple search
            var parts = trimmed.Split(' ', 2);
            var index = parts.Length > 1 ? parts[1] : "_all";
            endpoint = $"/{index}/_search";
            method = HttpMethod.Get;
        }

        HttpResponseMessage response;
        if (body != null)
        {
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            response = await _httpClient.SendAsync(
                new HttpRequestMessage(method, endpoint) { Content = content }, ct);
        }
        else
        {
            response = await _httpClient.GetAsync(endpoint, ct);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            return new QueryResult
            {
                Success = false,
                ErrorMessage = responseBody,
                ErrorCode = ((int)response.StatusCode).ToString()
            };
        }

        return ParseSearchResponse(responseBody);
    }

    private QueryResult ParseSearchResponse(string responseBody)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        long rowsAffected = 0;

        try
        {
            using var doc = JsonDocument.Parse(responseBody);

            if (doc.RootElement.TryGetProperty("hits", out var hits))
            {
                if (hits.TryGetProperty("total", out var total))
                {
                    rowsAffected = total.ValueKind == JsonValueKind.Object
                        ? total.GetProperty("value").GetInt64()
                        : total.GetInt64();
                }

                if (hits.TryGetProperty("hits", out var hitArray))
                {
                    foreach (var hit in hitArray.EnumerateArray())
                    {
                        var row = new Dictionary<string, object?>
                        {
                            ["_index"] = hit.TryGetProperty("_index", out var idx) ? idx.GetString() : null,
                            ["_id"] = hit.TryGetProperty("_id", out var id) ? id.GetString() : null
                        };

                        if (hit.TryGetProperty("_source", out var source))
                        {
                            foreach (var prop in source.EnumerateObject())
                            {
                                row[prop.Name] = prop.Value.ToString();
                            }
                        }

                        rows.Add(row);
                    }
                }
            }
        }
        catch (JsonException)
        {
            rows.Add(new Dictionary<string, object?> { ["raw"] = responseBody });
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = rowsAffected,
            Rows = rows
        };
    }

    /// <inheritdoc/>
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(
        string command,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        return ExecuteQueryCoreAsync(command, parameters, ct);
    }

    /// <inheritdoc/>
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        throw new NotSupportedException("OpenSearch does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("OpenSearch does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("OpenSearch does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        if (_httpClient == null) return false;

        try
        {
            using var response = await _httpClient.GetAsync("/_cluster/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    protected override async Task CleanupConnectionAsync()
    {
        _httpClient?.Dispose();
        _httpClient = null;
        await base.CleanupConnectionAsync();
    }
}
