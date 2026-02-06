using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Database;

/// <summary>
/// Oracle Database connection strategy using TCP connectivity check.
/// Provides connection validation via TCP socket to Oracle Database (default port 1521).
/// Note: Full query operations require Oracle.ManagedDataAccess NuGet package.
/// </summary>
public sealed class OracleConnectionStrategy : DatabaseConnectionStrategyBase
{
    private const int DefaultOraclePort = 1521;

    /// <inheritdoc/>
    public override string StrategyId => "oracle";

    /// <inheritdoc/>
    public override string DisplayName => "Oracle Database";

    /// <inheritdoc/>
    public override ConnectionStrategyCapabilities Capabilities => new();

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Oracle Database relational database connection. Enterprise-grade RDBMS with advanced features including " +
        "PL/SQL, Real Application Clusters (RAC), Advanced Security, partitioning, and in-memory column store. " +
        "Supports Oracle Database 11g, 12c, 18c, 19c, 21c, and Oracle Cloud Infrastructure.";

    /// <inheritdoc/>
    public override string[] Tags =>
    [
        "oracle", "sql", "relational", "enterprise", "plsql",
        "rac", "exadata", "database", "commercial"
    ];

    /// <summary>
    /// Initializes a new instance of <see cref="OracleConnectionStrategy"/>.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public OracleConnectionStrategy(ILogger? logger = null) : base(logger) { }

    /// <inheritdoc/>
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var connectionString = config.ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string is required for Oracle Database connection.");

        // Parse connection string to extract host and port
        var (host, port) = ParseConnectionString(connectionString);

        // Test TCP connectivity
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(host, port, ct);

        // Store connection metadata
        var connectionInfo = new Dictionary<string, object>
        {
            ["Provider"] = "TCP/Oracle",
            ["Host"] = host,
            ["Port"] = port,
            ["ConnectionString"] = connectionString,
            ["State"] = "Connected"
        };

        var mockConnection = new OracleTcpConnection(host, port, connectionString);

        return new DefaultConnectionHandle(mockConnection, connectionInfo);
    }

    /// <inheritdoc/>
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        try
        {
            var mockConnection = handle.GetConnection<OracleTcpConnection>();

            // Test TCP connectivity
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(mockConnection.Host, mockConnection.Port, ct);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        if (handle is DefaultConnectionHandle defaultHandle)
        {
            defaultHandle.MarkDisconnected();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var mockConnection = handle.GetConnection<OracleTcpConnection>();

            // Test TCP connectivity and measure latency
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(mockConnection.Host, mockConnection.Port, ct);
            sw.Stop();

            return new ConnectionHealth(
                IsHealthy: true,
                StatusMessage: $"Oracle Database TCP connection active - {mockConnection.Host}:{mockConnection.Port}",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectionHealth(
                IsHealthy: false,
                StatusMessage: $"TCP health check failed: {ex.Message}",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Query execution requires Oracle.ManagedDataAccess or Oracle.ManagedDataAccess.Core NuGet package.
    /// This strategy only provides TCP connectivity validation.
    /// Returns empty result set with operation status information.
    /// </remarks>
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
        IConnectionHandle handle,
        string query,
        Dictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        var result = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["__status"] = "OPERATION_NOT_SUPPORTED",
                ["__message"] = "Query execution requires Oracle.ManagedDataAccess.Core NuGet package. This strategy provides TCP connectivity validation only.",
                ["__strategy"] = StrategyId,
                ["__capabilities"] = "connectivity_test,health_check"
            }
        };
        return Task.FromResult<IReadOnlyList<Dictionary<string, object?>>>(result);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Non-query execution requires Oracle.ManagedDataAccess or Oracle.ManagedDataAccess.Core NuGet package.
    /// This strategy only provides TCP connectivity validation.
    /// Returns -1 to indicate operation not supported.
    /// </remarks>
    public override Task<int> ExecuteNonQueryAsync(
        IConnectionHandle handle,
        string command,
        Dictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        // Return -1 to indicate operation not supported (graceful degradation)
        return Task.FromResult(-1);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Schema retrieval requires Oracle.ManagedDataAccess or Oracle.ManagedDataAccess.Core NuGet package.
    /// This strategy only provides TCP connectivity validation.
    /// Returns empty schema list.
    /// </remarks>
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(
        IConnectionHandle handle,
        CancellationToken ct = default)
    {
        // Return empty schema list (graceful degradation)
        return Task.FromResult<IReadOnlyList<DataSchema>>(Array.Empty<DataSchema>());
    }

    /// <inheritdoc/>
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(
        ConnectionConfig config, CancellationToken ct = default)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.ConnectionString))
        {
            errors.Add("ConnectionString is required for Oracle Database connection.");
        }
        else
        {
            try
            {
                var (host, port) = ParseConnectionString(config.ConnectionString);

                if (string.IsNullOrWhiteSpace(host))
                    errors.Add("Host/Data Source is required in the connection string.");

                if (port <= 0 || port > 65535)
                    errors.Add("Port must be between 1 and 65535.");
            }
            catch (Exception ex)
            {
                errors.Add($"Invalid Oracle connection string format: {ex.Message}");
            }
        }

        if (config.Timeout <= TimeSpan.Zero)
            errors.Add("Timeout must be a positive duration.");

        if (config.MaxRetries < 0)
            errors.Add("MaxRetries must be non-negative.");

        return Task.FromResult((errors.Count == 0, errors.ToArray()));
    }

    /// <summary>
    /// Parses an Oracle connection string to extract host and port.
    /// Supports both Easy Connect and TNS formats.
    /// </summary>
    /// <param name="connectionString">Oracle connection string.</param>
    /// <returns>Tuple containing host and port.</returns>
    private static (string Host, int Port) ParseConnectionString(string connectionString)
    {
        string? host = null;
        int port = DefaultOraclePort;

        // Try parsing Easy Connect format: Host:Port/ServiceName
        if (connectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase))
        {
            var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var keyValue = part.Split('=', 2, StringSplitOptions.TrimEntries);
                if (keyValue.Length == 2 && keyValue[0].Equals("Data Source", StringComparison.OrdinalIgnoreCase))
                {
                    var dataSource = keyValue[1];

                    // Parse Easy Connect: host:port/service or host/service
                    if (dataSource.Contains(':'))
                    {
                        var hostPort = dataSource.Split('/', 2)[0];
                        var hostPortParts = hostPort.Split(':', 2);
                        host = hostPortParts[0];
                        if (hostPortParts.Length > 1 && int.TryParse(hostPortParts[1], out var parsedPort))
                            port = parsedPort;
                    }
                    else
                    {
                        host = dataSource.Split('/', 2)[0];
                    }
                    break;
                }
            }
        }
        else
        {
            // Simple format
            var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var keyValue = part.Split('=', 2, StringSplitOptions.TrimEntries);
                if (keyValue.Length != 2) continue;

                var key = keyValue[0].ToLowerInvariant();
                var value = keyValue[1];

                switch (key)
                {
                    case "host":
                    case "server":
                        host = value;
                        break;
                    case "port":
                        if (int.TryParse(value, out var parsedPort))
                            port = parsedPort;
                        break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Host/Data Source not found in connection string.");

        return (host, port);
    }

    /// <summary>
    /// Mock connection object for TCP-based Oracle connectivity.
    /// </summary>
    private sealed class OracleTcpConnection
    {
        public string Host { get; }
        public int Port { get; }
        public string ConnectionString { get; }

        public OracleTcpConnection(string host, int port, string connectionString)
        {
            Host = host;
            Port = port;
            ConnectionString = connectionString;
        }
    }
}
