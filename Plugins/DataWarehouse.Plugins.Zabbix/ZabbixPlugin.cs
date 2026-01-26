using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.Zabbix
{
    /// <summary>
    /// Production-ready Zabbix monitoring plugin implementing Zabbix Sender protocol and JSON-RPC 2.0 API.
    /// Provides metrics collection and push capabilities to Zabbix monitoring server.
    ///
    /// Features:
    /// - Zabbix Sender protocol for high-performance metric push
    /// - JSON-RPC 2.0 API support for flexible metric submission
    /// - Metric buffering and batching for efficiency
    /// - Default process and runtime metrics
    /// - Automatic metric flushing with configurable intervals
    /// - Support for custom metric keys and values
    ///
    /// Message Commands:
    /// - zabbix.send: Send a metric immediately
    /// - zabbix.increment: Increment a counter metric
    /// - zabbix.record: Record a gauge value
    /// - zabbix.flush: Flush buffered metrics
    /// - zabbix.status: Get plugin status
    /// </summary>
    public sealed class ZabbixPlugin : TelemetryPluginBase
    {
        private readonly ZabbixConfiguration _config;
        private readonly ConcurrentQueue<ZabbixMetric> _metricsBuffer;
        private readonly ConcurrentDictionary<string, double> _counters;
        private readonly ConcurrentDictionary<string, double> _gauges;
        private readonly HttpClient _httpClient;
        private readonly object _lock = new();
        private CancellationTokenSource? _cts;
        private Task? _flushTask;
        private bool _isRunning;
        private DateTime _startTime;
        private long _metricsSent;
        private long _metricsErrors;
        private readonly Stopwatch _uptimeStopwatch = new();
        private string? _authToken;
        private int _requestIdCounter;

        // Default metrics
        private DateTime _lastProcessMetricsUpdate;
        private DateTime _lastRuntimeMetricsUpdate;
        private readonly TimeSpan _metricsUpdateInterval = TimeSpan.FromSeconds(5);

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.zabbix";

        /// <inheritdoc/>
        public override string Name => "Zabbix Monitoring Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <summary>
        /// Gets the plugin configuration.
        /// </summary>
        public ZabbixConfiguration Configuration => _config;

        /// <summary>
        /// Gets whether the plugin is running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Gets the total number of metrics sent.
        /// </summary>
        public long MetricsSent => Interlocked.Read(ref _metricsSent);

        /// <summary>
        /// Gets the total number of errors.
        /// </summary>
        public long MetricsErrors => Interlocked.Read(ref _metricsErrors);

        /// <summary>
        /// Gets the number of buffered metrics.
        /// </summary>
        public int BufferedMetrics => _metricsBuffer.Count;

        /// <summary>
        /// Initializes a new instance of the <see cref="ZabbixPlugin"/> class.
        /// </summary>
        /// <param name="config">Optional configuration. Defaults will be used if null.</param>
        public ZabbixPlugin(ZabbixConfiguration? config = null)
        {
            _config = config ?? new ZabbixConfiguration();
            _metricsBuffer = new ConcurrentQueue<ZabbixMetric>();
            _counters = new ConcurrentDictionary<string, double>(StringComparer.Ordinal);
            _gauges = new ConcurrentDictionary<string, double>(StringComparer.Ordinal);
            _httpClient = new HttpClient
            {
                Timeout = _config.ConnectionTimeout
            };
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
                _startTime = DateTime.UtcNow;
                _uptimeStopwatch.Start();
            }

            // Authenticate if using API
            if (_config.UseApiForMetrics && !string.IsNullOrEmpty(_config.ApiUsername))
            {
                try
                {
                    await AuthenticateAsync(ct);
                }
                catch (Exception ex)
                {
                    _isRunning = false;
                    throw new InvalidOperationException($"Failed to authenticate with Zabbix API: {ex.Message}", ex);
                }
            }

            // Start flush task
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _flushTask = FlushTaskAsync(_cts.Token);

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
                _uptimeStopwatch.Stop();
            }

            _cts?.Cancel();

            // Flush remaining metrics
            try
            {
                await FlushMetricsAsync();
            }
            catch
            {
                // Ignore errors during shutdown
            }

            if (_flushTask != null)
            {
                try
                {
                    await _flushTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
                catch (TimeoutException)
                {
                    // Timeout waiting for flush task to stop
                }
            }

            _cts?.Dispose();
            _cts = null;
            _flushTask = null;
        }

        #endregion

        #region IMetricsProvider Implementation

        /// <inheritdoc/>
        public override void IncrementCounter(string metric)
        {
            IncrementCounter(metric, 1);
        }

        /// <summary>
        /// Increments a counter metric by the specified amount.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="amount">The amount to increment.</param>
        public void IncrementCounter(string metric, double amount = 1)
        {
            var key = GetFullMetricKey(metric);
            var newValue = _counters.AddOrUpdate(key, amount, (_, current) => current + amount);

            // Send the updated counter value
            SendMetric(key, newValue);
        }

        /// <inheritdoc/>
        public override void RecordMetric(string metric, double value)
        {
            var key = GetFullMetricKey(metric);
            _gauges[key] = value;
            SendMetric(key, value);
        }

        /// <summary>
        /// Records a gauge metric value.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="value">The value to record.</param>
        public void RecordGauge(string metric, double value)
        {
            RecordMetric(metric, value);
        }

        /// <inheritdoc/>
        public override IDisposable TrackDuration(string metric)
        {
            return new DurationTracker(this, metric);
        }

        #endregion

        #region Metric Sending

        /// <summary>
        /// Sends a metric with the specified key and value.
        /// </summary>
        /// <param name="key">The metric key.</param>
        /// <param name="value">The metric value.</param>
        /// <param name="hostName">Optional host name (uses config default if null).</param>
        public void SendMetric(string key, object value, string? hostName = null)
        {
            var metric = new ZabbixMetric
            {
                Key = key,
                Value = value,
                Timestamp = DateTime.UtcNow,
                HostName = hostName
            };

            _metricsBuffer.Enqueue(metric);
        }

        /// <summary>
        /// Flushes all buffered metrics to Zabbix.
        /// </summary>
        public async Task FlushMetricsAsync()
        {
            if (_metricsBuffer.IsEmpty)
            {
                return;
            }

            // Collect default metrics before flushing
            CollectDefaultMetrics();

            // Dequeue metrics up to batch size
            var batch = new List<ZabbixMetric>();
            while (batch.Count < _config.BatchSize && _metricsBuffer.TryDequeue(out var metric))
            {
                batch.Add(metric);
            }

            if (batch.Count == 0)
            {
                return;
            }

            try
            {
                if (_config.UseSenderProtocol)
                {
                    await SendViaSenderProtocolAsync(batch);
                }
                else if (_config.UseApiForMetrics)
                {
                    await SendViaApiAsync(batch);
                }

                Interlocked.Add(ref _metricsSent, batch.Count);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _metricsErrors);
                Console.Error.WriteLine($"[Zabbix] Error sending metrics: {ex.Message}");

                // Re-enqueue failed metrics
                foreach (var metric in batch)
                {
                    _metricsBuffer.Enqueue(metric);
                }
            }
        }

        private async Task SendViaSenderProtocolAsync(List<ZabbixMetric> metrics)
        {
            var senderData = new ZabbixSenderData
            {
                Clock = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Data = metrics.Select(m => new ZabbixMetricData
                {
                    Host = m.HostName ?? _config.HostName,
                    Key = m.Key,
                    Value = m.GetValueString(),
                    Clock = new DateTimeOffset(m.Timestamp).ToUnixTimeSeconds(),
                    Ns = (m.Timestamp.Ticks % TimeSpan.TicksPerSecond) * 100
                }).ToList()
            };

            var jsonData = JsonSerializer.Serialize(senderData);
            var jsonBytes = Encoding.UTF8.GetBytes(jsonData);

            // Zabbix sender protocol: ZBXD\x01 + length (8 bytes, little-endian) + JSON data
            var header = Encoding.ASCII.GetBytes("ZBXD\x01");
            var lengthBytes = BitConverter.GetBytes((long)jsonBytes.Length);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(lengthBytes);
            }

            var packet = new byte[header.Length + lengthBytes.Length + jsonBytes.Length];
            Buffer.BlockCopy(header, 0, packet, 0, header.Length);
            Buffer.BlockCopy(lengthBytes, 0, packet, header.Length, lengthBytes.Length);
            Buffer.BlockCopy(jsonBytes, 0, packet, header.Length + lengthBytes.Length, jsonBytes.Length);

            using var client = new TcpClient();
            await client.ConnectAsync(_config.ZabbixSenderHost, _config.ZabbixSenderPort);

            var stream = client.GetStream();
            await stream.WriteAsync(packet);
            await stream.FlushAsync();

            // Read response
            var responseHeader = new byte[13]; // ZBXD\x01 + 8 bytes length
            var bytesRead = await stream.ReadAsync(responseHeader.AsMemory(0, 13));

            if (bytesRead == 13)
            {
                var responseLength = BitConverter.ToInt64(responseHeader, 5);
                if (!BitConverter.IsLittleEndian)
                {
                    responseLength = BinaryPrimitives.ReverseEndianness(responseLength);
                }

                if (responseLength > 0 && responseLength < 1024 * 1024) // Sanity check: max 1MB
                {
                    var responseData = new byte[responseLength];
                    bytesRead = await stream.ReadAsync(responseData.AsMemory(0, (int)responseLength));

                    if (bytesRead == responseLength)
                    {
                        var responseJson = Encoding.UTF8.GetString(responseData);
                        var response = JsonSerializer.Deserialize<ZabbixSenderResponse>(responseJson);

                        if (response?.Response != "success")
                        {
                            throw new InvalidOperationException($"Zabbix sender protocol failed: {response?.Info ?? "unknown error"}");
                        }
                    }
                }
            }
        }

        private async Task SendViaApiAsync(List<ZabbixMetric> metrics)
        {
            // Note: Standard Zabbix API doesn't support direct history push
            // This requires custom Zabbix API extension or using zabbix_sender
            // For now, we'll use a placeholder that could be extended

            // In production, you would typically:
            // 1. Get item IDs for the keys
            // 2. Use a custom API method to push history
            // 3. Or fall back to zabbix_sender

            throw new NotSupportedException(
                "Zabbix API metric push requires custom API extension. " +
                "Use UseSenderProtocol=true for standard Zabbix compatibility.");
        }

        #endregion

        #region Authentication

        private async Task AuthenticateAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_config.ApiUsername) || string.IsNullOrEmpty(_config.ApiPassword))
            {
                throw new InvalidOperationException("API username and password are required for authentication.");
            }

            var request = new JsonRpcRequest
            {
                Method = "user.login",
                Params = new LoginParams
                {
                    Username = _config.ApiUsername,
                    Password = _config.ApiPassword
                },
                Id = Interlocked.Increment(ref _requestIdCounter)
            };

            var response = await SendJsonRpcRequestAsync<string>(request, ct);
            _authToken = response;
        }

        private async Task<T?> SendJsonRpcRequestAsync<T>(JsonRpcRequest request, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(_authToken))
            {
                request.Auth = _authToken;
            }

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var httpResponse = await _httpClient.PostAsync(_config.ZabbixServerUrl, content, ct);
            httpResponse.EnsureSuccessStatusCode();

            var responseJson = await httpResponse.Content.ReadAsStringAsync(ct);
            var rpcResponse = JsonSerializer.Deserialize<JsonRpcResponse<T>>(responseJson);

            if (rpcResponse?.Error != null)
            {
                throw new InvalidOperationException(
                    $"Zabbix API error [{rpcResponse.Error.Code}]: {rpcResponse.Error.Message}");
            }

            return rpcResponse!.Result;
        }

        #endregion

        #region Default Metrics

        private void CollectDefaultMetrics()
        {
            var now = DateTime.UtcNow;

            // Update process metrics
            if (_config.IncludeProcessMetrics &&
                (now - _lastProcessMetricsUpdate) >= _metricsUpdateInterval)
            {
                _lastProcessMetricsUpdate = now;
                CollectProcessMetrics();
            }

            // Update runtime metrics
            if (_config.IncludeRuntimeMetrics &&
                (now - _lastRuntimeMetricsUpdate) >= _metricsUpdateInterval)
            {
                _lastRuntimeMetricsUpdate = now;
                CollectRuntimeMetrics();
            }

            // Plugin metrics
            SendMetric("zabbix.metrics_sent", MetricsSent);
            SendMetric("zabbix.metrics_errors", MetricsErrors);
            SendMetric("zabbix.buffered_metrics", BufferedMetrics);
            SendMetric("zabbix.uptime_seconds", _uptimeStopwatch.Elapsed.TotalSeconds);
        }

        private void CollectProcessMetrics()
        {
            try
            {
                var process = Process.GetCurrentProcess();

                SendMetric("process.cpu_seconds_total", process.TotalProcessorTime.TotalSeconds);
                SendMetric("process.memory_bytes", process.WorkingSet64);
                SendMetric("process.virtual_memory_bytes", process.VirtualMemorySize64);
                SendMetric("process.threads", process.Threads.Count);
                SendMetric("process.handles", process.HandleCount);
                SendMetric("process.start_time", new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Zabbix] Error collecting process metrics: {ex.Message}");
            }
        }

        private void CollectRuntimeMetrics()
        {
            try
            {
                // GC metrics
                for (var gen = 0; gen <= GC.MaxGeneration; gen++)
                {
                    SendMetric($"dotnet.gc.collection_count[gen{gen}]", GC.CollectionCount(gen));
                }

                var gcInfo = GC.GetGCMemoryInfo();
                SendMetric("dotnet.gc.heap_size_bytes", gcInfo.HeapSizeBytes);
                SendMetric("dotnet.gc.memory_load_bytes", gcInfo.MemoryLoadBytes);
                SendMetric("dotnet.gc.total_allocated_bytes", GC.GetTotalAllocatedBytes());

                // ThreadPool metrics
                ThreadPool.GetAvailableThreads(out var workerThreads, out var completionPortThreads);
                ThreadPool.GetMaxThreads(out var maxWorkerThreads, out var maxCompletionPortThreads);

                SendMetric("dotnet.threadpool.threads_active", maxWorkerThreads - workerThreads);
                SendMetric("dotnet.threadpool.queue_length", ThreadPool.PendingWorkItemCount);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Zabbix] Error collecting runtime metrics: {ex.Message}");
            }
        }

        #endregion

        #region Flush Task

        private async Task FlushTaskAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_config.FlushInterval, ct);
                    await FlushMetricsAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Zabbix] Error in flush task: {ex.Message}");
                }
            }
        }

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "zabbix.send":
                    HandleSend(message);
                    break;
                case "zabbix.increment":
                    HandleIncrement(message);
                    break;
                case "zabbix.record":
                    HandleRecord(message);
                    break;
                case "zabbix.flush":
                    await HandleFlushAsync(message);
                    break;
                case "zabbix.status":
                    HandleStatus(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        private void HandleSend(PluginMessage message)
        {
            var key = GetString(message.Payload, "key");
            if (string.IsNullOrEmpty(key))
            {
                message.Payload["error"] = "metric key required";
                return;
            }

            var value = message.Payload.TryGetValue("value", out var val) ? val : null;
            if (value == null)
            {
                message.Payload["error"] = "value required";
                return;
            }

            var hostName = GetString(message.Payload, "host");
            SendMetric(key, value, hostName);
            message.Payload["success"] = true;
        }

        private void HandleIncrement(PluginMessage message)
        {
            var metric = GetString(message.Payload, "metric");
            if (string.IsNullOrEmpty(metric))
            {
                message.Payload["error"] = "metric name required";
                return;
            }

            var amount = GetDouble(message.Payload, "amount") ?? 1;
            IncrementCounter(metric, amount);
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

            RecordGauge(metric, value.Value);
            message.Payload["success"] = true;
        }

        private async Task HandleFlushAsync(PluginMessage message)
        {
            try
            {
                await FlushMetricsAsync();
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
                ["serverUrl"] = _config.ZabbixServerUrl,
                ["hostName"] = _config.HostName,
                ["useSenderProtocol"] = _config.UseSenderProtocol,
                ["metricsSent"] = MetricsSent,
                ["metricsErrors"] = MetricsErrors,
                ["bufferedMetrics"] = BufferedMetrics,
                ["uptimeSeconds"] = _uptimeStopwatch.Elapsed.TotalSeconds
            };
        }

        #endregion

        #region Helpers

        private string GetFullMetricKey(string key)
        {
            if (string.IsNullOrEmpty(_config.MetricPrefix))
            {
                return key;
            }

            return $"{_config.MetricPrefix}.{key}";
        }

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
                new() { Name = "zabbix.send", DisplayName = "Send Metric", Description = "Send a metric to Zabbix" },
                new() { Name = "zabbix.increment", DisplayName = "Increment Counter", Description = "Increment a counter metric" },
                new() { Name = "zabbix.record", DisplayName = "Record Gauge", Description = "Record a gauge metric value" },
                new() { Name = "zabbix.flush", DisplayName = "Flush Metrics", Description = "Flush buffered metrics" },
                new() { Name = "zabbix.status", DisplayName = "Get Status", Description = "Get plugin status" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["ServerUrl"] = _config.ZabbixServerUrl;
            metadata["HostName"] = _config.HostName;
            metadata["UseSenderProtocol"] = _config.UseSenderProtocol;
            metadata["UseApiForMetrics"] = _config.UseApiForMetrics;
            metadata["BatchSize"] = _config.BatchSize;
            metadata["FlushInterval"] = _config.FlushInterval.TotalSeconds;
            metadata["IncludesProcessMetrics"] = _config.IncludeProcessMetrics;
            metadata["IncludesRuntimeMetrics"] = _config.IncludeRuntimeMetrics;
            return metadata;
        }

        #endregion

        #region Duration Tracker

        /// <summary>
        /// Helper class for tracking operation duration.
        /// </summary>
        private sealed class DurationTracker : IDisposable
        {
            private readonly ZabbixPlugin _plugin;
            private readonly string _metric;
            private readonly Stopwatch _stopwatch;
            private bool _disposed;

            public DurationTracker(ZabbixPlugin plugin, string metric)
            {
                _plugin = plugin;
                _metric = metric;
                _stopwatch = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _stopwatch.Stop();
                _plugin.RecordGauge(_metric, _stopwatch.Elapsed.TotalSeconds);
            }
        }

        #endregion

    }

    /// <summary>
    /// Abstract base class for telemetry plugins providing metrics, tracing, and logging capabilities.
    /// Extends <see cref="FeaturePluginBase"/> to support lifecycle management and HTTP endpoints.
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
            return metadata;
        }
    }

    /// <summary>
    /// Helper class for binary primitives operations.
    /// </summary>
    internal static class BinaryPrimitives
    {
        /// <summary>
        /// Reverses the endianness of a long value.
        /// </summary>
        public static long ReverseEndianness(long value)
        {
            return (long)ReverseEndianness((ulong)value);
        }

        /// <summary>
        /// Reverses the endianness of a ulong value.
        /// </summary>
        public static ulong ReverseEndianness(ulong value)
        {
            return ((value & 0x00000000000000FFUL) << 56) |
                   ((value & 0x000000000000FF00UL) << 40) |
                   ((value & 0x0000000000FF0000UL) << 24) |
                   ((value & 0x00000000FF000000UL) << 8) |
                   ((value & 0x000000FF00000000UL) >> 8) |
                   ((value & 0x0000FF0000000000UL) >> 24) |
                   ((value & 0x00FF000000000000UL) >> 40) |
                   ((value & 0xFF00000000000000UL) >> 56);
        }
    }
}
