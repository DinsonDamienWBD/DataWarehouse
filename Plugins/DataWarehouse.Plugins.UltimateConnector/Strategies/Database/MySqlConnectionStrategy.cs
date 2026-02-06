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
/// MySQL connection strategy using TCP connectivity check.
/// Provides connection validation via TCP socket to MySQL server (default port 3306).
/// Note: Full query operations require MySql.Data or MySqlConnector NuGet package.
/// </summary>
public sealed class MySqlConnectionStrategy : DatabaseConnectionStrategyBase
{
    private const int DefaultMySqlPort = 3306;

    /// <inheritdoc/>
    public override string StrategyId => "mysql";

    /// <inheritdoc/>
    public override string DisplayName => "MySQL";

    /// <inheritdoc/>
    public override ConnectionStrategyCapabilities Capabilities => new();

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "MySQL relational database connection. Open-source RDBMS with support for ACID transactions, " +
        "replication, partitioning, stored procedures, and full-text search. Widely used for web applications, " +
        "e-commerce, and content management systems. Compatible with MySQL 5.7+ and MySQL 8.x.";

    /// <inheritdoc/>
    public override string[] Tags =>
    [
        "mysql", "sql", "relational", "open-source", "mariadb-compatible",
        "web", "lamp", "database", "oracle"
    ];

    /// <summary>
    /// Initializes a new instance of <see cref="MySqlConnectionStrategy"/>.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public MySqlConnectionStrategy(ILogger? logger = null) : base(logger) { }

    /// <inheritdoc/>
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var connectionString = config.ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string is required for MySQL connection.");

        // Parse connection string to extract host and port
        var (host, port) = ParseConnectionString(connectionString);

        // Test TCP connectivity
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(host, port, ct);

        // Store connection metadata
        var connectionInfo = new Dictionary<string, object>
        {
            ["Provider"] = "TCP/MySQL",
            ["Host"] = host,
            ["Port"] = port,
            ["ConnectionString"] = connectionString,
            ["State"] = "Connected"
        };

        // Use connection string as the underlying connection for TCP-based validation
        var mockConnection = new MySqlTcpConnection(host, port, connectionString);

        return new DefaultConnectionHandle(mockConnection, connectionInfo);
    }

    /// <inheritdoc/>
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        try
        {
            var mockConnection = handle.GetConnection<MySqlTcpConnection>();

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
            var mockConnection = handle.GetConnection<MySqlTcpConnection>();

            // Test TCP connectivity and measure latency
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(mockConnection.Host, mockConnection.Port, ct);
            sw.Stop();

            return new ConnectionHealth(
                IsHealthy: true,
                StatusMessage: $"MySQL TCP connection active - {mockConnection.Host}:{mockConnection.Port}",
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
    /// Query execution requires MySql.Data or MySqlConnector NuGet package.
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
                ["__message"] = "Query execution requires MySqlConnector NuGet package. This strategy provides TCP connectivity validation only.",
                ["__strategy"] = StrategyId,
                ["__capabilities"] = "connectivity_test,health_check"
            }
        };
        return Task.FromResult<IReadOnlyList<Dictionary<string, object?>>>(result);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Non-query execution requires MySql.Data or MySqlConnector NuGet package.
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
    /// Schema retrieval requires MySql.Data or MySqlConnector NuGet package.
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
            errors.Add("ConnectionString is required for MySQL connection.");
        }
        else
        {
            try
            {
                var (host, port) = ParseConnectionString(config.ConnectionString);

                if (string.IsNullOrWhiteSpace(host))
                    errors.Add("Host/Server is required in the connection string.");

                if (port <= 0 || port > 65535)
                    errors.Add("Port must be between 1 and 65535.");
            }
            catch (Exception ex)
            {
                errors.Add($"Invalid MySQL connection string format: {ex.Message}");
            }
        }

        if (config.Timeout <= TimeSpan.Zero)
            errors.Add("Timeout must be a positive duration.");

        if (config.MaxRetries < 0)
            errors.Add("MaxRetries must be non-negative.");

        return Task.FromResult((errors.Count == 0, errors.ToArray()));
    }

    /// <summary>
    /// Parses a MySQL connection string to extract host and port.
    /// </summary>
    /// <param name="connectionString">MySQL connection string.</param>
    /// <returns>Tuple containing host and port.</returns>
    private static (string Host, int Port) ParseConnectionString(string connectionString)
    {
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        string? host = null;
        int port = DefaultMySqlPort;

        foreach (var part in parts)
        {
            var keyValue = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (keyValue.Length != 2) continue;

            var key = keyValue[0].ToLowerInvariant();
            var value = keyValue[1];

            switch (key)
            {
                case "server":
                case "host":
                case "data source":
                    host = value;
                    break;
                case "port":
                    if (int.TryParse(value, out var parsedPort))
                        port = parsedPort;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Host/Server not found in connection string.");

        return (host, port);
    }

    /// <summary>
    /// Mock connection object for TCP-based MySQL connectivity.
    /// </summary>
    private sealed class MySqlTcpConnection
    {
        public string Host { get; }
        public int Port { get; }
        public string ConnectionString { get; }

        public MySqlTcpConnection(string host, int port, string connectionString)
        {
            Host = host;
            Port = port;
            ConnectionString = connectionString;
        }
    }
}
