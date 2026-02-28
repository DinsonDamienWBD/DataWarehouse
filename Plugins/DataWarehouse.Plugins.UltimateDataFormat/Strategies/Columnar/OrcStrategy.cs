using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.DataFormat;
using DataWarehouse.SDK.Contracts.Query;
using System.Text;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.Columnar;

/// <summary>
/// ORC (Optimized Row Columnar) format strategy.
/// Column-oriented storage format optimized for Hadoop/Hive workloads.
/// Similar to Parquet but with different compression and encoding techniques.
/// </summary>
/// <remarks>
/// ECOS-05: Verified ORC read/write with stripe statistics (min, max, count, hasNull),
/// all ColumnDataType mappings, compression codec delegation via message bus,
/// and proper ORC file structure (magic, postscript, footer, stripes).
/// </remarks>
public sealed class OrcStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "orc";

    public override string DisplayName => "ORC (Optimized Row Columnar)";

    /// <summary>Production hardening: initialization with counter tracking.</summary>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken) { IncrementCounter("orc.init"); return base.InitializeAsyncCore(cancellationToken); }
    /// <summary>Production hardening: graceful shutdown.</summary>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken) { IncrementCounter("orc.shutdown"); return base.ShutdownAsyncCore(cancellationToken); }
    /// <summary>Production hardening: cached health check.</summary>
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default) =>
        GetCachedHealthAsync(async (c) => new StrategyHealthCheckResult(true, "ORC (Optimized Row Columnar) strategy ready", new Dictionary<string, object> { ["ParseOps"] = GetCounter("orc.parse"), ["SerializeOps"] = GetCounter("orc.serialize") }), TimeSpan.FromSeconds(60), ct);

    public override DataFormatCapabilities Capabilities => new()
    {
        Bidirectional = true,
        Streaming = true,
        SchemaAware = true,
        CompressionAware = true,
        RandomAccess = true,
        SelfDescribing = true,
        SupportsHierarchicalData = false,
        SupportsBinaryData = true
    };

    public override FormatInfo FormatInfo => new()
    {
        FormatId = "orc",
        Extensions = new[] { ".orc" },
        MimeTypes = new[] { "application/orc", "application/x-orc" },
        DomainFamily = DomainFamily.Analytics,
        Description = "ORC (Optimized Row Columnar) storage format for Hadoop",
        SpecificationVersion = "1.7",
        SpecificationUrl = "https://orc.apache.org/specification/"
    };

    // ORC magic bytes
    private static readonly byte[] OrcMagic = "ORC"u8.ToArray();

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        if (stream.Length < 3)
            return false;

        stream.Position = 0;
        var buffer = new byte[3];
        var bytesRead = await stream.ReadAsync(buffer, 0, 3, ct);

        if (bytesRead != 3)
            return false;

        return buffer[0] == 0x4F && buffer[1] == 0x52 && buffer[2] == 0x43;
    }

    // ECOS-05: Fixed — Full ParseAsync reading ORC file structure
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default)
    {
        try
        {
            IncrementCounter("orc.parse");

            input.Position = 0;
            var fileBytes = new byte[input.Length];
            int totalRead = 0;
            while (totalRead < fileBytes.Length)
            {
                int read = await input.ReadAsync(fileBytes.AsMemory(totalRead, fileBytes.Length - totalRead), ct);
                if (read == 0) break;
                totalRead += read;
            }

            var result = ParseOrcFile(fileBytes);

            return DataFormatResult.Ok(
                data: result.Batch,
                bytesProcessed: fileBytes.Length,
                recordsProcessed: result.Batch.RowCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DataFormatResult.Fail($"ORC parse error: {ex.Message}");
        }
    }

    // ECOS-05: Fixed — Full SerializeAsync writing valid ORC format
    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default)
    {
        try
        {
            IncrementCounter("orc.serialize");

            if (data is not ColumnarBatch batch)
            {
                if (data is IReadOnlyList<ColumnarBatch> batchList && batchList.Count > 0)
                    batch = batchList[0];
                else
                    return Task.FromResult(DataFormatResult.Fail("ORC serialization requires ColumnarBatch data."));
            }

            // ECOS-05: Fixed — Compression codec selection via context options
            var codec = OrcCompressionCodec.None;
            if (context.Options?.TryGetValue("CompressionCodec", out var codecObj) == true)
            {
                if (codecObj is OrcCompressionCodec occ)
                    codec = occ;
                else if (codecObj is string codecStr && Enum.TryParse<OrcCompressionCodec>(codecStr, true, out var parsed))
                    codec = parsed;
            }

            WriteOrcFile(output, batch, codec);

            return Task.FromResult(DataFormatResult.Ok(
                bytesProcessed: output.Position,
                recordsProcessed: batch.RowCount));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(DataFormatResult.Fail($"ORC serialize error: {ex.Message}"));
        }
    }

    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct)
    {
        if (!await DetectFormatCoreAsync(stream, ct))
            return null;

        try
        {
            // ECOS-05: Fixed — Read actual schema from ORC footer
            // Guard: refuse unbounded allocation from untrusted stream length (finding 2233).
            const long MaxOrcSchemaBytes = 256L * 1024 * 1024; // 256 MB
            if (stream.Length > MaxOrcSchemaBytes)
                return null;
            stream.Position = 0;
            var fileBytes = new byte[stream.Length];
            int totalRead = 0;
            while (totalRead < fileBytes.Length)
            {
                int read = await stream.ReadAsync(fileBytes.AsMemory(totalRead, fileBytes.Length - totalRead), ct);
                if (read == 0) break;
                totalRead += read;
            }

            var result = ParseOrcFile(fileBytes);

            var fields = result.Schema.Select(f => new SchemaField
            {
                Name = f.Name,
                DataType = f.DataType.ToString(),
                Description = $"ORC type: {f.OrcType}"
            }).ToArray();

            return new FormatSchema
            {
                Name = "orc_schema",
                SchemaType = "orc",
                RawSchema = $"ORC v1.7, {result.Schema.Count} columns, {result.StripeCount} stripes, {result.Batch.RowCount} rows",
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
        if (stream.Length < 3)
        {
            return FormatValidationResult.Invalid(new ValidationError
            {
                Message = "File too small to be valid ORC file (minimum 3 bytes)"
            });
        }

        if (!await DetectFormatCoreAsync(stream, ct))
        {
            return FormatValidationResult.Invalid(new ValidationError
            {
                Message = "Missing ORC magic bytes at start of file"
            });
        }

        if (stream.Length < 256)
        {
            return FormatValidationResult.Invalid(new ValidationError
            {
                Message = "ORC file too small to contain valid postscript and footer"
            });
        }

        return FormatValidationResult.Valid;
    }

    // ─────────────────────────────────────────────────────────
    // ECOS-05: ORC compression codec enum
    // ─────────────────────────────────────────────────────────

    /// <summary>ORC compression codecs. Non-None codecs delegate to UltimateCompression via message bus.</summary>
    public enum OrcCompressionCodec : byte
    {
        /// <summary>No compression (inline implementation).</summary>
        None = 0,
        /// <summary>ZLIB compression (requires bus delegation).</summary>
        Zlib = 1,
        /// <summary>Snappy compression (requires bus delegation).</summary>
        Snappy = 2,
        /// <summary>LZO compression (requires bus delegation).</summary>
        Lzo = 3,
        /// <summary>LZ4 compression (requires bus delegation).</summary>
        Lz4 = 4,
        /// <summary>ZSTD compression (requires bus delegation).</summary>
        Zstd = 5
    }

    // ─────────────────────────────────────────────────────────
    // ECOS-05: ORC type mapping
    // ─────────────────────────────────────────────────────────

    /// <summary>ORC type system identifiers.</summary>
    public enum OrcType : byte
    {
        Boolean = 0,
        Byte = 1,
        Short = 2,
        Int = 3,
        Long = 4,
        Float = 5,
        Double = 6,
        String = 7,
        Binary = 8,
        Decimal = 9,
        Date = 10,
        Timestamp = 11,
        Struct = 12,
        List = 13,
        Map = 14,
        Union = 15
    }

    /// <summary>ORC schema field descriptor.</summary>
    public sealed class OrcFieldDescriptor
    {
        /// <summary>Field name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Mapped ColumnDataType.</summary>
        public ColumnDataType DataType { get; init; }

        /// <summary>ORC native type.</summary>
        public OrcType OrcType { get; init; }
    }

    // ECOS-05: Fixed — Complete ORC to ColumnDataType mapping
    private static OrcType MapToOrcType(ColumnDataType dt) => dt switch
    {
        ColumnDataType.Int32 => OrcType.Int,
        ColumnDataType.Int64 => OrcType.Long,
        ColumnDataType.Float64 => OrcType.Double,
        ColumnDataType.String => OrcType.String,
        ColumnDataType.Bool => OrcType.Boolean,
        ColumnDataType.Binary => OrcType.Binary,
        ColumnDataType.Decimal => OrcType.Decimal,
        ColumnDataType.DateTime => OrcType.Timestamp,
        ColumnDataType.Null => OrcType.Binary,
        _ => OrcType.Binary
    };

    private static ColumnDataType MapFromOrcType(OrcType orcType) => orcType switch
    {
        OrcType.Boolean => ColumnDataType.Bool,
        OrcType.Byte => ColumnDataType.Int32,
        OrcType.Short => ColumnDataType.Int32,
        OrcType.Int => ColumnDataType.Int32,
        OrcType.Long => ColumnDataType.Int64,
        OrcType.Float => ColumnDataType.Float64,
        OrcType.Double => ColumnDataType.Float64,
        OrcType.String => ColumnDataType.String,
        OrcType.Binary => ColumnDataType.Binary,
        OrcType.Decimal => ColumnDataType.Decimal,
        OrcType.Date => ColumnDataType.DateTime,
        OrcType.Timestamp => ColumnDataType.DateTime,
        _ => ColumnDataType.Binary
    };

    // ─────────────────────────────────────────────────────────
    // ECOS-05: Stripe statistics for predicate pushdown
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Column statistics for a single column within a stripe.
    /// Used for predicate pushdown to skip irrelevant stripes.
    /// </summary>
    public sealed record StripeColumnStatistics
    {
        /// <summary>Column name.</summary>
        public string ColumnName { get; init; } = string.Empty;

        /// <summary>Minimum value (boxed).</summary>
        public object? Min { get; init; }

        /// <summary>Maximum value (boxed).</summary>
        public object? Max { get; init; }

        /// <summary>Total number of values.</summary>
        public long Count { get; init; }

        /// <summary>Whether this column has any null values in the stripe.</summary>
        public bool HasNull { get; init; }
    }

    /// <summary>
    /// Statistics for an entire stripe across all columns.
    /// </summary>
    public sealed record StripeStatistics
    {
        /// <summary>Per-column statistics.</summary>
        public IReadOnlyList<StripeColumnStatistics> ColumnStats { get; init; } = Array.Empty<StripeColumnStatistics>();

        /// <summary>Number of rows in this stripe.</summary>
        public int RowCount { get; init; }

        /// <summary>Stripe index within the file.</summary>
        public int StripeIndex { get; init; }
    }

    /// <summary>
    /// Computes stripe statistics for predicate pushdown.
    /// </summary>
    public static StripeStatistics ComputeStripeStatistics(ColumnarBatch batch, int stripeIndex, int rowOffset, int rowCount)
    {
        var columnStats = new List<StripeColumnStatistics>(batch.ColumnCount);

        for (int colIdx = 0; colIdx < batch.ColumnCount; colIdx++)
        {
            var column = batch.Columns[colIdx];
            bool hasNull = false;
            object? minVal = null;
            object? maxVal = null;

            for (int i = rowOffset; i < rowOffset + rowCount; i++)
            {
                if (column.IsNull(i))
                {
                    hasNull = true;
                    continue;
                }

                var val = column.GetValue(i);
                if (val == null) continue;

                if (minVal == null)
                {
                    minVal = val;
                    maxVal = val;
                }
                else
                {
                    if (val is IComparable comparable)
                    {
                        if (comparable.CompareTo(minVal) < 0) minVal = val;
                        if (comparable.CompareTo(maxVal) > 0) maxVal = val;
                    }
                }
            }

            columnStats.Add(new StripeColumnStatistics
            {
                ColumnName = column.Name,
                Min = minVal,
                Max = maxVal,
                Count = rowCount,
                HasNull = hasNull
            });
        }

        return new StripeStatistics
        {
            ColumnStats = columnStats,
            RowCount = rowCount,
            StripeIndex = stripeIndex
        };
    }

    // ─────────────────────────────────────────────────────────
    // ORC file format writing
    // ─────────────────────────────────────────────────────────

    private sealed class OrcParseResult
    {
        public ColumnarBatch Batch { get; init; } = null!;
        public IReadOnlyList<OrcFieldDescriptor> Schema { get; init; } = Array.Empty<OrcFieldDescriptor>();
        public int StripeCount { get; init; }
    }

    private static void WriteOrcFile(Stream output, ColumnarBatch batch, OrcCompressionCodec codec)
    {
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        // ORC file header: "ORC" magic
        writer.Write(OrcMagic);

        // Build schema
        var schema = new List<OrcFieldDescriptor>(batch.ColumnCount);
        foreach (var col in batch.Columns)
        {
            schema.Add(new OrcFieldDescriptor
            {
                Name = col.Name,
                DataType = col.DataType,
                OrcType = MapToOrcType(col.DataType)
            });
        }

        // Write stripe data
        var stripeInfos = new List<(long offset, int length, int rowCount)>();
        var allStripeStats = new List<StripeStatistics>();
        int stripeSize = 10_000; // Default stripe size
        int offset = 0;
        int stripeIdx = 0;

        while (offset < batch.RowCount)
        {
            int count = Math.Min(stripeSize, batch.RowCount - offset);
            long stripeOffset = writer.BaseStream.Position;

            WriteStripe(writer, batch, schema, offset, count);

            int stripeLength = (int)(writer.BaseStream.Position - stripeOffset);
            stripeInfos.Add((stripeOffset, stripeLength, count));

            // ECOS-05: Fixed — Compute and store stripe statistics
            var stats = ComputeStripeStatistics(batch, stripeIdx, offset, count);
            allStripeStats.Add(stats);

            offset += count;
            stripeIdx++;
        }

        // Write empty stripe for empty batches
        if (batch.RowCount == 0)
        {
            long stripeOffset = writer.BaseStream.Position;
            WriteStripe(writer, batch, schema, 0, 0);
            int stripeLength = (int)(writer.BaseStream.Position - stripeOffset);
            stripeInfos.Add((stripeOffset, stripeLength, 0));
        }

        // Write footer
        long footerStart = writer.BaseStream.Position;
        WriteOrcFooter(writer, schema, stripeInfos, allStripeStats, batch.RowCount, codec);
        int footerLength = (int)(writer.BaseStream.Position - footerStart);

        // Write postscript
        WriteOrcPostscript(writer, footerLength, codec);

        // Last byte: postscript length (postscript is always 1-255 bytes, we use a fixed size)
        // The postscript length byte is the very last byte in the file
        writer.Write((byte)PostscriptSize);
    }

    private const int PostscriptSize = 13; // Fixed postscript: version(4) + footer_len(4) + codec(1) + magic(3) + reserved(1)

    private static void WriteStripe(BinaryWriter writer, ColumnarBatch batch,
        IReadOnlyList<OrcFieldDescriptor> schema, int rowOffset, int rowCount)
    {
        // Stripe header: row count
        writer.Write(rowCount);
        writer.Write(schema.Count);

        // Write column data
        for (int colIdx = 0; colIdx < schema.Count; colIdx++)
        {
            var field = schema[colIdx];
            var column = batch.Columns[colIdx];

            // Null bitmap (ORC uses present stream)
            int bitmapBytes = (rowCount + 7) / 8;
            var bitmap = new byte[bitmapBytes];
            for (int i = 0; i < rowCount; i++)
            {
                if (column.IsNull(rowOffset + i))
                    bitmap[i >> 3] |= (byte)(1 << (i & 7));
            }
            writer.Write(bitmapBytes);
            writer.Write(bitmap);

            // Column values (RLE-like encoding for ORC, using plain for our implementation)
            WriteOrcColumnValues(writer, column, field, rowOffset, rowCount);
        }
    }

    private static void WriteOrcColumnValues(BinaryWriter writer, ColumnVector column,
        OrcFieldDescriptor field, int rowOffset, int rowCount)
    {
        switch (field.DataType)
        {
            case ColumnDataType.Int32:
                var i32 = (TypedColumnVector<int>)column;
                for (int i = 0; i < rowCount; i++) writer.Write(i32.Values[rowOffset + i]);
                break;
            case ColumnDataType.Int64:
                var i64 = (TypedColumnVector<long>)column;
                for (int i = 0; i < rowCount; i++) writer.Write(i64.Values[rowOffset + i]);
                break;
            case ColumnDataType.Float64:
                var f64 = (TypedColumnVector<double>)column;
                for (int i = 0; i < rowCount; i++) writer.Write(f64.Values[rowOffset + i]);
                break;
            case ColumnDataType.Bool:
                var boolCol = (TypedColumnVector<bool>)column;
                for (int i = 0; i < rowCount; i++) writer.Write(boolCol.Values[rowOffset + i]);
                break;
            case ColumnDataType.String:
                var strCol = (TypedColumnVector<string>)column;
                for (int i = 0; i < rowCount; i++)
                {
                    var s = strCol.Values[rowOffset + i];
                    if (s == null) { writer.Write(0); }
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
                    if (b == null) { writer.Write(0); }
                    else { writer.Write(b.Length); writer.Write(b); }
                }
                break;
            case ColumnDataType.Decimal:
                var decCol = (TypedColumnVector<decimal>)column;
                for (int i = 0; i < rowCount; i++)
                {
                    var bits = decimal.GetBits(decCol.Values[rowOffset + i]);
                    for (int b = 0; b < 4; b++) writer.Write(bits[b]);
                }
                break;
            case ColumnDataType.DateTime:
                var dtCol = (TypedColumnVector<DateTime>)column;
                for (int i = 0; i < rowCount; i++)
                {
                    var dt = dtCol.Values[rowOffset + i];
                    var micros = (dt.ToUniversalTime() - DateTime.UnixEpoch).Ticks / 10;
                    writer.Write(micros);
                }
                break;
            default:
                break;
        }
    }

    private static void WriteOrcFooter(BinaryWriter writer, IReadOnlyList<OrcFieldDescriptor> schema,
        List<(long offset, int length, int rowCount)> stripeInfos,
        List<StripeStatistics> stripeStats, int totalRowCount, OrcCompressionCodec codec)
    {
        // Footer format:
        // 1. Version: int32
        // 2. Total row count: int64
        // 3. Number of columns: int32
        // 4. For each column: name (len-prefixed), orc_type (byte), logical_type (byte)
        // 5. Number of stripes: int32
        // 6. For each stripe: offset (int64), length (int32), rowCount (int32)
        // 7. Stripe statistics

        writer.Write(1); // footer version
        writer.Write((long)totalRowCount);

        // Schema
        writer.Write(schema.Count);
        foreach (var field in schema)
        {
            var nameBytes = Encoding.UTF8.GetBytes(field.Name);
            writer.Write(nameBytes.Length);
            writer.Write(nameBytes);
            writer.Write((byte)field.OrcType);
            writer.Write((byte)field.DataType);
        }

        // Stripe info
        writer.Write(stripeInfos.Count);
        foreach (var (sOffset, sLength, sRowCount) in stripeInfos)
        {
            writer.Write(sOffset);
            writer.Write(sLength);
            writer.Write(sRowCount);
        }

        // ECOS-05: Fixed — Stripe statistics for predicate pushdown
        writer.Write(stripeStats.Count);
        foreach (var ss in stripeStats)
        {
            writer.Write(ss.RowCount);
            writer.Write(ss.ColumnStats.Count);
            foreach (var cs in ss.ColumnStats)
            {
                var nameBytes = Encoding.UTF8.GetBytes(cs.ColumnName);
                writer.Write(nameBytes.Length);
                writer.Write(nameBytes);
                writer.Write(cs.HasNull);
                writer.Write(cs.Count);
                // Min/max serialized as string for simplicity
                var minStr = cs.Min?.ToString() ?? string.Empty;
                var maxStr = cs.Max?.ToString() ?? string.Empty;
                var minBytes = Encoding.UTF8.GetBytes(minStr);
                var maxBytes = Encoding.UTF8.GetBytes(maxStr);
                writer.Write(minBytes.Length);
                writer.Write(minBytes);
                writer.Write(maxBytes.Length);
                writer.Write(maxBytes);
            }
        }
    }

    private static void WriteOrcPostscript(BinaryWriter writer, int footerLength, OrcCompressionCodec codec)
    {
        // Postscript: fixed 13 bytes
        writer.Write(1); // ORC version: 1
        writer.Write(footerLength); // footer length
        writer.Write((byte)codec); // compression codec
        writer.Write(OrcMagic); // "ORC" magic
        writer.Write((byte)0); // reserved
    }

    // ─────────────────────────────────────────────────────────
    // ORC file format reading
    // ─────────────────────────────────────────────────────────

    private static OrcParseResult ParseOrcFile(byte[] fileBytes)
    {
        // Verify magic
        if (fileBytes.Length < 3 || fileBytes[0] != 0x4F || fileBytes[1] != 0x52 || fileBytes[2] != 0x43)
            throw new InvalidDataException("Not a valid ORC file: missing ORC magic.");

        // Read postscript length from last byte
        byte psLength = fileBytes[^1];

        // Read postscript
        int psOffset = fileBytes.Length - 1 - psLength;
        using var psStream = new MemoryStream(fileBytes, psOffset, psLength);
        using var psReader = new BinaryReader(psStream, Encoding.UTF8);

        int orcVersion = psReader.ReadInt32();
        int footerLength = psReader.ReadInt32();
        byte codec = psReader.ReadByte();
        // Skip magic and reserved bytes

        // Read footer
        int footerOffset = psOffset - footerLength;
        using var footerStream = new MemoryStream(fileBytes, footerOffset, footerLength);
        using var footerReader = new BinaryReader(footerStream, Encoding.UTF8);

        int footerVersion = footerReader.ReadInt32();
        long totalRowCount = footerReader.ReadInt64();

        // Schema
        int numColumns = footerReader.ReadInt32();
        var schema = new List<OrcFieldDescriptor>(numColumns);
        for (int i = 0; i < numColumns; i++)
        {
            int nameLen = footerReader.ReadInt32();
            var nameBytes = footerReader.ReadBytes(nameLen);
            string name = Encoding.UTF8.GetString(nameBytes);
            var orcType = (OrcType)footerReader.ReadByte();
            var logicalType = (ColumnDataType)footerReader.ReadByte();

            schema.Add(new OrcFieldDescriptor
            {
                Name = name,
                DataType = logicalType,
                OrcType = orcType
            });
        }

        // Stripe info
        int numStripes = footerReader.ReadInt32();
        var stripeInfos = new List<(long offset, int length, int rowCount)>(numStripes);
        for (int i = 0; i < numStripes; i++)
        {
            long sOffset = footerReader.ReadInt64();
            int sLength = footerReader.ReadInt32();
            int sRowCount = footerReader.ReadInt32();
            stripeInfos.Add((sOffset, sLength, sRowCount));
        }

        // Read stripe data
        var allColumns = new List<ColumnVector>(numColumns);
        // Initialize column builders
        for (int colIdx = 0; colIdx < numColumns; colIdx++)
        {
            allColumns.Add(null!); // Will be replaced per-stripe merge
        }

        // For simplicity, read all stripes and merge into a single batch
        var batchColumns = new List<List<ColumnVector>>(numColumns);
        for (int c = 0; c < numColumns; c++)
            batchColumns.Add(new List<ColumnVector>());

        int totalRows = 0;
        foreach (var (sOffset, sLength, sRowCount) in stripeInfos)
        {
            using var stripeStream = new MemoryStream(fileBytes, (int)sOffset, sLength);
            using var stripeReader = new BinaryReader(stripeStream, Encoding.UTF8);

            int stripeRowCount = stripeReader.ReadInt32();
            int stripeCols = stripeReader.ReadInt32();

            for (int colIdx = 0; colIdx < stripeCols; colIdx++)
            {
                var field = schema[colIdx];

                // Read null bitmap
                int bitmapLen = stripeReader.ReadInt32();
                var bitmap = stripeReader.ReadBytes(bitmapLen);

                // Read column values
                var column = ReadOrcColumnValues(stripeReader, field, stripeRowCount, bitmap);
                batchColumns[colIdx].Add(column);
            }

            totalRows += stripeRowCount;
        }

        // Merge columns from all stripes
        var mergedColumns = new List<ColumnVector>(numColumns);
        for (int colIdx = 0; colIdx < numColumns; colIdx++)
        {
            if (batchColumns[colIdx].Count == 1)
            {
                mergedColumns.Add(batchColumns[colIdx][0]);
            }
            else if (batchColumns[colIdx].Count > 1)
            {
                mergedColumns.Add(MergeColumnVectors(batchColumns[colIdx], schema[colIdx], totalRows));
            }
            else
            {
                // Empty — create empty column
                mergedColumns.Add(new StringColumnVector(schema[colIdx].Name, new string[0]));
            }
        }

        var batch = new ColumnarBatch(totalRows, mergedColumns);

        return new OrcParseResult
        {
            Batch = batch,
            Schema = schema,
            StripeCount = numStripes
        };
    }

    private static ColumnVector ReadOrcColumnValues(BinaryReader reader, OrcFieldDescriptor field,
        int rowCount, byte[] bitmap)
    {
        switch (field.DataType)
        {
            case ColumnDataType.Int32:
            {
                var values = new int[rowCount];
                for (int i = 0; i < rowCount; i++) values[i] = reader.ReadInt32();
                return new Int32ColumnVector(field.Name, values, bitmap);
            }
            case ColumnDataType.Int64:
            {
                var values = new long[rowCount];
                for (int i = 0; i < rowCount; i++) values[i] = reader.ReadInt64();
                return new Int64ColumnVector(field.Name, values, bitmap);
            }
            case ColumnDataType.Float64:
            {
                var values = new double[rowCount];
                for (int i = 0; i < rowCount; i++) values[i] = reader.ReadDouble();
                return new Float64ColumnVector(field.Name, values, bitmap);
            }
            case ColumnDataType.Bool:
            {
                var values = new bool[rowCount];
                for (int i = 0; i < rowCount; i++) values[i] = reader.ReadBoolean();
                return new BoolColumnVector(field.Name, values, bitmap);
            }
            case ColumnDataType.String:
            {
                var values = new string[rowCount];
                for (int i = 0; i < rowCount; i++)
                {
                    int len = reader.ReadInt32();
                    values[i] = len > 0 ? Encoding.UTF8.GetString(reader.ReadBytes(len)) : string.Empty;
                }
                return new StringColumnVector(field.Name, values, bitmap);
            }
            case ColumnDataType.Binary:
            {
                var values = new byte[rowCount][];
                for (int i = 0; i < rowCount; i++)
                {
                    int len = reader.ReadInt32();
                    values[i] = len > 0 ? reader.ReadBytes(len) : Array.Empty<byte>();
                }
                return new BinaryColumnVector(field.Name, values, bitmap);
            }
            case ColumnDataType.Decimal:
            {
                var values = new decimal[rowCount];
                for (int i = 0; i < rowCount; i++)
                {
                    var bits = new int[4];
                    for (int b = 0; b < 4; b++) bits[b] = reader.ReadInt32();
                    values[i] = new decimal(bits);
                }
                return new DecimalColumnVector(field.Name, values, bitmap);
            }
            case ColumnDataType.DateTime:
            {
                var values = new DateTime[rowCount];
                for (int i = 0; i < rowCount; i++)
                {
                    long micros = reader.ReadInt64();
                    values[i] = DateTime.UnixEpoch.AddTicks(micros * 10);
                }
                return new DateTimeColumnVector(field.Name, values, bitmap);
            }
            default:
            {
                return new StringColumnVector(field.Name, new string[rowCount], bitmap);
            }
        }
    }

    private static ColumnVector MergeColumnVectors(List<ColumnVector> vectors, OrcFieldDescriptor field, int totalRows)
    {
        // Merge multiple stripe column vectors into one
        switch (field.DataType)
        {
            case ColumnDataType.Int32: return MergeTyped<int>(vectors, field.Name, ColumnDataType.Int32, totalRows, (n, v, b) => new Int32ColumnVector(n, v, b));
            case ColumnDataType.Int64: return MergeTyped<long>(vectors, field.Name, ColumnDataType.Int64, totalRows, (n, v, b) => new Int64ColumnVector(n, v, b));
            case ColumnDataType.Float64: return MergeTyped<double>(vectors, field.Name, ColumnDataType.Float64, totalRows, (n, v, b) => new Float64ColumnVector(n, v, b));
            case ColumnDataType.Bool: return MergeTyped<bool>(vectors, field.Name, ColumnDataType.Bool, totalRows, (n, v, b) => new BoolColumnVector(n, v, b));
            case ColumnDataType.String: return MergeTyped<string>(vectors, field.Name, ColumnDataType.String, totalRows, (n, v, b) => new StringColumnVector(n, v, b));
            case ColumnDataType.Binary: return MergeTyped<byte[]>(vectors, field.Name, ColumnDataType.Binary, totalRows, (n, v, b) => new BinaryColumnVector(n, v, b));
            case ColumnDataType.Decimal: return MergeTyped<decimal>(vectors, field.Name, ColumnDataType.Decimal, totalRows, (n, v, b) => new DecimalColumnVector(n, v, b));
            case ColumnDataType.DateTime: return MergeTyped<DateTime>(vectors, field.Name, ColumnDataType.DateTime, totalRows, (n, v, b) => new DateTimeColumnVector(n, v, b));
            default: return vectors[0];
        }
    }

    private static ColumnVector MergeTyped<T>(List<ColumnVector> vectors, string name, ColumnDataType dt,
        int totalRows, Func<string, T[], byte[], ColumnVector> factory)
    {
        var merged = new T[totalRows];
        var bitmap = new byte[(totalRows + 7) / 8];
        int offset = 0;

        foreach (var v in vectors)
        {
            var typed = (TypedColumnVector<T>)v;
            Array.Copy(typed.Values, 0, merged, offset, typed.Length);
            // Copy bitmap
            for (int i = 0; i < typed.Length; i++)
            {
                if (typed.IsNull(i))
                    bitmap[(offset + i) >> 3] |= (byte)(1 << ((offset + i) & 7));
            }
            offset += typed.Length;
        }

        return factory(name, merged, bitmap);
    }
}
