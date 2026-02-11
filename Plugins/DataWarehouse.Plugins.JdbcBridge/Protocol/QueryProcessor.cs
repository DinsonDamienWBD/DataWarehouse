using System.Text;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.JdbcBridge.Protocol;

/// <summary>
/// Processes SQL queries for the JDBC bridge.
/// Handles query execution, parameter substitution, and result mapping.
/// </summary>
public sealed class QueryProcessor
{
    private readonly Func<string, CancellationToken, Task<JdbcQueryResult>> _executeSqlAsync;
    private static readonly Regex ParameterPattern = new(@"\?", RegexOptions.Compiled);
    private static readonly Regex NamedParameterPattern = new(@":(\w+)", RegexOptions.Compiled);

    /// <summary>
    /// Initializes a new instance of the QueryProcessor class.
    /// </summary>
    /// <param name="executeSqlAsync">The SQL execution delegate.</param>
    /// <exception cref="ArgumentNullException">Thrown when executeSqlAsync is null.</exception>
    public QueryProcessor(Func<string, CancellationToken, Task<JdbcQueryResult>> executeSqlAsync)
    {
        _executeSqlAsync = executeSqlAsync ?? throw new ArgumentNullException(nameof(executeSqlAsync));
    }

    /// <summary>
    /// Executes a SQL query.
    /// </summary>
    /// <param name="sql">The SQL query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The query result.</returns>
    public async Task<JdbcQueryResult> ExecuteQueryAsync(string sql, CancellationToken ct = default)
    {
        sql = NormalizeSql(sql);

        // Handle empty query
        if (string.IsNullOrWhiteSpace(sql))
        {
            return new JdbcQueryResult
            {
                UpdateCount = 0
            };
        }

        // Handle JDBC metadata queries
        if (IsJdbcMetadataQuery(sql))
        {
            return ExecuteMetadataQuery(sql);
        }

        // Execute via the provided delegate
        try
        {
            return await _executeSqlAsync(sql, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new JdbcQueryResult
            {
                ErrorMessage = ex.Message,
                SqlState = JdbcSqlState.InternalError
            };
        }
    }

    /// <summary>
    /// Prepares a SQL statement and returns parameter information.
    /// </summary>
    /// <param name="sql">The SQL query with parameters.</param>
    /// <param name="statementId">The statement identifier.</param>
    /// <returns>The prepared statement info.</returns>
    public JdbcPreparedStatementInfo PrepareStatement(string sql, int statementId)
    {
        sql = NormalizeSql(sql);

        var parameters = ExtractParameters(sql);

        return new JdbcPreparedStatementInfo
        {
            StatementId = statementId,
            Sql = sql,
            Parameters = parameters
        };
    }

    /// <summary>
    /// Substitutes parameters into a prepared statement.
    /// </summary>
    /// <param name="sql">The SQL query with placeholders.</param>
    /// <param name="parameters">The parameter values.</param>
    /// <returns>The SQL with substituted parameters.</returns>
    public string SubstituteParameters(string sql, IList<object?> parameters)
    {
        if (parameters.Count == 0)
        {
            return sql;
        }

        var result = new StringBuilder(sql.Length + parameters.Count * 10);
        var paramIndex = 0;
        var lastIndex = 0;

        foreach (Match match in ParameterPattern.Matches(sql))
        {
            result.Append(sql, lastIndex, match.Index - lastIndex);

            if (paramIndex < parameters.Count)
            {
                result.Append(FormatValue(parameters[paramIndex]));
                paramIndex++;
            }
            else
            {
                result.Append("NULL");
            }

            lastIndex = match.Index + match.Length;
        }

        result.Append(sql, lastIndex, sql.Length - lastIndex);

        return result.ToString();
    }

    /// <summary>
    /// Normalizes SQL by trimming whitespace and semicolons.
    /// </summary>
    /// <param name="sql">The SQL query.</param>
    /// <returns>The normalized SQL.</returns>
    private static string NormalizeSql(string sql)
    {
        return sql.Trim().TrimEnd(';');
    }

    /// <summary>
    /// Extracts parameter metadata from a SQL query.
    /// </summary>
    /// <param name="sql">The SQL query.</param>
    /// <returns>The parameter metadata list.</returns>
    private static List<JdbcParameterMetadata> ExtractParameters(string sql)
    {
        var parameters = new List<JdbcParameterMetadata>();
        var index = 1;

        foreach (Match match in ParameterPattern.Matches(sql))
        {
            parameters.Add(new JdbcParameterMetadata
            {
                Index = index++,
                SqlType = JdbcSqlTypes.VARCHAR, // Default to VARCHAR
                TypeName = "VARCHAR",
                Mode = 1 // parameterModeIn
            });
        }

        return parameters;
    }

    /// <summary>
    /// Formats a value for SQL string substitution.
    /// </summary>
    /// <param name="value">The value to format.</param>
    /// <returns>The formatted SQL literal.</returns>
    private static string FormatValue(object? value)
    {
        if (value == null || value == DBNull.Value)
        {
            return "NULL";
        }

        return value switch
        {
            bool b => b ? "TRUE" : "FALSE",
            byte or sbyte or short or ushort or int or uint or long or ulong => value.ToString()!,
            float f => f.ToString(System.Globalization.CultureInfo.InvariantCulture),
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            decimal dec => dec.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss.fff}'",
            DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss.fffzzz}'",
            DateOnly date => $"'{date:yyyy-MM-dd}'",
            TimeOnly time => $"'{time:HH:mm:ss.fff}'",
            TimeSpan ts => $"'{ts:hh\\:mm\\:ss\\.fff}'",
            byte[] bytes => $"X'{Convert.ToHexString(bytes)}'",
            string s => $"'{EscapeString(s)}'",
            _ => $"'{EscapeString(value.ToString()!)}'"
        };
    }

    /// <summary>
    /// Escapes a string for SQL.
    /// </summary>
    /// <param name="value">The string to escape.</param>
    /// <returns>The escaped string.</returns>
    private static string EscapeString(string value)
    {
        return value.Replace("'", "''");
    }

    /// <summary>
    /// Checks if a query is a JDBC metadata query.
    /// </summary>
    /// <param name="sql">The SQL query.</param>
    /// <returns>True if it's a metadata query.</returns>
    private static bool IsJdbcMetadataQuery(string sql)
    {
        var lower = sql.ToLowerInvariant();
        return lower.StartsWith("select 1") ||
               lower.Contains("information_schema") ||
               lower.Contains("jdbc_metadata") ||
               lower.StartsWith("call ");
    }

    /// <summary>
    /// Executes a JDBC metadata query.
    /// </summary>
    /// <param name="sql">The SQL query.</param>
    /// <returns>The query result.</returns>
    private static JdbcQueryResult ExecuteMetadataQuery(string sql)
    {
        var lower = sql.ToLowerInvariant();

        // Simple connection validation query
        if (lower.StartsWith("select 1"))
        {
            return new JdbcQueryResult
            {
                Columns = new List<JdbcColumnMetadata>
                {
                    TypeConverter.CreateColumnMetadata("1", JdbcSqlTypes.INTEGER, 0)
                },
                Rows = new List<object?[]>
                {
                    new object?[] { 1 }
                }
            };
        }

        // Return empty result for other metadata queries
        return new JdbcQueryResult
        {
            Columns = new List<JdbcColumnMetadata>(),
            Rows = new List<object?[]>()
        };
    }

    /// <summary>
    /// Determines the command type (SELECT, INSERT, UPDATE, DELETE, etc.).
    /// </summary>
    /// <param name="sql">The SQL query.</param>
    /// <returns>The command type.</returns>
    public static string GetCommandType(string sql)
    {
        var trimmed = sql.TrimStart();
        var firstWord = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.ToUpperInvariant() ?? "";

        return firstWord switch
        {
            "SELECT" => "SELECT",
            "INSERT" => "INSERT",
            "UPDATE" => "UPDATE",
            "DELETE" => "DELETE",
            "CREATE" => "DDL",
            "ALTER" => "DDL",
            "DROP" => "DDL",
            "TRUNCATE" => "DDL",
            "BEGIN" => "TRANSACTION",
            "COMMIT" => "TRANSACTION",
            "ROLLBACK" => "TRANSACTION",
            "SAVEPOINT" => "TRANSACTION",
            "SET" => "SET",
            "SHOW" => "SHOW",
            "EXPLAIN" => "EXPLAIN",
            "CALL" => "CALL",
            _ => "OTHER"
        };
    }

    /// <summary>
    /// Checks if a SQL statement is a query (returns results).
    /// </summary>
    /// <param name="sql">The SQL query.</param>
    /// <returns>True if the statement is a query.</returns>
    public static bool IsQuery(string sql)
    {
        var commandType = GetCommandType(sql);
        return commandType is "SELECT" or "SHOW" or "EXPLAIN";
    }
}
