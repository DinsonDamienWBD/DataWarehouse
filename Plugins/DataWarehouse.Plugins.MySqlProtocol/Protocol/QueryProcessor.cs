using System.Text;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.MySqlProtocol.Protocol;

/// <summary>
/// Processes SQL queries for the MySQL protocol.
/// Handles system queries, prepared statements, and query transformation.
/// </summary>
public sealed partial class QueryProcessor
{
    private readonly Func<string, CancellationToken, Task<MySqlQueryResult>> _executeSqlAsync;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryProcessor"/> class.
    /// </summary>
    /// <param name="executeSqlAsync">Delegate to execute SQL queries against the backend.</param>
    /// <exception cref="ArgumentNullException">Thrown when executeSqlAsync is null.</exception>
    public QueryProcessor(Func<string, CancellationToken, Task<MySqlQueryResult>> executeSqlAsync)
    {
        _executeSqlAsync = executeSqlAsync ?? throw new ArgumentNullException(nameof(executeSqlAsync));
    }

    /// <summary>
    /// Executes a SQL query and returns the result.
    /// </summary>
    /// <param name="sql">The SQL query to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The query result.</returns>
    public async Task<MySqlQueryResult> ExecuteQueryAsync(string sql, CancellationToken ct = default)
    {
        sql = sql.Trim().TrimEnd(';');

        if (string.IsNullOrWhiteSpace(sql))
        {
            return new MySqlQueryResult
            {
                IsEmpty = true,
                CommandTag = ""
            };
        }

        // Handle MySQL system queries for tool compatibility
        if (IsMySqlSystemQuery(sql))
        {
            return ExecuteSystemQuery(sql);
        }

        // Handle special statements
        if (IsSetStatement(sql))
        {
            return HandleSetStatement(sql);
        }

        if (IsShowStatement(sql))
        {
            return HandleShowStatement(sql);
        }

        // Execute via backend
        return await _executeSqlAsync(sql, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts parameter placeholders from a SQL query.
    /// </summary>
    /// <param name="sql">The SQL query.</param>
    /// <returns>Number of parameters found.</returns>
    public int ExtractParameterCount(string sql)
    {
        // Count ? placeholders
        var count = 0;
        for (int i = 0; i < sql.Length; i++)
        {
            if (sql[i] == '?')
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Substitutes parameters into a prepared statement query.
    /// </summary>
    /// <param name="sql">The prepared statement SQL with ? placeholders.</param>
    /// <param name="parameters">The parameter values.</param>
    /// <returns>The SQL with substituted parameters.</returns>
    public string SubstituteParameters(string sql, IReadOnlyList<object?> parameters)
    {
        var sb = new StringBuilder();
        var paramIndex = 0;

        for (int i = 0; i < sql.Length; i++)
        {
            if (sql[i] == '?' && paramIndex < parameters.Count)
            {
                var value = parameters[paramIndex++];
                sb.Append(FormatParameterValue(value));
            }
            else
            {
                sb.Append(sql[i]);
            }
        }

        return sb.ToString();
    }

    private bool IsMySqlSystemQuery(string sql)
    {
        var lower = sql.ToLowerInvariant();

        return lower.Contains("information_schema") ||
               lower.Contains("mysql.") ||
               lower.Contains("performance_schema") ||
               lower.Contains("sys.") ||
               lower.StartsWith("select @@") ||
               lower.StartsWith("select version()") ||
               lower.StartsWith("select database()") ||
               lower.StartsWith("select user()") ||
               lower.StartsWith("select current_user") ||
               lower.StartsWith("select connection_id") ||
               lower.Contains("collation_connection");
    }

    private bool IsSetStatement(string sql)
    {
        return sql.StartsWith("SET ", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsShowStatement(string sql)
    {
        return sql.StartsWith("SHOW ", StringComparison.OrdinalIgnoreCase);
    }

    private MySqlQueryResult ExecuteSystemQuery(string sql)
    {
        var lower = sql.ToLowerInvariant();

        // SELECT @@version
        if (lower.Contains("@@version") || lower.Contains("version()"))
        {
            return CreateSingleValueResult("@@version", "8.0.35-DataWarehouse");
        }

        // SELECT DATABASE()
        if (lower.Contains("database()"))
        {
            return CreateSingleValueResult("database()", "datawarehouse");
        }

        // SELECT USER() or CURRENT_USER()
        if (lower.Contains("user()") || lower.Contains("current_user"))
        {
            return CreateSingleValueResult("user()", "root@localhost");
        }

        // SELECT CONNECTION_ID()
        if (lower.Contains("connection_id"))
        {
            return CreateSingleValueResult("connection_id()", "1");
        }

        // SELECT @@session variables
        if (lower.Contains("@@session.") || lower.Contains("@@"))
        {
            return HandleSessionVariableQuery(sql);
        }

        // information_schema.schemata (databases)
        if (lower.Contains("information_schema.schemata"))
        {
            return new MySqlQueryResult
            {
                Columns = new List<MySqlColumnDescription>
                {
                    TypeConverter.CreateColumnDescription("CATALOG_NAME", 0),
                    TypeConverter.CreateColumnDescription("SCHEMA_NAME", 1),
                    TypeConverter.CreateColumnDescription("DEFAULT_CHARACTER_SET_NAME", 2),
                    TypeConverter.CreateColumnDescription("DEFAULT_COLLATION_NAME", 3)
                },
                Rows = new List<List<byte[]?>>
                {
                    new() {
                        Encoding.UTF8.GetBytes("def"),
                        Encoding.UTF8.GetBytes("datawarehouse"),
                        Encoding.UTF8.GetBytes("utf8mb4"),
                        Encoding.UTF8.GetBytes("utf8mb4_general_ci")
                    },
                    new() {
                        Encoding.UTF8.GetBytes("def"),
                        Encoding.UTF8.GetBytes("information_schema"),
                        Encoding.UTF8.GetBytes("utf8mb3"),
                        Encoding.UTF8.GetBytes("utf8mb3_general_ci")
                    }
                },
                AffectedRows = 2,
                CommandTag = "SELECT"
            };
        }

        // information_schema.tables
        if (lower.Contains("information_schema.tables"))
        {
            return new MySqlQueryResult
            {
                Columns = new List<MySqlColumnDescription>
                {
                    TypeConverter.CreateColumnDescription("TABLE_CATALOG", 0),
                    TypeConverter.CreateColumnDescription("TABLE_SCHEMA", 1),
                    TypeConverter.CreateColumnDescription("TABLE_NAME", 2),
                    TypeConverter.CreateColumnDescription("TABLE_TYPE", 3),
                    TypeConverter.CreateColumnDescription("ENGINE", 4)
                },
                Rows = new List<List<byte[]?>>
                {
                    new() {
                        Encoding.UTF8.GetBytes("def"),
                        Encoding.UTF8.GetBytes("datawarehouse"),
                        Encoding.UTF8.GetBytes("manifests"),
                        Encoding.UTF8.GetBytes("BASE TABLE"),
                        Encoding.UTF8.GetBytes("InnoDB")
                    },
                    new() {
                        Encoding.UTF8.GetBytes("def"),
                        Encoding.UTF8.GetBytes("datawarehouse"),
                        Encoding.UTF8.GetBytes("blobs"),
                        Encoding.UTF8.GetBytes("BASE TABLE"),
                        Encoding.UTF8.GetBytes("InnoDB")
                    }
                },
                AffectedRows = 2,
                CommandTag = "SELECT"
            };
        }

        // information_schema.columns
        if (lower.Contains("information_schema.columns"))
        {
            return new MySqlQueryResult
            {
                Columns = new List<MySqlColumnDescription>
                {
                    TypeConverter.CreateColumnDescription("TABLE_CATALOG", 0),
                    TypeConverter.CreateColumnDescription("TABLE_SCHEMA", 1),
                    TypeConverter.CreateColumnDescription("TABLE_NAME", 2),
                    TypeConverter.CreateColumnDescription("COLUMN_NAME", 3),
                    TypeConverter.CreateColumnDescription("ORDINAL_POSITION", 4),
                    TypeConverter.CreateColumnDescription("DATA_TYPE", 5),
                    TypeConverter.CreateColumnDescription("IS_NULLABLE", 6)
                },
                Rows = new List<List<byte[]?>>(),
                AffectedRows = 0,
                CommandTag = "SELECT"
            };
        }

        // Default empty result
        return new MySqlQueryResult
        {
            Columns = new List<MySqlColumnDescription>(),
            Rows = new List<List<byte[]?>>(),
            AffectedRows = 0,
            CommandTag = "SELECT"
        };
    }

    private MySqlQueryResult HandleSessionVariableQuery(string sql)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["@@version"] = "8.0.35-DataWarehouse",
            ["@@version_comment"] = "DataWarehouse MySQL Protocol",
            ["@@version_compile_os"] = Environment.OSVersion.Platform.ToString(),
            ["@@version_compile_machine"] = Environment.Is64BitProcess ? "x86_64" : "x86",
            ["@@character_set_client"] = "utf8mb4",
            ["@@character_set_connection"] = "utf8mb4",
            ["@@character_set_results"] = "utf8mb4",
            ["@@character_set_server"] = "utf8mb4",
            ["@@character_set_database"] = "utf8mb4",
            ["@@collation_connection"] = "utf8mb4_general_ci",
            ["@@collation_server"] = "utf8mb4_general_ci",
            ["@@collation_database"] = "utf8mb4_general_ci",
            ["@@max_allowed_packet"] = "67108864",
            ["@@sql_mode"] = "ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION",
            ["@@time_zone"] = "SYSTEM",
            ["@@system_time_zone"] = TimeZoneInfo.Local.StandardName,
            ["@@wait_timeout"] = "28800",
            ["@@interactive_timeout"] = "28800",
            ["@@transaction_isolation"] = "REPEATABLE-READ",
            ["@@tx_isolation"] = "REPEATABLE-READ",
            ["@@autocommit"] = "1",
            ["@@lower_case_table_names"] = "0",
            ["@@net_write_timeout"] = "60",
            ["@@net_read_timeout"] = "30",
            ["@@sql_auto_is_null"] = "0",
            ["@@session.auto_increment_increment"] = "1",
            ["@@init_connect"] = "",
            ["@@license"] = "Commercial"
        };

        // Extract variable names from query
        var matches = SessionVarRegex().Matches(sql);
        var columns = new List<MySqlColumnDescription>();
        var values = new List<byte[]?>();

        foreach (Match match in matches)
        {
            var varName = match.Value;
            columns.Add(TypeConverter.CreateColumnDescription(varName, columns.Count));

            if (variables.TryGetValue(varName, out var value) ||
                variables.TryGetValue(varName.Replace("@@session.", "@@"), out value))
            {
                values.Add(Encoding.UTF8.GetBytes(value));
            }
            else
            {
                values.Add(Encoding.UTF8.GetBytes(""));
            }
        }

        if (columns.Count == 0)
        {
            return CreateSingleValueResult("value", "");
        }

        return new MySqlQueryResult
        {
            Columns = columns,
            Rows = new List<List<byte[]?>> { values },
            AffectedRows = 1,
            CommandTag = "SELECT"
        };
    }

    private MySqlQueryResult HandleSetStatement(string sql)
    {
        // SET statements are generally accepted but ignored
        // We return OK for compatibility
        return new MySqlQueryResult
        {
            IsEmpty = false,
            AffectedRows = 0,
            CommandTag = "SET"
        };
    }

    private MySqlQueryResult HandleShowStatement(string sql)
    {
        var lower = sql.ToLowerInvariant();

        // SHOW DATABASES
        if (lower.Contains("databases") || lower.Contains("schemas"))
        {
            return new MySqlQueryResult
            {
                Columns = new List<MySqlColumnDescription>
                {
                    TypeConverter.CreateColumnDescription("Database", 0)
                },
                Rows = new List<List<byte[]?>>
                {
                    new() { Encoding.UTF8.GetBytes("datawarehouse") },
                    new() { Encoding.UTF8.GetBytes("information_schema") },
                    new() { Encoding.UTF8.GetBytes("performance_schema") },
                    new() { Encoding.UTF8.GetBytes("mysql") }
                },
                AffectedRows = 4,
                CommandTag = "SHOW"
            };
        }

        // SHOW TABLES
        if (lower.Contains("tables"))
        {
            return new MySqlQueryResult
            {
                Columns = new List<MySqlColumnDescription>
                {
                    TypeConverter.CreateColumnDescription("Tables_in_datawarehouse", 0)
                },
                Rows = new List<List<byte[]?>>
                {
                    new() { Encoding.UTF8.GetBytes("manifests") },
                    new() { Encoding.UTF8.GetBytes("blobs") }
                },
                AffectedRows = 2,
                CommandTag = "SHOW"
            };
        }

        // SHOW VARIABLES
        if (lower.Contains("variables"))
        {
            return new MySqlQueryResult
            {
                Columns = new List<MySqlColumnDescription>
                {
                    TypeConverter.CreateColumnDescription("Variable_name", 0),
                    TypeConverter.CreateColumnDescription("Value", 1)
                },
                Rows = new List<List<byte[]?>>
                {
                    new() { Encoding.UTF8.GetBytes("version"), Encoding.UTF8.GetBytes("8.0.35-DataWarehouse") },
                    new() { Encoding.UTF8.GetBytes("version_comment"), Encoding.UTF8.GetBytes("DataWarehouse MySQL Protocol") },
                    new() { Encoding.UTF8.GetBytes("character_set_server"), Encoding.UTF8.GetBytes("utf8mb4") },
                    new() { Encoding.UTF8.GetBytes("collation_server"), Encoding.UTF8.GetBytes("utf8mb4_general_ci") }
                },
                AffectedRows = 4,
                CommandTag = "SHOW"
            };
        }

        // SHOW STATUS
        if (lower.Contains("status"))
        {
            return new MySqlQueryResult
            {
                Columns = new List<MySqlColumnDescription>
                {
                    TypeConverter.CreateColumnDescription("Variable_name", 0),
                    TypeConverter.CreateColumnDescription("Value", 1)
                },
                Rows = new List<List<byte[]?>>
                {
                    new() { Encoding.UTF8.GetBytes("Threads_connected"), Encoding.UTF8.GetBytes("1") },
                    new() { Encoding.UTF8.GetBytes("Uptime"), Encoding.UTF8.GetBytes("3600") }
                },
                AffectedRows = 2,
                CommandTag = "SHOW"
            };
        }

        // SHOW COLLATION
        if (lower.Contains("collation"))
        {
            return new MySqlQueryResult
            {
                Columns = new List<MySqlColumnDescription>
                {
                    TypeConverter.CreateColumnDescription("Collation", 0),
                    TypeConverter.CreateColumnDescription("Charset", 1),
                    TypeConverter.CreateColumnDescription("Id", 2),
                    TypeConverter.CreateColumnDescription("Default", 3),
                    TypeConverter.CreateColumnDescription("Compiled", 4),
                    TypeConverter.CreateColumnDescription("Sortlen", 5)
                },
                Rows = new List<List<byte[]?>>
                {
                    new() {
                        Encoding.UTF8.GetBytes("utf8mb4_general_ci"),
                        Encoding.UTF8.GetBytes("utf8mb4"),
                        Encoding.UTF8.GetBytes("45"),
                        Encoding.UTF8.GetBytes("Yes"),
                        Encoding.UTF8.GetBytes("Yes"),
                        Encoding.UTF8.GetBytes("1")
                    }
                },
                AffectedRows = 1,
                CommandTag = "SHOW"
            };
        }

        // SHOW ENGINES
        if (lower.Contains("engines"))
        {
            return new MySqlQueryResult
            {
                Columns = new List<MySqlColumnDescription>
                {
                    TypeConverter.CreateColumnDescription("Engine", 0),
                    TypeConverter.CreateColumnDescription("Support", 1),
                    TypeConverter.CreateColumnDescription("Comment", 2)
                },
                Rows = new List<List<byte[]?>>
                {
                    new() {
                        Encoding.UTF8.GetBytes("InnoDB"),
                        Encoding.UTF8.GetBytes("DEFAULT"),
                        Encoding.UTF8.GetBytes("DataWarehouse storage engine")
                    }
                },
                AffectedRows = 1,
                CommandTag = "SHOW"
            };
        }

        // SHOW WARNINGS
        if (lower.Contains("warnings"))
        {
            return new MySqlQueryResult
            {
                Columns = new List<MySqlColumnDescription>
                {
                    TypeConverter.CreateColumnDescription("Level", 0),
                    TypeConverter.CreateColumnDescription("Code", 1),
                    TypeConverter.CreateColumnDescription("Message", 2)
                },
                Rows = new List<List<byte[]?>>(),
                AffectedRows = 0,
                CommandTag = "SHOW"
            };
        }

        // Default empty result
        return new MySqlQueryResult
        {
            Columns = new List<MySqlColumnDescription>(),
            Rows = new List<List<byte[]?>>(),
            AffectedRows = 0,
            CommandTag = "SHOW"
        };
    }

    private MySqlQueryResult CreateSingleValueResult(string columnName, string value)
    {
        return new MySqlQueryResult
        {
            Columns = new List<MySqlColumnDescription>
            {
                TypeConverter.CreateColumnDescription(columnName, 0)
            },
            Rows = new List<List<byte[]?>>
            {
                new() { Encoding.UTF8.GetBytes(value) }
            },
            AffectedRows = 1,
            CommandTag = "SELECT"
        };
    }

    private string FormatParameterValue(object? value)
    {
        return value switch
        {
            null => "NULL",
            bool b => b ? "1" : "0",
            byte or sbyte or short or ushort or int or uint or long or ulong =>
                value.ToString() ?? "0",
            float f => f.ToString(System.Globalization.CultureInfo.InvariantCulture),
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            decimal dec => dec.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
            TimeSpan ts => $"'{ts}'",
            byte[] bytes => $"X'{Convert.ToHexString(bytes)}'",
            string s => $"'{s.Replace("'", "''")}'",
            _ => $"'{value.ToString()?.Replace("'", "''") ?? ""}'"
        };
    }

    [GeneratedRegex(@"@@[\w.]+", RegexOptions.IgnoreCase)]
    private static partial Regex SessionVarRegex();
}

/// <summary>
/// Result of a MySQL query execution.
/// </summary>
public sealed class MySqlQueryResult
{
    /// <summary>
    /// Whether the query returned an empty result.
    /// </summary>
    public bool IsEmpty { get; init; }

    /// <summary>
    /// Column definitions for result set.
    /// </summary>
    public List<MySqlColumnDescription> Columns { get; init; } = new();

    /// <summary>
    /// Row data (each row is a list of column values as bytes).
    /// </summary>
    public List<List<byte[]?>> Rows { get; init; } = new();

    /// <summary>
    /// Number of affected rows for INSERT/UPDATE/DELETE.
    /// </summary>
    public ulong AffectedRows { get; init; }

    /// <summary>
    /// Last insert ID for auto-increment columns.
    /// </summary>
    public ulong LastInsertId { get; init; }

    /// <summary>
    /// Command tag (e.g., "SELECT", "INSERT", "UPDATE").
    /// </summary>
    public string CommandTag { get; init; } = string.Empty;

    /// <summary>
    /// Error message if query failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// MySQL error code if query failed.
    /// </summary>
    public ushort? ErrorCode { get; init; }

    /// <summary>
    /// SQL state if query failed.
    /// </summary>
    public string? SqlState { get; init; }

    /// <summary>
    /// Number of warnings generated.
    /// </summary>
    public ushort Warnings { get; init; }
}
