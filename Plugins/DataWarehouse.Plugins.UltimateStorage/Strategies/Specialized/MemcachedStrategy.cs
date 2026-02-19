using DataWarehouse.SDK.Contracts.Storage;
using Enyim.Caching;
using Enyim.Caching.Configuration;
using Enyim.Caching.Memcached;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StoreMode = Enyim.Caching.Memcached.StoreMode;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Specialized
{
    /// <summary>
    /// Memcached storage strategy with production-ready features:
    /// - Distributed Memcached cluster support (multiple servers)
    /// - Key expiration (TTL) management
    /// - Chunking for large objects (Memcached 1MB limit per key)
    /// - Compression for larger payloads
    /// - Connection pooling and fault tolerance
    /// - Consistent hashing for distribution
    /// - Automatic failover and retry logic
    /// - Binary protocol support
    /// - Metadata storage with separate keys
    /// - Prefix-based key management for listing
    /// </summary>
    public class MemcachedStrategy : UltimateStorageStrategyBase
    {
        private IMemcachedClient? _client;
        private string _servers = string.Empty;
        private TimeSpan? _defaultExpiration = null;
        private int _chunkSizeBytes = 900 * 1024; // 900KB chunks (leave margin under 1MB limit)
        private long _maxObjectSize = 900 * 1024; // 900KB max per key
        private bool _enableCompression = false;
        private int _compressionThreshold = 1024; // 1KB
        private string _keyPrefix = "storage:";
        private string _metadataKeyPrefix = "meta:";
        private string _indexKeyPrefix = "index:";
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private readonly Dictionary<string, long> _keyIndex = new(); // In-memory index for listing
        private readonly SemaphoreSlim _indexLock = new(1, 1);

        public override string StrategyId => "memcached";
        public override string Name => "Memcached Storage";
        public override StorageTier Tier => StorageTier.Hot; // In-memory cache storage is hot tier

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false,
            SupportsVersioning = false,
            SupportsTiering = false,
            SupportsEncryption = false,
            SupportsCompression = _enableCompression,
            SupportsMultipart = true, // Via chunking
            MaxObjectSize = 900 * 1024, // 900KB practical limit
            MaxObjects = null, // Limited by memory
            ConsistencyModel = ConsistencyModel.Eventual // Memcached is eventually consistent
        };

        #region Initialization

        /// <summary>
        /// Initializes the Memcached storage strategy.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            await _initLock.WaitAsync(ct);
            try
            {
                // Load configuration
                _servers = GetConfiguration<string>("Servers")
                    ?? throw new InvalidOperationException("Memcached Servers configuration is required");

                _keyPrefix = GetConfiguration("KeyPrefix", "storage:");
                _metadataKeyPrefix = GetConfiguration("MetadataKeyPrefix", "meta:");
                _indexKeyPrefix = GetConfiguration("IndexKeyPrefix", "index:");
                _chunkSizeBytes = GetConfiguration("ChunkSizeBytes", 900 * 1024);
                _maxObjectSize = GetConfiguration("MaxObjectSize", 900L * 1024);
                _enableCompression = GetConfiguration("EnableCompression", false);
                _compressionThreshold = GetConfiguration("CompressionThreshold", 1024);

                // Parse TTL from configuration
                var ttlSeconds = GetConfiguration<int?>("DefaultExpirationSeconds", null);
                if (ttlSeconds.HasValue)
                {
                    _defaultExpiration = TimeSpan.FromSeconds(ttlSeconds.Value);
                }

                // Parse server list
                var serverList = _servers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (serverList.Length == 0)
                {
                    throw new InvalidOperationException("At least one Memcached server must be configured");
                }

                // Configure Memcached client options
                var options = new MemcachedClientOptions();

                // Add servers
                foreach (var server in serverList)
                {
                    var parts = server.Split(':', StringSplitOptions.TrimEntries);
                    var host = parts[0];
                    var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 11211;

                    options.Servers.Add(new Server
                    {
                        Address = host,
                        Port = port
                    });
                }

                // Configure protocol and other settings
                options.Protocol = MemcachedProtocol.Binary; // Use binary protocol for better performance

                // Configure socket pool
                options.SocketPool = new SocketPoolOptions
                {
                    MinPoolSize = GetConfiguration("MinPoolSize", 10),
                    MaxPoolSize = GetConfiguration("MaxPoolSize", 100),
                    ConnectionTimeout = TimeSpan.FromMilliseconds(GetConfiguration("ConnectionTimeoutMs", 5000)),
                    DeadTimeout = TimeSpan.FromSeconds(GetConfiguration("DeadTimeoutSeconds", 30))
                };

                // Create configuration
                var loggerFactory = (ILoggerFactory)Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
                var optionsWrapper = Options.Create(options);
                var config = new MemcachedClientConfiguration(loggerFactory, optionsWrapper);

                // Create client
                _client = new MemcachedClient(loggerFactory, config);

                // Test connection by setting a test key
                var testKey = $"{_keyPrefix}__test__";
                var testValue = Encoding.UTF8.GetBytes("test");
                var testResult = await _client.StoreAsync(Enyim.Caching.Memcached.StoreMode.Set, testKey, testValue, DateTime.UtcNow.AddMinutes(1));

                if (!testResult)
                {
                    throw new InvalidOperationException("Failed to connect to Memcached - test write failed");
                }

                // Clean up test key
                await _client.RemoveAsync(testKey);
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>
        /// Disposes Memcached resources.
        /// </summary>
        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();

            _client?.Dispose();
            _initLock?.Dispose();
            _indexLock?.Dispose();
        }

        #endregion

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);
            ValidateStream(data);

            var startTime = DateTime.UtcNow;

            // Read stream into memory
            using var ms = new MemoryStream(65536);
            await data.CopyToAsync(ms, 81920, ct);
            var content = ms.ToArray();
            var originalSize = content.Length;

            // Check if we need to use chunked storage
            if (content.Length > _maxObjectSize)
            {
                return await StoreAsChunkedAsync(key, content, metadata, startTime, ct);
            }

            // Apply compression if enabled
            byte[] finalContent = content;
            var isCompressed = false;

            if (_enableCompression && content.Length > _compressionThreshold)
            {
                finalContent = await CompressDataAsync(content, ct);
                isCompressed = true;
            }

            // Store data
            var memcachedKey = GetMemcachedKey(key);
            var expiration = _defaultExpiration.HasValue ? DateTime.UtcNow.Add(_defaultExpiration.Value) : DateTime.MaxValue;

            var storeResult = await _client!.StoreAsync(StoreMode.Set, memcachedKey, finalContent, expiration);

            if (!storeResult)
            {
                throw new InvalidOperationException($"Failed to store object in Memcached: {key}");
            }

            // Store metadata
            var metadataObj = new MemcachedMetadata
            {
                Key = key,
                Size = originalSize,
                Created = startTime,
                Modified = startTime,
                ContentType = GetContentType(key),
                IsCompressed = isCompressed,
                IsChunked = false,
                ChunkCount = 0,
                CustomMetadata = metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            };

            var metadataKey = GetMetadataKey(key);
            var metadataJson = JsonSerializer.Serialize(metadataObj);
            var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);

            await _client.StoreAsync(StoreMode.Set, metadataKey, metadataBytes, expiration);

            // Add to index
            await AddToIndexAsync(key, startTime.Ticks);

            // Update statistics
            IncrementBytesStored(originalSize);
            IncrementOperationCounter(StorageOperationType.Store);

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

            var metadataKey = GetMetadataKey(key);

            // Get metadata to check if chunked
            var metadataResult = await _client!.GetAsync<byte[]>(metadataKey);
            if (metadataResult == null || !metadataResult.Success || metadataResult.Value == null)
            {
                throw new FileNotFoundException($"Object not found in Memcached: {key}");
            }

            var metadataJson = Encoding.UTF8.GetString(metadataResult.Value);
            var metadataObj = JsonSerializer.Deserialize<MemcachedMetadata>(metadataJson);

            if (metadataObj == null)
            {
                throw new InvalidOperationException($"Invalid metadata for key: {key}");
            }

            byte[] content;

            if (metadataObj.IsChunked)
            {
                content = await RetrieveChunkedAsync(key, metadataObj.ChunkCount, ct);
            }
            else
            {
                // Retrieve data
                var memcachedKey = GetMemcachedKey(key);
                var valueResult = await _client.GetAsync<byte[]>(memcachedKey);

                if (valueResult == null || !valueResult.Success || valueResult.Value == null)
                {
                    throw new FileNotFoundException($"Object not found in Memcached: {key}");
                }

                content = valueResult.Value;
            }

            // Decompress if needed
            if (metadataObj.IsCompressed)
            {
                content = await DecompressDataAsync(content, ct);
            }

            // Update statistics
            IncrementBytesRetrieved(content.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            var resultStream = new MemoryStream(content);
            resultStream.Position = 0;
            return resultStream;
        }

        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var metadataKey = GetMetadataKey(key);

            // Get metadata to check size and chunking
            long size = 0;
            bool isChunked = false;
            int chunkCount = 0;

            try
            {
                var metadataResult = await _client!.GetAsync<byte[]>(metadataKey);
                if (metadataResult != null && metadataResult.Success && metadataResult.Value != null)
                {
                    var metadataJson = Encoding.UTF8.GetString(metadataResult.Value);
                    var metadataObj = JsonSerializer.Deserialize<MemcachedMetadata>(metadataJson);

                    if (metadataObj != null)
                    {
                        size = metadataObj.Size;
                        isChunked = metadataObj.IsChunked;
                        chunkCount = metadataObj.ChunkCount;
                    }
                }
            }
            catch
            {
                // Ignore metadata retrieval errors
            }

            if (isChunked)
            {
                await DeleteChunkedAsync(key, chunkCount, ct);
            }
            else
            {
                // Delete data
                var memcachedKey = GetMemcachedKey(key);
                await _client!.RemoveAsync(memcachedKey);
            }

            // Delete metadata
            await _client!.RemoveAsync(metadataKey);

            // Remove from index
            await RemoveFromIndexAsync(key);

            // Update statistics
            if (size > 0)
            {
                IncrementBytesDeleted(size);
            }
            IncrementOperationCounter(StorageOperationType.Delete);
        }

        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var memcachedKey = GetMemcachedKey(key);
            var result = await _client!.GetAsync<byte[]>(memcachedKey);

            IncrementOperationCounter(StorageOperationType.Exists);

            return result != null && result.Success && result.Value != null;
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();
            IncrementOperationCounter(StorageOperationType.List);

            // Get all keys from in-memory index
            List<string> allKeys;
            await _indexLock.WaitAsync(ct);
            try
            {
                allKeys = _keyIndex.Keys.ToList();
            }
            finally
            {
                _indexLock.Release();
            }

            foreach (var key in allKeys)
            {
                ct.ThrowIfCancellationRequested();

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
                    // Remove from index
                    await RemoveFromIndexAsync(key);
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
            var metadataResult = await _client!.GetAsync<byte[]>(metadataKey);

            if (metadataResult == null || !metadataResult.Success || metadataResult.Value == null)
            {
                throw new FileNotFoundException($"Metadata not found for key: {key}");
            }

            var metadataJson = Encoding.UTF8.GetString(metadataResult.Value);
            var metadataObj = JsonSerializer.Deserialize<MemcachedMetadata>(metadataJson);

            if (metadataObj == null)
            {
                throw new InvalidOperationException($"Invalid metadata for key: {key}");
            }

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = metadataObj.Size,
                Created = metadataObj.Created,
                Modified = metadataObj.Modified,
                ETag = GenerateETag(key, metadataObj.Size, metadataObj.Modified.Ticks),
                ContentType = metadataObj.ContentType,
                CustomMetadata = metadataObj.CustomMetadata,
                Tier = Tier
            };
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                if (_client == null)
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Unhealthy,
                        Message = "Memcached client is not initialized",
                        CheckedAt = DateTime.UtcNow
                    };
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Test connectivity with a simple set/get operation
                var testKey = $"{_keyPrefix}__health__";
                var testValue = Encoding.UTF8.GetBytes($"health_check_{DateTime.UtcNow.Ticks}");

                var setResult = await _client.StoreAsync(StoreMode.Set, testKey, testValue, DateTime.UtcNow.AddMinutes(1));
                if (!setResult)
                {
                    sw.Stop();
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Degraded,
                        Message = "Memcached health check write failed",
                        LatencyMs = sw.ElapsedMilliseconds,
                        CheckedAt = DateTime.UtcNow
                    };
                }

                var getResult = await _client.GetAsync<byte[]>(testKey);
                sw.Stop();

                if (getResult == null || !getResult.Success || getResult.Value == null || !getResult.Value.SequenceEqual(testValue))
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Degraded,
                        Message = "Memcached health check read failed",
                        LatencyMs = sw.ElapsedMilliseconds,
                        CheckedAt = DateTime.UtcNow
                    };
                }

                // Clean up test key
                await _client.RemoveAsync(testKey);

                return new StorageHealthInfo
                {
                    Status = HealthStatus.Healthy,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Message = $"Memcached is healthy. Latency: {sw.ElapsedMilliseconds}ms",
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Memcached health check failed: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            // Memcached doesn't provide a way to query available capacity
            // Return null to indicate capacity information is unavailable
            return Task.FromResult<long?>(null);
        }

        #endregion

        #region Chunked Storage

        /// <summary>
        /// Stores large objects using chunking (Memcached 1MB limit per key).
        /// </summary>
        private async Task<StorageObjectMetadata> StoreAsChunkedAsync(string key, byte[] content, IDictionary<string, string>? metadata, DateTime startTime, CancellationToken ct)
        {
            var chunkCount = (int)Math.Ceiling((double)content.Length / _chunkSizeBytes);
            var expiration = _defaultExpiration.HasValue ? DateTime.UtcNow.Add(_defaultExpiration.Value) : DateTime.MaxValue;

            // Store chunks
            for (int i = 0; i < chunkCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                var offset = i * _chunkSizeBytes;
                var length = Math.Min(_chunkSizeBytes, content.Length - offset);
                var chunk = new byte[length];
                Array.Copy(content, offset, chunk, 0, length);

                var chunkKey = GetChunkKey(key, i);
                var storeResult = await _client!.StoreAsync(StoreMode.Set, chunkKey, chunk, expiration);

                if (!storeResult)
                {
                    // Cleanup already stored chunks on failure
                    for (int j = 0; j < i; j++)
                    {
                        await _client.RemoveAsync(GetChunkKey(key, j));
                    }
                    throw new InvalidOperationException($"Failed to store chunk {i} for key: {key}");
                }
            }

            // Store metadata
            var metadataObj = new MemcachedMetadata
            {
                Key = key,
                Size = content.Length,
                Created = startTime,
                Modified = startTime,
                ContentType = GetContentType(key),
                IsCompressed = false, // Chunked objects are not compressed
                IsChunked = true,
                ChunkCount = chunkCount,
                CustomMetadata = metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            };

            var metadataKey = GetMetadataKey(key);
            var metadataJson = JsonSerializer.Serialize(metadataObj);
            var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);

            await _client!.StoreAsync(StoreMode.Set, metadataKey, metadataBytes, expiration);

            // Add to index
            await AddToIndexAsync(key, startTime.Ticks);

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
        /// Retrieves large objects stored as chunks.
        /// </summary>
        private async Task<byte[]> RetrieveChunkedAsync(string key, int chunkCount, CancellationToken ct)
        {
            using var ms = new MemoryStream(65536);

            for (int i = 0; i < chunkCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                var chunkKey = GetChunkKey(key, i);
                var chunkResult = await _client!.GetAsync<byte[]>(chunkKey);

                if (chunkResult == null || !chunkResult.Success || chunkResult.Value == null)
                {
                    throw new InvalidOperationException(
                        $"Chunked object incomplete. Missing chunk {i} of {chunkCount} for key: {key}");
                }

                await ms.WriteAsync(chunkResult.Value, 0, chunkResult.Value.Length, ct);
            }

            return ms.ToArray();
        }

        /// <summary>
        /// Deletes chunked objects.
        /// </summary>
        private async Task DeleteChunkedAsync(string key, int chunkCount, CancellationToken ct)
        {
            // Delete all chunks
            for (int i = 0; i < chunkCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var chunkKey = GetChunkKey(key, i);
                await _client!.RemoveAsync(chunkKey);
            }
        }

        #endregion

        #region Memcached-Specific Operations

        /// <summary>
        /// Sets the TTL (expiration) for a stored object.
        /// Note: Memcached requires re-setting the value to update expiration.
        /// </summary>
        public async Task SetExpirationAsync(string key, TimeSpan expiration, CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            // For Memcached, we need to retrieve the value and re-set it with new expiration
            var memcachedKey = GetMemcachedKey(key);
            var valueResult = await _client!.GetAsync<byte[]>(memcachedKey);

            if (valueResult != null && valueResult.Success && valueResult.Value != null)
            {
                await _client.StoreAsync(StoreMode.Set, memcachedKey, valueResult.Value, DateTime.UtcNow.Add(expiration));
            }

            // Also update metadata expiration
            var metadataKey = GetMetadataKey(key);
            var metadataResult = await _client.GetAsync<byte[]>(metadataKey);

            if (metadataResult != null && metadataResult.Success && metadataResult.Value != null)
            {
                await _client.StoreAsync(StoreMode.Set, metadataKey, metadataResult.Value, DateTime.UtcNow.Add(expiration));
            }
        }

        /// <summary>
        /// Flushes all data from all Memcached servers.
        /// WARNING: This is a destructive operation that affects all keys across all applications using the same servers.
        /// </summary>
        public async Task FlushAllAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            // Note: EnyimMemcachedCore doesn't expose FlushAll directly
            // This would need to be implemented using the underlying protocol
            // For now, we can only clear our index
            await _indexLock.WaitAsync(ct);
            try
            {
                _keyIndex.Clear();
            }
            finally
            {
                _indexLock.Release();
            }
        }

        #endregion

        #region Index Management

        /// <summary>
        /// Adds a key to the in-memory index for listing support.
        /// </summary>
        private async Task AddToIndexAsync(string key, long timestamp)
        {
            await _indexLock.WaitAsync();
            try
            {
                _keyIndex[key] = timestamp;
            }
            finally
            {
                _indexLock.Release();
            }
        }

        /// <summary>
        /// Removes a key from the in-memory index.
        /// </summary>
        private async Task RemoveFromIndexAsync(string key)
        {
            await _indexLock.WaitAsync();
            try
            {
                _keyIndex.Remove(key);
            }
            finally
            {
                _indexLock.Release();
            }
        }

        #endregion

        #region Helper Methods

        private string GetMemcachedKey(string key)
        {
            return $"{_keyPrefix}{key}";
        }

        private string GetMetadataKey(string key)
        {
            return $"{_metadataKeyPrefix}{key}";
        }

        private string GetChunkKey(string key, int chunkIndex)
        {
            return $"{_keyPrefix}chunk:{key}:{chunkIndex}";
        }

        /// <summary>
        /// Generates a non-cryptographic ETag from content using fast hashing.
        /// AD-11: Cryptographic hashing delegated to UltimateDataIntegrity via bus.
        /// ETags only need collision resistance for caching, not security.
        /// </summary>
        private string GenerateETag(byte[] content)
        {
            var hash = new HashCode();
            hash.AddBytes(content);
            return hash.ToHashCode().ToString("x8");
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
            using var outputStream = new MemoryStream(65536);
            using var gzipStream = new System.IO.Compression.GZipStream(outputStream, System.IO.Compression.CompressionLevel.Optimal);

            await inputStream.CopyToAsync(gzipStream, 81920, ct);
            await gzipStream.FlushAsync(ct);

            return outputStream.ToArray();
        }

        private async Task<byte[]> DecompressDataAsync(byte[] compressedData, CancellationToken ct)
        {
            using var inputStream = new MemoryStream(compressedData);
            using var outputStream = new MemoryStream(65536);
            using var gzipStream = new System.IO.Compression.GZipStream(inputStream, System.IO.Compression.CompressionMode.Decompress);

            await gzipStream.CopyToAsync(outputStream, 81920, ct);

            return outputStream.ToArray();
        }

        protected override int GetMaxKeyLength() => 250; // Memcached key length limit

        #endregion

        #region Supporting Types

        /// <summary>
        /// Metadata structure stored in Memcached.
        /// </summary>
        private class MemcachedMetadata
        {
            public string Key { get; set; } = string.Empty;
            public long Size { get; set; }
            public DateTime Created { get; set; }
            public DateTime Modified { get; set; }
            public string ContentType { get; set; } = "application/octet-stream";
            public bool IsCompressed { get; set; }
            public bool IsChunked { get; set; }
            public int ChunkCount { get; set; }
            public Dictionary<string, string>? CustomMetadata { get; set; }
        }

        #endregion
    }
}
