using System.Text;
using System.Xml.Linq;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.FileExtension.OsIntegration;

/// <summary>
/// Generates macOS Uniform Type Identifier (UTI) declarations, Info.plist fragments,
/// Quick Look generator configuration, and Spotlight metadata importer configuration
/// for DWVD file type registration on macOS.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 79: macOS UTI (FEXT-04)")]
public static class MacOsUti
{
    /// <summary>Primary UTI identifier for DWVD files.</summary>
    public const string PrimaryUti = "com.datawarehouse.dwvd";

    /// <summary>UTI for DWVD snapshot files (.dwvd.snap).</summary>
    public const string SnapshotUti = "com.datawarehouse.dwvd.snapshot";

    /// <summary>UTI for DWVD delta files (.dwvd.delta).</summary>
    public const string DeltaUti = "com.datawarehouse.dwvd.delta";

    /// <summary>UTI for DWVD metadata sidecar files (.dwvd.meta).</summary>
    public const string MetadataUti = "com.datawarehouse.dwvd.metadata";

    /// <summary>UTI for DWVD lock files (.dwvd.lock).</summary>
    public const string LockUti = "com.datawarehouse.dwvd.lock";

    /// <summary>
    /// Gets all UTI identifiers (primary + secondary).
    /// </summary>
    public static IReadOnlyList<string> AllUtis { get; } = new[]
    {
        PrimaryUti,
        SnapshotUti,
        DeltaUti,
        MetadataUti,
        LockUti,
    };

    /// <summary>
    /// Generates the UTExportedTypeDeclarations array fragment for an Info.plist file.
    /// Includes the primary DWVD UTI and all secondary UTIs with correct conformance
    /// and tag specifications.
    /// </summary>
    /// <returns>An XML plist fragment string containing UTExportedTypeDeclarations.</returns>
    public static string BuildInfoPlistFragment()
    {
        var doc = new XDocument();
        var root = new XElement("plist", new XAttribute("version", "1.0"));

        var outerDict = new XElement("dict");
        outerDict.Add(new XElement("key", "UTExportedTypeDeclarations"));

        var array = new XElement("array");

        // Primary UTI
        array.Add(BuildUtiDict(
            PrimaryUti,
            DwvdMimeType.FormatName,
            new[] { "public.data", "public.disk-image" },
            "dwvd",
            DwvdMimeType.MimeType));

        // Secondary UTIs
        foreach (var kind in SecondaryExtensions.All)
        {
            var uti = GetUtiForKind(kind);
            var description = SecondaryExtensions.GetDescription(kind);
            var ext = SecondaryExtensions.GetExtension(kind);
            var mimeType = SecondaryExtensions.GetMimeType(kind);

            // Strip leading dot and "dwvd." prefix to get the bare secondary extension part
            var bareExt = ext.TrimStart('.');

            array.Add(BuildUtiDict(
                uti,
                description,
                new[] { PrimaryUti, "public.data" },
                bareExt,
                mimeType));
        }

        outerDict.Add(array);
        root.Add(outerDict);
        doc.Add(root);

        return doc.ToString(SaveOptions.None);
    }

    /// <summary>
    /// Generates a Quick Look generator Info.plist fragment with supported content types
    /// and default preview dimensions.
    /// </summary>
    /// <returns>An XML plist fragment string for Quick Look configuration.</returns>
    public static string BuildQuickLookGeneratorPlist()
    {
        var doc = new XDocument();
        var root = new XElement("plist", new XAttribute("version", "1.0"));

        var dict = new XElement("dict");

        // QLSupportedContentTypes
        dict.Add(new XElement("key", "QLSupportedContentTypes"));
        var typesArray = new XElement("array");
        foreach (var uti in AllUtis)
        {
            typesArray.Add(new XElement("string", uti));
        }
        dict.Add(typesArray);

        // QLNeedsApplicationBundleIdentifier
        dict.Add(new XElement("key", "QLNeedsApplicationBundleIdentifier"));
        dict.Add(new XElement("false"));

        // QLPreviewWidth / QLPreviewHeight
        dict.Add(new XElement("key", "QLPreviewWidth"));
        dict.Add(new XElement("integer", "800"));
        dict.Add(new XElement("key", "QLPreviewHeight"));
        dict.Add(new XElement("integer", "600"));

        root.Add(dict);
        doc.Add(root);

        return doc.ToString(SaveOptions.None);
    }

    /// <summary>
    /// Generates a Spotlight metadata importer Info.plist fragment that maps
    /// DWVD metadata fields to Spotlight attributes.
    /// </summary>
    /// <returns>An XML plist fragment string for Spotlight metadata importer configuration.</returns>
    public static string BuildMdImporterPlist()
    {
        var doc = new XDocument();
        var root = new XElement("plist", new XAttribute("version", "1.0"));

        var dict = new XElement("dict");

        // CFBundleDocumentTypes
        dict.Add(new XElement("key", "CFBundleDocumentTypes"));
        var docTypesArray = new XElement("array");

        var docTypeDict = new XElement("dict");
        docTypeDict.Add(new XElement("key", "CFBundleTypeName"));
        docTypeDict.Add(new XElement("string", DwvdMimeType.FormatName));
        docTypeDict.Add(new XElement("key", "LSItemContentTypes"));

        var contentTypesArray = new XElement("array");
        foreach (var uti in AllUtis)
        {
            contentTypesArray.Add(new XElement("string", uti));
        }
        docTypeDict.Add(contentTypesArray);
        docTypesArray.Add(docTypeDict);
        dict.Add(docTypesArray);

        // Spotlight attribute mappings
        dict.Add(new XElement("key", "com.apple.metadata.importer.mapping"));
        var mappingDict = new XElement("dict");

        // Volume label -> kMDItemDisplayName
        mappingDict.Add(new XElement("key", "kMDItemDisplayName"));
        mappingDict.Add(new XElement("string", "dwvd.volume.label"));

        // Volume UUID -> kMDItemIdentifier
        mappingDict.Add(new XElement("key", "kMDItemIdentifier"));
        mappingDict.Add(new XElement("string", "dwvd.volume.uuid"));

        // Block count -> kMDItemFSSize (approximate)
        mappingDict.Add(new XElement("key", "kMDItemFSSize"));
        mappingDict.Add(new XElement("string", "dwvd.volume.block_count"));

        // Format version -> kMDItemVersion
        mappingDict.Add(new XElement("key", "kMDItemVersion"));
        mappingDict.Add(new XElement("string", "dwvd.format.version"));

        // Creation date -> kMDItemContentCreationDate
        mappingDict.Add(new XElement("key", "kMDItemContentCreationDate"));
        mappingDict.Add(new XElement("string", "dwvd.volume.created"));

        dict.Add(mappingDict);

        root.Add(dict);
        doc.Add(root);

        return doc.ToString(SaveOptions.None);
    }

    /// <summary>
    /// Maps a file extension to its corresponding macOS UTI string.
    /// </summary>
    /// <param name="extension">The file extension including the leading dot (e.g., ".dwvd").</param>
    /// <returns>The UTI string, or null if the extension is not recognized.</returns>
    public static string? GetUtiForExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return null;

        var normalized = extension.ToLowerInvariant();

        if (normalized == DwvdMimeType.PrimaryExtension)
            return PrimaryUti;

        if (SecondaryExtensions.TryParse(normalized, out var kind))
            return GetUtiForKind(kind);

        return null;
    }

    /// <summary>
    /// Generates a bash script that registers DWVD UTIs with macOS Launch Services
    /// using the lsregister command. The script accepts an optional .app bundle path.
    /// </summary>
    /// <returns>A bash script string for UTI registration.</returns>
    public static string BuildLaunchServicesRegistrationScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine();
        sb.AppendLine("# DataWarehouse DWVD UTI registration for macOS (idempotent)");
        sb.AppendLine();
        sb.AppendLine("LSREGISTER=\"/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister\"");
        sb.AppendLine();
        sb.AppendLine("APP_BUNDLE=\"${1:-/Applications/DataWarehouse.app}\"");
        sb.AppendLine();
        sb.AppendLine("if [ ! -x \"${LSREGISTER}\" ]; then");
        sb.AppendLine("  echo \"Error: lsregister not found at ${LSREGISTER}\" >&2");
        sb.AppendLine("  exit 1");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("if [ ! -d \"${APP_BUNDLE}\" ]; then");
        sb.AppendLine("  echo \"Error: App bundle not found at ${APP_BUNDLE}\" >&2");
        sb.AppendLine("  exit 1");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("# Register the app bundle with Launch Services");
        sb.AppendLine("\"${LSREGISTER}\" -f \"${APP_BUNDLE}\"");
        sb.AppendLine();
        sb.Append("echo \"DataWarehouse UTIs registered from ${APP_BUNDLE}.\"");
        return sb.ToString();
    }

    private static string GetUtiForKind(SecondaryExtensionKind kind) => kind switch
    {
        SecondaryExtensionKind.Snapshot => SnapshotUti,
        SecondaryExtensionKind.Delta => DeltaUti,
        SecondaryExtensionKind.Metadata => MetadataUti,
        SecondaryExtensionKind.Lock => LockUti,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown secondary extension kind."),
    };

    private static XElement BuildUtiDict(
        string uti,
        string description,
        string[] conformsTo,
        string fileExtension,
        string mimeType)
    {
        var dict = new XElement("dict");

        dict.Add(new XElement("key", "UTTypeIdentifier"));
        dict.Add(new XElement("string", uti));

        dict.Add(new XElement("key", "UTTypeDescription"));
        dict.Add(new XElement("string", description));

        dict.Add(new XElement("key", "UTTypeConformsTo"));
        var conformsArray = new XElement("array");
        foreach (var c in conformsTo)
        {
            conformsArray.Add(new XElement("string", c));
        }
        dict.Add(conformsArray);

        dict.Add(new XElement("key", "UTTypeTagSpecification"));
        var tagDict = new XElement("dict");

        tagDict.Add(new XElement("key", "public.filename-extension"));
        var extArray = new XElement("array");
        extArray.Add(new XElement("string", fileExtension));
        tagDict.Add(extArray);

        tagDict.Add(new XElement("key", "public.mime-type"));
        tagDict.Add(new XElement("string", mimeType));

        dict.Add(tagDict);

        return dict;
    }
}
