using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.CloudDW;

/// <summary>
/// Snowflake SQL REST API protocol strategy.
/// Uses Snowflake's REST API with OAuth2/Key-pair authentication.
/// </summary>
public sealed class SnowflakeProtocolStrategy : DatabaseProtocolStrategyBase
{
    private HttpClient? _httpClient;
    private string _accountUrl = "";
    private string _sessionToken = "";
    private string _database = "";
    private string _schema = "";
    private string _warehouse = "";
    private string _role = "";

    /// <inheritdoc/>
    public override string StrategyId => "snowflake-rest";

    /// <inheritdoc/>
    public override string StrategyName => "Snowflake REST API Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Snowflake SQL REST API",
        ProtocolVersion = "2",
        DefaultPort = 443,
        Family = ProtocolFamily.CloudDW,
        MaxPacketSize = 16 * 1024 * 1024,
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
                AuthenticationMethod.Token,
                AuthenticationMethod.OAuth2,
                AuthenticationMethod.Certificate
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var account = parameters.Host.Replace(".snowflakecomputing.com", "");
        _accountUrl = $"https://{account}.snowflakecomputing.com";

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_accountUrl),
            Timeout = TimeSpan.FromSeconds(parameters.CommandTimeout > 0 ? parameters.CommandTimeout : 300)
        };

        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "DataWarehouse/1.0");

        _database = parameters.Database ?? "";
        _schema = parameters.ExtendedProperties?.GetValueOrDefault("Schema")?.ToString() ?? "PUBLIC";
        _warehouse = parameters.ExtendedProperties?.GetValueOrDefault("Warehouse")?.ToString() ?? "COMPUTE_WH";
        _role = parameters.ExtendedProperties?.GetValueOrDefault("Role")?.ToString() ?? "";

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        var loginRequest = new
        {
            data = new
            {
                CLIENT_APP_ID = "DataWarehouse",
                CLIENT_APP_VERSION = "1.0",
                SVN_REVISION = "0",
                ACCOUNT_NAME = parameters.Host.Replace(".snowflakecomputing.com", ""),
                LOGIN_NAME = parameters.Username,
                PASSWORD = parameters.Password,
                CLIENT_ENVIRONMENT = new
                {
                    APPLICATION = "DataWarehouse",
                    OS = Environment.OSVersion.Platform.ToString(),
                    OS_VERSION = Environment.OSVersion.Version.ToString()
                }
            }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(loginRequest),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync("/session/v1/login-request", content, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseJson);

        if (doc.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("token", out var token))
        {
            _sessionToken = token.GetString() ?? "";
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Snowflake", $"Token=\"{_sessionToken}\"");
        }
        else if (doc.RootElement.TryGetProperty("message", out var message))
        {
            throw new Exception($"Snowflake login failed: {message.GetString()}");
        }
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        var queryRequest = new
        {
            sqlText = query,
            sequenceId = 1,
            database = _database,
            schema = _schema,
            warehouse = _warehouse,
            role = _role,
            parameters = new { }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(queryRequest),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync(
            "/queries/v1/query-request?requestId=" + Guid.NewGuid().ToString(),
            content,
            ct);

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseJson);

        if (!response.IsSuccessStatusCode ||
            (doc.RootElement.TryGetProperty("success", out var success) && !success.GetBoolean()))
        {
            var errorMessage = doc.RootElement.TryGetProperty("message", out var msg)
                ? msg.GetString() : "Unknown error";
            return new QueryResult
            {
                Success = false,
                ErrorMessage = errorMessage
            };
        }

        return ParseSnowflakeResult(doc.RootElement);
    }

    private static QueryResult ParseSnowflakeResult(JsonElement root)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        long rowsAffected = 0;

        if (root.TryGetProperty("data", out var data))
        {
            // Get column metadata
            var columns = new List<string>();
            if (data.TryGetProperty("rowtype", out var rowtype))
            {
                foreach (var col in rowtype.EnumerateArray())
                {
                    columns.Add(col.GetProperty("name").GetString() ?? "");
                }
            }

            // Get rows
            if (data.TryGetProperty("rowset", out var rowset))
            {
                foreach (var rowData in rowset.EnumerateArray())
                {
                    var row = new Dictionary<string, object?>();
                    int i = 0;
                    foreach (var value in rowData.EnumerateArray())
                    {
                        var colName = i < columns.Count ? columns[i] : $"col{i}";
                        row[colName] = GetJsonValue(value);
                        i++;
                    }
                    rows.Add(row);
                }
                rowsAffected = rows.Count;
            }

            // Get stats
            if (data.TryGetProperty("stats", out var stats) &&
                stats.TryGetProperty("numRowsInserted", out var inserted))
            {
                rowsAffected = inserted.GetInt64();
            }
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
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("BEGIN TRANSACTION", null, ct);
        return Guid.NewGuid().ToString("N");
    }

    /// <inheritdoc/>
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("COMMIT", null, ct);
    }

    /// <inheritdoc/>
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("ROLLBACK", null, ct);
    }

    /// <inheritdoc/>
    protected override Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        try
        {
            var result = await ExecuteQueryCoreAsync("SELECT 1", null, ct);
            return result.Success;
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

/// <summary>
/// Google BigQuery REST API protocol strategy.
/// </summary>
public sealed class BigQueryProtocolStrategy : DatabaseProtocolStrategyBase
{
    private HttpClient? _httpClient;
    private string _projectId = "";
    private string _datasetId = "";
    private string _location = "US";
    private string _accessToken = "";

    /// <inheritdoc/>
    public override string StrategyId => "bigquery-rest";

    /// <inheritdoc/>
    public override string StrategyName => "Google BigQuery REST API Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Google BigQuery REST API",
        ProtocolVersion = "v2",
        DefaultPort = 443,
        Family = ProtocolFamily.CloudDW,
        MaxPacketSize = 10 * 1024 * 1024,
        Capabilities = new ProtocolCapabilities
        {
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
                AuthenticationMethod.OAuth2,
                AuthenticationMethod.ServiceAccount
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://bigquery.googleapis.com"),
            Timeout = TimeSpan.FromSeconds(parameters.CommandTimeout > 0 ? parameters.CommandTimeout : 300)
        };

        _projectId = parameters.ExtendedProperties?.GetValueOrDefault("ProjectId")?.ToString()
            ?? parameters.Database ?? "";
        _datasetId = parameters.ExtendedProperties?.GetValueOrDefault("DatasetId")?.ToString() ?? "";
        _location = parameters.ExtendedProperties?.GetValueOrDefault("Location")?.ToString() ?? "US";

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        // Access token should be passed via parameters (typically obtained via OAuth2 flow)
        _accessToken = parameters.Password ?? "";

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _accessToken);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        var queryRequest = new
        {
            query,
            useLegacySql = false,
            location = _location,
            defaultDataset = !string.IsNullOrEmpty(_datasetId)
                ? new { projectId = _projectId, datasetId = _datasetId }
                : null
        };

        var content = new StringContent(
            JsonSerializer.Serialize(queryRequest),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync(
            $"/bigquery/v2/projects/{_projectId}/queries",
            content,
            ct);

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseJson);

        if (!response.IsSuccessStatusCode)
        {
            var errorMessage = doc.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var msg)
                    ? msg.GetString() : "Unknown error";
            return new QueryResult
            {
                Success = false,
                ErrorMessage = errorMessage
            };
        }

        return ParseBigQueryResult(doc.RootElement);
    }

    private static QueryResult ParseBigQueryResult(JsonElement root)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        long rowsAffected = 0;

        // Get schema
        var columns = new List<string>();
        if (root.TryGetProperty("schema", out var schema) &&
            schema.TryGetProperty("fields", out var fields))
        {
            foreach (var field in fields.EnumerateArray())
            {
                columns.Add(field.GetProperty("name").GetString() ?? "");
            }
        }

        // Get rows
        if (root.TryGetProperty("rows", out var rowsData))
        {
            foreach (var rowData in rowsData.EnumerateArray())
            {
                var row = new Dictionary<string, object?>();
                if (rowData.TryGetProperty("f", out var values))
                {
                    int i = 0;
                    foreach (var value in values.EnumerateArray())
                    {
                        var colName = i < columns.Count ? columns[i] : $"col{i}";
                        if (value.TryGetProperty("v", out var v))
                        {
                            row[colName] = GetJsonValue(v);
                        }
                        i++;
                    }
                }
                rows.Add(row);
            }
        }

        if (root.TryGetProperty("totalRows", out var total))
        {
            rowsAffected = long.Parse(total.GetString() ?? "0");
        }
        else
        {
            rowsAffected = rows.Count;
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
        throw new NotSupportedException("BigQuery does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("BigQuery does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("BigQuery does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        try
        {
            var result = await ExecuteQueryCoreAsync("SELECT 1", null, ct);
            return result.Success;
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

/// <summary>
/// Amazon Redshift protocol strategy (PostgreSQL wire protocol with Redshift extensions).
/// </summary>
public sealed class RedshiftProtocolStrategy : DatabaseProtocolStrategyBase
{
    // Redshift uses PostgreSQL protocol
    private const int ProtocolVersion3 = 196608;
    private int _processId;
    private int _secretKey;
    private string _serverVersion = "";

    /// <inheritdoc/>
    public override string StrategyId => "redshift-pgwire";

    /// <inheritdoc/>
    public override string StrategyName => "Amazon Redshift PostgreSQL Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Amazon Redshift PostgreSQL Wire Protocol",
        ProtocolVersion = "3.0",
        DefaultPort = 5439,
        Family = ProtocolFamily.CloudDW,
        MaxPacketSize = 64 * 1024 * 1024,
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
            SupportsMultiplexing = false,
            SupportsServerCursors = true,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods =
            [
                AuthenticationMethod.ClearText,
                AuthenticationMethod.MD5,
                AuthenticationMethod.IAM
            ]
        }
    };

    /// <inheritdoc/>
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var startupParams = new Dictionary<string, string>
        {
            ["user"] = parameters.Username ?? "awsuser",
            ["database"] = parameters.Database ?? "dev",
            ["client_encoding"] = "UTF8",
            ["application_name"] = "DataWarehouse"
        };

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var version = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(ProtocolVersion3);
        bw.Write(version);

        foreach (var param in startupParams)
        {
            bw.Write(Encoding.UTF8.GetBytes(param.Key));
            bw.Write((byte)0);
            bw.Write(Encoding.UTF8.GetBytes(param.Value));
            bw.Write((byte)0);
        }
        bw.Write((byte)0);

        var payload = ms.ToArray();
        var length = payload.Length + 4;

        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, length);
        await ActiveStream!.WriteAsync(header, ct);
        await ActiveStream.WriteAsync(payload, ct);
        await ActiveStream.FlushAsync(ct);
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        while (true)
        {
            var (type, data) = await ReadMessageAsync(ct);

            switch ((char)type)
            {
                case 'R':
                    await HandleAuthenticationAsync(data, parameters, ct);
                    break;

                case 'K':
                    _processId = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0));
                    _secretKey = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4));
                    break;

                case 'S':
                    ParseParameterStatus(data);
                    break;

                case 'Z':
                    return;

                case 'E':
                    throw new Exception($"Authentication failed: {ParseErrorResponse(data)}");
            }
        }
    }

    private async Task HandleAuthenticationAsync(byte[] data, ConnectionParameters parameters, CancellationToken ct)
    {
        var authType = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data);

        switch (authType)
        {
            case 0: return;

            case 3:
                await SendPasswordAsync(parameters.Password ?? "", ct);
                break;

            case 5:
                var salt = data[4..8];
                var hash = ComputeMd5Password(parameters.Username ?? "", parameters.Password ?? "", salt);
                await SendPasswordAsync(hash, ct);
                break;

            default:
                throw new NotSupportedException($"Unsupported authentication type: {authType}");
        }
    }

    private async Task SendPasswordAsync(string password, CancellationToken ct)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var length = passwordBytes.Length + 5;

        var buffer = new byte[1 + 4 + passwordBytes.Length + 1];
        buffer[0] = (byte)'p';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1), length);
        passwordBytes.CopyTo(buffer.AsSpan(5));
        buffer[^1] = 0;

        await ActiveStream!.WriteAsync(buffer, ct);
        await ActiveStream.FlushAsync(ct);
    }

    private static string ComputeMd5Password(string user, string password, byte[] salt)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var inner = md5.ComputeHash(Encoding.UTF8.GetBytes(password + user));
        var innerHex = Convert.ToHexString(inner).ToLowerInvariant();
        var outer = md5.ComputeHash(Encoding.UTF8.GetBytes(innerHex).Concat(salt).ToArray());
        return "md5" + Convert.ToHexString(outer).ToLowerInvariant();
    }

    private void ParseParameterStatus(byte[] data)
    {
        var parts = Encoding.UTF8.GetString(data).TrimEnd('\0').Split('\0');
        if (parts.Length >= 2 && parts[0] == "server_version")
            _serverVersion = parts[1];
    }

    private static string ParseErrorResponse(byte[] data)
    {
        var message = new StringBuilder();
        var pos = 0;
        while (pos < data.Length && data[pos] != 0)
        {
            var fieldType = (char)data[pos++];
            var end = Array.IndexOf(data, (byte)0, pos);
            if (end < 0) break;
            var value = Encoding.UTF8.GetString(data, pos, end - pos);
            pos = end + 1;

            if (fieldType == 'M')
                message.Append(value);
        }
        return message.ToString();
    }

    private async Task<(byte type, byte[] data)> ReadMessageAsync(CancellationToken ct)
    {
        var header = new byte[5];
        await ActiveStream!.ReadExactlyAsync(header, 0, 5, ct);

        var type = header[0];
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)) - 4;

        var data = new byte[length];
        if (length > 0)
            await ActiveStream.ReadExactlyAsync(data, 0, length, ct);

        return (type, data);
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        var queryBytes = Encoding.UTF8.GetBytes(query);
        var length = queryBytes.Length + 5;

        var buffer = new byte[1 + 4 + queryBytes.Length + 1];
        buffer[0] = (byte)'Q';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1), length);
        queryBytes.CopyTo(buffer.AsSpan(5));
        buffer[^1] = 0;

        await ActiveStream!.WriteAsync(buffer, ct);
        await ActiveStream.FlushAsync(ct);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var columns = new List<string>();
        long rowsAffected = 0;
        string? errorMessage = null;

        while (true)
        {
            var (type, data) = await ReadMessageAsync(ct);

            switch ((char)type)
            {
                case 'T':
                    columns = ParseRowDescription(data);
                    break;

                case 'D':
                    var row = ParseDataRow(data, columns);
                    rows.Add(row);
                    break;

                case 'C':
                    var tag = Encoding.UTF8.GetString(data).TrimEnd('\0');
                    rowsAffected = ParseCommandComplete(tag);
                    break;

                case 'E':
                    errorMessage = ParseErrorResponse(data);
                    break;

                case 'Z':
                    if (errorMessage != null)
                    {
                        return new QueryResult
                        {
                            Success = false,
                            ErrorMessage = errorMessage
                        };
                    }
                    return new QueryResult
                    {
                        Success = true,
                        RowsAffected = rowsAffected,
                        Rows = rows
                    };

                case 'I':
                    break;
            }
        }
    }

    private static List<string> ParseRowDescription(byte[] data)
    {
        var columns = new List<string>();
        var fieldCount = System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(data);
        var pos = 2;

        for (int i = 0; i < fieldCount; i++)
        {
            var nameEnd = Array.IndexOf(data, (byte)0, pos);
            var name = Encoding.UTF8.GetString(data, pos, nameEnd - pos);
            columns.Add(name);
            pos = nameEnd + 1 + 18;
        }

        return columns;
    }

    private static Dictionary<string, object?> ParseDataRow(byte[] data, List<string> columns)
    {
        var row = new Dictionary<string, object?>();
        var fieldCount = System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(data);
        var pos = 2;

        for (int i = 0; i < fieldCount && i < columns.Count; i++)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
            pos += 4;

            if (length == -1)
            {
                row[columns[i]] = null;
            }
            else
            {
                var value = Encoding.UTF8.GetString(data, pos, length);
                row[columns[i]] = value;
                pos += length;
            }
        }

        return row;
    }

    private static long ParseCommandComplete(string tag)
    {
        var parts = tag.Split(' ');
        if (parts.Length >= 2 && long.TryParse(parts[^1], out var count))
            return count;
        return 0;
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
        await ExecuteQueryCoreAsync("BEGIN", null, ct);
        return Guid.NewGuid().ToString("N");
    }

    /// <inheritdoc/>
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("COMMIT", null, ct);
    }

    /// <inheritdoc/>
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("ROLLBACK", null, ct);
    }

    /// <inheritdoc/>
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        var buffer = new byte[] { (byte)'X', 0, 0, 0, 4 };
        await ActiveStream!.WriteAsync(buffer, ct);
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        try
        {
            var result = await ExecuteQueryCoreAsync("SELECT 1", null, ct);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Databricks SQL protocol strategy.
/// Uses Databricks SQL Connector (Thrift-based) protocol.
/// </summary>
public sealed class DatabricksProtocolStrategy : DatabaseProtocolStrategyBase
{
    private HttpClient? _httpClient;
    private string _host = "";
    private string _httpPath = "";
    private string _catalog = "";
    private string _schema = "";

    /// <inheritdoc/>
    public override string StrategyId => "databricks-sql";

    /// <inheritdoc/>
    public override string StrategyName => "Databricks SQL Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Databricks SQL REST API",
        ProtocolVersion = "2.0",
        DefaultPort = 443,
        Family = ProtocolFamily.CloudDW,
        MaxPacketSize = 100 * 1024 * 1024,
        Capabilities = new ProtocolCapabilities
        {
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
                AuthenticationMethod.OAuth2
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        _host = parameters.Host;
        _httpPath = parameters.ExtendedProperties?.GetValueOrDefault("HttpPath")?.ToString() ?? "/sql/1.0/warehouses";
        _catalog = parameters.Database ?? "hive_metastore";
        _schema = parameters.ExtendedProperties?.GetValueOrDefault("Schema")?.ToString() ?? "default";

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"https://{_host}"),
            Timeout = TimeSpan.FromSeconds(parameters.CommandTimeout > 0 ? parameters.CommandTimeout : 300)
        };

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", parameters.Password);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        var request = new
        {
            statement = query,
            warehouse_id = _httpPath.Split('/').LastOrDefault(),
            catalog = _catalog,
            schema = _schema,
            wait_timeout = "30s"
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync("/api/2.0/sql/statements", content, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseJson);

        if (!response.IsSuccessStatusCode)
        {
            var errorMessage = doc.RootElement.TryGetProperty("message", out var msg)
                ? msg.GetString() : "Unknown error";
            return new QueryResult
            {
                Success = false,
                ErrorMessage = errorMessage
            };
        }

        return ParseDatabricksResult(doc.RootElement);
    }

    private static QueryResult ParseDatabricksResult(JsonElement root)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        long rowsAffected = 0;

        // Get column schema
        var columns = new List<string>();
        if (root.TryGetProperty("manifest", out var manifest) &&
            manifest.TryGetProperty("schema", out var schema) &&
            schema.TryGetProperty("columns", out var cols))
        {
            foreach (var col in cols.EnumerateArray())
            {
                columns.Add(col.GetProperty("name").GetString() ?? "");
            }
        }

        // Get result data
        if (root.TryGetProperty("result", out var result) &&
            result.TryGetProperty("data_array", out var dataArray))
        {
            foreach (var rowData in dataArray.EnumerateArray())
            {
                var row = new Dictionary<string, object?>();
                int i = 0;
                foreach (var value in rowData.EnumerateArray())
                {
                    var colName = i < columns.Count ? columns[i] : $"col{i}";
                    row[colName] = value.ValueKind == JsonValueKind.Null ? null : value.ToString();
                    i++;
                }
                rows.Add(row);
            }
            rowsAffected = rows.Count;
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
        throw new NotSupportedException("Databricks does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("Databricks does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("Databricks does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        try
        {
            var result = await ExecuteQueryCoreAsync("SELECT 1", null, ct);
            return result.Success;
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

/// <summary>
/// Azure Synapse Analytics protocol strategy (TDS-based).
/// </summary>
public sealed class SynapseProtocolStrategy : DatabaseProtocolStrategyBase
{
    // Uses TDS protocol (same as SQL Server)
    private const byte TdsPreLogin = 18;
    private const byte TdsLogin7 = 16;
    private const byte TdsSqlBatch = 1;

    private byte _tdsVersion = 0x74; // TDS 7.4
    private string _serverVersion = "";
    private uint _packetSize = 4096;

    /// <inheritdoc/>
    public override string StrategyId => "synapse-tds";

    /// <inheritdoc/>
    public override string StrategyName => "Azure Synapse Analytics TDS Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Azure Synapse TDS Protocol",
        ProtocolVersion = "7.4",
        DefaultPort = 1433,
        Family = ProtocolFamily.CloudDW,
        MaxPacketSize = 32767,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = true,
            SupportsPreparedStatements = true,
            SupportsCursors = true,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = true,
            SupportsCompression = false,
            SupportsMultiplexing = false,
            SupportsServerCursors = true,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods =
            [
                AuthenticationMethod.ClearText,
                AuthenticationMethod.ActiveDirectory,
                AuthenticationMethod.ManagedIdentity
            ]
        }
    };

    /// <inheritdoc/>
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Send PreLogin
        var preLoginRequest = BuildPreLoginPacket();
        await SendTdsPacketAsync(TdsPreLogin, preLoginRequest, ct);

        // Read PreLogin response
        var (_, preLoginResponse) = await ReadTdsPacketAsync(ct);
        ParsePreLoginResponse(preLoginResponse);
    }

    private byte[] BuildPreLoginPacket()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Version token
        bw.Write((byte)0); // Token type
        bw.Write((ushort)0x1A); // Offset
        bw.Write((ushort)6); // Length

        // Encryption token
        bw.Write((byte)1);
        bw.Write((ushort)0x20);
        bw.Write((ushort)1);

        // MARS token
        bw.Write((byte)3);
        bw.Write((ushort)0x21);
        bw.Write((ushort)1);

        // Terminator
        bw.Write((byte)0xFF);

        // Version data
        bw.Write((uint)0x0E000A00); // Version 14.0.10
        bw.Write((ushort)0);

        // Encryption
        bw.Write((byte)0x02); // Encrypt if supported

        // MARS
        bw.Write((byte)0);

        return ms.ToArray();
    }

    private void ParsePreLoginResponse(byte[] data)
    {
        // Parse version from response
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var loginPacket = BuildLogin7Packet(parameters);
        await SendTdsPacketAsync(TdsLogin7, loginPacket, ct);

        // Read response
        var (_, response) = await ReadTdsPacketAsync(ct);

        // Parse response tokens
        var pos = 0;
        while (pos < response.Length)
        {
            var token = response[pos++];
            switch (token)
            {
                case 0xAA: // Error
                    var errorLength = BitConverter.ToUInt16(response, pos);
                    pos += 2;
                    var errorData = response[pos..(pos + errorLength)];
                    pos += errorLength;
                    throw new Exception($"Login failed: {ParseErrorToken(errorData)}");

                case 0xAD: // LoginAck
                    var ackLength = BitConverter.ToUInt16(response, pos);
                    pos += 2;
                    pos += ackLength;
                    break;

                case 0xE3: // EnvChange
                    var envLength = BitConverter.ToUInt16(response, pos);
                    pos += 2;
                    pos += envLength;
                    break;

                case 0xFD: // Done
                    return;

                default:
                    // Skip unknown token
                    if (pos + 1 < response.Length)
                    {
                        var len = BitConverter.ToUInt16(response, pos);
                        pos += 2 + len;
                    }
                    else
                    {
                        return;
                    }
                    break;
            }
        }
    }

    private byte[] BuildLogin7Packet(ConnectionParameters parameters)
    {
        var hostname = Environment.MachineName;
        var username = parameters.Username ?? "";
        var password = parameters.Password ?? "";
        var appName = "DataWarehouse";
        var serverName = parameters.Host;
        var database = parameters.Database ?? "master";

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Calculate offsets
        var fixedLength = 94;
        var offset = fixedLength;

        var hostnameBytes = Encoding.Unicode.GetBytes(hostname);
        var usernameBytes = Encoding.Unicode.GetBytes(username);
        var passwordBytes = EncryptPassword(password);
        var appNameBytes = Encoding.Unicode.GetBytes(appName);
        var serverNameBytes = Encoding.Unicode.GetBytes(serverName);
        var databaseBytes = Encoding.Unicode.GetBytes(database);

        var totalLength = fixedLength +
            hostnameBytes.Length + usernameBytes.Length + passwordBytes.Length +
            appNameBytes.Length + serverNameBytes.Length + databaseBytes.Length;

        // Fixed part
        bw.Write(totalLength); // Length
        bw.Write((uint)0x74000004); // TDS version
        bw.Write(_packetSize);
        bw.Write((uint)0x0E000000); // Client version
        bw.Write((uint)Environment.ProcessId);
        bw.Write((uint)0); // Connection ID

        // Option flags
        bw.Write((byte)0xE0); // OptionFlags1
        bw.Write((byte)0x03); // OptionFlags2
        bw.Write((byte)0); // TypeFlags
        bw.Write((byte)0); // OptionFlags3

        bw.Write((int)0); // ClientTimeZone
        bw.Write((uint)0x0409); // ClientLCID

        // Offsets and lengths
        bw.Write((ushort)offset); // HostName offset
        bw.Write((ushort)(hostnameBytes.Length / 2));
        offset += hostnameBytes.Length;

        bw.Write((ushort)offset); // UserName offset
        bw.Write((ushort)(usernameBytes.Length / 2));
        offset += usernameBytes.Length;

        bw.Write((ushort)offset); // Password offset
        bw.Write((ushort)(passwordBytes.Length / 2));
        offset += passwordBytes.Length;

        bw.Write((ushort)offset); // AppName offset
        bw.Write((ushort)(appNameBytes.Length / 2));
        offset += appNameBytes.Length;

        bw.Write((ushort)offset); // ServerName offset
        bw.Write((ushort)(serverNameBytes.Length / 2));
        offset += serverNameBytes.Length;

        bw.Write((ushort)0); // Extension offset
        bw.Write((ushort)0);

        bw.Write((ushort)0); // CltIntName offset
        bw.Write((ushort)0);

        bw.Write((ushort)0); // Language offset
        bw.Write((ushort)0);

        bw.Write((ushort)offset); // Database offset
        bw.Write((ushort)(databaseBytes.Length / 2));

        bw.Write(Guid.Empty.ToByteArray()); // ClientID

        bw.Write((ushort)0); // SSPI offset
        bw.Write((ushort)0);

        bw.Write((ushort)0); // AtchDBFile offset
        bw.Write((ushort)0);

        bw.Write((ushort)0); // ChangePassword offset
        bw.Write((ushort)0);

        bw.Write((uint)0); // SSPILong

        // Variable part
        bw.Write(hostnameBytes);
        bw.Write(usernameBytes);
        bw.Write(passwordBytes);
        bw.Write(appNameBytes);
        bw.Write(serverNameBytes);
        bw.Write(databaseBytes);

        return ms.ToArray();
    }

    private static byte[] EncryptPassword(string password)
    {
        var bytes = Encoding.Unicode.GetBytes(password);
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(((bytes[i] & 0x0F) << 4) | ((bytes[i] & 0xF0) >> 4));
            bytes[i] ^= 0xA5;
        }
        return bytes;
    }

    private static string ParseErrorToken(byte[] data)
    {
        if (data.Length < 6) return "Unknown error";

        var msgLength = BitConverter.ToUInt16(data, 4);
        if (data.Length < 6 + msgLength * 2) return "Unknown error";

        return Encoding.Unicode.GetString(data, 6, msgLength * 2);
    }

    private async Task SendTdsPacketAsync(byte packetType, byte[] data, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var packetLength = (ushort)(8 + data.Length);

        bw.Write(packetType);
        bw.Write((byte)1); // Status: EOM
        bw.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(packetLength));
        bw.Write((ushort)0); // SPID
        bw.Write((byte)1); // Packet ID
        bw.Write((byte)0); // Window

        bw.Write(data);

        await ActiveStream!.WriteAsync(ms.ToArray(), ct);
        await ActiveStream.FlushAsync(ct);
    }

    private async Task<(byte type, byte[] data)> ReadTdsPacketAsync(CancellationToken ct)
    {
        var header = new byte[8];
        await ActiveStream!.ReadExactlyAsync(header, 0, 8, ct);

        var type = header[0];
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) - 8;

        var data = new byte[length];
        if (length > 0)
            await ActiveStream.ReadExactlyAsync(data, 0, length, ct);

        return (type, data);
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        // Build SQL batch
        var unicodeQuery = Encoding.Unicode.GetBytes(query);

        // Headers
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var totalHeadersLength = 22;
        bw.Write(totalHeadersLength); // Total length

        // Transaction descriptor header
        bw.Write((ushort)18); // Header length
        bw.Write((ushort)2); // Header type
        bw.Write((ulong)0); // Transaction descriptor
        bw.Write((uint)1); // Outstanding request count

        bw.Write(unicodeQuery);

        var batchData = ms.ToArray();
        await SendTdsPacketAsync(TdsSqlBatch, batchData, ct);

        // Read response
        var (_, response) = await ReadTdsPacketAsync(ct);

        return ParseSqlResponse(response);
    }

    private static QueryResult ParseSqlResponse(byte[] data)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var columns = new List<string>();
        long rowsAffected = 0;
        string? errorMessage = null;

        var pos = 0;
        while (pos < data.Length)
        {
            var token = data[pos++];

            switch (token)
            {
                case 0x81: // COLMETADATA
                    (columns, pos) = ParseColMetadata(data, pos);
                    break;

                case 0xD1: // ROW
                    var (row, newPos) = ParseRow(data, pos, columns);
                    rows.Add(row);
                    pos = newPos;
                    break;

                case 0xAA: // ERROR
                    var errLen = BitConverter.ToUInt16(data, pos);
                    pos += 2;
                    errorMessage = ParseErrorToken(data[pos..(pos + errLen)]);
                    pos += errLen;
                    break;

                case 0xFD: // DONE
                case 0xFE: // DONEPROC
                case 0xFF: // DONEINPROC
                    if (pos + 7 < data.Length)
                    {
                        pos += 2; // Status
                        pos += 2; // CurCmd
                        rowsAffected = BitConverter.ToInt64(data, pos);
                        pos += 8;
                    }
                    else
                    {
                        pos = data.Length;
                    }
                    break;

                default:
                    // Skip unknown
                    pos = data.Length;
                    break;
            }
        }

        if (errorMessage != null)
        {
            return new QueryResult
            {
                Success = false,
                ErrorMessage = errorMessage
            };
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = rowsAffected > 0 ? rowsAffected : rows.Count,
            Rows = rows
        };
    }

    private static (List<string>, int) ParseColMetadata(byte[] data, int pos)
    {
        var columns = new List<string>();
        var count = BitConverter.ToUInt16(data, pos);
        pos += 2;

        for (int i = 0; i < count && pos < data.Length; i++)
        {
            pos += 8; // UserType, Flags, Type, etc.

            var colType = data[pos - 1];

            // Skip type-specific info
            pos += GetTypeInfoLength(colType);

            var nameLen = data[pos++];
            var name = Encoding.Unicode.GetString(data, pos, nameLen * 2);
            columns.Add(name);
            pos += nameLen * 2;
        }

        return (columns, pos);
    }

    private static int GetTypeInfoLength(byte colType)
    {
        return colType switch
        {
            0x26 => 1, // INT4
            0x24 => 16, // GUID
            0x6D => 1, // FLT8
            0xA7 or 0xE7 => 7, // VARCHAR/NVARCHAR
            0xA5 or 0xAD => 2, // VARBINARY
            _ => 0
        };
    }

    private static (Dictionary<string, object?>, int) ParseRow(byte[] data, int pos, List<string> columns)
    {
        var row = new Dictionary<string, object?>();

        for (int i = 0; i < columns.Count && pos < data.Length; i++)
        {
            // Simplified - actual parsing depends on column type
            var len = data[pos++];
            if (len == 0xFF)
            {
                row[columns[i]] = null;
            }
            else if (pos + len <= data.Length)
            {
                var value = Encoding.UTF8.GetString(data, pos, len);
                row[columns[i]] = value;
                pos += len;
            }
        }

        return (row, pos);
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
        await ExecuteQueryCoreAsync("BEGIN TRANSACTION", null, ct);
        return Guid.NewGuid().ToString("N");
    }

    /// <inheritdoc/>
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("COMMIT TRANSACTION", null, ct);
    }

    /// <inheritdoc/>
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("ROLLBACK TRANSACTION", null, ct);
    }

    /// <inheritdoc/>
    protected override Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        try
        {
            var result = await ExecuteQueryCoreAsync("SELECT 1", null, ct);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }
}
