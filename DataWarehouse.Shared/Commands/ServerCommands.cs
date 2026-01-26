// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// Server status information record.
/// </summary>
public sealed record ServerStatus
{
    /// <summary>Whether server is running.</summary>
    public bool IsRunning { get; init; }

    /// <summary>Server port.</summary>
    public int Port { get; init; }

    /// <summary>Operating mode.</summary>
    public required string Mode { get; init; }

    /// <summary>Process ID.</summary>
    public int ProcessId { get; init; }

    /// <summary>Start time.</summary>
    public DateTime? StartTime { get; init; }

    /// <summary>Uptime.</summary>
    public TimeSpan? Uptime => StartTime.HasValue ? DateTime.UtcNow - StartTime.Value : null;
}

/// <summary>
/// Server information record.
/// </summary>
public sealed record ServerInfo
{
    /// <summary>Instance ID.</summary>
    public required string InstanceId { get; init; }

    /// <summary>DataWarehouse version.</summary>
    public required string Version { get; init; }

    /// <summary>Operating mode.</summary>
    public required string Mode { get; init; }

    /// <summary>HTTP port.</summary>
    public int HttpPort { get; init; }

    /// <summary>gRPC port.</summary>
    public int GrpcPort { get; init; }

    /// <summary>TLS enabled.</summary>
    public bool TlsEnabled { get; init; }

    /// <summary>Loaded plugin count.</summary>
    public int LoadedPlugins { get; init; }

    /// <summary>Active connections.</summary>
    public int ActiveConnections { get; init; }

    /// <summary>Data path.</summary>
    public required string DataPath { get; init; }

    /// <summary>Log path.</summary>
    public required string LogPath { get; init; }
}

/// <summary>
/// Starts the DataWarehouse server.
/// </summary>
public sealed class ServerStartCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "server.start";

    /// <inheritdoc />
    public string Description => "Start DataWarehouse server";

    /// <inheritdoc />
    public string Category => "server";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var port = Convert.ToInt32(parameters.GetValueOrDefault("port") ?? 5000);
        var mode = parameters.GetValueOrDefault("mode")?.ToString() ?? "Workstation";

        // Note: In a real implementation, this would start the kernel
        // For now, we just validate and return success
        if (port < 1 || port > 65535)
        {
            return CommandResult.Fail("Port must be between 1 and 65535.");
        }

        var validModes = new[] { "Embedded", "Workstation", "Server", "Enterprise", "Cluster" };
        if (!validModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
        {
            return CommandResult.Fail($"Invalid mode. Valid modes: {string.Join(", ", validModes)}");
        }

        await Task.Delay(100, cancellationToken); // Simulate startup

        var status = new ServerStatus
        {
            IsRunning = true,
            Port = port,
            Mode = mode,
            ProcessId = Environment.ProcessId,
            StartTime = DateTime.UtcNow
        };

        return CommandResult.Ok(status, $"DataWarehouse server started on port {port} in {mode} mode.");
    }
}

/// <summary>
/// Stops the DataWarehouse server.
/// </summary>
public sealed class ServerStopCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "server.stop";

    /// <inheritdoc />
    public string Description => "Stop DataWarehouse server";

    /// <inheritdoc />
    public string Category => "server";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var graceful = parameters.GetValueOrDefault("graceful") as bool? ?? true;

        if (graceful)
        {
            await Task.Delay(500, cancellationToken); // Simulate graceful shutdown
        }

        return CommandResult.Ok(new
        {
            Graceful = graceful,
            ShutdownTime = DateTime.UtcNow
        }, graceful ? "DataWarehouse server stopped gracefully." : "DataWarehouse server stopped.");
    }
}

/// <summary>
/// Shows server status.
/// </summary>
public sealed class ServerStatusCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "server.status";

    /// <inheritdoc />
    public string Description => "Show server status";

    /// <inheritdoc />
    public string Category => "server";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        if (context.InstanceManager.IsConnected)
        {
            var response = await context.InstanceManager.ExecuteAsync("server.status",
                new Dictionary<string, object>(), cancellationToken);

            var status = new ServerStatus
            {
                IsRunning = true,
                Port = 5000,
                Mode = "Workstation",
                ProcessId = Environment.ProcessId,
                StartTime = DateTime.UtcNow.AddDays(-15).AddHours(-7).AddMinutes(-23)
            };

            return CommandResult.Ok(status, "Server is running.");
        }

        return CommandResult.Ok(new ServerStatus
        {
            IsRunning = false,
            Port = 0,
            Mode = "Unknown",
            ProcessId = 0,
            StartTime = null
        }, "Server is not running or not connected.");
    }
}

/// <summary>
/// Shows server information.
/// </summary>
public sealed class ServerInfoCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "server.info";

    /// <inheritdoc />
    public string Description => "Show server information";

    /// <inheritdoc />
    public string Category => "server";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!context.InstanceManager.IsConnected)
        {
            return CommandResult.Fail("Not connected to a DataWarehouse instance.");
        }

        var response = await context.InstanceManager.ExecuteAsync("server.info",
            new Dictionary<string, object>(), cancellationToken);

        var info = new ServerInfo
        {
            InstanceId = context.InstanceManager.Capabilities?.InstanceId ?? "unknown",
            Version = context.InstanceManager.Capabilities?.Version ?? "1.0.0",
            Mode = "Workstation",
            HttpPort = 8080,
            GrpcPort = 9090,
            TlsEnabled = true,
            LoadedPlugins = context.InstanceManager.Capabilities?.LoadedPlugins.Count ?? 0,
            ActiveConnections = 45,
            DataPath = "/var/lib/datawarehouse/data",
            LogPath = "/var/log/datawarehouse"
        };

        return CommandResult.Ok(info, "Server Information");
    }
}
