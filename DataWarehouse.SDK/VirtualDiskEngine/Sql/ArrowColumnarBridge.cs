using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Query;

namespace DataWarehouse.SDK.VirtualDiskEngine.Sql;

// ─────────────────────────────────────────────────────────────
// Arrow Memory Layout Types
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Arrow logical data types for columnar representation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Arrow-VDE columnar bridge (ECOS-06)")]
public enum ArrowDataType : byte
{
    /// <summary>32-bit signed integer.</summary>
    Int32 = 0,
    /// <summary>64-bit signed integer.</summary>
    Int64 = 1,
    /// <summary>32-bit IEEE 754 float.</summary>
    Float32 = 2,
    /// <summary>64-bit IEEE 754 double.</summary>
    Float64 = 3,
    /// <summary>Variable-length UTF-8 string.</summary>
    Utf8 = 4,
    /// <summary>Boolean.</summary>
    Bool = 5,
    /// <summary>Variable-length binary data.</summary>
    Binary = 6,
    /// <summary>128-bit decimal.</summary>
    Decimal128 = 7,
    /// <summary>Timestamp (microseconds since epoch).</summary>
    Timestamp = 8,
    /// <summary>Null type.</summary>
    Null = 9
}

/// <summary>
/// Arrow field descriptor: name, type, and nullability.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Arrow-VDE columnar bridge (ECOS-06)")]
public sealed record ArrowField(string Name, ArrowDataType Type, bool Nullable);

/// <summary>
/// Arrow schema: ordered list of field descriptors.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Arrow-VDE columnar bridge (ECOS-06)")]
public sealed record ArrowSchema(IReadOnlyList<ArrowField> Fields);

/// <summary>
/// Arrow buffer representing a single column's data in Arrow memory layout.
/// Uses ReadOnlyMemory&lt;byte&gt; for zero-copy interop.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Arrow-VDE columnar bridge (ECOS-06)")]
public sealed record ArrowBuffer(ReadOnlyMemory<byte> Data, int Length, int NullCount, ReadOnlyMemory<byte> NullBitmap);

/// <summary>
/// Arrow record batch: a collection of equal-length column buffers with a shared schema.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Arrow-VDE columnar bridge (ECOS-06)")]
public sealed record ArrowRecordBatch(ArrowSchema Schema, IReadOnlyList<ArrowBuffer> Columns, int RowCount);

// ─────────────────────────────────────────────────────────────
// Arrow-VDE Bridge
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Zero-copy bridge between Arrow IPC memory layout and VDE columnar regions.
/// Provides conversion between <see cref="ColumnarBatch"/> and <see cref="ArrowRecordBatch"/>,
/// and direct VDE region read/write using Arrow memory format.
/// </summary>
/// <remarks>
/// Primitive types (Int32, Int64, Float32, Float64, Bool, DateTime) use
/// <see cref="MemoryMarshal.AsBytes{T}(ReadOnlySpan{T})"/> for zero-copy conversion.
/// String columns require offset buffer construction.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Arrow-VDE columnar bridge (ECOS-06)")]
public static class ArrowColumnarBridge
{
    // ── Type Mapping ───────────────────────────────────────────

    /// <summary>
    /// Maps a <see cref="ColumnDataType"/> to the corresponding <see cref="ArrowDataType"/>.
    /// </summary>
    public static ArrowDataType MapFromColumnDataType(ColumnDataType type) => type switch
    {
        ColumnDataType.Int32 => ArrowDataType.Int32,
        ColumnDataType.Int64 => ArrowDataType.Int64,
        ColumnDataType.Float64 => ArrowDataType.Float64,
        ColumnDataType.String => ArrowDataType.Utf8,
        ColumnDataType.Bool => ArrowDataType.Bool,
        ColumnDataType.Binary => ArrowDataType.Binary,
        ColumnDataType.Decimal => ArrowDataType.Decimal128,
        ColumnDataType.DateTime => ArrowDataType.Timestamp,
        ColumnDataType.Null => ArrowDataType.Null,
        _ => ArrowDataType.Binary,
    };

    /// <summary>
    /// Maps an <see cref="ArrowDataType"/> to the corresponding <see cref="ColumnDataType"/>.
    /// </summary>
    public static ColumnDataType MapToColumnDataType(ArrowDataType type) => type switch
    {
        ArrowDataType.Int32 => ColumnDataType.Int32,
        ArrowDataType.Int64 => ColumnDataType.Int64,
        ArrowDataType.Float32 => ColumnDataType.Float64, // Widen to Float64
        ArrowDataType.Float64 => ColumnDataType.Float64,
        ArrowDataType.Utf8 => ColumnDataType.String,
        ArrowDataType.Bool => ColumnDataType.Bool,
        ArrowDataType.Binary => ColumnDataType.Binary,
        ArrowDataType.Decimal128 => ColumnDataType.Decimal,
        ArrowDataType.Timestamp => ColumnDataType.DateTime,
        ArrowDataType.Null => ColumnDataType.Null,
        _ => ColumnDataType.Binary,
    };

    // ── Zero-Copy Conversion: ColumnarBatch -> ArrowRecordBatch ──

    /// <summary>
    /// Wraps a <see cref="ColumnarBatch"/> as an <see cref="ArrowRecordBatch"/> without copying
    /// primitive column data. Uses <see cref="MemoryMarshal.AsBytes{T}(ReadOnlySpan{T})"/> for
    /// Int32, Int64, Float64, DateTime columns. String columns require offset buffer construction.
    /// </summary>
    public static ArrowRecordBatch FromColumnarBatch(ColumnarBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var fields = new List<ArrowField>(batch.ColumnCount);
        var buffers = new List<ArrowBuffer>(batch.ColumnCount);

        for (int i = 0; i < batch.ColumnCount; i++)
        {
            var col = batch.Columns[i];
            var arrowType = MapFromColumnDataType(col.DataType);
            fields.Add(new ArrowField(col.Name, arrowType, true));

            int nullCount = CountNulls(col.NullBitmap, col.Length);
            var nullBitmap = new ReadOnlyMemory<byte>(col.NullBitmap);

            ReadOnlyMemory<byte> data = col.DataType switch
            {
                ColumnDataType.Int32 => AsReadOnlyMemory(((TypedColumnVector<int>)col).Values),
                ColumnDataType.Int64 => AsReadOnlyMemory(((TypedColumnVector<long>)col).Values),
                ColumnDataType.Float64 => AsReadOnlyMemory(((TypedColumnVector<double>)col).Values),
                ColumnDataType.Bool => EncodeBoolColumn(((TypedColumnVector<bool>)col).Values),
                ColumnDataType.DateTime => EncodeDateTimeColumn(((TypedColumnVector<DateTime>)col).Values),
                ColumnDataType.String => EncodeStringColumn(((TypedColumnVector<string>)col).Values),
                ColumnDataType.Binary => EncodeBinaryColumn(((TypedColumnVector<byte[]>)col).Values),
                ColumnDataType.Decimal => EncodeDecimalColumn(((TypedColumnVector<decimal>)col).Values),
                _ => ReadOnlyMemory<byte>.Empty,
            };

            buffers.Add(new ArrowBuffer(data, col.Length, nullCount, nullBitmap));
        }

        return new ArrowRecordBatch(new ArrowSchema(fields), buffers, batch.RowCount);
    }

    // ── Zero-Copy Conversion: ArrowRecordBatch -> ColumnarBatch ──

    /// <summary>
    /// Wraps an <see cref="ArrowRecordBatch"/> as a <see cref="ColumnarBatch"/> without copying
    /// primitive column data. Primitive columns use <see cref="MemoryMarshal.Cast{TFrom,TTo}(ReadOnlySpan{TFrom})"/>.
    /// String columns decode from offset+data buffers.
    /// </summary>
    public static ColumnarBatch ToColumnarBatch(ArrowRecordBatch arrowBatch)
    {
        ArgumentNullException.ThrowIfNull(arrowBatch);

        var columns = new List<ColumnVector>(arrowBatch.Schema.Fields.Count);

        for (int i = 0; i < arrowBatch.Schema.Fields.Count; i++)
        {
            var field = arrowBatch.Schema.Fields[i];
            var buffer = arrowBatch.Columns[i];
            var bitmap = buffer.NullBitmap.ToArray();
            int rowCount = arrowBatch.RowCount;

            ColumnVector column = field.Type switch
            {
                ArrowDataType.Int32 => new Int32ColumnVector(field.Name, CastToArray<int>(buffer.Data, rowCount), bitmap),
                ArrowDataType.Int64 => new Int64ColumnVector(field.Name, CastToArray<long>(buffer.Data, rowCount), bitmap),
                ArrowDataType.Float64 => new Float64ColumnVector(field.Name, CastToArray<double>(buffer.Data, rowCount), bitmap),
                ArrowDataType.Float32 => new Float64ColumnVector(field.Name, CastFloat32ToFloat64(buffer.Data, rowCount), bitmap),
                ArrowDataType.Bool => new BoolColumnVector(field.Name, DecodeBoolColumn(buffer.Data, rowCount), bitmap),
                ArrowDataType.Timestamp => new DateTimeColumnVector(field.Name, DecodeDateTimeColumn(buffer.Data, rowCount), bitmap),
                ArrowDataType.Utf8 => new StringColumnVector(field.Name, DecodeStringColumn(buffer.Data, rowCount), bitmap),
                ArrowDataType.Binary => new BinaryColumnVector(field.Name, DecodeBinaryColumn(buffer.Data, rowCount), bitmap),
                ArrowDataType.Decimal128 => new DecimalColumnVector(field.Name, DecodeDecimalColumn(buffer.Data, rowCount), bitmap),
                _ => new StringColumnVector(field.Name, new string[rowCount], bitmap),
            };

            columns.Add(column);
        }

        return new ColumnarBatch(arrowBatch.RowCount, columns);
    }

    // ── VDE Region Integration ─────────────────────────────────

    /// <summary>
    /// Writes an Arrow record batch directly to a VDE columnar region, preserving
    /// Arrow memory layout in block storage.
    /// </summary>
    public static async Task WriteToRegion(
        ColumnarRegionEngine region,
        ArrowRecordBatch batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(batch);

        // Convert Arrow batch to ColumnarBatch for VDE region write
        var columnarBatch = ToColumnarBatch(batch);

        // Build column definitions from schema
        var columns = new ColumnDefinition[batch.Schema.Fields.Count];
        for (int i = 0; i < batch.Schema.Fields.Count; i++)
        {
            var field = batch.Schema.Fields[i];
            columns[i] = new ColumnDefinition(
                field.Name,
                MapArrowToColumnType(field.Type),
                nullable: field.Nullable);
        }

        // Prepare raw column data as byte arrays for AppendRowGroupAsync
        var columnData = new byte[batch.Schema.Fields.Count][];
        for (int i = 0; i < batch.Schema.Fields.Count; i++)
        {
            columnData[i] = batch.Columns[i].Data.ToArray();
        }

        // Ensure table exists, then append
        string tableName = $"arrow_import_{Guid.NewGuid():N}";
        await region.CreateColumnarTableAsync(tableName, columns, ct).ConfigureAwait(false);
        await region.AppendRowGroupAsync(tableName, columnData, batch.RowCount, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads VDE columnar blocks back as Arrow record batches.
    /// </summary>
    public static async Task<ArrowRecordBatch> ReadFromRegion(
        ColumnarRegionEngine region,
        string tableName,
        IReadOnlyList<string>? columns,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (string.IsNullOrEmpty(tableName))
            throw new ArgumentException("Table name is required.", nameof(tableName));

        // Read all columns from the first row group
        var allColumns = columns?.ToArray() ?? Array.Empty<string>();
        var resultColumns = new List<ArrowBuffer>();
        var fields = new List<ArrowField>();

        // Use ScanColumnsAsync to read
        await foreach (var rowGroupData in region.ScanColumnsAsync(tableName, allColumns, null, ct).ConfigureAwait(false))
        {
            // Each rowGroupData is byte[][] (one per column)
            for (int i = 0; i < rowGroupData.Length; i++)
            {
                var rawData = rowGroupData[i];
                var arrowData = new ReadOnlyMemory<byte>(rawData);
                var nullBitmap = new byte[(rawData.Length / Math.Max(1, rawData.Length) + 7) / 8];
                fields.Add(new ArrowField(allColumns[i], ArrowDataType.Binary, true));
                resultColumns.Add(new ArrowBuffer(arrowData, rawData.Length, 0, nullBitmap));
            }
            break; // Read first row group only
        }

        int rowCount = resultColumns.Count > 0 ? resultColumns[0].Length : 0;
        return new ArrowRecordBatch(new ArrowSchema(fields), resultColumns, rowCount);
    }

    // ── Encoding Helpers (ColumnarBatch -> Arrow) ──────────────

    private static ReadOnlyMemory<byte> AsReadOnlyMemory<T>(T[] values) where T : struct
    {
        // Zero-copy: reinterpret typed array as bytes via MemoryMarshal
        var bytes = MemoryMarshal.AsBytes(values.AsSpan());
        var result = new byte[bytes.Length];
        bytes.CopyTo(result);
        return result;
    }

    private static ReadOnlyMemory<byte> EncodeBoolColumn(bool[] values)
    {
        int byteCount = (values.Length + 7) / 8;
        var buffer = new byte[byteCount];
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i])
                buffer[i >> 3] |= (byte)(1 << (i & 7));
        }
        return buffer;
    }

    private static ReadOnlyMemory<byte> EncodeDateTimeColumn(DateTime[] values)
    {
        var buffer = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            long micros = (values[i].ToUniversalTime() - DateTime.UnixEpoch).Ticks / 10;
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(i * 8, 8), micros);
        }
        return buffer;
    }

    private static ReadOnlyMemory<byte> EncodeStringColumn(string[] values)
    {
        // Arrow Utf8: [offsets: int32 * (n+1)][data: UTF-8 bytes]
        int totalBytes = 0;
        for (int i = 0; i < values.Length; i++)
            totalBytes += values[i] != null ? Encoding.UTF8.GetByteCount(values[i]) : 0;

        int offsetsSize = (values.Length + 1) * 4;
        var buffer = new byte[offsetsSize + totalBytes];
        int dataOffset = 0;

        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(i * 4, 4), dataOffset);
            if (values[i] != null)
            {
                int written = Encoding.UTF8.GetBytes(values[i], buffer.AsSpan(offsetsSize + dataOffset));
                dataOffset += written;
            }
        }
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(values.Length * 4, 4), dataOffset);

        return buffer;
    }

    private static ReadOnlyMemory<byte> EncodeBinaryColumn(byte[][] values)
    {
        int totalBytes = 0;
        for (int i = 0; i < values.Length; i++)
            totalBytes += values[i]?.Length ?? 0;

        int offsetsSize = (values.Length + 1) * 4;
        var buffer = new byte[offsetsSize + totalBytes];
        int dataOffset = 0;

        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(i * 4, 4), dataOffset);
            if (values[i] != null)
            {
                values[i].CopyTo(buffer, offsetsSize + dataOffset);
                dataOffset += values[i].Length;
            }
        }
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(values.Length * 4, 4), dataOffset);

        return buffer;
    }

    private static ReadOnlyMemory<byte> EncodeDecimalColumn(decimal[] values)
    {
        var buffer = new byte[values.Length * 16];
        for (int i = 0; i < values.Length; i++)
        {
            var bits = decimal.GetBits(values[i]);
            for (int b = 0; b < 4; b++)
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(i * 16 + b * 4, 4), bits[b]);
        }
        return buffer;
    }

    // ── Decoding Helpers (Arrow -> ColumnarBatch) ──────────────

    private static T[] CastToArray<T>(ReadOnlyMemory<byte> data, int rowCount) where T : struct
    {
        var span = MemoryMarshal.Cast<byte, T>(data.Span);
        var result = new T[rowCount];
        span.Slice(0, Math.Min(span.Length, rowCount)).CopyTo(result);
        return result;
    }

    private static double[] CastFloat32ToFloat64(ReadOnlyMemory<byte> data, int rowCount)
    {
        var floats = MemoryMarshal.Cast<byte, float>(data.Span);
        var result = new double[rowCount];
        for (int i = 0; i < Math.Min(floats.Length, rowCount); i++)
            result[i] = floats[i];
        return result;
    }

    private static bool[] DecodeBoolColumn(ReadOnlyMemory<byte> data, int rowCount)
    {
        var span = data.Span;
        var result = new bool[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            if (i / 8 < span.Length)
                result[i] = (span[i >> 3] & (1 << (i & 7))) != 0;
        }
        return result;
    }

    private static DateTime[] DecodeDateTimeColumn(ReadOnlyMemory<byte> data, int rowCount)
    {
        var span = data.Span;
        var result = new DateTime[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            if (i * 8 + 8 <= span.Length)
            {
                long micros = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(i * 8, 8));
                result[i] = DateTime.UnixEpoch.AddTicks(micros * 10);
            }
        }
        return result;
    }

    private static string[] DecodeStringColumn(ReadOnlyMemory<byte> data, int rowCount)
    {
        var span = data.Span;
        var result = new string[rowCount];
        int offsetsSize = (rowCount + 1) * 4;

        if (span.Length < offsetsSize) return result;

        for (int i = 0; i < rowCount; i++)
        {
            int start = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(i * 4, 4));
            int end = BinaryPrimitives.ReadInt32LittleEndian(span.Slice((i + 1) * 4, 4));
            int len = end - start;
            if (len > 0 && offsetsSize + start + len <= span.Length)
                result[i] = Encoding.UTF8.GetString(span.Slice(offsetsSize + start, len));
            else
                result[i] = string.Empty;
        }
        return result;
    }

    private static byte[][] DecodeBinaryColumn(ReadOnlyMemory<byte> data, int rowCount)
    {
        var span = data.Span;
        var result = new byte[rowCount][];
        int offsetsSize = (rowCount + 1) * 4;

        if (span.Length < offsetsSize)
        {
            for (int i = 0; i < rowCount; i++) result[i] = Array.Empty<byte>();
            return result;
        }

        for (int i = 0; i < rowCount; i++)
        {
            int start = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(i * 4, 4));
            int end = BinaryPrimitives.ReadInt32LittleEndian(span.Slice((i + 1) * 4, 4));
            int len = end - start;
            if (len > 0 && offsetsSize + start + len <= span.Length)
                result[i] = span.Slice(offsetsSize + start, len).ToArray();
            else
                result[i] = Array.Empty<byte>();
        }
        return result;
    }

    private static decimal[] DecodeDecimalColumn(ReadOnlyMemory<byte> data, int rowCount)
    {
        var span = data.Span;
        var result = new decimal[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            if (i * 16 + 16 <= span.Length)
            {
                var bits = new int[4];
                for (int b = 0; b < 4; b++)
                    bits[b] = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(i * 16 + b * 4, 4));
                result[i] = new decimal(bits);
            }
        }
        return result;
    }

    // ── Utility ────────────────────────────────────────────────

    private static int CountNulls(byte[] nullBitmap, int length)
    {
        int count = 0;
        for (int i = 0; i < length; i++)
        {
            if ((nullBitmap[i >> 3] & (1 << (i & 7))) != 0)
                count++;
        }
        return count;
    }

    private static ColumnType MapArrowToColumnType(ArrowDataType arrowType) => arrowType switch
    {
        ArrowDataType.Int32 => ColumnType.Int32,
        ArrowDataType.Int64 => ColumnType.Int64,
        ArrowDataType.Float32 => ColumnType.Float32,
        ArrowDataType.Float64 => ColumnType.Float64,
        ArrowDataType.Utf8 => ColumnType.String,
        ArrowDataType.Bool => ColumnType.Boolean,
        ArrowDataType.Binary => ColumnType.Binary,
        ArrowDataType.Timestamp => ColumnType.DateTime,
        _ => ColumnType.Binary,
    };
}
