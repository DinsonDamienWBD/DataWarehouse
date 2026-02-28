using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CrossCutting
{
    /// <summary>
    /// Wraps an <see cref="IConnectionHandle"/> with automatic reconnection logic using
    /// exponential backoff with jitter. Detects disconnection through periodic probes or
    /// exception notification, and transparently re-establishes the connection.
    /// </summary>
    /// <remarks>
    /// The handler monitors the wrapped connection handle and, upon detecting disconnection,
    /// initiates a reconnection sequence with exponential backoff (base delay doubled each
    /// attempt) plus random jitter to prevent thundering herd scenarios. The maximum number
    /// of reconnection attempts and maximum delay are configurable.
    /// </remarks>
    public sealed class AutoReconnectionHandler : IAsyncDisposable
    {
        private readonly IConnectionStrategy _strategy;
        private readonly ILogger? _logger;
        private readonly ReconnectionConfiguration _config;
        private readonly object _reconnectLock = new();
        private IConnectionHandle? _currentHandle;
        private ConnectionConfig? _connectionConfig;
        private volatile bool _isReconnecting;
        private volatile bool _disposed;
        private int _reconnectAttempts;
        private CancellationTokenSource? _monitorCts;

        /// <summary>
        /// Fired when a reconnection attempt begins.
        /// </summary>
        public event Func<ReconnectionEventArgs, Task>? OnReconnecting;

        /// <summary>
        /// Fired when reconnection succeeds.
        /// </summary>
        public event Func<ReconnectionEventArgs, Task>? OnReconnected;

        /// <summary>
        /// Fired when all reconnection attempts are exhausted.
        /// </summary>
        public event Func<ReconnectionEventArgs, Task>? OnReconnectionFailed;

        /// <summary>
        /// Initializes a new instance of <see cref="AutoReconnectionHandler"/>.
        /// </summary>
        /// <param name="strategy">Strategy used to establish connections.</param>
        /// <param name="config">Reconnection configuration.</param>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public AutoReconnectionHandler(
            IConnectionStrategy strategy,
            ReconnectionConfiguration? config = null,
            ILogger? logger = null)
        {
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
            _config = config ?? new ReconnectionConfiguration();
            _logger = logger;
        }

        /// <summary>
        /// Gets the current connection handle, which may change after reconnection.
        /// </summary>
        public IConnectionHandle? CurrentHandle => _currentHandle;

        /// <summary>
        /// Whether a reconnection attempt is currently in progress.
        /// </summary>
        public bool IsReconnecting => _isReconnecting;

        /// <summary>
        /// Total number of reconnection attempts made during this handler's lifetime.
        /// </summary>
        public int TotalReconnectAttempts => _reconnectAttempts;

        /// <summary>
        /// Wraps an existing connection handle and begins monitoring it for disconnection.
        /// </summary>
        /// <param name="handle">The initial connection handle.</param>
        /// <param name="connectionConfig">Config used to re-establish the connection.</param>
        /// <param name="ct">Cancellation token.</param>
        public void Attach(IConnectionHandle handle, ConnectionConfig connectionConfig, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(handle);
            ArgumentNullException.ThrowIfNull(connectionConfig);

            lock (_reconnectLock)
            {
                _currentHandle = handle;
                _connectionConfig = connectionConfig;

                _monitorCts?.Cancel();
                _monitorCts?.Dispose();
                _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            }

            _ = MonitorConnectionAsync(_monitorCts.Token);
        }

        /// <summary>
        /// Notifies the handler that the current connection has been lost, triggering
        /// an immediate reconnection attempt.
        /// </summary>
        public Task NotifyDisconnectedAsync()
        {
            if (_disposed || _isReconnecting) return Task.CompletedTask;
            return ReconnectAsync(CancellationToken.None);
        }

        /// <summary>
        /// Monitors the connection and triggers reconnection when disconnection is detected.
        /// </summary>
        private async Task MonitorConnectionAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && !_disposed)
            {
                try
                {
                    await Task.Delay(_config.ProbeInterval, ct);

                    if (_currentHandle == null || !_currentHandle.IsConnected)
                    {
                        _logger?.LogWarning(
                            "Connection lost for strategy {StrategyId}, initiating reconnection",
                            _strategy.StrategyId);
                        await ReconnectAsync(ct);
                    }
                    else if (_connectionConfig != null)
                    {
                        var isAlive = await _strategy.TestConnectionAsync(_currentHandle, ct);
                        if (!isAlive)
                        {
                            _logger?.LogWarning(
                                "Connection test failed for strategy {StrategyId}, initiating reconnection",
                                _strategy.StrategyId);
                            await ReconnectAsync(ct);
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "Error during connection monitoring for strategy {StrategyId}",
                        _strategy.StrategyId);
                }
            }
        }

        /// <summary>
        /// Performs reconnection with exponential backoff and jitter.
        /// </summary>
        private async Task ReconnectAsync(CancellationToken ct)
        {
            if (_connectionConfig == null || _isReconnecting) return;

            lock (_reconnectLock)
            {
                if (_isReconnecting) return;
                _isReconnecting = true;
            }

            var random = Random.Shared;

            try
            {
                for (int attempt = 1; attempt <= _config.MaxAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref _reconnectAttempts);

                    var args = new ReconnectionEventArgs(
                        _strategy.StrategyId,
                        _currentHandle?.ConnectionId ?? "unknown",
                        attempt,
                        _config.MaxAttempts);

                    if (OnReconnecting != null)
                        await OnReconnecting(args);

                    _logger?.LogInformation(
                        "Reconnection attempt {Attempt}/{MaxAttempts} for strategy {StrategyId}",
                        attempt, _config.MaxAttempts, _strategy.StrategyId);

                    try
                    {
                        if (_currentHandle != null)
                        {
                            try { await _currentHandle.DisposeAsync(); }
                            catch { /* Ignore disposal errors on stale handle */ }
                        }

                        _currentHandle = await _strategy.ConnectAsync(_connectionConfig, ct);

                        _logger?.LogInformation(
                            "Reconnection successful for strategy {StrategyId} on attempt {Attempt}",
                            _strategy.StrategyId, attempt);

                        if (OnReconnected != null)
                            await OnReconnected(args);

                        return;
                    }
                    catch (Exception ex) when (attempt < _config.MaxAttempts)
                    {
                        var baseDelay = _config.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
                        var maxDelay = _config.MaxDelay.TotalMilliseconds;
                        var jitter = Random.Shared.NextDouble() * _config.JitterFactor * baseDelay;
                        var delay = Math.Min(baseDelay + jitter, maxDelay);

                        _logger?.LogWarning(ex,
                            "Reconnection attempt {Attempt} failed for strategy {StrategyId}. " +
                            "Retrying in {Delay}ms",
                            attempt, _strategy.StrategyId, (int)delay);

                        await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
                    }
                }

                _logger?.LogError(
                    "All {MaxAttempts} reconnection attempts exhausted for strategy {StrategyId}",
                    _config.MaxAttempts, _strategy.StrategyId);

                if (OnReconnectionFailed != null)
                {
                    await OnReconnectionFailed(new ReconnectionEventArgs(
                        _strategy.StrategyId,
                        _currentHandle?.ConnectionId ?? "unknown",
                        _config.MaxAttempts,
                        _config.MaxAttempts));
                }
            }
            finally
            {
                _isReconnecting = false;
            }
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            _monitorCts?.Cancel();
            // Allow monitor loop to observe cancellation before disposing handle
            await Task.Delay(50);
            _monitorCts?.Dispose();

            if (_currentHandle != null)
            {
                try { await _currentHandle.DisposeAsync(); }
                catch { /* Best-effort cleanup */ }
            }
        }
    }

    /// <summary>
    /// Configuration for automatic reconnection behavior.
    /// </summary>
    /// <param name="MaxAttempts">Maximum number of reconnection attempts before giving up.</param>
    /// <param name="BaseDelayValue">Initial delay before the first retry.</param>
    /// <param name="MaxDelayValue">Maximum delay cap for exponential backoff.</param>
    /// <param name="JitterFactor">Jitter factor (0.0 to 1.0) applied to the delay.</param>
    /// <param name="ProbeIntervalValue">Interval between connection liveness probes.</param>
    public sealed record ReconnectionConfiguration(
        int MaxAttempts = 10,
        TimeSpan? BaseDelayValue = null,
        TimeSpan? MaxDelayValue = null,
        double JitterFactor = 0.25,
        TimeSpan? ProbeIntervalValue = null)
    {
        /// <summary>Effective base delay, defaulting to 500ms.</summary>
        public TimeSpan BaseDelay { get; } = BaseDelayValue ?? TimeSpan.FromMilliseconds(500);
        /// <summary>Effective max delay, defaulting to 60 seconds.</summary>
        public TimeSpan MaxDelay { get; } = MaxDelayValue ?? TimeSpan.FromSeconds(60);
        /// <summary>Effective probe interval, defaulting to 15 seconds.</summary>
        public TimeSpan ProbeInterval { get; } = ProbeIntervalValue ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Event arguments for reconnection lifecycle events.
    /// </summary>
    /// <param name="StrategyId">Strategy being reconnected.</param>
    /// <param name="ConnectionId">Connection that was lost.</param>
    /// <param name="Attempt">Current attempt number.</param>
    /// <param name="MaxAttempts">Maximum configured attempts.</param>
    public sealed record ReconnectionEventArgs(
        string StrategyId,
        string ConnectionId,
        int Attempt,
        int MaxAttempts);
}
