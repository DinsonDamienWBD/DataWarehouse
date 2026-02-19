namespace DataWarehouse.SDK.Contracts.Query;

/// <summary>
/// Parses SQL text into typed AST nodes. Produces SqlStatement trees
/// that downstream query planners, cost-based optimizers, and federated
/// query engines consume.
/// </summary>
public interface ISqlParser
{
    /// <summary>
    /// Parses SQL text into a typed SqlStatement AST.
    /// </summary>
    /// <param name="sql">The SQL text to parse.</param>
    /// <returns>A fully typed SqlStatement AST.</returns>
    /// <exception cref="SqlParseException">Thrown when the SQL is syntactically invalid.</exception>
    SqlStatement Parse(string sql);

    /// <summary>
    /// Attempts to parse SQL text without throwing on failure.
    /// </summary>
    /// <param name="sql">The SQL text to parse.</param>
    /// <param name="result">The parsed statement, or null on failure.</param>
    /// <param name="error">The error message, or null on success.</param>
    /// <returns>True if parsing succeeded.</returns>
    bool TryParse(string sql, out SqlStatement? result, out string? error);
}

/// <summary>
/// Exception thrown when SQL parsing fails, with line/column context.
/// </summary>
public class SqlParseException : Exception
{
    public int Line { get; }
    public int Column { get; }
    public string? Token { get; }

    public SqlParseException(string message, int line, int column, string? token = null)
        : base($"{message} at line {line}, column {column}{(token != null ? $" near '{token}'" : "")}")
    {
        Line = line;
        Column = column;
        Token = token;
    }

    public SqlParseException(string message, int line, int column, string? token, Exception innerException)
        : base($"{message} at line {line}, column {column}{(token != null ? $" near '{token}'" : "")}", innerException)
    {
        Line = line;
        Column = column;
        Token = token;
    }
}
