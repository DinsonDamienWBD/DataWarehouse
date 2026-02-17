// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Shared.Services;

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// Extracts profile parameter from command parameters for profile-aware service commands.
/// </summary>
file static class ProfileHelper
{
    /// <summary>
    /// Extracts the profile parameter. Returns null for "auto" or missing values.
    /// </summary>
    internal static string? GetProfile(Dictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("profile", out var profileVal) && profileVal is string profile && !string.IsNullOrWhiteSpace(profile))
        {
            return profile.ToLowerInvariant() switch
            {
                "auto" or "both" => null,
                _ => profile.ToLowerInvariant()
            };
        }
        return null;
    }
}

/// <summary>
/// Shows the status of the DataWarehouse system service.
/// Supports profile-aware service names (server/client profiles).
/// </summary>
public sealed class ServiceStatusCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "service.status";

    /// <inheritdoc />
    public string Description => "Show DataWarehouse service status";

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
        var profile = ProfileHelper.GetProfile(parameters);
        var serviceName = PlatformServiceManager.GetProfileServiceName(profile);
        var status = await PlatformServiceManager.GetServiceStatusAsync(serviceName, cancellationToken);

        var data = new Dictionary<string, object?>
        {
            ["serviceName"] = serviceName,
            ["profile"] = profile ?? "default",
            ["installed"] = status.IsInstalled,
            ["running"] = status.IsRunning,
            ["state"] = status.State,
            ["pid"] = status.PID,
            ["platform"] = GetPlatformName()
        };

        if (!status.IsInstalled)
        {
            return CommandResult.Ok(data, $"DataWarehouse service '{serviceName}' is not installed on this system.");
        }

        return CommandResult.Ok(data,
            $"DataWarehouse service '{serviceName}': {status.State}" +
            (status.PID.HasValue ? $" (PID: {status.PID})" : ""));
    }

    private static string GetPlatformName()
    {
        if (OperatingSystem.IsWindows()) return "Windows (sc)";
        if (OperatingSystem.IsLinux()) return "Linux (systemd)";
        if (OperatingSystem.IsMacOS()) return "macOS (launchd)";
        return "Unknown";
    }
}

/// <summary>
/// Installs the DataWarehouse as a system service with profile-aware naming.
/// Uses <see cref="PlatformServiceManager.CreateProfileRegistration"/> for platform-specific service names.
/// </summary>
public sealed class ServiceInstallCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "service.install";

    /// <inheritdoc />
    public string Description => "Install DataWarehouse as a system service";

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
        if (!PlatformServiceManager.HasAdminPrivileges())
        {
            return CommandResult.Fail(
                "Administrative privileges required to install the service. " +
                "Run with elevated permissions (Administrator/sudo).");
        }

        var profile = ProfileHelper.GetProfile(parameters);
        var execPath = parameters.TryGetValue("path", out var pathVal) && pathVal is string p && !string.IsNullOrWhiteSpace(p)
            ? p
            : GetDefaultExecutablePath();

        var autoStart = !parameters.TryGetValue("autostart", out var autoVal) || autoVal is not false;

        var registration = PlatformServiceManager.CreateProfileRegistration(
            execPath,
            profile,
            Path.GetDirectoryName(execPath),
            autoStart);

        try
        {
            await PlatformServiceManager.RegisterServiceAsync(registration, cancellationToken);

            var data = new Dictionary<string, object?>
            {
                ["serviceName"] = registration.Name,
                ["displayName"] = registration.DisplayName,
                ["profile"] = profile ?? "default",
                ["executablePath"] = registration.ExecutablePath,
                ["autoStart"] = registration.AutoStart,
                ["action"] = "installed"
            };

            return CommandResult.Ok(data,
                $"DataWarehouse service '{registration.Name}' installed successfully.\n" +
                $"  Profile: {profile ?? "default"}\n" +
                $"  Executable: {registration.ExecutablePath}\n" +
                $"  Auto-start: {registration.AutoStart}");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"Failed to install service: {ex.Message}");
        }
    }

    private static string GetDefaultExecutablePath()
    {
        var currentExe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(currentExe))
        {
            var dir = Path.GetDirectoryName(currentExe)!;
            var launcherPath = Path.Combine(dir, "DataWarehouse.Launcher" +
                (OperatingSystem.IsWindows() ? ".exe" : ""));
            if (File.Exists(launcherPath))
                return launcherPath;
        }
        return currentExe ?? "DataWarehouse.Launcher";
    }
}

/// <summary>
/// Starts the DataWarehouse system service.
/// </summary>
public sealed class ServiceStartCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "service.start";

    /// <inheritdoc />
    public string Description => "Start the DataWarehouse service";

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
        if (!PlatformServiceManager.HasAdminPrivileges())
        {
            return CommandResult.Fail(
                "Administrative privileges required to start the service. " +
                "Run with elevated permissions (Administrator/sudo).");
        }

        var profile = ProfileHelper.GetProfile(parameters);
        var serviceName = PlatformServiceManager.GetProfileServiceName(profile);

        try
        {
            await PlatformServiceManager.StartServiceAsync(serviceName, cancellationToken);
            return CommandResult.Ok(
                new { serviceName, profile = profile ?? "default", action = "started" },
                $"DataWarehouse service '{serviceName}' started.");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"Failed to start service '{serviceName}': {ex.Message}");
        }
    }
}

/// <summary>
/// Stops the DataWarehouse system service.
/// </summary>
public sealed class ServiceStopCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "service.stop";

    /// <inheritdoc />
    public string Description => "Stop the DataWarehouse service";

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
        if (!PlatformServiceManager.HasAdminPrivileges())
        {
            return CommandResult.Fail(
                "Administrative privileges required to stop the service. " +
                "Run with elevated permissions (Administrator/sudo).");
        }

        var profile = ProfileHelper.GetProfile(parameters);
        var serviceName = PlatformServiceManager.GetProfileServiceName(profile);

        try
        {
            await PlatformServiceManager.StopServiceAsync(serviceName, cancellationToken);
            return CommandResult.Ok(
                new { serviceName, profile = profile ?? "default", action = "stopped" },
                $"DataWarehouse service '{serviceName}' stopped.");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"Failed to stop service '{serviceName}': {ex.Message}");
        }
    }
}

/// <summary>
/// Restarts the DataWarehouse system service.
/// </summary>
public sealed class ServiceRestartCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "service.restart";

    /// <inheritdoc />
    public string Description => "Restart the DataWarehouse service";

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
        if (!PlatformServiceManager.HasAdminPrivileges())
        {
            return CommandResult.Fail(
                "Administrative privileges required to restart the service. " +
                "Run with elevated permissions (Administrator/sudo).");
        }

        var profile = ProfileHelper.GetProfile(parameters);
        var serviceName = PlatformServiceManager.GetProfileServiceName(profile);

        try
        {
            await PlatformServiceManager.RestartServiceAsync(serviceName, cancellationToken);
            return CommandResult.Ok(
                new { serviceName, profile = profile ?? "default", action = "restarted" },
                $"DataWarehouse service '{serviceName}' restarted.");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"Failed to restart service '{serviceName}': {ex.Message}");
        }
    }
}

/// <summary>
/// Uninstalls (unregisters) the DataWarehouse system service.
/// </summary>
public sealed class ServiceUninstallCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "service.uninstall";

    /// <inheritdoc />
    public string Description => "Uninstall the DataWarehouse service";

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
        if (!PlatformServiceManager.HasAdminPrivileges())
        {
            return CommandResult.Fail(
                "Administrative privileges required to uninstall the service. " +
                "Run with elevated permissions (Administrator/sudo).");
        }

        var profile = ProfileHelper.GetProfile(parameters);
        var serviceName = PlatformServiceManager.GetProfileServiceName(profile);

        try
        {
            await PlatformServiceManager.UnregisterServiceAsync(serviceName, cancellationToken);
            return CommandResult.Ok(
                new { serviceName, profile = profile ?? "default", action = "uninstalled" },
                $"DataWarehouse service '{serviceName}' uninstalled. Service files have been removed.");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"Failed to uninstall service '{serviceName}': {ex.Message}");
        }
    }
}
