using System.Diagnostics;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.FileExtension;
using DataWarehouse.SDK.VirtualDiskEngine.FileExtension.OsIntegration;

namespace DataWarehouse.SDK.Hosting;

/// <summary>
/// Result of a shell registration or unregistration operation.
/// </summary>
public sealed record ShellRegistrationResult(
    bool Success,
    string[] RegisteredExtensions,
    string? Error = null);

/// <summary>
/// Cross-platform shell handler and file extension registration orchestrator.
/// Registers .dwvd (and secondary extensions) with the operating system so that
/// double-clicking a DWVD file launches DataWarehouse. Also registers the .dw
/// script extension.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><description>Windows: HKCU registry entries via PowerShell (non-admin)</description></item>
///   <item><description>Linux: freedesktop MIME XML, .desktop file, magic rules (user-local)</description></item>
///   <item><description>macOS: UTI plist via lsregister (user-local)</description></item>
/// </list>
/// All registration is idempotent -- safe to run multiple times.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 84: Install-time shell registration (DPLY-07, DPLY-08)")]
public static class InstallShellRegistration
{
    private const string DwScriptExtension = ".dw";
    private const string DwScriptProgId = "DataWarehouse.DwScript";
    private const string DwScriptFriendlyName = "DataWarehouse Script";

    /// <summary>
    /// All extensions registered by this orchestrator.
    /// </summary>
    private static readonly string[] AllRegisteredExtensions =
    [
        DwvdMimeType.PrimaryExtension,   // .dwvd
        ".dwvd.snap",
        ".dwvd.delta",
        ".dwvd.meta",
        ".dwvd.lock",
        DwScriptExtension,               // .dw
    ];

    /// <summary>
    /// Registers all DWVD file extensions and the .dw script extension with the
    /// current operating system. Registration is non-admin and idempotent.
    /// </summary>
    /// <param name="installPath">Path to the DataWarehouse installation directory containing dw(.exe).</param>
    /// <param name="progress">Optional progress reporter for status messages.</param>
    /// <returns>A <see cref="ShellRegistrationResult"/> indicating success and which extensions were registered.</returns>
    public static ShellRegistrationResult RegisterFileExtensions(
        string installPath,
        IProgress<string>? progress = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(installPath);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return RegisterWindows(installPath, progress);
            }

            if (OperatingSystem.IsLinux())
            {
                return RegisterLinux(installPath, progress);
            }

            if (OperatingSystem.IsMacOS())
            {
                return RegisterMacOs(installPath, progress);
            }

            return new ShellRegistrationResult(
                false,
                [],
                "Unsupported operating system for shell registration.");
        }
        catch (Exception ex)
        {
            return new ShellRegistrationResult(
                false,
                [],
                $"Shell registration failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes all DWVD and .dw file extension registrations from the current operating system.
    /// </summary>
    /// <param name="progress">Optional progress reporter for status messages.</param>
    /// <returns>A <see cref="ShellRegistrationResult"/> indicating success.</returns>
    public static ShellRegistrationResult UnregisterFileExtensions(
        IProgress<string>? progress = null)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return UnregisterWindows(progress);
            }

            if (OperatingSystem.IsLinux())
            {
                return UnregisterLinux(progress);
            }

            if (OperatingSystem.IsMacOS())
            {
                return UnregisterMacOs(progress);
            }

            return new ShellRegistrationResult(
                false,
                [],
                "Unsupported operating system for shell unregistration.");
        }
        catch (Exception ex)
        {
            return new ShellRegistrationResult(
                false,
                [],
                $"Shell unregistration failed: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Windows
    // ─────────────────────────────────────────────────────────────────────

    private static ShellRegistrationResult RegisterWindows(
        string installPath,
        IProgress<string>? progress)
    {
        progress?.Report("Registering Windows shell handlers...");

        var dwCliPath = Path.Combine(installPath, "dw.exe");

        // Build the PowerShell script for HKCU registration (non-admin)
        var sb = new StringBuilder();
        sb.AppendLine(WindowsRegistryBuilder.BuildPowerShellScript(dwCliPath));

        // Append .dw script extension registration
        sb.AppendLine();
        sb.AppendLine("# Script extension: .dw");
        sb.AppendLine($"New-Item -Path 'HKCU:\\Software\\Classes\\{DwScriptExtension}' -Force | Out-Null");
        sb.AppendLine($"Set-ItemProperty -Path 'HKCU:\\Software\\Classes\\{DwScriptExtension}' -Name '(Default)' -Value '{DwScriptProgId}'");
        sb.AppendLine($"Set-ItemProperty -Path 'HKCU:\\Software\\Classes\\{DwScriptExtension}' -Name 'Content Type' -Value 'text/x-datawarehouse-script'");
        sb.AppendLine($"Set-ItemProperty -Path 'HKCU:\\Software\\Classes\\{DwScriptExtension}' -Name 'PerceivedType' -Value 'text'");
        sb.AppendLine();
        sb.AppendLine($"New-Item -Path 'HKCU:\\Software\\Classes\\{DwScriptProgId}' -Force | Out-Null");
        sb.AppendLine($"Set-ItemProperty -Path 'HKCU:\\Software\\Classes\\{DwScriptProgId}' -Name '(Default)' -Value '{DwScriptFriendlyName}'");
        sb.AppendLine($"New-Item -Path 'HKCU:\\Software\\Classes\\{DwScriptProgId}\\shell\\open\\command' -Force | Out-Null");
        sb.AppendLine($"Set-ItemProperty -Path 'HKCU:\\Software\\Classes\\{DwScriptProgId}\\shell\\open\\command' -Name '(Default)' -Value '\"{dwCliPath}\" run \"%1\"'");

        ExecutePowerShell(sb.ToString());

        return new ShellRegistrationResult(true, AllRegisteredExtensions);
    }

    private static ShellRegistrationResult UnregisterWindows(IProgress<string>? progress)
    {
        progress?.Report("Removing Windows shell handlers...");

        var sb = new StringBuilder();
        sb.AppendLine(WindowsRegistryBuilder.BuildUninstallPowerShellScript());

        // Remove .dw script extension
        sb.AppendLine();
        sb.AppendLine($"Remove-Item -Path 'HKCU:\\Software\\Classes\\{DwScriptExtension}' -Recurse -Force");
        sb.AppendLine($"Remove-Item -Path 'HKCU:\\Software\\Classes\\{DwScriptProgId}' -Recurse -Force");

        ExecutePowerShell(sb.ToString());

        return new ShellRegistrationResult(true, AllRegisteredExtensions);
    }

    private static void ExecutePowerShell(string script)
    {
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedScript}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        process.Start();

        // Must drain stdout/stderr concurrently before WaitForExit to prevent pipe-buffer
        // deadlock when output exceeds the OS pipe buffer (~4KB) (finding P1-404).
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        process.WaitForExit(TimeSpan.FromSeconds(30));

        // Ensure reader tasks complete to avoid abandoned stream handles
        stdoutTask.Wait(TimeSpan.FromSeconds(5));
        stderrTask.Wait(TimeSpan.FromSeconds(5));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Linux
    // ─────────────────────────────────────────────────────────────────────

    private static ShellRegistrationResult RegisterLinux(
        string installPath,
        IProgress<string>? progress)
    {
        progress?.Report("Registering Linux MIME types and desktop entries...");

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var mimePackagesDir = Path.Combine(homeDir, ".local", "share", "mime", "packages");
        var applicationsDir = Path.Combine(homeDir, ".local", "share", "applications");
        var mimeDir = Path.Combine(homeDir, ".local", "share", "mime");

        // Ensure directories exist
        Directory.CreateDirectory(mimePackagesDir);
        Directory.CreateDirectory(applicationsDir);

        // Write shared-mime-info XML
        var mimeXml = LinuxMimeInfo.BuildSharedMimeInfoXml();
        File.WriteAllText(
            Path.Combine(mimePackagesDir, "datawarehouse-dwvd.xml"),
            mimeXml,
            Encoding.UTF8);

        // Write magic rules to user magic file
        var magicRule = LinuxMagicRule.BuildEtcMagicRule();
        var magicFile = Path.Combine(homeDir, ".magic");
        var existingMagic = File.Exists(magicFile) ? File.ReadAllText(magicFile) : string.Empty;
        if (!existingMagic.Contains("DataWarehouse Virtual Disk", StringComparison.Ordinal))
        {
            File.AppendAllText(magicFile, Environment.NewLine + magicRule + Environment.NewLine, Encoding.UTF8);
        }

        // Write .desktop file
        var dwCliPath = Path.Combine(installPath, "dw");
        var desktopEntry = LinuxMimeInfo.BuildDesktopFileEntry(dwCliPath);
        File.WriteAllText(
            Path.Combine(applicationsDir, "datawarehouse.desktop"),
            desktopEntry,
            Encoding.UTF8);

        // Run update-mime-database and update-desktop-database (best-effort)
        TryRunProcess("update-mime-database", [mimeDir]);
        TryRunProcess("update-desktop-database", [applicationsDir]);

        return new ShellRegistrationResult(true, AllRegisteredExtensions);
    }

    private static ShellRegistrationResult UnregisterLinux(IProgress<string>? progress)
    {
        progress?.Report("Removing Linux MIME types and desktop entries...");

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var mimeXmlPath = Path.Combine(homeDir, ".local", "share", "mime", "packages", "datawarehouse-dwvd.xml");
        var desktopPath = Path.Combine(homeDir, ".local", "share", "applications", "datawarehouse.desktop");
        var mimeDir = Path.Combine(homeDir, ".local", "share", "mime");
        var applicationsDir = Path.Combine(homeDir, ".local", "share", "applications");

        if (File.Exists(mimeXmlPath)) File.Delete(mimeXmlPath);
        if (File.Exists(desktopPath)) File.Delete(desktopPath);

        // Rebuild databases
        TryRunProcess("update-mime-database", [mimeDir]);
        TryRunProcess("update-desktop-database", [applicationsDir]);

        return new ShellRegistrationResult(true, AllRegisteredExtensions);
    }

    // ─────────────────────────────────────────────────────────────────────
    // macOS
    // ─────────────────────────────────────────────────────────────────────

    private static ShellRegistrationResult RegisterMacOs(
        string installPath,
        IProgress<string>? progress)
    {
        progress?.Report("Registering macOS UTI types...");

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var launchServicesDir = Path.Combine(homeDir, "Library", "LaunchServices");
        Directory.CreateDirectory(launchServicesDir);

        // Write UTI plist
        var plistContent = MacOsUti.BuildInfoPlistFragment();
        File.WriteAllText(
            Path.Combine(launchServicesDir, "com.datawarehouse.dwvd.plist"),
            plistContent,
            Encoding.UTF8);

        // Refresh Launch Services registration
        const string lsRegister = "/System/Library/Frameworks/CoreServices.framework/" +
                                  "Frameworks/LaunchServices.framework/Support/lsregister";
        // Use ArgumentList (not string interpolation) to prevent argument injection via
        // installPath containing spaces, semicolons, or backticks (finding P1-403).
        TryRunProcess(lsRegister, ["-f", installPath]);

        return new ShellRegistrationResult(true, AllRegisteredExtensions);
    }

    private static ShellRegistrationResult UnregisterMacOs(IProgress<string>? progress)
    {
        progress?.Report("Removing macOS UTI types...");

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var plistPath = Path.Combine(homeDir, "Library", "LaunchServices", "com.datawarehouse.dwvd.plist");

        if (File.Exists(plistPath)) File.Delete(plistPath);

        return new ShellRegistrationResult(true, AllRegisteredExtensions);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to run an external process with a list of arguments (no shell-injection risk).
    /// Failures are silently ignored since shell registration is non-fatal.
    /// </summary>
    private static void TryRunProcess(string fileName, IEnumerable<string> arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var arg in arguments)
                process.StartInfo.ArgumentList.Add(arg);

            process.Start();

            // Must drain stdout/stderr concurrently before WaitForExit to prevent pipe-buffer
            // deadlock when output exceeds the OS pipe buffer (~4KB) (finding P1-405).
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            process.WaitForExit(TimeSpan.FromSeconds(15));

            stdoutTask.Wait(TimeSpan.FromSeconds(5));
            stderrTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Best-effort: registration databases not updated but files are in place.
        }
    }
}
