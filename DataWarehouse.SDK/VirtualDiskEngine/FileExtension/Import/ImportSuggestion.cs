using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.FileExtension.Import;

/// <summary>
/// Provides a migration suggestion for non-DWVD virtual disk files, including
/// the detected format, estimated size, a CLI import command, and a human-readable
/// message explaining how to convert the file to DWVD.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 79: Import suggestion (FEXT-06)")]
public readonly record struct ImportSuggestion
{
    /// <summary>The detected source format.</summary>
    public VirtualDiskFormat DetectedFormat { get; init; }

    /// <summary>Human-readable format name (e.g., "VMware VMDK").</summary>
    public string FormatName { get; init; }

    /// <summary>Path to the source file.</summary>
    public string SourcePath { get; init; }

    /// <summary>Estimated DWVD output file size in bytes.</summary>
    public long EstimatedDwvdSizeBytes { get; init; }

    /// <summary>
    /// Suggested CLI command to import the file, e.g.:
    /// <c>dw import --from vmdk "source.vmdk" --output "source.dwvd"</c>
    /// </summary>
    public string SuggestedCommand { get; init; }

    /// <summary>
    /// Full human-readable message explaining the detected format and how to convert it.
    /// </summary>
    public string Message { get; init; }

    // ── Factory Methods ─────────────────────────────────────────────────

    /// <summary>
    /// Creates an import suggestion for the given file path and detected format.
    /// Builds the CLI command and human-readable message automatically.
    /// </summary>
    /// <param name="filePath">Path to the source virtual disk file.</param>
    /// <param name="format">The detected format (must be importable).</param>
    /// <returns>A fully populated <see cref="ImportSuggestion"/>.</returns>
    public static ImportSuggestion CreateSuggestion(string filePath, VirtualDiskFormat format)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var formatName = VirtualDiskFormatInfo.GetDescription(format);
        // Use a switch expression to avoid ToString()+ToLowerInvariant() allocations on the hot import path (finding 797).
        var formatFlag = format switch
        {
            VirtualDiskFormat.Vhd   => "vhd",
            VirtualDiskFormat.Vhdx  => "vhdx",
            VirtualDiskFormat.Vmdk  => "vmdk",
            VirtualDiskFormat.Qcow2 => "qcow2",
            VirtualDiskFormat.Vdi   => "vdi",
            VirtualDiskFormat.Raw   => "raw",
            VirtualDiskFormat.Img   => "img",
            VirtualDiskFormat.Dwvd  => "dwvd",
            _                       => format.ToString().ToLowerInvariant()
        };
        var fileName = Path.GetFileName(filePath);
        var outputPath = Path.ChangeExtension(filePath, ".dwvd");
        var outputFileName = Path.GetFileName(outputPath);

        // Estimate: DWVD size is approximately equal to source file size
        // (actual may vary due to metadata overhead and sparse regions)
        long estimatedSize = 0;
        try
        {
            if (File.Exists(filePath))
                estimatedSize = new FileInfo(filePath).Length;
        }
        catch
        {
            // File access may fail; estimation is best-effort
        }

        var command = $"dw import --from {formatFlag} \"{fileName}\" --output \"{outputFileName}\"";
        var message = $"The file '{fileName}' appears to be a {formatName} virtual disk. " +
                      $"To convert it to DWVD format, run:\n  {command}";

        return new ImportSuggestion
        {
            DetectedFormat = format,
            FormatName = formatName,
            SourcePath = filePath,
            EstimatedDwvdSizeBytes = estimatedSize,
            SuggestedCommand = command,
            Message = message,
        };
    }

    /// <summary>
    /// Convenience method: detects the format of the file and returns an import suggestion
    /// if the file is importable (not already DWVD and not Unknown).
    /// Returns null if the file is already DWVD or the format cannot be determined.
    /// </summary>
    /// <param name="filePath">Path to the file to analyze.</param>
    /// <returns>
    /// An <see cref="ImportSuggestion"/> if the file is importable; otherwise, null.
    /// </returns>
    public static ImportSuggestion? TryCreateSuggestion(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        if (!File.Exists(filePath))
            return null;

        // Read header for magic detection
        Span<byte> header = stackalloc byte[512];
        int bytesRead;
        long fileSize;

        try
        {
            using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            fileSize = stream.Length;
            bytesRead = stream.Read(header);
        }
        catch
        {
            return null;
        }

        var extension = Path.GetExtension(filePath);
        var format = FormatDetector.DetectFormat(header[..bytesRead], extension, fileSize);

        if (!FormatDetector.IsImportable(format))
            return null;

        return CreateSuggestion(filePath, format);
    }
}
