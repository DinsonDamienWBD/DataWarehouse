namespace DataWarehouse.SDK.Database;

/// <summary>
/// Result of a database query operation.
/// </summary>
public sealed class QueryResult
{
    /// <summary>
    /// Whether the query executed successfully.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error message if the query failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Result rows from a SELECT query.
    /// </summary>
    public IReadOnlyList<Dictionary<string, object?>> Rows { get; init; } = Array.Empty<Dictionary<string, object?>>();

    /// <summary>
    /// Number of rows affected (for INSERT/UPDATE/DELETE).
    /// </summary>
    public int AffectedRows { get; init; }

    /// <summary>
    /// Execution time in milliseconds.
    /// </summary>
    public long ExecutionTimeMs { get; init; }

    /// <summary>
    /// Column metadata if available.
    /// </summary>
    public IReadOnlyList<ColumnInfo>? Columns { get; init; }

    /// <summary>
    /// Whether there are more results available (for pagination).
    /// </summary>
    public bool HasMoreResults { get; init; }

    /// <summary>
    /// Continuation token for paginated results.
    /// </summary>
    public string? ContinuationToken { get; init; }

    /// <summary>
    /// Total row count if known (may be estimated).
    /// </summary>
    public long? TotalCount { get; init; }

    /// <summary>
    /// Additional metadata about the query execution.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Creates an error result.
    /// </summary>
    public static QueryResult Error(string message) => new()
    {
        Success = false,
        ErrorMessage = message,
        Rows = Array.Empty<Dictionary<string, object?>>()
    };

    /// <summary>
    /// Creates a success result from rows.
    /// </summary>
    public static QueryResult FromRows(
        List<Dictionary<string, object?>> rows,
        IReadOnlyList<ColumnInfo>? columns = null,
        long executionTimeMs = 0) => new()
    {
        Success = true,
        Rows = rows,
        Columns = columns,
        AffectedRows = rows.Count,
        ExecutionTimeMs = executionTimeMs
    };

    /// <summary>
    /// Creates a success result for non-query operations.
    /// </summary>
    public static QueryResult FromAffectedRows(int affectedRows, long executionTimeMs = 0) => new()
    {
        Success = true,
        AffectedRows = affectedRows,
        ExecutionTimeMs = executionTimeMs
    };

    /// <summary>
    /// Creates an empty success result.
    /// </summary>
    public static QueryResult Empty(long executionTimeMs = 0) => new()
    {
        Success = true,
        ExecutionTimeMs = executionTimeMs
    };
}

/// <summary>
/// Column metadata for query results.
/// </summary>
public sealed class ColumnInfo
{
    /// <summary>
    /// Column name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Data type name.
    /// </summary>
    public string? DataType { get; init; }

    /// <summary>
    /// Whether the column allows null values.
    /// </summary>
    public bool IsNullable { get; init; }

    /// <summary>
    /// Maximum length for string types.
    /// </summary>
    public int? MaxLength { get; init; }

    /// <summary>
    /// Precision for numeric types.
    /// </summary>
    public int? Precision { get; init; }

    /// <summary>
    /// Scale for numeric types.
    /// </summary>
    public int? Scale { get; init; }

    /// <summary>
    /// Whether this is a primary key column.
    /// </summary>
    public bool IsPrimaryKey { get; init; }

    /// <summary>
    /// Whether this column auto-increments.
    /// </summary>
    public bool IsAutoIncrement { get; init; }
}
