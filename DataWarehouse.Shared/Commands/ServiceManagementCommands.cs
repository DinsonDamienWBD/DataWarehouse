// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Shared.Services;

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// Shows the status of the DataWarehouse system service.
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
        var serviceName = OperatingSystem.IsMacOS() ? "com.datawarehouse" : "DataWarehouse";
        var status = await PlatformServiceManager.GetServiceStatusAsync(serviceName, cancellationToken);

        var data = new Dictionary<string, object?>
        {
            ["serviceName"] = serviceName,
            ["installed"] = status.IsInstalled,
            ["running"] = status.IsRunning,
            ["state"] = status.State,
            ["pid"] = status.PID,
            ["platform"] = GetPlatformName()
        };

        if (!status.IsInstalled)
        {
            return CommandResult.Ok(data, $"DataWarehouse service is not installed on this system.");
        }

        return CommandResult.Ok(data,
            $"DataWarehouse service: {status.State}" +
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

        var serviceName = OperatingSystem.IsMacOS() ? "com.datawarehouse" : "DataWarehouse";

        try
        {
            await PlatformServiceManager.StartServiceAsync(serviceName, cancellationToken);
            return CommandResult.Ok(
                new { serviceName, action = "started" },
                $"DataWarehouse service started.");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"Failed to start service: {ex.Message}");
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

        var serviceName = OperatingSystem.IsMacOS() ? "com.datawarehouse" : "DataWarehouse";

        try
        {
            await PlatformServiceManager.StopServiceAsync(serviceName, cancellationToken);
            return CommandResult.Ok(
                new { serviceName, action = "stopped" },
                "DataWarehouse service stopped.");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"Failed to stop service: {ex.Message}");
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

        var serviceName = OperatingSystem.IsMacOS() ? "com.datawarehouse" : "DataWarehouse";

        try
        {
            await PlatformServiceManager.RestartServiceAsync(serviceName, cancellationToken);
            return CommandResult.Ok(
                new { serviceName, action = "restarted" },
                "DataWarehouse service restarted.");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"Failed to restart service: {ex.Message}");
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

        var serviceName = OperatingSystem.IsMacOS() ? "com.datawarehouse" : "DataWarehouse";

        try
        {
            await PlatformServiceManager.UnregisterServiceAsync(serviceName, cancellationToken);
            return CommandResult.Ok(
                new { serviceName, action = "uninstalled" },
                "DataWarehouse service uninstalled. Service files have been removed.");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"Failed to uninstall service: {ex.Message}");
        }
    }
}
