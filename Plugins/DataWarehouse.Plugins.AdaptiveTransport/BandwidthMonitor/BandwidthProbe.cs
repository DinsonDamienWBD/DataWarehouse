using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;

namespace DataWarehouse.Plugins.AdaptiveTransport.BandwidthMonitor;

/// <summary>
/// Probes network bandwidth using TCP window analysis and active throughput measurement.
/// Maintains a sliding window of historical measurements for averaging.
/// </summary>
public sealed class BandwidthProbe
{
    private readonly ConcurrentQueue<BandwidthMeasurement> _measurements = new();
    private readonly int _maxHistorySize = 60; // Keep 60 measurements (1 per second = 1 minute)

    /// <summary>
    /// Measures bandwidth to the specified endpoint using TCP-based probing.
    /// </summary>
    /// <param name="endpoint">Target endpoint in format "host:port".</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Bandwidth measurement snapshot.</returns>
    public async Task<BandwidthMeasurement> MeasureAsync(string endpoint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Endpoint cannot be null or empty", nameof(endpoint));

        var parts = endpoint.Split(':');
        var host = parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 80;

        BandwidthMeasurement measurement;

        try
        {
            // Perform TCP window analysis and active probing
            var tcpResult = await MeasureTcpBandwidthAsync(host, port, ct);
            measurement = new BandwidthMeasurement(
                DateTimeOffset.UtcNow,
                tcpResult.DownloadBps,
                tcpResult.UploadBps,
                tcpResult.LatencyMs,
                tcpResult.JitterMs,
                tcpResult.PacketLossPercent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Connection failed - return degraded measurement
            measurement = new BandwidthMeasurement(
                DateTimeOffset.UtcNow,
                DownloadBytesPerSecond: 0,
                UploadBytesPerSecond: 0,
                LatencyMs: double.MaxValue,
                JitterMs: 0,
                PacketLossPercent: 100);
        }

        // Add to history
        AddMeasurement(measurement);

        return measurement;
    }

    /// <summary>
    /// Gets the average bandwidth measurement over the specified time window.
    /// </summary>
    /// <param name="window">Time window to average over.</param>
    /// <returns>Averaged measurement, or null if no measurements available.</returns>
    public BandwidthMeasurement? GetAverageMeasurement(TimeSpan window)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        var recentMeasurements = _measurements
            .Where(m => m.Timestamp >= cutoff)
            .ToList();

        if (recentMeasurements.Count == 0)
            return null;

        var avgDownload = (long)recentMeasurements.Average(m => m.DownloadBytesPerSecond);
        var avgUpload = (long)recentMeasurements.Average(m => m.UploadBytesPerSecond);
        var avgLatency = recentMeasurements.Average(m => m.LatencyMs);
        var avgJitter = recentMeasurements.Average(m => m.JitterMs);
        var avgPacketLoss = recentMeasurements.Average(m => m.PacketLossPercent);

        return new BandwidthMeasurement(
            DateTimeOffset.UtcNow,
            avgDownload,
            avgUpload,
            avgLatency,
            avgJitter,
            avgPacketLoss);
    }

    private void AddMeasurement(BandwidthMeasurement measurement)
    {
        _measurements.Enqueue(measurement);

        // Prune old measurements
        while (_measurements.Count > _maxHistorySize)
        {
            _measurements.TryDequeue(out _);
        }
    }

    private async Task<TcpMeasurementResult> MeasureTcpBandwidthAsync(string host, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        var sw = Stopwatch.StartNew();

        try
        {
            // Connect and measure RTT
            await client.ConnectAsync(host, port, ct);
            sw.Stop();
            var rttMs = sw.Elapsed.TotalMilliseconds;

            // Estimate bandwidth from TCP window size and RTT
            var receiveBufferSize = client.ReceiveBufferSize;
            var sendBufferSize = client.SendBufferSize;

            // Bandwidth-delay product estimation
            // BW = WindowSize / RTT
            var estimatedDownloadBps = rttMs > 0
                ? (long)(receiveBufferSize / (rttMs / 1000.0))
                : 0;

            var estimatedUploadBps = rttMs > 0
                ? (long)(sendBufferSize / (rttMs / 1000.0))
                : 0;

            // Perform active throughput test with small payload
            var (actualDownloadBps, actualUploadBps, jitter) = await ActiveProbeAsync(client, ct);

            // Use the higher of window-based estimate and actual measurement
            var downloadBps = Math.Max(estimatedDownloadBps, actualDownloadBps);
            var uploadBps = Math.Max(estimatedUploadBps, actualUploadBps);

            return new TcpMeasurementResult(
                downloadBps,
                uploadBps,
                rttMs,
                jitter,
                PacketLossPercent: 0); // TCP handles retransmission, so no direct packet loss
        }
        catch (SocketException)
        {
            // Connection failed
            return new TcpMeasurementResult(0, 0, double.MaxValue, 0, 100);
        }
    }

    private async Task<(long DownloadBps, long UploadBps, double JitterMs)> ActiveProbeAsync(
        TcpClient client,
        CancellationToken ct)
    {
        const int probeSize = 8192; // 8KB probe
        const int probeCount = 3;

        try
        {
            var stream = client.GetStream();
            var sendData = new byte[probeSize];
            var receiveData = new byte[probeSize];
            var latencies = new List<double>();

            long totalSent = 0;
            long totalReceived = 0;
            var totalSw = Stopwatch.StartNew();

            for (var i = 0; i < probeCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                // Send probe
                var sendSw = Stopwatch.StartNew();
                await stream.WriteAsync(sendData, ct);
                await stream.FlushAsync(ct);
                sendSw.Stop();
                totalSent += probeSize;

                // Receive response (if echo is available)
                var recvSw = Stopwatch.StartNew();
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(500));
                    var bytesRead = await stream.ReadAsync(receiveData.AsMemory(0, probeSize), timeoutCts.Token);
                    recvSw.Stop();
                    totalReceived += bytesRead;
                    latencies.Add(recvSw.Elapsed.TotalMilliseconds);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout - no echo response
                }
            }

            totalSw.Stop();

            var elapsedSeconds = totalSw.Elapsed.TotalSeconds;
            var uploadBps = elapsedSeconds > 0 ? (long)(totalSent / elapsedSeconds) : 0;
            var downloadBps = elapsedSeconds > 0 ? (long)(totalReceived / elapsedSeconds) : 0;

            // Calculate jitter from latency variations
            var jitter = CalculateJitter(latencies);

            return (downloadBps, uploadBps, jitter);
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    private double CalculateJitter(List<double> latencies)
    {
        if (latencies.Count < 2)
            return 0;

        var diffs = new List<double>();
        for (var i = 1; i < latencies.Count; i++)
        {
            diffs.Add(Math.Abs(latencies[i] - latencies[i - 1]));
        }

        return diffs.Any() ? diffs.Average() : 0;
    }

    private sealed record TcpMeasurementResult(
        long DownloadBps,
        long UploadBps,
        double LatencyMs,
        double JitterMs,
        double PacketLossPercent);
}
