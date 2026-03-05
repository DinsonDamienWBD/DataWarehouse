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
/// MariaDB connection strategy using TCP connectivity check.
/// Provides connection validation via TCP socket to MariaDB server (default port 3306).
/// MariaDB is MySQL-compatible and uses the same wire protocol.
/// Note: Full query operations require MySqlConnector NuGet package.
/// </summary>
public sealed class MariaDbConnectionStrategy : DatabaseConnectionStrategyBase
{
    private const int DefaultMariaDbPort = 3306;

    /// <inheritdoc/>
    public override string StrategyId => "mariadb";

    /// <inheritdoc/>
    public override string DisplayName => "MariaDB";

    /// <inheritdoc/>
    public override ConnectionStrategyCapabilities Capabilities => new();

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "MariaDB relational database connection. MySQL-compatible open-source RDBMS with enhanced performance, " +
        "Galera Cluster for multi-master replication, columnar storage engine (ColumnStore), and improved query optimizer. " +
        "Fully compatible with MySQL applications and supports additional storage engines.";

    /// <inheritdoc/>
    public override string[] Tags =>
    [
        "mariadb", "mysql-compatible", "sql", "relational", "open-source",
        "galera", "columnstore", "database", "mysql-fork"
    ];

    /// <summary>
    /// Initializes a new instance of <see cref="MariaDbConnectionStrategy"/>.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public MariaDbConnectionStrategy(ILogger? logger = null) : base(logger) { }

    /// <inheritdoc/>
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var connectionString = config.ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string is required for MariaDB connection.");

        // Parse connection string to extract host and port
        var (host, port) = ParseConnectionString(connectionString);

        var tcpConnection = new MariaDbTcpConnection(host, port, connectionString);

        // Test TCP connectivity (caches the TcpClient for reuse by health checks)
        var connected = await tcpConnection.TestConnectivityAsync(ct);
        if (!connected) throw new InvalidOperationException($"Unable to connect to MariaDB at {host}:{port}");

        // Store connection metadata
        var connectionInfo = new Dictionary<string, object>
        {
            ["Provider"] = "TCP/MariaDB",
            ["Host"] = host,
            ["Port"] = port,
            ["ConnectionString"] = connectionString,
            ["State"] = "Connected"
        };

        return new DefaultConnectionHandle(tcpConnection, connectionInfo);
    }

    /// <inheritdoc/>
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        try
        {
            var tcpConnection = handle.GetConnection<MariaDbTcpConnection>();
            return await tcpConnection.TestConnectivityAsync(ct);
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
            var tcpConnection = handle.GetConnection<MariaDbTcpConnection>();
            // Finding 1903: Reuse cached connection instead of creating TcpClient per check.
            var isHealthy = await tcpConnection.TestConnectivityAsync(ct);
            sw.Stop();

            return new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy
                    ? $"MariaDB TCP connection active - {tcpConnection.Host}:{tcpConnection.Port}"
                    : $"MariaDB TCP connection failed - {tcpConnection.Host}:{tcpConnection.Port}",
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
    /// Query execution requires MySqlConnector NuGet package (MariaDB is MySQL wire-compatible).
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
    /// Non-query execution requires MySqlConnector NuGet package (MariaDB is MySQL wire-compatible).
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
    /// Schema retrieval requires MySqlConnector NuGet package (MariaDB is MySQL wire-compatible).
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
            errors.Add("ConnectionString is required for MariaDB connection.");
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
                errors.Add($"Invalid MariaDB connection string format: {ex.Message}");
            }
        }

        if (config.Timeout <= TimeSpan.Zero)
            errors.Add("Timeout must be a positive duration.");

        if (config.MaxRetries < 0)
            errors.Add("MaxRetries must be non-negative.");

        return Task.FromResult((errors.Count == 0, errors.ToArray()));
    }

    /// <summary>
    /// Parses a MariaDB connection string to extract host and port.
    /// </summary>
    /// <param name="connectionString">MariaDB connection string.</param>
    /// <returns>Tuple containing host and port.</returns>
    private static (string Host, int Port) ParseConnectionString(string connectionString)
    {
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        string? host = null;
        int port = DefaultMariaDbPort;

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
    /// Mock connection object for TCP-based MariaDB connectivity.
    /// </summary>
    private sealed class MariaDbTcpConnection : IDisposable
    {
        public string Host { get; }
        public int Port { get; }
        public string ConnectionString { get; }
        // Finding 1903: Cache a reusable TcpClient rather than creating one per health check.
        private TcpClient? _cachedClient;
        private readonly object _lock = new();

        public MariaDbTcpConnection(string host, int port, string connectionString)
        {
            Host = host;
            Port = port;
            ConnectionString = connectionString;
        }

        /// <summary>
        /// Tests connectivity, reusing the cached TcpClient if still connected.
        /// Creates a new connection only when the existing one is disconnected or absent.
        /// </summary>
        public async Task<bool> TestConnectivityAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                if (_cachedClient?.Connected == true) return true;
                _cachedClient?.Dispose();
                _cachedClient = null;
            }
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(Host, Port, ct);
                lock (_lock) { _cachedClient?.Dispose(); _cachedClient = client; }
                return true;
            }
            catch
            {
                client.Dispose();
                return false;
            }
        }

        public void Dispose()
        {
            lock (_lock) { _cachedClient?.Dispose(); _cachedClient = null; }
        }
    }
}
