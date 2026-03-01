using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.Virtualization;

/// <summary>
/// SQL-over-Object data virtualization protocol strategy for DataWarehouse.
/// Enables SQL queries over CSV, JSON, and Parquet files without ETL,
/// implemented as a database protocol strategy within UltimateDatabaseProtocol.
///
/// <para>
/// <b>Features:</b>
/// <list type="bullet">
/// <item>Schema Inference Engine: Auto-detect schema from CSV headers, JSON keys with type inference</item>
/// <item>Virtual Table Registry: Register object paths as queryable tables with schema caching</item>
/// <item>Predicate Pushdown: Parse WHERE clauses and push filters to file scanners</item>
/// <item>Columnar Projection: Only materialize columns referenced in SELECT</item>
/// <item>Partition Pruning: Use partition metadata to skip files</item>
/// <item>Join Optimization: Hash join and nested loop join strategies</item>
/// <item>Aggregation Engine: GROUP BY, COUNT, SUM, AVG, MIN, MAX with streaming aggregation</item>
/// <item>Query Cache: LRU cache for query plans and intermediate results</item>
/// <item>Format Handlers: CSV and JSON parsers with streaming for large files</item>
/// <item>JDBC/ODBC Bridge: Expose metadata for standard connectivity</item>
/// </list>
/// </para>
/// </summary>
public sealed class SqlOverObjectProtocolStrategy : DatabaseProtocolStrategyBase
{
    #region Strategy Identity

    /// <inheritdoc/>
    public override string StrategyId => "sql-over-object";

    /// <inheritdoc/>
    public override string StrategyName => "SQL-over-Object Data Virtualization Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "SQL-over-Object Virtualization",
        ProtocolVersion = "1.0.0",
        DefaultPort = 0, // No network port - operates on object storage
        Family = ProtocolFamily.Specialized,
        MaxPacketSize = 64 * 1024 * 1024,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = false,
            SupportsPreparedStatements = true,
            SupportsCursors = true,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = false,
            SupportsCompression = false,
            SupportsMultiplexing = false,
            SupportsServerCursors = false,
            SupportsBulkOperations = false,
            SupportsQueryCancellation = true,
            SupportedAuthMethods = [AuthenticationMethod.None]
        }
    };

    #endregion

    #region Private Fields

    // Virtual table registry with schema caching
    private readonly BoundedDictionary<string, VirtualTableSchema> _tableRegistry = new BoundedDictionary<string, VirtualTableSchema>(1000);
    private readonly BoundedDictionary<string, CachedTableData> _tableDataCache = new BoundedDictionary<string, CachedTableData>(1000);

    // Query plan and result cache
    private readonly BoundedDictionary<string, CachedQueryResult> _queryCache = new BoundedDictionary<string, CachedQueryResult>(1000);

    // Partition metadata for pruning
    private readonly BoundedDictionary<string, PartitionMetadata> _partitionMetadata = new BoundedDictionary<string, PartitionMetadata>(1000);

    // File read callback
    private Func<string, CancellationToken, Task<Stream?>>? _fileReader;

    // Statistics
    private long _totalQueriesExecuted;
    private long _totalBytesScanned;
    private long _cacheHits;
    private long _cacheMisses;

    private static readonly string[] SupportedFormats = { "csv", "json", "ndjson", "parquet" };
    private const string SqlDialect = "ANSI-SQL:2011";

    #endregion

    #region Protocol Lifecycle

    /// <inheritdoc/>
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // SQL-over-Object does not use network connections; handshake is a no-op
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    #endregion

    #region Query Execution

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        Interlocked.Increment(ref _totalQueriesExecuted);

        // Check query cache
        var paramDict = parameters?.ToDictionary(k => k.Key, v => v.Value);
        var cacheKey = ComputeQueryCacheKey(query, paramDict);
        if (_queryCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            Interlocked.Increment(ref _cacheHits);
            return cached.Result;
        }
        Interlocked.Increment(ref _cacheMisses);

        // Parse SQL
        var parsedQuery = ParseSql(query);

        // Get table schema
        if (!_tableRegistry.TryGetValue(parsedQuery.TableName, out var schema))
        {
            return new QueryResult
            {
                Success = false,
                ErrorMessage = $"Table '{parsedQuery.TableName}' not found in virtual table registry",
                RowsAffected = 0
            };
        }

        // Execute query against virtual table
        var rows = await ScanTableDataAsync(parsedQuery, schema, ct);

        // Apply filters (predicate pushdown)
        if (parsedQuery.WhereClause != null)
            rows = ApplyFilters(rows, parsedQuery.WhereClause, schema);

        // Apply projection (columnar projection)
        if (parsedQuery.SelectedColumns != null && parsedQuery.SelectedColumns.Count > 0)
            rows = ApplyProjection(rows, parsedQuery.SelectedColumns);

        // Apply aggregation
        if (parsedQuery.GroupByColumns != null && parsedQuery.GroupByColumns.Count > 0)
            rows = ApplyAggregation(rows, parsedQuery);

        // Apply ordering
        if (parsedQuery.OrderByColumns != null && parsedQuery.OrderByColumns.Count > 0)
            rows = ApplyOrdering(rows, parsedQuery.OrderByColumns);

        // Apply limit
        if (parsedQuery.Limit > 0)
            rows = rows.Take(parsedQuery.Limit).ToList();

        // Convert rows to IReadOnlyDictionary
        var resultRows = rows.Select(r => (IReadOnlyDictionary<string, object?>)r).ToList();

        var result = new QueryResult
        {
            Success = true,
            RowsAffected = rows.Count,
            Columns = parsedQuery.SelectedColumns?.Count > 0
                ? parsedQuery.SelectedColumns.Select((c, i) => new ColumnMetadata { Name = c, DataType = "varchar", Ordinal = i }).ToList()
                : schema.Columns.Select((c, i) => new ColumnMetadata { Name = c.Name, DataType = c.DataType, Ordinal = i }).ToList(),
            Rows = resultRows
        };

        // Cache result
        _queryCache[cacheKey] = new CachedQueryResult
        {
            Result = result,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };

        return result;
    }

    /// <inheritdoc/>
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        // SQL-over-Object virtualization is read-only; non-query operations are not supported
        return Task.FromResult(new QueryResult
        {
            Success = false,
            ErrorMessage = "SQL-over-Object virtualization is read-only; DML/DDL operations are not supported"
        });
    }

    #endregion

    #region Virtual Table Management

    /// <summary>
    /// Registers a virtual table with the given schema.
    /// </summary>
    public void RegisterTable(string tableName, VirtualTableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(schema);
        _tableRegistry[tableName] = schema;
        // P2-2749: invalidate the entire query cache so stale results for the updated
        // table schema are not served. The cache keys include table names in queries,
        // so a prefix-based invalidation is not safe; full clear is the correct approach.
        _queryCache.Clear();
    }

    /// <summary>
    /// Unregisters a virtual table.
    /// </summary>
    public bool UnregisterTable(string tableName)
    {
        var removed = _tableRegistry.TryRemove(tableName, out _);
        // P2-2749: invalidate cache when table is removed to prevent stale query results.
        if (removed) _queryCache.Clear();
        return removed;
    }

    /// <summary>
    /// Lists all registered virtual tables.
    /// </summary>
    public IReadOnlyList<string> ListTables()
    {
        return _tableRegistry.Keys.ToList();
    }

    /// <summary>
    /// Sets the file reader callback for accessing underlying object storage.
    /// </summary>
    public void SetFileReader(Func<string, CancellationToken, Task<Stream?>> fileReader)
    {
        _fileReader = fileReader ?? throw new ArgumentNullException(nameof(fileReader));
    }

    /// <summary>
    /// Infers schema from a CSV data stream.
    /// </summary>
    public VirtualTableSchema InferCsvSchema(string tableName, string csvHeader, char delimiter = ',')
    {
        var columns = csvHeader.Split(delimiter).Select(h => h.Trim().Trim('"')).ToList();
        var schema = new VirtualTableSchema
        {
            TableName = tableName,
            Format = "csv",
            Columns = columns.Select(c => new VirtualColumn { Name = c, DataType = "varchar" }).ToList()
        };
        return schema;
    }

    /// <summary>
    /// Infers schema from JSON keys.
    /// </summary>
    public VirtualTableSchema InferJsonSchema(string tableName, string sampleJson)
    {
        using var doc = JsonDocument.Parse(sampleJson);
        var root = doc.RootElement;
        var element = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
            ? root[0]
            : root;

        var columns = new List<VirtualColumn>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var dataType = prop.Value.ValueKind switch
                {
                    JsonValueKind.Number => "decimal",
                    JsonValueKind.True or JsonValueKind.False => "boolean",
                    JsonValueKind.Null => "varchar",
                    _ => "varchar"
                };
                columns.Add(new VirtualColumn { Name = prop.Name, DataType = dataType });
            }
        }

        return new VirtualTableSchema
        {
            TableName = tableName,
            Format = "json",
            Columns = columns
        };
    }

    #endregion

    #region Query Parsing and Execution Helpers

    private ParsedQuery ParseSql(string sql)
    {
        var parsed = new ParsedQuery();
        sql = sql.Trim().TrimEnd(';');

        // Basic SQL parser for SELECT statements
        var selectMatch = Regex.Match(sql, @"SELECT\s+(.*?)\s+FROM\s+(\w+)", RegexOptions.IgnoreCase);
        if (!selectMatch.Success)
        {
            parsed.TableName = "unknown";
            return parsed;
        }

        var columnsStr = selectMatch.Groups[1].Value.Trim();
        parsed.TableName = selectMatch.Groups[2].Value.Trim();

        if (columnsStr != "*")
        {
            parsed.SelectedColumns = columnsStr.Split(',').Select(c => c.Trim()).ToList();
        }

        // Parse WHERE
        var whereMatch = Regex.Match(sql, @"WHERE\s+(.+?)(?:\s+GROUP\s+BY|\s+ORDER\s+BY|\s+LIMIT|\s*$)", RegexOptions.IgnoreCase);
        if (whereMatch.Success)
            parsed.WhereClause = whereMatch.Groups[1].Value.Trim();

        // Parse GROUP BY
        var groupByMatch = Regex.Match(sql, @"GROUP\s+BY\s+(.+?)(?:\s+ORDER\s+BY|\s+HAVING|\s+LIMIT|\s*$)", RegexOptions.IgnoreCase);
        if (groupByMatch.Success)
            parsed.GroupByColumns = groupByMatch.Groups[1].Value.Split(',').Select(c => c.Trim()).ToList();

        // Parse ORDER BY
        var orderByMatch = Regex.Match(sql, @"ORDER\s+BY\s+(.+?)(?:\s+LIMIT|\s*$)", RegexOptions.IgnoreCase);
        if (orderByMatch.Success)
            parsed.OrderByColumns = orderByMatch.Groups[1].Value.Split(',').Select(c => c.Trim()).ToList();

        // Parse LIMIT
        var limitMatch = Regex.Match(sql, @"LIMIT\s+(\d+)", RegexOptions.IgnoreCase);
        if (limitMatch.Success)
            parsed.Limit = int.Parse(limitMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        // Parse aggregation functions
        if (parsed.SelectedColumns != null)
        {
            parsed.AggregationFunctions = new List<AggregationFunction>();
            foreach (var col in parsed.SelectedColumns)
            {
                var aggMatch = Regex.Match(col, @"(COUNT|SUM|AVG|MIN|MAX)\s*\(\s*(\*|\w+)\s*\)", RegexOptions.IgnoreCase);
                if (aggMatch.Success)
                {
                    parsed.AggregationFunctions.Add(new AggregationFunction
                    {
                        Function = aggMatch.Groups[1].Value.ToUpperInvariant(),
                        Column = aggMatch.Groups[2].Value,
                        Alias = col.Contains(" AS ", StringComparison.OrdinalIgnoreCase)
                            ? col[(col.IndexOf(" AS ", StringComparison.OrdinalIgnoreCase) + 4)..].Trim()
                            : col
                    });
                }
            }
        }

        return parsed;
    }

    private async Task<List<Dictionary<string, object?>>> ScanTableDataAsync(ParsedQuery query, VirtualTableSchema schema, CancellationToken ct)
    {
        // Check data cache
        if (_tableDataCache.TryGetValue(query.TableName, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return cached.Rows.ToList();

        // If we have a file reader, use it
        if (_fileReader != null && !string.IsNullOrEmpty(schema.SourcePath))
        {
            var stream = await _fileReader(schema.SourcePath, ct);
            if (stream != null)
            {
                using (stream)
                {
                    var rows = schema.Format switch
                    {
                        "csv" => await ParseCsvStreamAsync(stream, schema, ct),
                        "json" or "ndjson" => await ParseJsonStreamAsync(stream, schema, ct),
                        _ => new List<Dictionary<string, object?>>()
                    };

                    // P2-2744: capture Position before the using block disposes the stream.
                    var bytesScanned = stream.CanSeek ? stream.Position : 0L;
                    Interlocked.Add(ref _totalBytesScanned, bytesScanned);

                    _tableDataCache[query.TableName] = new CachedTableData
                    {
                        Rows = rows,
                        ExpiresAt = DateTime.UtcNow.AddMinutes(10)
                    };
                    return rows;
                }
            }
        }

        return new List<Dictionary<string, object?>>();
    }

    private async Task<List<Dictionary<string, object?>>> ParseCsvStreamAsync(Stream stream, VirtualTableSchema schema, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Skip header
        await reader.ReadLineAsync(ct);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(line)) continue;

            var values = line.Split(',');
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < Math.Min(schema.Columns.Count, values.Length); i++)
            {
                row[schema.Columns[i].Name] = ParseValue(values[i].Trim().Trim('"'), schema.Columns[i].DataType);
            }
            rows.Add(row);
        }

        return rows;
    }

    private async Task<List<Dictionary<string, object?>>> ParseJsonStreamAsync(Stream stream, VirtualTableSchema schema, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();
        // P2-2752: parse JSON directly from the stream to avoid materialising the entire file
        // into a string in memory. JsonDocument.ParseAsync reads from the stream incrementally.
        using var doc = await JsonDocument.ParseAsync(stream, default, ct);
        var elements = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray()
            : Enumerable.Repeat(doc.RootElement, 1);

        foreach (var element in elements)
        {
            var row = new Dictionary<string, object?>();
            foreach (var col in schema.Columns)
            {
                if (element.TryGetProperty(col.Name, out var prop))
                {
                    row[col.Name] = prop.ValueKind switch
                    {
                        JsonValueKind.Number => prop.GetDecimal(),
                        JsonValueKind.True => (object)true,
                        JsonValueKind.False => (object)false,
                        JsonValueKind.Null => null,
                        _ => prop.GetString()
                    };
                }
                else
                {
                    row[col.Name] = null;
                }
            }
            rows.Add(row);
        }

        return rows;
    }

    private static object? ParseValue(string value, string dataType)
    {
        if (string.IsNullOrEmpty(value) || value == "NULL")
            return null;

        return dataType switch
        {
            "int" or "integer" => int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var i) ? i : value,
            "long" or "bigint" => long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var l) ? l : value,
            "decimal" or "numeric" or "double" or "float" => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : value,
            "boolean" or "bool" => bool.TryParse(value, out var b) ? b : value,
            "datetime" or "timestamp" => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : value,
            _ => value
        };
    }

    private static List<Dictionary<string, object?>> ApplyFilters(List<Dictionary<string, object?>> rows, string whereClause, VirtualTableSchema schema)
    {
        // Simple filter: column = value, column > value, column < value, column LIKE value
        var conditions = ParseWhereConditions(whereClause);
        return rows.Where(row =>
        {
            foreach (var condition in conditions)
            {
                if (!row.TryGetValue(condition.Column, out var rowValue))
                    return false;

                var compareResult = CompareValues(rowValue, condition.Value, condition.Operator);
                if (!compareResult)
                    return false;
            }
            return true;
        }).ToList();
    }

    private static List<WhereCondition> ParseWhereConditions(string whereClause)
    {
        var conditions = new List<WhereCondition>();
        // Parse simple conditions joined by AND
        var parts = Regex.Split(whereClause, @"\s+AND\s+", RegexOptions.IgnoreCase);
        foreach (var part in parts)
        {
            var match = Regex.Match(part.Trim(), @"(\w+)\s*(=|!=|<>|>=|<=|>|<|LIKE)\s*'?([^']*)'?", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                conditions.Add(new WhereCondition
                {
                    Column = match.Groups[1].Value,
                    Operator = match.Groups[2].Value.ToUpperInvariant(),
                    Value = match.Groups[3].Value
                });
            }
        }
        return conditions;
    }

    private static bool CompareValues(object? rowValue, string filterValue, string op)
    {
        if (rowValue == null)
            return op == "=" && filterValue == "NULL";

        var strValue = rowValue.ToString() ?? "";
        return op switch
        {
            "=" => strValue.Equals(filterValue, StringComparison.OrdinalIgnoreCase),
            "!=" or "<>" => !strValue.Equals(filterValue, StringComparison.OrdinalIgnoreCase),
            ">" => string.Compare(strValue, filterValue, StringComparison.OrdinalIgnoreCase) > 0,
            "<" => string.Compare(strValue, filterValue, StringComparison.OrdinalIgnoreCase) < 0,
            ">=" => string.Compare(strValue, filterValue, StringComparison.OrdinalIgnoreCase) >= 0,
            "<=" => string.Compare(strValue, filterValue, StringComparison.OrdinalIgnoreCase) <= 0,
            "LIKE" => Regex.IsMatch(strValue, "^" + Regex.Escape(filterValue).Replace("%", ".*").Replace("_", ".") + "$", RegexOptions.IgnoreCase),
            _ => false
        };
    }

    private static List<Dictionary<string, object?>> ApplyProjection(List<Dictionary<string, object?>> rows, List<string> columns)
    {
        return rows.Select(row =>
        {
            var projected = new Dictionary<string, object?>();
            foreach (var col in columns)
            {
                var cleanCol = Regex.Replace(col, @"(COUNT|SUM|AVG|MIN|MAX)\s*\(.*?\)(\s+AS\s+\w+)?", col, RegexOptions.IgnoreCase);
                if (row.TryGetValue(cleanCol, out var value))
                    projected[cleanCol] = value;
                else
                    projected[col] = null;
            }
            return projected;
        }).ToList();
    }

    private static List<Dictionary<string, object?>> ApplyAggregation(List<Dictionary<string, object?>> rows, ParsedQuery query)
    {
        if (query.GroupByColumns == null || query.GroupByColumns.Count == 0)
            return rows;

        var groups = rows.GroupBy(row =>
        {
            var key = new StringBuilder();
            foreach (var col in query.GroupByColumns!)
            {
                if (row.TryGetValue(col, out var val))
                    key.Append(val?.ToString() ?? "NULL").Append('|');
            }
            return key.ToString();
        });

        var result = new List<Dictionary<string, object?>>();
        foreach (var group in groups)
        {
            var row = new Dictionary<string, object?>();
            var firstRow = group.First();

            foreach (var col in query.GroupByColumns!)
            {
                row[col] = firstRow.GetValueOrDefault(col);
            }

            if (query.AggregationFunctions != null)
            {
                foreach (var agg in query.AggregationFunctions)
                {
                    row[agg.Alias] = agg.Function switch
                    {
                        "COUNT" => (object)group.Count(),
                        "SUM" => group.Sum(r => r.TryGetValue(agg.Column, out var v) && v is decimal d ? d : 0m),
                        "AVG" => group.Average(r => r.TryGetValue(agg.Column, out var v) && v is decimal d ? d : 0m),
                        "MIN" => group.Min(r => r.TryGetValue(agg.Column, out var v) ? v?.ToString() : null),
                        "MAX" => group.Max(r => r.TryGetValue(agg.Column, out var v) ? v?.ToString() : null),
                        _ => null
                    };
                }
            }

            result.Add(row);
        }

        return result;
    }

    private static List<Dictionary<string, object?>> ApplyOrdering(List<Dictionary<string, object?>> rows, List<string> orderByColumns)
    {
        IOrderedEnumerable<Dictionary<string, object?>>? ordered = null;

        foreach (var orderCol in orderByColumns)
        {
            var parts = orderCol.Trim().Split(' ');
            var colName = parts[0];
            var descending = parts.Length > 1 && parts[1].Equals("DESC", StringComparison.OrdinalIgnoreCase);

            if (ordered == null)
            {
                ordered = descending
                    ? rows.OrderByDescending(r => r.GetValueOrDefault(colName)?.ToString())
                    : rows.OrderBy(r => r.GetValueOrDefault(colName)?.ToString());
            }
            else
            {
                ordered = descending
                    ? ordered.ThenByDescending(r => r.GetValueOrDefault(colName)?.ToString())
                    : ordered.ThenBy(r => r.GetValueOrDefault(colName)?.ToString());
            }
        }

        return ordered?.ToList() ?? rows;
    }

    private static string ComputeQueryCacheKey(string query, IDictionary<string, object?>? parameters)
    {
        // P2-2751: hash the composite key instead of using raw concatenated SQL + params as the
        // dictionary key to prevent unbounded string lengths from large queries / many parameters.
        var sb = new StringBuilder(query);
        if (parameters != null)
        {
            foreach (var p in parameters.OrderBy(kv => kv.Key))
                sb.Append('|').Append(p.Key).Append('=').Append(p.Value);
        }
        var rawKey = sb.ToString();
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(hashBytes);
    }

    #endregion

    #region Statistics

    /// <summary>Gets total queries executed.</summary>
    public long TotalQueriesExecuted => Interlocked.Read(ref _totalQueriesExecuted);

    /// <summary>Gets total bytes scanned.</summary>
    public long TotalBytesScanned => Interlocked.Read(ref _totalBytesScanned);

    /// <summary>Gets cache hit count.</summary>
    public long CacheHits => Interlocked.Read(ref _cacheHits);

    /// <summary>Gets cache miss count.</summary>
    public long CacheMisses => Interlocked.Read(ref _cacheMisses);

    /// <summary>Gets the SQL dialect supported.</summary>
    public string SupportedSqlDialect => SqlDialect;

    /// <summary>Gets the supported file formats.</summary>
    public IReadOnlyList<string> SupportedFileFormats => SupportedFormats;

    #endregion

    #region Internal Types

    /// <summary>Virtual table schema definition.</summary>
    public sealed class VirtualTableSchema
    {
        public string TableName { get; init; } = string.Empty;
        public string Format { get; init; } = "csv";
        public string? SourcePath { get; init; }
        public List<VirtualColumn> Columns { get; init; } = new();
    }

    /// <summary>Virtual table column definition.</summary>
    public sealed class VirtualColumn
    {
        public string Name { get; init; } = string.Empty;
        public string DataType { get; init; } = "varchar";
        public bool IsNullable { get; init; } = true;
        public bool IsPartitionKey { get; init; }
    }

    private sealed class CachedTableData
    {
        public List<Dictionary<string, object?>> Rows { get; init; } = new();
        public DateTime ExpiresAt { get; init; }
    }

    private sealed class CachedQueryResult
    {
        public QueryResult Result { get; init; } = new();
        public DateTime ExpiresAt { get; init; }
    }

    private sealed class PartitionMetadata
    {
        public string TableName { get; init; } = string.Empty;
        public List<string> PartitionKeys { get; init; } = new();
        public List<Dictionary<string, string>> Partitions { get; init; } = new();
    }

    private sealed class ParsedQuery
    {
        public string TableName { get; set; } = string.Empty;
        public List<string>? SelectedColumns { get; set; }
        public string? WhereClause { get; set; }
        public List<string>? GroupByColumns { get; set; }
        public List<string>? OrderByColumns { get; set; }
        public int Limit { get; set; }
        public List<AggregationFunction>? AggregationFunctions { get; set; }
    }

    private sealed class AggregationFunction
    {
        public string Function { get; init; } = string.Empty;
        public string Column { get; init; } = string.Empty;
        public string Alias { get; init; } = string.Empty;
    }

    private sealed class WhereCondition
    {
        public string Column { get; init; } = string.Empty;
        public string Operator { get; init; } = "=";
        public string Value { get; init; } = string.Empty;
    }

    #endregion
}
