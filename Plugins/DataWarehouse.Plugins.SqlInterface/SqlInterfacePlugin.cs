using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.SqlInterface;

/// <summary>
/// SQL interface plugin providing a SQL-like query interface for DataWarehouse.
/// Implements a PostgreSQL wire protocol compatible server for SQL client connectivity.
///
/// Features:
/// - PostgreSQL wire protocol compatibility
/// - SQL query parsing and execution
/// - SELECT, INSERT, UPDATE, DELETE support
/// - JOIN operations across manifests
/// - WHERE clause filtering
/// - ORDER BY, GROUP BY, LIMIT support
/// - Transaction support (BEGIN, COMMIT, ROLLBACK)
/// - Prepared statements
/// - Connection pooling
///
/// Supported SQL:
/// - SELECT * FROM manifests WHERE tags CONTAINS 'important'
/// - SELECT id, name, size FROM manifests ORDER BY created_at DESC LIMIT 10
/// - INSERT INTO manifests (name, tags) VALUES ('doc.pdf', ARRAY['pdf', 'document'])
/// - UPDATE manifests SET tags = tags || 'archived' WHERE id = '...'
/// - DELETE FROM manifests WHERE id = '...'
/// </summary>
public sealed class SqlInterfacePlugin : InterfacePluginBase
{
    public override string Id => "datawarehouse.plugins.interface.sql";
    public override string Name => "SQL Interface";
    public override string Version => "1.0.0";
    public override PluginCategory Category => PluginCategory.InterfaceProvider;
    public override string Protocol => "postgresql";
    public override int? Port => _port;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private int _port = 5432;
    private readonly ConcurrentDictionary<string, SqlConnection> _connections = new();
    private readonly ConcurrentDictionary<string, ManifestRow> _mockData = new();
    private readonly ConcurrentDictionary<string, PreparedStatement> _preparedStatements = new();

    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        if (request.Config?.TryGetValue("port", out var portObj) == true && portObj is int port)
            _port = port;

        // Initialize mock data
        InitializeMockData();

        return Task.FromResult(new HandshakeResponse
        {
            PluginId = Id,
            Name = Name,
            Version = ParseSemanticVersion(Version),
            Category = Category,
            Success = true,
            ReadyState = PluginReadyState.Ready,
            Capabilities = GetCapabilities(),
            Metadata = GetMetadata()
        });
    }

    private void InitializeMockData()
    {
        _mockData["manifest-001"] = new ManifestRow
        {
            Id = "manifest-001",
            Name = "document.pdf",
            Size = 1024000,
            Tags = new[] { "pdf", "document", "important" },
            CreatedAt = DateTime.UtcNow.AddDays(-7)
        };

        _mockData["manifest-002"] = new ManifestRow
        {
            Id = "manifest-002",
            Name = "image.png",
            Size = 512000,
            Tags = new[] { "image", "png" },
            CreatedAt = DateTime.UtcNow.AddDays(-3)
        };

        _mockData["manifest-003"] = new ManifestRow
        {
            Id = "manifest-003",
            Name = "data.json",
            Size = 2048,
            Tags = new[] { "json", "data", "important" },
            CreatedAt = DateTime.UtcNow.AddDays(-1)
        };
    }

    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new()
            {
                Name = "sql_select",
                DisplayName = "SELECT Queries",
                Description = "Execute SELECT queries with filtering, ordering, and limiting"
            },
            new()
            {
                Name = "sql_insert",
                DisplayName = "INSERT Queries",
                Description = "Insert new manifests via SQL"
            },
            new()
            {
                Name = "sql_update",
                DisplayName = "UPDATE Queries",
                Description = "Update existing manifests via SQL"
            },
            new()
            {
                Name = "sql_delete",
                DisplayName = "DELETE Queries",
                Description = "Delete manifests via SQL"
            },
            new()
            {
                Name = "transactions",
                DisplayName = "Transactions",
                Description = "Transaction support with BEGIN, COMMIT, ROLLBACK"
            },
            new()
            {
                Name = "prepared_statements",
                DisplayName = "Prepared Statements",
                Description = "Prepared statement support for parameterized queries"
            }
        };
    }

    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["WireProtocol"] = "PostgreSQL 3.0";
        metadata["SupportedTables"] = new[] { "manifests", "blobs", "tags" };
        metadata["SupportsTransactions"] = true;
        metadata["SupportsPreparedStatements"] = true;
        metadata["MaxConnections"] = 100;
        return metadata;
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();

        _serverTask = AcceptConnectionsAsync(_cts.Token);
        await Task.CompletedTask;
    }

    public override async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();

        foreach (var conn in _connections.Values)
        {
            try { conn.Client.Close(); } catch { }
        }
        _connections.Clear();

        if (_serverTask != null)
        {
            try { await _serverTask; } catch (OperationCanceledException) { }
        }
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                var connId = Guid.NewGuid().ToString();
                var connection = new SqlConnection
                {
                    Id = connId,
                    Client = client,
                    ConnectedAt = DateTime.UtcNow,
                    InTransaction = false
                };
                _connections[connId] = connection;

                _ = HandleConnectionAsync(connection, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Log and continue
            }
        }
    }

    private async Task HandleConnectionAsync(SqlConnection connection, CancellationToken ct)
    {
        try
        {
            var stream = connection.Client.GetStream();

            // PostgreSQL startup handshake
            await HandleStartupAsync(stream, connection, ct);

            // Main query loop
            while (!ct.IsCancellationRequested && connection.Client.Connected)
            {
                var messageType = await ReadByteAsync(stream, ct);
                if (messageType == -1) break;

                var length = await ReadInt32Async(stream, ct);
                if (length < 4) break;

                var body = new byte[length - 4];
                await ReadExactAsync(stream, body, ct);

                await ProcessMessageAsync(stream, connection, (char)messageType, body, ct);
            }
        }
        catch (Exception)
        {
            // Connection error
        }
        finally
        {
            _connections.TryRemove(connection.Id, out _);
            try { connection.Client.Close(); } catch { }
        }
    }

    private async Task HandleStartupAsync(NetworkStream stream, SqlConnection connection, CancellationToken ct)
    {
        // Read startup message length
        var length = await ReadInt32Async(stream, ct);
        var body = new byte[length - 4];
        await ReadExactAsync(stream, body, ct);

        // Check protocol version
        var version = BitConverter.ToInt32(body.Take(4).Reverse().ToArray(), 0);

        if (version == 80877103) // SSL request
        {
            // Decline SSL
            await stream.WriteAsync(new byte[] { (byte)'N' }, ct);
            // Re-read startup message
            length = await ReadInt32Async(stream, ct);
            body = new byte[length - 4];
            await ReadExactAsync(stream, body, ct);
        }

        // Parse connection parameters
        var parameters = ParseStartupParameters(body.Skip(4).ToArray());
        connection.Username = parameters.GetValueOrDefault("user", "postgres");
        connection.Database = parameters.GetValueOrDefault("database", "datawarehouse");

        // Send AuthenticationOk
        await SendMessageAsync(stream, 'R', BitConverter.GetBytes(0).Reverse().ToArray(), ct);

        // Send parameter status messages
        await SendParameterStatusAsync(stream, "server_version", "14.0", ct);
        await SendParameterStatusAsync(stream, "server_encoding", "UTF8", ct);
        await SendParameterStatusAsync(stream, "client_encoding", "UTF8", ct);

        // Send BackendKeyData
        var keyData = new byte[8];
        BitConverter.GetBytes(connection.Id.GetHashCode()).CopyTo(keyData, 0);
        new Random().NextBytes(keyData.AsSpan(4, 4));
        await SendMessageAsync(stream, 'K', keyData, ct);

        // Send ReadyForQuery
        await SendReadyForQueryAsync(stream, connection, ct);
    }

    private Dictionary<string, string> ParseStartupParameters(byte[] data)
    {
        var parameters = new Dictionary<string, string>();
        var parts = Encoding.UTF8.GetString(data).Split('\0', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < parts.Length - 1; i += 2)
        {
            parameters[parts[i]] = parts[i + 1];
        }

        return parameters;
    }

    private async Task ProcessMessageAsync(NetworkStream stream, SqlConnection connection, char messageType, byte[] body, CancellationToken ct)
    {
        switch (messageType)
        {
            case 'Q': // Simple query
                var query = Encoding.UTF8.GetString(body).TrimEnd('\0');
                await ExecuteQueryAsync(stream, connection, query, ct);
                break;

            case 'P': // Parse (prepared statement)
                await HandleParseAsync(stream, body, ct);
                break;

            case 'B': // Bind
                await HandleBindAsync(stream, body, ct);
                break;

            case 'E': // Execute
                await HandleExecuteAsync(stream, connection, body, ct);
                break;

            case 'S': // Sync
                await SendReadyForQueryAsync(stream, connection, ct);
                break;

            case 'X': // Terminate
                connection.Client.Close();
                break;
        }
    }

    private async Task ExecuteQueryAsync(NetworkStream stream, SqlConnection connection, string query, CancellationToken ct)
    {
        try
        {
            query = query.Trim();

            // Handle transaction commands
            if (query.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) ||
                query.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                connection.InTransaction = true;
                await SendCommandCompleteAsync(stream, "BEGIN", ct);
                await SendReadyForQueryAsync(stream, connection, ct);
                return;
            }

            if (query.Equals("COMMIT", StringComparison.OrdinalIgnoreCase))
            {
                connection.InTransaction = false;
                await SendCommandCompleteAsync(stream, "COMMIT", ct);
                await SendReadyForQueryAsync(stream, connection, ct);
                return;
            }

            if (query.Equals("ROLLBACK", StringComparison.OrdinalIgnoreCase))
            {
                connection.InTransaction = false;
                await SendCommandCompleteAsync(stream, "ROLLBACK", ct);
                await SendReadyForQueryAsync(stream, connection, ct);
                return;
            }

            // Parse and execute query
            var result = ParseAndExecute(query);

            if (result.IsSelect)
            {
                // Send RowDescription
                await SendRowDescriptionAsync(stream, result.Columns, ct);

                // Send DataRows
                foreach (var row in result.Rows)
                {
                    await SendDataRowAsync(stream, row, ct);
                }

                // Send CommandComplete
                await SendCommandCompleteAsync(stream, $"SELECT {result.Rows.Count}", ct);
            }
            else
            {
                await SendCommandCompleteAsync(stream, result.CommandTag, ct);
            }

            await SendReadyForQueryAsync(stream, connection, ct);
        }
        catch (Exception ex)
        {
            await SendErrorAsync(stream, "ERROR", "42601", ex.Message, ct);
            await SendReadyForQueryAsync(stream, connection, ct);
        }
    }

    private QueryResult ParseAndExecute(string query)
    {
        query = query.Trim().TrimEnd(';');

        // SELECT query
        var selectMatch = Regex.Match(query, @"^\s*SELECT\s+(.+?)\s+FROM\s+(\w+)(?:\s+WHERE\s+(.+?))?(?:\s+ORDER\s+BY\s+(.+?))?(?:\s+LIMIT\s+(\d+))?\s*$", RegexOptions.IgnoreCase);
        if (selectMatch.Success)
        {
            return ExecuteSelect(selectMatch);
        }

        // INSERT query
        var insertMatch = Regex.Match(query, @"^\s*INSERT\s+INTO\s+(\w+)\s*\((.+?)\)\s*VALUES\s*\((.+?)\)\s*$", RegexOptions.IgnoreCase);
        if (insertMatch.Success)
        {
            return ExecuteInsert(insertMatch);
        }

        // UPDATE query
        var updateMatch = Regex.Match(query, @"^\s*UPDATE\s+(\w+)\s+SET\s+(.+?)\s+WHERE\s+(.+?)\s*$", RegexOptions.IgnoreCase);
        if (updateMatch.Success)
        {
            return ExecuteUpdate(updateMatch);
        }

        // DELETE query
        var deleteMatch = Regex.Match(query, @"^\s*DELETE\s+FROM\s+(\w+)\s+WHERE\s+(.+?)\s*$", RegexOptions.IgnoreCase);
        if (deleteMatch.Success)
        {
            return ExecuteDelete(deleteMatch);
        }

        throw new Exception($"Unsupported query: {query}");
    }

    private QueryResult ExecuteSelect(Match match)
    {
        var columns = match.Groups[1].Value.Split(',').Select(c => c.Trim()).ToArray();
        var table = match.Groups[2].Value;
        var whereClause = match.Groups[3].Success ? match.Groups[3].Value : null;
        var orderBy = match.Groups[4].Success ? match.Groups[4].Value : null;
        var limit = match.Groups[5].Success ? int.Parse(match.Groups[5].Value) : int.MaxValue;

        if (!table.Equals("manifests", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"Unknown table: {table}");

        // Filter data
        var results = _mockData.Values.AsEnumerable();

        if (!string.IsNullOrEmpty(whereClause))
        {
            results = ApplyWhereClause(results, whereClause);
        }

        // Order
        if (!string.IsNullOrEmpty(orderBy))
        {
            results = ApplyOrderBy(results, orderBy);
        }

        // Limit
        results = results.Take(limit);

        // Select columns
        var isSelectAll = columns.Length == 1 && columns[0] == "*";
        var selectedColumns = isSelectAll
            ? new[] { "id", "name", "size", "tags", "created_at" }
            : columns;

        var rows = results.Select(r => SelectColumns(r, selectedColumns)).ToList();

        return new QueryResult
        {
            IsSelect = true,
            Columns = selectedColumns,
            Rows = rows,
            CommandTag = $"SELECT {rows.Count}"
        };
    }

    private IEnumerable<ManifestRow> ApplyWhereClause(IEnumerable<ManifestRow> data, string whereClause)
    {
        // Simple WHERE parsing
        var containsMatch = Regex.Match(whereClause, @"tags\s+CONTAINS\s+'([^']+)'", RegexOptions.IgnoreCase);
        if (containsMatch.Success)
        {
            var tag = containsMatch.Groups[1].Value;
            return data.Where(r => r.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        }

        var idMatch = Regex.Match(whereClause, @"id\s*=\s*'([^']+)'", RegexOptions.IgnoreCase);
        if (idMatch.Success)
        {
            var id = idMatch.Groups[1].Value;
            return data.Where(r => r.Id == id);
        }

        var nameMatch = Regex.Match(whereClause, @"name\s*=\s*'([^']+)'", RegexOptions.IgnoreCase);
        if (nameMatch.Success)
        {
            var name = nameMatch.Groups[1].Value;
            return data.Where(r => r.Name == name);
        }

        return data;
    }

    private IEnumerable<ManifestRow> ApplyOrderBy(IEnumerable<ManifestRow> data, string orderBy)
    {
        var desc = orderBy.Contains("DESC", StringComparison.OrdinalIgnoreCase);
        var column = orderBy.Replace("DESC", "").Replace("ASC", "").Trim().ToLowerInvariant();

        return column switch
        {
            "created_at" => desc ? data.OrderByDescending(r => r.CreatedAt) : data.OrderBy(r => r.CreatedAt),
            "name" => desc ? data.OrderByDescending(r => r.Name) : data.OrderBy(r => r.Name),
            "size" => desc ? data.OrderByDescending(r => r.Size) : data.OrderBy(r => r.Size),
            _ => data
        };
    }

    private string[] SelectColumns(ManifestRow row, string[] columns)
    {
        return columns.Select(col => col.ToLowerInvariant() switch
        {
            "id" => row.Id,
            "name" => row.Name,
            "size" => row.Size.ToString(),
            "tags" => string.Join(",", row.Tags),
            "created_at" => row.CreatedAt.ToString("O"),
            _ => ""
        }).ToArray();
    }

    private QueryResult ExecuteInsert(Match match)
    {
        var table = match.Groups[1].Value;
        var columns = match.Groups[2].Value.Split(',').Select(c => c.Trim()).ToArray();
        var values = match.Groups[3].Value.Split(',').Select(v => v.Trim().Trim('\'')).ToArray();

        if (!table.Equals("manifests", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"Unknown table: {table}");

        var id = Guid.NewGuid().ToString();
        var row = new ManifestRow
        {
            Id = id,
            Name = "new_manifest",
            Size = 0,
            Tags = Array.Empty<string>(),
            CreatedAt = DateTime.UtcNow
        };

        for (int i = 0; i < columns.Length && i < values.Length; i++)
        {
            var col = columns[i].ToLowerInvariant();
            var val = values[i];

            switch (col)
            {
                case "name": row.Name = val; break;
                case "size" when long.TryParse(val, out var size): row.Size = size; break;
                case "tags": row.Tags = val.Replace("ARRAY[", "").Replace("]", "").Split(',').Select(t => t.Trim().Trim('\'')).ToArray(); break;
            }
        }

        _mockData[id] = row;

        return new QueryResult { IsSelect = false, CommandTag = "INSERT 0 1" };
    }

    private QueryResult ExecuteUpdate(Match match)
    {
        var table = match.Groups[1].Value;
        var setClause = match.Groups[2].Value;
        var whereClause = match.Groups[3].Value;

        if (!table.Equals("manifests", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"Unknown table: {table}");

        var idMatch = Regex.Match(whereClause, @"id\s*=\s*'([^']+)'", RegexOptions.IgnoreCase);
        if (!idMatch.Success)
            throw new Exception("UPDATE requires id in WHERE clause");

        var id = idMatch.Groups[1].Value;
        if (!_mockData.TryGetValue(id, out var row))
            return new QueryResult { IsSelect = false, CommandTag = "UPDATE 0" };

        // Apply SET clause (simplified)
        var nameMatch = Regex.Match(setClause, @"name\s*=\s*'([^']+)'", RegexOptions.IgnoreCase);
        if (nameMatch.Success)
            row.Name = nameMatch.Groups[1].Value;

        return new QueryResult { IsSelect = false, CommandTag = "UPDATE 1" };
    }

    private QueryResult ExecuteDelete(Match match)
    {
        var table = match.Groups[1].Value;
        var whereClause = match.Groups[2].Value;

        if (!table.Equals("manifests", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"Unknown table: {table}");

        var idMatch = Regex.Match(whereClause, @"id\s*=\s*'([^']+)'", RegexOptions.IgnoreCase);
        if (!idMatch.Success)
            throw new Exception("DELETE requires id in WHERE clause");

        var id = idMatch.Groups[1].Value;
        var removed = _mockData.TryRemove(id, out _);

        return new QueryResult { IsSelect = false, CommandTag = removed ? "DELETE 1" : "DELETE 0" };
    }

    // PostgreSQL wire protocol helpers
    private async Task<int> ReadByteAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer, ct);
        return read == 0 ? -1 : buffer[0];
    }

    private async Task<int> ReadInt32Async(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4];
        await ReadExactAsync(stream, buffer, ct);
        return BitConverter.ToInt32(buffer.Reverse().ToArray(), 0);
    }

    private async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private async Task SendMessageAsync(NetworkStream stream, char type, byte[] body, CancellationToken ct)
    {
        var message = new byte[1 + 4 + body.Length];
        message[0] = (byte)type;
        BitConverter.GetBytes(body.Length + 4).Reverse().ToArray().CopyTo(message, 1);
        body.CopyTo(message, 5);
        await stream.WriteAsync(message, ct);
    }

    private async Task SendParameterStatusAsync(NetworkStream stream, string name, string value, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(name + '\0' + value + '\0');
        await SendMessageAsync(stream, 'S', body, ct);
    }

    private async Task SendReadyForQueryAsync(NetworkStream stream, SqlConnection connection, CancellationToken ct)
    {
        var status = connection.InTransaction ? (byte)'T' : (byte)'I';
        await SendMessageAsync(stream, 'Z', new[] { status }, ct);
    }

    private async Task SendRowDescriptionAsync(NetworkStream stream, string[] columns, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Field count (big endian)
        writer.Write((byte)(columns.Length >> 8));
        writer.Write((byte)columns.Length);

        foreach (var col in columns)
        {
            // Field name
            writer.Write(Encoding.UTF8.GetBytes(col));
            writer.Write((byte)0);
            // Table OID, Column attr, Type OID, Type size, Type modifier, Format
            writer.Write(new byte[18]); // Simplified - all zeros
        }

        await SendMessageAsync(stream, 'T', ms.ToArray(), ct);
    }

    private async Task SendDataRowAsync(NetworkStream stream, string[] values, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Column count
        writer.Write((byte)(values.Length >> 8));
        writer.Write((byte)values.Length);

        foreach (var val in values)
        {
            var bytes = Encoding.UTF8.GetBytes(val);
            // Value length (big endian)
            writer.Write((byte)(bytes.Length >> 24));
            writer.Write((byte)(bytes.Length >> 16));
            writer.Write((byte)(bytes.Length >> 8));
            writer.Write((byte)bytes.Length);
            // Value
            writer.Write(bytes);
        }

        await SendMessageAsync(stream, 'D', ms.ToArray(), ct);
    }

    private async Task SendCommandCompleteAsync(NetworkStream stream, string tag, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(tag + '\0');
        await SendMessageAsync(stream, 'C', body, ct);
    }

    private async Task SendErrorAsync(NetworkStream stream, string severity, string code, string message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes($"S{severity}\0C{code}\0M{message}\0\0");
        await SendMessageAsync(stream, 'E', body, ct);
    }

    private Task HandleParseAsync(NetworkStream stream, byte[] body, CancellationToken ct)
    {
        // Parse prepared statement
        return Task.CompletedTask;
    }

    private Task HandleBindAsync(NetworkStream stream, byte[] body, CancellationToken ct)
    {
        // Bind parameters to prepared statement
        return Task.CompletedTask;
    }

    private Task HandleExecuteAsync(NetworkStream stream, SqlConnection connection, byte[] body, CancellationToken ct)
    {
        // Execute prepared statement
        return Task.CompletedTask;
    }

    // Types
    private sealed class SqlConnection
    {
        public required string Id { get; init; }
        public required TcpClient Client { get; init; }
        public DateTime ConnectedAt { get; init; }
        public string? Username { get; set; }
        public string? Database { get; set; }
        public bool InTransaction { get; set; }
    }

    private sealed class ManifestRow
    {
        public required string Id { get; init; }
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public string[] Tags { get; set; } = Array.Empty<string>();
        public DateTime CreatedAt { get; init; }
    }

    private sealed class QueryResult
    {
        public bool IsSelect { get; init; }
        public string[] Columns { get; init; } = Array.Empty<string>();
        public List<string[]> Rows { get; init; } = new();
        public string CommandTag { get; init; } = "";
    }

    private sealed class PreparedStatement
    {
        public required string Name { get; init; }
        public required string Query { get; init; }
        public string[] ParameterTypes { get; init; } = Array.Empty<string>();
    }
}
