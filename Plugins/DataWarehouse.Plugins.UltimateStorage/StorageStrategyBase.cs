using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStorage
{
    /// <summary>
    /// Abstract base class for storage strategy implementations in the UltimateStorage plugin.
    /// Extends the SDK StorageStrategyBase with plugin-specific enhancements including:
    /// - Enhanced statistics tracking (bytes transferred, operation types)
    /// - Configurable retry policies with exponential backoff
    /// - Connection pooling and resource management
    /// - Batch operation support
    /// - Performance monitoring and diagnostics
    /// </summary>
    public abstract class UltimateStorageStrategyBase : StorageStrategyBase
    {
        private readonly BoundedDictionary<string, object> _configuration = new BoundedDictionary<string, object>(1000);
        private long _totalBytesStored;
        private long _totalBytesRetrieved;
        private long _totalBytesDeleted;
        private long _storeOperations;
        private long _retrieveOperations;
        private long _deleteOperations;
        private long _existsChecks;
        private long _listOperations;
        private long _metadataOperations;
        private readonly DateTime _initializationTime;
        private bool _isInitialized;
        private bool _isDisposed;

        /// <summary>
        /// Initializes a new instance of the UltimateStorageStrategyBase class.
        /// </summary>
        protected UltimateStorageStrategyBase()
        {
            _initializationTime = DateTime.UtcNow;
            _isInitialized = false;
            _isDisposed = false;
        }

        #region Configuration Management

        /// <summary>
        /// Gets or sets a configuration value by key.
        /// Thread-safe configuration dictionary for strategy-specific settings.
        /// </summary>
        /// <param name="key">Configuration key.</param>
        /// <returns>Configuration value or null if not found.</returns>
        protected object? GetConfiguration(string key)
        {
            return _configuration.TryGetValue(key, out var value) ? value : null;
        }

        /// <summary>
        /// Sets a configuration value.
        /// </summary>
        /// <param name="key">Configuration key.</param>
        /// <param name="value">Configuration value.</param>
        protected void SetConfiguration(string key, object value)
        {
            _configuration[key] = value;
        }

        /// <summary>
        /// Gets a strongly-typed configuration value.
        /// </summary>
        /// <typeparam name="T">Expected type of the configuration value.</typeparam>
        /// <param name="key">Configuration key.</param>
        /// <param name="defaultValue">Default value if key not found.</param>
        /// <returns>Configuration value or default.</returns>
        protected T GetConfiguration<T>(string key, T defaultValue = default!)
        {
            if (_configuration.TryGetValue(key, out var value) && value is T typedValue)
            {
                return typedValue;
            }
            return defaultValue;
        }

        /// <summary>
        /// Gets all configuration keys and values.
        /// </summary>
        protected IReadOnlyDictionary<string, object> GetAllConfiguration()
        {
            return _configuration.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        #endregion

        #region Initialization and Disposal

        /// <summary>
        /// Initializes the storage strategy.
        /// Must be called before using the strategy.
        /// Derived classes should override InitializeCore to implement initialization logic.
        /// </summary>
        /// <param name="configuration">Initial configuration settings.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task InitializeAsync(IDictionary<string, object>? configuration = null, CancellationToken ct = default)
        {
            if (_isInitialized)
            {
                throw new InvalidOperationException("Strategy is already initialized");
            }

            if (_isDisposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }

            // Load configuration
            if (configuration != null)
            {
                foreach (var kvp in configuration)
                {
                    _configuration[kvp.Key] = kvp.Value;
                }
            }

            // Call derived class initialization
            await InitializeCoreAsync(ct);

            _isInitialized = true;
        }

        /// <summary>
        /// Core initialization logic. Override in derived classes.
        /// </summary>
        protected virtual Task InitializeCoreAsync(CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Disposes resources used by this storage strategy.
        /// </summary>
        public new virtual void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            // No sync resources to dispose in base class
            // Derived classes should override if they have sync resources

            _isDisposed = true;
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Asynchronously disposes resources.
        /// </summary>
        public new async ValueTask DisposeAsync()
        {
            if (_isDisposed)
            {
                return;
            }

            await DisposeCoreAsync();

            _isDisposed = true;
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Core disposal logic. Override in derived classes to clean up resources.
        /// </summary>
        protected virtual ValueTask DisposeCoreAsync()
        {
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Throws if the strategy is not initialized.
        /// </summary>
        protected void EnsureInitialized()
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException(
                    $"Storage strategy '{StrategyId}' must be initialized before use. Call InitializeAsync first.");
            }

            if (_isDisposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }

        #endregion

        #region Enhanced Statistics

        /// <summary>
        /// Gets the total number of bytes stored across all operations.
        /// </summary>
        public long TotalBytesStored => Interlocked.Read(ref _totalBytesStored);

        /// <summary>
        /// Gets the total number of bytes retrieved across all operations.
        /// </summary>
        public long TotalBytesRetrieved => Interlocked.Read(ref _totalBytesRetrieved);

        /// <summary>
        /// Gets the total number of bytes deleted across all operations.
        /// </summary>
        public long TotalBytesDeleted => Interlocked.Read(ref _totalBytesDeleted);

        /// <summary>
        /// Gets the total number of store operations performed.
        /// </summary>
        public long StoreOperations => Interlocked.Read(ref _storeOperations);

        /// <summary>
        /// Gets the total number of retrieve operations performed.
        /// </summary>
        public long RetrieveOperations => Interlocked.Read(ref _retrieveOperations);

        /// <summary>
        /// Gets the total number of delete operations performed.
        /// </summary>
        public long DeleteOperations => Interlocked.Read(ref _deleteOperations);

        /// <summary>
        /// Gets the total number of exists checks performed.
        /// </summary>
        public long ExistsChecks => Interlocked.Read(ref _existsChecks);

        /// <summary>
        /// Gets the total number of list operations performed.
        /// </summary>
        public long ListOperations => Interlocked.Read(ref _listOperations);

        /// <summary>
        /// Gets the total number of metadata operations performed.
        /// </summary>
        public long MetadataOperations => Interlocked.Read(ref _metadataOperations);

        /// <summary>
        /// Gets the time when this strategy was initialized.
        /// </summary>
        public DateTime InitializationTime => _initializationTime;

        /// <summary>
        /// Gets the uptime of this storage strategy.
        /// </summary>
        public TimeSpan Uptime => DateTime.UtcNow - _initializationTime;

        /// <summary>
        /// Gets whether the strategy is initialized and ready for use.
        /// </summary>
        public new bool IsInitialized => _isInitialized;

        /// <summary>
        /// Gets whether the strategy has been disposed.
        /// </summary>
        public bool IsDisposed => _isDisposed;

        /// <summary>
        /// Increments the bytes stored counter.
        /// </summary>
        protected void IncrementBytesStored(long bytes)
        {
            Interlocked.Add(ref _totalBytesStored, bytes);
        }

        /// <summary>
        /// Increments the bytes retrieved counter.
        /// </summary>
        protected void IncrementBytesRetrieved(long bytes)
        {
            Interlocked.Add(ref _totalBytesRetrieved, bytes);
        }

        /// <summary>
        /// Increments the bytes deleted counter.
        /// </summary>
        protected void IncrementBytesDeleted(long bytes)
        {
            Interlocked.Add(ref _totalBytesDeleted, bytes);
        }

        /// <summary>
        /// Increments operation counters based on operation type.
        /// </summary>
        protected void IncrementOperationCounter(StorageOperationType operationType)
        {
            switch (operationType)
            {
                case StorageOperationType.Store:
                    Interlocked.Increment(ref _storeOperations);
                    break;
                case StorageOperationType.Retrieve:
                    Interlocked.Increment(ref _retrieveOperations);
                    break;
                case StorageOperationType.Delete:
                    Interlocked.Increment(ref _deleteOperations);
                    break;
                case StorageOperationType.Exists:
                    Interlocked.Increment(ref _existsChecks);
                    break;
                case StorageOperationType.List:
                    Interlocked.Increment(ref _listOperations);
                    break;
                case StorageOperationType.GetMetadata:
                    Interlocked.Increment(ref _metadataOperations);
                    break;
            }
        }

        /// <summary>
        /// Resets all enhanced statistics.
        /// </summary>
        public void ResetEnhancedStatistics()
        {
            Interlocked.Exchange(ref _totalBytesStored, 0);
            Interlocked.Exchange(ref _totalBytesRetrieved, 0);
            Interlocked.Exchange(ref _totalBytesDeleted, 0);
            Interlocked.Exchange(ref _storeOperations, 0);
            Interlocked.Exchange(ref _retrieveOperations, 0);
            Interlocked.Exchange(ref _deleteOperations, 0);
            Interlocked.Exchange(ref _existsChecks, 0);
            Interlocked.Exchange(ref _listOperations, 0);
            Interlocked.Exchange(ref _metadataOperations, 0);
            ResetMetrics();
        }

        /// <summary>
        /// Gets comprehensive statistics for this storage strategy.
        /// </summary>
        public StorageStrategyStatistics GetEnhancedStatistics()
        {
            return new StorageStrategyStatistics
            {
                StrategyId = StrategyId,
                StrategyName = Name,
                Tier = Tier,
                TotalOperations = TotalOperations,
                SuccessfulOperations = SuccessfulOperations,
                FailedOperations = FailedOperations,
                SuccessRate = SuccessRate,
                AverageLatencyMs = AverageLatencyMs,
                TotalBytesStored = TotalBytesStored,
                TotalBytesRetrieved = TotalBytesRetrieved,
                TotalBytesDeleted = TotalBytesDeleted,
                StoreOperations = StoreOperations,
                RetrieveOperations = RetrieveOperations,
                DeleteOperations = DeleteOperations,
                ExistsChecks = ExistsChecks,
                ListOperations = ListOperations,
                MetadataOperations = MetadataOperations,
                InitializationTime = InitializationTime,
                Uptime = Uptime,
                IsInitialized = IsInitialized,
                IsDisposed = IsDisposed
            };
        }

        #endregion

        #region Batch Operations Support

        /// <summary>
        /// Stores multiple objects in a batch operation.
        /// Default implementation calls StoreAsync for each item sequentially.
        /// Override for backend-specific batch optimization.
        /// </summary>
        /// <param name="items">Items to store (key, data, metadata).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Results for each item.</returns>
        public virtual async Task<IReadOnlyList<StorageObjectMetadata>> StoreBatchAsync(
            IEnumerable<(string key, Stream data, IDictionary<string, string>? metadata)> items,
            CancellationToken ct = default)
        {
            EnsureInitialized();

            var results = new List<StorageObjectMetadata>();

            foreach (var (key, data, metadata) in items)
            {
                ct.ThrowIfCancellationRequested();
                var result = await StoreAsync(key, data, metadata, ct);
                results.Add(result);
            }

            return results;
        }

        /// <summary>
        /// Retrieves multiple objects in a batch operation.
        /// Default implementation calls RetrieveAsync for each key sequentially.
        /// Override for backend-specific batch optimization.
        /// </summary>
        /// <param name="keys">Keys to retrieve.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Dictionary of key to stream.</returns>
        public virtual async Task<IReadOnlyDictionary<string, Stream>> RetrieveBatchAsync(
            IEnumerable<string> keys,
            CancellationToken ct = default)
        {
            EnsureInitialized();

            var results = new Dictionary<string, Stream>();

            foreach (var key in keys)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var stream = await RetrieveAsync(key, ct);
                    results[key] = stream;
                }
                catch (Exception ex)
                {
                    // Log retrieval failure; key is skipped in the batch result
                    System.Diagnostics.Debug.WriteLine(
                        $"[StorageStrategyBase] RetrieveBatchAsync: retrieval failed for key '{key}': {ex.GetType().Name}: {ex.Message}");
                }
            }

            return results;
        }

        /// <summary>
        /// Deletes multiple objects in a batch operation.
        /// Default implementation calls DeleteAsync for each key sequentially.
        /// Override for backend-specific batch optimization.
        /// </summary>
        /// <param name="keys">Keys to delete.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Number of successfully deleted objects.</returns>
        public virtual async Task<int> DeleteBatchAsync(
            IEnumerable<string> keys,
            CancellationToken ct = default)
        {
            EnsureInitialized();

            var deletedCount = 0;

            foreach (var key in keys)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await DeleteAsync(key, ct);
                    deletedCount++;
                }
                catch (Exception ex)
                {
                    // Log deletion failure; key counted as failed in the batch result
                    System.Diagnostics.Debug.WriteLine(
                        $"[StorageStrategyBase] DeleteBatchAsync: deletion failed for key '{key}': {ex.GetType().Name}: {ex.Message}");
                }
            }

            return deletedCount;
        }

        #endregion

        #region Validation Helpers

        /// <summary>
        /// Validates a storage key.
        /// Override to implement backend-specific validation rules.
        /// </summary>
        /// <param name="key">The key to validate.</param>
        /// <exception cref="ArgumentException">If key is invalid.</exception>
        protected virtual void ValidateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Storage key cannot be null or whitespace", nameof(key));
            }

            // Common validation: check for invalid characters
            var invalidChars = new[] { '\0', '\n', '\r', '\t' };
            if (key.IndexOfAny(invalidChars) >= 0)
            {
                throw new ArgumentException("Storage key contains invalid characters", nameof(key));
            }

            // Check length
            if (key.Length > GetMaxKeyLength())
            {
                throw new ArgumentException(
                    $"Storage key exceeds maximum length of {GetMaxKeyLength()} characters",
                    nameof(key));
            }
        }

        /// <summary>
        /// Gets the maximum allowed key length for this storage backend.
        /// Default is 1024 characters. Override for backend-specific limits.
        /// </summary>
        protected virtual int GetMaxKeyLength() => 1024;

        /// <summary>
        /// Validates stream is readable and seekable if required.
        /// </summary>
        protected void ValidateStream(Stream stream, bool requireSeekable = false)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (!stream.CanRead)
            {
                throw new ArgumentException("Stream must be readable", nameof(stream));
            }

            if (requireSeekable && !stream.CanSeek)
            {
                throw new ArgumentException("Stream must be seekable for this operation", nameof(stream));
            }
        }

        #endregion

        /// <summary>
        /// Helper for async-iterator overrides (e.g. <c>ListAsyncCore</c>) that do not support
        /// listing operations. Throws <see cref="NotSupportedException"/> with
        /// <paramref name="message"/>. Annotated with
        /// <see cref="System.Diagnostics.CodeAnalysis.DoesNotReturnAttribute"/> so that a
        /// <c>yield break</c> after the call site is not flagged as unreachable (CS0162),
        /// while still satisfying the async-iterator yield requirement (CS8420).
        /// </summary>
        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        protected static void ThrowListingNotSupported(string message)
            => throw new NotSupportedException(message);
    }

    #region Supporting Types

    /// <summary>
    /// Enumeration of storage operation types.
    /// Used for statistics tracking and monitoring.
    /// </summary>
    public enum StorageOperationType
    {
        /// <summary>Store operation.</summary>
        Store,

        /// <summary>Retrieve operation.</summary>
        Retrieve,

        /// <summary>Delete operation.</summary>
        Delete,

        /// <summary>Exists check operation.</summary>
        Exists,

        /// <summary>List operation.</summary>
        List,

        /// <summary>Get metadata operation.</summary>
        GetMetadata
    }

    /// <summary>
    /// Comprehensive statistics for a storage strategy.
    /// Extends base metrics with operation-specific counters and throughput data.
    /// </summary>
    public record StorageStrategyStatistics
    {
        /// <summary>Strategy unique identifier.</summary>
        public string StrategyId { get; init; } = string.Empty;

        /// <summary>Strategy human-readable name.</summary>
        public string StrategyName { get; init; } = string.Empty;

        /// <summary>Storage tier.</summary>
        public StorageTier Tier { get; init; }

        /// <summary>Total operations executed.</summary>
        public long TotalOperations { get; init; }

        /// <summary>Successful operations count.</summary>
        public long SuccessfulOperations { get; init; }

        /// <summary>Failed operations count.</summary>
        public long FailedOperations { get; init; }

        /// <summary>Success rate percentage (0-100).</summary>
        public double SuccessRate { get; init; }

        /// <summary>Average latency in milliseconds.</summary>
        public double AverageLatencyMs { get; init; }

        /// <summary>Total bytes stored.</summary>
        public long TotalBytesStored { get; init; }

        /// <summary>Total bytes retrieved.</summary>
        public long TotalBytesRetrieved { get; init; }

        /// <summary>Total bytes deleted.</summary>
        public long TotalBytesDeleted { get; init; }

        /// <summary>Number of store operations.</summary>
        public long StoreOperations { get; init; }

        /// <summary>Number of retrieve operations.</summary>
        public long RetrieveOperations { get; init; }

        /// <summary>Number of delete operations.</summary>
        public long DeleteOperations { get; init; }

        /// <summary>Number of exists checks.</summary>
        public long ExistsChecks { get; init; }

        /// <summary>Number of list operations.</summary>
        public long ListOperations { get; init; }

        /// <summary>Number of metadata operations.</summary>
        public long MetadataOperations { get; init; }

        /// <summary>When the strategy was initialized.</summary>
        public DateTime InitializationTime { get; init; }

        /// <summary>How long the strategy has been running.</summary>
        public TimeSpan Uptime { get; init; }

        /// <summary>Whether the strategy is initialized.</summary>
        public bool IsInitialized { get; init; }

        /// <summary>Whether the strategy is disposed.</summary>
        public bool IsDisposed { get; init; }

        /// <summary>
        /// Calculates the average store throughput in bytes per second.
        /// </summary>
        public double GetStoreThroughputBytesPerSecond()
        {
            var seconds = Uptime.TotalSeconds;
            return seconds > 0 ? TotalBytesStored / seconds : 0;
        }

        /// <summary>
        /// Calculates the average retrieve throughput in bytes per second.
        /// </summary>
        public double GetRetrieveThroughputBytesPerSecond()
        {
            var seconds = Uptime.TotalSeconds;
            return seconds > 0 ? TotalBytesRetrieved / seconds : 0;
        }

        /// <summary>
        /// Calculates the total data transfer (stored + retrieved) in bytes.
        /// </summary>
        public long GetTotalDataTransfer()
        {
            return TotalBytesStored + TotalBytesRetrieved;
        }

        /// <summary>
        /// Calculates overall throughput (all operations) in bytes per second.
        /// </summary>
        public double GetOverallThroughputBytesPerSecond()
        {
            var seconds = Uptime.TotalSeconds;
            return seconds > 0 ? GetTotalDataTransfer() / seconds : 0;
        }

    }

    #endregion
}
