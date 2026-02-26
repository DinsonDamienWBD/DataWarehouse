using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.Search;

/// <summary>
/// Apache Solr protocol strategy.
/// Implements Solr's REST/JSON API for full-text search operations.
/// </summary>
public sealed class SolrProtocolStrategy : DatabaseProtocolStrategyBase
{
    private HttpClient? _httpClient;
    private string _collection = "";
    private string _baseUrl = "";
    private bool _verifySsl = true;

    /// <inheritdoc/>
    public override string StrategyId => "solr-rest";

    /// <inheritdoc/>
    public override string StrategyName => "Apache Solr REST Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Apache Solr REST",
        ProtocolVersion = "9.x",
        DefaultPort = 8983,
        Family = ProtocolFamily.Search,
        MaxPacketSize = 100 * 1024 * 1024, // 100 MB
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = false,
            SupportsPreparedStatements = false,
            SupportsCursors = true,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = true,
            SupportsCompression = true,
            SupportsMultiplexing = true,
            SupportsServerCursors = true,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = false,
            SupportedAuthMethods =
            [
                AuthenticationMethod.None,
                AuthenticationMethod.ClearText,
                AuthenticationMethod.Kerberos
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        _collection = parameters.Database ?? "default";
        var scheme = parameters.UseSsl ? "https" : "http";
        var port = parameters.Port ?? ProtocolInfo.DefaultPort;
        _baseUrl = $"{scheme}://{parameters.Host}:{port}/solr";
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var handler = new HttpClientHandler();
        // SECURITY: TLS certificate validation is enabled by default.
        // Only bypass when explicitly configured to false.
        if (parameters.UseSsl && !_verifySsl)
        {
            handler.ServerCertificateCustomValidationCallback =
                (message, cert, chain, errors) => true;
        }

        _httpClient = new HttpClient(handler) { BaseAddress = new Uri(_baseUrl) };

        if (!string.IsNullOrEmpty(parameters.Username))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{parameters.Username}:{parameters.Password}"));
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        }

        // Verify connection with admin ping
        using var response = await _httpClient.GetAsync($"/{_collection}/admin/ping", ct);
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

        // Build query URL
        var queryParams = new Dictionary<string, string>
        {
            ["q"] = query,
            ["wt"] = "json"
        };

        if (parameters != null)
        {
            foreach (var param in parameters)
            {
                if (param.Value != null)
                    queryParams[param.Key] = param.Value.ToString()!;
            }
        }

        var queryString = string.Join("&", queryParams.Select(p =>
            $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

        using var response = await _httpClient.GetAsync($"/{_collection}/select?{queryString}", ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<JsonElement>(json);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        if (result.TryGetProperty("response", out var responseObj) &&
            responseObj.TryGetProperty("docs", out var docs))
        {
            foreach (var doc in docs.EnumerateArray())
            {
                var row = new Dictionary<string, object?>();
                foreach (var prop in doc.EnumerateObject())
                {
                    row[prop.Name] = GetJsonValue(prop.Value);
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
            JsonValueKind.Array => element.EnumerateArray().Select(GetJsonValue).ToList(),
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

        // Parse command as JSON for updates
        var content = new StringContent(command, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync($"/{_collection}/update?commit=true", content, ct);
        response.EnsureSuccessStatusCode();

        return new QueryResult { Success = true, RowsAffected = 1 };
    }

    /// <inheritdoc/>
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        throw new NotSupportedException("Solr does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("Solr does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("Solr does not support transactions");
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
            using var response = await _httpClient.GetAsync($"/{_collection}/admin/ping", ct);
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
/// MeiliSearch protocol strategy.
/// Implements MeiliSearch's REST API for fast search operations.
/// </summary>
public sealed class MeiliSearchProtocolStrategy : DatabaseProtocolStrategyBase
{
    private HttpClient? _httpClient;
    private string _index = "";

    /// <inheritdoc/>
    public override string StrategyId => "meilisearch-rest";

    /// <inheritdoc/>
    public override string StrategyName => "MeiliSearch REST Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "MeiliSearch REST",
        ProtocolVersion = "1.x",
        DefaultPort = 7700,
        Family = ProtocolFamily.Search,
        MaxPacketSize = 100 * 1024 * 1024,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = false,
            SupportsPreparedStatements = false,
            SupportsCursors = true,
            SupportsStreaming = false,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = true,
            SupportsCompression = false,
            SupportsMultiplexing = true,
            SupportsServerCursors = false,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = false,
            SupportedAuthMethods =
            [
                AuthenticationMethod.None,
                AuthenticationMethod.Token
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        _index = parameters.Database ?? "default";
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var scheme = parameters.UseSsl ? "https" : "http";
        var port = parameters.Port ?? ProtocolInfo.DefaultPort;
        var baseUrl = $"{scheme}://{parameters.Host}:{port}";

        _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };

        // Add API key if provided
        if (!string.IsNullOrEmpty(parameters.Password))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", parameters.Password);
        }

        // Verify connection
        using var response = await _httpClient.GetAsync("/health", ct);
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

        var searchRequest = new Dictionary<string, object>
        {
            ["q"] = query
        };

        if (parameters != null)
        {
            foreach (var param in parameters)
            {
                if (param.Value != null)
                    searchRequest[param.Key] = param.Value;
            }
        }

        var response = await _httpClient.PostAsJsonAsync($"/indexes/{_index}/search", searchRequest, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        if (result.TryGetProperty("hits", out var hits))
        {
            foreach (var hit in hits.EnumerateArray())
            {
                var row = new Dictionary<string, object?>();
                foreach (var prop in hit.EnumerateObject())
                {
                    row[prop.Name] = GetJsonValue(prop.Value);
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

        // Assume command is JSON for document operations
        var content = new StringContent(command, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync($"/indexes/{_index}/documents", content, ct);
        response.EnsureSuccessStatusCode();

        return new QueryResult { Success = true, RowsAffected = 1 };
    }

    /// <inheritdoc/>
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        throw new NotSupportedException("MeiliSearch does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("MeiliSearch does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("MeiliSearch does not support transactions");
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
            using var response = await _httpClient.GetAsync("/health", ct);
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
/// Typesense protocol strategy.
/// Implements Typesense's REST API for instant search.
/// </summary>
public sealed class TypesenseProtocolStrategy : DatabaseProtocolStrategyBase
{
    private HttpClient? _httpClient;
    private string _collection = "";

    /// <inheritdoc/>
    public override string StrategyId => "typesense-rest";

    /// <inheritdoc/>
    public override string StrategyName => "Typesense REST Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Typesense REST",
        ProtocolVersion = "0.25",
        DefaultPort = 8108,
        Family = ProtocolFamily.Search,
        MaxPacketSize = 100 * 1024 * 1024,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = false,
            SupportsPreparedStatements = false,
            SupportsCursors = true,
            SupportsStreaming = false,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = true,
            SupportsCompression = false,
            SupportsMultiplexing = true,
            SupportsServerCursors = false,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = false,
            SupportedAuthMethods =
            [
                AuthenticationMethod.Token
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        _collection = parameters.Database ?? "default";
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var scheme = parameters.UseSsl ? "https" : "http";
        var port = parameters.Port ?? ProtocolInfo.DefaultPort;
        var baseUrl = $"{scheme}://{parameters.Host}:{port}";

        _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _httpClient.DefaultRequestHeaders.Remove("X-TYPESENSE-API-KEY");
        _httpClient.DefaultRequestHeaders.Add("X-TYPESENSE-API-KEY", parameters.Password ?? "");

        // Verify connection
        using var response = await _httpClient.GetAsync("/health", ct);
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

        var searchParams = new Dictionary<string, string>
        {
            ["q"] = query,
            ["query_by"] = parameters?.GetValueOrDefault("query_by")?.ToString() ?? "*"
        };

        if (parameters != null)
        {
            foreach (var param in parameters)
            {
                if (param.Key != "query_by" && param.Value != null)
                    searchParams[param.Key] = param.Value.ToString()!;
            }
        }

        var queryString = string.Join("&", searchParams.Select(p =>
            $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

        using var response = await _httpClient.GetAsync($"/collections/{_collection}/documents/search?{queryString}", ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        if (result.TryGetProperty("hits", out var hits))
        {
            foreach (var hit in hits.EnumerateArray())
            {
                if (hit.TryGetProperty("document", out var doc))
                {
                    var row = new Dictionary<string, object?>();
                    foreach (var prop in doc.EnumerateObject())
                    {
                        row[prop.Name] = GetJsonValue(prop.Value);
                    }
                    rows.Add(row);
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

        var content = new StringContent(command, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync($"/collections/{_collection}/documents", content, ct);
        response.EnsureSuccessStatusCode();

        return new QueryResult { Success = true, RowsAffected = 1 };
    }

    /// <inheritdoc/>
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        throw new NotSupportedException("Typesense does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("Typesense does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("Typesense does not support transactions");
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
            using var response = await _httpClient.GetAsync("/health", ct);
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
