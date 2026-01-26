using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.Geckoboard
{
    /// <summary>
    /// Production-ready Geckoboard dashboard integration plugin.
    /// Provides dataset push capabilities to Geckoboard's Datasets API.
    ///
    /// Features:
    /// - Datasets API integration for widget data
    /// - API key authentication
    /// - Batch data updates
    /// - Automatic retry logic
    /// - Dataset schema management
    /// - Error handling and logging
    ///
    /// Message Commands:
    /// - geckoboard.push: Push data to a dataset
    /// - geckoboard.create: Create a new dataset
    /// - geckoboard.delete: Delete a dataset
    /// - geckoboard.status: Get plugin status
    /// </summary>
    public sealed class GeckoboardPlugin : FeaturePluginBase, IMetricsProvider
    {
        private readonly GeckoboardPluginConfig _config;
        private readonly HttpClient _httpClient;
        private bool _isInitialized;
        private long _pushCount;
        private long _errorCount;
        private readonly object _lock = new();

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.geckoboard";

        /// <inheritdoc/>
        public override string Name => "Geckoboard Dashboard Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override PluginCategory Category => PluginCategory.MetricsProvider;

        /// <summary>
        /// Gets the total number of data pushes.
        /// </summary>
        public long PushCount => Interlocked.Read(ref _pushCount);

        /// <summary>
        /// Gets the total number of errors.
        /// </summary>
        public long ErrorCount => Interlocked.Read(ref _errorCount);

        /// <summary>
        /// Initializes a new instance of the <see cref="GeckoboardPlugin"/> class.
        /// </summary>
        /// <param name="config">The plugin configuration. If null, environment variables will be checked.</param>
        public GeckoboardPlugin(GeckoboardPluginConfig? config = null)
        {
            _config = config ?? LoadConfigFromEnvironment();
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(_config.ApiBaseUrl),
                Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
            };

            // Configure authentication
            var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_config.ApiKey}:"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authValue);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        #region Lifecycle Management

        /// <inheritdoc/>
        public override async Task StartAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                if (_isInitialized)
                {
                    return;
                }
            }

            if (_config.ValidateOnStartup)
            {
                _config.Validate();
                await ValidateApiConnectionAsync(ct);
            }

            lock (_lock)
            {
                _isInitialized = true;
            }
        }

        /// <inheritdoc/>
        public override async Task StopAsync()
        {
            lock (_lock)
            {
                if (!_isInitialized)
                {
                    return;
                }

                _isInitialized = false;
            }

            _httpClient.Dispose();
            await Task.CompletedTask;
        }

        #endregion

        #region IMetricsProvider Implementation

        /// <inheritdoc/>
        public void IncrementCounter(string metric)
        {
            _ = PushMetricAsync(metric, 1, MetricType.Counter);
        }

        /// <inheritdoc/>
        public void RecordMetric(string metric, double value)
        {
            _ = PushMetricAsync(metric, value, MetricType.Gauge);
        }

        /// <inheritdoc/>
        public IDisposable TrackDuration(string metric)
        {
            return new DurationTracker(this, metric);
        }

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "geckoboard.push":
                    await HandlePushAsync(message);
                    break;
                case "geckoboard.create":
                    await HandleCreateDatasetAsync(message);
                    break;
                case "geckoboard.delete":
                    await HandleDeleteDatasetAsync(message);
                    break;
                case "geckoboard.status":
                    HandleStatus(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        private async Task HandlePushAsync(PluginMessage message)
        {
            try
            {
                var datasetId = GetString(message.Payload, "datasetId") ?? _config.DatasetId;
                var data = message.Payload.GetValueOrDefault("data");

                if (data == null)
                {
                    message.Payload["error"] = "Data is required";
                    return;
                }

                var success = await PushDataAsync(datasetId, data);
                message.Payload["success"] = success;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
                Interlocked.Increment(ref _errorCount);
            }
        }

        private async Task HandleCreateDatasetAsync(PluginMessage message)
        {
            try
            {
                var datasetId = GetString(message.Payload, "datasetId");
                if (string.IsNullOrWhiteSpace(datasetId))
                {
                    message.Payload["error"] = "Dataset ID is required";
                    return;
                }

                var fields = message.Payload.GetValueOrDefault("fields");
                if (fields == null)
                {
                    message.Payload["error"] = "Fields schema is required";
                    return;
                }

                var success = await CreateDatasetAsync(datasetId, fields);
                message.Payload["success"] = success;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
                Interlocked.Increment(ref _errorCount);
            }
        }

        private async Task HandleDeleteDatasetAsync(PluginMessage message)
        {
            try
            {
                var datasetId = GetString(message.Payload, "datasetId");
                if (string.IsNullOrWhiteSpace(datasetId))
                {
                    message.Payload["error"] = "Dataset ID is required";
                    return;
                }

                var success = await DeleteDatasetAsync(datasetId);
                message.Payload["success"] = success;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
                Interlocked.Increment(ref _errorCount);
            }
        }

        private void HandleStatus(PluginMessage message)
        {
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["isInitialized"] = _isInitialized,
                ["datasetId"] = _config.DatasetId,
                ["apiBaseUrl"] = _config.ApiBaseUrl,
                ["pushCount"] = PushCount,
                ["errorCount"] = ErrorCount
            };
        }

        #endregion

        #region Geckoboard API Methods

        /// <summary>
        /// Pushes data to a Geckoboard dataset.
        /// </summary>
        /// <param name="datasetId">The dataset identifier.</param>
        /// <param name="data">The data to push.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if successful; otherwise, false.</returns>
        public async Task<bool> PushDataAsync(string datasetId, object data, CancellationToken ct = default)
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException("Plugin not initialized. Call StartAsync first.");
            }

            var payload = new { data };
            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            var retries = 0;
            while (retries <= (_config.EnableRetries ? _config.MaxRetries : 0))
            {
                try
                {
                    var response = await _httpClient.PostAsync(
                        $"/datasets/{datasetId}/data",
                        content,
                        ct);

                    if (response.IsSuccessStatusCode)
                    {
                        Interlocked.Increment(ref _pushCount);
                        LogVerbose($"Successfully pushed data to dataset '{datasetId}'");
                        return true;
                    }

                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    LogError($"Failed to push data to dataset '{datasetId}': {response.StatusCode} - {errorBody}");

                    if (!ShouldRetry(response.StatusCode))
                    {
                        break;
                    }

                    retries++;
                    if (retries <= _config.MaxRetries)
                    {
                        await Task.Delay(GetRetryDelay(retries), ct);
                    }
                }
                catch (Exception ex)
                {
                    LogError($"Exception pushing data to dataset '{datasetId}': {ex.Message}");
                    retries++;
                    if (retries <= _config.MaxRetries)
                    {
                        await Task.Delay(GetRetryDelay(retries), ct);
                    }
                }
            }

            Interlocked.Increment(ref _errorCount);
            return false;
        }

        /// <summary>
        /// Creates a new dataset with the specified schema.
        /// </summary>
        /// <param name="datasetId">The dataset identifier.</param>
        /// <param name="fields">The fields schema.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if successful; otherwise, false.</returns>
        public async Task<bool> CreateDatasetAsync(string datasetId, object fields, CancellationToken ct = default)
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException("Plugin not initialized. Call StartAsync first.");
            }

            var payload = new { fields };
            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            try
            {
                var response = await _httpClient.PutAsync(
                    $"/datasets/{datasetId}",
                    content,
                    ct);

                if (response.IsSuccessStatusCode)
                {
                    LogVerbose($"Successfully created dataset '{datasetId}'");
                    return true;
                }

                var errorBody = await response.Content.ReadAsStringAsync(ct);
                LogError($"Failed to create dataset '{datasetId}': {response.StatusCode} - {errorBody}");
                Interlocked.Increment(ref _errorCount);
                return false;
            }
            catch (Exception ex)
            {
                LogError($"Exception creating dataset '{datasetId}': {ex.Message}");
                Interlocked.Increment(ref _errorCount);
                return false;
            }
        }

        /// <summary>
        /// Deletes a dataset.
        /// </summary>
        /// <param name="datasetId">The dataset identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if successful; otherwise, false.</returns>
        public async Task<bool> DeleteDatasetAsync(string datasetId, CancellationToken ct = default)
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException("Plugin not initialized. Call StartAsync first.");
            }

            try
            {
                var response = await _httpClient.DeleteAsync(
                    $"/datasets/{datasetId}",
                    ct);

                if (response.IsSuccessStatusCode)
                {
                    LogVerbose($"Successfully deleted dataset '{datasetId}'");
                    return true;
                }

                var errorBody = await response.Content.ReadAsStringAsync(ct);
                LogError($"Failed to delete dataset '{datasetId}': {response.StatusCode} - {errorBody}");
                Interlocked.Increment(ref _errorCount);
                return false;
            }
            catch (Exception ex)
            {
                LogError($"Exception deleting dataset '{datasetId}': {ex.Message}");
                Interlocked.Increment(ref _errorCount);
                return false;
            }
        }

        /// <summary>
        /// Pushes a single metric value to Geckoboard.
        /// </summary>
        private async Task PushMetricAsync(string metric, double value, MetricType type, CancellationToken ct = default)
        {
            var data = new[]
            {
                new Dictionary<string, object>
                {
                    ["metric"] = metric,
                    ["value"] = value,
                    ["type"] = type.ToString().ToLowerInvariant(),
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                }
            };

            await PushDataAsync(_config.DatasetId, data, ct);
        }

        #endregion

        #region Helper Methods

        private async Task ValidateApiConnectionAsync(CancellationToken ct)
        {
            try
            {
                // Test connection by attempting to access the datasets endpoint
                var response = await _httpClient.GetAsync("/datasets", ct);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"Failed to validate Geckoboard API connection: {response.StatusCode}");
                }

                LogVerbose("Geckoboard API connection validated successfully");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to validate Geckoboard API connection: {ex.Message}", ex);
            }
        }

        private static GeckoboardPluginConfig LoadConfigFromEnvironment()
        {
            return new GeckoboardPluginConfig
            {
                ApiKey = Environment.GetEnvironmentVariable("GECKOBOARD_API_KEY") ?? string.Empty,
                DatasetId = Environment.GetEnvironmentVariable("GECKOBOARD_DATASET_ID") ?? "datawarehouse_metrics",
                ApiBaseUrl = Environment.GetEnvironmentVariable("GECKOBOARD_API_URL") ?? "https://api.geckoboard.com"
            };
        }

        private static bool ShouldRetry(System.Net.HttpStatusCode statusCode)
        {
            // Retry on transient errors
            return statusCode == System.Net.HttpStatusCode.RequestTimeout ||
                   statusCode == System.Net.HttpStatusCode.TooManyRequests ||
                   statusCode == System.Net.HttpStatusCode.InternalServerError ||
                   statusCode == System.Net.HttpStatusCode.BadGateway ||
                   statusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                   statusCode == System.Net.HttpStatusCode.GatewayTimeout;
        }

        private static TimeSpan GetRetryDelay(int retryAttempt)
        {
            // Exponential backoff: 1s, 2s, 4s, 8s...
            return TimeSpan.FromSeconds(Math.Pow(2, retryAttempt - 1));
        }

        private static string? GetString(Dictionary<string, object> payload, string key)
        {
            return payload.TryGetValue(key, out var val) && val is string s ? s : null;
        }

        private void LogVerbose(string message)
        {
            if (_config.EnableVerboseLogging)
            {
                Console.WriteLine($"[Geckoboard] {message}");
            }
        }

        private static void LogError(string message)
        {
            Console.Error.WriteLine($"[Geckoboard] {message}");
        }

        #endregion

        #region Plugin Metadata

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "geckoboard.push", DisplayName = "Push Data", Description = "Push data to a Geckoboard dataset" },
                new() { Name = "geckoboard.create", DisplayName = "Create Dataset", Description = "Create a new dataset" },
                new() { Name = "geckoboard.delete", DisplayName = "Delete Dataset", Description = "Delete a dataset" },
                new() { Name = "geckoboard.status", DisplayName = "Get Status", Description = "Get plugin status" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Telemetry";
            metadata["DatasetId"] = _config.DatasetId;
            metadata["ApiBaseUrl"] = _config.ApiBaseUrl;
            metadata["BatchSize"] = _config.BatchSize;
            metadata["EnableRetries"] = _config.EnableRetries;
            metadata["MaxRetries"] = _config.MaxRetries;
            return metadata;
        }

        #endregion

        #region Duration Tracker

        /// <summary>
        /// Helper class for tracking operation duration.
        /// </summary>
        private sealed class DurationTracker : IDisposable
        {
            private readonly GeckoboardPlugin _plugin;
            private readonly string _metric;
            private readonly System.Diagnostics.Stopwatch _stopwatch;
            private bool _disposed;

            public DurationTracker(GeckoboardPlugin plugin, string metric)
            {
                _plugin = plugin;
                _metric = metric;
                _stopwatch = System.Diagnostics.Stopwatch.StartNew();
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _stopwatch.Stop();
                _plugin.RecordMetric($"{_metric}_duration_ms", _stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        #endregion

        /// <summary>
        /// Metric type enumeration for internal tracking.
        /// </summary>
        private enum MetricType
        {
            Counter,
            Gauge
        }
    }
}
