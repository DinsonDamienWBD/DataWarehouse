using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.DataFormat;
using DataWarehouse.SDK.Contracts.Query;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.Columnar;

/// <summary>
/// Cross-format verification and compatibility matrix for Parquet, Arrow, and ORC
/// columnar formats. Provides round-trip testing, data type coverage, and external
/// tool compatibility information.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Columnar format verification (ECOS-03/04/05)")]
public sealed class ColumnarFormatVerification
{
    // ─────────────────────────────────────────────────────────
    // Format coverage
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Data type coverage for a single format.
    /// </summary>
    public sealed class FormatDataTypeCoverage
    {
        /// <summary>Format identifier.</summary>
        public string FormatId { get; init; } = string.Empty;

        /// <summary>Supported ColumnDataType values.</summary>
        public IReadOnlyList<ColumnDataType> SupportedDataTypes { get; init; } = Array.Empty<ColumnDataType>();

        /// <summary>Supported compression codecs.</summary>
        public IReadOnlyList<string> SupportedCompressionCodecs { get; init; } = Array.Empty<string>();

        /// <summary>Whether this format supports column-level statistics for predicate pushdown.</summary>
        public bool SupportsStatistics { get; init; }

        /// <summary>Whether this format supports column pruning (reading only requested columns).</summary>
        public bool SupportsColumnPruning { get; init; }

        /// <summary>Whether this format supports zero-copy reads.</summary>
        public bool SupportsZeroCopyReads { get; init; }
    }

    /// <summary>
    /// Cross-format type compatibility entry showing how a ColumnDataType maps to native types.
    /// </summary>
    public sealed class TypeCompatibilityEntry
    {
        /// <summary>The ColumnDataType enum value.</summary>
        public ColumnDataType DataType { get; init; }

        /// <summary>Native Parquet physical type name.</summary>
        public string ParquetNativeType { get; init; } = string.Empty;

        /// <summary>Native Arrow type name.</summary>
        public string ArrowNativeType { get; init; } = string.Empty;

        /// <summary>Native ORC type name.</summary>
        public string OrcNativeType { get; init; } = string.Empty;

        /// <summary>Whether all three formats support this type.</summary>
        public bool UniversallySupported { get; init; }
    }

    /// <summary>
    /// Complete columnar format coverage report.
    /// </summary>
    public sealed class ColumnarFormatCoverage
    {
        /// <summary>Per-format coverage details.</summary>
        public IReadOnlyList<FormatDataTypeCoverage> FormatCoverage { get; init; } = Array.Empty<FormatDataTypeCoverage>();

        /// <summary>Cross-format type compatibility matrix.</summary>
        public IReadOnlyList<TypeCompatibilityEntry> TypeCompatibilityMatrix { get; init; } = Array.Empty<TypeCompatibilityEntry>();
    }

    /// <summary>
    /// Returns comprehensive format coverage including per-format data type support,
    /// compression codec support, statistics support, column pruning, and a cross-format
    /// type compatibility matrix.
    /// </summary>
    public static ColumnarFormatCoverage GetFormatCoverage()
    {
        var allDataTypes = new[]
        {
            ColumnDataType.Int32, ColumnDataType.Int64, ColumnDataType.Float64,
            ColumnDataType.String, ColumnDataType.Bool, ColumnDataType.Binary,
            ColumnDataType.Decimal, ColumnDataType.DateTime, ColumnDataType.Null
        };

        var parquetCoverage = new FormatDataTypeCoverage
        {
            FormatId = "parquet",
            SupportedDataTypes = allDataTypes,
            SupportedCompressionCodecs = new[] { "None", "Snappy", "Gzip", "Zstd" },
            SupportsStatistics = true,
            SupportsColumnPruning = true,
            SupportsZeroCopyReads = false
        };

        var arrowCoverage = new FormatDataTypeCoverage
        {
            FormatId = "arrow",
            SupportedDataTypes = allDataTypes,
            SupportedCompressionCodecs = new[] { "None" }, // Arrow IPC doesn't compress (separate concern)
            SupportsStatistics = false,
            SupportsColumnPruning = false, // Arrow batches are columnar but read whole batches
            SupportsZeroCopyReads = true
        };

        var orcCoverage = new FormatDataTypeCoverage
        {
            FormatId = "orc",
            SupportedDataTypes = allDataTypes,
            SupportedCompressionCodecs = new[] { "None", "Zlib", "Snappy", "Lzo", "Lz4", "Zstd" },
            SupportsStatistics = true,
            SupportsColumnPruning = false, // ORC reads full stripes
            SupportsZeroCopyReads = false
        };

        var typeMatrix = new List<TypeCompatibilityEntry>
        {
            new() { DataType = ColumnDataType.Int32, ParquetNativeType = "INT32", ArrowNativeType = "Int32", OrcNativeType = "INT", UniversallySupported = true },
            new() { DataType = ColumnDataType.Int64, ParquetNativeType = "INT64", ArrowNativeType = "Int64", OrcNativeType = "LONG", UniversallySupported = true },
            new() { DataType = ColumnDataType.Float64, ParquetNativeType = "DOUBLE", ArrowNativeType = "Float64", OrcNativeType = "DOUBLE", UniversallySupported = true },
            new() { DataType = ColumnDataType.String, ParquetNativeType = "BYTE_ARRAY (UTF8)", ArrowNativeType = "Utf8", OrcNativeType = "STRING", UniversallySupported = true },
            new() { DataType = ColumnDataType.Bool, ParquetNativeType = "BOOLEAN", ArrowNativeType = "Bool", OrcNativeType = "BOOLEAN", UniversallySupported = true },
            new() { DataType = ColumnDataType.Binary, ParquetNativeType = "BYTE_ARRAY", ArrowNativeType = "Binary", OrcNativeType = "BINARY", UniversallySupported = true },
            new() { DataType = ColumnDataType.Decimal, ParquetNativeType = "FIXED_LEN_BYTE_ARRAY (16B)", ArrowNativeType = "Decimal128", OrcNativeType = "DECIMAL", UniversallySupported = true },
            new() { DataType = ColumnDataType.DateTime, ParquetNativeType = "INT64 (TIMESTAMP_MICROS)", ArrowNativeType = "Timestamp (us)", OrcNativeType = "TIMESTAMP", UniversallySupported = true },
            new() { DataType = ColumnDataType.Null, ParquetNativeType = "BYTE_ARRAY (null bitmap)", ArrowNativeType = "Null", OrcNativeType = "BINARY (null bitmap)", UniversallySupported = true }
        };

        return new ColumnarFormatCoverage
        {
            FormatCoverage = new[] { parquetCoverage, arrowCoverage, orcCoverage },
            TypeCompatibilityMatrix = typeMatrix
        };
    }

    // ─────────────────────────────────────────────────────────
    // Round-trip verification
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Result of a round-trip verification test.
    /// </summary>
    public sealed class RoundTripResult
    {
        /// <summary>Whether the round-trip preserved all data correctly.</summary>
        public bool Success { get; init; }

        /// <summary>List of differences found between original and round-tripped data.</summary>
        public string[] Differences { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Verifies that a columnar batch survives a serialize-then-parse round-trip
    /// through the given format strategy. Compares schema (column names, types,
    /// nullable flags) and data values row-by-row.
    /// </summary>
    public static async Task<RoundTripResult> VerifyRoundTrip(
        DataFormatStrategyBase strategy,
        ColumnarBatch testBatch,
        CancellationToken ct = default)
    {
        var differences = new List<string>();

        try
        {
            // Serialize
            using var ms = new MemoryStream();
            var serializeResult = await strategy.SerializeAsync(testBatch, ms, new DataFormatContext(), ct);
            if (!serializeResult.Success)
            {
                differences.Add($"Serialization failed: {serializeResult.ErrorMessage}");
                return new RoundTripResult { Success = false, Differences = differences.ToArray() };
            }

            // Parse back
            ms.Position = 0;
            var parseResult = await strategy.ParseAsync(ms, new DataFormatContext(), ct);
            if (!parseResult.Success)
            {
                differences.Add($"Parse failed: {parseResult.ErrorMessage}");
                return new RoundTripResult { Success = false, Differences = differences.ToArray() };
            }

            // Extract parsed batch
            ColumnarBatch parsedBatch;
            if (parseResult.Data is ColumnarBatch batch)
                parsedBatch = batch;
            else if (parseResult.Data is IReadOnlyList<ColumnarBatch> batchList && batchList.Count > 0)
                parsedBatch = batchList[0];
            else
            {
                differences.Add($"Unexpected parse result type: {parseResult.Data?.GetType().Name ?? "null"}");
                return new RoundTripResult { Success = false, Differences = differences.ToArray() };
            }

            // Compare schema
            if (testBatch.ColumnCount != parsedBatch.ColumnCount)
            {
                differences.Add($"Column count mismatch: expected {testBatch.ColumnCount}, got {parsedBatch.ColumnCount}");
            }
            else
            {
                for (int colIdx = 0; colIdx < testBatch.ColumnCount; colIdx++)
                {
                    var origCol = testBatch.Columns[colIdx];
                    var parsedCol = parsedBatch.Columns[colIdx];

                    if (origCol.Name != parsedCol.Name)
                        differences.Add($"Column {colIdx} name mismatch: expected '{origCol.Name}', got '{parsedCol.Name}'");

                    if (origCol.DataType != parsedCol.DataType)
                        differences.Add($"Column '{origCol.Name}' type mismatch: expected {origCol.DataType}, got {parsedCol.DataType}");
                }
            }

            // Compare row count
            if (testBatch.RowCount != parsedBatch.RowCount)
            {
                differences.Add($"Row count mismatch: expected {testBatch.RowCount}, got {parsedBatch.RowCount}");
            }
            else
            {
                // Compare data values row-by-row
                int maxCols = Math.Min(testBatch.ColumnCount, parsedBatch.ColumnCount);
                for (int colIdx = 0; colIdx < maxCols; colIdx++)
                {
                    var origCol = testBatch.Columns[colIdx];
                    var parsedCol = parsedBatch.Columns[colIdx];

                    for (int row = 0; row < testBatch.RowCount; row++)
                    {
                        bool origNull = origCol.IsNull(row);
                        bool parsedNull = parsedCol.IsNull(row);

                        if (origNull != parsedNull)
                        {
                            differences.Add($"Column '{origCol.Name}' row {row} null mismatch: expected {origNull}, got {parsedNull}");
                            continue;
                        }

                        if (origNull) continue; // Both null, OK

                        var origVal = origCol.GetValue(row);
                        var parsedVal = parsedCol.GetValue(row);

                        if (!ValuesEqual(origVal, parsedVal, origCol.DataType))
                        {
                            differences.Add($"Column '{origCol.Name}' row {row} value mismatch: expected {origVal}, got {parsedVal}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            differences.Add($"Round-trip exception: {ex.Message}");
        }

        return new RoundTripResult
        {
            Success = differences.Count == 0,
            Differences = differences.ToArray()
        };
    }

    // ─────────────────────────────────────────────────────────
    // Test batch generation
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a test ColumnarBatch with all 9 ColumnDataType variants including null values.
    /// Used for comprehensive format verification.
    /// </summary>
    public static ColumnarBatch CreateTestBatch()
    {
        const int rowCount = 5;
        var builder = new ColumnarBatchBuilder(rowCount);

        // Add all 9 data type columns
        builder.AddColumn("col_int32", ColumnDataType.Int32);         // 0
        builder.AddColumn("col_int64", ColumnDataType.Int64);         // 1
        builder.AddColumn("col_float64", ColumnDataType.Float64);     // 2
        builder.AddColumn("col_string", ColumnDataType.String);       // 3
        builder.AddColumn("col_bool", ColumnDataType.Bool);           // 4
        builder.AddColumn("col_binary", ColumnDataType.Binary);       // 5
        builder.AddColumn("col_decimal", ColumnDataType.Decimal);     // 6
        builder.AddColumn("col_datetime", ColumnDataType.DateTime);   // 7
        builder.AddColumn("col_null", ColumnDataType.Null);           // 8

        // Row 0: all non-null values
        builder.SetValue(0, 0, 42);
        builder.SetValue(1, 0, 9999999999L);
        builder.SetValue(2, 0, 3.14159);
        builder.SetValue(3, 0, "hello world");
        builder.SetValue(4, 0, true);
        builder.SetValue(5, 0, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        builder.SetValue(6, 0, 123.456m);
        builder.SetValue(7, 0, new DateTime(2026, 2, 24, 12, 0, 0, DateTimeKind.Utc));
        // col_null row 0: leave as null

        // Row 1: different values
        builder.SetValue(0, 1, -100);
        builder.SetValue(1, 1, long.MinValue);
        builder.SetValue(2, 1, -0.001);
        builder.SetValue(3, 1, "");
        builder.SetValue(4, 1, false);
        builder.SetValue(5, 1, Array.Empty<byte>());
        builder.SetValue(6, 1, 0m);
        builder.SetValue(7, 1, DateTime.UnixEpoch);

        // Row 2: nulls in various columns
        builder.SetValue(0, 2, null);
        builder.SetValue(1, 2, 0L);
        builder.SetValue(2, 2, null);
        builder.SetValue(3, 2, "test");
        builder.SetValue(4, 2, null);
        builder.SetValue(5, 2, null);
        builder.SetValue(6, 2, null);
        builder.SetValue(7, 2, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // Row 3: edge case values
        builder.SetValue(0, 3, int.MaxValue);
        builder.SetValue(1, 3, long.MaxValue);
        builder.SetValue(2, 3, double.MaxValue);
        builder.SetValue(3, 3, new string('x', 1000));
        builder.SetValue(4, 3, true);
        builder.SetValue(5, 3, new byte[256]);
        builder.SetValue(6, 3, decimal.MaxValue);
        builder.SetValue(7, 3, new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc));

        // Row 4: more nulls
        builder.SetValue(0, 4, null);
        builder.SetValue(1, 4, null);
        builder.SetValue(2, 4, null);
        builder.SetValue(3, 4, null);
        builder.SetValue(4, 4, null);
        builder.SetValue(5, 4, null);
        builder.SetValue(6, 4, null);
        builder.SetValue(7, 4, null);

        return builder.Build();
    }

    // ─────────────────────────────────────────────────────────
    // External tool compatibility
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Compatibility note for an external tool with a specific format.
    /// </summary>
    public sealed class ExternalToolCompatibility
    {
        /// <summary>External tool name.</summary>
        public string ToolName { get; init; } = string.Empty;

        /// <summary>Format identifier.</summary>
        public string FormatId { get; init; } = string.Empty;

        /// <summary>Compatibility status.</summary>
        public CompatibilityStatus Status { get; init; }

        /// <summary>Notes on compatibility, limitations, or required configuration.</summary>
        public string Notes { get; init; } = string.Empty;
    }

    /// <summary>Compatibility status levels.</summary>
    public enum CompatibilityStatus
    {
        /// <summary>Fully compatible: read and write without issues.</summary>
        Full,
        /// <summary>Read-only compatible: can read but writing may differ.</summary>
        ReadOnly,
        /// <summary>Partial compatibility: some features or types may not work.</summary>
        Partial,
        /// <summary>Not compatible or not tested.</summary>
        Unknown
    }

    /// <summary>
    /// Returns compatibility notes for external tools (pandas, PyArrow, Spark, Hive)
    /// with each columnar format.
    /// </summary>
    public static IReadOnlyList<ExternalToolCompatibility> GetExternalToolCompatibility()
    {
        return new[]
        {
            // pandas
            new ExternalToolCompatibility
            {
                ToolName = "pandas",
                FormatId = "parquet",
                Status = CompatibilityStatus.Full,
                Notes = "pandas.read_parquet() and DataFrame.to_parquet() via pyarrow or fastparquet engine. " +
                        "All DW ColumnDataType variants map to pandas dtypes: Int32->int32, Int64->int64, " +
                        "Float64->float64, String->object, Bool->bool, Binary->object, Decimal->object, DateTime->datetime64[us]."
            },
            new ExternalToolCompatibility
            {
                ToolName = "pandas",
                FormatId = "arrow",
                Status = CompatibilityStatus.Full,
                Notes = "pandas.read_feather() reads Arrow IPC files (Feather v2). ArrowDtype extension arrays " +
                        "provide zero-copy access. Use pa.ipc.RecordBatchFileReader for IPC file format."
            },
            new ExternalToolCompatibility
            {
                ToolName = "pandas",
                FormatId = "orc",
                Status = CompatibilityStatus.Full,
                Notes = "pandas.read_orc() via pyarrow ORC reader. Requires pyarrow >= 6.0. " +
                        "ORC Decimal maps to Python decimal.Decimal, Timestamp to pandas Timestamp."
            },

            // PyArrow
            new ExternalToolCompatibility
            {
                ToolName = "PyArrow",
                FormatId = "parquet",
                Status = CompatibilityStatus.Full,
                Notes = "pyarrow.parquet.read_table() and write_table() are the reference Parquet implementation. " +
                        "Supports column pruning (columns parameter), row group filtering (filters parameter), " +
                        "and all compression codecs (snappy, gzip, zstd, lz4)."
            },
            new ExternalToolCompatibility
            {
                ToolName = "PyArrow",
                FormatId = "arrow",
                Status = CompatibilityStatus.Full,
                Notes = "PyArrow is the reference Arrow implementation. pa.ipc.RecordBatchFileReader for IPC files, " +
                        "pa.ipc.RecordBatchStreamReader for streaming. Zero-copy reads via pa.Buffer. " +
                        "Flight support via pyarrow.flight module."
            },
            new ExternalToolCompatibility
            {
                ToolName = "PyArrow",
                FormatId = "orc",
                Status = CompatibilityStatus.Full,
                Notes = "pyarrow.orc.read_table() and write_table(). Full ORC type system support. " +
                        "Stripe-level statistics accessible via metadata."
            },

            // Apache Spark
            new ExternalToolCompatibility
            {
                ToolName = "Apache Spark",
                FormatId = "parquet",
                Status = CompatibilityStatus.Full,
                Notes = "spark.read.parquet() is Spark's native columnar format. Supports predicate pushdown, " +
                        "column pruning, partition pruning. DW Parquet files readable by Spark SQL directly. " +
                        "Decimal type requires matching precision/scale configuration."
            },
            new ExternalToolCompatibility
            {
                ToolName = "Apache Spark",
                FormatId = "arrow",
                Status = CompatibilityStatus.Partial,
                Notes = "Spark uses Arrow for pandas UDF serialization (spark.sql.execution.arrow.pyspark.enabled). " +
                        "Direct Arrow IPC file reading requires custom DataSource. Arrow Flight via spark-flight-connector."
            },
            new ExternalToolCompatibility
            {
                ToolName = "Apache Spark",
                FormatId = "orc",
                Status = CompatibilityStatus.Full,
                Notes = "spark.read.orc() with native ORC vectorized reader. Supports predicate pushdown via " +
                        "stripe statistics, column pruning, and all ORC compression codecs. " +
                        "Hive-compatible ORC by default."
            },

            // Apache Hive
            new ExternalToolCompatibility
            {
                ToolName = "Apache Hive",
                FormatId = "parquet",
                Status = CompatibilityStatus.Full,
                Notes = "CREATE TABLE ... STORED AS PARQUET. Hive reads Parquet via parquet-mr library. " +
                        "Supports predicate pushdown, column pruning. DateTime stored as INT96 in legacy mode " +
                        "or INT64 TIMESTAMP_MICROS in modern mode (DW uses INT64)."
            },
            new ExternalToolCompatibility
            {
                ToolName = "Apache Hive",
                FormatId = "arrow",
                Status = CompatibilityStatus.Unknown,
                Notes = "Hive does not natively support Arrow IPC as a storage format. " +
                        "Arrow is used internally for vectorized execution (LLAP) but not as a file format. " +
                        "Convert via Parquet or ORC for Hive compatibility."
            },
            new ExternalToolCompatibility
            {
                ToolName = "Apache Hive",
                FormatId = "orc",
                Status = CompatibilityStatus.Full,
                Notes = "ORC is Hive's native storage format. CREATE TABLE ... STORED AS ORC. " +
                        "Full support for stripe statistics, predicate pushdown, ACID transactions, " +
                        "and all ORC compression codecs. DW ORC files are directly Hive-compatible."
            }
        };
    }

    // ─────────────────────────────────────────────────────────
    // Value comparison helpers
    // ─────────────────────────────────────────────────────────

    private static bool ValuesEqual(object? a, object? b, ColumnDataType dataType)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;

        return dataType switch
        {
            ColumnDataType.Int32 => (int)a == (int)b,
            ColumnDataType.Int64 => (long)a == (long)b,
            ColumnDataType.Float64 => Math.Abs((double)a - (double)b) < 1e-10,
            ColumnDataType.String => string.Equals((string)a, (string)b, StringComparison.Ordinal),
            ColumnDataType.Bool => (bool)a == (bool)b,
            ColumnDataType.Binary => ((byte[])a).AsSpan().SequenceEqual((byte[])b),
            ColumnDataType.Decimal => (decimal)a == (decimal)b,
            ColumnDataType.DateTime => Math.Abs(((DateTime)a - (DateTime)b).TotalMicroseconds) < 1,
            _ => Equals(a, b)
        };
    }
}
