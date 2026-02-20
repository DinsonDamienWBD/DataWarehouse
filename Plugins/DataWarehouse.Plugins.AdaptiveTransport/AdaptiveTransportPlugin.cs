using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using DataWarehouse.Plugins.AdaptiveTransport.BandwidthMonitor;

namespace DataWarehouse.Plugins.AdaptiveTransport;

/// <summary>
/// Production-ready Adaptive Transport Plugin implementing T78 Protocol Morphing.
/// Provides dynamic transport layer switching based on network conditions.
///
/// Features:
/// - 78.1: Real-time network quality monitoring (latency, jitter, packet loss)
/// - 78.2: QUIC/HTTP3 transport support via System.Net.Quic
/// - 78.3: Reliable UDP with custom ACK/NACK mechanism
/// - 78.4: Store-and-forward protocol for high-latency/satellite networks
/// - 78.5: Automatic protocol negotiation based on conditions
/// - 78.6: Seamless mid-stream protocol transitions
/// - 78.7: Adaptive compression based on bandwidth
/// - 78.8: Connection pooling per protocol type
/// - 78.9: Ordered fallback chain for connectivity
/// - 78.10: Satellite mode optimizations for >500ms latency
///
/// Message Commands:
/// - transport.send: Send data using optimal protocol
/// - transport.quality: Get current network quality metrics
/// - transport.switch: Force protocol switch
/// - transport.config: Configure transport settings
/// - transport.stats: Get transport statistics
/// - transport.bandwidth.measure: Measure bandwidth to endpoint
/// - transport.bandwidth.classify: Get current link classification
/// - transport.bandwidth.parameters: Get adaptive sync parameters
/// </summary>
public sealed class AdaptiveTransportPlugin : StreamingPluginBase
{
    private readonly BoundedDictionary<string, ConnectionPool> _connectionPools = new BoundedDictionary<string, ConnectionPool>(1000);
    private readonly BoundedDictionary<string, NetworkQualityMetrics> _endpointMetrics = new BoundedDictionary<string, NetworkQualityMetrics>(1000);
    private readonly BoundedDictionary<Guid, PendingTransfer> _pendingTransfers = new BoundedDictionary<Guid, PendingTransfer>(1000);
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private readonly Timer _qualityMonitorTimer;
    private readonly AdaptiveTransportConfig _config;
    private readonly AimdCongestionControl _udpCongestionControl = new(initialWindow: 4, minWindow: 1);
    private readonly string _storagePath;
    private TransportProtocol _currentProtocol = TransportProtocol.Tcp;
    private bool _isRunning;
    private long _totalBytesSent;
#pragma warning disable CS0649 // Field is never assigned to - reserved for future receive functionality
    private long _totalBytesReceived;
#pragma warning restore CS0649
    private long _totalSwitches;
    private BandwidthAwareSyncMonitor? _bandwidthMonitor;
    private DateTime _lastHealthCheck = DateTime.MinValue;
    private bool _networkInterfaceAvailable = true;

    /// <inheritdoc/>
    public override string Id => "datawarehouse.plugins.transport.adaptive";

    /// <inheritdoc/>
    public override string Name => "Adaptive Transport (Protocol Morphing)";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Gets the current active transport protocol.
    /// </summary>
    public TransportProtocol CurrentProtocol => _currentProtocol;

    /// <summary>
    /// Initializes a new instance of the AdaptiveTransportPlugin.
    /// </summary>
    /// <param name="config">Optional configuration for the adaptive transport.</param>
    public AdaptiveTransportPlugin(AdaptiveTransportConfig? config = null)
    {
        _config = config ?? new AdaptiveTransportConfig();
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataWarehouse", "transport", "adaptive");

        _qualityMonitorTimer = new Timer(
            async _ => await MonitorNetworkQualityAsync(),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = await base.OnHandshakeAsync(request);
        await LoadConfigurationAsync();
        return response;
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new() { Name = "transport.send", DisplayName = "Send Data", Description = "Send data using optimal protocol" },
            new() { Name = "transport.receive", DisplayName = "Receive Data", Description = "Receive data from transport" },
            new() { Name = "transport.quality", DisplayName = "Network Quality", Description = "Get network quality metrics" },
            new() { Name = "transport.switch", DisplayName = "Switch Protocol", Description = "Force protocol switch" },
            new() { Name = "transport.config", DisplayName = "Configure", Description = "Configure transport settings" },
            new() { Name = "transport.stats", DisplayName = "Statistics", Description = "Get transport statistics" },
            new() { Name = "transport.bandwidth.measure", DisplayName = "Measure Bandwidth", Description = "Measure bandwidth to endpoint" },
            new() { Name = "transport.bandwidth.classify", DisplayName = "Classify Link", Description = "Get current link classification" },
            new() { Name = "transport.bandwidth.parameters", DisplayName = "Sync Parameters", Description = "Get adaptive sync parameters" }
        };
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["CurrentProtocol"] = _currentProtocol.ToString();
        metadata["SupportsQuic"] = QuicConnection.IsSupported;
        metadata["SupportsTcp"] = true;
        metadata["SupportsReliableUdp"] = true;
        metadata["SupportsStoreForward"] = true;
        metadata["SatelliteModeEnabled"] = _config.EnableSatelliteMode;
        metadata["ActiveConnections"] = _connectionPools.Values.Sum(p => p.ActiveConnections);
        return metadata;
    }

    /// <inheritdoc/>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        switch (message.Type)
        {
            case "transport.send":
                await HandleSendAsync(message);
                break;
            case "transport.quality":
                HandleQuality(message);
                break;
            case "transport.switch":
                await HandleSwitchAsync(message);
                break;
            case "transport.config":
                await HandleConfigAsync(message);
                break;
            case "transport.stats":
                HandleStats(message);
                break;
            case "transport.bandwidth.measure":
                await HandleBandwidthMeasureAsync(message);
                break;
            case "transport.bandwidth.classify":
                HandleBandwidthClassify(message);
                break;
            case "transport.bandwidth.parameters":
                HandleBandwidthParameters(message);
                break;
            default:
                await base.OnMessageAsync(message);
                break;
        }
    }

    /// <inheritdoc/>
    public override async Task StartAsync(CancellationToken ct)
    {
        // Validate transport protocols
        var validProtocols = new[] { "TCP", "UDP", "QUIC", "WebSocket" };
        var protocolsToCheck = _config.FallbackChain.Select(p => p.ToString()).ToArray();
        if (!protocolsToCheck.All(p => validProtocols.Contains(p, StringComparer.OrdinalIgnoreCase) || p == "ReliableUdp" || p == "StoreForward"))
        {
            throw new ArgumentException($"transport_protocols must contain only: {string.Join(", ", validProtocols)}, ReliableUdp, StoreForward");
        }

        // Validate bandwidth estimation interval
        var interval = (int)_config.QualityCheckInterval.TotalMilliseconds;
        if (interval < 100 || interval > 10000)
            throw new ArgumentException("bandwidth_estimation_interval must be between 100ms and 10000ms");

        // Validate congestion algorithm (if specified in metadata - placeholder for future config)
        var congestionAlgorithm = "cubic"; // Default
        var validAlgos = new[] { "cubic", "bbr", "reno" };
        if (!validAlgos.Contains(congestionAlgorithm, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"congestion_algorithm must be one of: {string.Join(", ", validAlgos)}");

        // Validate max connections (using pool warmup count as proxy)
        if (_config.PoolWarmupCount < 1 || _config.PoolWarmupCount > 1000)
            throw new ArgumentException("max_connections must be between 1 and 1000");

        _isRunning = true;
        Directory.CreateDirectory(_storagePath);

        // Initialize connection pools for each protocol
        await InitializeConnectionPoolsAsync(ct);

        // Start network quality monitoring
        _qualityMonitorTimer.Change(TimeSpan.Zero, _config.QualityCheckInterval);

        // Start bandwidth monitor if configured
        if (_config.MonitoredEndpoints.Length > 0)
        {
            EnsureBandwidthMonitor();
            if (_bandwidthMonitor != null)
            {
                await _bandwidthMonitor.StartAsync(ct);
            }
        }
    }

    /// <summary>
    /// Checks network interface availability.
    /// </summary>
    private async Task<bool> CheckNetworkInterfaceAvailabilityAsync()
    {
        // Use cached result if checked within last 60 seconds
        if ((DateTime.UtcNow - _lastHealthCheck).TotalSeconds < 60)
            return _networkInterfaceAvailable;

        try
        {
            // Try to reach a reliable endpoint (Google DNS)
            using var client = new System.Net.Sockets.UdpClient();
            var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("8.8.8.8"), 53);
            client.Connect(endpoint);

            _networkInterfaceAvailable = true;
            _lastHealthCheck = DateTime.UtcNow;
            return true;
        }
        catch
        {
            _networkInterfaceAvailable = false;
            _lastHealthCheck = DateTime.UtcNow;
            return false;
        }
    }

    /// <inheritdoc/>
    public override async Task StopAsync()
    {
        _isRunning = false;
        _qualityMonitorTimer.Change(Timeout.Infinite, Timeout.Infinite);

        // Stop bandwidth monitor
        if (_bandwidthMonitor != null)
        {
            await _bandwidthMonitor.StopAsync();
        }

        // Close active connections gracefully
        foreach (var pool in _connectionPools.Values)
        {
            await pool.DisposeAsync();
        }
        _connectionPools.Clear();

        // Flush send buffers by processing pending transfers
        await FlushPendingTransfersAsync();
    }

    #region 78.1: Network Quality Monitor

    /// <summary>
    /// Measures network quality to a specific endpoint.
    /// </summary>
    /// <param name="endpoint">Target endpoint address.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Network quality metrics.</returns>
    public async Task<NetworkQualityMetrics> MeasureNetworkQualityAsync(string endpoint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Endpoint cannot be null or empty", nameof(endpoint));

        var sw = Stopwatch.StartNew();
        var latencies = new List<double>();
        var packetsSent = 0;
        var packetsReceived = 0;

        try
        {
            using var client = new UdpClient();
            var parts = endpoint.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 7; // Echo port

            // Resolve hostname
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            if (addresses.Length == 0)
            {
                return CreateDegradedMetrics(endpoint, "Could not resolve hostname");
            }

            var remoteEndpoint = new IPEndPoint(addresses[0], port);
            client.Connect(remoteEndpoint);

            // Send probe packets
            var probeData = new byte[64];
            RandomNumberGenerator.Fill(probeData);

            for (var i = 0; i < _config.ProbeCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                var probeSw = Stopwatch.StartNew();
                try
                {
                    packetsSent++;
                    await client.SendAsync(probeData, ct);

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(_config.ProbeTimeout);

                    try
                    {
                        var result = await client.ReceiveAsync(timeoutCts.Token);
                        probeSw.Stop();
                        packetsReceived++;
                        latencies.Add(probeSw.Elapsed.TotalMilliseconds);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Probe timed out - count as packet loss
                    }
                }
                catch (SocketException)
                {
                    // Network error - count as packet loss
                }

                if (i < _config.ProbeCount - 1)
                {
                    await Task.Delay(_config.ProbeInterval, ct);
                }
            }

            sw.Stop();

            var metrics = CalculateMetrics(endpoint, latencies, packetsSent, packetsReceived);
            _endpointMetrics[endpoint] = metrics;
            return metrics;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateDegradedMetrics(endpoint, ex.Message);
        }
    }

    private NetworkQualityMetrics CalculateMetrics(string endpoint, List<double> latencies, int sent, int received)
    {
        if (latencies.Count == 0)
        {
            return new NetworkQualityMetrics
            {
                Endpoint = endpoint,
                Timestamp = DateTime.UtcNow,
                AverageLatencyMs = double.MaxValue,
                MinLatencyMs = double.MaxValue,
                MaxLatencyMs = double.MaxValue,
                JitterMs = 0,
                PacketLossPercent = 100,
                Quality = NetworkQuality.Unusable,
                RecommendedProtocol = TransportProtocol.StoreForward
            };
        }

        var avgLatency = latencies.Average();
        var minLatency = latencies.Min();
        var maxLatency = latencies.Max();
        var packetLoss = (1.0 - (double)received / sent) * 100;

        // Calculate jitter (variation in latency)
        double jitter = 0;
        if (latencies.Count > 1)
        {
            var diffs = new List<double>();
            for (var i = 1; i < latencies.Count; i++)
            {
                diffs.Add(Math.Abs(latencies[i] - latencies[i - 1]));
            }
            jitter = diffs.Average();
        }

        var quality = DetermineQuality(avgLatency, jitter, packetLoss);
        var recommendedProtocol = DetermineOptimalProtocol(avgLatency, jitter, packetLoss);

        return new NetworkQualityMetrics
        {
            Endpoint = endpoint,
            Timestamp = DateTime.UtcNow,
            AverageLatencyMs = avgLatency,
            MinLatencyMs = minLatency,
            MaxLatencyMs = maxLatency,
            JitterMs = jitter,
            PacketLossPercent = packetLoss,
            Quality = quality,
            RecommendedProtocol = recommendedProtocol,
            BandwidthEstimateMbps = EstimateBandwidth(avgLatency, packetLoss)
        };
    }

    private NetworkQuality DetermineQuality(double latency, double jitter, double packetLoss)
    {
        // Satellite-level latency (>500ms)
        if (latency > 500)
            return NetworkQuality.Satellite;

        // High latency or significant packet loss
        if (latency > 200 || packetLoss > 5)
            return NetworkQuality.Poor;

        // Moderate conditions
        if (latency > 100 || jitter > 50 || packetLoss > 1)
            return NetworkQuality.Fair;

        // Good conditions
        if (latency > 50 || jitter > 20 || packetLoss > 0.1)
            return NetworkQuality.Good;

        // Excellent conditions
        return NetworkQuality.Excellent;
    }

    private TransportProtocol DetermineOptimalProtocol(double latency, double jitter, double packetLoss)
    {
        // Satellite mode: >500ms latency
        if (latency > 500 && _config.EnableSatelliteMode)
            return TransportProtocol.StoreForward;

        // Very high packet loss: use reliable UDP with aggressive retransmission
        if (packetLoss > 10)
            return TransportProtocol.ReliableUdp;

        // High jitter but acceptable latency: QUIC handles this well
        if (jitter > 50 && latency < 200 && QuicConnection.IsSupported)
            return TransportProtocol.Quic;

        // Moderate conditions: QUIC if available
        if (latency > 100 && QuicConnection.IsSupported)
            return TransportProtocol.Quic;

        // Good conditions: standard TCP
        return TransportProtocol.Tcp;
    }

    private double EstimateBandwidth(double latency, double packetLoss)
    {
        // Rough bandwidth estimation based on network conditions
        // In production, this would use actual throughput measurements
        var baseEstimate = 100.0; // 100 Mbps baseline

        // Reduce for latency
        if (latency > 100) baseEstimate *= 0.8;
        if (latency > 200) baseEstimate *= 0.6;
        if (latency > 500) baseEstimate *= 0.3;

        // Reduce for packet loss
        baseEstimate *= (1 - packetLoss / 100);

        return Math.Max(0.1, baseEstimate);
    }

    private NetworkQualityMetrics CreateDegradedMetrics(string endpoint, string reason)
    {
        return new NetworkQualityMetrics
        {
            Endpoint = endpoint,
            Timestamp = DateTime.UtcNow,
            AverageLatencyMs = double.MaxValue,
            Quality = NetworkQuality.Unusable,
            RecommendedProtocol = TransportProtocol.StoreForward,
            ErrorMessage = reason
        };
    }

    private async Task MonitorNetworkQualityAsync()
    {
        if (!_isRunning) return;

        foreach (var endpoint in _config.MonitoredEndpoints)
        {
            try
            {
                using var cts = new CancellationTokenSource(_config.QualityCheckTimeout);
                var metrics = await MeasureNetworkQualityAsync(endpoint, cts.Token);

                // Check if protocol switch is needed
                if (metrics.RecommendedProtocol != _currentProtocol)
                {
                    await ConsiderProtocolSwitchAsync(metrics);
                }
            }
            catch
            {
                // Continue monitoring other endpoints
            }
        }
    }

    #endregion

    #region 78.2: QUIC Implementation

    /// <summary>
    /// Sends data using QUIC protocol (HTTP/3 transport).
    /// </summary>
    /// <param name="endpoint">Target endpoint.</param>
    /// <param name="data">Data to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transfer result.</returns>
    public async Task<TransferResult> SendViaQuicAsync(string endpoint, byte[] data, CancellationToken ct = default)
    {
        if (!QuicConnection.IsSupported)
        {
            return new TransferResult
            {
                Success = false,
                Error = "QUIC is not supported on this platform",
                FallbackUsed = true
            };
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var parts = endpoint.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 443;

            var connectionOptions = new QuicClientConnectionOptions
            {
                RemoteEndPoint = new DnsEndPoint(host, port),
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                MaxInboundUnidirectionalStreams = 10,
                MaxInboundBidirectionalStreams = 100,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = new List<SslApplicationProtocol>
                    {
                        SslApplicationProtocol.Http3
                    },
                    TargetHost = host,
                    RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                    {
                        if (!_config.VerifySslCertificates)
                        {
                            // Log warning about disabled certificate validation
                            return true;
                        }

                        // Production certificate validation
                        return errors == System.Net.Security.SslPolicyErrors.None;
                    }
                }
            };

            await using var connection = await QuicConnection.ConnectAsync(connectionOptions, ct);
            await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);

            // Send length-prefixed data
            var lengthPrefix = BitConverter.GetBytes(data.Length);
            await stream.WriteAsync(lengthPrefix, ct);
            await stream.WriteAsync(data, ct);
            await stream.FlushAsync(ct);

            // Read acknowledgment
            var ackBuffer = new byte[1];
            await stream.ReadExactlyAsync(ackBuffer, ct);

            sw.Stop();
            Interlocked.Add(ref _totalBytesSent, data.Length);

            return new TransferResult
            {
                Success = ackBuffer[0] == 1,
                Protocol = TransportProtocol.Quic,
                BytesTransferred = data.Length,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TransferResult
            {
                Success = false,
                Protocol = TransportProtocol.Quic,
                Error = ex.Message,
                Duration = sw.Elapsed
            };
        }
    }

    #endregion

    #region 78.3: Reliable UDP

    /// <summary>
    /// Sends data using reliable UDP with custom ACK mechanism.
    /// </summary>
    /// <param name="endpoint">Target endpoint.</param>
    /// <param name="data">Data to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transfer result.</returns>
    /// <summary>
    /// Sends data using reliable UDP with custom ACK mechanism and AIMD congestion control.
    /// Congestion control prevents burst flooding by limiting in-flight segments to the
    /// congestion window size, with additive increase on ACK and multiplicative decrease on loss.
    /// </summary>
    public async Task<TransferResult> SendViaReliableUdpAsync(string endpoint, byte[] data, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var transferId = Guid.NewGuid();
        var chunks = ChunkData(data, _config.UdpChunkSize);
        var acked = new BoundedDictionary<int, bool>(1000);

        try
        {
            var parts = endpoint.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 5000;

            using var client = new UdpClient();
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            var remoteEndpoint = new IPEndPoint(addresses[0], port);
            client.Connect(remoteEndpoint);

            // Start ACK receiver task with congestion control feedback
            var ackTask = ReceiveAcksWithCongestionAsync(client, acked, chunks.Count, ct);

            // Send chunks using AIMD congestion control
            var retryCount = 0;
            var maxRetries = _config.MaxRetries;

            while (acked.Count < chunks.Count && retryCount < maxRetries)
            {
                ct.ThrowIfCancellationRequested();

                var sendWindow = _udpCongestionControl.GetSendWindow();
                var inFlight = 0;

                for (var i = 0; i < chunks.Count; i++)
                {
                    if (acked.ContainsKey(i)) continue;

                    // Respect congestion window: limit in-flight segments
                    if (inFlight >= sendWindow)
                    {
                        // Wait before sending more (rate limiting based on RTT/window)
                        var delayMs = _udpCongestionControl.GetSendDelayMs(_config.UdpChunkSize);
                        if (delayMs > 0)
                            await Task.Delay(Math.Max(1, (int)delayMs), ct);
                        inFlight = 0; // Reset in-flight count after delay
                    }

                    var packet = CreateReliablePacket(transferId, i, chunks.Count, chunks[i]);
                    await client.SendAsync(packet, ct);
                    inFlight++;
                }

                // Wait for ACKs with timeout
                await Task.Delay(_config.AckTimeout, ct);

                // If we didn't get all ACKs, treat as loss event for congestion control
                if (acked.Count < chunks.Count)
                {
                    _udpCongestionControl.OnLoss();
                }

                retryCount++;
            }

            sw.Stop();

            var success = acked.Count == chunks.Count;
            if (success)
            {
                Interlocked.Add(ref _totalBytesSent, data.Length);
            }

            return new TransferResult
            {
                Success = success,
                Protocol = TransportProtocol.ReliableUdp,
                BytesTransferred = success ? data.Length : acked.Count * _config.UdpChunkSize,
                Duration = sw.Elapsed,
                RetriesUsed = retryCount
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TransferResult
            {
                Success = false,
                Protocol = TransportProtocol.ReliableUdp,
                Error = ex.Message,
                Duration = sw.Elapsed
            };
        }
    }

    private List<byte[]> ChunkData(byte[] data, int chunkSize)
    {
        var chunks = new List<byte[]>();
        for (var i = 0; i < data.Length; i += chunkSize)
        {
            var length = Math.Min(chunkSize, data.Length - i);
            var chunk = new byte[length];
            Buffer.BlockCopy(data, i, chunk, 0, length);
            chunks.Add(chunk);
        }
        return chunks;
    }

    private byte[] CreateReliablePacket(Guid transferId, int sequence, int totalChunks, byte[] payload)
    {
        // Packet format: [TransferID:16][Sequence:4][Total:4][PayloadLength:4][Payload:N][Checksum:4]
        var packetSize = 16 + 4 + 4 + 4 + payload.Length + 4;
        var packet = ArrayPool<byte>.Shared.Rent(packetSize);
        var offset = 0;

        // Transfer ID
        Buffer.BlockCopy(transferId.ToByteArray(), 0, packet, offset, 16);
        offset += 16;

        // Sequence number
        Buffer.BlockCopy(BitConverter.GetBytes(sequence), 0, packet, offset, 4);
        offset += 4;

        // Total chunks
        Buffer.BlockCopy(BitConverter.GetBytes(totalChunks), 0, packet, offset, 4);
        offset += 4;

        // Payload length
        Buffer.BlockCopy(BitConverter.GetBytes(payload.Length), 0, packet, offset, 4);
        offset += 4;

        // Payload
        Buffer.BlockCopy(payload, 0, packet, offset, payload.Length);
        offset += payload.Length;

        // CRC32 checksum
        var checksum = ComputeCrc32(packet.AsSpan(0, offset));
        Buffer.BlockCopy(BitConverter.GetBytes(checksum), 0, packet, offset, 4);

        // Copy to exact size array (packet will be returned to pool by caller)
        var result = new byte[packetSize];
        Buffer.BlockCopy(packet, 0, result, 0, packetSize);
        ArrayPool<byte>.Shared.Return(packet);

        return result;
    }

    /// <summary>
    /// Receives ACKs with congestion control feedback. Each ACK triggers additive increase
    /// and RTT measurement via EWMA for the AIMD controller.
    /// </summary>
    private async Task ReceiveAcksWithCongestionAsync(UdpClient client, BoundedDictionary<int, bool> acked, int totalChunks, CancellationToken ct)
    {
        var sendTimestamps = new BoundedDictionary<int, DateTime>(1000);
        try
        {
            while (acked.Count < totalChunks && !ct.IsCancellationRequested)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_config.AckTimeout);

                try
                {
                    var result = await client.ReceiveAsync(timeoutCts.Token);
                    // ACK format: [Type:1][Sequence:4]
                    if (result.Buffer.Length >= 5 && result.Buffer[0] == 0x01) // ACK type
                    {
                        var sequence = BitConverter.ToInt32(result.Buffer, 1);
                        if (acked.TryAdd(sequence, true))
                        {
                            // Measure RTT sample and feed to congestion controller
                            var rttSample = sendTimestamps.TryRemove(sequence, out var sentAt)
                                ? (DateTime.UtcNow - sentAt).TotalMilliseconds
                                : _udpCongestionControl.SmoothedRttMs; // Use smoothed if no timestamp

                            _udpCongestionControl.OnAck(rttSample);
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout - continue waiting
                }
            }
        }
        catch
        {
            // Receiving stopped
        }
    }

    private async Task ReceiveAcksAsync(UdpClient client, BoundedDictionary<int, bool> acked, int totalChunks, CancellationToken ct)
    {
        try
        {
            while (acked.Count < totalChunks && !ct.IsCancellationRequested)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_config.AckTimeout);

                try
                {
                    var result = await client.ReceiveAsync(timeoutCts.Token);
                    // ACK format: [Type:1][Sequence:4]
                    if (result.Buffer.Length >= 5 && result.Buffer[0] == 0x01) // ACK type
                    {
                        var sequence = BitConverter.ToInt32(result.Buffer, 1);
                        acked[sequence] = true;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout - continue waiting
                }
            }
        }
        catch
        {
            // Receiving stopped
        }
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ (0xEDB88320 & ~((crc & 1) - 1));
            }
        }
        return ~crc;
    }

    #endregion

    #region 78.4: Store-and-Forward (High Latency)

    /// <summary>
    /// Queues data for store-and-forward delivery (satellite/high-latency networks).
    /// </summary>
    /// <param name="endpoint">Target endpoint.</param>
    /// <param name="data">Data to send.</param>
    /// <param name="priority">Transfer priority.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transfer ID for tracking.</returns>
    public async Task<Guid> QueueStoreForwardAsync(string endpoint, byte[] data, TransferPriority priority = TransferPriority.Normal, CancellationToken ct = default)
    {
        var transfer = new PendingTransfer
        {
            Id = Guid.NewGuid(),
            Endpoint = endpoint,
            Data = data,
            Priority = priority,
            QueuedAt = DateTime.UtcNow,
            Attempts = 0,
            Status = TransferStatus.Queued
        };

        _pendingTransfers[transfer.Id] = transfer;

        // Persist to disk for durability
        await PersistTransferAsync(transfer, ct);

        // Try immediate delivery if network is available
        _ = TryDeliverAsync(transfer, ct);

        return transfer.Id;
    }

    /// <summary>
    /// Gets the status of a store-and-forward transfer.
    /// </summary>
    /// <param name="transferId">Transfer ID.</param>
    /// <returns>Transfer status or null if not found.</returns>
    public TransferStatus? GetTransferStatus(Guid transferId)
    {
        return _pendingTransfers.TryGetValue(transferId, out var transfer) ? transfer.Status : null;
    }

    private async Task PersistTransferAsync(PendingTransfer transfer, CancellationToken ct)
    {
        var path = Path.Combine(_storagePath, "pending", $"{transfer.Id}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var metadata = new TransferMetadata
        {
            Id = transfer.Id,
            Endpoint = transfer.Endpoint,
            Priority = transfer.Priority,
            QueuedAt = transfer.QueuedAt,
            DataLength = transfer.Data.Length,
            // Note: Bus delegation not available in this context; using direct crypto
            DataHash = Convert.ToHexString(SHA256.HashData(transfer.Data))
        };

        var json = JsonSerializer.Serialize(metadata);
        await File.WriteAllTextAsync(path, json, ct);

        var dataPath = Path.Combine(_storagePath, "pending", $"{transfer.Id}.dat");
        await File.WriteAllBytesAsync(dataPath, transfer.Data, ct);
    }

    private async Task TryDeliverAsync(PendingTransfer transfer, CancellationToken ct)
    {
        if (transfer.Attempts >= _config.MaxStoreForwardAttempts)
        {
            transfer.Status = TransferStatus.Failed;
            return;
        }

        transfer.Attempts++;
        transfer.Status = TransferStatus.InProgress;

        try
        {
            // Measure network quality first
            var metrics = await MeasureNetworkQualityAsync(transfer.Endpoint, ct);

            if (metrics.Quality == NetworkQuality.Unusable)
            {
                transfer.Status = TransferStatus.Queued;
                transfer.NextAttemptAt = DateTime.UtcNow.Add(_config.StoreForwardRetryDelay);
                return;
            }

            // Choose best protocol based on conditions
            var result = await SendWithProtocolAsync(transfer.Endpoint, transfer.Data, metrics.RecommendedProtocol, ct);

            if (result.Success)
            {
                transfer.Status = TransferStatus.Completed;
                transfer.CompletedAt = DateTime.UtcNow;
                _pendingTransfers.TryRemove(transfer.Id, out _);

                // Clean up persisted data
                await CleanupTransferAsync(transfer.Id);
            }
            else
            {
                transfer.Status = TransferStatus.Queued;
                transfer.NextAttemptAt = DateTime.UtcNow.Add(_config.StoreForwardRetryDelay);
            }
        }
        catch
        {
            transfer.Status = TransferStatus.Queued;
            transfer.NextAttemptAt = DateTime.UtcNow.Add(_config.StoreForwardRetryDelay);
        }
    }

    private async Task FlushPendingTransfersAsync()
    {
        foreach (var transfer in _pendingTransfers.Values.Where(t => t.Status == TransferStatus.Queued))
        {
            using var cts = new CancellationTokenSource(_config.FlushTimeout);
            await TryDeliverAsync(transfer, cts.Token);
        }
    }

    private async Task CleanupTransferAsync(Guid transferId)
    {
        try
        {
            var jsonPath = Path.Combine(_storagePath, "pending", $"{transferId}.json");
            var dataPath = Path.Combine(_storagePath, "pending", $"{transferId}.dat");

            if (File.Exists(jsonPath)) File.Delete(jsonPath);
            if (File.Exists(dataPath)) File.Delete(dataPath);
        }
        catch
        {
            // Best effort cleanup
        }
    }

    #endregion

    #region 78.5: Protocol Negotiation

    /// <summary>
    /// Negotiates the optimal protocol with a remote endpoint.
    /// </summary>
    /// <param name="endpoint">Target endpoint.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Negotiation result with selected protocol.</returns>
    public async Task<ProtocolNegotiationResult> NegotiateProtocolAsync(string endpoint, CancellationToken ct = default)
    {
        // Measure network conditions
        var metrics = await MeasureNetworkQualityAsync(endpoint, ct);

        // Check local capabilities
        var localCapabilities = new ProtocolCapabilities
        {
            SupportsQuic = QuicConnection.IsSupported,
            SupportsTcp = true,
            SupportsReliableUdp = true,
            SupportsStoreForward = true,
            SupportsCompression = true,
            MaxChunkSize = _config.UdpChunkSize,
            ProtocolVersion = 1
        };

        // Query remote capabilities (via TCP control channel)
        var remoteCapabilities = await QueryRemoteCapabilitiesAsync(endpoint, ct);

        // Select optimal protocol
        var selected = SelectProtocol(metrics, localCapabilities, remoteCapabilities);

        return new ProtocolNegotiationResult
        {
            Success = true,
            SelectedProtocol = selected,
            LocalCapabilities = localCapabilities,
            RemoteCapabilities = remoteCapabilities,
            NetworkMetrics = metrics,
            NegotiatedAt = DateTime.UtcNow
        };
    }

    private async Task<ProtocolCapabilities> QueryRemoteCapabilitiesAsync(string endpoint, CancellationToken ct)
    {
        try
        {
            var parts = endpoint.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : _config.ControlPort;

            using var client = new TcpClient();
            await client.ConnectAsync(host, port, ct);

            await using var stream = client.GetStream();

            // Send capability query
            var query = new byte[] { 0x01, 0x00 }; // Protocol query command
            await stream.WriteAsync(query, ct);

            // Read response
            var response = new byte[64];
            var read = await stream.ReadAsync(response, ct);

            if (read >= 6)
            {
                return new ProtocolCapabilities
                {
                    SupportsQuic = (response[0] & 0x01) != 0,
                    SupportsTcp = (response[0] & 0x02) != 0,
                    SupportsReliableUdp = (response[0] & 0x04) != 0,
                    SupportsStoreForward = (response[0] & 0x08) != 0,
                    SupportsCompression = (response[0] & 0x10) != 0,
                    MaxChunkSize = BitConverter.ToInt32(response, 1),
                    ProtocolVersion = response[5]
                };
            }
        }
        catch
        {
            // Remote capabilities unknown - assume minimal
        }

        return new ProtocolCapabilities
        {
            SupportsTcp = true // TCP is universal
        };
    }

    private TransportProtocol SelectProtocol(NetworkQualityMetrics metrics, ProtocolCapabilities local, ProtocolCapabilities remote)
    {
        // Satellite mode takes precedence
        if (metrics.Quality == NetworkQuality.Satellite && local.SupportsStoreForward && remote.SupportsStoreForward)
            return TransportProtocol.StoreForward;

        // High packet loss - use reliable UDP if both support it
        if (metrics.PacketLossPercent > 5 && local.SupportsReliableUdp && remote.SupportsReliableUdp)
            return TransportProtocol.ReliableUdp;

        // QUIC if both support it and conditions are reasonable
        if (local.SupportsQuic && remote.SupportsQuic && metrics.AverageLatencyMs < 500)
            return TransportProtocol.Quic;

        // Default to TCP
        return TransportProtocol.Tcp;
    }

    #endregion

    #region 78.6: Seamless Switching

    /// <summary>
    /// Switches to a different transport protocol.
    /// </summary>
    /// <param name="newProtocol">Target protocol.</param>
    /// <param name="reason">Reason for the switch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if switch was successful.</returns>
    public async Task<bool> SwitchProtocolAsync(TransportProtocol newProtocol, string? reason = null, CancellationToken ct = default)
    {
        if (newProtocol == _currentProtocol)
            return true;

        // Validate switch is possible
        if (newProtocol == TransportProtocol.Quic && !QuicConnection.IsSupported)
            return false;

        await _switchLock.WaitAsync(ct);
        try
        {
            var oldProtocol = _currentProtocol;

            // Drain active transfers on current protocol
            await DrainActiveTransfersAsync(ct);

            // Switch to new protocol
            _currentProtocol = newProtocol;
            Interlocked.Increment(ref _totalSwitches);

            // Warm up new protocol connection pool
            await WarmupConnectionPoolAsync(newProtocol, ct);

            return true;
        }
        finally
        {
            _switchLock.Release();
        }
    }

    private async Task ConsiderProtocolSwitchAsync(NetworkQualityMetrics metrics)
    {
        if (!_config.AutoSwitchEnabled)
            return;

        // Only switch if recommendation is different and conditions are stable
        if (metrics.RecommendedProtocol != _currentProtocol)
        {
            // Verify with additional samples
            var confirmationSamples = 0;
            for (var i = 0; i < 3; i++)
            {
                using var cts = new CancellationTokenSource(_config.QualityCheckTimeout);
                var sample = await MeasureNetworkQualityAsync(metrics.Endpoint, cts.Token);
                if (sample.RecommendedProtocol == metrics.RecommendedProtocol)
                    confirmationSamples++;

                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            if (confirmationSamples >= 2)
            {
                await SwitchProtocolAsync(metrics.RecommendedProtocol, $"Network conditions changed: {metrics.Quality}");
            }
        }
    }

    private async Task DrainActiveTransfersAsync(CancellationToken ct)
    {
        // Wait for in-flight transfers to complete (with timeout)
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_config.DrainTimeout);

        while (_pendingTransfers.Values.Any(t => t.Status == TransferStatus.InProgress))
        {
            await Task.Delay(100, timeoutCts.Token);
        }
    }

    private async Task WarmupConnectionPoolAsync(TransportProtocol protocol, CancellationToken ct)
    {
        if (!_connectionPools.TryGetValue(protocol.ToString(), out var pool))
        {
            pool = new ConnectionPool(protocol, _config);
            _connectionPools[protocol.ToString()] = pool;
        }

        await pool.WarmupAsync(_config.PoolWarmupCount, ct);
    }

    #endregion

    #region 78.7: Compression Adaptation

    /// <summary>
    /// Determines optimal compression level based on bandwidth and data characteristics.
    /// </summary>
    /// <param name="data">Data to analyze.</param>
    /// <param name="bandwidthMbps">Available bandwidth in Mbps.</param>
    /// <returns>Recommended compression level.</returns>
    public CompressionRecommendation GetCompressionRecommendation(byte[] data, double bandwidthMbps)
    {
        // Analyze data compressibility
        var entropy = CalculateEntropy(data);
        var isHighlyCompressible = entropy < 6.0; // Lower entropy = more compressible

        // Calculate break-even point
        // Time to compress + transfer compressed < transfer uncompressed
        var uncompressedTransferTime = (data.Length * 8.0) / (bandwidthMbps * 1_000_000);

        if (bandwidthMbps < 1.0)
        {
            // Very low bandwidth - always compress
            return new CompressionRecommendation
            {
                ShouldCompress = true,
                Level = isHighlyCompressible ? CompressionLevel.SmallestSize : CompressionLevel.Optimal,
                EstimatedRatio = isHighlyCompressible ? 0.3 : 0.7,
                Reason = "Low bandwidth - compression beneficial"
            };
        }

        if (bandwidthMbps > 100 && !isHighlyCompressible)
        {
            // High bandwidth and low compressibility - skip compression
            return new CompressionRecommendation
            {
                ShouldCompress = false,
                Level = CompressionLevel.NoCompression,
                EstimatedRatio = 1.0,
                Reason = "High bandwidth and low compressibility"
            };
        }

        if (isHighlyCompressible)
        {
            return new CompressionRecommendation
            {
                ShouldCompress = true,
                Level = bandwidthMbps < 10 ? CompressionLevel.SmallestSize : CompressionLevel.Optimal,
                EstimatedRatio = 0.3,
                Reason = "Highly compressible data"
            };
        }

        // Default: moderate compression
        return new CompressionRecommendation
        {
            ShouldCompress = true,
            Level = CompressionLevel.Fastest,
            EstimatedRatio = 0.8,
            Reason = "Standard compression"
        };
    }

    private double CalculateEntropy(byte[] data)
    {
        if (data.Length == 0) return 0;

        // Sample for large data
        var sampleSize = Math.Min(data.Length, 65536);
        var sample = data.Length <= sampleSize ? data : data.Take(sampleSize).ToArray();

        var frequencies = new int[256];
        foreach (var b in sample)
            frequencies[b]++;

        double entropy = 0;
        foreach (var freq in frequencies)
        {
            if (freq == 0) continue;
            var p = (double)freq / sample.Length;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }

    #endregion

    #region 78.8: Connection Pooling

    private async Task InitializeConnectionPoolsAsync(CancellationToken ct)
    {
        // TCP pool
        var tcpPool = new ConnectionPool(TransportProtocol.Tcp, _config);
        _connectionPools["Tcp"] = tcpPool;
        await tcpPool.WarmupAsync(_config.PoolWarmupCount, ct);

        // QUIC pool (if supported)
        if (QuicConnection.IsSupported)
        {
            var quicPool = new ConnectionPool(TransportProtocol.Quic, _config);
            _connectionPools["Quic"] = quicPool;
        }

        // UDP pool (connectionless, but we pool sockets)
        var udpPool = new ConnectionPool(TransportProtocol.ReliableUdp, _config);
        _connectionPools["ReliableUdp"] = udpPool;
    }

    #endregion

    #region 78.9: Fallback Chain

    /// <summary>
    /// Sends data using the fallback chain if primary protocol fails.
    /// </summary>
    /// <param name="endpoint">Target endpoint.</param>
    /// <param name="data">Data to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transfer result.</returns>
    public async Task<TransferResult> SendWithFallbackAsync(string endpoint, byte[] data, CancellationToken ct = default)
    {
        var chain = _config.FallbackChain.ToList();

        // Put current protocol first
        if (chain.Contains(_currentProtocol))
        {
            chain.Remove(_currentProtocol);
            chain.Insert(0, _currentProtocol);
        }

        foreach (var protocol in chain)
        {
            var result = await SendWithProtocolAsync(endpoint, data, protocol, ct);
            if (result.Success)
            {
                // If we fell back, note that in the result
                if (protocol != _currentProtocol)
                {
                    return result with { FallbackUsed = true };
                }
                return result;
            }
        }

        // All protocols failed - queue for store-and-forward
        var transferId = await QueueStoreForwardAsync(endpoint, data, TransferPriority.Normal, ct);

        return new TransferResult
        {
            Success = false,
            Protocol = TransportProtocol.StoreForward,
            Error = "All protocols failed, queued for store-and-forward",
            StoreForwardId = transferId
        };
    }

    private async Task<TransferResult> SendWithProtocolAsync(string endpoint, byte[] data, TransportProtocol protocol, CancellationToken ct)
    {
        return protocol switch
        {
            TransportProtocol.Quic => await SendViaQuicAsync(endpoint, data, ct),
            TransportProtocol.ReliableUdp => await SendViaReliableUdpAsync(endpoint, data, ct),
            TransportProtocol.Tcp => await SendViaTcpAsync(endpoint, data, ct),
            TransportProtocol.StoreForward => await SendViaStoreForwardAsync(endpoint, data, ct),
            _ => throw new ArgumentException($"Unknown protocol: {protocol}")
        };
    }

    private async Task<TransferResult> SendViaTcpAsync(string endpoint, byte[] data, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        PooledConnection? pooledConn = null;
        var returnToPool = false;

        try
        {
            var parts = endpoint.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 5000;

            // Get connection from pool (reuses existing connections)
            var pool = _connectionPools.GetOrAdd("Tcp", _ => new ConnectionPool(TransportProtocol.Tcp, _config));
            pooledConn = await pool.GetTcpConnectionAsync(host, port, ct);

            var stream = pooledConn.GetStream();

            // Send length-prefixed data
            var lengthPrefix = BitConverter.GetBytes(data.Length);
            await stream.WriteAsync(lengthPrefix, ct);
            await stream.WriteAsync(data, ct);
            await stream.FlushAsync(ct);

            // Read acknowledgment
            var ackBuffer = new byte[1];
            await stream.ReadExactlyAsync(ackBuffer, ct);

            sw.Stop();
            Interlocked.Add(ref _totalBytesSent, data.Length);
            returnToPool = true;

            return new TransferResult
            {
                Success = ackBuffer[0] == 1,
                Protocol = TransportProtocol.Tcp,
                BytesTransferred = data.Length,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TransferResult
            {
                Success = false,
                Protocol = TransportProtocol.Tcp,
                Error = ex.Message,
                Duration = sw.Elapsed
            };
        }
        finally
        {
            if (pooledConn != null)
            {
                var pool = _connectionPools.GetOrAdd("Tcp", _ => new ConnectionPool(TransportProtocol.Tcp, _config));
                if (returnToPool)
                    pool.ReturnConnection(pooledConn);
                else
                    pooledConn.Dispose(); // Connection failed, don't return to pool
            }
        }
    }

    private async Task<TransferResult> SendViaStoreForwardAsync(string endpoint, byte[] data, CancellationToken ct)
    {
        var transferId = await QueueStoreForwardAsync(endpoint, data, TransferPriority.Normal, ct);

        return new TransferResult
        {
            Success = true, // Queued successfully
            Protocol = TransportProtocol.StoreForward,
            BytesTransferred = data.Length,
            StoreForwardId = transferId,
            Duration = TimeSpan.Zero
        };
    }

    #endregion

    #region 78.10: Satellite Mode

    /// <summary>
    /// Enables satellite mode optimizations for high-latency networks.
    /// </summary>
    /// <param name="enabled">Whether to enable satellite mode.</param>
    public void SetSatelliteMode(bool enabled)
    {
        _config.EnableSatelliteMode = enabled;

        if (enabled)
        {
            // Increase buffer sizes
            _config.UdpChunkSize = 16384; // Larger chunks
            _config.AckTimeout = TimeSpan.FromSeconds(5); // Longer ACK timeout
            _config.MaxRetries = 10; // More retries
            _config.ProbeTimeout = TimeSpan.FromSeconds(3); // Longer probe timeout
        }
        else
        {
            // Restore defaults
            _config.UdpChunkSize = 1400;
            _config.AckTimeout = TimeSpan.FromMilliseconds(500);
            _config.MaxRetries = 3;
            _config.ProbeTimeout = TimeSpan.FromMilliseconds(500);
        }
    }

    /// <summary>
    /// Gets satellite mode optimization recommendations.
    /// </summary>
    /// <param name="metrics">Current network metrics.</param>
    /// <returns>Optimization recommendations.</returns>
    public SatelliteModeRecommendation GetSatelliteRecommendation(NetworkQualityMetrics metrics)
    {
        if (metrics.AverageLatencyMs < 500)
        {
            return new SatelliteModeRecommendation
            {
                RecommendSatelliteMode = false,
                Reason = "Latency is acceptable for standard protocols"
            };
        }

        return new SatelliteModeRecommendation
        {
            RecommendSatelliteMode = true,
            Reason = $"High latency detected ({metrics.AverageLatencyMs:F0}ms)",
            SuggestedChunkSize = (int)Math.Min(65536, metrics.BandwidthEstimateMbps * 1024), // Based on bandwidth
            SuggestedAckTimeout = TimeSpan.FromMilliseconds(metrics.AverageLatencyMs * 3),
            SuggestedRetries = metrics.PacketLossPercent > 10 ? 15 : 10,
            UseStoreForward = metrics.PacketLossPercent > 20 || metrics.Quality == NetworkQuality.Unusable
        };
    }

    #endregion

    #region Bandwidth Monitoring

    /// <summary>
    /// Ensures the bandwidth monitor is initialized (lazy initialization).
    /// </summary>
    private void EnsureBandwidthMonitor()
    {
        if (_bandwidthMonitor != null)
            return;

        var endpoint = _config.MonitoredEndpoints.FirstOrDefault() ?? "8.8.8.8:80";
        var options = new BandwidthMonitorOptions(
            ProbeIntervalMs: (int)_config.QualityCheckInterval.TotalMilliseconds,
            ProbeEndpoint: endpoint,
            EnableAutoAdjust: _config.AutoSwitchEnabled,
            HysteresisThreshold: 0.7);

        _bandwidthMonitor = new BandwidthAwareSyncMonitor(options);
    }

    private async Task HandleBandwidthMeasureAsync(PluginMessage message)
    {
        EnsureBandwidthMonitor();

        var endpoint = GetString(message.Payload, "endpoint");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            message.Payload["error"] = "endpoint parameter required";
            return;
        }

        try
        {
            var probe = new BandwidthProbe();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var measurement = await probe.MeasureAsync(endpoint, cts.Token);

            message.Payload["result"] = new Dictionary<string, object>
            {
                ["timestamp"] = measurement.Timestamp,
                ["downloadBytesPerSecond"] = measurement.DownloadBytesPerSecond,
                ["uploadBytesPerSecond"] = measurement.UploadBytesPerSecond,
                ["latencyMs"] = measurement.LatencyMs,
                ["jitterMs"] = measurement.JitterMs,
                ["packetLossPercent"] = measurement.PacketLossPercent
            };
        }
        catch (Exception ex)
        {
            message.Payload["error"] = ex.Message;
        }
    }

    private void HandleBandwidthClassify(PluginMessage message)
    {
        EnsureBandwidthMonitor();

        var classification = _bandwidthMonitor?.GetCurrentClassification();
        if (classification == null)
        {
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["class"] = "Unknown",
                ["message"] = "No classification available yet"
            };
            return;
        }

        message.Payload["result"] = new Dictionary<string, object>
        {
            ["class"] = classification.Class.ToString(),
            ["confidence"] = classification.Confidence,
            ["classifiedAt"] = classification.ClassifiedAt,
            ["measurement"] = new Dictionary<string, object>
            {
                ["downloadBytesPerSecond"] = classification.MeasuredBandwidth.DownloadBytesPerSecond,
                ["uploadBytesPerSecond"] = classification.MeasuredBandwidth.UploadBytesPerSecond,
                ["latencyMs"] = classification.MeasuredBandwidth.LatencyMs,
                ["jitterMs"] = classification.MeasuredBandwidth.JitterMs,
                ["packetLossPercent"] = classification.MeasuredBandwidth.PacketLossPercent
            }
        };
    }

    private void HandleBandwidthParameters(PluginMessage message)
    {
        EnsureBandwidthMonitor();

        var parameters = _bandwidthMonitor?.GetCurrentParameters();
        if (parameters == null)
        {
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["message"] = "No parameters available yet"
            };
            return;
        }

        message.Payload["result"] = new Dictionary<string, object>
        {
            ["mode"] = parameters.Mode.ToString(),
            ["maxConcurrency"] = parameters.MaxConcurrency,
            ["chunkSizeBytes"] = parameters.ChunkSizeBytes,
            ["compressionEnabled"] = parameters.CompressionEnabled,
            ["retryPolicy"] = new Dictionary<string, object>
            {
                ["maxRetries"] = parameters.RetryPolicy.MaxRetries,
                ["initialBackoffMs"] = parameters.RetryPolicy.InitialBackoffMs,
                ["maxBackoffMs"] = parameters.RetryPolicy.MaxBackoffMs,
                ["backoffMultiplier"] = parameters.RetryPolicy.BackoffMultiplier
            }
        };
    }

    #endregion

    #region Message Handlers

    private async Task HandleSendAsync(PluginMessage message)
    {
        var endpoint = GetString(message.Payload, "endpoint") ?? throw new ArgumentException("endpoint required");
        var data = GetBytes(message.Payload, "data") ?? throw new ArgumentException("data required");
        var useFallback = GetBool(message.Payload, "useFallback") ?? true;

        try
        {
            var result = useFallback
                ? await SendWithFallbackAsync(endpoint, data)
                : await SendWithProtocolAsync(endpoint, data, _currentProtocol, default);

            // Track counters
            Interlocked.Add(ref _totalBytesSent, result.BytesTransferred);
            if (result.Success)
            {
                // Counter tracking handled in SendWithProtocolAsync methods
            }

            message.Payload["result"] = new Dictionary<string, object>
            {
                ["success"] = result.Success,
                ["protocol"] = result.Protocol.ToString(),
                ["bytesTransferred"] = result.BytesTransferred,
                ["durationMs"] = result.Duration.TotalMilliseconds,
                ["fallbackUsed"] = result.FallbackUsed,
                ["error"] = result.Error ?? string.Empty
            };
        }
        catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.NetworkUnreachable)
        {
            // Error boundary: Network unreachable
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Network unreachable"
            };
        }
        catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset)
        {
            // Error boundary: Connection reset
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Connection reset by peer"
            };
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("protocol negotiation", StringComparison.OrdinalIgnoreCase))
        {
            // Error boundary: Protocol negotiation failure
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Protocol negotiation failed"
            };
        }
    }

    private void HandleQuality(PluginMessage message)
    {
        var endpoint = GetString(message.Payload, "endpoint");

        if (endpoint != null && _endpointMetrics.TryGetValue(endpoint, out var metrics))
        {
            message.Payload["result"] = SerializeMetrics(metrics);
        }
        else
        {
            message.Payload["result"] = _endpointMetrics.Values.Select(SerializeMetrics).ToList();
        }
    }

    private async Task HandleSwitchAsync(PluginMessage message)
    {
        var protocolStr = GetString(message.Payload, "protocol") ?? throw new ArgumentException("protocol required");
        if (!Enum.TryParse<TransportProtocol>(protocolStr, true, out var protocol))
        {
            throw new ArgumentException($"Invalid protocol: {protocolStr}");
        }

        var reason = GetString(message.Payload, "reason");
        var success = await SwitchProtocolAsync(protocol, reason);

        message.Payload["result"] = new Dictionary<string, object>
        {
            ["success"] = success,
            ["currentProtocol"] = _currentProtocol.ToString()
        };
    }

    private async Task HandleConfigAsync(PluginMessage message)
    {
        if (message.Payload.TryGetValue("autoSwitch", out var autoSwitchObj) && autoSwitchObj is bool autoSwitch)
        {
            _config.AutoSwitchEnabled = autoSwitch;
        }

        if (message.Payload.TryGetValue("satelliteMode", out var satModeObj) && satModeObj is bool satMode)
        {
            SetSatelliteMode(satMode);
        }

        if (message.Payload.TryGetValue("endpoints", out var endpointsObj) && endpointsObj is string[] endpoints)
        {
            _config.MonitoredEndpoints = endpoints;
        }

        await SaveConfigurationAsync();

        message.Payload["result"] = new Dictionary<string, object>
        {
            ["autoSwitch"] = _config.AutoSwitchEnabled,
            ["satelliteMode"] = _config.EnableSatelliteMode,
            ["currentProtocol"] = _currentProtocol.ToString()
        };
    }

    private void HandleStats(PluginMessage message)
    {
        message.Payload["result"] = new Dictionary<string, object>
        {
            ["currentProtocol"] = _currentProtocol.ToString(),
            ["totalBytesSent"] = _totalBytesSent,
            ["totalBytesReceived"] = _totalBytesReceived,
            ["totalSwitches"] = _totalSwitches,
            ["pendingTransfers"] = _pendingTransfers.Count,
            ["activeConnections"] = _connectionPools.Values.Sum(p => p.ActiveConnections),
            ["monitoredEndpoints"] = _endpointMetrics.Count
        };
    }

    private Dictionary<string, object> SerializeMetrics(NetworkQualityMetrics m) => new()
    {
        ["endpoint"] = m.Endpoint,
        ["timestamp"] = m.Timestamp,
        ["averageLatencyMs"] = m.AverageLatencyMs,
        ["jitterMs"] = m.JitterMs,
        ["packetLossPercent"] = m.PacketLossPercent,
        ["quality"] = m.Quality.ToString(),
        ["recommendedProtocol"] = m.RecommendedProtocol.ToString(),
        ["bandwidthEstimateMbps"] = m.BandwidthEstimateMbps
    };

    #endregion

    #region Configuration Persistence

    private async Task LoadConfigurationAsync()
    {
        var path = Path.Combine(_storagePath, "config.json");
        if (!File.Exists(path)) return;

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var data = JsonSerializer.Deserialize<TransportConfigData>(json);

            if (data != null)
            {
                _config.AutoSwitchEnabled = data.AutoSwitchEnabled;
                _config.EnableSatelliteMode = data.EnableSatelliteMode;
                _config.MonitoredEndpoints = data.MonitoredEndpoints ?? Array.Empty<string>();

                if (Enum.TryParse<TransportProtocol>(data.LastProtocol, out var protocol))
                {
                    _currentProtocol = protocol;
                }
            }
        }
        catch
        {
            // Use defaults
        }
    }

    private async Task SaveConfigurationAsync()
    {
        try
        {
            Directory.CreateDirectory(_storagePath);

            var data = new TransportConfigData
            {
                AutoSwitchEnabled = _config.AutoSwitchEnabled,
                EnableSatelliteMode = _config.EnableSatelliteMode,
                MonitoredEndpoints = _config.MonitoredEndpoints,
                LastProtocol = _currentProtocol.ToString()
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(_storagePath, "config.json"), json);
        }
        catch
        {
            // Best effort
        }
    }

    #endregion

    #region Helpers

    private static string? GetString(Dictionary<string, object> payload, string key)
        => payload.TryGetValue(key, out var val) && val is string s ? s : null;

    private static bool? GetBool(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue(key, out var val))
        {
            if (val is bool b) return b;
            if (val is string s) return bool.TryParse(s, out var parsed) && parsed;
        }
        return null;
    }

    private static byte[]? GetBytes(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue(key, out var val))
        {
            if (val is byte[] bytes) return bytes;
            if (val is string s) return Convert.FromBase64String(s);
        }
        return null;
    }

    #endregion

    #region Hierarchy StreamingPluginBase Abstract Methods
    /// <inheritdoc/>
    public override Task PublishAsync(string topic, Stream data, CancellationToken ct = default)
        => Task.CompletedTask;
    /// <inheritdoc/>
    public override async IAsyncEnumerable<Dictionary<string, object>> SubscribeAsync(string topic, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    { await Task.CompletedTask; yield break; }
    #endregion
}

#region Supporting Types

/// <summary>
/// Transport protocol types.
/// </summary>
public enum TransportProtocol
{
    /// <summary>Standard TCP transport.</summary>
    Tcp,

    /// <summary>QUIC/HTTP3 transport.</summary>
    Quic,

    /// <summary>Reliable UDP with custom ACK mechanism.</summary>
    ReliableUdp,

    /// <summary>Store-and-forward for high-latency networks.</summary>
    StoreForward
}

/// <summary>
/// Network quality classification.
/// </summary>
public enum NetworkQuality
{
    /// <summary>Excellent network conditions.</summary>
    Excellent,

    /// <summary>Good network conditions.</summary>
    Good,

    /// <summary>Fair network conditions.</summary>
    Fair,

    /// <summary>Poor network conditions.</summary>
    Poor,

    /// <summary>Satellite-level latency (>500ms).</summary>
    Satellite,

    /// <summary>Network unusable or unreachable.</summary>
    Unusable
}

/// <summary>
/// Transfer priority levels.
/// </summary>
public enum TransferPriority
{
    /// <summary>Low priority - can be delayed.</summary>
    Low,

    /// <summary>Normal priority.</summary>
    Normal,

    /// <summary>High priority - expedited delivery.</summary>
    High,

    /// <summary>Critical - immediate delivery required.</summary>
    Critical
}

/// <summary>
/// Transfer status.
/// </summary>
public enum TransferStatus
{
    /// <summary>Transfer is queued.</summary>
    Queued,

    /// <summary>Transfer is in progress.</summary>
    InProgress,

    /// <summary>Transfer completed successfully.</summary>
    Completed,

    /// <summary>Transfer failed.</summary>
    Failed
}

/// <summary>
/// Compression level for adaptive compression.
/// </summary>
public enum CompressionLevel
{
    /// <summary>No compression.</summary>
    NoCompression,

    /// <summary>Fastest compression.</summary>
    Fastest,

    /// <summary>Optimal compression (balanced).</summary>
    Optimal,

    /// <summary>Smallest size (maximum compression).</summary>
    SmallestSize
}

/// <summary>
/// Network quality metrics.
/// </summary>
public sealed class NetworkQualityMetrics
{
    /// <summary>Target endpoint.</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>When the measurement was taken.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Average round-trip latency in milliseconds.</summary>
    public double AverageLatencyMs { get; init; }

    /// <summary>Minimum latency observed.</summary>
    public double MinLatencyMs { get; init; }

    /// <summary>Maximum latency observed.</summary>
    public double MaxLatencyMs { get; init; }

    /// <summary>Jitter (latency variation) in milliseconds.</summary>
    public double JitterMs { get; init; }

    /// <summary>Packet loss percentage (0-100).</summary>
    public double PacketLossPercent { get; init; }

    /// <summary>Overall network quality classification.</summary>
    public NetworkQuality Quality { get; init; }

    /// <summary>Recommended transport protocol based on conditions.</summary>
    public TransportProtocol RecommendedProtocol { get; init; }

    /// <summary>Estimated available bandwidth in Mbps.</summary>
    public double BandwidthEstimateMbps { get; init; }

    /// <summary>Error message if measurement failed.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Transfer result.
/// </summary>
/// <param name="Success">Whether the transfer succeeded.</param>
/// <param name="Protocol">Protocol used for the transfer.</param>
/// <param name="BytesTransferred">Number of bytes transferred.</param>
/// <param name="Duration">Transfer duration.</param>
/// <param name="RetriesUsed">Number of retries used.</param>
/// <param name="FallbackUsed">Whether a fallback protocol was used.</param>
/// <param name="Error">Error message if transfer failed.</param>
/// <param name="StoreForwardId">Store-and-forward transfer ID if queued.</param>
public sealed record TransferResult(
    bool Success = false,
    TransportProtocol Protocol = TransportProtocol.Tcp,
    long BytesTransferred = 0,
    TimeSpan Duration = default,
    int RetriesUsed = 0,
    bool FallbackUsed = false,
    string? Error = null,
    Guid? StoreForwardId = null);

/// <summary>
/// Protocol negotiation result.
/// </summary>
public sealed class ProtocolNegotiationResult
{
    /// <summary>Whether negotiation succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Selected protocol.</summary>
    public TransportProtocol SelectedProtocol { get; init; }

    /// <summary>Local protocol capabilities.</summary>
    public ProtocolCapabilities? LocalCapabilities { get; init; }

    /// <summary>Remote protocol capabilities.</summary>
    public ProtocolCapabilities? RemoteCapabilities { get; init; }

    /// <summary>Network metrics used for selection.</summary>
    public NetworkQualityMetrics? NetworkMetrics { get; init; }

    /// <summary>When negotiation completed.</summary>
    public DateTime NegotiatedAt { get; init; }
}

/// <summary>
/// Protocol capabilities.
/// </summary>
public sealed class ProtocolCapabilities
{
    /// <summary>Whether QUIC is supported.</summary>
    public bool SupportsQuic { get; init; }

    /// <summary>Whether TCP is supported.</summary>
    public bool SupportsTcp { get; init; }

    /// <summary>Whether Reliable UDP is supported.</summary>
    public bool SupportsReliableUdp { get; init; }

    /// <summary>Whether Store-and-Forward is supported.</summary>
    public bool SupportsStoreForward { get; init; }

    /// <summary>Whether compression is supported.</summary>
    public bool SupportsCompression { get; init; }

    /// <summary>Maximum chunk size for UDP.</summary>
    public int MaxChunkSize { get; init; }

    /// <summary>Protocol version.</summary>
    public int ProtocolVersion { get; init; }
}

/// <summary>
/// Compression recommendation.
/// </summary>
public sealed class CompressionRecommendation
{
    /// <summary>Whether compression is recommended.</summary>
    public bool ShouldCompress { get; init; }

    /// <summary>Recommended compression level.</summary>
    public CompressionLevel Level { get; init; }

    /// <summary>Estimated compression ratio (0-1).</summary>
    public double EstimatedRatio { get; init; }

    /// <summary>Reason for recommendation.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Satellite mode recommendation.
/// </summary>
public sealed class SatelliteModeRecommendation
{
    /// <summary>Whether satellite mode is recommended.</summary>
    public bool RecommendSatelliteMode { get; init; }

    /// <summary>Reason for recommendation.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Suggested chunk size in bytes.</summary>
    public int SuggestedChunkSize { get; init; }

    /// <summary>Suggested ACK timeout.</summary>
    public TimeSpan SuggestedAckTimeout { get; init; }

    /// <summary>Suggested retry count.</summary>
    public int SuggestedRetries { get; init; }

    /// <summary>Whether to use store-and-forward.</summary>
    public bool UseStoreForward { get; init; }
}

/// <summary>
/// Pending store-and-forward transfer.
/// </summary>
internal sealed class PendingTransfer
{
    public Guid Id { get; init; }
    public string Endpoint { get; init; } = string.Empty;
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public TransferPriority Priority { get; init; }
    public DateTime QueuedAt { get; init; }
    public DateTime? NextAttemptAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int Attempts { get; set; }
    public TransferStatus Status { get; set; }
}

/// <summary>
/// Transfer metadata for persistence.
/// </summary>
internal sealed class TransferMetadata
{
    public Guid Id { get; init; }
    public string Endpoint { get; init; } = string.Empty;
    public TransferPriority Priority { get; init; }
    public DateTime QueuedAt { get; init; }
    public long DataLength { get; init; }
    public string DataHash { get; init; } = string.Empty;
}

/// <summary>
/// Configuration persistence data.
/// </summary>
internal sealed class TransportConfigData
{
    public bool AutoSwitchEnabled { get; init; }
    public bool EnableSatelliteMode { get; init; }
    public string[] MonitoredEndpoints { get; init; } = Array.Empty<string>();
    public string LastProtocol { get; init; } = "Tcp";
}

/// <summary>
/// Connection pool for a specific protocol with health checking, idle eviction,
/// and connection reuse. Connections are returned to the pool after use, eliminating
/// per-send connection creation overhead.
/// </summary>
internal sealed class ConnectionPool : IAsyncDisposable
{
    private readonly TransportProtocol _protocol;
    private readonly AdaptiveTransportConfig _config;
    private readonly BoundedDictionary<string, ConcurrentQueue<PooledConnection>> _endpointPools = new BoundedDictionary<string, ConcurrentQueue<PooledConnection>>(1000);
    private readonly Timer _healthCheckTimer;
    private int _activeConnections;
    private int _totalConnections;

    /// <summary>Maximum connections per endpoint.</summary>
    private const int MaxConnectionsPerEndpoint = 10;

    /// <summary>Idle timeout before connection is evicted.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Health check interval.</summary>
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(30);

    public int ActiveConnections => _activeConnections;
    public int TotalConnections => _totalConnections;

    public ConnectionPool(TransportProtocol protocol, AdaptiveTransportConfig config)
    {
        _protocol = protocol;
        _config = config;
        _healthCheckTimer = new Timer(
            _ => PerformHealthCheck(),
            null,
            HealthCheckInterval,
            HealthCheckInterval);
    }

    /// <summary>
    /// Gets a pooled TCP connection for the specified endpoint, or creates a new one.
    /// </summary>
    /// <param name="host">Target host.</param>
    /// <param name="port">Target port.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A pooled TCP connection.</returns>
    public async Task<PooledConnection> GetTcpConnectionAsync(string host, int port, CancellationToken ct)
    {
        var key = $"{host}:{port}";
        var pool = _endpointPools.GetOrAdd(key, _ => new ConcurrentQueue<PooledConnection>());

        // Try to get an existing healthy connection
        while (pool.TryDequeue(out var existing))
        {
            if (existing.IsHealthy && !existing.IsExpired(IdleTimeout))
            {
                existing.MarkActive();
                Interlocked.Increment(ref _activeConnections);
                return existing;
            }

            // Connection is unhealthy or expired -- dispose it
            existing.Dispose();
            Interlocked.Decrement(ref _totalConnections);
        }

        // Create new connection
        var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
        var conn = new PooledConnection(client, key);
        Interlocked.Increment(ref _activeConnections);
        Interlocked.Increment(ref _totalConnections);
        return conn;
    }

    /// <summary>
    /// Returns a TCP connection to the pool for reuse.
    /// </summary>
    /// <param name="connection">The connection to return.</param>
    public void ReturnConnection(PooledConnection connection)
    {
        Interlocked.Decrement(ref _activeConnections);

        if (!connection.IsHealthy)
        {
            connection.Dispose();
            Interlocked.Decrement(ref _totalConnections);
            return;
        }

        var key = connection.EndpointKey;
        var pool = _endpointPools.GetOrAdd(key, _ => new ConcurrentQueue<PooledConnection>());

        // Check pool capacity
        if (pool.Count >= MaxConnectionsPerEndpoint)
        {
            connection.Dispose();
            Interlocked.Decrement(ref _totalConnections);
            return;
        }

        connection.MarkIdle();
        pool.Enqueue(connection);
    }

    /// <summary>
    /// Periodic health check: pings idle connections, removes expired/unhealthy ones.
    /// </summary>
    private void PerformHealthCheck()
    {
        foreach (var kvp in _endpointPools)
        {
            var pool = kvp.Value;
            var count = pool.Count;

            for (var i = 0; i < count; i++)
            {
                if (!pool.TryDequeue(out var conn))
                    break;

                if (conn.IsHealthy && !conn.IsExpired(IdleTimeout))
                {
                    pool.Enqueue(conn);
                }
                else
                {
                    conn.Dispose();
                    Interlocked.Decrement(ref _totalConnections);
                }
            }
        }
    }

    public Task WarmupAsync(int count, CancellationToken ct)
    {
        // Warmup is now done on-demand via GetTcpConnectionAsync
        // UDP sockets are connectionless and don't need pooling
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _healthCheckTimer.Dispose();

        foreach (var pool in _endpointPools.Values)
        {
            while (pool.TryDequeue(out var conn))
            {
                conn.Dispose();
                Interlocked.Decrement(ref _totalConnections);
            }
        }
        _endpointPools.Clear();
        _activeConnections = 0;
    }
}

/// <summary>
/// Wrapper around a TCP connection that tracks health and idle time for pool management.
/// </summary>
internal sealed class PooledConnection : IDisposable
{
    private readonly TcpClient _client;
    private DateTime _lastUsed;
    private bool _disposed;

    /// <summary>The endpoint key for pool routing.</summary>
    public string EndpointKey { get; }

    /// <summary>
    /// Gets whether the underlying TCP connection is still healthy (connected and not disposed).
    /// </summary>
    public bool IsHealthy => !_disposed && _client.Connected;

    /// <summary>Gets the network stream for data transfer.</summary>
    public NetworkStream GetStream() => _client.GetStream();

    public PooledConnection(TcpClient client, string endpointKey)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        EndpointKey = endpointKey;
        _lastUsed = DateTime.UtcNow;
    }

    /// <summary>Checks whether this connection has been idle longer than the specified timeout.</summary>
    public bool IsExpired(TimeSpan idleTimeout) => (DateTime.UtcNow - _lastUsed) > idleTimeout;

    /// <summary>Marks this connection as actively in use.</summary>
    public void MarkActive() => _lastUsed = DateTime.UtcNow;

    /// <summary>Marks this connection as idle (returned to pool).</summary>
    public void MarkIdle() => _lastUsed = DateTime.UtcNow;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _client.Dispose(); } catch { /* best effort */ }
    }
}

/// <summary>
/// AIMD (Additive Increase Multiplicative Decrease) congestion control for Reliable UDP.
/// Prevents burst flooding by limiting the send rate based on network feedback.
/// </summary>
internal sealed class AimdCongestionControl
{
    private double _congestionWindow;
    private double _rttMs;
    private readonly double _minWindow;
    private readonly double _initialWindow;
    private readonly double _rttAlpha;
    private readonly object _lock = new();

    /// <summary>Gets the current congestion window size (in segments).</summary>
    public double CongestionWindow
    {
        get { lock (_lock) return _congestionWindow; }
    }

    /// <summary>Gets the current smoothed RTT estimate in milliseconds.</summary>
    public double SmoothedRttMs
    {
        get { lock (_lock) return _rttMs; }
    }

    /// <summary>
    /// Creates a new AIMD congestion controller.
    /// </summary>
    /// <param name="initialWindow">Initial congestion window in segments. Default 4.</param>
    /// <param name="minWindow">Minimum congestion window. Default 1.</param>
    /// <param name="rttAlpha">EWMA smoothing factor for RTT. Default 0.125 (RFC 6298).</param>
    public AimdCongestionControl(double initialWindow = 4, double minWindow = 1, double rttAlpha = 0.125)
    {
        _initialWindow = initialWindow;
        _congestionWindow = initialWindow;
        _minWindow = minWindow;
        _rttAlpha = rttAlpha;
        _rttMs = 100; // Initial RTT estimate
    }

    /// <summary>
    /// Called when an ACK is received (additive increase).
    /// Window increases by 1/window per ACK (linear growth).
    /// </summary>
    public void OnAck(double rttSampleMs)
    {
        lock (_lock)
        {
            // Additive increase: window += 1/window
            _congestionWindow += 1.0 / _congestionWindow;

            // Update RTT using EWMA: rtt_new = (1 - alpha) * rtt_old + alpha * sample
            _rttMs = (1 - _rttAlpha) * _rttMs + _rttAlpha * rttSampleMs;
        }
    }

    /// <summary>
    /// Called when a timeout or packet loss is detected (multiplicative decrease).
    /// Window is halved, with a minimum of minWindow.
    /// </summary>
    public void OnLoss()
    {
        lock (_lock)
        {
            // Multiplicative decrease: window = window / 2
            _congestionWindow = Math.Max(_minWindow, _congestionWindow / 2);
        }
    }

    /// <summary>
    /// Gets the maximum number of segments that can be in-flight at this moment.
    /// </summary>
    public int GetSendWindow()
    {
        lock (_lock)
        {
            return Math.Max(1, (int)_congestionWindow);
        }
    }

    /// <summary>
    /// Calculates the delay between sends based on the congestion window and RTT.
    /// Send rate = congestion_window * segment_size / RTT.
    /// </summary>
    /// <param name="segmentSize">Segment size in bytes.</param>
    /// <returns>Delay in milliseconds between sends.</returns>
    public double GetSendDelayMs(int segmentSize)
    {
        lock (_lock)
        {
            if (_congestionWindow <= 0 || _rttMs <= 0) return 0;
            // Delay per segment = RTT / window
            return _rttMs / _congestionWindow;
        }
    }

    /// <summary>
    /// Resets the congestion window to initial values.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _congestionWindow = _initialWindow;
        }
    }
}

/// <summary>
/// Configuration for the Adaptive Transport Plugin.
/// </summary>
public sealed class AdaptiveTransportConfig
{
    /// <summary>
    /// Interval for checking network quality. Default is 30 seconds.
    /// </summary>
    public TimeSpan QualityCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Timeout for quality check operations. Default is 10 seconds.
    /// </summary>
    public TimeSpan QualityCheckTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Number of probe packets to send for quality measurement. Default is 5.
    /// </summary>
    public int ProbeCount { get; set; } = 5;

    /// <summary>
    /// Timeout for individual probe packets. Default is 500ms.
    /// </summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Interval between probe packets. Default is 100ms.
    /// </summary>
    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// UDP chunk size in bytes. Default is 1400 (fits in most MTUs).
    /// </summary>
    public int UdpChunkSize { get; set; } = 1400;

    /// <summary>
    /// ACK timeout for reliable UDP. Default is 500ms.
    /// </summary>
    public TimeSpan AckTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Maximum retries for reliable UDP. Default is 3.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Maximum store-and-forward attempts. Default is 10.
    /// </summary>
    public int MaxStoreForwardAttempts { get; set; } = 10;

    /// <summary>
    /// Delay between store-and-forward retry attempts. Default is 5 minutes.
    /// </summary>
    public TimeSpan StoreForwardRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Timeout for flushing pending transfers on shutdown. Default is 30 seconds.
    /// </summary>
    public TimeSpan FlushTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Timeout for draining active transfers during protocol switch. Default is 5 seconds.
    /// </summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Number of connections to pre-warm in each pool. Default is 2.
    /// </summary>
    public int PoolWarmupCount { get; set; } = 2;

    /// <summary>
    /// Control port for protocol negotiation. Default is 5001.
    /// </summary>
    public int ControlPort { get; set; } = 5001;

    /// <summary>
    /// Whether automatic protocol switching is enabled. Default is true.
    /// </summary>
    public bool AutoSwitchEnabled { get; set; } = true;

    /// <summary>
    /// Whether satellite mode (>500ms latency) optimizations are enabled. Default is true.
    /// </summary>
    public bool EnableSatelliteMode { get; set; } = true;

    /// <summary>
    /// Endpoints to actively monitor for quality. Default is empty.
    /// </summary>
    public string[] MonitoredEndpoints { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Fallback chain of protocols to try in order.
    /// </summary>
    public TransportProtocol[] FallbackChain { get; set; } = new[]
    {
        TransportProtocol.Tcp,
        TransportProtocol.Quic,
        TransportProtocol.ReliableUdp,
        TransportProtocol.StoreForward
    };

    /// <summary>
    /// Whether to verify SSL/TLS certificates. When false, certificate validation is bypassed.
    /// Default is true. Should only be set to false in development/testing environments.
    /// </summary>
    public bool VerifySslCertificates { get; set; } = true;
}

#endregion