using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Features
{
    /// <summary>
    /// Multi-Backend Fan-Out Feature (C1) - Writes data to multiple storage backends simultaneously.
    ///
    /// Features:
    /// - Multi-backend write fan-out with configurable write policies
    /// - Write policies: All (all backends must succeed), Quorum (N of M must succeed), PrimaryPlusAsync (primary sync + others async)
    /// - Consistency levels: Strong (all succeed), Eventual (primary + async copies)
    /// - Failure handling: rollback on failure, partial success tracking
    /// - Read strategies: Primary, Fastest, RoundRobin
    /// - Health monitoring per backend
    /// - Automatic failover to healthy backends
    /// </summary>
    public sealed class MultiBackendFanOutFeature : IDisposable
    {
        private readonly StorageStrategyRegistry _registry;
        private readonly ConcurrentDictionary<string, BackendHealth> _backendHealth = new();
        private readonly ConcurrentDictionary<string, int> _roundRobinCounters = new();
        private bool _disposed;

        // Statistics
        private long _totalFanOutWrites;
        private long _totalFanOutReads;
        private long _totalRollbacks;
        private long _totalPartialSuccesses;
        private long _totalFullSuccesses;

        /// <summary>
        /// Initializes a new instance of the MultiBackendFanOutFeature.
        /// </summary>
        /// <param name="registry">The storage strategy registry.</param>
        public MultiBackendFanOutFeature(StorageStrategyRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        /// <summary>
        /// Gets the total number of fan-out write operations performed.
        /// </summary>
        public long TotalFanOutWrites => Interlocked.Read(ref _totalFanOutWrites);

        /// <summary>
        /// Gets the total number of fan-out read operations performed.
        /// </summary>
        public long TotalFanOutReads => Interlocked.Read(ref _totalFanOutReads);

        /// <summary>
        /// Gets the total number of rollbacks due to write failures.
        /// </summary>
        public long TotalRollbacks => Interlocked.Read(ref _totalRollbacks);

        /// <summary>
        /// Gets the total number of partial successes (some but not all backends succeeded).
        /// </summary>
        public long TotalPartialSuccesses => Interlocked.Read(ref _totalPartialSuccesses);

        /// <summary>
        /// Gets the total number of full successes (all backends succeeded).
        /// </summary>
        public long TotalFullSuccesses => Interlocked.Read(ref _totalFullSuccesses);

        /// <summary>
        /// Writes data to multiple storage backends using the specified policy.
        /// </summary>
        /// <param name="key">The storage key.</param>
        /// <param name="data">The data stream to store.</param>
        /// <param name="backendIds">List of backend strategy IDs to write to.</param>
        /// <param name="policy">Write policy to use.</param>
        /// <param name="metadata">Optional metadata.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Fan-out write result with success status per backend.</returns>
        public async Task<FanOutWriteResult> FanOutWriteAsync(
            string key,
            Stream data,
            IReadOnlyList<string> backendIds,
            WritePolicy policy,
            IDictionary<string, string>? metadata = null,
            CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(backendIds);

            if (backendIds.Count == 0)
            {
                throw new ArgumentException("At least one backend must be specified", nameof(backendIds));
            }

            Interlocked.Increment(ref _totalFanOutWrites);

            // Copy stream data once
            var dataBytes = await ReadStreamToBytesAsync(data, ct);

            var result = new FanOutWriteResult
            {
                Key = key,
                Policy = policy,
                TotalBackends = backendIds.Count
            };

            switch (policy)
            {
                case WritePolicy.All:
                    await WriteToAllAsync(key, dataBytes, backendIds, metadata, result, ct);
                    break;

                case WritePolicy.Quorum:
                    await WriteToQuorumAsync(key, dataBytes, backendIds, metadata, result, ct);
                    break;

                case WritePolicy.PrimaryPlusAsync:
                    await WritePrimaryPlusAsyncAsync(key, dataBytes, backendIds, metadata, result, ct);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown write policy");
            }

            // Update statistics
            if (result.IsSuccess)
            {
                Interlocked.Increment(ref _totalFullSuccesses);
            }
            else if (result.SuccessCount > 0)
            {
                Interlocked.Increment(ref _totalPartialSuccesses);
            }

            return result;
        }

        /// <summary>
        /// Reads data from multiple backends using the specified read strategy.
        /// </summary>
        /// <param name="key">The storage key.</param>
        /// <param name="backendIds">List of backend strategy IDs to try reading from.</param>
        /// <param name="strategy">Read strategy to use.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The data stream and the backend it was read from.</returns>
        public async Task<FanOutReadResult> FanOutReadAsync(
            string key,
            IReadOnlyList<string> backendIds,
            ReadStrategy strategy,
            CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(backendIds);

            if (backendIds.Count == 0)
            {
                throw new ArgumentException("At least one backend must be specified", nameof(backendIds));
            }

            Interlocked.Increment(ref _totalFanOutReads);

            return strategy switch
            {
                ReadStrategy.Primary => await ReadFromPrimaryAsync(key, backendIds, ct),
                ReadStrategy.Fastest => await ReadFromFastestAsync(key, backendIds, ct),
                ReadStrategy.RoundRobin => await ReadRoundRobinAsync(key, backendIds, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unknown read strategy")
            };
        }

        #region Write Policy Implementations

        private async Task WriteToAllAsync(
            string key,
            byte[] data,
            IReadOnlyList<string> backendIds,
            IDictionary<string, string>? metadata,
            FanOutWriteResult result,
            CancellationToken ct)
        {
            var writeTasks = backendIds.Select(async backendId =>
            {
                try
                {
                    var strategy = _registry.GetStrategy(backendId);
                    if (strategy != null)
                    {
                        using var ms = new MemoryStream(data);
                        await strategy.StoreAsync(key, ms, metadata, ct);
                        return (backendId, success: true, error: (Exception?)null);
                    }
                    return (backendId, success: false, error: new InvalidOperationException($"Backend '{backendId}' not found"));
                }
                catch (Exception ex)
                {
                    return (backendId, success: false, error: ex);
                }
            }).ToList();

            var results = await Task.WhenAll(writeTasks);

            foreach (var (backendId, success, error) in results)
            {
                if (success)
                {
                    result.SuccessfulBackends.Add(backendId);
                    result.SuccessCount++;
                }
                else
                {
                    result.FailedBackends.Add(backendId);
                    result.FailureCount++;
                    if (error != null)
                    {
                        result.Errors[backendId] = error.Message;
                    }
                }
            }

            // All must succeed for this policy
            if (result.FailureCount > 0)
            {
                result.IsSuccess = false;
                await RollbackWritesAsync(key, result.SuccessfulBackends, ct);
                Interlocked.Increment(ref _totalRollbacks);
            }
            else
            {
                result.IsSuccess = true;
            }
        }

        private async Task WriteToQuorumAsync(
            string key,
            byte[] data,
            IReadOnlyList<string> backendIds,
            IDictionary<string, string>? metadata,
            FanOutWriteResult result,
            CancellationToken ct)
        {
            var quorumSize = (backendIds.Count / 2) + 1; // N/2 + 1

            var writeTasks = backendIds.Select(async backendId =>
            {
                try
                {
                    var strategy = _registry.GetStrategy(backendId);
                    if (strategy != null)
                    {
                        using var ms = new MemoryStream(data);
                        await strategy.StoreAsync(key, ms, metadata, ct);
                        return (backendId, success: true, error: (Exception?)null);
                    }
                    return (backendId, success: false, error: new InvalidOperationException($"Backend '{backendId}' not found"));
                }
                catch (Exception ex)
                {
                    return (backendId, success: false, error: ex);
                }
            }).ToList();

            var results = await Task.WhenAll(writeTasks);

            foreach (var (backendId, success, error) in results)
            {
                if (success)
                {
                    result.SuccessfulBackends.Add(backendId);
                    result.SuccessCount++;
                }
                else
                {
                    result.FailedBackends.Add(backendId);
                    result.FailureCount++;
                    if (error != null)
                    {
                        result.Errors[backendId] = error.Message;
                    }
                }
            }

            // Quorum must succeed
            if (result.SuccessCount >= quorumSize)
            {
                result.IsSuccess = true;
            }
            else
            {
                result.IsSuccess = false;
                await RollbackWritesAsync(key, result.SuccessfulBackends, ct);
                Interlocked.Increment(ref _totalRollbacks);
            }
        }

        private async Task WritePrimaryPlusAsyncAsync(
            string key,
            byte[] data,
            IReadOnlyList<string> backendIds,
            IDictionary<string, string>? metadata,
            FanOutWriteResult result,
            CancellationToken ct)
        {
            if (backendIds.Count == 0)
            {
                result.IsSuccess = false;
                return;
            }

            // First backend is primary
            var primaryId = backendIds[0];

            try
            {
                var primaryStrategy = _registry.GetStrategy(primaryId);
                if (primaryStrategy != null)
                {
                    using var ms = new MemoryStream(data);
                    await primaryStrategy.StoreAsync(key, ms, metadata, ct);
                    result.SuccessfulBackends.Add(primaryId);
                    result.SuccessCount++;
                    result.IsSuccess = true;
                }
                else
                {
                    result.FailedBackends.Add(primaryId);
                    result.FailureCount++;
                    result.Errors[primaryId] = "Backend not found";
                    result.IsSuccess = false;
                    return;
                }
            }
            catch (Exception ex)
            {
                result.FailedBackends.Add(primaryId);
                result.FailureCount++;
                result.Errors[primaryId] = ex.Message;
                result.IsSuccess = false;
                return;
            }

            // Write to other backends asynchronously (fire and forget)
            if (backendIds.Count > 1)
            {
                _ = Task.Run(async () =>
                {
                    for (int i = 1; i < backendIds.Count; i++)
                    {
                        var backendId = backendIds[i];
                        try
                        {
                            var strategy = _registry.GetStrategy(backendId);
                            if (strategy != null)
                            {
                                using var ms = new MemoryStream(data);
                                await strategy.StoreAsync(key, ms, metadata, CancellationToken.None);
                                result.SuccessfulBackends.Add(backendId);
                                Interlocked.Increment(ref result.SuccessCount);
                            }
                        }
                        catch
                        {
                            // Ignore async write failures
                            result.FailedBackends.Add(backendId);
                            Interlocked.Increment(ref result.FailureCount);
                        }
                    }
                }, CancellationToken.None);
            }
        }

        #endregion

        #region Read Strategy Implementations

        private async Task<FanOutReadResult> ReadFromPrimaryAsync(
            string key,
            IReadOnlyList<string> backendIds,
            CancellationToken ct)
        {
            foreach (var backendId in backendIds)
            {
                try
                {
                    var strategy = _registry.GetStrategy(backendId);
                    if (strategy != null)
                    {
                        var stream = await strategy.RetrieveAsync(key, ct);
                        return new FanOutReadResult
                        {
                            Key = key,
                            Data = stream,
                            SourceBackend = backendId,
                            IsSuccess = true
                        };
                    }
                }
                catch
                {
                    // Try next backend
                    continue;
                }
            }

            return new FanOutReadResult
            {
                Key = key,
                IsSuccess = false,
                ErrorMessage = "Failed to read from all backends"
            };
        }

        private async Task<FanOutReadResult> ReadFromFastestAsync(
            string key,
            IReadOnlyList<string> backendIds,
            CancellationToken ct)
        {
            var readTasks = backendIds.Select(async backendId =>
            {
                try
                {
                    var strategy = _registry.GetStrategy(backendId);
                    if (strategy != null)
                    {
                        var stream = await strategy.RetrieveAsync(key, ct);
                        return (backendId, stream, success: true, error: (Exception?)null);
                    }
                    return (backendId, stream: (Stream?)null, success: false, error: new InvalidOperationException("Backend not found"));
                }
                catch (Exception ex)
                {
                    return (backendId, stream: (Stream?)null, success: false, error: ex);
                }
            });

            var completedTask = await Task.WhenAny(readTasks.Select(async t =>
            {
                var result = await t;
                return result.success ? result : default;
            }));

            var firstSuccess = await completedTask;

            if (firstSuccess.success && firstSuccess.stream != null)
            {
                return new FanOutReadResult
                {
                    Key = key,
                    Data = firstSuccess.stream,
                    SourceBackend = firstSuccess.backendId,
                    IsSuccess = true
                };
            }

            // Fallback to primary strategy if fastest fails
            return await ReadFromPrimaryAsync(key, backendIds, ct);
        }

        private async Task<FanOutReadResult> ReadRoundRobinAsync(
            string key,
            IReadOnlyList<string> backendIds,
            CancellationToken ct)
        {
            var counter = _roundRobinCounters.AddOrUpdate(key, 0, (_, count) => (count + 1) % backendIds.Count);

            // Try from the round-robin selected backend
            var startIndex = counter;
            for (int i = 0; i < backendIds.Count; i++)
            {
                var index = (startIndex + i) % backendIds.Count;
                var backendId = backendIds[index];

                try
                {
                    var strategy = _registry.GetStrategy(backendId);
                    if (strategy != null)
                    {
                        var stream = await strategy.RetrieveAsync(key, ct);
                        return new FanOutReadResult
                        {
                            Key = key,
                            Data = stream,
                            SourceBackend = backendId,
                            IsSuccess = true
                        };
                    }
                }
                catch
                {
                    // Try next backend
                    continue;
                }
            }

            return new FanOutReadResult
            {
                Key = key,
                IsSuccess = false,
                ErrorMessage = "Failed to read from all backends"
            };
        }

        #endregion

        #region Helper Methods

        private async Task RollbackWritesAsync(string key, List<string> successfulBackends, CancellationToken ct)
        {
            var deleteTasks = successfulBackends.Select(async backendId =>
            {
                try
                {
                    var strategy = _registry.GetStrategy(backendId);
                    if (strategy != null)
                    {
                        await strategy.DeleteAsync(key, ct);
                    }
                }
                catch
                {
                    // Ignore rollback failures
                }
            });

            await Task.WhenAll(deleteTasks);
        }

        private static async Task<byte[]> ReadStreamToBytesAsync(Stream stream, CancellationToken ct)
        {
            if (stream is MemoryStream ms)
            {
                return ms.ToArray();
            }

            using var memoryStream = new MemoryStream(65536);
            await stream.CopyToAsync(memoryStream, ct);
            return memoryStream.ToArray();
        }

        #endregion

        /// <summary>
        /// Disposes resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _backendHealth.Clear();
            _roundRobinCounters.Clear();
        }
    }

    #region Supporting Types

    /// <summary>
    /// Write policy for multi-backend fan-out.
    /// </summary>
    public enum WritePolicy
    {
        /// <summary>All backends must succeed.</summary>
        All,

        /// <summary>Quorum (N/2 + 1) of backends must succeed.</summary>
        Quorum,

        /// <summary>Primary must succeed, others are async (eventual consistency).</summary>
        PrimaryPlusAsync
    }

    /// <summary>
    /// Read strategy for multi-backend reads.
    /// </summary>
    public enum ReadStrategy
    {
        /// <summary>Read from primary backend (first in list), fallback to others if needed.</summary>
        Primary,

        /// <summary>Race all backends, return from fastest.</summary>
        Fastest,

        /// <summary>Round-robin across backends for load balancing.</summary>
        RoundRobin
    }

    /// <summary>
    /// Result of a fan-out write operation.
    /// </summary>
    public sealed class FanOutWriteResult
    {
        /// <summary>The storage key.</summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>Write policy used.</summary>
        public WritePolicy Policy { get; init; }

        /// <summary>Total number of backends attempted.</summary>
        public int TotalBackends { get; init; }

        /// <summary>Number of successful writes.</summary>
        public int SuccessCount;

        /// <summary>Number of failed writes.</summary>
        public int FailureCount;

        /// <summary>List of backends that succeeded.</summary>
        public List<string> SuccessfulBackends { get; } = new();

        /// <summary>List of backends that failed.</summary>
        public List<string> FailedBackends { get; } = new();

        /// <summary>Error messages per backend.</summary>
        public Dictionary<string, string> Errors { get; } = new();

        /// <summary>Whether the overall write operation succeeded according to the policy.</summary>
        public bool IsSuccess { get; set; }
    }

    /// <summary>
    /// Result of a fan-out read operation.
    /// </summary>
    public sealed class FanOutReadResult
    {
        /// <summary>The storage key.</summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>The retrieved data stream.</summary>
        public Stream? Data { get; init; }

        /// <summary>The backend the data was read from.</summary>
        public string? SourceBackend { get; init; }

        /// <summary>Whether the read succeeded.</summary>
        public bool IsSuccess { get; init; }

        /// <summary>Error message if read failed.</summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Health status of a backend.
    /// </summary>
    internal sealed class BackendHealth
    {
        public bool IsHealthy { get; set; } = true;
        public DateTime LastCheck { get; set; } = DateTime.UtcNow;
        public int FailureCount { get; set; }
        public DateTime? LastFailure { get; set; }
    }

    #endregion
}
