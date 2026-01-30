using DataWarehouse.SDK.Connectors;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Abstract base class for data connector plugins.
    /// Provides common infrastructure for connecting to and interacting with external data sources.
    /// Implements connection lifecycle management, error handling, and capability enforcement.
    /// </summary>
    public abstract class DataConnectorPluginBase : FeaturePluginBase, IDataConnector
    {
        #region Properties

        /// <summary>
        /// Unique identifier for this connector instance.
        /// </summary>
        public abstract string ConnectorId { get; }

        /// <summary>
        /// Category of the data source this connector handles.
        /// </summary>
        public abstract ConnectorCategory ConnectorCategory { get; }

        /// <summary>
        /// Current connection state.
        /// </summary>
        public ConnectionState State { get; protected set; } = ConnectionState.Disconnected;

        /// <summary>
        /// Capabilities supported by this connector.
        /// </summary>
        public abstract ConnectorCapabilities Capabilities { get; }

        /// <summary>
        /// Current connection configuration (set after successful connection).
        /// </summary>
        protected ConnectorConfig? CurrentConfig { get; private set; }

        #endregion

        #region Abstract Methods (Must be implemented by derived classes)

        /// <summary>
        /// Establishes the actual connection to the data source.
        /// Derived classes implement the specific connection logic.
        /// </summary>
        /// <param name="config">Connection configuration.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Result indicating success or failure with server info.</returns>
        protected abstract Task<ConnectionResult> EstablishConnectionAsync(ConnectorConfig config, CancellationToken ct);

        /// <summary>
        /// Closes the connection to the data source.
        /// Derived classes implement cleanup logic.
        /// </summary>
        /// <returns>Task representing the close operation.</returns>
        protected abstract Task CloseConnectionAsync();

        /// <summary>
        /// Pings the data source to verify connectivity.
        /// </summary>
        /// <returns>True if connection is active and responsive.</returns>
        protected abstract Task<bool> PingAsync();

        /// <summary>
        /// Fetches the schema/structure from the data source.
        /// </summary>
        /// <returns>Schema information.</returns>
        protected abstract Task<DataSchema> FetchSchemaAsync();

        /// <summary>
        /// Executes a read query against the data source.
        /// </summary>
        /// <param name="query">Query parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Async enumerable of data records.</returns>
        protected abstract IAsyncEnumerable<DataRecord> ExecuteReadAsync(DataQuery query, CancellationToken ct);

        /// <summary>
        /// Executes a write operation against the data source.
        /// </summary>
        /// <param name="records">Records to write.</param>
        /// <param name="options">Write options.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Result of the write operation.</returns>
        protected abstract Task<WriteResult> ExecuteWriteAsync(IAsyncEnumerable<DataRecord> records, WriteOptions options, CancellationToken ct);

        #endregion

        #region Public Interface Implementation

        /// <summary>
        /// Connects to the data source with proper state management and error handling.
        /// </summary>
        /// <param name="config">Connection configuration.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Result indicating success or failure.</returns>
        public async Task<ConnectionResult> ConnectAsync(ConnectorConfig config, CancellationToken ct = default)
        {
            State = ConnectionState.Connecting;
            try
            {
                var result = await EstablishConnectionAsync(config, ct);
                State = result.Success ? ConnectionState.Connected : ConnectionState.Error;
                if (result.Success)
                {
                    CurrentConfig = config;
                }
                return result;
            }
            catch (Exception ex)
            {
                State = ConnectionState.Error;
                return new ConnectionResult(false, ex.Message, null);
            }
        }

        /// <summary>
        /// Disconnects from the data source and resets state.
        /// </summary>
        /// <returns>Task representing the disconnect operation.</returns>
        public async Task DisconnectAsync()
        {
            await CloseConnectionAsync();
            State = ConnectionState.Disconnected;
            CurrentConfig = null;
        }

        /// <summary>
        /// Tests connectivity to the data source.
        /// </summary>
        /// <returns>True if connection is active and responsive.</returns>
        public Task<bool> TestConnectionAsync() => PingAsync();

        /// <summary>
        /// Retrieves the schema/structure of the data source.
        /// </summary>
        /// <returns>Schema information.</returns>
        public Task<DataSchema> GetSchemaAsync() => FetchSchemaAsync();

        /// <summary>
        /// Reads data from the source, enforcing read capability.
        /// </summary>
        /// <param name="query">Query parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Async enumerable of data records.</returns>
        /// <exception cref="NotSupportedException">Thrown if connector doesn't support read operations.</exception>
        public IAsyncEnumerable<DataRecord> ReadAsync(DataQuery query, CancellationToken ct = default)
        {
            if (!Capabilities.HasFlag(ConnectorCapabilities.Read))
                throw new NotSupportedException("Connector does not support read operations");
            return ExecuteReadAsync(query, ct);
        }

        /// <summary>
        /// Writes data to the source, enforcing write capability.
        /// </summary>
        /// <param name="records">Records to write.</param>
        /// <param name="options">Write options.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Result of the write operation.</returns>
        /// <exception cref="NotSupportedException">Thrown if connector doesn't support write operations.</exception>
        public Task<WriteResult> WriteAsync(IAsyncEnumerable<DataRecord> records, WriteOptions options, CancellationToken ct = default)
        {
            if (!Capabilities.HasFlag(ConnectorCapabilities.Write))
                throw new NotSupportedException("Connector does not support write operations");
            return ExecuteWriteAsync(records, options, ct);
        }

        #endregion

        #region Metadata

        /// <summary>
        /// Gets metadata about this connector.
        /// </summary>
        /// <returns>Dictionary of metadata key-value pairs.</returns>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["ConnectorType"] = "DataConnector";
            metadata["ConnectorId"] = ConnectorId;
            metadata["ConnectorCategory"] = ConnectorCategory.ToString();
            metadata["ConnectionState"] = State.ToString();
            metadata["Capabilities"] = Capabilities.ToString();
            metadata["SupportsRead"] = Capabilities.HasFlag(ConnectorCapabilities.Read);
            metadata["SupportsWrite"] = Capabilities.HasFlag(ConnectorCapabilities.Write);
            metadata["SupportsSchema"] = Capabilities.HasFlag(ConnectorCapabilities.Schema);
            metadata["SupportsTransactions"] = Capabilities.HasFlag(ConnectorCapabilities.Transactions);
            metadata["SupportsStreaming"] = Capabilities.HasFlag(ConnectorCapabilities.Streaming);
            metadata["SupportsChangeTracking"] = Capabilities.HasFlag(ConnectorCapabilities.ChangeTracking);
            metadata["SupportsBulkOperations"] = Capabilities.HasFlag(ConnectorCapabilities.BulkOperations);
            return metadata;
        }

        #endregion
    }

    /// <summary>
    /// Abstract base class for database connector plugins.
    /// Provides specialized infrastructure for relational and NoSQL databases.
    /// Supports PostgreSQL, MySQL, MongoDB, Cassandra, Redis, and similar systems.
    /// </summary>
    public abstract class DatabaseConnectorPluginBase : DataConnectorPluginBase
    {
        /// <summary>
        /// Database connectors are always in the Database category.
        /// </summary>
        public override ConnectorCategory ConnectorCategory => Connectors.ConnectorCategory.Database;

        /// <summary>
        /// Standard database capabilities: Read, Write, Schema, Transactions.
        /// </summary>
        public override ConnectorCapabilities Capabilities =>
            ConnectorCapabilities.Read |
            ConnectorCapabilities.Write |
            ConnectorCapabilities.Schema |
            ConnectorCapabilities.Transactions;

        #region Database-Specific Methods

        /// <summary>
        /// Builds a SELECT query from query parameters.
        /// Override to implement database-specific query syntax.
        /// </summary>
        /// <param name="query">Query parameters.</param>
        /// <returns>SQL or database-specific query string.</returns>
        protected abstract string BuildSelectQuery(DataQuery query);

        /// <summary>
        /// Builds an INSERT statement for the specified table and columns.
        /// Override to implement database-specific INSERT syntax.
        /// </summary>
        /// <param name="table">Target table name.</param>
        /// <param name="columns">Column names to insert.</param>
        /// <returns>SQL or database-specific INSERT statement.</returns>
        protected abstract string BuildInsertStatement(string table, string[] columns);

        #endregion

        /// <summary>
        /// Gets metadata about this database connector.
        /// </summary>
        /// <returns>Dictionary of metadata key-value pairs.</returns>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["ConnectorType"] = "DatabaseConnector";
            metadata["SupportsSQL"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for messaging connector plugins.
    /// Provides specialized infrastructure for message queues and event streams.
    /// Supports Kafka, RabbitMQ, Pulsar, NATS, and similar messaging systems.
    /// </summary>
    public abstract class MessagingConnectorPluginBase : DataConnectorPluginBase
    {
        /// <summary>
        /// Messaging connectors are always in the Messaging category.
        /// </summary>
        public override ConnectorCategory ConnectorCategory => Connectors.ConnectorCategory.Messaging;

        /// <summary>
        /// Standard messaging capabilities: Read, Write, Streaming.
        /// </summary>
        public override ConnectorCapabilities Capabilities =>
            ConnectorCapabilities.Read |
            ConnectorCapabilities.Write |
            ConnectorCapabilities.Streaming;

        #region Messaging-Specific Methods

        /// <summary>
        /// Publishes a message to a topic.
        /// Override to implement messaging-system-specific publish logic.
        /// </summary>
        /// <param name="topic">Topic name.</param>
        /// <param name="message">Message payload.</param>
        /// <param name="headers">Optional message headers.</param>
        /// <returns>Task representing the publish operation.</returns>
        protected abstract Task PublishAsync(string topic, byte[] message, Dictionary<string, string>? headers);

        /// <summary>
        /// Consumes messages from a topic.
        /// Override to implement messaging-system-specific consume logic.
        /// </summary>
        /// <param name="topic">Topic name.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Async enumerable of messages with headers.</returns>
        protected abstract IAsyncEnumerable<(byte[] Data, Dictionary<string, string> Headers)> ConsumeAsync(string topic, CancellationToken ct);

        #endregion

        /// <summary>
        /// Gets metadata about this messaging connector.
        /// </summary>
        /// <returns>Dictionary of metadata key-value pairs.</returns>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["ConnectorType"] = "MessagingConnector";
            metadata["SupportsPublishSubscribe"] = true;
            metadata["SupportsEventStreaming"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for SaaS connector plugins.
    /// Provides specialized infrastructure for SaaS platforms with OAuth/token-based authentication.
    /// Supports Salesforce, HubSpot, Zendesk, Jira, and similar cloud services.
    /// </summary>
    public abstract class SaaSConnectorPluginBase : DataConnectorPluginBase
    {
        /// <summary>
        /// SaaS connectors are always in the SaaS category.
        /// </summary>
        public override ConnectorCategory ConnectorCategory => Connectors.ConnectorCategory.SaaS;

        /// <summary>
        /// Standard SaaS capabilities: Read, Write, Schema.
        /// </summary>
        public override ConnectorCapabilities Capabilities =>
            ConnectorCapabilities.Read |
            ConnectorCapabilities.Write |
            ConnectorCapabilities.Schema;

        #region Authentication Properties

        /// <summary>
        /// Current access token for API authentication.
        /// </summary>
        protected string? AccessToken { get; set; }

        /// <summary>
        /// Expiry time of the current access token.
        /// </summary>
        protected DateTimeOffset TokenExpiry { get; set; }

        #endregion

        #region SaaS-Specific Methods

        /// <summary>
        /// Authenticates with the SaaS platform and obtains an access token.
        /// Override to implement platform-specific OAuth or API key authentication.
        /// </summary>
        /// <param name="config">Connection configuration containing credentials.</param>
        /// <returns>Access token for API calls.</returns>
        protected abstract Task<string> AuthenticateAsync(ConnectorConfig config);

        /// <summary>
        /// Refreshes the access token when it expires.
        /// Override to implement platform-specific token refresh logic.
        /// </summary>
        /// <returns>Task representing the refresh operation.</returns>
        protected abstract Task RefreshTokenAsync();

        #endregion

        #region Token Management Helpers

        /// <summary>
        /// Checks if the current access token is expired or about to expire.
        /// </summary>
        /// <returns>True if token needs refresh.</returns>
        protected bool IsTokenExpired()
        {
            // Consider token expired if it expires within the next 5 minutes
            return DateTimeOffset.UtcNow >= TokenExpiry.AddMinutes(-5);
        }

        /// <summary>
        /// Ensures the access token is valid, refreshing if necessary.
        /// </summary>
        /// <returns>Task representing the ensure operation.</returns>
        protected async Task EnsureValidTokenAsync()
        {
            if (IsTokenExpired())
            {
                await RefreshTokenAsync();
            }
        }

        #endregion

        /// <summary>
        /// Gets metadata about this SaaS connector.
        /// </summary>
        /// <returns>Dictionary of metadata key-value pairs.</returns>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["ConnectorType"] = "SaaSConnector";
            metadata["RequiresAuthentication"] = true;
            metadata["AuthenticationType"] = "OAuth/Token";
            metadata["HasValidToken"] = !string.IsNullOrEmpty(AccessToken) && !IsTokenExpired();
            return metadata;
        }
    }
}
