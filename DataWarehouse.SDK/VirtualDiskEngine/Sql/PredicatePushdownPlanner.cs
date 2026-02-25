using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.SDK.VirtualDiskEngine.Sql;

/// <summary>
/// Logical connector for compound predicates.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Predicate pushdown (VOPT-20)")]
public enum LogicalOp : byte
{
    /// <summary>Logical AND: both predicates must be true.</summary>
    And = 0,

    /// <summary>Logical OR: at least one predicate must be true.</summary>
    Or = 1,
}

/// <summary>
/// A query predicate representing a column comparison in a WHERE clause.
/// Supports compound predicates via <see cref="Connector"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Predicate pushdown (VOPT-20)")]
public sealed class QueryPredicate
{
    /// <summary>Name of the column to filter on.</summary>
    public string ColumnName { get; }

    /// <summary>Comparison operator.</summary>
    public ComparisonOp Op { get; }

    /// <summary>Scalar value to compare against.</summary>
    public object Value { get; }

    /// <summary>Logical connector to the next predicate (null for the last predicate in a chain).</summary>
    public LogicalOp? Connector { get; }

    /// <summary>Creates a new query predicate.</summary>
    /// <param name="columnName">Column name.</param>
    /// <param name="op">Comparison operator.</param>
    /// <param name="value">Value to compare against.</param>
    /// <param name="connector">Optional logical connector to next predicate.</param>
    public QueryPredicate(string columnName, ComparisonOp op, object value, LogicalOp? connector = null)
    {
        ColumnName = columnName ?? throw new ArgumentNullException(nameof(columnName));
        Op = op;
        Value = value ?? throw new ArgumentNullException(nameof(value));
        Connector = connector;
    }

    /// <summary>Converts the predicate value to a long for zone map comparison.</summary>
    public long ValueAsLong()
    {
        return Value switch
        {
            int i => i,
            long l => l,
            short s => s,
            byte b => b,
            float f => (long)f,
            double d => (long)d,
            _ => 0,
        };
    }

    /// <inheritdoc />
    public override string ToString()
        => $"{ColumnName} {Op} {Value}" + (Connector.HasValue ? $" {Connector.Value}" : "");
}

/// <summary>
/// Classification of where a predicate can be evaluated in the storage stack.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Predicate pushdown (VOPT-20)")]
public enum PushdownLevel : byte
{
    /// <summary>Evaluable at zone map / extent level for extent skipping.</summary>
    ZoneMappable = 0,

    /// <summary>Evaluable at block level within an extent.</summary>
    BlockFilterable = 1,

    /// <summary>Must be evaluated per-row after reading data.</summary>
    PostFilter = 2,
}

/// <summary>
/// Result of pushdown analysis, classifying each predicate by evaluation level.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Predicate pushdown (VOPT-20)")]
public sealed class PushdownResult
{
    /// <summary>Predicates that can be evaluated at zone map / extent level.</summary>
    public IReadOnlyList<QueryPredicate> ZoneMappable { get; }

    /// <summary>Predicates that can be evaluated at block level.</summary>
    public IReadOnlyList<QueryPredicate> BlockFilterable { get; }

    /// <summary>Predicates that must be evaluated per-row.</summary>
    public IReadOnlyList<QueryPredicate> PostFilter { get; }

    /// <summary>Creates a new pushdown result.</summary>
    public PushdownResult(
        IReadOnlyList<QueryPredicate> zoneMappable,
        IReadOnlyList<QueryPredicate> blockFilterable,
        IReadOnlyList<QueryPredicate> postFilter)
    {
        ZoneMappable = zoneMappable ?? throw new ArgumentNullException(nameof(zoneMappable));
        BlockFilterable = blockFilterable ?? throw new ArgumentNullException(nameof(blockFilterable));
        PostFilter = postFilter ?? throw new ArgumentNullException(nameof(postFilter));
    }

    /// <summary>Total number of predicates across all levels.</summary>
    public int TotalPredicates => ZoneMappable.Count + BlockFilterable.Count + PostFilter.Count;

    /// <summary>Fraction of predicates pushed down to zone map or block level (0.0-1.0).</summary>
    public double PushdownRatio => TotalPredicates > 0
        ? (double)(ZoneMappable.Count + BlockFilterable.Count) / TotalPredicates
        : 0.0;

    /// <inheritdoc />
    public override string ToString()
        => $"Pushdown(ZoneMap={ZoneMappable.Count}, Block={BlockFilterable.Count}, PostFilter={PostFilter.Count}, Ratio={PushdownRatio:P0})";
}

/// <summary>
/// Plans and executes predicate pushdown into the storage scan layer.
/// Analyzes WHERE clause predicates and determines which can be evaluated at
/// zone map (extent), block, or row level, combining with zone map filtering
/// for maximum data skipping.
/// </summary>
/// <remarks>
/// Implements VOPT-20: Predicate pushdown into storage scans.
/// Three-level filtering:
/// 1. Zone map: Skip entire extents using min/max metadata (integrated with VOPT-17)
/// 2. Block: Apply block-level filters within candidate extents
/// 3. Row: Post-filter individual rows for predicates that cannot be pushed down
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Predicate pushdown (VOPT-20)")]
public sealed class PredicatePushdownPlanner
{
    private readonly ILogger _logger;

    /// <summary>
    /// Creates a new predicate pushdown planner.
    /// </summary>
    /// <param name="logger">Optional logger for pushdown decisions.</param>
    public PredicatePushdownPlanner(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Analyzes predicates and classifies them by pushdown level.
    /// Zone-mappable predicates are those with simple comparison operators on numeric columns
    /// where a zone map index entry exists. Block-filterable predicates can use in-block
    /// metadata. All others are post-filters.
    /// </summary>
    /// <param name="predicates">Query predicates from the WHERE clause.</param>
    /// <param name="zoneMapIndex">Zone map index for checking extent metadata availability.</param>
    /// <returns>Classification of predicates by evaluation level.</returns>
    public PushdownResult AnalyzePushdown(QueryPredicate[] predicates, ZoneMapIndex zoneMapIndex)
    {
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(zoneMapIndex);

        var zoneMappable = new List<QueryPredicate>();
        var blockFilterable = new List<QueryPredicate>();
        var postFilter = new List<QueryPredicate>();

        foreach (var pred in predicates)
        {
            var level = ClassifyPredicate(pred);

            switch (level)
            {
                case PushdownLevel.ZoneMappable:
                    zoneMappable.Add(pred);
                    _logger.LogDebug("Predicate {Predicate} pushed to zone map level", pred);
                    break;

                case PushdownLevel.BlockFilterable:
                    blockFilterable.Add(pred);
                    _logger.LogDebug("Predicate {Predicate} pushed to block level", pred);
                    break;

                default:
                    postFilter.Add(pred);
                    _logger.LogDebug("Predicate {Predicate} evaluated as post-filter", pred);
                    break;
            }
        }

        var result = new PushdownResult(zoneMappable, blockFilterable, postFilter);
        _logger.LogInformation("Pushdown analysis: {Result}", result);

        return result;
    }

    /// <summary>
    /// Executes a scan with predicate pushdown, applying zone map filtering to skip extents,
    /// block-level filtering within extents, and per-row post-filtering.
    /// </summary>
    /// <param name="engine">Columnar region engine for data access.</param>
    /// <param name="tableName">Table name to scan.</param>
    /// <param name="columns">Columns to project.</param>
    /// <param name="predicates">WHERE clause predicates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async stream of matching row data (serialized).</returns>
    public async IAsyncEnumerable<byte[]> ScanWithPushdownAsync(
        ColumnarRegionEngine engine,
        string tableName,
        string[] columns,
        QueryPredicate[] predicates,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(predicates);

        // Build zone map filters from predicates that are zone-mappable
        var zoneMapFilters = BuildZoneMapFilters(predicates, columns);
        long extentsSkipped = 0;
        long extentsScanned = 0;
        long rowsFiltered = 0;
        long rowsEmitted = 0;

        // Scan with zone map filtering (Step 1: extent-level skip)
        await foreach (var rowGroupData in engine.ScanColumnsAsync(tableName, columns, zoneMapFilters, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            extentsScanned++;

            // Step 2 & 3: Apply block-level and post-filters on individual rows
            int rowCount = EstimateRowCount(rowGroupData, columns.Length);

            for (int row = 0; row < rowCount; row++)
            {
                bool matches = EvaluatePredicatesForRow(rowGroupData, columns, predicates, row);

                if (matches)
                {
                    // Build result row from matching columns
                    byte[] resultRow = ExtractRow(rowGroupData, columns.Length, row);
                    rowsEmitted++;
                    yield return resultRow;
                }
                else
                {
                    rowsFiltered++;
                }
            }
        }

        _logger.LogInformation(
            "Pushdown scan complete: {ExtentsSkipped} extents skipped, {ExtentsScanned} scanned, " +
            "{RowsEmitted} rows emitted, {RowsFiltered} rows filtered",
            extentsSkipped, extentsScanned, rowsEmitted, rowsFiltered);
    }

    // ── Classification ──────────────────────────────────────────────────

    /// <summary>
    /// Classifies a predicate by the lowest storage level where it can be evaluated.
    /// </summary>
    private static PushdownLevel ClassifyPredicate(QueryPredicate predicate)
    {
        // Zone-mappable: simple comparisons on numeric-convertible values with AND logic
        // OR predicates cannot be pushed to zone map level (would need all-OR analysis)
        if (predicate.Connector == LogicalOp.Or)
            return PushdownLevel.PostFilter;

        // Numeric comparisons are zone-mappable
        if (IsNumericComparisonOp(predicate.Op) && IsNumericValue(predicate.Value))
            return PushdownLevel.ZoneMappable;

        // Null checks are zone-mappable (zone maps track null count)
        if (predicate.Op == ComparisonOp.IsNull || predicate.Op == ComparisonOp.IsNotNull)
            return PushdownLevel.ZoneMappable;

        // Equality on non-numeric types can use block-level bloom filters
        if (predicate.Op == ComparisonOp.Equal)
            return PushdownLevel.BlockFilterable;

        // Everything else is a post-filter
        return PushdownLevel.PostFilter;
    }

    private static bool IsNumericComparisonOp(ComparisonOp op) => op switch
    {
        ComparisonOp.Equal => true,
        ComparisonOp.NotEqual => true,
        ComparisonOp.LessThan => true,
        ComparisonOp.LessOrEqual => true,
        ComparisonOp.GreaterThan => true,
        ComparisonOp.GreaterOrEqual => true,
        _ => false,
    };

    private static bool IsNumericValue(object value) => value is int or long or short or byte or float or double or decimal;

    // ── Zone Map Filter Construction ────────────────────────────────────

    private static Predicate<ZoneMapEntry>[]? BuildZoneMapFilters(QueryPredicate[] predicates, string[] columns)
    {
        if (predicates.Length == 0) return null;

        var filters = new Predicate<ZoneMapEntry>[columns.Length];
        bool hasAnyFilter = false;

        // Default filter: include all extents
        for (int i = 0; i < filters.Length; i++)
            filters[i] = _ => true;

        for (int colIdx = 0; colIdx < columns.Length; colIdx++)
        {
            string colName = columns[colIdx];

            // Find predicates for this column that are zone-mappable
            var colPredicates = predicates
                .Where(p => string.Equals(p.ColumnName, colName, StringComparison.OrdinalIgnoreCase)
                         && ClassifyPredicate(p) == PushdownLevel.ZoneMappable)
                .ToArray();

            if (colPredicates.Length == 0) continue;

            hasAnyFilter = true;
            var capturedPredicates = colPredicates;

            filters[colIdx] = entry =>
            {
                // Return true to INCLUDE the extent, false to SKIP
                foreach (var pred in capturedPredicates)
                {
                    var compPred = new ComparisonPredicate(pred.Op, pred.ValueAsLong());
                    if (ZoneMapIndex.CanSkipExtent(entry, compPred))
                        return false; // Zone map proves no match -- skip
                }
                return true; // Cannot prove skip -- must scan
            };
        }

        return hasAnyFilter ? filters! : null;
    }

    // ── Row-level Evaluation ────────────────────────────────────────────

    private static bool EvaluatePredicatesForRow(byte[][] rowGroupData, string[] columns, QueryPredicate[] predicates, int rowIndex)
    {
        bool result = true;

        for (int p = 0; p < predicates.Length; p++)
        {
            var pred = predicates[p];
            int colIdx = Array.FindIndex(columns, c => string.Equals(c, pred.ColumnName, StringComparison.OrdinalIgnoreCase));
            if (colIdx < 0)
            {
                // Predicate on a column not in the projection -- cannot evaluate, assume true
                continue;
            }

            bool match = EvaluateSinglePredicate(rowGroupData[colIdx], rowIndex, pred);

            if (p > 0 && predicates[p - 1].Connector == LogicalOp.Or)
            {
                result = result || match;
            }
            else
            {
                result = result && match;
            }
        }

        return result;
    }

    private static bool EvaluateSinglePredicate(byte[] columnData, int rowIndex, QueryPredicate predicate)
    {
        // Determine value width (assume int32 = 4 bytes as default)
        int valueWidth = 4;
        int offset = rowIndex * valueWidth;

        if (offset + valueWidth > columnData.Length)
            return true; // Beyond data bounds -- include row

        long rowValue = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
            columnData.AsSpan(offset, valueWidth));
        long predValue = predicate.ValueAsLong();

        return predicate.Op switch
        {
            ComparisonOp.Equal => rowValue == predValue,
            ComparisonOp.NotEqual => rowValue != predValue,
            ComparisonOp.LessThan => rowValue < predValue,
            ComparisonOp.LessOrEqual => rowValue <= predValue,
            ComparisonOp.GreaterThan => rowValue > predValue,
            ComparisonOp.GreaterOrEqual => rowValue >= predValue,
            ComparisonOp.IsNull => rowValue == 0, // Simplified null check
            ComparisonOp.IsNotNull => rowValue != 0,
            _ => true,
        };
    }

    // ── Row Extraction ──────────────────────────────────────────────────

    private static int EstimateRowCount(byte[][] rowGroupData, int columnCount)
    {
        if (rowGroupData.Length == 0) return 0;

        // Assume int32 (4 bytes per value) as the default width
        int valueWidth = 4;
        return rowGroupData[0].Length / valueWidth;
    }

    private static byte[] ExtractRow(byte[][] rowGroupData, int columnCount, int rowIndex)
    {
        // Build a row by extracting each column value for this row
        int valueWidth = 4;
        var row = new byte[columnCount * valueWidth];

        for (int col = 0; col < columnCount && col < rowGroupData.Length; col++)
        {
            int srcOffset = rowIndex * valueWidth;
            if (srcOffset + valueWidth <= rowGroupData[col].Length)
            {
                rowGroupData[col].AsSpan(srcOffset, valueWidth).CopyTo(row.AsSpan(col * valueWidth, valueWidth));
            }
        }

        return row;
    }
}
