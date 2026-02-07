using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.SDK.Connectors
{
    /// <summary>
    /// Base class for database connection strategies.
    /// Provides query execution and schema discovery abstractions for relational
    /// and non-relational database systems.
    /// </summary>
    public abstract class DatabaseConnectionStrategyBase : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Database;

        /// <summary>
        /// Initializes a new instance of <see cref="DatabaseConnectionStrategyBase"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        protected DatabaseConnectionStrategyBase(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override string GetConnectorCategory() => "Database";

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetConnectionCapabilities() => new()
        {
            ["supportsTransactions"] = true,
            ["supportsQueryExecution"] = true,
            ["supportsSchemaDiscovery"] = true
        };

        /// <summary>
        /// Executes a query and returns the results as a list of dictionaries.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="query">SQL or query-language query string.</param>
        /// <param name="parameters">Optional query parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of rows, each represented as a dictionary of column name to value.</returns>
        public abstract Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle,
            string query,
            Dictionary<string, object?>? parameters = null,
            CancellationToken ct = default);

        /// <summary>
        /// Executes a non-query command (INSERT, UPDATE, DELETE, DDL).
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="command">SQL or command string.</param>
        /// <param name="parameters">Optional command parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Number of rows affected.</returns>
        public abstract Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle,
            string command,
            Dictionary<string, object?>? parameters = null,
            CancellationToken ct = default);

        /// <summary>
        /// Retrieves the schema metadata for the connected database.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of schema definitions for all discovered tables/collections.</returns>
        public abstract Task<IReadOnlyList<DataSchema>> GetSchemaAsync(
            IConnectionHandle handle,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Base class for messaging system connection strategies.
    /// Provides publish/subscribe abstractions for message brokers and event buses.
    /// </summary>
    public abstract class MessagingConnectionStrategyBase : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Messaging;

        /// <summary>
        /// Initializes a new instance of <see cref="MessagingConnectionStrategyBase"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        protected MessagingConnectionStrategyBase(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override string GetConnectorCategory() => "Messaging";

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetConnectionCapabilities() => new()
        {
            ["supportsPublish"] = true,
            ["supportsSubscribe"] = true,
            ["supportsTopics"] = true
        };

        /// <summary>
        /// Publishes a message to a topic or queue.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="topic">Target topic, queue, or exchange name.</param>
        /// <param name="message">Message payload as bytes.</param>
        /// <param name="headers">Optional message headers/metadata.</param>
        /// <param name="ct">Cancellation token.</param>
        public abstract Task PublishAsync(
            IConnectionHandle handle,
            string topic,
            byte[] message,
            Dictionary<string, string>? headers = null,
            CancellationToken ct = default);

        /// <summary>
        /// Subscribes to a topic or queue and returns messages as an async stream.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="topic">Source topic, queue, or subscription name.</param>
        /// <param name="consumerGroup">Optional consumer group for partitioned consumption.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Async enumerable of message payloads.</returns>
        public abstract IAsyncEnumerable<byte[]> SubscribeAsync(
            IConnectionHandle handle,
            string topic,
            string? consumerGroup = null,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Base class for SaaS platform connection strategies.
    /// Provides token management and OAuth2 flow abstractions for cloud APIs.
    /// </summary>
    public abstract class SaaSConnectionStrategyBase : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.SaaS;

        private string? _currentToken;
        private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;
        private readonly SemaphoreSlim _tokenLock = new(1, 1);

        /// <summary>
        /// Initializes a new instance of <see cref="SaaSConnectionStrategyBase"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        protected SaaSConnectionStrategyBase(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override string GetConnectorCategory() => "SaaS";

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetConnectionCapabilities() => new()
        {
            ["supportsOAuth"] = true,
            ["supportsTokenRefresh"] = true,
            ["supportsApiCalls"] = true
        };

        /// <summary>
        /// Ensures a valid access token is available, refreshing if necessary.
        /// Thread-safe via internal semaphore.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A valid access token string.</returns>
        public async Task<string> EnsureValidTokenAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            if (_currentToken != null && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-2))
                return _currentToken;

            await _tokenLock.WaitAsync(ct);
            try
            {
                // Double-check after acquiring lock
                if (_currentToken != null && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-2))
                    return _currentToken;

                if (_currentToken == null)
                {
                    var (token, expiry) = await AuthenticateAsync(handle, ct);
                    _currentToken = token;
                    _tokenExpiry = expiry;
                }
                else
                {
                    var (token, expiry) = await RefreshTokenAsync(handle, _currentToken, ct);
                    _currentToken = token;
                    _tokenExpiry = expiry;
                }

                return _currentToken;
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        /// <summary>
        /// Performs initial authentication to obtain an access token.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Tuple of access token and its expiry time.</returns>
        protected abstract Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(
            IConnectionHandle handle,
            CancellationToken ct = default);

        /// <summary>
        /// Refreshes an expired or expiring access token.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="currentToken">The current token to refresh.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Tuple of new access token and its expiry time.</returns>
        protected abstract Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(
            IConnectionHandle handle,
            string currentToken,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Base class for IoT device connection strategies.
    /// Provides telemetry reading and command sending abstractions for IoT protocols.
    /// </summary>
    public abstract class IoTConnectionStrategyBase : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.IoT;

        /// <summary>
        /// Initializes a new instance of <see cref="IoTConnectionStrategyBase"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        protected IoTConnectionStrategyBase(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override string GetConnectorCategory() => "IoT";

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetConnectionCapabilities() => new()
        {
            ["supportsTelemetry"] = true,
            ["supportsCommands"] = true,
            ["supportsDeviceManagement"] = true
        };

        /// <summary>
        /// Reads telemetry data from a device or device group.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="deviceId">Device identifier or wildcard pattern.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Dictionary of metric names to their latest values.</returns>
        public abstract Task<Dictionary<string, object>> ReadTelemetryAsync(
            IConnectionHandle handle,
            string deviceId,
            CancellationToken ct = default);

        /// <summary>
        /// Sends a command to a device.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="deviceId">Target device identifier.</param>
        /// <param name="command">Command name or payload.</param>
        /// <param name="parameters">Optional command parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Command acknowledgement or response as a string.</returns>
        public abstract Task<string> SendCommandAsync(
            IConnectionHandle handle,
            string deviceId,
            string command,
            Dictionary<string, object>? parameters = null,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Base class for legacy system connection strategies.
    /// Provides protocol emulation and command translation abstractions for
    /// mainframe, AS/400, and other legacy systems.
    /// </summary>
    public abstract class LegacyConnectionStrategyBase : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Legacy;

        /// <summary>
        /// Initializes a new instance of <see cref="LegacyConnectionStrategyBase"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        protected LegacyConnectionStrategyBase(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override string GetConnectorCategory() => "Legacy";

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetConnectionCapabilities() => new()
        {
            ["supportsProtocolEmulation"] = true,
            ["supportsCommandTranslation"] = true,
            ["supportsMainframe"] = true
        };

        /// <summary>
        /// Emulates a legacy protocol interaction (e.g., 3270 terminal, AS/400 5250).
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="protocolCommand">The raw protocol command or screen interaction.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The raw protocol response.</returns>
        public abstract Task<string> EmulateProtocolAsync(
            IConnectionHandle handle,
            string protocolCommand,
            CancellationToken ct = default);

        /// <summary>
        /// Translates a modern command into a legacy-compatible format.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="modernCommand">The modern command representation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The translated legacy-compatible command string.</returns>
        public abstract Task<string> TranslateCommandAsync(
            IConnectionHandle handle,
            string modernCommand,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Base class for healthcare system connection strategies.
    /// Provides HL7, FHIR, and DICOM protocol abstractions for healthcare interoperability.
    /// </summary>
    public abstract class HealthcareConnectionStrategyBase : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Healthcare;

        /// <summary>
        /// Initializes a new instance of <see cref="HealthcareConnectionStrategyBase"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        protected HealthcareConnectionStrategyBase(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override string GetConnectorCategory() => "Healthcare";

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetConnectionCapabilities() => new()
        {
            ["supportsHL7"] = true,
            ["supportsFHIR"] = true,
            ["supportsDICOM"] = true
        };

        /// <summary>
        /// Validates an HL7 message for structural and semantic correctness.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="hl7Message">The HL7 v2.x message string.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Tuple of validity flag and any validation error messages.</returns>
        public abstract Task<(bool IsValid, string[] Errors)> ValidateHl7Async(
            IConnectionHandle handle,
            string hl7Message,
            CancellationToken ct = default);

        /// <summary>
        /// Queries a FHIR resource endpoint.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="resourceType">FHIR resource type (Patient, Observation, etc.).</param>
        /// <param name="query">FHIR search query parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>JSON string containing the FHIR Bundle response.</returns>
        public abstract Task<string> QueryFhirAsync(
            IConnectionHandle handle,
            string resourceType,
            string? query = null,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Base class for blockchain network connection strategies.
    /// Provides block retrieval and transaction submission abstractions for DLT networks.
    /// </summary>
    public abstract class BlockchainConnectionStrategyBase : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Blockchain;

        /// <summary>
        /// Initializes a new instance of <see cref="BlockchainConnectionStrategyBase"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        protected BlockchainConnectionStrategyBase(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override string GetConnectorCategory() => "Blockchain";

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetConnectionCapabilities() => new()
        {
            ["supportsBlockRetrieval"] = true,
            ["supportsTransactionSubmission"] = true,
            ["supportsDLT"] = true
        };

        /// <summary>
        /// Retrieves a block by its hash or number.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="blockIdentifier">Block hash or number (e.g., "latest", "0x1a2b...", "12345").</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>JSON string representing the block data.</returns>
        public abstract Task<string> GetBlockAsync(
            IConnectionHandle handle,
            string blockIdentifier,
            CancellationToken ct = default);

        /// <summary>
        /// Submits a signed transaction to the blockchain network.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="signedTransaction">The signed transaction payload (hex or bytes).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Transaction hash or receipt identifier.</returns>
        public abstract Task<string> SubmitTransactionAsync(
            IConnectionHandle handle,
            string signedTransaction,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Base class for observability platform connection strategies.
    /// Provides metrics, logs, and traces push abstractions for monitoring systems.
    /// </summary>
    public abstract class ObservabilityConnectionStrategyBase : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Observability;

        /// <summary>
        /// Initializes a new instance of <see cref="ObservabilityConnectionStrategyBase"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        protected ObservabilityConnectionStrategyBase(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override string GetConnectorCategory() => "Observability";

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetConnectionCapabilities() => new()
        {
            ["supportsMetrics"] = true,
            ["supportsLogs"] = true,
            ["supportsTraces"] = true
        };

        /// <summary>
        /// Pushes metric data points to the observability platform.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="metrics">List of metric data points, each as a dictionary with name, value, tags, and timestamp.</param>
        /// <param name="ct">Cancellation token.</param>
        public abstract Task PushMetricsAsync(
            IConnectionHandle handle,
            IReadOnlyList<Dictionary<string, object>> metrics,
            CancellationToken ct = default);

        /// <summary>
        /// Pushes log entries to the observability platform.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="logs">List of log entries, each as a dictionary with message, level, timestamp, and attributes.</param>
        /// <param name="ct">Cancellation token.</param>
        public abstract Task PushLogsAsync(
            IConnectionHandle handle,
            IReadOnlyList<Dictionary<string, object>> logs,
            CancellationToken ct = default);

        /// <summary>
        /// Pushes trace spans to the observability platform.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="traces">List of trace spans, each as a dictionary with traceId, spanId, name, duration, and attributes.</param>
        /// <param name="ct">Cancellation token.</param>
        public abstract Task PushTracesAsync(
            IConnectionHandle handle,
            IReadOnlyList<Dictionary<string, object>> traces,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Base class for dashboard and BI platform connection strategies.
    /// Provides dashboard provisioning and data push abstractions for visualization platforms.
    /// </summary>
    public abstract class DashboardConnectionStrategyBase : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Dashboard;

        /// <summary>
        /// Initializes a new instance of <see cref="DashboardConnectionStrategyBase"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        protected DashboardConnectionStrategyBase(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override string GetConnectorCategory() => "Dashboard";

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetConnectionCapabilities() => new()
        {
            ["supportsDashboardProvisioning"] = true,
            ["supportsDataPush"] = true,
            ["supportsBI"] = true
        };

        /// <summary>
        /// Provisions or updates a dashboard definition on the platform.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="dashboardDefinition">JSON string containing the dashboard layout and configuration.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The dashboard identifier on the target platform.</returns>
        public abstract Task<string> ProvisionDashboardAsync(
            IConnectionHandle handle,
            string dashboardDefinition,
            CancellationToken ct = default);

        /// <summary>
        /// Pushes data to a dashboard data source or dataset.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="datasetId">Target dataset or data source identifier.</param>
        /// <param name="data">Rows of data, each row as a dictionary of column name to value.</param>
        /// <param name="ct">Cancellation token.</param>
        public abstract Task PushDataAsync(
            IConnectionHandle handle,
            string datasetId,
            IReadOnlyList<Dictionary<string, object?>> data,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Base class for AI and ML platform connection strategies.
    /// Provides request/response and streaming abstractions for LLM and ML inference APIs.
    /// </summary>
    public abstract class AiConnectionStrategyBase : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.AI;

        /// <summary>
        /// Initializes a new instance of <see cref="AiConnectionStrategyBase"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        protected AiConnectionStrategyBase(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override string GetConnectorCategory() => "AI";

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetConnectionCapabilities() => new()
        {
            ["supportsLLM"] = true,
            ["supportsStreaming"] = true,
            ["supportsInference"] = true
        };

        /// <summary>
        /// Sends a request to the AI platform and returns the complete response.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="prompt">The prompt or input text.</param>
        /// <param name="options">Optional request parameters (model, temperature, max_tokens, etc.).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The complete AI response as a string.</returns>
        public abstract Task<string> SendRequestAsync(
            IConnectionHandle handle,
            string prompt,
            Dictionary<string, object>? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Sends a request and streams the response token by token.
        /// </summary>
        /// <param name="handle">Active connection handle.</param>
        /// <param name="prompt">The prompt or input text.</param>
        /// <param name="options">Optional request parameters (model, temperature, max_tokens, etc.).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Async enumerable of response tokens/chunks.</returns>
        public abstract IAsyncEnumerable<string> StreamResponseAsync(
            IConnectionHandle handle,
            string prompt,
            Dictionary<string, object>? options = null,
            CancellationToken ct = default);
    }
}
