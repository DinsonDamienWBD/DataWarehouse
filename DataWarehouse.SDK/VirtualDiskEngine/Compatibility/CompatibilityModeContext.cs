using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Compatibility;

/// <summary>
/// Provides compatibility mode state for opening VDE files that do not match the
/// current v2.0 native format. For v1.0 files, the context enforces read-only access
/// and enumerates degraded/available features. For v2.0 files, the context indicates
/// native mode with no restrictions.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 81: Compatibility mode context (MIGR-01)")]
public sealed class CompatibilityModeContext
{
    /// <summary>The detected source format version of the opened VDE.</summary>
    public DetectedFormatVersion SourceVersion { get; }

    /// <summary>True if the VDE is opened in read-only mode (writes disabled).</summary>
    public bool IsReadOnly { get; }

    /// <summary>True if the VDE is running in compatibility mode (not native v2.0).</summary>
    public bool IsCompatibilityMode { get; }

    /// <summary>
    /// Features unavailable in the current compatibility mode.
    /// Empty for native v2.0 mode.
    /// </summary>
    public IReadOnlyList<string> DegradedFeatures { get; }

    /// <summary>
    /// Features that remain functional in the current mode.
    /// </summary>
    public IReadOnlyList<string> AvailableFeatures { get; }

    /// <summary>
    /// User-facing hint describing how to migrate to v2.0 for full feature access.
    /// Null for native v2.0 mode.
    /// </summary>
    public string? MigrationHint { get; }

    private CompatibilityModeContext(
        DetectedFormatVersion sourceVersion,
        bool isReadOnly,
        bool isCompatibilityMode,
        IReadOnlyList<string> degradedFeatures,
        IReadOnlyList<string> availableFeatures,
        string? migrationHint)
    {
        SourceVersion = sourceVersion;
        IsReadOnly = isReadOnly;
        IsCompatibilityMode = isCompatibilityMode;
        DegradedFeatures = degradedFeatures;
        AvailableFeatures = availableFeatures;
        MigrationHint = migrationHint;
    }

    /// <summary>
    /// Well-known features that are degraded (unavailable) when opening a v1.0 VDE
    /// in compatibility mode.
    /// </summary>
    private static readonly string[] V1DegradedFeatures =
    [
        "ModuleManagement",
        "PolicyCascade",
        "ExtentBasedAllocation",
        "InodeV2Fields",
        "EncryptionRegion",
        "IntegrityTree",
        "ReplicationWatermark",
        "WormRegion",
        "ComplianceSeal",
        "StreamingRegion",
        "FabricNamespace",
        "ComputeCodeCache",
        "AuditLogRegion",
        "SnapshotManifest",
        "TagIndex",
        "CompressionDictionary",
        "MetricsLog",
        "AnonymizationTable"
    ];

    /// <summary>
    /// Well-known features that remain available when opening a v1.0 VDE
    /// in compatibility mode.
    /// </summary>
    private static readonly string[] V1AvailableFeatures =
    [
        "DataRead",
        "MetadataRead",
        "BasicSearch",
        "Export",
        "VolumeInfo",
        "BlockRead"
    ];

    /// <summary>
    /// Creates a compatibility context for a v1.0 VDE (read-only, degraded feature set).
    /// </summary>
    /// <param name="version">The detected v1.0 format version.</param>
    /// <returns>A context configured for v1.0 compatibility mode.</returns>
    public static CompatibilityModeContext ForV1(DetectedFormatVersion version)
    {
        return new CompatibilityModeContext(
            sourceVersion: version,
            isReadOnly: true,
            isCompatibilityMode: true,
            degradedFeatures: V1DegradedFeatures,
            availableFeatures: V1AvailableFeatures,
            migrationHint: "Run 'dw migrate <path>' to convert to v2.0 format for full feature access.");
    }

    /// <summary>
    /// Creates a context for a v2.0 VDE opened in native mode (no restrictions).
    /// </summary>
    /// <param name="version">The detected v2.0 format version.</param>
    /// <returns>A context configured for native v2.0 operation.</returns>
    public static CompatibilityModeContext ForV2Native(DetectedFormatVersion version)
    {
        return new CompatibilityModeContext(
            sourceVersion: version,
            isReadOnly: false,
            isCompatibilityMode: false,
            degradedFeatures: Array.Empty<string>(),
            availableFeatures: Array.Empty<string>(),
            migrationHint: null);
    }

    /// <summary>
    /// Throws <see cref="VdeFormatException"/> for an unrecognized format version.
    /// </summary>
    /// <param name="version">The detected unknown format version.</param>
    /// <exception cref="VdeFormatException">Always thrown with a descriptive message.</exception>
    public static CompatibilityModeContext ForUnknown(DetectedFormatVersion version)
    {
        throw new VdeFormatException(
            $"Unrecognized VDE format version {version.FormatDescription}. " +
            $"This software supports DWVD v1.x (compatibility mode) and v2.x (native mode). " +
            $"The file may have been created by a newer version of DataWarehouse or may be corrupted.");
    }
}

/// <summary>
/// Exception thrown when a VDE file has an unrecognized or corrupted format.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 81: VDE format exception (MIGR-01)")]
public class VdeFormatException : Exception
{
    /// <summary>Creates a new instance with the specified message.</summary>
    public VdeFormatException(string message) : base(message) { }

    /// <summary>Creates a new instance with the specified message and inner exception.</summary>
    public VdeFormatException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a default instance.</summary>
    protected VdeFormatException() : base() { }
}
