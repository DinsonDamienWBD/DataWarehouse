using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.FileExtension.Import;

/// <summary>
/// Reports the progress of an import operation, including the current phase,
/// bytes processed, and percentage completion.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 79: Import progress (FEXT-07)")]
public readonly record struct ImportProgress
{
    /// <summary>Percentage complete (0-100).</summary>
    public double PercentComplete { get; init; }

    /// <summary>Number of bytes processed so far.</summary>
    public long BytesProcessed { get; init; }

    /// <summary>Total bytes to process.</summary>
    public long TotalBytes { get; init; }

    /// <summary>
    /// Current import phase: "detecting", "creating", "copying", or "finalizing".
    /// </summary>
    public string Phase { get; init; }
}

/// <summary>
/// Represents the result of a virtual disk import operation, including success status,
/// output path, size metrics, and timing information.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 79: Import result (FEXT-07)")]
public readonly record struct ImportResult
{
    /// <summary>Whether the import completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Path to the created .dwvd file.</summary>
    public string OutputPath { get; init; }

    /// <summary>Detected source format.</summary>
    public VirtualDiskFormat SourceFormat { get; init; }

    /// <summary>Original file size in bytes.</summary>
    public long SourceSizeBytes { get; init; }

    /// <summary>Resulting .dwvd file size in bytes.</summary>
    public long DwvdSizeBytes { get; init; }

    /// <summary>Total import duration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Error message (null on success).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Creates a successful import result.</summary>
    internal static ImportResult Succeeded(
        string outputPath,
        VirtualDiskFormat sourceFormat,
        long sourceSizeBytes,
        long dwvdSizeBytes,
        TimeSpan duration) => new()
    {
        Success = true,
        OutputPath = outputPath,
        SourceFormat = sourceFormat,
        SourceSizeBytes = sourceSizeBytes,
        DwvdSizeBytes = dwvdSizeBytes,
        Duration = duration,
        ErrorMessage = null,
    };

    /// <summary>Creates a failed import result.</summary>
    internal static ImportResult Failed(
        VirtualDiskFormat sourceFormat,
        string errorMessage,
        TimeSpan duration) => new()
    {
        Success = false,
        OutputPath = string.Empty,
        SourceFormat = sourceFormat,
        SourceSizeBytes = 0,
        DwvdSizeBytes = 0,
        Duration = duration,
        ErrorMessage = errorMessage,
    };
}
