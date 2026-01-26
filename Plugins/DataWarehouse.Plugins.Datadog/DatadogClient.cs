using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.Datadog
{
    /// <summary>
    /// HTTP client for interacting with Datadog APIs.
    /// Handles metrics and logs submission with batching and retries.
    /// </summary>
    public sealed class DatadogClient : IDisposable
    {
        private readonly DatadogConfiguration _config;
        private readonly HttpClient _httpClient;
        private readonly ConcurrentQueue<DatadogMetricPoint> _metricsQueue;
        private readonly ConcurrentQueue<DatadogLogEntry> _logsQueue;
        private readonly Timer _flushTimer;
        private readonly SemaphoreSlim _flushLock;
        private readonly JsonSerializerOptions _jsonOptions;
        private bool _disposed;
        private long _totalMetricsSent;
        private long _totalLogsSent;
        private long _totalErrors;

        /// <summary>
        /// Gets the total number of metrics sent.
        /// </summary>
        public long TotalMetricsSent => Interlocked.Read(ref _totalMetricsSent);

        /// <summary>
        /// Gets the total number of logs sent.
        /// </summary>
        public long TotalLogsSent => Interlocked.Read(ref _totalLogsSent);

        /// <summary>
        /// Gets the total number of errors encountered.
        /// </summary>
        public long TotalErrors => Interlocked.Read(ref _totalErrors);

        /// <summary>
        /// Gets the current metrics queue size.
        /// </summary>
        public int MetricsQueueSize => _metricsQueue.Count;

        /// <summary>
        /// Gets the current logs queue size.
        /// </summary>
        public int LogsQueueSize => _logsQueue.Count;

        /// <summary>
        /// Initializes a new instance of the <see cref="DatadogClient"/> class.
        /// </summary>
        /// <param name="config">The Datadog configuration.</param>
        public DatadogClient(DatadogConfiguration config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _config.Validate();

            _httpClient = new HttpClient
            {
                Timeout = _config.RequestTimeout
            };

            _httpClient.DefaultRequestHeaders.Add("DD-API-KEY", _config.ApiKey);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            _metricsQueue = new ConcurrentQueue<DatadogMetricPoint>();
            _logsQueue = new ConcurrentQueue<DatadogLogEntry>();
            _flushLock = new SemaphoreSlim(1, 1);

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = false
            };

            // Start periodic flush timer
            _flushTimer = new Timer(
                callback: async _ => await FlushAsync(),
                state: null,
                dueTime: _config.FlushInterval,
                period: _config.FlushInterval);
        }

        #region Metrics

        /// <summary>
        /// Enqueues a metric for submission.
        /// </summary>
        /// <param name="metric">The metric to enqueue.</param>
        public void EnqueueMetric(DatadogMetricPoint metric)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_metricsQueue.Count >= _config.MaxQueueSize)
            {
                // Drop oldest metric to prevent unbounded growth
                _metricsQueue.TryDequeue(out _);
                Interlocked.Increment(ref _totalErrors);
            }

            _metricsQueue.Enqueue(metric);
        }

        /// <summary>
        /// Submits metrics to Datadog.
        /// </summary>
        /// <param name="metrics">The metrics to submit.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if successful; otherwise, false.</returns>
        public async Task<bool> SubmitMetricsAsync(List<DatadogMetricPoint> metrics, CancellationToken ct = default)
        {
            if (metrics.Count == 0)
            {
                return true;
            }

            try
            {
                var request = new DatadogMetricsRequest
                {
                    Series = metrics
                };

                var json = JsonSerializer.Serialize(request, _jsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(_config.MetricsEndpoint, content, ct);

                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Add(ref _totalMetricsSent, metrics.Count);
                    return true;
                }
                else
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    Console.Error.WriteLine($"[Datadog] Failed to submit metrics: {response.StatusCode} - {errorBody}");
                    Interlocked.Increment(ref _totalErrors);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Datadog] Error submitting metrics: {ex.Message}");
                Interlocked.Increment(ref _totalErrors);
                return false;
            }
        }

        #endregion

        #region Logs

        /// <summary>
        /// Enqueues a log entry for submission.
        /// </summary>
        /// <param name="logEntry">The log entry to enqueue.</param>
        public void EnqueueLog(DatadogLogEntry logEntry)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_logsQueue.Count >= _config.MaxQueueSize)
            {
                // Drop oldest log to prevent unbounded growth
                _logsQueue.TryDequeue(out _);
                Interlocked.Increment(ref _totalErrors);
            }

            _logsQueue.Enqueue(logEntry);
        }

        /// <summary>
        /// Submits log entries to Datadog.
        /// </summary>
        /// <param name="logs">The log entries to submit.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if successful; otherwise, false.</returns>
        public async Task<bool> SubmitLogsAsync(List<DatadogLogEntry> logs, CancellationToken ct = default)
        {
            if (logs.Count == 0)
            {
                return true;
            }

            try
            {
                var json = JsonSerializer.Serialize(logs, _jsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(_config.LogsEndpoint, content, ct);

                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Add(ref _totalLogsSent, logs.Count);
                    return true;
                }
                else
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    Console.Error.WriteLine($"[Datadog] Failed to submit logs: {response.StatusCode} - {errorBody}");
                    Interlocked.Increment(ref _totalErrors);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Datadog] Error submitting logs: {ex.Message}");
                Interlocked.Increment(ref _totalErrors);
                return false;
            }
        }

        #endregion

        #region Flushing

        /// <summary>
        /// Flushes all queued metrics and logs to Datadog.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public async Task FlushAsync(CancellationToken ct = default)
        {
            if (_disposed)
            {
                return;
            }

            // Prevent concurrent flushes
            if (!await _flushLock.WaitAsync(0, ct))
            {
                return;
            }

            try
            {
                // Flush metrics
                await FlushMetricsAsync(ct);

                // Flush logs
                await FlushLogsAsync(ct);
            }
            finally
            {
                _flushLock.Release();
            }
        }

        private async Task FlushMetricsAsync(CancellationToken ct)
        {
            var batch = new List<DatadogMetricPoint>();

            while (_metricsQueue.TryDequeue(out var metric) && batch.Count < _config.MetricsBatchSize)
            {
                batch.Add(metric);
            }

            if (batch.Count > 0)
            {
                await SubmitMetricsAsync(batch, ct);
            }
        }

        private async Task FlushLogsAsync(CancellationToken ct)
        {
            var batch = new List<DatadogLogEntry>();

            while (_logsQueue.TryDequeue(out var log) && batch.Count < _config.LogsBatchSize)
            {
                batch.Add(log);
            }

            if (batch.Count > 0)
            {
                await SubmitLogsAsync(batch, ct);
            }
        }

        #endregion

        #region Dispose

        /// <summary>
        /// Disposes the client and flushes any pending data.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Stop the flush timer
            _flushTimer.Dispose();

            // Final flush
            try
            {
                FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
                // Ignore errors during final flush
            }

            _flushLock.Dispose();
            _httpClient.Dispose();
        }

        #endregion
    }
}
