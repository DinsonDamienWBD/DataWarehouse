using DataWarehouse.SDK.Contracts.Storage;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Specialized
{
    /// <summary>
    /// Redis storage strategy with production-ready features:
    /// - Redis Cluster support for distributed storage
    /// - Key expiration (TTL) management
    /// - Redis Streams for large object chunking (>512MB)
    /// - Pub/sub notifications for storage events
    /// - Connection pooling and multiplexing
    /// - Automatic failover and retry logic
    /// - Compression for large payloads
    /// - Atomic operations with transactions
    /// - Hash-based metadata storage
    /// - Sorted sets for prefix-based listing
    /// </summary>
    public class RedisStrategy : UltimateStorageStrategyBase
    {
        private ConnectionMultiplexer? _redis;
        private IDatabase? _database;
        private ISubscriber? _subscriber;
        private string _connectionString = string.Empty;
        private int _databaseIndex = 0;
        private TimeSpan? _defaultExpiration = null;
        private int _chunkSizeBytes = 10 * 1024 * 1024; // 10MB chunks for large objects
        private long _largeObjectThreshold = 512 * 1024 * 1024; // 512MB
        private bool _enablePubSub = false;
        private string _pubSubChannel = "storage:events";
        private bool _enableCompression = false;
        private int _compressionThreshold = 1024; // 1KB
        private bool _useCluster = false;
        private string _keyPrefix = "storage:";
        private string _metadataKeyPrefix = "meta:";
        private string _indexSetKey = "storage:index";
        private readonly SemaphoreSlim _initLock = new(1, 1);

        public override string StrategyId => "redis";
        public override string Name => "Redis Storage";
        public override StorageTier Tier => StorageTier.Hot; // In-memory storage is hot tier

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = true,
            SupportsVersioning = false,
            SupportsTiering = false,
            SupportsEncryption = false, // Redis supports TLS but not application-level encryption here
            SupportsCompression = _enableCompression,
            SupportsMultipart = true,
            MaxObjectSize = 512 * 1024 * 1024, // 512MB practical limit for Redis strings
            MaxObjects = null, // Limited by memory
            ConsistencyModel = ConsistencyModel.Strong
        };

        #region Initialization

        /// <summary>
        /// Initializes the Redis storage strategy.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            await _initLock.WaitAsync(ct);
            try
            {
                // Load configuration
                _connectionString = GetConfiguration<string>("ConnectionString")
                    ?? throw new InvalidOperationException("Redis ConnectionString is required");

                _databaseIndex = GetConfiguration("DatabaseIndex", 0);
                _keyPrefix = GetConfiguration("KeyPrefix", "storage:");
                _metadataKeyPrefix = GetConfiguration("MetadataKeyPrefix", "meta:");
                _indexSetKey = GetConfiguration("IndexSetKey", "storage:index");
                _chunkSizeBytes = GetConfiguration("ChunkSizeBytes", 10 * 1024 * 1024);
                _largeObjectThreshold = GetConfiguration("LargeObjectThreshold", 512L * 1024 * 1024);
                _enablePubSub = GetConfiguration("EnablePubSub", false);
                _pubSubChannel = GetConfiguration("PubSubChannel", "storage:events");
                _enableCompression = GetConfiguration("EnableCompression", false);
                _compressionThreshold = GetConfiguration("CompressionThreshold", 1024);
                _useCluster = GetConfiguration("UseCluster", false);

                // Parse TTL from configuration
                var ttlSeconds = GetConfiguration<int?>("DefaultExpirationSeconds", null);
                if (ttlSeconds.HasValue)
                {
                    _defaultExpiration = TimeSpan.FromSeconds(ttlSeconds.Value);
                }

                // Configure Redis connection options
                var configOptions = ConfigurationOptions.Parse(_connectionString);
                configOptions.AbortOnConnectFail = false;
                configOptions.ConnectRetry = 3;
                configOptions.ConnectTimeout = 5000;
                configOptions.SyncTimeout = 5000;
                configOptions.AsyncTimeout = 5000;
                configOptions.KeepAlive = 60;
                configOptions.DefaultDatabase = _databaseIndex;

                if (_useCluster)
                {
                    // Enable cluster mode
                    configOptions.EndPoints.Clear();
                    var endpoints = _connectionString.Split(',');
                    foreach (var endpoint in endpoints)
                    {
                        if (!string.IsNullOrWhiteSpace(endpoint))
                        {
                            configOptions.EndPoints.Add(endpoint.Trim());
                        }
                    }
                }

                // Connect to Redis
                _redis = await ConnectionMultiplexer.ConnectAsync(configOptions);
                _database = _redis.GetDatabase(_databaseIndex);

                // Setup pub/sub if enabled
                if (_enablePubSub)
                {
                    _subscriber = _redis.GetSubscriber();
                }

                // Test connection
                await _database.PingAsync();
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>
        /// Disposes Redis resources.
        /// </summary>
        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();

            if (_redis != null)
            {
                await _redis.CloseAsync();
                await _redis.DisposeAsync();
            }

            _initLock?.Dispose();
        }

        #endregion

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);
            ValidateStream(data);

            var redisKey = GetRedisKey(key);
            var metadataKey = GetMetadataKey(key);
            var startTime = DateTime.UtcNow;

            // Read stream into memory
            using var ms = new MemoryStream(65536);
            await data.CopyToAsync(ms, 81920, ct);
            var content = ms.ToArray();
            var originalSize = content.Length;

            // Check if we need to use chunked storage (Redis Streams)
            if (content.Length > _largeObjectThreshold)
            {
                return await StoreAsChunkedAsync(key, content, metadata, ct);
            }

            // Apply compression if enabled
            byte[] finalContent = content;
            var isCompressed = false;

            if (_enableCompression && content.Length > _compressionThreshold)
            {
                finalContent = await CompressDataAsync(content, ct);
                isCompressed = true;
            }

            // Use transaction for atomic operation
            var transaction = _database!.CreateTransaction();

            // Store data (expiration is a separate command)
            var setTask = transaction.StringSetAsync(redisKey, finalContent);
            Task? expireTask = null;
            if (_defaultExpiration.HasValue)
            {
                expireTask = transaction.KeyExpireAsync(redisKey, _defaultExpiration.Value);
            }

            // Store metadata
            var metadataEntries = new HashEntry[]
            {
                new HashEntry("size", originalSize),
                new HashEntry("created", startTime.Ticks),
                new HashEntry("modified", startTime.Ticks),
                new HashEntry("content-type", GetContentType(key)),
                new HashEntry("compressed", isCompressed),
                new HashEntry("chunked", false)
            };

            if (metadata != null)
            {
                var customEntries = metadata.Select(kvp => new HashEntry($"custom:{kvp.Key}", kvp.Value)).ToArray();
                metadataEntries = metadataEntries.Concat(customEntries).ToArray();
            }

            var metaTask = transaction.HashSetAsync(metadataKey, metadataEntries);

            // Add to index for listing
            var indexTask = transaction.SortedSetAddAsync(_indexSetKey, key, startTime.Ticks);

            // Execute transaction
            var success = await transaction.ExecuteAsync();

            if (!success)
            {
                throw new InvalidOperationException($"Failed to store object in Redis: {key}");
            }

            var tasks = new List<Task> { setTask, metaTask, indexTask };
            if (expireTask != null)
            {
                tasks.Add(expireTask);
            }
            await Task.WhenAll(tasks);

            // Update statistics
            IncrementBytesStored(originalSize);
            IncrementOperationCounter(StorageOperationType.Store);

            // Publish storage event if enabled
            if (_enablePubSub && _subscriber != null)
            {
                await _subscriber.PublishAsync(RedisChannel.Literal(_pubSubChannel),
                    $"stored:{key}:{originalSize}");
            }

            return new StorageObjectMetadata
            {
                Key = key,
                Size = originalSize,
                Created = startTime,
                Modified = startTime,
                ETag = GenerateETag(content),
                ContentType = GetContentType(key),
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var redisKey = GetRedisKey(key);
            var metadataKey = GetMetadataKey(key);

            // Get metadata to check if chunked
            var metadataEntries = await _database!.HashGetAllAsync(metadataKey);
            if (metadataEntries.Length == 0)
            {
                throw new FileNotFoundException($"Object not found in Redis: {key}");
            }

            var metadata = metadataEntries.ToDictionary(e => e.Name.ToString(), e => e.Value);
            var isChunked = metadata.TryGetValue("chunked", out var chunkedValue) && (bool)chunkedValue;

            if (isChunked)
            {
                return await RetrieveChunkedAsync(key, ct);
            }

            // Retrieve data
            var value = await _database.StringGetAsync(redisKey);

            if (!value.HasValue)
            {
                throw new FileNotFoundException($"Object not found in Redis: {key}");
            }

            var content = (byte[])value!;

            // Decompress if needed
            var isCompressed = metadata.TryGetValue("compressed", out var compressedValue) && (bool)compressedValue;
            if (isCompressed)
            {
                content = await DecompressDataAsync(content, ct);
            }

            // Update statistics
            IncrementBytesRetrieved(content.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            // Publish retrieval event if enabled
            if (_enablePubSub && _subscriber != null)
            {
                await _subscriber.PublishAsync(RedisChannel.Literal(_pubSubChannel),
                    $"retrieved:{key}:{content.Length}");
            }

            var ms = new MemoryStream(content);
            ms.Position = 0;
            return ms;
        }

        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var redisKey = GetRedisKey(key);
            var metadataKey = GetMetadataKey(key);

            // Get size before deletion for statistics
            long size = 0;
            try
            {
                var sizeValue = await _database!.HashGetAsync(metadataKey, "size");
                if (sizeValue.HasValue)
                {
                    size = (long)sizeValue;
                }
            }
            catch
            {
                // Ignore metadata retrieval errors
            }

            // Check if chunked
            var isChunkedValue = await _database!.HashGetAsync(metadataKey, "chunked");
            var isChunked = isChunkedValue.HasValue && (bool)isChunkedValue;

            if (isChunked)
            {
                await DeleteChunkedAsync(key, ct);
            }
            else
            {
                // Use transaction for atomic deletion
                var transaction = _database.CreateTransaction();

                var deleteDataTask = transaction.KeyDeleteAsync(redisKey);
                var deleteMetaTask = transaction.KeyDeleteAsync(metadataKey);
                var removeIndexTask = transaction.SortedSetRemoveAsync(_indexSetKey, key);

                await transaction.ExecuteAsync();
                await Task.WhenAll(deleteDataTask, deleteMetaTask, removeIndexTask);
            }

            // Update statistics
            if (size > 0)
            {
                IncrementBytesDeleted(size);
            }
            IncrementOperationCounter(StorageOperationType.Delete);

            // Publish deletion event if enabled
            if (_enablePubSub && _subscriber != null)
            {
                await _subscriber.PublishAsync(RedisChannel.Literal(_pubSubChannel),
                    $"deleted:{key}:{size}");
            }
        }

        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var redisKey = GetRedisKey(key);
            var exists = await _database!.KeyExistsAsync(redisKey);

            IncrementOperationCounter(StorageOperationType.Exists);

            return exists;
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();
            IncrementOperationCounter(StorageOperationType.List);

            // Get all keys from sorted set index
            var allKeys = await _database!.SortedSetRangeByScoreAsync(_indexSetKey);

            foreach (var keyValue in allKeys)
            {
                ct.ThrowIfCancellationRequested();

                var key = keyValue.ToString();

                // Filter by prefix if specified
                if (!string.IsNullOrEmpty(prefix) && !key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                // Get metadata
                StorageObjectMetadata? meta = null;
                try
                {
                    meta = await GetMetadataAsyncCore(key, ct);
                }
                catch
                {
                    // Skip keys that no longer exist or have invalid metadata
                    continue;
                }

                if (meta != null)
                {
                    yield return meta;
                }

                await Task.Yield();
            }
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var metadataKey = GetMetadataKey(key);
            var entries = await _database!.HashGetAllAsync(metadataKey);

            if (entries.Length == 0)
            {
                throw new FileNotFoundException($"Metadata not found for key: {key}");
            }

            var metadata = entries.ToDictionary(e => e.Name.ToString(), e => e.Value);

            var size = metadata.TryGetValue("size", out var sizeValue) ? (long)sizeValue : 0L;
            var createdTicks = metadata.TryGetValue("created", out var createdValue) ? (long)createdValue : DateTime.UtcNow.Ticks;
            var modifiedTicks = metadata.TryGetValue("modified", out var modifiedValue) ? (long)modifiedValue : DateTime.UtcNow.Ticks;
            var contentType = metadata.TryGetValue("content-type", out var ctValue) ? ctValue.ToString() : "application/octet-stream";

            // Extract custom metadata
            var customMetadata = new Dictionary<string, string>();
            foreach (var entry in metadata.Where(kvp => kvp.Key.StartsWith("custom:", StringComparison.Ordinal)))
            {
                var customKey = entry.Key.Substring("custom:".Length);
                customMetadata[customKey] = entry.Value.ToString();
            }

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = size,
                Created = new DateTime(createdTicks, DateTimeKind.Utc),
                Modified = new DateTime(modifiedTicks, DateTimeKind.Utc),
                ETag = GenerateETag(key, size, modifiedTicks),
                ContentType = contentType,
                CustomMetadata = customMetadata.Count > 0 ? customMetadata : null,
                Tier = Tier
            };
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                if (_redis == null || !_redis.IsConnected)
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Unhealthy,
                        Message = "Redis connection is not established",
                        CheckedAt = DateTime.UtcNow
                    };
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var pingLatency = await _database!.PingAsync();
                sw.Stop();

                var info = await _redis.GetServer(_redis.GetEndPoints().First()).InfoAsync("memory");
                var memorySection = info.FirstOrDefault(g => g.Key == "Memory");

                long? usedMemory = null;
                long? maxMemory = null;

                if (memorySection != null)
                {
                    var usedMemoryKvp = memorySection.FirstOrDefault(kvp => kvp.Key == "used_memory");
                    if (usedMemoryKvp.Key != null && long.TryParse(usedMemoryKvp.Value, out var used))
                    {
                        usedMemory = used;
                    }

                    var maxMemoryKvp = memorySection.FirstOrDefault(kvp => kvp.Key == "maxmemory");
                    if (maxMemoryKvp.Key != null && long.TryParse(maxMemoryKvp.Value, out var max) && max > 0)
                    {
                        maxMemory = max;
                    }
                }

                return new StorageHealthInfo
                {
                    Status = HealthStatus.Healthy,
                    LatencyMs = sw.ElapsedMilliseconds,
                    AvailableCapacity = maxMemory.HasValue && usedMemory.HasValue ? maxMemory.Value - usedMemory.Value : null,
                    TotalCapacity = maxMemory,
                    UsedCapacity = usedMemory,
                    Message = $"Redis is healthy. Ping latency: {pingLatency.TotalMilliseconds:F2}ms",
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Redis health check failed: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override async Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            try
            {
                if (_redis == null || !_redis.IsConnected)
                {
                    return null;
                }

                var info = await _redis.GetServer(_redis.GetEndPoints().First()).InfoAsync("memory");
                var memorySection = info.FirstOrDefault(g => g.Key == "Memory");

                if (memorySection == null)
                {
                    return null;
                }

                var usedMemoryKvp = memorySection.FirstOrDefault(kvp => kvp.Key == "used_memory");
                var maxMemoryKvp = memorySection.FirstOrDefault(kvp => kvp.Key == "maxmemory");

                if (usedMemoryKvp.Key != null && maxMemoryKvp.Key != null &&
                    long.TryParse(usedMemoryKvp.Value, out var used) &&
                    long.TryParse(maxMemoryKvp.Value, out var max) && max > 0)
                {
                    return max - used;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Chunked Storage (Redis Streams)

        /// <summary>
        /// Stores large objects using Redis Streams for chunking.
        /// </summary>
        private async Task<StorageObjectMetadata> StoreAsChunkedAsync(string key, byte[] content, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            var streamKey = GetStreamKey(key);
            var metadataKey = GetMetadataKey(key);
            var startTime = DateTime.UtcNow;

            // Delete existing stream if it exists
            await _database!.KeyDeleteAsync(streamKey);

            // Chunk the data and store in Redis Stream
            var chunkCount = (int)Math.Ceiling((double)content.Length / _chunkSizeBytes);
            var streamTasks = new List<Task<RedisValue>>();

            for (int i = 0; i < chunkCount; i++)
            {
                var offset = i * _chunkSizeBytes;
                var length = Math.Min(_chunkSizeBytes, content.Length - offset);
                var chunk = new byte[length];
                Array.Copy(content, offset, chunk, 0, length);

                var entries = new NameValueEntry[]
                {
                    new NameValueEntry("chunk", i),
                    new NameValueEntry("data", chunk)
                };

                streamTasks.Add(_database.StreamAddAsync(streamKey, entries));
            }

            await Task.WhenAll(streamTasks);

            // Store metadata
            var metadataEntries = new HashEntry[]
            {
                new HashEntry("size", content.Length),
                new HashEntry("created", startTime.Ticks),
                new HashEntry("modified", startTime.Ticks),
                new HashEntry("content-type", GetContentType(key)),
                new HashEntry("compressed", false),
                new HashEntry("chunked", true),
                new HashEntry("chunk-count", chunkCount)
            };

            if (metadata != null)
            {
                var customEntries = metadata.Select(kvp => new HashEntry($"custom:{kvp.Key}", kvp.Value)).ToArray();
                metadataEntries = metadataEntries.Concat(customEntries).ToArray();
            }

            await _database.HashSetAsync(metadataKey, metadataEntries);

            // Add to index
            await _database.SortedSetAddAsync(_indexSetKey, key, startTime.Ticks);

            // Set expiration if configured
            if (_defaultExpiration.HasValue)
            {
                await _database.KeyExpireAsync(streamKey, _defaultExpiration);
                await _database.KeyExpireAsync(metadataKey, _defaultExpiration);
            }

            // Update statistics
            IncrementBytesStored(content.Length);
            IncrementOperationCounter(StorageOperationType.Store);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = content.Length,
                Created = startTime,
                Modified = startTime,
                ETag = GenerateETag(content),
                ContentType = GetContentType(key),
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }

        /// <summary>
        /// Retrieves large objects stored as chunks in Redis Streams.
        /// </summary>
        private async Task<Stream> RetrieveChunkedAsync(string key, CancellationToken ct)
        {
            var streamKey = GetStreamKey(key);
            var metadataKey = GetMetadataKey(key);

            // Get chunk count from metadata
            var chunkCountValue = await _database!.HashGetAsync(metadataKey, "chunk-count");
            if (!chunkCountValue.HasValue)
            {
                throw new InvalidOperationException($"Chunked object missing chunk-count metadata: {key}");
            }

            var chunkCount = (int)chunkCountValue;

            // Read all chunks from stream
            var entries = await _database.StreamRangeAsync(streamKey);

            if (entries.Length != chunkCount)
            {
                throw new InvalidOperationException(
                    $"Chunked object incomplete. Expected {chunkCount} chunks, found {entries.Length}: {key}");
            }

            // Reconstruct the data
            var ms = new MemoryStream(65536);
            foreach (var entry in entries.OrderBy(e => e.Values.FirstOrDefault(v => v.Name == "chunk").Value))
            {
                var dataValue = entry.Values.FirstOrDefault(v => v.Name == "data").Value;
                if (!dataValue.IsNull)
                {
                    var chunk = (byte[])dataValue!;
                    await ms.WriteAsync(chunk, 0, chunk.Length, ct);
                }
            }

            ms.Position = 0;

            // Update statistics
            IncrementBytesRetrieved(ms.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            return ms;
        }

        /// <summary>
        /// Deletes chunked objects stored in Redis Streams.
        /// </summary>
        private async Task DeleteChunkedAsync(string key, CancellationToken ct)
        {
            var streamKey = GetStreamKey(key);
            var metadataKey = GetMetadataKey(key);

            var transaction = _database!.CreateTransaction();
            var deleteStreamTask = transaction.KeyDeleteAsync(streamKey);
            var deleteMetaTask = transaction.KeyDeleteAsync(metadataKey);
            var removeIndexTask = transaction.SortedSetRemoveAsync(_indexSetKey, key);

            await transaction.ExecuteAsync();
            await Task.WhenAll(deleteStreamTask, deleteMetaTask, removeIndexTask);
        }

        #endregion

        #region Redis-Specific Operations

        /// <summary>
        /// Sets the TTL (expiration) for a stored object.
        /// </summary>
        public async Task SetExpirationAsync(string key, TimeSpan expiration, CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            var redisKey = GetRedisKey(key);
            var metadataKey = GetMetadataKey(key);

            var transaction = _database!.CreateTransaction();
            var dataExpireTask = transaction.KeyExpireAsync(redisKey, expiration);
            var metaExpireTask = transaction.KeyExpireAsync(metadataKey, expiration);

            await transaction.ExecuteAsync();
            await Task.WhenAll(dataExpireTask, metaExpireTask);
        }

        /// <summary>
        /// Gets the remaining TTL for a stored object.
        /// </summary>
        public async Task<TimeSpan?> GetExpirationAsync(string key, CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            var redisKey = GetRedisKey(key);
            return await _database!.KeyTimeToLiveAsync(redisKey);
        }

        /// <summary>
        /// Removes the expiration from a stored object (makes it persistent).
        /// </summary>
        public async Task RemoveExpirationAsync(string key, CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            var redisKey = GetRedisKey(key);
            var metadataKey = GetMetadataKey(key);

            var transaction = _database!.CreateTransaction();
            var dataPersistTask = transaction.KeyPersistAsync(redisKey);
            var metaPersistTask = transaction.KeyPersistAsync(metadataKey);

            await transaction.ExecuteAsync();
            await Task.WhenAll(dataPersistTask, metaPersistTask);
        }

        /// <summary>
        /// Subscribes to storage events via Redis pub/sub.
        /// </summary>
        public async Task SubscribeToEventsAsync(Action<string, string> eventHandler, CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enablePubSub || _subscriber == null)
            {
                throw new InvalidOperationException("Pub/sub is not enabled. Set EnablePubSub=true in configuration.");
            }

            await _subscriber.SubscribeAsync(RedisChannel.Literal(_pubSubChannel), (channel, message) =>
            {
                var parts = message.ToString().Split(':', 3);
                if (parts.Length >= 2)
                {
                    eventHandler(parts[0], parts[1]);
                }
            });
        }

        /// <summary>
        /// Unsubscribes from storage events.
        /// </summary>
        public async Task UnsubscribeFromEventsAsync(CancellationToken ct = default)
        {
            if (_subscriber != null)
            {
                await _subscriber.UnsubscribeAsync(RedisChannel.Literal(_pubSubChannel));
            }
        }

        /// <summary>
        /// Gets Redis server statistics.
        /// </summary>
        public async Task<Dictionary<string, string>> GetRedisInfoAsync(string? section = null, CancellationToken ct = default)
        {
            EnsureInitialized();

            if (_redis == null || !_redis.IsConnected)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            var server = _redis.GetServer(_redis.GetEndPoints().First());
            var info = section != null
                ? await server.InfoAsync(section)
                : await server.InfoAsync();

            var result = new Dictionary<string, string>();

            foreach (var group in info)
            {
                foreach (var kvp in group)
                {
                    result[$"{group.Key}:{kvp.Key}"] = kvp.Value;
                }
            }

            return result;
        }

        #endregion

        #region Helper Methods

        private string GetRedisKey(string key)
        {
            return $"{_keyPrefix}{key}";
        }

        private string GetMetadataKey(string key)
        {
            return $"{_metadataKeyPrefix}{key}";
        }

        private string GetStreamKey(string key)
        {
            return $"{_keyPrefix}stream:{key}";
        }

        private string GenerateETag(byte[] content)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = sha256.ComputeHash(content);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private string GenerateETag(string key, long size, long modifiedTicks)
        {
            var hash = HashCode.Combine(key, size, modifiedTicks);
            return hash.ToString("x");
        }

        private string GetContentType(string key)
        {
            var extension = Path.GetExtension(key).ToLowerInvariant();
            return extension switch
            {
                ".json" => "application/json",
                ".xml" => "application/xml",
                ".txt" => "text/plain",
                ".csv" => "text/csv",
                ".html" or ".htm" => "text/html",
                ".pdf" => "application/pdf",
                ".zip" => "application/zip",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".mp4" => "video/mp4",
                ".mp3" => "audio/mpeg",
                _ => "application/octet-stream"
            };
        }

        private async Task<byte[]> CompressDataAsync(byte[] data, CancellationToken ct)
        {
            using var inputStream = new MemoryStream(data);
            using var outputStream = new MemoryStream();
            using var gzipStream = new System.IO.Compression.GZipStream(outputStream, System.IO.Compression.CompressionLevel.Optimal);

            await inputStream.CopyToAsync(gzipStream, 81920, ct);
            await gzipStream.FlushAsync(ct);

            return outputStream.ToArray();
        }

        private async Task<byte[]> DecompressDataAsync(byte[] compressedData, CancellationToken ct)
        {
            using var inputStream = new MemoryStream(compressedData);
            using var outputStream = new MemoryStream();
            using var gzipStream = new System.IO.Compression.GZipStream(inputStream, System.IO.Compression.CompressionMode.Decompress);

            await gzipStream.CopyToAsync(outputStream, 81920, ct);

            return outputStream.ToArray();
        }

        protected override int GetMaxKeyLength() => 512 * 1024 * 1024; // Redis key length limit

        #endregion
    }
}
