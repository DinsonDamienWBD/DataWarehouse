using System.Buffers;
using System.Runtime.CompilerServices;

namespace DataWarehouse.SDK.Contracts.Query;

// ─────────────────────────────────────────────────────────────
// Aggregate Specification
// ─────────────────────────────────────────────────────────────

/// <summary>Supported aggregate function types for columnar vectorized operations.</summary>
public enum AggregateFunctionType
{
    Sum,
    Count,
    Min,
    Max,
    Avg
}

/// <summary>Specification for an aggregate operation: column + function.</summary>
public sealed class AggregateSpec
{
    /// <summary>Column to aggregate. Null for COUNT(*).</summary>
    public string? ColumnName { get; }

    /// <summary>Aggregate function to apply.</summary>
    public AggregateFunctionType Function { get; }

    /// <summary>Output column name in the result batch.</summary>
    public string OutputName { get; }

    public AggregateSpec(string? columnName, AggregateFunctionType function, string? outputName = null)
    {
        ColumnName = columnName;
        Function = function;
        OutputName = outputName ?? $"{function}_{columnName ?? "star"}";
    }
}

// ─────────────────────────────────────────────────────────────
// CompositeKey for GROUP BY
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Composite key wrapping object[] with structural equality for hash-based grouping.
/// </summary>
public sealed class CompositeKey : IEquatable<CompositeKey>
{
    public object?[] Values { get; }
    private readonly int _hashCode;

    public CompositeKey(object?[] values)
    {
        Values = values;
        _hashCode = ComputeHashCode(values);
    }

    private static int ComputeHashCode(object?[] values)
    {
        var hash = new HashCode();
        foreach (var v in values)
            hash.Add(v);
        return hash.ToHashCode();
    }

    public bool Equals(CompositeKey? other)
    {
        if (other is null || Values.Length != other.Values.Length)
            return false;
        for (int i = 0; i < Values.Length; i++)
        {
            if (!Equals(Values[i], other.Values[i]))
                return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as CompositeKey);
    public override int GetHashCode() => _hashCode;
}

// ─────────────────────────────────────────────────────────────
// Aggregate Accumulators
// ─────────────────────────────────────────────────────────────

/// <summary>Abstract accumulator for aggregate computation.</summary>
public abstract class AggregateAccumulator
{
    public abstract void Accumulate(object? value);
    public abstract object? GetResult();
    public abstract ColumnDataType ResultType { get; }
}

/// <summary>SUM accumulator for numeric types (uses double for universal precision).</summary>
public sealed class SumAccumulator : AggregateAccumulator
{
    private double _sum;
    private bool _hasValue;

    public override ColumnDataType ResultType => ColumnDataType.Float64;

    public override void Accumulate(object? value)
    {
        if (value is null) return;
        _sum += Convert.ToDouble(value);
        _hasValue = true;
    }

    public override object? GetResult() => _hasValue ? _sum : null;
}

/// <summary>COUNT accumulator. Counts non-null values (or all rows for COUNT(*)).</summary>
public sealed class CountAccumulator : AggregateAccumulator
{
    private long _count;
    private readonly bool _countAll;

    public override ColumnDataType ResultType => ColumnDataType.Int64;

    public CountAccumulator(bool countAll = false) { _countAll = countAll; }

    public override void Accumulate(object? value)
    {
        if (_countAll || value is not null)
            _count++;
    }

    public override object? GetResult() => _count;
}

/// <summary>MIN accumulator for IComparable values.</summary>
public sealed class MinAccumulator : AggregateAccumulator
{
    private object? _min;
    private bool _hasValue;
    private readonly ColumnDataType _sourceType;

    public override ColumnDataType ResultType => _sourceType;

    public MinAccumulator(ColumnDataType sourceType) { _sourceType = sourceType; }

    public override void Accumulate(object? value)
    {
        if (value is null) return;
        if (!_hasValue || Comparer<object>.Default.Compare(value, _min) < 0)
        {
            _min = value;
            _hasValue = true;
        }
    }

    public override object? GetResult() => _hasValue ? _min : null;
}

/// <summary>MAX accumulator for IComparable values.</summary>
public sealed class MaxAccumulator : AggregateAccumulator
{
    private object? _max;
    private bool _hasValue;
    private readonly ColumnDataType _sourceType;

    public override ColumnDataType ResultType => _sourceType;

    public MaxAccumulator(ColumnDataType sourceType) { _sourceType = sourceType; }

    public override void Accumulate(object? value)
    {
        if (value is null) return;
        if (!_hasValue || Comparer<object>.Default.Compare(value, _max) > 0)
        {
            _max = value;
            _hasValue = true;
        }
    }

    public override object? GetResult() => _hasValue ? _max : null;
}

/// <summary>AVG accumulator: sum / count of non-null values.</summary>
public sealed class AvgAccumulator : AggregateAccumulator
{
    private double _sum;
    private long _count;

    public override ColumnDataType ResultType => ColumnDataType.Float64;

    public override void Accumulate(object? value)
    {
        if (value is null) return;
        _sum += Convert.ToDouble(value);
        _count++;
    }

    public override object? GetResult() => _count > 0 ? _sum / _count : null;
}

// ─────────────────────────────────────────────────────────────
// Columnar Engine
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Columnar execution engine: projection, filtering, and vectorized aggregation
/// on ColumnarBatch data. All operations produce new batches (immutable semantics).
/// Uses ArrayPool for temporary allocations in filter/group operations.
/// </summary>
public static class ColumnarEngine
{
    // ───────── Projection ─────────

    /// <summary>
    /// Project (select) a subset of columns from a batch.
    /// </summary>
    public static ColumnarBatch ScanBatch(ColumnarBatch batch, List<string> columns)
    {
        var projected = new List<ColumnVector>(columns.Count);
        foreach (var colName in columns)
        {
            var col = batch.GetColumn(colName)
                ?? throw new ArgumentException($"Column '{colName}' not found in batch.");
            projected.Add(col);
        }
        return new ColumnarBatch(batch.RowCount, projected);
    }

    // ───────── Filtering ─────────

    /// <summary>
    /// Filter rows by a predicate. Returns a new batch with only matching rows.
    /// Uses ArrayPool for the index buffer.
    /// </summary>
    public static ColumnarBatch FilterBatch(ColumnarBatch batch, Func<int, bool> predicate)
    {
        // Collect matching indices
        int[] indexBuffer = ArrayPool<int>.Shared.Rent(batch.RowCount);
        try
        {
            int matchCount = 0;
            for (int i = 0; i < batch.RowCount; i++)
            {
                if (predicate(i))
                    indexBuffer[matchCount++] = i;
            }

            if (matchCount == 0)
                return new ColumnarBatch(0, batch.Columns.Select(c => CreateEmptyColumn(c)).ToList());

            var newColumns = new List<ColumnVector>(batch.ColumnCount);
            foreach (var col in batch.Columns)
            {
                newColumns.Add(GatherColumn(col, indexBuffer, matchCount));
            }

            return new ColumnarBatch(matchCount, newColumns);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(indexBuffer);
        }
    }

    // ───────── Vectorized Aggregations ─────────

    /// <summary>Vectorized SUM on Int64 column — tight loop, no per-row boxing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static long VectorizedSum(Int64ColumnVector col)
    {
        long sum = 0;
        var values = col.Values;
        var bitmap = col.NullBitmap;
        for (int i = 0; i < col.Length; i++)
        {
            if ((bitmap[i >> 3] & (1 << (i & 7))) == 0)
                sum += values[i];
        }
        return sum;
    }

    /// <summary>Vectorized SUM on Float64 column.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double VectorizedSum(Float64ColumnVector col)
    {
        double sum = 0;
        var values = col.Values;
        var bitmap = col.NullBitmap;
        for (int i = 0; i < col.Length; i++)
        {
            if ((bitmap[i >> 3] & (1 << (i & 7))) == 0)
                sum += values[i];
        }
        return sum;
    }

    /// <summary>Vectorized SUM on Int32 column.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static long VectorizedSum(Int32ColumnVector col)
    {
        long sum = 0;
        var values = col.Values;
        var bitmap = col.NullBitmap;
        for (int i = 0; i < col.Length; i++)
        {
            if ((bitmap[i >> 3] & (1 << (i & 7))) == 0)
                sum += values[i];
        }
        return sum;
    }

    /// <summary>Vectorized SUM on Decimal column.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static decimal VectorizedSum(DecimalColumnVector col)
    {
        decimal sum = 0;
        var values = col.Values;
        var bitmap = col.NullBitmap;
        for (int i = 0; i < col.Length; i++)
        {
            if ((bitmap[i >> 3] & (1 << (i & 7))) == 0)
                sum += values[i];
        }
        return sum;
    }

    /// <summary>Vectorized COUNT of non-null values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static long VectorizedCount(ColumnVector col, bool countNulls = false)
    {
        if (countNulls) return col.Length;

        long count = 0;
        var bitmap = col.NullBitmap;
        for (int i = 0; i < col.Length; i++)
        {
            if ((bitmap[i >> 3] & (1 << (i & 7))) == 0)
                count++;
        }
        return count;
    }

    /// <summary>Vectorized MIN on Int64 column.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static long? VectorizedMin(Int64ColumnVector col)
    {
        long min = long.MaxValue;
        bool found = false;
        var values = col.Values;
        var bitmap = col.NullBitmap;
        for (int i = 0; i < col.Length; i++)
        {
            if ((bitmap[i >> 3] & (1 << (i & 7))) == 0)
            {
                if (values[i] < min) min = values[i];
                found = true;
            }
        }
        return found ? min : null;
    }

    /// <summary>Vectorized MAX on Int64 column.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static long? VectorizedMax(Int64ColumnVector col)
    {
        long max = long.MinValue;
        bool found = false;
        var values = col.Values;
        var bitmap = col.NullBitmap;
        for (int i = 0; i < col.Length; i++)
        {
            if ((bitmap[i >> 3] & (1 << (i & 7))) == 0)
            {
                if (values[i] > max) max = values[i];
                found = true;
            }
        }
        return found ? max : null;
    }

    /// <summary>Vectorized MIN on Float64 column.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double? VectorizedMin(Float64ColumnVector col)
    {
        double min = double.MaxValue;
        bool found = false;
        var values = col.Values;
        var bitmap = col.NullBitmap;
        for (int i = 0; i < col.Length; i++)
        {
            if ((bitmap[i >> 3] & (1 << (i & 7))) == 0)
            {
                if (values[i] < min) min = values[i];
                found = true;
            }
        }
        return found ? min : null;
    }

    /// <summary>Vectorized MAX on Float64 column.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double? VectorizedMax(Float64ColumnVector col)
    {
        double max = double.MinValue;
        bool found = false;
        var values = col.Values;
        var bitmap = col.NullBitmap;
        for (int i = 0; i < col.Length; i++)
        {
            if ((bitmap[i >> 3] & (1 << (i & 7))) == 0)
            {
                if (values[i] > max) max = values[i];
                found = true;
            }
        }
        return found ? max : null;
    }

    /// <summary>Vectorized MIN on Int32 column.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int? VectorizedMin(Int32ColumnVector col)
    {
        int min = int.MaxValue;
        bool found = false;
        var values = col.Values;
        var bitmap = col.NullBitmap;
        for (int i = 0; i < col.Length; i++)
        {
            if ((bitmap[i >> 3] & (1 << (i & 7))) == 0)
            {
                if (values[i] < min) min = values[i];
                found = true;
            }
        }
        return found ? min : null;
    }

    /// <summary>Vectorized MAX on Int32 column.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int? VectorizedMax(Int32ColumnVector col)
    {
        int max = int.MinValue;
        bool found = false;
        var values = col.Values;
        var bitmap = col.NullBitmap;
        for (int i = 0; i < col.Length; i++)
        {
            if ((bitmap[i >> 3] & (1 << (i & 7))) == 0)
            {
                if (values[i] > max) max = values[i];
                found = true;
            }
        }
        return found ? max : null;
    }

    /// <summary>Vectorized MIN on DateTime column.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static DateTime? VectorizedMin(DateTimeColumnVector col)
    {
        DateTime min = DateTime.MaxValue;
        bool found = false;
        var values = col.Values;
        var bitmap = col.NullBitmap;
        for (int i = 0; i < col.Length; i++)
        {
            if ((bitmap[i >> 3] & (1 << (i & 7))) == 0)
            {
                if (values[i] < min) min = values[i];
                found = true;
            }
        }
        return found ? min : null;
    }

    /// <summary>Vectorized MAX on DateTime column.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static DateTime? VectorizedMax(DateTimeColumnVector col)
    {
        DateTime max = DateTime.MinValue;
        bool found = false;
        var values = col.Values;
        var bitmap = col.NullBitmap;
        for (int i = 0; i < col.Length; i++)
        {
            if ((bitmap[i >> 3] & (1 << (i & 7))) == 0)
            {
                if (values[i] > max) max = values[i];
                found = true;
            }
        }
        return found ? max : null;
    }

    /// <summary>Vectorized AVG on Int64 column (returns double).</summary>
    public static double? VectorizedAvg(Int64ColumnVector col)
    {
        long sum = 0;
        long count = 0;
        var values = col.Values;
        var bitmap = col.NullBitmap;
        for (int i = 0; i < col.Length; i++)
        {
            if ((bitmap[i >> 3] & (1 << (i & 7))) == 0)
            {
                sum += values[i];
                count++;
            }
        }
        return count > 0 ? (double)sum / count : null;
    }

    /// <summary>Vectorized AVG on Float64 column.</summary>
    public static double? VectorizedAvg(Float64ColumnVector col)
    {
        double sum = 0;
        long count = 0;
        var values = col.Values;
        var bitmap = col.NullBitmap;
        for (int i = 0; i < col.Length; i++)
        {
            if ((bitmap[i >> 3] & (1 << (i & 7))) == 0)
            {
                sum += values[i];
                count++;
            }
        }
        return count > 0 ? sum / count : null;
    }

    // ───────── GROUP BY Aggregation ─────────

    /// <summary>
    /// Hash-based GROUP BY aggregation. Groups rows by composite key columns,
    /// then applies aggregate functions. Uses Dictionary for O(1) group lookup.
    /// </summary>
    public static ColumnarBatch GroupByAggregate(
        ColumnarBatch batch,
        List<string> groupKeys,
        List<AggregateSpec> aggregates)
    {
        // Resolve group key columns
        var keyColumns = groupKeys.Select(k =>
            batch.GetColumn(k) ?? throw new ArgumentException($"Group key column '{k}' not found."))
            .ToList();

        // Resolve aggregate source columns
        var aggColumns = aggregates.Select(a =>
            a.ColumnName is not null
                ? batch.GetColumn(a.ColumnName) ?? throw new ArgumentException($"Aggregate column '{a.ColumnName}' not found.")
                : null)
            .ToList();

        // Build groups: CompositeKey -> list of accumulators
        var groups = new Dictionary<CompositeKey, AggregateAccumulator[]>();

        for (int row = 0; row < batch.RowCount; row++)
        {
            // Extract key values
            var keyValues = new object?[groupKeys.Count];
            for (int k = 0; k < keyColumns.Count; k++)
                keyValues[k] = keyColumns[k].GetValue(row);

            var key = new CompositeKey(keyValues);

            if (!groups.TryGetValue(key, out var accumulators))
            {
                accumulators = CreateAccumulators(aggregates, aggColumns);
                groups[key] = accumulators;
            }

            // Feed values to accumulators
            for (int a = 0; a < aggregates.Count; a++)
            {
                var col = aggColumns[a];
                var value = col?.GetValue(row);
                accumulators[a].Accumulate(value);
            }
        }

        // Build result batch
        return BuildGroupByResult(groupKeys, keyColumns, aggregates, groups);
    }

    // ───────── Private Helpers ─────────

    private static AggregateAccumulator[] CreateAccumulators(
        List<AggregateSpec> specs, List<ColumnVector?> columns)
    {
        var accumulators = new AggregateAccumulator[specs.Count];
        for (int i = 0; i < specs.Count; i++)
        {
            var sourceType = columns[i]?.DataType ?? ColumnDataType.Null;
            accumulators[i] = specs[i].Function switch
            {
                AggregateFunctionType.Sum => new SumAccumulator(),
                AggregateFunctionType.Count => new CountAccumulator(specs[i].ColumnName is null),
                AggregateFunctionType.Min => new MinAccumulator(sourceType),
                AggregateFunctionType.Max => new MaxAccumulator(sourceType),
                AggregateFunctionType.Avg => new AvgAccumulator(),
                _ => throw new ArgumentOutOfRangeException()
            };
        }
        return accumulators;
    }

    private static ColumnarBatch BuildGroupByResult(
        List<string> groupKeys,
        List<ColumnVector> keyColumns,
        List<AggregateSpec> aggregates,
        Dictionary<CompositeKey, AggregateAccumulator[]> groups)
    {
        int rowCount = groups.Count;
        var builder = new ColumnarBatchBuilder(rowCount);

        // Add key columns
        for (int k = 0; k < groupKeys.Count; k++)
            builder.AddColumn(groupKeys[k], keyColumns[k].DataType);

        // Add aggregate result columns
        for (int a = 0; a < aggregates.Count; a++)
        {
            var resultType = groups.Count > 0
                ? groups.Values.First()[a].ResultType
                : ColumnDataType.Null;
            builder.AddColumn(aggregates[a].OutputName, resultType);
        }

        int row = 0;
        foreach (var (key, accumulators) in groups)
        {
            // Set key values
            for (int k = 0; k < groupKeys.Count; k++)
                builder.SetValue(k, row, key.Values[k]);

            // Set aggregate results
            for (int a = 0; a < aggregates.Count; a++)
                builder.SetValue(groupKeys.Count + a, row, accumulators[a].GetResult());

            row++;
        }

        return builder.Build();
    }

    private static ColumnVector CreateEmptyColumn(ColumnVector source) => source.DataType switch
    {
        ColumnDataType.Int32 => new Int32ColumnVector(source.Name, Array.Empty<int>()),
        ColumnDataType.Int64 => new Int64ColumnVector(source.Name, Array.Empty<long>()),
        ColumnDataType.Float64 => new Float64ColumnVector(source.Name, Array.Empty<double>()),
        ColumnDataType.String => new StringColumnVector(source.Name, Array.Empty<string>()),
        ColumnDataType.Bool => new BoolColumnVector(source.Name, Array.Empty<bool>()),
        ColumnDataType.Binary => new BinaryColumnVector(source.Name, Array.Empty<byte[]>()),
        ColumnDataType.Decimal => new DecimalColumnVector(source.Name, Array.Empty<decimal>()),
        ColumnDataType.DateTime => new DateTimeColumnVector(source.Name, Array.Empty<DateTime>()),
        _ => throw new InvalidOperationException($"Unsupported data type: {source.DataType}")
    };

    private static ColumnVector GatherColumn(ColumnVector source, int[] indices, int count)
    {
        return source.DataType switch
        {
            ColumnDataType.Int32 => GatherTyped<int>((TypedColumnVector<int>)source, indices, count,
                (n, v, b) => new Int32ColumnVector(n, v, b)),
            ColumnDataType.Int64 => GatherTyped<long>((TypedColumnVector<long>)source, indices, count,
                (n, v, b) => new Int64ColumnVector(n, v, b)),
            ColumnDataType.Float64 => GatherTyped<double>((TypedColumnVector<double>)source, indices, count,
                (n, v, b) => new Float64ColumnVector(n, v, b)),
            ColumnDataType.String => GatherTyped<string>((TypedColumnVector<string>)source, indices, count,
                (n, v, b) => new StringColumnVector(n, v, b)),
            ColumnDataType.Bool => GatherTyped<bool>((TypedColumnVector<bool>)source, indices, count,
                (n, v, b) => new BoolColumnVector(n, v, b)),
            ColumnDataType.Binary => GatherTyped<byte[]>((TypedColumnVector<byte[]>)source, indices, count,
                (n, v, b) => new BinaryColumnVector(n, v, b)),
            ColumnDataType.Decimal => GatherTyped<decimal>((TypedColumnVector<decimal>)source, indices, count,
                (n, v, b) => new DecimalColumnVector(n, v, b)),
            ColumnDataType.DateTime => GatherTyped<DateTime>((TypedColumnVector<DateTime>)source, indices, count,
                (n, v, b) => new DateTimeColumnVector(n, v, b)),
            _ => throw new InvalidOperationException($"Unsupported data type: {source.DataType}")
        };
    }

    private static ColumnVector GatherTyped<T>(
        TypedColumnVector<T> source,
        int[] indices,
        int count,
        Func<string, T[], byte[], ColumnVector> factory)
    {
        var values = new T[count];
        var bitmap = new byte[(count + 7) / 8];

        for (int i = 0; i < count; i++)
        {
            int srcIdx = indices[i];
            values[i] = source.Values[srcIdx];
            if (source.IsNull(srcIdx))
                bitmap[i >> 3] |= (byte)(1 << (i & 7));
        }

        return factory(source.Name, values, bitmap);
    }
}
