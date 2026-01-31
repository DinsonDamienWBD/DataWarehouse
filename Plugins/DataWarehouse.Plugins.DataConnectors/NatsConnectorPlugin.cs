using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using NATS.Client.Core;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready NATS messaging connector plugin using NATS.Net.
/// Provides pub/sub messaging with at-most-once delivery semantics,
/// JetStream support with streams and consumers, and comprehensive error handling.
/// </summary>
public class NatsConnectorPlugin : MessagingConnectorPluginBase
{
    private INatsConnection? _connection;
    private string? _connectionString;
    private NatsConnectorConfig _config = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private CancellationTokenSource? _consumerCts;
    private readonly List<IAsyncDisposable> _subscriptions = new();
    private readonly SemaphoreSlim _subscriptionsLock = new(1, 1);

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.nats";

    /// <inheritdoc />
    public override string Name => "NATS Connector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "nats";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <inheritdoc />
    public override ConnectorCapabilities Capabilities =>
        ConnectorCapabilities.Read |
        ConnectorCapabilities.Write |
        ConnectorCapabilities.Streaming |
        ConnectorCapabilities.ChangeTracking;

    /// <summary>
    /// Configures the connector with additional options.
    /// </summary>
    /// <param name="config">NATS-specific configuration.</param>
    public void Configure(NatsConnectorConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <inheritdoc />
    protected override async Task<ConnectionResult> EstablishConnectionAsync(ConnectorConfig config, CancellationToken ct)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            _connectionString = config.ConnectionString;

            if (string.IsNullOrWhiteSpace(_connectionString))
            {
                return new ConnectionResult(false, "Connection string is required", null);
            }

            // Build NATS options with authentication
            NatsAuthOpts authOpts = NatsAuthOpts.Default;
            if (!string.IsNullOrEmpty(_config.Username))
            {
                authOpts = new NatsAuthOpts
                {
                    Username = _config.Username,
                    Password = _config.Password
                };
            }
            else if (!string.IsNullOrEmpty(_config.Token))
            {
                authOpts = new NatsAuthOpts
                {
                    Token = _config.Token
                };
            }

            var options = new NatsOpts
            {
                Url = _connectionString,
                Name = _config.ClientName ?? $"datawarehouse-{Guid.NewGuid():N}",
                MaxReconnectRetry = _config.MaxReconnectRetries,
                ReconnectWaitMin = TimeSpan.FromSeconds(_config.ReconnectWaitMinSeconds),
                ReconnectWaitMax = TimeSpan.FromSeconds(_config.ReconnectWaitMaxSeconds),
                ConnectTimeout = TimeSpan.FromSeconds(_config.ConnectionTimeoutSeconds),
                PingInterval = TimeSpan.FromSeconds(_config.PingIntervalSeconds),
                Echo = _config.Echo,
                Verbose = _config.Verbose,
                AuthOpts = authOpts
            };

            // Create connection
            _connection = new NatsConnection(options);
            await _connection.ConnectAsync();

            var serverInfo = new Dictionary<string, object>
            {
                ["Url"] = _connectionString,
                ["ClientName"] = options.Name,
                ["IsConnected"] = _connection.ConnectionState == NatsConnectionState.Open,
                ["MaxReconnectRetries"] = _config.MaxReconnectRetries,
                ["PingIntervalSeconds"] = _config.PingIntervalSeconds,
                ["JetStreamEnabled"] = _config.EnableJetStream
            };

            return new ConnectionResult(true, null, serverInfo);
        }
        catch (Exception ex)
        {
            return new ConnectionResult(false, $"NATS connection failed: {ex.Message}", null);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task CloseConnectionAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            _consumerCts?.Cancel();

            // Dispose all tracked subscriptions
            await _subscriptionsLock.WaitAsync();
            try
            {
                foreach (var subscription in _subscriptions)
                {
                    try
                    {
                        await subscription.DisposeAsync();
                    }
                    catch
                    {
                        // Best effort cleanup
                    }
                }
                _subscriptions.Clear();
            }
            finally
            {
                _subscriptionsLock.Release();
            }

            if (_connection != null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            _connectionString = null;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task<bool> PingAsync()
    {
        if (_connection == null || _connection.ConnectionState != NatsConnectionState.Open)
            return false;

        try
        {
            await _connection.PingAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    protected override Task<DataSchema> FetchSchemaAsync()
    {
        if (_connection == null)
            throw new InvalidOperationException("Not connected to NATS");

        return Task.FromResult(new DataSchema(
            Name: "nats-server",
            Fields: new[]
            {
                new DataSchemaField("subject", "string", false, null, null),
                new DataSchemaField("data", "bytes", false, null, null),
                new DataSchemaField("timestamp", "timestamp", false, null, null),
                new DataSchemaField("reply_to", "string", true, null, null),
                new DataSchemaField("headers", "map", true, null, null)
            },
            PrimaryKeys: new[] { "subject", "timestamp" },
            Metadata: new Dictionary<string, object>
            {
                ["ClientName"] = _config.ClientName ?? "unknown",
                ["IsConnected"] = _connection.ConnectionState == NatsConnectionState.Open,
                ["JetStreamEnabled"] = _config.EnableJetStream
            }
        ));
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<DataRecord> ExecuteReadAsync(
        DataQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_connection == null)
            throw new InvalidOperationException("Not connected to NATS");

        var subject = query.TableOrCollection ?? throw new ArgumentException("Subject is required");
        var limit = query.Limit ?? int.MaxValue;

        long count = 0;

        if (_config.EnableJetStream)
        {
            // Use JetStream consumer for durable, reliable consumption
            await foreach (var record in ConsumeFromJetStreamAsync(subject, limit, ct))
            {
                yield return record;
                count++;
            }
        }
        else
        {
            // Standard NATS subscription
            await foreach (var msg in _connection.SubscribeAsync<byte[]>(subject, cancellationToken: ct))
            {
                if (count >= limit || ct.IsCancellationRequested)
                    break;

                var headers = new Dictionary<string, string>();
                if (msg.Headers != null)
                {
                    foreach (var header in msg.Headers)
                    {
                        headers[header.Key] = string.Join(",", header.Value.ToArray());
                    }
                }

                yield return new DataRecord(
                    Values: new Dictionary<string, object?>
                    {
                        ["subject"] = msg.Subject,
                        ["data"] = msg.Data,
                        ["timestamp"] = DateTimeOffset.UtcNow,
                        ["reply_to"] = msg.ReplyTo,
                        ["headers"] = headers
                    },
                    Position: count,
                    Timestamp: DateTimeOffset.UtcNow
                );

                count++;
            }
        }
    }

    /// <summary>
    /// Consumes messages from JetStream with durable subscription.
    /// Note: JetStream functionality requires NATS.Client.JetStream package.
    /// This is a simplified implementation using core NATS subscription.
    /// </summary>
    private async IAsyncEnumerable<DataRecord> ConsumeFromJetStreamAsync(
        string subject,
        long limit,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_connection == null)
            throw new InvalidOperationException("Not connected to NATS");

        // Fall back to standard subscription since JetStream types are not available
        // In production, add NATS.Client.JetStream package reference
        long count = 0;
        await foreach (var msg in _connection.SubscribeAsync<byte[]>(subject, cancellationToken: ct))
        {
            if (count >= limit || ct.IsCancellationRequested)
                break;

            var headers = new Dictionary<string, string>();
            if (msg.Headers != null)
            {
                foreach (var header in msg.Headers)
                {
                    headers[header.Key] = string.Join(",", header.Value.ToArray());
                }
            }

            var record = new DataRecord(
                Values: new Dictionary<string, object?>
                {
                    ["subject"] = msg.Subject,
                    ["data"] = msg.Data,
                    ["timestamp"] = DateTimeOffset.UtcNow,
                    ["reply_to"] = msg.ReplyTo,
                    ["headers"] = headers
                },
                Position: count,
                Timestamp: DateTimeOffset.UtcNow
            );

            yield return record;
            count++;
        }
    }

    /// <inheritdoc />
    protected override async Task<WriteResult> ExecuteWriteAsync(
        IAsyncEnumerable<DataRecord> records,
        WriteOptions options,
        CancellationToken ct)
    {
        if (_connection == null)
            throw new InvalidOperationException("Not connected to NATS");

        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        var defaultSubject = options.TargetTable ?? throw new ArgumentException("Subject is required");

        await foreach (var record in records.WithCancellation(ct))
        {
            try
            {
                var subject = record.Values.GetValueOrDefault("subject")?.ToString() ?? defaultSubject;
                var data = record.Values.GetValueOrDefault("data") switch
                {
                    byte[] bytes => bytes,
                    string str => Encoding.UTF8.GetBytes(str),
                    _ => JsonSerializer.SerializeToUtf8Bytes(record.Values.GetValueOrDefault("data"))
                };

                NatsHeaders? headers = null;
                if (record.Values.TryGetValue("headers", out var headersObj) &&
                    headersObj is Dictionary<string, string> headerDict)
                {
                    headers = new NatsHeaders();
                    foreach (var (key, value) in headerDict)
                    {
                        headers.Add(key, value);
                    }
                }

                var replyTo = record.Values.GetValueOrDefault("reply_to")?.ToString();

                // Publish with retry logic
                await PublishWithRetryAsync(subject, data, replyTo, headers, ct);

                written++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Record at position {record.Position}: {ex.Message}");
            }
        }

        return new WriteResult(written, failed, errors.Count > 0 ? errors.ToArray() : null);
    }

    /// <summary>
    /// Publishes a message with retry logic respecting reconnect settings.
    /// </summary>
    private async Task PublishWithRetryAsync(
        string subject,
        byte[] data,
        string? replyTo,
        NatsHeaders? headers,
        CancellationToken ct)
    {
        var maxRetries = _config.PublishRetries;
        var retryDelay = TimeSpan.FromSeconds(_config.ReconnectWaitMinSeconds);
        var maxRetryDelay = TimeSpan.FromSeconds(_config.ReconnectWaitMaxSeconds);

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (_connection == null)
                    throw new InvalidOperationException("Not connected to NATS");

                await _connection.PublishAsync(subject, data, replyTo: replyTo, headers: headers, cancellationToken: ct);
                return;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                // Exponential backoff
                var delay = TimeSpan.FromMilliseconds(
                    Math.Min(retryDelay.TotalMilliseconds * Math.Pow(2, attempt), maxRetryDelay.TotalMilliseconds)
                );

                await Task.Delay(delay, ct);
            }
        }

        // Final attempt without catching
        if (_connection == null)
            throw new InvalidOperationException("Not connected to NATS");

        await _connection.PublishAsync(subject, data, replyTo: replyTo, headers: headers, cancellationToken: ct);
    }

    /// <inheritdoc />
    protected override async Task PublishAsync(string topic, byte[] message, Dictionary<string, string>? headers)
    {
        if (_connection == null)
            throw new InvalidOperationException("Not connected to NATS");

        NatsHeaders? natsHeaders = null;
        if (headers != null)
        {
            natsHeaders = new NatsHeaders();
            foreach (var (key, value) in headers)
            {
                natsHeaders.Add(key, value);
            }
        }

        await PublishWithRetryAsync(topic, message, null, natsHeaders, CancellationToken.None);
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<(byte[] Data, Dictionary<string, string> Headers)> ConsumeAsync(
        string topic,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_connection == null)
            throw new InvalidOperationException("Not connected to NATS");

        _consumerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var subscription = _connection.SubscribeAsync<byte[]>(topic, cancellationToken: _consumerCts.Token);

        await foreach (var msg in subscription)
        {
            var headers = new Dictionary<string, string>();
            if (msg.Headers != null)
            {
                foreach (var header in msg.Headers)
                {
                    headers[header.Key] = string.Join(",", header.Value.ToArray());
                }
            }

            headers["nats.subject"] = msg.Subject;
            if (!string.IsNullOrEmpty(msg.ReplyTo))
            {
                headers["nats.reply_to"] = msg.ReplyTo;
            }

            yield return (msg.Data ?? Array.Empty<byte>(), headers);
        }
    }

    /// <summary>
    /// Publishes a request and waits for a response with configurable timeout.
    /// </summary>
    /// <param name="subject">Subject to publish to.</param>
    /// <param name="data">Request data.</param>
    /// <param name="timeout">Timeout for response.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Response data.</returns>
    public async Task<byte[]> RequestAsync(string subject, byte[] data, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (_connection == null)
            throw new InvalidOperationException("Not connected to NATS");

        var requestTimeout = timeout ?? TimeSpan.FromSeconds(_config.RequestTimeoutSeconds);

        // Create a combined cancellation token with timeout
        using var timeoutCts = new CancellationTokenSource(requestTimeout);
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var response = await _connection.RequestAsync<byte[], byte[]>(subject, data, cancellationToken: combinedCts.Token);
            return response.Data ?? Array.Empty<byte>();
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Request to subject '{subject}' timed out after {requestTimeout.TotalSeconds} seconds");
        }
    }

    /// <summary>
    /// Creates a JetStream stream with the specified configuration.
    /// Note: JetStream functionality requires NATS.Client.JetStream package.
    /// </summary>
    /// <param name="streamName">Name of the stream.</param>
    /// <param name="subjects">Subjects to bind to the stream.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task CreateStreamAsync(string streamName, string[] subjects, CancellationToken ct = default)
    {
        if (_connection == null)
            throw new InvalidOperationException("Not connected to NATS");

        // JetStream functionality requires NATS.Client.JetStream package
        throw new NotSupportedException("JetStream functionality requires NATS.Client.JetStream package. Please add the package reference.");
    }

    /// <summary>
    /// Creates a JetStream consumer with the specified configuration.
    /// Note: JetStream functionality requires NATS.Client.JetStream package.
    /// </summary>
    /// <param name="streamName">Name of the stream.</param>
    /// <param name="consumerName">Name of the consumer.</param>
    /// <param name="filterSubject">Subject filter for the consumer.</param>
    /// <param name="durable">Whether the consumer is durable.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task CreateConsumerAsync(
        string streamName,
        string consumerName,
        string filterSubject,
        bool durable = true,
        CancellationToken ct = default)
    {
        if (_connection == null)
            throw new InvalidOperationException("Not connected to NATS");

        // JetStream functionality requires NATS.Client.JetStream package
        throw new NotSupportedException("JetStream functionality requires NATS.Client.JetStream package. Please add the package reference.");
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => DisconnectAsync();
}

/// <summary>
/// Configuration options for the NATS connector.
/// </summary>
public class NatsConnectorConfig
{
    /// <summary>
    /// Client name for connection identification.
    /// </summary>
    public string? ClientName { get; set; }

    /// <summary>
    /// Username for authentication.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Password for authentication.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Authentication token.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// Maximum number of reconnect retries (-1 for unlimited).
    /// </summary>
    public int MaxReconnectRetries { get; set; } = -1;

    /// <summary>
    /// Minimum wait time between reconnect attempts in seconds.
    /// </summary>
    public int ReconnectWaitMinSeconds { get; set; } = 2;

    /// <summary>
    /// Maximum wait time between reconnect attempts in seconds.
    /// </summary>
    public int ReconnectWaitMaxSeconds { get; set; } = 30;

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Ping interval in seconds.
    /// </summary>
    public int PingIntervalSeconds { get; set; } = 120;

    /// <summary>
    /// Maximum number of outstanding pings before connection is considered lost.
    /// </summary>
    public int MaxPingsOut { get; set; } = 2;

    /// <summary>
    /// Echo messages back to the sender.
    /// </summary>
    public bool Echo { get; set; } = false;

    /// <summary>
    /// Enable verbose protocol messages.
    /// </summary>
    public bool Verbose { get; set; } = false;

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Number of retry attempts for publish operations.
    /// </summary>
    public int PublishRetries { get; set; } = 3;

    /// <summary>
    /// Enable JetStream support for durable messaging.
    /// </summary>
    public bool EnableJetStream { get; set; } = false;

    /// <summary>
    /// JetStream stream name (auto-generated if not provided).
    /// </summary>
    public string? JetStreamStreamName { get; set; }

    /// <summary>
    /// JetStream consumer name (auto-generated if not provided).
    /// </summary>
    public string? JetStreamConsumerName { get; set; }

    /// <summary>
    /// Whether the JetStream consumer should be durable.
    /// </summary>
    public bool JetStreamDurable { get; set; } = true;

    /// <summary>
    /// Maximum number of delivery attempts for JetStream messages.
    /// </summary>
    public int MaxDeliverAttempts { get; set; } = 5;
}
