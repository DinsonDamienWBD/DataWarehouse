using System.Globalization;
using System.Text;

namespace DataWarehouse.Plugins.OracleTnsProtocol.Protocol;

/// <summary>
/// Converts between .NET types and Oracle wire protocol formats.
/// Handles serialization/deserialization of data types according to Oracle's internal representations.
/// </summary>
/// <remarks>
/// Oracle uses specific binary formats for different data types:
/// - NUMBER: Variable-length BCD encoding with mantissa and exponent
/// - DATE: 7-byte fixed format (century, year, month, day, hour, minute, second)
/// - VARCHAR2: Length-prefixed UTF-8 or character set specific encoding
/// - RAW: Length-prefixed binary data
///
/// Thread-safety: All methods in this class are stateless and thread-safe.
/// </remarks>
public static class OracleTypeConverter
{
    /// <summary>
    /// Gets the Oracle type OID for a .NET type.
    /// </summary>
    /// <param name="type">The .NET type to map.</param>
    /// <returns>The corresponding Oracle type OID.</returns>
    public static int GetOracleTypeOid(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        // Handle nullable types
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

        return underlyingType switch
        {
            Type t when t == typeof(bool) => OracleTypeOid.Number, // Oracle has no bool, use NUMBER(1)
            Type t when t == typeof(byte) => OracleTypeOid.Number,
            Type t when t == typeof(sbyte) => OracleTypeOid.Number,
            Type t when t == typeof(short) => OracleTypeOid.Number,
            Type t when t == typeof(ushort) => OracleTypeOid.Number,
            Type t when t == typeof(int) => OracleTypeOid.Number,
            Type t when t == typeof(uint) => OracleTypeOid.Number,
            Type t when t == typeof(long) => OracleTypeOid.Number,
            Type t when t == typeof(ulong) => OracleTypeOid.Number,
            Type t when t == typeof(float) => OracleTypeOid.BinaryFloat,
            Type t when t == typeof(double) => OracleTypeOid.BinaryDouble,
            Type t when t == typeof(decimal) => OracleTypeOid.Number,
            Type t when t == typeof(string) => OracleTypeOid.Varchar2,
            Type t when t == typeof(char) => OracleTypeOid.Char,
            Type t when t == typeof(byte[]) => OracleTypeOid.Raw,
            Type t when t == typeof(DateTime) => OracleTypeOid.Timestamp,
            Type t when t == typeof(DateTimeOffset) => OracleTypeOid.TimestampTz,
            Type t when t == typeof(TimeSpan) => OracleTypeOid.IntervalDs,
            Type t when t == typeof(Guid) => OracleTypeOid.Raw, // GUID as RAW(16)
            _ => OracleTypeOid.Varchar2 // Default to VARCHAR2 for unknown types
        };
    }

    /// <summary>
    /// Gets the Oracle type OID based on column name heuristics.
    /// </summary>
    /// <param name="columnName">The column name to analyze.</param>
    /// <returns>The inferred Oracle type OID.</returns>
    public static int GetOracleTypeOidFromColumnName(string columnName)
    {
        if (string.IsNullOrEmpty(columnName))
            return OracleTypeOid.Varchar2;

        var lower = columnName.ToLowerInvariant();

        // ID-related columns are typically NUMBER
        if (lower.EndsWith("_id") || lower == "id" || lower.EndsWith("id"))
            return OracleTypeOid.Number;

        // UUID/GUID columns
        if (lower.Contains("uuid") || lower.Contains("guid"))
            return OracleTypeOid.Raw;

        // Timestamp/date columns
        if (lower.Contains("timestamp") || lower.Contains("created_at") ||
            lower.Contains("updated_at") || lower.Contains("modified_at"))
            return OracleTypeOid.Timestamp;

        if (lower.Contains("date") && !lower.Contains("update"))
            return OracleTypeOid.Date;

        // Count/size columns
        if (lower.Contains("count") || lower.Contains("size") ||
            lower.Contains("length") || lower.Contains("amount") ||
            lower.Contains("quantity") || lower.Contains("num"))
            return OracleTypeOid.Number;

        // Boolean-like columns
        if (lower.StartsWith("is_") || lower.StartsWith("has_") ||
            lower.StartsWith("can_") || lower.EndsWith("_flag"))
            return OracleTypeOid.Number;

        // JSON/XML columns
        if (lower.Contains("json"))
            return OracleTypeOid.Clob; // JSON stored in CLOB

        if (lower.Contains("xml"))
            return OracleTypeOid.XmlType;

        // Binary data columns
        if (lower.Contains("data") || lower.Contains("content") ||
            lower.Contains("binary") || lower.Contains("blob"))
            return OracleTypeOid.Blob;

        // Default to VARCHAR2
        return OracleTypeOid.Varchar2;
    }

    /// <summary>
    /// Converts a .NET value to Oracle wire format bytes.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    /// <param name="typeOid">The target Oracle type OID.</param>
    /// <returns>The value encoded in Oracle wire format, or null for NULL values.</returns>
    public static byte[]? ValueToOracleFormat(object? value, int typeOid)
    {
        if (value == null || value == DBNull.Value)
            return null;

        return typeOid switch
        {
            OracleTypeOid.Number => EncodeNumber(value),
            OracleTypeOid.Integer => EncodeNumber(value),
            OracleTypeOid.Float => EncodeNumber(value),
            OracleTypeOid.BinaryFloat => EncodeBinaryFloat(value),
            OracleTypeOid.BinaryDouble => EncodeBinaryDouble(value),
            OracleTypeOid.Varchar2 or OracleTypeOid.Char => EncodeString(value),
            OracleTypeOid.Date => EncodeDate(value),
            OracleTypeOid.Timestamp => EncodeTimestamp(value),
            OracleTypeOid.TimestampTz => EncodeTimestampTz(value),
            OracleTypeOid.Raw or OracleTypeOid.LongRaw => EncodeRaw(value),
            OracleTypeOid.Clob => EncodeString(value), // CLOB as text in simple cases
            OracleTypeOid.Blob => EncodeRaw(value),
            OracleTypeOid.Rowid => EncodeString(value), // ROWID as base64-like string
            _ => EncodeString(value) // Default: convert to string
        };
    }

    /// <summary>
    /// Parses Oracle wire format bytes to a .NET value.
    /// </summary>
    /// <param name="data">The Oracle wire format data.</param>
    /// <param name="typeOid">The Oracle type OID.</param>
    /// <returns>The parsed .NET value.</returns>
    public static object? ParseOracleValue(byte[]? data, int typeOid)
    {
        if (data == null || data.Length == 0)
            return null;

        return typeOid switch
        {
            OracleTypeOid.Number or OracleTypeOid.Integer or OracleTypeOid.Float => DecodeNumber(data),
            OracleTypeOid.BinaryFloat => DecodeBinaryFloat(data),
            OracleTypeOid.BinaryDouble => DecodeBinaryDouble(data),
            OracleTypeOid.Varchar2 or OracleTypeOid.Char or OracleTypeOid.Long => Encoding.UTF8.GetString(data),
            OracleTypeOid.Date => DecodeDate(data),
            OracleTypeOid.Timestamp => DecodeTimestamp(data),
            OracleTypeOid.TimestampTz => DecodeTimestampTz(data),
            OracleTypeOid.Raw or OracleTypeOid.LongRaw or OracleTypeOid.Blob => data,
            OracleTypeOid.Clob => Encoding.UTF8.GetString(data),
            _ => Encoding.UTF8.GetString(data)
        };
    }

    /// <summary>
    /// Creates an OracleColumnDescription from a column name and .NET type.
    /// </summary>
    /// <param name="columnName">The column name.</param>
    /// <param name="type">The .NET type of the column.</param>
    /// <param name="position">The column position (1-based).</param>
    /// <returns>A populated column description.</returns>
    public static OracleColumnDescription CreateColumnDescription(string columnName, Type type, int position)
    {
        ArgumentNullException.ThrowIfNull(columnName);
        ArgumentNullException.ThrowIfNull(type);

        var typeOid = GetOracleTypeOid(type);
        var (displaySize, precision, scale) = GetTypeMetrics(typeOid, type);

        return new OracleColumnDescription
        {
            Name = columnName.ToUpperInvariant(), // Oracle convention: uppercase names
            TypeOid = typeOid,
            DisplaySize = displaySize,
            Precision = precision,
            Scale = scale,
            IsNullable = !type.IsValueType || Nullable.GetUnderlyingType(type) != null,
            Position = position
        };
    }

    /// <summary>
    /// Creates an OracleColumnDescription from just a column name using heuristics.
    /// </summary>
    /// <param name="columnName">The column name.</param>
    /// <param name="position">The column position (1-based).</param>
    /// <returns>A populated column description.</returns>
    public static OracleColumnDescription CreateColumnDescription(string columnName, int position)
    {
        ArgumentNullException.ThrowIfNull(columnName);

        var typeOid = GetOracleTypeOidFromColumnName(columnName);
        var (displaySize, precision, scale) = GetTypeMetrics(typeOid, null);

        return new OracleColumnDescription
        {
            Name = columnName.ToUpperInvariant(),
            TypeOid = typeOid,
            DisplaySize = displaySize,
            Precision = precision,
            Scale = scale,
            IsNullable = true,
            Position = position
        };
    }

    /// <summary>
    /// Gets display size, precision, and scale metrics for an Oracle type.
    /// </summary>
    private static (int displaySize, int precision, int scale) GetTypeMetrics(int typeOid, Type? netType)
    {
        return typeOid switch
        {
            OracleTypeOid.Number => GetNumberMetrics(netType),
            OracleTypeOid.Integer => (11, 10, 0),
            OracleTypeOid.Float => (40, 126, 0),
            OracleTypeOid.BinaryFloat => (15, 0, 0),
            OracleTypeOid.BinaryDouble => (25, 0, 0),
            OracleTypeOid.Varchar2 => (4000, 0, 0),
            OracleTypeOid.Char => (2000, 0, 0),
            OracleTypeOid.Date => (19, 0, 0), // DD-MON-RR HH:MI:SS
            OracleTypeOid.Timestamp => (26, 6, 6), // With fractional seconds
            OracleTypeOid.TimestampTz => (34, 6, 6), // With timezone
            OracleTypeOid.Raw => (2000, 0, 0),
            OracleTypeOid.LongRaw => (2147483647, 0, 0),
            OracleTypeOid.Clob => (2147483647, 0, 0),
            OracleTypeOid.Blob => (2147483647, 0, 0),
            OracleTypeOid.Rowid => (18, 0, 0),
            _ => (4000, 0, 0)
        };
    }

    /// <summary>
    /// Gets NUMBER type metrics based on .NET type.
    /// </summary>
    private static (int displaySize, int precision, int scale) GetNumberMetrics(Type? netType)
    {
        if (netType == null)
            return (40, 38, 127); // Maximum NUMBER precision

        var underlyingType = Nullable.GetUnderlyingType(netType) ?? netType;

        return underlyingType switch
        {
            Type t when t == typeof(bool) => (1, 1, 0),
            Type t when t == typeof(byte) || t == typeof(sbyte) => (4, 3, 0),
            Type t when t == typeof(short) || t == typeof(ushort) => (6, 5, 0),
            Type t when t == typeof(int) || t == typeof(uint) => (11, 10, 0),
            Type t when t == typeof(long) || t == typeof(ulong) => (20, 19, 0),
            Type t when t == typeof(decimal) => (40, 38, 10),
            Type t when t == typeof(float) => (15, 7, 0),
            Type t when t == typeof(double) => (25, 15, 0),
            _ => (40, 38, 127)
        };
    }

    /// <summary>
    /// Encodes a numeric value to Oracle NUMBER format (text representation for simplicity).
    /// </summary>
    private static byte[] EncodeNumber(object value)
    {
        // For wire protocol, Oracle accepts text representation of numbers
        var numStr = Convert.ToDecimal(value, CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture);
        return Encoding.ASCII.GetBytes(numStr);
    }

    /// <summary>
    /// Encodes a float value to Oracle BINARY_FLOAT format.
    /// </summary>
    private static byte[] EncodeBinaryFloat(object value)
    {
        var floatValue = Convert.ToSingle(value, CultureInfo.InvariantCulture);
        return Encoding.ASCII.GetBytes(floatValue.ToString("G9", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Encodes a double value to Oracle BINARY_DOUBLE format.
    /// </summary>
    private static byte[] EncodeBinaryDouble(object value)
    {
        var doubleValue = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        return Encoding.ASCII.GetBytes(doubleValue.ToString("G17", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Encodes a string value to UTF-8 bytes.
    /// </summary>
    private static byte[] EncodeString(object value)
    {
        return Encoding.UTF8.GetBytes(value?.ToString() ?? string.Empty);
    }

    /// <summary>
    /// Encodes a DateTime to Oracle DATE format string.
    /// </summary>
    private static byte[] EncodeDate(object value)
    {
        DateTime dt;
        if (value is DateTime dateTime)
            dt = dateTime;
        else if (value is DateTimeOffset dto)
            dt = dto.DateTime;
        else
            dt = Convert.ToDateTime(value, CultureInfo.InvariantCulture);

        // Oracle DATE format: DD-MON-RR HH24:MI:SS
        return Encoding.ASCII.GetBytes(dt.ToString("dd-MMM-yy HH:mm:ss", CultureInfo.InvariantCulture).ToUpperInvariant());
    }

    /// <summary>
    /// Encodes a DateTime to Oracle TIMESTAMP format string.
    /// </summary>
    private static byte[] EncodeTimestamp(object value)
    {
        DateTime dt;
        if (value is DateTime dateTime)
            dt = dateTime;
        else if (value is DateTimeOffset dto)
            dt = dto.DateTime;
        else
            dt = Convert.ToDateTime(value, CultureInfo.InvariantCulture);

        // Oracle TIMESTAMP format: DD-MON-RR HH24.MI.SS.FF6
        return Encoding.ASCII.GetBytes(dt.ToString("dd-MMM-yy HH.mm.ss.ffffff", CultureInfo.InvariantCulture).ToUpperInvariant());
    }

    /// <summary>
    /// Encodes a DateTimeOffset to Oracle TIMESTAMP WITH TIME ZONE format string.
    /// </summary>
    private static byte[] EncodeTimestampTz(object value)
    {
        DateTimeOffset dto;
        if (value is DateTimeOffset offset)
            dto = offset;
        else if (value is DateTime dt)
            dto = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
        else
            dto = DateTimeOffset.Parse(value.ToString()!, CultureInfo.InvariantCulture);

        // Oracle TIMESTAMP WITH TIME ZONE format
        return Encoding.ASCII.GetBytes(dto.ToString("dd-MMM-yy HH.mm.ss.ffffff zzz", CultureInfo.InvariantCulture).ToUpperInvariant());
    }

    /// <summary>
    /// Encodes binary data (RAW/BLOB).
    /// </summary>
    private static byte[] EncodeRaw(object value)
    {
        if (value is byte[] bytes)
            return bytes;
        if (value is Guid guid)
            return guid.ToByteArray();

        // Try to interpret as hex string
        var str = value.ToString() ?? string.Empty;
        if (str.Length % 2 == 0 && str.All(c => Uri.IsHexDigit(c)))
        {
            return Convert.FromHexString(str);
        }

        return Encoding.UTF8.GetBytes(str);
    }

    /// <summary>
    /// Decodes Oracle NUMBER format to decimal.
    /// </summary>
    private static object DecodeNumber(byte[] data)
    {
        var str = Encoding.ASCII.GetString(data);
        if (decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var dec))
            return dec;
        if (double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var dbl))
            return dbl;
        return str;
    }

    /// <summary>
    /// Decodes Oracle BINARY_FLOAT format.
    /// </summary>
    private static object DecodeBinaryFloat(byte[] data)
    {
        var str = Encoding.ASCII.GetString(data);
        if (float.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var flt))
            return flt;
        return str;
    }

    /// <summary>
    /// Decodes Oracle BINARY_DOUBLE format.
    /// </summary>
    private static object DecodeBinaryDouble(byte[] data)
    {
        var str = Encoding.ASCII.GetString(data);
        if (double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var dbl))
            return dbl;
        return str;
    }

    /// <summary>
    /// Decodes Oracle DATE format to DateTime.
    /// </summary>
    private static object DecodeDate(byte[] data)
    {
        var str = Encoding.ASCII.GetString(data);
        if (DateTime.TryParseExact(str, new[]
            {
                "dd-MMM-yy HH:mm:ss",
                "dd-MMM-yy",
                "yyyy-MM-dd HH:mm:ss",
                "yyyy-MM-dd"
            },
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var dt))
        {
            return dt;
        }
        return str;
    }

    /// <summary>
    /// Decodes Oracle TIMESTAMP format to DateTime.
    /// </summary>
    private static object DecodeTimestamp(byte[] data)
    {
        var str = Encoding.ASCII.GetString(data);
        if (DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;
        return str;
    }

    /// <summary>
    /// Decodes Oracle TIMESTAMP WITH TIME ZONE format to DateTimeOffset.
    /// </summary>
    private static object DecodeTimestampTz(byte[] data)
    {
        var str = Encoding.ASCII.GetString(data);
        if (DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return dto;
        return str;
    }

    /// <summary>
    /// Gets the Oracle type name for a type OID.
    /// </summary>
    /// <param name="typeOid">The Oracle type OID.</param>
    /// <returns>The Oracle type name string.</returns>
    public static string GetOracleTypeName(int typeOid)
    {
        return typeOid switch
        {
            OracleTypeOid.Varchar2 => "VARCHAR2",
            OracleTypeOid.Number => "NUMBER",
            OracleTypeOid.Integer => "INTEGER",
            OracleTypeOid.Float => "FLOAT",
            OracleTypeOid.Long => "LONG",
            OracleTypeOid.Date => "DATE",
            OracleTypeOid.Raw => "RAW",
            OracleTypeOid.LongRaw => "LONG RAW",
            OracleTypeOid.Char => "CHAR",
            OracleTypeOid.BinaryFloat => "BINARY_FLOAT",
            OracleTypeOid.BinaryDouble => "BINARY_DOUBLE",
            OracleTypeOid.Clob => "CLOB",
            OracleTypeOid.Blob => "BLOB",
            OracleTypeOid.Bfile => "BFILE",
            OracleTypeOid.Timestamp => "TIMESTAMP",
            OracleTypeOid.TimestampTz => "TIMESTAMP WITH TIME ZONE",
            OracleTypeOid.TimestampLtz => "TIMESTAMP WITH LOCAL TIME ZONE",
            OracleTypeOid.IntervalYm => "INTERVAL YEAR TO MONTH",
            OracleTypeOid.IntervalDs => "INTERVAL DAY TO SECOND",
            OracleTypeOid.Rowid => "ROWID",
            OracleTypeOid.XmlType => "XMLTYPE",
            OracleTypeOid.Json => "JSON",
            OracleTypeOid.Boolean => "BOOLEAN",
            _ => "UNKNOWN"
        };
    }
}
