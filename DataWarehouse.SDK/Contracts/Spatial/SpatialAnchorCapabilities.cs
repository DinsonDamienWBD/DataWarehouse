namespace DataWarehouse.SDK.Contracts.Spatial;

/// <summary>
/// Defines the capabilities of a spatial anchor strategy implementation.
/// </summary>
public sealed record SpatialAnchorCapabilities
{
    /// <summary>
    /// Gets whether the strategy supports SLAM-based visual feature matching.
    /// </summary>
    public required bool SupportsSLAM { get; init; }

    /// <summary>
    /// Gets whether the strategy supports cloud-backed persistent anchors.
    /// </summary>
    /// <remarks>
    /// Cloud persistence enables anchors to be shared across devices and sessions.
    /// Examples: Azure Spatial Anchors, ARCore Cloud Anchors, Niantic Lightship.
    /// </remarks>
    public required bool SupportsCloudPersistence { get; init; }

    /// <summary>
    /// Gets whether the strategy supports local caching for offline anchor access.
    /// </summary>
    public required bool SupportsLocalCache { get; init; }

    /// <summary>
    /// Gets the minimum guaranteed positioning precision in meters.
    /// </summary>
    /// <remarks>
    /// Typical values:
    /// - GPS-only: 5-50 meters
    /// - GPS + visual features: 0.1-1 meter
    /// - High-precision RTK GPS: 0.01-0.1 meters
    /// </remarks>
    public required double MinPrecisionMeters { get; init; }

    /// <summary>
    /// Gets the maximum allowed anchor expiration duration in days.
    /// </summary>
    /// <remarks>
    /// Cloud anchor services typically limit persistence to 1-365 days.
    /// Local-only implementations may support unlimited duration.
    /// </remarks>
    public required int MaxExpirationDays { get; init; }

    /// <summary>
    /// Gets whether the strategy supports multi-user shared anchors.
    /// </summary>
    /// <remarks>
    /// Shared anchors enable collaborative AR experiences where multiple users
    /// see the same anchored content in the same physical location.
    /// </remarks>
    public required bool SupportsMultiUser { get; init; }
}
