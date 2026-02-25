using System.Text;
using System.Xml.Linq;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.FileExtension.OsIntegration;

/// <summary>
/// Generates Linux freedesktop.org shared-mime-info XML, .desktop file entries,
/// and install scripts for DWVD file type registration on Linux systems.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 79: Linux MIME info (FEXT-03)")]
public static class LinuxMimeInfo
{
    private static readonly XNamespace SharedMimeInfoNs =
        "http://www.freedesktop.org/standards/shared-mime-info";

    /// <summary>
    /// Generates valid freedesktop.org shared-mime-info XML for the DWVD format.
    /// Includes glob patterns for all extensions, magic match at offset 0,
    /// and sub-class-of annotation.
    /// </summary>
    /// <returns>A complete shared-mime-info XML document as a string.</returns>
    public static string BuildSharedMimeInfoXml()
    {
        var mimeType = new XElement(SharedMimeInfoNs + "mime-type",
            new XAttribute("type", DwvdMimeType.MimeType));

        mimeType.Add(new XElement(SharedMimeInfoNs + "comment", DwvdMimeType.FormatName));
        mimeType.Add(new XElement(SharedMimeInfoNs + "comment",
            new XAttribute(XNamespace.Xml + "lang", "en"),
            DwvdMimeType.FormatName));
        mimeType.Add(new XElement(SharedMimeInfoNs + "acronym", "DWVD"));
        mimeType.Add(new XElement(SharedMimeInfoNs + "expanded-acronym", DwvdMimeType.FormatName));

        // Primary extension glob
        mimeType.Add(new XElement(SharedMimeInfoNs + "glob",
            new XAttribute("pattern", "*" + DwvdMimeType.PrimaryExtension)));

        // Secondary extension globs
        foreach (var kind in SecondaryExtensions.All)
        {
            var ext = SecondaryExtensions.GetExtension(kind);
            mimeType.Add(new XElement(SharedMimeInfoNs + "glob",
                new XAttribute("pattern", "*" + ext)));
        }

        // Magic match: 4-byte "DWVD" at offset 0
        mimeType.Add(new XElement(SharedMimeInfoNs + "magic",
            new XAttribute("priority", "60"),
            new XElement(SharedMimeInfoNs + "match",
                new XAttribute("type", "string"),
                new XAttribute("value", "DWVD"),
                new XAttribute("offset", "0"))));

        mimeType.Add(new XElement(SharedMimeInfoNs + "sub-class-of",
            new XAttribute("type", "application/octet-stream")));

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(SharedMimeInfoNs + "mime-info", mimeType));

        return doc.Declaration + Environment.NewLine + doc.Root;
    }

    /// <summary>
    /// Generates a freedesktop.org .desktop file entry for DWVD file association.
    /// </summary>
    /// <param name="dwCliPath">Path or command name for the DataWarehouse CLI (default: "dw").</param>
    /// <returns>A .desktop file contents string.</returns>
    public static string BuildDesktopFileEntry(string dwCliPath = "dw")
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Desktop Entry]");
        sb.AppendLine("Type=Application");
        sb.AppendLine("Name=DataWarehouse");
        sb.AppendLine("Comment=Open DataWarehouse Virtual Disk files");
        sb.Append("Exec=").Append(dwCliPath).AppendLine(" open %f");
        sb.Append("MimeType=").Append(DwvdMimeType.MimeType).AppendLine(";");
        sb.AppendLine("Terminal=false");
        sb.Append("NoDisplay=true");
        return sb.ToString();
    }

    /// <summary>
    /// Generates an idempotent bash install script that registers the DWVD MIME type
    /// and desktop entry on Linux systems. Works without root privileges by using
    /// user-local directories (~/.local/share).
    /// </summary>
    /// <returns>A bash script string.</returns>
    public static string BuildInstallScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine();
        sb.AppendLine("# DataWarehouse DWVD MIME type registration (idempotent)");
        sb.AppendLine();
        sb.AppendLine("MIME_DIR=\"${HOME}/.local/share/mime\"");
        sb.AppendLine("APP_DIR=\"${HOME}/.local/share/applications\"");
        sb.AppendLine("PACKAGE_DIR=\"${MIME_DIR}/packages\"");
        sb.AppendLine();
        sb.AppendLine("mkdir -p \"${PACKAGE_DIR}\"");
        sb.AppendLine("mkdir -p \"${APP_DIR}\"");
        sb.AppendLine();
        sb.AppendLine("# Install shared-mime-info XML");
        sb.AppendLine("cat > \"${PACKAGE_DIR}/datawarehouse-dwvd.xml\" << 'MIMEXML'");
        sb.AppendLine(BuildSharedMimeInfoXml());
        sb.AppendLine("MIMEXML");
        sb.AppendLine();
        sb.AppendLine("update-mime-database \"${MIME_DIR}\"");
        sb.AppendLine();
        sb.AppendLine("# Install desktop entry");
        sb.AppendLine("cat > \"${APP_DIR}/datawarehouse.desktop\" << 'DESKTOP'");
        sb.AppendLine(BuildDesktopFileEntry());
        sb.AppendLine("DESKTOP");
        sb.AppendLine();
        sb.AppendLine("update-desktop-database \"${APP_DIR}\"");
        sb.AppendLine();
        sb.Append("echo \"DataWarehouse DWVD MIME type registered successfully.\"");
        return sb.ToString();
    }
}
