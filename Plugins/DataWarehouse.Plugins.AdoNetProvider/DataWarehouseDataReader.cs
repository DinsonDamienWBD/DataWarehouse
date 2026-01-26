using System.Collections;
using System.Data;
using System.Data.Common;
using System.Text.Json;

namespace DataWarehouse.Plugins.AdoNetProvider;

/// <summary>
/// Reads a forward-only stream of rows from a DataWarehouse database.
/// Provides efficient data access with support for multiple result sets.
/// </summary>
/// <remarks>
/// This reader is not thread-safe. Access should be synchronized by the caller.
/// The reader must be disposed after use to release resources.
/// </remarks>
public sealed class DataWarehouseDataReader : DbDataReader
{
    private readonly DataWarehouseCommand _command;
    private readonly CommandBehavior _behavior;
    private readonly List<ResultSet> _resultSets;
    private int _currentResultSetIndex;
    private int _currentRowIndex;
    private bool _closed;
    private bool _disposed;

    /// <inheritdoc/>
    public override int FieldCount => CurrentResultSet?.Columns.Length ?? 0;

    /// <inheritdoc/>
    public override int RecordsAffected => _resultSets.Sum(r => r.RecordsAffected);

    /// <inheritdoc/>
    public override bool HasRows => CurrentResultSet?.Rows.Count > 0;

    /// <inheritdoc/>
    public override bool IsClosed => _closed;

    /// <inheritdoc/>
    public override int Depth => 0;

    /// <inheritdoc/>
    public override object this[int ordinal] => GetValue(ordinal);

    /// <inheritdoc/>
    public override object this[string name] => GetValue(GetOrdinal(name));

    private ResultSet? CurrentResultSet =>
        _currentResultSetIndex >= 0 && _currentResultSetIndex < _resultSets.Count
            ? _resultSets[_currentResultSetIndex]
            : null;

    private object[]? CurrentRow =>
        CurrentResultSet != null &&
        _currentRowIndex >= 0 &&
        _currentRowIndex < CurrentResultSet.Rows.Count
            ? CurrentResultSet.Rows[_currentRowIndex]
            : null;

    /// <summary>
    /// Initializes a new instance of the DataWarehouseDataReader class.
    /// </summary>
    /// <param name="command">The command that produced this reader.</param>
    /// <param name="behavior">The command behavior.</param>
    internal DataWarehouseDataReader(DataWarehouseCommand command, CommandBehavior behavior)
    {
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _behavior = behavior;
        _resultSets = new List<ResultSet>();
        _currentResultSetIndex = -1;
        _currentRowIndex = -1;
    }

    /// <summary>
    /// Adds a result set to this reader.
    /// </summary>
    /// <param name="columns">The column metadata.</param>
    /// <param name="rows">The rows data.</param>
    /// <param name="recordsAffected">The number of records affected.</param>
    internal void AddResultSet(ColumnInfo[] columns, List<object[]> rows, int recordsAffected)
    {
        _resultSets.Add(new ResultSet
        {
            Columns = columns,
            Rows = rows,
            RecordsAffected = recordsAffected
        });

        // Move to first result set if this is the first one
        if (_currentResultSetIndex < 0)
        {
            _currentResultSetIndex = 0;
            _currentRowIndex = -1;
        }
    }

    /// <inheritdoc/>
    public override bool Read()
    {
        ThrowIfClosed();

        if (CurrentResultSet == null)
            return false;

        _currentRowIndex++;
        return _currentRowIndex < CurrentResultSet.Rows.Count;
    }

    /// <inheritdoc/>
    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        return await Task.FromResult(Read());
    }

    /// <inheritdoc/>
    public override bool NextResult()
    {
        ThrowIfClosed();

        _currentResultSetIndex++;
        _currentRowIndex = -1;

        return _currentResultSetIndex < _resultSets.Count;
    }

    /// <inheritdoc/>
    public override async Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        return await Task.FromResult(NextResult());
    }

    /// <inheritdoc/>
    public override void Close()
    {
        if (_closed)
            return;

        _closed = true;

        if ((_behavior & CommandBehavior.CloseConnection) != 0)
        {
            _command.Connection?.Close();
        }
    }

    /// <inheritdoc/>
    public override async Task CloseAsync()
    {
        if (_closed)
            return;

        _closed = true;

        if ((_behavior & CommandBehavior.CloseConnection) != 0 && _command.Connection != null)
        {
            await _command.Connection.CloseAsync();
        }
    }

    /// <inheritdoc/>
    public override bool GetBoolean(int ordinal)
    {
        var value = GetValue(ordinal);
        return value switch
        {
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            string s when bool.TryParse(s, out var result) => result,
            string s when s.Equals("t", StringComparison.OrdinalIgnoreCase) => true,
            string s when s.Equals("f", StringComparison.OrdinalIgnoreCase) => false,
            _ => Convert.ToBoolean(value)
        };
    }

    /// <inheritdoc/>
    public override byte GetByte(int ordinal)
    {
        return Convert.ToByte(GetValue(ordinal));
    }

    /// <inheritdoc/>
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var value = GetValue(ordinal);
        if (value is not byte[] bytes)
        {
            if (value is string s)
                bytes = Convert.FromHexString(s.Replace("\\x", ""));
            else
                throw new InvalidCastException($"Cannot convert {value?.GetType().Name ?? "null"} to byte[]");
        }

        if (buffer == null)
            return bytes.Length;

        var toCopy = Math.Min(length, bytes.Length - (int)dataOffset);
        if (toCopy > 0)
        {
            Array.Copy(bytes, (int)dataOffset, buffer, bufferOffset, toCopy);
        }
        return toCopy;
    }

    /// <inheritdoc/>
    public override char GetChar(int ordinal)
    {
        var value = GetValue(ordinal);
        return value switch
        {
            char c => c,
            string s when s.Length > 0 => s[0],
            _ => Convert.ToChar(value)
        };
    }

    /// <inheritdoc/>
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var value = GetString(ordinal);
        if (buffer == null)
            return value.Length;

        var toCopy = Math.Min(length, value.Length - (int)dataOffset);
        if (toCopy > 0)
        {
            value.CopyTo((int)dataOffset, buffer, bufferOffset, toCopy);
        }
        return toCopy;
    }

    /// <inheritdoc/>
    public override DateTime GetDateTime(int ordinal)
    {
        var value = GetValue(ordinal);
        return value switch
        {
            DateTime dt => dt,
            DateTimeOffset dto => dto.DateTime,
            string s when DateTime.TryParse(s, out var result) => result,
            long ticks => new DateTime(ticks),
            _ => Convert.ToDateTime(value)
        };
    }

    /// <inheritdoc/>
    public override decimal GetDecimal(int ordinal)
    {
        return Convert.ToDecimal(GetValue(ordinal));
    }

    /// <inheritdoc/>
    public override double GetDouble(int ordinal)
    {
        return Convert.ToDouble(GetValue(ordinal));
    }

    /// <inheritdoc/>
    public override float GetFloat(int ordinal)
    {
        return Convert.ToSingle(GetValue(ordinal));
    }

    /// <inheritdoc/>
    public override Guid GetGuid(int ordinal)
    {
        var value = GetValue(ordinal);
        return value switch
        {
            Guid g => g,
            string s => Guid.Parse(s),
            byte[] b when b.Length == 16 => new Guid(b),
            _ => throw new InvalidCastException($"Cannot convert {value?.GetType().Name ?? "null"} to Guid")
        };
    }

    /// <inheritdoc/>
    public override short GetInt16(int ordinal)
    {
        return Convert.ToInt16(GetValue(ordinal));
    }

    /// <inheritdoc/>
    public override int GetInt32(int ordinal)
    {
        return Convert.ToInt32(GetValue(ordinal));
    }

    /// <inheritdoc/>
    public override long GetInt64(int ordinal)
    {
        return Convert.ToInt64(GetValue(ordinal));
    }

    /// <inheritdoc/>
    public override string GetName(int ordinal)
    {
        ThrowIfClosed();
        ValidateOrdinal(ordinal);
        return CurrentResultSet!.Columns[ordinal].Name;
    }

    /// <inheritdoc/>
    public override int GetOrdinal(string name)
    {
        ThrowIfClosed();
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (CurrentResultSet == null)
            throw new InvalidOperationException("No result set is available.");

        for (int i = 0; i < CurrentResultSet.Columns.Length; i++)
        {
            if (string.Equals(CurrentResultSet.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        throw new DataWarehouseDataReaderException(
            DataWarehouseProviderException.ProviderErrorCode.InvalidColumnAccess,
            $"Column '{name}' not found.",
            columnName: name);
    }

    /// <inheritdoc/>
    public override string GetString(int ordinal)
    {
        var value = GetValue(ordinal);
        return value?.ToString() ?? string.Empty;
    }

    /// <inheritdoc/>
    public override object GetValue(int ordinal)
    {
        ThrowIfClosed();
        ValidateOrdinal(ordinal);

        if (CurrentRow == null)
            throw new InvalidOperationException("Invalid attempt to read when no data is present. Call Read() first.");

        var value = CurrentRow[ordinal];
        return value ?? DBNull.Value;
    }

    /// <inheritdoc/>
    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ThrowIfClosed();

        if (CurrentRow == null)
            return 0;

        var count = Math.Min(values.Length, FieldCount);
        for (int i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }
        return count;
    }

    /// <inheritdoc/>
    public override bool IsDBNull(int ordinal)
    {
        ThrowIfClosed();
        ValidateOrdinal(ordinal);

        if (CurrentRow == null)
            throw new InvalidOperationException("Invalid attempt to read when no data is present.");

        var value = CurrentRow[ordinal];
        return value == null || value == DBNull.Value;
    }

    /// <inheritdoc/>
    public override async Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
    {
        return await Task.FromResult(IsDBNull(ordinal));
    }

    /// <inheritdoc/>
    public override string GetDataTypeName(int ordinal)
    {
        ThrowIfClosed();
        ValidateOrdinal(ordinal);
        return CurrentResultSet!.Columns[ordinal].DataTypeName;
    }

    /// <inheritdoc/>
    public override Type GetFieldType(int ordinal)
    {
        ThrowIfClosed();
        ValidateOrdinal(ordinal);
        return CurrentResultSet!.Columns[ordinal].DataType;
    }

    /// <inheritdoc/>
    public override DataTable GetSchemaTable()
    {
        ThrowIfClosed();

        if (CurrentResultSet == null)
            return new DataTable();

        var table = new DataTable("SchemaTable");
        table.Columns.Add("ColumnName", typeof(string));
        table.Columns.Add("ColumnOrdinal", typeof(int));
        table.Columns.Add("ColumnSize", typeof(int));
        table.Columns.Add("NumericPrecision", typeof(short));
        table.Columns.Add("NumericScale", typeof(short));
        table.Columns.Add("DataType", typeof(Type));
        table.Columns.Add("ProviderType", typeof(int));
        table.Columns.Add("IsLong", typeof(bool));
        table.Columns.Add("AllowDBNull", typeof(bool));
        table.Columns.Add("IsReadOnly", typeof(bool));
        table.Columns.Add("IsRowVersion", typeof(bool));
        table.Columns.Add("IsUnique", typeof(bool));
        table.Columns.Add("IsKey", typeof(bool));
        table.Columns.Add("IsAutoIncrement", typeof(bool));
        table.Columns.Add("BaseCatalogName", typeof(string));
        table.Columns.Add("BaseSchemaName", typeof(string));
        table.Columns.Add("BaseTableName", typeof(string));
        table.Columns.Add("BaseColumnName", typeof(string));
        table.Columns.Add("DataTypeName", typeof(string));

        for (int i = 0; i < CurrentResultSet.Columns.Length; i++)
        {
            var col = CurrentResultSet.Columns[i];
            var row = table.NewRow();
            row["ColumnName"] = col.Name;
            row["ColumnOrdinal"] = i;
            row["ColumnSize"] = col.Size;
            row["NumericPrecision"] = (short)col.Precision;
            row["NumericScale"] = (short)col.Scale;
            row["DataType"] = col.DataType;
            row["ProviderType"] = (int)col.DbType;
            row["IsLong"] = col.Size > 8000;
            row["AllowDBNull"] = col.AllowDBNull;
            row["IsReadOnly"] = true;
            row["IsRowVersion"] = false;
            row["IsUnique"] = col.IsUnique;
            row["IsKey"] = col.IsKey;
            row["IsAutoIncrement"] = col.IsAutoIncrement;
            row["BaseCatalogName"] = string.Empty;
            row["BaseSchemaName"] = string.Empty;
            row["BaseTableName"] = col.BaseTableName ?? string.Empty;
            row["BaseColumnName"] = col.BaseColumnName ?? col.Name;
            row["DataTypeName"] = col.DataTypeName;
            table.Rows.Add(row);
        }

        return table;
    }

    /// <inheritdoc/>
    public override T GetFieldValue<T>(int ordinal)
    {
        var value = GetValue(ordinal);

        if (value == DBNull.Value)
        {
            if (default(T) == null)
                return default!;
            throw new InvalidCastException($"Cannot convert DBNull to {typeof(T).Name}");
        }

        if (value is T typedValue)
            return typedValue;

        // Handle JSON element conversion
        if (value is JsonElement element)
        {
            return ConvertJsonElement<T>(element);
        }

        // Attempt conversion
        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch (Exception ex)
        {
            throw new DataWarehouseDataReaderException(
                DataWarehouseProviderException.ProviderErrorCode.DataConversionError,
                $"Failed to convert value of type {value.GetType().Name} to {typeof(T).Name}",
                ex,
                ordinal);
        }
    }

    /// <inheritdoc/>
    public override async Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
    {
        return await Task.FromResult(GetFieldValue<T>(ordinal));
    }

    /// <inheritdoc/>
    public override IEnumerator GetEnumerator()
    {
        return new DbEnumerator(this);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            Close();
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    private void ThrowIfClosed()
    {
        if (_closed)
        {
            throw new InvalidOperationException("Data reader is closed.");
        }
    }

    private void ValidateOrdinal(int ordinal)
    {
        if (CurrentResultSet == null)
            throw new InvalidOperationException("No result set is available.");

        if (ordinal < 0 || ordinal >= CurrentResultSet.Columns.Length)
        {
            throw new DataWarehouseDataReaderException(
                DataWarehouseProviderException.ProviderErrorCode.InvalidColumnAccess,
                $"Column ordinal {ordinal} is out of range. Valid range is 0 to {CurrentResultSet.Columns.Length - 1}.",
                ordinal);
        }
    }

    private static T ConvertJsonElement<T>(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String when typeof(T) == typeof(string) => (T)(object)element.GetString()!,
            JsonValueKind.Number when typeof(T) == typeof(int) => (T)(object)element.GetInt32(),
            JsonValueKind.Number when typeof(T) == typeof(long) => (T)(object)element.GetInt64(),
            JsonValueKind.Number when typeof(T) == typeof(double) => (T)(object)element.GetDouble(),
            JsonValueKind.Number when typeof(T) == typeof(decimal) => (T)(object)element.GetDecimal(),
            JsonValueKind.True or JsonValueKind.False when typeof(T) == typeof(bool) => (T)(object)element.GetBoolean(),
            _ => JsonSerializer.Deserialize<T>(element.GetRawText())!
        };
    }

    private sealed class ResultSet
    {
        public ColumnInfo[] Columns { get; init; } = Array.Empty<ColumnInfo>();
        public List<object[]> Rows { get; init; } = new();
        public int RecordsAffected { get; init; }
    }
}

/// <summary>
/// Represents column metadata for a DataWarehouse result set.
/// </summary>
public sealed class ColumnInfo
{
    /// <summary>
    /// Gets or sets the column name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the data type name.
    /// </summary>
    public string DataTypeName { get; init; } = "object";

    /// <summary>
    /// Gets or sets the .NET data type.
    /// </summary>
    public Type DataType { get; init; } = typeof(object);

    /// <summary>
    /// Gets or sets the DbType.
    /// </summary>
    public DbType DbType { get; init; } = DbType.Object;

    /// <summary>
    /// Gets or sets the column size.
    /// </summary>
    public int Size { get; init; } = -1;

    /// <summary>
    /// Gets or sets the numeric precision.
    /// </summary>
    public int Precision { get; init; }

    /// <summary>
    /// Gets or sets the numeric scale.
    /// </summary>
    public int Scale { get; init; }

    /// <summary>
    /// Gets or sets whether the column allows null values.
    /// </summary>
    public bool AllowDBNull { get; init; } = true;

    /// <summary>
    /// Gets or sets whether the column is a key.
    /// </summary>
    public bool IsKey { get; init; }

    /// <summary>
    /// Gets or sets whether the column is unique.
    /// </summary>
    public bool IsUnique { get; init; }

    /// <summary>
    /// Gets or sets whether the column is auto-increment.
    /// </summary>
    public bool IsAutoIncrement { get; init; }

    /// <summary>
    /// Gets or sets the base table name.
    /// </summary>
    public string? BaseTableName { get; init; }

    /// <summary>
    /// Gets or sets the base column name.
    /// </summary>
    public string? BaseColumnName { get; init; }

    /// <summary>
    /// Infers column metadata from a PostgreSQL type OID.
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <param name="typeOid">The PostgreSQL type OID.</param>
    /// <param name="typeModifier">The type modifier.</param>
    /// <returns>The column info.</returns>
    public static ColumnInfo FromTypeOid(string name, int typeOid, int typeModifier)
    {
        return typeOid switch
        {
            16 => new ColumnInfo { Name = name, DataTypeName = "boolean", DataType = typeof(bool), DbType = DbType.Boolean },
            17 => new ColumnInfo { Name = name, DataTypeName = "bytea", DataType = typeof(byte[]), DbType = DbType.Binary },
            18 => new ColumnInfo { Name = name, DataTypeName = "char", DataType = typeof(char), DbType = DbType.StringFixedLength, Size = 1 },
            20 => new ColumnInfo { Name = name, DataTypeName = "int8", DataType = typeof(long), DbType = DbType.Int64 },
            21 => new ColumnInfo { Name = name, DataTypeName = "int2", DataType = typeof(short), DbType = DbType.Int16 },
            23 => new ColumnInfo { Name = name, DataTypeName = "int4", DataType = typeof(int), DbType = DbType.Int32 },
            25 => new ColumnInfo { Name = name, DataTypeName = "text", DataType = typeof(string), DbType = DbType.String },
            700 => new ColumnInfo { Name = name, DataTypeName = "float4", DataType = typeof(float), DbType = DbType.Single },
            701 => new ColumnInfo { Name = name, DataTypeName = "float8", DataType = typeof(double), DbType = DbType.Double },
            1043 => new ColumnInfo { Name = name, DataTypeName = "varchar", DataType = typeof(string), DbType = DbType.String, Size = typeModifier > 4 ? typeModifier - 4 : -1 },
            1082 => new ColumnInfo { Name = name, DataTypeName = "date", DataType = typeof(DateTime), DbType = DbType.Date },
            1083 => new ColumnInfo { Name = name, DataTypeName = "time", DataType = typeof(TimeSpan), DbType = DbType.Time },
            1114 => new ColumnInfo { Name = name, DataTypeName = "timestamp", DataType = typeof(DateTime), DbType = DbType.DateTime },
            1184 => new ColumnInfo { Name = name, DataTypeName = "timestamptz", DataType = typeof(DateTimeOffset), DbType = DbType.DateTimeOffset },
            1700 => new ColumnInfo { Name = name, DataTypeName = "numeric", DataType = typeof(decimal), DbType = DbType.Decimal, Precision = (typeModifier - 4) >> 16, Scale = (typeModifier - 4) & 0xFFFF },
            2950 => new ColumnInfo { Name = name, DataTypeName = "uuid", DataType = typeof(Guid), DbType = DbType.Guid },
            _ => new ColumnInfo { Name = name, DataTypeName = "unknown", DataType = typeof(object), DbType = DbType.Object }
        };
    }
}
