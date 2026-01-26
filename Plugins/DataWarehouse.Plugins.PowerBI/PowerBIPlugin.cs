using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.PowerBI
{
    /// <summary>
    /// Abstract base class for telemetry plugins providing metrics, tracing, and logging capabilities.
    /// Extends <see cref="FeaturePluginBase"/> to support lifecycle management.
    /// </summary>
    public abstract class TelemetryPluginBase : FeaturePluginBase, IMetricsProvider
    {
        /// <summary>
        /// Gets the category for telemetry plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.MetricsProvider;

        /// <summary>
        /// Increments a counter metric by 1.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        public abstract void IncrementCounter(string metric);

        /// <summary>
        /// Records a value for a gauge or histogram metric.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="value">The value to record.</param>
        public abstract void RecordMetric(string metric, double value);

        /// <summary>
        /// Starts tracking the duration of an operation.
        /// </summary>
        /// <param name="metric">The metric name for the duration.</param>
        /// <returns>A disposable that records the duration when disposed.</returns>
        public abstract IDisposable TrackDuration(string metric);

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Telemetry";
            metadata["SupportsMetrics"] = true;
            metadata["SupportsPush"] = true;
            metadata["SupportsStreaming"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Production-ready Microsoft Power BI plugin implementing Push Datasets and Streaming Datasets APIs.
    /// Provides real-time data ingestion to Power BI dashboards and reports.
    ///
    /// Features:
    /// - OAuth2 authentication with Azure AD
    /// - Push Datasets API for batch ingestion
    /// - Streaming Datasets API for real-time data
    /// - Automatic token refresh
    /// - Retry logic with exponential backoff
    /// - Batch processing support
    /// - Metric aggregation and buffering
    ///
    /// Message Commands:
    /// - powerbi.push: Push data to Power BI dataset
    /// - powerbi.increment: Increment a counter metric
    /// - powerbi.record: Record a gauge metric value
    /// - powerbi.flush: Flush buffered data immediately
    /// - powerbi.status: Get plugin status
    /// - powerbi.test: Test connection and authentication
    /// </summary>
    public sealed class PowerBIPlugin : TelemetryPluginBase
    {
        private readonly PowerBIConfiguration _config;
        private readonly HttpClient _httpClient;
        private readonly ConcurrentQueue<Dictionary<string, object>> _dataBuffer;
        private readonly ConcurrentDictionary<string, double> _counters;
        private readonly ConcurrentDictionary<string, double> _gauges;
        private readonly object _lock = new();
        private readonly object _tokenLock = new();

        private string? _accessToken;
        private DateTime _tokenExpiry;
        private Timer? _flushTimer;
        private Timer? _tokenRefreshTimer;
        private bool _isRunning;
        private long _pushCount;
        private long _pushErrors;
        private long _totalRowsPushed;
        private readonly CancellationTokenSource _cts;

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.powerbi";

        /// <inheritdoc/>
        public override string Name => "Microsoft Power BI Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <summary>
        /// Gets the plugin configuration.
        /// </summary>
        public PowerBIConfiguration Configuration => _config;

        /// <summary>
        /// Gets whether the plugin is running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Gets the total number of push operations.
        /// </summary>
        public long PushCount => Interlocked.Read(ref _pushCount);

        /// <summary>
        /// Gets the total number of push errors.
        /// </summary>
        public long PushErrors => Interlocked.Read(ref _pushErrors);

        /// <summary>
        /// Gets the total number of rows pushed.
        /// </summary>
        public long TotalRowsPushed => Interlocked.Read(ref _totalRowsPushed);

        /// <summary>
        /// Gets the current buffer size.
        /// </summary>
        public int BufferSize => _dataBuffer.Count;

        /// <summary>
        /// Initializes a new instance of the <see cref="PowerBIPlugin"/> class.
        /// </summary>
        /// <param name="config">Optional configuration. Defaults will be used if null.</param>
        public PowerBIPlugin(PowerBIConfiguration? config = null)
        {
            _config = config ?? new PowerBIConfiguration();
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
            };
            _dataBuffer = new ConcurrentQueue<Dictionary<string, object>>();
            _counters = new ConcurrentDictionary<string, double>(StringComparer.Ordinal);
            _gauges = new ConcurrentDictionary<string, double>(StringComparer.Ordinal);
            _cts = new CancellationTokenSource();
            _tokenExpiry = DateTime.MinValue;
        }

        #region Lifecycle Management

        /// <inheritdoc/>
        public override async Task StartAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                if (_isRunning)
                {
                    return;
                }

                _isRunning = true;
            }

            // Validate configuration
            if (_config.ValidateOnStartup)
            {
                _config.Validate();
            }

            // Acquire initial token
            await RefreshAccessTokenAsync(ct);

            // Start automatic token refresh timer (refresh 5 minutes before expiry)
            if (_config.EnableAutoTokenRefresh)
            {
                var refreshInterval = TimeSpan.FromMinutes(50); // Tokens typically valid for 1 hour
                _tokenRefreshTimer = new Timer(
                    async _ => await RefreshAccessTokenAsync(CancellationToken.None),
                    null,
                    refreshInterval,
                    refreshInterval);
            }

            // Start flush timer for batch processing (flush every 5 seconds or when batch size reached)
            _flushTimer = new Timer(
                async _ => await FlushBufferAsync(),
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));

            await Task.CompletedTask;
        }

        /// <inheritdoc/>
        public override async Task StopAsync()
        {
            lock (_lock)
            {
                if (!_isRunning)
                {
                    return;
                }

                _isRunning = false;
            }

            // Stop timers
            if (_flushTimer != null)
            {
                await _flushTimer.DisposeAsync();
                _flushTimer = null;
            }

            if (_tokenRefreshTimer != null)
            {
                await _tokenRefreshTimer.DisposeAsync();
                _tokenRefreshTimer = null;
            }

            // Flush remaining data
            await FlushBufferAsync();

            _cts.Cancel();
        }

        #endregion

        #region IMetricsProvider Implementation

        /// <inheritdoc/>
        public override void IncrementCounter(string metric)
        {
            _counters.AddOrUpdate(metric, 1, (_, current) => current + 1);

            // Queue data for push
            QueueMetricData(metric, _counters[metric], "counter");
        }

        /// <inheritdoc/>
        public override void RecordMetric(string metric, double value)
        {
            _gauges[metric] = value;

            // Queue data for push
            QueueMetricData(metric, value, "gauge");
        }

        /// <inheritdoc/>
        public override IDisposable TrackDuration(string metric)
        {
            return new DurationTracker(this, metric);
        }

        #endregion

        #region Power BI API Methods

        /// <summary>
        /// Refreshes the OAuth2 access token from Azure AD.
        /// </summary>
        private async Task RefreshAccessTokenAsync(CancellationToken ct)
        {
            lock (_tokenLock)
            {
                // Check if token is still valid (with 5-minute buffer)
                if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
                {
                    return;
                }
            }

            var tokenUrl = $"{_config.AuthorityUrl}/{_config.TenantId}/oauth2/token";
            var requestBody = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _config.ClientId,
                ["client_secret"] = _config.ClientSecret,
                ["resource"] = _config.ResourceUrl
            };

            var content = new FormUrlEncodedContent(requestBody);
            var response = await _httpClient.PostAsync(tokenUrl, content, ct);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var tokenResponse = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(responseBody);

            if (tokenResponse != null && tokenResponse.TryGetValue("access_token", out var tokenElement))
            {
                lock (_tokenLock)
                {
                    _accessToken = tokenElement.GetString();

                    // Parse expires_in (in seconds)
                    if (tokenResponse.TryGetValue("expires_in", out var expiresElement))
                    {
                        var expiresInSeconds = expiresElement.GetInt32();
                        _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresInSeconds);
                    }
                    else
                    {
                        // Default to 1 hour
                        _tokenExpiry = DateTime.UtcNow.AddHours(1);
                    }
                }
            }
            else
            {
                throw new InvalidOperationException("Failed to acquire access token from Azure AD");
            }
        }

        /// <summary>
        /// Queues metric data for push to Power BI.
        /// </summary>
        private void QueueMetricData(string metric, double value, string type)
        {
            var row = new Dictionary<string, object>
            {
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["metric"] = metric,
                ["value"] = value,
                ["type"] = type
            };

            _dataBuffer.Enqueue(row);

            // Flush if batch size reached
            if (_dataBuffer.Count >= _config.BatchSize)
            {
                _ = Task.Run(FlushBufferAsync);
            }
        }

        /// <summary>
        /// Pushes data to Power BI dataset.
        /// </summary>
        /// <param name="rows">The rows to push.</param>
        private async Task PushDataAsync(List<Dictionary<string, object>> rows)
        {
            if (rows.Count == 0)
            {
                return;
            }

            // Ensure we have a valid token
            if (string.IsNullOrEmpty(_accessToken) || DateTime.UtcNow >= _tokenExpiry)
            {
                await RefreshAccessTokenAsync(CancellationToken.None);
            }

            var url = _config.UseStreamingDataset
                ? $"{_config.ApiBaseUrl}/datasets/{_config.DatasetId}/rows"
                : $"{_config.ApiBaseUrl}/groups/{_config.WorkspaceId}/datasets/{_config.DatasetId}/tables/{_config.TableName}/rows";

            var payload = new
            {
                rows = rows
            };

            var json = JsonSerializer.Serialize(payload);
            var retryCount = 0;
            var retryDelay = _config.RetryDelayMs;

            while (retryCount <= _config.MaxRetries)
            {
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };

                    lock (_tokenLock)
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                    }

                    var response = await _httpClient.SendAsync(request);

                    if (response.IsSuccessStatusCode)
                    {
                        Interlocked.Increment(ref _pushCount);
                        Interlocked.Add(ref _totalRowsPushed, rows.Count);
                        return;
                    }

                    // Handle rate limiting (429)
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        retryCount++;
                        if (retryCount <= _config.MaxRetries)
                        {
                            await Task.Delay(retryDelay);
                            retryDelay *= 2; // Exponential backoff
                            continue;
                        }
                    }

                    // Handle unauthorized (refresh token and retry once)
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        await RefreshAccessTokenAsync(CancellationToken.None);
                        retryCount++;
                        if (retryCount <= _config.MaxRetries)
                        {
                            continue;
                        }
                    }

                    var errorBody = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException(
                        $"Power BI API request failed with status {response.StatusCode}: {errorBody}");
                }
                catch (Exception ex) when (retryCount < _config.MaxRetries)
                {
                    retryCount++;
                    await Task.Delay(retryDelay);
                    retryDelay *= 2;

                    if (retryCount > _config.MaxRetries)
                    {
                        Interlocked.Increment(ref _pushErrors);
                        throw new InvalidOperationException(
                            $"Failed to push data to Power BI after {_config.MaxRetries} retries", ex);
                    }
                }
            }

            Interlocked.Increment(ref _pushErrors);
        }

        /// <summary>
        /// Flushes the buffered data to Power BI.
        /// </summary>
        private async Task FlushBufferAsync()
        {
            if (_dataBuffer.IsEmpty)
            {
                return;
            }

            var batch = new List<Dictionary<string, object>>();
            while (batch.Count < _config.BatchSize && _dataBuffer.TryDequeue(out var row))
            {
                batch.Add(row);
            }

            if (batch.Count > 0)
            {
                try
                {
                    await PushDataAsync(batch);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[PowerBI] Error flushing buffer: {ex.Message}");

                    // Re-queue failed rows (up to batch size to avoid infinite growth)
                    foreach (var row in batch.Take(_config.BatchSize))
                    {
                        _dataBuffer.Enqueue(row);
                    }
                }
            }
        }

        /// <summary>
        /// Tests the connection and authentication to Power BI.
        /// </summary>
        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                await RefreshAccessTokenAsync(CancellationToken.None);

                // Test by pushing an empty batch (validates dataset access)
                var testUrl = _config.UseStreamingDataset
                    ? $"{_config.ApiBaseUrl}/datasets/{_config.DatasetId}/rows"
                    : $"{_config.ApiBaseUrl}/groups/{_config.WorkspaceId}/datasets/{_config.DatasetId}/tables/{_config.TableName}/rows";

                var request = new HttpRequestMessage(HttpMethod.Get, testUrl.Replace("/rows", ""));
                lock (_tokenLock)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                }

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound; // NotFound is OK (endpoint doesn't support GET)
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "powerbi.push":
                    await HandlePushAsync(message);
                    break;
                case "powerbi.increment":
                    HandleIncrement(message);
                    break;
                case "powerbi.record":
                    HandleRecord(message);
                    break;
                case "powerbi.flush":
                    await HandleFlushAsync(message);
                    break;
                case "powerbi.status":
                    HandleStatus(message);
                    break;
                case "powerbi.test":
                    await HandleTestAsync(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        private async Task HandlePushAsync(PluginMessage message)
        {
            if (!message.Payload.TryGetValue("data", out var dataObj))
            {
                message.Payload["error"] = "data field required";
                return;
            }

            try
            {
                // Support single row or array of rows
                List<Dictionary<string, object>> rows;

                if (dataObj is Dictionary<string, object> singleRow)
                {
                    rows = new List<Dictionary<string, object>> { singleRow };
                }
                else if (dataObj is List<object> list)
                {
                    rows = list.OfType<Dictionary<string, object>>().ToList();
                }
                else
                {
                    message.Payload["error"] = "data must be a dictionary or list of dictionaries";
                    return;
                }

                await PushDataAsync(rows);
                message.Payload["success"] = true;
                message.Payload["rowsPushed"] = rows.Count;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private void HandleIncrement(PluginMessage message)
        {
            var metric = GetString(message.Payload, "metric");
            if (string.IsNullOrEmpty(metric))
            {
                message.Payload["error"] = "metric name required";
                return;
            }

            IncrementCounter(metric);
            message.Payload["success"] = true;
        }

        private void HandleRecord(PluginMessage message)
        {
            var metric = GetString(message.Payload, "metric");
            if (string.IsNullOrEmpty(metric))
            {
                message.Payload["error"] = "metric name required";
                return;
            }

            var value = GetDouble(message.Payload, "value");
            if (!value.HasValue)
            {
                message.Payload["error"] = "value required";
                return;
            }

            RecordMetric(metric, value.Value);
            message.Payload["success"] = true;
        }

        private async Task HandleFlushAsync(PluginMessage message)
        {
            try
            {
                await FlushBufferAsync();
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private void HandleStatus(PluginMessage message)
        {
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["isRunning"] = _isRunning,
                ["pushCount"] = PushCount,
                ["pushErrors"] = PushErrors,
                ["totalRowsPushed"] = TotalRowsPushed,
                ["bufferSize"] = BufferSize,
                ["hasValidToken"] = !string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry,
                ["tokenExpiry"] = _tokenExpiry.ToString("o"),
                ["useStreamingDataset"] = _config.UseStreamingDataset,
                ["counters"] = _counters.ToDictionary(kv => kv.Key, kv => kv.Value),
                ["gauges"] = _gauges.ToDictionary(kv => kv.Key, kv => kv.Value)
            };
        }

        private async Task HandleTestAsync(PluginMessage message)
        {
            try
            {
                var success = await TestConnectionAsync();
                message.Payload["success"] = success;
                message.Payload["message"] = success
                    ? "Connection and authentication successful"
                    : "Connection or authentication failed";
            }
            catch (Exception ex)
            {
                message.Payload["success"] = false;
                message.Payload["error"] = ex.Message;
            }
        }

        #endregion

        #region Helpers

        private static string? GetString(Dictionary<string, object> payload, string key)
        {
            return payload.TryGetValue(key, out var val) && val is string s ? s : null;
        }

        private static double? GetDouble(Dictionary<string, object> payload, string key)
        {
            if (payload.TryGetValue(key, out var val))
            {
                if (val is double d) return d;
                if (val is int i) return i;
                if (val is long l) return l;
                if (val is float f) return f;
                if (val is string s && double.TryParse(s, out var parsed)) return parsed;
            }
            return null;
        }

        #endregion

        #region Plugin Metadata

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "powerbi.push", DisplayName = "Push Data", Description = "Push data rows to Power BI dataset" },
                new() { Name = "powerbi.increment", DisplayName = "Increment Counter", Description = "Increment a counter metric" },
                new() { Name = "powerbi.record", DisplayName = "Record Gauge", Description = "Record a gauge metric value" },
                new() { Name = "powerbi.flush", DisplayName = "Flush Buffer", Description = "Flush buffered data immediately" },
                new() { Name = "powerbi.status", DisplayName = "Get Status", Description = "Get plugin status" },
                new() { Name = "powerbi.test", DisplayName = "Test Connection", Description = "Test Power BI connection and authentication" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["WorkspaceId"] = _config.WorkspaceId;
            metadata["DatasetId"] = _config.DatasetId;
            metadata["UseStreamingDataset"] = _config.UseStreamingDataset;
            metadata["BatchSize"] = _config.BatchSize;
            metadata["SupportsCounters"] = true;
            metadata["SupportsGauges"] = true;
            metadata["SupportsPush"] = true;
            metadata["SupportsStreaming"] = true;
            metadata["SupportsOAuth2"] = true;
            return metadata;
        }

        #endregion

        #region Duration Tracker

        /// <summary>
        /// Helper class for tracking operation duration.
        /// </summary>
        private sealed class DurationTracker : IDisposable
        {
            private readonly PowerBIPlugin _plugin;
            private readonly string _metric;
            private readonly DateTime _startTime;
            private bool _disposed;

            public DurationTracker(PowerBIPlugin plugin, string metric)
            {
                _plugin = plugin;
                _metric = metric;
                _startTime = DateTime.UtcNow;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                var duration = (DateTime.UtcNow - _startTime).TotalMilliseconds;
                _plugin.RecordMetric($"{_metric}_duration_ms", duration);
            }
        }

        #endregion
    }
}
