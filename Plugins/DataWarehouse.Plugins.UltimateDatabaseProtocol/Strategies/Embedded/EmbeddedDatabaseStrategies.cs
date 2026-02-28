using System.Data;
using System.Data.Common;
using System.Text;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.Embedded;

/// <summary>
/// SQLite native protocol strategy.
/// Uses SQLite's native C API via P/Invoke or ADO.NET provider.
/// </summary>
public sealed class SqliteProtocolStrategy : DatabaseProtocolStrategyBase
{
    private DbConnection? _connection;
    private DbTransaction? _currentTransaction;
    private string _databasePath = "";
    private string _journalMode = "WAL";

    /// <inheritdoc/>
    public override string StrategyId => "sqlite-native";

    /// <inheritdoc/>
    public override string StrategyName => "SQLite Native Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "SQLite Native",
        ProtocolVersion = "3.45",
        DefaultPort = 0, // File-based
        Family = ProtocolFamily.Embedded,
        MaxPacketSize = 1024 * 1024 * 1024, // 1 GB blob limit
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = true,
            SupportsPreparedStatements = true,
            SupportsCursors = true,
            SupportsStreaming = false,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = false,
            SupportsCompression = false,
            SupportsMultiplexing = false,
            SupportsServerCursors = false,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods =
            [
                AuthenticationMethod.None
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        _databasePath = parameters.Database ?? ":memory:";
        _journalMode = parameters.ExtendedProperties?.GetValueOrDefault("JournalMode")?.ToString() ?? "WAL";
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var factory = DbProviderFactories.GetFactory("Microsoft.Data.Sqlite");
        _connection = factory.CreateConnection()
            ?? throw new InvalidOperationException("Failed to create SQLite connection");

        var builder = new StringBuilder($"Data Source={_databasePath}");

        if (!string.IsNullOrEmpty(parameters.Password))
        {
            builder.Append($";Password={parameters.Password}");
        }

        // Add additional options
        if (parameters.ExtendedProperties != null)
        {
            if (parameters.ExtendedProperties.TryGetValue("Mode", out var mode))
                builder.Append($";Mode={mode}");
            if (parameters.ExtendedProperties.TryGetValue("Cache", out var cache))
                builder.Append($";Cache={cache}");
        }

        _connection.ConnectionString = builder.ToString();
        await _connection.OpenAsync(ct);

        // Set pragmas
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA journal_mode={_journalMode}; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        await cmd.ExecuteNonQueryAsync(ct);
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
                dbParam.ParameterName = param.Key.StartsWith("@") ? param.Key : $"@{param.Key}";
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
                dbParam.ParameterName = param.Key.StartsWith("@") ? param.Key : $"@{param.Key}";
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

/// <summary>
/// DuckDB native protocol strategy.
/// High-performance analytical embedded database.
/// </summary>
public sealed class DuckDbProtocolStrategy : DatabaseProtocolStrategyBase
{
    private DbConnection? _connection;
    private DbTransaction? _currentTransaction;
    private string _databasePath = "";

    /// <inheritdoc/>
    public override string StrategyId => "duckdb-native";

    /// <inheritdoc/>
    public override string StrategyName => "DuckDB Native Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "DuckDB Native",
        ProtocolVersion = "0.10",
        DefaultPort = 0,
        Family = ProtocolFamily.Embedded,
        MaxPacketSize = int.MaxValue,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = true,
            SupportsPreparedStatements = true,
            SupportsCursors = true,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = false,
            SupportsCompression = true,
            SupportsMultiplexing = false,
            SupportsServerCursors = false,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods =
            [
                AuthenticationMethod.None
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        _databasePath = parameters.Database ?? ":memory:";
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        try
        {
            var factory = DbProviderFactories.GetFactory("DuckDB.NET.Data");
            _connection = factory.CreateConnection()
                ?? throw new InvalidOperationException("Failed to create DuckDB connection");

            _connection.ConnectionString = $"Data Source={_databasePath}";
            await _connection.OpenAsync(ct);
        }
        catch (Exception ex) when (ex.Message.Contains("provider"))
        {
            // Fallback - DuckDB provider might not be registered
            throw new InvalidOperationException(
                "DuckDB provider not available. Install DuckDB.NET.Data package.", ex);
        }
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
                dbParam.ParameterName = param.Key.StartsWith("$") ? param.Key : $"${param.Key}";
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
                dbParam.ParameterName = param.Key.StartsWith("$") ? param.Key : $"${param.Key}";
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
                ErrorMessage = ex.Message
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

/// <summary>
/// LevelDB protocol strategy.
/// Key-value embedded database.
/// </summary>
public sealed class LevelDbProtocolStrategy : DatabaseProtocolStrategyBase
{
    private string _databasePath = "";
    private bool _createIfMissing = true;
    private bool _isOpen;

    // LevelDB would typically use native bindings
    // This is a simplified implementation using file-based storage

    /// <inheritdoc/>
    public override string StrategyId => "leveldb-native";

    /// <inheritdoc/>
    public override string StrategyName => "LevelDB Native Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "LevelDB Native",
        ProtocolVersion = "1.23",
        DefaultPort = 0,
        Family = ProtocolFamily.Embedded,
        MaxPacketSize = int.MaxValue,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = false,
            SupportsPreparedStatements = false,
            SupportsCursors = true, // Iterator
            SupportsStreaming = true,
            SupportsBatch = true, // WriteBatch
            SupportsNotifications = false,
            SupportsSsl = false,
            SupportsCompression = true, // Snappy
            SupportsMultiplexing = false,
            SupportsServerCursors = false,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = false,
            SupportedAuthMethods =
            [
                AuthenticationMethod.None
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        _databasePath = parameters.Database ?? "./leveldb";
        _createIfMissing = parameters.ExtendedProperties?.GetValueOrDefault("CreateIfMissing")?.ToString() != "false";
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Open LevelDB database
        if (!Directory.Exists(_databasePath) && _createIfMissing)
        {
            Directory.CreateDirectory(_databasePath);
        }

        if (!Directory.Exists(_databasePath))
        {
            throw new DirectoryNotFoundException($"Database directory not found: {_databasePath}");
        }

        _isOpen = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (!_isOpen)
            throw new InvalidOperationException("Not connected");

        var parts = query.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return Task.FromResult(new QueryResult
            {
                Success = false,
                ErrorMessage = "Empty query"
            });
        }

        var command = parts[0].ToUpperInvariant();
        var key = parts.Length > 1 ? parts[1] : parameters?.GetValueOrDefault("key")?.ToString() ?? "";

        return Task.FromResult(command switch
        {
            "GET" => GetValue(key),
            "PUT" or "SET" => PutValue(key, parameters?.GetValueOrDefault("value")?.ToString() ?? ""),
            "DELETE" or "DEL" => DeleteValue(key),
            "SCAN" or "RANGE" => ScanRange(key, parameters),
            _ => new QueryResult
            {
                Success = false,
                ErrorMessage = $"Unknown command: {command}"
            }
        });
    }

    private QueryResult GetValue(string key)
    {
        var filePath = GetKeyFilePath(key);

        if (!File.Exists(filePath))
        {
            return new QueryResult
            {
                Success = true,
                RowsAffected = 0,
                Rows = []
            };
        }

        try
        {
            var value = File.ReadAllText(filePath);
            return new QueryResult
            {
                Success = true,
                RowsAffected = 1,
                Rows =
                [
                    new Dictionary<string, object?>
                    {
                        ["key"] = key,
                        ["value"] = value
                    }
                ]
            };
        }
        catch (IOException ex)
        {
            return new QueryResult { Success = false, ErrorMessage = $"I/O error reading key '{key}': {ex.Message}" };
        }
    }

    private QueryResult PutValue(string key, string value)
    {
        var filePath = GetKeyFilePath(key);
        var dir = Path.GetDirectoryName(filePath);

        try
        {
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(filePath, value);
        }
        catch (IOException ex)
        {
            return new QueryResult { Success = false, ErrorMessage = $"I/O error writing key '{key}': {ex.Message}" };
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = 1
        };
    }

    private QueryResult DeleteValue(string key)
    {
        var filePath = GetKeyFilePath(key);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            return new QueryResult { Success = true, RowsAffected = 1 };
        }

        return new QueryResult { Success = true, RowsAffected = 0 };
    }

    private QueryResult ScanRange(string startKey, IReadOnlyDictionary<string, object?>? parameters)
    {
        var endKey = parameters?.GetValueOrDefault("end")?.ToString() ?? "\xFF";
        var limit = int.TryParse(parameters?.GetValueOrDefault("limit")?.ToString(), out var l) ? l : 100;

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var dataDir = Path.Combine(_databasePath, "data");

        if (!Directory.Exists(dataDir))
        {
            return new QueryResult { Success = true, RowsAffected = 0, Rows = rows };
        }

        foreach (var file in Directory.GetFiles(dataDir, "*.dat").OrderBy(f => f).Take(limit))
        {
            var key = Path.GetFileNameWithoutExtension(file);
            if (string.Compare(key, startKey, StringComparison.Ordinal) >= 0 &&
                string.Compare(key, endKey, StringComparison.Ordinal) <= 0)
            {
                var value = File.ReadAllText(file);
                rows.Add(new Dictionary<string, object?>
                {
                    ["key"] = key,
                    ["value"] = value
                });
            }
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = rows.Count,
            Rows = rows
        };
    }

    private string GetKeyFilePath(string key)
    {
        var safeKey = Convert.ToHexString(Encoding.UTF8.GetBytes(key));
        return Path.Combine(_databasePath, "data", $"{safeKey}.dat");
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
        throw new NotSupportedException("LevelDB does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("LevelDB does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("LevelDB does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task<bool> PingCoreAsync(CancellationToken ct)
    {
        return Task.FromResult(_isOpen && Directory.Exists(_databasePath));
    }

    /// <inheritdoc/>
    protected override Task CleanupConnectionAsync()
    {
        _isOpen = false;
        return base.CleanupConnectionAsync();
    }
}

/// <summary>
/// RocksDB protocol strategy.
/// High-performance embedded key-value store.
/// </summary>
public sealed class RocksDbProtocolStrategy : DatabaseProtocolStrategyBase
{
    private string _databasePath = "";
    private bool _createIfMissing = true;
    private bool _isOpen;

    /// <summary>
    /// Transactions and compaction are not yet wired to native RocksDB APIs.
    /// File I/O-based key-value operations are functional.
    /// </summary>
    public override bool IsProductionReady => false;

    /// <inheritdoc/>
    public override string StrategyId => "rocksdb-native";

    /// <inheritdoc/>
    public override string StrategyName => "RocksDB Native Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "RocksDB Native",
        ProtocolVersion = "8.x",
        DefaultPort = 0,
        Family = ProtocolFamily.Embedded,
        MaxPacketSize = int.MaxValue,
        Capabilities = new ProtocolCapabilities
        {
            // RocksDb transaction support is not yet wired (Begin/Commit/Rollback are no-ops).
            // Set false to prevent callers from relying on transaction semantics. (finding 2689)
            SupportsTransactions = false,
            SupportsPreparedStatements = false,
            SupportsCursors = true,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = false,
            SupportsCompression = true,
            SupportsMultiplexing = false,
            SupportsServerCursors = false,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = false,
            SupportedAuthMethods =
            [
                AuthenticationMethod.None
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        _databasePath = parameters.Database ?? "./rocksdb";
        _createIfMissing = parameters.ExtendedProperties?.GetValueOrDefault("CreateIfMissing")?.ToString() != "false";
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        if (!Directory.Exists(_databasePath) && _createIfMissing)
        {
            Directory.CreateDirectory(_databasePath);
        }

        if (!Directory.Exists(_databasePath))
        {
            throw new DirectoryNotFoundException($"Database directory not found: {_databasePath}");
        }

        _isOpen = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (!_isOpen)
            throw new InvalidOperationException("Not connected");

        var parts = query.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return Task.FromResult(new QueryResult
            {
                Success = false,
                ErrorMessage = "Empty query"
            });
        }

        var command = parts[0].ToUpperInvariant();
        var key = parts.Length > 1 ? parts[1] : parameters?.GetValueOrDefault("key")?.ToString() ?? "";

        return Task.FromResult(command switch
        {
            "GET" => GetValue(key),
            "PUT" or "SET" => PutValue(key, parameters?.GetValueOrDefault("value")?.ToString() ?? ""),
            "DELETE" or "DEL" => DeleteValue(key),
            "MGET" => MultiGet(parameters),
            "SCAN" or "RANGE" => ScanRange(key, parameters),
            "COMPACT" => Compact(),
            _ => new QueryResult
            {
                Success = false,
                ErrorMessage = $"Unknown command: {command}"
            }
        });
    }

    private QueryResult GetValue(string key)
    {
        var filePath = GetKeyFilePath(key);

        if (!File.Exists(filePath))
        {
            return new QueryResult
            {
                Success = true,
                RowsAffected = 0,
                Rows = []
            };
        }

        try
        {
            var value = File.ReadAllText(filePath);
            return new QueryResult
            {
                Success = true,
                RowsAffected = 1,
                Rows =
                [
                    new Dictionary<string, object?>
                    {
                        ["key"] = key,
                        ["value"] = value
                    }
                ]
            };
        }
        catch (IOException ex)
        {
            return new QueryResult { Success = false, ErrorMessage = $"I/O error reading key '{key}': {ex.Message}" };
        }
    }

    private QueryResult PutValue(string key, string value)
    {
        var filePath = GetKeyFilePath(key);
        var dir = Path.GetDirectoryName(filePath);

        try
        {
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(filePath, value);
        }
        catch (IOException ex)
        {
            return new QueryResult { Success = false, ErrorMessage = $"I/O error writing key '{key}': {ex.Message}" };
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = 1
        };
    }

    private QueryResult DeleteValue(string key)
    {
        var filePath = GetKeyFilePath(key);

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                return new QueryResult { Success = true, RowsAffected = 1 };
            }
        }
        catch (IOException ex)
        {
            return new QueryResult { Success = false, ErrorMessage = $"I/O error deleting key '{key}': {ex.Message}" };
        }

        return new QueryResult { Success = true, RowsAffected = 0 };
    }

    private QueryResult MultiGet(IReadOnlyDictionary<string, object?>? parameters)
    {
        var keysParam = parameters?.GetValueOrDefault("keys");
        var keys = keysParam switch
        {
            IEnumerable<string> s => s.ToList(),
            string s => s.Split(',').Select(k => k.Trim()).ToList(),
            _ => new List<string>()
        };

        var rows = new List<IReadOnlyDictionary<string, object?>>();

        foreach (var key in keys)
        {
            var filePath = GetKeyFilePath(key);
            if (File.Exists(filePath))
            {
                rows.Add(new Dictionary<string, object?>
                {
                    ["key"] = key,
                    ["value"] = File.ReadAllText(filePath)
                });
            }
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = rows.Count,
            Rows = rows
        };
    }

    private QueryResult ScanRange(string startKey, IReadOnlyDictionary<string, object?>? parameters)
    {
        var endKey = parameters?.GetValueOrDefault("end")?.ToString() ?? "\xFF";
        var limit = int.TryParse(parameters?.GetValueOrDefault("limit")?.ToString(), out var l) ? l : 100;

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var dataDir = Path.Combine(_databasePath, "data");

        if (!Directory.Exists(dataDir))
        {
            return new QueryResult { Success = true, RowsAffected = 0, Rows = rows };
        }

        try
        {
            foreach (var file in Directory.GetFiles(dataDir, "*.dat").OrderBy(f => f).Take(limit))
            {
                var key = Encoding.UTF8.GetString(Convert.FromHexString(Path.GetFileNameWithoutExtension(file)));
                if (string.Compare(key, startKey, StringComparison.Ordinal) >= 0 &&
                    string.Compare(key, endKey, StringComparison.Ordinal) <= 0)
                {
                    var value = File.ReadAllText(file);
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["key"] = key,
                        ["value"] = value
                    });
                }
            }
        }
        catch (IOException ex)
        {
            return new QueryResult { Success = false, ErrorMessage = $"I/O error scanning range: {ex.Message}" };
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = rows.Count,
            Rows = rows
        };
    }

    private QueryResult Compact()
    {
        // In real RocksDB, this would trigger compaction
        return new QueryResult
        {
            Success = true,
            RowsAffected = 0,
            Rows = [new Dictionary<string, object?> { ["status"] = "compaction_triggered" }]
        };
    }

    private string GetKeyFilePath(string key)
    {
        var safeKey = Convert.ToHexString(Encoding.UTF8.GetBytes(key));
        return Path.Combine(_databasePath, "data", $"{safeKey}.dat");
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
        return Task.FromResult(Guid.NewGuid().ToString("N"));
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
    protected override Task<bool> PingCoreAsync(CancellationToken ct)
    {
        return Task.FromResult(_isOpen && Directory.Exists(_databasePath));
    }

    /// <inheritdoc/>
    protected override Task CleanupConnectionAsync()
    {
        _isOpen = false;
        return base.CleanupConnectionAsync();
    }
}

/// <summary>
/// Berkeley DB protocol strategy.
/// Classic embedded key-value database.
/// </summary>
public sealed class BerkeleyDbProtocolStrategy : DatabaseProtocolStrategyBase
{
    private string _databasePath = "";
    private string _databaseType = "btree";
    private bool _isOpen;

    /// <inheritdoc/>
    public override string StrategyId => "berkeleydb-native";

    /// <inheritdoc/>
    public override string StrategyName => "Berkeley DB Native Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Berkeley DB Native",
        ProtocolVersion = "18.x",
        DefaultPort = 0,
        Family = ProtocolFamily.Embedded,
        MaxPacketSize = int.MaxValue,
        Capabilities = new ProtocolCapabilities
        {
            // BerkeleyDb transaction support is not yet wired (Begin/Commit/Rollback are no-ops).
            // Set false to prevent callers from relying on transaction semantics. (finding 2689)
            SupportsTransactions = false,
            SupportsPreparedStatements = false,
            SupportsCursors = true,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = false,
            SupportsCompression = false,
            SupportsMultiplexing = false,
            SupportsServerCursors = false,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = false,
            SupportedAuthMethods =
            [
                AuthenticationMethod.None
            ]
        }
    };

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        _databasePath = parameters.Database ?? "./berkeleydb";
        _databaseType = parameters.ExtendedProperties?.GetValueOrDefault("Type")?.ToString() ?? "btree";
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        if (!Directory.Exists(_databasePath))
        {
            Directory.CreateDirectory(_databasePath);
        }

        _isOpen = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (!_isOpen)
            throw new InvalidOperationException("Not connected");

        var parts = query.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return Task.FromResult(new QueryResult
            {
                Success = false,
                ErrorMessage = "Empty query"
            });
        }

        var command = parts[0].ToUpperInvariant();
        var key = parts.Length > 1 ? parts[1] : parameters?.GetValueOrDefault("key")?.ToString() ?? "";

        return Task.FromResult(command switch
        {
            "GET" => GetValue(key),
            "PUT" or "SET" => PutValue(key, parameters?.GetValueOrDefault("value")?.ToString() ?? ""),
            "DELETE" or "DEL" => DeleteValue(key),
            "CURSOR" => ScanAll(parameters),
            _ => new QueryResult
            {
                Success = false,
                ErrorMessage = $"Unknown command: {command}"
            }
        });
    }

    private QueryResult GetValue(string key)
    {
        var filePath = GetKeyFilePath(key);

        if (!File.Exists(filePath))
        {
            return new QueryResult { Success = true, RowsAffected = 0, Rows = [] };
        }

        var value = File.ReadAllText(filePath);
        return new QueryResult
        {
            Success = true,
            RowsAffected = 1,
            Rows = [new Dictionary<string, object?> { ["key"] = key, ["value"] = value }]
        };
    }

    private QueryResult PutValue(string key, string value)
    {
        var filePath = GetKeyFilePath(key);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(filePath, value);
        return new QueryResult { Success = true, RowsAffected = 1 };
    }

    private QueryResult DeleteValue(string key)
    {
        var filePath = GetKeyFilePath(key);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            return new QueryResult { Success = true, RowsAffected = 1 };
        }
        return new QueryResult { Success = true, RowsAffected = 0 };
    }

    private QueryResult ScanAll(IReadOnlyDictionary<string, object?>? parameters)
    {
        var limit = int.TryParse(parameters?.GetValueOrDefault("limit")?.ToString(), out var l) ? l : 100;
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var dataDir = Path.Combine(_databasePath, "data");

        if (Directory.Exists(dataDir))
        {
            foreach (var file in Directory.GetFiles(dataDir, "*.dat").Take(limit))
            {
                var key = Encoding.UTF8.GetString(Convert.FromHexString(Path.GetFileNameWithoutExtension(file)));
                var value = File.ReadAllText(file);
                rows.Add(new Dictionary<string, object?> { ["key"] = key, ["value"] = value });
            }
        }

        return new QueryResult { Success = true, RowsAffected = rows.Count, Rows = rows };
    }

    private string GetKeyFilePath(string key)
    {
        var safeKey = Convert.ToHexString(Encoding.UTF8.GetBytes(key));
        return Path.Combine(_databasePath, "data", $"{safeKey}.dat");
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
        return Task.FromResult(Guid.NewGuid().ToString("N"));
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
    protected override Task<bool> PingCoreAsync(CancellationToken ct)
    {
        return Task.FromResult(_isOpen && Directory.Exists(_databasePath));
    }

    /// <inheritdoc/>
    protected override Task CleanupConnectionAsync()
    {
        _isOpen = false;
        return base.CleanupConnectionAsync();
    }
}
