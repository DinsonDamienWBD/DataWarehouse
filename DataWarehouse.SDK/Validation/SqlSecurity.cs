using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace DataWarehouse.SDK.Validation;

// ============================================================================
// SQL SECURITY
// Comprehensive SQL injection protection, query sanitization, and security
// Enforces parameterized queries and blocks dangerous SQL patterns
// ============================================================================

#region SQL Security Analyzer

/// <summary>
/// SQL security analyzer that detects and prevents SQL injection attacks.
/// Implements defense-in-depth with multiple detection layers.
/// </summary>
public sealed class SqlSecurityAnalyzer
{
    private static readonly Regex SqlInjectionPatterns = new(
        @"(?i)" +
        @"(\b(SELECT|INSERT|UPDATE|DELETE|DROP|TRUNCATE|ALTER|CREATE|EXEC|EXECUTE|UNION|MERGE)\b.*\b(FROM|INTO|TABLE|DATABASE|SCHEMA|PROCEDURE|FUNCTION)\b)|" +
        @"(--|#|/\*|\*/)|" +                                           // SQL comments
        @"('.*'.*=.*')|" +                                              // Quote-based injection
        @"(\bOR\b\s+\d+\s*=\s*\d+)|" +                                   // OR 1=1 pattern
        @"(\bAND\b\s+\d+\s*=\s*\d+)|" +                                  // AND 1=1 pattern
        @"(\bOR\b\s+'[^']*'\s*=\s*'[^']*')|" +                           // OR 'a'='a' pattern
        @"(\bWAITFOR\b\s+\bDELAY\b)|" +                                  // Time-based injection
        @"(\bBENCHMARK\b\s*\()|" +                                       // MySQL benchmark
        @"(\bSLEEP\b\s*\()|" +                                           // Sleep injection
        @"(\bLOAD_FILE\b\s*\()|" +                                       // File access
        @"(\bINTO\s+OUTFILE\b)|" +                                       // File write
        @"(\bINTO\s+DUMPFILE\b)|" +                                      // Binary file write
        @"(\bINFORMATION_SCHEMA\b)|" +                                   // Schema enumeration
        @"(\bsys\.\b)|" +                                                // System tables
        @"(\bxp_\w+)|" +                                                 // SQL Server extended procs
        @"(\bsp_\w+)|" +                                                 // SQL Server system procs
        @"(;\s*(SELECT|INSERT|UPDATE|DELETE|DROP|EXEC))|" +              // Stacked queries
        @"(\bCHAR\b\s*\(\s*\d+\s*\))|" +                                  // CHAR() obfuscation
        @"(\bCONCAT\b\s*\()|" +                                          // String concat
        @"(\b0x[0-9a-fA-F]+\b)",                                         // Hex encoding
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DangerousKeywords = new(
        @"(?i)\b(GRANT|REVOKE|BACKUP|RESTORE|SHUTDOWN|BULK\s+INSERT|OPENROWSET|OPENDATASOURCE|XP_CMDSHELL)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CommentStripping = new(
        @"(--[^\r\n]*)|(/\*[\s\S]*?\*/)",
        RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, SqlAnalysisResult> _analysisCache = new();
    private readonly int _cacheMaxSize;

    public SqlSecurityAnalyzer(int cacheMaxSize = 10000)
    {
        _cacheMaxSize = cacheMaxSize;
    }

    /// <summary>
    /// Analyzes a SQL query for potential injection attacks.
    /// </summary>
    public SqlAnalysisResult Analyze(string query, SqlAnalysisOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new SqlAnalysisResult { IsValid = true, Query = query ?? string.Empty };
        }

        options ??= SqlAnalysisOptions.Default;

        // Check cache
        var cacheKey = $"{query}_{options.GetHashCode()}";
        if (_analysisCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var result = PerformAnalysis(query, options);

        // Cache result (with size limit)
        if (_analysisCache.Count < _cacheMaxSize)
        {
            _analysisCache.TryAdd(cacheKey, result);
        }

        return result;
    }

    private SqlAnalysisResult PerformAnalysis(string query, SqlAnalysisOptions options)
    {
        var result = new SqlAnalysisResult
        {
            Query = query,
            AnalyzedAt = DateTime.UtcNow
        };

        var threats = new List<SqlThreat>();
        var warnings = new List<string>();

        // Layer 1: Check for SQL injection patterns
        var matches = SqlInjectionPatterns.Matches(query);
        foreach (Match match in matches)
        {
            threats.Add(new SqlThreat
            {
                Type = SqlThreatType.InjectionPattern,
                Severity = ThreatSeverity.Critical,
                Description = $"SQL injection pattern detected: '{match.Value}'",
                Position = match.Index,
                Length = match.Length
            });
        }

        // Layer 2: Check for dangerous keywords
        if (options.BlockDangerousKeywords)
        {
            var dangerousMatches = DangerousKeywords.Matches(query);
            foreach (Match match in dangerousMatches)
            {
                threats.Add(new SqlThreat
                {
                    Type = SqlThreatType.DangerousKeyword,
                    Severity = ThreatSeverity.High,
                    Description = $"Dangerous SQL keyword: '{match.Value}'",
                    Position = match.Index,
                    Length = match.Length
                });
            }
        }

        // Layer 3: Check for stacked queries
        if (options.BlockStackedQueries && ContainsStackedQueries(query))
        {
            threats.Add(new SqlThreat
            {
                Type = SqlThreatType.StackedQuery,
                Severity = ThreatSeverity.High,
                Description = "Multiple SQL statements detected (stacked queries)"
            });
        }

        // Layer 4: Check for comment-based evasion
        if (options.DetectCommentEvasion && ContainsCommentEvasion(query))
        {
            threats.Add(new SqlThreat
            {
                Type = SqlThreatType.CommentEvasion,
                Severity = ThreatSeverity.Medium,
                Description = "SQL comment detected (potential evasion technique)"
            });
        }

        // Layer 5: Check for encoding attacks
        if (options.DetectEncodingAttacks)
        {
            var encodingThreats = DetectEncodingAttacks(query);
            threats.AddRange(encodingThreats);
        }

        // Layer 6: Check for UNION-based injection
        if (ContainsUnionInjection(query))
        {
            threats.Add(new SqlThreat
            {
                Type = SqlThreatType.UnionInjection,
                Severity = ThreatSeverity.Critical,
                Description = "UNION-based SQL injection pattern detected"
            });
        }

        // Layer 7: Check query length
        if (options.MaxQueryLength > 0 && query.Length > options.MaxQueryLength)
        {
            warnings.Add($"Query exceeds maximum length ({query.Length} > {options.MaxQueryLength})");
        }

        result.Threats = threats;
        result.Warnings = warnings;
        result.IsValid = threats.Count == 0 || threats.All(t => t.Severity < ThreatSeverity.High);
        result.ThreatLevel = threats.Count > 0 ? threats.Max(t => t.Severity) : ThreatSeverity.None;

        return result;
    }

    private bool ContainsStackedQueries(string query)
    {
        // Remove string literals and comments first
        var cleaned = RemoveStringLiterals(CommentStripping.Replace(query, " "));
        return cleaned.Contains(';') && cleaned.IndexOf(';') < cleaned.Length - 1;
    }

    private bool ContainsCommentEvasion(string query)
    {
        return query.Contains("--") || query.Contains("/*") || query.Contains("#");
    }

    private List<SqlThreat> DetectEncodingAttacks(string query)
    {
        var threats = new List<SqlThreat>();

        // Check for URL encoding
        if (Regex.IsMatch(query, @"%[0-9a-fA-F]{2}", RegexOptions.None, TimeSpan.FromMilliseconds(100)))
        {
            threats.Add(new SqlThreat
            {
                Type = SqlThreatType.EncodingAttack,
                Severity = ThreatSeverity.Medium,
                Description = "URL-encoded characters detected"
            });
        }

        // Check for Unicode encoding
        if (Regex.IsMatch(query, @"\\u[0-9a-fA-F]{4}", RegexOptions.None, TimeSpan.FromMilliseconds(100)))
        {
            threats.Add(new SqlThreat
            {
                Type = SqlThreatType.EncodingAttack,
                Severity = ThreatSeverity.Medium,
                Description = "Unicode-encoded characters detected"
            });
        }

        // Check for hex encoding
        if (Regex.IsMatch(query, @"0x[0-9a-fA-F]{2,}", RegexOptions.None, TimeSpan.FromMilliseconds(100)))
        {
            threats.Add(new SqlThreat
            {
                Type = SqlThreatType.EncodingAttack,
                Severity = ThreatSeverity.Medium,
                Description = "Hexadecimal encoding detected"
            });
        }

        return threats;
    }

    private bool ContainsUnionInjection(string query)
    {
        return Regex.IsMatch(query, @"(?i)\bUNION\b\s+(ALL\s+)?\bSELECT\b", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    }

    private string RemoveStringLiterals(string query)
    {
        return Regex.Replace(query, @"'[^']*'", " ", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// Clears the analysis cache.
    /// </summary>
    public void ClearCache()
    {
        _analysisCache.Clear();
    }
}

#endregion

#region SQL Sanitizer

/// <summary>
/// SQL input sanitizer that cleans user input for safe SQL usage.
/// Should be used in addition to (not instead of) parameterized queries.
/// </summary>
public static class SqlSanitizer
{
    /// <summary>
    /// Sanitizes a string value for use in SQL.
    /// Note: Always prefer parameterized queries over sanitization.
    /// </summary>
    public static string SanitizeString(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // Escape single quotes by doubling them
        var sanitized = input.Replace("'", "''");

        // Remove null bytes
        sanitized = sanitized.Replace("\0", "");

        // Escape backslashes (for MySQL)
        sanitized = sanitized.Replace("\\", "\\\\");

        return sanitized;
    }

    /// <summary>
    /// Sanitizes an identifier (table/column name) for use in SQL.
    /// </summary>
    public static string SanitizeIdentifier(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            throw new ArgumentException("Identifier cannot be null or empty", nameof(identifier));
        }

        // Only allow alphanumeric and underscore
        if (!Regex.IsMatch(identifier, @"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.None, TimeSpan.FromMilliseconds(100)))
        {
            throw new ArgumentException($"Invalid identifier: '{identifier}'", nameof(identifier));
        }

        // Check against reserved words
        if (IsReservedWord(identifier))
        {
            throw new ArgumentException($"Reserved word cannot be used as identifier: '{identifier}'", nameof(identifier));
        }

        return identifier;
    }

    /// <summary>
    /// Sanitizes a numeric value.
    /// </summary>
    public static string SanitizeNumeric(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "0";
        }

        // Remove all non-numeric characters except decimal point and minus
        var sanitized = Regex.Replace(input, @"[^0-9.\-]", "", RegexOptions.None, TimeSpan.FromMilliseconds(100));

        // Validate it's a valid number
        if (!double.TryParse(sanitized, out _))
        {
            throw new ArgumentException($"Invalid numeric value: '{input}'", nameof(input));
        }

        return sanitized;
    }

    /// <summary>
    /// Validates and sanitizes a LIKE pattern.
    /// </summary>
    public static string SanitizeLikePattern(string? pattern, char escapeChar = '\\')
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return "%";
        }

        var sb = new StringBuilder();
        foreach (var c in pattern)
        {
            switch (c)
            {
                case '%':
                case '_':
                case '[':
                case ']':
                    sb.Append(escapeChar);
                    sb.Append(c);
                    break;
                case '\'':
                    sb.Append("''");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Checks if a word is a SQL reserved word.
    /// </summary>
    public static bool IsReservedWord(string word)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "INSERT", "UPDATE", "DELETE", "DROP", "CREATE", "ALTER", "TRUNCATE",
            "TABLE", "DATABASE", "INDEX", "VIEW", "PROCEDURE", "FUNCTION", "TRIGGER",
            "FROM", "WHERE", "AND", "OR", "NOT", "IN", "EXISTS", "BETWEEN", "LIKE",
            "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS", "ON",
            "GROUP", "BY", "HAVING", "ORDER", "ASC", "DESC", "LIMIT", "OFFSET",
            "UNION", "INTERSECT", "EXCEPT", "ALL", "DISTINCT", "AS", "NULL",
            "TRUE", "FALSE", "IS", "CASE", "WHEN", "THEN", "ELSE", "END",
            "GRANT", "REVOKE", "COMMIT", "ROLLBACK", "SAVEPOINT", "TRANSACTION",
            "PRIMARY", "KEY", "FOREIGN", "REFERENCES", "UNIQUE", "CHECK", "DEFAULT",
            "CONSTRAINT", "CASCADE", "SET", "VALUES", "INTO"
        };

        return reserved.Contains(word);
    }
}

#endregion

#region Parameterized Query Builder

/// <summary>
/// Safe SQL query builder that enforces parameterized queries.
/// Prevents SQL injection by construction.
/// </summary>
public sealed class SafeQueryBuilder
{
    private readonly StringBuilder _query = new();
    private readonly Dictionary<string, object?> _parameters = new();
    private int _parameterIndex;

    /// <summary>
    /// Gets the built query string.
    /// </summary>
    public string Query => _query.ToString();

    /// <summary>
    /// Gets the query parameters.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Parameters => _parameters;

    /// <summary>
    /// Appends raw SQL text. Use sparingly and only with trusted content.
    /// </summary>
    public SafeQueryBuilder AppendSql(string sql)
    {
        _query.Append(sql);
        return this;
    }

    /// <summary>
    /// Appends a safe identifier (table/column name).
    /// </summary>
    public SafeQueryBuilder AppendIdentifier(string identifier, char quoteChar = '"')
    {
        var sanitized = SqlSanitizer.SanitizeIdentifier(identifier);
        _query.Append(quoteChar).Append(sanitized).Append(quoteChar);
        return this;
    }

    /// <summary>
    /// Appends a parameterized value.
    /// </summary>
    public SafeQueryBuilder AppendParameter(object? value, string? parameterName = null)
    {
        var name = parameterName ?? $"@p{_parameterIndex++}";
        if (!name.StartsWith('@'))
        {
            name = "@" + name;
        }

        _query.Append(name);
        _parameters[name] = value;
        return this;
    }

    /// <summary>
    /// Appends a list of parameterized values for IN clause.
    /// </summary>
    public SafeQueryBuilder AppendInClause<T>(IEnumerable<T> values)
    {
        _query.Append('(');
        var first = true;
        foreach (var value in values)
        {
            if (!first) _query.Append(", ");
            first = false;
            AppendParameter(value);
        }
        _query.Append(')');
        return this;
    }

    /// <summary>
    /// Creates a SELECT query.
    /// </summary>
    public static SafeQueryBuilder Select(params string[] columns)
    {
        var builder = new SafeQueryBuilder();
        builder.AppendSql("SELECT ");

        if (columns.Length == 0)
        {
            builder.AppendSql("*");
        }
        else
        {
            for (int i = 0; i < columns.Length; i++)
            {
                if (i > 0) builder.AppendSql(", ");
                builder.AppendIdentifier(columns[i]);
            }
        }

        return builder;
    }

    /// <summary>
    /// Adds FROM clause.
    /// </summary>
    public SafeQueryBuilder From(string table)
    {
        AppendSql(" FROM ");
        AppendIdentifier(table);
        return this;
    }

    /// <summary>
    /// Adds WHERE clause.
    /// </summary>
    public SafeQueryBuilder Where(string column, string op, object? value)
    {
        AppendSql(" WHERE ");
        AppendIdentifier(column);
        AppendSql($" {ValidateOperator(op)} ");
        AppendParameter(value);
        return this;
    }

    /// <summary>
    /// Adds AND condition.
    /// </summary>
    public SafeQueryBuilder And(string column, string op, object? value)
    {
        AppendSql(" AND ");
        AppendIdentifier(column);
        AppendSql($" {ValidateOperator(op)} ");
        AppendParameter(value);
        return this;
    }

    /// <summary>
    /// Adds OR condition.
    /// </summary>
    public SafeQueryBuilder Or(string column, string op, object? value)
    {
        AppendSql(" OR ");
        AppendIdentifier(column);
        AppendSql($" {ValidateOperator(op)} ");
        AppendParameter(value);
        return this;
    }

    /// <summary>
    /// Adds ORDER BY clause.
    /// </summary>
    public SafeQueryBuilder OrderBy(string column, bool descending = false)
    {
        AppendSql(" ORDER BY ");
        AppendIdentifier(column);
        if (descending) AppendSql(" DESC");
        return this;
    }

    /// <summary>
    /// Adds LIMIT clause.
    /// </summary>
    public SafeQueryBuilder Limit(int count)
    {
        AppendSql(" LIMIT ");
        AppendParameter(count);
        return this;
    }

    /// <summary>
    /// Adds OFFSET clause.
    /// </summary>
    public SafeQueryBuilder Offset(int offset)
    {
        AppendSql(" OFFSET ");
        AppendParameter(offset);
        return this;
    }

    private static string ValidateOperator(string op)
    {
        var allowed = new HashSet<string> { "=", "!=", "<>", "<", ">", "<=", ">=", "LIKE", "IN", "IS", "IS NOT" };
        var normalized = op.Trim().ToUpperInvariant();
        if (!allowed.Contains(normalized))
        {
            throw new ArgumentException($"Invalid SQL operator: '{op}'", nameof(op));
        }
        return normalized;
    }

    /// <summary>
    /// Resets the builder for reuse.
    /// </summary>
    public void Clear()
    {
        _query.Clear();
        _parameters.Clear();
        _parameterIndex = 0;
    }
}

#endregion

#region Result Types

/// <summary>
/// Result of SQL security analysis.
/// </summary>
public sealed class SqlAnalysisResult
{
    public required string Query { get; init; }
    public bool IsValid { get; set; }
    public ThreatSeverity ThreatLevel { get; set; }
    public List<SqlThreat> Threats { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public DateTime AnalyzedAt { get; set; }
}

/// <summary>
/// Represents a detected SQL threat.
/// </summary>
public sealed class SqlThreat
{
    public SqlThreatType Type { get; init; }
    public ThreatSeverity Severity { get; init; }
    public required string Description { get; init; }
    public int Position { get; init; }
    public int Length { get; init; }
}

/// <summary>
/// Types of SQL security threats.
/// </summary>
public enum SqlThreatType
{
    InjectionPattern,
    DangerousKeyword,
    StackedQuery,
    CommentEvasion,
    EncodingAttack,
    UnionInjection,
    TimeBasedInjection,
    ErrorBasedInjection,
    BlindInjection
}

/// <summary>
/// Severity levels for threats.
/// </summary>
public enum ThreatSeverity
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

/// <summary>
/// Options for SQL analysis.
/// </summary>
public sealed class SqlAnalysisOptions
{
    public bool BlockDangerousKeywords { get; init; } = true;
    public bool BlockStackedQueries { get; init; } = true;
    public bool DetectCommentEvasion { get; init; } = true;
    public bool DetectEncodingAttacks { get; init; } = true;
    public int MaxQueryLength { get; init; } = 100000;

    public static SqlAnalysisOptions Default => new();

    public static SqlAnalysisOptions Strict => new()
    {
        BlockDangerousKeywords = true,
        BlockStackedQueries = true,
        DetectCommentEvasion = true,
        DetectEncodingAttacks = true,
        MaxQueryLength = 10000
    };

    public static SqlAnalysisOptions Permissive => new()
    {
        BlockDangerousKeywords = false,
        BlockStackedQueries = false,
        DetectCommentEvasion = false,
        DetectEncodingAttacks = false,
        MaxQueryLength = 0
    };
}

#endregion

#region SQL Audit Logger

/// <summary>
/// SQL audit logger for tracking all query executions.
/// </summary>
public sealed class SqlAuditLogger
{
    private readonly ConcurrentQueue<SqlAuditEntry> _auditLog = new();
    private readonly int _maxEntries;
    private long _totalQueries;
    private long _blockedQueries;

    public SqlAuditLogger(int maxEntries = 10000)
    {
        _maxEntries = maxEntries;
    }

    /// <summary>
    /// Logs a SQL query execution.
    /// </summary>
    public void LogQuery(SqlAuditEntry entry)
    {
        Interlocked.Increment(ref _totalQueries);
        if (entry.WasBlocked)
        {
            Interlocked.Increment(ref _blockedQueries);
        }

        _auditLog.Enqueue(entry);

        // Trim old entries
        while (_auditLog.Count > _maxEntries)
        {
            _auditLog.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Gets recent audit entries.
    /// </summary>
    public IEnumerable<SqlAuditEntry> GetRecentEntries(int count = 100)
    {
        return _auditLog.TakeLast(count);
    }

    /// <summary>
    /// Gets blocked queries.
    /// </summary>
    public IEnumerable<SqlAuditEntry> GetBlockedQueries(int count = 100)
    {
        return _auditLog.Where(e => e.WasBlocked).TakeLast(count);
    }

    /// <summary>
    /// Gets audit statistics.
    /// </summary>
    public SqlAuditStats GetStats()
    {
        return new SqlAuditStats
        {
            TotalQueries = _totalQueries,
            BlockedQueries = _blockedQueries,
            BlockRate = _totalQueries > 0 ? (double)_blockedQueries / _totalQueries * 100 : 0
        };
    }
}

/// <summary>
/// SQL audit entry.
/// </summary>
public sealed class SqlAuditEntry
{
    public string? QueryHash { get; init; }
    public string? UserId { get; init; }
    public string? ClientIp { get; init; }
    public DateTime Timestamp { get; init; }
    public TimeSpan Duration { get; init; }
    public bool WasBlocked { get; init; }
    public string? BlockReason { get; init; }
    public List<SqlThreat> DetectedThreats { get; init; } = new();
}

/// <summary>
/// SQL audit statistics.
/// </summary>
public sealed class SqlAuditStats
{
    public long TotalQueries { get; init; }
    public long BlockedQueries { get; init; }
    public double BlockRate { get; init; }
}

#endregion
