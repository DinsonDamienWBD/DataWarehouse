using System.Buffers.Binary;
using System.Text;

namespace DataWarehouse.Plugins.NoSqlProtocol;

/// <summary>
/// MongoDB wire protocol opcodes.
/// </summary>
public enum MongoOpCode : int
{
    /// <summary>Reply to a client request (legacy).</summary>
    OpReply = 1,
    /// <summary>Update document (legacy).</summary>
    OpUpdate = 2001,
    /// <summary>Insert documents (legacy).</summary>
    OpInsert = 2002,
    /// <summary>Query (legacy).</summary>
    OpQuery = 2004,
    /// <summary>Get more data (legacy).</summary>
    OpGetMore = 2005,
    /// <summary>Delete documents (legacy).</summary>
    OpDelete = 2006,
    /// <summary>Kill cursors (legacy).</summary>
    OpKillCursors = 2007,
    /// <summary>Compressed message.</summary>
    OpCompressed = 2012,
    /// <summary>Modern message format (MongoDB 3.6+).</summary>
    OpMsg = 2013
}

/// <summary>
/// MongoDB message header (16 bytes).
/// </summary>
public readonly struct MongoMessageHeader
{
    /// <summary>Total message size including header.</summary>
    public readonly int MessageLength;
    /// <summary>Client or server generated request ID.</summary>
    public readonly int RequestId;
    /// <summary>Request ID from original request (for responses).</summary>
    public readonly int ResponseTo;
    /// <summary>Type of message.</summary>
    public readonly MongoOpCode OpCode;

    /// <summary>
    /// Creates a new message header.
    /// </summary>
    public MongoMessageHeader(int messageLength, int requestId, int responseTo, MongoOpCode opCode)
    {
        MessageLength = messageLength;
        RequestId = requestId;
        ResponseTo = responseTo;
        OpCode = opCode;
    }

    /// <summary>
    /// Reads a header from a byte array.
    /// </summary>
    public static MongoMessageHeader Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16)
            throw new InvalidDataException("Header must be at least 16 bytes");

        return new MongoMessageHeader(
            BinaryPrimitives.ReadInt32LittleEndian(data),
            BinaryPrimitives.ReadInt32LittleEndian(data[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[8..]),
            (MongoOpCode)BinaryPrimitives.ReadInt32LittleEndian(data[12..])
        );
    }

    /// <summary>
    /// Writes the header to a byte array.
    /// </summary>
    public void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination, MessageLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], RequestId);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], ResponseTo);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], (int)OpCode);
    }
}

/// <summary>
/// OP_MSG flag bits for MongoDB 3.6+ wire protocol.
/// </summary>
[Flags]
public enum OpMsgFlags : uint
{
    /// <summary>No flags.</summary>
    None = 0,
    /// <summary>Checksum present at end of message.</summary>
    ChecksumPresent = 1 << 0,
    /// <summary>More messages follow this one.</summary>
    MoreToCome = 1 << 1,
    /// <summary>Client expects no response.</summary>
    ExhaustAllowed = 1 << 16
}

/// <summary>
/// OP_MSG section types.
/// </summary>
public enum OpMsgSectionType : byte
{
    /// <summary>Single BSON document body.</summary>
    Body = 0,
    /// <summary>Document sequence.</summary>
    DocumentSequence = 1
}

/// <summary>
/// Represents a section in an OP_MSG message.
/// </summary>
public sealed class OpMsgSection
{
    /// <summary>The section type.</summary>
    public OpMsgSectionType Type { get; init; }
    /// <summary>For Body sections, the document. For DocumentSequence, the first document.</summary>
    public BsonDocument? Document { get; init; }
    /// <summary>For DocumentSequence sections, the sequence identifier.</summary>
    public string? Identifier { get; init; }
    /// <summary>For DocumentSequence sections, all documents.</summary>
    public List<BsonDocument>? Documents { get; init; }
}

/// <summary>
/// Represents a MongoDB OP_MSG message (modern protocol).
/// </summary>
public sealed class OpMsgMessage
{
    /// <summary>The message header.</summary>
    public MongoMessageHeader Header { get; init; }
    /// <summary>Message flags.</summary>
    public OpMsgFlags Flags { get; init; }
    /// <summary>Message sections.</summary>
    public List<OpMsgSection> Sections { get; init; } = new();
    /// <summary>Optional CRC32C checksum.</summary>
    public uint? Checksum { get; init; }

    /// <summary>
    /// Gets the primary body document from this message.
    /// </summary>
    public BsonDocument? Body => Sections.FirstOrDefault(s => s.Type == OpMsgSectionType.Body)?.Document;
}

/// <summary>
/// Represents a legacy MongoDB OP_QUERY message.
/// </summary>
public sealed class OpQueryMessage
{
    /// <summary>The message header.</summary>
    public MongoMessageHeader Header { get; init; }
    /// <summary>Query flags.</summary>
    public int Flags { get; init; }
    /// <summary>Full collection name (database.collection).</summary>
    public string FullCollectionName { get; init; } = "";
    /// <summary>Number of documents to skip.</summary>
    public int NumberToSkip { get; init; }
    /// <summary>Number of documents to return.</summary>
    public int NumberToReturn { get; init; }
    /// <summary>Query document.</summary>
    public BsonDocument Query { get; init; } = new();
    /// <summary>Optional field selector.</summary>
    public BsonDocument? ReturnFieldsSelector { get; init; }
}

/// <summary>
/// Represents a MongoDB OP_REPLY message (response to OP_QUERY).
/// </summary>
public sealed class OpReplyMessage
{
    /// <summary>The message header.</summary>
    public MongoMessageHeader Header { get; init; }
    /// <summary>Response flags.</summary>
    public int ResponseFlags { get; init; }
    /// <summary>Cursor ID for getMore operations.</summary>
    public long CursorId { get; init; }
    /// <summary>Starting position in cursor.</summary>
    public int StartingFrom { get; init; }
    /// <summary>Number of documents returned.</summary>
    public int NumberReturned { get; init; }
    /// <summary>Result documents.</summary>
    public List<BsonDocument> Documents { get; init; } = new();
}

/// <summary>
/// Represents a legacy MongoDB OP_INSERT message.
/// </summary>
public sealed class OpInsertMessage
{
    /// <summary>The message header.</summary>
    public MongoMessageHeader Header { get; init; }
    /// <summary>Insert flags.</summary>
    public int Flags { get; init; }
    /// <summary>Full collection name.</summary>
    public string FullCollectionName { get; init; } = "";
    /// <summary>Documents to insert.</summary>
    public List<BsonDocument> Documents { get; init; } = new();
}

/// <summary>
/// Represents a legacy MongoDB OP_UPDATE message.
/// </summary>
public sealed class OpUpdateMessage
{
    /// <summary>The message header.</summary>
    public MongoMessageHeader Header { get; init; }
    /// <summary>Full collection name.</summary>
    public string FullCollectionName { get; init; } = "";
    /// <summary>Update flags.</summary>
    public int Flags { get; init; }
    /// <summary>Query selector.</summary>
    public BsonDocument Selector { get; init; } = new();
    /// <summary>Update document.</summary>
    public BsonDocument Update { get; init; } = new();
}

/// <summary>
/// Represents a legacy MongoDB OP_DELETE message.
/// </summary>
public sealed class OpDeleteMessage
{
    /// <summary>The message header.</summary>
    public MongoMessageHeader Header { get; init; }
    /// <summary>Full collection name.</summary>
    public string FullCollectionName { get; init; } = "";
    /// <summary>Delete flags.</summary>
    public int Flags { get; init; }
    /// <summary>Query selector.</summary>
    public BsonDocument Selector { get; init; } = new();
}

/// <summary>
/// MongoDB wire protocol message parser.
/// Thread-safe, stateless implementation.
/// </summary>
public static class MongoWireParser
{
    /// <summary>
    /// Parses a MongoDB message from raw bytes.
    /// </summary>
    /// <param name="data">The raw message data.</param>
    /// <returns>The parsed message object.</returns>
    /// <exception cref="InvalidDataException">Thrown if message is malformed.</exception>
    public static object ParseMessage(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16)
            throw new InvalidDataException("Message too short for header");

        var header = MongoMessageHeader.Read(data);

        if (header.MessageLength > data.Length)
            throw new InvalidDataException($"Message length {header.MessageLength} exceeds data length {data.Length}");

        return header.OpCode switch
        {
            MongoOpCode.OpMsg => ParseOpMsg(header, data[16..header.MessageLength]),
            MongoOpCode.OpQuery => ParseOpQuery(header, data[16..header.MessageLength]),
            MongoOpCode.OpInsert => ParseOpInsert(header, data[16..header.MessageLength]),
            MongoOpCode.OpUpdate => ParseOpUpdate(header, data[16..header.MessageLength]),
            MongoOpCode.OpDelete => ParseOpDelete(header, data[16..header.MessageLength]),
            _ => throw new InvalidDataException($"Unsupported opcode: {header.OpCode}")
        };
    }

    private static OpMsgMessage ParseOpMsg(MongoMessageHeader header, ReadOnlySpan<byte> body)
    {
        var flags = (OpMsgFlags)BinaryPrimitives.ReadUInt32LittleEndian(body);
        var offset = 4;
        var sections = new List<OpMsgSection>();

        while (offset < body.Length - (flags.HasFlag(OpMsgFlags.ChecksumPresent) ? 4 : 0))
        {
            var sectionType = (OpMsgSectionType)body[offset++];

            if (sectionType == OpMsgSectionType.Body)
            {
                var docSize = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
                var doc = BsonSerializer.Deserialize(body.Slice(offset, docSize));
                sections.Add(new OpMsgSection { Type = sectionType, Document = doc });
                offset += docSize;
            }
            else if (sectionType == OpMsgSectionType.DocumentSequence)
            {
                var seqSize = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
                var seqEnd = offset + seqSize;
                offset += 4;

                var identifier = ReadCString(body, ref offset);
                var docs = new List<BsonDocument>();

                while (offset < seqEnd)
                {
                    var docSize = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
                    docs.Add(BsonSerializer.Deserialize(body.Slice(offset, docSize)));
                    offset += docSize;
                }

                sections.Add(new OpMsgSection
                {
                    Type = sectionType,
                    Identifier = identifier,
                    Documents = docs,
                    Document = docs.FirstOrDefault()
                });
            }
        }

        uint? checksum = null;
        if (flags.HasFlag(OpMsgFlags.ChecksumPresent))
        {
            checksum = BinaryPrimitives.ReadUInt32LittleEndian(body[(body.Length - 4)..]);
        }

        return new OpMsgMessage
        {
            Header = header,
            Flags = flags,
            Sections = sections,
            Checksum = checksum
        };
    }

    private static OpQueryMessage ParseOpQuery(MongoMessageHeader header, ReadOnlySpan<byte> body)
    {
        var flags = BinaryPrimitives.ReadInt32LittleEndian(body);
        var offset = 4;
        var fullCollectionName = ReadCString(body, ref offset);
        var numberToSkip = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
        offset += 4;
        var numberToReturn = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
        offset += 4;

        var querySize = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
        var query = BsonSerializer.Deserialize(body.Slice(offset, querySize));
        offset += querySize;

        BsonDocument? returnFields = null;
        if (offset < body.Length)
        {
            var fieldsSize = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
            returnFields = BsonSerializer.Deserialize(body.Slice(offset, fieldsSize));
        }

        return new OpQueryMessage
        {
            Header = header,
            Flags = flags,
            FullCollectionName = fullCollectionName,
            NumberToSkip = numberToSkip,
            NumberToReturn = numberToReturn,
            Query = query,
            ReturnFieldsSelector = returnFields
        };
    }

    private static OpInsertMessage ParseOpInsert(MongoMessageHeader header, ReadOnlySpan<byte> body)
    {
        var flags = BinaryPrimitives.ReadInt32LittleEndian(body);
        var offset = 4;
        var fullCollectionName = ReadCString(body, ref offset);

        var documents = new List<BsonDocument>();
        while (offset < body.Length)
        {
            var docSize = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
            documents.Add(BsonSerializer.Deserialize(body.Slice(offset, docSize)));
            offset += docSize;
        }

        return new OpInsertMessage
        {
            Header = header,
            Flags = flags,
            FullCollectionName = fullCollectionName,
            Documents = documents
        };
    }

    private static OpUpdateMessage ParseOpUpdate(MongoMessageHeader header, ReadOnlySpan<byte> body)
    {
        var offset = 4; // Skip ZERO
        var fullCollectionName = ReadCString(body, ref offset);
        var flags = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
        offset += 4;

        var selectorSize = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
        var selector = BsonSerializer.Deserialize(body.Slice(offset, selectorSize));
        offset += selectorSize;

        var updateSize = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
        var update = BsonSerializer.Deserialize(body.Slice(offset, updateSize));

        return new OpUpdateMessage
        {
            Header = header,
            FullCollectionName = fullCollectionName,
            Flags = flags,
            Selector = selector,
            Update = update
        };
    }

    private static OpDeleteMessage ParseOpDelete(MongoMessageHeader header, ReadOnlySpan<byte> body)
    {
        var offset = 4; // Skip ZERO
        var fullCollectionName = ReadCString(body, ref offset);
        var flags = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
        offset += 4;

        var selectorSize = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
        var selector = BsonSerializer.Deserialize(body.Slice(offset, selectorSize));

        return new OpDeleteMessage
        {
            Header = header,
            FullCollectionName = fullCollectionName,
            Flags = flags,
            Selector = selector
        };
    }

    private static string ReadCString(ReadOnlySpan<byte> data, ref int offset)
    {
        var start = offset;
        while (offset < data.Length && data[offset] != 0)
            offset++;
        var result = Encoding.UTF8.GetString(data[start..offset]);
        offset++; // Skip null terminator
        return result;
    }
}

/// <summary>
/// MongoDB wire protocol message builder.
/// Thread-safe, stateless implementation.
/// </summary>
public static class MongoWireBuilder
{
    private static int _requestId;

    /// <summary>
    /// Gets the next unique request ID.
    /// </summary>
    public static int NextRequestId() => Interlocked.Increment(ref _requestId);

    /// <summary>
    /// Builds an OP_MSG response.
    /// </summary>
    /// <param name="responseTo">The request ID to respond to.</param>
    /// <param name="document">The response document.</param>
    /// <returns>The serialized message bytes.</returns>
    public static byte[] BuildOpMsgResponse(int responseTo, BsonDocument document)
    {
        var docBytes = BsonSerializer.Serialize(document);
        var messageLength = 16 + 4 + 1 + docBytes.Length; // header + flags + section type + doc

        var result = new byte[messageLength];
        var header = new MongoMessageHeader(messageLength, NextRequestId(), responseTo, MongoOpCode.OpMsg);
        header.Write(result);

        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), 0); // flags
        result[20] = (byte)OpMsgSectionType.Body;
        docBytes.CopyTo(result.AsSpan(21));

        return result;
    }

    /// <summary>
    /// Builds an OP_REPLY response (legacy).
    /// </summary>
    /// <param name="responseTo">The request ID to respond to.</param>
    /// <param name="documents">The response documents.</param>
    /// <param name="cursorId">The cursor ID (0 if no cursor).</param>
    /// <returns>The serialized message bytes.</returns>
    public static byte[] BuildOpReply(int responseTo, List<BsonDocument> documents, long cursorId = 0)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Write placeholder header
        writer.Write(0); // messageLength placeholder
        writer.Write(NextRequestId());
        writer.Write(responseTo);
        writer.Write((int)MongoOpCode.OpReply);

        // Write reply body
        writer.Write(0); // responseFlags
        writer.Write(cursorId);
        writer.Write(0); // startingFrom
        writer.Write(documents.Count);

        foreach (var doc in documents)
        {
            var docBytes = BsonSerializer.Serialize(doc);
            writer.Write(docBytes);
        }

        // Update message length
        var result = ms.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(result, result.Length);

        return result;
    }

    /// <summary>
    /// Builds an error response.
    /// </summary>
    /// <param name="responseTo">The request ID to respond to.</param>
    /// <param name="errorCode">The error code.</param>
    /// <param name="errorMessage">The error message.</param>
    /// <param name="useOpMsg">Whether to use OP_MSG format.</param>
    /// <returns>The serialized error response.</returns>
    public static byte[] BuildErrorResponse(int responseTo, int errorCode, string errorMessage, bool useOpMsg = true)
    {
        var errorDoc = new BsonDocument
        {
            ["ok"] = 0,
            ["errmsg"] = errorMessage,
            ["code"] = errorCode,
            ["codeName"] = GetErrorCodeName(errorCode)
        };

        return useOpMsg
            ? BuildOpMsgResponse(responseTo, errorDoc)
            : BuildOpReply(responseTo, new List<BsonDocument> { errorDoc });
    }

    private static string GetErrorCodeName(int code) => code switch
    {
        1 => "InternalError",
        2 => "BadValue",
        11 => "UserNotFound",
        13 => "Unauthorized",
        18 => "AuthenticationFailed",
        26 => "NamespaceNotFound",
        59 => "CommandNotFound",
        _ => "UnknownError"
    };
}
