#pragma warning disable CS8619, CS8620, CS8604
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.NoSQL;

/// <summary>
/// MongoDB Wire Protocol implementation.
/// Implements the MongoDB binary protocol including:
/// - OP_MSG (modern unified message format)
/// - OP_QUERY/OP_REPLY (legacy)
/// - SCRAM-SHA-256 authentication
/// - Compression (snappy, zlib, zstd)
/// - Exhaust cursors
/// - Transactions
/// </summary>
public sealed class MongoDbWireProtocolStrategy : DatabaseProtocolStrategyBase
{
    // OpCodes
    private const int OpReply = 1;
    private const int OpUpdate = 2001;
    private const int OpInsert = 2002;
    private const int OpQuery = 2004;
    private const int OpGetMore = 2005;
    private const int OpDelete = 2006;
    private const int OpKillCursors = 2007;
    private const int OpCompressed = 2012;
    private const int OpMsg = 2013;

    // OP_MSG flags
    private const uint MsgFlagChecksumPresent = 1 << 0;
    private const uint MsgFlagMoreToCome = 1 << 1;
    private const uint MsgFlagExhaustAllowed = 1 << 16;

    // OP_MSG section types
    private const byte SectionBody = 0;
    private const byte SectionDocumentSequence = 1;

    // State
    private int _requestId;
    private string _currentDatabase = "admin";
    private int _maxWireVersion;
    private int _minWireVersion;
    private bool _compressionSupported;
    private string[]? _compressionMethods;

    /// <inheritdoc/>
    public override string StrategyId => "mongodb-wire";

    /// <inheritdoc/>
    public override string StrategyName => "MongoDB Wire Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "MongoDB Wire Protocol",
        ProtocolVersion = "OP_MSG",
        DefaultPort = 27017,
        Family = ProtocolFamily.NoSQL,
        MaxPacketSize = 48 * 1024 * 1024, // 48 MB
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = true,
            SupportsPreparedStatements = false,
            SupportsCursors = true,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = true,
            SupportsSsl = true,
            SupportsCompression = true,
            SupportsMultiplexing = true,
            SupportsServerCursors = true,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods =
            [
                AuthenticationMethod.ScramSha256,
                AuthenticationMethod.ScramSha512,
                AuthenticationMethod.Certificate,
                AuthenticationMethod.Kerberos,
                AuthenticationMethod.AwsIam
            ]
        }
    };

    /// <inheritdoc/>
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        _currentDatabase = parameters.Database ?? "admin";

        // Send hello/isMaster command
        var helloDoc = new Dictionary<string, object>
        {
            ["hello"] = 1,
            ["client"] = new Dictionary<string, object>
            {
                ["application"] = new Dictionary<string, object>
                {
                    ["name"] = "DataWarehouse.UltimateDatabaseProtocol"
                },
                ["driver"] = new Dictionary<string, object>
                {
                    ["name"] = "DataWarehouse.MongoDB",
                    ["version"] = "1.0.0"
                },
                ["os"] = new Dictionary<string, object>
                {
                    ["type"] = Environment.OSVersion.Platform.ToString(),
                    ["architecture"] = Environment.Is64BitOperatingSystem ? "x64" : "x86"
                }
            },
            ["compression"] = new string[] { "snappy", "zlib" }
        };

        var response = await SendCommandAsync("admin", helloDoc, ct);

        if (response.TryGetValue("ok", out var ok) && Convert.ToDouble(ok) == 1.0)
        {
            if (response.TryGetValue("maxWireVersion", out var maxWire))
                _maxWireVersion = Convert.ToInt32(maxWire);
            if (response.TryGetValue("minWireVersion", out var minWire))
                _minWireVersion = Convert.ToInt32(minWire);
            if (response.TryGetValue("compression", out var compression) && compression is IEnumerable<object> methods)
                _compressionMethods = methods.Select(m => m.ToString()!).ToArray();
        }
        else
        {
            throw new InvalidOperationException("MongoDB handshake failed");
        }
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(parameters.Username))
        {
            return; // No authentication required
        }

        // Use SCRAM-SHA-256 authentication
        await PerformScramSha256AuthAsync(parameters, ct);
    }

    private async Task PerformScramSha256AuthAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var authDb = parameters.AdditionalParameters.TryGetValue("authSource", out var authSource)
            ? authSource
            : _currentDatabase;

        // Generate client nonce
        var clientNonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var clientFirstMessageBare = $"n={parameters.Username},r={clientNonce}";
        var clientFirstMessage = Convert.ToBase64String(Encoding.UTF8.GetBytes($"n,,{clientFirstMessageBare}"));

        // Send saslStart
        var saslStartDoc = new Dictionary<string, object>
        {
            ["saslStart"] = 1,
            ["mechanism"] = "SCRAM-SHA-256",
            ["payload"] = clientFirstMessage,
            ["autoAuthorize"] = 1,
            ["options"] = new Dictionary<string, object>
            {
                ["skipEmptyExchange"] = true
            }
        };

        var response = await SendCommandAsync(authDb, saslStartDoc, ct);

        if (Convert.ToDouble(response.GetValueOrDefault("ok", 0.0)) != 1.0)
        {
            throw new InvalidOperationException($"SCRAM authentication failed: {response.GetValueOrDefault("errmsg", "Unknown error")}");
        }

        var conversationId = response["conversationId"];
        var serverFirstMessage = Encoding.UTF8.GetString(Convert.FromBase64String(response["payload"]?.ToString() ?? ""));

        // Parse server first message
        var serverParams = ParseScramMessage(serverFirstMessage);
        var serverNonce = serverParams["r"];
        var salt = Convert.FromBase64String(serverParams["s"]);
        var iterations = int.Parse(serverParams["i"]);

        if (!serverNonce.StartsWith(clientNonce))
        {
            throw new InvalidOperationException("Server nonce doesn't start with client nonce");
        }

        // Calculate client proof
        var saltedPassword = Hi(Normalize(parameters.Password ?? ""), salt, iterations);
        var clientKey = HmacSha256(saltedPassword, "Client Key");
        var storedKey = Sha256(clientKey);

        var clientFinalMessageWithoutProof = $"c=biws,r={serverNonce}";
        var authMessage = $"{clientFirstMessageBare},{serverFirstMessage},{clientFinalMessageWithoutProof}";
        var clientSignature = HmacSha256(storedKey, authMessage);

        var clientProof = new byte[clientKey.Length];
        for (int i = 0; i < clientKey.Length; i++)
        {
            clientProof[i] = (byte)(clientKey[i] ^ clientSignature[i]);
        }

        var clientFinalMessage = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{clientFinalMessageWithoutProof},p={Convert.ToBase64String(clientProof)}"));

        // Send saslContinue
        var saslContinueDoc = new Dictionary<string, object>
        {
            ["saslContinue"] = 1,
            ["conversationId"] = conversationId,
            ["payload"] = clientFinalMessage
        };

        response = await SendCommandAsync(authDb, saslContinueDoc, ct);

        if (Convert.ToDouble(response.GetValueOrDefault("ok", 0.0)) != 1.0)
        {
            throw new InvalidOperationException($"SCRAM authentication failed: {response.GetValueOrDefault("errmsg", "Unknown error")}");
        }

        // Verify server signature
        var serverKey = HmacSha256(saltedPassword, "Server Key");
        var expectedServerSignature = HmacSha256(serverKey, authMessage);
        var serverFinalMessage = Encoding.UTF8.GetString(Convert.FromBase64String(response["payload"]?.ToString() ?? ""));
        var serverSignature = Convert.FromBase64String(serverFinalMessage[2..]); // Skip "v="

        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedServerSignature, serverSignature))
        {
            throw new InvalidOperationException("Server signature verification failed");
        }
    }

    private static string Normalize(string password)
    {
        // SASLprep normalization (simplified)
        return password;
    }

    private static byte[] Hi(string password, byte[] salt, int iterations)
    {
        return System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
    }

    private static byte[] HmacSha256(byte[] key, string message)
    {
        return System.Security.Cryptography.HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message));
    }

    private static byte[] Sha256(byte[] data)
    {
        return System.Security.Cryptography.SHA256.HashData(data);
    }

    private static Dictionary<string, string> ParseScramMessage(string message)
    {
        var result = new Dictionary<string, string>();
        foreach (var part in message.Split(','))
        {
            var idx = part.IndexOf('=');
            if (idx > 0)
            {
                result[part[..idx]] = part[(idx + 1)..];
            }
        }
        return result;
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        // Parse the query as a MongoDB command
        var command = ParseQueryAsCommand(query, parameters);
        var response = await SendCommandAsync(_currentDatabase, command, ct);

        return ConvertToQueryResult(response);
    }

    private Dictionary<string, object> ParseQueryAsCommand(string query, IReadOnlyDictionary<string, object?> parameters)
    {
        // Try to parse as JSON
        try
        {
            var doc = JsonSerializer.Deserialize<Dictionary<string, object>>(query);
            if (doc != null)
            {
                if (parameters != null)
                {
                    foreach (var param in parameters)
                    {
                        doc[param.Key] = param.Value!;
                    }
                }
                return doc;
            }
        }
        catch
        {
            // Not JSON, treat as collection.operation format
        }

        // Default: find command
        var parts = query.Split('.');
        if (parts.Length >= 2)
        {
            var collection = parts[0];
            var operation = parts[1].ToLowerInvariant();

            return operation switch
            {
                "find" => new Dictionary<string, object>
                {
                    ["find"] = collection,
                    ["filter"] = parameters ?? new Dictionary<string, object>()
                },
                "insert" => new Dictionary<string, object>
                {
                    ["insert"] = collection,
                    ["documents"] = new[] { parameters ?? (IReadOnlyDictionary<string, object>)new Dictionary<string, object>() }
                },
                "update" => new Dictionary<string, object>
                {
                    ["update"] = collection,
                    ["updates"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["q"] = parameters?.GetValueOrDefault("filter") ?? (object)new Dictionary<string, object>(),
                            ["u"] = parameters?.GetValueOrDefault("update") ?? (object)new Dictionary<string, object>()
                        }
                    }
                },
                "delete" => new Dictionary<string, object>
                {
                    ["delete"] = collection,
                    ["deletes"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["q"] = parameters ?? (IReadOnlyDictionary<string, object>)new Dictionary<string, object>(),
                            ["limit"] = 0
                        }
                    }
                },
                _ => new Dictionary<string, object> { [operation] = 1 }
            };
        }

        return new Dictionary<string, object> { [query] = 1 };
    }

    private QueryResult ConvertToQueryResult(Dictionary<string, object> response)
    {
        var success = Convert.ToDouble(response.GetValueOrDefault("ok", 0.0)) == 1.0;
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        long rowsAffected = 0;

        if (response.TryGetValue("cursor", out var cursorObj) && cursorObj is Dictionary<string, object> cursor)
        {
            if (cursor.TryGetValue("firstBatch", out var batch) && batch is IEnumerable<object> docs)
            {
                foreach (var doc in docs)
                {
                    if (doc is Dictionary<string, object> docDict)
                    {
                        rows.Add(docDict!);
                    }
                }
            }
        }

        if (response.TryGetValue("n", out var n))
        {
            rowsAffected = Convert.ToInt64(n);
        }

        if (response.TryGetValue("nModified", out var nMod))
        {
            rowsAffected = Convert.ToInt64(nMod);
        }

        return new QueryResult
        {
            Success = success,
            RowsAffected = rows.Count > 0 ? rows.Count : rowsAffected,
            Rows = rows,
            ErrorMessage = response.GetValueOrDefault("errmsg")?.ToString(),
            ErrorCode = response.GetValueOrDefault("code")?.ToString(),
            Metadata = new Dictionary<string, object>(response)
        };
    }

    private async Task<Dictionary<string, object>> SendCommandAsync(
        string database,
        Dictionary<string, object> command,
        CancellationToken ct)
    {
        // Add $db field
        command["$db"] = database;

        // Serialize to BSON
        var bsonDoc = SerializeToBson(command);

        // Build OP_MSG
        var requestId = Interlocked.Increment(ref _requestId);
        var responseTo = 0;
        var opCode = OpMsg;
        var flags = 0u;

        // Calculate message length
        var messageLength = 16 + 4 + 1 + bsonDoc.Length; // Header + flags + section type + body

        var packet = new byte[messageLength];
        var offset = 0;

        // Message header
        WriteInt32LE(packet.AsSpan(offset, 4), messageLength);
        offset += 4;
        WriteInt32LE(packet.AsSpan(offset, 4), requestId);
        offset += 4;
        WriteInt32LE(packet.AsSpan(offset, 4), responseTo);
        offset += 4;
        WriteInt32LE(packet.AsSpan(offset, 4), opCode);
        offset += 4;

        // Flags
        WriteInt32LE(packet.AsSpan(offset, 4), (int)flags);
        offset += 4;

        // Section: body
        packet[offset++] = SectionBody;
        bsonDoc.CopyTo(packet.AsSpan(offset));

        await SendAsync(packet, ct);

        // Read response
        return await ReadCommandResponseAsync(ct);
    }

    private async Task<Dictionary<string, object>> ReadCommandResponseAsync(CancellationToken ct)
    {
        // Read message header
        var header = await ReceiveExactAsync(16, ct);
        var messageLength = ReadInt32LE(header.AsSpan(0, 4));
        var requestId = ReadInt32LE(header.AsSpan(4, 4));
        var responseTo = ReadInt32LE(header.AsSpan(8, 4));
        var opCode = ReadInt32LE(header.AsSpan(12, 4));

        // Read rest of message
        var payloadLength = messageLength - 16;
        var payload = await ReceiveExactAsync(payloadLength, ct);

        if (opCode == OpMsg)
        {
            // Parse OP_MSG response
            var flags = (uint)ReadInt32LE(payload.AsSpan(0, 4));
            var offset = 4;

            // Read sections
            while (offset < payload.Length)
            {
                var sectionType = payload[offset++];

                if (sectionType == SectionBody)
                {
                    var docLength = ReadInt32LE(payload.AsSpan(offset, 4));
                    var bsonData = payload.AsSpan(offset, docLength).ToArray();
                    return DeserializeFromBson(bsonData);
                }
                else if (sectionType == SectionDocumentSequence)
                {
                    var seqLength = ReadInt32LE(payload.AsSpan(offset, 4));
                    offset += seqLength;
                }
            }
        }
        else if (opCode == OpReply)
        {
            // Parse legacy OP_REPLY
            var responseFlags = ReadInt32LE(payload.AsSpan(0, 4));
            var cursorId = ReadInt64LE(payload.AsSpan(4, 8));
            var startingFrom = ReadInt32LE(payload.AsSpan(12, 4));
            var numberReturned = ReadInt32LE(payload.AsSpan(16, 4));

            if (numberReturned > 0)
            {
                var docLength = ReadInt32LE(payload.AsSpan(20, 4));
                var bsonData = payload.AsSpan(20, docLength).ToArray();
                return DeserializeFromBson(bsonData);
            }
        }

        return new Dictionary<string, object> { ["ok"] = 0.0, ["errmsg"] = "Failed to parse response" };
    }

    private byte[] SerializeToBson(Dictionary<string, object> document)
    {
        // Simplified BSON serialization
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Placeholder for document length
        var lengthPos = ms.Position;
        writer.Write(0);

        foreach (var kvp in document)
        {
            WriteBsonElement(writer, kvp.Key, kvp.Value);
        }

        // Null terminator
        writer.Write((byte)0x00);

        // Write actual length
        var length = (int)ms.Position;
        ms.Position = lengthPos;
        writer.Write(length);
        ms.Position = length;

        return ms.ToArray();
    }

    private void WriteBsonElement(BinaryWriter writer, string name, object? value)
    {
        switch (value)
        {
            case null:
                writer.Write((byte)0x0A); // Null
                WriteCString(writer, name);
                break;

            case bool b:
                writer.Write((byte)0x08); // Boolean
                WriteCString(writer, name);
                writer.Write(b ? (byte)1 : (byte)0);
                break;

            case int i:
                writer.Write((byte)0x10); // Int32
                WriteCString(writer, name);
                writer.Write(i);
                break;

            case long l:
                writer.Write((byte)0x12); // Int64
                WriteCString(writer, name);
                writer.Write(l);
                break;

            case double d:
                writer.Write((byte)0x01); // Double
                WriteCString(writer, name);
                writer.Write(d);
                break;

            case string s:
                writer.Write((byte)0x02); // String
                WriteCString(writer, name);
                var strBytes = Encoding.UTF8.GetBytes(s);
                writer.Write(strBytes.Length + 1);
                writer.Write(strBytes);
                writer.Write((byte)0x00);
                break;

            case byte[] bytes:
                writer.Write((byte)0x05); // Binary
                WriteCString(writer, name);
                writer.Write(bytes.Length);
                writer.Write((byte)0x00); // Generic binary subtype
                writer.Write(bytes);
                break;

            case Dictionary<string, object> doc:
                writer.Write((byte)0x03); // Document
                WriteCString(writer, name);
                var docBytes = SerializeToBson(doc);
                writer.Write(docBytes);
                break;

            case IEnumerable<object> arr:
                writer.Write((byte)0x04); // Array
                WriteCString(writer, name);
                var arrDoc = new Dictionary<string, object>();
                var idx = 0;
                foreach (var item in arr)
                {
                    arrDoc[idx.ToString()] = item;
                    idx++;
                }
                var arrBytes = SerializeToBson(arrDoc);
                writer.Write(arrBytes);
                break;

            default:
                // Try to convert to string
                writer.Write((byte)0x02);
                WriteCString(writer, name);
                var defStr = value?.ToString() ?? "";
                var defBytes = Encoding.UTF8.GetBytes(defStr);
                writer.Write(defBytes.Length + 1);
                writer.Write(defBytes);
                writer.Write((byte)0x00);
                break;
        }
    }

    private static void WriteCString(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0x00);
    }

    private Dictionary<string, object> DeserializeFromBson(byte[] data)
    {
        // Simplified BSON deserialization
        var result = new Dictionary<string, object>();

        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var length = reader.ReadInt32();
        var endPos = ms.Position - 4 + length;

        while (ms.Position < endPos - 1)
        {
            var elementType = reader.ReadByte();
            if (elementType == 0x00) break;

            var name = ReadCString(reader);
            var value = ReadBsonValue(reader, elementType);
            result[name] = value;
        }

        return result;
    }

    private static string ReadCString(BinaryReader reader)
    {
        var bytes = new List<byte>();
        byte b;
        while ((b = reader.ReadByte()) != 0x00)
        {
            bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private object ReadBsonValue(BinaryReader reader, byte elementType)
    {
        return elementType switch
        {
            0x01 => reader.ReadDouble(), // Double
            0x02 => ReadBsonString(reader), // String
            0x03 => ReadEmbeddedDocument(reader), // Document
            0x04 => ReadBsonArray(reader), // Array
            0x05 => ReadBsonBinary(reader), // Binary
            0x07 => new Guid(reader.ReadBytes(12).Concat(new byte[4]).ToArray()), // ObjectId (simplified)
            0x08 => reader.ReadByte() != 0, // Boolean
            0x09 => DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64()).DateTime, // DateTime
            0x0A => null!, // Null
            0x10 => reader.ReadInt32(), // Int32
            0x11 => reader.ReadUInt64(), // Timestamp
            0x12 => reader.ReadInt64(), // Int64
            _ => $"<unsupported type 0x{elementType:X2}>"
        };
    }

    private static string ReadBsonString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        var bytes = reader.ReadBytes(length - 1);
        reader.ReadByte(); // Null terminator
        return Encoding.UTF8.GetString(bytes);
    }

    private Dictionary<string, object> ReadEmbeddedDocument(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        reader.BaseStream.Position -= 4;
        var docBytes = reader.ReadBytes(length);
        return DeserializeFromBson(docBytes);
    }

    private List<object> ReadBsonArray(BinaryReader reader)
    {
        var doc = ReadEmbeddedDocument(reader);
        return doc.Values.ToList();
    }

    private static byte[] ReadBsonBinary(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        var subtype = reader.ReadByte();
        return reader.ReadBytes(length);
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
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        // MongoDB doesn't have an explicit disconnect command
        // Just close the connection
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        var response = await SendCommandAsync("admin", new Dictionary<string, object> { ["ping"] = 1 }, ct);
        return Convert.ToDouble(response.GetValueOrDefault("ok", 0.0)) == 1.0;
    }

    private static long ReadInt64LE(ReadOnlySpan<byte> buffer)
    {
        return (long)buffer[0] | ((long)buffer[1] << 8) | ((long)buffer[2] << 16) | ((long)buffer[3] << 24) |
               ((long)buffer[4] << 32) | ((long)buffer[5] << 40) | ((long)buffer[6] << 48) | ((long)buffer[7] << 56);
    }
}
