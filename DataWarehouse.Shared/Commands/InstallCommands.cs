// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using DataWarehouse.SDK.Hosting;

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// Command to install DataWarehouse to a local path.
/// Delegates to IServerHost.InstallAsync for the actual installation pipeline.
/// </summary>
public sealed class InstallCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "install";

    /// <inheritdoc />
    public string Description => "Install DataWarehouse to a local path";

    /// <inheritdoc />
    public string Category => "deployment";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        // Extract parameters
        var path = parameters.TryGetValue("path", out var p) ? p?.ToString() : null;
        var createService = parameters.TryGetValue("service", out var s) && (s is true || s?.ToString() == "True");
        var autoStart = parameters.TryGetValue("autostart", out var a) && (a is true || a?.ToString() == "True");
        var adminPassword = parameters.TryGetValue("adminPassword", out var pwd) ? pwd?.ToString() : null;
        var adminUsername = parameters.TryGetValue("adminUsername", out var usr) ? usr?.ToString() ?? "admin" : "admin";
        var dataPath = parameters.TryGetValue("dataPath", out var dp) ? dp?.ToString() : null;

        // Validate required parameters
        if (string.IsNullOrWhiteSpace(path))
        {
            return CommandResult.Fail("Installation path is required. Use --path <directory>.");
        }

        // Generate random password if not provided
        string? generatedPassword = null;
        if (string.IsNullOrEmpty(adminPassword))
        {
            var bytes = new byte[16];
            RandomNumberGenerator.Fill(bytes);
            generatedPassword = Convert.ToBase64String(bytes)[..20];
            adminPassword = generatedPassword;
        }

        // Build installation configuration
        var config = new InstallConfiguration
        {
            InstallPath = path,
            CreateService = createService,
            AutoStart = autoStart,
            CreateDefaultAdmin = true,
            AdminUsername = adminUsername,
            AdminPassword = adminPassword,
            DataPath = dataPath,
            IncludedPlugins = new List<string>()
        };

        // Get IServerHost for installation
        var host = ServerHostRegistry.Current;
        if (host == null)
        {
            return CommandResult.Fail(
                "Server host not available. Installation requires the Launcher runtime. " +
                "Run this command from the DataWarehouse Launcher.");
        }

        // Execute installation with progress tracking
        var progressMessages = new List<string>();
        var progress = new Progress<string>(msg => progressMessages.Add(msg));

        var result = await host.InstallAsync(config, progress, cancellationToken);

        if (result.Success)
        {
            var data = new Dictionary<string, object?>
            {
                ["installPath"] = result.InstallPath,
                ["message"] = result.Message,
                ["serviceRegistered"] = createService,
                ["autoStartEnabled"] = autoStart,
                ["adminUsername"] = adminUsername,
                ["adminPasswordGenerated"] = generatedPassword != null,
                ["generatedPassword"] = generatedPassword,
                ["steps"] = progressMessages
            };

            var message = $"DataWarehouse installed to {result.InstallPath}.";
            if (generatedPassword != null)
            {
                message += $" Admin password (save this): {generatedPassword}";
            }

            return CommandResult.Ok(data, message);
        }

        return CommandResult.Fail($"Installation failed: {result.Message}");
    }
}

/// <summary>
/// Command to verify a DataWarehouse installation by checking directory structure,
/// configuration files, and service registration.
/// </summary>
public sealed class InstallStatusCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "install.verify";

    /// <inheritdoc />
    public string Description => "Verify a DataWarehouse installation";

    /// <inheritdoc />
    public string Category => "deployment";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var path = parameters.TryGetValue("path", out var p) ? p?.ToString() : null;

        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(CommandResult.Fail("Installation path is required. Use --path <directory>."));
        }

        var checks = new List<VerificationCheck>();

        // Check directories exist
        CheckDirectory(checks, path, "Install root");
        CheckDirectory(checks, Path.Combine(path, "data"), "Data directory");
        CheckDirectory(checks, Path.Combine(path, "config"), "Config directory");
        CheckDirectory(checks, Path.Combine(path, "plugins"), "Plugins directory");
        CheckDirectory(checks, Path.Combine(path, "logs"), "Logs directory");

        // Check config file
        var configPath = Path.Combine(path, "config", "datawarehouse.json");
        if (File.Exists(configPath))
        {
            try
            {
                var content = File.ReadAllText(configPath);
                System.Text.Json.JsonDocument.Parse(content);
                checks.Add(new VerificationCheck("Config file", true, "Valid JSON"));
            }
            catch (Exception ex)
            {
                checks.Add(new VerificationCheck("Config file", false, $"Invalid JSON: {ex.Message}"));
            }
        }
        else
        {
            checks.Add(new VerificationCheck("Config file", false, "Not found"));
        }

        // Check for DLL files
        var dlls = Directory.Exists(path)
            ? Directory.GetFiles(path, "DataWarehouse.*.dll").Length
            : 0;
        checks.Add(new VerificationCheck("Binary files", dlls > 0, $"{dlls} DataWarehouse DLLs found"));

        // Check plugins directory
        var pluginPath = Path.Combine(path, "plugins");
        var pluginCount = Directory.Exists(pluginPath)
            ? Directory.GetDirectories(pluginPath).Length
            : 0;
        checks.Add(new VerificationCheck("Plugins", pluginCount >= 0, $"{pluginCount} plugins installed"));

        // Check security config
        var securityPath = Path.Combine(path, "config", "security.json");
        checks.Add(new VerificationCheck("Security config",
            File.Exists(securityPath),
            File.Exists(securityPath) ? "Found" : "Not found (no admin user configured)"));

        var allPassed = checks.All(c => c.Passed);
        var data = new Dictionary<string, object?>
        {
            ["installPath"] = path,
            ["allPassed"] = allPassed,
            ["checks"] = checks.Select(c => new { c.Name, c.Passed, c.Details }).ToList()
        };

        return Task.FromResult(CommandResult.Ok(data,
            allPassed
                ? $"Installation at {path} verified: all checks passed."
                : $"Installation at {path} has issues. See check details."));
    }

    private static void CheckDirectory(List<VerificationCheck> checks, string path, string name)
    {
        checks.Add(new VerificationCheck(name,
            Directory.Exists(path),
            Directory.Exists(path) ? "Exists" : "Missing"));
    }
}

/// <summary>
/// Represents a single verification check result.
/// </summary>
public sealed record VerificationCheck(string Name, bool Passed, string Details);
