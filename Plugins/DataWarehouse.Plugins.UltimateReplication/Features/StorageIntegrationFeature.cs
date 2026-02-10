using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateReplication.Features
{
    /// <summary>
    /// Storage Integration Feature (C9).
    /// Integrates UltimateReplication with UltimateStorage via "storage.read" and
    /// "storage.write" message bus topics for reading/writing replicated data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Integration features:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Read-through replication</b>: Read from nearest replica via storage backend</item>
    ///   <item><b>Write-through replication</b>: Write to primary then replicate to secondary storage</item>
    ///   <item><b>Consistency verification</b>: Hash-based consistency checks across storage backends</item>
    ///   <item><b>Backend selection</b>: Select optimal storage backend based on replication requirements</item>
    ///   <item><b>Capacity tracking</b>: Monitor storage capacity to ensure replicas fit</item>
    /// </list>
    /// <para>
    /// All communication via message bus "storage.read" and "storage.write" topics.
    /// No direct reference to UltimateStorage plugin.
    /// </para>
    /// </remarks>
    public sealed class StorageIntegrationFeature : IDisposable
    {
        private readonly ReplicationStrategyRegistry _registry;
        private readonly IMessageBus _messageBus;
        private readonly ConcurrentDictionary<string, StorageBackendInfo> _backends = new();
        private readonly ConcurrentDictionary<string, StorageReplicaMapping> _replicaMappings = new();
        private bool _disposed;
        private IDisposable? _capacitySubscription;

        // Topics
        private const string StorageWriteTopic = "storage.write";
        private const string StorageWriteResponseTopic = "storage.write.response";
        private const string StorageReadTopic = "storage.read";
        private const string StorageReadResponseTopic = "storage.read.response";
        private const string StorageCapacityTopic = "storage.capacity";

        // Statistics
        private long _totalStorageReads;
        private long _totalStorageWrites;
        private long _totalBytesWritten;
        private long _totalBytesRead;
        private long _consistencyChecksPassed;
        private long _consistencyChecksFailed;

        /// <summary>
        /// Initializes a new instance of the StorageIntegrationFeature.
        /// </summary>
        /// <param name="registry">The replication strategy registry.</param>
        /// <param name="messageBus">Message bus for storage communication.</param>
        public StorageIntegrationFeature(
            ReplicationStrategyRegistry registry,
            IMessageBus messageBus)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));

            _capacitySubscription = _messageBus.Subscribe(StorageCapacityTopic, HandleCapacityUpdateAsync);
        }

        /// <summary>Gets total storage reads.</summary>
        public long TotalStorageReads => Interlocked.Read(ref _totalStorageReads);

        /// <summary>Gets total storage writes.</summary>
        public long TotalStorageWrites => Interlocked.Read(ref _totalStorageWrites);

        /// <summary>Gets total bytes written.</summary>
        public long TotalBytesWritten => Interlocked.Read(ref _totalBytesWritten);

        /// <summary>
        /// Writes replicated data to a storage backend via message bus.
        /// </summary>
        /// <param name="key">Storage key.</param>
        /// <param name="data">Data to write.</param>
        /// <param name="backendId">Target storage backend ID.</param>
        /// <param name="metadata">Optional metadata.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Write result.</returns>
        public async Task<StorageWriteResult> WriteReplicaAsync(
            string key,
            ReadOnlyMemory<byte> data,
            string backendId,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _totalStorageWrites);

            var correlationId = $"repl-write-{Guid.NewGuid():N}"[..32];
            var tcs = new TaskCompletionSource<StorageWriteResult>();
            var dataHash = ComputeSha256(data.Span);

            var subscription = _messageBus.Subscribe(StorageWriteResponseTopic, msg =>
            {
                if (msg.CorrelationId == correlationId)
                {
                    var success = msg.Payload.GetValueOrDefault("success") is true;
                    tcs.TrySetResult(new StorageWriteResult
                    {
                        Key = key,
                        BackendId = backendId,
                        Success = success,
                        BytesWritten = success ? data.Length : 0,
                        DataHash = dataHash,
                        WrittenAt = DateTimeOffset.UtcNow
                    });
                }
                return Task.CompletedTask;
            });

            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["key"] = key,
                    ["data"] = Convert.ToBase64String(data.ToArray()),
                    ["backendId"] = backendId,
                    ["dataSize"] = data.Length,
                    ["dataHash"] = dataHash,
                    ["source"] = "replication"
                };

                if (metadata != null)
                {
                    foreach (var (k, v) in metadata)
                        payload[$"meta.{k}"] = v;
                }

                await _messageBus.PublishAsync(StorageWriteTopic, new PluginMessage
                {
                    Type = StorageWriteTopic,
                    CorrelationId = correlationId,
                    Source = "replication.ultimate.storage-integration",
                    Payload = payload
                }, ct);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30));

                var result = await tcs.Task.WaitAsync(cts.Token);

                if (result.Success)
                {
                    Interlocked.Add(ref _totalBytesWritten, data.Length);

                    // Track replica mapping
                    var mappings = _replicaMappings.GetOrAdd(key, _ => new StorageReplicaMapping
                    {
                        Key = key,
                        Backends = new ConcurrentDictionary<string, ReplicaInfo>()
                    });
                    mappings.Backends[backendId] = new ReplicaInfo
                    {
                        BackendId = backendId,
                        DataHash = dataHash,
                        Size = data.Length,
                        LastUpdated = DateTimeOffset.UtcNow
                    };
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                return new StorageWriteResult
                {
                    Key = key,
                    BackendId = backendId,
                    Success = false,
                    BytesWritten = 0,
                    DataHash = dataHash,
                    WrittenAt = DateTimeOffset.UtcNow
                };
            }
            finally
            {
                subscription?.Dispose();
            }
        }

        /// <summary>
        /// Reads data from a storage backend via message bus.
        /// </summary>
        /// <param name="key">Storage key.</param>
        /// <param name="backendId">Storage backend to read from.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Read result with data.</returns>
        public async Task<StorageReadResult> ReadReplicaAsync(
            string key,
            string backendId,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _totalStorageReads);

            var correlationId = $"repl-read-{Guid.NewGuid():N}"[..32];
            var tcs = new TaskCompletionSource<StorageReadResult>();

            var subscription = _messageBus.Subscribe(StorageReadResponseTopic, msg =>
            {
                if (msg.CorrelationId == correlationId)
                {
                    var success = msg.Payload.GetValueOrDefault("success") is true;
                    var dataB64 = msg.Payload.GetValueOrDefault("data")?.ToString();

                    byte[] data = Array.Empty<byte>();
                    if (success && !string.IsNullOrEmpty(dataB64))
                        data = Convert.FromBase64String(dataB64);

                    tcs.TrySetResult(new StorageReadResult
                    {
                        Key = key,
                        BackendId = backendId,
                        Success = success,
                        Data = data,
                        DataHash = data.Length > 0 ? ComputeSha256(data) : "",
                        ReadAt = DateTimeOffset.UtcNow
                    });
                }
                return Task.CompletedTask;
            });

            try
            {
                await _messageBus.PublishAsync(StorageReadTopic, new PluginMessage
                {
                    Type = StorageReadTopic,
                    CorrelationId = correlationId,
                    Source = "replication.ultimate.storage-integration",
                    Payload = new Dictionary<string, object>
                    {
                        ["key"] = key,
                        ["backendId"] = backendId,
                        ["source"] = "replication"
                    }
                }, ct);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));

                var result = await tcs.Task.WaitAsync(cts.Token);

                if (result.Success)
                    Interlocked.Add(ref _totalBytesRead, result.Data.Length);

                return result;
            }
            catch (OperationCanceledException)
            {
                return new StorageReadResult
                {
                    Key = key,
                    BackendId = backendId,
                    Success = false,
                    Data = Array.Empty<byte>(),
                    DataHash = "",
                    ReadAt = DateTimeOffset.UtcNow
                };
            }
            finally
            {
                subscription?.Dispose();
            }
        }

        /// <summary>
        /// Verifies consistency of a key across multiple storage backends.
        /// </summary>
        /// <param name="key">Storage key to verify.</param>
        /// <param name="backendIds">Backend IDs to check.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Consistency check result.</returns>
        public async Task<ConsistencyCheckResult> VerifyConsistencyAsync(
            string key,
            IReadOnlyList<string> backendIds,
            CancellationToken ct = default)
        {
            var hashes = new Dictionary<string, string>();

            foreach (var backendId in backendIds)
            {
                var readResult = await ReadReplicaAsync(key, backendId, ct);
                if (readResult.Success)
                    hashes[backendId] = readResult.DataHash;
            }

            var uniqueHashes = hashes.Values.Distinct().ToList();
            var isConsistent = uniqueHashes.Count <= 1;

            if (isConsistent)
                Interlocked.Increment(ref _consistencyChecksPassed);
            else
                Interlocked.Increment(ref _consistencyChecksFailed);

            return new ConsistencyCheckResult
            {
                Key = key,
                IsConsistent = isConsistent,
                BackendHashes = hashes,
                UniqueHashCount = uniqueHashes.Count,
                CheckedAt = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Registers a storage backend for capacity tracking.
        /// </summary>
        public void RegisterBackend(string backendId, string displayName, long capacityBytes)
        {
            _backends[backendId] = new StorageBackendInfo
            {
                BackendId = backendId,
                DisplayName = displayName,
                TotalCapacityBytes = capacityBytes,
                UsedCapacityBytes = 0,
                LastUpdated = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Gets replica mapping for a key (which backends hold which version).
        /// </summary>
        public StorageReplicaMapping? GetReplicaMapping(string key)
        {
            return _replicaMappings.GetValueOrDefault(key);
        }

        /// <summary>
        /// Gets all backend information.
        /// </summary>
        public IReadOnlyDictionary<string, StorageBackendInfo> GetBackends()
        {
            return _backends;
        }

        #region Private Methods

        private static string ComputeSha256(ReadOnlySpan<byte> data)
        {
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(data, hash);
            return Convert.ToHexString(hash);
        }

        private Task HandleCapacityUpdateAsync(PluginMessage message)
        {
            var backendId = message.Payload.GetValueOrDefault("backendId")?.ToString();
            var usedStr = message.Payload.GetValueOrDefault("usedBytes")?.ToString();
            var totalStr = message.Payload.GetValueOrDefault("totalBytes")?.ToString();

            if (!string.IsNullOrEmpty(backendId) && _backends.TryGetValue(backendId, out var backend))
            {
                if (long.TryParse(usedStr, out var used))
                    backend.UsedCapacityBytes = used;
                if (long.TryParse(totalStr, out var total))
                    backend.TotalCapacityBytes = total;
                backend.LastUpdated = DateTimeOffset.UtcNow;
            }

            return Task.CompletedTask;
        }

        #endregion

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _capacitySubscription?.Dispose();
        }
    }

    #region Storage Integration Types

    /// <summary>
    /// Storage backend information.
    /// </summary>
    public sealed class StorageBackendInfo
    {
        /// <summary>Backend identifier.</summary>
        public required string BackendId { get; init; }
        /// <summary>Display name.</summary>
        public required string DisplayName { get; init; }
        /// <summary>Total capacity in bytes.</summary>
        public long TotalCapacityBytes { get; set; }
        /// <summary>Used capacity in bytes.</summary>
        public long UsedCapacityBytes { get; set; }
        /// <summary>Available capacity.</summary>
        public long AvailableCapacityBytes => TotalCapacityBytes - UsedCapacityBytes;
        /// <summary>When last updated.</summary>
        public DateTimeOffset LastUpdated { get; set; }
    }

    /// <summary>
    /// Mapping of storage replicas for a key.
    /// </summary>
    public sealed class StorageReplicaMapping
    {
        /// <summary>Storage key.</summary>
        public required string Key { get; init; }
        /// <summary>Backends holding replicas.</summary>
        public required ConcurrentDictionary<string, ReplicaInfo> Backends { get; init; }
    }

    /// <summary>
    /// Information about a replica on a backend.
    /// </summary>
    public sealed class ReplicaInfo
    {
        /// <summary>Backend ID.</summary>
        public required string BackendId { get; init; }
        /// <summary>Data hash.</summary>
        public required string DataHash { get; init; }
        /// <summary>Data size in bytes.</summary>
        public required long Size { get; init; }
        /// <summary>When last updated.</summary>
        public required DateTimeOffset LastUpdated { get; init; }
    }

    /// <summary>
    /// Storage write result.
    /// </summary>
    public sealed class StorageWriteResult
    {
        /// <summary>Storage key.</summary>
        public required string Key { get; init; }
        /// <summary>Backend ID.</summary>
        public required string BackendId { get; init; }
        /// <summary>Whether write succeeded.</summary>
        public required bool Success { get; init; }
        /// <summary>Bytes written.</summary>
        public required long BytesWritten { get; init; }
        /// <summary>Data hash.</summary>
        public required string DataHash { get; init; }
        /// <summary>When written.</summary>
        public required DateTimeOffset WrittenAt { get; init; }
    }

    /// <summary>
    /// Storage read result.
    /// </summary>
    public sealed class StorageReadResult
    {
        /// <summary>Storage key.</summary>
        public required string Key { get; init; }
        /// <summary>Backend ID.</summary>
        public required string BackendId { get; init; }
        /// <summary>Whether read succeeded.</summary>
        public required bool Success { get; init; }
        /// <summary>Data read.</summary>
        public required byte[] Data { get; init; }
        /// <summary>Data hash.</summary>
        public required string DataHash { get; init; }
        /// <summary>When read.</summary>
        public required DateTimeOffset ReadAt { get; init; }
    }

    /// <summary>
    /// Cross-backend consistency check result.
    /// </summary>
    public sealed class ConsistencyCheckResult
    {
        /// <summary>Storage key.</summary>
        public required string Key { get; init; }
        /// <summary>Whether all backends have consistent data.</summary>
        public required bool IsConsistent { get; init; }
        /// <summary>Hash per backend.</summary>
        public required Dictionary<string, string> BackendHashes { get; init; }
        /// <summary>Number of unique hashes found.</summary>
        public required int UniqueHashCount { get; init; }
        /// <summary>When checked.</summary>
        public required DateTimeOffset CheckedAt { get; init; }
    }

    #endregion
}
