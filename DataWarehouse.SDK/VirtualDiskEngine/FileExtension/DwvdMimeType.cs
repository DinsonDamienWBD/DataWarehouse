using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.FileExtension;

/// <summary>
/// Provides MIME type constants, file extension constants, and IANA registration
/// metadata for the DWVD (DataWarehouse Virtual Disk) file format.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 79: File extension MIME type (FEXT-01)")]
public static class DwvdMimeType
{
    /// <summary>Primary IANA MIME type for DWVD files.</summary>
    public const string MimeType = "application/vnd.datawarehouse.dwvd";

    /// <summary>Primary file extension for DWVD files.</summary>
    public const string PrimaryExtension = ".dwvd";

    /// <summary>Human-readable format name.</summary>
    public const string FormatName = "DataWarehouse Virtual Disk";

    /// <summary>
    /// All recognized file extensions for DWVD files (primary + secondary).
    /// </summary>
    public static readonly string[] AllExtensions =
    [
        PrimaryExtension,
        ".dwvd.snap",
        ".dwvd.delta",
        ".dwvd.meta",
        ".dwvd.lock",
    ];

    /// <summary>
    /// Returns the IANA MIME type registration XML template for the DWVD format.
    /// This template follows the IANA media type registration structure.
    /// </summary>
    public static string IanaRegistrationXml => """
        <?xml version="1.0" encoding="UTF-8"?>
        <mime-type-registration>
          <media-type>application</media-type>
          <subtype>vnd.datawarehouse.dwvd</subtype>
          <file-extension>.dwvd</file-extension>
          <magic-number offset="0">0x44575644</magic-number>
          <description>DataWarehouse Virtual Disk format</description>
          <reference>DataWarehouse SDK Specification v6.0</reference>
          <contact>DataWarehouse Project</contact>
        </mime-type-registration>
        """;

    /// <summary>
    /// Gets the MIME type string for the given file extension.
    /// Returns the primary MIME type for ".dwvd" and the appropriate secondary
    /// MIME type variant for recognized secondary extensions.
    /// </summary>
    /// <param name="extension">The file extension including leading dot (e.g., ".dwvd").</param>
    /// <returns>The MIME type string, or null if the extension is not recognized.</returns>
    public static string? GetMimeTypeForExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return null;

        var normalized = extension.ToLowerInvariant();

        if (normalized == PrimaryExtension)
            return MimeType;

        if (SecondaryExtensions.TryParse(normalized, out var kind))
            return SecondaryExtensions.GetMimeType(kind);

        return null;
    }

    /// <summary>
    /// Determines whether the specified file extension is a recognized DWVD extension.
    /// Recognizes the primary ".dwvd" extension and all secondary extensions.
    /// </summary>
    /// <param name="extension">The file extension including leading dot.</param>
    /// <returns>True if the extension is recognized; otherwise, false.</returns>
    public static bool IsRecognizedExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return false;

        var normalized = extension.ToLowerInvariant();

        if (normalized == PrimaryExtension)
            return true;

        return SecondaryExtensions.TryParse(normalized, out _);
    }
}
