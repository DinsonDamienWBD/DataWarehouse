using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.FileExtension;

/// <summary>
/// Enumerates the secondary extension kinds for DWVD files.
/// Each kind represents a specific file role within the DWVD ecosystem.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 79: Secondary extension definitions (FEXT-08)")]
public enum SecondaryExtensionKind
{
    /// <summary>Point-in-time snapshot of a DWVD volume (.dwvd.snap).</summary>
    Snapshot = 0,

    /// <summary>Incremental delta between two DWVD volume states (.dwvd.delta).</summary>
    Delta = 1,

    /// <summary>External metadata sidecar for a DWVD volume (.dwvd.meta).</summary>
    Metadata = 2,

    /// <summary>Advisory lock file for exclusive DWVD volume access (.dwvd.lock).</summary>
    Lock = 3,
}

/// <summary>
/// Provides metadata for DWVD secondary file extensions including their
/// extension strings, MIME types, and human-readable descriptions.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 79: Secondary extension utilities (FEXT-08)")]
public static class SecondaryExtensions
{
    private static readonly SecondaryExtensionKind[] AllValues =
    [
        SecondaryExtensionKind.Snapshot,
        SecondaryExtensionKind.Delta,
        SecondaryExtensionKind.Metadata,
        SecondaryExtensionKind.Lock,
    ];

    /// <summary>Returns all <see cref="SecondaryExtensionKind"/> values.</summary>
    public static ReadOnlySpan<SecondaryExtensionKind> All => AllValues;

    /// <summary>
    /// Gets the file extension string for the specified secondary extension kind.
    /// </summary>
    /// <param name="kind">The secondary extension kind.</param>
    /// <returns>The file extension including the leading dot (e.g., ".dwvd.snap").</returns>
    public static string GetExtension(SecondaryExtensionKind kind) => kind switch
    {
        SecondaryExtensionKind.Snapshot => ".dwvd.snap",
        SecondaryExtensionKind.Delta => ".dwvd.delta",
        SecondaryExtensionKind.Metadata => ".dwvd.meta",
        SecondaryExtensionKind.Lock => ".dwvd.lock",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown secondary extension kind."),
    };

    /// <summary>
    /// Gets the MIME type string for the specified secondary extension kind.
    /// </summary>
    /// <param name="kind">The secondary extension kind.</param>
    /// <returns>The MIME type string (e.g., "application/vnd.datawarehouse.dwvd.snapshot").</returns>
    public static string GetMimeType(SecondaryExtensionKind kind) => kind switch
    {
        SecondaryExtensionKind.Snapshot => "application/vnd.datawarehouse.dwvd.snapshot",
        SecondaryExtensionKind.Delta => "application/vnd.datawarehouse.dwvd.delta",
        SecondaryExtensionKind.Metadata => "application/vnd.datawarehouse.dwvd.metadata",
        SecondaryExtensionKind.Lock => "application/vnd.datawarehouse.dwvd.lock",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown secondary extension kind."),
    };

    /// <summary>
    /// Gets a human-readable description for the specified secondary extension kind.
    /// </summary>
    /// <param name="kind">The secondary extension kind.</param>
    /// <returns>A human-readable description of the extension's purpose.</returns>
    public static string GetDescription(SecondaryExtensionKind kind) => kind switch
    {
        SecondaryExtensionKind.Snapshot => "DataWarehouse Virtual Disk Snapshot",
        SecondaryExtensionKind.Delta => "DataWarehouse Virtual Disk Delta",
        SecondaryExtensionKind.Metadata => "DataWarehouse Virtual Disk Metadata Sidecar",
        SecondaryExtensionKind.Lock => "DataWarehouse Virtual Disk Lock File",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown secondary extension kind."),
    };

    /// <summary>
    /// Attempts to parse a file extension string into a <see cref="SecondaryExtensionKind"/>.
    /// </summary>
    /// <param name="extension">The file extension to parse (e.g., ".dwvd.snap").</param>
    /// <param name="kind">When this method returns, contains the parsed kind if successful.</param>
    /// <returns>True if the extension was recognized as a secondary DWVD extension; otherwise, false.</returns>
    public static bool TryParse(string extension, out SecondaryExtensionKind kind)
    {
        var normalized = extension?.ToLowerInvariant();
        switch (normalized)
        {
            case ".dwvd.snap":
                kind = SecondaryExtensionKind.Snapshot;
                return true;
            case ".dwvd.delta":
                kind = SecondaryExtensionKind.Delta;
                return true;
            case ".dwvd.meta":
                kind = SecondaryExtensionKind.Metadata;
                return true;
            case ".dwvd.lock":
                kind = SecondaryExtensionKind.Lock;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
