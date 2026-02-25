namespace DataWarehouse.SDK.Contracts.Spatial;

/// <summary>
/// Represents the result of proximity verification for a spatial anchor.
/// </summary>
/// <remarks>
/// Combines GPS-based coarse positioning with optional SLAM-based visual matching
/// to determine if a user is physically present at an anchor location.
/// </remarks>
public sealed class ProximityVerificationResult
{
    /// <summary>
    /// Gets whether the GPS proximity check passed (user is within configured distance).
    /// </summary>
    public required bool IsWithinRange { get; init; }

    /// <summary>
    /// Gets the visual feature match confirmation result.
    /// </summary>
    /// <remarks>
    /// Null if visual matching was not attempted (no visual features available or not required).
    /// True if visual features matched with sufficient confidence.
    /// False if visual features did not match or confidence was too low.
    /// </remarks>
    public bool? VisualMatchConfirmed { get; init; }

    /// <summary>
    /// Gets the actual measured distance from the user's position to the anchor in meters.
    /// </summary>
    public required double DistanceMeters { get; init; }

    /// <summary>
    /// Gets the overall confidence score combining GPS and visual verification.
    /// </summary>
    /// <remarks>
    /// Range: 0.0 (verification failed) to 1.0 (high confidence match).
    /// GPS-only verification typically yields 0.5-0.7 confidence.
    /// GPS + visual match yields 0.8-1.0 confidence.
    /// </remarks>
    public required float OverallConfidence { get; init; }

    /// <summary>
    /// Gets the failure reason if verification did not succeed.
    /// </summary>
    /// <remarks>
    /// Null if verification succeeded.
    /// Examples: "Distance exceeds maximum", "Visual features do not match", "Anchor expired".
    /// </remarks>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Creates a successful verification result.
    /// </summary>
    public static ProximityVerificationResult Success(double distanceMeters, bool? visualMatch, float confidence) =>
        new()
        {
            IsWithinRange = true,
            VisualMatchConfirmed = visualMatch,
            DistanceMeters = distanceMeters,
            OverallConfidence = confidence,
            FailureReason = null
        };

    /// <summary>
    /// Creates a failed verification result.
    /// </summary>
    public static ProximityVerificationResult Failed(double distanceMeters, string reason, float confidence = 0.0f) =>
        new()
        {
            IsWithinRange = false,
            VisualMatchConfirmed = null,
            DistanceMeters = distanceMeters,
            OverallConfidence = confidence,
            FailureReason = reason
        };
}
