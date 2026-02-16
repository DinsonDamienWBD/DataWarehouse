// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.Hosting;

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
/// Starts the DataWarehouse server using the IServerHost interface for real kernel startup.
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

        if (port < 1 || port > 65535)
        {
            return CommandResult.Fail("Port must be between 1 and 65535.");
        }

        var validModes = new[] { "Embedded", "Workstation", "Server", "Enterprise", "Cluster" };
        if (!validModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
        {
            return CommandResult.Fail($"Invalid mode. Valid modes: {string.Join(", ", validModes)}");
        }

        var serverHost = ServerHostRegistry.Current;
        if (serverHost == null)
        {
            return CommandResult.Fail("No server host registered. The Launcher must be initialized first.");
        }

        if (serverHost.IsRunning)
        {
            return CommandResult.Fail("Server is already running.");
        }

        try
        {
            var config = new EmbeddedConfiguration
            {
                PersistData = true,
                ExposeHttp = true,
                HttpPort = port,
                DataPath = parameters.GetValueOrDefault("dataPath")?.ToString()
            };

            await serverHost.StartAsync(config, cancellationToken);

            var status = new ServerStatus
            {
                IsRunning = true,
                Port = serverHost.Status?.Port ?? port,
                Mode = mode,
                ProcessId = Environment.ProcessId,
                StartTime = serverHost.Status?.StartTime ?? DateTime.UtcNow
            };

            return CommandResult.Ok(status, $"DataWarehouse server started on port {status.Port} in {mode} mode.");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"Failed to start server: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Stops the DataWarehouse server using the IServerHost interface for real kernel shutdown.
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

        var serverHost = ServerHostRegistry.Current;
        if (serverHost == null)
        {
            return CommandResult.Fail("No server host registered. The Launcher must be initialized first.");
        }

        if (!serverHost.IsRunning)
        {
            return CommandResult.Fail("Server is not running.");
        }

        try
        {
            using var shutdownCts = graceful
                ? new CancellationTokenSource(TimeSpan.FromSeconds(10))
                : new CancellationTokenSource();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, shutdownCts.Token);

            await serverHost.StopAsync(linked.Token);

            return CommandResult.Ok(new
            {
                Graceful = graceful,
                ShutdownTime = DateTime.UtcNow
            }, graceful ? "DataWarehouse server stopped gracefully." : "DataWarehouse server stopped.");
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Ok(new
            {
                Graceful = false,
                ShutdownTime = DateTime.UtcNow
            }, "DataWarehouse server stop timed out, forced shutdown.");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"Failed to stop server: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Shows server status by querying the IServerHost for real status information.
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
    public Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var serverHost = ServerHostRegistry.Current;

        if (serverHost != null && serverHost.IsRunning && serverHost.Status != null)
        {
            var hostStatus = serverHost.Status;
            var status = new ServerStatus
            {
                IsRunning = true,
                Port = hostStatus.Port,
                Mode = hostStatus.Mode,
                ProcessId = hostStatus.ProcessId,
                StartTime = hostStatus.StartTime
            };

            return Task.FromResult(CommandResult.Ok(status, "Server is running."));
        }

        return Task.FromResult(CommandResult.Ok(new ServerStatus
        {
            IsRunning = false,
            Port = 0,
            Mode = "Unknown",
            ProcessId = 0,
            StartTime = null
        }, "Server is not running or not connected."));
    }
}

/// <summary>
/// Shows server information by querying the IServerHost for real instance data.
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
    public Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var serverHost = ServerHostRegistry.Current;

        if (serverHost != null && serverHost.IsRunning && serverHost.Status != null)
        {
            var hostStatus = serverHost.Status;
            var info = new ServerInfo
            {
                InstanceId = hostStatus.InstanceId ?? "unknown",
                Version = "2.0.0",
                Mode = hostStatus.Mode,
                HttpPort = hostStatus.Port,
                GrpcPort = 0,
                TlsEnabled = false,
                LoadedPlugins = hostStatus.LoadedPlugins,
                ActiveConnections = 0,
                DataPath = hostStatus.DataPath ?? "N/A",
                LogPath = Path.Combine(hostStatus.DataPath ?? ".", "logs")
            };

            return Task.FromResult(CommandResult.Ok(info, "Server Information"));
        }

        if (!context.InstanceManager.IsConnected)
        {
            return Task.FromResult(CommandResult.Fail(
                "Not connected to a DataWarehouse instance. Use 'dw connect' or start a local server."));
        }

        // Query connected instance for its info
        var connectedInfo = new ServerInfo
        {
            InstanceId = context.InstanceManager.Capabilities?.InstanceId ?? "unknown",
            Version = context.InstanceManager.Capabilities?.Version ?? "2.0.0",
            Mode = "Remote",
            HttpPort = context.InstanceManager.CurrentConnection?.Port ?? 0,
            GrpcPort = 0,
            TlsEnabled = false,
            LoadedPlugins = context.InstanceManager.Capabilities?.LoadedPlugins.Count ?? 0,
            ActiveConnections = 0,
            DataPath = "Remote instance",
            LogPath = "Remote instance"
        };

        return Task.FromResult(CommandResult.Ok(connectedInfo, "Server Information (Remote)"));
    }
}
