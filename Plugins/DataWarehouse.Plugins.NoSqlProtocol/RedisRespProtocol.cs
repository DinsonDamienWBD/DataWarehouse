using System.Buffers;
using System.Text;

namespace DataWarehouse.Plugins.NoSqlProtocol;

/// <summary>
/// RESP3 data types.
/// </summary>
public enum RespType : byte
{
    /// <summary>Simple string (+).</summary>
    SimpleString = (byte)'+',
    /// <summary>Simple error (-).</summary>
    SimpleError = (byte)'-',
    /// <summary>Integer (:).</summary>
    Integer = (byte)':',
    /// <summary>Bulk string ($).</summary>
    BulkString = (byte)'$',
    /// <summary>Array (*).</summary>
    Array = (byte)'*',
    /// <summary>Null (_) - RESP3.</summary>
    Null = (byte)'_',
    /// <summary>Boolean (#) - RESP3.</summary>
    Boolean = (byte)'#',
    /// <summary>Double (,) - RESP3.</summary>
    Double = (byte)',',
    /// <summary>Big number ((RESP3).</summary>
    BigNumber = (byte)'(',
    /// <summary>Bulk error (!) - RESP3.</summary>
    BulkError = (byte)'!',
    /// <summary>Verbatim string (=) - RESP3.</summary>
    VerbatimString = (byte)'=',
    /// <summary>Map (%) - RESP3.</summary>
    Map = (byte)'%',
    /// <summary>Set (~) - RESP3.</summary>
    Set = (byte)'~',
    /// <summary>Attribute (|) - RESP3.</summary>
    Attribute = (byte)'|',
    /// <summary>Push (>) - RESP3.</summary>
    Push = (byte)'>'
}

/// <summary>
/// Represents a parsed RESP value.
/// </summary>
public sealed class RespValue
{
    /// <summary>The RESP type.</summary>
    public RespType Type { get; init; }
    /// <summary>String value for simple strings, bulk strings, errors.</summary>
    public string? StringValue { get; init; }
    /// <summary>Integer value.</summary>
    public long IntegerValue { get; init; }
    /// <summary>Double value.</summary>
    public double DoubleValue { get; init; }
    /// <summary>Boolean value.</summary>
    public bool BooleanValue { get; init; }
    /// <summary>Array elements.</summary>
    public RespValue[]? ArrayValue { get; init; }
    /// <summary>Map entries (for RESP3 maps).</summary>
    public Dictionary<string, RespValue>? MapValue { get; init; }
    /// <summary>Raw bytes for bulk strings.</summary>
    public byte[]? BulkBytes { get; init; }
    /// <summary>Whether this is a null value.</summary>
    public bool IsNull { get; init; }

    /// <summary>
    /// Creates a simple string value.
    /// </summary>
    public static RespValue SimpleString(string value) => new()
    {
        Type = RespType.SimpleString,
        StringValue = value
    };

    /// <summary>
    /// Creates a bulk string value.
    /// </summary>
    public static RespValue BulkString(string? value) => value == null
        ? Null()
        : new RespValue
        {
            Type = RespType.BulkString,
            StringValue = value,
            BulkBytes = Encoding.UTF8.GetBytes(value)
        };

    /// <summary>
    /// Creates a bulk string value from bytes.
    /// </summary>
    public static RespValue BulkString(byte[] value) => new()
    {
        Type = RespType.BulkString,
        BulkBytes = value,
        StringValue = Encoding.UTF8.GetString(value)
    };

    /// <summary>
    /// Creates an error value.
    /// </summary>
    public static RespValue Error(string message) => new()
    {
        Type = RespType.SimpleError,
        StringValue = message
    };

    /// <summary>
    /// Creates an error value with a type prefix.
    /// </summary>
    public static RespValue Error(string type, string message) => new()
    {
        Type = RespType.SimpleError,
        StringValue = $"{type} {message}"
    };

    /// <summary>
    /// Creates an integer value.
    /// </summary>
    public static RespValue Integer(long value) => new()
    {
        Type = RespType.Integer,
        IntegerValue = value
    };

    /// <summary>
    /// Creates a double value.
    /// </summary>
    public static RespValue Double(double value) => new()
    {
        Type = RespType.Double,
        DoubleValue = value
    };

    /// <summary>
    /// Creates a boolean value.
    /// </summary>
    public static RespValue Boolean(bool value) => new()
    {
        Type = RespType.Boolean,
        BooleanValue = value
    };

    /// <summary>
    /// Creates a null value.
    /// </summary>
    public static RespValue Null() => new()
    {
        Type = RespType.Null,
        IsNull = true
    };

    /// <summary>
    /// Creates an array value.
    /// </summary>
    public static RespValue Array(params RespValue[] elements) => new()
    {
        Type = RespType.Array,
        ArrayValue = elements
    };

    /// <summary>
    /// Creates an array value.
    /// </summary>
    public static RespValue Array(IEnumerable<RespValue> elements) => new()
    {
        Type = RespType.Array,
        ArrayValue = elements.ToArray()
    };

    /// <summary>
    /// Creates a map value.
    /// </summary>
    public static RespValue Map(Dictionary<string, RespValue> entries) => new()
    {
        Type = RespType.Map,
        MapValue = entries
    };

    /// <summary>
    /// Creates a set value.
    /// </summary>
    public static RespValue Set(params RespValue[] elements) => new()
    {
        Type = RespType.Set,
        ArrayValue = elements
    };

    /// <summary>
    /// Creates a push value (for pub/sub).
    /// </summary>
    public static RespValue Push(params RespValue[] elements) => new()
    {
        Type = RespType.Push,
        ArrayValue = elements
    };

    /// <summary>
    /// Gets the string representation of this value.
    /// </summary>
    public string? AsString() => Type switch
    {
        RespType.SimpleString => StringValue,
        RespType.BulkString => StringValue,
        RespType.Integer => IntegerValue.ToString(),
        RespType.Double => DoubleValue.ToString(),
        RespType.Boolean => BooleanValue ? "true" : "false",
        RespType.Null => null,
        _ => StringValue
    };

    /// <summary>
    /// Gets the integer representation of this value.
    /// </summary>
    public long AsInteger() => Type switch
    {
        RespType.Integer => IntegerValue,
        RespType.BulkString => long.TryParse(StringValue, out var v) ? v : 0,
        RespType.SimpleString => long.TryParse(StringValue, out var v2) ? v2 : 0,
        _ => 0
    };
}

/// <summary>
/// RESP3 protocol parser.
/// Thread-safe, allocation-efficient implementation.
/// </summary>
public static class RespParser
{
    private static readonly byte[] CrLf = { (byte)'\r', (byte)'\n' };

    /// <summary>
    /// Parses a RESP message from a byte array.
    /// </summary>
    /// <param name="data">The data to parse.</param>
    /// <param name="bytesConsumed">The number of bytes consumed.</param>
    /// <returns>The parsed value, or null if incomplete.</returns>
    public static RespValue? Parse(ReadOnlySpan<byte> data, out int bytesConsumed)
    {
        bytesConsumed = 0;

        if (data.IsEmpty)
            return null;

        var type = (RespType)data[0];
        var remaining = data[1..];
        var consumed = 1;

        var lineEnd = remaining.IndexOf(CrLf);
        if (lineEnd < 0)
            return null;

        var line = remaining[..lineEnd];
        consumed += lineEnd + 2;

        return type switch
        {
            RespType.SimpleString => ParseSimpleString(line, ref bytesConsumed, consumed),
            RespType.SimpleError => ParseSimpleError(line, ref bytesConsumed, consumed),
            RespType.Integer => ParseInteger(line, ref bytesConsumed, consumed),
            RespType.BulkString => ParseBulkString(line, data, ref bytesConsumed, consumed),
            RespType.Array => ParseArray(line, data, ref bytesConsumed, consumed),
            RespType.Null => ParseNull(ref bytesConsumed, consumed),
            RespType.Boolean => ParseBoolean(line, ref bytesConsumed, consumed),
            RespType.Double => ParseDouble(line, ref bytesConsumed, consumed),
            RespType.Map => ParseMap(line, data, ref bytesConsumed, consumed),
            RespType.Set => ParseSet(line, data, ref bytesConsumed, consumed),
            RespType.Push => ParsePush(line, data, ref bytesConsumed, consumed),
            _ => throw new InvalidDataException($"Unknown RESP type: {(char)type}")
        };
    }

    /// <summary>
    /// Parses a command from inline format (space-separated).
    /// </summary>
    public static RespValue? ParseInline(ReadOnlySpan<byte> data, out int bytesConsumed)
    {
        bytesConsumed = 0;

        var lineEnd = data.IndexOf(CrLf);
        if (lineEnd < 0)
            return null;

        var line = Encoding.UTF8.GetString(data[..lineEnd]);
        bytesConsumed = lineEnd + 2;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var elements = parts.Select(p => RespValue.BulkString(p)).ToArray();

        return RespValue.Array(elements);
    }

    private static RespValue ParseSimpleString(ReadOnlySpan<byte> line, ref int bytesConsumed, int consumed)
    {
        bytesConsumed = consumed;
        return RespValue.SimpleString(Encoding.UTF8.GetString(line));
    }

    private static RespValue ParseSimpleError(ReadOnlySpan<byte> line, ref int bytesConsumed, int consumed)
    {
        bytesConsumed = consumed;
        return RespValue.Error(Encoding.UTF8.GetString(line));
    }

    private static RespValue ParseInteger(ReadOnlySpan<byte> line, ref int bytesConsumed, int consumed)
    {
        bytesConsumed = consumed;
        var value = long.Parse(Encoding.UTF8.GetString(line));
        return RespValue.Integer(value);
    }

    private static RespValue? ParseBulkString(ReadOnlySpan<byte> line, ReadOnlySpan<byte> data, ref int bytesConsumed, int consumed)
    {
        var length = int.Parse(Encoding.UTF8.GetString(line));

        if (length < 0)
        {
            bytesConsumed = consumed;
            return RespValue.Null();
        }

        var totalNeeded = consumed + length + 2; // +2 for trailing CRLF
        if (data.Length < totalNeeded)
            return null;

        bytesConsumed = totalNeeded;
        var stringData = data.Slice(consumed, length).ToArray();
        return RespValue.BulkString(stringData);
    }

    private static RespValue? ParseArray(ReadOnlySpan<byte> line, ReadOnlySpan<byte> data, ref int bytesConsumed, int consumed)
    {
        var count = int.Parse(Encoding.UTF8.GetString(line));

        if (count < 0)
        {
            bytesConsumed = consumed;
            return RespValue.Null();
        }

        var elements = new RespValue[count];
        var offset = consumed;

        for (int i = 0; i < count; i++)
        {
            var element = Parse(data[offset..], out var elementConsumed);
            if (element == null)
                return null;

            elements[i] = element;
            offset += elementConsumed;
        }

        bytesConsumed = offset;
        return RespValue.Array(elements);
    }

    private static RespValue ParseNull(ref int bytesConsumed, int consumed)
    {
        bytesConsumed = consumed;
        return RespValue.Null();
    }

    private static RespValue ParseBoolean(ReadOnlySpan<byte> line, ref int bytesConsumed, int consumed)
    {
        bytesConsumed = consumed;
        var value = line[0] == (byte)'t';
        return RespValue.Boolean(value);
    }

    private static RespValue ParseDouble(ReadOnlySpan<byte> line, ref int bytesConsumed, int consumed)
    {
        bytesConsumed = consumed;
        var str = Encoding.UTF8.GetString(line);

        if (str == "inf")
            return RespValue.Double(double.PositiveInfinity);
        if (str == "-inf")
            return RespValue.Double(double.NegativeInfinity);

        var value = double.Parse(str);
        return RespValue.Double(value);
    }

    private static RespValue? ParseMap(ReadOnlySpan<byte> line, ReadOnlySpan<byte> data, ref int bytesConsumed, int consumed)
    {
        var count = int.Parse(Encoding.UTF8.GetString(line));
        var entries = new Dictionary<string, RespValue>();
        var offset = consumed;

        for (int i = 0; i < count; i++)
        {
            var key = Parse(data[offset..], out var keyConsumed);
            if (key == null)
                return null;
            offset += keyConsumed;

            var value = Parse(data[offset..], out var valueConsumed);
            if (value == null)
                return null;
            offset += valueConsumed;

            entries[key.AsString() ?? ""] = value;
        }

        bytesConsumed = offset;
        return RespValue.Map(entries);
    }

    private static RespValue? ParseSet(ReadOnlySpan<byte> line, ReadOnlySpan<byte> data, ref int bytesConsumed, int consumed)
    {
        var count = int.Parse(Encoding.UTF8.GetString(line));
        var elements = new RespValue[count];
        var offset = consumed;

        for (int i = 0; i < count; i++)
        {
            var element = Parse(data[offset..], out var elementConsumed);
            if (element == null)
                return null;

            elements[i] = element;
            offset += elementConsumed;
        }

        bytesConsumed = offset;
        return RespValue.Set(elements);
    }

    private static RespValue? ParsePush(ReadOnlySpan<byte> line, ReadOnlySpan<byte> data, ref int bytesConsumed, int consumed)
    {
        var count = int.Parse(Encoding.UTF8.GetString(line));
        var elements = new RespValue[count];
        var offset = consumed;

        for (int i = 0; i < count; i++)
        {
            var element = Parse(data[offset..], out var elementConsumed);
            if (element == null)
                return null;

            elements[i] = element;
            offset += elementConsumed;
        }

        bytesConsumed = offset;
        return RespValue.Push(elements);
    }
}

/// <summary>
/// RESP3 protocol serializer.
/// Thread-safe, allocation-efficient implementation.
/// </summary>
public static class RespSerializer
{
    /// <summary>
    /// Serializes a RESP value to bytes.
    /// </summary>
    public static byte[] Serialize(RespValue value)
    {
        using var ms = new MemoryStream();
        Serialize(value, ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Serializes a RESP value to a stream.
    /// </summary>
    public static void Serialize(RespValue value, Stream stream)
    {
        switch (value.Type)
        {
            case RespType.SimpleString:
                WriteSimpleString(stream, value.StringValue!);
                break;

            case RespType.SimpleError:
                WriteSimpleError(stream, value.StringValue!);
                break;

            case RespType.Integer:
                WriteInteger(stream, value.IntegerValue);
                break;

            case RespType.BulkString:
                WriteBulkString(stream, value.BulkBytes);
                break;

            case RespType.Array:
                WriteArray(stream, value.ArrayValue);
                break;

            case RespType.Null:
                WriteNull(stream);
                break;

            case RespType.Boolean:
                WriteBoolean(stream, value.BooleanValue);
                break;

            case RespType.Double:
                WriteDouble(stream, value.DoubleValue);
                break;

            case RespType.Map:
                WriteMap(stream, value.MapValue!);
                break;

            case RespType.Set:
                WriteSet(stream, value.ArrayValue!);
                break;

            case RespType.Push:
                WritePush(stream, value.ArrayValue!);
                break;

            default:
                throw new InvalidOperationException($"Cannot serialize type: {value.Type}");
        }
    }

    private static void WriteSimpleString(Stream stream, string value)
    {
        stream.WriteByte((byte)'+');
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
        stream.Write(new byte[] { (byte)'\r', (byte)'\n' });
    }

    private static void WriteSimpleError(Stream stream, string value)
    {
        stream.WriteByte((byte)'-');
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
        stream.Write(new byte[] { (byte)'\r', (byte)'\n' });
    }

    private static void WriteInteger(Stream stream, long value)
    {
        stream.WriteByte((byte)':');
        var bytes = Encoding.UTF8.GetBytes(value.ToString());
        stream.Write(bytes);
        stream.Write(new byte[] { (byte)'\r', (byte)'\n' });
    }

    private static void WriteBulkString(Stream stream, byte[]? value)
    {
        if (value == null)
        {
            stream.Write(Encoding.UTF8.GetBytes("$-1\r\n"));
            return;
        }

        stream.WriteByte((byte)'$');
        stream.Write(Encoding.UTF8.GetBytes(value.Length.ToString()));
        stream.Write(new byte[] { (byte)'\r', (byte)'\n' });
        stream.Write(value);
        stream.Write(new byte[] { (byte)'\r', (byte)'\n' });
    }

    private static void WriteArray(Stream stream, RespValue[]? elements)
    {
        if (elements == null)
        {
            stream.Write(Encoding.UTF8.GetBytes("*-1\r\n"));
            return;
        }

        stream.WriteByte((byte)'*');
        stream.Write(Encoding.UTF8.GetBytes(elements.Length.ToString()));
        stream.Write(new byte[] { (byte)'\r', (byte)'\n' });

        foreach (var element in elements)
        {
            Serialize(element, stream);
        }
    }

    private static void WriteNull(Stream stream)
    {
        stream.Write(Encoding.UTF8.GetBytes("_\r\n"));
    }

    private static void WriteBoolean(Stream stream, bool value)
    {
        stream.WriteByte((byte)'#');
        stream.WriteByte(value ? (byte)'t' : (byte)'f');
        stream.Write(new byte[] { (byte)'\r', (byte)'\n' });
    }

    private static void WriteDouble(Stream stream, double value)
    {
        stream.WriteByte((byte)',');
        string str;
        if (double.IsPositiveInfinity(value))
            str = "inf";
        else if (double.IsNegativeInfinity(value))
            str = "-inf";
        else
            str = value.ToString("G17");
        stream.Write(Encoding.UTF8.GetBytes(str));
        stream.Write(new byte[] { (byte)'\r', (byte)'\n' });
    }

    private static void WriteMap(Stream stream, Dictionary<string, RespValue> entries)
    {
        stream.WriteByte((byte)'%');
        stream.Write(Encoding.UTF8.GetBytes(entries.Count.ToString()));
        stream.Write(new byte[] { (byte)'\r', (byte)'\n' });

        foreach (var kvp in entries)
        {
            Serialize(RespValue.BulkString(kvp.Key), stream);
            Serialize(kvp.Value, stream);
        }
    }

    private static void WriteSet(Stream stream, RespValue[] elements)
    {
        stream.WriteByte((byte)'~');
        stream.Write(Encoding.UTF8.GetBytes(elements.Length.ToString()));
        stream.Write(new byte[] { (byte)'\r', (byte)'\n' });

        foreach (var element in elements)
        {
            Serialize(element, stream);
        }
    }

    private static void WritePush(Stream stream, RespValue[] elements)
    {
        stream.WriteByte((byte)'>');
        stream.Write(Encoding.UTF8.GetBytes(elements.Length.ToString()));
        stream.Write(new byte[] { (byte)'\r', (byte)'\n' });

        foreach (var element in elements)
        {
            Serialize(element, stream);
        }
    }
}
