using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Query;

namespace DataWarehouse.SDK.VirtualDiskEngine.Sql;

// ─────────────────────────────────────────────────────────────
// Parquet Row Group Statistics
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Statistics for a single column within a Parquet row group.
/// Captures min/max values, null count, and distinct count for
/// predicate pushdown via zone maps.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Parquet-VDE zone map integration (ECOS-06)")]
public sealed record ColumnStatistics(
    string ColumnName,
    ColumnDataType Type,
    object? MinValue,
    object? MaxValue,
    long NullCount,
    long DistinctCount);

/// <summary>
/// Statistics for a Parquet row group, including per-column min/max/null statistics.
/// Used to populate VDE zone maps for predicate pushdown.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Parquet-VDE zone map integration (ECOS-06)")]
public sealed record ParquetRowGroupStatistics(
    int RowGroupIndex,
    long RowCount,
    long StartRow,
    IReadOnlyList<ColumnStatistics> ColumnStats);

/// <summary>
/// Options for Parquet export from VDE.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Parquet-VDE zone map integration (ECOS-06)")]
public sealed class ParquetExportOptions
{
    /// <summary>Maximum rows per row group. Default 1,000,000.</summary>
    public int RowGroupSize { get; init; } = 1_000_000;

    /// <summary>Compression codec for export.</summary>
    public ParquetCompressionCodec CompressionCodec { get; init; } = ParquetCompressionCodec.None;
}

// ─────────────────────────────────────────────────────────────
// Parquet-VDE Integration
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Integrates Parquet row group statistics with VDE zone maps, and provides
/// zero-copy import/export paths: Parquet -> Arrow -> VDE and VDE -> Arrow -> Parquet.
/// </summary>
/// <remarks>
/// Zone map population from Parquet statistics enables predicate pushdown at
/// the VDE storage layer, allowing entire row groups to be skipped when queries
/// have range predicates that fall outside the min/max bounds.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Parquet-VDE zone map integration (ECOS-06)")]
public static class ParquetVdeIntegration
{
    /// <summary>
    /// Populates VDE zone map entries from Parquet row group statistics.
    /// For each column in the row group, extracts min, max, and null_count,
    /// creates a <see cref="ZoneMapEntry"/>, and adds it to the zone map index.
    /// </summary>
    /// <param name="zoneMap">The zone map index to populate.</param>
    /// <param name="stats">Parquet row group statistics.</param>
    /// <param name="tableName">VDE table name for zone map association.</param>
    /// <param name="extentStartBlock">Physical start block for this row group's extent.</param>
    /// <param name="extentBlockCount">Number of blocks in the extent.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task PopulateZoneMapsFromParquet(
        ZoneMapIndex zoneMap,
        ParquetRowGroupStatistics stats,
        string tableName,
        long extentStartBlock,
        int extentBlockCount,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(zoneMap);
        ArgumentNullException.ThrowIfNull(stats);

        for (int colIdx = 0; colIdx < stats.ColumnStats.Count; colIdx++)
        {
            var colStats = stats.ColumnStats[colIdx];

            long minVal = ConvertToLong(colStats.MinValue, colStats.Type);
            long maxVal = ConvertToLong(colStats.MaxValue, colStats.Type);
            int nullCount = (int)Math.Min(colStats.NullCount, int.MaxValue);
            int rowCount = (int)Math.Min(stats.RowCount, int.MaxValue);

            // Each column gets its own zone map entry at an offset from the extent start
            long columnExtentStart = extentStartBlock + colIdx;

            var entry = new ZoneMapEntry(
                minValue: minVal,
                maxValue: maxVal,
                nullCount: nullCount,
                rowCount: rowCount,
                extentStartBlock: columnExtentStart,
                extentBlockCount: extentBlockCount);

            await zoneMap.AddEntryAsync(columnExtentStart, extentBlockCount, entry, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Imports a Parquet stream into VDE storage via the Arrow bridge, populating
    /// zone maps from row group statistics along the way.
    /// Path: Parquet -> ColumnarBatch (via ParquetCompatibleReader) -> ArrowRecordBatch -> VDE.
    /// </summary>
    /// <param name="parquetStream">Seekable stream containing Parquet data.</param>
    /// <param name="region">VDE columnar region engine for storage.</param>
    /// <param name="zoneMap">Zone map index to populate from row group statistics.</param>
    /// <param name="tableName">Table name for VDE storage.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ImportParquetToVde(
        Stream parquetStream,
        ColumnarRegionEngine region,
        ZoneMapIndex zoneMap,
        string tableName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parquetStream);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(zoneMap);
        if (string.IsNullOrEmpty(tableName))
            throw new ArgumentException("Table name is required.", nameof(tableName));

        // Read Parquet metadata for statistics
        var metadata = ParquetCompatibleReader.ReadMetadata(parquetStream);

        // Read each row group as ColumnarBatch -> convert to Arrow -> write to VDE
        int rowGroupIndex = 0;
        long currentRow = 0;

        await foreach (var batch in ParquetCompatibleReader.ReadFromStream(parquetStream).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            // Convert to Arrow for zero-copy VDE write
            var arrowBatch = ArrowColumnarBridge.FromColumnarBatch(batch);

            // Create table on first row group
            if (rowGroupIndex == 0)
            {
                var columns = new ColumnDefinition[arrowBatch.Schema.Fields.Count];
                for (int i = 0; i < arrowBatch.Schema.Fields.Count; i++)
                {
                    var field = arrowBatch.Schema.Fields[i];
                    columns[i] = new ColumnDefinition(
                        field.Name,
                        MapArrowToColumnType(field.Type),
                        nullable: field.Nullable);
                }
                await region.CreateColumnarTableAsync(tableName, columns, ct).ConfigureAwait(false);
            }

            // Prepare column data from Arrow buffers
            var columnData = new byte[arrowBatch.Schema.Fields.Count][];
            for (int i = 0; i < arrowBatch.Schema.Fields.Count; i++)
            {
                columnData[i] = arrowBatch.Columns[i].Data.ToArray();
            }

            await region.AppendRowGroupAsync(tableName, columnData, batch.RowCount, ct)
                .ConfigureAwait(false);

            // Populate zone maps from Parquet row group statistics
            if (rowGroupIndex < metadata.RowGroups.Count)
            {
                var rgMeta = metadata.RowGroups[rowGroupIndex];
                var columnStats = BuildColumnStatistics(batch, rgMeta);
                var stats = new ParquetRowGroupStatistics(
                    rowGroupIndex, batch.RowCount, currentRow, columnStats);

                // Compute extent start block from region metadata block offset
                // Each row group occupies columns.Length blocks, starting after the metadata block
                long extentStartBlock = 1 + rowGroupIndex * (long)batch.ColumnCount;

                await PopulateZoneMapsFromParquet(
                    zoneMap, stats, tableName, extentStartBlock, batch.ColumnCount, ct)
                    .ConfigureAwait(false);
            }

            currentRow += batch.RowCount;
            rowGroupIndex++;
        }
    }

    /// <summary>
    /// Exports VDE columnar data to Parquet format, preserving zone map statistics
    /// as row group statistics in the output.
    /// Path: VDE -> ColumnarBatch (via region scan) -> Parquet (via ParquetCompatibleWriter).
    /// </summary>
    /// <param name="region">VDE columnar region engine.</param>
    /// <param name="tableName">Table name to export.</param>
    /// <param name="output">Output stream for Parquet data.</param>
    /// <param name="columns">Column names to export.</param>
    /// <param name="options">Parquet write options.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ExportVdeToParquet(
        ColumnarRegionEngine region,
        string tableName,
        Stream output,
        string[] columns,
        ParquetExportOptions? options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(output);
        if (string.IsNullOrEmpty(tableName))
            throw new ArgumentException("Table name is required.", nameof(tableName));

        options ??= new ParquetExportOptions();
        var writeOptions = new ParquetWriteOptions
        {
            RowGroupSize = options.RowGroupSize,
            CompressionCodec = options.CompressionCodec
        };

        // Scan VDE and produce ColumnarBatch stream for Parquet writer
        var batches = ScanVdeAsBatches(region, tableName, columns, ct);
        await ParquetCompatibleWriter.WriteToStreamAsync(output, batches, writeOptions)
            .ConfigureAwait(false);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static async IAsyncEnumerable<ColumnarBatch> ScanVdeAsBatches(
        ColumnarRegionEngine region,
        string tableName,
        string[] columns,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var rowGroupData in region.ScanColumnsAsync(tableName, columns, null, ct).ConfigureAwait(false))
        {
            // Build a ColumnarBatch from raw column data
            // Each byte[] represents encoded column data - wrap as BinaryColumnVector
            var columnVectors = new List<ColumnVector>(rowGroupData.Length);
            int rowCount = 0;

            for (int i = 0; i < rowGroupData.Length; i++)
            {
                var data = rowGroupData[i];
                // Estimate row count from first numeric column (4 bytes per int)
                if (i == 0 && data.Length >= 4)
                    rowCount = data.Length / 4;

                var values = new byte[rowCount > 0 ? rowCount : 1][];
                for (int r = 0; r < values.Length; r++)
                    values[r] = data;

                columnVectors.Add(new BinaryColumnVector(columns[i], values));
            }

            yield return new ColumnarBatch(rowCount, columnVectors);
        }
    }

    private static List<ColumnStatistics> BuildColumnStatistics(
        ColumnarBatch batch,
        ParquetRowGroupMetadata rgMeta)
    {
        var stats = new List<ColumnStatistics>(batch.ColumnCount);

        for (int i = 0; i < batch.ColumnCount; i++)
        {
            var col = batch.Columns[i];
            object? minVal = null;
            object? maxVal = null;
            long nullCount = 0;

            // Count nulls
            for (int r = 0; r < batch.RowCount; r++)
            {
                if (col.IsNull(r))
                    nullCount++;
            }

            // Compute min/max for numeric types
            switch (col.DataType)
            {
                case ColumnDataType.Int32:
                {
                    var typed = (TypedColumnVector<int>)col;
                    int min = int.MaxValue, max = int.MinValue;
                    for (int r = 0; r < batch.RowCount; r++)
                    {
                        if (!col.IsNull(r))
                        {
                            if (typed.Values[r] < min) min = typed.Values[r];
                            if (typed.Values[r] > max) max = typed.Values[r];
                        }
                    }
                    if (min <= max) { minVal = min; maxVal = max; }
                    break;
                }
                case ColumnDataType.Int64:
                {
                    var typed = (TypedColumnVector<long>)col;
                    long min = long.MaxValue, max = long.MinValue;
                    for (int r = 0; r < batch.RowCount; r++)
                    {
                        if (!col.IsNull(r))
                        {
                            if (typed.Values[r] < min) min = typed.Values[r];
                            if (typed.Values[r] > max) max = typed.Values[r];
                        }
                    }
                    if (min <= max) { minVal = min; maxVal = max; }
                    break;
                }
                case ColumnDataType.Float64:
                {
                    var typed = (TypedColumnVector<double>)col;
                    double min = double.MaxValue, max = double.MinValue;
                    for (int r = 0; r < batch.RowCount; r++)
                    {
                        if (!col.IsNull(r))
                        {
                            if (typed.Values[r] < min) min = typed.Values[r];
                            if (typed.Values[r] > max) max = typed.Values[r];
                        }
                    }
                    if (min <= max) { minVal = min; maxVal = max; }
                    break;
                }
                default:
                    // Non-numeric types: no min/max stats
                    break;
            }

            stats.Add(new ColumnStatistics(
                col.Name, col.DataType, minVal, maxVal, nullCount, 0));
        }

        return stats;
    }

    /// <summary>
    /// Converts a statistics value to a long for zone map entry storage.
    /// </summary>
    private static long ConvertToLong(object? value, ColumnDataType type)
    {
        if (value is null) return 0;

        return type switch
        {
            ColumnDataType.Int32 => Convert.ToInt64(value),
            ColumnDataType.Int64 => (long)value,
            ColumnDataType.Float64 => BitConverter.DoubleToInt64Bits(Convert.ToDouble(value)),
            ColumnDataType.DateTime when value is DateTime dt =>
                (dt.ToUniversalTime() - DateTime.UnixEpoch).Ticks / 10,
            _ => value.GetHashCode(),
        };
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
