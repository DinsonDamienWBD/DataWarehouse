using System.Text;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.Graph;

/// <summary>
/// Neo4j Bolt Protocol v4.x/v5.x implementation.
/// Implements the Bolt binary protocol for Neo4j including:
/// - Protocol handshake and version negotiation
/// - Authentication
/// - Cypher query execution
/// - Transaction management (explicit and auto-commit)
/// - Result streaming
/// - PackStream encoding/decoding
/// </summary>
public sealed class Neo4jBoltProtocolStrategy : DatabaseProtocolStrategyBase
{
    // Bolt magic preamble
    private static readonly byte[] BoltMagic = [0x60, 0x60, 0xB0, 0x17];

    // Message signatures
    private const byte MsgHello = 0x01;
    private const byte MsgGoodbye = 0x02;
    private const byte MsgReset = 0x0F;
    private const byte MsgRun = 0x10;
    private const byte MsgDiscard = 0x2F;
    private const byte MsgPull = 0x3F;
    private const byte MsgBegin = 0x11;
    private const byte MsgCommit = 0x12;
    private const byte MsgRollback = 0x13;
    private const byte MsgRoute = 0x66;
    private const byte MsgLogon = 0x6A;
    private const byte MsgLogoff = 0x6B;

    // Response signatures
    private const byte MsgSuccess = 0x70;
    private const byte MsgRecord = 0x71;
    private const byte MsgIgnored = 0x7E;
    private const byte MsgFailure = 0x7F;

    // PackStream markers
    private const byte TinyString = 0x80;
    private const byte TinyList = 0x90;
    private const byte TinyMap = 0xA0;
    private const byte TinyStruct = 0xB0;
    private const byte Null = 0xC0;
    private const byte Float64 = 0xC1;
    private const byte False = 0xC2;
    private const byte True = 0xC3;
    private const byte Int8 = 0xC8;
    private const byte Int16 = 0xC9;
    private const byte Int32 = 0xCA;
    private const byte Int64 = 0xCB;
    private const byte Bytes8 = 0xCC;
    private const byte Bytes16 = 0xCD;
    private const byte Bytes32 = 0xCE;
    private const byte String8 = 0xD0;
    private const byte String16 = 0xD1;
    private const byte String32 = 0xD2;
    private const byte List8 = 0xD4;
    private const byte List16 = 0xD5;
    private const byte List32 = 0xD6;
    private const byte Map8 = 0xD8;
    private const byte Map16 = 0xD9;
    private const byte Map32 = 0xDA;
    private const byte Struct8 = 0xDC;
    private const byte Struct16 = 0xDD;

    // Bolt structure tags
    private const byte NodeTag = (byte)'N';
    private const byte RelationshipTag = (byte)'R';
    private const byte UnboundRelationshipTag = (byte)'r';
    private const byte PathTag = (byte)'P';
    private const byte DateTag = (byte)'D';
    private const byte TimeTag = (byte)'T';
    private const byte LocalTimeTag = (byte)'t';
    private const byte DateTimeTag = (byte)'F';
    private const byte DateTimeZoneIdTag = (byte)'f';
    private const byte LocalDateTimeTag = (byte)'d';
    private const byte DurationTag = (byte)'E';
    private const byte Point2DTag = (byte)'X';
    private const byte Point3DTag = (byte)'Y';

    // State
    private int _protocolVersion = 0x00040004; // Bolt 4.4
    private string _serverAgent = "";
    private string _connectionId = "";
    private bool _inTransaction;
    private readonly List<ColumnMetadata> _currentFields = new();

    /// <inheritdoc/>
    public override string StrategyId => "neo4j-bolt";

    /// <inheritdoc/>
    public override string StrategyName => "Neo4j Bolt Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Neo4j Bolt Protocol",
        ProtocolVersion = "4.4",
        DefaultPort = 7687,
        Family = ProtocolFamily.Graph,
        MaxPacketSize = 64 * 1024 * 1024, // 64 MB
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
                AuthenticationMethod.ClearText,
                AuthenticationMethod.Token,
                AuthenticationMethod.Kerberos
            ]
        }
    };

    /// <inheritdoc/>
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Send Bolt magic preamble
        await SendAsync(BoltMagic, ct);

        // Send supported protocol versions (4 x 4 bytes)
        // Format: major.minor (2 bytes each, big-endian)
        var versionProposal = new byte[16];
        // Bolt 5.0
        WriteInt32BE(versionProposal.AsSpan(0, 4), 0x00050000);
        // Bolt 4.4
        WriteInt32BE(versionProposal.AsSpan(4, 4), 0x00040004);
        // Bolt 4.3
        WriteInt32BE(versionProposal.AsSpan(8, 4), 0x00040003);
        // Bolt 4.0
        WriteInt32BE(versionProposal.AsSpan(12, 4), 0x00040000);

        await SendAsync(versionProposal, ct);

        // Read selected version
        var selectedVersion = await ReceiveExactAsync(4, ct);
        _protocolVersion = ReadInt32BE(selectedVersion.AsSpan());

        if (_protocolVersion == 0)
        {
            throw new InvalidOperationException("Server rejected all protocol versions");
        }
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Send HELLO message
        var helloParams = new Dictionary<string, object>
        {
            ["user_agent"] = "DataWarehouse.UltimateDatabaseProtocol/1.0.0",
            ["routing"] = new Dictionary<string, object>(),
            ["scheme"] = "basic",
            ["principal"] = parameters.Username ?? "neo4j",
            ["credentials"] = parameters.Password ?? ""
        };

        await SendMessageAsync(MsgHello, helloParams, ct);

        // Read response
        var (signature, metadata) = await ReadMessageAsync(ct);

        if (signature == MsgFailure)
        {
            var errorMessage = metadata.TryGetValue("message", out var msg) ? msg?.ToString() : "Unknown error";
            throw new InvalidOperationException($"Authentication failed: {errorMessage}");
        }

        if (signature == MsgSuccess)
        {
            if (metadata.TryGetValue("server", out var server))
                _serverAgent = server?.ToString() ?? "";
            if (metadata.TryGetValue("connection_id", out var connId))
                _connectionId = connId?.ToString() ?? "";
        }
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        // Send RUN message
        var runParams = new Dictionary<string, object>
        {
            ["query"] = query,
            ["parameters"] = parameters ?? new Dictionary<string, object?>()
        };

        var extra = new Dictionary<string, object>();
        if (!_inTransaction)
        {
            extra["mode"] = "w"; // Write mode
        }

        await SendRunMessageAsync(query, parameters ?? new Dictionary<string, object?>(), extra, ct);

        // Read RUN response
        var (signature, metadata) = await ReadMessageAsync(ct);

        if (signature == MsgFailure)
        {
            var errorMessage = metadata.TryGetValue("message", out var msg) ? msg?.ToString() : "Unknown error";
            var errorCode = metadata.TryGetValue("code", out var code) ? code?.ToString() : "";
            return new QueryResult
            {
                Success = false,
                ErrorMessage = errorMessage,
                ErrorCode = errorCode
            };
        }

        // Parse field names
        _currentFields.Clear();
        if (metadata.TryGetValue("fields", out var fields) && fields is List<object> fieldList)
        {
            for (int i = 0; i < fieldList.Count; i++)
            {
                _currentFields.Add(new ColumnMetadata
                {
                    Name = fieldList[i]?.ToString() ?? $"column_{i}",
                    DataType = "any",
                    Ordinal = i
                });
            }
        }

        // Send PULL message to get results
        await SendPullMessageAsync(-1, ct);

        // Read records
        var rows = new List<Dictionary<string, object?>>();
        long? resultAvailableAfter = null;
        long? resultConsumedAfter = null;

        while (true)
        {
            (signature, metadata) = await ReadMessageAsync(ct);

            if (signature == MsgRecord && metadata.TryGetValue("values", out var values) && values is List<object> valueList)
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < Math.Min(valueList.Count, _currentFields.Count); i++)
                {
                    row[_currentFields[i].Name] = ConvertValue(valueList[i]);
                }
                rows.Add(row);
            }
            else if (signature == MsgSuccess)
            {
                if (metadata.TryGetValue("t_first", out var tFirst))
                    resultAvailableAfter = Convert.ToInt64(tFirst);
                if (metadata.TryGetValue("t_last", out var tLast))
                    resultConsumedAfter = Convert.ToInt64(tLast);
                break;
            }
            else if (signature == MsgFailure)
            {
                var errorMessage = metadata.TryGetValue("message", out var msg) ? msg?.ToString() : "Unknown error";
                return new QueryResult
                {
                    Success = false,
                    ErrorMessage = errorMessage,
                    Rows = rows.Select(r => (IReadOnlyDictionary<string, object?>)r).ToList(),
                    Columns = _currentFields.ToList()
                };
            }
        }

        return new QueryResult
        {
            Success = true,
            Rows = rows.Select(r => (IReadOnlyDictionary<string, object?>)r).ToList(),
            Columns = _currentFields.ToList(),
            RowsAffected = rows.Count,
            Metadata = new Dictionary<string, object>
            {
                ["result_available_after"] = resultAvailableAfter ?? 0,
                ["result_consumed_after"] = resultConsumedAfter ?? 0
            }
        };
    }

    private object? ConvertValue(object? value)
    {
        return value switch
        {
            Dictionary<string, object> dict when dict.ContainsKey("__type__") => ConvertStructure(dict),
            Dictionary<string, object> dict => dict,
            List<object> list => list.Select(ConvertValue).ToList(),
            _ => value
        };
    }

    private object? ConvertStructure(Dictionary<string, object> structure)
    {
        var type = structure["__type__"]?.ToString();
        return type switch
        {
            "Node" => new
            {
                Id = structure.GetValueOrDefault("id"),
                Labels = structure.GetValueOrDefault("labels"),
                Properties = structure.GetValueOrDefault("properties")
            },
            "Relationship" => new
            {
                Id = structure.GetValueOrDefault("id"),
                StartNodeId = structure.GetValueOrDefault("startNodeId"),
                EndNodeId = structure.GetValueOrDefault("endNodeId"),
                Type = structure.GetValueOrDefault("type"),
                Properties = structure.GetValueOrDefault("properties")
            },
            "Path" => structure,
            "Date" or "Time" or "DateTime" or "LocalTime" or "LocalDateTime" or "Duration" => structure.GetValueOrDefault("value"),
            "Point" => structure,
            _ => structure
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
        var extra = new Dictionary<string, object>
        {
            ["mode"] = "w"
        };

        await SendMessageAsync(MsgBegin, extra, ct);

        var (signature, metadata) = await ReadMessageAsync(ct);
        if (signature != MsgSuccess)
        {
            throw new InvalidOperationException("Failed to begin transaction");
        }

        _inTransaction = true;
        return Guid.NewGuid().ToString("N");
    }

    /// <inheritdoc/>
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await SendMessageAsync(MsgCommit, new Dictionary<string, object>(), ct);

        var (signature, metadata) = await ReadMessageAsync(ct);
        if (signature != MsgSuccess)
        {
            throw new InvalidOperationException("Failed to commit transaction");
        }

        _inTransaction = false;
    }

    /// <inheritdoc/>
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await SendMessageAsync(MsgRollback, new Dictionary<string, object>(), ct);

        var (signature, rollbackMeta) = await ReadMessageAsync(ct);
        // P2-2708: only clear _inTransaction when rollback is confirmed. If the server
        // returns a FAILURE response, the transaction may still be active on the server side.
        if (signature == MsgSuccess)
        {
            _inTransaction = false;
        }
        else
        {
            // Rollback failed — transaction may still be active on the server.
            // Throw so the caller does not reuse this connection silently.
            var errMsg = rollbackMeta.TryGetValue("message", out var m) ? m?.ToString() : "Rollback rejected by server";
            throw new InvalidOperationException($"[Neo4j] Rollback failed: {errMsg}. Connection must be discarded.");
        }
    }

    /// <inheritdoc/>
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        try
        {
            await SendMessageAsync(MsgGoodbye, new Dictionary<string, object>(), ct);
        }
        catch
        {

            // Ignore errors during disconnect
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        var result = await ExecuteQueryCoreAsync("RETURN 1 AS value", null, ct);
        return result.Success;
    }

    #region Bolt Message Protocol

    private async Task SendRunMessageAsync(string query, IReadOnlyDictionary<string, object?> parameters, Dictionary<string, object> extra, CancellationToken ct)
    {
        using var ms = new MemoryStream(4096);

        // Structure marker: TINY_STRUCT + 3 fields
        ms.WriteByte(TinyStruct | 3);
        ms.WriteByte(MsgRun);

        // Query string
        PackString(ms, query);

        // Parameters map
        PackMap(ms, parameters.ToDictionary(k => k.Key, v => v.Value ?? DBNull.Value));

        // Extra map
        PackMap(ms, extra.ToDictionary(k => k.Key, v => (object)v.Value));

        await SendChunkedMessageAsync(ms.ToArray(), ct);
    }

    private async Task SendPullMessageAsync(long n, CancellationToken ct)
    {
        using var ms = new MemoryStream(4096);

        ms.WriteByte(TinyStruct | 1);
        ms.WriteByte(MsgPull);

        // Extra map with n parameter
        var extra = new Dictionary<string, object> { ["n"] = n };
        PackMap(ms, extra);

        await SendChunkedMessageAsync(ms.ToArray(), ct);
    }

    private async Task SendMessageAsync(byte signature, Dictionary<string, object> fields, CancellationToken ct)
    {
        using var ms = new MemoryStream(4096);

        ms.WriteByte(TinyStruct | 1);
        ms.WriteByte(signature);
        PackMap(ms, fields);

        await SendChunkedMessageAsync(ms.ToArray(), ct);
    }

    private async Task SendChunkedMessageAsync(byte[] message, CancellationToken ct)
    {
        // Bolt uses chunked encoding: each chunk is prefixed with 2-byte big-endian length
        // Message end is marked by a zero-length chunk

        var offset = 0;
        const int maxChunkSize = 65535;

        while (offset < message.Length)
        {
            var chunkSize = Math.Min(maxChunkSize, message.Length - offset);
            var header = new byte[2];
            header[0] = (byte)(chunkSize >> 8);
            header[1] = (byte)chunkSize;

            await SendAsync(header, ct);
            await SendAsync(message.AsMemory(offset, chunkSize), ct);
            offset += chunkSize;
        }

        // End of message marker
        await SendAsync(new byte[2], ct);
    }

    private async Task<(byte signature, Dictionary<string, object> metadata)> ReadMessageAsync(CancellationToken ct)
    {
        // Read chunked message
        using var ms = new MemoryStream(4096);

        while (true)
        {
            var header = await ReceiveExactAsync(2, ct);
            var chunkSize = (header[0] << 8) | header[1];

            if (chunkSize == 0)
            {
                break;
            }

            var chunk = await ReceiveExactAsync(chunkSize, ct);
            ms.Write(chunk);
        }

        var message = ms.ToArray();
        var offset = 0;

        // Unpack structure — P2-2710: validate the marker is a Bolt struct marker (0xB0-0xBF).
        // A non-struct marker means the message was malformed; reading the next byte as a signature
        // would parse garbage. Throw a meaningful exception rather than silently misinterpreting.
        var marker = message[offset++];
        if ((marker & 0xF0) != 0xB0)
            throw new InvalidDataException(
                $"Bolt protocol: expected struct marker (0xB0-0xBF) but got 0x{marker:X2}. Message may be malformed.");
        var fieldCount = marker & 0x0F;
        var signature = message[offset++];

        var metadata = new Dictionary<string, object>();

        if (signature == MsgRecord)
        {
            // RECORD has a single field: list of values
            var values = UnpackValue(message, ref offset);
            metadata["values"] = values!;
        }
        else
        {
            // SUCCESS, FAILURE, IGNORED have a metadata map
            var fields = UnpackValue(message, ref offset);
            if (fields is Dictionary<string, object> map)
            {
                metadata = map;
            }
        }

        return (signature, metadata);
    }

    #endregion

    #region PackStream Encoding

    private static void PackString(MemoryStream ms, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length < 16)
        {
            ms.WriteByte((byte)(TinyString | bytes.Length));
        }
        else if (bytes.Length < 256)
        {
            ms.WriteByte(String8);
            ms.WriteByte((byte)bytes.Length);
        }
        else if (bytes.Length < 65536)
        {
            ms.WriteByte(String16);
            ms.WriteByte((byte)(bytes.Length >> 8));
            ms.WriteByte((byte)bytes.Length);
        }
        else
        {
            ms.WriteByte(String32);
            var len = bytes.Length;
            ms.WriteByte((byte)(len >> 24));
            ms.WriteByte((byte)(len >> 16));
            ms.WriteByte((byte)(len >> 8));
            ms.WriteByte((byte)len);
        }
        ms.Write(bytes);
    }

    private static void PackMap(MemoryStream ms, Dictionary<string, object> map)
    {
        if (map.Count < 16)
        {
            ms.WriteByte((byte)(TinyMap | map.Count));
        }
        else if (map.Count < 256)
        {
            ms.WriteByte(Map8);
            ms.WriteByte((byte)map.Count);
        }
        else if (map.Count < 65536)
        {
            ms.WriteByte(Map16);
            ms.WriteByte((byte)(map.Count >> 8));
            ms.WriteByte((byte)map.Count);
        }
        else
        {
            ms.WriteByte(Map32);
            var count = map.Count;
            ms.WriteByte((byte)(count >> 24));
            ms.WriteByte((byte)(count >> 16));
            ms.WriteByte((byte)(count >> 8));
            ms.WriteByte((byte)count);
        }

        foreach (var kvp in map)
        {
            PackString(ms, kvp.Key);
            PackValue(ms, kvp.Value);
        }
    }

    private static void PackValue(MemoryStream ms, object? value)
    {
        switch (value)
        {
            case null:
            case DBNull:
                ms.WriteByte(Null);
                break;

            case bool b:
                ms.WriteByte(b ? True : False);
                break;

            case long l:
                PackInteger(ms, l);
                break;

            case int i:
                PackInteger(ms, i);
                break;

            case double d:
                ms.WriteByte(Float64);
                var bytes = BitConverter.GetBytes(d);
                if (BitConverter.IsLittleEndian)
                    Array.Reverse(bytes);
                ms.Write(bytes);
                break;

            case string s:
                PackString(ms, s);
                break;

            case Dictionary<string, object> dict:
                PackMap(ms, dict);
                break;

            case IEnumerable<object> list:
                PackList(ms, list.ToList());
                break;

            default:
                PackString(ms, value.ToString() ?? "");
                break;
        }
    }

    private static void PackInteger(MemoryStream ms, long value)
    {
        if (value >= -16 && value <= 127)
        {
            ms.WriteByte((byte)value);
        }
        else if (value >= sbyte.MinValue && value <= sbyte.MaxValue)
        {
            ms.WriteByte(Int8);
            ms.WriteByte((byte)value);
        }
        else if (value >= short.MinValue && value <= short.MaxValue)
        {
            ms.WriteByte(Int16);
            ms.WriteByte((byte)(value >> 8));
            ms.WriteByte((byte)value);
        }
        else if (value >= int.MinValue && value <= int.MaxValue)
        {
            ms.WriteByte(Int32);
            var v = (int)value;
            ms.WriteByte((byte)(v >> 24));
            ms.WriteByte((byte)(v >> 16));
            ms.WriteByte((byte)(v >> 8));
            ms.WriteByte((byte)v);
        }
        else
        {
            ms.WriteByte(Int64);
            ms.WriteByte((byte)(value >> 56));
            ms.WriteByte((byte)(value >> 48));
            ms.WriteByte((byte)(value >> 40));
            ms.WriteByte((byte)(value >> 32));
            ms.WriteByte((byte)(value >> 24));
            ms.WriteByte((byte)(value >> 16));
            ms.WriteByte((byte)(value >> 8));
            ms.WriteByte((byte)value);
        }
    }

    private static void PackList(MemoryStream ms, List<object> list)
    {
        if (list.Count < 16)
        {
            ms.WriteByte((byte)(TinyList | list.Count));
        }
        else if (list.Count < 256)
        {
            ms.WriteByte(List8);
            ms.WriteByte((byte)list.Count);
        }
        else if (list.Count < 65536)
        {
            ms.WriteByte(List16);
            ms.WriteByte((byte)(list.Count >> 8));
            ms.WriteByte((byte)list.Count);
        }
        else
        {
            ms.WriteByte(List32);
            var count = list.Count;
            ms.WriteByte((byte)(count >> 24));
            ms.WriteByte((byte)(count >> 16));
            ms.WriteByte((byte)(count >> 8));
            ms.WriteByte((byte)count);
        }

        foreach (var item in list)
        {
            PackValue(ms, item);
        }
    }

    #endregion

    #region PackStream Decoding

    private object? UnpackValue(byte[] data, ref int offset)
    {
        var marker = data[offset++];

        // Tiny types (high nibble encodes type, low nibble encodes size/value)
        if (marker >= 0x80 && marker < 0x90) // Tiny string
        {
            var length = marker & 0x0F;
            var str = Encoding.UTF8.GetString(data, offset, length);
            offset += length;
            return str;
        }
        if (marker >= 0x90 && marker < 0xA0) // Tiny list
        {
            var count = marker & 0x0F;
            return UnpackList(data, ref offset, count);
        }
        if (marker >= 0xA0 && marker < 0xB0) // Tiny map
        {
            var count = marker & 0x0F;
            return UnpackMap(data, ref offset, count);
        }
        if (marker >= 0xB0 && marker < 0xC0) // Tiny struct
        {
            var count = marker & 0x0F;
            return UnpackStruct(data, ref offset, count);
        }

        // Fixed markers
        return marker switch
        {
            Null => null,
            True => true,
            False => false,
            Float64 => UnpackFloat64(data, ref offset),
            Int8 => (long)(sbyte)data[offset++],
            Int16 => UnpackInt16(data, ref offset),
            Int32 => UnpackInt32(data, ref offset),
            Int64 => UnpackInt64(data, ref offset),
            Bytes8 => UnpackBytes(data, ref offset, data[offset++]),
            Bytes16 => UnpackBytes(data, ref offset, UnpackUInt16(data, ref offset)),
            Bytes32 => UnpackBytes(data, ref offset, (int)UnpackUInt32(data, ref offset)),
            String8 => UnpackString(data, ref offset, data[offset++]),
            String16 => UnpackString(data, ref offset, UnpackUInt16(data, ref offset)),
            String32 => UnpackString(data, ref offset, (int)UnpackUInt32(data, ref offset)),
            List8 => UnpackList(data, ref offset, data[offset++]),
            List16 => UnpackList(data, ref offset, UnpackUInt16(data, ref offset)),
            List32 => UnpackList(data, ref offset, (int)UnpackUInt32(data, ref offset)),
            Map8 => UnpackMap(data, ref offset, data[offset++]),
            Map16 => UnpackMap(data, ref offset, UnpackUInt16(data, ref offset)),
            Map32 => UnpackMap(data, ref offset, (int)UnpackUInt32(data, ref offset)),
            Struct8 => UnpackStruct(data, ref offset, data[offset++]),
            Struct16 => UnpackStruct(data, ref offset, UnpackUInt16(data, ref offset)),
            _ when marker <= 0x7F => marker, // Tiny positive int
            _ when marker >= 0xF0 => (long)(sbyte)marker, // Tiny negative int
            _ => throw new InvalidOperationException($"Unknown PackStream marker: 0x{marker:X2}")
        };
    }

    private double UnpackFloat64(byte[] data, ref int offset)
    {
        var bytes = new byte[8];
        Array.Copy(data, offset, bytes, 0, 8);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        offset += 8;
        return BitConverter.ToDouble(bytes);
    }

    private long UnpackInt16(byte[] data, ref int offset)
    {
        var value = (short)((data[offset] << 8) | data[offset + 1]);
        offset += 2;
        return value;
    }

    private long UnpackInt32(byte[] data, ref int offset)
    {
        var value = (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        offset += 4;
        return value;
    }

    private long UnpackInt64(byte[] data, ref int offset)
    {
        long value = 0;
        for (int i = 0; i < 8; i++)
        {
            value = (value << 8) | data[offset + i];
        }
        offset += 8;
        return value;
    }

    private int UnpackUInt16(byte[] data, ref int offset)
    {
        var value = (data[offset] << 8) | data[offset + 1];
        offset += 2;
        return value;
    }

    private uint UnpackUInt32(byte[] data, ref int offset)
    {
        var value = (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
        offset += 4;
        return value;
    }

    private byte[] UnpackBytes(byte[] data, ref int offset, int length)
    {
        var result = new byte[length];
        Array.Copy(data, offset, result, 0, length);
        offset += length;
        return result;
    }

    private string UnpackString(byte[] data, ref int offset, int length)
    {
        var str = Encoding.UTF8.GetString(data, offset, length);
        offset += length;
        return str;
    }

    private List<object> UnpackList(byte[] data, ref int offset, int count)
    {
        var result = new List<object>(count);
        for (int i = 0; i < count; i++)
        {
            result.Add(UnpackValue(data, ref offset)!);
        }
        return result;
    }

    private Dictionary<string, object> UnpackMap(byte[] data, ref int offset, int count)
    {
        var result = new Dictionary<string, object>(count);
        for (int i = 0; i < count; i++)
        {
            var key = UnpackValue(data, ref offset)?.ToString() ?? "";
            var value = UnpackValue(data, ref offset);
            result[key] = value!;
        }
        return result;
    }

    private object UnpackStruct(byte[] data, ref int offset, int fieldCount)
    {
        var tag = data[offset++];
        var fields = new List<object>();
        for (int i = 0; i < fieldCount; i++)
        {
            fields.Add(UnpackValue(data, ref offset)!);
        }

        // Convert to typed structure based on tag
        return tag switch
        {
            NodeTag when fieldCount >= 3 => new Dictionary<string, object>
            {
                ["__type__"] = "Node",
                ["id"] = fields[0],
                ["labels"] = fields[1],
                ["properties"] = fields[2]
            },
            RelationshipTag when fieldCount >= 5 => new Dictionary<string, object>
            {
                ["__type__"] = "Relationship",
                ["id"] = fields[0],
                ["startNodeId"] = fields[1],
                ["endNodeId"] = fields[2],
                ["type"] = fields[3],
                ["properties"] = fields[4]
            },
            PathTag when fieldCount >= 3 => new Dictionary<string, object>
            {
                ["__type__"] = "Path",
                ["nodes"] = fields[0],
                ["relationships"] = fields[1],
                ["sequence"] = fields[2]
            },
            _ => new Dictionary<string, object>
            {
                ["__type__"] = $"Struct_{tag}",
                ["fields"] = fields
            }
        };
    }

    #endregion
}
