using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.FileExtension.OsIntegration;

/// <summary>
/// Generates /etc/magic and file(1) magic rules for DWVD file detection on Linux systems.
/// The rules detect the 4-byte "DWVD" magic signature at offset 0 and extract version bytes.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 79: Linux magic rule (FEXT-03)")]
public static class LinuxMagicRule
{
    /// <summary>
    /// Generates an /etc/magic format rule for DWVD file detection.
    /// The rule matches the 4-byte "DWVD" magic at offset 0, annotates the MIME type,
    /// file extension, and extracts version bytes at offsets 4 and 5.
    /// </summary>
    /// <returns>A string containing the /etc/magic rule.</returns>
    public static string BuildEtcMagicRule()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DataWarehouse Virtual Disk (DWVD v2.0)");
        sb.AppendLine("0\tstring\tDWVD\tDataWarehouse Virtual Disk (DWVD)");
        sb.AppendLine("!:mime\t" + DwvdMimeType.MimeType);
        sb.AppendLine("!:ext\tdwvd");
        sb.AppendLine(">4\tbyte\tx\t\\b, version %d");
        sb.Append(">5\tbyte\tx\t\\b.%d");
        return sb.ToString();
    }

    /// <summary>
    /// Generates a file(1) magic format rule for DWVD file detection.
    /// This format is compatible with the file(1) command's magic file syntax.
    /// </summary>
    /// <returns>A string containing the file(1) magic rule.</returns>
    public static string BuildFileMagicRule()
    {
        var sb = new StringBuilder();
        sb.AppendLine("#------------------------------------------------------------------------------");
        sb.AppendLine("# datawarehouse: DataWarehouse Virtual Disk (DWVD)");
        sb.AppendLine("#");
        sb.AppendLine("0\tstring\tDWVD\tDataWarehouse Virtual Disk (DWVD)");
        sb.AppendLine("!:mime\t" + DwvdMimeType.MimeType);
        sb.AppendLine("!:ext\tdwvd");
        sb.AppendLine(">4\tubyte\tx\t\\b, version %u");
        sb.Append(">5\tubyte\tx\t\\b.%u");
        return sb.ToString();
    }

    /// <summary>
    /// Generates an idempotent bash script that installs the DWVD magic rule
    /// to the user's ~/.magic file. Uses a grep guard to avoid duplicate entries.
    /// Does not require root privileges.
    /// </summary>
    /// <returns>A bash script string.</returns>
    public static string BuildMagicInstallScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine();
        sb.AppendLine("# DataWarehouse DWVD magic rule installation (idempotent)");
        sb.AppendLine();
        sb.AppendLine("MAGIC_FILE=\"${HOME}/.magic\"");
        sb.AppendLine();
        sb.AppendLine("if grep -q 'DataWarehouse Virtual Disk (DWVD v2.0)' \"${MAGIC_FILE}\" 2>/dev/null; then");
        sb.AppendLine("  echo \"DWVD magic rule already installed.\"");
        sb.AppendLine("  exit 0");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("cat >> \"${MAGIC_FILE}\" << 'MAGIC'");
        sb.AppendLine();
        sb.AppendLine(BuildEtcMagicRule());
        sb.AppendLine("MAGIC");
        sb.AppendLine();
        sb.Append("echo \"DWVD magic rule installed to ${MAGIC_FILE}.\"");
        return sb.ToString();
    }

    /// <summary>
    /// Returns the 4-byte "DWVD" magic signature as a byte array.
    /// Convenience method for testing and validation.
    /// </summary>
    /// <returns>A 4-byte array containing 0x44, 0x57, 0x56, 0x44 ("DWVD").</returns>
    public static byte[] GetMagicBytes()
    {
        return FormatConstants.DwvdAsciiBytes.ToArray();
    }
}
