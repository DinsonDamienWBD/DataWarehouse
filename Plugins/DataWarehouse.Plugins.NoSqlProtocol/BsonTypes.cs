using System.Text;

namespace DataWarehouse.Plugins.NoSqlProtocol;

/// <summary>
/// BSON element types as defined by the BSON specification.
/// </summary>
public enum BsonType : byte
{
    /// <summary>End of document marker.</summary>
    EndOfDocument = 0x00,
    /// <summary>64-bit floating point.</summary>
    Double = 0x01,
    /// <summary>UTF-8 string.</summary>
    String = 0x02,
    /// <summary>Embedded document.</summary>
    Document = 0x03,
    /// <summary>Array.</summary>
    Array = 0x04,
    /// <summary>Binary data.</summary>
    Binary = 0x05,
    /// <summary>Undefined (deprecated).</summary>
    Undefined = 0x06,
    /// <summary>ObjectId.</summary>
    ObjectId = 0x07,
    /// <summary>Boolean.</summary>
    Boolean = 0x08,
    /// <summary>UTC datetime.</summary>
    DateTime = 0x09,
    /// <summary>Null value.</summary>
    Null = 0x0A,
    /// <summary>Regular expression.</summary>
    Regex = 0x0B,
    /// <summary>DBPointer (deprecated).</summary>
    DbPointer = 0x0C,
    /// <summary>JavaScript code.</summary>
    JavaScript = 0x0D,
    /// <summary>Symbol (deprecated).</summary>
    Symbol = 0x0E,
    /// <summary>JavaScript code with scope.</summary>
    JavaScriptWithScope = 0x0F,
    /// <summary>32-bit integer.</summary>
    Int32 = 0x10,
    /// <summary>Timestamp.</summary>
    Timestamp = 0x11,
    /// <summary>64-bit integer.</summary>
    Int64 = 0x12,
    /// <summary>128-bit decimal.</summary>
    Decimal128 = 0x13,
    /// <summary>Min key.</summary>
    MinKey = 0xFF,
    /// <summary>Max key.</summary>
    MaxKey = 0x7F
}

/// <summary>
/// BSON binary subtypes.
/// </summary>
public enum BsonBinarySubtype : byte
{
    /// <summary>Generic binary.</summary>
    Generic = 0x00,
    /// <summary>Function.</summary>
    Function = 0x01,
    /// <summary>Binary (old).</summary>
    BinaryOld = 0x02,
    /// <summary>UUID (old).</summary>
    UuidOld = 0x03,
    /// <summary>UUID.</summary>
    Uuid = 0x04,
    /// <summary>MD5.</summary>
    Md5 = 0x05,
    /// <summary>Encrypted BSON value.</summary>
    Encrypted = 0x06,
    /// <summary>User-defined.</summary>
    UserDefined = 0x80
}

/// <summary>
/// Represents a BSON ObjectId (12 bytes).
/// Thread-safe implementation with proper timestamp, machine, process, and counter components.
/// </summary>
public sealed class BsonObjectId : IEquatable<BsonObjectId>, IComparable<BsonObjectId>
{
    private static readonly object _counterLock = new();
    private static int _counter = new Random().Next();
    private static readonly byte[] _machineId = GenerateMachineId();
    private static readonly byte[] _processId = GenerateProcessId();

    private readonly byte[] _bytes;

    /// <summary>
    /// Gets the raw bytes of this ObjectId.
    /// </summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>
    /// Gets the timestamp component of this ObjectId.
    /// </summary>
    public DateTime Timestamp
    {
        get
        {
            var seconds = (_bytes[0] << 24) | (_bytes[1] << 16) | (_bytes[2] << 8) | _bytes[3];
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
    }

    /// <summary>
    /// Creates a new ObjectId with the current timestamp.
    /// </summary>
    public BsonObjectId()
    {
        _bytes = new byte[12];
        var timestamp = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _bytes[0] = (byte)(timestamp >> 24);
        _bytes[1] = (byte)(timestamp >> 16);
        _bytes[2] = (byte)(timestamp >> 8);
        _bytes[3] = (byte)timestamp;

        Array.Copy(_machineId, 0, _bytes, 4, 3);
        Array.Copy(_processId, 0, _bytes, 7, 2);

        int counter;
        lock (_counterLock)
        {
            counter = _counter++;
        }

        _bytes[9] = (byte)(counter >> 16);
        _bytes[10] = (byte)(counter >> 8);
        _bytes[11] = (byte)counter;
    }

    /// <summary>
    /// Creates an ObjectId from raw bytes.
    /// </summary>
    /// <param name="bytes">The 12-byte array.</param>
    /// <exception cref="ArgumentException">Thrown if bytes is not exactly 12 bytes.</exception>
    public BsonObjectId(byte[] bytes)
    {
        if (bytes == null || bytes.Length != 12)
            throw new ArgumentException("ObjectId must be exactly 12 bytes", nameof(bytes));
        _bytes = (byte[])bytes.Clone();
    }

    /// <summary>
    /// Creates an ObjectId from a hex string.
    /// </summary>
    /// <param name="hex">The 24-character hex string.</param>
    /// <exception cref="ArgumentException">Thrown if hex is not exactly 24 characters.</exception>
    public BsonObjectId(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length != 24)
            throw new ArgumentException("ObjectId hex string must be exactly 24 characters", nameof(hex));
        _bytes = Convert.FromHexString(hex);
    }

    /// <summary>
    /// Parses a hex string into an ObjectId.
    /// </summary>
    /// <param name="hex">The hex string to parse.</param>
    /// <returns>The parsed ObjectId.</returns>
    public static BsonObjectId Parse(string hex) => new(hex);

    /// <summary>
    /// Tries to parse a hex string into an ObjectId.
    /// </summary>
    /// <param name="hex">The hex string to parse.</param>
    /// <param name="result">The parsed ObjectId if successful.</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParse(string? hex, out BsonObjectId? result)
    {
        result = null;
        if (string.IsNullOrEmpty(hex) || hex.Length != 24)
            return false;
        try
        {
            result = new BsonObjectId(hex);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Converts the ObjectId to a byte array.
    /// </summary>
    public byte[] ToByteArray() => (byte[])_bytes.Clone();

    /// <inheritdoc/>
    public override string ToString() => Convert.ToHexString(_bytes).ToLowerInvariant();

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            for (int i = 0; i < 12; i++)
                hash = hash * 31 + _bytes[i];
            return hash;
        }
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is BsonObjectId other && Equals(other);

    /// <inheritdoc/>
    public bool Equals(BsonObjectId? other)
    {
        if (other is null) return false;
        return _bytes.AsSpan().SequenceEqual(other._bytes);
    }

    /// <inheritdoc/>
    public int CompareTo(BsonObjectId? other)
    {
        if (other is null) return 1;
        return _bytes.AsSpan().SequenceCompareTo(other._bytes);
    }

    /// <summary>
    /// Equality operator.
    /// </summary>
    public static bool operator ==(BsonObjectId? left, BsonObjectId? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>
    /// Inequality operator.
    /// </summary>
    public static bool operator !=(BsonObjectId? left, BsonObjectId? right) => !(left == right);

    private static byte[] GenerateMachineId()
    {
        var machineHash = Environment.MachineName.GetHashCode();
        return new byte[] { (byte)(machineHash >> 16), (byte)(machineHash >> 8), (byte)machineHash };
    }

    private static byte[] GenerateProcessId()
    {
        var pid = Environment.ProcessId;
        return new byte[] { (byte)(pid >> 8), (byte)pid };
    }
}

/// <summary>
/// Represents a BSON document as an ordered dictionary of key-value pairs.
/// Thread-safe for read operations.
/// </summary>
public sealed class BsonDocument : Dictionary<string, object?>
{
    /// <summary>
    /// Creates an empty BSON document.
    /// </summary>
    public BsonDocument() : base(StringComparer.Ordinal) { }

    /// <summary>
    /// Creates a BSON document from a dictionary.
    /// </summary>
    /// <param name="dictionary">The source dictionary.</param>
    public BsonDocument(IDictionary<string, object?> dictionary) : base(dictionary, StringComparer.Ordinal) { }

    /// <summary>
    /// Gets a value by key, returning null if not found.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <returns>The value or null.</returns>
    public object? GetValue(string key) => TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Gets a value as a specific type.
    /// </summary>
    /// <typeparam name="T">The expected type.</typeparam>
    /// <param name="key">The key to look up.</param>
    /// <returns>The value cast to T, or default if not found or wrong type.</returns>
    public T? GetValue<T>(string key) => TryGetValue(key, out var value) && value is T typed ? typed : default;

    /// <summary>
    /// Gets a nested document by key.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <returns>The nested document or null.</returns>
    public BsonDocument? GetDocument(string key) => GetValue<BsonDocument>(key);

    /// <summary>
    /// Gets an array by key.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <returns>The array or null.</returns>
    public List<object?>? GetArray(string key) => GetValue<List<object?>>(key);

    /// <summary>
    /// Creates a deep clone of this document.
    /// </summary>
    /// <returns>A new document with cloned values.</returns>
    public BsonDocument Clone()
    {
        var clone = new BsonDocument();
        foreach (var kvp in this)
        {
            clone[kvp.Key] = kvp.Value switch
            {
                BsonDocument doc => doc.Clone(),
                List<object?> arr => new List<object?>(arr),
                _ => kvp.Value
            };
        }
        return clone;
    }
}

/// <summary>
/// BSON serialization and deserialization utilities.
/// Provides thread-safe, production-ready BSON encoding/decoding.
/// </summary>
public static class BsonSerializer
{
    /// <summary>
    /// Serializes a BSON document to bytes.
    /// </summary>
    /// <param name="document">The document to serialize.</param>
    /// <returns>The serialized bytes.</returns>
    /// <exception cref="ArgumentNullException">Thrown if document is null.</exception>
    public static byte[] Serialize(BsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        WriteDocument(writer, document);
        return ms.ToArray();
    }

    /// <summary>
    /// Deserializes bytes to a BSON document.
    /// </summary>
    /// <param name="data">The data to deserialize.</param>
    /// <returns>The deserialized document.</returns>
    /// <exception cref="ArgumentNullException">Thrown if data is null.</exception>
    /// <exception cref="InvalidDataException">Thrown if data is invalid BSON.</exception>
    public static BsonDocument Deserialize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        return ReadDocument(reader);
    }

    /// <summary>
    /// Deserializes bytes to a BSON document.
    /// </summary>
    /// <param name="data">The data span to deserialize.</param>
    /// <returns>The deserialized document.</returns>
    public static BsonDocument Deserialize(ReadOnlySpan<byte> data)
    {
        return Deserialize(data.ToArray());
    }

    private static void WriteDocument(BinaryWriter writer, BsonDocument document)
    {
        var startPos = writer.BaseStream.Position;
        writer.Write(0); // Placeholder for size

        foreach (var kvp in document)
        {
            WriteElement(writer, kvp.Key, kvp.Value);
        }

        writer.Write((byte)0x00); // Document terminator

        var endPos = writer.BaseStream.Position;
        var size = (int)(endPos - startPos);
        writer.BaseStream.Position = startPos;
        writer.Write(size);
        writer.BaseStream.Position = endPos;
    }

    private static void WriteElement(BinaryWriter writer, string key, object? value)
    {
        switch (value)
        {
            case null:
                writer.Write((byte)BsonType.Null);
                WriteCString(writer, key);
                break;

            case bool b:
                writer.Write((byte)BsonType.Boolean);
                WriteCString(writer, key);
                writer.Write(b ? (byte)1 : (byte)0);
                break;

            case int i:
                writer.Write((byte)BsonType.Int32);
                WriteCString(writer, key);
                writer.Write(i);
                break;

            case long l:
                writer.Write((byte)BsonType.Int64);
                WriteCString(writer, key);
                writer.Write(l);
                break;

            case double d:
                writer.Write((byte)BsonType.Double);
                WriteCString(writer, key);
                writer.Write(d);
                break;

            case float f:
                writer.Write((byte)BsonType.Double);
                WriteCString(writer, key);
                writer.Write((double)f);
                break;

            case string s:
                writer.Write((byte)BsonType.String);
                WriteCString(writer, key);
                WriteString(writer, s);
                break;

            case DateTime dt:
                writer.Write((byte)BsonType.DateTime);
                WriteCString(writer, key);
                writer.Write(new DateTimeOffset(dt).ToUnixTimeMilliseconds());
                break;

            case DateTimeOffset dto:
                writer.Write((byte)BsonType.DateTime);
                WriteCString(writer, key);
                writer.Write(dto.ToUnixTimeMilliseconds());
                break;

            case BsonObjectId oid:
                writer.Write((byte)BsonType.ObjectId);
                WriteCString(writer, key);
                writer.Write(oid.ToByteArray());
                break;

            case byte[] bytes:
                writer.Write((byte)BsonType.Binary);
                WriteCString(writer, key);
                writer.Write(bytes.Length);
                writer.Write((byte)BsonBinarySubtype.Generic);
                writer.Write(bytes);
                break;

            case BsonDocument doc:
                writer.Write((byte)BsonType.Document);
                WriteCString(writer, key);
                WriteDocument(writer, doc);
                break;

            case List<object?> arr:
                writer.Write((byte)BsonType.Array);
                WriteCString(writer, key);
                WriteArray(writer, arr);
                break;

            case IEnumerable<object?> enumerable:
                writer.Write((byte)BsonType.Array);
                WriteCString(writer, key);
                WriteArray(writer, enumerable.ToList());
                break;

            default:
                // Convert unknown types to string
                writer.Write((byte)BsonType.String);
                WriteCString(writer, key);
                WriteString(writer, value.ToString() ?? "");
                break;
        }
    }

    private static void WriteArray(BinaryWriter writer, List<object?> array)
    {
        var doc = new BsonDocument();
        for (int i = 0; i < array.Count; i++)
        {
            doc[i.ToString()] = array[i];
        }
        WriteDocument(writer, doc);
    }

    private static void WriteCString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes);
        writer.Write((byte)0x00);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length + 1);
        writer.Write(bytes);
        writer.Write((byte)0x00);
    }

    private static BsonDocument ReadDocument(BinaryReader reader)
    {
        var size = reader.ReadInt32();
        var doc = new BsonDocument();

        while (true)
        {
            var type = (BsonType)reader.ReadByte();
            if (type == BsonType.EndOfDocument)
                break;

            var key = ReadCString(reader);
            var value = ReadValue(reader, type);
            doc[key] = value;
        }

        return doc;
    }

    private static object? ReadValue(BinaryReader reader, BsonType type)
    {
        return type switch
        {
            BsonType.Double => reader.ReadDouble(),
            BsonType.String => ReadString(reader),
            BsonType.Document => ReadDocument(reader),
            BsonType.Array => ReadArray(reader),
            BsonType.Binary => ReadBinary(reader),
            BsonType.ObjectId => new BsonObjectId(reader.ReadBytes(12)),
            BsonType.Boolean => reader.ReadByte() != 0,
            BsonType.DateTime => DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64()).UtcDateTime,
            BsonType.Null => null,
            BsonType.Int32 => reader.ReadInt32(),
            BsonType.Timestamp => reader.ReadInt64(),
            BsonType.Int64 => reader.ReadInt64(),
            BsonType.Regex => ReadRegex(reader),
            BsonType.JavaScript => ReadString(reader),
            BsonType.Symbol => ReadString(reader),
            BsonType.MinKey => double.MinValue,
            BsonType.MaxKey => double.MaxValue,
            _ => throw new InvalidDataException($"Unsupported BSON type: {type}")
        };
    }

    private static List<object?> ReadArray(BinaryReader reader)
    {
        var doc = ReadDocument(reader);
        var list = new List<object?>();
        for (int i = 0; doc.ContainsKey(i.ToString()); i++)
        {
            list.Add(doc[i.ToString()]);
        }
        return list;
    }

    private static byte[] ReadBinary(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        var subtype = reader.ReadByte();
        return reader.ReadBytes(length);
    }

    private static string ReadRegex(BinaryReader reader)
    {
        var pattern = ReadCString(reader);
        var options = ReadCString(reader);
        return $"/{pattern}/{options}";
    }

    private static string ReadCString(BinaryReader reader)
    {
        var bytes = new List<byte>();
        byte b;
        while ((b = reader.ReadByte()) != 0)
        {
            bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        var bytes = reader.ReadBytes(length - 1);
        reader.ReadByte(); // null terminator
        return Encoding.UTF8.GetString(bytes);
    }
}
