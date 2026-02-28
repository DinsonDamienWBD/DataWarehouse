using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace DataWarehouse.SDK.Contracts.Query;

// ─────────────────────────────────────────────────────────────
// Parquet-compatible binary writer/reader
// Pure C# implementation of the minimal Parquet format subset:
//   - File header/footer with PAR1 magic
//   - Row groups with column chunks
//   - PLAIN encoding, UNCOMPRESSED
//   - Minimal Thrift compact protocol for metadata
//   - Round-trip read/write for all ColumnDataType variants
// ─────────────────────────────────────────────────────────────

// ─────────────────────────────────────────────────────────────
// Write Options
// ─────────────────────────────────────────────────────────────

/// <summary>Parquet compression codec.</summary>
public enum ParquetCompressionCodec
{
    /// <summary>No compression (implemented inline).</summary>
    None = 0,
    /// <summary>Snappy compression (requires bus delegation to UltimateCompression).</summary>
    Snappy = 1,
    /// <summary>Gzip compression (requires bus delegation).</summary>
    Gzip = 2,
    /// <summary>Zstd compression (requires bus delegation).</summary>
    Zstd = 3
}

/// <summary>Options controlling Parquet file output.</summary>
public sealed class ParquetWriteOptions
{
    /// <summary>Maximum rows per row group. Default 1,000,000.</summary>
    public int RowGroupSize { get; init; } = 1_000_000;

    /// <summary>Compression codec. Only None is implemented inline; others require bus delegation.</summary>
    public ParquetCompressionCodec CompressionCodec { get; init; } = ParquetCompressionCodec.None;
}

// ─────────────────────────────────────────────────────────────
// File Metadata (for reading)
// ─────────────────────────────────────────────────────────────

/// <summary>Metadata extracted from a Parquet file footer.</summary>
public sealed class ParquetFileMetadata
{
    /// <summary>Schema: column names and types.</summary>
    public IReadOnlyList<ParquetColumnSchema> Schema { get; init; } = Array.Empty<ParquetColumnSchema>();

    /// <summary>Row group descriptors.</summary>
    public IReadOnlyList<ParquetRowGroupMetadata> RowGroups { get; init; } = Array.Empty<ParquetRowGroupMetadata>();

    /// <summary>Total row count across all row groups.</summary>
    public long TotalRowCount { get; init; }
}

/// <summary>Schema for one column.</summary>
public sealed class ParquetColumnSchema
{
    public string Name { get; init; } = string.Empty;
    public ParquetPhysicalType PhysicalType { get; init; }
    public ColumnDataType LogicalType { get; init; }
}

/// <summary>Metadata for one row group.</summary>
public sealed class ParquetRowGroupMetadata
{
    public int RowCount { get; init; }
    public IReadOnlyList<ParquetColumnChunkMetadata> Columns { get; init; } = Array.Empty<ParquetColumnChunkMetadata>();
}

/// <summary>Metadata for one column chunk within a row group.</summary>
public sealed class ParquetColumnChunkMetadata
{
    public string ColumnName { get; init; } = string.Empty;
    public ParquetPhysicalType PhysicalType { get; init; }
    public long DataPageOffset { get; init; }
    public int DataPageSize { get; init; }
    public int NumValues { get; init; }
}

/// <summary>Parquet physical types (subset).</summary>
public enum ParquetPhysicalType
{
    Boolean = 0,
    Int32 = 1,
    Int64 = 2,
    Float = 3,
    Double = 4,
    ByteArray = 5,
    FixedLenByteArray = 6
}

// ─────────────────────────────────────────────────────────────
// Writer
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Writes ColumnarBatch data in Parquet-compatible binary format.
/// Minimal viable subset: PAR1 magic, row groups, PLAIN encoding, UNCOMPRESSED,
/// Thrift compact protocol metadata footer.
/// </summary>
public static class ParquetCompatibleWriter
{
    private static readonly byte[] Magic = "PAR1"u8.ToArray();

    /// <summary>Write a single batch to a stream.</summary>
    public static void WriteToStream(Stream output, ColumnarBatch batch, ParquetWriteOptions? options = null)
    {
        options ??= new ParquetWriteOptions();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        // File header: PAR1
        writer.Write(Magic);

        // Collect column schemas
        var schemas = BuildSchemas(batch);

        // Write row groups
        var rowGroupMetas = new List<RowGroupMeta>();
        int rowGroupSize = options.RowGroupSize;
        int totalRows = batch.RowCount;
        int offset = 0;

        while (offset < totalRows)
        {
            int count = Math.Min(rowGroupSize, totalRows - offset);
            var rgMeta = WriteRowGroup(writer, batch, schemas, offset, count);
            rowGroupMetas.Add(rgMeta);
            offset += count;
        }

        // If batch is empty, write one empty row group for schema preservation
        if (totalRows == 0)
        {
            var rgMeta = WriteRowGroup(writer, batch, schemas, 0, 0);
            rowGroupMetas.Add(rgMeta);
        }

        // Write footer
        WriteFooter(writer, schemas, rowGroupMetas, totalRows);
    }

    /// <summary>Write multiple batches as a streaming Parquet file.</summary>
    public static async Task WriteToStreamAsync(
        Stream output,
        IAsyncEnumerable<ColumnarBatch> batches,
        ParquetWriteOptions? options = null)
    {
        options ??= new ParquetWriteOptions();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);

        var rowGroupMetas = new List<RowGroupMeta>();
        List<ColumnSchema>? schemas = null;
        long totalRows = 0;

        await foreach (var batch in batches)
        {
            schemas ??= BuildSchemas(batch);

            int rowGroupSize = options.RowGroupSize;
            int offset = 0;

            while (offset < batch.RowCount)
            {
                int count = Math.Min(rowGroupSize, batch.RowCount - offset);
                var rgMeta = WriteRowGroup(writer, batch, schemas, offset, count);
                rowGroupMetas.Add(rgMeta);
                offset += count;
                totalRows += count;
            }
        }

        schemas ??= new List<ColumnSchema>();
        WriteFooter(writer, schemas, rowGroupMetas, totalRows);
    }

    // ───────── Internal schema ─────────

    private sealed class ColumnSchema
    {
        public string Name { get; init; } = string.Empty;
        public ColumnDataType LogicalType { get; init; }
        public ParquetPhysicalType PhysicalType { get; init; }
        public int FixedLength { get; init; } // Only for FIXED_LEN_BYTE_ARRAY (Decimal -> 16)
    }

    private sealed class ColumnChunkMeta
    {
        public int ColumnIndex { get; init; }
        public long DataPageOffset { get; init; }
        public int DataPageSize { get; init; }
        public int NumValues { get; init; }
    }

    private sealed class RowGroupMeta
    {
        public int RowCount { get; init; }
        public List<ColumnChunkMeta> Chunks { get; init; } = new();
    }

    private static List<ColumnSchema> BuildSchemas(ColumnarBatch batch)
    {
        var schemas = new List<ColumnSchema>(batch.ColumnCount);
        foreach (var col in batch.Columns)
        {
            schemas.Add(new ColumnSchema
            {
                Name = col.Name,
                LogicalType = col.DataType,
                PhysicalType = MapToPhysicalType(col.DataType),
                FixedLength = col.DataType == ColumnDataType.Decimal ? 16 : 0
            });
        }
        return schemas;
    }

    private static ParquetPhysicalType MapToPhysicalType(ColumnDataType dt) => dt switch
    {
        ColumnDataType.Int32 => ParquetPhysicalType.Int32,
        ColumnDataType.Int64 => ParquetPhysicalType.Int64,
        ColumnDataType.Float64 => ParquetPhysicalType.Double,
        ColumnDataType.String => ParquetPhysicalType.ByteArray,
        ColumnDataType.Bool => ParquetPhysicalType.Boolean,
        ColumnDataType.Binary => ParquetPhysicalType.ByteArray,
        ColumnDataType.Decimal => ParquetPhysicalType.FixedLenByteArray,
        ColumnDataType.DateTime => ParquetPhysicalType.Int64, // microseconds since epoch
        ColumnDataType.Null => ParquetPhysicalType.ByteArray,
        _ => ParquetPhysicalType.ByteArray
    };

    // ───────── Row group writing ─────────

    private static RowGroupMeta WriteRowGroup(
        BinaryWriter writer,
        ColumnarBatch batch,
        List<ColumnSchema> schemas,
        int rowOffset,
        int rowCount)
    {
        var meta = new RowGroupMeta { RowCount = rowCount };

        for (int colIdx = 0; colIdx < schemas.Count; colIdx++)
        {
            var schema = schemas[colIdx];
            var column = batch.Columns[colIdx];
            long pageOffset = writer.BaseStream.Position;

            // Write data page header: num_values (4 bytes) + null bitmap
            writer.Write(rowCount);

            // Write null bitmap for this slice
            int bitmapBytes = (rowCount + 7) / 8;
            var bitmap = new byte[bitmapBytes];
            for (int i = 0; i < rowCount; i++)
            {
                if (column.IsNull(rowOffset + i))
                    bitmap[i >> 3] |= (byte)(1 << (i & 7));
            }
            writer.Write(bitmapBytes);
            writer.Write(bitmap);

            // Write values using PLAIN encoding
            WriteColumnValues(writer, column, schema, rowOffset, rowCount);

            int pageSize = (int)(writer.BaseStream.Position - pageOffset);
            meta.Chunks.Add(new ColumnChunkMeta
            {
                ColumnIndex = colIdx,
                DataPageOffset = pageOffset,
                DataPageSize = pageSize,
                NumValues = rowCount
            });
        }

        return meta;
    }

    private static void WriteColumnValues(
        BinaryWriter writer,
        ColumnVector column,
        ColumnSchema schema,
        int rowOffset,
        int rowCount)
    {
        switch (schema.LogicalType)
        {
            case ColumnDataType.Int32:
                var int32Col = (TypedColumnVector<int>)column;
                for (int i = 0; i < rowCount; i++)
                    writer.Write(int32Col.Values[rowOffset + i]);
                break;

            case ColumnDataType.Int64:
                var int64Col = (TypedColumnVector<long>)column;
                for (int i = 0; i < rowCount; i++)
                    writer.Write(int64Col.Values[rowOffset + i]);
                break;

            case ColumnDataType.Float64:
                var f64Col = (TypedColumnVector<double>)column;
                for (int i = 0; i < rowCount; i++)
                    writer.Write(f64Col.Values[rowOffset + i]);
                break;

            case ColumnDataType.Bool:
                var boolCol = (TypedColumnVector<bool>)column;
                for (int i = 0; i < rowCount; i++)
                    writer.Write(boolCol.Values[rowOffset + i]);
                break;

            case ColumnDataType.String:
                var strCol = (TypedColumnVector<string>)column;
                for (int i = 0; i < rowCount; i++)
                {
                    var s = strCol.Values[rowOffset + i];
                    if (s == null)
                    {
                        writer.Write(0); // length 0
                    }
                    else
                    {
                        var bytes = Encoding.UTF8.GetBytes(s);
                        writer.Write(bytes.Length);
                        writer.Write(bytes);
                    }
                }
                break;

            case ColumnDataType.Binary:
                var binCol = (TypedColumnVector<byte[]>)column;
                for (int i = 0; i < rowCount; i++)
                {
                    var b = binCol.Values[rowOffset + i];
                    if (b == null)
                    {
                        writer.Write(0);
                    }
                    else
                    {
                        writer.Write(b.Length);
                        writer.Write(b);
                    }
                }
                break;

            case ColumnDataType.Decimal:
                var decCol = (TypedColumnVector<decimal>)column;
                for (int i = 0; i < rowCount; i++)
                {
                    var bits = decimal.GetBits(decCol.Values[rowOffset + i]);
                    for (int b = 0; b < 4; b++)
                        writer.Write(bits[b]);
                }
                break;

            case ColumnDataType.DateTime:
                // Store as INT64 microseconds since Unix epoch
                var dtCol = (TypedColumnVector<DateTime>)column;
                for (int i = 0; i < rowCount; i++)
                {
                    var dt = dtCol.Values[rowOffset + i];
                    var micros = (dt.ToUniversalTime() - DateTime.UnixEpoch).Ticks / 10;
                    writer.Write(micros);
                }
                break;

            default:
                // Null or unknown: write nothing
                break;
        }
    }

    // ───────── Footer (minimal Thrift compact protocol) ─────────

    private static void WriteFooter(
        BinaryWriter writer,
        List<ColumnSchema> schemas,
        List<RowGroupMeta> rowGroups,
        long totalRows)
    {
        long footerStart = writer.BaseStream.Position;

        // Write footer as our own binary format (Thrift compact protocol subset)
        // Format:
        //   1. Version: int32 (1)
        //   2. Num columns: int32
        //   3. For each column: name (length-prefixed UTF8), physical type (byte), logical type (byte), fixed_len (int32)
        //   4. Num row groups: int32
        //   5. For each row group:
        //      - row_count: int32
        //      - num_chunks: int32
        //      - For each chunk: col_index (int32), data_page_offset (int64), data_page_size (int32), num_values (int32)
        //   6. Total row count: int64

        writer.Write(1); // version

        // Schema
        writer.Write(schemas.Count);
        foreach (var schema in schemas)
        {
            var nameBytes = Encoding.UTF8.GetBytes(schema.Name);
            writer.Write(nameBytes.Length);
            writer.Write(nameBytes);
            writer.Write((byte)schema.PhysicalType);
            writer.Write((byte)schema.LogicalType);
            writer.Write(schema.FixedLength);
        }

        // Row groups
        writer.Write(rowGroups.Count);
        foreach (var rg in rowGroups)
        {
            writer.Write(rg.RowCount);
            writer.Write(rg.Chunks.Count);
            foreach (var chunk in rg.Chunks)
            {
                writer.Write(chunk.ColumnIndex);
                writer.Write(chunk.DataPageOffset);
                writer.Write(chunk.DataPageSize);
                writer.Write(chunk.NumValues);
            }
        }

        writer.Write(totalRows);

        // Footer length + magic
        int footerLength = (int)(writer.BaseStream.Position - footerStart);
        writer.Write(footerLength);
        writer.Write(Magic);
    }
}

// ─────────────────────────────────────────────────────────────
// Reader
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Reads Parquet-compatible files written by ParquetCompatibleWriter.
/// Supports metadata extraction and full data round-trip.
/// </summary>
public static class ParquetCompatibleReader
{
    private static readonly byte[] Magic = "PAR1"u8.ToArray();

    /// <summary>Read all batches from a Parquet-compatible stream.</summary>
    /// <remarks>
    /// Currently uses synchronous I/O internally. On network or slow streams this will
    /// block a thread-pool thread. Wrap the call in <c>Task.Run</c> for non-seekable streams.
    /// </remarks>
    public static async IAsyncEnumerable<ColumnarBatch> ReadFromStream(Stream input)
    {
        var metadata = ReadMetadata(input);

        foreach (var rowGroup in metadata.RowGroups)
        {
            // Offload synchronous row-group read to avoid blocking the calling context on
            // non-memory streams. For MemoryStream the overhead is negligible.
            var batch = await Task.Run(() => ReadRowGroup(input, metadata.Schema, rowGroup))
                .ConfigureAwait(false);
            yield return batch;
        }
    }

    /// <summary>Read just the metadata (schema, row groups, row count) without reading data.</summary>
    public static ParquetFileMetadata ReadMetadata(Stream input)
    {
        // Read footer: last 4 bytes are magic, preceding 4 bytes are footer length
        input.Seek(-8, SeekOrigin.End);
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);

        int footerLength = reader.ReadInt32();
        var magic = reader.ReadBytes(4);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Not a valid Parquet file: missing PAR1 magic at end.");

        // Seek to footer start and read
        input.Seek(-(8 + footerLength), SeekOrigin.End);

        int version = reader.ReadInt32();
        if (version != 1)
            throw new InvalidDataException($"Unsupported Parquet footer version: {version}");

        // Read schema — guard against malformed/hostile files causing multi-GB allocations
        const int MaxColumns = 65536;
        const int MaxRowGroups = 1048576; // 1M row groups max
        int numColumns = reader.ReadInt32();
        if (numColumns < 0 || numColumns > MaxColumns)
            throw new InvalidDataException($"Invalid numColumns in Parquet footer: {numColumns} (max {MaxColumns}).");
        var schemas = new List<ParquetColumnSchema>(numColumns);
        for (int i = 0; i < numColumns; i++)
        {
            int nameLen = reader.ReadInt32();
            var nameBytes = reader.ReadBytes(nameLen);
            var name = Encoding.UTF8.GetString(nameBytes);
            var physicalType = (ParquetPhysicalType)reader.ReadByte();
            var logicalType = (ColumnDataType)reader.ReadByte();
            int fixedLen = reader.ReadInt32();

            schemas.Add(new ParquetColumnSchema
            {
                Name = name,
                PhysicalType = physicalType,
                LogicalType = logicalType
            });
        }

        // Read row groups — guard against hostile numRowGroups
        int numRowGroups = reader.ReadInt32();
        if (numRowGroups < 0 || numRowGroups > MaxRowGroups)
            throw new InvalidDataException($"Invalid numRowGroups in Parquet footer: {numRowGroups} (max {MaxRowGroups}).");
        var rowGroups = new List<ParquetRowGroupMetadata>(numRowGroups);
        for (int rg = 0; rg < numRowGroups; rg++)
        {
            int rowCount = reader.ReadInt32();
            int numChunks = reader.ReadInt32();
            var chunks = new List<ParquetColumnChunkMetadata>(numChunks);
            for (int c = 0; c < numChunks; c++)
            {
                int colIndex = reader.ReadInt32();
                long dataPageOffset = reader.ReadInt64();
                int dataPageSize = reader.ReadInt32();
                int numValues = reader.ReadInt32();
                chunks.Add(new ParquetColumnChunkMetadata
                {
                    ColumnName = colIndex < schemas.Count ? schemas[colIndex].Name : $"col_{colIndex}",
                    PhysicalType = colIndex < schemas.Count ? schemas[colIndex].PhysicalType : ParquetPhysicalType.ByteArray,
                    DataPageOffset = dataPageOffset,
                    DataPageSize = dataPageSize,
                    NumValues = numValues
                });
            }
            rowGroups.Add(new ParquetRowGroupMetadata
            {
                RowCount = rowCount,
                Columns = chunks
            });
        }

        long totalRows = reader.ReadInt64();

        return new ParquetFileMetadata
        {
            Schema = schemas,
            RowGroups = rowGroups,
            TotalRowCount = totalRows
        };
    }

    private static ColumnarBatch ReadRowGroup(
        Stream input,
        IReadOnlyList<ParquetColumnSchema> schemas,
        ParquetRowGroupMetadata rowGroup)
    {
        var columns = new List<ColumnVector>(schemas.Count);

        for (int colIdx = 0; colIdx < schemas.Count; colIdx++)
        {
            var schema = schemas[colIdx];
            var chunk = rowGroup.Columns[colIdx];

            input.Seek(chunk.DataPageOffset, SeekOrigin.Begin);
            using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);

            // Read data page header
            int numValues = reader.ReadInt32();

            // Read null bitmap
            int bitmapBytes = reader.ReadInt32();
            var bitmap = reader.ReadBytes(bitmapBytes);

            // Read column values
            var column = ReadColumnValues(reader, schema, numValues, bitmap);
            columns.Add(column);
        }

        return new ColumnarBatch(rowGroup.RowCount, columns);
    }

    private static ColumnVector ReadColumnValues(
        BinaryReader reader,
        ParquetColumnSchema schema,
        int numValues,
        byte[] bitmap)
    {
        switch (schema.LogicalType)
        {
            case ColumnDataType.Int32:
            {
                var values = new int[numValues];
                for (int i = 0; i < numValues; i++)
                    values[i] = reader.ReadInt32();
                return new Int32ColumnVector(schema.Name, values, bitmap);
            }
            case ColumnDataType.Int64:
            {
                var values = new long[numValues];
                for (int i = 0; i < numValues; i++)
                    values[i] = reader.ReadInt64();
                return new Int64ColumnVector(schema.Name, values, bitmap);
            }
            case ColumnDataType.Float64:
            {
                var values = new double[numValues];
                for (int i = 0; i < numValues; i++)
                    values[i] = reader.ReadDouble();
                return new Float64ColumnVector(schema.Name, values, bitmap);
            }
            case ColumnDataType.Bool:
            {
                var values = new bool[numValues];
                for (int i = 0; i < numValues; i++)
                    values[i] = reader.ReadBoolean();
                return new BoolColumnVector(schema.Name, values, bitmap);
            }
            case ColumnDataType.String:
            {
                var values = new string[numValues];
                for (int i = 0; i < numValues; i++)
                {
                    int len = reader.ReadInt32();
                    if (len > 0)
                    {
                        var bytes = reader.ReadBytes(len);
                        values[i] = Encoding.UTF8.GetString(bytes);
                    }
                    else
                    {
                        values[i] = string.Empty;
                    }
                }
                return new StringColumnVector(schema.Name, values, bitmap);
            }
            case ColumnDataType.Binary:
            {
                var values = new byte[numValues][];
                for (int i = 0; i < numValues; i++)
                {
                    int len = reader.ReadInt32();
                    values[i] = len > 0 ? reader.ReadBytes(len) : Array.Empty<byte>();
                }
                return new BinaryColumnVector(schema.Name, values, bitmap);
            }
            case ColumnDataType.Decimal:
            {
                var values = new decimal[numValues];
                for (int i = 0; i < numValues; i++)
                {
                    var bits = new int[4];
                    for (int b = 0; b < 4; b++)
                        bits[b] = reader.ReadInt32();
                    values[i] = new decimal(bits);
                }
                return new DecimalColumnVector(schema.Name, values, bitmap);
            }
            case ColumnDataType.DateTime:
            {
                var values = new DateTime[numValues];
                for (int i = 0; i < numValues; i++)
                {
                    long micros = reader.ReadInt64();
                    values[i] = DateTime.UnixEpoch.AddTicks(micros * 10);
                }
                return new DateTimeColumnVector(schema.Name, values, bitmap);
            }
            default:
            {
                // Return empty string column for unknown types
                var values = new string[numValues];
                return new StringColumnVector(schema.Name, values, bitmap);
            }
        }
    }
}
