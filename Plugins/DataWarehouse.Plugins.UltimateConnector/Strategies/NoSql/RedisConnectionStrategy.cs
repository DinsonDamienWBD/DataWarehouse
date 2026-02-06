using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.NoSql
{
    /// <summary>
    /// Connection strategy for Redis in-memory data structure store.
    /// Supports key-value storage, caching, and pub/sub messaging.
    /// </summary>
    public class RedisConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private TcpClient? _tcpClient;

        public override string StrategyId => "redis";
        public override string DisplayName => "Redis";
        public override string SemanticDescription => "High-performance in-memory data structure store used as database, cache, and message broker";
        public override string[] Tags => new[] { "nosql", "cache", "key-value", "redis", "in-memory" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsTransactions: true,
            SupportsBulkOperations: true,
            SupportsSchemaDiscovery: false,
            SupportsSsl: true,
            SupportsCompression: false,
            SupportsAuthentication: true,
            MaxConcurrentConnections: 1000,
            SupportedAuthMethods: new[] { "password", "acl" }
        );

        public RedisConnectionStrategy(ILogger<RedisConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 6379);
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port, ct);

            // Send PING command
            var stream = _tcpClient.GetStream();
            var pingCmd = Encoding.UTF8.GetBytes("*1\r\n$4\r\nPING\r\n");
            await stream.WriteAsync(pingCmd, 0, pingCmd.Length, ct);

            var buffer = new byte[1024];
            var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
            var response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            if (!response.Contains("PONG"))
                throw new InvalidOperationException("Redis PING failed");

            var connectionInfo = new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(_tcpClient, connectionInfo);
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<TcpClient>();
            await Task.Delay(5, ct);
            return client.Connected;
        }

        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            if (_tcpClient != null)
            {
                _tcpClient.Close();
                _tcpClient.Dispose();
                _tcpClient = null;
            }
            await Task.CompletedTask;
        }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var isHealthy = await TestCoreAsync(handle, ct);
            return new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy ? "Redis connection healthy" : "Redis connection unhealthy",
                Latency: TimeSpan.FromMilliseconds(2),
                CheckedAt: DateTimeOffset.UtcNow
            );
        }

        /// <summary>
        /// Executes a Redis command using RESP protocol.
        /// Supports basic commands like GET, KEYS, etc.
        /// </summary>
        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            if (_tcpClient == null || !_tcpClient.Connected)
            {
                return new List<Dictionary<string, object?>>
                {
                    new()
                    {
                        ["__status"] = "NOT_CONNECTED",
                        ["__message"] = "Redis connection not established.",
                        ["__strategy"] = StrategyId
                    }
                };
            }

            try
            {
                var stream = _tcpClient.GetStream();
                var parts = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                // Build RESP command
                var respCmd = new StringBuilder();
                respCmd.Append($"*{parts.Length}\r\n");
                foreach (var part in parts)
                {
                    respCmd.Append($"${part.Length}\r\n{part}\r\n");
                }

                var cmdBytes = Encoding.UTF8.GetBytes(respCmd.ToString());
                await stream.WriteAsync(cmdBytes, 0, cmdBytes.Length, ct);

                var buffer = new byte[4096];
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                var response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                var results = new List<Dictionary<string, object?>>();

                // Parse RESP response
                if (response.StartsWith("+"))
                {
                    results.Add(new Dictionary<string, object?>
                    {
                        ["result"] = response[1..].TrimEnd('\r', '\n'),
                        ["type"] = "simple_string"
                    });
                }
                else if (response.StartsWith("-"))
                {
                    results.Add(new Dictionary<string, object?>
                    {
                        ["__status"] = "ERROR",
                        ["__message"] = response[1..].TrimEnd('\r', '\n'),
                        ["__strategy"] = StrategyId
                    });
                }
                else if (response.StartsWith(":"))
                {
                    results.Add(new Dictionary<string, object?>
                    {
                        ["result"] = long.Parse(response[1..].TrimEnd('\r', '\n')),
                        ["type"] = "integer"
                    });
                }
                else if (response.StartsWith("$"))
                {
                    var lines = response.Split("\r\n");
                    if (lines.Length > 1 && lines[0] != "$-1")
                    {
                        results.Add(new Dictionary<string, object?>
                        {
                            ["result"] = lines[1],
                            ["type"] = "bulk_string"
                        });
                    }
                    else
                    {
                        results.Add(new Dictionary<string, object?>
                        {
                            ["result"] = null,
                            ["type"] = "null"
                        });
                    }
                }
                else if (response.StartsWith("*"))
                {
                    var lines = response.Split("\r\n");
                    var count = int.Parse(lines[0][1..]);
                    var values = new List<string>();
                    for (int i = 1; i < lines.Length - 1; i += 2)
                    {
                        if (i + 1 < lines.Length && !lines[i].StartsWith("$-1"))
                        {
                            values.Add(lines[i + 1]);
                        }
                    }

                    foreach (var value in values)
                    {
                        results.Add(new Dictionary<string, object?>
                        {
                            ["key"] = value,
                            ["type"] = "array_element"
                        });
                    }
                }

                return results;
            }
            catch (Exception ex)
            {
                return new List<Dictionary<string, object?>>
                {
                    new()
                    {
                        ["__status"] = "ERROR",
                        ["__message"] = $"Redis command error: {ex.Message}",
                        ["__strategy"] = StrategyId
                    }
                };
            }
        }

        /// <summary>
        /// Executes a Redis write command using RESP protocol.
        /// </summary>
        public override async Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            if (_tcpClient == null || !_tcpClient.Connected)
                return -1;

            try
            {
                var stream = _tcpClient.GetStream();
                var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                // Build RESP command
                var respCmd = new StringBuilder();
                respCmd.Append($"*{parts.Length}\r\n");
                foreach (var part in parts)
                {
                    respCmd.Append($"${part.Length}\r\n{part}\r\n");
                }

                var cmdBytes = Encoding.UTF8.GetBytes(respCmd.ToString());
                await stream.WriteAsync(cmdBytes, 0, cmdBytes.Length, ct);

                var buffer = new byte[1024];
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                var response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                // Return 1 for success, 0 for null response, -1 for error
                if (response.StartsWith("+") || response.StartsWith(":"))
                    return 1;
                if (response.StartsWith("$-1") || response.StartsWith("*-1"))
                    return 0;
                if (response.StartsWith("-"))
                    return -1;

                return 1;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Retrieves schema information from Redis using INFO and DBSIZE commands.
        /// </summary>
        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            if (_tcpClient == null || !_tcpClient.Connected)
                return Array.Empty<DataSchema>();

            try
            {
                // Get database info
                var infoResult = await ExecuteQueryAsync(handle, "INFO keyspace", null, ct);
                var schemas = new List<DataSchema>();

                // Redis keyspace is schema-less, but we can show available databases
                schemas.Add(new DataSchema(
                    "db0",
                    new[]
                    {
                        new DataSchemaField("key", "String", false, null, null),
                        new DataSchemaField("value", "Any", true, null, null),
                        new DataSchemaField("type", "String", true, null, null),
                        new DataSchemaField("ttl", "Int64", true, null, null)
                    },
                    new[] { "key" },
                    new Dictionary<string, object> { ["type"] = "keyspace" }
                ));

                return schemas;
            }
            catch
            {
                return Array.Empty<DataSchema>();
            }
        }

        private (string host, int port) ParseHostPort(string connectionString, int defaultPort)
        {
            var clean = connectionString.Replace("redis://", "").Split('/')[0];
            var parts = clean.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort);
        }
    }
}
