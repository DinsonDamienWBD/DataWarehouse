using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Connectors
{
    /// <summary>
    /// Capabilities of a connection strategy.
    /// </summary>
    public record ConnectionStrategyCapabilities(
        /// <summary>Whether the strategy supports connection pooling.</summary>
        bool SupportsPooling = false,
        /// <summary>Whether the strategy supports streaming data transfer.</summary>
        bool SupportsStreaming = false,
        /// <summary>Whether the strategy supports transactional operations.</summary>
        bool SupportsTransactions = false,
        /// <summary>Whether the strategy supports bulk/batch operations.</summary>
        bool SupportsBulkOperations = false,
        /// <summary>Whether the strategy supports change data capture.</summary>
        bool SupportsChangeDataCapture = false,
        /// <summary>Whether the strategy supports schema discovery.</summary>
        bool SupportsSchemaDiscovery = false,
        /// <summary>Whether the strategy supports SSL/TLS encryption.</summary>
        bool SupportsSsl = false,
        /// <summary>Whether the strategy supports data compression.</summary>
        bool SupportsCompression = false,
        /// <summary>Whether the strategy supports authentication.</summary>
        bool SupportsAuthentication = true,
        /// <summary>Whether the strategy supports health check operations.</summary>
        bool SupportsHealthCheck = true,
        /// <summary>Whether the strategy requires authentication to connect.</summary>
        bool RequiresAuthentication = false,
        /// <summary>Whether the strategy supports automatic reconnection.</summary>
        bool SupportsReconnection = false,
        /// <summary>Whether the strategy supports connection testing/ping.</summary>
        bool SupportsConnectionTesting = true,
        /// <summary>Maximum number of concurrent connections supported.</summary>
        int MaxConcurrentConnections = 100,
        /// <summary>Supported authentication methods (bearer, basic, apikey, oauth2, none).</summary>
        string[]? SupportedAuthMethods = null
    );

    /// <summary>
    /// Configuration for establishing a connection via a strategy.
    /// </summary>
    public record ConnectionConfig
    {
        /// <summary>Connection string or base URL.</summary>
        public required string ConnectionString { get; init; }
        /// <summary>Additional properties (auth tokens, headers, etc.).</summary>
        public Dictionary<string, string> Properties { get; init; } = new();
        /// <summary>Connection timeout.</summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
        /// <summary>Max retry attempts.</summary>
        public int MaxRetries { get; init; } = 3;
        /// <summary>Whether to use SSL/TLS.</summary>
        public bool UseSsl { get; init; } = true;
        /// <summary>Authentication method (bearer, basic, apikey, oauth2, none).</summary>
        public string AuthMethod { get; init; } = "none";
        /// <summary>Authentication credential (token, password, key).</summary>
        public string? AuthCredential { get; init; }
        /// <summary>Optional secondary credential (username for basic auth, org for OAuth).</summary>
        public string? AuthSecondary { get; init; }
        /// <summary>Connection pool size.</summary>
        public int PoolSize { get; init; } = 10;
    }

    /// <summary>
    /// Represents an active connection handle returned by a strategy.
    /// </summary>
    public interface IConnectionHandle : IAsyncDisposable
    {
        /// <summary>Unique ID for this connection instance.</summary>
        string ConnectionId { get; }
        /// <summary>Whether the connection is currently open.</summary>
        bool IsConnected { get; }
        /// <summary>The underlying connection object (HttpClient, DbConnection, etc.).</summary>
        object UnderlyingConnection { get; }
        /// <summary>Gets the underlying connection as a specific type.</summary>
        T GetConnection<T>();
        /// <summary>Metadata about the connection (server version, etc.).</summary>
        Dictionary<string, object> ConnectionInfo { get; }
    }

    /// <summary>
    /// Health status of a connection.
    /// </summary>
    public record ConnectionHealth(
        /// <summary>Whether the connection is healthy.</summary>
        bool IsHealthy,
        /// <summary>Human-readable status message.</summary>
        string StatusMessage,
        /// <summary>Latency of the health check.</summary>
        TimeSpan Latency,
        /// <summary>When the health check was performed.</summary>
        DateTimeOffset CheckedAt,
        /// <summary>Optional detailed health information.</summary>
        Dictionary<string, object>? Details = null
    );

    /// <summary>
    /// Core interface for all connection strategies in the UltimateConnector plugin.
    /// Each strategy handles a single type of external system connection.
    /// </summary>
    public interface IConnectionStrategy
    {
        /// <summary>Unique identifier for this strategy (e.g., "postgresql", "kafka", "openai").</summary>
        string StrategyId { get; }
        /// <summary>Human-readable display name.</summary>
        string DisplayName { get; }
        /// <summary>Category of connections this strategy handles.</summary>
        ConnectorCategory Category { get; }
        /// <summary>Capabilities of this strategy.</summary>
        ConnectionStrategyCapabilities Capabilities { get; }
        /// <summary>Semantic description for AI discovery.</summary>
        string SemanticDescription { get; }
        /// <summary>Tags for categorization and search.</summary>
        string[] Tags { get; }

        /// <summary>Establishes a connection to the external system.</summary>
        /// <param name="config">Connection configuration.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A connection handle for the established connection.</returns>
        Task<IConnectionHandle> ConnectAsync(ConnectionConfig config, CancellationToken ct = default);

        /// <summary>Tests an existing connection.</summary>
        /// <param name="handle">The connection handle to test.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if the connection is alive and responsive.</returns>
        Task<bool> TestConnectionAsync(IConnectionHandle handle, CancellationToken ct = default);

        /// <summary>Disconnects and cleans up.</summary>
        /// <param name="handle">The connection handle to disconnect.</param>
        /// <param name="ct">Cancellation token.</param>
        Task DisconnectAsync(IConnectionHandle handle, CancellationToken ct = default);

        /// <summary>Gets health status of a connection.</summary>
        /// <param name="handle">The connection handle to check.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Health status of the connection.</returns>
        Task<ConnectionHealth> GetHealthAsync(IConnectionHandle handle, CancellationToken ct = default);

        /// <summary>Validates a config before connecting.</summary>
        /// <param name="config">The configuration to validate.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Tuple indicating validity and any error messages.</returns>
        Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
    }
}
