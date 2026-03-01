using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Query;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.Relational;

/// <summary>
/// Handles \d and other catalog queries by implementing virtual pg_catalog tables
/// backed by VDE metadata. Makes DW appear as a PostgreSQL-compatible database
/// for tools like psql and pgAdmin.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: PostgreSQL catalog provider (ECOS-02)")]
public sealed class PostgreSqlCatalogProvider
{
    private readonly IDataSourceProvider _vdeDataSource;

    /// <summary>
    /// Delegate to retrieve table names from VDE storage.
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<string>>>? GetTableNamesAsync { get; set; }

    /// <summary>
    /// Delegate to retrieve column definitions for a specific table.
    /// </summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<CatalogColumnInfo>>>? GetTableColumnsAsync { get; set; }

    /// <summary>
    /// Initializes a new PostgreSqlCatalogProvider.
    /// </summary>
    /// <param name="vdeDataSource">VDE storage data source provider for metadata retrieval.</param>
    public PostgreSqlCatalogProvider(IDataSourceProvider vdeDataSource)
    {
        _vdeDataSource = vdeDataSource ?? throw new ArgumentNullException(nameof(vdeDataSource));
    }

    // ─────────────────────────────────────────────────────────────
    // Catalog Query Detection
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the query targets pg_catalog or information_schema tables,
    /// or is a psql meta-command equivalent.
    /// </summary>
    /// <param name="sql">The SQL query to check.</param>
    /// <returns>True if this is a catalog query.</returns>
    public bool IsCatalogQuery(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return false;

        var normalized = sql.Trim().ToLowerInvariant();

        // psql backslash commands (translated to SQL by psql)
        if (normalized.StartsWith("\\d"))
            return true;

        // Queries targeting pg_catalog or information_schema
        if (normalized.Contains("pg_catalog.") ||
            normalized.Contains("pg_tables") ||
            normalized.Contains("pg_class") ||
            normalized.Contains("pg_attribute") ||
            normalized.Contains("pg_type") ||
            normalized.Contains("pg_namespace") ||
            normalized.Contains("information_schema."))
            return true;

        return false;
    }

    // ─────────────────────────────────────────────────────────────
    // Catalog Query Handling
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Handles a catalog query and returns PostgreSQL-formatted results.
    /// Supports \d, \dt, \d tablename, and queries against pg_catalog and information_schema.
    /// </summary>
    /// <param name="sql">The catalog query SQL.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>PostgreSQL query result with catalog data.</returns>
    public async Task<PostgreSqlQueryResult> HandleCatalogQuery(string sql, CancellationToken ct)
    {
        var normalized = sql.Trim();
        var lower = normalized.ToLowerInvariant();

        // Handle psql \d commands
        if (lower.StartsWith("\\dt") || lower == "\\d")
        {
            return await ListTablesAsync(ct);
        }

        // \d tablename - describe specific table
        var describeMatch = Regex.Match(lower, @"^\\d\s+(\S+)");
        if (describeMatch.Success)
        {
            var tableName = describeMatch.Groups[1].Value;
            return await DescribeTableAsync(tableName, ct);
        }

        // pg_tables query
        if (lower.Contains("pg_tables") || lower.Contains("pg_catalog.pg_tables"))
        {
            return await HandlePgTablesQuery(sql, ct);
        }

        // pg_class query
        if (lower.Contains("pg_class") || lower.Contains("pg_catalog.pg_class"))
        {
            return await HandlePgClassQuery(ct);
        }

        // pg_attribute query
        if (lower.Contains("pg_attribute") || lower.Contains("pg_catalog.pg_attribute"))
        {
            return await HandlePgAttributeQuery(sql, ct);
        }

        // pg_type query
        if (lower.Contains("pg_type") || lower.Contains("pg_catalog.pg_type"))
        {
            return HandlePgTypeQuery();
        }

        // pg_namespace query
        if (lower.Contains("pg_namespace") || lower.Contains("pg_catalog.pg_namespace"))
        {
            return HandlePgNamespaceQuery();
        }

        // information_schema.columns query
        if (lower.Contains("information_schema.columns"))
        {
            return await HandleInformationSchemaColumnsQuery(sql, ct);
        }

        // information_schema.tables query
        if (lower.Contains("information_schema.tables"))
        {
            return await HandleInformationSchemaTablesQuery(ct);
        }

        // Default: return empty result for unrecognized catalog queries
        return new PostgreSqlQueryResult
        {
            RowDescription = [],
            DataRows = [],
            CommandTag = "SELECT 0"
        };
    }

    // ─────────────────────────────────────────────────────────────
    // Virtual Catalog Table Implementations
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists all tables (\dt equivalent).
    /// </summary>
    private async Task<PostgreSqlQueryResult> ListTablesAsync(CancellationToken ct)
    {
        var tableNames = await GetTableNamesInternalAsync(ct);

        var rowDesc = new List<PostgreSqlFieldDescription>
        {
            MakeField("Schema", 25),    // text
            MakeField("Name", 25),      // text
            MakeField("Type", 25),      // text
            MakeField("Owner", 25)      // text
        };

        var dataRows = new List<List<byte[]?>>();
        foreach (var name in tableNames)
        {
            dataRows.Add(new List<byte[]?>
            {
                TextBytes("public"),
                TextBytes(name),
                TextBytes("table"),
                TextBytes("datawarehouse")
            });
        }

        return new PostgreSqlQueryResult
        {
            RowDescription = rowDesc,
            DataRows = dataRows,
            CommandTag = $"SELECT {dataRows.Count}"
        };
    }

    /// <summary>
    /// Describes a specific table (\d tablename equivalent).
    /// </summary>
    private async Task<PostgreSqlQueryResult> DescribeTableAsync(string tableName, CancellationToken ct)
    {
        var columns = await GetTableColumnsInternalAsync(tableName, ct);

        var rowDesc = new List<PostgreSqlFieldDescription>
        {
            MakeField("Column", 25),        // text
            MakeField("Type", 25),           // text
            MakeField("Collation", 25),      // text
            MakeField("Nullable", 25),       // text
            MakeField("Default", 25)         // text
        };

        var dataRows = new List<List<byte[]?>>();
        foreach (var col in columns)
        {
            dataRows.Add(new List<byte[]?>
            {
                TextBytes(col.Name),
                TextBytes(col.TypeName),
                null, // collation
                TextBytes(col.IsNullable ? "YES" : "NO"),
                col.DefaultValue != null ? TextBytes(col.DefaultValue) : null
            });
        }

        return new PostgreSqlQueryResult
        {
            RowDescription = rowDesc,
            DataRows = dataRows,
            CommandTag = $"SELECT {dataRows.Count}"
        };
    }

    /// <summary>
    /// Handles queries against pg_catalog.pg_tables.
    /// </summary>
    private async Task<PostgreSqlQueryResult> HandlePgTablesQuery(string sql, CancellationToken ct)
    {
        var tableNames = await GetTableNamesInternalAsync(ct);

        var rowDesc = new List<PostgreSqlFieldDescription>
        {
            MakeField("schemaname", 25),
            MakeField("tablename", 25),
            MakeField("tableowner", 25),
            MakeField("tablespace", 25),
            MakeField("hasindexes", 16),
            MakeField("hasrules", 16),
            MakeField("hastriggers", 16)
        };

        var dataRows = new List<List<byte[]?>>();

        // Filter by schemaname if present in WHERE
        var schemaFilter = ExtractWhereValue(sql, "schemaname");

        foreach (var name in tableNames)
        {
            if (schemaFilter != null && schemaFilter != "public")
                continue;

            dataRows.Add(new List<byte[]?>
            {
                TextBytes("public"),
                TextBytes(name),
                TextBytes("datawarehouse"),
                null, // tablespace
                TextBytes("f"),
                TextBytes("f"),
                TextBytes("f")
            });
        }

        return new PostgreSqlQueryResult
        {
            RowDescription = rowDesc,
            DataRows = dataRows,
            CommandTag = $"SELECT {dataRows.Count}"
        };
    }

    /// <summary>
    /// Handles queries against pg_catalog.pg_class.
    /// </summary>
    private async Task<PostgreSqlQueryResult> HandlePgClassQuery(CancellationToken ct)
    {
        var tableNames = await GetTableNamesInternalAsync(ct);

        var rowDesc = new List<PostgreSqlFieldDescription>
        {
            MakeField("oid", 23),           // int4
            MakeField("relname", 25),       // text
            MakeField("relnamespace", 23),  // int4 (pg_namespace OID)
            MakeField("relkind", 25),       // char
            MakeField("reltuples", 701),    // float8
            MakeField("relpages", 23)       // int4
        };

        var dataRows = new List<List<byte[]?>>();
        int oid = 16384; // Start of user table OIDs

        foreach (var name in tableNames)
        {
            var stats = _vdeDataSource.GetTableStatistics(name);
            var rowCount = stats?.RowCount ?? 0;

            dataRows.Add(new List<byte[]?>
            {
                TextBytes(oid.ToString()),
                TextBytes(name),
                TextBytes("2200"), // pg_public namespace OID
                TextBytes("r"),    // 'r' for ordinary table
                TextBytes(rowCount.ToString()),
                TextBytes("0")     // pages
            });
            oid++;
        }

        return new PostgreSqlQueryResult
        {
            RowDescription = rowDesc,
            DataRows = dataRows,
            CommandTag = $"SELECT {dataRows.Count}"
        };
    }

    /// <summary>
    /// Handles queries against pg_catalog.pg_attribute.
    /// </summary>
    private async Task<PostgreSqlQueryResult> HandlePgAttributeQuery(string sql, CancellationToken ct)
    {
        var rowDesc = new List<PostgreSqlFieldDescription>
        {
            MakeField("attrelid", 23),     // int4
            MakeField("attname", 25),      // text
            MakeField("atttypid", 23),     // int4
            MakeField("attnum", 23),       // int4 (smallint in PG, we use int4)
            MakeField("attnotnull", 16),   // bool
            MakeField("atttypmod", 23)     // int4
        };

        var dataRows = new List<List<byte[]?>>();
        var tableNames = await GetTableNamesInternalAsync(ct);

        // P2-2750: fetch all tables' columns in parallel instead of sequential N+1 awaits.
        var columnTasks = tableNames.Select(t => GetTableColumnsInternalAsync(t, ct)).ToArray();
        var allColumns = await Task.WhenAll(columnTasks);

        int tableOid = 16384;
        for (var ti = 0; ti < tableNames.Count; ti++)
        {
            var columns = allColumns[ti];
            short attNum = 1;

            foreach (var col in columns)
            {
                var typeOid = PostgreSqlTypeMapping.ToOid(col.DataType);
                dataRows.Add(new List<byte[]?>
                {
                    TextBytes(tableOid.ToString()),
                    TextBytes(col.Name),
                    TextBytes(typeOid.ToString()),
                    TextBytes(attNum.ToString()),
                    TextBytes(col.IsNullable ? "f" : "t"),
                    TextBytes("-1")
                });
                attNum++;
            }
            tableOid++;
        }

        return new PostgreSqlQueryResult
        {
            RowDescription = rowDesc,
            DataRows = dataRows,
            CommandTag = $"SELECT {dataRows.Count}"
        };
    }

    /// <summary>
    /// Handles queries against pg_catalog.pg_type.
    /// Returns the built-in PostgreSQL type OID registry.
    /// </summary>
    private static PostgreSqlQueryResult HandlePgTypeQuery()
    {
        var rowDesc = new List<PostgreSqlFieldDescription>
        {
            MakeField("oid", 23),        // int4
            MakeField("typname", 25),    // text
            MakeField("typnamespace", 23), // int4
            MakeField("typlen", 23),     // int4 (smallint in PG)
            MakeField("typtype", 25)     // char
        };

        var types = new (int Oid, string Name, int Len)[]
        {
            (16, "bool", 1),
            (17, "bytea", -1),
            (20, "int8", 8),
            (23, "int4", 4),
            (25, "text", -1),
            (701, "float8", 8),
            (1043, "varchar", -1),
            (1700, "numeric", -1),
            (1114, "timestamp", 8),
            (1184, "timestamptz", 8),
            (0, "void", 0)
        };

        var dataRows = types.Select(t => new List<byte[]?>
        {
            TextBytes(t.Oid.ToString()),
            TextBytes(t.Name),
            TextBytes("11"), // pg_catalog namespace OID
            TextBytes(t.Len.ToString()),
            TextBytes("b")  // 'b' for base type
        }).ToList();

        return new PostgreSqlQueryResult
        {
            RowDescription = rowDesc,
            DataRows = dataRows,
            CommandTag = $"SELECT {dataRows.Count}"
        };
    }

    /// <summary>
    /// Handles queries against pg_catalog.pg_namespace.
    /// Returns default schema namespaces.
    /// </summary>
    private static PostgreSqlQueryResult HandlePgNamespaceQuery()
    {
        var rowDesc = new List<PostgreSqlFieldDescription>
        {
            MakeField("oid", 23),        // int4
            MakeField("nspname", 25),    // text
            MakeField("nspowner", 23)    // int4
        };

        var namespaces = new (int Oid, string Name)[]
        {
            (11, "pg_catalog"),
            (2200, "public"),
            (11000, "information_schema")
        };

        var dataRows = namespaces.Select(ns => new List<byte[]?>
        {
            TextBytes(ns.Oid.ToString()),
            TextBytes(ns.Name),
            TextBytes("10") // superuser OID
        }).ToList();

        return new PostgreSqlQueryResult
        {
            RowDescription = rowDesc,
            DataRows = dataRows,
            CommandTag = $"SELECT {dataRows.Count}"
        };
    }

    /// <summary>
    /// Handles queries against information_schema.columns.
    /// </summary>
    private async Task<PostgreSqlQueryResult> HandleInformationSchemaColumnsQuery(string sql, CancellationToken ct)
    {
        var rowDesc = new List<PostgreSqlFieldDescription>
        {
            MakeField("table_catalog", 25),
            MakeField("table_schema", 25),
            MakeField("table_name", 25),
            MakeField("column_name", 25),
            MakeField("ordinal_position", 23),
            MakeField("column_default", 25),
            MakeField("is_nullable", 25),
            MakeField("data_type", 25)
        };

        var dataRows = new List<List<byte[]?>>();

        // Filter by table_name if present in WHERE
        var tableFilter = ExtractWhereValue(sql, "table_name");
        var tableNames = await GetTableNamesInternalAsync(ct);

        foreach (var tableName in tableNames)
        {
            if (tableFilter != null && !string.Equals(tableName, tableFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var columns = await GetTableColumnsInternalAsync(tableName, ct);
            int ordinal = 1;

            foreach (var col in columns)
            {
                dataRows.Add(new List<byte[]?>
                {
                    TextBytes("datawarehouse"),
                    TextBytes("public"),
                    TextBytes(tableName),
                    TextBytes(col.Name),
                    TextBytes(ordinal.ToString()),
                    col.DefaultValue != null ? TextBytes(col.DefaultValue) : null,
                    TextBytes(col.IsNullable ? "YES" : "NO"),
                    TextBytes(col.TypeName)
                });
                ordinal++;
            }
        }

        return new PostgreSqlQueryResult
        {
            RowDescription = rowDesc,
            DataRows = dataRows,
            CommandTag = $"SELECT {dataRows.Count}"
        };
    }

    /// <summary>
    /// Handles queries against information_schema.tables.
    /// </summary>
    private async Task<PostgreSqlQueryResult> HandleInformationSchemaTablesQuery(CancellationToken ct)
    {
        var tableNames = await GetTableNamesInternalAsync(ct);

        var rowDesc = new List<PostgreSqlFieldDescription>
        {
            MakeField("table_catalog", 25),
            MakeField("table_schema", 25),
            MakeField("table_name", 25),
            MakeField("table_type", 25)
        };

        var dataRows = tableNames.Select(name => new List<byte[]?>
        {
            TextBytes("datawarehouse"),
            TextBytes("public"),
            TextBytes(name),
            TextBytes("BASE TABLE")
        }).ToList();

        return new PostgreSqlQueryResult
        {
            RowDescription = rowDesc,
            DataRows = dataRows,
            CommandTag = $"SELECT {dataRows.Count}"
        };
    }

    // ─────────────────────────────────────────────────────────────
    // Internal Helpers
    // ─────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<string>> GetTableNamesInternalAsync(CancellationToken ct)
    {
        if (GetTableNamesAsync != null)
            return await GetTableNamesAsync(ct);

        // Fallback: return empty list if no delegate is configured
        return Array.Empty<string>();
    }

    private async Task<IReadOnlyList<CatalogColumnInfo>> GetTableColumnsInternalAsync(string tableName, CancellationToken ct)
    {
        if (GetTableColumnsAsync != null)
            return await GetTableColumnsAsync(tableName, ct);

        // Fallback: return empty list if no delegate is configured
        return Array.Empty<CatalogColumnInfo>();
    }

    private static PostgreSqlFieldDescription MakeField(string name, int typeOid)
    {
        return new PostgreSqlFieldDescription(
            name,
            0,
            0,
            typeOid,
            PostgreSqlTypeMapping.GetTypeSize(typeOid),
            PostgreSqlTypeMapping.GetTypeModifier(typeOid),
            PostgreSqlTypeMapping.GetFormatCode(typeOid));
    }

    private static byte[] TextBytes(string value)
    {
        return System.Text.Encoding.UTF8.GetBytes(value);
    }

    /// <summary>
    /// Extracts a WHERE clause value for a simple column = 'value' pattern.
    /// </summary>
    private static string? ExtractWhereValue(string sql, string columnName)
    {
        var pattern = $@"{Regex.Escape(columnName)}\s*=\s*'([^']*)'";
        var match = Regex.Match(sql, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}

/// <summary>
/// Column information for catalog queries, describing a single column in a VDE table.
/// </summary>
/// <param name="Name">Column name.</param>
/// <param name="TypeName">PostgreSQL type name (e.g., "int4", "text", "timestamp").</param>
/// <param name="DataType">DW ColumnDataType.</param>
/// <param name="IsNullable">Whether the column allows NULL values.</param>
/// <param name="DefaultValue">Optional default value expression.</param>
public sealed record CatalogColumnInfo(
    string Name,
    string TypeName,
    ColumnDataType DataType,
    bool IsNullable = true,
    string? DefaultValue = null);
