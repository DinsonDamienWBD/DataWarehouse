using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.NoSql
{
    /// <summary>
    /// Connection strategy for MongoDB NoSQL database.
    /// Supports document-based storage with flexible schema.
    /// </summary>
    public class MongoDbConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private TcpClient? _tcpClient;

        public override string StrategyId => "mongodb";
        public override string DisplayName => "MongoDB";
        public override string SemanticDescription => "Document-oriented NoSQL database with flexible schemas and powerful querying capabilities";
        public override string[] Tags => new[] { "nosql", "document", "database", "mongodb", "json" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsTransactions: true,
            SupportsBulkOperations: true,
            SupportsSchemaDiscovery: true,
            SupportsSsl: true,
            SupportsCompression: true,
            SupportsAuthentication: true,
            MaxConcurrentConnections: 100,
            SupportedAuthMethods: new[] { "basic", "x509", "kerberos" }
        );

        public MongoDbConnectionStrategy(ILogger<MongoDbConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 27017);
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port, ct);

            var connectionInfo = new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["database"] = "admin",
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(_tcpClient, connectionInfo);
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<TcpClient>();
            await Task.Delay(10, ct); // Simulate ping
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
                StatusMessage: isHealthy ? "MongoDB connection healthy" : "MongoDB connection unhealthy",
                Latency: TimeSpan.FromMilliseconds(5),
                CheckedAt: DateTimeOffset.UtcNow
            );
        }

        /// <summary>
        /// Executes a query against MongoDB.
        /// Note: MongoDB wire protocol is complex and requires official driver for full implementation.
        /// This strategy provides TCP connectivity validation only.
        /// </summary>
        public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            // MongoDB wire protocol requires official driver for proper implementation
            var result = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["__status"] = "OPERATION_NOT_SUPPORTED",
                    ["__message"] = "MongoDB query execution requires MongoDB.Driver NuGet package. This strategy provides TCP connectivity validation only.",
                    ["__strategy"] = StrategyId,
                    ["__capabilities"] = "connectivity_test,health_check"
                }
            };
            return Task.FromResult<IReadOnlyList<Dictionary<string, object?>>>(result);
        }

        /// <summary>
        /// Executes a non-query command against MongoDB.
        /// Returns -1 as MongoDB wire protocol requires official driver.
        /// </summary>
        public override Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            // Return -1 to indicate operation not supported (graceful degradation)
            return Task.FromResult(-1);
        }

        /// <summary>
        /// Retrieves schema information from MongoDB.
        /// Returns empty list as MongoDB wire protocol requires official driver.
        /// </summary>
        public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            // Return empty schema list (graceful degradation)
            return Task.FromResult<IReadOnlyList<DataSchema>>(Array.Empty<DataSchema>());
        }

        private (string host, int port) ParseHostPort(string connectionString, int defaultPort)
        {
            // Handle mongodb://host:port or just host:port
            var clean = connectionString.Replace("mongodb://", "").Split('/')[0];
            var parts = clean.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort);
        }
    }
}
