namespace DataWarehouse.Plugins.AdaptiveTransport.BandwidthMonitor;

/// <summary>
/// Dynamically adjusts synchronization parameters based on classified link type.
/// Provides optimized settings for different network conditions.
/// </summary>
public sealed class SyncParameterAdjuster
{
    /// <summary>
    /// Gets optimal synchronization parameters for the classified link.
    /// </summary>
    /// <param name="link">The link classification to base parameters on.</param>
    /// <returns>Recommended synchronization parameters.</returns>
    public SyncParameters GetParameters(LinkClassification link)
    {
        return link.Class switch
        {
            LinkClass.Fiber => CreateFiberParameters(),
            LinkClass.Broadband => CreateBroadbandParameters(),
            LinkClass.Mobile5G => CreateMobile5GParameters(),
            LinkClass.Mobile4G => CreateMobile4GParameters(),
            LinkClass.Satellite => CreateSatelliteParameters(),
            LinkClass.Intermittent => CreateIntermittentParameters(),
            LinkClass.Unknown => CreateSafeDefaultParameters(),
            _ => CreateSafeDefaultParameters()
        };
    }

    private SyncParameters CreateFiberParameters()
    {
        // Fiber: maximize throughput with large chunks and high concurrency
        return new SyncParameters(
            Mode: SyncMode.FullReplication,
            MaxConcurrency: 16,
            ChunkSizeBytes: 4 * 1024 * 1024, // 4MB chunks
            RetryPolicy: new RetryPolicy(
                MaxRetries: 2,
                InitialBackoffMs: 100,
                MaxBackoffMs: 1000,
                BackoffMultiplier: 2.0),
            CompressionEnabled: false); // Skip compression overhead on fast links
    }

    private SyncParameters CreateBroadbandParameters()
    {
        // Broadband: balanced approach with moderate chunks and concurrency
        return new SyncParameters(
            Mode: SyncMode.FullReplication,
            MaxConcurrency: 8,
            ChunkSizeBytes: 1 * 1024 * 1024, // 1MB chunks
            RetryPolicy: new RetryPolicy(
                MaxRetries: 3,
                InitialBackoffMs: 200,
                MaxBackoffMs: 2000,
                BackoffMultiplier: 2.0),
            CompressionEnabled: true);
    }

    private SyncParameters CreateMobile5GParameters()
    {
        // 5G: delta-only to save bandwidth, moderate chunks
        return new SyncParameters(
            Mode: SyncMode.DeltaOnly,
            MaxConcurrency: 4,
            ChunkSizeBytes: 256 * 1024, // 256KB chunks
            RetryPolicy: new RetryPolicy(
                MaxRetries: 4,
                InitialBackoffMs: 300,
                MaxBackoffMs: 3000,
                BackoffMultiplier: 2.0),
            CompressionEnabled: true);
    }

    private SyncParameters CreateMobile4GParameters()
    {
        // 4G: delta-only, smaller chunks, lower concurrency
        return new SyncParameters(
            Mode: SyncMode.DeltaOnly,
            MaxConcurrency: 4,
            ChunkSizeBytes: 256 * 1024, // 256KB chunks
            RetryPolicy: new RetryPolicy(
                MaxRetries: 5,
                InitialBackoffMs: 500,
                MaxBackoffMs: 5000,
                BackoffMultiplier: 2.0),
            CompressionEnabled: true);
    }

    private SyncParameters CreateSatelliteParameters()
    {
        // Satellite: delta-only, very small chunks, aggressive retries, low concurrency
        return new SyncParameters(
            Mode: SyncMode.DeltaOnly,
            MaxConcurrency: 2,
            ChunkSizeBytes: 64 * 1024, // 64KB chunks
            RetryPolicy: new RetryPolicy(
                MaxRetries: 10, // Many retries due to high latency
                InitialBackoffMs: 1000,
                MaxBackoffMs: 10000,
                BackoffMultiplier: 1.5), // Gentler backoff
            CompressionEnabled: true);
    }

    private SyncParameters CreateIntermittentParameters()
    {
        // Intermittent: store-and-forward, minimal chunks, single connection
        return new SyncParameters(
            Mode: SyncMode.StoreAndForward,
            MaxConcurrency: 1,
            ChunkSizeBytes: 16 * 1024, // 16KB chunks
            RetryPolicy: new RetryPolicy(
                MaxRetries: 15, // Very aggressive retries
                InitialBackoffMs: 2000,
                MaxBackoffMs: 30000,
                BackoffMultiplier: 2.0),
            CompressionEnabled: true);
    }

    private SyncParameters CreateSafeDefaultParameters()
    {
        // Unknown: conservative settings to avoid overloading
        return new SyncParameters(
            Mode: SyncMode.DeltaOnly,
            MaxConcurrency: 2,
            ChunkSizeBytes: 128 * 1024, // 128KB chunks
            RetryPolicy: new RetryPolicy(
                MaxRetries: 5,
                InitialBackoffMs: 500,
                MaxBackoffMs: 5000,
                BackoffMultiplier: 2.0),
            CompressionEnabled: true);
    }
}
