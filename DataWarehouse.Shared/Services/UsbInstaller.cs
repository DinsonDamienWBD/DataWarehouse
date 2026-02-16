// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;

namespace DataWarehouse.Shared.Services;

/// <summary>
/// Handles installation of DataWarehouse from USB/portable media to local disk.
/// Validates the USB source, copies the directory tree, remaps configuration paths,
/// and verifies the resulting installation.
/// </summary>
public sealed class UsbInstaller
{
    private static readonly HashSet<string> SkipDirectories =
        new(StringComparer.OrdinalIgnoreCase) { "obj", "bin", ".git", ".vs" };

    /// <summary>
    /// Validates that a USB source directory contains a valid DataWarehouse installation.
    /// </summary>
    /// <param name="sourcePath">Path to the USB source directory.</param>
    /// <returns>Validation result with details about the source.</returns>
    public UsbValidationResult ValidateUsbSource(string sourcePath)
    {
        var errors = new List<string>();
        var pluginCount = 0;
        var hasData = false;
        long totalSize = 0;
        string? version = null;

        // Check directory exists
        if (!Directory.Exists(sourcePath))
        {
            return new UsbValidationResult
            {
                IsValid = false,
                Errors = new List<string> { $"Source directory does not exist: {sourcePath}" }
            };
        }

        // Check for DataWarehouse binaries
        var dwFiles = Directory.GetFiles(sourcePath, "DataWarehouse.*")
            .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (dwFiles.Count == 0)
        {
            errors.Add("No DataWarehouse binaries (*.exe or *.dll) found in source directory.");
        }

        // Check for config directory
        var configDir = Path.Combine(sourcePath, "config");
        if (Directory.Exists(configDir))
        {
            var jsonFiles = Directory.GetFiles(configDir, "*.json");
            if (jsonFiles.Length == 0)
            {
                errors.Add("Config directory exists but contains no JSON files.");
            }

            // Try to read version from config
            var dwConfig = Path.Combine(configDir, "datawarehouse.json");
            if (File.Exists(dwConfig))
            {
                try
                {
                    var json = File.ReadAllText(dwConfig);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("version", out var versionProp))
                    {
                        version = versionProp.GetString();
                    }
                }
                catch { /* ignore parse errors */ }
            }
        }
        else
        {
            // Check for appsettings as fallback config
            var appSettings = Directory.GetFiles(sourcePath, "appsettings*.json");
            if (appSettings.Length == 0)
            {
                errors.Add("No configuration files found (no config/ directory and no appsettings.json).");
            }
        }

        // Check for plugins
        var pluginDir = Path.Combine(sourcePath, "plugins");
        if (Directory.Exists(pluginDir))
        {
            pluginCount = Directory.GetDirectories(pluginDir).Length;
        }

        // Check for data directory
        var dataDir = Path.Combine(sourcePath, "data");
        hasData = Directory.Exists(dataDir) && Directory.EnumerateFileSystemEntries(dataDir).Any();

        // Calculate total size
        try
        {
            totalSize = CalculateDirectorySize(sourcePath);
        }
        catch { /* ignore size calculation errors */ }

        return new UsbValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            SourceVersion = version,
            PluginCount = pluginCount,
            HasData = hasData,
            TotalSizeBytes = totalSize
        };
    }

    /// <summary>
    /// Installs DataWarehouse from a USB source to a local target directory.
    /// </summary>
    /// <param name="config">USB installation configuration.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Installation result with details.</returns>
    public async Task<UsbInstallResult> InstallFromUsbAsync(
        UsbInstallConfiguration config,
        IProgress<UsbInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var totalFilesCopied = 0;
        long totalBytesCopied = 0;

        // Step 0: Validate source
        progress?.Report(new UsbInstallProgress("Validating", 0, "Validating USB source..."));
        var validation = ValidateUsbSource(config.SourcePath);
        if (!validation.IsValid)
        {
            return new UsbInstallResult
            {
                Success = false,
                Message = "USB source validation failed: " + string.Join("; ", validation.Errors),
                Warnings = warnings,
                Verification = null
            };
        }

        try
        {
            // Step 1: Create target directories
            progress?.Report(new UsbInstallProgress("Creating directories", 5, "Creating target directories..."));
            Directory.CreateDirectory(config.TargetPath);
            Directory.CreateDirectory(Path.Combine(config.TargetPath, "data"));
            Directory.CreateDirectory(Path.Combine(config.TargetPath, "config"));
            Directory.CreateDirectory(Path.Combine(config.TargetPath, "plugins"));
            Directory.CreateDirectory(Path.Combine(config.TargetPath, "logs"));

            // Step 2: Copy binaries
            progress?.Report(new UsbInstallProgress("Copying binaries", 15, "Copying binary files..."));
            var binaryExtensions = new[] { ".exe", ".dll", ".pdb", ".json", ".runtimeconfig.json", ".deps.json" };
            foreach (var file in Directory.GetFiles(config.SourcePath))
            {
                ct.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(file);
                if (binaryExtensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase)))
                {
                    var dest = Path.Combine(config.TargetPath, Path.GetFileName(file));
                    File.Copy(file, dest, overwrite: true);
                    totalFilesCopied++;
                    totalBytesCopied += new FileInfo(file).Length;
                }
            }

            // Step 3: Copy config
            progress?.Report(new UsbInstallProgress("Copying config", 35, "Copying configuration files..."));
            var sourceConfig = Path.Combine(config.SourcePath, "config");
            if (Directory.Exists(sourceConfig))
            {
                var (files, bytes) = CopyDirectoryRecursive(
                    sourceConfig, Path.Combine(config.TargetPath, "config"), ct);
                totalFilesCopied += files;
                totalBytesCopied += bytes;
            }

            // Step 4: Copy plugins
            progress?.Report(new UsbInstallProgress("Copying plugins", 50, "Copying plugins..."));
            var sourcePlugins = Path.Combine(config.SourcePath, "plugins");
            if (Directory.Exists(sourcePlugins))
            {
                var (files, bytes) = CopyDirectoryRecursive(
                    sourcePlugins, Path.Combine(config.TargetPath, "plugins"), ct);
                totalFilesCopied += files;
                totalBytesCopied += bytes;
            }

            // Step 5: Copy data (optional)
            if (config.CopyData)
            {
                progress?.Report(new UsbInstallProgress("Copying data", 65, "Copying data files..."));
                var sourceData = Path.Combine(config.SourcePath, "data");
                if (Directory.Exists(sourceData))
                {
                    var (files, bytes) = CopyDirectoryRecursive(
                        sourceData, Path.Combine(config.TargetPath, "data"), ct);
                    totalFilesCopied += files;
                    totalBytesCopied += bytes;
                }
            }
            else
            {
                warnings.Add("Data files were not copied (--copy-data was false).");
            }

            // Step 6: Remap configuration paths
            progress?.Report(new UsbInstallProgress("Remapping paths", 80, "Remapping configuration paths..."));
            RemapConfigurationPaths(
                Path.Combine(config.TargetPath, "config"),
                config.SourcePath,
                config.TargetPath);

            // Also remap appsettings in root
            RemapConfigurationPaths(
                config.TargetPath,
                config.SourcePath,
                config.TargetPath,
                rootOnly: true);

            // Step 7: Verify installation
            progress?.Report(new UsbInstallProgress("Verifying", 90, "Verifying installation..."));
            var verification = VerifyInstallation(config.TargetPath, config.SourcePath);

            progress?.Report(new UsbInstallProgress("Complete", 100, "Installation complete."));

            return new UsbInstallResult
            {
                Success = verification.AllPassed,
                Message = verification.AllPassed
                    ? $"Successfully installed from USB to {config.TargetPath}"
                    : $"Installation completed with verification warnings at {config.TargetPath}",
                FilesCopied = totalFilesCopied,
                BytesCopied = totalBytesCopied,
                Warnings = warnings,
                Verification = verification
            };
        }
        catch (Exception ex)
        {
            return new UsbInstallResult
            {
                Success = false,
                Message = $"USB installation failed: {ex.Message}",
                FilesCopied = totalFilesCopied,
                BytesCopied = totalBytesCopied,
                Warnings = warnings,
                Verification = null
            };
        }
    }

    /// <summary>
    /// Remaps all file paths in JSON configuration files from old base path to new base path.
    /// </summary>
    /// <param name="configDir">Directory containing configuration files.</param>
    /// <param name="oldBasePath">The old (USB source) base path to replace.</param>
    /// <param name="newBasePath">The new (local target) base path.</param>
    /// <param name="rootOnly">If true, only process files in the root of configDir (not subdirectories).</param>
    public void RemapConfigurationPaths(string configDir, string oldBasePath, string newBasePath, bool rootOnly = false)
    {
        if (!Directory.Exists(configDir)) return;

        var searchOption = rootOnly ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
        var jsonFiles = Directory.GetFiles(configDir, "*.json", searchOption);

        // Normalize paths for replacement
        var oldNormalized = oldBasePath.Replace('\\', '/').TrimEnd('/');
        var newNormalized = newBasePath.Replace('\\', '/').TrimEnd('/');
        var oldBackslash = oldBasePath.Replace('/', '\\').TrimEnd('\\');
        var newBackslash = newBasePath.Replace('/', '\\').TrimEnd('\\');

        foreach (var file in jsonFiles)
        {
            var content = File.ReadAllText(file);
            var modified = false;

            // Replace forward-slash paths
            if (content.Contains(oldNormalized, StringComparison.OrdinalIgnoreCase))
            {
                content = content.Replace(oldNormalized, newNormalized, StringComparison.OrdinalIgnoreCase);
                modified = true;
            }

            // Replace backslash paths
            if (content.Contains(oldBackslash, StringComparison.OrdinalIgnoreCase))
            {
                content = content.Replace(oldBackslash, newBackslash, StringComparison.OrdinalIgnoreCase);
                modified = true;
            }

            // Replace escaped backslash paths (common in JSON)
            var oldEscaped = oldBackslash.Replace("\\", "\\\\");
            var newEscaped = newBackslash.Replace("\\", "\\\\");
            if (content.Contains(oldEscaped, StringComparison.OrdinalIgnoreCase))
            {
                content = content.Replace(oldEscaped, newEscaped, StringComparison.OrdinalIgnoreCase);
                modified = true;
            }

            if (modified)
            {
                File.WriteAllText(file, content);
            }
        }
    }

    /// <summary>
    /// Verifies that an installation is complete and functional.
    /// </summary>
    /// <param name="installPath">The installation directory to verify.</param>
    /// <param name="oldSourcePath">Optional old source path to check for stale references.</param>
    /// <returns>Verification report.</returns>
    public UsbInstallVerification VerifyInstallation(string installPath, string? oldSourcePath = null)
    {
        var checks = new List<UsbVerificationCheck>();

        // Check directories
        CheckDir(checks, installPath, "Install root");
        CheckDir(checks, Path.Combine(installPath, "data"), "Data directory");
        CheckDir(checks, Path.Combine(installPath, "config"), "Config directory");
        CheckDir(checks, Path.Combine(installPath, "plugins"), "Plugins directory");
        CheckDir(checks, Path.Combine(installPath, "logs"), "Logs directory");

        // Check main executable
        var exeExists = File.Exists(Path.Combine(installPath, "DataWarehouse.Launcher.exe")) ||
                        File.Exists(Path.Combine(installPath, "DataWarehouse.Launcher.dll")) ||
                        Directory.GetFiles(installPath, "DataWarehouse.*.dll").Length > 0;
        checks.Add(new UsbVerificationCheck("Main executable", exeExists,
            exeExists ? "DataWarehouse binaries found" : "No DataWarehouse binaries found"));

        // Check config file
        var configPath = Path.Combine(installPath, "config", "datawarehouse.json");
        if (File.Exists(configPath))
        {
            try
            {
                var content = File.ReadAllText(configPath);
                JsonDocument.Parse(content);
                checks.Add(new UsbVerificationCheck("Config file", true, "Valid JSON"));
            }
            catch (Exception ex)
            {
                checks.Add(new UsbVerificationCheck("Config file", false, $"Invalid JSON: {ex.Message}"));
            }
        }
        else
        {
            checks.Add(new UsbVerificationCheck("Config file", false, "datawarehouse.json not found"));
        }

        // Check plugins
        var pluginDir = Path.Combine(installPath, "plugins");
        var pluginCount = Directory.Exists(pluginDir)
            ? Directory.GetDirectories(pluginDir).Length + Directory.GetFiles(pluginDir, "*.dll").Length
            : 0;
        checks.Add(new UsbVerificationCheck("Plugins", true, $"{pluginCount} plugins found"));

        // Check for stale source path references
        if (!string.IsNullOrEmpty(oldSourcePath))
        {
            var staleRefs = false;
            var configDir = Path.Combine(installPath, "config");
            if (Directory.Exists(configDir))
            {
                foreach (var file in Directory.GetFiles(configDir, "*.json"))
                {
                    var content = File.ReadAllText(file);
                    if (content.Contains(oldSourcePath, StringComparison.OrdinalIgnoreCase))
                    {
                        staleRefs = true;
                        break;
                    }
                }
            }

            checks.Add(new UsbVerificationCheck("Path remapping",
                !staleRefs,
                staleRefs ? "Stale source paths found in config" : "All paths remapped correctly"));
        }

        return new UsbInstallVerification
        {
            AllPassed = checks.All(c => c.Passed),
            Checks = checks
        };
    }

    /// <summary>
    /// Recursively copies a directory tree, skipping known non-essential directories.
    /// </summary>
    /// <returns>Tuple of (files copied, bytes copied).</returns>
    private (int FilesCopied, long BytesCopied) CopyDirectoryRecursive(
        string sourceDir, string targetDir, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);
        var totalFiles = 0;
        long totalBytes = 0;

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
            totalFiles++;
            totalBytes += new FileInfo(file).Length;
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dir);
            if (!SkipDirectories.Contains(dirName))
            {
                var (files, bytes) = CopyDirectoryRecursive(
                    dir, Path.Combine(targetDir, dirName), ct);
                totalFiles += files;
                totalBytes += bytes;
            }
        }

        return (totalFiles, totalBytes);
    }

    private static long CalculateDirectorySize(string path)
    {
        long size = 0;
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            size += new FileInfo(file).Length;
        }
        return size;
    }

    private static void CheckDir(List<UsbVerificationCheck> checks, string path, string name)
    {
        checks.Add(new UsbVerificationCheck(name,
            Directory.Exists(path),
            Directory.Exists(path) ? "Exists" : "Missing"));
    }
}

#region USB Install Configuration and Result Types

/// <summary>
/// Configuration for installing DataWarehouse from USB media.
/// </summary>
public sealed class UsbInstallConfiguration
{
    /// <summary>Path to the USB source directory.</summary>
    public required string SourcePath { get; set; }

    /// <summary>Path to the local target directory.</summary>
    public required string TargetPath { get; set; }

    /// <summary>Whether to copy data files from the source.</summary>
    public bool CopyData { get; set; } = true;

    /// <summary>Whether to register as a system service after installation.</summary>
    public bool CreateService { get; set; }

    /// <summary>Whether to enable autostart after service registration.</summary>
    public bool AutoStart { get; set; }

    /// <summary>Admin password for new installation.</summary>
    public string? AdminPassword { get; set; }
}

/// <summary>
/// Result of a USB installation operation.
/// </summary>
public sealed class UsbInstallResult
{
    /// <summary>Whether the installation succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Result message.</summary>
    public string? Message { get; init; }

    /// <summary>Number of files copied.</summary>
    public int FilesCopied { get; init; }

    /// <summary>Total bytes copied.</summary>
    public long BytesCopied { get; init; }

    /// <summary>Warning messages generated during installation.</summary>
    public List<string> Warnings { get; init; } = new();

    /// <summary>Post-install verification result.</summary>
    public UsbInstallVerification? Verification { get; init; }
}

/// <summary>
/// Result of USB source validation.
/// </summary>
public sealed class UsbValidationResult
{
    /// <summary>Whether the source is valid.</summary>
    public bool IsValid { get; init; }

    /// <summary>Validation error messages.</summary>
    public List<string> Errors { get; init; } = new();

    /// <summary>Detected source version.</summary>
    public string? SourceVersion { get; init; }

    /// <summary>Number of plugins found in source.</summary>
    public int PluginCount { get; init; }

    /// <summary>Whether the source contains data files.</summary>
    public bool HasData { get; init; }

    /// <summary>Total size of the source in bytes.</summary>
    public long TotalSizeBytes { get; init; }
}

/// <summary>
/// Post-installation verification result.
/// </summary>
public sealed class UsbInstallVerification
{
    /// <summary>Whether all verification checks passed.</summary>
    public bool AllPassed { get; init; }

    /// <summary>Individual verification check results.</summary>
    public List<UsbVerificationCheck> Checks { get; init; } = new();
}

/// <summary>
/// A single USB installation verification check.
/// </summary>
public sealed record UsbVerificationCheck(string Name, bool Passed, string Details);

/// <summary>
/// Progress information during USB installation.
/// </summary>
public sealed record UsbInstallProgress(string Step, int PercentComplete, string Message);

#endregion
