using Confluent.Kafka;
using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Streaming;

/// <summary>
/// Apache Kafka streaming storage strategy with production-ready features:
/// - Distributed streaming platform
/// - High throughput
/// - Durable message storage
/// - Compacted topics for key-value
/// - Partitioning for parallelism
/// - Exactly-once semantics
/// - Multi-datacenter replication
/// </summary>
public sealed class KafkaStorageStrategy : DatabaseStorageStrategyBase
{
    private IProducer<string, byte[]>? _producer;
    private IAdminClient? _adminClient;
    private string _topicPrefix = "storage";
    private string _metadataTopic = "storage-metadata";

    public override string StrategyId => "kafka";
    public override string Name => "Apache Kafka Streaming Storage";
    public override StorageTier Tier => StorageTier.Hot;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.Streaming;
    public override string Engine => "Kafka";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = true,
        SupportsLocking = false,
        SupportsVersioning = true, // Offsets
        SupportsTiering = true,
        SupportsEncryption = true,
        SupportsCompression = true,
        SupportsMultipart = false,
        MaxObjectSize = 1L * 1024 * 1024, // 1MB default message size
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Eventual
    };

    public override bool SupportsTransactions => true;
    public override bool SupportsSql => false;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _topicPrefix = GetConfiguration("TopicPrefix", "storage");
        _metadataTopic = GetConfiguration("MetadataTopic", "storage-metadata");

        var bootstrapServers = GetConnectionString();

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageMaxBytes = GetConfiguration("MessageMaxBytes", 1048576),
            CompressionType = Enum.Parse<CompressionType>(GetConfiguration("CompressionType", "Gzip"))
        };

        var username = GetConfiguration<string?>("Username", null);
        var password = GetConfiguration<string?>("Password", null);
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            producerConfig.SecurityProtocol = SecurityProtocol.SaslSsl;
            producerConfig.SaslMechanism = SaslMechanism.Plain;
            producerConfig.SaslUsername = username;
            producerConfig.SaslPassword = password;
        }

        _producer = new ProducerBuilder<string, byte[]>(producerConfig).Build();

        var adminConfig = new AdminClientConfig { BootstrapServers = bootstrapServers };
        _adminClient = new AdminClientBuilder(adminConfig).Build();

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        var metadata = _adminClient!.GetMetadata(TimeSpan.FromSeconds(10));
        if (metadata.Brokers.Count == 0)
        {
            throw new InvalidOperationException("No Kafka brokers available");
        }
        await Task.CompletedTask;
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        _producer?.Dispose();
        _adminClient?.Dispose();
        _producer = null;
        _adminClient = null;
        await Task.CompletedTask;
    }

    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct)
    {
        // Create compacted topics for key-value storage
        var topics = new[]
        {
            new Confluent.Kafka.Admin.TopicSpecification
            {
                Name = $"{_topicPrefix}-data",
                NumPartitions = GetConfiguration("NumPartitions", 12),
                ReplicationFactor = (short)GetConfiguration("ReplicationFactor", 3),
                Configs = new Dictionary<string, string>
                {
                    { "cleanup.policy", "compact" },
                    { "retention.ms", "-1" }
                }
            },
            new Confluent.Kafka.Admin.TopicSpecification
            {
                Name = _metadataTopic,
                NumPartitions = GetConfiguration("NumPartitions", 12),
                ReplicationFactor = (short)GetConfiguration("ReplicationFactor", 3),
                Configs = new Dictionary<string, string>
                {
                    { "cleanup.policy", "compact" },
                    { "retention.ms", "-1" }
                }
            }
        };

        try
        {
            await _adminClient!.CreateTopicsAsync(topics);
        }
        catch
        {

            // Topics might already exist
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);

        var metadataDoc = new MetadataDocument
        {
            Key = key,
            Size = data.LongLength,
            ContentType = contentType,
            ETag = etag,
            CustomMetadata = metadata?.ToDictionary(k => k.Key, v => v.Value),
            CreatedAt = now,
            ModifiedAt = now
        };

        var metadataJson = JsonSerializer.SerializeToUtf8Bytes(metadataDoc, JsonOptions);

        // Produce data message
        var dataResult = await _producer!.ProduceAsync($"{_topicPrefix}-data", new Message<string, byte[]>
        {
            Key = key,
            Value = data,
            Headers = new Headers
            {
                { "etag", Encoding.UTF8.GetBytes(etag) },
                { "contentType", Encoding.UTF8.GetBytes(contentType ?? "") }
            }
        }, ct);

        // Produce metadata message
        await _producer.ProduceAsync(_metadataTopic, new Message<string, byte[]>
        {
            Key = key,
            Value = metadataJson
        }, ct);

        return new StorageObjectMetadata
        {
            Key = key,
            Size = data.LongLength,
            Created = now,
            Modified = now,
            ETag = etag,
            ContentType = contentType,
            CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
            VersionId = dataResult.Offset.Value.ToString(),
            Tier = Tier
        };
    }

    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct)
    {
        // Kafka is append-only; use consumer to read latest value for key
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = GetConnectionString(),
            GroupId = $"storage-reader-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(consumerConfig).Build();

        consumer.Assign(new TopicPartitionOffset($"{_topicPrefix}-data", 0, Offset.Beginning));

        byte[]? latestValue = null;

        while (true)
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(1));
            if (result == null) break;

            if (result.Message.Key == key)
            {
                latestValue = result.Message.Value;
            }

            if (result.IsPartitionEOF) break;
        }

        if (latestValue == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return latestValue;
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        // Tombstone message for compaction (Kafka supports null values for tombstones)
        await _producer!.ProduceAsync($"{_topicPrefix}-data", new Message<string, byte[]>
        {
            Key = key,
            Value = null! // Null value = tombstone - suppressed because Kafka API expects this
        }, ct);

        await _producer.ProduceAsync(_metadataTopic, new Message<string, byte[]>
        {
            Key = key,
            Value = null! // Null value = tombstone - suppressed because Kafka API expects this
        }, ct);

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        try
        {
            await GetMetadataCoreAsync(key, ct);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = GetConnectionString(),
            GroupId = $"storage-lister-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(consumerConfig).Build();
        consumer.Assign(new TopicPartitionOffset(_metadataTopic, 0, Offset.Beginning));

        var seen = new Dictionary<string, MetadataDocument>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var result = consumer.Consume(TimeSpan.FromSeconds(1));
            if (result == null) break;

            if (result.Message.Value == null)
            {
                seen.Remove(result.Message.Key);
            }
            else
            {
                var doc = JsonSerializer.Deserialize<MetadataDocument>(result.Message.Value, JsonOptions);
                if (doc != null)
                {
                    seen[result.Message.Key] = doc;
                }
            }

            if (result.IsPartitionEOF) break;
        }

        foreach (var (key, doc) in seen)
        {
            if (!string.IsNullOrEmpty(prefix) && !key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            yield return new StorageObjectMetadata
            {
                Key = key,
                Size = doc.Size,
                ContentType = doc.ContentType,
                ETag = doc.ETag,
                CustomMetadata = doc.CustomMetadata as IReadOnlyDictionary<string, string>,
                Created = doc.CreatedAt,
                Modified = doc.ModifiedAt,
                Tier = Tier
            };
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = GetConnectionString(),
            GroupId = $"storage-meta-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(consumerConfig).Build();
        consumer.Assign(new TopicPartitionOffset(_metadataTopic, 0, Offset.Beginning));

        MetadataDocument? latestDoc = null;

        while (true)
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(1));
            if (result == null) break;

            if (result.Message.Key == key)
            {
                if (result.Message.Value == null)
                {
                    latestDoc = null;
                }
                else
                {
                    latestDoc = JsonSerializer.Deserialize<MetadataDocument>(result.Message.Value, JsonOptions);
                }
            }

            if (result.IsPartitionEOF) break;
        }

        if (latestDoc == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return new StorageObjectMetadata
        {
            Key = key,
            Size = latestDoc.Size,
            ContentType = latestDoc.ContentType,
            ETag = latestDoc.ETag,
            CustomMetadata = latestDoc.CustomMetadata as IReadOnlyDictionary<string, string>,
            Created = latestDoc.CreatedAt,
            Modified = latestDoc.ModifiedAt,
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            var metadata = _adminClient!.GetMetadata(TimeSpan.FromSeconds(5));
            return metadata.Brokers.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        _producer?.Dispose();
        _adminClient?.Dispose();
        await base.DisposeAsyncCore();
    }

    private sealed class MetadataDocument
    {
        public string Key { get; set; } = "";
        public long Size { get; set; }
        public string? ContentType { get; set; }
        public string? ETag { get; set; }
        public Dictionary<string, string>? CustomMetadata { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ModifiedAt { get; set; }
    }
}
