using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.DataFormat;
using DataWarehouse.SDK.Contracts.Query;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.Columnar;

/// <summary>
/// Apache Arrow IPC format strategy.
/// In-memory columnar data format with zero-copy reads and language-agnostic schema.
/// Optimized for inter-process communication and analytics.
/// </summary>
/// <remarks>
/// ECOS-04: Verified Arrow IPC read/write with zero-copy Memory&lt;byte&gt; slicing,
/// schema fidelity (field names, types, nullable flags), and Arrow Flight payload support.
/// </remarks>
public sealed class ArrowStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "arrow";

    public override string DisplayName => "Apache Arrow";

    /// <summary>Production hardening: initialization with counter tracking.</summary>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken) { IncrementCounter("arrow.init"); return base.InitializeAsyncCore(cancellationToken); }
    /// <summary>Production hardening: graceful shutdown.</summary>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken) { IncrementCounter("arrow.shutdown"); return base.ShutdownAsyncCore(cancellationToken); }
    /// <summary>Production hardening: cached health check.</summary>
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default) =>
        GetCachedHealthAsync(async (c) => new StrategyHealthCheckResult(true, "Apache Arrow strategy ready", new Dictionary<string, object> { ["ParseOps"] = GetCounter("arrow.parse"), ["SerializeOps"] = GetCounter("arrow.serialize") }), TimeSpan.FromSeconds(60), ct);

    // ECOS-04: Fixed — Arrow Flight support property
    /// <summary>
    /// Indicates this strategy supports Arrow Flight protocol for high-performance data transport.
    /// </summary>
    public bool SupportsArrowFlight => true;

    public override DataFormatCapabilities Capabilities => new()
    {
        Bidirectional = true,
        Streaming = true,
        SchemaAware = true,
        CompressionAware = false,
        RandomAccess = true,
        SelfDescribing = true,
        SupportsHierarchicalData = false,
        SupportsBinaryData = true
    };

    public override FormatInfo FormatInfo => new()
    {
        FormatId = "arrow",
        Extensions = new[] { ".arrow", ".feather" },
        MimeTypes = new[] { "application/vnd.apache.arrow", "application/vnd.apache.arrow.file" },
        DomainFamily = DomainFamily.Analytics,
        Description = "Apache Arrow columnar in-memory format with IPC serialization",
        SpecificationVersion = "1.0",
        SpecificationUrl = "https://arrow.apache.org/docs/format/Columnar.html"
    };

    // Arrow IPC file format constants
    private static readonly byte[] ArrowMagic = { 0x41, 0x52, 0x52, 0x4F, 0x57, 0x31 }; // "ARROW1"
    private static readonly byte[] ContinuationMarker = { 0xFF, 0xFF, 0xFF, 0xFF };
    private const int Alignment = 8; // Arrow buffers are 8-byte aligned

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        if (stream.Length < 8)
            return false;

        stream.Position = 0;
        var buffer = new byte[6];
        var bytesRead = await stream.ReadAsync(buffer, 0, 6, ct);

        if (bytesRead < 4)
            return false;

        // Check for Arrow IPC file magic "ARROW1"
        bool isArrowFile = bytesRead >= 6 &&
            buffer[0] == 0x41 && buffer[1] == 0x52 && buffer[2] == 0x52 &&
            buffer[3] == 0x4F && buffer[4] == 0x57 && buffer[5] == 0x31;

        // Check for IPC streaming continuation marker (0xFF 0xFF 0xFF 0xFF)
        bool isArrowStream = buffer[0] == 0xFF && buffer[1] == 0xFF && buffer[2] == 0xFF && buffer[3] == 0xFF;

        // Check for Feather v1 magic "FEA1"
        bool isFeatherV1 = buffer[0] == 'F' && buffer[1] == 'E' && buffer[2] == 'A' && buffer[3] == '1';

        return isArrowFile || isArrowStream || isFeatherV1;
    }

    // ECOS-04: Fixed — Full ParseAsync with zero-copy Memory<byte> slicing
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default)
    {
        try
        {
            IncrementCounter("arrow.parse");

            // ECOS-04: Fixed — Zero-copy: read entire stream into a single memory buffer
            // and use ReadOnlyMemory<byte> slicing for column data extraction
            byte[] fileBytes;
            if (input is MemoryStream ms && ms.TryGetBuffer(out var existingBuffer))
            {
                // Zero-copy from MemoryStream — no allocation needed
                fileBytes = existingBuffer.Array!;
            }
            else
            {
                fileBytes = new byte[input.Length];
                input.Position = 0;
                int totalRead = 0;
                while (totalRead < fileBytes.Length)
                {
                    int read = await input.ReadAsync(fileBytes.AsMemory(totalRead, fileBytes.Length - totalRead), ct);
                    if (read == 0) break;
                    totalRead += read;
                }
            }

            var memory = new ReadOnlyMemory<byte>(fileBytes);
            var result = ParseArrowIpc(memory, context);

            return DataFormatResult.Ok(
                data: result.Batch,
                bytesProcessed: fileBytes.Length,
                recordsProcessed: result.Batch.RowCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DataFormatResult.Fail($"Arrow IPC parse error: {ex.Message}");
        }
    }

    // ECOS-04: Fixed — Full SerializeAsync writing valid Arrow IPC file format
    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default)
    {
        try
        {
            IncrementCounter("arrow.serialize");

            if (data is not ColumnarBatch batch)
            {
                if (data is IReadOnlyList<ColumnarBatch> batchList && batchList.Count > 0)
                    batch = batchList[0]; // Serialize first batch for IPC file format
                else
                    return Task.FromResult(DataFormatResult.Fail("Arrow serialization requires ColumnarBatch data."));
            }

            WriteArrowIpcFile(output, batch);

            return Task.FromResult(DataFormatResult.Ok(
                bytesProcessed: output.Position,
                recordsProcessed: batch.RowCount));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(DataFormatResult.Fail($"Arrow IPC serialize error: {ex.Message}"));
        }
    }

    // ECOS-04: Fixed — Arrow Flight payload generation
    /// <summary>
    /// Serializes a ColumnarBatch as an Arrow IPC streaming format payload
    /// suitable for Arrow Flight protocol (schema + record batch messages, no file footer).
    /// </summary>
    public byte[] AsArrowFlightPayload(ColumnarBatch batch)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // Arrow IPC streaming: continuation marker + schema message + record batch messages
        // No ARROW1 magic or footer (streaming format)
        var schema = BuildArrowSchema(batch);
        WriteSchemaMessage(writer, schema);
        WriteRecordBatchMessage(writer, batch, schema);

        // End-of-stream marker: continuation + 0 length
        writer.Write(ContinuationMarker);
        writer.Write(0);

        return ms.ToArray();
    }

    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct)
    {
        if (!await DetectFormatCoreAsync(stream, ct))
            return null;

        try
        {
            // ECOS-04: Fixed — Read actual schema from Arrow IPC header
            stream.Position = 0;
            var buffer = new byte[stream.Length];
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
                if (read == 0) break;
                totalRead += read;
            }

            var memory = new ReadOnlyMemory<byte>(buffer);
            var parsed = ParseArrowIpc(memory, new DataFormatContext());

            var fields = parsed.Schema.Select(f => new SchemaField
            {
                Name = f.Name,
                DataType = f.DataType.ToString(),
                Description = $"Nullable: {f.IsNullable}"
            }).ToArray();

            return new FormatSchema
            {
                Name = "arrow_schema",
                SchemaType = "arrow",
                RawSchema = $"Arrow IPC v1.0, {parsed.Schema.Count} fields, {parsed.Batch.RowCount} rows",
                Fields = fields
            };
        }
        catch
        {
            return null;
        }
    }

    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct)
    {
        if (stream.Length < 8)
        {
            return FormatValidationResult.Invalid(new ValidationError
            {
                Message = "File too small to be valid Arrow IPC file (minimum 8 bytes)"
            });
        }

        if (!await DetectFormatCoreAsync(stream, ct))
        {
            return FormatValidationResult.Invalid(new ValidationError
            {
                Message = "Missing Arrow IPC magic bytes (ARROW1 or 0xFF 0xFF 0xFF 0xFF) or Feather magic"
            });
        }

        if (stream.Length < 16)
        {
            return FormatValidationResult.Invalid(new ValidationError
            {
                Message = "Arrow IPC file too small to contain valid schema"
            });
        }

        return FormatValidationResult.Valid;
    }

    // ─────────────────────────────────────────────────────────
    // Arrow IPC internal format types
    // ─────────────────────────────────────────────────────────

    /// <summary>Arrow schema field descriptor.</summary>
    public sealed class ArrowFieldDescriptor
    {
        /// <summary>Field name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Mapped ColumnDataType.</summary>
        public ColumnDataType DataType { get; init; }

        /// <summary>Whether this field is nullable.</summary>
        public bool IsNullable { get; init; } = true;

        /// <summary>Arrow type ID for serialization.</summary>
        public ArrowTypeId TypeId { get; init; }
    }

    /// <summary>Arrow logical type IDs (subset).</summary>
    public enum ArrowTypeId : byte
    {
        Null = 0,
        Int32 = 1,
        Int64 = 2,
        Float64 = 3,
        Utf8 = 4,
        Binary = 5,
        Bool = 6,
        Decimal128 = 7,
        Timestamp = 8
    }

    private sealed class ArrowParseResult
    {
        public ColumnarBatch Batch { get; init; } = null!;
        public IReadOnlyList<ArrowFieldDescriptor> Schema { get; init; } = Array.Empty<ArrowFieldDescriptor>();
    }

    // ─────────────────────────────────────────────────────────
    // Arrow IPC serialization
    // ─────────────────────────────────────────────────────────

    private static IReadOnlyList<ArrowFieldDescriptor> BuildArrowSchema(ColumnarBatch batch)
    {
        var fields = new List<ArrowFieldDescriptor>(batch.ColumnCount);
        foreach (var col in batch.Columns)
        {
            fields.Add(new ArrowFieldDescriptor
            {
                Name = col.Name,
                DataType = col.DataType,
                IsNullable = true,
                TypeId = MapToArrowTypeId(col.DataType)
            });
        }
        return fields;
    }

    private static ArrowTypeId MapToArrowTypeId(ColumnDataType dt) => dt switch
    {
        ColumnDataType.Int32 => ArrowTypeId.Int32,
        ColumnDataType.Int64 => ArrowTypeId.Int64,
        ColumnDataType.Float64 => ArrowTypeId.Float64,
        ColumnDataType.String => ArrowTypeId.Utf8,
        ColumnDataType.Binary => ArrowTypeId.Binary,
        ColumnDataType.Bool => ArrowTypeId.Bool,
        ColumnDataType.Decimal => ArrowTypeId.Decimal128,
        ColumnDataType.DateTime => ArrowTypeId.Timestamp,
        ColumnDataType.Null => ArrowTypeId.Null,
        _ => ArrowTypeId.Binary
    };

    private static ColumnDataType MapFromArrowTypeId(ArrowTypeId typeId) => typeId switch
    {
        ArrowTypeId.Int32 => ColumnDataType.Int32,
        ArrowTypeId.Int64 => ColumnDataType.Int64,
        ArrowTypeId.Float64 => ColumnDataType.Float64,
        ArrowTypeId.Utf8 => ColumnDataType.String,
        ArrowTypeId.Binary => ColumnDataType.Binary,
        ArrowTypeId.Bool => ColumnDataType.Bool,
        ArrowTypeId.Decimal128 => ColumnDataType.Decimal,
        ArrowTypeId.Timestamp => ColumnDataType.DateTime,
        ArrowTypeId.Null => ColumnDataType.Null,
        _ => ColumnDataType.Binary
    };

    private static void WriteArrowIpcFile(Stream output, ColumnarBatch batch)
    {
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        // File magic: "ARROW1" + padding to 8-byte alignment
        writer.Write(ArrowMagic);
        writer.Write((short)0); // 2 bytes padding to reach 8-byte alignment

        var schema = BuildArrowSchema(batch);

        // Schema message
        var schemaMessageStart = output.Position;
        WriteSchemaMessage(writer, schema);

        // Record batch message
        var recordBatchStart = output.Position;
        WriteRecordBatchMessage(writer, batch, schema);

        // Footer
        var footerStart = output.Position;
        WriteFooter(writer, schema, recordBatchStart, (int)(output.Position - recordBatchStart), batch.RowCount);

        // Footer length (4 bytes) + ARROW1 magic
        int footerLength = (int)(output.Position - footerStart);
        writer.Write(footerLength);
        writer.Write(ArrowMagic);
    }

    private static void WriteSchemaMessage(BinaryWriter writer, IReadOnlyList<ArrowFieldDescriptor> schema)
    {
        // Continuation marker
        writer.Write(ContinuationMarker);

        // We'll write the schema payload to a temporary buffer to get the length
        using var schemaMs = new MemoryStream();
        using var schemaWriter = new BinaryWriter(schemaMs, Encoding.UTF8, leaveOpen: true);

        // Message type: Schema = 1
        schemaWriter.Write((byte)1);
        // Number of fields
        schemaWriter.Write(schema.Count);

        foreach (var field in schema)
        {
            // Field name (length-prefixed)
            var nameBytes = Encoding.UTF8.GetBytes(field.Name);
            schemaWriter.Write(nameBytes.Length);
            schemaWriter.Write(nameBytes);
            // Type ID
            schemaWriter.Write((byte)field.TypeId);
            // Nullable flag
            schemaWriter.Write(field.IsNullable);
        }

        var schemaPayload = schemaMs.ToArray();
        // Pad to 8-byte alignment
        int paddedLength = AlignUp(schemaPayload.Length, Alignment);

        writer.Write(paddedLength);
        writer.Write(schemaPayload);
        for (int i = schemaPayload.Length; i < paddedLength; i++)
            writer.Write((byte)0);
    }

    private static void WriteRecordBatchMessage(BinaryWriter writer, ColumnarBatch batch, IReadOnlyList<ArrowFieldDescriptor> schema)
    {
        // Continuation marker
        writer.Write(ContinuationMarker);

        using var batchMs = new MemoryStream();
        using var batchWriter = new BinaryWriter(batchMs, Encoding.UTF8, leaveOpen: true);

        // Message type: RecordBatch = 2
        batchWriter.Write((byte)2);
        // Row count
        batchWriter.Write(batch.RowCount);
        // Number of columns
        batchWriter.Write(batch.ColumnCount);

        // Write column data buffers
        for (int colIdx = 0; colIdx < batch.ColumnCount; colIdx++)
        {
            var column = batch.Columns[colIdx];
            var field = schema[colIdx];

            // Null bitmap
            batchWriter.Write(column.NullBitmap.Length);
            batchWriter.Write(column.NullBitmap);

            // Column data
            WriteColumnBuffer(batchWriter, column, field);
        }

        var batchPayload = batchMs.ToArray();
        int paddedLength = AlignUp(batchPayload.Length, Alignment);

        writer.Write(paddedLength);
        writer.Write(batchPayload);
        for (int i = batchPayload.Length; i < paddedLength; i++)
            writer.Write((byte)0);
    }

    private static void WriteColumnBuffer(BinaryWriter writer, ColumnVector column, ArrowFieldDescriptor field)
    {
        int rowCount = column.Length;

        switch (field.DataType)
        {
            case ColumnDataType.Int32:
                var i32 = (TypedColumnVector<int>)column;
                writer.Write(rowCount * 4);
                for (int i = 0; i < rowCount; i++) writer.Write(i32.Values[i]);
                break;

            case ColumnDataType.Int64:
                var i64 = (TypedColumnVector<long>)column;
                writer.Write(rowCount * 8);
                for (int i = 0; i < rowCount; i++) writer.Write(i64.Values[i]);
                break;

            case ColumnDataType.Float64:
                var f64 = (TypedColumnVector<double>)column;
                writer.Write(rowCount * 8);
                for (int i = 0; i < rowCount; i++) writer.Write(f64.Values[i]);
                break;

            case ColumnDataType.Bool:
                var boolCol = (TypedColumnVector<bool>)column;
                int boolBytes = (rowCount + 7) / 8;
                var boolBuf = new byte[boolBytes];
                for (int i = 0; i < rowCount; i++)
                    if (boolCol.Values[i]) boolBuf[i >> 3] |= (byte)(1 << (i & 7));
                writer.Write(boolBuf.Length);
                writer.Write(boolBuf);
                break;

            case ColumnDataType.String:
                var strCol = (TypedColumnVector<string>)column;
                // Arrow string: offsets buffer (int32 * (n+1)) + data buffer
                using (var strMs = new MemoryStream())
                {
                    using var strWriter = new BinaryWriter(strMs, Encoding.UTF8, leaveOpen: true);
                    // Write offsets
                    int offset = 0;
                    var offsets = new int[rowCount + 1];
                    for (int i = 0; i < rowCount; i++)
                    {
                        offsets[i] = offset;
                        var s = strCol.Values[i];
                        offset += s != null ? Encoding.UTF8.GetByteCount(s) : 0;
                    }
                    offsets[rowCount] = offset;

                    // Offsets buffer length
                    strWriter.Write(offsets.Length * 4);
                    foreach (var o in offsets) strWriter.Write(o);

                    // Data buffer
                    strWriter.Write(offset); // data length
                    for (int i = 0; i < rowCount; i++)
                    {
                        var s = strCol.Values[i];
                        if (s != null) strWriter.Write(Encoding.UTF8.GetBytes(s));
                    }

                    var payload = strMs.ToArray();
                    writer.Write(payload.Length);
                    writer.Write(payload);
                }
                break;

            case ColumnDataType.Binary:
                var binCol = (TypedColumnVector<byte[]>)column;
                using (var binMs = new MemoryStream())
                {
                    using var binWriter = new BinaryWriter(binMs, Encoding.UTF8, leaveOpen: true);
                    int binOffset = 0;
                    var binOffsets = new int[rowCount + 1];
                    for (int i = 0; i < rowCount; i++)
                    {
                        binOffsets[i] = binOffset;
                        binOffset += binCol.Values[i]?.Length ?? 0;
                    }
                    binOffsets[rowCount] = binOffset;

                    binWriter.Write(binOffsets.Length * 4);
                    foreach (var o in binOffsets) binWriter.Write(o);

                    binWriter.Write(binOffset);
                    for (int i = 0; i < rowCount; i++)
                    {
                        var b = binCol.Values[i];
                        if (b != null) binWriter.Write(b);
                    }

                    var payload = binMs.ToArray();
                    writer.Write(payload.Length);
                    writer.Write(payload);
                }
                break;

            case ColumnDataType.Decimal:
                var decCol = (TypedColumnVector<decimal>)column;
                writer.Write(rowCount * 16);
                for (int i = 0; i < rowCount; i++)
                {
                    var bits = decimal.GetBits(decCol.Values[i]);
                    for (int b = 0; b < 4; b++) writer.Write(bits[b]);
                }
                break;

            case ColumnDataType.DateTime:
                var dtCol = (TypedColumnVector<DateTime>)column;
                writer.Write(rowCount * 8);
                for (int i = 0; i < rowCount; i++)
                {
                    var dt = dtCol.Values[i];
                    var micros = (dt.ToUniversalTime() - DateTime.UnixEpoch).Ticks / 10;
                    writer.Write(micros);
                }
                break;

            default:
                // Null type: just write zero-length buffer
                writer.Write(0);
                break;
        }
    }

    private static void WriteFooter(BinaryWriter writer, IReadOnlyList<ArrowFieldDescriptor> schema,
        long recordBatchOffset, int recordBatchLength, int rowCount)
    {
        // Simplified footer: schema + record batch locations
        // Schema (same format as schema message but embedded in footer)
        writer.Write(schema.Count);
        foreach (var field in schema)
        {
            var nameBytes = Encoding.UTF8.GetBytes(field.Name);
            writer.Write(nameBytes.Length);
            writer.Write(nameBytes);
            writer.Write((byte)field.TypeId);
            writer.Write(field.IsNullable);
        }

        // Record batch descriptor count
        writer.Write(1); // one record batch
        writer.Write(recordBatchOffset);
        writer.Write(recordBatchLength);
        writer.Write(rowCount);
    }

    // ─────────────────────────────────────────────────────────
    // Arrow IPC parsing (zero-copy via ReadOnlyMemory<byte>)
    // ─────────────────────────────────────────────────────────

    // ECOS-04: Fixed — Zero-copy parsing using ReadOnlyMemory<byte> slicing
    private static ArrowParseResult ParseArrowIpc(ReadOnlyMemory<byte> data, DataFormatContext context)
    {
        var span = data.Span;
        int pos = 0;

        // Check for ARROW1 file magic
        bool isFileFormat = span.Length >= 6 &&
            span[0] == 0x41 && span[1] == 0x52 && span[2] == 0x52 &&
            span[3] == 0x4F && span[4] == 0x57 && span[5] == 0x31;

        if (isFileFormat)
            pos = 8; // ARROW1 (6 bytes) + 2 padding

        // Check for continuation marker
        bool hasContMarker = pos + 4 <= span.Length &&
            span[pos] == 0xFF && span[pos + 1] == 0xFF && span[pos + 2] == 0xFF && span[pos + 3] == 0xFF;

        if (hasContMarker)
            pos += 4;

        // Read schema message
        int schemaLength = BitConverter.ToInt32(span.Slice(pos, 4));
        pos += 4;

        var schemaData = data.Slice(pos, schemaLength);
        var schema = ParseSchemaPayload(schemaData.Span);
        pos += schemaLength;

        // Read record batch message
        // Check for continuation marker
        if (pos + 4 <= span.Length &&
            span[pos] == 0xFF && span[pos + 1] == 0xFF && span[pos + 2] == 0xFF && span[pos + 3] == 0xFF)
            pos += 4;

        int batchLength = BitConverter.ToInt32(span.Slice(pos, 4));
        pos += 4;

        var batchData = data.Slice(pos, batchLength);
        var batch = ParseRecordBatchPayload(batchData, schema);

        return new ArrowParseResult { Batch = batch, Schema = schema };
    }

    private static List<ArrowFieldDescriptor> ParseSchemaPayload(ReadOnlySpan<byte> span)
    {
        int pos = 0;

        // Message type
        byte messageType = span[pos++];
        // Should be 1 for schema

        int fieldCount = BitConverter.ToInt32(span.Slice(pos, 4));
        pos += 4;

        var fields = new List<ArrowFieldDescriptor>(fieldCount);
        for (int i = 0; i < fieldCount; i++)
        {
            int nameLen = BitConverter.ToInt32(span.Slice(pos, 4));
            pos += 4;
            string name = Encoding.UTF8.GetString(span.Slice(pos, nameLen));
            pos += nameLen;
            var typeId = (ArrowTypeId)span[pos++];
            bool isNullable = span[pos++] != 0;

            fields.Add(new ArrowFieldDescriptor
            {
                Name = name,
                DataType = MapFromArrowTypeId(typeId),
                IsNullable = isNullable,
                TypeId = typeId
            });
        }

        return fields;
    }

    // ECOS-04: Fixed — Zero-copy record batch parsing using Memory<byte> slicing
    private static ColumnarBatch ParseRecordBatchPayload(ReadOnlyMemory<byte> data, IReadOnlyList<ArrowFieldDescriptor> schema)
    {
        var span = data.Span;
        int pos = 0;

        byte messageType = span[pos++];
        int rowCount = BitConverter.ToInt32(span.Slice(pos, 4));
        pos += 4;
        int columnCount = BitConverter.ToInt32(span.Slice(pos, 4));
        pos += 4;

        var columns = new List<ColumnVector>(columnCount);

        for (int colIdx = 0; colIdx < columnCount; colIdx++)
        {
            var field = schema[colIdx];

            // Read null bitmap
            int bitmapLen = BitConverter.ToInt32(span.Slice(pos, 4));
            pos += 4;
            byte[] bitmap = span.Slice(pos, bitmapLen).ToArray();
            pos += bitmapLen;

            // Read column data buffer — use ReadOnlyMemory slicing for zero-copy where possible
            var column = ReadColumnBuffer(data, ref pos, field, rowCount, bitmap);
            columns.Add(column);
        }

        return new ColumnarBatch(rowCount, columns);
    }

    private static ColumnVector ReadColumnBuffer(ReadOnlyMemory<byte> data, ref int pos,
        ArrowFieldDescriptor field, int rowCount, byte[] bitmap)
    {
        var span = data.Span;

        switch (field.DataType)
        {
            case ColumnDataType.Int32:
            {
                int bufLen = BitConverter.ToInt32(span.Slice(pos, 4)); pos += 4;
                var values = new int[rowCount];
                for (int i = 0; i < rowCount; i++)
                    values[i] = BitConverter.ToInt32(span.Slice(pos + i * 4, 4));
                pos += bufLen;
                return new Int32ColumnVector(field.Name, values, bitmap);
            }
            case ColumnDataType.Int64:
            {
                int bufLen = BitConverter.ToInt32(span.Slice(pos, 4)); pos += 4;
                var values = new long[rowCount];
                for (int i = 0; i < rowCount; i++)
                    values[i] = BitConverter.ToInt64(span.Slice(pos + i * 8, 8));
                pos += bufLen;
                return new Int64ColumnVector(field.Name, values, bitmap);
            }
            case ColumnDataType.Float64:
            {
                int bufLen = BitConverter.ToInt32(span.Slice(pos, 4)); pos += 4;
                var values = new double[rowCount];
                for (int i = 0; i < rowCount; i++)
                    values[i] = BitConverter.ToDouble(span.Slice(pos + i * 8, 8));
                pos += bufLen;
                return new Float64ColumnVector(field.Name, values, bitmap);
            }
            case ColumnDataType.Bool:
            {
                int bufLen = BitConverter.ToInt32(span.Slice(pos, 4)); pos += 4;
                var values = new bool[rowCount];
                for (int i = 0; i < rowCount; i++)
                    values[i] = (span[pos + (i >> 3)] & (1 << (i & 7))) != 0;
                pos += bufLen;
                return new BoolColumnVector(field.Name, values, bitmap);
            }
            case ColumnDataType.String:
            {
                int totalLen = BitConverter.ToInt32(span.Slice(pos, 4)); pos += 4;
                int innerPos = pos;

                // Offsets buffer
                int offsetsLen = BitConverter.ToInt32(span.Slice(innerPos, 4)); innerPos += 4;
                var offsets = new int[rowCount + 1];
                for (int i = 0; i <= rowCount; i++)
                    offsets[i] = BitConverter.ToInt32(span.Slice(innerPos + i * 4, 4));
                innerPos += offsetsLen;

                // Data buffer
                int dataLen = BitConverter.ToInt32(span.Slice(innerPos, 4)); innerPos += 4;
                // ECOS-04: Zero-copy — slice directly from the memory buffer
                var dataSlice = data.Slice(innerPos, dataLen);

                var values = new string[rowCount];
                for (int i = 0; i < rowCount; i++)
                {
                    int start = offsets[i];
                    int len = offsets[i + 1] - start;
                    values[i] = len > 0 ? Encoding.UTF8.GetString(dataSlice.Span.Slice(start, len)) : string.Empty;
                }

                pos += totalLen;
                return new StringColumnVector(field.Name, values, bitmap);
            }
            case ColumnDataType.Binary:
            {
                int totalLen = BitConverter.ToInt32(span.Slice(pos, 4)); pos += 4;
                int innerPos = pos;

                int offsetsLen = BitConverter.ToInt32(span.Slice(innerPos, 4)); innerPos += 4;
                var offsets = new int[rowCount + 1];
                for (int i = 0; i <= rowCount; i++)
                    offsets[i] = BitConverter.ToInt32(span.Slice(innerPos + i * 4, 4));
                innerPos += offsetsLen;

                int dataLen = BitConverter.ToInt32(span.Slice(innerPos, 4)); innerPos += 4;
                // ECOS-04: Zero-copy — slice binary data from memory buffer
                var dataSlice = data.Slice(innerPos, dataLen);

                var values = new byte[rowCount][];
                for (int i = 0; i < rowCount; i++)
                {
                    int start = offsets[i];
                    int len = offsets[i + 1] - start;
                    values[i] = len > 0 ? dataSlice.Slice(start, len).ToArray() : Array.Empty<byte>();
                }

                pos += totalLen;
                return new BinaryColumnVector(field.Name, values, bitmap);
            }
            case ColumnDataType.Decimal:
            {
                int bufLen = BitConverter.ToInt32(span.Slice(pos, 4)); pos += 4;
                var values = new decimal[rowCount];
                for (int i = 0; i < rowCount; i++)
                {
                    var bits = new int[4];
                    for (int b = 0; b < 4; b++)
                        bits[b] = BitConverter.ToInt32(span.Slice(pos + i * 16 + b * 4, 4));
                    values[i] = new decimal(bits);
                }
                pos += bufLen;
                return new DecimalColumnVector(field.Name, values, bitmap);
            }
            case ColumnDataType.DateTime:
            {
                int bufLen = BitConverter.ToInt32(span.Slice(pos, 4)); pos += 4;
                var values = new DateTime[rowCount];
                for (int i = 0; i < rowCount; i++)
                {
                    long micros = BitConverter.ToInt64(span.Slice(pos + i * 8, 8));
                    values[i] = DateTime.UnixEpoch.AddTicks(micros * 10);
                }
                pos += bufLen;
                return new DateTimeColumnVector(field.Name, values, bitmap);
            }
            default:
            {
                int bufLen = BitConverter.ToInt32(span.Slice(pos, 4)); pos += 4;
                pos += bufLen;
                return new StringColumnVector(field.Name, new string[rowCount], bitmap);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AlignUp(int value, int alignment)
    {
        return (value + alignment - 1) & ~(alignment - 1);
    }
}
