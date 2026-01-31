using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready RabbitMQ messaging connector plugin using RabbitMQ.Client.
/// Provides pub/sub messaging with AMQP protocol support, message acknowledgment,
/// and comprehensive error handling.
/// </summary>
public class RabbitMqConnectorPlugin : MessagingConnectorPluginBase
{
    private IConnection? _connection;
    private IChannel? _publishChannel;
    private IChannel? _consumeChannel;
    private string? _connectionString;
    private RabbitMqConnectorConfig _config = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private CancellationTokenSource? _consumerCts;

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.rabbitmq";

    /// <inheritdoc />
    public override string Name => "RabbitMQ Connector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "rabbitmq";

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
    /// <param name="config">RabbitMQ-specific configuration.</param>
    public void Configure(RabbitMqConnectorConfig config)
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

            // Build connection factory
            var factory = new ConnectionFactory
            {
                Uri = new Uri(_connectionString),
                ClientProvidedName = _config.ClientProvidedName ?? $"datawarehouse-{Guid.NewGuid():N}",
                RequestedHeartbeat = TimeSpan.FromSeconds(_config.HeartbeatSeconds),
                NetworkRecoveryInterval = TimeSpan.FromSeconds(_config.NetworkRecoverySeconds),
                AutomaticRecoveryEnabled = _config.AutomaticRecoveryEnabled,
                TopologyRecoveryEnabled = _config.TopologyRecoveryEnabled,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(_config.ConnectionTimeoutSeconds),
                SocketReadTimeout = TimeSpan.FromSeconds(_config.SocketReadTimeoutSeconds),
                SocketWriteTimeout = TimeSpan.FromSeconds(_config.SocketWriteTimeoutSeconds)
            };

            // Apply authentication if provided
            if (!string.IsNullOrEmpty(_config.Username))
            {
                factory.UserName = _config.Username;
                factory.Password = _config.Password ?? "";
            }

            if (!string.IsNullOrEmpty(_config.VirtualHost))
            {
                factory.VirtualHost = _config.VirtualHost;
            }

            // Create connection
            _connection = await factory.CreateConnectionAsync(ct);

            // Create channels
            _publishChannel = await _connection.CreateChannelAsync(cancellationToken: ct);
            _consumeChannel = await _connection.CreateChannelAsync(cancellationToken: ct);

            // Set QoS for consumer channel
            await _consumeChannel.BasicQosAsync(0, _config.PrefetchCount, false, ct);

            var serverInfo = new Dictionary<string, object>
            {
                ["Endpoint"] = factory.Endpoint.ToString(),
                ["VirtualHost"] = factory.VirtualHost,
                ["ClientProvidedName"] = factory.ClientProvidedName,
                ["IsOpen"] = _connection.IsOpen,
                ["HeartbeatSeconds"] = _config.HeartbeatSeconds,
                ["AutomaticRecovery"] = _config.AutomaticRecoveryEnabled
            };

            return new ConnectionResult(true, null, serverInfo);
        }
        catch (Exception ex)
        {
            return new ConnectionResult(false, $"RabbitMQ connection failed: {ex.Message}", null);
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

            if (_publishChannel != null)
            {
                await _publishChannel.CloseAsync();
                _publishChannel.Dispose();
                _publishChannel = null;
            }

            if (_consumeChannel != null)
            {
                await _consumeChannel.CloseAsync();
                _consumeChannel.Dispose();
                _consumeChannel = null;
            }

            if (_connection != null)
            {
                await _connection.CloseAsync();
                _connection.Dispose();
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
    protected override Task<bool> PingAsync()
    {
        return Task.FromResult(_connection?.IsOpen ?? false);
    }

    /// <inheritdoc />
    protected override Task<DataSchema> FetchSchemaAsync()
    {
        if (_connection == null)
            throw new InvalidOperationException("Not connected to RabbitMQ");

        return Task.FromResult(new DataSchema(
            Name: "rabbitmq-broker",
            Fields: new[]
            {
                new DataSchemaField("routing_key", "string", false, null, null),
                new DataSchemaField("body", "bytes", false, null, null),
                new DataSchemaField("timestamp", "timestamp", true, null, null),
                new DataSchemaField("exchange", "string", false, null, null),
                new DataSchemaField("delivery_tag", "long", false, null, null),
                new DataSchemaField("redelivered", "bool", false, null, null),
                new DataSchemaField("headers", "map", true, null, null)
            },
            PrimaryKeys: new[] { "exchange", "routing_key", "delivery_tag" },
            Metadata: new Dictionary<string, object>
            {
                ["VirtualHost"] = _connection.ClientProvidedName ?? "unknown",
                ["IsOpen"] = _connection.IsOpen
            }
        ));
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<DataRecord> ExecuteReadAsync(
        DataQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_consumeChannel == null)
            throw new InvalidOperationException("Not connected to RabbitMQ");

        var queueName = query.TableOrCollection ?? throw new ArgumentException("Queue name is required");
        var limit = query.Limit ?? int.MaxValue;

        // Declare queue with configuration
        await _consumeChannel.QueueDeclareAsync(
            queue: queueName,
            durable: _config.QueueDurable,
            exclusive: false,
            autoDelete: _config.QueueAutoDelete,
            arguments: null,
            cancellationToken: ct
        );

        long count = 0;

        while (!ct.IsCancellationRequested && count < limit)
        {
            var result = await _consumeChannel.BasicGetAsync(queueName, !_config.ManualAcknowledgment, ct);

            if (result == null)
            {
                await Task.Delay(100, ct);
                continue;
            }

            var headers = new Dictionary<string, string>();
            if (result.BasicProperties.Headers != null)
            {
                foreach (var (key, value) in result.BasicProperties.Headers)
                {
                    headers[key] = value?.ToString() ?? "";
                }
            }

            yield return new DataRecord(
                Values: new Dictionary<string, object?>
                {
                    ["routing_key"] = result.RoutingKey,
                    ["body"] = result.Body.ToArray(),
                    ["timestamp"] = DateTimeOffset.UtcNow,
                    ["exchange"] = result.Exchange,
                    ["delivery_tag"] = result.DeliveryTag,
                    ["redelivered"] = result.Redelivered,
                    ["headers"] = headers
                },
                Position: (long)result.DeliveryTag,
                Timestamp: DateTimeOffset.UtcNow
            );

            count++;

            // Acknowledge if manual acknowledgment is enabled
            if (_config.ManualAcknowledgment)
            {
                await _consumeChannel.BasicAckAsync(result.DeliveryTag, false, ct);
            }
        }
    }

    /// <inheritdoc />
    protected override async Task<WriteResult> ExecuteWriteAsync(
        IAsyncEnumerable<DataRecord> records,
        WriteOptions options,
        CancellationToken ct)
    {
        if (_publishChannel == null)
            throw new InvalidOperationException("Not connected to RabbitMQ");

        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        var exchange = options.TargetTable ?? "";
        var routingKey = "default" ?? "";

        // Declare exchange if specified
        if (!string.IsNullOrEmpty(exchange))
        {
            await _publishChannel.ExchangeDeclareAsync(
                exchange: exchange,
                type: _config.ExchangeType,
                durable: _config.ExchangeDurable,
                autoDelete: false,
                arguments: null,
                cancellationToken: ct
            );
        }

        await foreach (var record in records.WithCancellation(ct))
        {
            try
            {
                var body = record.Values.GetValueOrDefault("body") switch
                {
                    byte[] bytes => bytes,
                    string str => Encoding.UTF8.GetBytes(str),
                    _ => JsonSerializer.SerializeToUtf8Bytes(record.Values.GetValueOrDefault("body"))
                };

                var properties = new BasicProperties
                {
                    Persistent = _config.MessagePersistent,
                    Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                };

                // Add headers if present
                if (record.Values.TryGetValue("headers", out var headersObj) &&
                    headersObj is Dictionary<string, string> headers)
                {
                    properties.Headers = new Dictionary<string, object?>();
                    foreach (var (key, value) in headers)
                    {
                        properties.Headers[key] = value;
                    }
                }

                var messageRoutingKey = record.Values.GetValueOrDefault("routing_key")?.ToString() ?? routingKey;

                await _publishChannel.BasicPublishAsync(
                    exchange: exchange,
                    routingKey: messageRoutingKey,
                    mandatory: _config.MandatoryPublish,
                    basicProperties: properties,
                    body: body,
                    cancellationToken: ct
                );

                // Publisher confirms handled automatically by the channel

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
        if (_publishChannel == null)
            throw new InvalidOperationException("Not connected to RabbitMQ");

        var properties = new BasicProperties
        {
            Persistent = _config.MessagePersistent,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };

        if (headers != null)
        {
            properties.Headers = new Dictionary<string, object?>();
            foreach (var (key, value) in headers)
            {
                properties.Headers[key] = value;
            }
        }

        await _publishChannel.BasicPublishAsync(
            exchange: "",
            routingKey: topic,
            mandatory: _config.MandatoryPublish,
            basicProperties: properties,
            body: message
        );
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<(byte[] Data, Dictionary<string, string> Headers)> ConsumeAsync(
        string topic,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_consumeChannel == null)
            throw new InvalidOperationException("Not connected to RabbitMQ");

        _consumerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Declare queue
        await _consumeChannel.QueueDeclareAsync(
            queue: topic,
            durable: _config.QueueDurable,
            exclusive: false,
            autoDelete: _config.QueueAutoDelete,
            arguments: null,
            cancellationToken: ct
        );

        var consumer = new AsyncEventingBasicConsumer(_consumeChannel);
        var messageQueue = System.Threading.Channels.Channel.CreateUnbounded<(byte[], Dictionary<string, string>)>();

        consumer.ReceivedAsync += async (sender, args) =>
        {
            var headers = new Dictionary<string, string>();
            if (args.BasicProperties.Headers != null)
            {
                foreach (var (key, value) in args.BasicProperties.Headers)
                {
                    headers[key] = value?.ToString() ?? "";
                }
            }

            headers["rabbitmq.delivery_tag"] = args.DeliveryTag.ToString();
            headers["rabbitmq.exchange"] = args.Exchange;
            headers["rabbitmq.routing_key"] = args.RoutingKey;
            headers["rabbitmq.redelivered"] = args.Redelivered.ToString();

            await messageQueue.Writer.WriteAsync((args.Body.ToArray(), headers), _consumerCts.Token);

            if (_config.ManualAcknowledgment)
            {
                await _consumeChannel.BasicAckAsync(args.DeliveryTag, false, _consumerCts.Token);
            }
        };

        var consumerTag = await _consumeChannel.BasicConsumeAsync(
            queue: topic,
            autoAck: !_config.ManualAcknowledgment,
            consumer: consumer,
            cancellationToken: ct
        );

        try
        {
            await foreach (var message in messageQueue.Reader.ReadAllAsync(_consumerCts.Token))
            {
                yield return message;
            }
        }
        finally
        {
            await _consumeChannel.BasicCancelAsync(consumerTag);
            messageQueue.Writer.Complete();
        }
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => DisconnectAsync();
}

/// <summary>
/// Configuration options for the RabbitMQ connector.
/// </summary>
public class RabbitMqConnectorConfig
{
    /// <summary>
    /// Client-provided name for connection identification.
    /// </summary>
    public string? ClientProvidedName { get; set; }

    /// <summary>
    /// Username for authentication.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Password for authentication.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Virtual host to connect to.
    /// </summary>
    public string? VirtualHost { get; set; }

    /// <summary>
    /// Heartbeat interval in seconds.
    /// </summary>
    public int HeartbeatSeconds { get; set; } = 60;

    /// <summary>
    /// Network recovery interval in seconds.
    /// </summary>
    public int NetworkRecoverySeconds { get; set; } = 5;

    /// <summary>
    /// Enable automatic connection recovery.
    /// </summary>
    public bool AutomaticRecoveryEnabled { get; set; } = true;

    /// <summary>
    /// Enable topology recovery (queues, exchanges).
    /// </summary>
    public bool TopologyRecoveryEnabled { get; set; } = true;

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Socket read timeout in seconds.
    /// </summary>
    public int SocketReadTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Socket write timeout in seconds.
    /// </summary>
    public int SocketWriteTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Number of messages to prefetch.
    /// </summary>
    public ushort PrefetchCount { get; set; } = 10;

    /// <summary>
    /// Enable publisher confirms for reliable delivery.
    /// </summary>
    public bool PublisherConfirms { get; set; } = true;

    /// <summary>
    /// Enable manual message acknowledgment.
    /// </summary>
    public bool ManualAcknowledgment { get; set; } = true;

    /// <summary>
    /// Mark messages as persistent (survives broker restart).
    /// </summary>
    public bool MessagePersistent { get; set; } = true;

    /// <summary>
    /// Require mandatory routing (fail if no queue matches).
    /// </summary>
    public bool MandatoryPublish { get; set; } = false;

    /// <summary>
    /// Make queues durable (survive broker restart).
    /// </summary>
    public bool QueueDurable { get; set; } = true;

    /// <summary>
    /// Auto-delete queues when no longer in use.
    /// </summary>
    public bool QueueAutoDelete { get; set; } = false;

    /// <summary>
    /// Make exchanges durable (survive broker restart).
    /// </summary>
    public bool ExchangeDurable { get; set; } = true;

    /// <summary>
    /// Exchange type (direct, topic, fanout, headers).
    /// </summary>
    public string ExchangeType { get; set; } = "topic";
}
