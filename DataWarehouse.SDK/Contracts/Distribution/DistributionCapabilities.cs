namespace DataWarehouse.SDK.Contracts.Distribution;

/// <summary>
/// Describes the capabilities of a content distribution network (CDN) strategy.
/// </summary>
/// <param name="SupportsEdgeCaching">
/// Indicates whether the strategy supports caching content at edge locations.
/// </param>
/// <param name="SupportsGeoRestriction">
/// Indicates whether the strategy supports geographic restrictions on content access.
/// </param>
/// <param name="SupportsPurge">
/// Indicates whether the strategy supports cache invalidation/purging.
/// </param>
/// <param name="SupportsSignedUrls">
/// Indicates whether the strategy supports generating signed URLs for secure access.
/// </param>
/// <param name="EdgeLocationCount">
/// The approximate number of edge locations in the CDN network.
/// Null if the information is not available.
/// </param>
/// <param name="SupportsHttps">
/// Indicates whether the CDN supports HTTPS content delivery.
/// </param>
/// <param name="SupportsHttp2">
/// Indicates whether the CDN supports HTTP/2 protocol.
/// </param>
/// <param name="SupportsHttp3">
/// Indicates whether the CDN supports HTTP/3 (QUIC) protocol.
/// </param>
/// <param name="SupportsCompression">
/// Indicates whether the CDN can automatically compress content (gzip, brotli).
/// </param>
/// <param name="SupportsCustomHeaders">
/// Indicates whether custom HTTP headers can be configured for responses.
/// </param>
/// <param name="SupportsOriginShield">
/// Indicates whether the CDN provides an origin shield to reduce origin load.
/// </param>
/// <param name="SupportsRealTimeMetrics">
/// Indicates whether real-time performance and usage metrics are available.
/// </param>
/// <param name="MaxFileSize">
/// The maximum file size that can be distributed in bytes.
/// Null if there is no size limit.
/// </param>
/// <param name="SupportedRegions">
/// The set of geographic regions where edge locations are available.
/// Empty set if region information is not available.
/// </param>
/// <remarks>
/// <para>
/// This record is immutable and should be constructed once during strategy initialization.
/// </para>
/// <para>
/// <strong>Example Usage:</strong>
/// <code>
/// var capabilities = new DistributionCapabilities(
///     SupportsEdgeCaching: true,
///     SupportsGeoRestriction: true,
///     SupportsPurge: true,
///     SupportsSignedUrls: true,
///     EdgeLocationCount: 450,
///     SupportsHttps: true,
///     SupportsHttp2: true,
///     SupportsHttp3: true,
///     SupportsCompression: true,
///     SupportsCustomHeaders: true,
///     SupportsOriginShield: true,
///     SupportsRealTimeMetrics: true,
///     MaxFileSize: 30_000_000_000, // 30 GB
///     SupportedRegions: new[] { "us-east", "us-west", "eu-central", "ap-southeast" }
/// );
/// </code>
/// </para>
/// </remarks>
public sealed record DistributionCapabilities(
    bool SupportsEdgeCaching,
    bool SupportsGeoRestriction,
    bool SupportsPurge,
    bool SupportsSignedUrls,
    int? EdgeLocationCount,
    bool SupportsHttps,
    bool SupportsHttp2,
    bool SupportsHttp3,
    bool SupportsCompression,
    bool SupportsCustomHeaders,
    bool SupportsOriginShield,
    bool SupportsRealTimeMetrics,
    long? MaxFileSize,
    IReadOnlySet<string> SupportedRegions)
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DistributionCapabilities"/> record with minimal capabilities.
    /// </summary>
    public DistributionCapabilities()
        : this(
            SupportsEdgeCaching: false,
            SupportsGeoRestriction: false,
            SupportsPurge: false,
            SupportsSignedUrls: false,
            EdgeLocationCount: null,
            SupportsHttps: false,
            SupportsHttp2: false,
            SupportsHttp3: false,
            SupportsCompression: false,
            SupportsCustomHeaders: false,
            SupportsOriginShield: false,
            SupportsRealTimeMetrics: false,
            MaxFileSize: null,
            SupportedRegions: new HashSet<string>())
    {
    }

    /// <summary>
    /// Determines whether the specified file size is within the maximum supported size.
    /// </summary>
    /// <param name="fileSize">The file size to check in bytes.</param>
    /// <returns>
    /// <c>true</c> if the file size is supported or no limit is defined; otherwise, <c>false</c>.
    /// </returns>
    public bool SupportsFileSize(long fileSize)
    {
        if (MaxFileSize is null)
            return true;

        return fileSize <= MaxFileSize.Value;
    }

    /// <summary>
    /// Determines whether the specified region is supported.
    /// </summary>
    /// <param name="region">The region identifier to check (case-insensitive).</param>
    /// <returns>
    /// <c>true</c> if the region is supported or no regions are defined; otherwise, <c>false</c>.
    /// </returns>
    public bool SupportsRegion(string region)
    {
        if (SupportedRegions.Count == 0)
            return true;

        return SupportedRegions.Contains(region, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets a capability score from 0-100 indicating the overall feature richness of the CDN.
    /// </summary>
    public int CapabilityScore
    {
        get
        {
            int score = 0;
            if (SupportsEdgeCaching) score += 15;
            if (SupportsGeoRestriction) score += 10;
            if (SupportsPurge) score += 10;
            if (SupportsSignedUrls) score += 15;
            if (SupportsHttps) score += 10;
            if (SupportsHttp2) score += 8;
            if (SupportsHttp3) score += 7;
            if (SupportsCompression) score += 8;
            if (SupportsCustomHeaders) score += 5;
            if (SupportsOriginShield) score += 7;
            if (SupportsRealTimeMetrics) score += 5;
            return score;
        }
    }
}
