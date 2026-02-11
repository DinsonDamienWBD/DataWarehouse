namespace DataWarehouse.SDK.Contracts.Spatial;

/// <summary>
/// Represents a spatial anchor binding a KnowledgeObject to a physical location.
/// </summary>
/// <remarks>
/// Spatial anchors combine GPS-based coarse positioning with SLAM-based visual feature signatures
/// to enable sub-meter precision for augmented reality and location-based data access.
/// </remarks>
public sealed class SpatialAnchor
{
    /// <summary>
    /// Gets the unique anchor identifier.
    /// </summary>
    public required string AnchorId { get; init; }

    /// <summary>
    /// Gets the ID of the KnowledgeObject bound to this anchor.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Gets the GPS position providing coarse location (typically 5-50m accuracy).
    /// </summary>
    public required GpsCoordinate GpsPosition { get; init; }

    /// <summary>
    /// Gets the visual feature signature for fine-grained positioning (sub-meter precision).
    /// </summary>
    /// <remarks>
    /// Null if anchor was created without visual features (GPS-only mode).
    /// </remarks>
    public VisualFeatureSignature? VisualFeatures { get; init; }

    /// <summary>
    /// Gets the timestamp when the anchor was created.
    /// </summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>
    /// Gets the timestamp when the anchor expires and should be deleted.
    /// </summary>
    public required DateTime ExpiresAt { get; init; }

    /// <summary>
    /// Gets whether the anchor is currently expired.
    /// </summary>
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

    /// <summary>
    /// Gets extensible metadata properties for the anchor.
    /// </summary>
    /// <remarks>
    /// May include: creator user ID, access control tags, environment type (indoor/outdoor),
    /// floor level, building ID, or application-specific data.
    /// </remarks>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Gets whether this anchor supports sub-meter precision via visual features.
    /// </summary>
    public bool HasVisualFeatures => VisualFeatures != null && VisualFeatures.IsValid();

    /// <summary>
    /// Gets the expected positioning precision in meters.
    /// </summary>
    /// <remarks>
    /// GPS-only anchors: 5-50 meters depending on satellite visibility.
    /// GPS + visual features: 0.1-1 meter depending on feature quality.
    /// </remarks>
    public double EstimatedPrecisionMeters =>
        HasVisualFeatures
            ? VisualFeatures!.ConfidenceScore >= 0.7f ? 0.5 : 1.0
            : 10.0;
}
