using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Storage;
using DataWarehouse.SDK.Tags;
using DataWarehouse.SDK.Utilities;
using System.Collections.Generic;
using System.Diagnostics;

namespace DataWarehouse.SDK.Contracts.Storage
{
    #region Storage Strategy Interface

    /// <summary>
    /// Defines a storage strategy for backend storage operations.
    /// Provides methods for storing, retrieving, deleting, and managing storage objects.
    /// Implementations should handle backend-specific details like authentication, connectivity, and error handling.
    /// </summary>
    public interface IStorageStrategy
    {
        /// <summary>
        /// Gets the unique identifier for this storage strategy.
        /// </summary>
        string StrategyId { get; }

        /// <summary>
        /// Gets the human-readable name of this storage strategy.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the storage tier this strategy operates on.
        /// </summary>
        StorageTier Tier { get; }

        /// <summary>
        /// Gets the capabilities supported by this storage backend.
        /// </summary>
        StorageCapabilities Capabilities { get; }

        /// <summary>
        /// Stores data to the storage backend.
        /// </summary>
        /// <param name="key">The unique key/path for the object.</param>
        /// <param name="data">The data stream to store.</param>
        /// <param name="metadata">Optional metadata to associate with the object.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Metadata of the stored object.</returns>
        Task<StorageObjectMetadata> StoreAsync(string key, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default);

        /// <summary>
        /// Retrieves data from the storage backend.
        /// </summary>
        /// <param name="key">The unique key/path of the object.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The data stream.</returns>
        Task<Stream> RetrieveAsync(string key, CancellationToken ct = default);

        /// <summary>
        /// Deletes an object from the storage backend.
        /// </summary>
        /// <param name="key">The unique key/path of the object.</param>
        /// <param name="ct">Cancellation token.</param>
        Task DeleteAsync(string key, CancellationToken ct = default);

        /// <summary>
        /// Checks if an object exists in the storage backend.
        /// </summary>
        /// <param name="key">The unique key/path of the object.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if the object exists, false otherwise.</returns>
        Task<bool> ExistsAsync(string key, CancellationToken ct = default);

        /// <summary>
        /// Lists objects in the storage backend with an optional prefix filter.
        /// </summary>
        /// <param name="prefix">Optional prefix to filter results.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>An async enumerable of object metadata.</returns>
        IAsyncEnumerable<StorageObjectMetadata> ListAsync(string? prefix = null, CancellationToken ct = default);

        /// <summary>
        /// Gets metadata for a specific object without retrieving its data.
        /// </summary>
        /// <param name="key">The unique key/path of the object.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The object metadata.</returns>
        Task<StorageObjectMetadata> GetMetadataAsync(string key, CancellationToken ct = default);

        /// <summary>
        /// Gets the current health status of the storage backend.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Health information including status, latency, and capacity.</returns>
        Task<StorageHealthInfo> GetHealthAsync(CancellationToken ct = default);

        /// <summary>
        /// Gets the available capacity in bytes.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Available capacity in bytes, or null if capacity information is not available.</returns>
        Task<long?> GetAvailableCapacityAsync(CancellationToken ct = default);

        #region StorageAddress Overloads (HAL-05)

        /// <summary>Stores data using a StorageAddress. Default: delegates to string-key overload via ToKey().</summary>
        Task<StorageObjectMetadata> StoreAsync(StorageAddress address, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default)
            => StoreAsync(address.ToKey(), data, metadata, ct);

        /// <summary>Retrieves data using a StorageAddress. Default: delegates to string-key overload via ToKey().</summary>
        Task<Stream> RetrieveAsync(StorageAddress address, CancellationToken ct = default)
            => RetrieveAsync(address.ToKey(), ct);

        /// <summary>Deletes an object using a StorageAddress. Default: delegates to string-key overload via ToKey().</summary>
        Task DeleteAsync(StorageAddress address, CancellationToken ct = default)
            => DeleteAsync(address.ToKey(), ct);

        /// <summary>Checks existence using a StorageAddress. Default: delegates to string-key overload via ToKey().</summary>
        Task<bool> ExistsAsync(StorageAddress address, CancellationToken ct = default)
            => ExistsAsync(address.ToKey(), ct);

        /// <summary>Gets metadata using a StorageAddress. Default: delegates to string-key overload via ToKey().</summary>
        Task<StorageObjectMetadata> GetMetadataAsync(StorageAddress address, CancellationToken ct = default)
            => GetMetadataAsync(address.ToKey(), ct);

        #endregion
    }

    #endregion

    #region Storage Capabilities

    /// <summary>
    /// Describes the capabilities and features supported by a storage backend.
    /// Used to determine what operations and optimizations are available.
    /// </summary>
    public record StorageCapabilities
    {
        /// <summary>
        /// Indicates if the backend supports versioning of objects.
        /// </summary>
        public bool SupportsVersioning { get; init; }

        /// <summary>
        /// Indicates if the backend supports custom metadata on objects.
        /// </summary>
        public bool SupportsMetadata { get; init; } = true;

        /// <summary>
        /// Indicates if the backend supports object-level locking.
        /// </summary>
        public bool SupportsLocking { get; init; }

        /// <summary>
        /// Indicates if the backend supports storage tiering (hot/warm/cold/archive).
        /// </summary>
        public bool SupportsTiering { get; init; }

        /// <summary>
        /// Indicates if the backend supports server-side encryption.
        /// </summary>
        public bool SupportsEncryption { get; init; }

        /// <summary>
        /// Indicates if the backend supports server-side compression.
        /// </summary>
        public bool SupportsCompression { get; init; }

        /// <summary>
        /// Indicates if the backend supports streaming reads/writes.
        /// </summary>
        public bool SupportsStreaming { get; init; } = true;

        /// <summary>
        /// Indicates if the backend supports multipart uploads for large objects.
        /// </summary>
        public bool SupportsMultipart { get; init; }

        /// <summary>
        /// Maximum object size in bytes that this backend can handle.
        /// Null indicates no practical limit.
        /// </summary>
        public long? MaxObjectSize { get; init; }

        /// <summary>
        /// Maximum number of objects this backend can store.
        /// Null indicates no practical limit.
        /// </summary>
        public long? MaxObjects { get; init; }

        /// <summary>
        /// The consistency model provided by this storage backend.
        /// </summary>
        public ConsistencyModel ConsistencyModel { get; init; } = ConsistencyModel.Eventual;

        /// <summary>
        /// Default capabilities for most storage backends.
        /// </summary>
        public static StorageCapabilities Default => new()
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            ConsistencyModel = ConsistencyModel.Eventual
        };
    }

    /// <summary>
    /// Defines the consistency model provided by a storage backend.
    /// </summary>
    public enum ConsistencyModel
    {
        /// <summary>
        /// Eventually consistent - reads may return stale data briefly after writes.
        /// Provides the best performance and availability.
        /// </summary>
        Eventual,

        /// <summary>
        /// Strongly consistent - reads always return the most recent write.
        /// May have higher latency and lower availability.
        /// </summary>
        Strong,

        /// <summary>
        /// Read-after-write consistency - a client will always see their own writes.
        /// Reads from other clients may be eventually consistent.
        /// </summary>
        ReadAfterWrite
    }

    #endregion

    #region Storage Tier

    /// <summary>
    /// Defines storage tiers for data lifecycle management.
    /// Different tiers have different performance, cost, and availability characteristics.
    /// </summary>
    public enum StorageTier
    {
        /// <summary>
        /// Hot tier - frequently accessed data with low latency (milliseconds).
        /// Highest cost, best performance.
        /// Examples: NVMe SSD, RAM cache, premium cloud storage.
        /// </summary>
        Hot,

        /// <summary>
        /// Warm tier - occasionally accessed data with moderate latency (seconds).
        /// Moderate cost and performance.
        /// Examples: Standard SSD/HDD, standard cloud storage.
        /// </summary>
        Warm,

        /// <summary>
        /// Cold tier - rarely accessed data with higher latency (minutes).
        /// Lower cost, reduced performance.
        /// Examples: Nearline storage, cool cloud tiers.
        /// </summary>
        Cold,

        /// <summary>
        /// Archive tier - long-term retention with high latency (hours).
        /// Lowest cost, retrieval may require hours.
        /// Examples: Tape, glacier storage, archive cloud tiers.
        /// </summary>
        Archive,

        /// <summary>
        /// RAM disk - ultra-low latency in-memory storage (microseconds).
        /// Highest performance, volatile, very high cost per GB.
        /// Examples: tmpfs, RAM disk, Redis.
        /// </summary>
        RamDisk,

        /// <summary>
        /// Tape storage - sequential access, very long-term archival (days to retrieve).
        /// Ultra-low cost, extremely high latency, best for compliance archives.
        /// Examples: LTO tape libraries.
        /// </summary>
        Tape
    }

    #endregion

    #region Storage Object Metadata

    /// <summary>
    /// Metadata information for a storage object.
    /// Contains properties about the object without loading its content.
    /// </summary>
    public record StorageObjectMetadata
    {
        /// <summary>
        /// The unique key/path of the object.
        /// </summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>
        /// Size of the object in bytes.
        /// </summary>
        public long Size { get; init; }

        /// <summary>
        /// When the object was created.
        /// </summary>
        public DateTime Created { get; init; }

        /// <summary>
        /// When the object was last modified.
        /// </summary>
        public DateTime Modified { get; init; }

        /// <summary>
        /// ETag for versioning and cache validation.
        /// Format varies by backend (MD5 hash, version ID, etc).
        /// </summary>
        public string? ETag { get; init; }

        /// <summary>
        /// MIME content type of the object.
        /// </summary>
        public string? ContentType { get; init; }

        /// <summary>
        /// Custom metadata key-value pairs associated with the object.
        /// </summary>
        public IReadOnlyDictionary<string, string>? CustomMetadata { get; init; }

        /// <summary>
        /// Storage tier where this object resides.
        /// </summary>
        public StorageTier? Tier { get; init; }

        /// <summary>
        /// Version identifier if versioning is supported.
        /// </summary>
        public string? VersionId { get; init; }

        /// <summary>
        /// Typed tag collection attached to this object.
        /// Null when tags have not been loaded (lazy-load pattern).
        /// Empty TagCollection when object has no tags.
        /// </summary>
        public TagCollection? Tags { get; init; }
    }

    #endregion

    #region Storage Health Info

    /// <summary>
    /// Health and status information for a storage backend.
    /// Used for monitoring, alerting, and load balancing decisions.
    /// </summary>
    public record StorageHealthInfo
    {
        /// <summary>
        /// Overall health status of the storage backend.
        /// </summary>
        public HealthStatus Status { get; init; }

        /// <summary>
        /// Recent operation latency in milliseconds.
        /// Useful for performance monitoring and SLA tracking.
        /// </summary>
        public double LatencyMs { get; init; }

        /// <summary>
        /// Available capacity in bytes.
        /// Null if capacity information is not available.
        /// </summary>
        public long? AvailableCapacity { get; init; }

        /// <summary>
        /// Total capacity in bytes.
        /// Null if capacity information is not available or unlimited.
        /// </summary>
        public long? TotalCapacity { get; init; }

        /// <summary>
        /// Used capacity in bytes.
        /// Null if capacity information is not available.
        /// </summary>
        public long? UsedCapacity { get; init; }

        /// <summary>
        /// Percentage of capacity used (0-100).
        /// Null if capacity information is not available.
        /// </summary>
        public double? UsagePercent => TotalCapacity.HasValue && TotalCapacity.Value > 0
            ? (UsedCapacity ?? 0) * 100.0 / TotalCapacity.Value
            : null;

        /// <summary>
        /// Additional diagnostic message or error details.
        /// </summary>
        public string? Message { get; init; }

        /// <summary>
        /// When this health check was performed.
        /// </summary>
        public DateTime CheckedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Health status levels for storage backends.
    /// </summary>
    public enum HealthStatus
    {
        /// <summary>
        /// Backend is healthy and operating normally.
        /// </summary>
        Healthy,

        /// <summary>
        /// Backend is operational but experiencing issues (high latency, warnings).
        /// </summary>
        Degraded,

        /// <summary>
        /// Backend is not operational or unreachable.
        /// </summary>
        Unhealthy,

        /// <summary>
        /// Health status is unknown or cannot be determined.
        /// </summary>
        Unknown
    }

    #endregion

    #region Storage Strategy Base Class

    /// <summary>
    /// Abstract base class for storage strategy implementations.
    /// Provides common infrastructure including health monitoring, metrics collection,
    /// retry logic, and connection management.
    /// Derived classes implement backend-specific operations.
    /// </summary>
    public abstract class StorageStrategyBase : StrategyBase, IStorageStrategy
    {
        private readonly SemaphoreSlim _healthCheckLock = new(1, 1);
        private DateTime _lastHealthCheck = DateTime.MinValue;
        private StorageHealthInfo? _cachedHealthInfo;
        private readonly TimeSpan _healthCacheDuration = TimeSpan.FromSeconds(30);

        // Metrics tracking
        private long _totalOperations;
        private long _successfulOperations;
        private long _failedOperations;
        private readonly List<double> _recentLatencies = new();
        private readonly object _metricsLock = new();

        /// <summary>
        /// Gets the unique identifier for this storage strategy.
        /// </summary>
        public override abstract string StrategyId { get; }

        /// <summary>
        /// Gets the human-readable name of this storage strategy.
        /// </summary>
        public override abstract string Name { get; }

        /// <summary>
        /// Gets the storage tier this strategy operates on.
        /// </summary>
        public abstract StorageTier Tier { get; }

        /// <summary>
        /// Gets the capabilities supported by this storage backend.
        /// </summary>
        public abstract StorageCapabilities Capabilities { get; }

        /// <summary>
        /// Maximum number of retry attempts for transient failures.
        /// Default is 3. Override to customize.
        /// </summary>
        protected virtual int MaxRetries => 3;

        /// <summary>
        /// Base delay for exponential backoff between retries.
        /// Default is 200ms. Override to customize.
        /// </summary>
        protected virtual TimeSpan RetryBaseDelay => TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// Determines if an exception represents a transient failure that should be retried.
        /// Override to customize retry logic for specific backends.
        /// </summary>
        /// <param name="exception">The exception to evaluate.</param>
        /// <returns>True if the operation should be retried, false otherwise.</returns>
        protected override bool IsTransientException(Exception exception)
        {
            return exception is IOException
                || exception is TimeoutException
                || exception is HttpRequestException;
        }

        #region Public Interface Implementation

        /// <inheritdoc/>
        public async Task<StorageObjectMetadata> StoreAsync(string key, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            return await ExecuteWithRetryAndMetricsAsync(
                async () => await StoreAsyncCore(key, data, metadata, ct),
                $"Store({key})",
                ct);
        }

        /// <inheritdoc/>
        public async Task<Stream> RetrieveAsync(string key, CancellationToken ct = default)
        {
            return await ExecuteWithRetryAndMetricsAsync(
                async () => await RetrieveAsyncCore(key, ct),
                $"Retrieve({key})",
                ct);
        }

        /// <inheritdoc/>
        public async Task DeleteAsync(string key, CancellationToken ct = default)
        {
            await ExecuteWithRetryAndMetricsAsync(
                async () =>
                {
                    await DeleteAsyncCore(key, ct);
                    return true;
                },
                $"Delete({key})",
                ct);
        }

        /// <inheritdoc/>
        public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        {
            return await ExecuteWithRetryAndMetricsAsync(
                async () => await ExistsAsyncCore(key, ct),
                $"Exists({key})",
                ct);
        }

        /// <inheritdoc/>
        public async IAsyncEnumerable<StorageObjectMetadata> ListAsync(string? prefix = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var item in ListAsyncCore(prefix, ct))
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }
        }

        /// <inheritdoc/>
        public async Task<StorageObjectMetadata> GetMetadataAsync(string key, CancellationToken ct = default)
        {
            return await ExecuteWithRetryAndMetricsAsync(
                async () => await GetMetadataAsyncCore(key, ct),
                $"GetMetadata({key})",
                ct);
        }

        /// <inheritdoc/>
        public async Task<StorageHealthInfo> GetHealthAsync(CancellationToken ct = default)
        {
            // Check if cached health info is still valid
            if (_cachedHealthInfo != null &&
                DateTime.UtcNow - _lastHealthCheck < _healthCacheDuration)
            {
                return _cachedHealthInfo;
            }

            await _healthCheckLock.WaitAsync(ct);
            try
            {
                // Double-check after acquiring lock
                if (_cachedHealthInfo != null &&
                    DateTime.UtcNow - _lastHealthCheck < _healthCacheDuration)
                {
                    return _cachedHealthInfo;
                }

                var sw = Stopwatch.StartNew();
                var health = await GetHealthAsyncCore(ct);
                sw.Stop();

                _cachedHealthInfo = health with { LatencyMs = sw.Elapsed.TotalMilliseconds };
                _lastHealthCheck = DateTime.UtcNow;

                return _cachedHealthInfo;
            }
            finally
            {
                _healthCheckLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<long?> GetAvailableCapacityAsync(CancellationToken ct = default)
        {
            return await GetAvailableCapacityAsyncCore(ct);
        }

        #endregion

        #region StorageAddress Overloads (HAL-05)

        /// <summary>Stores data using a StorageAddress. Override for native StorageAddress support.</summary>
        public virtual Task<StorageObjectMetadata> StoreAsync(StorageAddress address, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default)
            => StoreAsync(address.ToKey(), data, metadata, ct);

        /// <summary>Retrieves data using a StorageAddress. Override for native StorageAddress support.</summary>
        public virtual Task<Stream> RetrieveAsync(StorageAddress address, CancellationToken ct = default)
            => RetrieveAsync(address.ToKey(), ct);

        /// <summary>Deletes an object using a StorageAddress. Override for native StorageAddress support.</summary>
        public virtual Task DeleteAsync(StorageAddress address, CancellationToken ct = default)
            => DeleteAsync(address.ToKey(), ct);

        /// <summary>Checks existence using a StorageAddress. Override for native StorageAddress support.</summary>
        public virtual Task<bool> ExistsAsync(StorageAddress address, CancellationToken ct = default)
            => ExistsAsync(address.ToKey(), ct);

        /// <summary>Lists objects using a StorageAddress prefix. Override for native StorageAddress support.</summary>
        public virtual IAsyncEnumerable<StorageObjectMetadata> ListAsync(StorageAddress? prefix, CancellationToken ct = default)
            => ListAsync(prefix?.ToKey(), ct);

        /// <summary>Gets metadata using a StorageAddress. Override for native StorageAddress support.</summary>
        public virtual Task<StorageObjectMetadata> GetMetadataAsync(StorageAddress address, CancellationToken ct = default)
            => GetMetadataAsync(address.ToKey(), ct);

        /// <summary>Gets available capacity using a StorageAddress context. Override for native StorageAddress support.</summary>
        public virtual Task<long?> GetAvailableCapacityAsync(StorageAddress address, CancellationToken ct = default)
            => GetAvailableCapacityAsync(ct);

        #endregion

        #region Abstract Methods (Backend-Specific Implementation)

        /// <summary>
        /// Core implementation of store operation. Must be implemented by derived classes.
        /// </summary>
        protected abstract Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct);

        /// <summary>
        /// Core implementation of retrieve operation. Must be implemented by derived classes.
        /// </summary>
        protected abstract Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct);

        /// <summary>
        /// Core implementation of delete operation. Must be implemented by derived classes.
        /// </summary>
        protected abstract Task DeleteAsyncCore(string key, CancellationToken ct);

        /// <summary>
        /// Core implementation of exists check. Must be implemented by derived classes.
        /// </summary>
        protected abstract Task<bool> ExistsAsyncCore(string key, CancellationToken ct);

        /// <summary>
        /// Core implementation of list operation. Must be implemented by derived classes.
        /// </summary>
        protected abstract IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, CancellationToken ct);

        /// <summary>
        /// Core implementation of metadata retrieval. Must be implemented by derived classes.
        /// </summary>
        protected abstract Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct);

        /// <summary>
        /// Core implementation of health check. Must be implemented by derived classes.
        /// </summary>
        protected abstract Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct);

        /// <summary>
        /// Core implementation of capacity check. Must be implemented by derived classes.
        /// Returns null if capacity information is not available.
        /// </summary>
        protected abstract Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct);

        #endregion

        #region Retry Logic and Metrics

        /// <summary>
        /// Executes an operation with retry logic and metrics tracking.
        /// Automatically retries transient failures with exponential backoff.
        /// </summary>
        protected async Task<T> ExecuteWithRetryAndMetricsAsync<T>(
            Func<Task<T>> operation,
            string operationName,
            CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var attempt = 0;
            Exception? lastException = null;

            while (attempt <= MaxRetries)
            {
                try
                {
                    IncrementTotalOperations();

                    var result = await operation();

                    sw.Stop();
                    RecordLatency(sw.Elapsed.TotalMilliseconds);
                    IncrementSuccessfulOperations();

                    return result;
                }
                catch (Exception ex) when (attempt < MaxRetries && IsTransientException(ex))
                {
                    lastException = ex;
                    attempt++;

                    // Exponential backoff with jitter
                    var delay = TimeSpan.FromMilliseconds(
                        RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1) +
                        System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 100));

                    await Task.Delay(delay, ct);
                }
                catch (Exception ex)
                {
                    // Non-transient exception or max retries exceeded
                    sw.Stop();
                    IncrementFailedOperations();
                    throw;
                }
            }

            // Max retries exceeded
            sw.Stop();
            IncrementFailedOperations();
            throw new InvalidOperationException(
                $"Operation '{operationName}' failed after {MaxRetries} retry attempts.",
                lastException);
        }

        private void IncrementTotalOperations()
        {
            Interlocked.Increment(ref _totalOperations);
        }

        private void IncrementSuccessfulOperations()
        {
            Interlocked.Increment(ref _successfulOperations);
        }

        private void IncrementFailedOperations()
        {
            Interlocked.Increment(ref _failedOperations);
        }

        private void RecordLatency(double latencyMs)
        {
            lock (_metricsLock)
            {
                _recentLatencies.Add(latencyMs);

                // Keep only the last 100 latencies for moving average
                if (_recentLatencies.Count > 100)
                {
                    _recentLatencies.RemoveAt(0);
                }
            }
        }

        #endregion

        #region Metrics Access

        /// <summary>
        /// Gets the total number of operations executed.
        /// </summary>
        public long TotalOperations => Interlocked.Read(ref _totalOperations);

        /// <summary>
        /// Gets the number of successful operations.
        /// </summary>
        public long SuccessfulOperations => Interlocked.Read(ref _successfulOperations);

        /// <summary>
        /// Gets the number of failed operations.
        /// </summary>
        public long FailedOperations => Interlocked.Read(ref _failedOperations);

        /// <summary>
        /// Gets the success rate as a percentage (0-100).
        /// </summary>
        public double SuccessRate
        {
            get
            {
                var total = TotalOperations;
                return total > 0 ? (SuccessfulOperations * 100.0 / total) : 100.0;
            }
        }

        /// <summary>
        /// Gets the average latency of recent operations in milliseconds.
        /// </summary>
        public double AverageLatencyMs
        {
            get
            {
                lock (_metricsLock)
                {
                    return _recentLatencies.Count > 0 ? _recentLatencies.Average() : 0;
                }
            }
        }

        /// <summary>
        /// Resets all metrics counters.
        /// </summary>
        public void ResetMetrics()
        {
            Interlocked.Exchange(ref _totalOperations, 0);
            Interlocked.Exchange(ref _successfulOperations, 0);
            Interlocked.Exchange(ref _failedOperations, 0);

            lock (_metricsLock)
            {
                _recentLatencies.Clear();
            }
        }

        #endregion
    }

    #endregion
}
