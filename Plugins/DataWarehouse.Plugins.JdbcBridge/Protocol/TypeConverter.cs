using System.Globalization;
using System.Text;

namespace DataWarehouse.Plugins.JdbcBridge.Protocol;

/// <summary>
/// Converts between .NET types and JDBC SQL types.
/// Provides type mapping for the JDBC wire protocol.
/// </summary>
public static class TypeConverter
{
    /// <summary>
    /// Gets the JDBC SQL type for a .NET type.
    /// </summary>
    /// <param name="type">The .NET type.</param>
    /// <returns>The corresponding JDBC SQL type.</returns>
    public static int GetJdbcType(Type type)
    {
        if (type == typeof(bool))
            return JdbcSqlTypes.BOOLEAN;
        if (type == typeof(byte))
            return JdbcSqlTypes.TINYINT;
        if (type == typeof(sbyte))
            return JdbcSqlTypes.TINYINT;
        if (type == typeof(short))
            return JdbcSqlTypes.SMALLINT;
        if (type == typeof(ushort))
            return JdbcSqlTypes.SMALLINT;
        if (type == typeof(int))
            return JdbcSqlTypes.INTEGER;
        if (type == typeof(uint))
            return JdbcSqlTypes.INTEGER;
        if (type == typeof(long))
            return JdbcSqlTypes.BIGINT;
        if (type == typeof(ulong))
            return JdbcSqlTypes.BIGINT;
        if (type == typeof(float))
            return JdbcSqlTypes.REAL;
        if (type == typeof(double))
            return JdbcSqlTypes.DOUBLE;
        if (type == typeof(decimal))
            return JdbcSqlTypes.DECIMAL;
        if (type == typeof(string))
            return JdbcSqlTypes.VARCHAR;
        if (type == typeof(char))
            return JdbcSqlTypes.CHAR;
        if (type == typeof(DateTime))
            return JdbcSqlTypes.TIMESTAMP;
        if (type == typeof(DateTimeOffset))
            return JdbcSqlTypes.TIMESTAMP_WITH_TIMEZONE;
        if (type == typeof(DateOnly))
            return JdbcSqlTypes.DATE;
        if (type == typeof(TimeOnly))
            return JdbcSqlTypes.TIME;
        if (type == typeof(TimeSpan))
            return JdbcSqlTypes.TIME;
        if (type == typeof(byte[]))
            return JdbcSqlTypes.VARBINARY;
        if (type == typeof(Guid))
            return JdbcSqlTypes.VARCHAR;

        // Nullable types
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
        {
            return GetJdbcType(underlying);
        }

        // Default to VARCHAR for unknown types
        return JdbcSqlTypes.VARCHAR;
    }

    /// <summary>
    /// Gets the JDBC type name for a SQL type code.
    /// </summary>
    /// <param name="sqlType">The SQL type code.</param>
    /// <returns>The type name.</returns>
    public static string GetTypeName(int sqlType)
    {
        return sqlType switch
        {
            JdbcSqlTypes.BIT => "BIT",
            JdbcSqlTypes.TINYINT => "TINYINT",
            JdbcSqlTypes.SMALLINT => "SMALLINT",
            JdbcSqlTypes.INTEGER => "INTEGER",
            JdbcSqlTypes.BIGINT => "BIGINT",
            JdbcSqlTypes.FLOAT => "FLOAT",
            JdbcSqlTypes.REAL => "REAL",
            JdbcSqlTypes.DOUBLE => "DOUBLE",
            JdbcSqlTypes.NUMERIC => "NUMERIC",
            JdbcSqlTypes.DECIMAL => "DECIMAL",
            JdbcSqlTypes.CHAR => "CHAR",
            JdbcSqlTypes.VARCHAR => "VARCHAR",
            JdbcSqlTypes.LONGVARCHAR => "LONGVARCHAR",
            JdbcSqlTypes.DATE => "DATE",
            JdbcSqlTypes.TIME => "TIME",
            JdbcSqlTypes.TIMESTAMP => "TIMESTAMP",
            JdbcSqlTypes.BINARY => "BINARY",
            JdbcSqlTypes.VARBINARY => "VARBINARY",
            JdbcSqlTypes.LONGVARBINARY => "LONGVARBINARY",
            JdbcSqlTypes.NULL => "NULL",
            JdbcSqlTypes.BOOLEAN => "BOOLEAN",
            JdbcSqlTypes.BLOB => "BLOB",
            JdbcSqlTypes.CLOB => "CLOB",
            JdbcSqlTypes.NCHAR => "NCHAR",
            JdbcSqlTypes.NVARCHAR => "NVARCHAR",
            JdbcSqlTypes.LONGNVARCHAR => "LONGNVARCHAR",
            JdbcSqlTypes.NCLOB => "NCLOB",
            JdbcSqlTypes.TIME_WITH_TIMEZONE => "TIME WITH TIME ZONE",
            JdbcSqlTypes.TIMESTAMP_WITH_TIMEZONE => "TIMESTAMP WITH TIME ZONE",
            _ => "OTHER"
        };
    }

    /// <summary>
    /// Gets the Java class name for a SQL type.
    /// </summary>
    /// <param name="sqlType">The SQL type code.</param>
    /// <returns>The Java class name.</returns>
    public static string GetClassName(int sqlType)
    {
        return sqlType switch
        {
            JdbcSqlTypes.BIT => "java.lang.Boolean",
            JdbcSqlTypes.BOOLEAN => "java.lang.Boolean",
            JdbcSqlTypes.TINYINT => "java.lang.Byte",
            JdbcSqlTypes.SMALLINT => "java.lang.Short",
            JdbcSqlTypes.INTEGER => "java.lang.Integer",
            JdbcSqlTypes.BIGINT => "java.lang.Long",
            JdbcSqlTypes.FLOAT => "java.lang.Float",
            JdbcSqlTypes.REAL => "java.lang.Float",
            JdbcSqlTypes.DOUBLE => "java.lang.Double",
            JdbcSqlTypes.NUMERIC => "java.math.BigDecimal",
            JdbcSqlTypes.DECIMAL => "java.math.BigDecimal",
            JdbcSqlTypes.CHAR => "java.lang.String",
            JdbcSqlTypes.VARCHAR => "java.lang.String",
            JdbcSqlTypes.LONGVARCHAR => "java.lang.String",
            JdbcSqlTypes.NCHAR => "java.lang.String",
            JdbcSqlTypes.NVARCHAR => "java.lang.String",
            JdbcSqlTypes.LONGNVARCHAR => "java.lang.String",
            JdbcSqlTypes.DATE => "java.sql.Date",
            JdbcSqlTypes.TIME => "java.sql.Time",
            JdbcSqlTypes.TIME_WITH_TIMEZONE => "java.time.OffsetTime",
            JdbcSqlTypes.TIMESTAMP => "java.sql.Timestamp",
            JdbcSqlTypes.TIMESTAMP_WITH_TIMEZONE => "java.time.OffsetDateTime",
            JdbcSqlTypes.BINARY => "[B",
            JdbcSqlTypes.VARBINARY => "[B",
            JdbcSqlTypes.LONGVARBINARY => "[B",
            JdbcSqlTypes.BLOB => "java.sql.Blob",
            JdbcSqlTypes.CLOB => "java.sql.Clob",
            JdbcSqlTypes.NCLOB => "java.sql.NClob",
            _ => "java.lang.Object"
        };
    }

    /// <summary>
    /// Gets the default precision for a SQL type.
    /// </summary>
    /// <param name="sqlType">The SQL type code.</param>
    /// <returns>The default precision.</returns>
    public static int GetPrecision(int sqlType)
    {
        return sqlType switch
        {
            JdbcSqlTypes.BIT => 1,
            JdbcSqlTypes.BOOLEAN => 1,
            JdbcSqlTypes.TINYINT => 3,
            JdbcSqlTypes.SMALLINT => 5,
            JdbcSqlTypes.INTEGER => 10,
            JdbcSqlTypes.BIGINT => 19,
            JdbcSqlTypes.FLOAT => 7,
            JdbcSqlTypes.REAL => 7,
            JdbcSqlTypes.DOUBLE => 15,
            JdbcSqlTypes.NUMERIC => 38,
            JdbcSqlTypes.DECIMAL => 38,
            JdbcSqlTypes.CHAR => 1,
            JdbcSqlTypes.VARCHAR => 2147483647,
            JdbcSqlTypes.LONGVARCHAR => 2147483647,
            JdbcSqlTypes.NCHAR => 1,
            JdbcSqlTypes.NVARCHAR => 1073741823,
            JdbcSqlTypes.LONGNVARCHAR => 1073741823,
            JdbcSqlTypes.DATE => 10,
            JdbcSqlTypes.TIME => 8,
            JdbcSqlTypes.TIME_WITH_TIMEZONE => 14,
            JdbcSqlTypes.TIMESTAMP => 23,
            JdbcSqlTypes.TIMESTAMP_WITH_TIMEZONE => 29,
            JdbcSqlTypes.BINARY => 2147483647,
            JdbcSqlTypes.VARBINARY => 2147483647,
            JdbcSqlTypes.LONGVARBINARY => 2147483647,
            _ => 0
        };
    }

    /// <summary>
    /// Gets the default scale for a SQL type.
    /// </summary>
    /// <param name="sqlType">The SQL type code.</param>
    /// <returns>The default scale.</returns>
    public static int GetScale(int sqlType)
    {
        return sqlType switch
        {
            JdbcSqlTypes.NUMERIC => 0,
            JdbcSqlTypes.DECIMAL => 0,
            JdbcSqlTypes.FLOAT => 7,
            JdbcSqlTypes.REAL => 7,
            JdbcSqlTypes.DOUBLE => 15,
            JdbcSqlTypes.TIME => 0,
            JdbcSqlTypes.TIME_WITH_TIMEZONE => 0,
            JdbcSqlTypes.TIMESTAMP => 3,
            JdbcSqlTypes.TIMESTAMP_WITH_TIMEZONE => 3,
            _ => 0
        };
    }

    /// <summary>
    /// Gets the display size for a SQL type.
    /// </summary>
    /// <param name="sqlType">The SQL type code.</param>
    /// <returns>The display size.</returns>
    public static int GetDisplaySize(int sqlType)
    {
        return sqlType switch
        {
            JdbcSqlTypes.BIT => 1,
            JdbcSqlTypes.BOOLEAN => 5, // "false"
            JdbcSqlTypes.TINYINT => 4, // -128
            JdbcSqlTypes.SMALLINT => 6, // -32768
            JdbcSqlTypes.INTEGER => 11, // -2147483648
            JdbcSqlTypes.BIGINT => 20, // -9223372036854775808
            JdbcSqlTypes.FLOAT => 14,
            JdbcSqlTypes.REAL => 14,
            JdbcSqlTypes.DOUBLE => 24,
            JdbcSqlTypes.NUMERIC => 40,
            JdbcSqlTypes.DECIMAL => 40,
            JdbcSqlTypes.CHAR => 1,
            JdbcSqlTypes.VARCHAR => 2147483647,
            JdbcSqlTypes.LONGVARCHAR => 2147483647,
            JdbcSqlTypes.DATE => 10, // YYYY-MM-DD
            JdbcSqlTypes.TIME => 8, // HH:MM:SS
            JdbcSqlTypes.TIME_WITH_TIMEZONE => 14,
            JdbcSqlTypes.TIMESTAMP => 23, // YYYY-MM-DD HH:MM:SS.sss
            JdbcSqlTypes.TIMESTAMP_WITH_TIMEZONE => 29,
            _ => 0
        };
    }

    /// <summary>
    /// Determines if a SQL type is signed.
    /// </summary>
    /// <param name="sqlType">The SQL type code.</param>
    /// <returns>True if signed, false otherwise.</returns>
    public static bool IsSigned(int sqlType)
    {
        return sqlType switch
        {
            JdbcSqlTypes.TINYINT => true,
            JdbcSqlTypes.SMALLINT => true,
            JdbcSqlTypes.INTEGER => true,
            JdbcSqlTypes.BIGINT => true,
            JdbcSqlTypes.FLOAT => true,
            JdbcSqlTypes.REAL => true,
            JdbcSqlTypes.DOUBLE => true,
            JdbcSqlTypes.NUMERIC => true,
            JdbcSqlTypes.DECIMAL => true,
            _ => false
        };
    }

    /// <summary>
    /// Creates column metadata from a column name with type inference.
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <param name="index">The column index (0-based).</param>
    /// <returns>The column metadata.</returns>
    public static JdbcColumnMetadata CreateColumnMetadata(string name, int index)
    {
        var sqlType = InferTypeFromColumnName(name);
        return CreateColumnMetadata(name, sqlType, index);
    }

    /// <summary>
    /// Creates column metadata from a column name and type.
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <param name="sqlType">The SQL type.</param>
    /// <param name="index">The column index (0-based).</param>
    /// <returns>The column metadata.</returns>
    public static JdbcColumnMetadata CreateColumnMetadata(string name, int sqlType, int index)
    {
        return new JdbcColumnMetadata
        {
            Label = name,
            Name = name,
            SqlType = sqlType,
            TypeName = GetTypeName(sqlType),
            Precision = GetPrecision(sqlType),
            Scale = GetScale(sqlType),
            DisplaySize = GetDisplaySize(sqlType),
            Signed = IsSigned(sqlType),
            ClassName = GetClassName(sqlType)
        };
    }

    /// <summary>
    /// Creates column metadata from a .NET type.
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <param name="type">The .NET type.</param>
    /// <param name="index">The column index (0-based).</param>
    /// <returns>The column metadata.</returns>
    public static JdbcColumnMetadata CreateColumnMetadata(string name, Type type, int index)
    {
        var sqlType = GetJdbcType(type);
        return CreateColumnMetadata(name, sqlType, index);
    }

    /// <summary>
    /// Infers SQL type from column name using heuristics.
    /// </summary>
    /// <param name="columnName">The column name.</param>
    /// <returns>The inferred SQL type.</returns>
    private static int InferTypeFromColumnName(string columnName)
    {
        var lower = columnName.ToLowerInvariant();

        if (lower.Contains("id") || lower.Contains("uuid") || lower.Contains("guid"))
            return JdbcSqlTypes.VARCHAR;
        if (lower.Contains("timestamp") || lower.Contains("created") || lower.Contains("updated") || lower.Contains("modified"))
            return JdbcSqlTypes.TIMESTAMP;
        if (lower.Contains("date"))
            return JdbcSqlTypes.DATE;
        if (lower.Contains("time"))
            return JdbcSqlTypes.TIME;
        if (lower.Contains("count") || lower.Contains("size") || lower.Contains("length") || lower.Contains("num"))
            return JdbcSqlTypes.BIGINT;
        if (lower.Contains("amount") || lower.Contains("price") || lower.Contains("total") || lower.Contains("cost"))
            return JdbcSqlTypes.DECIMAL;
        if (lower.Contains("percent") || lower.Contains("rate") || lower.Contains("ratio"))
            return JdbcSqlTypes.DOUBLE;
        if (lower.Contains("is_") || lower.Contains("has_") || lower.Contains("enabled") || lower.Contains("active") || lower.Contains("flag"))
            return JdbcSqlTypes.BOOLEAN;
        if (lower.Contains("json") || lower.Contains("xml"))
            return JdbcSqlTypes.LONGVARCHAR;
        if (lower.Contains("data") || lower.Contains("bytes") || lower.Contains("binary") || lower.Contains("blob"))
            return JdbcSqlTypes.VARBINARY;

        return JdbcSqlTypes.VARCHAR;
    }

    /// <summary>
    /// Converts a .NET object to the appropriate format for wire transfer.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    /// <param name="sqlType">The target SQL type.</param>
    /// <returns>The converted value.</returns>
    public static object? ConvertValue(object? value, int sqlType)
    {
        if (value == null || value == DBNull.Value)
        {
            return null;
        }

        try
        {
            return sqlType switch
            {
                JdbcSqlTypes.BIT or JdbcSqlTypes.BOOLEAN => Convert.ToBoolean(value),
                JdbcSqlTypes.TINYINT => Convert.ToByte(value),
                JdbcSqlTypes.SMALLINT => Convert.ToInt16(value),
                JdbcSqlTypes.INTEGER => Convert.ToInt32(value),
                JdbcSqlTypes.BIGINT => Convert.ToInt64(value),
                JdbcSqlTypes.REAL or JdbcSqlTypes.FLOAT => Convert.ToSingle(value),
                JdbcSqlTypes.DOUBLE => Convert.ToDouble(value),
                JdbcSqlTypes.NUMERIC or JdbcSqlTypes.DECIMAL => Convert.ToDecimal(value),
                JdbcSqlTypes.DATE => value is DateTime dt ? dt.Date : DateTime.Parse(value.ToString()!).Date,
                JdbcSqlTypes.TIME or JdbcSqlTypes.TIME_WITH_TIMEZONE => value is DateTime dtt ? dtt : DateTime.Parse(value.ToString()!),
                JdbcSqlTypes.TIMESTAMP or JdbcSqlTypes.TIMESTAMP_WITH_TIMEZONE => value is DateTime dts ? dts : DateTime.Parse(value.ToString()!),
                JdbcSqlTypes.BINARY or JdbcSqlTypes.VARBINARY or JdbcSqlTypes.LONGVARBINARY or JdbcSqlTypes.BLOB => value is byte[] bytes ? bytes : Encoding.UTF8.GetBytes(value.ToString()!),
                _ => value.ToString()
            };
        }
        catch
        {
            return value.ToString();
        }
    }

    /// <summary>
    /// Parses a parameter value from wire format.
    /// </summary>
    /// <param name="data">The wire data.</param>
    /// <param name="offset">The current offset.</param>
    /// <param name="sqlType">The SQL type.</param>
    /// <returns>The parsed value.</returns>
    public static object? ParseParameterValue(byte[] data, ref int offset, int sqlType)
    {
        // Check null indicator
        var isNull = MessageReader.ReadBoolean(data, ref offset);
        if (isNull)
        {
            return null;
        }

        return sqlType switch
        {
            JdbcSqlTypes.BIT or JdbcSqlTypes.BOOLEAN => MessageReader.ReadBoolean(data, ref offset),
            JdbcSqlTypes.TINYINT => MessageReader.ReadByte(data, ref offset),
            JdbcSqlTypes.SMALLINT => MessageReader.ReadInt16(data, ref offset),
            JdbcSqlTypes.INTEGER => MessageReader.ReadInt32(data, ref offset),
            JdbcSqlTypes.BIGINT => MessageReader.ReadInt64(data, ref offset),
            JdbcSqlTypes.REAL or JdbcSqlTypes.FLOAT => MessageReader.ReadFloat(data, ref offset),
            JdbcSqlTypes.DOUBLE => MessageReader.ReadDouble(data, ref offset),
            JdbcSqlTypes.NUMERIC or JdbcSqlTypes.DECIMAL => decimal.Parse(MessageReader.ReadString(data, ref offset), CultureInfo.InvariantCulture),
            JdbcSqlTypes.DATE => new DateTime(MessageReader.ReadInt64(data, ref offset)),
            JdbcSqlTypes.TIME or JdbcSqlTypes.TIME_WITH_TIMEZONE => new DateTime(MessageReader.ReadInt64(data, ref offset)),
            JdbcSqlTypes.TIMESTAMP or JdbcSqlTypes.TIMESTAMP_WITH_TIMEZONE => new DateTime(MessageReader.ReadInt64(data, ref offset)),
            JdbcSqlTypes.BINARY or JdbcSqlTypes.VARBINARY or JdbcSqlTypes.LONGVARBINARY or JdbcSqlTypes.BLOB => MessageReader.ReadBytes(data, ref offset),
            _ => MessageReader.ReadString(data, ref offset)
        };
    }
}
