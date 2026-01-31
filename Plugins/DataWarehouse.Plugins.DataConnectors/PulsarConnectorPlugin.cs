using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DotPulsar;
using DotPulsar.Abstractions;
using DotPulsar.Extensions;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready Apache Pulsar messaging connector plugin using DotPulsar.
/// Provides pub/sub messaging with multi-tenancy, geo-replication support,
/// and comprehensive error handling.
/// </summary>
public class PulsarConnectorPlugin : MessagingConnectorPluginBase
{
    private IPulsarClient? _client;
    private IProducer<byte[]>? _producer;
    private IConsumer<byte[]>? _consumer;
    private string? _serviceUrl;
    private PulsarConnectorConfig _config = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private CancellationTokenSource? _consumerCts;

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.pulsar";

    /// <inheritdoc />
    public override string Name => "Apache Pulsar Connector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "pulsar";

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
    /// <param name="config">Pulsar-specific configuration.</param>
    public void Configure(PulsarConnectorConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <inheritdoc />
    protected override async Task<ConnectionResult> EstablishConnectionAsync(ConnectorConfig config, CancellationToken ct)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            _serviceUrl = config.ConnectionString;

            if (string.IsNullOrWhiteSpace(_serviceUrl))
            {
                return new ConnectionResult(false, "Service URL is required", null);
            }

            // Build Pulsar client
            var clientBuilder = PulsarClient.Builder()
                .ServiceUrl(new Uri(_serviceUrl))
                .KeepAliveInterval(TimeSpan.FromSeconds(_config.KeepAliveIntervalSeconds));

            // Add authentication if provided
            if (!string.IsNullOrEmpty(_config.AuthenticationToken))
            {
                clientBuilder.AuthenticateUsingToken(_config.AuthenticationToken);
            }

            _client = clientBuilder.Build();

            var serverInfo = new Dictionary<string, object>
            {
                ["ServiceUrl"] = _serviceUrl,
                ["KeepAliveIntervalSeconds"] = _config.KeepAliveIntervalSeconds
            };

            return new ConnectionResult(true, null, serverInfo);
        }
        catch (Exception ex)
        {
            return new ConnectionResult(false, $"Pulsar connection failed: {ex.Message}", null);
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

            if (_producer != null)
            {
                await _producer.DisposeAsync();
                _producer = null;
            }

            if (_consumer != null)
            {
                await _consumer.DisposeAsync();
                _consumer = null;
            }

            if (_client != null)
            {
                await _client.DisposeAsync();
                _client = null;
            }

            _serviceUrl = null;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task<bool> PingAsync()
    {
        if (_client == null) return false;

        try
        {
            // Create a temporary producer to test connectivity
            var testProducer = _client.NewProducer(Schema.ByteArray)
                .Topic($"persistent://public/default/test-{Guid.NewGuid():N}")
                .Create();

            await testProducer.DisposeAsync();
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
        if (_client == null)
            throw new InvalidOperationException("Not connected to Pulsar");

        return Task.FromResult(new DataSchema(
            Name: "pulsar-cluster",
            Fields: new[]
            {
                new DataSchemaField("topic", "string", false, null, null),
                new DataSchemaField("data", "bytes", false, null, null),
                new DataSchemaField("timestamp", "timestamp", false, null, null),
                new DataSchemaField("message_id", "string", false, null, null),
                new DataSchemaField("key", "string", true, null, null),
                new DataSchemaField("properties", "map", true, null, null)
            },
            PrimaryKeys: new[] { "topic", "message_id" },
            Metadata: new Dictionary<string, object>
            {
                ["ServiceUrl"] = _serviceUrl ?? "unknown"
            }
        ));
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<DataRecord> ExecuteReadAsync(
        DataQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_client == null)
            throw new InvalidOperationException("Not connected to Pulsar");

        var topic = query.TableOrCollection ?? throw new ArgumentException("Topic is required");
        var limit = query.Limit ?? int.MaxValue;

        // Create consumer if not exists
        _consumer ??= _client.NewConsumer(Schema.ByteArray)
            .Topic(topic)
            .SubscriptionName(_config.SubscriptionName ?? $"datawarehouse-sub-{Guid.NewGuid():N}")
            .SubscriptionType(_config.SubscriptionType)
            .InitialPosition(_config.InitialPosition)
            .Create();

        long count = 0;

        while (!ct.IsCancellationRequested && count < limit)
        {
            IMessage<byte[]> message;
            try
            {
                message = await _consumer.Receive(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var properties = new Dictionary<string, string>();
            if (message.Properties != null)
            {
                foreach (var (key, value) in message.Properties)
                {
                    properties[key] = value;
                }
            }

            var messageData = message.Data.ToArray();
            var publishTime = DateTimeOffset.FromUnixTimeMilliseconds((long)message.PublishTimeAsUnixTimeMilliseconds);

            yield return new DataRecord(
                Values: new Dictionary<string, object?>
                {
                    ["topic"] = topic,
                    ["data"] = messageData,
                    ["timestamp"] = publishTime,
                    ["message_id"] = message.MessageId.ToString(),
                    ["key"] = message.KeyBytes != null ? Encoding.UTF8.GetString(message.KeyBytes.ToArray()) : null,
                    ["properties"] = properties
                },
                Position: count,
                Timestamp: publishTime
            );

            count++;

            // Acknowledge the message
            await _consumer.Acknowledge(message.MessageId, ct);
        }
    }

    /// <inheritdoc />
    protected override async Task<WriteResult> ExecuteWriteAsync(
        IAsyncEnumerable<DataRecord> records,
        WriteOptions options,
        CancellationToken ct)
    {
        if (_client == null)
            throw new InvalidOperationException("Not connected to Pulsar");

        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        var defaultTopic = options.TargetTable ?? throw new ArgumentException("Topic is required");

        // Create producer if not exists
        _producer ??= _client.NewProducer(Schema.ByteArray)
            .Topic(defaultTopic)
            .CompressionType(_config.CompressionType)
            .MaxPendingMessages((uint)_config.MaxPendingMessages)
            .SendTimeout(TimeSpan.FromSeconds(_config.SendTimeoutSeconds))
            .Create();

        await foreach (var record in records.WithCancellation(ct))
        {
            try
            {
                var data = record.Values.GetValueOrDefault("data") switch
                {
                    byte[] bytes => bytes,
                    string str => Encoding.UTF8.GetBytes(str),
                    _ => JsonSerializer.SerializeToUtf8Bytes(record.Values.GetValueOrDefault("data"))
                };

                var messageBuilder = _producer.NewMessage();

                // Add key if present
                if (record.Values.TryGetValue("key", out var keyObj) && keyObj != null)
                {
                    messageBuilder.Key(keyObj.ToString()!);
                }

                // Add properties if present
                if (record.Values.TryGetValue("properties", out var propsObj) &&
                    propsObj is Dictionary<string, string> properties)
                {
                    foreach (var (key, value) in properties)
                    {
                        messageBuilder.Property(key, value);
                    }
                }

                var messageId = await messageBuilder.Send(data, ct);

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

    /// <inheritdoc />
    protected override async Task PublishAsync(string topic, byte[] message, Dictionary<string, string>? headers)
    {
        if (_client == null)
            throw new InvalidOperationException("Not connected to Pulsar");

        var producer = _client.NewProducer(Schema.ByteArray)
            .Topic(topic)
            .CompressionType(_config.CompressionType)
            .Create();

        try
        {
            var messageBuilder = producer.NewMessage();

            if (headers != null)
            {
                foreach (var (key, value) in headers)
                {
                    messageBuilder.Property(key, value);
                }
            }

            await messageBuilder.Send(message);
        }
        finally
        {
            await producer.DisposeAsync();
        }
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<(byte[] Data, Dictionary<string, string> Headers)> ConsumeAsync(
        string topic,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_client == null)
            throw new InvalidOperationException("Not connected to Pulsar");

        _consumerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var consumer = _client.NewConsumer(Schema.ByteArray)
            .Topic(topic)
            .SubscriptionName(_config.SubscriptionName ?? $"datawarehouse-sub-{Guid.NewGuid():N}")
            .SubscriptionType(_config.SubscriptionType)
            .InitialPosition(_config.InitialPosition)
            .Create();

        try
        {
            while (!_consumerCts.Token.IsCancellationRequested)
            {
                IMessage<byte[]> message;

                try
                {
                    message = await consumer.Receive(_consumerCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var headers = new Dictionary<string, string>();
                if (message.Properties != null)
                {
                    foreach (var (key, value) in message.Properties)
                    {
                        headers[key] = value;
                    }
                }

                headers["pulsar.topic"] = topic;
                headers["pulsar.message_id"] = message.MessageId.ToString();
                headers["pulsar.timestamp"] = message.PublishTimeAsUnixTimeMilliseconds.ToString();
                if (message.KeyBytes != null)
                {
                    headers["pulsar.key"] = Encoding.UTF8.GetString(message.KeyBytes.ToArray());
                }

                var messageData = message.Data.ToArray();

                yield return (messageData, headers);

                await consumer.Acknowledge(message.MessageId, _consumerCts.Token);
            }
        }
        finally
        {
            await consumer.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => DisconnectAsync();
}

/// <summary>
/// Configuration options for the Pulsar connector.
/// </summary>
public class PulsarConnectorConfig
{
    /// <summary>
    /// Authentication token for Pulsar.
    /// </summary>
    public string? AuthenticationToken { get; set; }

    /// <summary>
    /// Keep-alive interval in seconds.
    /// </summary>
    public int KeepAliveIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Subscription name for consumers.
    /// </summary>
    public string? SubscriptionName { get; set; }

    /// <summary>
    /// Subscription type (Exclusive, Shared, Failover, KeyShared).
    /// </summary>
    public SubscriptionType SubscriptionType { get; set; } = SubscriptionType.Shared;

    /// <summary>
    /// Initial position for new subscriptions (Latest, Earliest).
    /// </summary>
    public SubscriptionInitialPosition InitialPosition { get; set; } = SubscriptionInitialPosition.Latest;

    /// <summary>
    /// Compression type for messages.
    /// </summary>
    public CompressionType CompressionType { get; set; } = CompressionType.None;

    /// <summary>
    /// Maximum number of pending messages in producer.
    /// </summary>
    public int MaxPendingMessages { get; set; } = 1000;

    /// <summary>
    /// Send timeout in seconds.
    /// </summary>
    public int SendTimeoutSeconds { get; set; } = 30;
}
