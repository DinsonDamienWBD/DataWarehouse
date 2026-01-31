using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready Apache Kafka messaging connector plugin using Confluent.Kafka.
/// Provides pub/sub messaging with full consumer group support, exactly-once semantics,
/// and comprehensive error handling.
/// </summary>
public class KafkaMessagingConnectorPlugin : MessagingConnectorPluginBase
{
    private IProducer<string, byte[]>? _producer;
    private IConsumer<string, byte[]>? _consumer;
    private IAdminClient? _adminClient;
    private string? _bootstrapServers;
    private string? _groupId;
    private KafkaConnectorConfig _config = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private CancellationTokenSource? _consumerCts;

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.kafka";

    /// <inheritdoc />
    public override string Name => "Apache Kafka Connector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "kafka";

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
    /// <param name="config">Kafka-specific configuration.</param>
    public void Configure(KafkaConnectorConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <inheritdoc />
    protected override async Task<ConnectionResult> EstablishConnectionAsync(ConnectorConfig config, CancellationToken ct)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            _bootstrapServers = config.ConnectionString;
            _groupId = config.Properties.GetValueOrDefault("GroupId", _config.DefaultGroupId);

            if (string.IsNullOrWhiteSpace(_bootstrapServers))
            {
                return new ConnectionResult(false, "Bootstrap servers are required", null);
            }

            // Build producer configuration
            var producerConfig = new ProducerConfig
            {
                BootstrapServers = _bootstrapServers,
                ClientId = _config.ClientId ?? $"datawarehouse-producer-{Guid.NewGuid():N}",
                Acks = _config.RequireAcks ? Acks.All : Acks.Leader,
                EnableIdempotence = _config.EnableIdempotence,
                MessageSendMaxRetries = _config.MaxRetries,
                RetryBackoffMs = _config.RetryBackoffMs,
                LingerMs = _config.LingerMs,
                BatchSize = _config.BatchSize,
                CompressionType = MapCompressionType(_config.CompressionType)
            };

            // Add security settings if configured
            if (!string.IsNullOrEmpty(_config.SecurityProtocol))
            {
                producerConfig.SecurityProtocol = ParseSecurityProtocol(_config.SecurityProtocol);
            }
            if (!string.IsNullOrEmpty(_config.SaslMechanism))
            {
                producerConfig.SaslMechanism = ParseSaslMechanism(_config.SaslMechanism);
                producerConfig.SaslUsername = _config.SaslUsername;
                producerConfig.SaslPassword = _config.SaslPassword;
            }

            // Build consumer configuration
            var consumerConfig = new ConsumerConfig
            {
                BootstrapServers = _bootstrapServers,
                GroupId = _groupId,
                ClientId = _config.ClientId ?? $"datawarehouse-consumer-{Guid.NewGuid():N}",
                AutoOffsetReset = _config.AutoOffsetReset == "earliest" ? AutoOffsetReset.Earliest : AutoOffsetReset.Latest,
                EnableAutoCommit = _config.EnableAutoCommit,
                AutoCommitIntervalMs = _config.AutoCommitIntervalMs,
                SessionTimeoutMs = _config.SessionTimeoutMs,
                MaxPollIntervalMs = _config.MaxPollIntervalMs,
                FetchMinBytes = _config.FetchMinBytes,
                FetchMaxBytes = _config.FetchMaxBytes
            };

            // Add security settings to consumer
            if (!string.IsNullOrEmpty(_config.SecurityProtocol))
            {
                consumerConfig.SecurityProtocol = ParseSecurityProtocol(_config.SecurityProtocol);
            }
            if (!string.IsNullOrEmpty(_config.SaslMechanism))
            {
                consumerConfig.SaslMechanism = ParseSaslMechanism(_config.SaslMechanism);
                consumerConfig.SaslUsername = _config.SaslUsername;
                consumerConfig.SaslPassword = _config.SaslPassword;
            }

            // Build admin client configuration
            var adminConfig = new AdminClientConfig
            {
                BootstrapServers = _bootstrapServers
            };
            if (!string.IsNullOrEmpty(_config.SecurityProtocol))
            {
                adminConfig.SecurityProtocol = ParseSecurityProtocol(_config.SecurityProtocol);
            }

            // Create clients
            _producer = new ProducerBuilder<string, byte[]>(producerConfig).Build();
            _consumer = new ConsumerBuilder<string, byte[]>(consumerConfig).Build();
            _adminClient = new AdminClientBuilder(adminConfig).Build();

            // Verify connection by fetching cluster metadata
            var metadata = _adminClient.GetMetadata(TimeSpan.FromSeconds(10));

            var serverInfo = new Dictionary<string, object>
            {
                ["BootstrapServers"] = _bootstrapServers,
                ["GroupId"] = _groupId ?? "none",
                ["OriginatingBrokerId"] = metadata.OriginatingBrokerId,
                ["OriginatingBrokerName"] = metadata.OriginatingBrokerName,
                ["BrokerCount"] = metadata.Brokers.Count,
                ["TopicCount"] = metadata.Topics.Count,
                ["Brokers"] = metadata.Brokers.Select(b => $"{b.Host}:{b.Port}").ToArray()
            };

            return new ConnectionResult(true, null, serverInfo);
        }
        catch (KafkaException ex)
        {
            return new ConnectionResult(false, $"Kafka connection failed: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            return new ConnectionResult(false, $"Connection failed: {ex.Message}", null);
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

            _producer?.Flush(TimeSpan.FromSeconds(10));
            _producer?.Dispose();
            _producer = null;

            _consumer?.Close();
            _consumer?.Dispose();
            _consumer = null;

            _adminClient?.Dispose();
            _adminClient = null;

            _bootstrapServers = null;
            _groupId = null;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override Task<bool> PingAsync()
    {
        if (_adminClient == null) return Task.FromResult(false);

        try
        {
            var metadata = _adminClient.GetMetadata(TimeSpan.FromSeconds(5));
            return Task.FromResult(metadata.Brokers.Count > 0);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    /// <inheritdoc />
    protected override Task<DataSchema> FetchSchemaAsync()
    {
        if (_adminClient == null)
            throw new InvalidOperationException("Not connected to Kafka");

        var metadata = _adminClient.GetMetadata(TimeSpan.FromSeconds(10));

        var topics = metadata.Topics
            .Where(t => !t.Topic.StartsWith("__")) // Exclude internal topics
            .Select(t => new
            {
                t.Topic,
                t.Partitions.Count,
                Partitions = t.Partitions.Select(p => new { p.PartitionId, p.Leader }).ToList()
            })
            .ToList();

        return Task.FromResult(new DataSchema(
            Name: "kafka-cluster",
            Fields: new[]
            {
                new DataSchemaField("key", "string", true, null, null),
                new DataSchemaField("value", "bytes", false, null, null),
                new DataSchemaField("timestamp", "timestamp", false, null, null),
                new DataSchemaField("partition", "int", false, null, null),
                new DataSchemaField("offset", "long", false, null, null),
                new DataSchemaField("topic", "string", false, null, null),
                new DataSchemaField("headers", "map", true, null, null)
            },
            PrimaryKeys: new[] { "topic", "partition", "offset" },
            Metadata: new Dictionary<string, object>
            {
                ["TopicCount"] = topics.Count,
                ["Topics"] = topics.Select(t => t.Topic).ToArray(),
                ["BrokerCount"] = metadata.Brokers.Count
            }
        ));
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<DataRecord> ExecuteReadAsync(
        DataQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_consumer == null)
            throw new InvalidOperationException("Not connected to Kafka");

        var topic = query.TableOrCollection ?? throw new ArgumentException("Topic is required");
        var limit = query.Limit ?? int.MaxValue;

        // Subscribe to the topic
        _consumer.Subscribe(topic);

        long count = 0;

        try
        {
            while (!ct.IsCancellationRequested && count < limit)
            {
                var consumeResult = _consumer.Consume(TimeSpan.FromMilliseconds(_config.ConsumeTimeoutMs));

                if (consumeResult == null) continue;
                if (consumeResult.IsPartitionEOF) break;

                var headers = new Dictionary<string, string>();
                if (consumeResult.Message.Headers != null)
                {
                    foreach (var header in consumeResult.Message.Headers)
                    {
                        headers[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes());
                    }
                }

                yield return new DataRecord(
                    Values: new Dictionary<string, object?>
                    {
                        ["key"] = consumeResult.Message.Key,
                        ["value"] = consumeResult.Message.Value,
                        ["timestamp"] = consumeResult.Message.Timestamp.UtcDateTime,
                        ["partition"] = consumeResult.Partition.Value,
                        ["offset"] = consumeResult.Offset.Value,
                        ["topic"] = consumeResult.Topic,
                        ["headers"] = headers
                    },
                    Position: consumeResult.Offset.Value,
                    Timestamp: new DateTimeOffset(consumeResult.Message.Timestamp.UtcDateTime)
                );

                count++;

                // Commit offset if auto-commit is disabled
                if (!_config.EnableAutoCommit)
                {
                    _consumer.Commit(consumeResult);
                }
            }
        }
        finally
        {
            _consumer.Unsubscribe();
        }
    }

    /// <inheritdoc />
    protected override async Task<WriteResult> ExecuteWriteAsync(
        IAsyncEnumerable<DataRecord> records,
        WriteOptions options,
        CancellationToken ct)
    {
        if (_producer == null)
            throw new InvalidOperationException("Not connected to Kafka");

        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        var topic = options.TargetTable ?? throw new ArgumentException("Topic is required");

        await foreach (var record in records.WithCancellation(ct))
        {
            try
            {
                var key = record.Values.GetValueOrDefault("key")?.ToString() ?? "";
                var value = record.Values.GetValueOrDefault("value") switch
                {
                    byte[] bytes => bytes,
                    string str => Encoding.UTF8.GetBytes(str),
                    _ => JsonSerializer.SerializeToUtf8Bytes(record.Values.GetValueOrDefault("value"))
                };

                var message = new Message<string, byte[]>
                {
                    Key = key,
                    Value = value
                };

                // Add headers if present
                if (record.Values.TryGetValue("headers", out var headersObj) &&
                    headersObj is Dictionary<string, string> headers)
                {
                    message.Headers = new Headers();
                    foreach (var (hKey, hValue) in headers)
                    {
                        message.Headers.Add(hKey, Encoding.UTF8.GetBytes(hValue));
                    }
                }

                var deliveryResult = await _producer.ProduceAsync(topic, message, ct);

                if (deliveryResult.Status == PersistenceStatus.Persisted ||
                    deliveryResult.Status == PersistenceStatus.PossiblyPersisted)
                {
                    written++;
                }
                else
                {
                    failed++;
                    errors.Add($"Record at position {record.Position}: Delivery status {deliveryResult.Status}");
                }
            }
            catch (ProduceException<string, byte[]> ex)
            {
                failed++;
                errors.Add($"Record at position {record.Position}: {ex.Error.Reason}");
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Record at position {record.Position}: {ex.Message}");
            }
        }

        // Flush to ensure all messages are sent
        _producer.Flush(TimeSpan.FromSeconds(30));

        return new WriteResult(written, failed, errors.Count > 0 ? errors.ToArray() : null);
    }

    /// <inheritdoc />
    protected override async Task PublishAsync(string topic, byte[] message, Dictionary<string, string>? headers)
    {
        if (_producer == null)
            throw new InvalidOperationException("Not connected to Kafka");

        var kafkaMessage = new Message<string, byte[]>
        {
            Key = Guid.NewGuid().ToString("N"),
            Value = message
        };

        if (headers != null)
        {
            kafkaMessage.Headers = new Headers();
            foreach (var (key, value) in headers)
            {
                kafkaMessage.Headers.Add(key, Encoding.UTF8.GetBytes(value));
            }
        }

        var result = await _producer.ProduceAsync(topic, kafkaMessage);

        if (result.Status != PersistenceStatus.Persisted &&
            result.Status != PersistenceStatus.PossiblyPersisted)
        {
            throw new InvalidOperationException($"Message delivery failed: {result.Status}");
        }
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<(byte[] Data, Dictionary<string, string> Headers)> ConsumeAsync(
        string topic,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_consumer == null)
            throw new InvalidOperationException("Not connected to Kafka");

        _consumerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _consumer.Subscribe(topic);

        try
        {
            while (!_consumerCts.Token.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]>? consumeResult = null;

                try
                {
                    consumeResult = _consumer.Consume(_consumerCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (consumeResult == null || consumeResult.IsPartitionEOF)
                {
                    await Task.Delay(100, _consumerCts.Token);
                    continue;
                }

                var headers = new Dictionary<string, string>();
                if (consumeResult.Message.Headers != null)
                {
                    foreach (var header in consumeResult.Message.Headers)
                    {
                        headers[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes());
                    }
                }

                // Add Kafka metadata to headers
                headers["kafka.offset"] = consumeResult.Offset.Value.ToString();
                headers["kafka.partition"] = consumeResult.Partition.Value.ToString();
                headers["kafka.topic"] = consumeResult.Topic;
                headers["kafka.timestamp"] = consumeResult.Message.Timestamp.UtcDateTime.ToString("O");

                yield return (consumeResult.Message.Value, headers);

                if (!_config.EnableAutoCommit)
                {
                    _consumer.Commit(consumeResult);
                }
            }
        }
        finally
        {
            _consumer.Unsubscribe();
        }
    }

    /// <summary>
    /// Creates a topic with the specified configuration.
    /// </summary>
    /// <param name="topic">Topic name.</param>
    /// <param name="partitions">Number of partitions.</param>
    /// <param name="replicationFactor">Replication factor.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CreateTopicAsync(string topic, int partitions = 1, short replicationFactor = 1, CancellationToken ct = default)
    {
        if (_adminClient == null)
            throw new InvalidOperationException("Not connected to Kafka");

        var topicSpec = new TopicSpecification
        {
            Name = topic,
            NumPartitions = partitions,
            ReplicationFactor = replicationFactor
        };

        await _adminClient.CreateTopicsAsync(new[] { topicSpec });
    }

    /// <summary>
    /// Deletes a topic.
    /// </summary>
    /// <param name="topic">Topic name.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DeleteTopicAsync(string topic, CancellationToken ct = default)
    {
        if (_adminClient == null)
            throw new InvalidOperationException("Not connected to Kafka");

        await _adminClient.DeleteTopicsAsync(new[] { topic });
    }

    /// <summary>
    /// Gets topic metadata.
    /// </summary>
    /// <param name="topic">Topic name.</param>
    /// <returns>Topic metadata.</returns>
    public KafkaTopicInfo GetTopicInfo(string topic)
    {
        if (_adminClient == null)
            throw new InvalidOperationException("Not connected to Kafka");

        var metadata = _adminClient.GetMetadata(topic, TimeSpan.FromSeconds(10));
        var topicMeta = metadata.Topics.FirstOrDefault(t => t.Topic == topic);

        if (topicMeta == null)
            throw new ArgumentException($"Topic '{topic}' not found");

        return new KafkaTopicInfo
        {
            Topic = topicMeta.Topic,
            PartitionCount = topicMeta.Partitions.Count,
            Partitions = topicMeta.Partitions.Select(p => new KafkaPartitionInfo
            {
                PartitionId = p.PartitionId,
                Leader = p.Leader,
                Replicas = p.Replicas,
                InSyncReplicas = p.InSyncReplicas
            }).ToList()
        };
    }

    /// <summary>
    /// Seeks to a specific offset for a partition.
    /// </summary>
    /// <param name="topic">Topic name.</param>
    /// <param name="partition">Partition number.</param>
    /// <param name="offset">Offset to seek to.</param>
    public void Seek(string topic, int partition, long offset)
    {
        if (_consumer == null)
            throw new InvalidOperationException("Not connected to Kafka");

        _consumer.Assign(new[] { new TopicPartitionOffset(topic, new Partition(partition), new Offset(offset)) });
    }

    /// <summary>
    /// Commits the current offsets.
    /// </summary>
    public void CommitOffsets()
    {
        _consumer?.Commit();
    }

    private static CompressionType MapCompressionType(string? compression)
    {
        return compression?.ToLowerInvariant() switch
        {
            "gzip" => CompressionType.Gzip,
            "snappy" => CompressionType.Snappy,
            "lz4" => CompressionType.Lz4,
            "zstd" => CompressionType.Zstd,
            _ => CompressionType.None
        };
    }

    private static SecurityProtocol ParseSecurityProtocol(string protocol)
    {
        return protocol.ToUpperInvariant() switch
        {
            "PLAINTEXT" => SecurityProtocol.Plaintext,
            "SSL" => SecurityProtocol.Ssl,
            "SASL_PLAINTEXT" => SecurityProtocol.SaslPlaintext,
            "SASL_SSL" => SecurityProtocol.SaslSsl,
            _ => SecurityProtocol.Plaintext
        };
    }

    private static SaslMechanism ParseSaslMechanism(string mechanism)
    {
        return mechanism.ToUpperInvariant() switch
        {
            "PLAIN" => SaslMechanism.Plain,
            "SCRAM-SHA-256" => SaslMechanism.ScramSha256,
            "SCRAM-SHA-512" => SaslMechanism.ScramSha512,
            "OAUTHBEARER" => SaslMechanism.OAuthBearer,
            _ => SaslMechanism.Plain
        };
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => DisconnectAsync();
}

/// <summary>
/// Configuration options for the Kafka connector.
/// </summary>
public class KafkaConnectorConfig
{
    /// <summary>
    /// Default consumer group ID.
    /// </summary>
    public string DefaultGroupId { get; set; } = "datawarehouse-consumer";

    /// <summary>
    /// Client identifier for the Kafka client.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Whether to require acknowledgments from all replicas.
    /// </summary>
    public bool RequireAcks { get; set; } = true;

    /// <summary>
    /// Whether to enable idempotent producer.
    /// </summary>
    public bool EnableIdempotence { get; set; } = true;

    /// <summary>
    /// Maximum number of retries for failed sends.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Backoff time between retries in milliseconds.
    /// </summary>
    public int RetryBackoffMs { get; set; } = 100;

    /// <summary>
    /// Time to wait before sending a batch in milliseconds.
    /// </summary>
    public int LingerMs { get; set; } = 5;

    /// <summary>
    /// Maximum batch size in bytes.
    /// </summary>
    public int BatchSize { get; set; } = 16384;

    /// <summary>
    /// Compression type (none, gzip, snappy, lz4, zstd).
    /// </summary>
    public string? CompressionType { get; set; }

    /// <summary>
    /// Auto offset reset behavior (earliest, latest).
    /// </summary>
    public string AutoOffsetReset { get; set; } = "earliest";

    /// <summary>
    /// Whether to enable automatic offset commits.
    /// </summary>
    public bool EnableAutoCommit { get; set; } = true;

    /// <summary>
    /// Interval for automatic offset commits in milliseconds.
    /// </summary>
    public int AutoCommitIntervalMs { get; set; } = 5000;

    /// <summary>
    /// Session timeout in milliseconds.
    /// </summary>
    public int SessionTimeoutMs { get; set; } = 45000;

    /// <summary>
    /// Maximum poll interval in milliseconds.
    /// </summary>
    public int MaxPollIntervalMs { get; set; } = 300000;

    /// <summary>
    /// Minimum bytes to fetch per request.
    /// </summary>
    public int FetchMinBytes { get; set; } = 1;

    /// <summary>
    /// Maximum bytes to fetch per request.
    /// </summary>
    public int FetchMaxBytes { get; set; } = 52428800;

    /// <summary>
    /// Consume timeout in milliseconds.
    /// </summary>
    public int ConsumeTimeoutMs { get; set; } = 1000;

    /// <summary>
    /// Security protocol (PLAINTEXT, SSL, SASL_PLAINTEXT, SASL_SSL).
    /// </summary>
    public string? SecurityProtocol { get; set; }

    /// <summary>
    /// SASL mechanism (PLAIN, SCRAM-SHA-256, SCRAM-SHA-512, OAUTHBEARER).
    /// </summary>
    public string? SaslMechanism { get; set; }

    /// <summary>
    /// SASL username for authentication.
    /// </summary>
    public string? SaslUsername { get; set; }

    /// <summary>
    /// SASL password for authentication.
    /// </summary>
    public string? SaslPassword { get; set; }
}

/// <summary>
/// Information about a Kafka topic.
/// </summary>
public class KafkaTopicInfo
{
    /// <summary>
    /// Topic name.
    /// </summary>
    public string Topic { get; set; } = string.Empty;

    /// <summary>
    /// Number of partitions.
    /// </summary>
    public int PartitionCount { get; set; }

    /// <summary>
    /// Partition details.
    /// </summary>
    public List<KafkaPartitionInfo> Partitions { get; set; } = new();
}

/// <summary>
/// Information about a Kafka partition.
/// </summary>
public class KafkaPartitionInfo
{
    /// <summary>
    /// Partition ID.
    /// </summary>
    public int PartitionId { get; set; }

    /// <summary>
    /// Leader broker ID.
    /// </summary>
    public int Leader { get; set; }

    /// <summary>
    /// Replica broker IDs.
    /// </summary>
    public int[] Replicas { get; set; } = Array.Empty<int>();

    /// <summary>
    /// In-sync replica broker IDs.
    /// </summary>
    public int[] InSyncReplicas { get; set; } = Array.Empty<int>();
}
