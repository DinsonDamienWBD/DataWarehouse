using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CrossCutting
{
    /// <summary>
    /// Performs parallel health checks across all registered connection strategies and their
    /// active connection handles. Returns an aggregated health report suitable for dashboard
    /// integration and alerting systems.
    /// </summary>
    /// <remarks>
    /// Health checks are executed concurrently with configurable parallelism limits to avoid
    /// overwhelming target endpoints. Each check is individually timed and failures are
    /// captured without aborting the overall sweep.
    /// </remarks>
    public sealed class BulkConnectionTester
    {
        private readonly ConcurrentDictionary<string, StrategyRegistration> _registrations = new();
        private readonly ILogger? _logger;
        private readonly int _maxParallelism;

        /// <summary>
        /// Initializes a new instance of <see cref="BulkConnectionTester"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        /// <param name="maxParallelism">Maximum number of concurrent health checks. Defaults to 10.</param>
        public BulkConnectionTester(ILogger? logger = null, int maxParallelism = 10)
        {
            _logger = logger;
            _maxParallelism = maxParallelism;
        }

        /// <summary>
        /// Registers a strategy and its active connection handles for bulk health checking.
        /// </summary>
        /// <param name="strategy">The connection strategy.</param>
        /// <param name="handles">Active connection handles for this strategy.</param>
        public void Register(IConnectionStrategy strategy, IEnumerable<IConnectionHandle> handles)
        {
            ArgumentNullException.ThrowIfNull(strategy);
            ArgumentNullException.ThrowIfNull(handles);

            _registrations[strategy.StrategyId] = new StrategyRegistration(strategy, new List<IConnectionHandle>(handles));
        }

        /// <summary>
        /// Adds a connection handle to an already-registered strategy.
        /// </summary>
        /// <param name="strategyId">Strategy identifier.</param>
        /// <param name="handle">Connection handle to add.</param>
        public void AddHandle(string strategyId, IConnectionHandle handle)
        {
            if (_registrations.TryGetValue(strategyId, out var registration))
            {
                lock (registration.Handles)
                {
                    registration.Handles.Add(handle);
                }
            }
        }

        /// <summary>
        /// Removes a connection handle from the registry.
        /// </summary>
        /// <param name="strategyId">Strategy identifier.</param>
        /// <param name="connectionId">Connection ID to remove.</param>
        public void RemoveHandle(string strategyId, string connectionId)
        {
            if (_registrations.TryGetValue(strategyId, out var registration))
            {
                lock (registration.Handles)
                {
                    registration.Handles.RemoveAll(h => h.ConnectionId == connectionId);
                }
            }
        }

        /// <summary>
        /// Executes parallel health checks across all registered strategies and connections.
        /// </summary>
        /// <param name="timeout">Per-check timeout. Defaults to 10 seconds.</param>
        /// <param name="ct">Cancellation token for the entire operation.</param>
        /// <returns>Aggregated health report across all strategies.</returns>
        public async Task<BulkHealthReport> RunHealthChecksAsync(
            TimeSpan? timeout = null, CancellationToken ct = default)
        {
            var checkTimeout = timeout ?? TimeSpan.FromSeconds(10);
            var overallSw = Stopwatch.StartNew();
            var results = new ConcurrentBag<ConnectionCheckResult>();
            var semaphore = new SemaphoreSlim(_maxParallelism, _maxParallelism);

            var tasks = new List<Task>();

            foreach (var (strategyId, registration) in _registrations)
            {
                List<IConnectionHandle> handlesCopy;
                lock (registration.Handles)
                {
                    handlesCopy = new List<IConnectionHandle>(registration.Handles);
                }

                foreach (var handle in handlesCopy)
                {
                    tasks.Add(CheckSingleConnectionAsync(
                        registration.Strategy, handle, checkTimeout, semaphore, results, ct));
                }
            }

            await Task.WhenAll(tasks);
            overallSw.Stop();
            semaphore.Dispose();

            var resultList = results.ToList();

            return new BulkHealthReport(
                TotalChecked: resultList.Count,
                HealthyCount: resultList.Count(r => r.IsHealthy),
                UnhealthyCount: resultList.Count(r => !r.IsHealthy),
                TotalDuration: overallSw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Results: resultList);
        }

        /// <summary>
        /// Checks a single connection with timeout and concurrency control.
        /// </summary>
        private async Task CheckSingleConnectionAsync(
            IConnectionStrategy strategy,
            IConnectionHandle handle,
            TimeSpan timeout,
            SemaphoreSlim semaphore,
            ConcurrentBag<ConnectionCheckResult> results,
            CancellationToken ct)
        {
            await semaphore.WaitAsync(ct);
            var sw = Stopwatch.StartNew();

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);

                var health = await strategy.GetHealthAsync(handle, cts.Token);
                sw.Stop();

                results.Add(new ConnectionCheckResult(
                    StrategyId: strategy.StrategyId,
                    ConnectionId: handle.ConnectionId,
                    IsHealthy: health.IsHealthy,
                    StatusMessage: health.StatusMessage,
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow,
                    Error: null));

                _logger?.LogDebug(
                    "Health check for {StrategyId}/{ConnectionId}: {Status} in {Duration}ms",
                    strategy.StrategyId, handle.ConnectionId,
                    health.IsHealthy ? "healthy" : "unhealthy",
                    sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                sw.Stop();
                results.Add(new ConnectionCheckResult(
                    StrategyId: strategy.StrategyId,
                    ConnectionId: handle.ConnectionId,
                    IsHealthy: false,
                    StatusMessage: "Health check timed out",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow,
                    Error: "Timeout"));

                _logger?.LogWarning(
                    "Health check timed out for {StrategyId}/{ConnectionId} after {Duration}ms",
                    strategy.StrategyId, handle.ConnectionId, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                results.Add(new ConnectionCheckResult(
                    StrategyId: strategy.StrategyId,
                    ConnectionId: handle.ConnectionId,
                    IsHealthy: false,
                    StatusMessage: $"Health check failed: {ex.Message}",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow,
                    Error: ex.Message));

                _logger?.LogWarning(ex,
                    "Health check failed for {StrategyId}/{ConnectionId}",
                    strategy.StrategyId, handle.ConnectionId);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Internal registration pairing a strategy with its active handles.
        /// </summary>
        private sealed record StrategyRegistration(IConnectionStrategy Strategy, List<IConnectionHandle> Handles);
    }

    /// <summary>
    /// Aggregated health report from a bulk health check sweep.
    /// </summary>
    /// <param name="TotalChecked">Total number of connections checked.</param>
    /// <param name="HealthyCount">Number of healthy connections.</param>
    /// <param name="UnhealthyCount">Number of unhealthy connections.</param>
    /// <param name="TotalDuration">Total wall-clock time for the entire sweep.</param>
    /// <param name="CheckedAt">When the sweep was performed.</param>
    /// <param name="Results">Individual check results.</param>
    public sealed record BulkHealthReport(
        int TotalChecked,
        int HealthyCount,
        int UnhealthyCount,
        TimeSpan TotalDuration,
        DateTimeOffset CheckedAt,
        IReadOnlyList<ConnectionCheckResult> Results)
    {
        /// <summary>
        /// Overall health percentage (0-100).
        /// </summary>
        public double HealthPercentage => TotalChecked > 0 ? (double)HealthyCount / TotalChecked * 100.0 : 100.0;
    }

    /// <summary>
    /// Result of a single connection health check.
    /// </summary>
    /// <param name="StrategyId">Strategy that owns the connection.</param>
    /// <param name="ConnectionId">Connection identifier.</param>
    /// <param name="IsHealthy">Whether the connection is healthy.</param>
    /// <param name="StatusMessage">Human-readable status.</param>
    /// <param name="Latency">Time taken for the health check.</param>
    /// <param name="CheckedAt">When this check was performed.</param>
    /// <param name="Error">Error message if the check failed.</param>
    public sealed record ConnectionCheckResult(
        string StrategyId,
        string ConnectionId,
        bool IsHealthy,
        string StatusMessage,
        TimeSpan Latency,
        DateTimeOffset CheckedAt,
        string? Error);
}
