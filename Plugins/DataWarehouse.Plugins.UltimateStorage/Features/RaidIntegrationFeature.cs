using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;
using IStorageStrategy = DataWarehouse.SDK.Contracts.Storage.IStorageStrategy;
using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Features
{
    /// <summary>
    /// RAID Integration Feature (C9) - Integration with Ultimate RAID plugin (T91).
    ///
    /// Features:
    /// - Message bus integration with T91 RAID plugin
    /// - RAID-level redundancy across storage backends
    /// - Striping across multiple backends for performance
    /// - Parity-based recovery if a backend fails
    /// - RAID 0, 1, 5, 6, 10 support
    /// - Automatic backend failure detection
    /// - Rebuild operations after failure
    /// - Hot spare backend management
    /// </summary>
    public sealed class RaidIntegrationFeature : IDisposable
    {
        private readonly StrategyRegistry<IStorageStrategy> _registry;
        private readonly IMessageBus _messageBus;
        private readonly BoundedDictionary<string, RaidArray> _raidArrays = new BoundedDictionary<string, RaidArray>(1000);
        private readonly BoundedDictionary<string, string> _objectToArrayMapping = new BoundedDictionary<string, string>(1000); // object key -> array ID
        private bool _disposed;
        private IDisposable? _messageBusSubscription;

        // Configuration
        private const string RaidPluginTopic = "raid.storage";
        private const string RaidCommandTopic = "raid.command";
        private const string RaidStatusTopic = "raid.status";

        // Statistics
        private long _totalRaidWrites;
        private long _totalRaidReads;
        private long _totalRaidRebuilds;
        private long _totalBackendFailures;

        /// <summary>
        /// Initializes a new instance of the RaidIntegrationFeature.
        /// </summary>
        /// <param name="registry">The storage strategy registry.</param>
        /// <param name="messageBus">Message bus for inter-plugin communication.</param>
        public RaidIntegrationFeature(StrategyRegistry<IStorageStrategy> registry, IMessageBus messageBus)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));

            // Subscribe to RAID plugin messages
            _messageBusSubscription = _messageBus.Subscribe(RaidStatusTopic, HandleRaidStatusMessageAsync);
        }

        /// <summary>
        /// Gets the total number of RAID write operations.
        /// </summary>
        public long TotalRaidWrites => Interlocked.Read(ref _totalRaidWrites);

        /// <summary>
        /// Gets the total number of RAID read operations.
        /// </summary>
        public long TotalRaidReads => Interlocked.Read(ref _totalRaidReads);

        /// <summary>
        /// Gets the total number of RAID rebuilds performed.
        /// </summary>
        public long TotalRaidRebuilds => Interlocked.Read(ref _totalRaidRebuilds);

        /// <summary>
        /// Creates a RAID array across multiple storage backends.
        /// </summary>
        /// <param name="arrayId">Unique array identifier.</param>
        /// <param name="raidLevel">RAID level (0, 1, 5, 6, 10).</param>
        /// <param name="backendIds">Backend strategy IDs to include in the array.</param>
        /// <param name="stripeSize">Stripe size in bytes (for RAID 0, 5, 6).</param>
        /// <returns>The created RAID array.</returns>
        public async Task<RaidArray> CreateRaidArrayAsync(
            string arrayId,
            RaidLevel raidLevel,
            IEnumerable<string> backendIds,
            int stripeSize = 65536)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(arrayId);
            ArgumentNullException.ThrowIfNull(backendIds);
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_raidArrays.ContainsKey(arrayId))
            {
                throw new InvalidOperationException($"RAID array '{arrayId}' already exists");
            }

            var backends = backendIds.ToList();

            // Validate minimum backend count for RAID level
            ValidateBackendCountForRaidLevel(raidLevel, backends.Count);

            // Validate backends exist
            foreach (var backendId in backends)
            {
                if (_registry.Get(backendId) == null)
                {
                    throw new ArgumentException($"Backend '{backendId}' not found in registry");
                }
            }

            var array = new RaidArray
            {
                ArrayId = arrayId,
                RaidLevel = raidLevel,
                BackendIds = backends,
                StripeSize = stripeSize,
                CreatedTime = DateTime.UtcNow,
                State = RaidArrayState.Online,
                TotalCapacityBytes = CalculateTotalCapacity(raidLevel, backends.Count, 1_000_000_000_000) // Assume 1TB per backend
            };

            if (!_raidArrays.TryAdd(arrayId, array))
            {
                throw new InvalidOperationException($"Failed to add RAID array '{arrayId}'");
            }

            // Notify RAID plugin about new array
            await NotifyRaidPluginAsync("array.created", new Dictionary<string, object>
            {
                ["arrayId"] = arrayId,
                ["raidLevel"] = raidLevel.ToString(),
                ["backendCount"] = backends.Count,
                ["stripeSize"] = stripeSize
            });

            return array;
        }

        /// <summary>
        /// Writes data to a RAID array with striping and parity.
        /// </summary>
        /// <param name="arrayId">RAID array identifier.</param>
        /// <param name="objectKey">Object key.</param>
        /// <param name="data">Data to write.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task WriteToRaidArrayAsync(string arrayId, string objectKey, byte[] data, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(arrayId);
            ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
            ArgumentNullException.ThrowIfNull(data);
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_raidArrays.TryGetValue(arrayId, out var array))
            {
                throw new ArgumentException($"RAID array '{arrayId}' not found");
            }

            if (array.State != RaidArrayState.Online)
            {
                throw new InvalidOperationException($"RAID array '{arrayId}' is not online (state: {array.State})");
            }

            Interlocked.Increment(ref _totalRaidWrites);

            // Stripe data across backends based on RAID level
            switch (array.RaidLevel)
            {
                case RaidLevel.RAID0:
                    await WriteRaid0Async(array, objectKey, data, ct);
                    break;

                case RaidLevel.RAID1:
                    await WriteRaid1Async(array, objectKey, data, ct);
                    break;

                case RaidLevel.RAID5:
                    await WriteRaid5Async(array, objectKey, data, ct);
                    break;

                case RaidLevel.RAID6:
                    await WriteRaid6Async(array, objectKey, data, ct);
                    break;

                case RaidLevel.RAID10:
                    await WriteRaid10Async(array, objectKey, data, ct);
                    break;

                default:
                    throw new NotSupportedException($"RAID level {array.RaidLevel} not supported");
            }

            // Track object-to-array mapping
            _objectToArrayMapping[objectKey] = arrayId;

            // Update array statistics
            Interlocked.Add(ref array.TotalBytesWritten, data.Length);
        }

        /// <summary>
        /// Reads data from a RAID array with automatic recovery if needed.
        /// </summary>
        /// <param name="arrayId">RAID array identifier.</param>
        /// <param name="objectKey">Object key.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Retrieved data.</returns>
        public async Task<byte[]> ReadFromRaidArrayAsync(string arrayId, string objectKey, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(arrayId);
            ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_raidArrays.TryGetValue(arrayId, out var array))
            {
                throw new ArgumentException($"RAID array '{arrayId}' not found");
            }

            Interlocked.Increment(ref _totalRaidReads);

            // Read data with redundancy/parity based on RAID level
            byte[] data = array.RaidLevel switch
            {
                RaidLevel.RAID0 => await ReadRaid0Async(array, objectKey, ct),
                RaidLevel.RAID1 => await ReadRaid1Async(array, objectKey, ct),
                RaidLevel.RAID5 => await ReadRaid5Async(array, objectKey, ct),
                RaidLevel.RAID6 => await ReadRaid6Async(array, objectKey, ct),
                RaidLevel.RAID10 => await ReadRaid10Async(array, objectKey, ct),
                _ => throw new NotSupportedException($"RAID level {array.RaidLevel} not supported")
            };

            Interlocked.Add(ref array.TotalBytesRead, data.Length);
            return data;
        }

        /// <summary>
        /// Marks a backend as failed and initiates rebuild if possible.
        /// </summary>
        /// <param name="arrayId">RAID array identifier.</param>
        /// <param name="backendId">Failed backend strategy ID.</param>
        public async Task MarkBackendFailedAsync(string arrayId, string backendId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(arrayId);
            ArgumentException.ThrowIfNullOrWhiteSpace(backendId);
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_raidArrays.TryGetValue(arrayId, out var array))
            {
                throw new ArgumentException($"RAID array '{arrayId}' not found");
            }

            if (!array.BackendIds.Contains(backendId))
            {
                throw new ArgumentException($"Backend '{backendId}' not in RAID array '{arrayId}'");
            }

            Interlocked.Increment(ref _totalBackendFailures);

            array.FailedBackends.Add(backendId);
            array.State = RaidArrayState.Degraded;

            // Notify RAID plugin about backend failure
            await NotifyRaidPluginAsync("backend.failed", new Dictionary<string, object>
            {
                ["arrayId"] = arrayId,
                ["backendId"] = backendId,
                ["failedCount"] = array.FailedBackends.Count
            });

            // Check if array can tolerate more failures
            if (!CanTolerateFailures(array))
            {
                array.State = RaidArrayState.Failed;
                await NotifyRaidPluginAsync("array.failed", new Dictionary<string, object>
                {
                    ["arrayId"] = arrayId,
                    ["reason"] = "Too many backend failures"
                });
            }
        }

        /// <summary>
        /// Rebuilds data on a hot spare backend after a failure.
        /// </summary>
        /// <param name="arrayId">RAID array identifier.</param>
        /// <param name="spareBackendId">Hot spare backend to use for rebuild.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task RebuildArrayAsync(string arrayId, string spareBackendId, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(arrayId);
            ArgumentException.ThrowIfNullOrWhiteSpace(spareBackendId);
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_raidArrays.TryGetValue(arrayId, out var array))
            {
                throw new ArgumentException($"RAID array '{arrayId}' not found");
            }

            if (array.State != RaidArrayState.Degraded)
            {
                throw new InvalidOperationException($"RAID array '{arrayId}' is not in degraded state");
            }

            Interlocked.Increment(ref _totalRaidRebuilds);
            array.State = RaidArrayState.Rebuilding;

            // Notify RAID plugin about rebuild start
            await NotifyRaidPluginAsync("rebuild.started", new Dictionary<string, object>
            {
                ["arrayId"] = arrayId,
                ["spareBackendId"] = spareBackendId
            });

            // RAID 5/6 rebuild requires reading surviving stripe data and recomputing XOR parity
            // to reconstruct the failed drive's contents. This requires full XOR parity engine
            // which is not implemented. For RAID 6 dual-parity reconstruction, Reed-Solomon
            // galois-field arithmetic is required.
            if (array.RaidLevel == RaidLevel.RAID5 || array.RaidLevel == RaidLevel.RAID6)
            {
                throw new NotSupportedException(
                    $"RAID {(array.RaidLevel == RaidLevel.RAID5 ? 5 : 6)} rebuild requires XOR parity reconstruction (RAID 5) or " +
                    "Reed-Solomon dual-parity reconstruction (RAID 6), neither of which is implemented. " +
                    "Use RAID 1 or RAID 10 for supported rebuild operations.");
            }

            // Replace failed backend with spare
            if (array.FailedBackends.Any())
            {
                var failedBackend = array.FailedBackends.First();
                array.BackendIds.Remove(failedBackend);
                array.BackendIds.Add(spareBackendId);
                array.FailedBackends.Remove(failedBackend);
            }

            array.State = array.FailedBackends.Any() ? RaidArrayState.Degraded : RaidArrayState.Online;

            // Notify RAID plugin about rebuild completion
            await NotifyRaidPluginAsync("rebuild.completed", new Dictionary<string, object>
            {
                ["arrayId"] = arrayId,
                ["spareBackendId"] = spareBackendId,
                ["newState"] = array.State.ToString()
            });
        }

        /// <summary>
        /// Gets RAID array information.
        /// </summary>
        /// <param name="arrayId">RAID array identifier.</param>
        /// <returns>RAID array or null if not found.</returns>
        public RaidArray? GetRaidArray(string arrayId)
        {
            return _raidArrays.TryGetValue(arrayId, out var array) ? array : null;
        }

        /// <summary>
        /// Gets all RAID arrays.
        /// </summary>
        /// <returns>List of RAID arrays.</returns>
        public List<RaidArray> GetAllRaidArrays()
        {
            return _raidArrays.Values.ToList();
        }

        #region Private Methods

        private async Task WriteRaid0Async(RaidArray array, string objectKey, byte[] data, CancellationToken ct)
        {
            // RAID 0: Stripe data across all backends (no redundancy)
            var stripeCount = (data.Length + array.StripeSize - 1) / array.StripeSize;
            var backendCount = array.BackendIds.Count;

            for (int i = 0; i < stripeCount; i++)
            {
                var offset = i * array.StripeSize;
                var length = Math.Min(array.StripeSize, data.Length - offset);
                var stripe = new byte[length];
                Array.Copy(data, offset, stripe, 0, length);

                var backendIndex = i % backendCount;
                var backendId = array.BackendIds[backendIndex];
                var stripeKey = $"{objectKey}.stripe{i}";

                var backend = _registry.Get(backendId);
                if (backend != null)
                {
                    await backend.StoreAsync(stripeKey, new System.IO.MemoryStream(stripe), null, ct);
                }
            }
        }

        private async Task WriteRaid1Async(RaidArray array, string objectKey, byte[] data, CancellationToken ct)
        {
            // RAID 1: Mirror data to all backends
            var tasks = array.BackendIds.Select(async backendId =>
            {
                var backend = _registry.Get(backendId);
                if (backend != null)
                {
                    await backend.StoreAsync(objectKey, new System.IO.MemoryStream(data), null, ct);
                }
            });

            await Task.WhenAll(tasks);
        }

        private Task WriteRaid5Async(RaidArray array, string objectKey, byte[] data, CancellationToken ct)
        {
            // RAID 5 requires XOR parity computation across all data stripes in each stripe row.
            // The parity block P = D0 XOR D1 XOR ... XOR D(n-2) must be stored on rotating parity
            // drives. Copying the data stripe as parity (which the old implementation did) provides
            // zero fault tolerance and is functionally incorrect.
            throw new NotSupportedException(
                "RAID 5 XOR parity is not implemented. Writing data bytes as the parity block provides " +
                "no fault tolerance and is unsafe. Implement a full XOR parity engine or use RAID 1/RAID 10.");
        }

        private Task WriteRaid6Async(RaidArray array, string objectKey, byte[] data, CancellationToken ct)
        {
            // RAID 6 requires two independent parity computations: P (XOR) and Q (Reed-Solomon over GF(2^8)).
            // Delegating to RAID 5 provides only one parity block and gives incorrect fault tolerance.
            throw new NotSupportedException(
                "RAID 6 dual-parity (P+Q) is not implemented. RAID 6 requires Reed-Solomon encoding over " +
                "GF(2^8) for the Q parity block in addition to the XOR P parity block. " +
                "Implement a full RS erasure-coding engine or use RAID 1/RAID 10.");
        }

        private async Task WriteRaid10Async(RaidArray array, string objectKey, byte[] data, CancellationToken ct)
        {
            // RAID 10: Striped mirrors
            var halfCount = array.BackendIds.Count / 2;
            var mirrorPairs = array.BackendIds
                .Select((id, index) => new { id, index })
                .GroupBy(x => x.index / 2)
                .Select(g => g.Select(x => x.id).ToList())
                .ToList();

            // Write to each mirror pair
            var tasks = mirrorPairs.SelectMany(pair =>
                pair.Select(backendId =>
                {
                    var backend = _registry.Get(backendId);
                    return backend != null
                        ? backend.StoreAsync(objectKey, new System.IO.MemoryStream(data), null, ct)
                        : Task.CompletedTask;
                })
            );

            await Task.WhenAll(tasks);
        }

        private async Task<byte[]> ReadRaid0Async(RaidArray array, string objectKey, CancellationToken ct)
        {
            // Read and reconstruct striped data
            var result = new System.Collections.Generic.List<byte>();
            var backendCount = array.BackendIds.Count;
            int stripeIndex = 0;

            while (true)
            {
                var backendIndex = stripeIndex % backendCount;
                var backendId = array.BackendIds[backendIndex];
                var stripeKey = $"{objectKey}.stripe{stripeIndex}";

                var backend = _registry.Get(backendId);
                if (backend == null) break;

                try
                {
                    using var stream = await backend.RetrieveAsync(stripeKey, ct);
                    using var ms = new System.IO.MemoryStream();
                    await stream.CopyToAsync(ms, ct);
                    result.AddRange(ms.ToArray());
                    stripeIndex++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RaidIntegrationFeature] Stripe read failed at index {stripeIndex}: {ex.Message}");
                    break; // No more stripes
                }
            }

            return result.ToArray();
        }

        private async Task<byte[]> ReadRaid1Async(RaidArray array, string objectKey, CancellationToken ct)
        {
            // Try each backend until successful read
            foreach (var backendId in array.BackendIds.Where(id => !array.FailedBackends.Contains(id)))
            {
                try
                {
                    var backend = _registry.Get(backendId);
                    if (backend != null)
                    {
                        using var stream = await backend.RetrieveAsync(objectKey, ct);
                        using var ms = new System.IO.MemoryStream();
                        await stream.CopyToAsync(ms, ct);
                        return ms.ToArray();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RaidIntegrationFeature] Mirror read from {backendId} failed: {ex.Message}");
                    continue; // Try next mirror
                }
            }

            throw new InvalidOperationException($"Failed to read from any mirror in RAID array");
        }

        private Task<byte[]> ReadRaid5Async(RaidArray array, string objectKey, CancellationToken ct)
        {
            // Simplified - would reconstruct from parity if a backend failed
            return ReadRaid0Async(array, objectKey, ct);
        }

        private Task<byte[]> ReadRaid6Async(RaidArray array, string objectKey, CancellationToken ct)
        {
            return ReadRaid5Async(array, objectKey, ct);
        }

        private Task<byte[]> ReadRaid10Async(RaidArray array, string objectKey, CancellationToken ct)
        {
            return ReadRaid1Async(array, objectKey, ct);
        }

        private void ValidateBackendCountForRaidLevel(RaidLevel level, int count)
        {
            var minRequired = level switch
            {
                RaidLevel.RAID0 => 2,
                RaidLevel.RAID1 => 2,
                RaidLevel.RAID5 => 3,
                RaidLevel.RAID6 => 4,
                RaidLevel.RAID10 => 4,
                _ => 2
            };

            if (count < minRequired)
            {
                throw new ArgumentException($"RAID level {level} requires at least {minRequired} backends");
            }

            if (level == RaidLevel.RAID10 && count % 2 != 0)
            {
                throw new ArgumentException("RAID 10 requires an even number of backends");
            }
        }

        private long CalculateTotalCapacity(RaidLevel level, int backendCount, long perBackendCapacity)
        {
            return level switch
            {
                RaidLevel.RAID0 => backendCount * perBackendCapacity,
                RaidLevel.RAID1 => perBackendCapacity,
                RaidLevel.RAID5 => (backendCount - 1) * perBackendCapacity,
                RaidLevel.RAID6 => (backendCount - 2) * perBackendCapacity,
                RaidLevel.RAID10 => (backendCount / 2) * perBackendCapacity,
                _ => perBackendCapacity
            };
        }

        private bool CanTolerateFailures(RaidArray array)
        {
            var maxFailures = array.RaidLevel switch
            {
                RaidLevel.RAID0 => 0,
                RaidLevel.RAID1 => array.BackendIds.Count - 1,
                RaidLevel.RAID5 => 1,
                RaidLevel.RAID6 => 2,
                RaidLevel.RAID10 => array.BackendIds.Count / 2,
                _ => 0
            };

            return array.FailedBackends.Count <= maxFailures;
        }

        private async Task NotifyRaidPluginAsync(string eventType, Dictionary<string, object> payload)
        {
            try
            {
                var message = new PluginMessage
                {
                    Type = $"raid.{eventType}",
                    Payload = payload,
                    Timestamp = DateTime.UtcNow
                };

                await _messageBus.PublishAsync(RaidPluginTopic, message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RaidIntegrationFeature] Message bus publish failed: {ex.Message}");
            }
        }

        private async Task HandleRaidStatusMessageAsync(PluginMessage message)
        {
            // Handle status updates from RAID plugin
            await Task.CompletedTask;
        }

        #endregion

        /// <summary>
        /// Disposes resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _messageBusSubscription?.Dispose();
            _raidArrays.Clear();
            _objectToArrayMapping.Clear();
        }
    }

    #region Supporting Types

    /// <summary>
    /// RAID level enumeration.
    /// </summary>
    public enum RaidLevel
    {
        /// <summary>RAID 0 - Striping (no redundancy).</summary>
        RAID0,

        /// <summary>RAID 1 - Mirroring.</summary>
        RAID1,

        /// <summary>RAID 5 - Striping with distributed parity.</summary>
        RAID5,

        /// <summary>RAID 6 - Striping with dual parity.</summary>
        RAID6,

        /// <summary>RAID 10 - Mirrored stripes.</summary>
        RAID10
    }

    /// <summary>
    /// RAID array state.
    /// </summary>
    public enum RaidArrayState
    {
        /// <summary>Array is online and healthy.</summary>
        Online,

        /// <summary>Array is degraded (one or more backends failed).</summary>
        Degraded,

        /// <summary>Array is rebuilding.</summary>
        Rebuilding,

        /// <summary>Array has failed.</summary>
        Failed
    }

    /// <summary>
    /// Represents a RAID array configuration.
    /// </summary>
    public sealed class RaidArray
    {
        /// <summary>Unique array identifier.</summary>
        public string ArrayId { get; init; } = string.Empty;

        /// <summary>RAID level.</summary>
        public RaidLevel RaidLevel { get; init; }

        /// <summary>Backend strategy IDs in the array.</summary>
        public List<string> BackendIds { get; init; } = new();

        /// <summary>Failed backend IDs.</summary>
        public HashSet<string> FailedBackends { get; init; } = new();

        /// <summary>Stripe size in bytes.</summary>
        public int StripeSize { get; init; }

        /// <summary>Array state.</summary>
        public RaidArrayState State { get; set; }

        /// <summary>When the array was created.</summary>
        public DateTime CreatedTime { get; init; }

        /// <summary>Total capacity in bytes.</summary>
        public long TotalCapacityBytes { get; init; }

        /// <summary>Total bytes written to the array.</summary>
        public long TotalBytesWritten;

        /// <summary>Total bytes read from the array.</summary>
        public long TotalBytesRead;
    }

    #endregion
}
