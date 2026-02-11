using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace DataWarehouse.Plugins.AdoNetProvider;

/// <summary>
/// Represents a parameter to a DataWarehouseCommand.
/// Supports all standard ADO.NET parameter features including named parameters,
/// input/output direction, and type inference.
/// </summary>
/// <remarks>
/// This class is thread-safe for reading but not for writing.
/// Callers should synchronize writes when modifying parameters.
/// </remarks>
public sealed class DataWarehouseParameter : DbParameter, ICloneable
{
    private string _parameterName = string.Empty;
    private DbType _dbType = DbType.Object;
    private ParameterDirection _direction = ParameterDirection.Input;
    private bool _isNullable = true;
    private string _sourceColumn = string.Empty;
    private DataRowVersion _sourceVersion = DataRowVersion.Current;
    private object? _value;
    private int _size;
    private byte _precision;
    private byte _scale;
    private bool _dbTypeExplicit;

    /// <summary>
    /// Initializes a new instance of the DataWarehouseParameter class.
    /// </summary>
    public DataWarehouseParameter()
    {
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseParameter class with a name and value.
    /// </summary>
    /// <param name="parameterName">The name of the parameter.</param>
    /// <param name="value">The value of the parameter.</param>
    public DataWarehouseParameter(string parameterName, object? value)
    {
        ParameterName = parameterName;
        Value = value;
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseParameter class with a name and type.
    /// </summary>
    /// <param name="parameterName">The name of the parameter.</param>
    /// <param name="dbType">The type of the parameter.</param>
    public DataWarehouseParameter(string parameterName, DbType dbType)
    {
        ParameterName = parameterName;
        DbType = dbType;
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseParameter class with a name, type, and size.
    /// </summary>
    /// <param name="parameterName">The name of the parameter.</param>
    /// <param name="dbType">The type of the parameter.</param>
    /// <param name="size">The maximum size of the data.</param>
    public DataWarehouseParameter(string parameterName, DbType dbType, int size)
    {
        ParameterName = parameterName;
        DbType = dbType;
        Size = size;
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseParameter class with full details.
    /// </summary>
    /// <param name="parameterName">The name of the parameter.</param>
    /// <param name="dbType">The type of the parameter.</param>
    /// <param name="size">The maximum size of the data.</param>
    /// <param name="sourceColumn">The source column name.</param>
    public DataWarehouseParameter(string parameterName, DbType dbType, int size, string sourceColumn)
    {
        ParameterName = parameterName;
        DbType = dbType;
        Size = size;
        SourceColumn = sourceColumn;
    }

    /// <inheritdoc/>
    public override DbType DbType
    {
        get => _dbType;
        set
        {
            _dbType = value;
            _dbTypeExplicit = true;
        }
    }

    /// <inheritdoc/>
    public override ParameterDirection Direction
    {
        get => _direction;
        set
        {
            if (value != ParameterDirection.Input &&
                value != ParameterDirection.Output &&
                value != ParameterDirection.InputOutput &&
                value != ParameterDirection.ReturnValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Invalid parameter direction.");
            }
            _direction = value;
        }
    }

    /// <inheritdoc/>
    public override bool IsNullable
    {
        get => _isNullable;
        set => _isNullable = value;
    }

    /// <inheritdoc/>
    [AllowNull]
    public override string ParameterName
    {
        get => _parameterName;
        set => _parameterName = NormalizeParameterName(value);
    }

    /// <inheritdoc/>
    public override int Size
    {
        get => _size;
        set
        {
            if (value < -1)
                throw new ArgumentOutOfRangeException(nameof(value), "Size must be -1 or greater.");
            _size = value;
        }
    }

    /// <inheritdoc/>
    [AllowNull]
    public override string SourceColumn
    {
        get => _sourceColumn;
        set => _sourceColumn = value ?? string.Empty;
    }

    /// <inheritdoc/>
    public override bool SourceColumnNullMapping { get; set; }

    /// <inheritdoc/>
    public override DataRowVersion SourceVersion
    {
        get => _sourceVersion;
        set => _sourceVersion = value;
    }

    /// <inheritdoc/>
    public override object? Value
    {
        get => _value;
        set
        {
            _value = value;
            if (!_dbTypeExplicit)
            {
                _dbType = InferDbType(value);
            }
        }
    }

    /// <summary>
    /// Gets or sets the precision for numeric parameters.
    /// </summary>
    public override byte Precision
    {
        get => _precision;
        set => _precision = value;
    }

    /// <summary>
    /// Gets or sets the scale for numeric parameters.
    /// </summary>
    public override byte Scale
    {
        get => _scale;
        set => _scale = value;
    }

    /// <inheritdoc/>
    public override void ResetDbType()
    {
        _dbTypeExplicit = false;
        _dbType = InferDbType(_value);
    }

    /// <summary>
    /// Creates a shallow copy of this parameter.
    /// </summary>
    /// <returns>A new DataWarehouseParameter with the same values.</returns>
    public object Clone()
    {
        return new DataWarehouseParameter
        {
            _parameterName = _parameterName,
            _dbType = _dbType,
            _dbTypeExplicit = _dbTypeExplicit,
            _direction = _direction,
            _isNullable = _isNullable,
            _sourceColumn = _sourceColumn,
            _sourceVersion = _sourceVersion,
            _value = _value,
            _size = _size,
            _precision = _precision,
            _scale = _scale,
            SourceColumnNullMapping = SourceColumnNullMapping
        };
    }

    /// <summary>
    /// Returns a string representation of this parameter.
    /// </summary>
    /// <returns>The parameter name.</returns>
    public override string ToString()
    {
        return _parameterName;
    }

    /// <summary>
    /// Gets the formatted value for use in SQL commands.
    /// </summary>
    /// <returns>The formatted value string.</returns>
    internal string GetFormattedValue()
    {
        if (_value == null || _value == DBNull.Value)
            return "NULL";

        return _dbType switch
        {
            DbType.String or DbType.StringFixedLength or DbType.AnsiString or DbType.AnsiStringFixedLength =>
                $"'{EscapeString(_value.ToString() ?? string.Empty)}'",
            DbType.DateTime or DbType.DateTime2 or DbType.DateTimeOffset =>
                FormatDateTime(_value),
            DbType.Date =>
                FormatDate(_value),
            DbType.Time =>
                FormatTime(_value),
            DbType.Boolean =>
                ((bool)_value) ? "TRUE" : "FALSE",
            DbType.Guid =>
                $"'{_value}'",
            DbType.Binary =>
                FormatBinary(_value),
            DbType.Byte or DbType.SByte or DbType.Int16 or DbType.Int32 or DbType.Int64 or
            DbType.UInt16 or DbType.UInt32 or DbType.UInt64 =>
                _value.ToString() ?? "0",
            DbType.Single or DbType.Double or DbType.Decimal or DbType.Currency =>
                FormatNumeric(_value),
            _ => _value.ToString() ?? "NULL"
        };
    }

    private static string NormalizeParameterName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        // Remove leading @ or : if present
        if (name.StartsWith('@') || name.StartsWith(':'))
            return name[1..];

        return name;
    }

    private static DbType InferDbType(object? value)
    {
        if (value == null || value == DBNull.Value)
            return DbType.Object;

        return value switch
        {
            string => DbType.String,
            int => DbType.Int32,
            long => DbType.Int64,
            short => DbType.Int16,
            byte => DbType.Byte,
            sbyte => DbType.SByte,
            uint => DbType.UInt32,
            ulong => DbType.UInt64,
            ushort => DbType.UInt16,
            float => DbType.Single,
            double => DbType.Double,
            decimal => DbType.Decimal,
            bool => DbType.Boolean,
            DateTime => DbType.DateTime,
            DateTimeOffset => DbType.DateTimeOffset,
            DateOnly => DbType.Date,
            TimeOnly => DbType.Time,
            TimeSpan => DbType.Time,
            Guid => DbType.Guid,
            byte[] => DbType.Binary,
            char => DbType.StringFixedLength,
            char[] => DbType.String,
            _ => DbType.Object
        };
    }

    private static string EscapeString(string value)
    {
        return value.Replace("'", "''");
    }

    private static string FormatDateTime(object value)
    {
        var dt = value switch
        {
            DateTime d => d,
            DateTimeOffset dto => dto.UtcDateTime,
            _ => DateTime.MinValue
        };
        return $"'{dt:O}'";
    }

    private static string FormatDate(object value)
    {
        var date = value switch
        {
            DateTime d => DateOnly.FromDateTime(d),
            DateOnly d => d,
            _ => DateOnly.MinValue
        };
        return $"'{date:yyyy-MM-dd}'";
    }

    private static string FormatTime(object value)
    {
        var time = value switch
        {
            TimeOnly t => t,
            TimeSpan ts => TimeOnly.FromTimeSpan(ts),
            DateTime dt => TimeOnly.FromDateTime(dt),
            _ => TimeOnly.MinValue
        };
        return $"'{time:HH:mm:ss.ffffff}'";
    }

    private static string FormatNumeric(object value)
    {
        return Convert.ToDecimal(value).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatBinary(object value)
    {
        if (value is byte[] bytes)
        {
            return $"'\\x{Convert.ToHexString(bytes)}'";
        }
        return "NULL";
    }
}

/// <summary>
/// Represents a collection of DataWarehouseParameter objects.
/// Thread-safe for iteration; modifications should be synchronized by the caller.
/// </summary>
public sealed class DataWarehouseParameterCollection : DbParameterCollection
{
    private readonly List<DataWarehouseParameter> _parameters = new();
    private readonly object _syncRoot = new();

    /// <inheritdoc/>
    public override int Count => _parameters.Count;

    /// <inheritdoc/>
    public override object SyncRoot => _syncRoot;

    /// <summary>
    /// Gets or sets the parameter at the specified index.
    /// </summary>
    /// <param name="index">The zero-based index.</param>
    /// <returns>The parameter at the specified index.</returns>
    public new DataWarehouseParameter this[int index]
    {
        get => _parameters[index];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _parameters[index] = value;
        }
    }

    /// <summary>
    /// Gets or sets the parameter with the specified name.
    /// </summary>
    /// <param name="parameterName">The name of the parameter.</param>
    /// <returns>The parameter with the specified name.</returns>
    public new DataWarehouseParameter this[string parameterName]
    {
        get
        {
            var index = IndexOf(parameterName);
            if (index < 0)
                throw new ArgumentException($"Parameter '{parameterName}' not found.", nameof(parameterName));
            return _parameters[index];
        }
        set
        {
            var index = IndexOf(parameterName);
            if (index < 0)
                throw new ArgumentException($"Parameter '{parameterName}' not found.", nameof(parameterName));
            _parameters[index] = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    /// <inheritdoc/>
    public override int Add(object value)
    {
        var param = ValidateParameter(value);
        _parameters.Add(param);
        return _parameters.Count - 1;
    }

    /// <summary>
    /// Adds a parameter with the specified name and value.
    /// </summary>
    /// <param name="parameterName">The name of the parameter.</param>
    /// <param name="value">The value of the parameter.</param>
    /// <returns>The added parameter.</returns>
    public DataWarehouseParameter Add(string parameterName, object? value)
    {
        var param = new DataWarehouseParameter(parameterName, value);
        _parameters.Add(param);
        return param;
    }

    /// <summary>
    /// Adds a parameter with the specified name and type.
    /// </summary>
    /// <param name="parameterName">The name of the parameter.</param>
    /// <param name="dbType">The type of the parameter.</param>
    /// <returns>The added parameter.</returns>
    public DataWarehouseParameter Add(string parameterName, DbType dbType)
    {
        var param = new DataWarehouseParameter(parameterName, dbType);
        _parameters.Add(param);
        return param;
    }

    /// <summary>
    /// Adds a parameter with the specified name, type, and size.
    /// </summary>
    /// <param name="parameterName">The name of the parameter.</param>
    /// <param name="dbType">The type of the parameter.</param>
    /// <param name="size">The size of the parameter.</param>
    /// <returns>The added parameter.</returns>
    public DataWarehouseParameter Add(string parameterName, DbType dbType, int size)
    {
        var param = new DataWarehouseParameter(parameterName, dbType, size);
        _parameters.Add(param);
        return param;
    }

    /// <summary>
    /// Adds a parameter and returns this collection for fluent chaining.
    /// </summary>
    /// <param name="parameter">The parameter to add.</param>
    /// <returns>This collection.</returns>
    public DataWarehouseParameterCollection AddParameter(DataWarehouseParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        _parameters.Add(parameter);
        return this;
    }

    /// <inheritdoc/>
    public override void AddRange(Array values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var value in values)
        {
            Add(value);
        }
    }

    /// <inheritdoc/>
    public override void Clear()
    {
        _parameters.Clear();
    }

    /// <inheritdoc/>
    public override bool Contains(object value)
    {
        return _parameters.Contains(ValidateParameter(value));
    }

    /// <inheritdoc/>
    public override bool Contains(string value)
    {
        return IndexOf(value) >= 0;
    }

    /// <inheritdoc/>
    public override void CopyTo(Array array, int index)
    {
        ((System.Collections.ICollection)_parameters).CopyTo(array, index);
    }

    /// <inheritdoc/>
    public override System.Collections.IEnumerator GetEnumerator()
    {
        return _parameters.GetEnumerator();
    }

    /// <inheritdoc/>
    public override int IndexOf(object value)
    {
        return _parameters.IndexOf(ValidateParameter(value));
    }

    /// <inheritdoc/>
    public override int IndexOf(string parameterName)
    {
        var normalizedName = NormalizeParameterName(parameterName);
        for (int i = 0; i < _parameters.Count; i++)
        {
            if (string.Equals(_parameters[i].ParameterName, normalizedName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <inheritdoc/>
    public override void Insert(int index, object value)
    {
        _parameters.Insert(index, ValidateParameter(value));
    }

    /// <inheritdoc/>
    public override void Remove(object value)
    {
        _parameters.Remove(ValidateParameter(value));
    }

    /// <inheritdoc/>
    public override void RemoveAt(int index)
    {
        _parameters.RemoveAt(index);
    }

    /// <inheritdoc/>
    public override void RemoveAt(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index >= 0)
            RemoveAt(index);
    }

    /// <inheritdoc/>
    protected override DbParameter GetParameter(int index)
    {
        return _parameters[index];
    }

    /// <inheritdoc/>
    protected override DbParameter GetParameter(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index < 0)
            throw new ArgumentException($"Parameter '{parameterName}' not found.", nameof(parameterName));
        return _parameters[index];
    }

    /// <inheritdoc/>
    protected override void SetParameter(int index, DbParameter value)
    {
        _parameters[index] = ValidateParameter(value);
    }

    /// <inheritdoc/>
    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);
        if (index < 0)
            throw new ArgumentException($"Parameter '{parameterName}' not found.", nameof(parameterName));
        _parameters[index] = ValidateParameter(value);
    }

    private static DataWarehouseParameter ValidateParameter(object? value)
    {
        if (value is DataWarehouseParameter param)
            return param;

        if (value is DbParameter dbParam)
        {
            // Convert from another DbParameter type
            return new DataWarehouseParameter
            {
                ParameterName = dbParam.ParameterName,
                DbType = dbParam.DbType,
                Direction = dbParam.Direction,
                IsNullable = dbParam.IsNullable,
                SourceColumn = dbParam.SourceColumn,
                SourceVersion = dbParam.SourceVersion,
                Value = dbParam.Value,
                Size = dbParam.Size,
                SourceColumnNullMapping = dbParam.SourceColumnNullMapping
            };
        }

        throw new ArgumentException("Parameter must be a DataWarehouseParameter.", nameof(value));
    }

    private static string NormalizeParameterName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        if (name.StartsWith('@') || name.StartsWith(':'))
            return name[1..];

        return name;
    }
}
