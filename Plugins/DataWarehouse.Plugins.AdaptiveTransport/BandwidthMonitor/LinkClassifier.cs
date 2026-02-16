namespace DataWarehouse.Plugins.AdaptiveTransport.BandwidthMonitor;

/// <summary>
/// Classifies network links based on bandwidth and latency characteristics.
/// Implements hysteresis to prevent classification flapping.
/// </summary>
public sealed class LinkClassifier
{
    private LinkClassification? _currentClassification;
    private readonly double _hysteresisThreshold;

    /// <summary>
    /// Gets the current link classification, or null if no classification has been performed yet.
    /// </summary>
    public LinkClassification? CurrentClass => _currentClassification;

    /// <summary>
    /// Initializes a new instance of the LinkClassifier.
    /// </summary>
    /// <param name="hysteresisThreshold">Minimum confidence required to change classification (0.0-1.0). Default is 0.7.</param>
    public LinkClassifier(double hysteresisThreshold = 0.7)
    {
        if (hysteresisThreshold < 0.0 || hysteresisThreshold > 1.0)
            throw new ArgumentOutOfRangeException(nameof(hysteresisThreshold), "Must be between 0.0 and 1.0");

        _hysteresisThreshold = hysteresisThreshold;
    }

    /// <summary>
    /// Classifies a network link based on the provided measurement.
    /// Uses hysteresis to prevent rapid classification changes.
    /// </summary>
    /// <param name="measurement">The bandwidth measurement to classify.</param>
    /// <returns>Link classification with confidence score.</returns>
    public LinkClassification Classify(BandwidthMeasurement measurement)
    {
        var (linkClass, confidence) = DetermineClassification(measurement);

        // Apply hysteresis: only change if new classification is different and has high confidence
        if (_currentClassification != null &&
            _currentClassification.Class != linkClass &&
            confidence < _hysteresisThreshold)
        {
            // Keep current classification - not confident enough to switch
            return _currentClassification;
        }

        // Update current classification
        _currentClassification = new LinkClassification(
            linkClass,
            confidence,
            measurement,
            DateTimeOffset.UtcNow);

        return _currentClassification;
    }

    private (LinkClass Class, double Confidence) DetermineClassification(BandwidthMeasurement measurement)
    {
        var downloadMbps = measurement.DownloadBytesPerSecond * 8.0 / 1_000_000;
        var latencyMs = measurement.LatencyMs;
        var packetLoss = measurement.PacketLossPercent;

        // Check for intermittent connection first (high packet loss)
        if (packetLoss > 5.0)
        {
            var confidence = Math.Min(1.0, packetLoss / 20.0); // Higher loss = higher confidence
            return (LinkClass.Intermittent, confidence);
        }

        // Check for satellite (very high latency)
        if (latencyMs > 500)
        {
            var confidence = Math.Min(1.0, (latencyMs - 500) / 500.0 + 0.8); // Scale confidence with latency
            return (LinkClass.Satellite, Math.Min(confidence, 1.0));
        }

        // Fiber: >100 Mbps, <5ms latency
        if (downloadMbps > 100 && latencyMs < 5)
        {
            var bandwidthConfidence = Math.Min(1.0, downloadMbps / 200.0); // Higher bandwidth = higher confidence
            var latencyConfidence = Math.Min(1.0, (5 - latencyMs) / 5.0 + 0.5);
            var confidence = (bandwidthConfidence + latencyConfidence) / 2.0;
            return (LinkClass.Fiber, Math.Min(confidence, 1.0));
        }

        // Mobile 5G: 50-500 Mbps, 10-30ms latency
        if (downloadMbps >= 50 && downloadMbps <= 500 && latencyMs >= 10 && latencyMs <= 30)
        {
            var bandwidthScore = 1.0 - Math.Abs(downloadMbps - 150) / 150.0; // Peak at 150 Mbps
            var latencyScore = 1.0 - Math.Abs(latencyMs - 20) / 20.0; // Peak at 20ms
            var confidence = Math.Max(0.6, (bandwidthScore + latencyScore) / 2.0);
            return (LinkClass.Mobile5G, Math.Min(confidence, 1.0));
        }

        // Broadband: 10-100 Mbps, <50ms latency
        if (downloadMbps >= 10 && downloadMbps <= 100 && latencyMs < 50)
        {
            var bandwidthScore = Math.Min(1.0, downloadMbps / 50.0);
            var latencyScore = Math.Min(1.0, (50 - latencyMs) / 50.0);
            var confidence = Math.Max(0.6, (bandwidthScore + latencyScore) / 2.0);
            return (LinkClass.Broadband, Math.Min(confidence, 1.0));
        }

        // Mobile 4G: 1-50 Mbps, 30-100ms latency
        if (downloadMbps >= 1 && downloadMbps <= 50 && latencyMs >= 30 && latencyMs <= 100)
        {
            var bandwidthScore = Math.Min(1.0, downloadMbps / 25.0);
            var latencyScore = 1.0 - Math.Abs(latencyMs - 65) / 65.0; // Peak at 65ms
            var confidence = Math.Max(0.6, (bandwidthScore + latencyScore) / 2.0);
            return (LinkClass.Mobile4G, Math.Min(confidence, 1.0));
        }

        // Partial matches with lower confidence
        if (downloadMbps > 100)
        {
            // High bandwidth but high latency - likely fiber with congestion
            return (LinkClass.Fiber, 0.5);
        }

        if (downloadMbps >= 50)
        {
            // Moderate-high bandwidth - could be 5G or good broadband
            return latencyMs < 30 ? (LinkClass.Mobile5G, 0.5) : (LinkClass.Broadband, 0.5);
        }

        if (downloadMbps >= 10)
        {
            // Moderate bandwidth - likely broadband
            return (LinkClass.Broadband, 0.5);
        }

        if (downloadMbps >= 1)
        {
            // Low bandwidth - likely 4G
            return (LinkClass.Mobile4G, 0.5);
        }

        // Cannot determine - unknown
        return (LinkClass.Unknown, 0.3);
    }
}
