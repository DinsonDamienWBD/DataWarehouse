using System.Text;

namespace DataWarehouse.Plugins.PostgresWireProtocol.Protocol;

/// <summary>
/// Converts DataWarehouse types to PostgreSQL wire protocol types.
/// </summary>
public static class TypeConverter
{
    /// <summary>
    /// Gets PostgreSQL OID for a .NET type.
    /// </summary>
    public static int GetPgOidForType(Type type)
    {
        if (type == typeof(bool))
            return PgProtocolConstants.OidBool;
        if (type == typeof(short))
            return PgProtocolConstants.OidInt2;
        if (type == typeof(int))
            return PgProtocolConstants.OidInt4;
        if (type == typeof(long))
            return PgProtocolConstants.OidInt8;
        if (type == typeof(float))
            return PgProtocolConstants.OidFloat4;
        if (type == typeof(double))
            return PgProtocolConstants.OidFloat8;
        if (type == typeof(decimal))
            return PgProtocolConstants.OidNumeric;
        if (type == typeof(string))
            return PgProtocolConstants.OidText;
        if (type == typeof(byte[]))
            return PgProtocolConstants.OidBytea;
        if (type == typeof(DateTime))
            return PgProtocolConstants.OidTimestamp;
        if (type == typeof(DateTimeOffset))
            return PgProtocolConstants.OidTimestampTz;
        if (type == typeof(Guid))
            return PgProtocolConstants.OidUuid;

        // Default to text for unknown types
        return PgProtocolConstants.OidText;
    }

    /// <summary>
    /// Gets PostgreSQL OID for a column name hint.
    /// </summary>
    public static int GetPgOidForColumnName(string columnName)
    {
        var lower = columnName.ToLowerInvariant();

        if (lower.Contains("id") || lower.Contains("uuid"))
            return PgProtocolConstants.OidUuid;
        if (lower.Contains("timestamp") || lower.Contains("created") || lower.Contains("updated"))
            return PgProtocolConstants.OidTimestampTz;
        if (lower.Contains("date"))
            return PgProtocolConstants.OidDate;
        if (lower.Contains("time"))
            return PgProtocolConstants.OidTime;
        if (lower.Contains("count") || lower.Contains("size") || lower.Contains("length"))
            return PgProtocolConstants.OidInt8;
        if (lower.Contains("json"))
            return PgProtocolConstants.OidJsonb;
        if (lower.Contains("xml"))
            return PgProtocolConstants.OidXml;
        if (lower.Contains("data") || lower.Contains("bytes"))
            return PgProtocolConstants.OidBytea;

        // Default to text
        return PgProtocolConstants.OidText;
    }

    /// <summary>
    /// Converts a .NET value to PostgreSQL text format.
    /// </summary>
    public static byte[]? ValueToPostgresText(object? value)
    {
        if (value == null)
            return null;

        if (value is byte[] bytes)
            return bytes;

        if (value is bool b)
            return Encoding.UTF8.GetBytes(b ? "t" : "f");

        if (value is DateTime dt)
            return Encoding.UTF8.GetBytes(dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff"));

        if (value is DateTimeOffset dto)
            return Encoding.UTF8.GetBytes(dto.ToString("yyyy-MM-dd HH:mm:ss.ffffffzzz"));

        if (value is Guid guid)
            return Encoding.UTF8.GetBytes(guid.ToString());

        if (value is decimal dec)
            return Encoding.UTF8.GetBytes(dec.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (value is double dbl)
            return Encoding.UTF8.GetBytes(dbl.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (value is float flt)
            return Encoding.UTF8.GetBytes(flt.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Default to string representation
        return Encoding.UTF8.GetBytes(value.ToString() ?? "");
    }

    /// <summary>
    /// Creates column description from a simple column name.
    /// </summary>
    public static PgColumnDescription CreateColumnDescription(string columnName, int columnIndex)
    {
        var typeOid = GetPgOidForColumnName(columnName);

        return new PgColumnDescription
        {
            Name = columnName,
            TableOid = 0,
            ColumnAttributeNumber = (short)(columnIndex + 1),
            TypeOid = typeOid,
            TypeSize = GetTypeSizeForOid(typeOid),
            TypeModifier = -1,
            FormatCode = PgProtocolConstants.FormatText
        };
    }

    /// <summary>
    /// Creates column description from a .NET type.
    /// </summary>
    public static PgColumnDescription CreateColumnDescription(string columnName, Type type, int columnIndex)
    {
        var typeOid = GetPgOidForType(type);

        return new PgColumnDescription
        {
            Name = columnName,
            TableOid = 0,
            ColumnAttributeNumber = (short)(columnIndex + 1),
            TypeOid = typeOid,
            TypeSize = GetTypeSizeForOid(typeOid),
            TypeModifier = -1,
            FormatCode = PgProtocolConstants.FormatText
        };
    }

    /// <summary>
    /// Gets the type size for a PostgreSQL OID.
    /// </summary>
    private static short GetTypeSizeForOid(int oid)
    {
        return oid switch
        {
            PgProtocolConstants.OidBool => 1,
            PgProtocolConstants.OidInt2 => 2,
            PgProtocolConstants.OidInt4 => 4,
            PgProtocolConstants.OidInt8 => 8,
            PgProtocolConstants.OidFloat4 => 4,
            PgProtocolConstants.OidFloat8 => 8,
            PgProtocolConstants.OidUuid => 16,
            _ => -1 // Variable length
        };
    }

    /// <summary>
    /// Parses PostgreSQL text format value to .NET object.
    /// </summary>
    public static object? ParsePostgresValue(byte[]? data, int typeOid)
    {
        if (data == null)
            return null;

        var text = Encoding.UTF8.GetString(data);

        return typeOid switch
        {
            PgProtocolConstants.OidBool => text == "t" || text == "true" || text == "1",
            PgProtocolConstants.OidInt2 => short.Parse(text),
            PgProtocolConstants.OidInt4 => int.Parse(text),
            PgProtocolConstants.OidInt8 => long.Parse(text),
            PgProtocolConstants.OidFloat4 => float.Parse(text),
            PgProtocolConstants.OidFloat8 => double.Parse(text),
            PgProtocolConstants.OidNumeric => decimal.Parse(text),
            PgProtocolConstants.OidUuid => Guid.Parse(text),
            PgProtocolConstants.OidTimestamp or PgProtocolConstants.OidTimestampTz => DateTime.Parse(text),
            PgProtocolConstants.OidDate => DateTime.Parse(text).Date,
            PgProtocolConstants.OidBytea => data,
            _ => text
        };
    }
}
