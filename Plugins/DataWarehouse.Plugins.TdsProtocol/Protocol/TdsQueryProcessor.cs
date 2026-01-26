using System.Text;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.TdsProtocol.Protocol;

/// <summary>
/// Processes SQL queries and translates them for DataWarehouse execution.
/// Handles SQL Server-specific queries for SSMS/Azure Data Studio compatibility.
/// </summary>
public sealed class TdsQueryProcessor
{
    private readonly Func<string, CancellationToken, Task<TdsQueryResult>> _executeSqlAsync;
    private readonly string _serverName;
    private readonly string _databaseName;

    /// <summary>
    /// Initializes a new instance of the <see cref="TdsQueryProcessor"/> class.
    /// </summary>
    /// <param name="executeSqlAsync">Delegate to execute SQL against the backend.</param>
    /// <param name="serverName">Server name to report.</param>
    /// <param name="databaseName">Default database name.</param>
    public TdsQueryProcessor(
        Func<string, CancellationToken, Task<TdsQueryResult>> executeSqlAsync,
        string serverName = "DataWarehouse",
        string databaseName = "datawarehouse")
    {
        _executeSqlAsync = executeSqlAsync ?? throw new ArgumentNullException(nameof(executeSqlAsync));
        _serverName = serverName;
        _databaseName = databaseName;
    }

    /// <summary>
    /// Executes a SQL query and returns results.
    /// </summary>
    /// <param name="sql">The SQL query to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Query result with column metadata and rows.</returns>
    public async Task<TdsQueryResult> ExecuteQueryAsync(string sql, CancellationToken ct = default)
    {
        sql = sql.Trim();

        // Handle empty query
        if (string.IsNullOrWhiteSpace(sql))
        {
            return new TdsQueryResult
            {
                IsEmpty = true,
                CommandTag = ""
            };
        }

        // Handle SQL Server-specific system queries for tool compatibility
        if (IsSqlServerSystemQuery(sql))
        {
            return await ExecuteSystemQueryAsync(sql, ct);
        }

        // Handle SET statements
        if (sql.StartsWith("SET ", StringComparison.OrdinalIgnoreCase))
        {
            return HandleSetStatement(sql);
        }

        // Handle USE database
        if (sql.StartsWith("USE ", StringComparison.OrdinalIgnoreCase))
        {
            return HandleUseStatement(sql);
        }

        // Handle transaction commands
        if (IsTransactionCommand(sql, out var txResult))
        {
            return txResult;
        }

        // Execute via backend
        return await _executeSqlAsync(sql, ct);
    }

    /// <summary>
    /// Executes an RPC request (sp_executesql, sp_prepare, etc.).
    /// </summary>
    /// <param name="request">The RPC request.</param>
    /// <param name="state">The connection state.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Query result.</returns>
    public async Task<TdsQueryResult> ExecuteRpcAsync(RpcRequest request, TdsConnectionState state, CancellationToken ct = default)
    {
        switch (request.ProcedureName.ToLowerInvariant())
        {
            case "sp_executesql":
                return await ExecuteSpExecuteSqlAsync(request, ct);

            case "sp_prepare":
                return await ExecuteSpPrepareAsync(request, state, ct);

            case "sp_execute":
                return await ExecuteSpExecuteAsync(request, state, ct);

            case "sp_unprepare":
                return await ExecuteSpUnprepareAsync(request, state, ct);

            case "sp_prepexec":
                return await ExecuteSpPrepExecAsync(request, state, ct);

            case "sp_cursoropen":
            case "sp_cursorfetch":
            case "sp_cursorclose":
                // Cursor operations - return empty success for now
                return new TdsQueryResult { CommandTag = "OK" };

            default:
                // Try to execute as regular query
                if (request.Parameters.Count > 0 && request.Parameters[0].Value is string sql)
                {
                    return await ExecuteQueryAsync(sql, ct);
                }
                return new TdsQueryResult
                {
                    ErrorMessage = $"Procedure '{request.ProcedureName}' is not supported",
                    ErrorNumber = TdsErrorNumbers.GeneralError,
                    ErrorSeverity = TdsSeverity.UserError,
                    ErrorState = 1
                };
        }
    }

    /// <summary>
    /// Checks if the query is a SQL Server system query.
    /// </summary>
    private bool IsSqlServerSystemQuery(string sql)
    {
        var lower = sql.ToLowerInvariant();

        return lower.Contains("sys.") ||
               lower.Contains("information_schema.") ||
               lower.Contains("sysobjects") ||
               lower.Contains("syscolumns") ||
               lower.Contains("sysindexes") ||
               lower.Contains("sp_helptext") ||
               lower.Contains("sp_help") ||
               lower.Contains("sp_tables") ||
               lower.Contains("sp_columns") ||
               lower.Contains("@@version") ||
               lower.Contains("@@servername") ||
               lower.Contains("@@spid") ||
               lower.Contains("db_name()") ||
               lower.Contains("suser_sname()") ||
               lower.Contains("serverproperty(") ||
               lower.Contains("object_id(") ||
               lower.Contains("col_name(") ||
               lower.Contains("type_name(") ||
               lower.Contains("schema_name(") ||
               lower.Contains("databasepropertyex(") ||
               lower.Contains("has_perms_by_name(");
    }

    /// <summary>
    /// Executes SQL Server system queries for SSMS compatibility.
    /// </summary>
    private async Task<TdsQueryResult> ExecuteSystemQueryAsync(string sql, CancellationToken ct)
    {
        await Task.CompletedTask;
        var lower = sql.ToLowerInvariant();

        // @@version
        if (lower.Contains("@@version"))
        {
            return CreateSingleValueResult("Microsoft SQL Server 2022 (RTM-CU14) (KB5038325) - 16.0.4135.4 (X64)\n\tDataWarehouse Compatible",
                "version", TdsDataType.NVarChar);
        }

        // @@servername
        if (lower.Contains("@@servername"))
        {
            return CreateSingleValueResult(_serverName, "servername", TdsDataType.NVarChar);
        }

        // @@spid
        if (lower.Contains("@@spid"))
        {
            return CreateSingleValueResult(Random.Shared.Next(51, 1000), "spid", TdsDataType.SmallInt);
        }

        // DB_NAME()
        if (lower.Contains("db_name()"))
        {
            return CreateSingleValueResult(_databaseName, "database_name", TdsDataType.NVarChar);
        }

        // SUSER_SNAME()
        if (lower.Contains("suser_sname()"))
        {
            return CreateSingleValueResult("sa", "user_name", TdsDataType.NVarChar);
        }

        // SERVERPROPERTY queries
        if (lower.Contains("serverproperty("))
        {
            return ExecuteServerPropertyQuery(sql);
        }

        // sys.databases
        if (lower.Contains("sys.databases"))
        {
            return ExecuteSysDatabasesQuery();
        }

        // sys.tables or sys.objects
        if (lower.Contains("sys.tables") || (lower.Contains("sys.objects") && lower.Contains("type") && lower.Contains("'u'")))
        {
            return ExecuteSysTablesQuery();
        }

        // sys.columns
        if (lower.Contains("sys.columns"))
        {
            return ExecuteSysColumnsQuery(sql);
        }

        // sys.schemas
        if (lower.Contains("sys.schemas"))
        {
            return ExecuteSysSchemasQuery();
        }

        // sys.types
        if (lower.Contains("sys.types"))
        {
            return ExecuteSysTypesQuery();
        }

        // information_schema.tables
        if (lower.Contains("information_schema.tables"))
        {
            return ExecuteInformationSchemaTablesQuery();
        }

        // information_schema.columns
        if (lower.Contains("information_schema.columns"))
        {
            return ExecuteInformationSchemaColumnsQuery(sql);
        }

        // OBJECTPROPERTY, OBJECT_ID, etc.
        if (lower.Contains("object_id("))
        {
            return CreateSingleValueResult(0, "object_id", TdsDataType.Int);
        }

        // HAS_PERMS_BY_NAME
        if (lower.Contains("has_perms_by_name("))
        {
            return CreateSingleValueResult(1, "permission", TdsDataType.Int);
        }

        // DATABASEPROPERTYEX
        if (lower.Contains("databasepropertyex("))
        {
            var match = Regex.Match(sql, @"databasepropertyex\s*\([^,]+,\s*'([^']+)'\)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var property = match.Groups[1].Value.ToLowerInvariant();
                var value = property switch
                {
                    "collation" => (object)"Latin1_General_CI_AS",
                    "status" => "ONLINE",
                    "recovery" => "FULL",
                    "version" => 957,
                    "isansinullon" => 1,
                    "isquotedidentifierson" => 1,
                    _ => DBNull.Value
                };
                return CreateSingleValueResult(value, "property_value", TdsDataType.NVarChar);
            }
        }

        // Default: return empty result
        return new TdsQueryResult
        {
            Columns = new List<TdsColumnMetadata>(),
            Rows = new List<List<object?>>(),
            CommandTag = "SELECT 0"
        };
    }

    /// <summary>
    /// Handles SET statements.
    /// </summary>
    private TdsQueryResult HandleSetStatement(string sql)
    {
        // Most SET statements can be acknowledged without actual effect
        return new TdsQueryResult
        {
            IsEmpty = true,
            CommandTag = "SET"
        };
    }

    /// <summary>
    /// Handles USE database statement.
    /// </summary>
    private TdsQueryResult HandleUseStatement(string sql)
    {
        var match = Regex.Match(sql, @"USE\s+\[?(\w+)\]?", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var dbName = match.Groups[1].Value;
            return new TdsQueryResult
            {
                IsEmpty = true,
                CommandTag = "USE",
                Messages = new List<TdsMessage>
                {
                    new TdsMessage
                    {
                        Number = 5701,
                        Severity = TdsSeverity.Informational,
                        State = 2,
                        Text = $"Changed database context to '{dbName}'."
                    }
                }
            };
        }
        return new TdsQueryResult { IsEmpty = true, CommandTag = "USE" };
    }

    /// <summary>
    /// Checks for transaction commands.
    /// </summary>
    private bool IsTransactionCommand(string sql, out TdsQueryResult result)
    {
        var lower = sql.ToLowerInvariant().Trim();

        if (lower.StartsWith("begin tran") || lower == "begin transaction")
        {
            result = new TdsQueryResult { IsEmpty = true, CommandTag = "BEGIN TRANSACTION" };
            return true;
        }

        if (lower == "commit" || lower == "commit transaction" || lower.StartsWith("commit tran"))
        {
            result = new TdsQueryResult { IsEmpty = true, CommandTag = "COMMIT" };
            return true;
        }

        if (lower == "rollback" || lower == "rollback transaction" || lower.StartsWith("rollback tran"))
        {
            result = new TdsQueryResult { IsEmpty = true, CommandTag = "ROLLBACK" };
            return true;
        }

        result = null!;
        return false;
    }

    /// <summary>
    /// Executes sp_executesql RPC.
    /// </summary>
    private async Task<TdsQueryResult> ExecuteSpExecuteSqlAsync(RpcRequest request, CancellationToken ct)
    {
        if (request.Parameters.Count == 0 || request.Parameters[0].Value == null)
        {
            return new TdsQueryResult
            {
                ErrorMessage = "sp_executesql requires a SQL statement parameter",
                ErrorNumber = TdsErrorNumbers.SyntaxError,
                ErrorSeverity = TdsSeverity.UserError,
                ErrorState = 1
            };
        }

        var sql = request.Parameters[0].Value?.ToString() ?? "";

        // If there are parameter definitions and values, substitute them
        if (request.Parameters.Count > 2)
        {
            sql = SubstituteParameters(sql, request.Parameters.Skip(2).ToList());
        }

        return await ExecuteQueryAsync(sql, ct);
    }

    /// <summary>
    /// Executes sp_prepare RPC.
    /// </summary>
    private async Task<TdsQueryResult> ExecuteSpPrepareAsync(RpcRequest request, TdsConnectionState state, CancellationToken ct)
    {
        await Task.CompletedTask;

        // Parameters: @handle OUTPUT, @params, @stmt
        if (request.Parameters.Count < 3)
        {
            return new TdsQueryResult
            {
                ErrorMessage = "sp_prepare requires handle, params, and statement parameters",
                ErrorNumber = TdsErrorNumbers.SyntaxError,
                ErrorSeverity = TdsSeverity.UserError,
                ErrorState = 1
            };
        }

        var sql = request.Parameters[2].Value?.ToString() ?? "";
        var paramDefs = request.Parameters[1].Value?.ToString() ?? "";

        var handle = state.GetNextPrepareHandle();
        state.PreparedStatements[handle] = new TdsPreparedStatement
        {
            Handle = handle,
            Query = sql,
            Parameters = ParseParameterDefinitions(paramDefs)
        };

        return new TdsQueryResult
        {
            ReturnValue = handle,
            CommandTag = "sp_prepare"
        };
    }

    /// <summary>
    /// Executes sp_execute RPC.
    /// </summary>
    private async Task<TdsQueryResult> ExecuteSpExecuteAsync(RpcRequest request, TdsConnectionState state, CancellationToken ct)
    {
        if (request.Parameters.Count == 0)
        {
            return new TdsQueryResult
            {
                ErrorMessage = "sp_execute requires a handle parameter",
                ErrorNumber = TdsErrorNumbers.SyntaxError,
                ErrorSeverity = TdsSeverity.UserError,
                ErrorState = 1
            };
        }

        var handle = Convert.ToInt32(request.Parameters[0].Value);

        if (!state.PreparedStatements.TryGetValue(handle, out var stmt))
        {
            return new TdsQueryResult
            {
                ErrorMessage = $"Prepared statement handle {handle} not found",
                ErrorNumber = TdsErrorNumbers.GeneralError,
                ErrorSeverity = TdsSeverity.UserError,
                ErrorState = 1
            };
        }

        // Substitute parameters
        var sql = SubstituteParameters(stmt.Query, request.Parameters.Skip(1).ToList());
        stmt.LastExecutedAt = DateTime.UtcNow;
        stmt.ExecutionCount++;

        return await ExecuteQueryAsync(sql, ct);
    }

    /// <summary>
    /// Executes sp_unprepare RPC.
    /// </summary>
    private Task<TdsQueryResult> ExecuteSpUnprepareAsync(RpcRequest request, TdsConnectionState state, CancellationToken ct)
    {
        if (request.Parameters.Count > 0)
        {
            var handle = Convert.ToInt32(request.Parameters[0].Value);
            state.PreparedStatements.TryRemove(handle, out _);
        }

        return Task.FromResult(new TdsQueryResult { CommandTag = "sp_unprepare" });
    }

    /// <summary>
    /// Executes sp_prepexec RPC (prepare + execute in one call).
    /// </summary>
    private async Task<TdsQueryResult> ExecuteSpPrepExecAsync(RpcRequest request, TdsConnectionState state, CancellationToken ct)
    {
        // Similar to sp_prepare but also executes
        if (request.Parameters.Count < 3)
        {
            return new TdsQueryResult
            {
                ErrorMessage = "sp_prepexec requires handle, params, and statement parameters",
                ErrorNumber = TdsErrorNumbers.SyntaxError,
                ErrorSeverity = TdsSeverity.UserError,
                ErrorState = 1
            };
        }

        var sql = request.Parameters[2].Value?.ToString() ?? "";
        var paramDefs = request.Parameters[1].Value?.ToString() ?? "";

        var handle = state.GetNextPrepareHandle();
        state.PreparedStatements[handle] = new TdsPreparedStatement
        {
            Handle = handle,
            Query = sql,
            Parameters = ParseParameterDefinitions(paramDefs)
        };

        // Substitute any provided parameters (starting from index 3)
        if (request.Parameters.Count > 3)
        {
            sql = SubstituteParameters(sql, request.Parameters.Skip(3).ToList());
        }

        var result = await ExecuteQueryAsync(sql, ct);
        return new TdsQueryResult
        {
            IsEmpty = result.IsEmpty,
            Columns = result.Columns,
            Rows = result.Rows,
            RowsAffected = result.RowsAffected,
            CommandTag = result.CommandTag,
            ErrorMessage = result.ErrorMessage,
            ErrorNumber = result.ErrorNumber,
            ErrorSeverity = result.ErrorSeverity,
            ErrorState = result.ErrorState,
            Messages = result.Messages,
            ReturnValue = handle,
            OutputParameters = result.OutputParameters
        };
    }

    /// <summary>
    /// Parses parameter definitions string (e.g., "@p1 int, @p2 nvarchar(100)").
    /// </summary>
    private List<TdsParameterDefinition> ParseParameterDefinitions(string paramDefs)
    {
        var result = new List<TdsParameterDefinition>();
        if (string.IsNullOrWhiteSpace(paramDefs))
            return result;

        var parts = paramDefs.Split(',');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            var match = Regex.Match(trimmed, @"(@\w+)\s+(\w+)(?:\((\d+)(?:,\s*(\d+))?\))?", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var name = match.Groups[1].Value;
                var typeName = match.Groups[2].Value.ToLowerInvariant();
                var length = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
                var scale = match.Groups[4].Success ? byte.Parse(match.Groups[4].Value) : (byte)0;

                result.Add(new TdsParameterDefinition
                {
                    Name = name,
                    DataType = MapTypeNameToTdsType(typeName),
                    MaxLength = length,
                    Scale = scale,
                    Precision = (byte)length
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Maps SQL Server type name to TDS type.
    /// </summary>
    private TdsDataType MapTypeNameToTdsType(string typeName)
    {
        return typeName.ToLowerInvariant() switch
        {
            "int" => TdsDataType.IntN,
            "bigint" => TdsDataType.BigInt,
            "smallint" => TdsDataType.SmallInt,
            "tinyint" => TdsDataType.TinyInt,
            "bit" => TdsDataType.BitN,
            "float" => TdsDataType.FloatN,
            "real" => TdsDataType.Real,
            "decimal" or "numeric" => TdsDataType.DecimalN,
            "money" or "smallmoney" => TdsDataType.MoneyN,
            "datetime" or "datetime2" or "smalldatetime" => TdsDataType.DateTimeN,
            "date" => TdsDataType.Date,
            "time" => TdsDataType.Time,
            "nvarchar" or "nchar" or "ntext" => TdsDataType.NVarChar,
            "varchar" or "char" or "text" => TdsDataType.VarChar,
            "varbinary" or "binary" or "image" => TdsDataType.VarBinary,
            "uniqueidentifier" => TdsDataType.UniqueIdentifier,
            "xml" => TdsDataType.Xml,
            _ => TdsDataType.NVarChar
        };
    }

    /// <summary>
    /// Substitutes parameters in SQL with their values.
    /// </summary>
    private string SubstituteParameters(string sql, List<RpcParameter> parameters)
    {
        foreach (var param in parameters)
        {
            var paramName = param.Name.StartsWith("@") ? param.Name : "@" + param.Name;
            if (string.IsNullOrEmpty(paramName) || paramName == "@")
                continue;

            var replacement = param.Value switch
            {
                null => "NULL",
                string s => $"N'{s.Replace("'", "''")}'",
                bool b => b ? "1" : "0",
                DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss.fff}'",
                Guid g => $"'{g}'",
                byte[] bytes => "0x" + BitConverter.ToString(bytes).Replace("-", ""),
                _ => param.Value.ToString()
            };

            sql = Regex.Replace(sql, Regex.Escape(paramName) + @"(?!\w)", replacement ?? "NULL", RegexOptions.IgnoreCase);
        }

        return sql;
    }

    /// <summary>
    /// Creates a single-value result.
    /// </summary>
    private TdsQueryResult CreateSingleValueResult(object? value, string columnName, TdsDataType dataType)
    {
        var maxLen = dataType switch
        {
            TdsDataType.NVarChar or TdsDataType.VarChar => 4000,
            TdsDataType.Int or TdsDataType.IntN => 4,
            TdsDataType.BigInt => 8,
            TdsDataType.SmallInt => 2,
            _ => 0
        };

        return new TdsQueryResult
        {
            Columns = new List<TdsColumnMetadata>
            {
                new TdsColumnMetadata
                {
                    Name = columnName,
                    DataType = dataType,
                    MaxLength = maxLen,
                    Flags = 0x0001, // Nullable
                    Collation = TdsCollation.Default
                }
            },
            Rows = new List<List<object?>> { new List<object?> { value } },
            CommandTag = "SELECT 1"
        };
    }

    /// <summary>
    /// Executes SERVERPROPERTY query.
    /// </summary>
    private TdsQueryResult ExecuteServerPropertyQuery(string sql)
    {
        var match = Regex.Match(sql, @"serverproperty\s*\(\s*'([^']+)'\s*\)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return CreateSingleValueResult(null, "property", TdsDataType.NVarChar);
        }

        var property = match.Groups[1].Value.ToLowerInvariant();
        object? value = property switch
        {
            "productversion" => "16.0.1000.6",
            "productlevel" => "RTM",
            "edition" => "Developer Edition (64-bit)",
            "engineedition" => 3,
            "servername" => _serverName,
            "instancename" => "",
            "machinename" => Environment.MachineName,
            "isclustered" => 0,
            "collation" => "Latin1_General_CI_AS",
            "lcid" => 1033,
            "sqlcharset" => 1,
            "sqlcharsetname" => "iso_1",
            "sqlsortorder" => 52,
            "sqlsortordername" => "nocase_iso",
            "builderversion" => null,
            "isfulltextinstalled" => 1,
            "isintegratedsecurityonly" => 0,
            "issingleuser" => 0,
            "isadvancedanalyticsinstalled" => 0,
            "ispolybaseinstalled" => 0,
            "isxtpssupported" => 1,
            "ishadrenabled" => 0,
            "processid" => Environment.ProcessId,
            "resourceversion" => "16.0.1000.6",
            "servermanagementgroup" => DBNull.Value,
            _ => null
        };

        return CreateSingleValueResult(value, "property_value",
            value is int ? TdsDataType.IntN : TdsDataType.NVarChar);
    }

    /// <summary>
    /// Executes sys.databases query.
    /// </summary>
    private TdsQueryResult ExecuteSysDatabasesQuery()
    {
        return new TdsQueryResult
        {
            Columns = new List<TdsColumnMetadata>
            {
                new() { Name = "name", DataType = TdsDataType.NVarChar, MaxLength = 256, Flags = 0x0001 },
                new() { Name = "database_id", DataType = TdsDataType.IntN, MaxLength = 4, Flags = 0x0001 },
                new() { Name = "create_date", DataType = TdsDataType.DateTimeN, MaxLength = 8, Flags = 0x0001 },
                new() { Name = "collation_name", DataType = TdsDataType.NVarChar, MaxLength = 256, Flags = 0x0001 },
                new() { Name = "state", DataType = TdsDataType.TinyInt, MaxLength = 1, Flags = 0x0001 },
                new() { Name = "state_desc", DataType = TdsDataType.NVarChar, MaxLength = 60, Flags = 0x0001 }
            },
            Rows = new List<List<object?>>
            {
                new() { "master", 1, DateTime.UtcNow.AddYears(-1), "Latin1_General_CI_AS", (byte)0, "ONLINE" },
                new() { "tempdb", 2, DateTime.UtcNow, "Latin1_General_CI_AS", (byte)0, "ONLINE" },
                new() { "model", 3, DateTime.UtcNow.AddYears(-1), "Latin1_General_CI_AS", (byte)0, "ONLINE" },
                new() { "msdb", 4, DateTime.UtcNow.AddYears(-1), "Latin1_General_CI_AS", (byte)0, "ONLINE" },
                new() { _databaseName, 5, DateTime.UtcNow.AddMonths(-1), "Latin1_General_CI_AS", (byte)0, "ONLINE" }
            },
            CommandTag = "SELECT 5"
        };
    }

    /// <summary>
    /// Executes sys.tables query.
    /// </summary>
    private TdsQueryResult ExecuteSysTablesQuery()
    {
        return new TdsQueryResult
        {
            Columns = new List<TdsColumnMetadata>
            {
                new() { Name = "name", DataType = TdsDataType.NVarChar, MaxLength = 256, Flags = 0x0001 },
                new() { Name = "object_id", DataType = TdsDataType.IntN, MaxLength = 4, Flags = 0x0001 },
                new() { Name = "schema_id", DataType = TdsDataType.IntN, MaxLength = 4, Flags = 0x0001 },
                new() { Name = "type", DataType = TdsDataType.NChar, MaxLength = 2, Flags = 0x0001 },
                new() { Name = "type_desc", DataType = TdsDataType.NVarChar, MaxLength = 60, Flags = 0x0001 },
                new() { Name = "create_date", DataType = TdsDataType.DateTimeN, MaxLength = 8, Flags = 0x0001 }
            },
            Rows = new List<List<object?>>
            {
                new() { "manifests", 100, 1, "U ", "USER_TABLE", DateTime.UtcNow.AddDays(-30) },
                new() { "blobs", 101, 1, "U ", "USER_TABLE", DateTime.UtcNow.AddDays(-30) }
            },
            CommandTag = "SELECT 2"
        };
    }

    /// <summary>
    /// Executes sys.columns query.
    /// </summary>
    private TdsQueryResult ExecuteSysColumnsQuery(string sql)
    {
        return new TdsQueryResult
        {
            Columns = new List<TdsColumnMetadata>
            {
                new() { Name = "name", DataType = TdsDataType.NVarChar, MaxLength = 256, Flags = 0x0001 },
                new() { Name = "column_id", DataType = TdsDataType.IntN, MaxLength = 4, Flags = 0x0001 },
                new() { Name = "object_id", DataType = TdsDataType.IntN, MaxLength = 4, Flags = 0x0001 },
                new() { Name = "system_type_id", DataType = TdsDataType.TinyInt, MaxLength = 1, Flags = 0x0001 },
                new() { Name = "user_type_id", DataType = TdsDataType.IntN, MaxLength = 4, Flags = 0x0001 },
                new() { Name = "max_length", DataType = TdsDataType.SmallInt, MaxLength = 2, Flags = 0x0001 },
                new() { Name = "is_nullable", DataType = TdsDataType.BitN, MaxLength = 1, Flags = 0x0001 }
            },
            Rows = new List<List<object?>>
            {
                new() { "id", 1, 100, (byte)231, 231, (short)256, true },
                new() { "content_hash", 2, 100, (byte)231, 231, (short)256, false },
                new() { "size", 3, 100, (byte)127, 127, (short)8, false },
                new() { "created_at", 4, 100, (byte)61, 61, (short)8, false }
            },
            CommandTag = "SELECT 4"
        };
    }

    /// <summary>
    /// Executes sys.schemas query.
    /// </summary>
    private TdsQueryResult ExecuteSysSchemasQuery()
    {
        return new TdsQueryResult
        {
            Columns = new List<TdsColumnMetadata>
            {
                new() { Name = "name", DataType = TdsDataType.NVarChar, MaxLength = 256, Flags = 0x0001 },
                new() { Name = "schema_id", DataType = TdsDataType.IntN, MaxLength = 4, Flags = 0x0001 },
                new() { Name = "principal_id", DataType = TdsDataType.IntN, MaxLength = 4, Flags = 0x0001 }
            },
            Rows = new List<List<object?>>
            {
                new() { "dbo", 1, 1 },
                new() { "sys", 4, 4 },
                new() { "INFORMATION_SCHEMA", 3, 3 }
            },
            CommandTag = "SELECT 3"
        };
    }

    /// <summary>
    /// Executes sys.types query.
    /// </summary>
    private TdsQueryResult ExecuteSysTypesQuery()
    {
        return new TdsQueryResult
        {
            Columns = new List<TdsColumnMetadata>
            {
                new() { Name = "name", DataType = TdsDataType.NVarChar, MaxLength = 256, Flags = 0x0001 },
                new() { Name = "system_type_id", DataType = TdsDataType.TinyInt, MaxLength = 1, Flags = 0x0001 },
                new() { Name = "user_type_id", DataType = TdsDataType.IntN, MaxLength = 4, Flags = 0x0001 },
                new() { Name = "max_length", DataType = TdsDataType.SmallInt, MaxLength = 2, Flags = 0x0001 }
            },
            Rows = new List<List<object?>>
            {
                new() { "int", (byte)56, 56, (short)4 },
                new() { "bigint", (byte)127, 127, (short)8 },
                new() { "nvarchar", (byte)231, 231, (short)-1 },
                new() { "datetime", (byte)61, 61, (short)8 },
                new() { "bit", (byte)104, 104, (short)1 },
                new() { "uniqueidentifier", (byte)36, 36, (short)16 }
            },
            CommandTag = "SELECT 6"
        };
    }

    /// <summary>
    /// Executes INFORMATION_SCHEMA.TABLES query.
    /// </summary>
    private TdsQueryResult ExecuteInformationSchemaTablesQuery()
    {
        return new TdsQueryResult
        {
            Columns = new List<TdsColumnMetadata>
            {
                new() { Name = "TABLE_CATALOG", DataType = TdsDataType.NVarChar, MaxLength = 256, Flags = 0x0001 },
                new() { Name = "TABLE_SCHEMA", DataType = TdsDataType.NVarChar, MaxLength = 256, Flags = 0x0001 },
                new() { Name = "TABLE_NAME", DataType = TdsDataType.NVarChar, MaxLength = 256, Flags = 0x0001 },
                new() { Name = "TABLE_TYPE", DataType = TdsDataType.NVarChar, MaxLength = 60, Flags = 0x0001 }
            },
            Rows = new List<List<object?>>
            {
                new() { _databaseName, "dbo", "manifests", "BASE TABLE" },
                new() { _databaseName, "dbo", "blobs", "BASE TABLE" }
            },
            CommandTag = "SELECT 2"
        };
    }

    /// <summary>
    /// Executes INFORMATION_SCHEMA.COLUMNS query.
    /// </summary>
    private TdsQueryResult ExecuteInformationSchemaColumnsQuery(string sql)
    {
        return new TdsQueryResult
        {
            Columns = new List<TdsColumnMetadata>
            {
                new() { Name = "TABLE_CATALOG", DataType = TdsDataType.NVarChar, MaxLength = 256, Flags = 0x0001 },
                new() { Name = "TABLE_SCHEMA", DataType = TdsDataType.NVarChar, MaxLength = 256, Flags = 0x0001 },
                new() { Name = "TABLE_NAME", DataType = TdsDataType.NVarChar, MaxLength = 256, Flags = 0x0001 },
                new() { Name = "COLUMN_NAME", DataType = TdsDataType.NVarChar, MaxLength = 256, Flags = 0x0001 },
                new() { Name = "ORDINAL_POSITION", DataType = TdsDataType.IntN, MaxLength = 4, Flags = 0x0001 },
                new() { Name = "DATA_TYPE", DataType = TdsDataType.NVarChar, MaxLength = 256, Flags = 0x0001 },
                new() { Name = "IS_NULLABLE", DataType = TdsDataType.NVarChar, MaxLength = 3, Flags = 0x0001 }
            },
            Rows = new List<List<object?>>
            {
                new() { _databaseName, "dbo", "manifests", "id", 1, "nvarchar", "NO" },
                new() { _databaseName, "dbo", "manifests", "content_hash", 2, "nvarchar", "NO" },
                new() { _databaseName, "dbo", "manifests", "size", 3, "bigint", "NO" },
                new() { _databaseName, "dbo", "manifests", "created_at", 4, "datetime", "NO" }
            },
            CommandTag = "SELECT 4"
        };
    }
}
