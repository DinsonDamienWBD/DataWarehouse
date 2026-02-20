using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Query;
using QueryJoinType = DataWarehouse.SDK.Contracts.Query.JoinType;
using PlanNode = DataWarehouse.SDK.Contracts.QueryPlanNode;
using QueryPlanNode = DataWarehouse.SDK.Contracts.QueryPlanNode;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.Virtualization.SqlOverObject;

/// <summary>
/// Production-ready SQL-over-Object Data Virtualization plugin.
/// Enables SQL queries over CSV, JSON, and Parquet files without ETL.
///
/// Features (Task 71):
/// - 71.1 Schema Inference Engine: Auto-detect schema from CSV headers, JSON keys with type inference
/// - 71.2 Virtual Table Registry: Register object paths as queryable tables with schema caching
/// - 71.3 Predicate Pushdown: Parse WHERE clauses and push filters to file scanners
/// - 71.4 Columnar Projection: Only materialize columns referenced in SELECT
/// - 71.5 Partition Pruning: Use partition metadata (path patterns like /year=2024/) to skip files
/// - 71.6 Join Optimization: Hash join and nested loop join strategies
/// - 71.7 Aggregation Engine: GROUP BY, COUNT, SUM, AVG, MIN, MAX with streaming aggregation
/// - 71.8 Query Cache: LRU cache for query plans and intermediate results
/// - 71.9 Format Handlers: CSV and JSON parsers with streaming for large files
/// - 71.10 JDBC/ODBC Bridge: Expose metadata for standard connectivity
/// </summary>
public sealed class SqlOverObjectPlugin : DataVirtualizationPluginBase
{
    /// <inheritdoc />
    public override string Id => "com.datawarehouse.virtualization.sql";

    /// <inheritdoc />
    public override string Name => "SQL-over-Object Data Virtualization";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string SqlDialect => "ANSI-SQL:2011";

    /// <inheritdoc />
    public override IReadOnlyList<VirtualTableFormat> SupportedFormats => new[]
    {
        VirtualTableFormat.Csv,
        VirtualTableFormat.Json,
        VirtualTableFormat.NdJson,
        VirtualTableFormat.Parquet
    };

    // Virtual table registry with schema caching
    internal readonly ConcurrentDictionary<string, VirtualTableSchema> _tableRegistry = new(StringComparer.OrdinalIgnoreCase);
    internal readonly ConcurrentDictionary<string, CachedTableData> _tableDataCache = new(StringComparer.OrdinalIgnoreCase);

    // Query plan and result cache (LRU-style with capacity limit)
    private readonly QueryCache _queryCache = new(maxEntries: 1000);

    // Partition metadata for pruning
    private readonly ConcurrentDictionary<string, PartitionMetadata> _partitionMetadata = new(StringComparer.OrdinalIgnoreCase);

    // File read callback for reading data from storage
    internal Func<string, CancellationToken, Task<Stream?>>? _fileReader;

    // Statistics
    private long _totalQueriesExecuted;
    private long _totalBytesScanned;
    private long _cacheHits;
    private long _cacheMisses;

    // New query engine infrastructure (Plans 01-04)
    private QueryExecutionEngine? _queryEngine;
    private ISqlParser? _sqlParser;
    private bool _useNewEngine = true;

    /// <summary>
    /// Initializes a new instance of the SqlOverObjectPlugin.
    /// </summary>
    public SqlOverObjectPlugin()
    {
    }

    /// <summary>
    /// Sets the file reader callback for accessing storage.
    /// </summary>
    /// <param name="fileReader">Function to read files from storage.</param>
    public void SetFileReader(Func<string, CancellationToken, Task<Stream?>> fileReader)
    {
        _fileReader = fileReader ?? throw new ArgumentNullException(nameof(fileReader));
    }

    /// <inheritdoc />
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        // Extract file reader from configuration if provided
        if (request.Config?.TryGetValue("fileReader", out var readerObj) == true &&
            readerObj is Func<string, CancellationToken, Task<Stream?>> reader)
        {
            _fileReader = reader;
        }

        // Initialize the new query engine infrastructure
        InitializeQueryEngine(request);

        return new HandshakeResponse
        {
            PluginId = Id,
            Name = Name,
            Version = ParseSemanticVersion(Version),
            Category = Category,
            Success = true,
            ReadyState = PluginReadyState.Ready,
            Capabilities = GetCapabilities(),
            Metadata = GetMetadata()
        };
    }

    /// <summary>
    /// Initializes the query execution engine with the SQL parser, cost-based planner,
    /// and columnar engine from Plans 01-04.
    /// </summary>
    private void InitializeQueryEngine(HandshakeRequest request)
    {
        try
        {
            _sqlParser = new SqlParserEngine();
            var planner = new CostBasedQueryPlanner();
            var dataSource = new PluginDataSourceProvider(this);

            // Wire ITagProvider to DW tag system via MessageBus if available
            ITagProvider? tagProvider = null;
            if (MessageBus != null)
            {
                tagProvider = new MessageBusTagProvider(MessageBus);
            }

            _queryEngine = new QueryExecutionEngine(_sqlParser, planner, dataSource, tagProvider);
        }
        catch (Exception)
        {
            // If initialization fails, fall back to legacy parser
            _useNewEngine = false;
            _queryEngine = null;
        }
    }

    /// <inheritdoc />
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new()
            {
                Name = "schema_inference",
                DisplayName = "Schema Inference",
                Description = "Auto-detect schema from CSV headers, JSON keys with type inference"
            },
            new()
            {
                Name = "virtual_table_registry",
                DisplayName = "Virtual Table Registry",
                Description = "Register object paths as queryable tables with schema caching"
            },
            new()
            {
                Name = "predicate_pushdown",
                DisplayName = "Predicate Pushdown",
                Description = "Push WHERE filters to file scanners for early filtering"
            },
            new()
            {
                Name = "columnar_projection",
                DisplayName = "Columnar Projection",
                Description = "Only materialize columns referenced in SELECT"
            },
            new()
            {
                Name = "partition_pruning",
                DisplayName = "Partition Pruning",
                Description = "Use partition metadata to skip irrelevant files"
            },
            new()
            {
                Name = "join_optimization",
                DisplayName = "Join Optimization",
                Description = "Hash join and nested loop join strategies"
            },
            new()
            {
                Name = "aggregation_engine",
                DisplayName = "Aggregation Engine",
                Description = "GROUP BY, COUNT, SUM, AVG, MIN, MAX with streaming aggregation"
            },
            new()
            {
                Name = "query_cache",
                DisplayName = "Query Cache",
                Description = "LRU cache for query plans and intermediate results"
            },
            new()
            {
                Name = "format_handlers",
                DisplayName = "Format Handlers",
                Description = "CSV and JSON parsers with streaming support"
            },
            new()
            {
                Name = "jdbc_odbc_bridge",
                DisplayName = "JDBC/ODBC Bridge",
                Description = "Expose metadata for standard connectivity"
            },
            new()
            {
                Name = "cost_based_planner",
                DisplayName = "Cost-Based Query Planner",
                Description = "Cost-based query optimization with cardinality estimation and join reordering"
            },
            new()
            {
                Name = "columnar_execution",
                DisplayName = "Columnar Execution Engine",
                Description = "Vectorized columnar batch processing with efficient aggregation"
            },
            new()
            {
                Name = "tag_queries",
                DisplayName = "Tag-Aware Queries",
                Description = "SQL queries with tag(), has_tag(), tag_count() functions for DW metadata"
            }
        };
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["TotalQueriesExecuted"] = Interlocked.Read(ref _totalQueriesExecuted);
        metadata["TotalBytesScanned"] = Interlocked.Read(ref _totalBytesScanned);
        metadata["CacheHits"] = Interlocked.Read(ref _cacheHits);
        metadata["CacheMisses"] = Interlocked.Read(ref _cacheMisses);
        metadata["RegisteredTables"] = _tableRegistry.Count;
        metadata["CachedQueries"] = _queryCache.Count;
        metadata["SupportsPredicatePushdown"] = true;
        metadata["SupportsPartitionPruning"] = true;
        metadata["SupportsJoins"] = true;
        metadata["SupportsAggregations"] = true;
        return metadata;
    }

    #region 71.1 Schema Inference Engine

    /// <inheritdoc />
    protected override async Task<VirtualTableSchema> InferSchemaFromSourceAsync(
        string tableName,
        string sourcePath,
        VirtualTableFormat format,
        CancellationToken ct)
    {
        if (_fileReader == null)
        {
            throw new InvalidOperationException("File reader not configured. Call SetFileReader() first.");
        }

        using var stream = await _fileReader(sourcePath, ct)
            ?? throw new FileNotFoundException($"Source file not found: {sourcePath}");

        return format switch
        {
            VirtualTableFormat.Csv => await InferCsvSchemaAsync(tableName, sourcePath, stream, ct),
            VirtualTableFormat.Json => await InferJsonSchemaAsync(tableName, sourcePath, stream, ct),
            VirtualTableFormat.NdJson => await InferNdJsonSchemaAsync(tableName, sourcePath, stream, ct),
            VirtualTableFormat.Parquet => await InferParquetSchemaAsync(tableName, sourcePath, stream, ct),
            _ => throw new NotSupportedException($"Format {format} is not supported for schema inference")
        };
    }

    private async Task<VirtualTableSchema> InferCsvSchemaAsync(
        string tableName, string sourcePath, Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        // Read header line
        var headerLine = await reader.ReadLineAsync(ct);
        if (string.IsNullOrEmpty(headerLine))
        {
            throw new InvalidDataException("CSV file is empty or has no header");
        }

        var headers = ParseCsvLine(headerLine);
        var sampleRows = new List<IReadOnlyList<object?>>();

        // Read sample rows for type inference (up to 100 rows)
        for (int i = 0; i < 100 && !reader.EndOfStream; i++)
        {
            var line = await reader.ReadLineAsync(ct);
            if (!string.IsNullOrEmpty(line))
            {
                var values = ParseCsvLine(line);
                var row = new List<object?>();
                for (int j = 0; j < headers.Count; j++)
                {
                    row.Add(j < values.Count ? (object?)values[j] : null);
                }
                sampleRows.Add(row);
            }
        }

        // Infer schema from samples
        var schema = SchemaInference.InferSchema(tableName, headers, sampleRows, sourcePath, VirtualTableFormat.Csv);

        // Extract partition metadata from path
        ExtractPartitionMetadata(tableName, sourcePath);

        return schema;
    }

    private async Task<VirtualTableSchema> InferJsonSchemaAsync(
        string tableName, string sourcePath, Stream stream, CancellationToken ct)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;

        // Handle JSON array of objects
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("JSON file must contain an array of objects");
        }

        var headers = new HashSet<string>();
        var sampleRows = new List<IReadOnlyList<object?>>();
        var columnTypes = new Dictionary<string, List<SqlDataType>>();

        int rowCount = 0;
        foreach (var element in root.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            if (rowCount++ >= 100) break;

            var row = new Dictionary<string, object?>();
            foreach (var property in element.EnumerateObject())
            {
                headers.Add(property.Name);
                var value = JsonElementToObject(property.Value);
                row[property.Name] = value;

                if (!columnTypes.ContainsKey(property.Name))
                    columnTypes[property.Name] = new List<SqlDataType>();
                columnTypes[property.Name].Add(SchemaInference.InferType(value));
            }

            // Convert to ordered list
            var orderedHeaders = headers.ToList();
            sampleRows.Add(orderedHeaders.Select(h => row.GetValueOrDefault(h)).ToList());
        }

        var schema = SchemaInference.InferSchema(tableName, headers.ToList(), sampleRows, sourcePath, VirtualTableFormat.Json);
        ExtractPartitionMetadata(tableName, sourcePath);
        return schema;
    }

    private async Task<VirtualTableSchema> InferNdJsonSchemaAsync(
        string tableName, string sourcePath, Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var headers = new HashSet<string>();
        var sampleRows = new List<IReadOnlyList<object?>>();

        for (int i = 0; i < 100 && !reader.EndOfStream; i++)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var element = doc.RootElement;
            if (element.ValueKind != JsonValueKind.Object) continue;

            var row = new Dictionary<string, object?>();
            foreach (var property in element.EnumerateObject())
            {
                headers.Add(property.Name);
                row[property.Name] = JsonElementToObject(property.Value);
            }

            var orderedHeaders = headers.ToList();
            sampleRows.Add(orderedHeaders.Select(h => row.GetValueOrDefault(h)).ToList());
        }

        var schema = SchemaInference.InferSchema(tableName, headers.ToList(), sampleRows, sourcePath, VirtualTableFormat.NdJson);
        ExtractPartitionMetadata(tableName, sourcePath);
        return schema;
    }

    private Task<VirtualTableSchema> InferParquetSchemaAsync(
        string tableName, string sourcePath, Stream stream, CancellationToken ct)
    {
        // Parquet has embedded schema - read the footer metadata
        // For now, return a placeholder schema. Full Parquet support requires
        // a Parquet library like Parquet.NET
        var columns = new List<VirtualTableColumn>
        {
            new() { Name = "_parquet_data", DataType = SqlDataType.Binary, Ordinal = 0, Description = "Raw Parquet data" }
        };

        return Task.FromResult(new VirtualTableSchema
        {
            TableName = tableName,
            Columns = columns,
            SourceFormat = VirtualTableFormat.Parquet,
            SourcePath = sourcePath,
            IsInferred = true,
            LastUpdated = DateTime.UtcNow,
            Metadata = new Dictionary<string, string> { ["note"] = "Full Parquet schema requires Parquet.NET library" }
        });
    }

    private static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt32(out var i) => i,
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number when element.TryGetDouble(out var d) => d,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.Object => element,
            _ => element.GetRawText()
        };
    }

    #endregion

    #region 71.2 Virtual Table Registry

    /// <inheritdoc />
    public override async Task<VirtualTableSchema> RegisterTableAsync(
        string tableName,
        string sourcePath,
        VirtualTableFormat format,
        VirtualTableSchema? schema = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name cannot be empty", nameof(tableName));

        var tableSchema = schema ?? await InferSchemaFromSourceAsync(tableName, sourcePath, format, ct);

        // Cache the schema
        _tableRegistry[tableName] = tableSchema;

        // Pre-load data if file is small enough
        if (_fileReader != null)
        {
            await TryPreloadTableDataAsync(tableName, tableSchema, ct);
        }

        return tableSchema;
    }

    private async Task TryPreloadTableDataAsync(string tableName, VirtualTableSchema schema, CancellationToken ct)
    {
        try
        {
            if (_fileReader == null) return;

            using var stream = await _fileReader(schema.SourcePath, ct);
            if (stream == null) return;

            // Only preload if file is small (< 10MB)
            if (stream.CanSeek && stream.Length > 10 * 1024 * 1024)
                return;

            var data = await ReadAllDataAsync(schema, stream, ct);
            _tableDataCache[tableName] = new CachedTableData
            {
                TableName = tableName,
                Rows = data,
                CachedAt = DateTime.UtcNow,
                RowCount = data.Count
            };
        }
        catch
        {
            // Ignore preload failures - data will be read on demand
        }
    }

    /// <inheritdoc />
    public override Task<VirtualTableSchema?> GetTableSchemaAsync(string tableName, CancellationToken ct = default)
    {
        _tableRegistry.TryGetValue(tableName, out var schema);
        return Task.FromResult(schema);
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<VirtualTableSchema>> ListTablesAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<VirtualTableSchema>>(_tableRegistry.Values.ToList());
    }

    /// <summary>
    /// Gets JDBC/ODBC compatible metadata for all registered tables.
    /// </summary>
    /// <returns>Metadata suitable for database connectivity tools.</returns>
    public JdbcOdbcMetadata GetJdbcOdbcMetadata()
    {
        return new JdbcOdbcMetadata
        {
            Catalog = "datawarehouse",
            Schemas = new[] { "public" },
            Tables = _tableRegistry.Values.Select(t => new JdbcTableInfo
            {
                TableName = t.TableName,
                SchemaName = t.SchemaName ?? "public",
                TableType = "TABLE",
                Columns = t.Columns.Select(c => new JdbcColumnInfo
                {
                    ColumnName = c.Name,
                    DataType = MapToJdbcType(c.DataType),
                    TypeName = c.DataType.ToString(),
                    Nullable = c.IsNullable,
                    Ordinal = c.Ordinal,
                    ColumnSize = c.MaxLength ?? GetDefaultColumnSize(c.DataType)
                }).ToList()
            }).ToList()
        };
    }

    private static int MapToJdbcType(SqlDataType dataType)
    {
        return dataType switch
        {
            SqlDataType.Boolean => 16,      // JDBC BIT
            SqlDataType.Integer => 4,       // JDBC INTEGER
            SqlDataType.BigInt => -5,       // JDBC BIGINT
            SqlDataType.Float => 6,         // JDBC FLOAT
            SqlDataType.Double => 8,        // JDBC DOUBLE
            SqlDataType.Decimal => 3,       // JDBC DECIMAL
            SqlDataType.VarChar => 12,      // JDBC VARCHAR
            SqlDataType.Text => -1,         // JDBC LONGVARCHAR
            SqlDataType.Date => 91,         // JDBC DATE
            SqlDataType.Time => 92,         // JDBC TIME
            SqlDataType.Timestamp => 93,    // JDBC TIMESTAMP
            SqlDataType.Binary => -2,       // JDBC BINARY
            SqlDataType.Json => 12,         // JDBC VARCHAR
            SqlDataType.Array => 2003,      // JDBC ARRAY
            _ => 12                         // Default to VARCHAR
        };
    }

    private static int GetDefaultColumnSize(SqlDataType dataType)
    {
        return dataType switch
        {
            SqlDataType.Boolean => 1,
            SqlDataType.Integer => 10,
            SqlDataType.BigInt => 19,
            SqlDataType.Float => 15,
            SqlDataType.Double => 15,
            SqlDataType.Decimal => 38,
            SqlDataType.VarChar => 255,
            SqlDataType.Text => int.MaxValue,
            SqlDataType.Date => 10,
            SqlDataType.Time => 8,
            SqlDataType.Timestamp => 29,
            _ => 255
        };
    }

    #endregion

    #region 71.3-71.8 Query Execution Engine

    /// <inheritdoc />
    protected override async Task<SqlQueryResult> ExecuteQueryInternalAsync(
        string sql,
        IReadOnlyDictionary<string, object>? parameters,
        int maxRows,
        QueryExecutionPlan plan,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _totalQueriesExecuted);

        // Check query cache
        var cacheKey = ComputeQueryCacheKey(sql, parameters);
        if (_queryCache.TryGet(cacheKey, out var cachedResult))
        {
            Interlocked.Increment(ref _cacheHits);
            return cachedResult!;
        }
        Interlocked.Increment(ref _cacheMisses);

        // Try the new query engine first (Plans 01-04 infrastructure)
        if (_useNewEngine && _queryEngine != null)
        {
            try
            {
                var engineResult = await ExecuteViaNewEngineAsync(sql, parameters, maxRows, ct);
                _queryCache.Set(cacheKey, engineResult);
                return engineResult;
            }
            catch (SqlParseException)
            {
                // New parser cannot handle this query (DW-specific syntax);
                // fall back to legacy regex parser with warning
            }
            catch (NotSupportedException)
            {
                // Query type not supported by new engine; fall back
            }
        }

        // Legacy path: regex-based parser
        return await ExecuteViaLegacyEngineAsync(sql, parameters, maxRows, cacheKey, ct);
    }

    /// <summary>
    /// Executes a query using the new QueryExecutionEngine infrastructure.
    /// Converts QueryExecutionResult (columnar batches) to SqlQueryResult format.
    /// </summary>
    private async Task<SqlQueryResult> ExecuteViaNewEngineAsync(
        string sql,
        IReadOnlyDictionary<string, object>? parameters,
        int maxRows,
        CancellationToken ct)
    {
        // Apply parameter substitution to SQL string
        var resolvedSql = parameters != null ? SubstituteSqlParameters(sql, parameters) : sql;

        var result = await _queryEngine!.ExecuteQueryAsync(resolvedSql, ct);

        // Convert IAsyncEnumerable<ColumnarBatch> to SqlQueryResult format
        var allRows = new List<object?[]>();
        var columns = new List<VirtualTableColumn>();
        bool schemaBuilt = false;

        await foreach (var batch in result.Batches.WithCancellation(ct))
        {
            // Build schema from first batch
            if (!schemaBuilt)
            {
                for (int c = 0; c < batch.ColumnCount; c++)
                {
                    var col = batch.Columns[c];
                    columns.Add(new VirtualTableColumn
                    {
                        Name = col.Name,
                        DataType = MapColumnDataTypeToSql(col.DataType),
                        Ordinal = c
                    });
                }
                schemaBuilt = true;
            }

            // Extract rows from columnar batch
            for (int r = 0; r < batch.RowCount && allRows.Count < maxRows; r++)
            {
                var row = new object?[batch.ColumnCount];
                for (int c = 0; c < batch.ColumnCount; c++)
                    row[c] = batch.Columns[c].GetValue(r);
                allRows.Add(row);
            }

            if (allRows.Count >= maxRows) break;
        }

        return new SqlQueryResult
        {
            Columns = columns,
            Rows = allRows,
            RowCount = allRows.Count,
            HasMoreRows = allRows.Count >= maxRows,
            BytesScanned = Interlocked.Read(ref _totalBytesScanned)
        };
    }

    /// <summary>
    /// Executes a query using the legacy regex-based parser (backward compatibility).
    /// </summary>
    private async Task<SqlQueryResult> ExecuteViaLegacyEngineAsync(
        string sql,
        IReadOnlyDictionary<string, object>? parameters,
        int maxRows,
        string cacheKey,
        CancellationToken ct)
    {
        // Parse the SQL query
        var parsedQuery = ParseSqlQuery(sql);

        // Apply parameter substitution
        if (parameters != null)
        {
            parsedQuery = SubstituteParameters(parsedQuery, parameters);
        }

        // Execute based on query type
        var result = parsedQuery.QueryType switch
        {
            SqlQueryType.Select => await ExecuteSelectAsync(parsedQuery, maxRows, ct),
            SqlQueryType.Explain => await ExecuteExplainAsync(parsedQuery, ct),
            _ => throw new NotSupportedException($"Query type {parsedQuery.QueryType} is not supported")
        };

        // Cache the result
        _queryCache.Set(cacheKey, result);

        return result;
    }

    private static string SubstituteSqlParameters(string sql, IReadOnlyDictionary<string, object> parameters)
    {
        var result = sql;
        foreach (var param in parameters)
        {
            var placeholder = $"@{param.Key}";
            var value = param.Value switch
            {
                string s => $"'{s.Replace("'", "''")}'",
                DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
                null => "NULL",
                _ => param.Value.ToString()
            };
            result = result.Replace(placeholder, value);
        }
        return result;
    }

    private static SqlDataType MapColumnDataTypeToSql(ColumnDataType dt) => dt switch
    {
        ColumnDataType.Int32 => SqlDataType.Integer,
        ColumnDataType.Int64 => SqlDataType.BigInt,
        ColumnDataType.Float64 => SqlDataType.Double,
        ColumnDataType.String => SqlDataType.VarChar,
        ColumnDataType.Bool => SqlDataType.Boolean,
        ColumnDataType.Binary => SqlDataType.Binary,
        ColumnDataType.Decimal => SqlDataType.Decimal,
        ColumnDataType.DateTime => SqlDataType.Timestamp,
        _ => SqlDataType.VarChar
    };

    private async Task<SqlQueryResult> ExecuteSelectAsync(ParsedQuery query, int maxRows, CancellationToken ct)
    {
        // Get data from all referenced tables
        var tableData = new Dictionary<string, List<Dictionary<string, object?>>>();

        foreach (var tableName in query.Tables)
        {
            if (!_tableRegistry.TryGetValue(tableName, out var schema))
            {
                throw new InvalidOperationException($"Table '{tableName}' not found. Error code: SQL_42P01");
            }

            var data = await GetTableDataAsync(tableName, schema, query, ct);
            tableData[tableName] = data;
        }

        // Handle JOINs (71.6)
        var workingSet = ExecuteJoins(query, tableData);

        // Apply WHERE predicates (71.3 Predicate Pushdown happens in GetTableDataAsync)
        workingSet = ApplyWhereClause(workingSet, query.WhereClause);

        // Apply GROUP BY with aggregations (71.7)
        if (query.GroupByColumns.Count > 0)
        {
            workingSet = ApplyGroupBy(workingSet, query);
        }

        // Apply HAVING clause
        if (!string.IsNullOrEmpty(query.HavingClause))
        {
            workingSet = ApplyHavingClause(workingSet, query.HavingClause);
        }

        // Apply ORDER BY
        if (query.OrderByColumns.Count > 0)
        {
            workingSet = ApplyOrderBy(workingSet, query.OrderByColumns);
        }

        // Apply DISTINCT
        if (query.IsDistinct)
        {
            workingSet = ApplyDistinct(workingSet, query.SelectColumns);
        }

        // Apply LIMIT and OFFSET
        var totalRows = workingSet.Count;
        if (query.Offset > 0)
        {
            workingSet = workingSet.Skip(query.Offset).ToList();
        }
        var limit = query.Limit > 0 ? Math.Min(query.Limit, maxRows) : maxRows;
        var hasMore = workingSet.Count > limit;
        workingSet = workingSet.Take(limit).ToList();

        // Project selected columns (71.4)
        var (columns, rows) = ProjectColumns(workingSet, query);

        return new SqlQueryResult
        {
            Columns = columns,
            Rows = rows,
            RowCount = rows.Count,
            HasMoreRows = hasMore,
            BytesScanned = Interlocked.Read(ref _totalBytesScanned)
        };
    }

    private async Task<List<Dictionary<string, object?>>> GetTableDataAsync(
        string tableName, VirtualTableSchema schema, ParsedQuery query, CancellationToken ct)
    {
        // Check cache first
        if (_tableDataCache.TryGetValue(tableName, out var cached) &&
            (DateTime.UtcNow - cached.CachedAt) < TimeSpan.FromMinutes(5))
        {
            return ApplyPredicatePushdown(cached.Rows, query.WhereClause, tableName);
        }

        // Check partition pruning (71.5)
        if (_partitionMetadata.TryGetValue(tableName, out var partitions))
        {
            if (ShouldPrunePartition(partitions, query.WhereClause))
            {
                return new List<Dictionary<string, object?>>();
            }
        }

        // Read from source
        if (_fileReader == null)
        {
            throw new InvalidOperationException("File reader not configured");
        }

        using var stream = await _fileReader(schema.SourcePath, ct);
        if (stream == null)
        {
            throw new FileNotFoundException($"Source file not found: {schema.SourcePath}");
        }

        var data = await ReadAllDataAsync(schema, stream, ct);

        // Cache for future use
        _tableDataCache[tableName] = new CachedTableData
        {
            TableName = tableName,
            Rows = data,
            CachedAt = DateTime.UtcNow,
            RowCount = data.Count
        };

        // Apply predicate pushdown
        return ApplyPredicatePushdown(data, query.WhereClause, tableName);
    }

    private async Task<List<Dictionary<string, object?>>> ReadAllDataAsync(
        VirtualTableSchema schema, Stream stream, CancellationToken ct)
    {
        var data = new List<Dictionary<string, object?>>();
        long bytesRead = 0;

        switch (schema.SourceFormat)
        {
            case VirtualTableFormat.Csv:
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var headerLine = await reader.ReadLineAsync(ct);
                    if (headerLine == null) return data;

                    var headers = ParseCsvLine(headerLine);
                    bytesRead += Encoding.UTF8.GetByteCount(headerLine);

                    while (!reader.EndOfStream)
                    {
                        var line = await reader.ReadLineAsync(ct);
                        if (string.IsNullOrEmpty(line)) continue;

                        bytesRead += Encoding.UTF8.GetByteCount(line);
                        var values = ParseCsvLine(line);
                        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                        for (int i = 0; i < headers.Count && i < values.Count; i++)
                        {
                            row[headers[i]] = ConvertToTypedValue(values[i], schema.Columns.FirstOrDefault(c => c.Name.Equals(headers[i], StringComparison.OrdinalIgnoreCase))?.DataType ?? SqlDataType.VarChar);
                        }

                        data.Add(row);
                    }
                }
                break;

            case VirtualTableFormat.Json:
                using (var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct))
                {
                    bytesRead = stream.Position;
                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        if (element.ValueKind != JsonValueKind.Object) continue;
                        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        foreach (var property in element.EnumerateObject())
                        {
                            row[property.Name] = JsonElementToObject(property.Value);
                        }
                        data.Add(row);
                    }
                }
                break;

            case VirtualTableFormat.NdJson:
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    while (!reader.EndOfStream)
                    {
                        var line = await reader.ReadLineAsync(ct);
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        bytesRead += Encoding.UTF8.GetByteCount(line);
                        using var doc = JsonDocument.Parse(line);
                        var element = doc.RootElement;
                        if (element.ValueKind != JsonValueKind.Object) continue;

                        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        foreach (var property in element.EnumerateObject())
                        {
                            row[property.Name] = JsonElementToObject(property.Value);
                        }
                        data.Add(row);
                    }
                }
                break;
        }

        Interlocked.Add(ref _totalBytesScanned, bytesRead);
        return data;
    }

    #endregion

    #region 71.3 Predicate Pushdown

    private List<Dictionary<string, object?>> ApplyPredicatePushdown(
        List<Dictionary<string, object?>> data, string? whereClause, string tableName)
    {
        if (string.IsNullOrWhiteSpace(whereClause))
            return data;

        var predicates = ParseWhereClause(whereClause);
        if (predicates.Count == 0)
            return data;

        return data.Where(row => EvaluatePredicates(row, predicates)).ToList();
    }

    private List<Predicate> ParseWhereClause(string whereClause)
    {
        var predicates = new List<Predicate>();
        if (string.IsNullOrWhiteSpace(whereClause))
            return predicates;

        // Parse simple predicates: column = value, column > value, etc.
        var patterns = new[]
        {
            @"(\w+)\s*=\s*'([^']*)'",           // column = 'value'
            @"(\w+)\s*=\s*(\d+(?:\.\d+)?)",     // column = number
            @"(\w+)\s*<>\s*'([^']*)'",          // column <> 'value'
            @"(\w+)\s*!=\s*'([^']*)'",          // column != 'value'
            @"(\w+)\s*>\s*(\d+(?:\.\d+)?)",     // column > number
            @"(\w+)\s*<\s*(\d+(?:\.\d+)?)",     // column < number
            @"(\w+)\s*>=\s*(\d+(?:\.\d+)?)",    // column >= number
            @"(\w+)\s*<=\s*(\d+(?:\.\d+)?)",    // column <= number
            @"(\w+)\s+LIKE\s+'([^']*)'",        // column LIKE 'pattern'
            @"(\w+)\s+IN\s*\(([^)]+)\)",        // column IN (values)
            @"(\w+)\s+IS\s+NULL",               // column IS NULL
            @"(\w+)\s+IS\s+NOT\s+NULL",         // column IS NOT NULL
            @"(\w+)\s+BETWEEN\s+(\d+(?:\.\d+)?)\s+AND\s+(\d+(?:\.\d+)?)" // column BETWEEN x AND y
        };

        foreach (var pattern in patterns)
        {
            var matches = Regex.Matches(whereClause, pattern, RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                var predicate = CreatePredicate(match, pattern);
                if (predicate != null)
                    predicates.Add(predicate);
            }
        }

        return predicates;
    }

    private Predicate? CreatePredicate(Match match, string pattern)
    {
        var column = match.Groups[1].Value;
        var op = pattern switch
        {
            var p when p.Contains("=") && !p.Contains("<>") && !p.Contains("!=") && !p.Contains(">=") && !p.Contains("<=") => PredicateOperator.Equal,
            var p when p.Contains("<>") || p.Contains("!=") => PredicateOperator.NotEqual,
            var p when p.Contains(">=") => PredicateOperator.GreaterThanOrEqual,
            var p when p.Contains("<=") => PredicateOperator.LessThanOrEqual,
            var p when p.Contains(">") && !p.Contains(">=") => PredicateOperator.GreaterThan,
            var p when p.Contains("<") && !p.Contains("<=") && !p.Contains("<>") => PredicateOperator.LessThan,
            var p when p.Contains("LIKE") => PredicateOperator.Like,
            var p when p.Contains("IN") => PredicateOperator.In,
            var p when p.Contains("IS NOT NULL") => PredicateOperator.IsNotNull,
            var p when p.Contains("IS NULL") => PredicateOperator.IsNull,
            var p when p.Contains("BETWEEN") => PredicateOperator.Between,
            _ => PredicateOperator.Equal
        };

        object? value = match.Groups.Count > 2 ? match.Groups[2].Value : null;

        // Parse IN clause values
        if (op == PredicateOperator.In && value is string inStr)
        {
            value = inStr.Split(',')
                .Select(v => v.Trim().Trim('\''))
                .ToList();
        }

        // Parse BETWEEN values
        if (op == PredicateOperator.Between && match.Groups.Count > 3)
        {
            value = new object[] { ParseNumber(match.Groups[2].Value), ParseNumber(match.Groups[3].Value) };
        }

        return new Predicate { Column = column, Operator = op, Value = value };
    }

    private bool EvaluatePredicates(Dictionary<string, object?> row, List<Predicate> predicates)
    {
        foreach (var predicate in predicates)
        {
            if (!row.TryGetValue(predicate.Column, out var cellValue))
                return false;

            var result = predicate.Operator switch
            {
                PredicateOperator.Equal => Equals(cellValue?.ToString(), predicate.Value?.ToString()),
                PredicateOperator.NotEqual => !Equals(cellValue?.ToString(), predicate.Value?.ToString()),
                PredicateOperator.GreaterThan => CompareValues(cellValue, predicate.Value) > 0,
                PredicateOperator.LessThan => CompareValues(cellValue, predicate.Value) < 0,
                PredicateOperator.GreaterThanOrEqual => CompareValues(cellValue, predicate.Value) >= 0,
                PredicateOperator.LessThanOrEqual => CompareValues(cellValue, predicate.Value) <= 0,
                PredicateOperator.Like => EvaluateLike(cellValue?.ToString(), predicate.Value?.ToString()),
                PredicateOperator.In => predicate.Value is IList<string> list && list.Contains(cellValue?.ToString() ?? ""),
                PredicateOperator.IsNull => cellValue == null,
                PredicateOperator.IsNotNull => cellValue != null,
                PredicateOperator.Between => EvaluateBetween(cellValue, predicate.Value),
                _ => true
            };

            if (!result) return false;
        }

        return true;
    }

    private static int CompareValues(object? a, object? b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        if (double.TryParse(a.ToString(), out var aNum) && double.TryParse(b.ToString(), out var bNum))
            return aNum.CompareTo(bNum);

        return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
    }

    private static bool EvaluateLike(string? value, string? pattern)
    {
        if (value == null || pattern == null) return false;
        var regex = "^" + Regex.Escape(pattern).Replace("%", ".*").Replace("_", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }

    private static bool EvaluateBetween(object? value, object? range)
    {
        if (value == null || range is not object[] bounds || bounds.Length != 2)
            return false;

        return CompareValues(value, bounds[0]) >= 0 && CompareValues(value, bounds[1]) <= 0;
    }

    #endregion

    #region 71.5 Partition Pruning

    private void ExtractPartitionMetadata(string tableName, string sourcePath)
    {
        // Extract partition keys from path patterns like /year=2024/month=01/
        var partitionPattern = new Regex(@"[/\\](\w+)=([^/\\]+)", RegexOptions.IgnoreCase);
        var matches = partitionPattern.Matches(sourcePath);

        if (matches.Count == 0) return;

        var partitions = new Dictionary<string, object?>();
        foreach (Match match in matches)
        {
            var key = match.Groups[1].Value;
            var value = match.Groups[2].Value;
            partitions[key] = value;
        }

        _partitionMetadata[tableName] = new PartitionMetadata
        {
            TableName = tableName,
            PartitionKeys = partitions.Keys.ToList(),
            PartitionValues = partitions
        };
    }

    private bool ShouldPrunePartition(PartitionMetadata metadata, string? whereClause)
    {
        if (string.IsNullOrWhiteSpace(whereClause)) return false;

        // Check if WHERE clause references partition keys with non-matching values
        var predicates = ParseWhereClause(whereClause);
        foreach (var predicate in predicates)
        {
            if (metadata.PartitionValues.TryGetValue(predicate.Column, out var partitionValue))
            {
                if (predicate.Operator == PredicateOperator.Equal &&
                    !Equals(partitionValue?.ToString(), predicate.Value?.ToString()))
                {
                    return true; // Prune this partition
                }
            }
        }

        return false;
    }

    #endregion

    #region 71.6 Join Optimization

    private List<Dictionary<string, object?>> ExecuteJoins(
        ParsedQuery query, Dictionary<string, List<Dictionary<string, object?>>> tableData)
    {
        if (query.Joins.Count == 0)
        {
            // No joins - return data from first table with table prefix
            var firstTable = query.Tables.FirstOrDefault();
            if (firstTable == null || !tableData.TryGetValue(firstTable, out var data))
                return new List<Dictionary<string, object?>>();

            return data.Select(row => PrefixKeys(row, firstTable)).ToList();
        }

        // Start with the first table
        var result = tableData.GetValueOrDefault(query.Tables[0]) ?? new List<Dictionary<string, object?>>();
        result = result.Select(row => PrefixKeys(row, query.Tables[0])).ToList();

        // Process each join
        foreach (var join in query.Joins)
        {
            var rightTable = join.RightTable;
            var rightData = tableData.GetValueOrDefault(rightTable) ?? new List<Dictionary<string, object?>>();
            rightData = rightData.Select(row => PrefixKeys(row, rightTable)).ToList();

            result = join.JoinType switch
            {
                QueryJoinType.Inner => ExecuteHashJoin(result, rightData, join, false, false),
                QueryJoinType.Left => ExecuteHashJoin(result, rightData, join, true, false),
                QueryJoinType.Right => ExecuteHashJoin(result, rightData, join, false, true),
                QueryJoinType.Full => ExecuteHashJoin(result, rightData, join, true, true),
                QueryJoinType.Cross => ExecuteCrossJoin(result, rightData),
                _ => result
            };
        }

        return result;
    }

    private static Dictionary<string, object?> PrefixKeys(Dictionary<string, object?> row, string prefix)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in row)
        {
            result[$"{prefix}.{kvp.Key}"] = kvp.Value;
            // Also add unprefixed for convenience
            if (!result.ContainsKey(kvp.Key))
                result[kvp.Key] = kvp.Value;
        }
        return result;
    }

    private List<Dictionary<string, object?>> ExecuteHashJoin(
        List<Dictionary<string, object?>> left,
        List<Dictionary<string, object?>> right,
        JoinInfo join,
        bool keepLeftUnmatched,
        bool keepRightUnmatched)
    {
        // Build hash table on the smaller side
        var useRightForHash = right.Count < left.Count;
        var hashSide = useRightForHash ? right : left;
        var probeSide = useRightForHash ? left : right;
        var hashKey = useRightForHash ? join.RightColumn : join.LeftColumn;
        var probeKey = useRightForHash ? join.LeftColumn : join.RightColumn;

        // Build hash table
        var hashTable = new Dictionary<string, List<Dictionary<string, object?>>>();
        foreach (var row in hashSide)
        {
            var key = GetJoinKeyValue(row, hashKey);
            if (!hashTable.ContainsKey(key))
                hashTable[key] = new List<Dictionary<string, object?>>();
            hashTable[key].Add(row);
        }

        var result = new List<Dictionary<string, object?>>();
        var matchedRight = new HashSet<int>();

        // Probe phase
        for (int i = 0; i < probeSide.Count; i++)
        {
            var probeRow = probeSide[i];
            var key = GetJoinKeyValue(probeRow, probeKey);
            var matched = false;

            if (hashTable.TryGetValue(key, out var matchingRows))
            {
                foreach (var hashRow in matchingRows)
                {
                    var combined = new Dictionary<string, object?>(probeRow, StringComparer.OrdinalIgnoreCase);
                    foreach (var kvp in hashRow)
                    {
                        combined[kvp.Key] = kvp.Value;
                    }
                    result.Add(combined);
                    matched = true;

                    if (useRightForHash)
                        matchedRight.Add(hashSide.IndexOf(hashRow));
                }
            }

            if (!matched && ((useRightForHash && keepLeftUnmatched) || (!useRightForHash && keepRightUnmatched)))
            {
                result.Add(new Dictionary<string, object?>(probeRow, StringComparer.OrdinalIgnoreCase));
            }
        }

        // Add unmatched from hash side for outer joins
        if ((useRightForHash && keepRightUnmatched) || (!useRightForHash && keepLeftUnmatched))
        {
            for (int i = 0; i < hashSide.Count; i++)
            {
                if (!matchedRight.Contains(i))
                {
                    result.Add(new Dictionary<string, object?>(hashSide[i], StringComparer.OrdinalIgnoreCase));
                }
            }
        }

        return result;
    }

    private static List<Dictionary<string, object?>> ExecuteCrossJoin(
        List<Dictionary<string, object?>> left, List<Dictionary<string, object?>> right)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var leftRow in left)
        {
            foreach (var rightRow in right)
            {
                var combined = new Dictionary<string, object?>(leftRow, StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in rightRow)
                {
                    combined[kvp.Key] = kvp.Value;
                }
                result.Add(combined);
            }
        }
        return result;
    }

    private static string GetJoinKeyValue(Dictionary<string, object?> row, string column)
    {
        if (row.TryGetValue(column, out var value))
            return value?.ToString() ?? "";

        // Try with table prefix removed
        var dotIndex = column.IndexOf('.');
        if (dotIndex > 0)
        {
            var columnOnly = column.Substring(dotIndex + 1);
            if (row.TryGetValue(columnOnly, out value))
                return value?.ToString() ?? "";
        }

        return "";
    }

    #endregion

    #region 71.7 Aggregation Engine

    private List<Dictionary<string, object?>> ApplyGroupBy(
        List<Dictionary<string, object?>> data, ParsedQuery query)
    {
        var groups = data.GroupBy(row =>
        {
            var key = new StringBuilder();
            foreach (var col in query.GroupByColumns)
            {
                row.TryGetValue(col, out var val);
                key.Append(val?.ToString() ?? "NULL").Append("|");
            }
            return key.ToString();
        });

        var result = new List<Dictionary<string, object?>>();

        foreach (var group in groups)
        {
            var aggregatedRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var groupRows = group.ToList();

            // Add group by column values
            if (groupRows.Count > 0)
            {
                foreach (var col in query.GroupByColumns)
                {
                    groupRows[0].TryGetValue(col, out var val);
                    aggregatedRow[col] = val;
                }
            }

            // Process aggregations
            foreach (var selectCol in query.SelectColumns)
            {
                var aggMatch = Regex.Match(selectCol, @"(COUNT|SUM|AVG|MIN|MAX)\s*\(\s*(\*|\w+)\s*\)", RegexOptions.IgnoreCase);
                if (aggMatch.Success)
                {
                    var func = aggMatch.Groups[1].Value.ToUpperInvariant();
                    var col = aggMatch.Groups[2].Value;
                    var alias = GetColumnAlias(selectCol) ?? $"{func.ToLower()}_{col}";

                    aggregatedRow[alias] = func switch
                    {
                        "COUNT" => col == "*" ? groupRows.Count : groupRows.Count(r => r.ContainsKey(col) && r[col] != null),
                        "SUM" => ComputeSum(groupRows, col),
                        "AVG" => ComputeAvg(groupRows, col),
                        "MIN" => ComputeMin(groupRows, col),
                        "MAX" => ComputeMax(groupRows, col),
                        _ => null
                    };
                }
            }

            result.Add(aggregatedRow);
        }

        return result;
    }

    private static double? ComputeSum(List<Dictionary<string, object?>> rows, string column)
    {
        double sum = 0;
        bool hasValue = false;
        foreach (var row in rows)
        {
            if (row.TryGetValue(column, out var val) && val != null && double.TryParse(val.ToString(), out var num))
            {
                sum += num;
                hasValue = true;
            }
        }
        return hasValue ? sum : null;
    }

    private static double? ComputeAvg(List<Dictionary<string, object?>> rows, string column)
    {
        double sum = 0;
        int count = 0;
        foreach (var row in rows)
        {
            if (row.TryGetValue(column, out var val) && val != null && double.TryParse(val.ToString(), out var num))
            {
                sum += num;
                count++;
            }
        }
        return count > 0 ? sum / count : null;
    }

    private static object? ComputeMin(List<Dictionary<string, object?>> rows, string column)
    {
        object? min = null;
        foreach (var row in rows)
        {
            if (row.TryGetValue(column, out var val) && val != null)
            {
                if (min == null || CompareValues(val, min) < 0)
                    min = val;
            }
        }
        return min;
    }

    private static object? ComputeMax(List<Dictionary<string, object?>> rows, string column)
    {
        object? max = null;
        foreach (var row in rows)
        {
            if (row.TryGetValue(column, out var val) && val != null)
            {
                if (max == null || CompareValues(val, max) > 0)
                    max = val;
            }
        }
        return max;
    }

    private static string? GetColumnAlias(string column)
    {
        var match = Regex.Match(column, @"\s+AS\s+(\w+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    #endregion

    #region Query Processing Helpers

    private List<Dictionary<string, object?>> ApplyWhereClause(
        List<Dictionary<string, object?>> data, string? whereClause)
    {
        if (string.IsNullOrWhiteSpace(whereClause))
            return data;

        var predicates = ParseWhereClause(whereClause);
        return data.Where(row => EvaluatePredicates(row, predicates)).ToList();
    }

    private List<Dictionary<string, object?>> ApplyHavingClause(
        List<Dictionary<string, object?>> data, string havingClause)
    {
        var predicates = ParseWhereClause(havingClause);
        return data.Where(row => EvaluatePredicates(row, predicates)).ToList();
    }

    private List<Dictionary<string, object?>> ApplyOrderBy(
        List<Dictionary<string, object?>> data, List<(string Column, bool Descending)> orderBy)
    {
        if (orderBy.Count == 0) return data;

        IOrderedEnumerable<Dictionary<string, object?>>? ordered = null;

        foreach (var (column, desc) in orderBy)
        {
            Func<Dictionary<string, object?>, object?> keySelector = row =>
            {
                row.TryGetValue(column, out var val);
                return val;
            };

            if (ordered == null)
            {
                ordered = desc ? data.OrderByDescending(keySelector) : data.OrderBy(keySelector);
            }
            else
            {
                ordered = desc ? ordered.ThenByDescending(keySelector) : ordered.ThenBy(keySelector);
            }
        }

        return ordered?.ToList() ?? data;
    }

    private List<Dictionary<string, object?>> ApplyDistinct(
        List<Dictionary<string, object?>> data, List<string> columns)
    {
        var seen = new HashSet<string>();
        var result = new List<Dictionary<string, object?>>();

        foreach (var row in data)
        {
            var key = new StringBuilder();
            foreach (var col in columns)
            {
                var colName = col.Split(new[] { " AS " }, StringSplitOptions.None)[0].Trim();
                if (colName == "*")
                {
                    foreach (var kvp in row)
                        key.Append(kvp.Value?.ToString() ?? "NULL").Append("|");
                }
                else
                {
                    row.TryGetValue(colName, out var val);
                    key.Append(val?.ToString() ?? "NULL").Append("|");
                }
            }

            if (seen.Add(key.ToString()))
                result.Add(row);
        }

        return result;
    }

    private (IReadOnlyList<VirtualTableColumn> Columns, IReadOnlyList<object?[]> Rows) ProjectColumns(
        List<Dictionary<string, object?>> data, ParsedQuery query)
    {
        // Handle SELECT *
        if (query.SelectColumns.Count == 1 && query.SelectColumns[0] == "*")
        {
            var allColumns = data.SelectMany(r => r.Keys).Distinct().ToList();
            var columns = allColumns.Select((c, i) => new VirtualTableColumn
            {
                Name = c,
                DataType = InferTypeFromData(data, c),
                Ordinal = i
            }).ToList();

            var rows = data.Select(r => allColumns.Select(c => r.GetValueOrDefault(c)).ToArray()).ToList();
            return (columns, rows);
        }

        // Handle specific columns
        var resultColumns = new List<VirtualTableColumn>();
        var columnExpressions = new List<(string Expr, string Alias)>();

        foreach (var col in query.SelectColumns)
        {
            var parts = Regex.Split(col, @"\s+AS\s+", RegexOptions.IgnoreCase);
            var expr = parts[0].Trim();
            var alias = parts.Length > 1 ? parts[1].Trim() : expr;

            columnExpressions.Add((expr, alias));
            resultColumns.Add(new VirtualTableColumn
            {
                Name = alias,
                DataType = InferTypeFromData(data, expr),
                Ordinal = resultColumns.Count
            });
        }

        var resultRows = data.Select(row =>
        {
            var values = new object?[columnExpressions.Count];
            for (int i = 0; i < columnExpressions.Count; i++)
            {
                var (expr, _) = columnExpressions[i];

                // Handle aggregation functions already computed
                if (row.TryGetValue(columnExpressions[i].Alias, out var aggVal))
                {
                    values[i] = aggVal;
                }
                // Handle simple column references
                else if (row.TryGetValue(expr, out var val))
                {
                    values[i] = val;
                }
                // Handle expressions with table prefix
                else
                {
                    var dotIndex = expr.LastIndexOf('.');
                    if (dotIndex > 0)
                    {
                        var colOnly = expr.Substring(dotIndex + 1);
                        row.TryGetValue(colOnly, out val);
                        values[i] = val;
                    }
                }
            }
            return values;
        }).ToList();

        return (resultColumns, resultRows);
    }

    private SqlDataType InferTypeFromData(List<Dictionary<string, object?>> data, string column)
    {
        foreach (var row in data.Take(10))
        {
            if (row.TryGetValue(column, out var val) && val != null)
            {
                return SchemaInference.InferType(val);
            }
        }
        return SqlDataType.VarChar;
    }

    private async Task<SqlQueryResult> ExecuteExplainAsync(ParsedQuery query, CancellationToken ct)
    {
        var plan = await ExplainQueryAsync(query.OriginalSql, ct);

        var columns = new List<VirtualTableColumn>
        {
            new() { Name = "Plan", DataType = SqlDataType.Text, Ordinal = 0 }
        };

        var rows = new List<object?[]>
        {
            new object?[] { $"Query Plan for: {query.OriginalSql}" },
            new object?[] { $"Tables: {string.Join(", ", plan.TablesAccessed)}" },
            new object?[] { $"Estimated Cost: {plan.EstimatedCost}" },
            new object?[] { $"Estimated Rows: {plan.EstimatedRows}" }
        };

        return new SqlQueryResult
        {
            Columns = columns,
            Rows = rows,
            RowCount = rows.Count
        };
    }

    /// <inheritdoc />
    public override Task<QueryExecutionPlan> ExplainQueryAsync(string sql, CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;
        var parsed = ParseSqlQuery(sql);

        var nodes = new List<PlanNode>();

        // Build plan tree
        foreach (var table in parsed.Tables)
        {
            nodes.Add(new PlanNode
            {
                Operation = "TableScan",
                EstimatedCost = 1.0,
                EstimatedRows = _tableDataCache.TryGetValue(table, out var cache) ? cache.RowCount : 1000L,
                Properties = new Dictionary<string, object>
                {
                    ["Table"] = table,
                    ["Format"] = _tableRegistry.TryGetValue(table, out var schema) ? schema.SourceFormat.ToString() : "Unknown"
                }
            });
        }

        if (!string.IsNullOrEmpty(parsed.WhereClause))
        {
            nodes.Add(new PlanNode
            {
                Operation = "Filter",
                EstimatedCost = 0.1,
                EstimatedRows = 100L,
                Properties = new Dictionary<string, object>
                {
                    ["Predicate"] = parsed.WhereClause,
                    ["PushdownEnabled"] = true
                }
            });
        }

        foreach (var join in parsed.Joins)
        {
            nodes.Add(new PlanNode
            {
                Operation = $"HashJoin ({join.JoinType})",
                EstimatedCost = 2.0,
                EstimatedRows = 500L,
                Properties = new Dictionary<string, object>
                {
                    ["LeftColumn"] = join.LeftColumn,
                    ["RightColumn"] = join.RightColumn
                }
            });
        }

        if (parsed.GroupByColumns.Count > 0)
        {
            nodes.Add(new PlanNode
            {
                Operation = "Aggregate",
                EstimatedCost = 0.5,
                EstimatedRows = 50L,
                Properties = new Dictionary<string, object>
                {
                    ["GroupBy"] = string.Join(", ", parsed.GroupByColumns),
                    ["StreamingAggregation"] = true
                }
            });
        }

        if (parsed.OrderByColumns.Count > 0)
        {
            nodes.Add(new PlanNode
            {
                Operation = "Sort",
                EstimatedCost = 0.3,
                EstimatedRows = 100L,
                Properties = new Dictionary<string, object>
                {
                    ["OrderBy"] = string.Join(", ", parsed.OrderByColumns.Select(o => o.Column + (o.Descending ? " DESC" : "")))
                }
            });
        }

        var rootNode = new PlanNode
        {
            Operation = "Project",
            EstimatedCost = 0.1,
            EstimatedRows = 100L,
            Children = nodes,
            Properties = new Dictionary<string, object>
            {
                ["Columns"] = string.Join(", ", parsed.SelectColumns)
            }
        };

        var totalCost = nodes.Sum(n => n.EstimatedCost) + rootNode.EstimatedCost;

        return Task.FromResult(new QueryExecutionPlan
        {
            Query = sql,
            TablesAccessed = parsed.Tables,
            RootNode = rootNode,
            EstimatedCost = totalCost,
            EstimatedRows = rootNode.EstimatedRows,
            CanParallelize = parsed.Tables.Count > 1,
            UsesIndexes = false,
            PlanningTime = DateTime.UtcNow - startTime,
            Warnings = parsed.Joins.Count > 2 ? new[] { "Multiple joins may impact performance" } : Array.Empty<string>()
        });
    }

    #endregion

    #region SQL Parser

    private ParsedQuery ParseSqlQuery(string sql)
    {
        var query = new ParsedQuery { OriginalSql = sql };

        sql = sql.Trim();

        // Detect query type
        if (sql.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase))
        {
            query.QueryType = SqlQueryType.Explain;
            sql = sql.Substring(7).Trim();
            if (sql.StartsWith("ANALYZE", StringComparison.OrdinalIgnoreCase))
                sql = sql.Substring(7).Trim();
        }

        if (sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            query.QueryType = query.QueryType == SqlQueryType.Explain ? SqlQueryType.Explain : SqlQueryType.Select;
            ParseSelectQuery(sql, query);
        }
        else
        {
            throw new NotSupportedException($"Unsupported SQL statement. Only SELECT is supported. Error code: SQL_42601");
        }

        return query;
    }

    private void ParseSelectQuery(string sql, ParsedQuery query)
    {
        // Remove SELECT keyword
        sql = sql.Substring(6).Trim();

        // Check for DISTINCT
        if (sql.StartsWith("DISTINCT", StringComparison.OrdinalIgnoreCase))
        {
            query.IsDistinct = true;
            sql = sql.Substring(8).Trim();
        }

        // Find major clause boundaries
        var fromIndex = FindKeywordIndex(sql, "FROM");
        var whereIndex = FindKeywordIndex(sql, "WHERE");
        var groupByIndex = FindKeywordIndex(sql, "GROUP BY");
        var havingIndex = FindKeywordIndex(sql, "HAVING");
        var orderByIndex = FindKeywordIndex(sql, "ORDER BY");
        var limitIndex = FindKeywordIndex(sql, "LIMIT");
        var offsetIndex = FindKeywordIndex(sql, "OFFSET");

        // Extract SELECT columns
        var selectEnd = fromIndex > 0 ? fromIndex : sql.Length;
        var selectPart = sql.Substring(0, selectEnd).Trim();
        query.SelectColumns = ParseColumnList(selectPart);

        // Extract FROM clause (including JOINs)
        if (fromIndex >= 0)
        {
            var fromEnd = new[] { whereIndex, groupByIndex, havingIndex, orderByIndex, limitIndex }
                .Where(i => i > fromIndex).DefaultIfEmpty(sql.Length).Min();
            var fromPart = sql.Substring(fromIndex + 4, fromEnd - fromIndex - 4).Trim();
            ParseFromClause(fromPart, query);
        }

        // Extract WHERE clause
        if (whereIndex >= 0)
        {
            var whereEnd = new[] { groupByIndex, havingIndex, orderByIndex, limitIndex }
                .Where(i => i > whereIndex).DefaultIfEmpty(sql.Length).Min();
            query.WhereClause = sql.Substring(whereIndex + 5, whereEnd - whereIndex - 5).Trim();
        }

        // Extract GROUP BY columns
        if (groupByIndex >= 0)
        {
            var groupEnd = new[] { havingIndex, orderByIndex, limitIndex }
                .Where(i => i > groupByIndex).DefaultIfEmpty(sql.Length).Min();
            var groupPart = sql.Substring(groupByIndex + 8, groupEnd - groupByIndex - 8).Trim();
            query.GroupByColumns = ParseColumnList(groupPart);
        }

        // Extract HAVING clause
        if (havingIndex >= 0)
        {
            var havingEnd = new[] { orderByIndex, limitIndex }
                .Where(i => i > havingIndex).DefaultIfEmpty(sql.Length).Min();
            query.HavingClause = sql.Substring(havingIndex + 6, havingEnd - havingIndex - 6).Trim();
        }

        // Extract ORDER BY columns
        if (orderByIndex >= 0)
        {
            var orderEnd = limitIndex > orderByIndex ? limitIndex : sql.Length;
            var orderPart = sql.Substring(orderByIndex + 8, orderEnd - orderByIndex - 8).Trim();
            query.OrderByColumns = ParseOrderByList(orderPart);
        }

        // Extract LIMIT
        if (limitIndex >= 0)
        {
            var limitEnd = offsetIndex > limitIndex ? offsetIndex : sql.Length;
            var limitPart = sql.Substring(limitIndex + 5, limitEnd - limitIndex - 5).Trim();
            if (int.TryParse(limitPart, out var limit))
                query.Limit = limit;
        }

        // Extract OFFSET
        if (offsetIndex >= 0)
        {
            var offsetPart = sql.Substring(offsetIndex + 6).Trim();
            var parts = offsetPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && int.TryParse(parts[0], out var offset))
                query.Offset = offset;
        }
    }

    private void ParseFromClause(string fromClause, ParsedQuery query)
    {
        // Handle JOINs
        var joinPattern = new Regex(@"(LEFT\s+OUTER\s+|RIGHT\s+OUTER\s+|FULL\s+OUTER\s+|INNER\s+|LEFT\s+|RIGHT\s+|CROSS\s+)?JOIN\s+(\w+)\s+(?:AS\s+)?(\w+)?\s*(?:ON\s+(\w+(?:\.\w+)?)\s*=\s*(\w+(?:\.\w+)?))?", RegexOptions.IgnoreCase);
        var joinMatches = joinPattern.Matches(fromClause);

        // Get the base table (before any JOINs)
        var firstJoinIndex = joinMatches.Count > 0 ? joinMatches[0].Index : fromClause.Length;
        var baseTable = fromClause.Substring(0, firstJoinIndex).Trim();

        // Parse base table (handle alias)
        var baseTableParts = Regex.Split(baseTable, @"\s+AS\s+|\s+", RegexOptions.IgnoreCase);
        query.Tables.Add(baseTableParts[0].Trim(',').Trim());

        // Parse JOINs
        foreach (Match match in joinMatches)
        {
            var joinTypeStr = match.Groups[1].Value.Trim().ToUpperInvariant();
            var rightTable = match.Groups[2].Value;
            var leftCol = match.Groups[4].Value;
            var rightCol = match.Groups[5].Value;

            var joinType = joinTypeStr switch
            {
                "LEFT " or "LEFT OUTER " => QueryJoinType.Left,
                "RIGHT " or "RIGHT OUTER " => QueryJoinType.Right,
                "FULL OUTER " => QueryJoinType.Full,
                "CROSS " => QueryJoinType.Cross,
                _ => QueryJoinType.Inner
            };

            query.Tables.Add(rightTable);
            query.Joins.Add(new JoinInfo
            {
                JoinType = joinType,
                RightTable = rightTable,
                LeftColumn = leftCol,
                RightColumn = rightCol
            });
        }

        // Handle comma-separated tables (implicit cross join)
        if (joinMatches.Count == 0 && baseTable.Contains(','))
        {
            query.Tables.Clear();
            var tables = baseTable.Split(',');
            foreach (var table in tables)
            {
                var tableParts = Regex.Split(table.Trim(), @"\s+AS\s+|\s+", RegexOptions.IgnoreCase);
                query.Tables.Add(tableParts[0].Trim());
            }

            // Add implicit cross joins
            for (int i = 1; i < query.Tables.Count; i++)
            {
                query.Joins.Add(new JoinInfo
                {
                    JoinType = QueryJoinType.Cross,
                    RightTable = query.Tables[i]
                });
            }
        }
    }

    private List<string> ParseColumnList(string columnPart)
    {
        var columns = new List<string>();
        var depth = 0;
        var current = new StringBuilder();

        foreach (var c in columnPart)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                columns.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            current.Append(c);
        }

        if (current.Length > 0)
            columns.Add(current.ToString().Trim());

        return columns;
    }

    private List<(string Column, bool Descending)> ParseOrderByList(string orderPart)
    {
        var result = new List<(string, bool)>();
        var parts = orderPart.Split(',');

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            var desc = trimmed.EndsWith(" DESC", StringComparison.OrdinalIgnoreCase);
            var asc = trimmed.EndsWith(" ASC", StringComparison.OrdinalIgnoreCase);

            var column = trimmed;
            if (desc) column = trimmed.Substring(0, trimmed.Length - 5).Trim();
            else if (asc) column = trimmed.Substring(0, trimmed.Length - 4).Trim();

            result.Add((column, desc));
        }

        return result;
    }

    private int FindKeywordIndex(string sql, string keyword)
    {
        var pattern = $@"\b{keyword}\b";
        var match = Regex.Match(sql, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Index : -1;
    }

    private ParsedQuery SubstituteParameters(ParsedQuery query, IReadOnlyDictionary<string, object> parameters)
    {
        var result = new ParsedQuery
        {
            OriginalSql = query.OriginalSql,
            QueryType = query.QueryType,
            IsDistinct = query.IsDistinct,
            SelectColumns = query.SelectColumns,
            Tables = query.Tables,
            Joins = query.Joins,
            GroupByColumns = query.GroupByColumns,
            OrderByColumns = query.OrderByColumns,
            Limit = query.Limit,
            Offset = query.Offset
        };

        // Substitute parameters in WHERE clause
        if (!string.IsNullOrEmpty(query.WhereClause))
        {
            var where = query.WhereClause;
            foreach (var param in parameters)
            {
                var placeholder = $"@{param.Key}";
                var value = param.Value switch
                {
                    string s => $"'{s.Replace("'", "''")}'",
                    DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
                    null => "NULL",
                    _ => param.Value.ToString()
                };
                where = where.Replace(placeholder, value);
            }
            result.WhereClause = where;
        }

        return result;
    }

    #endregion

    #region Utility Methods

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var inQuotes = false;
        var current = new StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString());
        return result;
    }

    private static object? ConvertToTypedValue(string value, SqlDataType dataType)
    {
        if (string.IsNullOrEmpty(value)) return null;

        return dataType switch
        {
            SqlDataType.Boolean => bool.TryParse(value, out var b) ? b : null,
            SqlDataType.Integer => int.TryParse(value, out var i) ? i : null,
            SqlDataType.BigInt => long.TryParse(value, out var l) ? l : null,
            SqlDataType.Float => float.TryParse(value, CultureInfo.InvariantCulture, out var f) ? f : null,
            SqlDataType.Double => double.TryParse(value, CultureInfo.InvariantCulture, out var d) ? d : null,
            SqlDataType.Decimal => decimal.TryParse(value, CultureInfo.InvariantCulture, out var dec) ? dec : null,
            SqlDataType.Timestamp => DateTime.TryParse(value, out var dt) ? dt : null,
            SqlDataType.Date => DateOnly.TryParse(value, out var date) ? date : null,
            SqlDataType.Time => TimeOnly.TryParse(value, out var time) ? time : null,
            _ => value
        };
    }

    private static object ParseNumber(string value)
    {
        if (int.TryParse(value, out var i)) return i;
        if (long.TryParse(value, out var l)) return l;
        if (double.TryParse(value, CultureInfo.InvariantCulture, out var d)) return d;
        return value;
    }

    private static string ComputeQueryCacheKey(string sql, IReadOnlyDictionary<string, object>? parameters)
    {
        var key = new StringBuilder(sql.ToLowerInvariant());
        if (parameters != null)
        {
            foreach (var kvp in parameters.OrderBy(p => p.Key))
            {
                key.Append($"|{kvp.Key}={kvp.Value}");
            }
        }
        return key.ToString();
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Gets query execution statistics.
    /// </summary>
    /// <returns>Current statistics.</returns>
    public QueryStatistics GetStatistics()
    {
        return new QueryStatistics
        {
            TotalQueriesExecuted = Interlocked.Read(ref _totalQueriesExecuted),
            TotalBytesScanned = Interlocked.Read(ref _totalBytesScanned),
            CacheHits = Interlocked.Read(ref _cacheHits),
            CacheMisses = Interlocked.Read(ref _cacheMisses),
            CacheHitRate = _cacheHits + _cacheMisses > 0
                ? (double)_cacheHits / (_cacheHits + _cacheMisses)
                : 0,
            RegisteredTables = _tableRegistry.Count,
            CachedTables = _tableDataCache.Count,
            CachedQueries = _queryCache.Count
        };
    }

    /// <summary>
    /// Clears all caches.
    /// </summary>
    public void ClearCaches()
    {
        _tableDataCache.Clear();
        _queryCache.Clear();
    }

    #endregion
}

#region Query Engine Integration Types

/// <summary>
/// Bridges the plugin's table registry and file reader to the IDataSourceProvider contract
/// required by QueryExecutionEngine. Converts file data into ColumnarBatch format.
/// </summary>
internal sealed class PluginDataSourceProvider : IDataSourceProvider
{
    private readonly SqlOverObjectPlugin _plugin;

    public PluginDataSourceProvider(SqlOverObjectPlugin plugin) =>
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

    public async IAsyncEnumerable<ColumnarBatch> GetTableData(
        string tableName,
        List<string>? columns,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!_plugin._tableRegistry.TryGetValue(tableName, out var schema))
            throw new InvalidOperationException($"Table '{tableName}' not found. Error code: SQL_42P01");

        // Check cached data first
        if (_plugin._tableDataCache.TryGetValue(tableName, out var cached) &&
            (DateTime.UtcNow - cached.CachedAt) < TimeSpan.FromMinutes(5))
        {
            yield return ConvertRowsToBatch(cached.Rows, schema, columns);
            yield break;
        }

        // Read from source
        if (_plugin._fileReader == null)
            throw new InvalidOperationException("File reader not configured");

        using var stream = await _plugin._fileReader(schema.SourcePath, ct);
        if (stream == null)
            throw new FileNotFoundException($"Source file not found: {schema.SourcePath}");

        var data = await ReadAllDataInternalAsync(schema, stream, ct);

        // Cache for future use
        _plugin._tableDataCache[tableName] = new CachedTableData
        {
            TableName = tableName,
            Rows = data,
            CachedAt = DateTime.UtcNow,
            RowCount = data.Count
        };

        yield return ConvertRowsToBatch(data, schema, columns);
    }

    public TableStatistics? GetTableStatistics(string tableName)
    {
        if (_plugin._tableDataCache.TryGetValue(tableName, out var cached))
        {
            var colStats = ImmutableDictionary<string, ColumnStatistics>.Empty;
            if (_plugin._tableRegistry.TryGetValue(tableName, out var schema))
            {
                var builder = ImmutableDictionary.CreateBuilder<string, ColumnStatistics>();
                foreach (var col in schema.Columns)
                {
                    builder[col.Name] = new ColumnStatistics(
                        DistinctCount: Math.Min(cached.RowCount, 100),
                        NullCount: 0,
                        MinValue: null,
                        MaxValue: null,
                        AvgSize: 32
                    );
                }
                colStats = builder.ToImmutable();
            }

            return new TableStatistics(
                TableName: tableName,
                RowCount: cached.RowCount,
                SizeBytes: cached.RowCount * 256, // rough estimate
                Columns: colStats
            );
        }

        // Return basic stats if table is registered but not cached
        if (_plugin._tableRegistry.ContainsKey(tableName))
        {
            return new TableStatistics(
                TableName: tableName,
                RowCount: 1000, // default estimate
                SizeBytes: 256_000,
                Columns: ImmutableDictionary<string, ColumnStatistics>.Empty
            );
        }

        return null;
    }

    private static ColumnarBatch ConvertRowsToBatch(
        List<Dictionary<string, object?>> rows,
        VirtualTableSchema schema,
        List<string>? requestedColumns)
    {
        if (rows.Count == 0)
            return new ColumnarBatch(0, Array.Empty<ColumnVector>());

        var columnsToUse = requestedColumns != null
            ? schema.Columns.Where(c => requestedColumns.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList()
            : schema.Columns.ToList();

        // If no schema columns match, try using keys from data
        if (columnsToUse.Count == 0 && rows.Count > 0)
        {
            var allKeys = rows.SelectMany(r => r.Keys).Distinct().ToList();
            var colsToProject = requestedColumns != null
                ? allKeys.Where(k => requestedColumns.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList()
                : allKeys;

            columnsToUse = colsToProject.Select((k, i) => new VirtualTableColumn
            {
                Name = k,
                DataType = SqlDataType.VarChar,
                Ordinal = i
            }).ToList();
        }

        var builder = new ColumnarBatchBuilder(rows.Count);

        foreach (var col in columnsToUse)
        {
            var dataType = MapSqlDataTypeToColumnar(col.DataType);
            builder.AddColumn(col.Name, dataType);
        }

        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < columnsToUse.Count; c++)
            {
                var colName = columnsToUse[c].Name;
                rows[r].TryGetValue(colName, out var value);
                if (value != null)
                {
                    try
                    {
                        builder.SetValue(c, r, ConvertForColumnar(value, columnsToUse[c].DataType));
                    }
                    catch
                    {
                        // Type conversion failure; leave as null
                    }
                }
            }
        }

        return builder.Build();
    }

    private static ColumnDataType MapSqlDataTypeToColumnar(SqlDataType dt) => dt switch
    {
        SqlDataType.Boolean => ColumnDataType.Bool,
        SqlDataType.Integer => ColumnDataType.Int32,
        SqlDataType.BigInt => ColumnDataType.Int64,
        SqlDataType.Float or SqlDataType.Double => ColumnDataType.Float64,
        SqlDataType.Decimal => ColumnDataType.Decimal,
        SqlDataType.Date or SqlDataType.Time or SqlDataType.Timestamp => ColumnDataType.DateTime,
        SqlDataType.Binary => ColumnDataType.Binary,
        _ => ColumnDataType.String
    };

    private static object? ConvertForColumnar(object? value, SqlDataType targetType)
    {
        if (value == null) return null;

        return targetType switch
        {
            SqlDataType.Integer => Convert.ToInt32(value),
            SqlDataType.BigInt => Convert.ToInt64(value),
            SqlDataType.Float or SqlDataType.Double => Convert.ToDouble(value),
            SqlDataType.Decimal => Convert.ToDecimal(value),
            SqlDataType.Boolean => Convert.ToBoolean(value),
            SqlDataType.Timestamp or SqlDataType.Date or SqlDataType.Time =>
                value is DateTime dt ? dt : DateTime.TryParse(value.ToString(), out var parsed) ? parsed : value,
            _ => value.ToString() ?? ""
        };
    }

    private static async Task<List<Dictionary<string, object?>>> ReadAllDataInternalAsync(
        VirtualTableSchema schema, Stream stream, CancellationToken ct)
    {
        var data = new List<Dictionary<string, object?>>();

        switch (schema.SourceFormat)
        {
            case VirtualTableFormat.Csv:
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var headerLine = await reader.ReadLineAsync(ct);
                    if (headerLine == null) return data;

                    var headers = headerLine.Split(',').Select(h => h.Trim().Trim('"')).ToList();

                    while (!reader.EndOfStream)
                    {
                        var line = await reader.ReadLineAsync(ct);
                        if (string.IsNullOrEmpty(line)) continue;

                        var values = line.Split(',');
                        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                        for (int i = 0; i < headers.Count && i < values.Length; i++)
                            row[headers[i]] = values[i].Trim().Trim('"');

                        data.Add(row);
                    }
                }
                break;

            case VirtualTableFormat.Json:
                using (var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct))
                {
                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        if (element.ValueKind != JsonValueKind.Object) continue;
                        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        foreach (var property in element.EnumerateObject())
                            row[property.Name] = JsonElementToValue(property.Value);
                        data.Add(row);
                    }
                }
                break;

            case VirtualTableFormat.NdJson:
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    while (!reader.EndOfStream)
                    {
                        var line = await reader.ReadLineAsync(ct);
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;

                        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        foreach (var property in doc.RootElement.EnumerateObject())
                            row[property.Name] = JsonElementToValue(property.Value);
                        data.Add(row);
                    }
                }
                break;
        }

        return data;
    }

    private static object? JsonElementToValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when element.TryGetInt32(out var i) => i,
        JsonValueKind.Number when element.TryGetInt64(out var l) => l,
        JsonValueKind.Number when element.TryGetDouble(out var d) => d,
        JsonValueKind.String => element.GetString(),
        _ => element.GetRawText()
    };
}

/// <summary>
/// Bridges the DW MessageBus tag system to the ITagProvider contract.
/// Resolves tag(), has_tag(), and tag_count() SQL function calls against the
/// message bus topics "tags.get", "tags.has", and "tags.count".
/// </summary>
internal sealed class MessageBusTagProvider : ITagProvider
{
    private readonly IMessageBus _bus;

    public MessageBusTagProvider(IMessageBus bus) =>
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));

    public string? GetTag(string objectKey, string tagName)
    {
        try
        {
            var message = new PluginMessage
            {
                Type = "tags.get",
                Payload = new Dictionary<string, object>
                {
                    ["objectKey"] = objectKey,
                    ["tagName"] = tagName
                }
            };
            var response = _bus.SendAsync("tags.get", message).GetAwaiter().GetResult();
            return response.Success ? response.Payload?.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    public bool HasTag(string objectKey, string tagName)
    {
        try
        {
            var message = new PluginMessage
            {
                Type = "tags.has",
                Payload = new Dictionary<string, object>
                {
                    ["objectKey"] = objectKey,
                    ["tagName"] = tagName
                }
            };
            var response = _bus.SendAsync("tags.has", message).GetAwaiter().GetResult();
            return response.Success && response.Payload is true;
        }
        catch
        {
            return false;
        }
    }

    public int GetTagCount(string objectKey)
    {
        try
        {
            var message = new PluginMessage
            {
                Type = "tags.count",
                Payload = new Dictionary<string, object>
                {
                    ["objectKey"] = objectKey
                }
            };
            var response = _bus.SendAsync("tags.count", message).GetAwaiter().GetResult();
            return response.Success && response.Payload is int count ? count : 0;
        }
        catch
        {
            return 0;
        }
    }
}

#endregion

#region Supporting Types

/// <summary>
/// Query execution statistics.
/// </summary>
public class QueryStatistics
{
    /// <summary>Total queries executed.</summary>
    public long TotalQueriesExecuted { get; init; }

    /// <summary>Total bytes scanned from source files.</summary>
    public long TotalBytesScanned { get; init; }

    /// <summary>Number of cache hits.</summary>
    public long CacheHits { get; init; }

    /// <summary>Number of cache misses.</summary>
    public long CacheMisses { get; init; }

    /// <summary>Cache hit rate (0-1).</summary>
    public double CacheHitRate { get; init; }

    /// <summary>Number of registered tables.</summary>
    public int RegisteredTables { get; init; }

    /// <summary>Number of cached tables.</summary>
    public int CachedTables { get; init; }

    /// <summary>Number of cached query results.</summary>
    public int CachedQueries { get; init; }
}

/// <summary>
/// JDBC/ODBC metadata for standard connectivity.
/// </summary>
public class JdbcOdbcMetadata
{
    /// <summary>Database catalog name.</summary>
    public string Catalog { get; init; } = "datawarehouse";

    /// <summary>Available schemas.</summary>
    public IReadOnlyList<string> Schemas { get; init; } = Array.Empty<string>();

    /// <summary>Table information.</summary>
    public IReadOnlyList<JdbcTableInfo> Tables { get; init; } = Array.Empty<JdbcTableInfo>();
}

/// <summary>
/// JDBC table information.
/// </summary>
public class JdbcTableInfo
{
    /// <summary>Table name.</summary>
    public string TableName { get; init; } = string.Empty;

    /// <summary>Schema name.</summary>
    public string SchemaName { get; init; } = "public";

    /// <summary>Table type (TABLE, VIEW, etc.).</summary>
    public string TableType { get; init; } = "TABLE";

    /// <summary>Column information.</summary>
    public IReadOnlyList<JdbcColumnInfo> Columns { get; init; } = Array.Empty<JdbcColumnInfo>();
}

/// <summary>
/// JDBC column information.
/// </summary>
public class JdbcColumnInfo
{
    /// <summary>Column name.</summary>
    public string ColumnName { get; init; } = string.Empty;

    /// <summary>JDBC data type code.</summary>
    public int DataType { get; init; }

    /// <summary>Type name.</summary>
    public string TypeName { get; init; } = string.Empty;

    /// <summary>Whether column is nullable.</summary>
    public bool Nullable { get; init; } = true;

    /// <summary>Column ordinal position.</summary>
    public int Ordinal { get; init; }

    /// <summary>Column size.</summary>
    public int ColumnSize { get; init; }
}

/// <summary>
/// Cached table data.
/// </summary>
internal class CachedTableData
{
    public string TableName { get; init; } = string.Empty;
    public List<Dictionary<string, object?>> Rows { get; init; } = new();
    public DateTime CachedAt { get; init; }
    public long RowCount { get; init; }
}

/// <summary>
/// Partition metadata for pruning.
/// </summary>
internal class PartitionMetadata
{
    public string TableName { get; init; } = string.Empty;
    public List<string> PartitionKeys { get; init; } = new();
    public Dictionary<string, object?> PartitionValues { get; init; } = new();
}

/// <summary>
/// Parsed SQL query structure.
/// </summary>
internal class ParsedQuery
{
    public string OriginalSql { get; init; } = string.Empty;
    public SqlQueryType QueryType { get; set; } = SqlQueryType.Select;
    public bool IsDistinct { get; set; }
    public List<string> SelectColumns { get; set; } = new();
    public List<string> Tables { get; set; } = new();
    public List<JoinInfo> Joins { get; set; } = new();
    public string? WhereClause { get; set; }
    public List<string> GroupByColumns { get; set; } = new();
    public string? HavingClause { get; set; }
    public List<(string Column, bool Descending)> OrderByColumns { get; set; } = new();
    public int Limit { get; set; }
    public int Offset { get; set; }
}

/// <summary>
/// SQL query type.
/// </summary>
internal enum SqlQueryType
{
    Select,
    Explain
}

/// <summary>
/// Join information.
/// </summary>
internal class JoinInfo
{
    public QueryJoinType JoinType { get; init; }
    public string RightTable { get; init; } = string.Empty;
    public string LeftColumn { get; init; } = string.Empty;
    public string RightColumn { get; init; } = string.Empty;
}

/// <summary>
/// Predicate for WHERE clause filtering.
/// </summary>
internal class Predicate
{
    public string Column { get; init; } = string.Empty;
    public PredicateOperator Operator { get; init; }
    public object? Value { get; init; }
}

/// <summary>
/// Predicate operators.
/// </summary>
internal enum PredicateOperator
{
    Equal,
    NotEqual,
    GreaterThan,
    LessThan,
    GreaterThanOrEqual,
    LessThanOrEqual,
    Like,
    In,
    IsNull,
    IsNotNull,
    Between
}

/// <summary>
/// LRU query cache implementation.
/// </summary>
internal class QueryCache
{
    private readonly int _maxEntries;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly object _cleanupLock = new();

    public QueryCache(int maxEntries = 1000)
    {
        _maxEntries = maxEntries;
    }

    public int Count => _cache.Count;

    public bool TryGet(string key, out SqlQueryResult? result)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            entry.LastAccessed = DateTime.UtcNow;
            entry.AccessCount++;
            result = entry.Result;
            return true;
        }
        result = null;
        return false;
    }

    public void Set(string key, SqlQueryResult result)
    {
        var entry = new CacheEntry
        {
            Key = key,
            Result = result,
            CreatedAt = DateTime.UtcNow,
            LastAccessed = DateTime.UtcNow,
            AccessCount = 1
        };

        _cache[key] = entry;

        // Cleanup if over capacity
        if (_cache.Count > _maxEntries)
        {
            CleanupOldEntries();
        }
    }

    public void Clear()
    {
        _cache.Clear();
    }

    private void CleanupOldEntries()
    {
        lock (_cleanupLock)
        {
            if (_cache.Count <= _maxEntries)
                return;

            // Remove least recently accessed entries
            var toRemove = _cache.Values
                .OrderBy(e => e.LastAccessed)
                .Take(_cache.Count - _maxEntries + _maxEntries / 10)
                .Select(e => e.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    private class CacheEntry
    {
        public string Key { get; init; } = string.Empty;
        public SqlQueryResult Result { get; init; } = new();
        public DateTime CreatedAt { get; init; }
        public DateTime LastAccessed { get; set; }
        public int AccessCount { get; set; }
    }
}

#endregion
