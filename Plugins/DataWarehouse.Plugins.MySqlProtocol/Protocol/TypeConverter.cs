using System.Globalization;
using System.Text;

namespace DataWarehouse.Plugins.MySqlProtocol.Protocol;

/// <summary>
/// Converts between .NET types and MySQL wire protocol types.
/// Provides type mapping, value serialization, and column metadata generation.
/// </summary>
public static class TypeConverter
{
    /// <summary>
    /// Gets the MySQL field type for a .NET type.
    /// </summary>
    /// <param name="type">The .NET type.</param>
    /// <returns>The corresponding MySQL field type.</returns>
    public static MySqlFieldType GetMySqlTypeForClrType(Type type)
    {
        // Handle nullable types
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

        return underlyingType switch
        {
            _ when underlyingType == typeof(bool) => MySqlFieldType.MYSQL_TYPE_TINY,
            _ when underlyingType == typeof(byte) => MySqlFieldType.MYSQL_TYPE_TINY,
            _ when underlyingType == typeof(sbyte) => MySqlFieldType.MYSQL_TYPE_TINY,
            _ when underlyingType == typeof(short) => MySqlFieldType.MYSQL_TYPE_SHORT,
            _ when underlyingType == typeof(ushort) => MySqlFieldType.MYSQL_TYPE_SHORT,
            _ when underlyingType == typeof(int) => MySqlFieldType.MYSQL_TYPE_LONG,
            _ when underlyingType == typeof(uint) => MySqlFieldType.MYSQL_TYPE_LONG,
            _ when underlyingType == typeof(long) => MySqlFieldType.MYSQL_TYPE_LONGLONG,
            _ when underlyingType == typeof(ulong) => MySqlFieldType.MYSQL_TYPE_LONGLONG,
            _ when underlyingType == typeof(float) => MySqlFieldType.MYSQL_TYPE_FLOAT,
            _ when underlyingType == typeof(double) => MySqlFieldType.MYSQL_TYPE_DOUBLE,
            _ when underlyingType == typeof(decimal) => MySqlFieldType.MYSQL_TYPE_NEWDECIMAL,
            _ when underlyingType == typeof(string) => MySqlFieldType.MYSQL_TYPE_VAR_STRING,
            _ when underlyingType == typeof(char) => MySqlFieldType.MYSQL_TYPE_STRING,
            _ when underlyingType == typeof(byte[]) => MySqlFieldType.MYSQL_TYPE_BLOB,
            _ when underlyingType == typeof(DateTime) => MySqlFieldType.MYSQL_TYPE_DATETIME,
            _ when underlyingType == typeof(DateTimeOffset) => MySqlFieldType.MYSQL_TYPE_DATETIME,
            _ when underlyingType == typeof(TimeSpan) => MySqlFieldType.MYSQL_TYPE_TIME,
            _ when underlyingType == typeof(DateOnly) => MySqlFieldType.MYSQL_TYPE_DATE,
            _ when underlyingType == typeof(TimeOnly) => MySqlFieldType.MYSQL_TYPE_TIME,
            _ when underlyingType == typeof(Guid) => MySqlFieldType.MYSQL_TYPE_VAR_STRING,
            _ => MySqlFieldType.MYSQL_TYPE_VAR_STRING
        };
    }

    /// <summary>
    /// Gets the MySQL field type based on column name heuristics.
    /// </summary>
    /// <param name="columnName">The column name.</param>
    /// <returns>The inferred MySQL field type.</returns>
    public static MySqlFieldType GetMySqlTypeForColumnName(string columnName)
    {
        var lower = columnName.ToLowerInvariant();

        if (lower.Contains("id") && !lower.Contains("void"))
            return MySqlFieldType.MYSQL_TYPE_LONGLONG;
        if (lower.Contains("uuid") || lower.Contains("guid"))
            return MySqlFieldType.MYSQL_TYPE_VAR_STRING;
        if (lower.Contains("timestamp") || lower.Contains("created_at") ||
            lower.Contains("updated_at") || lower.Contains("modified"))
            return MySqlFieldType.MYSQL_TYPE_DATETIME;
        if (lower.Contains("date") && !lower.Contains("update"))
            return MySqlFieldType.MYSQL_TYPE_DATE;
        if (lower.Contains("time") && !lower.Contains("datetime") && !lower.Contains("timestamp"))
            return MySqlFieldType.MYSQL_TYPE_TIME;
        if (lower.Contains("count") || lower.Contains("size") || lower.Contains("length") || lower.Contains("num"))
            return MySqlFieldType.MYSQL_TYPE_LONGLONG;
        if (lower.Contains("price") || lower.Contains("amount") || lower.Contains("total") ||
            lower.Contains("rate") || lower.Contains("cost"))
            return MySqlFieldType.MYSQL_TYPE_NEWDECIMAL;
        if (lower.Contains("is_") || lower.Contains("has_") || lower.Contains("enabled") ||
            lower.Contains("active") || lower.Contains("flag"))
            return MySqlFieldType.MYSQL_TYPE_TINY;
        if (lower.Contains("json") || lower.Contains("data") || lower.Contains("metadata"))
            return MySqlFieldType.MYSQL_TYPE_JSON;
        if (lower.Contains("content") || lower.Contains("body") || lower.Contains("text") ||
            lower.Contains("description"))
            return MySqlFieldType.MYSQL_TYPE_BLOB;
        if (lower.Contains("email") || lower.Contains("name") || lower.Contains("title") ||
            lower.Contains("label"))
            return MySqlFieldType.MYSQL_TYPE_VAR_STRING;

        return MySqlFieldType.MYSQL_TYPE_VAR_STRING;
    }

    /// <summary>
    /// Gets the column length for a MySQL field type.
    /// </summary>
    /// <param name="type">The MySQL field type.</param>
    /// <returns>The display length.</returns>
    public static uint GetColumnLengthForType(MySqlFieldType type)
    {
        return type switch
        {
            MySqlFieldType.MYSQL_TYPE_TINY => 4,
            MySqlFieldType.MYSQL_TYPE_SHORT => 6,
            MySqlFieldType.MYSQL_TYPE_INT24 => 9,
            MySqlFieldType.MYSQL_TYPE_LONG => 11,
            MySqlFieldType.MYSQL_TYPE_LONGLONG => 20,
            MySqlFieldType.MYSQL_TYPE_FLOAT => 12,
            MySqlFieldType.MYSQL_TYPE_DOUBLE => 22,
            MySqlFieldType.MYSQL_TYPE_DECIMAL => 65,
            MySqlFieldType.MYSQL_TYPE_NEWDECIMAL => 65,
            MySqlFieldType.MYSQL_TYPE_DATE => 10,
            MySqlFieldType.MYSQL_TYPE_TIME => 10,
            MySqlFieldType.MYSQL_TYPE_DATETIME => 19,
            MySqlFieldType.MYSQL_TYPE_TIMESTAMP => 19,
            MySqlFieldType.MYSQL_TYPE_YEAR => 4,
            MySqlFieldType.MYSQL_TYPE_VAR_STRING => 255,
            MySqlFieldType.MYSQL_TYPE_STRING => 255,
            MySqlFieldType.MYSQL_TYPE_BLOB => 65535,
            MySqlFieldType.MYSQL_TYPE_TINY_BLOB => 255,
            MySqlFieldType.MYSQL_TYPE_MEDIUM_BLOB => 16777215,
            MySqlFieldType.MYSQL_TYPE_LONG_BLOB => uint.MaxValue,
            MySqlFieldType.MYSQL_TYPE_JSON => uint.MaxValue,
            MySqlFieldType.MYSQL_TYPE_BIT => 64,
            MySqlFieldType.MYSQL_TYPE_ENUM => 255,
            MySqlFieldType.MYSQL_TYPE_SET => 255,
            MySqlFieldType.MYSQL_TYPE_GEOMETRY => uint.MaxValue,
            _ => 255
        };
    }

    /// <summary>
    /// Converts a .NET value to MySQL text protocol format.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>UTF-8 encoded bytes or null for NULL values.</returns>
    public static byte[]? ValueToMySqlText(object? value)
    {
        if (value == null || value == DBNull.Value)
            return null;

        var text = value switch
        {
            bool b => b ? "1" : "0",
            byte by => by.ToString(CultureInfo.InvariantCulture),
            sbyte sb => sb.ToString(CultureInfo.InvariantCulture),
            short s => s.ToString(CultureInfo.InvariantCulture),
            ushort us => us.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            uint ui => ui.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            ulong ul => ul.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString("G", CultureInfo.InvariantCulture),
            double d => d.ToString("G", CultureInfo.InvariantCulture),
            decimal dec => dec.ToString(CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.DateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly timeOnly => timeOnly.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            TimeSpan ts => FormatTimeSpan(ts),
            Guid g => g.ToString(),
            byte[] bytes => Convert.ToHexString(bytes),
            string str => str,
            _ => value.ToString() ?? string.Empty
        };

        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>
    /// Parses a MySQL text protocol value to a .NET object.
    /// </summary>
    /// <param name="data">The text data bytes.</param>
    /// <param name="type">The MySQL field type.</param>
    /// <returns>The parsed .NET object.</returns>
    public static object? ParseMySqlValue(byte[]? data, MySqlFieldType type)
    {
        if (data == null)
            return null;

        var text = Encoding.UTF8.GetString(data);

        return type switch
        {
            MySqlFieldType.MYSQL_TYPE_TINY => sbyte.TryParse(text, out var sb) ? sb : (object)0,
            MySqlFieldType.MYSQL_TYPE_SHORT => short.TryParse(text, out var s) ? s : (object)0,
            MySqlFieldType.MYSQL_TYPE_INT24 or
            MySqlFieldType.MYSQL_TYPE_LONG => int.TryParse(text, out var i) ? i : (object)0,
            MySqlFieldType.MYSQL_TYPE_LONGLONG => long.TryParse(text, out var l) ? l : (object)0,
            MySqlFieldType.MYSQL_TYPE_FLOAT => float.TryParse(text, CultureInfo.InvariantCulture, out var f) ? f : (object)0.0f,
            MySqlFieldType.MYSQL_TYPE_DOUBLE => double.TryParse(text, CultureInfo.InvariantCulture, out var d) ? d : (object)0.0,
            MySqlFieldType.MYSQL_TYPE_DECIMAL or
            MySqlFieldType.MYSQL_TYPE_NEWDECIMAL => decimal.TryParse(text, CultureInfo.InvariantCulture, out var dec) ? dec : (object)0m,
            MySqlFieldType.MYSQL_TYPE_DATE => DateTime.TryParse(text, out var dt) ? dt.Date : DateTime.MinValue,
            MySqlFieldType.MYSQL_TYPE_TIME => TimeSpan.TryParse(text, out var ts) ? ts : TimeSpan.Zero,
            MySqlFieldType.MYSQL_TYPE_DATETIME or
            MySqlFieldType.MYSQL_TYPE_TIMESTAMP => DateTime.TryParse(text, out var dtm) ? dtm : DateTime.MinValue,
            MySqlFieldType.MYSQL_TYPE_YEAR => int.TryParse(text, out var yr) ? yr : 0,
            MySqlFieldType.MYSQL_TYPE_BLOB or
            MySqlFieldType.MYSQL_TYPE_TINY_BLOB or
            MySqlFieldType.MYSQL_TYPE_MEDIUM_BLOB or
            MySqlFieldType.MYSQL_TYPE_LONG_BLOB => data,
            _ => text
        };
    }

    /// <summary>
    /// Creates a column description for a simple column.
    /// </summary>
    /// <param name="name">Column name.</param>
    /// <param name="index">Column index.</param>
    /// <param name="database">Database name.</param>
    /// <param name="table">Table name.</param>
    /// <returns>MySQL column description.</returns>
    public static MySqlColumnDescription CreateColumnDescription(
        string name,
        int index,
        string database = "datawarehouse",
        string table = "")
    {
        var type = GetMySqlTypeForColumnName(name);
        return new MySqlColumnDescription
        {
            Catalog = "def",
            Schema = database,
            VirtualTable = table,
            PhysicalTable = table,
            VirtualName = name,
            PhysicalName = name,
            CharacterSet = MySqlProtocolConstants.Utf8Mb4GeneralCi,
            ColumnLength = GetColumnLengthForType(type),
            ColumnType = type,
            Flags = MySqlFieldFlags.None,
            Decimals = type == MySqlFieldType.MYSQL_TYPE_NEWDECIMAL ? (byte)2 : (byte)0
        };
    }

    /// <summary>
    /// Creates a column description from a .NET type.
    /// </summary>
    /// <param name="name">Column name.</param>
    /// <param name="clrType">The .NET type.</param>
    /// <param name="index">Column index.</param>
    /// <param name="database">Database name.</param>
    /// <param name="table">Table name.</param>
    /// <returns>MySQL column description.</returns>
    public static MySqlColumnDescription CreateColumnDescription(
        string name,
        Type clrType,
        int index,
        string database = "datawarehouse",
        string table = "")
    {
        var type = GetMySqlTypeForClrType(clrType);
        var isUnsigned = clrType == typeof(byte) || clrType == typeof(ushort) ||
                         clrType == typeof(uint) || clrType == typeof(ulong);
        var isNotNull = !clrType.IsClass && Nullable.GetUnderlyingType(clrType) == null;

        var flags = MySqlFieldFlags.None;
        if (isUnsigned) flags |= MySqlFieldFlags.UNSIGNED_FLAG;
        if (isNotNull) flags |= MySqlFieldFlags.NOT_NULL_FLAG;
        if (type == MySqlFieldType.MYSQL_TYPE_BLOB) flags |= MySqlFieldFlags.BLOB_FLAG | MySqlFieldFlags.BINARY_FLAG;

        return new MySqlColumnDescription
        {
            Catalog = "def",
            Schema = database,
            VirtualTable = table,
            PhysicalTable = table,
            VirtualName = name,
            PhysicalName = name,
            CharacterSet = type == MySqlFieldType.MYSQL_TYPE_BLOB
                ? MySqlProtocolConstants.BinaryCollation
                : MySqlProtocolConstants.Utf8Mb4GeneralCi,
            ColumnLength = GetColumnLengthForType(type),
            ColumnType = type,
            Flags = flags,
            Decimals = type == MySqlFieldType.MYSQL_TYPE_NEWDECIMAL ? (byte)2 :
                       type == MySqlFieldType.MYSQL_TYPE_FLOAT ? (byte)31 :
                       type == MySqlFieldType.MYSQL_TYPE_DOUBLE ? (byte)31 : (byte)0
        };
    }

    /// <summary>
    /// Gets the flags for a column based on type.
    /// </summary>
    /// <param name="type">The MySQL field type.</param>
    /// <param name="isNullable">Whether the column is nullable.</param>
    /// <param name="isPrimaryKey">Whether the column is a primary key.</param>
    /// <param name="isAutoIncrement">Whether the column auto-increments.</param>
    /// <returns>The column flags.</returns>
    public static MySqlFieldFlags GetColumnFlags(
        MySqlFieldType type,
        bool isNullable = true,
        bool isPrimaryKey = false,
        bool isAutoIncrement = false)
    {
        var flags = MySqlFieldFlags.None;

        if (!isNullable)
            flags |= MySqlFieldFlags.NOT_NULL_FLAG;
        if (isPrimaryKey)
            flags |= MySqlFieldFlags.PRI_KEY_FLAG;
        if (isAutoIncrement)
            flags |= MySqlFieldFlags.AUTO_INCREMENT_FLAG;

        // Type-specific flags
        switch (type)
        {
            case MySqlFieldType.MYSQL_TYPE_BLOB:
            case MySqlFieldType.MYSQL_TYPE_TINY_BLOB:
            case MySqlFieldType.MYSQL_TYPE_MEDIUM_BLOB:
            case MySqlFieldType.MYSQL_TYPE_LONG_BLOB:
                flags |= MySqlFieldFlags.BLOB_FLAG | MySqlFieldFlags.BINARY_FLAG;
                break;

            case MySqlFieldType.MYSQL_TYPE_TINY:
            case MySqlFieldType.MYSQL_TYPE_SHORT:
            case MySqlFieldType.MYSQL_TYPE_LONG:
            case MySqlFieldType.MYSQL_TYPE_LONGLONG:
            case MySqlFieldType.MYSQL_TYPE_INT24:
            case MySqlFieldType.MYSQL_TYPE_FLOAT:
            case MySqlFieldType.MYSQL_TYPE_DOUBLE:
            case MySqlFieldType.MYSQL_TYPE_DECIMAL:
            case MySqlFieldType.MYSQL_TYPE_NEWDECIMAL:
                flags |= MySqlFieldFlags.NUM_FLAG;
                break;

            case MySqlFieldType.MYSQL_TYPE_TIMESTAMP:
                flags |= MySqlFieldFlags.TIMESTAMP_FLAG | MySqlFieldFlags.BINARY_FLAG;
                break;

            case MySqlFieldType.MYSQL_TYPE_ENUM:
                flags |= MySqlFieldFlags.ENUM_FLAG;
                break;

            case MySqlFieldType.MYSQL_TYPE_SET:
                flags |= MySqlFieldFlags.SET_FLAG;
                break;
        }

        return flags;
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        var sign = ts < TimeSpan.Zero ? "-" : "";
        var absTs = ts < TimeSpan.Zero ? -ts : ts;
        var hours = (int)absTs.TotalHours;
        return $"{sign}{hours:00}:{absTs.Minutes:00}:{absTs.Seconds:00}";
    }
}
