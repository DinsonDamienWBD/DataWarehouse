namespace DataWarehouse.SDK.Contracts.Spatial;

/// <summary>
/// Strategy interface for AR spatial anchoring capabilities.
/// </summary>
/// <remarks>
/// Enables binding KnowledgeObjects to physical locations using GPS coordinates
/// combined with SLAM-based visual feature signatures for sub-meter precision.
/// Used for location-based data access, augmented reality applications, and
/// proximity-based verification.
/// </remarks>
public interface ISpatialAnchorStrategy
{
    /// <summary>
    /// Gets the capabilities of this spatial anchor strategy.
    /// </summary>
    SpatialAnchorCapabilities Capabilities { get; }

    /// <summary>
    /// Creates a new spatial anchor binding an object to a physical location.
    /// </summary>
    /// <param name="objectId">The KnowledgeObject ID to anchor.</param>
    /// <param name="gpsPosition">The GPS coordinates for coarse positioning.</param>
    /// <param name="visualFeatures">Optional visual feature signature for fine positioning.</param>
    /// <param name="expirationDays">Number of days until anchor expires (limited by Capabilities.MaxExpirationDays).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created spatial anchor with assigned AnchorId.</returns>
    /// <exception cref="ArgumentException">Thrown when parameters are invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when expiration exceeds maximum or visual features not supported.</exception>
    Task<SpatialAnchor> CreateAnchorAsync(
        string objectId,
        GpsCoordinate gpsPosition,
        VisualFeatureSignature? visualFeatures,
        int expirationDays,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves an existing spatial anchor by its ID.
    /// </summary>
    /// <param name="anchorId">The anchor identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The anchor if found and not expired, null otherwise.</returns>
    Task<SpatialAnchor?> RetrieveAnchorAsync(string anchorId, CancellationToken ct = default);

    /// <summary>
    /// Verifies that a user's current position is within acceptable proximity of an anchor.
    /// </summary>
    /// <param name="anchorId">The anchor to verify proximity against.</param>
    /// <param name="currentPosition">The user's current GPS position.</param>
    /// <param name="maxDistanceMeters">Maximum acceptable distance in meters.</param>
    /// <param name="requireVisualMatch">Whether to require visual feature matching (if available).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Verification result indicating success/failure with distance and confidence.</returns>
    /// <remarks>
    /// GPS-only verification provides 5-50m accuracy with 0.5-0.7 confidence.
    /// GPS + visual matching provides 0.1-1m accuracy with 0.8-1.0 confidence.
    /// </remarks>
    Task<ProximityVerificationResult> VerifyProximityAsync(
        string anchorId,
        GpsCoordinate currentPosition,
        double maxDistanceMeters,
        bool requireVisualMatch = false,
        CancellationToken ct = default);

    /// <summary>
    /// Finds all anchors within a specified radius of a GPS position.
    /// </summary>
    /// <param name="position">The center point for the search.</param>
    /// <param name="radiusMeters">Search radius in meters.</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of nearby anchors sorted by distance (nearest first).</returns>
    /// <remarks>
    /// Expired anchors are automatically excluded from results.
    /// </remarks>
    Task<IEnumerable<SpatialAnchor>> FindNearbyAnchorsAsync(
        GpsCoordinate position,
        double radiusMeters,
        int maxResults = 50,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes a spatial anchor.
    /// </summary>
    /// <param name="anchorId">The anchor identifier to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if anchor was found and deleted, false if not found.</returns>
    Task<bool> DeleteAnchorAsync(string anchorId, CancellationToken ct = default);
}
