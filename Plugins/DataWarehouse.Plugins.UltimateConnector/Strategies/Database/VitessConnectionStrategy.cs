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
/// Vitess connection strategy using TCP connectivity check.
/// Provides connection validation via TCP socket to Vitess MySQL-compatible server.
/// Note: Full query operations require MySqlConnector NuGet package.
/// </summary>
public sealed class VitessConnectionStrategy : DatabaseConnectionStrategyBase
{
    private const int DefaultVitessPort = 3306;

    /// <inheritdoc/>
    public override string StrategyId => "vitess";

    /// <inheritdoc/>
    public override string DisplayName => "Vitess";

    /// <inheritdoc/>
    public override ConnectionStrategyCapabilities Capabilities => new();

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Vitess distributed MySQL database connection. Cloud-native database clustering system for horizontal " +
        "scaling of MySQL. Provides automatic sharding, connection pooling, query rewriting, and transparent " +
        "failover. Used by YouTube, Slack, and GitHub for massive-scale MySQL deployments.";

    /// <inheritdoc/>
    public override string[] Tags =>
    [
        "vitess", "mysql-compatible", "distributed", "sharding", "cloud-native",
        "scalable", "youtube", "database", "clustering"
    ];

    /// <summary>
    /// Initializes a new instance of <see cref="VitessConnectionStrategy"/>.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public VitessConnectionStrategy(ILogger? logger = null) : base(logger) { }

    /// <inheritdoc/>
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var connectionString = config.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string is required for Vitess connection.");

        var (host, port) = ParseConnectionString(connectionString);

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(host, port, ct);

        var connectionInfo = new Dictionary<string, object>
        {
            ["Provider"] = "TCP/Vitess",
            ["Host"] = host,
            ["Port"] = port,
            ["ConnectionString"] = connectionString,
            ["State"] = "Connected"
        };

        var tcpConnection = new VitessTcpConnection(host, port, connectionString);
        return new DefaultConnectionHandle(tcpConnection, connectionInfo);
    }

    /// <inheritdoc/>
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        try
        {
            var tcpConnection = handle.GetConnection<VitessTcpConnection>();
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(tcpConnection.Host, tcpConnection.Port, ct);
            return true;
        }
        catch { return false; /* Connection validation - failure acceptable */ }
    }

    /// <inheritdoc/>
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        if (handle is DefaultConnectionHandle defaultHandle) defaultHandle.MarkDisconnected();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var tcpConnection = handle.GetConnection<VitessTcpConnection>();
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(tcpConnection.Host, tcpConnection.Port, ct);
            sw.Stop();

            return new ConnectionHealth(true, $"Vitess TCP connection active - {tcpConnection.Host}:{tcpConnection.Port}", sw.Elapsed, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectionHealth(false, $"TCP health check failed: {ex.Message}", sw.Elapsed, DateTimeOffset.UtcNow);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Query execution requires MySqlConnector NuGet package (Vitess is MySQL wire-compatible).
    /// This strategy only provides TCP connectivity validation.
    /// Returns empty result set with operation status information.
    /// </remarks>
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
        IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
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
    /// Non-query execution requires MySqlConnector NuGet package (Vitess is MySQL wire-compatible).
    /// This strategy only provides TCP connectivity validation.
    /// Returns -1 to indicate operation not supported.
    /// </remarks>
    public override Task<int> ExecuteNonQueryAsync(
        IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        // Return -1 to indicate operation not supported (graceful degradation)
        return Task.FromResult(-1);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Schema retrieval requires MySqlConnector NuGet package (Vitess is MySQL wire-compatible).
    /// This strategy only provides TCP connectivity validation.
    /// Returns empty schema list.
    /// </remarks>
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
    {
        // Return empty schema list (graceful degradation)
        return Task.FromResult<IReadOnlyList<DataSchema>>(Array.Empty<DataSchema>());
    }

    /// <inheritdoc/>
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.ConnectionString))
        {
            errors.Add("ConnectionString is required for Vitess connection.");
        }
        else
        {
            try
            {
                var (host, port) = ParseConnectionString(config.ConnectionString);
                if (string.IsNullOrWhiteSpace(host)) errors.Add("Host/Server is required.");
                if (port <= 0 || port > 65535) errors.Add("Port must be between 1 and 65535.");
            }
            catch (Exception ex) { errors.Add($"Invalid connection string: {ex.Message}"); }
        }

        if (config.Timeout <= TimeSpan.Zero) errors.Add("Timeout must be positive.");
        if (config.MaxRetries < 0) errors.Add("MaxRetries must be non-negative.");

        return Task.FromResult((errors.Count == 0, errors.ToArray()));
    }

    private static (string Host, int Port) ParseConnectionString(string connectionString)
    {
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        string? host = null;
        int port = DefaultVitessPort;

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

    private sealed class VitessTcpConnection
    {
        public string Host { get; }
        public int Port { get; }
        public string ConnectionString { get; }

        public VitessTcpConnection(string host, int port, string connectionString)
        {
            Host = host;
            Port = port;
            ConnectionString = connectionString;
        }
    }
}
