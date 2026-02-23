using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.FileExtension.OsIntegration;

/// <summary>
/// Represents a Windows ProgID (Programmatic Identifier) registration for a DWVD
/// file extension. ProgIDs associate file types with applications and define the
/// shell verbs (context menu actions) available for the file type.
/// </summary>
/// <remarks>
/// The primary ProgID <c>DataWarehouse.DwvdFile</c> is used for .dwvd files.
/// Secondary extensions (.dwvd.snap, .dwvd.delta, .dwvd.meta, .dwvd.lock) each
/// receive their own ProgID (e.g., <c>DataWarehouse.DwvdSnapshot</c>) with an
/// appropriate subset of shell verbs.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 79: Windows ProgID (FEXT-02)")]
public readonly struct WindowsProgId
{
    /// <summary>Primary ProgID constant for the .dwvd file type.</summary>
    public const string PrimaryProgId = "DataWarehouse.DwvdFile";

    /// <summary>Default icon resource path for DWVD files.</summary>
    public const string DefaultIconPath = "%ProgramFiles%\\DataWarehouse\\dw.exe,0";

    /// <summary>The ProgID string registered in the Windows registry (e.g., "DataWarehouse.DwvdFile").</summary>
    public string ProgId { get; }

    /// <summary>Human-readable type name shown in Windows Explorer (e.g., "DataWarehouse Virtual Disk").</summary>
    public string FriendlyTypeName { get; }

    /// <summary>MIME content type associated with this ProgID.</summary>
    public string ContentType { get; }

    /// <summary>Windows perceived type hint for indexing and grouping (e.g., "document").</summary>
    public string PerceivedType { get; }

    /// <summary>
    /// Icon resource path in the format "path,index". Defaults to <see cref="DefaultIconPath"/>.
    /// </summary>
    public string DefaultIcon { get; }

    /// <summary>The file extension this ProgID maps to (e.g., ".dwvd" or ".dwvd.snap").</summary>
    public string Extension { get; }

    /// <summary>The shell verbs (context menu actions) registered for this ProgID.</summary>
    public IReadOnlyList<WindowsShellVerb> ShellVerbs { get; }

    private WindowsProgId(
        string progId,
        string friendlyTypeName,
        string contentType,
        string perceivedType,
        string defaultIcon,
        string extension,
        IReadOnlyList<WindowsShellVerb> shellVerbs)
    {
        ProgId = progId;
        FriendlyTypeName = friendlyTypeName;
        ContentType = contentType;
        PerceivedType = perceivedType;
        DefaultIcon = defaultIcon;
        Extension = extension;
        ShellVerbs = shellVerbs;
    }

    /// <summary>
    /// Creates the primary ProgID registration for the .dwvd file extension.
    /// Includes Open (default), Inspect, and Verify shell verbs.
    /// </summary>
    /// <param name="iconPath">
    /// Optional custom icon resource path. Defaults to <see cref="DefaultIconPath"/>.
    /// </param>
    /// <returns>A <see cref="WindowsProgId"/> configured for the primary .dwvd extension.</returns>
    public static WindowsProgId CreatePrimary(string? iconPath = null) => new(
        PrimaryProgId,
        DwvdMimeType.FormatName,
        DwvdMimeType.MimeType,
        "document",
        iconPath ?? DefaultIconPath,
        DwvdMimeType.PrimaryExtension,
        WindowsShellHandler.GetPrimaryVerbs());

    /// <summary>
    /// Creates a ProgID registration for a secondary DWVD extension.
    /// The ProgID is derived from the extension kind (e.g., <c>DataWarehouse.DwvdSnapshot</c>).
    /// Shell verbs are limited to the operations appropriate for each secondary type.
    /// </summary>
    /// <param name="kind">The secondary extension kind.</param>
    /// <param name="iconPath">
    /// Optional custom icon resource path. Defaults to <see cref="DefaultIconPath"/>.
    /// </param>
    /// <returns>A <see cref="WindowsProgId"/> configured for the specified secondary extension.</returns>
    public static WindowsProgId CreateForSecondary(SecondaryExtensionKind kind, string? iconPath = null) => new(
        GetSecondaryProgId(kind),
        SecondaryExtensions.GetDescription(kind),
        SecondaryExtensions.GetMimeType(kind),
        "document",
        iconPath ?? DefaultIconPath,
        SecondaryExtensions.GetExtension(kind),
        WindowsShellHandler.GetSecondaryVerbs(kind));

    /// <summary>
    /// Maps a secondary extension kind to its ProgID string.
    /// </summary>
    private static string GetSecondaryProgId(SecondaryExtensionKind kind) => kind switch
    {
        SecondaryExtensionKind.Snapshot => "DataWarehouse.DwvdSnapshot",
        SecondaryExtensionKind.Delta => "DataWarehouse.DwvdDelta",
        SecondaryExtensionKind.Metadata => "DataWarehouse.DwvdMetadata",
        SecondaryExtensionKind.Lock => "DataWarehouse.DwvdLock",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown secondary extension kind."),
    };
}
