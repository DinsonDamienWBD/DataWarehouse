using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.FileExtension;

/// <summary>
/// Enumerates the steps in the DWVD content detection priority chain.
/// Each step adds approximately 0.2 to the cumulative confidence score.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 79: Content detection step enum (FEXT-05)")]
public enum ContentDetectionStep
{
    /// <summary>No detection steps have passed.</summary>
    None = 0,

    /// <summary>Step 1: Magic bytes "DWVD" (0x44575644) validated at offset 0.</summary>
    Magic = 1,

    /// <summary>Step 2: Format version validated (major >= 1, spec revision > 0).</summary>
    Version = 2,

    /// <summary>Step 3: Namespace anchor "dw://" validated at offset 8.</summary>
    Namespace = 3,

    /// <summary>Step 4: Feature flags validated (no unknown bits in incompatible flags).</summary>
    Flags = 4,

    /// <summary>Step 5: Header integrity seal is non-zero (requires full superblock).</summary>
    Seal = 5,
}

/// <summary>
/// Represents the result of DWVD content detection with per-step validation
/// booleans and a cumulative confidence score from 0.0 to 1.0.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 79: Content detection result (FEXT-05)")]
public readonly struct ContentDetectionResult
{
    /// <summary>Overall detection result: true if the data contains a valid DWVD header.</summary>
    public bool IsDwvd { get; }

    /// <summary>
    /// Cumulative confidence score from 0.0 to 1.0. Each detection step adds
    /// approximately 0.2 to the score. A score of 0.2 indicates magic-only detection.
    /// </summary>
    public float Confidence { get; }

    /// <summary>Detected format major version, or 0 if not detected.</summary>
    public byte MajorVersion { get; }

    /// <summary>Detected format minor version, or 0 if not detected.</summary>
    public byte MinorVersion { get; }

    /// <summary>Detected specification revision, or 0 if not detected.</summary>
    public ushort SpecRevision { get; }

    /// <summary>Resolved MIME type string, or null if the data is not recognized as DWVD.</summary>
    public string? DetectedMimeType { get; }

    /// <summary>The highest detection step that passed successfully.</summary>
    public ContentDetectionStep HighestStep { get; }

    /// <summary>Step 1 result: true if bytes 0-3 match "DWVD" magic signature.</summary>
    public bool HasValidMagic { get; }

    /// <summary>Step 2 result: true if format version fields are within valid ranges.</summary>
    public bool HasValidVersion { get; }

    /// <summary>Step 3 result: true if namespace anchor matches "dw://" at offset 8.</summary>
    public bool HasValidNamespace { get; }

    /// <summary>Step 4 result: true if feature flags contain no unknown bits.</summary>
    public bool HasValidFlags { get; }

    /// <summary>Step 5 result: true if the header integrity seal is non-zero.</summary>
    public bool HasValidSeal { get; }

    /// <summary>Human-readable summary of the detection result.</summary>
    public string Summary { get; }

    /// <summary>
    /// Creates a new ContentDetectionResult with all fields specified.
    /// </summary>
    public ContentDetectionResult(
        bool isDwvd,
        float confidence,
        byte majorVersion,
        byte minorVersion,
        ushort specRevision,
        string? detectedMimeType,
        ContentDetectionStep highestStep,
        bool hasValidMagic,
        bool hasValidVersion,
        bool hasValidNamespace,
        bool hasValidFlags,
        bool hasValidSeal,
        string summary)
    {
        IsDwvd = isDwvd;
        Confidence = confidence;
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        SpecRevision = specRevision;
        DetectedMimeType = detectedMimeType;
        HighestStep = highestStep;
        HasValidMagic = hasValidMagic;
        HasValidVersion = hasValidVersion;
        HasValidNamespace = hasValidNamespace;
        HasValidFlags = hasValidFlags;
        HasValidSeal = hasValidSeal;
        Summary = summary;
    }

    /// <summary>
    /// Creates a result indicating the data is not a DWVD file.
    /// </summary>
    public static ContentDetectionResult NotDwvd() => new(
        isDwvd: false,
        confidence: 0f,
        majorVersion: 0,
        minorVersion: 0,
        specRevision: 0,
        detectedMimeType: null,
        highestStep: ContentDetectionStep.None,
        hasValidMagic: false,
        hasValidVersion: false,
        hasValidNamespace: false,
        hasValidFlags: false,
        hasValidSeal: false,
        summary: "Not a DWVD file: magic bytes do not match.");

    /// <inheritdoc />
    public override string ToString() => Summary;
}
