// <copyright file="OdbcDriverPlugin.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using DataWarehouse.Plugins.OdbcDriver.Api;
using DataWarehouse.Plugins.OdbcDriver.Handles;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.OdbcDriver;

/// <summary>
/// ODBC 3.8 Driver Plugin for DataWarehouse.
/// Enables ODBC-compatible applications to connect to DataWarehouse through the standard ODBC interface.
///
/// Features:
/// - Full ODBC 3.8 specification compliance
/// - Handle management (Environment, Connection, Statement, Descriptor)
/// - Connection functions: SQLConnect, SQLDriverConnect, SQLDisconnect
/// - Statement execution: SQLExecDirect, SQLPrepare, SQLExecute
/// - Result fetching: SQLFetch, SQLGetData
/// - Column metadata: SQLDescribeCol, SQLNumResultCols, SQLRowCount
/// - Parameter binding: SQLBindParameter, SQLBindCol
/// - Unicode support (W-suffix functions)
/// - Full SQLSTATE error reporting
/// - Thread-safe implementation
///
/// Supported clients:
/// - Any ODBC-compatible application
/// - Microsoft Excel, Access
/// - Power BI, Tableau, Looker
/// - Python (pyodbc), .NET (System.Data.Odbc)
/// - Java (JDBC-ODBC bridge)
/// - Any language with ODBC bindings
///
/// Connection string format:
///   DRIVER={DataWarehouse ODBC Driver};SERVER=localhost;DATABASE=datawarehouse;UID=user;PWD=pass
/// </summary>
public sealed class OdbcDriverPlugin : InterfacePluginBase
{
    private readonly OdbcDriverConfig _config = new();
    private readonly ConcurrentDictionary<string, object> _state = new();
    private OdbcApi? _api;
    private OdbcUnicodeApi? _unicodeApi;
    private Func<string, CancellationToken, Task<OdbcQueryResult>>? _sqlExecutor;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    /// <summary>
    /// Gets the unique identifier for this plugin.
    /// </summary>
    public override string Id => "com.datawarehouse.driver.odbc";

    /// <summary>
    /// Gets the display name of this plugin.
    /// </summary>
    public override string Name => "ODBC Driver";

    /// <summary>
    /// Gets the version of this plugin.
    /// </summary>
    public override string Version => "1.0.0";

    /// <summary>
    /// Gets the category of this plugin.
    /// </summary>
    public override PluginCategory Category => PluginCategory.InterfaceProvider;

    /// <summary>
    /// Gets the protocol implemented by this plugin.
    /// </summary>
    public override string Protocol => "odbc";

    /// <summary>
    /// Gets the ODBC API for ANSI string operations.
    /// </summary>
    public OdbcApi Api => _api ?? throw new InvalidOperationException("Plugin not started.");

    /// <summary>
    /// Gets the ODBC API for Unicode string operations.
    /// </summary>
    public OdbcUnicodeApi UnicodeApi => _unicodeApi ?? throw new InvalidOperationException("Plugin not started.");

    /// <summary>
    /// Gets the driver configuration.
    /// </summary>
    public OdbcDriverConfig Configuration => _config;

    /// <summary>
    /// Handles the plugin handshake during initialization.
    /// </summary>
    /// <param name="request">The handshake request containing configuration.</param>
    /// <returns>The handshake response indicating success or failure.</returns>
    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        if (request.Config != null)
        {
            if (request.Config.TryGetValue("driverName", out var driverName) && driverName is string dn)
                _config.DriverName = dn;

            if (request.Config.TryGetValue("driverVersion", out var driverVer) && driverVer is string dv)
                _config.DriverVersion = dv;

            if (request.Config.TryGetValue("defaultDatabase", out var db) && db is string dbName)
                _config.DefaultDatabase = dbName;

            if (request.Config.TryGetValue("defaultSchema", out var schema) && schema is string schemaName)
                _config.DefaultSchema = schemaName;

            if (request.Config.TryGetValue("maxConnections", out var maxConn) && maxConn is int mc)
                _config.MaxConnections = mc;

            if (request.Config.TryGetValue("queryTimeout", out var timeout) && timeout is int qt)
                _config.DefaultQueryTimeoutSeconds = qt;

            if (request.Config.TryGetValue("enableTracing", out var trace) && trace is bool tr)
                _config.EnableTracing = tr;

            if (request.Config.TryGetValue("traceFile", out var traceFile) && traceFile is string tf)
                _config.TraceFile = tf;

            if (request.Config.TryGetValue("useUnicode", out var unicode) && unicode is bool uc)
                _config.UseUnicode = uc;

            // Get SQL executor callback (injected by kernel)
            if (request.Config.TryGetValue("sqlExecutor", out var executor) &&
                executor is Func<string, CancellationToken, Task<OdbcQueryResult>> sqlExec)
            {
                _sqlExecutor = sqlExec;
            }
        }

        return Task.FromResult(new HandshakeResponse
        {
            PluginId = Id,
            Name = Name,
            Version = ParseSemanticVersion(Version),
            Category = Category,
            Success = true,
            ReadyState = PluginReadyState.Ready,
            Capabilities = GetCapabilities(),
            Metadata = GetMetadata()
        });
    }

    /// <summary>
    /// Starts the ODBC driver plugin.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public override Task StartAsync(CancellationToken ct)
    {
        if (_isRunning)
        {
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Create API instances
        _api = new OdbcApi(_config, _sqlExecutor ?? DefaultSqlExecutor);
        _unicodeApi = new OdbcUnicodeApi(_api);

        _isRunning = true;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the ODBC driver plugin.
    /// </summary>
    public override Task StopAsync()
    {
        if (!_isRunning)
        {
            return Task.CompletedTask;
        }

        _cts?.Cancel();

        // Clean up handles
        HandleManager.Instance.Dispose();

        _api = null;
        _unicodeApi = null;
        _isRunning = false;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles messages sent to this plugin.
    /// </summary>
    /// <param name="message">The incoming message.</param>
    public override Task OnMessageAsync(PluginMessage message)
    {
        if (message.Payload == null)
            return Task.CompletedTask;

        var response = message.Type switch
        {
            "odbc.start" => HandleStart(),
            "odbc.stop" => HandleStop(),
            "odbc.status" => HandleStatus(),
            "odbc.allocEnv" => HandleAllocEnv(),
            "odbc.freeEnv" => HandleFreeEnv(message.Payload),
            "odbc.allocConn" => HandleAllocConn(message.Payload),
            "odbc.connect" => HandleConnect(message.Payload),
            "odbc.disconnect" => HandleDisconnect(message.Payload),
            "odbc.execute" => HandleExecute(message.Payload),
            _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
        };

        if (response != null)
        {
            message.Payload["_response"] = response;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets the SQL executor callback for executing queries.
    /// Should be called before starting the plugin.
    /// </summary>
    /// <param name="executor">The SQL executor function.</param>
    public void SetSqlExecutor(Func<string, CancellationToken, Task<OdbcQueryResult>> executor)
    {
        _sqlExecutor = executor ?? throw new ArgumentNullException(nameof(executor));

        // Update API if already created
        if (_api != null)
        {
            _api = new OdbcApi(_config, _sqlExecutor);
            _unicodeApi = new OdbcUnicodeApi(_api);
        }
    }

    /// <summary>
    /// Default SQL executor for testing (returns mock data).
    /// </summary>
    private Task<OdbcQueryResult> DefaultSqlExecutor(string sql, CancellationToken ct)
    {
        // Simple fallback executor
        var lower = sql.ToLowerInvariant().Trim();

        // Handle SELECT statements
        if (lower.StartsWith("select"))
        {
            return Task.FromResult(new OdbcQueryResult
            {
                HasResultSet = true,
                Columns = new List<OdbcColumnInfo>
                {
                    new()
                    {
                        Name = "message",
                        Ordinal = 1,
                        SqlType = SqlDataType.VarChar,
                        ColumnSize = 255,
                        TypeName = "VARCHAR"
                    }
                },
                Rows = new List<object?[]>
                {
                    new object?[] { "SQL executor not configured - configure via SetSqlExecutor or handshake" }
                }
            });
        }

        // Handle non-SELECT statements
        return Task.FromResult(new OdbcQueryResult
        {
            HasResultSet = false,
            RowsAffected = 0
        });
    }

    /// <summary>
    /// Gets the capabilities of this plugin.
    /// </summary>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new()
            {
                Name = "odbc_3_8",
                DisplayName = "ODBC 3.8 Support",
                Description = "Full ODBC 3.8 specification compliance"
            },
            new()
            {
                Name = "handle_management",
                DisplayName = "Handle Management",
                Description = "SQLAllocHandle, SQLFreeHandle for all handle types"
            },
            new()
            {
                Name = "connection_management",
                DisplayName = "Connection Management",
                Description = "SQLConnect, SQLDriverConnect, SQLDisconnect"
            },
            new()
            {
                Name = "statement_execution",
                DisplayName = "Statement Execution",
                Description = "SQLExecDirect, SQLPrepare, SQLExecute"
            },
            new()
            {
                Name = "result_fetching",
                DisplayName = "Result Fetching",
                Description = "SQLFetch, SQLGetData with type conversion"
            },
            new()
            {
                Name = "column_metadata",
                DisplayName = "Column Metadata",
                Description = "SQLDescribeCol, SQLNumResultCols, SQLColAttribute"
            },
            new()
            {
                Name = "parameter_binding",
                DisplayName = "Parameter Binding",
                Description = "SQLBindParameter for prepared statements"
            },
            new()
            {
                Name = "column_binding",
                DisplayName = "Column Binding",
                Description = "SQLBindCol for efficient result fetching"
            },
            new()
            {
                Name = "unicode_support",
                DisplayName = "Unicode Support",
                Description = "W-suffix functions for Unicode string handling"
            },
            new()
            {
                Name = "diagnostic_records",
                DisplayName = "Diagnostic Records",
                Description = "SQLGetDiagRec, SQLGetDiagField with SQLSTATE codes"
            },
            new()
            {
                Name = "catalog_functions",
                DisplayName = "Catalog Functions",
                Description = "SQLTables, SQLColumns for schema discovery"
            },
            new()
            {
                Name = "transaction_support",
                DisplayName = "Transaction Support",
                Description = "SQLEndTran for commit/rollback"
            },
            new()
            {
                Name = "connection_pooling",
                DisplayName = "Connection Pooling",
                Description = "Environment-level connection pooling configuration"
            }
        };
    }

    /// <summary>
    /// Gets the metadata for this plugin.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["DriverName"] = _config.DriverName;
        metadata["DriverVersion"] = _config.DriverVersion;
        metadata["OdbcVersion"] = _config.OdbcVersion;
        metadata["DefaultDatabase"] = _config.DefaultDatabase;
        metadata["MaxConnections"] = _config.MaxConnections;
        metadata["IsRunning"] = _isRunning;
        metadata["ActiveEnvironments"] = HandleManager.Instance.GetHandleCount(SqlHandleType.Environment);
        metadata["ActiveConnections"] = HandleManager.Instance.GetHandleCount(SqlHandleType.Connection);
        metadata["ActiveStatements"] = HandleManager.Instance.GetHandleCount(SqlHandleType.Statement);
        metadata["SupportsUnicode"] = _config.UseUnicode;
        metadata["SupportedClients"] = new[] { "Excel", "Power BI", "Tableau", "Python (pyodbc)", ".NET", "JDBC-ODBC" };
        return metadata;
    }

    #region Message Handlers

    private Dictionary<string, object> HandleStart()
    {
        return new Dictionary<string, object>
        {
            ["success"] = _isRunning,
            ["message"] = _isRunning ? "ODBC driver is running" : "Failed to start ODBC driver"
        };
    }

    private Dictionary<string, object> HandleStop()
    {
        StopAsync().GetAwaiter().GetResult();
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["message"] = "ODBC driver stopped"
        };
    }

    private Dictionary<string, object> HandleStatus()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["isRunning"] = _isRunning,
            ["driverName"] = _config.DriverName,
            ["driverVersion"] = _config.DriverVersion,
            ["odbcVersion"] = _config.OdbcVersion,
            ["activeEnvironments"] = HandleManager.Instance.GetHandleCount(SqlHandleType.Environment),
            ["activeConnections"] = HandleManager.Instance.GetHandleCount(SqlHandleType.Connection),
            ["activeStatements"] = HandleManager.Instance.GetHandleCount(SqlHandleType.Statement),
            ["maxConnections"] = _config.MaxConnections
        };
    }

    private Dictionary<string, object> HandleAllocEnv()
    {
        if (_api == null)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "ODBC driver not started"
            };
        }

        var result = _api.SQLAllocHandle(SqlHandleType.Environment, nint.Zero, out var handle);

        return new Dictionary<string, object>
        {
            ["success"] = result == SqlReturn.Success,
            ["handle"] = handle.ToInt64(),
            ["returnCode"] = (int)result
        };
    }

    private Dictionary<string, object> HandleFreeEnv(Dictionary<string, object> payload)
    {
        if (_api == null)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "ODBC driver not started"
            };
        }

        if (!payload.TryGetValue("handle", out var handleObj) || handleObj is not long handleLong)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Handle not specified"
            };
        }

        var result = _api.SQLFreeHandle(SqlHandleType.Environment, new nint(handleLong));

        return new Dictionary<string, object>
        {
            ["success"] = result == SqlReturn.Success,
            ["returnCode"] = (int)result
        };
    }

    private Dictionary<string, object> HandleAllocConn(Dictionary<string, object> payload)
    {
        if (_api == null)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "ODBC driver not started"
            };
        }

        if (!payload.TryGetValue("environmentHandle", out var envObj) || envObj is not long envLong)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Environment handle not specified"
            };
        }

        var result = _api.SQLAllocHandle(SqlHandleType.Connection, new nint(envLong), out var handle);

        return new Dictionary<string, object>
        {
            ["success"] = result == SqlReturn.Success,
            ["handle"] = handle.ToInt64(),
            ["returnCode"] = (int)result
        };
    }

    private Dictionary<string, object> HandleConnect(Dictionary<string, object> payload)
    {
        if (_api == null)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "ODBC driver not started"
            };
        }

        if (!payload.TryGetValue("connectionHandle", out var connObj) || connObj is not long connLong)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Connection handle not specified"
            };
        }

        var connectionString = payload.TryGetValue("connectionString", out var csObj) ? csObj as string : null;

        SqlReturn result;
        string outConnectionString;

        if (!string.IsNullOrEmpty(connectionString))
        {
            result = _api.SQLDriverConnect(
                new nint(connLong),
                nint.Zero,
                connectionString,
                out outConnectionString,
                DriverCompletion.NoPrompt);
        }
        else
        {
            var dsn = payload.TryGetValue("dsn", out var dsnObj) ? dsnObj as string ?? "" : "";
            var uid = payload.TryGetValue("uid", out var uidObj) ? uidObj as string ?? "" : "";
            var pwd = payload.TryGetValue("pwd", out var pwdObj) ? pwdObj as string ?? "" : "";

            result = _api.SQLConnect(new nint(connLong), dsn, uid, pwd);
            outConnectionString = $"DSN={dsn};UID={uid}";
        }

        return new Dictionary<string, object>
        {
            ["success"] = result == SqlReturn.Success || result == SqlReturn.SuccessWithInfo,
            ["connectionString"] = outConnectionString,
            ["returnCode"] = (int)result
        };
    }

    private Dictionary<string, object> HandleDisconnect(Dictionary<string, object> payload)
    {
        if (_api == null)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "ODBC driver not started"
            };
        }

        if (!payload.TryGetValue("connectionHandle", out var connObj) || connObj is not long connLong)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Connection handle not specified"
            };
        }

        var result = _api.SQLDisconnect(new nint(connLong));

        return new Dictionary<string, object>
        {
            ["success"] = result == SqlReturn.Success,
            ["returnCode"] = (int)result
        };
    }

    private Dictionary<string, object> HandleExecute(Dictionary<string, object> payload)
    {
        if (_api == null)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "ODBC driver not started"
            };
        }

        if (!payload.TryGetValue("statementHandle", out var stmtObj) || stmtObj is not long stmtLong)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Statement handle not specified"
            };
        }

        if (!payload.TryGetValue("sql", out var sqlObj) || sqlObj is not string sql)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "SQL statement not specified"
            };
        }

        var result = _api.SQLExecDirect(new nint(stmtLong), sql);

        var response = new Dictionary<string, object>
        {
            ["success"] = result == SqlReturn.Success || result == SqlReturn.SuccessWithInfo,
            ["returnCode"] = (int)result
        };

        // Get result info
        if (result == SqlReturn.Success || result == SqlReturn.SuccessWithInfo)
        {
            _api.SQLNumResultCols(new nint(stmtLong), out var colCount);
            _api.SQLRowCount(new nint(stmtLong), out var rowCount);

            response["columnCount"] = colCount;
            response["rowCount"] = rowCount;
        }

        return response;
    }

    #endregion
}
