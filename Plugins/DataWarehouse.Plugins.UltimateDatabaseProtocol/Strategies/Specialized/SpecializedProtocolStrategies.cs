using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.Specialized;

/// <summary>
/// ClickHouse native TCP protocol strategy.
/// High-performance columnar database protocol.
/// </summary>
public sealed class ClickHouseProtocolStrategy : DatabaseProtocolStrategyBase
{
    private const byte ClientHello = 0;
    private const byte ClientQuery = 1;
    private const byte ClientData = 2;
    private const byte ClientCancel = 3;
    private const byte ClientPing = 4;

    private const byte ServerHello = 0;
    private const byte ServerData = 1;
    private const byte ServerException = 2;
    private const byte ServerProgress = 3;
    private const byte ServerPong = 4;
    private const byte ServerEndOfStream = 5;

    private string _serverVersion = "";
    private string _serverTimezone = "";

    /// <inheritdoc/>
    public override string StrategyId => "clickhouse-native";

    /// <inheritdoc/>
    public override string StrategyName => "ClickHouse Native Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "ClickHouse Native TCP Protocol",
        ProtocolVersion = "54450",
        DefaultPort = 9000,
        Family = ProtocolFamily.Specialized,
        MaxPacketSize = 1024 * 1024 * 1024,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = false,
            SupportsPreparedStatements = true,
            SupportsCursors = false,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = true,
            SupportsCompression = true,
            SupportsMultiplexing = false,
            SupportsServerCursors = false,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods =
            [
                AuthenticationMethod.ClearText,
                AuthenticationMethod.SHA256
            ]
        }
    };

    /// <inheritdoc/>
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Send client hello
        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        WriteVarUInt(bw, ClientHello);
        WriteString(bw, "DataWarehouse"); // Client name
        WriteVarUInt(bw, 1); // Version major
        WriteVarUInt(bw, 0); // Version minor
        WriteVarUInt(bw, 54450); // Client revision
        WriteString(bw, parameters.Database ?? "default");
        WriteString(bw, parameters.Username ?? "default");
        WriteString(bw, parameters.Password ?? "");

        await ActiveStream!.WriteAsync(ms.ToArray(), ct);
        await ActiveStream.FlushAsync(ct);

        // Read server hello
        var packetType = await ReadVarUIntAsync(ct);
        if (packetType == ServerException)
        {
            throw new Exception(await ReadExceptionAsync(ct));
        }

        if (packetType == ServerHello)
        {
            var serverName = await ReadStringAsync(ct);
            var serverMajor = await ReadVarUIntAsync(ct);
            var serverMinor = await ReadVarUIntAsync(ct);
            var serverRevision = await ReadVarUIntAsync(ct);

            if (serverRevision >= 54423)
            {
                _serverTimezone = await ReadStringAsync(ct);
            }
            if (serverRevision >= 54410)
            {
                var displayName = await ReadStringAsync(ct);
            }

            _serverVersion = $"{serverMajor}.{serverMinor}.{serverRevision}";
        }
    }

    /// <inheritdoc/>
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Authentication happens during handshake
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        // Send query
        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        WriteVarUInt(bw, ClientQuery);
        WriteString(bw, Guid.NewGuid().ToString()); // Query ID
        bw.Write((byte)1); // Initial query
        WriteString(bw, ""); // Initial user
        WriteString(bw, ""); // Initial query ID
        WriteString(bw, "0.0.0.0:0"); // Initial address
        WriteVarUInt(bw, 1); // Interface: TCP
        WriteString(bw, Environment.MachineName);
        WriteString(bw, Environment.OSVersion.Platform.ToString());
        WriteString(bw, "DataWarehouse");
        WriteString(bw, "1.0");
        WriteVarUInt(bw, 0); // HTTP method (not used for TCP)
        WriteString(bw, ""); // HTTP user agent
        WriteString(bw, ""); // Quota key
        WriteVarUInt(bw, 0); // Distributed depth

        // Client settings
        WriteString(bw, "");

        // Query state
        WriteVarUInt(bw, 2); // State: Complete

        // Compression
        bw.Write((byte)0);

        // Query text
        WriteString(bw, query);

        await ActiveStream!.WriteAsync(ms.ToArray(), ct);

        // Send empty data block to signal end of data
        using var dataMs = new MemoryStream(4096);
        using var dataBw = new BinaryWriter(dataMs);
        WriteVarUInt(dataBw, ClientData);
        WriteString(dataBw, ""); // Temporary table name
        // Empty block info
        WriteVarUInt(dataBw, 0); // Columns
        WriteVarUInt(dataBw, 0); // Rows

        await ActiveStream.WriteAsync(dataMs.ToArray(), ct);
        await ActiveStream.FlushAsync(ct);

        // Read response
        var rows = new List<IReadOnlyDictionary<string, object?>>();

        while (true)
        {
            var packetType = await ReadVarUIntAsync(ct);

            switch (packetType)
            {
                case ServerData:
                    var blockRows = await ReadDataBlockAsync(ct);
                    rows.AddRange(blockRows);
                    break;

                case ServerException:
                    var error = await ReadExceptionAsync(ct);
                    return new QueryResult
                    {
                        Success = false,
                        ErrorMessage = error
                    };

                case ServerProgress:
                    // Read progress
                    await ReadVarUIntAsync(ct); // Rows
                    await ReadVarUIntAsync(ct); // Bytes
                    await ReadVarUIntAsync(ct); // Total rows
                    break;

                case ServerEndOfStream:
                    return new QueryResult
                    {
                        Success = true,
                        RowsAffected = rows.Count,
                        Rows = rows
                    };

                default:
                    // Skip unknown packet
                    break;
            }
        }
    }

    private async Task<List<IReadOnlyDictionary<string, object?>>> ReadDataBlockAsync(CancellationToken ct)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();

        var tempTableName = await ReadStringAsync(ct);

        // Block info (simplified)
        var numColumns = (int)await ReadVarUIntAsync(ct);
        var numRows = (int)await ReadVarUIntAsync(ct);

        if (numColumns == 0 || numRows == 0)
            return rows;

        // Read column names and types
        var columns = new List<(string name, string type)>();
        for (int i = 0; i < numColumns; i++)
        {
            var name = await ReadStringAsync(ct);
            var type = await ReadStringAsync(ct);
            columns.Add((name, type));
        }

        // Read data (simplified - actual implementation needs type-specific parsing)
        for (int r = 0; r < numRows; r++)
        {
            var row = new Dictionary<string, object?>();
            for (int c = 0; c < numColumns; c++)
            {
                // Read value based on type (simplified)
                var value = await ReadStringAsync(ct);
                row[columns[c].name] = value;
            }
            rows.Add(row);
        }

        return rows;
    }

    private async Task<string> ReadExceptionAsync(CancellationToken ct)
    {
        var code = (int)await ReadVarUIntAsync(ct);
        var name = await ReadStringAsync(ct);
        var message = await ReadStringAsync(ct);
        var stackTrace = await ReadStringAsync(ct);
        var hasNested = await ReadVarUIntAsync(ct) != 0;

        return $"[{code}] {name}: {message}";
    }

    private static void WriteVarUInt(BinaryWriter bw, ulong value)
    {
        while (value >= 0x80)
        {
            bw.Write((byte)(value | 0x80));
            value >>= 7;
        }
        bw.Write((byte)value);
    }

    private static void WriteString(BinaryWriter bw, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarUInt(bw, (ulong)bytes.Length);
        bw.Write(bytes);
    }

    private async Task<ulong> ReadVarUIntAsync(CancellationToken ct)
    {
        ulong result = 0;
        int shift = 0;

        while (true)
        {
            var buffer = new byte[1];
            await ActiveStream!.ReadExactlyAsync(buffer, 0, 1, ct);
            var b = buffer[0];

            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;

            shift += 7;
        }
    }

    private async Task<string> ReadStringAsync(CancellationToken ct)
    {
        var length = (int)await ReadVarUIntAsync(ct);
        if (length == 0) return "";

        var buffer = new byte[length];
        await ActiveStream!.ReadExactlyAsync(buffer, 0, length, ct);
        return Encoding.UTF8.GetString(buffer);
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
        throw new NotSupportedException("ClickHouse does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("ClickHouse does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("ClickHouse does not support transactions");
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
            using var ms = new MemoryStream(4096);
            using var bw = new BinaryWriter(ms);
            WriteVarUInt(bw, ClientPing);

            await ActiveStream!.WriteAsync(ms.ToArray(), ct);
            await ActiveStream.FlushAsync(ct);

            var response = await ReadVarUIntAsync(ct);
            return response == ServerPong;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Apache HBase Thrift protocol strategy.
/// </summary>
public sealed class HBaseProtocolStrategy : DatabaseProtocolStrategyBase
{
    private HttpClient? _httpClient;
    private string _namespace = "default";

    /// <inheritdoc/>
    public override string StrategyId => "hbase-rest";

    /// <inheritdoc/>
    public override string StrategyName => "Apache HBase REST Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Apache HBase REST API",
        ProtocolVersion = "2.x",
        DefaultPort = 8080,
        Family = ProtocolFamily.Specialized,
        MaxPacketSize = 64 * 1024 * 1024,
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
            SupportsQueryCancellation = false,
            SupportedAuthMethods =
            [
                AuthenticationMethod.None,
                AuthenticationMethod.Kerberos
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var scheme = parameters.UseSsl ? "https" : "http";
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"{scheme}://{parameters.Host}:{parameters.Port}"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _namespace = parameters.Database ?? "default";
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // HBase REST typically uses Kerberos or no auth
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        var parts = query.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return new QueryResult { Success = false, ErrorMessage = "Empty query" };
        }

        var command = parts[0].ToUpperInvariant();

        return command switch
        {
            "GET" => await GetAsync(parts, ct),
            "PUT" => await PutAsync(parts, parameters, ct),
            "DELETE" => await DeleteAsync(parts, ct),
            "SCAN" => await ScanAsync(parts, parameters, ct),
            "TABLES" or "LIST" => await ListTablesAsync(ct),
            "CREATE" => await CreateTableAsync(parts, parameters, ct),
            _ => new QueryResult { Success = false, ErrorMessage = $"Unknown command: {command}" }
        };
    }

    private async Task<QueryResult> GetAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length < 3)
            return new QueryResult { Success = false, ErrorMessage = "GET requires table and row key" };

        var table = parts[1];
        var rowKey = parts[2];

        var response = await _httpClient!.GetAsync($"/{table}/{rowKey}", ct);
        if (!response.IsSuccessStatusCode)
        {
            return new QueryResult
            {
                Success = response.StatusCode == System.Net.HttpStatusCode.NotFound,
                RowsAffected = 0,
                Rows = []
            };
        }

        var content = await response.Content.ReadAsStringAsync(ct);
        var row = JsonSerializer.Deserialize<Dictionary<string, object?>>(content) ?? [];

        return new QueryResult
        {
            Success = true,
            RowsAffected = 1,
            Rows = [row]
        };
    }

    private async Task<QueryResult> PutAsync(
        string[] parts,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (parts.Length < 3)
            return new QueryResult { Success = false, ErrorMessage = "PUT requires table and row key" };

        var table = parts[1];
        var rowKey = parts[2];

        var data = new
        {
            Row = new[]
            {
                new
                {
                    key = Convert.ToBase64String(Encoding.UTF8.GetBytes(rowKey)),
                    Cell = parameters?.Select(p => new
                    {
                        column = Convert.ToBase64String(Encoding.UTF8.GetBytes(p.Key)),
                        value = Convert.ToBase64String(Encoding.UTF8.GetBytes(p.Value?.ToString() ?? ""))
                    }).ToArray() ?? Array.Empty<object>()
                }
            }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(data),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient!.PutAsync($"/{table}/{rowKey}", content, ct);

        return new QueryResult
        {
            Success = response.IsSuccessStatusCode,
            RowsAffected = response.IsSuccessStatusCode ? 1 : 0
        };
    }

    private async Task<QueryResult> DeleteAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length < 3)
            return new QueryResult { Success = false, ErrorMessage = "DELETE requires table and row key" };

        var table = parts[1];
        var rowKey = parts[2];

        var response = await _httpClient!.DeleteAsync($"/{table}/{rowKey}", ct);

        return new QueryResult
        {
            Success = response.IsSuccessStatusCode,
            RowsAffected = response.IsSuccessStatusCode ? 1 : 0
        };
    }

    private async Task<QueryResult> ScanAsync(
        string[] parts,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        var table = parts.Length > 1 ? parts[1] : "";
        var limit = int.TryParse(parameters?.GetValueOrDefault("limit")?.ToString(), out var l) ? l : 100;

        var response = await _httpClient!.GetAsync($"/{table}/*?limit={limit}", ct);
        if (!response.IsSuccessStatusCode)
        {
            return new QueryResult { Success = false, ErrorMessage = "Scan failed" };
        }

        var content = await response.Content.ReadAsStringAsync(ct);
        // Parse response (simplified)
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["raw"] = content }
        };

        return new QueryResult
        {
            Success = true,
            RowsAffected = rows.Count,
            Rows = rows
        };
    }

    private async Task<QueryResult> ListTablesAsync(CancellationToken ct)
    {
        var response = await _httpClient!.GetAsync("/", ct);
        var content = await response.Content.ReadAsStringAsync(ct);

        var tables = JsonSerializer.Deserialize<Dictionary<string, object>>(content);
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["tables"] = content }
        };

        return new QueryResult
        {
            Success = true,
            RowsAffected = 1,
            Rows = rows
        };
    }

    private async Task<QueryResult> CreateTableAsync(
        string[] parts,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        var table = parts.Length > 2 ? parts[2] : "";
        var columnFamilies = parameters?.GetValueOrDefault("column_families")?.ToString()?.Split(',') ?? ["cf"];

        var schema = new
        {
            name = table,
            ColumnSchema = columnFamilies.Select(cf => new { name = cf }).ToArray()
        };

        var content = new StringContent(
            JsonSerializer.Serialize(schema),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient!.PutAsync($"/{table}/schema", content, ct);

        return new QueryResult
        {
            Success = response.IsSuccessStatusCode,
            RowsAffected = response.IsSuccessStatusCode ? 1 : 0
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
        throw new NotSupportedException("HBase does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("HBase does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("HBase does not support transactions");
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
            var response = await _httpClient!.GetAsync("/version", ct);
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
        await base.CleanupConnectionAsync();
    }
}

/// <summary>
/// Couchbase N1QL protocol strategy.
/// </summary>
public sealed class CouchbaseProtocolStrategy : DatabaseProtocolStrategyBase
{
    private HttpClient? _httpClient;
    private string _bucket = "";

    /// <inheritdoc/>
    public override string StrategyId => "couchbase-n1ql";

    /// <inheritdoc/>
    public override string StrategyName => "Couchbase N1QL Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Couchbase N1QL REST API",
        ProtocolVersion = "7.x",
        DefaultPort = 8093,
        Family = ProtocolFamily.NoSQL,
        MaxPacketSize = 20 * 1024 * 1024,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = true,
            SupportsPreparedStatements = true,
            SupportsCursors = false,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = true,
            SupportsCompression = true,
            SupportsMultiplexing = true,
            SupportsServerCursors = false,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods =
            [
                AuthenticationMethod.ClearText
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var scheme = parameters.UseSsl ? "https" : "http";
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"{scheme}://{parameters.Host}:{parameters.Port}"),
            Timeout = TimeSpan.FromSeconds(60)
        };

        _bucket = parameters.Database ?? "default";
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(parameters.Password))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{parameters.Username}:{parameters.Password}"));
            _httpClient!.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        var requestBody = new Dictionary<string, object>
        {
            ["statement"] = query
        };

        if (parameters != null)
        {
            foreach (var param in parameters)
            {
                requestBody[$"${param.Key}"] = param.Value!;
            }
        }

        var content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient!.PostAsync("/query/service", content, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(responseJson);

        if (doc.RootElement.TryGetProperty("errors", out var errors) &&
            errors.GetArrayLength() > 0)
        {
            var firstError = errors[0];
            var errorMsg = firstError.TryGetProperty("msg", out var msg) ? msg.GetString() : "Unknown error";
            return new QueryResult
            {
                Success = false,
                ErrorMessage = errorMsg
            };
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        if (doc.RootElement.TryGetProperty("results", out var results))
        {
            foreach (var row in results.EnumerateArray())
            {
                var dict = new Dictionary<string, object?>();
                foreach (var prop in row.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.ToString();
                }
                rows.Add(dict);
            }
        }

        long rowsAffected = rows.Count;
        if (doc.RootElement.TryGetProperty("metrics", out var metrics) &&
            metrics.TryGetProperty("mutationCount", out var mutations))
        {
            rowsAffected = mutations.GetInt64();
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
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        var result = await ExecuteQueryCoreAsync("BEGIN WORK", null, ct);
        return Guid.NewGuid().ToString("N");
    }

    /// <inheritdoc/>
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("COMMIT WORK", null, ct);
    }

    /// <inheritdoc/>
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("ROLLBACK WORK", null, ct);
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
            var response = await _httpClient!.GetAsync("/admin/ping", ct);
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
        await base.CleanupConnectionAsync();
    }
}

/// <summary>
/// Apache Druid protocol strategy.
/// </summary>
public sealed class DruidProtocolStrategy : DatabaseProtocolStrategyBase
{
    private HttpClient? _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "druid-sql";

    /// <inheritdoc/>
    public override string StrategyName => "Apache Druid SQL Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Apache Druid SQL REST API",
        ProtocolVersion = "0.23",
        DefaultPort = 8888,
        Family = ProtocolFamily.Specialized,
        MaxPacketSize = 100 * 1024 * 1024,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = false,
            SupportsPreparedStatements = true,
            SupportsCursors = false,
            SupportsStreaming = true,
            SupportsBatch = false,
            SupportsNotifications = false,
            SupportsSsl = true,
            SupportsCompression = true,
            SupportsMultiplexing = true,
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
        var scheme = parameters.UseSsl ? "https" : "http";
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"{scheme}://{parameters.Host}:{parameters.Port}"),
            Timeout = TimeSpan.FromMinutes(5)
        };
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(parameters.Password))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{parameters.Username}:{parameters.Password}"));
            _httpClient!.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        var request = new
        {
            query,
            resultFormat = "array",
            header = true
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient!.PostAsync("/druid/v2/sql", content, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            return new QueryResult
            {
                Success = false,
                ErrorMessage = responseJson
            };
        }

        using var doc = JsonDocument.Parse(responseJson);
        var rows = new List<IReadOnlyDictionary<string, object?>>();

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            var array = doc.RootElement.EnumerateArray().ToList();
            if (array.Count > 0)
            {
                // First row is headers
                var headers = array[0].EnumerateArray().Select(h => h.GetString() ?? "").ToList();

                // Remaining rows are data
                for (int i = 1; i < array.Count; i++)
                {
                    var row = new Dictionary<string, object?>();
                    int colIdx = 0;
                    foreach (var value in array[i].EnumerateArray())
                    {
                        var colName = colIdx < headers.Count ? headers[colIdx] : $"col{colIdx}";
                        row[colName] = value.ValueKind == JsonValueKind.Null ? null : value.ToString();
                        colIdx++;
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
        throw new NotSupportedException("Druid does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("Druid does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("Druid does not support transactions");
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
            var response = await _httpClient!.GetAsync("/status/health", ct);
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
        await base.CleanupConnectionAsync();
    }
}

/// <summary>
/// Apache Presto/Trino protocol strategy.
/// </summary>
public sealed class PrestoProtocolStrategy : DatabaseProtocolStrategyBase
{
    private HttpClient? _httpClient;
    private string _catalog = "";
    private string _schema = "";

    /// <inheritdoc/>
    public override string StrategyId => "presto-http";

    /// <inheritdoc/>
    public override string StrategyName => "Presto/Trino HTTP Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Presto/Trino HTTP Client Protocol",
        ProtocolVersion = "0.1",
        DefaultPort = 8080,
        Family = ProtocolFamily.Specialized,
        MaxPacketSize = 100 * 1024 * 1024,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = false,
            SupportsPreparedStatements = true,
            SupportsCursors = true,
            SupportsStreaming = true,
            SupportsBatch = false,
            SupportsNotifications = false,
            SupportsSsl = true,
            SupportsCompression = true,
            SupportsMultiplexing = true,
            SupportsServerCursors = true,
            SupportsBulkOperations = false,
            SupportsQueryCancellation = true,
            SupportedAuthMethods =
            [
                AuthenticationMethod.None,
                AuthenticationMethod.ClearText,
                AuthenticationMethod.Certificate
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var scheme = parameters.UseSsl ? "https" : "http";
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"{scheme}://{parameters.Host}:{parameters.Port}"),
            Timeout = TimeSpan.FromMinutes(30)
        };

        _catalog = parameters.Database ?? "hive";
        _schema = parameters.ExtendedProperties?.GetValueOrDefault("Schema")?.ToString() ?? "default";

        _httpClient.DefaultRequestHeaders.Add("X-Presto-User", parameters.Username ?? "presto");
        _httpClient.DefaultRequestHeaders.Add("X-Presto-Catalog", _catalog);
        _httpClient.DefaultRequestHeaders.Add("X-Presto-Schema", _schema);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(parameters.Password))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{parameters.Username}:{parameters.Password}"));
            _httpClient!.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        // Submit query
        var content = new StringContent(query, Encoding.UTF8, "text/plain");
        var response = await _httpClient!.PostAsync("/v1/statement", content, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            return new QueryResult { Success = false, ErrorMessage = responseJson };
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var columns = new List<string>();
        string? nextUri = null;

        // Parse initial response
        using (var doc = JsonDocument.Parse(responseJson))
        {
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                return new QueryResult
                {
                    Success = false,
                    ErrorMessage = error.GetProperty("message").GetString()
                };
            }

            // Get columns
            if (doc.RootElement.TryGetProperty("columns", out var cols))
            {
                foreach (var col in cols.EnumerateArray())
                {
                    columns.Add(col.GetProperty("name").GetString() ?? "");
                }
            }

            // Get data
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var rowData in data.EnumerateArray())
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
            }

            // Get next page
            if (doc.RootElement.TryGetProperty("nextUri", out var next))
            {
                nextUri = next.GetString();
            }
        }

        // Fetch remaining pages
        while (!string.IsNullOrEmpty(nextUri))
        {
            await Task.Delay(100, ct); // Small delay between requests

            response = await _httpClient.GetAsync(nextUri, ct);
            responseJson = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(responseJson);

            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var rowData in data.EnumerateArray())
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
            }

            nextUri = doc.RootElement.TryGetProperty("nextUri", out var next) ? next.GetString() : null;
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = rows.Count,
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
        throw new NotSupportedException("Presto does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("Presto does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("Presto does not support transactions");
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
            var response = await _httpClient!.GetAsync("/v1/info", ct);
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
        await base.CleanupConnectionAsync();
    }
}
