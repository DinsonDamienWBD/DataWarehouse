using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Query;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.Relational;

/// <summary>
/// Bidirectional mapping between PostgreSQL OID type system and DW ColumnDataType.
/// Handles serialization/deserialization of values in both text and binary wire formats.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: PostgreSQL OID type mapping (ECOS-02)")]
public static class PostgreSqlTypeMapping
{
    // ─────────────────────────────────────────────────────────────
    // OID Constants
    // ─────────────────────────────────────────────────────────────

    private const int OidBool = 16;
    private const int OidBytea = 17;
    private const int OidInt8 = 20;
    private const int OidInt4 = 23;
    private const int OidText = 25;
    private const int OidFloat8 = 701;
    private const int OidVarchar = 1043;
    private const int OidNumeric = 1700;
    private const int OidTimestamp = 1114;
    private const int OidTimestampTz = 1184;
    private const int OidVoid = 0;

    // ─────────────────────────────────────────────────────────────
    // DW ColumnDataType -> PostgreSQL OID
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a DW ColumnDataType to the corresponding PostgreSQL OID.
    /// </summary>
    /// <param name="type">The DW column data type.</param>
    /// <returns>The PostgreSQL OID.</returns>
    public static int ToOid(ColumnDataType type) => type switch
    {
        ColumnDataType.Int32 => OidInt4,
        ColumnDataType.Int64 => OidInt8,
        ColumnDataType.Float64 => OidFloat8,
        ColumnDataType.String => OidText,
        ColumnDataType.Bool => OidBool,
        ColumnDataType.Binary => OidBytea,
        ColumnDataType.Decimal => OidNumeric,
        ColumnDataType.DateTime => OidTimestamp,
        ColumnDataType.Null => OidVoid,
        _ => OidText
    };

    // ─────────────────────────────────────────────────────────────
    // PostgreSQL OID -> DW ColumnDataType
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a PostgreSQL OID to the corresponding DW ColumnDataType.
    /// Unknown OIDs default to String.
    /// </summary>
    /// <param name="oid">The PostgreSQL type OID.</param>
    /// <returns>The DW ColumnDataType.</returns>
    public static ColumnDataType FromOid(int oid) => oid switch
    {
        OidInt4 => ColumnDataType.Int32,
        OidInt8 => ColumnDataType.Int64,
        OidFloat8 => ColumnDataType.Float64,
        OidText => ColumnDataType.String,
        OidVarchar => ColumnDataType.String,
        OidBool => ColumnDataType.Bool,
        OidBytea => ColumnDataType.Binary,
        OidNumeric => ColumnDataType.Decimal,
        OidTimestamp => ColumnDataType.DateTime,
        OidTimestampTz => ColumnDataType.DateTime,
        OidVoid => ColumnDataType.Null,
        _ => ColumnDataType.String // Unknown OIDs default to String
    };

    // ─────────────────────────────────────────────────────────────
    // OID -> PostgreSQL Type Name
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the PostgreSQL type name for a DW ColumnDataType.
    /// Used for RowDescription messages.
    /// </summary>
    /// <param name="type">The DW column data type.</param>
    /// <returns>The PostgreSQL type name string.</returns>
    public static string ToPostgresTypeName(ColumnDataType type) => type switch
    {
        ColumnDataType.Int32 => "int4",
        ColumnDataType.Int64 => "int8",
        ColumnDataType.Float64 => "float8",
        ColumnDataType.String => "text",
        ColumnDataType.Bool => "bool",
        ColumnDataType.Binary => "bytea",
        ColumnDataType.Decimal => "numeric",
        ColumnDataType.DateTime => "timestamp",
        ColumnDataType.Null => "void",
        _ => "text"
    };

    // ─────────────────────────────────────────────────────────────
    // Type Size
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the wire size in bytes for a PostgreSQL type OID.
    /// Returns -1 for variable-length types (text, bytea, numeric, varchar).
    /// </summary>
    /// <param name="oid">The PostgreSQL type OID.</param>
    /// <returns>Size in bytes, or -1 for variable-length.</returns>
    public static short GetTypeSize(int oid) => oid switch
    {
        OidBool => 1,
        OidInt4 => 4,
        OidInt8 => 8,
        OidFloat8 => 8,
        OidTimestamp => 8,
        OidTimestampTz => 8,
        OidText => -1,
        OidVarchar => -1,
        OidBytea => -1,
        OidNumeric => -1,
        OidVoid => 0,
        _ => -1
    };

    // ─────────────────────────────────────────────────────────────
    // Type Modifier
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the type modifier for a PostgreSQL type OID.
    /// Returns -1 for types with no modifier.
    /// </summary>
    /// <param name="oid">The PostgreSQL type OID.</param>
    /// <returns>Type modifier, or -1 if unmodified.</returns>
    public static int GetTypeModifier(int oid) => -1; // No modifiers by default

    // ─────────────────────────────────────────────────────────────
    // Format Code
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the preferred format code for a PostgreSQL type OID.
    /// 0 = text format, 1 = binary format.
    /// Text format is used for all types for maximum compatibility.
    /// </summary>
    /// <param name="oid">The PostgreSQL type OID.</param>
    /// <returns>Format code: 0 for text, 1 for binary.</returns>
    public static short GetFormatCode(int oid) => 0; // Text format for all types (maximum compatibility)

    // ─────────────────────────────────────────────────────────────
    // Value Serialization
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes a DW value to PostgreSQL wire format (text or binary).
    /// </summary>
    /// <param name="value">The value to serialize. Null values should not be passed (use NULL length -1 instead).</param>
    /// <param name="type">The DW ColumnDataType of the value.</param>
    /// <param name="binaryFormat">True for binary format, false for text format.</param>
    /// <returns>Serialized bytes for the wire protocol.</returns>
    public static byte[] SerializeValue(object? value, ColumnDataType type, bool binaryFormat)
    {
        if (value == null)
            return Array.Empty<byte>();

        if (binaryFormat)
            return SerializeBinary(value, type);

        return SerializeText(value, type);
    }

    private static byte[] SerializeText(object value, ColumnDataType type)
    {
        var text = type switch
        {
            ColumnDataType.Int32 => Convert.ToInt32(value).ToString(CultureInfo.InvariantCulture),
            ColumnDataType.Int64 => Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture),
            ColumnDataType.Float64 => Convert.ToDouble(value).ToString("G17", CultureInfo.InvariantCulture),
            ColumnDataType.Decimal => Convert.ToDecimal(value).ToString(CultureInfo.InvariantCulture),
            ColumnDataType.Bool => Convert.ToBoolean(value) ? "t" : "f",
            ColumnDataType.DateTime => value is DateTime dt
                ? dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)
                : value.ToString() ?? "",
            ColumnDataType.Binary => value is byte[] bytes
                ? "\\x" + Convert.ToHexString(bytes).ToLowerInvariant()
                : value.ToString() ?? "",
            ColumnDataType.String => value.ToString() ?? "",
            ColumnDataType.Null => "",
            _ => value.ToString() ?? ""
        };

        return Encoding.UTF8.GetBytes(text);
    }

    private static byte[] SerializeBinary(object value, ColumnDataType type)
    {
        switch (type)
        {
            case ColumnDataType.Int32:
            {
                var result = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(result, Convert.ToInt32(value));
                return result;
            }
            case ColumnDataType.Int64:
            {
                var result = new byte[8];
                BinaryPrimitives.WriteInt64BigEndian(result, Convert.ToInt64(value));
                return result;
            }
            case ColumnDataType.Float64:
            {
                var result = new byte[8];
                BinaryPrimitives.WriteInt64BigEndian(result, BitConverter.DoubleToInt64Bits(Convert.ToDouble(value)));
                return result;
            }
            case ColumnDataType.Bool:
            {
                return new[] { Convert.ToBoolean(value) ? (byte)1 : (byte)0 };
            }
            case ColumnDataType.DateTime:
            {
                // PostgreSQL binary timestamp: microseconds since 2000-01-01 00:00:00 UTC
                var dt = value is DateTime dateTime ? dateTime : DateTime.MinValue;
                var pgEpoch = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var microseconds = (dt.Ticks - pgEpoch.Ticks) / 10; // ticks to microseconds
                var result = new byte[8];
                BinaryPrimitives.WriteInt64BigEndian(result, microseconds);
                return result;
            }
            case ColumnDataType.Binary:
            {
                return value is byte[] bytes ? bytes : Array.Empty<byte>();
            }
            default:
            {
                // Fall back to text encoding for other types
                return SerializeText(value, type);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Value Deserialization
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Deserializes a PostgreSQL wire value to a DW object.
    /// </summary>
    /// <param name="data">The raw wire bytes.</param>
    /// <param name="oid">The PostgreSQL type OID.</param>
    /// <param name="binaryFormat">True if binary format, false if text format.</param>
    /// <returns>The deserialized DW value.</returns>
    public static object? DeserializeValue(ReadOnlySpan<byte> data, int oid, bool binaryFormat)
    {
        if (data.IsEmpty)
            return null;

        if (binaryFormat)
            return DeserializeBinary(data, oid);

        return DeserializeText(data, oid);
    }

    private static object? DeserializeText(ReadOnlySpan<byte> data, int oid)
    {
        var text = Encoding.UTF8.GetString(data);

        return oid switch
        {
            OidInt4 => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i32) ? i32 : null,
            OidInt8 => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i64) ? i64 : null,
            OidFloat8 => double.TryParse(text, NumberStyles.Float | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var f64) ? f64 : null,
            OidNumeric => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var dec) ? dec : null,
            OidBool => text is "t" or "true" or "1",
            OidTimestamp or OidTimestampTz => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null,
            OidBytea when text.StartsWith("\\x") => Convert.FromHexString(text[2..]),
            OidText or OidVarchar => text,
            _ => text
        };
    }

    private static object? DeserializeBinary(ReadOnlySpan<byte> data, int oid)
    {
        return oid switch
        {
            OidInt4 when data.Length >= 4 => BinaryPrimitives.ReadInt32BigEndian(data),
            OidInt8 when data.Length >= 8 => BinaryPrimitives.ReadInt64BigEndian(data),
            OidFloat8 when data.Length >= 8 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(data)),
            OidBool when data.Length >= 1 => data[0] != 0,
            OidTimestamp or OidTimestampTz when data.Length >= 8 =>
                new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddTicks(BinaryPrimitives.ReadInt64BigEndian(data) * 10),
            OidBytea => data.ToArray(),
            OidText or OidVarchar => Encoding.UTF8.GetString(data),
            OidNumeric => decimal.TryParse(Encoding.UTF8.GetString(data), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var dec) ? dec : null,
            _ => Encoding.UTF8.GetString(data)
        };
    }
}
