using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.Graph;

/// <summary>
/// ArangoDB protocol strategy.
/// Implements ArangoDB's HTTP/VelocyPack protocol for multi-model operations.
/// </summary>
public sealed class ArangoDbProtocolStrategy : DatabaseProtocolStrategyBase
{
    private HttpClient? _httpClient;
    private string _database = "";
    private string? _jwt;

    /// <inheritdoc/>
    public override string StrategyId => "arangodb-http";

    /// <inheritdoc/>
    public override string StrategyName => "ArangoDB HTTP Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "ArangoDB HTTP",
        ProtocolVersion = "3.11",
        DefaultPort = 8529,
        Family = ProtocolFamily.Graph,
        MaxPacketSize = 512 * 1024 * 1024, // 512 MB
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = true,
            SupportsPreparedStatements = true,
            SupportsCursors = true,
            SupportsStreaming = true,
            SupportsBatch = true,
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
                AuthenticationMethod.Token
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        _database = parameters.Database ?? "_system";
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var scheme = parameters.UseSsl ? "https" : "http";
        var port = parameters.Port ?? ProtocolInfo.DefaultPort;
        var baseUrl = $"{scheme}://{parameters.Host}:{port}";

        _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };

        // Authenticate with username/password to get JWT
        if (!string.IsNullOrEmpty(parameters.Username))
        {
            var authRequest = new { username = parameters.Username, password = parameters.Password };
            var response = await _httpClient.PostAsJsonAsync("/_open/auth", authRequest, ct);
            response.EnsureSuccessStatusCode();

            var authResult = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            _jwt = authResult.GetProperty("jwt").GetString();

            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("bearer", _jwt);
        }

        // Verify connection by getting server version
        var versionResponse = await _httpClient.GetAsync("/_api/version", ct);
        versionResponse.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        var aqlRequest = new Dictionary<string, object>
        {
            ["query"] = query
        };

        if (parameters != null && parameters.Count > 0)
        {
            aqlRequest["bindVars"] = parameters;
        }

        var response = await _httpClient.PostAsJsonAsync($"/_db/{_database}/_api/cursor", aqlRequest, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        if (result.TryGetProperty("result", out var resultArray))
        {
            foreach (var item in resultArray.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var row = new Dictionary<string, object?>();
                    foreach (var prop in item.EnumerateObject())
                    {
                        row[prop.Name] = GetJsonValue(prop.Value);
                    }
                    rows.Add(row);
                }
                else
                {
                    rows.Add(new Dictionary<string, object?> { ["value"] = GetJsonValue(item) });
                }
            }
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = rows.Count,
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
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => GetJsonValue(p.Value)),
            _ => element.GetRawText()
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
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        var txRequest = new { collections = new { write = Array.Empty<string>(), read = Array.Empty<string>() } };
        var response = await _httpClient.PostAsJsonAsync($"/_db/{_database}/_api/transaction/begin", txRequest, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return result.GetProperty("result").GetProperty("id").GetString() ?? "";
    }

    /// <inheritdoc/>
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        using var response = await _httpClient.PutAsync($"/_db/{_database}/_api/transaction/{transactionId}", null, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        using var response = await _httpClient.DeleteAsync($"/_db/{_database}/_api/transaction/{transactionId}", ct);
        response.EnsureSuccessStatusCode();
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
            using var response = await _httpClient.GetAsync("/_api/version", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    protected override Task CleanupConnectionAsync()
    {
        _httpClient?.Dispose();
        _httpClient = null;
        return Task.CompletedTask;
    }
}

/// <summary>
/// JanusGraph protocol strategy.
/// Implements JanusGraph's Gremlin Server WebSocket protocol.
/// </summary>
public sealed class JanusGraphProtocolStrategy : DatabaseProtocolStrategyBase
{
    private HttpClient? _httpClient;
    private string _graphName = "";

    /// <inheritdoc/>
    public override string StrategyId => "janusgraph-gremlin";

    /// <inheritdoc/>
    public override string StrategyName => "JanusGraph Gremlin Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "JanusGraph Gremlin",
        ProtocolVersion = "1.0",
        DefaultPort = 8182,
        Family = ProtocolFamily.Graph,
        MaxPacketSize = 64 * 1024 * 1024,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = true,
            SupportsPreparedStatements = false,
            SupportsCursors = true,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = true,
            SupportsCompression = false,
            SupportsMultiplexing = false,
            SupportsServerCursors = false,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods =
            [
                AuthenticationMethod.None,
                AuthenticationMethod.ClearText
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        _graphName = parameters.Database ?? "g";
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var scheme = parameters.UseSsl ? "https" : "http";
        var port = parameters.Port ?? ProtocolInfo.DefaultPort;
        var baseUrl = $"{scheme}://{parameters.Host}:{port}";

        _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };

        if (!string.IsNullOrEmpty(parameters.Username))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{parameters.Username}:{parameters.Password}"));
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        }

        // Verify connection
        var testGremlin = new { gremlin = "g.V().limit(0)" };
        var response = await _httpClient.PostAsJsonAsync("/gremlin", testGremlin, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        var gremlinRequest = new Dictionary<string, object>
        {
            ["gremlin"] = query
        };

        if (parameters != null && parameters.Count > 0)
        {
            gremlinRequest["bindings"] = parameters;
        }

        var response = await _httpClient.PostAsJsonAsync("/gremlin", gremlinRequest, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        if (result.TryGetProperty("result", out var resultObj) &&
            resultObj.TryGetProperty("data", out var data))
        {
            foreach (var item in data.EnumerateArray())
            {
                var row = new Dictionary<string, object?>();
                if (item.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in item.EnumerateObject())
                    {
                        row[prop.Name] = GetJsonValue(prop.Value);
                    }
                }
                else
                {
                    row["value"] = GetJsonValue(item);
                }
                rows.Add(row);
            }
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = rows.Count,
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
            _ => element.GetRawText()
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
        // JanusGraph transactions are implicit in Gremlin traversals
        return Task.FromResult(Guid.NewGuid().ToString());
    }

    /// <inheritdoc/>
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("g.tx().commit()", null, ct);
    }

    /// <inheritdoc/>
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("g.tx().rollback()", null, ct);
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
            var testGremlin = new { gremlin = "g.V().limit(0)" };
            var response = await _httpClient.PostAsJsonAsync("/gremlin", testGremlin, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    protected override Task CleanupConnectionAsync()
    {
        _httpClient?.Dispose();
        _httpClient = null;
        return Task.CompletedTask;
    }
}

/// <summary>
/// TigerGraph protocol strategy.
/// Implements TigerGraph's GSQL REST API.
/// </summary>
public sealed class TigerGraphProtocolStrategy : DatabaseProtocolStrategyBase
{
    private HttpClient? _httpClient;
    private string _graphName = "";
    private string? _token;

    /// <inheritdoc/>
    public override string StrategyId => "tigergraph-gsql";

    /// <inheritdoc/>
    public override string StrategyName => "TigerGraph GSQL Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "TigerGraph GSQL",
        ProtocolVersion = "3.9",
        DefaultPort = 9000,
        Family = ProtocolFamily.Graph,
        MaxPacketSize = 1073741824, // 1 GB
        Capabilities = new ProtocolCapabilities
        {
            // P2-2696: TigerGraph REST API does not expose explicit ACID transaction management.
            // BeginTransaction/Commit/Rollback are no-ops; set SupportsTransactions = false to
            // accurately reflect what the protocol actually supports.
            SupportsTransactions = false,
            SupportsPreparedStatements = true,
            SupportsCursors = true,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = true,
            SupportsCompression = true,
            SupportsMultiplexing = true,
            SupportsServerCursors = true,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods =
            [
                AuthenticationMethod.Token,
                AuthenticationMethod.ClearText
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        _graphName = parameters.Database ?? "MyGraph";
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var scheme = parameters.UseSsl ? "https" : "http";
        var port = parameters.Port ?? ProtocolInfo.DefaultPort;
        var baseUrl = $"{scheme}://{parameters.Host}:{port}";

        _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };

        // Get token using username/password
        if (!string.IsNullOrEmpty(parameters.Username))
        {
            var tokenRequest = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("secret", parameters.Password ?? "")
            ]);

            using var response = await _httpClient.PostAsync(
                $"/requesttoken?graph={_graphName}", tokenRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            _token = result.GetProperty("token").GetString();

            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        }

        // Verify connection
        var versionResponse = await _httpClient.GetAsync("/version", ct);
        versionResponse.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        // Build query URL with parameters
        var url = $"/query/{_graphName}/{query}";
        if (parameters != null && parameters.Count > 0)
        {
            var queryParams = string.Join("&", parameters
                .Where(p => p.Value != null)
                .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value?.ToString() ?? "")}"));
            url += $"?{queryParams}";
        }

        using var response = await _httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        if (result.TryGetProperty("results", out var results))
        {
            foreach (var item in results.EnumerateArray())
            {
                var row = new Dictionary<string, object?>();
                if (item.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in item.EnumerateObject())
                    {
                        row[prop.Name] = GetJsonValue(prop.Value);
                    }
                }
                else
                {
                    row["value"] = GetJsonValue(item);
                }
                rows.Add(row);
            }
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = rows.Count,
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
            _ => element.GetRawText()
        };
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(
        string command,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        // POST GSQL command
        var content = new StringContent(command, Encoding.UTF8, "text/plain");
        using var response = await _httpClient.PostAsync("/gsqlserver/gsql/file", content, ct);
        response.EnsureSuccessStatusCode();

        return new QueryResult { Success = true, RowsAffected = 1 };
    }

    /// <inheritdoc/>
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        return Task.FromResult(Guid.NewGuid().ToString());
    }

    /// <inheritdoc/>
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        return Task.CompletedTask;
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
            using var response = await _httpClient.GetAsync("/version", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    protected override Task CleanupConnectionAsync()
    {
        _httpClient?.Dispose();
        _httpClient = null;
        return Task.CompletedTask;
    }
}
