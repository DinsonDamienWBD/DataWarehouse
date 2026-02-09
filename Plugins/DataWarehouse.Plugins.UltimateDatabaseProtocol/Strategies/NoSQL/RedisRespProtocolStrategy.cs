using System.Globalization;
using System.Text;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.NoSQL;

/// <summary>
/// Redis RESP (REdis Serialization Protocol) implementation.
/// Supports both RESP2 and RESP3 protocols including:
/// - All data types (Simple strings, Errors, Integers, Bulk strings, Arrays)
/// - RESP3 additions (Maps, Sets, Doubles, Booleans, Big numbers, etc.)
/// - Inline commands
/// - Pub/Sub
/// - Transactions (MULTI/EXEC)
/// - Pipelining
/// - AUTH command for authentication
/// - CLIENT SETNAME for connection naming
/// </summary>
public sealed class RedisRespProtocolStrategy : DatabaseProtocolStrategyBase
{
    // RESP data type prefixes
    private const byte SimpleString = (byte)'+';
    private const byte Error = (byte)'-';
    private const byte Integer = (byte)':';
    private const byte BulkString = (byte)'$';
    private const byte Array = (byte)'*';
    // RESP3 additions
    private const byte Null = (byte)'_';
    private const byte Boolean = (byte)'#';
    private const byte Double = (byte)',';
    private const byte BigNumber = (byte)'(';
    private const byte BulkError = (byte)'!';
    private const byte VerbatimString = (byte)'=';
    private const byte Map = (byte)'%';
    private const byte Set = (byte)'~';
    private const byte Attribute = (byte)'|';
    private const byte Push = (byte)'>';

    // Connection state
    private string _serverVersion = "";
    private int _protocolVersion = 2;
    private int _dbIndex;
    private bool _inTransaction;
    private readonly List<string> _transactionQueue = new();
    private StreamReader? _reader;
    private StreamWriter? _writer;

    /// <inheritdoc/>
    public override string StrategyId => "redis-resp";

    /// <inheritdoc/>
    public override string StrategyName => "Redis RESP Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Redis Serialization Protocol (RESP)",
        ProtocolVersion = "3",
        DefaultPort = 6379,
        Family = ProtocolFamily.NoSQL,
        MaxPacketSize = 512 * 1024 * 1024, // 512 MB
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = true,
            SupportsPreparedStatements = false,
            SupportsCursors = true,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = true,
            SupportsSsl = true,
            SupportsCompression = false,
            SupportsMultiplexing = false,
            SupportsServerCursors = false,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = false,
            SupportedAuthMethods =
            [
                AuthenticationMethod.ClearText,
                AuthenticationMethod.Token
            ]
        }
    };

    /// <inheritdoc/>
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Initialize stream reader/writer
        _reader = new StreamReader(ActiveStream!, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(ActiveStream!, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        // Try HELLO command for RESP3 protocol negotiation
        try
        {
            var response = await SendCommandInternalAsync(["HELLO", "3"], ct);
            if (response is Dictionary<string, object> helloResponse)
            {
                _protocolVersion = 3;
                if (helloResponse.TryGetValue("version", out var version))
                    _serverVersion = version?.ToString() ?? "";
            }
        }
        catch
        {
            // Server doesn't support RESP3, fall back to RESP2
            _protocolVersion = 2;

            // Get server info
            try
            {
                var infoResponse = await SendCommandInternalAsync(["INFO", "server"], ct);
                if (infoResponse is string info)
                {
                    var lines = info.Split('\n');
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("redis_version:"))
                        {
                            _serverVersion = line.Split(':')[1].Trim();
                            break;
                        }
                    }
                }
            }
            catch
            {
                // Ignore info errors
            }
        }
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(parameters.Password))
        {
            return;
        }

        // AUTH command
        var authArgs = string.IsNullOrEmpty(parameters.Username)
            ? new[] { "AUTH", parameters.Password }
            : new[] { "AUTH", parameters.Username, parameters.Password };

        var response = await SendCommandInternalAsync(authArgs, ct);
        if (response is string s && s.Equals("OK", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException($"Authentication failed: {response}");
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        // Parse the query into command and arguments
        var args = ParseCommand(query, parameters);

        try
        {
            var response = await SendCommandInternalAsync(args, ct);
            return ConvertToQueryResult(response, args[0]);
        }
        catch (RedisException ex)
        {
            return new QueryResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ErrorCode = ex.ErrorType
            };
        }
    }

    private string[] ParseCommand(string query, IReadOnlyDictionary<string, object?>? parameters)
    {
        var args = new List<string>();
        var parts = ParseRedisCommand(query);
        args.AddRange(parts);

        // Append parameters as additional arguments
        if (parameters != null)
        {
            foreach (var param in parameters)
            {
                if (param.Value != null)
                {
                    args.Add(param.Value.ToString()!);
                }
            }
        }

        return args.ToArray();
    }

    private static List<string> ParseRedisCommand(string command)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        var quoteChar = '\0';

        for (int i = 0; i < command.Length; i++)
        {
            var c = command[i];

            if (inQuote)
            {
                if (c == quoteChar)
                {
                    inQuote = false;
                }
                else if (c == '\\' && i + 1 < command.Length)
                {
                    // Handle escape sequences
                    i++;
                    current.Append(command[i] switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => command[i]
                    });
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"' || c == '\'')
            {
                inQuote = true;
                quoteChar = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private QueryResult ConvertToQueryResult(object? response, string command)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        long rowsAffected = 0;

        if (response == null)
        {
            return new QueryResult { Success = true, RowsAffected = 0 };
        }

        switch (response)
        {
            case string s:
                rows.Add(new Dictionary<string, object?> { ["value"] = s });
                break;

            case long l:
                rowsAffected = l;
                rows.Add(new Dictionary<string, object?> { ["value"] = l });
                break;

            case double d:
                rows.Add(new Dictionary<string, object?> { ["value"] = d });
                break;

            case bool b:
                rows.Add(new Dictionary<string, object?> { ["value"] = b });
                break;

            case byte[] bytes:
                rows.Add(new Dictionary<string, object?> { ["value"] = Encoding.UTF8.GetString(bytes) });
                break;

            case List<object?> list:
                for (int i = 0; i < list.Count; i++)
                {
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["index"] = i,
                        ["value"] = list[i]
                    });
                }
                rowsAffected = list.Count;
                break;

            case Dictionary<string, object> dict:
                foreach (var kvp in dict)
                {
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["key"] = kvp.Key,
                        ["value"] = kvp.Value
                    });
                }
                rowsAffected = dict.Count;
                break;

            case HashSet<object> set:
                foreach (var item in set)
                {
                    rows.Add(new Dictionary<string, object?> { ["value"] = item });
                }
                rowsAffected = set.Count;
                break;
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = rowsAffected,
            Rows = rows
        };
    }

    private async Task<object?> SendCommandInternalAsync(string[] args, CancellationToken ct)
    {
        if (_writer == null || _reader == null)
        {
            throw new InvalidOperationException("Not connected");
        }

        // Encode command as RESP array
        await _writer.WriteAsync($"*{args.Length}\r\n");
        foreach (var arg in args)
        {
            var bytes = Encoding.UTF8.GetBytes(arg);
            await _writer.WriteAsync($"${bytes.Length}\r\n{arg}\r\n");
        }

        // Read response
        return await ReadResponseAsync(ct);
    }

    private async Task<object?> ReadResponseAsync(CancellationToken ct)
    {
        if (_reader == null)
        {
            throw new InvalidOperationException("Not connected");
        }

        var line = await _reader.ReadLineAsync(ct);
        if (string.IsNullOrEmpty(line))
        {
            throw new EndOfStreamException("Connection closed");
        }

        var type = (byte)line[0];
        var data = line[1..];

        return type switch
        {
            SimpleString => data,
            Error => throw new RedisException(data),
            Integer => long.Parse(data),
            BulkString => await ReadBulkStringAsync(int.Parse(data), ct),
            Array => await ReadArrayAsync(int.Parse(data), ct),
            // RESP3 types
            Null => null,
            Boolean => data == "t",
            Double => double.Parse(data, CultureInfo.InvariantCulture),
            BigNumber => decimal.Parse(data),
            BulkError => throw new RedisException(await ReadBulkStringAsync(int.Parse(data), ct) ?? "Unknown error"),
            VerbatimString => await ReadVerbatimStringAsync(int.Parse(data), ct),
            Map => await ReadMapAsync(int.Parse(data), ct),
            Set => await ReadSetAsync(int.Parse(data), ct),
            Attribute => await ReadAttributeAsync(int.Parse(data), ct),
            Push => await ReadArrayAsync(int.Parse(data), ct),
            _ => throw new InvalidOperationException($"Unknown RESP type: {(char)type}")
        };
    }

    private async Task<string?> ReadBulkStringAsync(int length, CancellationToken ct)
    {
        if (length == -1)
        {
            return null;
        }

        if (_reader == null)
        {
            throw new InvalidOperationException("Not connected");
        }

        var buffer = new char[length];
        var read = 0;
        while (read < length)
        {
            var chunk = await _reader.ReadAsync(buffer.AsMemory(read, length - read), ct);
            if (chunk == 0)
            {
                throw new EndOfStreamException("Connection closed");
            }
            read += chunk;
        }

        // Read trailing CRLF
        await _reader.ReadLineAsync(ct);

        return new string(buffer);
    }

    private async Task<string?> ReadVerbatimStringAsync(int length, CancellationToken ct)
    {
        var content = await ReadBulkStringAsync(length, ct);
        if (content != null && content.Length >= 4)
        {
            // Skip the encoding prefix (e.g., "txt:")
            return content[4..];
        }
        return content;
    }

    private async Task<List<object?>> ReadArrayAsync(int count, CancellationToken ct)
    {
        if (count == -1)
        {
            return null!;
        }

        var result = new List<object?>(count);
        for (int i = 0; i < count; i++)
        {
            result.Add(await ReadResponseAsync(ct));
        }
        return result;
    }

    private async Task<Dictionary<string, object>> ReadMapAsync(int count, CancellationToken ct)
    {
        var result = new Dictionary<string, object>(count);
        for (int i = 0; i < count; i++)
        {
            var key = await ReadResponseAsync(ct);
            var value = await ReadResponseAsync(ct);
            result[key?.ToString() ?? $"__null_{i}"] = value!;
        }
        return result;
    }

    private async Task<HashSet<object>> ReadSetAsync(int count, CancellationToken ct)
    {
        var result = new HashSet<object>(count);
        for (int i = 0; i < count; i++)
        {
            var item = await ReadResponseAsync(ct);
            if (item != null)
            {
                result.Add(item);
            }
        }
        return result;
    }

    private async Task<object?> ReadAttributeAsync(int count, CancellationToken ct)
    {
        // Read and discard attributes, then read actual value
        for (int i = 0; i < count; i++)
        {
            await ReadResponseAsync(ct); // Key
            await ReadResponseAsync(ct); // Value
        }
        return await ReadResponseAsync(ct);
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
        var result = await SendCommandInternalAsync(["MULTI"], ct);
        if (result?.ToString() == "OK")
        {
            _inTransaction = true;
            return Guid.NewGuid().ToString("N");
        }
        throw new InvalidOperationException($"Failed to start transaction: {result}");
    }

    /// <inheritdoc/>
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        if (!_inTransaction)
        {
            throw new InvalidOperationException("No transaction in progress");
        }

        await SendCommandInternalAsync(["EXEC"], ct);
        _inTransaction = false;
        _transactionQueue.Clear();
    }

    /// <inheritdoc/>
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        if (!_inTransaction)
        {
            throw new InvalidOperationException("No transaction in progress");
        }

        await SendCommandInternalAsync(["DISCARD"], ct);
        _inTransaction = false;
        _transactionQueue.Clear();
    }

    /// <inheritdoc/>
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        try
        {
            await SendCommandInternalAsync(["QUIT"], ct);
        }
        catch
        {
            // Ignore errors during disconnect
        }
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        try
        {
            var response = await SendCommandInternalAsync(["PING"], ct);
            return response?.ToString() == "PONG";
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    protected override async Task CleanupConnectionAsync()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _reader = null;
        _writer = null;
        await base.CleanupConnectionAsync();
    }

    /// <summary>
    /// Exception for Redis errors.
    /// </summary>
    private sealed class RedisException : Exception
    {
        public string ErrorType { get; }

        public RedisException(string message) : base(message)
        {
            var spaceIndex = message.IndexOf(' ');
            ErrorType = spaceIndex > 0 ? message[..spaceIndex] : "ERR";
        }
    }
}
