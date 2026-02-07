using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Abstract base class for data connector plugins.
    /// Provides common infrastructure for connecting to and interacting with external data sources.
    /// Implements connection lifecycle management, error handling, and capability enforcement.
    /// Intelligence-aware: Supports AI-driven schema discovery and query optimization.
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

        #region Intelligence Integration

        /// <summary>
        /// Capabilities declared by this data connector.
        /// </summary>
        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.connector",
                DisplayName = $"{Name} - Data Connector",
                Description = $"Data connector for {ConnectorCategory} sources with read/write capabilities",
                Category = CapabilityCategory.Pipeline,
                SubCategory = "DataConnector",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "connector", "data", ConnectorCategory.ToString().ToLowerInvariant() },
                SemanticDescription = $"Use this for connecting to and interacting with {ConnectorCategory} data sources",
                Metadata = new Dictionary<string, object>
                {
                    ["connectorId"] = ConnectorId,
                    ["connectorCategory"] = ConnectorCategory.ToString(),
                    ["capabilities"] = Capabilities.ToString(),
                    ["supportsRead"] = Capabilities.HasFlag(ConnectorCapabilities.Read),
                    ["supportsWrite"] = Capabilities.HasFlag(ConnectorCapabilities.Write)
                }
            }
        };

        /// <summary>
        /// Gets static knowledge for Intelligence registration.
        /// </summary>
        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

            knowledge.Add(new KnowledgeObject
            {
                Id = $"{Id}.connector.capability",
                Topic = "data-connector",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Data connector for {ConnectorCategory} sources",
                Payload = new Dictionary<string, object>
                {
                    ["connectorId"] = ConnectorId,
                    ["connectorCategory"] = ConnectorCategory.ToString(),
                    ["capabilities"] = Capabilities.ToString(),
                    ["supportsRead"] = Capabilities.HasFlag(ConnectorCapabilities.Read),
                    ["supportsWrite"] = Capabilities.HasFlag(ConnectorCapabilities.Write),
                    ["supportsSchema"] = Capabilities.HasFlag(ConnectorCapabilities.Schema),
                    ["supportsTransactions"] = Capabilities.HasFlag(ConnectorCapabilities.Transactions)
                },
                Tags = new[] { "connector", ConnectorCategory.ToString().ToLowerInvariant() },
                Confidence = 1.0f,
                Timestamp = DateTimeOffset.UtcNow
            });

            return knowledge;
        }

        /// <summary>
        /// Requests AI-driven schema suggestion based on sample data.
        /// </summary>
        /// <param name="sampleRecords">Sample data records.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Suggested schema.</returns>
        protected async Task<DataSchema?> RequestSchemaSuggestionAsync(
            IReadOnlyList<DataRecord> sampleRecords,
            CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.suggest.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["suggestionType"] = "schema",
                        ["sampleCount"] = sampleRecords.Count,
                        ["connectorCategory"] = ConnectorCategory.ToString()
                    }
                };

                await MessageBus.PublishAsync("intelligence.suggest", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Requests AI-driven query optimization for the data source.
        /// </summary>
        /// <param name="query">Query to optimize.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Optimized query.</returns>
        protected async Task<DataQuery?> RequestQueryOptimizationAsync(
            DataQuery query,
            CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.optimize.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["optimizationType"] = "query",
                        ["connectorCategory"] = ConnectorCategory.ToString()
                    }
                };

                await MessageBus.PublishAsync("intelligence.optimize", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

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
    /// Intelligence-aware: Supports AI-driven query generation and index recommendations.
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

        #region Database Intelligence Integration

        /// <summary>
        /// Requests AI-driven index recommendations based on query patterns.
        /// </summary>
        /// <param name="queryPatterns">Recent query patterns.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Recommended indexes.</returns>
        protected async Task<IReadOnlyList<string>?> RequestIndexRecommendationsAsync(
            IReadOnlyList<string> queryPatterns,
            CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.recommend.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["recommendationType"] = "database_indexes",
                        ["queryPatternCount"] = queryPatterns.Count
                    }
                };

                await MessageBus.PublishAsync("intelligence.recommend", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Requests AI-generated SQL query from natural language.
        /// </summary>
        /// <param name="naturalLanguageQuery">Natural language query.</param>
        /// <param name="schema">Current schema for context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Generated SQL query.</returns>
        protected async Task<string?> RequestNaturalLanguageToSqlAsync(
            string naturalLanguageQuery,
            DataSchema schema,
            CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.generate.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["generationType"] = "nl_to_sql",
                        ["query"] = naturalLanguageQuery,
                        ["fieldCount"] = schema.Fields?.Length ?? 0
                    }
                };

                await MessageBus.PublishAsync("intelligence.generate", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

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
    /// Intelligence-aware: Supports AI-driven message routing and anomaly detection.
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

        #region Messaging Intelligence Integration

        /// <summary>
        /// Requests AI-driven message routing recommendation.
        /// </summary>
        /// <param name="messageContent">Message content to route.</param>
        /// <param name="availableTopics">Available topics.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Recommended topic.</returns>
        protected async Task<string?> RequestMessageRoutingAsync(
            byte[] messageContent,
            IReadOnlyList<string> availableTopics,
            CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.recommend.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["recommendationType"] = "message_routing",
                        ["messageSize"] = messageContent.Length,
                        ["topicCount"] = availableTopics.Count
                    }
                };

                await MessageBus.PublishAsync("intelligence.recommend", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Requests AI-driven message anomaly detection.
        /// </summary>
        /// <param name="recentMessages">Recent messages for analysis.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Detected anomalies.</returns>
        protected async Task<IReadOnlyList<(int MessageIndex, string AnomalyType)>?> RequestMessageAnomalyDetectionAsync(
            IReadOnlyList<byte[]> recentMessages,
            CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.anomaly.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["analysisType"] = "message_stream",
                        ["messageCount"] = recentMessages.Count
                    }
                };

                await MessageBus.PublishAsync("intelligence.anomaly", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

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
    /// Intelligence-aware: Supports AI-driven API mapping and rate limit optimization.
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

        #region SaaS Intelligence Integration

        /// <summary>
        /// Requests AI-driven API endpoint mapping suggestion.
        /// </summary>
        /// <param name="operation">Desired operation description.</param>
        /// <param name="availableEndpoints">Available API endpoints.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Recommended endpoint.</returns>
        protected async Task<string?> RequestApiEndpointMappingAsync(
            string operation,
            IReadOnlyList<string> availableEndpoints,
            CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.map.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["mappingType"] = "api_endpoint",
                        ["operation"] = operation,
                        ["endpointCount"] = availableEndpoints.Count
                    }
                };

                await MessageBus.PublishAsync("intelligence.map", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Requests AI-driven rate limit optimization.
        /// </summary>
        /// <param name="rateLimitInfo">Current rate limit information.</param>
        /// <param name="pendingRequests">Number of pending requests.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Recommended request delay.</returns>
        protected async Task<TimeSpan?> RequestRateLimitOptimizationAsync(
            Dictionary<string, object> rateLimitInfo,
            int pendingRequests,
            CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.optimize.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["optimizationType"] = "rate_limit",
                        ["rateLimitInfo"] = rateLimitInfo,
                        ["pendingRequests"] = pendingRequests
                    }
                };

                await MessageBus.PublishAsync("intelligence.optimize", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

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
