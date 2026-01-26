using System.Text;

namespace DataWarehouse.Plugins.TdsProtocol.Protocol;

/// <summary>
/// Converts between .NET types and TDS (Tabular Data Stream) types.
/// Provides type mapping for SQL Server wire protocol compatibility.
/// </summary>
public static class TdsTypeConverter
{
    /// <summary>
    /// Gets the TDS data type for a .NET type.
    /// </summary>
    /// <param name="type">The .NET type to convert.</param>
    /// <returns>The corresponding TDS data type.</returns>
    public static TdsDataType GetTdsTypeForClrType(Type type)
    {
        // Handle nullable types
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

        return underlyingType switch
        {
            Type t when t == typeof(bool) => TdsDataType.BitN,
            Type t when t == typeof(byte) => TdsDataType.TinyInt,
            Type t when t == typeof(short) => TdsDataType.SmallInt,
            Type t when t == typeof(int) => TdsDataType.IntN,
            Type t when t == typeof(long) => TdsDataType.BigInt,
            Type t when t == typeof(float) => TdsDataType.Real,
            Type t when t == typeof(double) => TdsDataType.FloatN,
            Type t when t == typeof(decimal) => TdsDataType.DecimalN,
            Type t when t == typeof(string) => TdsDataType.NVarChar,
            Type t when t == typeof(byte[]) => TdsDataType.VarBinary,
            Type t when t == typeof(DateTime) => TdsDataType.DateTimeN,
            Type t when t == typeof(DateTimeOffset) => TdsDataType.DateTimeOffset,
            Type t when t == typeof(TimeSpan) => TdsDataType.Time,
            Type t when t == typeof(DateOnly) => TdsDataType.Date,
            Type t when t == typeof(Guid) => TdsDataType.UniqueIdentifier,
            _ => TdsDataType.NVarChar // Default to NVARCHAR for unknown types
        };
    }

    /// <summary>
    /// Gets the maximum length for a TDS data type.
    /// </summary>
    /// <param name="dataType">The TDS data type.</param>
    /// <returns>The maximum length in bytes.</returns>
    public static int GetMaxLength(TdsDataType dataType)
    {
        return dataType switch
        {
            TdsDataType.Bit or TdsDataType.BitN => 1,
            TdsDataType.TinyInt => 1,
            TdsDataType.SmallInt => 2,
            TdsDataType.Int or TdsDataType.IntN => 4,
            TdsDataType.BigInt => 8,
            TdsDataType.Real => 4,
            TdsDataType.Float or TdsDataType.FloatN => 8,
            TdsDataType.SmallMoney => 4,
            TdsDataType.Money or TdsDataType.MoneyN => 8,
            TdsDataType.SmallDateTime => 4,
            TdsDataType.DateTime or TdsDataType.DateTimeN => 8,
            TdsDataType.Date => 3,
            TdsDataType.Time => 5,
            TdsDataType.DateTime2 => 8,
            TdsDataType.DateTimeOffset => 10,
            TdsDataType.UniqueIdentifier => 16,
            TdsDataType.Decimal or TdsDataType.Numeric or TdsDataType.DecimalN or TdsDataType.NumericN => 17,
            TdsDataType.NVarChar or TdsDataType.NChar => 4000 * 2, // Characters * 2 for Unicode
            TdsDataType.VarChar or TdsDataType.Char => 8000,
            TdsDataType.VarBinary or TdsDataType.Binary => 8000,
            TdsDataType.Text or TdsDataType.NText or TdsDataType.Image => int.MaxValue,
            TdsDataType.Xml => int.MaxValue,
            _ => 4000
        };
    }

    /// <summary>
    /// Creates column metadata from a column name and .NET type.
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <param name="clrType">The .NET type of the column.</param>
    /// <param name="isNullable">Whether the column is nullable.</param>
    /// <param name="maxLength">Optional maximum length override.</param>
    /// <returns>The column metadata.</returns>
    public static TdsColumnMetadata CreateColumnMetadata(
        string name,
        Type clrType,
        bool isNullable = true,
        int? maxLength = null)
    {
        var dataType = GetTdsTypeForClrType(clrType);
        var defaultMaxLen = GetMaxLength(dataType);

        ushort flags = 0;
        if (isNullable)
        {
            flags |= 0x0001; // Nullable flag
        }

        byte precision = 0;
        byte scale = 0;

        if (dataType == TdsDataType.DecimalN || dataType == TdsDataType.NumericN ||
            dataType == TdsDataType.Decimal || dataType == TdsDataType.Numeric)
        {
            precision = 18;
            scale = 2;
        }

        if (dataType == TdsDataType.Time || dataType == TdsDataType.DateTime2 ||
            dataType == TdsDataType.DateTimeOffset)
        {
            scale = 7; // Maximum precision for time types
        }

        return new TdsColumnMetadata
        {
            Name = name,
            DataType = dataType,
            MaxLength = maxLength ?? defaultMaxLen,
            Precision = precision,
            Scale = scale,
            Flags = flags,
            Collation = IsStringType(dataType) ? TdsCollation.Default : 0
        };
    }

    /// <summary>
    /// Creates column metadata by inferring type from a column name.
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <param name="isNullable">Whether the column is nullable.</param>
    /// <returns>The column metadata.</returns>
    public static TdsColumnMetadata CreateColumnMetadataFromName(string name, bool isNullable = true)
    {
        var lower = name.ToLowerInvariant();

        TdsDataType dataType;
        int maxLength;
        byte precision = 0;
        byte scale = 0;

        // Infer type from column name
        if (lower.Contains("id") || lower.Contains("uuid") || lower.Contains("guid"))
        {
            dataType = TdsDataType.UniqueIdentifier;
            maxLength = 16;
        }
        else if (lower.Contains("timestamp") || lower.Contains("created") || lower.Contains("updated") ||
                 lower.Contains("date") && lower.Contains("time"))
        {
            dataType = TdsDataType.DateTimeN;
            maxLength = 8;
        }
        else if (lower.Contains("date") && !lower.Contains("time"))
        {
            dataType = TdsDataType.Date;
            maxLength = 3;
        }
        else if (lower.Contains("time") && !lower.Contains("date"))
        {
            dataType = TdsDataType.Time;
            maxLength = 5;
            scale = 7;
        }
        else if (lower.Contains("count") || lower.Contains("num") || lower.Contains("qty") || lower.Contains("quantity"))
        {
            dataType = TdsDataType.IntN;
            maxLength = 4;
        }
        else if (lower.Contains("size") || lower.Contains("length") || lower.Contains("bytes") || lower.Contains("offset"))
        {
            dataType = TdsDataType.BigInt;
            maxLength = 8;
        }
        else if (lower.Contains("price") || lower.Contains("amount") || lower.Contains("total") ||
                 lower.Contains("cost") || lower.Contains("rate"))
        {
            dataType = TdsDataType.DecimalN;
            maxLength = 17;
            precision = 18;
            scale = 2;
        }
        else if (lower.Contains("percentage") || lower.Contains("percent") || lower.Contains("ratio"))
        {
            dataType = TdsDataType.FloatN;
            maxLength = 8;
        }
        else if (lower.Contains("is_") || lower.Contains("has_") || lower.Contains("can_") ||
                 lower.Contains("enabled") || lower.Contains("active") || lower.Contains("flag"))
        {
            dataType = TdsDataType.BitN;
            maxLength = 1;
        }
        else if (lower.Contains("data") || lower.Contains("binary") || lower.Contains("blob") ||
                 lower.Contains("image") || lower.Contains("content") && lower.Contains("hash"))
        {
            dataType = TdsDataType.VarBinary;
            maxLength = 8000;
        }
        else if (lower.Contains("json") || lower.Contains("xml"))
        {
            dataType = TdsDataType.NVarChar;
            maxLength = -1; // MAX
        }
        else
        {
            // Default to NVARCHAR
            dataType = TdsDataType.NVarChar;
            maxLength = 4000;
        }

        ushort flags = 0;
        if (isNullable)
        {
            flags |= 0x0001;
        }

        // Mark identity columns
        if (lower == "id" || lower.EndsWith("_id"))
        {
            flags |= 0x0010; // Identity flag
        }

        return new TdsColumnMetadata
        {
            Name = name,
            DataType = dataType,
            MaxLength = maxLength,
            Precision = precision,
            Scale = scale,
            Flags = flags,
            Collation = IsStringType(dataType) ? TdsCollation.Default : 0
        };
    }

    /// <summary>
    /// Checks if the data type is a string type.
    /// </summary>
    /// <param name="dataType">The TDS data type.</param>
    /// <returns>True if it's a string type; otherwise, false.</returns>
    public static bool IsStringType(TdsDataType dataType)
    {
        return dataType switch
        {
            TdsDataType.VarChar or TdsDataType.Char or TdsDataType.Text => true,
            TdsDataType.NVarChar or TdsDataType.NChar or TdsDataType.NText => true,
            TdsDataType.Xml => true,
            _ => false
        };
    }

    /// <summary>
    /// Checks if the data type is a Unicode string type.
    /// </summary>
    /// <param name="dataType">The TDS data type.</param>
    /// <returns>True if it's a Unicode string type; otherwise, false.</returns>
    public static bool IsUnicodeType(TdsDataType dataType)
    {
        return dataType switch
        {
            TdsDataType.NVarChar or TdsDataType.NChar or TdsDataType.NText => true,
            _ => false
        };
    }

    /// <summary>
    /// Checks if the data type is a binary type.
    /// </summary>
    /// <param name="dataType">The TDS data type.</param>
    /// <returns>True if it's a binary type; otherwise, false.</returns>
    public static bool IsBinaryType(TdsDataType dataType)
    {
        return dataType switch
        {
            TdsDataType.VarBinary or TdsDataType.Binary or TdsDataType.Image => true,
            TdsDataType.Timestamp => true,
            _ => false
        };
    }

    /// <summary>
    /// Checks if the data type is a numeric type.
    /// </summary>
    /// <param name="dataType">The TDS data type.</param>
    /// <returns>True if it's a numeric type; otherwise, false.</returns>
    public static bool IsNumericType(TdsDataType dataType)
    {
        return dataType switch
        {
            TdsDataType.TinyInt or TdsDataType.SmallInt or TdsDataType.Int or TdsDataType.BigInt => true,
            TdsDataType.IntN => true,
            TdsDataType.Real or TdsDataType.Float or TdsDataType.FloatN => true,
            TdsDataType.Decimal or TdsDataType.Numeric or TdsDataType.DecimalN or TdsDataType.NumericN => true,
            TdsDataType.Money or TdsDataType.SmallMoney or TdsDataType.MoneyN => true,
            _ => false
        };
    }

    /// <summary>
    /// Checks if the data type is a date/time type.
    /// </summary>
    /// <param name="dataType">The TDS data type.</param>
    /// <returns>True if it's a date/time type; otherwise, false.</returns>
    public static bool IsDateTimeType(TdsDataType dataType)
    {
        return dataType switch
        {
            TdsDataType.DateTime or TdsDataType.SmallDateTime or TdsDataType.DateTimeN => true,
            TdsDataType.Date or TdsDataType.Time or TdsDataType.DateTime2 or TdsDataType.DateTimeOffset => true,
            _ => false
        };
    }

    /// <summary>
    /// Checks if the data type is a fixed-length type.
    /// </summary>
    /// <param name="dataType">The TDS data type.</param>
    /// <returns>True if it's a fixed-length type; otherwise, false.</returns>
    public static bool IsFixedLengthType(TdsDataType dataType)
    {
        return dataType switch
        {
            TdsDataType.TinyInt or TdsDataType.SmallInt or TdsDataType.Int or TdsDataType.BigInt => true,
            TdsDataType.Bit => true,
            TdsDataType.Real or TdsDataType.Float => true,
            TdsDataType.Money or TdsDataType.SmallMoney => true,
            TdsDataType.DateTime or TdsDataType.SmallDateTime => true,
            TdsDataType.Char or TdsDataType.NChar or TdsDataType.Binary => true,
            _ => false
        };
    }

    /// <summary>
    /// Converts a value to its TDS text representation.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    /// <param name="dataType">The target TDS data type.</param>
    /// <returns>The value as bytes for the TDS protocol.</returns>
    public static byte[]? ConvertToTdsValue(object? value, TdsDataType dataType)
    {
        if (value == null || value == DBNull.Value)
        {
            return null;
        }

        return dataType switch
        {
            TdsDataType.Bit or TdsDataType.BitN => new[] { Convert.ToBoolean(value) ? (byte)1 : (byte)0 },
            TdsDataType.TinyInt => new[] { Convert.ToByte(value) },
            TdsDataType.SmallInt => BitConverter.GetBytes(Convert.ToInt16(value)),
            TdsDataType.Int or TdsDataType.IntN => BitConverter.GetBytes(Convert.ToInt32(value)),
            TdsDataType.BigInt => BitConverter.GetBytes(Convert.ToInt64(value)),
            TdsDataType.Real => BitConverter.GetBytes(Convert.ToSingle(value)),
            TdsDataType.Float or TdsDataType.FloatN => BitConverter.GetBytes(Convert.ToDouble(value)),
            TdsDataType.NVarChar or TdsDataType.NChar or TdsDataType.NText =>
                Encoding.Unicode.GetBytes(value.ToString() ?? ""),
            TdsDataType.VarChar or TdsDataType.Char or TdsDataType.Text =>
                Encoding.ASCII.GetBytes(value.ToString() ?? ""),
            TdsDataType.VarBinary or TdsDataType.Binary or TdsDataType.Image =>
                value as byte[] ?? Array.Empty<byte>(),
            TdsDataType.UniqueIdentifier => ConvertGuid(value),
            TdsDataType.DateTime or TdsDataType.DateTimeN => ConvertDateTime(value),
            TdsDataType.Date => ConvertDate(value),
            TdsDataType.Time => ConvertTime(value),
            TdsDataType.Decimal or TdsDataType.Numeric or TdsDataType.DecimalN or TdsDataType.NumericN =>
                ConvertDecimal(Convert.ToDecimal(value)),
            _ => Encoding.Unicode.GetBytes(value.ToString() ?? "")
        };
    }

    /// <summary>
    /// Converts a GUID value to TDS format.
    /// </summary>
    private static byte[] ConvertGuid(object value)
    {
        var guid = value is Guid g ? g : Guid.Parse(value.ToString() ?? Guid.Empty.ToString());
        return guid.ToByteArray();
    }

    /// <summary>
    /// Converts a DateTime value to TDS format.
    /// </summary>
    private static byte[] ConvertDateTime(object value)
    {
        var dt = value is DateTime d ? d : DateTime.Parse(value.ToString() ?? DateTime.MinValue.ToString());

        // SQL Server datetime: days since 1900-01-01 + time in 1/300 second intervals
        var baseDate = new DateTime(1900, 1, 1);
        var days = (dt.Date - baseDate).Days;
        var time = (int)((dt.TimeOfDay.TotalSeconds * 300) + 0.5);

        var result = new byte[8];
        BitConverter.GetBytes(days).CopyTo(result, 0);
        BitConverter.GetBytes(time).CopyTo(result, 4);
        return result;
    }

    /// <summary>
    /// Converts a Date value to TDS format.
    /// </summary>
    private static byte[] ConvertDate(object value)
    {
        DateTime dt;
        if (value is DateTime d)
            dt = d;
        else if (value is DateOnly dateOnly)
            dt = dateOnly.ToDateTime(TimeOnly.MinValue);
        else
            dt = DateTime.Parse(value.ToString() ?? DateTime.MinValue.ToString());

        // SQL Server date: days since 0001-01-01
        var days = (int)(dt.Date - DateTime.MinValue).TotalDays;

        var result = new byte[3];
        result[0] = (byte)(days & 0xFF);
        result[1] = (byte)((days >> 8) & 0xFF);
        result[2] = (byte)((days >> 16) & 0xFF);
        return result;
    }

    /// <summary>
    /// Converts a Time value to TDS format.
    /// </summary>
    private static byte[] ConvertTime(object value)
    {
        TimeSpan ts;
        if (value is TimeSpan t)
            ts = t;
        else if (value is TimeOnly timeOnly)
            ts = timeOnly.ToTimeSpan();
        else if (value is DateTime dt)
            ts = dt.TimeOfDay;
        else
            ts = TimeSpan.Parse(value.ToString() ?? "00:00:00");

        // SQL Server time: 100-nanosecond intervals since midnight (scale 7)
        var ticks = ts.Ticks;

        var result = new byte[5];
        result[0] = (byte)(ticks & 0xFF);
        result[1] = (byte)((ticks >> 8) & 0xFF);
        result[2] = (byte)((ticks >> 16) & 0xFF);
        result[3] = (byte)((ticks >> 24) & 0xFF);
        result[4] = (byte)((ticks >> 32) & 0xFF);
        return result;
    }

    /// <summary>
    /// Converts a Decimal value to TDS format.
    /// </summary>
    private static byte[] ConvertDecimal(decimal value)
    {
        var isNegative = value < 0;
        value = Math.Abs(value);

        // Get decimal bits
        var bits = decimal.GetBits(value);
        var lo = bits[0];
        var mid = bits[1];
        var hi = bits[2];
        var flags = bits[3];
        var scale = (byte)((flags >> 16) & 0xFF);

        // Calculate the 128-bit integer value
        var bytes = new byte[16];
        BitConverter.GetBytes(lo).CopyTo(bytes, 0);
        BitConverter.GetBytes(mid).CopyTo(bytes, 4);
        BitConverter.GetBytes(hi).CopyTo(bytes, 8);

        // Prepend sign byte
        var result = new byte[17];
        result[0] = isNegative ? (byte)0 : (byte)1;
        Buffer.BlockCopy(bytes, 0, result, 1, 16);

        return result;
    }

    /// <summary>
    /// Parses a TDS value to a .NET object.
    /// </summary>
    /// <param name="data">The raw byte data.</param>
    /// <param name="dataType">The TDS data type.</param>
    /// <returns>The parsed .NET object.</returns>
    public static object? ParseTdsValue(byte[]? data, TdsDataType dataType)
    {
        if (data == null || data.Length == 0)
        {
            return null;
        }

        return dataType switch
        {
            TdsDataType.Bit or TdsDataType.BitN => data[0] != 0,
            TdsDataType.TinyInt => data[0],
            TdsDataType.SmallInt => BitConverter.ToInt16(data, 0),
            TdsDataType.Int or TdsDataType.IntN => BitConverter.ToInt32(data, 0),
            TdsDataType.BigInt => BitConverter.ToInt64(data, 0),
            TdsDataType.Real => BitConverter.ToSingle(data, 0),
            TdsDataType.Float or TdsDataType.FloatN => BitConverter.ToDouble(data, 0),
            TdsDataType.NVarChar or TdsDataType.NChar or TdsDataType.NText => Encoding.Unicode.GetString(data),
            TdsDataType.VarChar or TdsDataType.Char or TdsDataType.Text => Encoding.ASCII.GetString(data),
            TdsDataType.VarBinary or TdsDataType.Binary or TdsDataType.Image => data,
            TdsDataType.UniqueIdentifier => new Guid(data),
            TdsDataType.DateTime or TdsDataType.DateTimeN => ParseDateTime(data),
            TdsDataType.Date => ParseDate(data),
            TdsDataType.Time => ParseTime(data),
            _ => Encoding.Unicode.GetString(data)
        };
    }

    /// <summary>
    /// Parses a TDS DateTime value.
    /// </summary>
    private static DateTime ParseDateTime(byte[] data)
    {
        if (data.Length < 8)
            return DateTime.MinValue;

        var days = BitConverter.ToInt32(data, 0);
        var time = BitConverter.ToInt32(data, 4);

        var baseDate = new DateTime(1900, 1, 1);
        return baseDate.AddDays(days).AddSeconds(time / 300.0);
    }

    /// <summary>
    /// Parses a TDS Date value.
    /// </summary>
    private static DateTime ParseDate(byte[] data)
    {
        if (data.Length < 3)
            return DateTime.MinValue;

        var days = data[0] | (data[1] << 8) | (data[2] << 16);
        return DateTime.MinValue.AddDays(days);
    }

    /// <summary>
    /// Parses a TDS Time value.
    /// </summary>
    private static TimeSpan ParseTime(byte[] data)
    {
        if (data.Length < 5)
            return TimeSpan.Zero;

        long ticks = data[0] | ((long)data[1] << 8) | ((long)data[2] << 16) |
                     ((long)data[3] << 24) | ((long)data[4] << 32);
        return new TimeSpan(ticks);
    }
}
