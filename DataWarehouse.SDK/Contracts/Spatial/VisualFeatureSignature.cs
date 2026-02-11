namespace DataWarehouse.SDK.Contracts.Spatial;

/// <summary>
/// Represents visual feature signatures extracted via SLAM (Simultaneous Localization and Mapping)
/// for precise spatial anchor positioning.
/// </summary>
/// <remarks>
/// Visual features enable sub-meter precision by matching camera-captured environment features
/// against previously recorded signatures. Common algorithms include ORB-SLAM, Azure Spatial Anchors,
/// ARCore Cloud Anchors, and ARKit.
/// </remarks>
public sealed class VisualFeatureSignature
{
    /// <summary>
    /// Gets the binary feature descriptor data.
    /// </summary>
    /// <remarks>
    /// Contains SLAM feature vectors such as ORB descriptors, SIFT/SURF features,
    /// or platform-specific encoded data (e.g., Azure Spatial Anchors blob).
    /// </remarks>
    public required byte[] FeatureDescriptors { get; init; }

    /// <summary>
    /// Gets the signature algorithm identifier.
    /// </summary>
    /// <remarks>
    /// Examples: "ORB-SLAM3", "Azure-Spatial-Anchors", "ARCore-Cloud", "ARKit", "Custom-SLAM".
    /// </remarks>
    public required string SignatureAlgorithm { get; init; }

    /// <summary>
    /// Gets the confidence score indicating feature stability and quality.
    /// </summary>
    /// <remarks>
    /// Range: 0.0 (very unstable, poor lighting, few features) to 1.0 (highly stable, clear features).
    /// Scores below 0.3 typically indicate unreliable anchors.
    /// </remarks>
    public required float ConfidenceScore { get; init; }

    /// <summary>
    /// Gets the timestamp when the visual features were captured.
    /// </summary>
    public required DateTime CapturedAt { get; init; }

    /// <summary>
    /// Gets optional metadata about the capture environment.
    /// </summary>
    /// <remarks>
    /// May include: camera model, lighting conditions, feature count, tracking quality, etc.
    /// </remarks>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Validates that the signature is well-formed and usable.
    /// </summary>
    /// <returns>True if signature is valid, false otherwise.</returns>
    public bool IsValid()
    {
        return FeatureDescriptors.Length > 0 &&
               !string.IsNullOrWhiteSpace(SignatureAlgorithm) &&
               ConfidenceScore >= 0.0f && ConfidenceScore <= 1.0f &&
               CapturedAt <= DateTime.UtcNow;
    }
}
