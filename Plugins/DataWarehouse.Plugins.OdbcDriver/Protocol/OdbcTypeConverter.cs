// <copyright file="OdbcTypeConverter.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Globalization;
using System.Text;

namespace DataWarehouse.Plugins.OdbcDriver.Protocol;

/// <summary>
/// Converts between ODBC SQL types, C types, and .NET types.
/// Provides comprehensive type mapping for the ODBC driver.
/// Thread-safe: All methods are thread-safe.
/// </summary>
public static class OdbcTypeConverter
{
    #region SQL Type to .NET Type Mapping

    /// <summary>
    /// Gets the .NET type corresponding to an ODBC SQL data type.
    /// </summary>
    /// <param name="sqlType">The ODBC SQL data type.</param>
    /// <returns>The corresponding .NET type.</returns>
    public static Type GetClrType(SqlDataType sqlType)
    {
        return sqlType switch
        {
            SqlDataType.Bit => typeof(bool),
            SqlDataType.TinyInt => typeof(byte),
            SqlDataType.SmallInt => typeof(short),
            SqlDataType.Integer => typeof(int),
            SqlDataType.BigInt => typeof(long),
            SqlDataType.Real => typeof(float),
            SqlDataType.Float or SqlDataType.Double => typeof(double),
            SqlDataType.Numeric or SqlDataType.Decimal => typeof(decimal),
            SqlDataType.Char or SqlDataType.VarChar or SqlDataType.LongVarChar => typeof(string),
            SqlDataType.WChar or SqlDataType.WVarChar or SqlDataType.WLongVarChar => typeof(string),
            SqlDataType.Binary or SqlDataType.VarBinary or SqlDataType.LongVarBinary => typeof(byte[]),
            SqlDataType.TypeDate => typeof(DateOnly),
            SqlDataType.TypeTime => typeof(TimeOnly),
            SqlDataType.TypeTimestamp or SqlDataType.DateTime => typeof(DateTime),
            SqlDataType.Guid => typeof(Guid),
            _ => typeof(object)
        };
    }

    /// <summary>
    /// Gets the ODBC SQL data type for a .NET type.
    /// </summary>
    /// <param name="type">The .NET type.</param>
    /// <returns>The corresponding ODBC SQL data type.</returns>
    public static SqlDataType GetSqlType(Type type)
    {
        if (type == typeof(bool))
            return SqlDataType.Bit;
        if (type == typeof(byte))
            return SqlDataType.TinyInt;
        if (type == typeof(sbyte))
            return SqlDataType.TinyInt;
        if (type == typeof(short))
            return SqlDataType.SmallInt;
        if (type == typeof(ushort))
            return SqlDataType.SmallInt;
        if (type == typeof(int))
            return SqlDataType.Integer;
        if (type == typeof(uint))
            return SqlDataType.Integer;
        if (type == typeof(long))
            return SqlDataType.BigInt;
        if (type == typeof(ulong))
            return SqlDataType.BigInt;
        if (type == typeof(float))
            return SqlDataType.Real;
        if (type == typeof(double))
            return SqlDataType.Double;
        if (type == typeof(decimal))
            return SqlDataType.Decimal;
        if (type == typeof(string))
            return SqlDataType.VarChar;
        if (type == typeof(char))
            return SqlDataType.Char;
        if (type == typeof(byte[]))
            return SqlDataType.VarBinary;
        if (type == typeof(DateTime))
            return SqlDataType.TypeTimestamp;
        if (type == typeof(DateTimeOffset))
            return SqlDataType.TypeTimestamp;
        if (type == typeof(DateOnly))
            return SqlDataType.TypeDate;
        if (type == typeof(TimeOnly))
            return SqlDataType.TypeTime;
        if (type == typeof(TimeSpan))
            return SqlDataType.TypeTime;
        if (type == typeof(Guid))
            return SqlDataType.Guid;

        return SqlDataType.VarChar;
    }

    #endregion

    #region C Type to SQL Type Mapping

    /// <summary>
    /// Gets the default C type for an ODBC SQL data type.
    /// </summary>
    /// <param name="sqlType">The ODBC SQL data type.</param>
    /// <returns>The default C type for binding.</returns>
    public static SqlCType GetDefaultCType(SqlDataType sqlType)
    {
        return sqlType switch
        {
            SqlDataType.Bit => SqlCType.Bit,
            SqlDataType.TinyInt => SqlCType.UTinyInt,
            SqlDataType.SmallInt => SqlCType.SShort,
            SqlDataType.Integer => SqlCType.SLong,
            SqlDataType.BigInt => SqlCType.SBigInt,
            SqlDataType.Real => SqlCType.Float,
            SqlDataType.Float or SqlDataType.Double => SqlCType.Double,
            SqlDataType.Numeric or SqlDataType.Decimal => SqlCType.Numeric,
            SqlDataType.Char or SqlDataType.VarChar or SqlDataType.LongVarChar => SqlCType.Char,
            SqlDataType.WChar or SqlDataType.WVarChar or SqlDataType.WLongVarChar => SqlCType.WChar,
            SqlDataType.Binary or SqlDataType.VarBinary or SqlDataType.LongVarBinary => SqlCType.Binary,
            SqlDataType.TypeDate => SqlCType.TypeDate,
            SqlDataType.TypeTime => SqlCType.TypeTime,
            SqlDataType.TypeTimestamp or SqlDataType.DateTime => SqlCType.TypeTimestamp,
            SqlDataType.Guid => SqlCType.Guid,
            _ => SqlCType.Char
        };
    }

    /// <summary>
    /// Gets the ODBC SQL type for a C type.
    /// </summary>
    /// <param name="cType">The C type.</param>
    /// <returns>The corresponding ODBC SQL type.</returns>
    public static SqlDataType GetSqlTypeFromCType(SqlCType cType)
    {
        return cType switch
        {
            SqlCType.Bit => SqlDataType.Bit,
            SqlCType.STinyInt or SqlCType.UTinyInt => SqlDataType.TinyInt,
            SqlCType.SShort or SqlCType.UShort => SqlDataType.SmallInt,
            SqlCType.SLong or SqlCType.ULong or SqlCType.Long => SqlDataType.Integer,
            SqlCType.SBigInt or SqlCType.UBigInt => SqlDataType.BigInt,
            SqlCType.Float => SqlDataType.Real,
            SqlCType.Double => SqlDataType.Double,
            SqlCType.Numeric => SqlDataType.Decimal,
            SqlCType.Char => SqlDataType.VarChar,
            SqlCType.WChar => SqlDataType.WVarChar,
            SqlCType.Binary => SqlDataType.VarBinary,
            SqlCType.TypeDate => SqlDataType.TypeDate,
            SqlCType.TypeTime => SqlDataType.TypeTime,
            SqlCType.TypeTimestamp => SqlDataType.TypeTimestamp,
            SqlCType.Guid => SqlDataType.Guid,
            _ => SqlDataType.VarChar
        };
    }

    #endregion

    #region Type Properties

    /// <summary>
    /// Gets the type name for an ODBC SQL data type.
    /// </summary>
    /// <param name="sqlType">The ODBC SQL data type.</param>
    /// <returns>The type name string.</returns>
    public static string GetTypeName(SqlDataType sqlType)
    {
        return sqlType switch
        {
            SqlDataType.Bit => "BIT",
            SqlDataType.TinyInt => "TINYINT",
            SqlDataType.SmallInt => "SMALLINT",
            SqlDataType.Integer => "INTEGER",
            SqlDataType.BigInt => "BIGINT",
            SqlDataType.Real => "REAL",
            SqlDataType.Float => "FLOAT",
            SqlDataType.Double => "DOUBLE PRECISION",
            SqlDataType.Numeric => "NUMERIC",
            SqlDataType.Decimal => "DECIMAL",
            SqlDataType.Char => "CHAR",
            SqlDataType.VarChar => "VARCHAR",
            SqlDataType.LongVarChar => "LONG VARCHAR",
            SqlDataType.WChar => "NCHAR",
            SqlDataType.WVarChar => "NVARCHAR",
            SqlDataType.WLongVarChar => "NTEXT",
            SqlDataType.Binary => "BINARY",
            SqlDataType.VarBinary => "VARBINARY",
            SqlDataType.LongVarBinary => "LONG VARBINARY",
            SqlDataType.TypeDate => "DATE",
            SqlDataType.TypeTime => "TIME",
            SqlDataType.TypeTimestamp => "TIMESTAMP",
            SqlDataType.DateTime => "DATETIME",
            SqlDataType.Guid => "GUID",
            _ => "UNKNOWN"
        };
    }

    /// <summary>
    /// Gets the default column size for an ODBC SQL data type.
    /// </summary>
    /// <param name="sqlType">The ODBC SQL data type.</param>
    /// <returns>The default column size.</returns>
    public static int GetDefaultColumnSize(SqlDataType sqlType)
    {
        return sqlType switch
        {
            SqlDataType.Bit => 1,
            SqlDataType.TinyInt => 3,
            SqlDataType.SmallInt => 5,
            SqlDataType.Integer => 10,
            SqlDataType.BigInt => 19,
            SqlDataType.Real => 7,
            SqlDataType.Float or SqlDataType.Double => 15,
            SqlDataType.Numeric or SqlDataType.Decimal => 38,
            SqlDataType.Char or SqlDataType.WChar => 1,
            SqlDataType.VarChar or SqlDataType.WVarChar => 255,
            SqlDataType.LongVarChar or SqlDataType.WLongVarChar => 65535,
            SqlDataType.Binary => 1,
            SqlDataType.VarBinary => 255,
            SqlDataType.LongVarBinary => 65535,
            SqlDataType.TypeDate => 10,
            SqlDataType.TypeTime => 8,
            SqlDataType.TypeTimestamp or SqlDataType.DateTime => 26,
            SqlDataType.Guid => 36,
            _ => 255
        };
    }

    /// <summary>
    /// Gets the display size for an ODBC SQL data type with given column size.
    /// </summary>
    /// <param name="sqlType">The ODBC SQL data type.</param>
    /// <param name="columnSize">The column size.</param>
    /// <returns>The display size in characters.</returns>
    public static int GetDisplaySize(SqlDataType sqlType, int columnSize)
    {
        return sqlType switch
        {
            SqlDataType.Bit => 1,
            SqlDataType.TinyInt => 4, // -128 to 255
            SqlDataType.SmallInt => 6, // -32768 to 32767
            SqlDataType.Integer => 11, // -2147483648
            SqlDataType.BigInt => 20, // -9223372036854775808
            SqlDataType.Real => 14, // -x.xxxxxxE+yy
            SqlDataType.Float or SqlDataType.Double => 24, // -x.xxxxxxxxxxxxxxxE+yyy
            SqlDataType.Numeric or SqlDataType.Decimal => columnSize + 2, // sign + decimal point
            SqlDataType.Char or SqlDataType.VarChar or SqlDataType.LongVarChar => columnSize,
            SqlDataType.WChar or SqlDataType.WVarChar or SqlDataType.WLongVarChar => columnSize,
            SqlDataType.Binary or SqlDataType.VarBinary or SqlDataType.LongVarBinary => columnSize * 2, // hex display
            SqlDataType.TypeDate => 10, // YYYY-MM-DD
            SqlDataType.TypeTime => 8, // HH:MM:SS
            SqlDataType.TypeTimestamp or SqlDataType.DateTime => 26, // YYYY-MM-DD HH:MM:SS.ffffff
            SqlDataType.Guid => 36, // xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
            _ => columnSize
        };
    }

    /// <summary>
    /// Gets the octet length for an ODBC SQL data type.
    /// </summary>
    /// <param name="sqlType">The ODBC SQL data type.</param>
    /// <param name="columnSize">The column size.</param>
    /// <returns>The octet (byte) length.</returns>
    public static int GetOctetLength(SqlDataType sqlType, int columnSize)
    {
        return sqlType switch
        {
            SqlDataType.Bit => 1,
            SqlDataType.TinyInt => 1,
            SqlDataType.SmallInt => 2,
            SqlDataType.Integer => 4,
            SqlDataType.BigInt => 8,
            SqlDataType.Real => 4,
            SqlDataType.Float or SqlDataType.Double => 8,
            SqlDataType.Numeric or SqlDataType.Decimal => 19, // SQL_NUMERIC_STRUCT size
            SqlDataType.Char or SqlDataType.VarChar or SqlDataType.LongVarChar => columnSize,
            SqlDataType.WChar or SqlDataType.WVarChar or SqlDataType.WLongVarChar => columnSize * 2, // UTF-16
            SqlDataType.Binary or SqlDataType.VarBinary or SqlDataType.LongVarBinary => columnSize,
            SqlDataType.TypeDate => 6, // SQL_DATE_STRUCT size
            SqlDataType.TypeTime => 6, // SQL_TIME_STRUCT size
            SqlDataType.TypeTimestamp or SqlDataType.DateTime => 16, // SQL_TIMESTAMP_STRUCT size
            SqlDataType.Guid => 16,
            _ => columnSize
        };
    }

    /// <summary>
    /// Gets whether a type is a fixed-length type.
    /// </summary>
    /// <param name="sqlType">The ODBC SQL data type.</param>
    /// <returns>True if fixed-length, false if variable-length.</returns>
    public static bool IsFixedLength(SqlDataType sqlType)
    {
        return sqlType switch
        {
            SqlDataType.Bit or
            SqlDataType.TinyInt or
            SqlDataType.SmallInt or
            SqlDataType.Integer or
            SqlDataType.BigInt or
            SqlDataType.Real or
            SqlDataType.Float or
            SqlDataType.Double or
            SqlDataType.TypeDate or
            SqlDataType.TypeTime or
            SqlDataType.TypeTimestamp or
            SqlDataType.DateTime or
            SqlDataType.Guid => true,
            _ => false
        };
    }

    /// <summary>
    /// Gets whether a type is a character type.
    /// </summary>
    /// <param name="sqlType">The ODBC SQL data type.</param>
    /// <returns>True if character type, false otherwise.</returns>
    public static bool IsCharacterType(SqlDataType sqlType)
    {
        return sqlType is SqlDataType.Char or
               SqlDataType.VarChar or
               SqlDataType.LongVarChar or
               SqlDataType.WChar or
               SqlDataType.WVarChar or
               SqlDataType.WLongVarChar;
    }

    /// <summary>
    /// Gets whether a type is a numeric type.
    /// </summary>
    /// <param name="sqlType">The ODBC SQL data type.</param>
    /// <returns>True if numeric type, false otherwise.</returns>
    public static bool IsNumericType(SqlDataType sqlType)
    {
        return sqlType is SqlDataType.TinyInt or
               SqlDataType.SmallInt or
               SqlDataType.Integer or
               SqlDataType.BigInt or
               SqlDataType.Real or
               SqlDataType.Float or
               SqlDataType.Double or
               SqlDataType.Numeric or
               SqlDataType.Decimal;
    }

    /// <summary>
    /// Gets whether a type is a binary type.
    /// </summary>
    /// <param name="sqlType">The ODBC SQL data type.</param>
    /// <returns>True if binary type, false otherwise.</returns>
    public static bool IsBinaryType(SqlDataType sqlType)
    {
        return sqlType is SqlDataType.Binary or
               SqlDataType.VarBinary or
               SqlDataType.LongVarBinary;
    }

    /// <summary>
    /// Gets whether a type is a datetime type.
    /// </summary>
    /// <param name="sqlType">The ODBC SQL data type.</param>
    /// <returns>True if datetime type, false otherwise.</returns>
    public static bool IsDateTimeType(SqlDataType sqlType)
    {
        return sqlType is SqlDataType.TypeDate or
               SqlDataType.TypeTime or
               SqlDataType.TypeTimestamp or
               SqlDataType.DateTime;
    }

    #endregion

    #region Value Conversion

    /// <summary>
    /// Converts a .NET value to an ODBC-compatible value based on target type.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    /// <param name="targetType">The target C type.</param>
    /// <returns>The converted value.</returns>
    public static object? ConvertToOdbcValue(object? value, SqlCType targetType)
    {
        if (value == null || value == DBNull.Value)
        {
            return null;
        }

        return targetType switch
        {
            SqlCType.Char or SqlCType.WChar => ConvertToString(value),
            SqlCType.Short or SqlCType.SShort => Convert.ToInt16(value, CultureInfo.InvariantCulture),
            SqlCType.Long or SqlCType.SLong => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            SqlCType.SBigInt => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            SqlCType.UShort => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
            SqlCType.ULong => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
            SqlCType.UBigInt => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
            SqlCType.Float => Convert.ToSingle(value, CultureInfo.InvariantCulture),
            SqlCType.Double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            SqlCType.Numeric => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            SqlCType.Bit => Convert.ToBoolean(value),
            SqlCType.STinyInt => Convert.ToSByte(value, CultureInfo.InvariantCulture),
            SqlCType.UTinyInt => Convert.ToByte(value, CultureInfo.InvariantCulture),
            SqlCType.Binary => ConvertToBinary(value),
            SqlCType.TypeDate => ConvertToDate(value),
            SqlCType.TypeTime => ConvertToTime(value),
            SqlCType.TypeTimestamp => ConvertToTimestamp(value),
            SqlCType.Guid => ConvertToGuid(value),
            _ => value
        };
    }

    private static string ConvertToString(object value)
    {
        return value switch
        {
            string s => s,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly t => t.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
            Guid g => g.ToString(),
            bool b => b ? "1" : "0",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };
    }

    private static byte[] ConvertToBinary(object value)
    {
        return value switch
        {
            byte[] bytes => bytes,
            string s => Encoding.UTF8.GetBytes(s),
            Guid g => g.ToByteArray(),
            _ => Encoding.UTF8.GetBytes(value.ToString() ?? "")
        };
    }

    private static DateTime ConvertToDate(object value)
    {
        return value switch
        {
            DateTime dt => dt.Date,
            DateOnly d => d.ToDateTime(TimeOnly.MinValue),
            string s => DateTime.Parse(s, CultureInfo.InvariantCulture).Date,
            _ => DateTime.MinValue
        };
    }

    private static TimeSpan ConvertToTime(object value)
    {
        return value switch
        {
            DateTime dt => dt.TimeOfDay,
            TimeOnly t => t.ToTimeSpan(),
            TimeSpan ts => ts,
            string s => TimeSpan.Parse(s, CultureInfo.InvariantCulture),
            _ => TimeSpan.Zero
        };
    }

    private static DateTime ConvertToTimestamp(object value)
    {
        return value switch
        {
            DateTime dt => dt,
            DateTimeOffset dto => dto.UtcDateTime,
            DateOnly d => d.ToDateTime(TimeOnly.MinValue),
            string s => DateTime.Parse(s, CultureInfo.InvariantCulture),
            long ticks => new DateTime(ticks),
            _ => DateTime.MinValue
        };
    }

    private static Guid ConvertToGuid(object value)
    {
        return value switch
        {
            Guid g => g,
            string s => Guid.Parse(s),
            byte[] bytes when bytes.Length == 16 => new Guid(bytes),
            _ => Guid.Empty
        };
    }

    #endregion

    #region Column Info Creation

    /// <summary>
    /// Creates an OdbcColumnInfo from a column name and .NET type.
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <param name="type">The .NET type.</param>
    /// <param name="ordinal">The 1-based column ordinal.</param>
    /// <param name="isNullable">Whether the column is nullable.</param>
    /// <returns>The column info.</returns>
    public static OdbcColumnInfo CreateColumnInfo(string name, Type type, int ordinal, bool isNullable = true)
    {
        var sqlType = GetSqlType(type);
        var columnSize = GetDefaultColumnSize(sqlType);

        return new OdbcColumnInfo
        {
            Name = name,
            Ordinal = ordinal,
            SqlType = sqlType,
            ColumnSize = columnSize,
            DecimalDigits = IsNumericType(sqlType) ? (short)0 : (short)0,
            IsNullable = isNullable,
            DisplaySize = GetDisplaySize(sqlType, columnSize),
            OctetLength = GetOctetLength(sqlType, columnSize),
            TypeName = GetTypeName(sqlType),
            IsSearchable = true,
            IsUpdatable = false
        };
    }

    /// <summary>
    /// Creates an OdbcColumnInfo from explicit type information.
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <param name="sqlType">The SQL data type.</param>
    /// <param name="columnSize">The column size.</param>
    /// <param name="ordinal">The 1-based column ordinal.</param>
    /// <param name="decimalDigits">The decimal digits (scale).</param>
    /// <param name="isNullable">Whether the column is nullable.</param>
    /// <returns>The column info.</returns>
    public static OdbcColumnInfo CreateColumnInfo(
        string name,
        SqlDataType sqlType,
        int columnSize,
        int ordinal,
        short decimalDigits = 0,
        bool isNullable = true)
    {
        return new OdbcColumnInfo
        {
            Name = name,
            Ordinal = ordinal,
            SqlType = sqlType,
            ColumnSize = columnSize,
            DecimalDigits = decimalDigits,
            IsNullable = isNullable,
            DisplaySize = GetDisplaySize(sqlType, columnSize),
            OctetLength = GetOctetLength(sqlType, columnSize),
            TypeName = GetTypeName(sqlType),
            IsSearchable = true,
            IsUpdatable = false
        };
    }

    #endregion
}
