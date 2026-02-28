using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.SDK.Security.Siem
{
    /// <summary>
    /// Bridges DataWarehouse security events to external SIEM systems.
    /// Subscribes to message bus security topics, converts events to SIEM format,
    /// batches them, and delivers via registered transports (syslog, HTTP, file).
    /// Thread-safe using Channel for producer-consumer pattern.
    /// </summary>
    public sealed class SiemTransportBridge : IDisposable
    {
        private readonly IMessageBus _messageBus;
        private readonly ILogger _logger;
        private readonly SiemTransportOptions _options;
        private readonly BoundedDictionary<string, ISiemTransport> _transports = new BoundedDictionary<string, ISiemTransport>(1000);
        private readonly Channel<SiemEvent> _eventChannel;
        private readonly List<IDisposable> _subscriptions = new();
        private readonly CancellationTokenSource _cts = new();
        private Task? _processingTask;
        private bool _disposed;

        /// <summary>
        /// Topics to subscribe to for security event forwarding.
        /// </summary>
        private static readonly string[] SubscriptionPatterns = new[]
        {
            "security.event.*",
            "security.alert.*",
            "access.denied",
            "auth.failed"
        };

        /// <summary>
        /// Creates a new SIEM transport bridge.
        /// </summary>
        /// <param name="messageBus">Message bus to subscribe for security events.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="options">Transport configuration options.</param>
        public SiemTransportBridge(IMessageBus messageBus, ILogger logger, SiemTransportOptions? options = null)
        {
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options ?? new SiemTransportOptions();

            _eventChannel = Channel.CreateBounded<SiemEvent>(new BoundedChannelOptions(_options.MaxBufferSize)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
        }

        /// <summary>
        /// Number of registered transports.
        /// </summary>
        public int TransportCount => _transports.Count;

        /// <summary>
        /// Registers a SIEM transport for event delivery.
        /// Multiple transports can be registered for fan-out delivery.
        /// </summary>
        public void RegisterTransport(ISiemTransport transport)
        {
            ArgumentNullException.ThrowIfNull(transport);
            if (!_transports.TryAdd(transport.TransportId, transport))
            {
                throw new InvalidOperationException($"Transport '{transport.TransportId}' is already registered.");
            }
            _logger.LogInformation("Registered SIEM transport: {TransportId} ({TransportName})", transport.TransportId, transport.TransportName);
        }

        /// <summary>
        /// Removes a SIEM transport.
        /// </summary>
        public bool RemoveTransport(string transportId)
        {
            return _transports.TryRemove(transportId, out _);
        }

        /// <summary>
        /// Starts the bridge: subscribes to message bus topics and begins processing events.
        /// </summary>
        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SiemTransportBridge));

            // Subscribe to security event patterns on the message bus
            foreach (var pattern in SubscriptionPatterns)
            {
                var sub = _messageBus.SubscribePattern(pattern, OnSecurityEventReceived);
                _subscriptions.Add(sub);
            }

            // Start background processing of queued events
            _processingTask = Task.Run(() => ProcessEventsAsync(_cts.Token));

            _logger.LogInformation("SIEM transport bridge started with {Count} topic patterns", SubscriptionPatterns.Length);
        }

        /// <summary>
        /// Enqueues a security event directly (for programmatic use outside message bus).
        /// </summary>
        public bool EnqueueEvent(SiemEvent evt)
        {
            if (_eventChannel.Writer.TryWrite(evt))
            {
                return true;
            }

            _logger.LogWarning("SIEM event buffer full ({MaxSize}), oldest event dropped", _options.MaxBufferSize);
            return false;
        }

        /// <summary>
        /// Flushes all buffered events immediately.
        /// </summary>
        public async Task FlushAsync(CancellationToken ct = default)
        {
            var batch = new List<SiemEvent>();
            while (_eventChannel.Reader.TryRead(out var evt))
            {
                batch.Add(evt);
            }

            if (batch.Count > 0)
            {
                await DeliverBatchAsync(batch, ct);
            }
        }

        private Task OnSecurityEventReceived(PluginMessage message)
        {
            var siemEvent = ConvertToSiemEvent(message);
            EnqueueEvent(siemEvent);
            return Task.CompletedTask;
        }

        private SiemEvent ConvertToSiemEvent(PluginMessage message)
        {
            var severity = SiemSeverity.Info;
            if (message.Type.Contains("alert", StringComparison.OrdinalIgnoreCase) ||
                message.Type.Contains("critical", StringComparison.OrdinalIgnoreCase))
            {
                severity = SiemSeverity.Critical;
            }
            else if (message.Type.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                     message.Type.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                severity = SiemSeverity.High;
            }
            else if (message.Type.Contains("warning", StringComparison.OrdinalIgnoreCase))
            {
                severity = SiemSeverity.Medium;
            }

            var metadata = new Dictionary<string, string>
            {
                ["messageId"] = message.MessageId,
                ["messageType"] = message.Type,
                ["source"] = message.Source ?? "unknown"
            };

            // Copy relevant payload data into metadata
            if (message.Payload is Dictionary<string, object> payload)
            {
                foreach (var kv in payload)
                {
                    metadata[kv.Key] = kv.Value?.ToString() ?? string.Empty;
                }
            }

            return new SiemEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Severity = severity,
                Source = message.Source ?? "DataWarehouse",
                EventType = message.Type,
                Description = message.Payload?.ToString() ?? message.Type,
                Metadata = metadata,
                RawData = System.Text.Json.JsonSerializer.Serialize(new
                {
                    message.MessageId,
                    message.Type,
                    message.Source,
                    message.Payload,
                    message.Timestamp
                })
            };
        }

        private async Task ProcessEventsAsync(CancellationToken ct)
        {
            var batch = new List<SiemEvent>(_options.BatchSize);
            var flushTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.FlushIntervalMs));

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Read events until batch is full or flush interval elapses
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                    var flushTask = flushTimer.WaitForNextTickAsync(ct).AsTask();
                    var readTask = _eventChannel.Reader.ReadAsync(ct).AsTask();

                    var completed = await Task.WhenAny(readTask, flushTask);

                    if (completed == readTask && readTask.IsCompletedSuccessfully)
                    {
                        batch.Add(readTask.Result);

                        // Drain available events up to batch size
                        while (batch.Count < _options.BatchSize && _eventChannel.Reader.TryRead(out var extra))
                        {
                            batch.Add(extra);
                        }
                    }

                    // Flush if batch is full or timer expired
                    if (batch.Count >= _options.BatchSize || (completed == flushTask && batch.Count > 0))
                    {
                        await DeliverBatchAsync(batch, ct);
                        batch.Clear();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Graceful shutdown — flush remaining events
                while (_eventChannel.Reader.TryRead(out var evt))
                {
                    batch.Add(evt);
                }
                if (batch.Count > 0)
                {
                    await DeliverBatchAsync(batch, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SIEM transport bridge processing loop failed");
            }
        }

        private async Task DeliverBatchAsync(IReadOnlyList<SiemEvent> events, CancellationToken ct)
        {
            // Fan-out to all registered transports
            var tasks = _transports.Values.Select(transport =>
                DeliverToTransportAsync(transport, events, ct));

            await Task.WhenAll(tasks);
        }

        private async Task DeliverToTransportAsync(ISiemTransport transport, IReadOnlyList<SiemEvent> events, CancellationToken ct)
        {
            for (int attempt = 0; attempt <= _options.MaxRetries; attempt++)
            {
                try
                {
                    await transport.SendBatchAsync(events, ct);
                    return;
                }
                catch (Exception ex) when (attempt < _options.MaxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 1s, 2s, 4s
                    _logger.LogWarning(ex, "SIEM transport {TransportId} delivery failed (attempt {Attempt}/{Max}), retrying in {Delay}s",
                        transport.TransportId, attempt + 1, _options.MaxRetries, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SIEM transport {TransportId} delivery failed after {Max} retries, {Count} events lost",
                        transport.TransportId, _options.MaxRetries, events.Count);
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cts.Cancel();
            _eventChannel.Writer.TryComplete();

            foreach (var sub in _subscriptions)
            {
                sub.Dispose();
            }
            _subscriptions.Clear();

            if (_processingTask != null)
            {
                try { _processingTask.Wait(TimeSpan.FromSeconds(5)); }
                catch (AggregateException) { /* Processing may throw on cancellation */ }
            }
            _cts.Dispose();
        }
    }

    /// <summary>
    /// SIEM transport that sends events via UDP or TCP syslog (RFC 5424 format).
    /// Suitable for traditional SIEM systems and log aggregators.
    /// </summary>
    public sealed class SyslogSiemTransport : ISiemTransport, IDisposable
    {
        private readonly SiemTransportOptions _options;
        private readonly ILogger _logger;
        private readonly string _hostname;
        private UdpClient? _udpClient;
        private TcpClient? _tcpClient;
        private NetworkStream? _tcpStream;
        private readonly SemaphoreSlim _connectLock = new(1, 1);
        private int _consecutiveFailures;
        private DateTimeOffset _circuitOpenUntil = DateTimeOffset.MinValue;

        /// <inheritdoc />
        public string TransportId { get; }

        /// <inheritdoc />
        public string TransportName => "Syslog (RFC 5424)";

        /// <summary>
        /// Whether to use TCP instead of UDP. Default: false (UDP).
        /// </summary>
        public bool UseTcp { get; init; }

        public SyslogSiemTransport(SiemTransportOptions options, ILogger logger, string? transportId = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            TransportId = transportId ?? $"syslog-{Guid.NewGuid():N}";
            _hostname = Environment.MachineName;
        }

        /// <inheritdoc />
        public async Task SendEventAsync(SiemEvent evt, CancellationToken ct = default)
        {
            await SendBatchAsync(new[] { evt }, ct);
        }

        /// <inheritdoc />
        public async Task SendBatchAsync(IReadOnlyList<SiemEvent> events, CancellationToken ct = default)
        {
            if (IsCircuitOpen())
            {
                throw new InvalidOperationException($"Circuit breaker is open for transport '{TransportId}' until {_circuitOpenUntil:o}");
            }

            try
            {
                var endpoint = _options.Endpoint
                    ?? throw new InvalidOperationException("Syslog endpoint not configured");

                foreach (var evt in events)
                {
                    var syslogMessage = evt.ToSyslog(_hostname);
                    var bytes = Encoding.UTF8.GetBytes(syslogMessage + "\n");

                    if (UseTcp)
                    {
                        await SendViaTcpAsync(endpoint, bytes, ct);
                    }
                    else
                    {
                        await SendViaUdpAsync(endpoint, bytes, ct);
                    }
                }

                Interlocked.Exchange(ref _consecutiveFailures, 0);
            }
            catch (Exception ex)
            {
                RecordFailure();
                throw new InvalidOperationException($"Syslog delivery failed: {ex.Message}", ex);
            }
        }

        /// <inheritdoc />
        public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
        {
            try
            {
                var endpoint = _options.Endpoint;
                if (endpoint == null) return false;

                if (UseTcp)
                {
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(endpoint.Host, endpoint.Port, ct);
                    return true;
                }
                else
                {
                    using var udp = new UdpClient();
                    udp.Connect(endpoint.Host, endpoint.Port);
                    var testMsg = Encoding.UTF8.GetBytes("<14>1 - - DataWarehouse - - - SIEM connectivity test");
                    await udp.SendAsync(testMsg, ct);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private async Task SendViaUdpAsync(Uri endpoint, byte[] data, CancellationToken ct)
        {
            var client = Volatile.Read(ref _udpClient);
            if (client == null)
            {
                await _connectLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    client = _udpClient;
                    if (client == null)
                    {
                        client = new UdpClient();
                        client.Connect(endpoint.Host, endpoint.Port);
                        Volatile.Write(ref _udpClient, client);
                    }
                }
                finally { _connectLock.Release(); }
            }
            await client.SendAsync(data, ct).ConfigureAwait(false);
        }

        private async Task SendViaTcpAsync(Uri endpoint, byte[] data, CancellationToken ct)
        {
            await _connectLock.WaitAsync(ct);
            try
            {
                if (_tcpClient == null || !_tcpClient.Connected)
                {
                    _tcpClient?.Dispose();
                    _tcpClient = new TcpClient();
                    await _tcpClient.ConnectAsync(endpoint.Host, endpoint.Port, ct);
                    _tcpStream = _tcpClient.GetStream();
                }
                await _tcpStream!.WriteAsync(data, ct);
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private bool IsCircuitOpen()
        {
            if (_circuitOpenUntil > DateTimeOffset.UtcNow) return true;
            if (_circuitOpenUntil != DateTimeOffset.MinValue)
            {
                _circuitOpenUntil = DateTimeOffset.MinValue;
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                _logger.LogInformation("Circuit breaker closed for syslog transport {TransportId}", TransportId);
            }
            return false;
        }

        private void RecordFailure()
        {
            var failures = Interlocked.Increment(ref _consecutiveFailures);
            if (failures >= _options.CircuitBreakerThreshold)
            {
                _circuitOpenUntil = DateTimeOffset.UtcNow.AddMilliseconds(_options.CircuitBreakerDurationMs);
                _logger.LogError("Circuit breaker opened for syslog transport {TransportId} after {Failures} consecutive failures, open until {Until:o}",
                    TransportId, failures, _circuitOpenUntil);
            }
        }

        public void Dispose()
        {
            _udpClient?.Dispose();
            _tcpStream?.Dispose();
            _tcpClient?.Dispose();
            _connectLock.Dispose();
        }
    }

    /// <summary>
    /// SIEM transport that sends events via HTTPS POST.
    /// Compatible with Splunk HEC, Azure Sentinel, QRadar, and generic webhooks.
    /// </summary>
    public sealed class HttpSiemTransport : ISiemTransport, IDisposable
    {
        private readonly SiemTransportOptions _options;
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private int _consecutiveFailures;
        private DateTimeOffset _circuitOpenUntil = DateTimeOffset.MinValue;

        /// <inheritdoc />
        public string TransportId { get; }

        /// <inheritdoc />
        public string TransportName => "HTTP/HTTPS (Splunk HEC / Webhook)";

        public HttpSiemTransport(SiemTransportOptions options, ILogger logger, HttpClient? httpClient = null, string? transportId = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient ?? new HttpClient();
            TransportId = transportId ?? $"http-{Guid.NewGuid():N}";

            // Set default authorization header if token provided
            if (!string.IsNullOrEmpty(_options.AuthToken))
            {
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Splunk {_options.AuthToken}");
            }

            // Add custom headers
            foreach (var header in _options.CustomHeaders)
            {
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        /// <inheritdoc />
        public async Task SendEventAsync(SiemEvent evt, CancellationToken ct = default)
        {
            await SendBatchAsync(new[] { evt }, ct);
        }

        /// <inheritdoc />
        public async Task SendBatchAsync(IReadOnlyList<SiemEvent> events, CancellationToken ct = default)
        {
            if (IsCircuitOpen())
            {
                throw new InvalidOperationException($"Circuit breaker is open for transport '{TransportId}' until {_circuitOpenUntil:o}");
            }

            var endpoint = _options.Endpoint
                ?? throw new InvalidOperationException("HTTP endpoint not configured");

            try
            {
                // Format as NDJSON (newline-delimited JSON) for Splunk HEC compatibility
                var sb = new StringBuilder();
                foreach (var evt in events)
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        time = evt.Timestamp.ToUnixTimeSeconds(),
                        host = evt.Source,
                        sourcetype = "datawarehouse:security",
                        @event = new
                        {
                            evt.EventId,
                            evt.Severity,
                            evt.EventType,
                            evt.Description,
                            evt.Metadata,
                            cef = evt.ToCef()
                        }
                    });
                    sb.AppendLine(json);
                }

                using var content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(endpoint, content, ct);
                response.EnsureSuccessStatusCode();

                Interlocked.Exchange(ref _consecutiveFailures, 0);
            }
            catch (Exception ex)
            {
                RecordFailure();
                throw new InvalidOperationException($"HTTP SIEM delivery failed: {ex.Message}", ex);
            }
        }

        /// <inheritdoc />
        public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
        {
            try
            {
                var endpoint = _options.Endpoint;
                if (endpoint == null) return false;

                using var response = await _httpClient.GetAsync(endpoint, ct);
                return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed;
            }
            catch
            {
                return false;
            }
        }

        private bool IsCircuitOpen()
        {
            if (_circuitOpenUntil > DateTimeOffset.UtcNow) return true;
            if (_circuitOpenUntil != DateTimeOffset.MinValue)
            {
                _circuitOpenUntil = DateTimeOffset.MinValue;
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                _logger.LogInformation("Circuit breaker closed for HTTP transport {TransportId}", TransportId);
            }
            return false;
        }

        private void RecordFailure()
        {
            var failures = Interlocked.Increment(ref _consecutiveFailures);
            if (failures >= _options.CircuitBreakerThreshold)
            {
                _circuitOpenUntil = DateTimeOffset.UtcNow.AddMilliseconds(_options.CircuitBreakerDurationMs);
                _logger.LogError("Circuit breaker opened for HTTP transport {TransportId} after {Failures} consecutive failures",
                    TransportId, failures);
            }
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>
    /// SIEM transport that writes events to local files.
    /// Designed for air-gapped environments where network SIEM delivery is not possible.
    /// Events are written as CEF-formatted lines for later collection.
    /// </summary>
    public sealed class FileSiemTransport : ISiemTransport
    {
        private readonly SiemTransportOptions _options;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        /// <inheritdoc />
        public string TransportId { get; }

        /// <inheritdoc />
        public string TransportName => "File (Air-gapped)";

        public FileSiemTransport(SiemTransportOptions options, ILogger logger, string? transportId = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            TransportId = transportId ?? $"file-{Guid.NewGuid():N}";
        }

        /// <inheritdoc />
        public async Task SendEventAsync(SiemEvent evt, CancellationToken ct = default)
        {
            await SendBatchAsync(new[] { evt }, ct);
        }

        /// <inheritdoc />
        public async Task SendBatchAsync(IReadOnlyList<SiemEvent> events, CancellationToken ct = default)
        {
            var filePath = _options.FilePath
                ?? throw new InvalidOperationException("File path not configured for file SIEM transport");

            await _writeLock.WaitAsync(ct);
            try
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var sb = new StringBuilder();
                foreach (var evt in events)
                {
                    sb.AppendLine(evt.ToCef());
                }

                await File.AppendAllTextAsync(filePath, sb.ToString(), ct);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <inheritdoc />
        public Task<bool> TestConnectionAsync(CancellationToken ct = default)
        {
            try
            {
                var filePath = _options.FilePath;
                if (string.IsNullOrEmpty(filePath)) return Task.FromResult(false);

                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Test write access
                using var fs = File.Open(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                return Task.FromResult(true);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }
    }
}
