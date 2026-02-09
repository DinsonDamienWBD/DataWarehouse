using System.Data;
using System.Data.Common;
using System.Net.Http.Json;
using System.Text;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.Driver;

/// <summary>
/// ADO.NET provider base protocol strategy.
/// Wraps any ADO.NET DbConnection/DbCommand for unified access.
/// </summary>
public sealed class AdoNetProviderStrategy : DatabaseProtocolStrategyBase
{
    private DbConnection? _connection;
    private DbTransaction? _currentTransaction;
    private readonly Dictionary<string, DbTransaction> _transactions = new();
    private string _providerName = "";
    private string _serverVersion = "";

    /// <inheritdoc/>
    public override string StrategyId => "adonet-provider";

    /// <inheritdoc/>
    public override string StrategyName => "ADO.NET Provider Base";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "ADO.NET Database Provider",
        ProtocolVersion = "4.0",
        DefaultPort = 0, // Varies by provider
        Family = ProtocolFamily.Driver,
        MaxPacketSize = int.MaxValue,
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
                AuthenticationMethod.Integrated,
                AuthenticationMethod.Certificate
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Handshake is handled by the ADO.NET provider
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Get provider factory from provider name
        _providerName = parameters.ExtendedProperties?.GetValueOrDefault("ProviderName")?.ToString()
            ?? "System.Data.SqlClient";

        var factory = DbProviderFactories.GetFactory(_providerName);
        _connection = factory.CreateConnection()
            ?? throw new InvalidOperationException($"Failed to create connection for provider: {_providerName}");

        // Build connection string
        _connection.ConnectionString = BuildConnectionString(parameters);

        await _connection.OpenAsync(ct);
        _serverVersion = _connection.ServerVersion;
    }

    private string BuildConnectionString(ConnectionParameters parameters)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrEmpty(parameters.Host))
        {
            builder.Append($"Server={parameters.Host}");
            if (parameters.Port > 0)
                builder.Append($",{parameters.Port}");
            builder.Append(';');
        }

        if (!string.IsNullOrEmpty(parameters.Database))
            builder.Append($"Database={parameters.Database};");

        if (!string.IsNullOrEmpty(parameters.Username))
        {
            builder.Append($"User Id={parameters.Username};");
            if (!string.IsNullOrEmpty(parameters.Password))
                builder.Append($"Password={parameters.Password};");
        }
        else
        {
            builder.Append("Integrated Security=true;");
        }

        if (parameters.UseSsl)
            builder.Append("Encrypt=true;");

        // Add any extended properties
        if (parameters.ExtendedProperties != null)
        {
            foreach (var prop in parameters.ExtendedProperties)
            {
                if (prop.Key != "ProviderName" && prop.Value != null)
                    builder.Append($"{prop.Key}={prop.Value};");
            }
        }

        return builder.ToString();
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("Not connected");

        using var command = _connection.CreateCommand();
        command.CommandText = query;
        command.Transaction = _currentTransaction;

        // Add parameters
        if (parameters != null)
        {
            foreach (var param in parameters)
            {
                var dbParam = command.CreateParameter();
                dbParam.ParameterName = param.Key;
                dbParam.Value = param.Value ?? DBNull.Value;
                command.Parameters.Add(dbParam);
            }
        }

        try
        {
            using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<IReadOnlyDictionary<string, object?>>();

            // Get column information
            var columns = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(reader.GetName(i));
            }

            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    row[columns[i]] = value;
                }
                rows.Add(row);
            }

            return new QueryResult
            {
                Success = true,
                RowsAffected = rows.Count,
                Rows = rows
            };
        }
        catch (DbException ex)
        {
            return new QueryResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ErrorCode = ex.ErrorCode.ToString()
            };
        }
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(
        string command,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("Not connected");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = command;
        cmd.Transaction = _currentTransaction;

        if (parameters != null)
        {
            foreach (var param in parameters)
            {
                var dbParam = cmd.CreateParameter();
                dbParam.ParameterName = param.Key;
                dbParam.Value = param.Value ?? DBNull.Value;
                cmd.Parameters.Add(dbParam);
            }
        }

        try
        {
            var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
            return new QueryResult
            {
                Success = true,
                RowsAffected = rowsAffected
            };
        }
        catch (DbException ex)
        {
            return new QueryResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ErrorCode = ex.ErrorCode.ToString()
            };
        }
    }

    /// <inheritdoc/>
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        if (_connection == null)
            throw new InvalidOperationException("Not connected");

        var tx = await _connection.BeginTransactionAsync(ct);
        var txId = Guid.NewGuid().ToString("N");
        _transactions[txId] = tx;
        _currentTransaction = tx;
        return txId;
    }

    /// <inheritdoc/>
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        if (_transactions.TryGetValue(transactionId, out var tx))
        {
            await tx.CommitAsync(ct);
            tx.Dispose();
            _transactions.Remove(transactionId);
            _currentTransaction = null;
        }
    }

    /// <inheritdoc/>
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        if (_transactions.TryGetValue(transactionId, out var tx))
        {
            await tx.RollbackAsync(ct);
            tx.Dispose();
            _transactions.Remove(transactionId);
            _currentTransaction = null;
        }
    }

    /// <inheritdoc/>
    protected override Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task<bool> PingCoreAsync(CancellationToken ct)
    {
        return Task.FromResult(_connection?.State == System.Data.ConnectionState.Open);
    }

    /// <inheritdoc/>
    protected override async Task CleanupConnectionAsync()
    {
        foreach (var tx in _transactions.Values)
        {
            try { tx.Dispose(); } catch { }
        }
        _transactions.Clear();

        if (_connection != null)
        {
            await _connection.CloseAsync();
            _connection.Dispose();
            _connection = null;
        }

        await base.CleanupConnectionAsync();
    }
}

/// <summary>
/// JDBC bridge protocol strategy.
/// Provides connectivity to Java databases via JNI or network bridge.
/// </summary>
public sealed class JdbcBridgeProtocolStrategy : DatabaseProtocolStrategyBase
{
    private HttpClient? _httpClient;
    private string _sessionId = "";
    private string _jdbcUrl = "";
    private string _driverClass = "";

    /// <inheritdoc/>
    public override string StrategyId => "jdbc-bridge";

    /// <inheritdoc/>
    public override string StrategyName => "JDBC Bridge Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "JDBC Network Bridge",
        ProtocolVersion = "4.3",
        DefaultPort = 9000,
        Family = ProtocolFamily.Driver,
        MaxPacketSize = 100 * 1024 * 1024,
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
        var scheme = parameters.UseSsl ? "https" : "http";
        var baseUrl = $"{scheme}://{parameters.Host}:{parameters.Port}";

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(parameters.CommandTimeout > 0 ? parameters.CommandTimeout : 30)
        };

        // Get JDBC URL from extended properties
        _jdbcUrl = parameters.ExtendedProperties?.GetValueOrDefault("JdbcUrl")?.ToString()
            ?? $"jdbc:postgresql://{parameters.Host}:{parameters.Port}/{parameters.Database}";

        _driverClass = parameters.ExtendedProperties?.GetValueOrDefault("DriverClass")?.ToString()
            ?? "org.postgresql.Driver";

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        // Connect to JDBC bridge service
        var connectRequest = new
        {
            action = "connect",
            url = _jdbcUrl,
            driver = _driverClass,
            username = parameters.Username,
            password = parameters.Password,
            properties = parameters.ExtendedProperties
        };

        var response = await _httpClient.PostAsJsonAsync("/api/jdbc", connectRequest, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JdbcResponse>(cancellationToken: ct);
        if (result?.Success != true)
        {
            throw new Exception($"JDBC connection failed: {result?.Error}");
        }

        _sessionId = result.SessionId ?? throw new Exception("No session ID returned");
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
            action = "executeQuery",
            sessionId = _sessionId,
            sql = query,
            parameters = parameters
        };

        var response = await _httpClient.PostAsJsonAsync("/api/jdbc", request, ct);
        var result = await response.Content.ReadFromJsonAsync<JdbcQueryResponse>(cancellationToken: ct);

        if (result?.Success != true)
        {
            return new QueryResult
            {
                Success = false,
                ErrorMessage = result?.Error ?? "Unknown error"
            };
        }

        var rows = result.Rows?.Select(r =>
            r as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>()).ToList()
            ?? [];

        return new QueryResult
        {
            Success = true,
            RowsAffected = result.RowsAffected,
            Rows = rows
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

        var request = new
        {
            action = "executeUpdate",
            sessionId = _sessionId,
            sql = command,
            parameters = parameters
        };

        var response = await _httpClient.PostAsJsonAsync("/api/jdbc", request, ct);
        var result = await response.Content.ReadFromJsonAsync<JdbcResponse>(cancellationToken: ct);

        if (result?.Success != true)
        {
            return new QueryResult
            {
                Success = false,
                ErrorMessage = result?.Error ?? "Unknown error"
            };
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = result.RowsAffected
        };
    }

    /// <inheritdoc/>
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        var request = new
        {
            action = "beginTransaction",
            sessionId = _sessionId
        };

        var response = await _httpClient.PostAsJsonAsync("/api/jdbc", request, ct);
        var result = await response.Content.ReadFromJsonAsync<JdbcResponse>(cancellationToken: ct);

        return result?.TransactionId ?? Guid.NewGuid().ToString("N");
    }

    /// <inheritdoc/>
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        var request = new
        {
            action = "commit",
            sessionId = _sessionId,
            transactionId
        };

        await _httpClient.PostAsJsonAsync("/api/jdbc", request, ct);
    }

    /// <inheritdoc/>
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        if (_httpClient == null)
            throw new InvalidOperationException("Not connected");

        var request = new
        {
            action = "rollback",
            sessionId = _sessionId,
            transactionId
        };

        await _httpClient.PostAsJsonAsync("/api/jdbc", request, ct);
    }

    /// <inheritdoc/>
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        if (_httpClient == null || string.IsNullOrEmpty(_sessionId))
            return;

        try
        {
            var request = new
            {
                action = "disconnect",
                sessionId = _sessionId
            };

            await _httpClient.PostAsJsonAsync("/api/jdbc", request, ct);
        }
        catch
        {
            // Ignore disconnect errors
        }
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        if (_httpClient == null)
            return false;

        try
        {
            var request = new
            {
                action = "ping",
                sessionId = _sessionId
            };

            var response = await _httpClient.PostAsJsonAsync("/api/jdbc", request, ct);
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

    private class JdbcResponse
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? SessionId { get; set; }
        public string? TransactionId { get; set; }
        public long RowsAffected { get; set; }
    }

    private sealed class JdbcQueryResponse : JdbcResponse
    {
        public List<Dictionary<string, object?>>? Rows { get; set; }
    }
}

/// <summary>
/// ODBC driver base protocol strategy.
/// Provides connectivity via ODBC DSN or connection string.
/// </summary>
public sealed class OdbcDriverProtocolStrategy : DatabaseProtocolStrategyBase
{
    private DbConnection? _connection;
    private DbTransaction? _currentTransaction;
    private string _driverName = "";

    /// <inheritdoc/>
    public override string StrategyId => "odbc-driver";

    /// <inheritdoc/>
    public override string StrategyName => "ODBC Driver Base";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "ODBC Driver",
        ProtocolVersion = "3.8",
        DefaultPort = 0,
        Family = ProtocolFamily.Driver,
        MaxPacketSize = int.MaxValue,
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
                AuthenticationMethod.Integrated
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Use System.Data.Odbc
        var factory = DbProviderFactories.GetFactory("System.Data.Odbc");
        _connection = factory.CreateConnection()
            ?? throw new InvalidOperationException("Failed to create ODBC connection");

        _connection.ConnectionString = BuildOdbcConnectionString(parameters);
        await _connection.OpenAsync(ct);
    }

    private string BuildOdbcConnectionString(ConnectionParameters parameters)
    {
        var builder = new StringBuilder();

        // Check for DSN
        var dsn = parameters.ExtendedProperties?.GetValueOrDefault("DSN")?.ToString();
        if (!string.IsNullOrEmpty(dsn))
        {
            builder.Append($"DSN={dsn};");
        }
        else
        {
            // Build driver connection string
            _driverName = parameters.ExtendedProperties?.GetValueOrDefault("Driver")?.ToString()
                ?? "SQL Server";

            builder.Append($"Driver={{{_driverName}}};");

            if (!string.IsNullOrEmpty(parameters.Host))
            {
                builder.Append($"Server={parameters.Host}");
                if (parameters.Port > 0)
                    builder.Append($",{parameters.Port}");
                builder.Append(';');
            }

            if (!string.IsNullOrEmpty(parameters.Database))
                builder.Append($"Database={parameters.Database};");
        }

        if (!string.IsNullOrEmpty(parameters.Username))
        {
            builder.Append($"Uid={parameters.Username};");
            if (!string.IsNullOrEmpty(parameters.Password))
                builder.Append($"Pwd={parameters.Password};");
        }
        else
        {
            builder.Append("Trusted_Connection=Yes;");
        }

        // Add extended properties
        if (parameters.ExtendedProperties != null)
        {
            foreach (var prop in parameters.ExtendedProperties)
            {
                if (prop.Key is not ("DSN" or "Driver") && prop.Value != null)
                    builder.Append($"{prop.Key}={prop.Value};");
            }
        }

        return builder.ToString();
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("Not connected");

        using var command = _connection.CreateCommand();
        command.CommandText = query;
        command.Transaction = _currentTransaction;

        if (parameters != null)
        {
            foreach (var param in parameters)
            {
                var dbParam = command.CreateParameter();
                dbParam.ParameterName = param.Key;
                dbParam.Value = param.Value ?? DBNull.Value;
                command.Parameters.Add(dbParam);
            }
        }

        try
        {
            using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<IReadOnlyDictionary<string, object?>>();

            var columns = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
                columns.Add(reader.GetName(i));

            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }

            return new QueryResult
            {
                Success = true,
                RowsAffected = rows.Count,
                Rows = rows
            };
        }
        catch (DbException ex)
        {
            return new QueryResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ErrorCode = ex.ErrorCode.ToString()
            };
        }
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(
        string command,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("Not connected");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = command;
        cmd.Transaction = _currentTransaction;

        if (parameters != null)
        {
            foreach (var param in parameters)
            {
                var dbParam = cmd.CreateParameter();
                dbParam.ParameterName = param.Key;
                dbParam.Value = param.Value ?? DBNull.Value;
                cmd.Parameters.Add(dbParam);
            }
        }

        try
        {
            var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
            return new QueryResult
            {
                Success = true,
                RowsAffected = rowsAffected
            };
        }
        catch (DbException ex)
        {
            return new QueryResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ErrorCode = ex.ErrorCode.ToString()
            };
        }
    }

    /// <inheritdoc/>
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        if (_connection == null)
            throw new InvalidOperationException("Not connected");

        _currentTransaction = await _connection.BeginTransactionAsync(ct);
        return Guid.NewGuid().ToString("N");
    }

    /// <inheritdoc/>
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        if (_currentTransaction != null)
        {
            await _currentTransaction.CommitAsync(ct);
            _currentTransaction.Dispose();
            _currentTransaction = null;
        }
    }

    /// <inheritdoc/>
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        if (_currentTransaction != null)
        {
            await _currentTransaction.RollbackAsync(ct);
            _currentTransaction.Dispose();
            _currentTransaction = null;
        }
    }

    /// <inheritdoc/>
    protected override Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task<bool> PingCoreAsync(CancellationToken ct)
    {
        return Task.FromResult(_connection?.State == System.Data.ConnectionState.Open);
    }

    /// <inheritdoc/>
    protected override async Task CleanupConnectionAsync()
    {
        _currentTransaction?.Dispose();
        _currentTransaction = null;

        if (_connection != null)
        {
            await _connection.CloseAsync();
            _connection.Dispose();
            _connection = null;
        }

        await base.CleanupConnectionAsync();
    }
}
