// <copyright file="OdbcDescriptorHandle.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.OdbcDriver.Handles;

/// <summary>
/// Represents an ODBC descriptor handle (HDESC).
/// The descriptor handle contains metadata about parameters and result columns.
/// ODBC uses four types of descriptors: APD, IPD, ARD, IRD.
/// </summary>
public sealed class OdbcDescriptorHandle : IDisposable
{
    private readonly ConcurrentQueue<OdbcDiagnosticRecord> _diagnostics = new();
    private readonly Dictionary<int, DescriptorRecord> _records = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="OdbcDescriptorHandle"/> class.
    /// </summary>
    /// <param name="handle">The native handle value.</param>
    /// <param name="connection">The parent connection handle.</param>
    public OdbcDescriptorHandle(nint handle, OdbcConnectionHandle connection)
    {
        Handle = handle;
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        CreatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets the native handle value.
    /// </summary>
    public nint Handle { get; }

    /// <summary>
    /// Gets the parent connection handle.
    /// </summary>
    public OdbcConnectionHandle Connection { get; }

    /// <summary>
    /// Gets the timestamp when this handle was created.
    /// </summary>
    public DateTime CreatedAt { get; }

    /// <summary>
    /// Gets or sets the type of this descriptor.
    /// </summary>
    public DescriptorType Type { get; set; } = DescriptorType.Application;

    /// <summary>
    /// Gets or sets the number of records in this descriptor.
    /// </summary>
    public int RecordCount => _records.Count;

    /// <summary>
    /// Gets or sets the array size (number of rows/parameter sets).
    /// </summary>
    public int ArraySize { get; set; } = 1;

    /// <summary>
    /// Gets or sets the bind type (column-wise or row-wise).
    /// </summary>
    public int BindType { get; set; }

    /// <summary>
    /// Gets or sets the bind offset pointer.
    /// </summary>
    public nint BindOffsetPtr { get; set; }

    /// <summary>
    /// Gets or sets the rows processed pointer.
    /// </summary>
    public nint RowsProcessedPtr { get; set; }

    /// <summary>
    /// Gets or sets the array status pointer.
    /// </summary>
    public nint ArrayStatusPtr { get; set; }

    /// <summary>
    /// Gets a descriptor record by index.
    /// </summary>
    /// <param name="recordNumber">The 1-based record number.</param>
    /// <returns>The descriptor record, or null if not found.</returns>
    public DescriptorRecord? GetRecord(int recordNumber)
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            return _records.TryGetValue(recordNumber, out var record) ? record : null;
        }
    }

    /// <summary>
    /// Sets a descriptor record.
    /// </summary>
    /// <param name="recordNumber">The 1-based record number.</param>
    /// <param name="record">The descriptor record.</param>
    /// <returns>SQL_SUCCESS on success.</returns>
    public SqlReturn SetRecord(int recordNumber, DescriptorRecord record)
    {
        ThrowIfDisposed();

        if (recordNumber < 1)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.InvalidDescriptorFieldId,
                Message = $"Invalid record number: {recordNumber}",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        lock (_lock)
        {
            _records[recordNumber] = record;
        }

        return SqlReturn.Success;
    }

    /// <summary>
    /// Sets a field in a descriptor record.
    /// </summary>
    /// <param name="recordNumber">The 1-based record number (0 for header fields).</param>
    /// <param name="fieldId">The field identifier.</param>
    /// <param name="value">The field value.</param>
    /// <returns>SQL_SUCCESS on success.</returns>
    public SqlReturn SetField(int recordNumber, SqlDescField fieldId, object? value)
    {
        ThrowIfDisposed();

        try
        {
            // Header fields (record 0)
            if (recordNumber == 0)
            {
                switch (fieldId)
                {
                    case SqlDescField.Count:
                        // Count is read-only
                        return SqlReturn.Success;

                    default:
                        AddDiagnostic(new OdbcDiagnosticRecord
                        {
                            SqlState = SqlState.InvalidDescriptorFieldId,
                            Message = $"Unknown header field: {fieldId}",
                            NativeError = 0
                        });
                        return SqlReturn.Error;
                }
            }

            // Get or create record
            DescriptorRecord record;
            lock (_lock)
            {
                if (!_records.TryGetValue(recordNumber, out record!))
                {
                    record = new DescriptorRecord { RecordNumber = recordNumber };
                    _records[recordNumber] = record;
                }
            }

            // Set field value
            switch (fieldId)
            {
                case SqlDescField.Name:
                    record.Name = value?.ToString();
                    break;

                case SqlDescField.Type:
                case SqlDescField.ConciseType:
                    record.Type = value is SqlDataType dt ? dt : (SqlDataType)(int)(value ?? 0);
                    break;

                case SqlDescField.Length:
                    record.Length = Convert.ToInt32(value ?? 0);
                    break;

                case SqlDescField.Precision:
                    record.Precision = Convert.ToInt32(value ?? 0);
                    break;

                case SqlDescField.Scale:
                    record.Scale = Convert.ToInt16(value ?? 0);
                    break;

                case SqlDescField.Nullable:
                    record.Nullable = value is SqlNullable n ? n : (SqlNullable)(int)(value ?? 0);
                    break;

                case SqlDescField.OctetLength:
                    record.OctetLength = Convert.ToInt32(value ?? 0);
                    break;

                case SqlDescField.DisplaySize:
                    record.DisplaySize = Convert.ToInt32(value ?? 0);
                    break;

                case SqlDescField.TypeName:
                    record.TypeName = value?.ToString();
                    break;

                case SqlDescField.TableName:
                    record.TableName = value?.ToString();
                    break;

                case SqlDescField.SchemaName:
                    record.SchemaName = value?.ToString();
                    break;

                case SqlDescField.CatalogName:
                    record.CatalogName = value?.ToString();
                    break;

                case SqlDescField.BaseColumnName:
                    record.BaseColumnName = value?.ToString();
                    break;

                case SqlDescField.BaseTableName:
                    record.BaseTableName = value?.ToString();
                    break;

                case SqlDescField.AutoUniqueValue:
                    record.AutoUniqueValue = Convert.ToBoolean(value);
                    break;

                case SqlDescField.CaseSensitive:
                    record.CaseSensitive = Convert.ToBoolean(value);
                    break;

                case SqlDescField.Unsigned:
                    record.Unsigned = Convert.ToBoolean(value);
                    break;

                case SqlDescField.Updatable:
                    record.Updatable = Convert.ToBoolean(value);
                    break;

                case SqlDescField.Searchable:
                    record.Searchable = value is SqlSearchable s ? s : (SqlSearchable)(int)(value ?? 0);
                    break;

                default:
                    AddDiagnostic(new OdbcDiagnosticRecord
                    {
                        SqlState = SqlState.GeneralWarning,
                        Message = $"Field {fieldId} not fully supported.",
                        NativeError = 0
                    });
                    return SqlReturn.SuccessWithInfo;
            }

            return SqlReturn.Success;
        }
        catch (Exception ex)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.GeneralError,
                Message = $"Error setting descriptor field: {ex.Message}",
                NativeError = 0
            });
            return SqlReturn.Error;
        }
    }

    /// <summary>
    /// Gets a field from a descriptor record.
    /// </summary>
    /// <param name="recordNumber">The 1-based record number (0 for header fields).</param>
    /// <param name="fieldId">The field identifier.</param>
    /// <param name="value">The field value.</param>
    /// <returns>SQL_SUCCESS on success.</returns>
    public SqlReturn GetField(int recordNumber, SqlDescField fieldId, out object? value)
    {
        ThrowIfDisposed();
        value = null;

        try
        {
            // Header fields (record 0)
            if (recordNumber == 0)
            {
                switch (fieldId)
                {
                    case SqlDescField.Count:
                        value = RecordCount;
                        return SqlReturn.Success;

                    default:
                        AddDiagnostic(new OdbcDiagnosticRecord
                        {
                            SqlState = SqlState.InvalidDescriptorFieldId,
                            Message = $"Unknown header field: {fieldId}",
                            NativeError = 0
                        });
                        return SqlReturn.Error;
                }
            }

            // Get record
            DescriptorRecord? record;
            lock (_lock)
            {
                if (!_records.TryGetValue(recordNumber, out record))
                {
                    AddDiagnostic(new OdbcDiagnosticRecord
                    {
                        SqlState = SqlState.InvalidDescriptorFieldId,
                        Message = $"Record {recordNumber} not found.",
                        NativeError = 0
                    });
                    return SqlReturn.Error;
                }
            }

            // Get field value
            value = fieldId switch
            {
                SqlDescField.Name => record.Name,
                SqlDescField.Type or SqlDescField.ConciseType => record.Type,
                SqlDescField.Length => record.Length,
                SqlDescField.Precision => record.Precision,
                SqlDescField.Scale => record.Scale,
                SqlDescField.Nullable => record.Nullable,
                SqlDescField.OctetLength => record.OctetLength,
                SqlDescField.DisplaySize => record.DisplaySize,
                SqlDescField.TypeName => record.TypeName,
                SqlDescField.TableName => record.TableName,
                SqlDescField.SchemaName => record.SchemaName,
                SqlDescField.CatalogName => record.CatalogName,
                SqlDescField.BaseColumnName => record.BaseColumnName,
                SqlDescField.BaseTableName => record.BaseTableName,
                SqlDescField.AutoUniqueValue => record.AutoUniqueValue,
                SqlDescField.CaseSensitive => record.CaseSensitive,
                SqlDescField.Unsigned => record.Unsigned,
                SqlDescField.Updatable => record.Updatable,
                SqlDescField.Searchable => record.Searchable,
                _ => null
            };

            return SqlReturn.Success;
        }
        catch (Exception ex)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.GeneralError,
                Message = $"Error getting descriptor field: {ex.Message}",
                NativeError = 0
            });
            return SqlReturn.Error;
        }
    }

    /// <summary>
    /// Clears all records from this descriptor.
    /// </summary>
    /// <returns>SQL_SUCCESS on success.</returns>
    public SqlReturn Clear()
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            _records.Clear();
        }

        return SqlReturn.Success;
    }

    /// <summary>
    /// Copies records from another descriptor.
    /// </summary>
    /// <param name="source">The source descriptor.</param>
    /// <returns>SQL_SUCCESS on success.</returns>
    public SqlReturn CopyFrom(OdbcDescriptorHandle source)
    {
        ThrowIfDisposed();

        if (source == null)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.InvalidNullPointer,
                Message = "Source descriptor is null.",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        lock (_lock)
        {
            _records.Clear();
            foreach (var kvp in source._records)
            {
                _records[kvp.Key] = kvp.Value.Clone();
            }
        }

        ArraySize = source.ArraySize;
        BindType = source.BindType;

        return SqlReturn.Success;
    }

    /// <summary>
    /// Adds a diagnostic record to this handle.
    /// </summary>
    /// <param name="record">The diagnostic record to add.</param>
    public void AddDiagnostic(OdbcDiagnosticRecord record)
    {
        _diagnostics.Enqueue(record);
        while (_diagnostics.Count > 100)
        {
            _diagnostics.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Gets all diagnostic records.
    /// </summary>
    /// <returns>The diagnostic records.</returns>
    public IEnumerable<OdbcDiagnosticRecord> GetDiagnostics()
    {
        return _diagnostics.ToArray();
    }

    /// <summary>
    /// Clears all diagnostic records.
    /// </summary>
    public void ClearDiagnostics()
    {
        while (_diagnostics.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Gets a diagnostic record by index (1-based).
    /// </summary>
    /// <param name="recordNumber">The 1-based record number.</param>
    /// <returns>The diagnostic record, or null if not found.</returns>
    public OdbcDiagnosticRecord? GetDiagnostic(int recordNumber)
    {
        var records = _diagnostics.ToArray();
        if (recordNumber < 1 || recordNumber > records.Length)
        {
            return null;
        }
        return records[recordNumber - 1];
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(OdbcDescriptorHandle));
    }

    /// <summary>
    /// Disposes this descriptor handle and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _records.Clear();
        while (_diagnostics.TryDequeue(out _)) { }
    }
}

/// <summary>
/// Types of ODBC descriptors.
/// </summary>
public enum DescriptorType
{
    /// <summary>
    /// Application Parameter Descriptor (APD).
    /// </summary>
    ApplicationParameter,

    /// <summary>
    /// Implementation Parameter Descriptor (IPD).
    /// </summary>
    ImplementationParameter,

    /// <summary>
    /// Application Row Descriptor (ARD).
    /// </summary>
    ApplicationRow,

    /// <summary>
    /// Implementation Row Descriptor (IRD).
    /// </summary>
    ImplementationRow,

    /// <summary>
    /// Explicitly allocated application descriptor.
    /// </summary>
    Application
}

/// <summary>
/// A single record in a descriptor.
/// </summary>
public sealed class DescriptorRecord
{
    /// <summary>
    /// Gets or sets the record number.
    /// </summary>
    public int RecordNumber { get; set; }

    /// <summary>
    /// Gets or sets the column/parameter name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the SQL data type.
    /// </summary>
    public SqlDataType Type { get; set; }

    /// <summary>
    /// Gets or sets the length.
    /// </summary>
    public int Length { get; set; }

    /// <summary>
    /// Gets or sets the precision.
    /// </summary>
    public int Precision { get; set; }

    /// <summary>
    /// Gets or sets the scale.
    /// </summary>
    public short Scale { get; set; }

    /// <summary>
    /// Gets or sets the nullable flag.
    /// </summary>
    public SqlNullable Nullable { get; set; } = SqlNullable.Unknown;

    /// <summary>
    /// Gets or sets the octet length.
    /// </summary>
    public int OctetLength { get; set; }

    /// <summary>
    /// Gets or sets the display size.
    /// </summary>
    public int DisplaySize { get; set; }

    /// <summary>
    /// Gets or sets the type name.
    /// </summary>
    public string? TypeName { get; set; }

    /// <summary>
    /// Gets or sets the table name.
    /// </summary>
    public string? TableName { get; set; }

    /// <summary>
    /// Gets or sets the schema name.
    /// </summary>
    public string? SchemaName { get; set; }

    /// <summary>
    /// Gets or sets the catalog name.
    /// </summary>
    public string? CatalogName { get; set; }

    /// <summary>
    /// Gets or sets the base column name.
    /// </summary>
    public string? BaseColumnName { get; set; }

    /// <summary>
    /// Gets or sets the base table name.
    /// </summary>
    public string? BaseTableName { get; set; }

    /// <summary>
    /// Gets or sets whether the column is auto-increment.
    /// </summary>
    public bool AutoUniqueValue { get; set; }

    /// <summary>
    /// Gets or sets whether the column is case-sensitive.
    /// </summary>
    public bool CaseSensitive { get; set; }

    /// <summary>
    /// Gets or sets whether the column is unsigned.
    /// </summary>
    public bool Unsigned { get; set; }

    /// <summary>
    /// Gets or sets whether the column is updatable.
    /// </summary>
    public bool Updatable { get; set; }

    /// <summary>
    /// Gets or sets the searchable attribute.
    /// </summary>
    public SqlSearchable Searchable { get; set; } = SqlSearchable.Searchable;

    /// <summary>
    /// Gets or sets the data pointer.
    /// </summary>
    public nint DataPtr { get; set; }

    /// <summary>
    /// Gets or sets the indicator pointer.
    /// </summary>
    public nint IndicatorPtr { get; set; }

    /// <summary>
    /// Creates a deep copy of this record.
    /// </summary>
    /// <returns>A new record with copied values.</returns>
    public DescriptorRecord Clone()
    {
        return new DescriptorRecord
        {
            RecordNumber = RecordNumber,
            Name = Name,
            Type = Type,
            Length = Length,
            Precision = Precision,
            Scale = Scale,
            Nullable = Nullable,
            OctetLength = OctetLength,
            DisplaySize = DisplaySize,
            TypeName = TypeName,
            TableName = TableName,
            SchemaName = SchemaName,
            CatalogName = CatalogName,
            BaseColumnName = BaseColumnName,
            BaseTableName = BaseTableName,
            AutoUniqueValue = AutoUniqueValue,
            CaseSensitive = CaseSensitive,
            Unsigned = Unsigned,
            Updatable = Updatable,
            Searchable = Searchable,
            DataPtr = DataPtr,
            IndicatorPtr = IndicatorPtr
        };
    }
}
