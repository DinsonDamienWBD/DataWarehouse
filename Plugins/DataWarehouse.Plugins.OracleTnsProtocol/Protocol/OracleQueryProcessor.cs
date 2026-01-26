using System.Text;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.OracleTnsProtocol.Protocol;

/// <summary>
/// Processes SQL queries and handles Oracle-specific query translation and execution.
/// Provides compatibility layer for Oracle SQL dialect and system queries.
/// </summary>
/// <remarks>
/// This processor handles:
/// - Oracle system catalog queries (v$, dba_, user_, all_ views)
/// - Oracle-specific SQL syntax (DUAL table, ROWNUM, etc.)
/// - Bind variable substitution
/// - Statement type detection
///
/// Thread-safety: The ExecuteQueryAsync method is thread-safe. Instance methods
/// that modify state should be called from a single thread (per connection).
/// </remarks>
public sealed class OracleQueryProcessor
{
    private readonly Func<string, CancellationToken, Task<OracleQueryResult>> _sqlExecutor;

    /// <summary>
    /// Initializes a new instance of OracleQueryProcessor.
    /// </summary>
    /// <param name="sqlExecutor">The SQL execution delegate.</param>
    /// <exception cref="ArgumentNullException">Thrown when sqlExecutor is null.</exception>
    public OracleQueryProcessor(Func<string, CancellationToken, Task<OracleQueryResult>> sqlExecutor)
    {
        _sqlExecutor = sqlExecutor ?? throw new ArgumentNullException(nameof(sqlExecutor));
    }

    /// <summary>
    /// Executes a SQL query, handling Oracle-specific queries internally.
    /// </summary>
    /// <param name="sql">The SQL statement to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The query result.</returns>
    public async Task<OracleQueryResult> ExecuteQueryAsync(string sql, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return new OracleQueryResult
            {
                IsEmpty = true,
                StatementType = OracleStatementType.Unknown
            };
        }

        sql = sql.Trim().TrimEnd(';');

        // Detect statement type
        var statementType = DetectStatementType(sql);

        // Handle Oracle system/dictionary queries internally
        if (IsOracleSystemQuery(sql))
        {
            return ExecuteSystemQuery(sql, statementType);
        }

        // Handle special Oracle constructs
        sql = TranslateOracleSpecificSyntax(sql);

        // Execute through the provided executor
        var result = await _sqlExecutor(sql, ct).ConfigureAwait(false);

        // Return a new result with the detected statement type if it differs
        if (result.StatementType == OracleStatementType.Unknown && statementType != OracleStatementType.Unknown)
        {
            return new OracleQueryResult
            {
                IsEmpty = result.IsEmpty,
                Columns = result.Columns,
                Rows = result.Rows,
                RowsAffected = result.RowsAffected,
                StatementType = statementType,
                ErrorMessage = result.ErrorMessage,
                ErrorCode = result.ErrorCode,
                HasMoreRows = result.HasMoreRows
            };
        }

        return result;
    }

    /// <summary>
    /// Detects the type of SQL statement.
    /// </summary>
    /// <param name="sql">The SQL statement.</param>
    /// <returns>The detected statement type.</returns>
    public OracleStatementType DetectStatementType(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return OracleStatementType.Unknown;

        var trimmed = sql.TrimStart();
        var firstWord = trimmed.Split(new[] { ' ', '\t', '\n', '\r', '(' }, 2)[0].ToUpperInvariant();

        return firstWord switch
        {
            "SELECT" => OracleStatementType.Select,
            "INSERT" => OracleStatementType.Insert,
            "UPDATE" => OracleStatementType.Update,
            "DELETE" => OracleStatementType.Delete,
            "CREATE" => OracleStatementType.Create,
            "DROP" => OracleStatementType.Drop,
            "ALTER" => OracleStatementType.Alter,
            "MERGE" => OracleStatementType.Merge,
            "CALL" => OracleStatementType.Call,
            "BEGIN" or "DECLARE" => OracleStatementType.PlSqlBlock,
            "COMMIT" => OracleStatementType.Commit,
            "ROLLBACK" => OracleStatementType.Rollback,
            "EXPLAIN" => OracleStatementType.ExplainPlan,
            _ => OracleStatementType.Unknown
        };
    }

    /// <summary>
    /// Checks if the query is an Oracle system/dictionary query.
    /// </summary>
    /// <param name="sql">The SQL statement.</param>
    /// <returns>True if this is a system query that should be handled internally.</returns>
    public bool IsOracleSystemQuery(string sql)
    {
        var lower = sql.ToLowerInvariant();

        // V$ dynamic performance views
        if (lower.Contains("v$") || lower.Contains("v_$"))
            return true;

        // Data dictionary views
        if (lower.Contains("dba_") || lower.Contains("all_") || lower.Contains("user_"))
            return true;

        // System tables
        if (lower.Contains("dual") && !lower.Contains("join"))
            return true;

        // NLS and session info
        if (lower.Contains("nls_") || lower.Contains("sys_context"))
            return true;

        // DBMS packages
        if (lower.Contains("dbms_"))
            return true;

        // Version and banner
        if (lower.Contains("v$version") || lower.Contains("product_component_version"))
            return true;

        return false;
    }

    /// <summary>
    /// Executes Oracle system/dictionary queries internally.
    /// </summary>
    private OracleQueryResult ExecuteSystemQuery(string sql, OracleStatementType statementType)
    {
        var lower = sql.ToLowerInvariant();

        // SELECT 1 FROM DUAL (common connectivity test)
        if (lower.Contains("from dual") && !lower.Contains("sysdate") && !lower.Contains("user"))
        {
            var selectMatch = Regex.Match(sql, @"select\s+(.+?)\s+from\s+dual", RegexOptions.IgnoreCase);
            if (selectMatch.Success)
            {
                return CreateSingleValueResult(selectMatch.Groups[1].Value.Trim());
            }
        }

        // SYSDATE
        if (lower.Contains("sysdate"))
        {
            return CreateSingleValueResult("SYSDATE",
                Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("dd-MMM-yy HH.mm.ss.ffffff").ToUpperInvariant()));
        }

        // CURRENT_TIMESTAMP
        if (lower.Contains("current_timestamp") || lower.Contains("systimestamp"))
        {
            return CreateSingleValueResult("SYSTIMESTAMP",
                Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("dd-MMM-yy HH.mm.ss.ffffff zzz").ToUpperInvariant()));
        }

        // USER
        if (lower.Contains("select user") || (lower.Contains("user") && lower.Contains("from dual")))
        {
            return CreateSingleValueResult("USER", Encoding.UTF8.GetBytes("DATAWAREHOUSE"));
        }

        // SYS_CONTEXT
        if (lower.Contains("sys_context"))
        {
            return HandleSysContext(sql);
        }

        // V$VERSION
        if (lower.Contains("v$version"))
        {
            return CreateVersionResult();
        }

        // V$SESSION
        if (lower.Contains("v$session"))
        {
            return CreateSessionResult();
        }

        // V$DATABASE
        if (lower.Contains("v$database"))
        {
            return CreateDatabaseInfoResult();
        }

        // V$INSTANCE
        if (lower.Contains("v$instance"))
        {
            return CreateInstanceResult();
        }

        // V$NLS_PARAMETERS
        if (lower.Contains("v$nls_parameters") || lower.Contains("nls_session_parameters"))
        {
            return CreateNlsParametersResult();
        }

        // ALL_TABLES / USER_TABLES
        if (lower.Contains("all_tables") || lower.Contains("user_tables"))
        {
            return CreateTablesResult();
        }

        // ALL_TAB_COLUMNS / USER_TAB_COLUMNS
        if (lower.Contains("all_tab_columns") || lower.Contains("user_tab_columns"))
        {
            return CreateColumnsResult();
        }

        // ALL_OBJECTS / USER_OBJECTS
        if (lower.Contains("all_objects") || lower.Contains("user_objects"))
        {
            return CreateObjectsResult();
        }

        // DBA_USERS / ALL_USERS
        if (lower.Contains("dba_users") || lower.Contains("all_users"))
        {
            return CreateUsersResult();
        }

        // Default: return empty result
        return new OracleQueryResult
        {
            IsEmpty = false,
            Columns = new List<OracleColumnDescription>(),
            Rows = new List<List<byte[]?>>(),
            StatementType = statementType
        };
    }

    /// <summary>
    /// Handles SYS_CONTEXT function calls.
    /// </summary>
    private OracleQueryResult HandleSysContext(string sql)
    {
        var match = Regex.Match(sql, @"sys_context\s*\(\s*'(\w+)'\s*,\s*'(\w+)'\s*\)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var contextNamespace = match.Groups[1].Value.ToUpperInvariant();
            var parameter = match.Groups[2].Value.ToUpperInvariant();

            var value = (contextNamespace, parameter) switch
            {
                ("USERENV", "CURRENT_USER") => "DATAWAREHOUSE",
                ("USERENV", "SESSION_USER") => "DATAWAREHOUSE",
                ("USERENV", "CURRENT_SCHEMA") => "DATAWAREHOUSE",
                ("USERENV", "DB_NAME") => "DATAWAREHOUSE",
                ("USERENV", "INSTANCE_NAME") => "datawarehouse1",
                ("USERENV", "SERVER_HOST") => Environment.MachineName,
                ("USERENV", "SERVICE_NAME") => "DATAWAREHOUSE",
                ("USERENV", "SID") => "1",
                ("USERENV", "LANGUAGE") => "AMERICAN_AMERICA.AL32UTF8",
                ("USERENV", "NLS_DATE_FORMAT") => "DD-MON-RR",
                ("USERENV", "NLS_CALENDAR") => "GREGORIAN",
                ("USERENV", "OS_USER") => Environment.UserName,
                ("USERENV", "HOST") => Environment.MachineName,
                ("USERENV", "IP_ADDRESS") => "127.0.0.1",
                ("USERENV", "TERMINAL") => "unknown",
                _ => string.Empty
            };

            return CreateSingleValueResult(parameter, Encoding.UTF8.GetBytes(value));
        }

        return CreateSingleValueResult("SYS_CONTEXT", Encoding.UTF8.GetBytes(string.Empty));
    }

    /// <summary>
    /// Creates a result with a single value.
    /// </summary>
    private OracleQueryResult CreateSingleValueResult(string expression, byte[]? value = null)
    {
        // Try to evaluate simple expressions
        if (value == null)
        {
            var trimmed = expression.Trim();

            // Numeric literal
            if (decimal.TryParse(trimmed, out var numVal))
            {
                value = Encoding.ASCII.GetBytes(numVal.ToString());
            }
            // String literal
            else if (trimmed.StartsWith("'") && trimmed.EndsWith("'"))
            {
                value = Encoding.UTF8.GetBytes(trimmed.Trim('\''));
            }
            else
            {
                value = Encoding.UTF8.GetBytes(trimmed);
            }
        }

        return new OracleQueryResult
        {
            IsEmpty = false,
            Columns = new List<OracleColumnDescription>
            {
                new()
                {
                    Name = expression.ToUpperInvariant(),
                    TypeOid = OracleTypeOid.Varchar2,
                    DisplaySize = 4000,
                    Position = 1
                }
            },
            Rows = new List<List<byte[]?>>
            {
                new() { value }
            },
            StatementType = OracleStatementType.Select
        };
    }

    /// <summary>
    /// Creates V$VERSION result.
    /// </summary>
    private OracleQueryResult CreateVersionResult()
    {
        return new OracleQueryResult
        {
            IsEmpty = false,
            Columns = new List<OracleColumnDescription>
            {
                new() { Name = "BANNER", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 80, Position = 1 },
                new() { Name = "BANNER_FULL", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 160, Position = 2 },
                new() { Name = "BANNER_LEGACY", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 80, Position = 3 },
                new() { Name = "CON_ID", TypeOid = OracleTypeOid.Number, DisplaySize = 10, Position = 4 }
            },
            Rows = new List<List<byte[]?>>
            {
                new()
                {
                    Encoding.UTF8.GetBytes("Oracle Database 19c Enterprise Edition Release 19.0.0.0.0 - Production"),
                    Encoding.UTF8.GetBytes("Oracle Database 19c Enterprise Edition Release 19.0.0.0.0 - Production\nVersion 19.19.0.0.0 (DataWarehouse Compatible)"),
                    Encoding.UTF8.GetBytes("Oracle Database 19c Enterprise Edition Release 19.0.0.0.0 - Production"),
                    Encoding.UTF8.GetBytes("0")
                }
            },
            StatementType = OracleStatementType.Select
        };
    }

    /// <summary>
    /// Creates V$SESSION result.
    /// </summary>
    private OracleQueryResult CreateSessionResult()
    {
        return new OracleQueryResult
        {
            IsEmpty = false,
            Columns = new List<OracleColumnDescription>
            {
                new() { Name = "SID", TypeOid = OracleTypeOid.Number, DisplaySize = 10, Position = 1 },
                new() { Name = "SERIAL#", TypeOid = OracleTypeOid.Number, DisplaySize = 10, Position = 2 },
                new() { Name = "USERNAME", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 128, Position = 3 },
                new() { Name = "STATUS", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 10, Position = 4 },
                new() { Name = "SCHEMANAME", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 128, Position = 5 },
                new() { Name = "OSUSER", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 128, Position = 6 },
                new() { Name = "MACHINE", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 64, Position = 7 },
                new() { Name = "PROGRAM", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 64, Position = 8 }
            },
            Rows = new List<List<byte[]?>>
            {
                new()
                {
                    Encoding.UTF8.GetBytes("1"),
                    Encoding.UTF8.GetBytes("1"),
                    Encoding.UTF8.GetBytes("DATAWAREHOUSE"),
                    Encoding.UTF8.GetBytes("ACTIVE"),
                    Encoding.UTF8.GetBytes("DATAWAREHOUSE"),
                    Encoding.UTF8.GetBytes(Environment.UserName),
                    Encoding.UTF8.GetBytes(Environment.MachineName),
                    Encoding.UTF8.GetBytes("DataWarehouse Client")
                }
            },
            StatementType = OracleStatementType.Select
        };
    }

    /// <summary>
    /// Creates V$DATABASE result.
    /// </summary>
    private OracleQueryResult CreateDatabaseInfoResult()
    {
        return new OracleQueryResult
        {
            IsEmpty = false,
            Columns = new List<OracleColumnDescription>
            {
                new() { Name = "DBID", TypeOid = OracleTypeOid.Number, DisplaySize = 10, Position = 1 },
                new() { Name = "NAME", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 9, Position = 2 },
                new() { Name = "CREATED", TypeOid = OracleTypeOid.Date, DisplaySize = 19, Position = 3 },
                new() { Name = "LOG_MODE", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 12, Position = 4 },
                new() { Name = "OPEN_MODE", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 20, Position = 5 },
                new() { Name = "DATABASE_ROLE", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 16, Position = 6 }
            },
            Rows = new List<List<byte[]?>>
            {
                new()
                {
                    Encoding.UTF8.GetBytes("1234567890"),
                    Encoding.UTF8.GetBytes("DWDB"),
                    Encoding.UTF8.GetBytes(DateTime.UtcNow.AddYears(-1).ToString("dd-MMM-yy").ToUpperInvariant()),
                    Encoding.UTF8.GetBytes("ARCHIVELOG"),
                    Encoding.UTF8.GetBytes("READ WRITE"),
                    Encoding.UTF8.GetBytes("PRIMARY")
                }
            },
            StatementType = OracleStatementType.Select
        };
    }

    /// <summary>
    /// Creates V$INSTANCE result.
    /// </summary>
    private OracleQueryResult CreateInstanceResult()
    {
        return new OracleQueryResult
        {
            IsEmpty = false,
            Columns = new List<OracleColumnDescription>
            {
                new() { Name = "INSTANCE_NUMBER", TypeOid = OracleTypeOid.Number, DisplaySize = 10, Position = 1 },
                new() { Name = "INSTANCE_NAME", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 16, Position = 2 },
                new() { Name = "HOST_NAME", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 64, Position = 3 },
                new() { Name = "VERSION", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 17, Position = 4 },
                new() { Name = "STARTUP_TIME", TypeOid = OracleTypeOid.Date, DisplaySize = 19, Position = 5 },
                new() { Name = "STATUS", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 12, Position = 6 }
            },
            Rows = new List<List<byte[]?>>
            {
                new()
                {
                    Encoding.UTF8.GetBytes("1"),
                    Encoding.UTF8.GetBytes("datawarehouse1"),
                    Encoding.UTF8.GetBytes(Environment.MachineName),
                    Encoding.UTF8.GetBytes("19.0.0.0.0"),
                    Encoding.UTF8.GetBytes(DateTime.UtcNow.AddHours(-1).ToString("dd-MMM-yy").ToUpperInvariant()),
                    Encoding.UTF8.GetBytes("OPEN")
                }
            },
            StatementType = OracleStatementType.Select
        };
    }

    /// <summary>
    /// Creates NLS parameters result.
    /// </summary>
    private OracleQueryResult CreateNlsParametersResult()
    {
        var nlsParams = new Dictionary<string, string>
        {
            ["NLS_LANGUAGE"] = "AMERICAN",
            ["NLS_TERRITORY"] = "AMERICA",
            ["NLS_CURRENCY"] = "$",
            ["NLS_ISO_CURRENCY"] = "AMERICA",
            ["NLS_NUMERIC_CHARACTERS"] = ".,",
            ["NLS_CALENDAR"] = "GREGORIAN",
            ["NLS_DATE_FORMAT"] = "DD-MON-RR",
            ["NLS_DATE_LANGUAGE"] = "AMERICAN",
            ["NLS_CHARACTERSET"] = "AL32UTF8",
            ["NLS_SORT"] = "BINARY",
            ["NLS_COMP"] = "BINARY",
            ["NLS_TIMESTAMP_FORMAT"] = "DD-MON-RR HH.MI.SSXFF AM",
            ["NLS_TIME_FORMAT"] = "HH.MI.SSXFF AM",
            ["NLS_LENGTH_SEMANTICS"] = "BYTE",
            ["NLS_NCHAR_CHARACTERSET"] = "AL16UTF16"
        };

        return new OracleQueryResult
        {
            IsEmpty = false,
            Columns = new List<OracleColumnDescription>
            {
                new() { Name = "PARAMETER", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 64, Position = 1 },
                new() { Name = "VALUE", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 64, Position = 2 }
            },
            Rows = nlsParams.Select(kv => new List<byte[]?>
            {
                Encoding.UTF8.GetBytes(kv.Key),
                Encoding.UTF8.GetBytes(kv.Value)
            }).ToList(),
            StatementType = OracleStatementType.Select
        };
    }

    /// <summary>
    /// Creates ALL_TABLES / USER_TABLES result.
    /// </summary>
    private OracleQueryResult CreateTablesResult()
    {
        return new OracleQueryResult
        {
            IsEmpty = false,
            Columns = new List<OracleColumnDescription>
            {
                new() { Name = "OWNER", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 128, Position = 1 },
                new() { Name = "TABLE_NAME", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 128, Position = 2 },
                new() { Name = "TABLESPACE_NAME", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 30, Position = 3 },
                new() { Name = "STATUS", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 8, Position = 4 },
                new() { Name = "NUM_ROWS", TypeOid = OracleTypeOid.Number, DisplaySize = 10, Position = 5 }
            },
            Rows = new List<List<byte[]?>>
            {
                new() { Encoding.UTF8.GetBytes("DATAWAREHOUSE"), Encoding.UTF8.GetBytes("MANIFESTS"), Encoding.UTF8.GetBytes("USERS"), Encoding.UTF8.GetBytes("VALID"), Encoding.UTF8.GetBytes("0") },
                new() { Encoding.UTF8.GetBytes("DATAWAREHOUSE"), Encoding.UTF8.GetBytes("BLOBS"), Encoding.UTF8.GetBytes("USERS"), Encoding.UTF8.GetBytes("VALID"), Encoding.UTF8.GetBytes("0") }
            },
            StatementType = OracleStatementType.Select
        };
    }

    /// <summary>
    /// Creates ALL_TAB_COLUMNS / USER_TAB_COLUMNS result.
    /// </summary>
    private OracleQueryResult CreateColumnsResult()
    {
        return new OracleQueryResult
        {
            IsEmpty = false,
            Columns = new List<OracleColumnDescription>
            {
                new() { Name = "OWNER", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 128, Position = 1 },
                new() { Name = "TABLE_NAME", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 128, Position = 2 },
                new() { Name = "COLUMN_NAME", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 128, Position = 3 },
                new() { Name = "DATA_TYPE", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 128, Position = 4 },
                new() { Name = "DATA_LENGTH", TypeOid = OracleTypeOid.Number, DisplaySize = 10, Position = 5 },
                new() { Name = "NULLABLE", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 1, Position = 6 }
            },
            Rows = new List<List<byte[]?>>
            {
                new() { Encoding.UTF8.GetBytes("DATAWAREHOUSE"), Encoding.UTF8.GetBytes("MANIFESTS"), Encoding.UTF8.GetBytes("ID"), Encoding.UTF8.GetBytes("VARCHAR2"), Encoding.UTF8.GetBytes("36"), Encoding.UTF8.GetBytes("N") },
                new() { Encoding.UTF8.GetBytes("DATAWAREHOUSE"), Encoding.UTF8.GetBytes("MANIFESTS"), Encoding.UTF8.GetBytes("NAME"), Encoding.UTF8.GetBytes("VARCHAR2"), Encoding.UTF8.GetBytes("256"), Encoding.UTF8.GetBytes("Y") }
            },
            StatementType = OracleStatementType.Select
        };
    }

    /// <summary>
    /// Creates ALL_OBJECTS / USER_OBJECTS result.
    /// </summary>
    private OracleQueryResult CreateObjectsResult()
    {
        return new OracleQueryResult
        {
            IsEmpty = false,
            Columns = new List<OracleColumnDescription>
            {
                new() { Name = "OWNER", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 128, Position = 1 },
                new() { Name = "OBJECT_NAME", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 128, Position = 2 },
                new() { Name = "OBJECT_TYPE", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 23, Position = 3 },
                new() { Name = "STATUS", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 7, Position = 4 }
            },
            Rows = new List<List<byte[]?>>
            {
                new() { Encoding.UTF8.GetBytes("DATAWAREHOUSE"), Encoding.UTF8.GetBytes("MANIFESTS"), Encoding.UTF8.GetBytes("TABLE"), Encoding.UTF8.GetBytes("VALID") },
                new() { Encoding.UTF8.GetBytes("DATAWAREHOUSE"), Encoding.UTF8.GetBytes("BLOBS"), Encoding.UTF8.GetBytes("TABLE"), Encoding.UTF8.GetBytes("VALID") }
            },
            StatementType = OracleStatementType.Select
        };
    }

    /// <summary>
    /// Creates DBA_USERS / ALL_USERS result.
    /// </summary>
    private OracleQueryResult CreateUsersResult()
    {
        return new OracleQueryResult
        {
            IsEmpty = false,
            Columns = new List<OracleColumnDescription>
            {
                new() { Name = "USERNAME", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 128, Position = 1 },
                new() { Name = "USER_ID", TypeOid = OracleTypeOid.Number, DisplaySize = 10, Position = 2 },
                new() { Name = "CREATED", TypeOid = OracleTypeOid.Date, DisplaySize = 19, Position = 3 },
                new() { Name = "ACCOUNT_STATUS", TypeOid = OracleTypeOid.Varchar2, DisplaySize = 32, Position = 4 }
            },
            Rows = new List<List<byte[]?>>
            {
                new()
                {
                    Encoding.UTF8.GetBytes("DATAWAREHOUSE"),
                    Encoding.UTF8.GetBytes("1"),
                    Encoding.UTF8.GetBytes(DateTime.UtcNow.AddYears(-1).ToString("dd-MMM-yy").ToUpperInvariant()),
                    Encoding.UTF8.GetBytes("OPEN")
                },
                new()
                {
                    Encoding.UTF8.GetBytes("SYS"),
                    Encoding.UTF8.GetBytes("0"),
                    Encoding.UTF8.GetBytes(DateTime.UtcNow.AddYears(-1).ToString("dd-MMM-yy").ToUpperInvariant()),
                    Encoding.UTF8.GetBytes("OPEN")
                }
            },
            StatementType = OracleStatementType.Select
        };
    }

    /// <summary>
    /// Translates Oracle-specific SQL syntax to standard SQL where possible.
    /// </summary>
    /// <param name="sql">The original SQL statement.</param>
    /// <returns>The translated SQL statement.</returns>
    public string TranslateOracleSpecificSyntax(string sql)
    {
        // Handle ROWNUM pseudo-column (convert to LIMIT if simple pattern)
        if (sql.Contains("ROWNUM", StringComparison.OrdinalIgnoreCase))
        {
            var rownumMatch = Regex.Match(sql, @"WHERE\s+ROWNUM\s*<=?\s*(\d+)", RegexOptions.IgnoreCase);
            if (rownumMatch.Success)
            {
                var limit = rownumMatch.Groups[1].Value;
                sql = Regex.Replace(sql, @"WHERE\s+ROWNUM\s*<=?\s*\d+", "", RegexOptions.IgnoreCase);
                sql = sql.TrimEnd() + $" LIMIT {limit}";
            }
        }

        // Handle (+) outer join syntax (Oracle-specific)
        sql = Regex.Replace(sql, @"(\w+\.\w+)\s*\(\+\)\s*=", "$1 = ", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"=\s*(\w+\.\w+)\s*\(\+\)", "= $1 ", RegexOptions.IgnoreCase);

        // Handle NVL -> COALESCE
        sql = Regex.Replace(sql, @"\bNVL\s*\(", "COALESCE(", RegexOptions.IgnoreCase);

        // Handle NVL2 (Oracle-specific three-argument null check)
        // NVL2(expr, if_not_null, if_null) -> CASE WHEN expr IS NOT NULL THEN if_not_null ELSE if_null END
        sql = Regex.Replace(sql, @"\bNVL2\s*\(\s*(\w+)\s*,\s*([^,]+)\s*,\s*([^)]+)\s*\)",
            "CASE WHEN $1 IS NOT NULL THEN $2 ELSE $3 END", RegexOptions.IgnoreCase);

        // Handle DECODE -> CASE
        // Simple DECODE(expr, val1, result1, default) -> CASE expr WHEN val1 THEN result1 ELSE default END
        // This is a simplified translation for common cases

        // Handle TO_DATE/TO_TIMESTAMP - pass through for now as most SQL engines understand this

        // Handle sequence.NEXTVAL / sequence.CURRVAL - would need schema context

        return sql;
    }

    /// <summary>
    /// Extracts bind variable names from a SQL statement.
    /// </summary>
    /// <param name="sql">The SQL statement.</param>
    /// <returns>List of bind variable names (without colon prefix).</returns>
    public List<string> ExtractBindVariables(string sql)
    {
        var variables = new List<string>();
        var matches = Regex.Matches(sql, @":(\w+)");

        foreach (Match match in matches)
        {
            var varName = match.Groups[1].Value;
            if (!variables.Contains(varName, StringComparer.OrdinalIgnoreCase))
            {
                variables.Add(varName);
            }
        }

        return variables;
    }

    /// <summary>
    /// Substitutes bind variables in a SQL statement with values.
    /// </summary>
    /// <param name="sql">The SQL statement with bind variables.</param>
    /// <param name="parameters">The parameters to substitute.</param>
    /// <returns>The SQL statement with values substituted.</returns>
    public string SubstituteBindVariables(string sql, List<OracleBindParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        foreach (var param in parameters)
        {
            var placeholder = $":{param.Name}";
            string replacement;

            if (param.IsNull || param.Value == null)
            {
                replacement = "NULL";
            }
            else
            {
                var valueStr = Encoding.UTF8.GetString(param.Value);
                // Escape single quotes
                valueStr = valueStr.Replace("'", "''");

                // Quote based on type
                if (param.TypeOid == OracleTypeOid.Number || param.TypeOid == OracleTypeOid.BinaryFloat ||
                    param.TypeOid == OracleTypeOid.BinaryDouble)
                {
                    replacement = valueStr;
                }
                else
                {
                    replacement = $"'{valueStr}'";
                }
            }

            sql = Regex.Replace(sql, Regex.Escape(placeholder) + @"(?!\w)", replacement, RegexOptions.IgnoreCase);
        }

        return sql;
    }
}
