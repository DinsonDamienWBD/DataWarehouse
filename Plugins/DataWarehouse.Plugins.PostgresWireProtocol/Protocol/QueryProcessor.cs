using System.Text;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.PostgresWireProtocol.Protocol;

/// <summary>
/// Processes SQL queries and executes them against DataWarehouse.
/// </summary>
public sealed class QueryProcessor
{
    private readonly Func<string, CancellationToken, Task<QueryResult>> _executeSqlAsync;

    public QueryProcessor(Func<string, CancellationToken, Task<QueryResult>> executeSqlAsync)
    {
        _executeSqlAsync = executeSqlAsync;
    }

    /// <summary>
    /// Executes a SQL query and returns result.
    /// </summary>
    public async Task<QueryResult> ExecuteQueryAsync(string sql, CancellationToken ct = default)
    {
        sql = sql.Trim().TrimEnd(';');

        // Handle empty query
        if (string.IsNullOrWhiteSpace(sql))
        {
            return new QueryResult
            {
                IsEmpty = true,
                CommandTag = ""
            };
        }

        // Handle PostgreSQL-specific queries
        if (IsPostgresSystemQuery(sql))
        {
            return ExecuteSystemQuery(sql);
        }

        // Execute via FederatedQuery or SQL interface
        return await _executeSqlAsync(sql, ct);
    }

    /// <summary>
    /// Checks if query is a PostgreSQL system catalog query.
    /// </summary>
    private bool IsPostgresSystemQuery(string sql)
    {
        var lower = sql.ToLowerInvariant();

        return lower.Contains("pg_catalog") ||
               lower.Contains("information_schema") ||
               lower.Contains("pg_type") ||
               lower.Contains("pg_class") ||
               lower.Contains("pg_namespace") ||
               lower.Contains("pg_attribute") ||
               lower.Contains("pg_database") ||
               lower.Contains("pg_tables") ||
               lower.Contains("current_schema") ||
               lower.Contains("version()");
    }

    /// <summary>
    /// Executes PostgreSQL system queries (for client compatibility).
    /// </summary>
    private QueryResult ExecuteSystemQuery(string sql)
    {
        var lower = sql.ToLowerInvariant();

        // version() function
        if (lower.Contains("version()"))
        {
            return new QueryResult
            {
                Columns = new List<PgColumnDescription>
                {
                    TypeConverter.CreateColumnDescription("version", typeof(string), 0)
                },
                Rows = new List<List<byte[]?>>
                {
                    new() { Encoding.UTF8.GetBytes("PostgreSQL 14.0 (DataWarehouse Compatible)") }
                },
                CommandTag = "SELECT 1"
            };
        }

        // current_schema()
        if (lower.Contains("current_schema"))
        {
            return new QueryResult
            {
                Columns = new List<PgColumnDescription>
                {
                    TypeConverter.CreateColumnDescription("current_schema", typeof(string), 0)
                },
                Rows = new List<List<byte[]?>>
                {
                    new() { Encoding.UTF8.GetBytes("public") }
                },
                CommandTag = "SELECT 1"
            };
        }

        // pg_catalog.pg_type (type information)
        if (lower.Contains("pg_type"))
        {
            return new QueryResult
            {
                Columns = new List<PgColumnDescription>
                {
                    TypeConverter.CreateColumnDescription("oid", typeof(int), 0),
                    TypeConverter.CreateColumnDescription("typname", typeof(string), 1),
                    TypeConverter.CreateColumnDescription("typlen", typeof(short), 2)
                },
                Rows = new List<List<byte[]?>>
                {
                    new() { Encoding.UTF8.GetBytes("25"), Encoding.UTF8.GetBytes("text"), Encoding.UTF8.GetBytes("-1") },
                    new() { Encoding.UTF8.GetBytes("23"), Encoding.UTF8.GetBytes("int4"), Encoding.UTF8.GetBytes("4") },
                    new() { Encoding.UTF8.GetBytes("20"), Encoding.UTF8.GetBytes("int8"), Encoding.UTF8.GetBytes("8") }
                },
                CommandTag = "SELECT 3"
            };
        }

        // pg_catalog.pg_database (database list)
        if (lower.Contains("pg_database"))
        {
            return new QueryResult
            {
                Columns = new List<PgColumnDescription>
                {
                    TypeConverter.CreateColumnDescription("datname", typeof(string), 0),
                    TypeConverter.CreateColumnDescription("datdba", typeof(int), 1),
                    TypeConverter.CreateColumnDescription("encoding", typeof(int), 2)
                },
                Rows = new List<List<byte[]?>>
                {
                    new() { Encoding.UTF8.GetBytes("datawarehouse"), Encoding.UTF8.GetBytes("10"), Encoding.UTF8.GetBytes("6") }
                },
                CommandTag = "SELECT 1"
            };
        }

        // pg_catalog.pg_tables (table list)
        if (lower.Contains("pg_tables"))
        {
            return new QueryResult
            {
                Columns = new List<PgColumnDescription>
                {
                    TypeConverter.CreateColumnDescription("schemaname", typeof(string), 0),
                    TypeConverter.CreateColumnDescription("tablename", typeof(string), 1),
                    TypeConverter.CreateColumnDescription("tableowner", typeof(string), 2)
                },
                Rows = new List<List<byte[]?>>
                {
                    new() { Encoding.UTF8.GetBytes("public"), Encoding.UTF8.GetBytes("manifests"), Encoding.UTF8.GetBytes("postgres") },
                    new() { Encoding.UTF8.GetBytes("public"), Encoding.UTF8.GetBytes("blobs"), Encoding.UTF8.GetBytes("postgres") }
                },
                CommandTag = "SELECT 2"
            };
        }

        // information_schema.tables
        if (lower.Contains("information_schema.tables"))
        {
            return new QueryResult
            {
                Columns = new List<PgColumnDescription>
                {
                    TypeConverter.CreateColumnDescription("table_schema", typeof(string), 0),
                    TypeConverter.CreateColumnDescription("table_name", typeof(string), 1),
                    TypeConverter.CreateColumnDescription("table_type", typeof(string), 2)
                },
                Rows = new List<List<byte[]?>>
                {
                    new() { Encoding.UTF8.GetBytes("public"), Encoding.UTF8.GetBytes("manifests"), Encoding.UTF8.GetBytes("BASE TABLE") },
                    new() { Encoding.UTF8.GetBytes("public"), Encoding.UTF8.GetBytes("blobs"), Encoding.UTF8.GetBytes("BASE TABLE") }
                },
                CommandTag = "SELECT 2"
            };
        }

        // Default: return empty result
        return new QueryResult
        {
            Columns = new List<PgColumnDescription>(),
            Rows = new List<List<byte[]?>>(),
            CommandTag = "SELECT 0"
        };
    }

    /// <summary>
    /// Parses a prepared statement to extract parameter placeholders.
    /// </summary>
    public List<int> ExtractParameterTypes(string sql)
    {
        var paramTypes = new List<int>();

        // Find all $1, $2, etc. parameter references
        var matches = Regex.Matches(sql, @"\$(\d+)");
        var maxParam = 0;

        foreach (Match match in matches)
        {
            if (int.TryParse(match.Groups[1].Value, out var paramNum))
            {
                if (paramNum > maxParam)
                    maxParam = paramNum;
            }
        }

        // Default all parameters to text type
        for (var i = 0; i < maxParam; i++)
        {
            paramTypes.Add(PgProtocolConstants.OidText);
        }

        return paramTypes;
    }

    /// <summary>
    /// Substitutes parameters into a prepared statement.
    /// </summary>
    public string SubstituteParameters(string sql, List<byte[]?> parameters)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            var param = parameters[i];
            var paramPlaceholder = $"${i + 1}";

            string replacement;
            if (param == null)
            {
                replacement = "NULL";
            }
            else
            {
                var value = Encoding.UTF8.GetString(param);
                // Escape single quotes
                value = value.Replace("'", "''");
                replacement = $"'{value}'";
            }

            sql = sql.Replace(paramPlaceholder, replacement);
        }

        return sql;
    }
}

/// <summary>
/// Query execution result.
/// </summary>
public sealed class QueryResult
{
    public bool IsEmpty { get; init; }
    public List<PgColumnDescription> Columns { get; init; } = new();
    public List<List<byte[]?>> Rows { get; init; } = new();
    public string CommandTag { get; init; } = "";
    public string? ErrorMessage { get; init; }
    public string? ErrorSqlState { get; init; }
}
