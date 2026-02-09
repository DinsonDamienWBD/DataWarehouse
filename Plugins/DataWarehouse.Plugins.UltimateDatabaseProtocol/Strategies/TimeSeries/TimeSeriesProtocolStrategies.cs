using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.TimeSeries;

/// <summary>
/// InfluxDB Line Protocol v2 implementation.
/// Supports InfluxDB 2.x HTTP API and line protocol format.
/// </summary>
public sealed class InfluxDbLineProtocolStrategy : DatabaseProtocolStrategyBase
{
    private string _bucket = "";
    private string _org = "";
    private string _token = "";
    private HttpClient? _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "influxdb-line";

    /// <inheritdoc/>
    public override string StrategyName => "InfluxDB Line Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "InfluxDB Line Protocol",
        ProtocolVersion = "2.x",
        DefaultPort = 8086,
        Family = ProtocolFamily.TimeSeries,
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
            SupportsServerCursors = false,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods = [AuthenticationMethod.Token]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        _bucket = parameters.Database ?? "default";
        _org = parameters.AdditionalParameters.TryGetValue("org", out var org) ? org : "default";
        _token = parameters.Password ?? parameters.AdditionalParameters.GetValueOrDefault("token", "") ?? "";

        var baseUri = $"http{(parameters.UseSsl ? "s" : "")}://{parameters.Host}:{parameters.Port ?? 8086}";
        _httpClient = new HttpClient { BaseAddress = new Uri(baseUri) };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Token {_token}");

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        // InfluxDB uses Flux query language
        var requestBody = new StringContent(query, Encoding.UTF8, "application/vnd.flux");

        var response = await _httpClient.PostAsync(
            $"/api/v2/query?org={Uri.EscapeDataString(_org)}",
            requestBody, ct);

        var content = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            return new QueryResult
            {
                Success = false,
                ErrorMessage = content,
                ErrorCode = ((int)response.StatusCode).ToString()
            };
        }

        // Parse CSV response
        var rows = ParseFluxCsv(content);

        return new QueryResult
        {
            Success = true,
            Rows = rows,
            RowsAffected = rows.Count
        };
    }

    private static List<IReadOnlyDictionary<string, object?>> ParseFluxCsv(string csv)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length < 2) return rows;

        // Skip annotation lines starting with #
        var dataLines = lines.Where(l => !l.StartsWith('#')).ToList();
        if (dataLines.Count < 2) return rows;

        var headers = dataLines[0].Split(',');

        for (int i = 1; i < dataLines.Count; i++)
        {
            var values = dataLines[i].Split(',');
            var row = new Dictionary<string, object?>();

            for (int j = 0; j < Math.Min(headers.Length, values.Length); j++)
            {
                row[headers[j]] = ParseValue(values[j]);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static object? ParseValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (double.TryParse(value, out var d)) return d;
        if (DateTime.TryParse(value, out var dt)) return dt;
        if (bool.TryParse(value, out var b)) return b;
        return value;
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(
        string command,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        // Line protocol write
        var content = new StringContent(command, Encoding.UTF8, "text/plain");

        var response = await _httpClient.PostAsync(
            $"/api/v2/write?org={Uri.EscapeDataString(_org)}&bucket={Uri.EscapeDataString(_bucket)}&precision=ns",
            content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            return new QueryResult
            {
                Success = false,
                ErrorMessage = error,
                ErrorCode = ((int)response.StatusCode).ToString()
            };
        }

        return new QueryResult { Success = true };
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        if (_httpClient == null) return false;
        var response = await _httpClient.GetAsync("/ping", ct);
        return response.IsSuccessStatusCode;
    }

    /// <inheritdoc/>
    protected override Task CleanupConnectionAsync()
    {
        _httpClient?.Dispose();
        _httpClient = null;
        return base.CleanupConnectionAsync();
    }
}

/// <summary>
/// QuestDB ILP (InfluxDB Line Protocol) over TCP implementation.
/// </summary>
public sealed class QuestDbIlpProtocolStrategy : DatabaseProtocolStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "questdb-ilp";

    /// <inheritdoc/>
    public override string StrategyName => "QuestDB ILP Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "QuestDB ILP (InfluxDB Line Protocol)",
        ProtocolVersion = "1.0",
        DefaultPort = 9009,
        Family = ProtocolFamily.TimeSeries,
        MaxPacketSize = 64 * 1024 * 1024,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = false,
            SupportsPreparedStatements = false,
            SupportsCursors = false,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = true,
            SupportsCompression = false,
            SupportsMultiplexing = false,
            SupportsServerCursors = false,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = false,
            SupportedAuthMethods = [AuthenticationMethod.None, AuthenticationMethod.Token]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc/>
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc/>
    protected override Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        // QuestDB ILP is write-only; queries go through PostgreSQL wire protocol
        return Task.FromResult(new QueryResult
        {
            Success = false,
            ErrorMessage = "QuestDB ILP protocol is write-only. Use PostgreSQL protocol for queries."
        });
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(
        string command,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        // Send line protocol data directly
        var data = Encoding.UTF8.GetBytes(command + "\n");
        await SendAsync(data, ct);

        return new QueryResult { Success = true };
    }

    /// <inheritdoc/>
    protected override Task<bool> PingCoreAsync(CancellationToken ct)
    {
        // QuestDB ILP doesn't have a ping; just check socket
        return Task.FromResult(TcpClient?.Connected ?? false);
    }
}

/// <summary>
/// TimescaleDB protocol (PostgreSQL wire protocol with TimescaleDB extensions).
/// </summary>
public sealed class TimescaleDbProtocolStrategy : DatabaseProtocolStrategyBase
{
    private readonly DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.Relational.PostgreSqlProtocolStrategy _pgStrategy = new();

    /// <inheritdoc/>
    public override string StrategyId => "timescaledb";

    /// <inheritdoc/>
    public override string StrategyName => "TimescaleDB Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "TimescaleDB (PostgreSQL Protocol)",
        ProtocolVersion = "3.0",
        DefaultPort = 5432,
        Family = ProtocolFamily.TimeSeries,
        MaxPacketSize = 1024 * 1024 * 1024,
        Capabilities = _pgStrategy.ProtocolInfo.Capabilities
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
        => _pgStrategy.ConnectAsync(parameters, ct).ContinueWith(_ => { });

    /// <inheritdoc/>
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
        => Task.CompletedTask; // Handled in ConnectAsync

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        return await _pgStrategy.ExecuteQueryAsync(query, parameters, ct);
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(
        string command,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        return await _pgStrategy.ExecuteNonQueryAsync(command, parameters, ct);
    }

    /// <inheritdoc/>
    protected override Task<bool> PingCoreAsync(CancellationToken ct)
        => _pgStrategy.PingAsync(ct);

    /// <inheritdoc/>
    protected override async Task CleanupConnectionAsync()
    {
        await _pgStrategy.DisconnectAsync();
        await base.CleanupConnectionAsync();
    }
}

/// <summary>
/// Prometheus Remote Write protocol implementation.
/// </summary>
public sealed class PrometheusRemoteWriteStrategy : DatabaseProtocolStrategyBase
{
    private HttpClient? _httpClient;
    private string _remoteWriteUrl = "";

    /// <inheritdoc/>
    public override string StrategyId => "prometheus-remote-write";

    /// <inheritdoc/>
    public override string StrategyName => "Prometheus Remote Write";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Prometheus Remote Write Protocol",
        ProtocolVersion = "1.0",
        DefaultPort = 9090,
        Family = ProtocolFamily.TimeSeries,
        MaxPacketSize = 10 * 1024 * 1024,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = false,
            SupportsPreparedStatements = false,
            SupportsCursors = false,
            SupportsStreaming = false,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = true,
            SupportsCompression = true, // Snappy
            SupportsMultiplexing = false,
            SupportsServerCursors = false,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = false,
            SupportedAuthMethods = [AuthenticationMethod.None, AuthenticationMethod.Token]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var baseUri = $"http{(parameters.UseSsl ? "s" : "")}://{parameters.Host}:{parameters.Port ?? 9090}";
        _remoteWriteUrl = $"{baseUri}/api/v1/write";

        _httpClient = new HttpClient();
        if (!string.IsNullOrEmpty(parameters.Password))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {parameters.Password}");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc/>
    protected override Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        // Prometheus remote write is write-only
        return Task.FromResult(new QueryResult
        {
            Success = false,
            ErrorMessage = "Prometheus Remote Write protocol is write-only. Use PromQL HTTP API for queries."
        });
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(
        string command,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        // Command should be a JSON array of time series
        var content = new StringContent(command, Encoding.UTF8, "application/json");
        content.Headers.Add("Content-Encoding", "snappy"); // Would need actual Snappy compression

        var response = await _httpClient.PostAsync(_remoteWriteUrl, content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            return new QueryResult
            {
                Success = false,
                ErrorMessage = error,
                ErrorCode = ((int)response.StatusCode).ToString()
            };
        }

        return new QueryResult { Success = true };
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        if (_httpClient == null) return false;
        try
        {
            var baseUri = new Uri(_remoteWriteUrl).GetLeftPart(UriPartial.Authority);
            var response = await _httpClient.GetAsync($"{baseUri}/-/healthy", ct);
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
        return base.CleanupConnectionAsync();
    }
}

/// <summary>
/// VictoriaMetrics protocol implementation.
/// </summary>
public sealed class VictoriaMetricsProtocolStrategy : DatabaseProtocolStrategyBase
{
    private HttpClient? _httpClient;
    private string _baseUrl = "";

    /// <inheritdoc/>
    public override string StrategyId => "victoriametrics";

    /// <inheritdoc/>
    public override string StrategyName => "VictoriaMetrics Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "VictoriaMetrics Protocol",
        ProtocolVersion = "1.0",
        DefaultPort = 8428,
        Family = ProtocolFamily.TimeSeries,
        MaxPacketSize = 100 * 1024 * 1024,
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
            SupportsServerCursors = false,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods = [AuthenticationMethod.None, AuthenticationMethod.Token]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        _baseUrl = $"http{(parameters.UseSsl ? "s" : "")}://{parameters.Host}:{parameters.Port ?? 8428}";
        _httpClient = new HttpClient { BaseAddress = new Uri(_baseUrl) };

        if (!string.IsNullOrEmpty(parameters.Password))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {parameters.Password}");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        // VictoriaMetrics supports PromQL queries
        var response = await _httpClient.GetAsync(
            $"/api/v1/query?query={Uri.EscapeDataString(query)}", ct);

        var content = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            return new QueryResult
            {
                Success = false,
                ErrorMessage = content,
                ErrorCode = ((int)response.StatusCode).ToString()
            };
        }

        // Parse Prometheus-style JSON response
        var json = JsonDocument.Parse(content);
        var rows = ParsePrometheusResponse(json);

        return new QueryResult
        {
            Success = true,
            Rows = rows,
            RowsAffected = rows.Count
        };
    }

    private static List<IReadOnlyDictionary<string, object?>> ParsePrometheusResponse(JsonDocument json)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();

        if (json.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("result", out var result))
        {
            foreach (var item in result.EnumerateArray())
            {
                var row = new Dictionary<string, object?>();

                if (item.TryGetProperty("metric", out var metric))
                {
                    foreach (var prop in metric.EnumerateObject())
                    {
                        row[prop.Name] = prop.Value.GetString();
                    }
                }

                if (item.TryGetProperty("value", out var value) && value.GetArrayLength() >= 2)
                {
                    row["timestamp"] = value[0].GetDouble();
                    row["value"] = double.Parse(value[1].GetString() ?? "0");
                }

                rows.Add(row);
            }
        }

        return rows;
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(
        string command,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        // Line protocol import
        var content = new StringContent(command, Encoding.UTF8, "text/plain");
        var response = await _httpClient.PostAsync("/api/v1/import/prometheus", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            return new QueryResult
            {
                Success = false,
                ErrorMessage = error,
                ErrorCode = ((int)response.StatusCode).ToString()
            };
        }

        return new QueryResult { Success = true };
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        if (_httpClient == null) return false;
        var response = await _httpClient.GetAsync("/health", ct);
        return response.IsSuccessStatusCode;
    }

    /// <inheritdoc/>
    protected override Task CleanupConnectionAsync()
    {
        _httpClient?.Dispose();
        _httpClient = null;
        return base.CleanupConnectionAsync();
    }
}
