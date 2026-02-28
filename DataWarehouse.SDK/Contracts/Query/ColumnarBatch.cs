using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace DataWarehouse.SDK.Contracts.Query;

// ─────────────────────────────────────────────────────────────
// Column Data Type
// ─────────────────────────────────────────────────────────────

/// <summary>Supported column data types for columnar storage.</summary>
public enum ColumnDataType
{
    Int32,
    Int64,
    Float64,
    String,
    Bool,
    Binary,
    Decimal,
    DateTime,
    Null
}

// ─────────────────────────────────────────────────────────────
// ColumnVector (abstract base)
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Abstract base for a single column of data in a columnar batch.
/// Uses a null bitmap (one bit per row) for efficient null tracking.
/// </summary>
public abstract class ColumnVector
{
    /// <summary>Column name.</summary>
    public string Name { get; }

    /// <summary>Logical data type of this column.</summary>
    public abstract ColumnDataType DataType { get; }

    /// <summary>Number of values in this column.</summary>
    public int Length { get; protected set; }

    /// <summary>
    /// Null bitmap: one bit per row. Bit = 1 means the value IS null.
    /// Byte index = rowIndex / 8, bit index = rowIndex % 8.
    /// </summary>
    public byte[] NullBitmap { get; protected set; }

    protected ColumnVector(string name, int length)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Length = length;
        NullBitmap = new byte[(length + 7) / 8];
    }

    /// <summary>Check whether the value at <paramref name="index"/> is null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsNull(int index)
    {
        return (NullBitmap[index >> 3] & (1 << (index & 7))) != 0;
    }

    /// <summary>Mark the value at <paramref name="index"/> as null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetNull(int index)
    {
        NullBitmap[index >> 3] |= (byte)(1 << (index & 7));
    }

    /// <summary>Clear the null flag at <paramref name="index"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearNull(int index)
    {
        NullBitmap[index >> 3] &= (byte)~(1 << (index & 7));
    }

    /// <summary>Get the boxed value at the given index (for generic access).</summary>
    public abstract object? GetValue(int index);
}

// ─────────────────────────────────────────────────────────────
// TypedColumnVector<T>
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Strongly-typed column vector backed by a T[] array.
/// Enables vectorized (tight-loop) processing without per-row boxing.
/// </summary>
public class TypedColumnVector<T> : ColumnVector
{
    /// <summary>Raw typed values array. Access directly for vectorized operations.</summary>
    public T[] Values { get; }

    public override ColumnDataType DataType { get; }

    public TypedColumnVector(string name, T[] values, ColumnDataType dataType)
        : base(name, values.Length)
    {
        Values = values;
        DataType = dataType;
    }

    public TypedColumnVector(string name, T[] values, ColumnDataType dataType, byte[] nullBitmap)
        : base(name, values.Length)
    {
        Values = values;
        DataType = dataType;
        if (nullBitmap.Length >= NullBitmap.Length)
            Array.Copy(nullBitmap, NullBitmap, NullBitmap.Length);
    }

    public override object? GetValue(int index) => IsNull(index) ? null : Values[index];
}

// ─────────────────────────────────────────────────────────────
// Concrete Column Vectors
// ─────────────────────────────────────────────────────────────

/// <summary>32-bit integer column.</summary>
public sealed class Int32ColumnVector : TypedColumnVector<int>
{
    public Int32ColumnVector(string name, int[] values) : base(name, values, ColumnDataType.Int32) { }
    public Int32ColumnVector(string name, int[] values, byte[] nullBitmap) : base(name, values, ColumnDataType.Int32, nullBitmap) { }
}

/// <summary>64-bit integer column.</summary>
public sealed class Int64ColumnVector : TypedColumnVector<long>
{
    public Int64ColumnVector(string name, long[] values) : base(name, values, ColumnDataType.Int64) { }
    public Int64ColumnVector(string name, long[] values, byte[] nullBitmap) : base(name, values, ColumnDataType.Int64, nullBitmap) { }
}

/// <summary>64-bit floating-point column.</summary>
public sealed class Float64ColumnVector : TypedColumnVector<double>
{
    public Float64ColumnVector(string name, double[] values) : base(name, values, ColumnDataType.Float64) { }
    public Float64ColumnVector(string name, double[] values, byte[] nullBitmap) : base(name, values, ColumnDataType.Float64, nullBitmap) { }
}

/// <summary>String column (UTF-16 .NET strings).</summary>
public sealed class StringColumnVector : TypedColumnVector<string>
{
    public StringColumnVector(string name, string[] values) : base(name, values, ColumnDataType.String) { }
    public StringColumnVector(string name, string[] values, byte[] nullBitmap) : base(name, values, ColumnDataType.String, nullBitmap) { }
}

/// <summary>Boolean column.</summary>
public sealed class BoolColumnVector : TypedColumnVector<bool>
{
    public BoolColumnVector(string name, bool[] values) : base(name, values, ColumnDataType.Bool) { }
    public BoolColumnVector(string name, bool[] values, byte[] nullBitmap) : base(name, values, ColumnDataType.Bool, nullBitmap) { }
}

/// <summary>Binary (byte array) column.</summary>
public sealed class BinaryColumnVector : TypedColumnVector<byte[]>
{
    public BinaryColumnVector(string name, byte[][] values) : base(name, values, ColumnDataType.Binary) { }
    public BinaryColumnVector(string name, byte[][] values, byte[] nullBitmap) : base(name, values, ColumnDataType.Binary, nullBitmap) { }
}

/// <summary>Decimal column (128-bit precision).</summary>
public sealed class DecimalColumnVector : TypedColumnVector<decimal>
{
    public DecimalColumnVector(string name, decimal[] values) : base(name, values, ColumnDataType.Decimal) { }
    public DecimalColumnVector(string name, decimal[] values, byte[] nullBitmap) : base(name, values, ColumnDataType.Decimal, nullBitmap) { }
}

/// <summary>DateTime column (tick precision).</summary>
public sealed class DateTimeColumnVector : TypedColumnVector<DateTime>
{
    public DateTimeColumnVector(string name, DateTime[] values) : base(name, values, ColumnDataType.DateTime) { }
    public DateTimeColumnVector(string name, DateTime[] values, byte[] nullBitmap) : base(name, values, ColumnDataType.DateTime, nullBitmap) { }
}

// ─────────────────────────────────────────────────────────────
// ColumnarBatch
// ─────────────────────────────────────────────────────────────

/// <summary>
/// An in-memory columnar batch: a set of equal-length column vectors.
/// Default batch size is 8192 rows, chosen for L1/L2 cache friendliness.
/// </summary>
public sealed class ColumnarBatch
{
    /// <summary>Default batch size optimized for L1/L2 cache locality.</summary>
    public const int DefaultBatchSize = 8192;

    /// <summary>Number of rows in this batch.</summary>
    public int RowCount { get; }

    /// <summary>Columns in this batch.</summary>
    public IReadOnlyList<ColumnVector> Columns { get; }

    private readonly Dictionary<string, int> _columnIndex;

    public ColumnarBatch(int rowCount, IReadOnlyList<ColumnVector> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (rowCount < 0) throw new ArgumentOutOfRangeException(nameof(rowCount), rowCount, "rowCount must be non-negative.");

        // Validate that every column vector length matches rowCount to prevent silent data corruption
        for (int i = 0; i < columns.Count; i++)
        {
            if (columns[i].Length != rowCount)
                throw new ArgumentException(
                    $"Column '{columns[i].Name}' (index {i}) has length {columns[i].Length} " +
                    $"but rowCount is {rowCount}. All column vectors must have the same length.",
                    nameof(columns));
        }

        RowCount = rowCount;
        Columns = columns;
        _columnIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < columns.Count; i++)
            _columnIndex[columns[i].Name] = i;
    }

    /// <summary>Get a column by name (case-insensitive).</summary>
    public ColumnVector? GetColumn(string name) =>
        _columnIndex.TryGetValue(name, out var idx) ? Columns[idx] : null;

    /// <summary>Get a typed column by name.</summary>
    public TypedColumnVector<T>? GetTypedColumn<T>(string name) =>
        GetColumn(name) as TypedColumnVector<T>;

    /// <summary>Number of columns.</summary>
    public int ColumnCount => Columns.Count;
}

// ─────────────────────────────────────────────────────────────
// ColumnarBatchBuilder
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Builds a ColumnarBatch by adding columns and setting values.
/// </summary>
public sealed class ColumnarBatchBuilder
{
    private readonly int _capacity;
    private readonly List<ColumnDef> _columns = new();
    private int _maxRowIndex = -1;
    private readonly object _syncRoot = new();

    private sealed class ColumnDef
    {
        public string Name { get; }
        public ColumnDataType DataType { get; }
        public Array Values { get; }
        public byte[] NullBitmap { get; }

        public ColumnDef(string name, ColumnDataType dataType, int capacity)
        {
            Name = name;
            DataType = dataType;
            Values = CreateArray(dataType, capacity);
            NullBitmap = new byte[(capacity + 7) / 8];
            // Mark all as null initially
            for (int i = 0; i < NullBitmap.Length; i++)
                NullBitmap[i] = 0xFF;
        }

        private static Array CreateArray(ColumnDataType dt, int capacity) => dt switch
        {
            ColumnDataType.Int32 => new int[capacity],
            ColumnDataType.Int64 => new long[capacity],
            ColumnDataType.Float64 => new double[capacity],
            ColumnDataType.String => new string[capacity],
            ColumnDataType.Bool => new bool[capacity],
            ColumnDataType.Binary => new byte[capacity][],
            ColumnDataType.Decimal => new decimal[capacity],
            ColumnDataType.DateTime => new DateTime[capacity],
            ColumnDataType.Null => new object[capacity],
            _ => throw new ArgumentOutOfRangeException(nameof(dt))
        };
    }

    /// <summary>Initializes a new builder with the specified row capacity.</summary>
    /// <param name="capacity">Maximum number of rows (default 8192).</param>
    public ColumnarBatchBuilder(int capacity = ColumnarBatch.DefaultBatchSize)
    {
        _capacity = capacity;
    }

    /// <summary>Add a column definition. Thread-safe.</summary>
    public ColumnarBatchBuilder AddColumn(string name, ColumnDataType dataType)
    {
        lock (_syncRoot)
        {
            _columns.Add(new ColumnDef(name, dataType, _capacity));
        }
        return this;
    }

    /// <summary>Set a value at (column index, row index). Thread-safe.</summary>
    public ColumnarBatchBuilder SetValue(int col, int row, object? value)
    {
        lock (_syncRoot)
        {
        if (col < 0 || col >= _columns.Count)
            throw new ArgumentOutOfRangeException(nameof(col));
        if (row < 0 || row >= _capacity)
            throw new ArgumentOutOfRangeException(nameof(row));

        var def = _columns[col];
        if (row > _maxRowIndex) _maxRowIndex = row;

        if (value is null)
        {
            // Already null by default; ensure bit is set
            def.NullBitmap[row >> 3] |= (byte)(1 << (row & 7));
            return this;
        }

        // Clear null bit
        def.NullBitmap[row >> 3] &= (byte)~(1 << (row & 7));

        switch (def.DataType)
        {
            case ColumnDataType.Int32: ((int[])def.Values)[row] = Convert.ToInt32(value); break;
            case ColumnDataType.Int64: ((long[])def.Values)[row] = Convert.ToInt64(value); break;
            case ColumnDataType.Float64: ((double[])def.Values)[row] = Convert.ToDouble(value); break;
            case ColumnDataType.String: ((string[])def.Values)[row] = (string)value; break;
            case ColumnDataType.Bool: ((bool[])def.Values)[row] = Convert.ToBoolean(value); break;
            case ColumnDataType.Binary: ((byte[][])def.Values)[row] = (byte[])value; break;
            case ColumnDataType.Decimal: ((decimal[])def.Values)[row] = Convert.ToDecimal(value); break;
            case ColumnDataType.DateTime: ((DateTime[])def.Values)[row] = (DateTime)value; break;
            default: break;
        }
        return this;
        } // lock
    }

    /// <summary>Build the batch. Row count = highest row index + 1.</summary>
    public ColumnarBatch Build()
    {
        int rowCount = _maxRowIndex + 1;
        if (rowCount <= 0 && _columns.Count > 0)
            rowCount = 0;

        var columns = new List<ColumnVector>(_columns.Count);
        foreach (var def in _columns)
        {
            columns.Add(CreateVector(def, rowCount));
        }
        return new ColumnarBatch(rowCount, columns);
    }

    private static ColumnVector CreateVector(ColumnDef def, int rowCount)
    {
        // Trim arrays to actual row count
        byte[] bitmap = new byte[(rowCount + 7) / 8];
        Array.Copy(def.NullBitmap, bitmap, bitmap.Length);

        return def.DataType switch
        {
            ColumnDataType.Int32 => new Int32ColumnVector(def.Name, TrimArray<int>(def.Values, rowCount), bitmap),
            ColumnDataType.Int64 => new Int64ColumnVector(def.Name, TrimArray<long>(def.Values, rowCount), bitmap),
            ColumnDataType.Float64 => new Float64ColumnVector(def.Name, TrimArray<double>(def.Values, rowCount), bitmap),
            ColumnDataType.String => new StringColumnVector(def.Name, TrimArray<string>(def.Values, rowCount), bitmap),
            ColumnDataType.Bool => new BoolColumnVector(def.Name, TrimArray<bool>(def.Values, rowCount), bitmap),
            ColumnDataType.Binary => new BinaryColumnVector(def.Name, TrimArray<byte[]>(def.Values, rowCount), bitmap),
            ColumnDataType.Decimal => new DecimalColumnVector(def.Name, TrimArray<decimal>(def.Values, rowCount), bitmap),
            ColumnDataType.DateTime => new DateTimeColumnVector(def.Name, TrimArray<DateTime>(def.Values, rowCount), bitmap),
            _ => throw new InvalidOperationException($"Unsupported data type: {def.DataType}")
        };
    }

    private static T[] TrimArray<T>(Array source, int length)
    {
        var typed = (T[])source;
        if (typed.Length == length) return typed;
        var result = new T[length];
        Array.Copy(typed, result, length);
        return result;
    }
}
