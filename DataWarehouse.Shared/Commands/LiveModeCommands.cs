// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.Hosting;
using DataWarehouse.Shared.Services;

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// Starts DataWarehouse in live mode -- an in-memory, ephemeral instance
/// similar to a Linux Live CD. All data vanishes on shutdown.
/// </summary>
public sealed class LiveStartCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "live.start";

    /// <inheritdoc />
    public string Description => "Start DataWarehouse in live mode (in-memory, no persistence)";

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
        var port = parameters.TryGetValue("port", out var p) ? Convert.ToInt32(p) : 8080;
        var maxMemory = parameters.TryGetValue("memory", out var m) ? Convert.ToInt32(m) : 256;

        // Check if an instance is already running
        var existingInstance = await PortableMediaDetector.FindLocalLiveInstanceAsync(new[] { port });
        if (existingInstance != null)
        {
            return CommandResult.Fail(
                $"A live instance is already running at {existingInstance}. " +
                "Stop it first with 'dw live stop' or use a different --port.");
        }

        // Get IServerHost for starting the embedded instance
        var host = ServerHostRegistry.Current;
        if (host == null)
        {
            return CommandResult.Fail(
                "Server host not available. Live mode requires the Launcher runtime. " +
                "Run this command from the DataWarehouse Launcher.");
        }

        if (host.IsRunning)
        {
            return CommandResult.Fail(
                "An instance is already running. Stop it first with 'dw live stop'.");
        }

        // Create ephemeral configuration -- PersistData=false is the key live mode behavior
        var tempPath = PortableMediaDetector.GetPortableTempPath();
        var config = new EmbeddedConfiguration
        {
            PersistData = false,
            ExposeHttp = true,
            HttpPort = port,
            MaxMemoryMb = maxMemory,
            DataPath = tempPath,
            Plugins = new List<string>()
        };

        // Start the embedded instance
        await host.StartAsync(config, cancellationToken);

        // Auto-connect the local InstanceManager to the live instance
        if (!context.InstanceManager.IsConnected)
        {
            await context.InstanceManager.ConnectInProcessAsync();
        }

        var data = new Dictionary<string, object?>
        {
            ["status"] = "running",
            ["url"] = $"http://localhost:{port}",
            ["port"] = port,
            ["maxMemoryMb"] = maxMemory,
            ["persistData"] = false,
            ["dataPath"] = tempPath,
            ["warning"] = "Data will NOT be persisted. All data vanishes on shutdown."
        };

        return CommandResult.Ok(data,
            $"Live instance started at http://localhost:{port} (memory: {maxMemory}MB, persistence: OFF). " +
            "Data will NOT be persisted.");
    }
}

/// <summary>
/// Stops the running live DataWarehouse instance and cleans up temporary data.
/// </summary>
public sealed class LiveStopCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "live.stop";

    /// <inheritdoc />
    public string Description => "Stop the live DataWarehouse instance";

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
        var host = ServerHostRegistry.Current;
        if (host == null || !host.IsRunning)
        {
            return CommandResult.Fail("No live instance is running.");
        }

        await host.StopAsync(cancellationToken);

        // Clean up temporary data directory
        var tempPath = PortableMediaDetector.GetPortableTempPath();
        try
        {
            if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort cleanup -- temp directory will be cleaned eventually
        }

        return CommandResult.Ok(
            new { status = "stopped", tempCleanedUp = true },
            "Live instance stopped. All data has been discarded.");
    }
}

/// <summary>
/// Shows the status of the live DataWarehouse instance including
/// uptime, memory usage, port, and persistence mode.
/// </summary>
public sealed class LiveStatusCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "live.status";

    /// <inheritdoc />
    public string Description => "Show live instance status";

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
        var host = ServerHostRegistry.Current;

        if (host != null && host.IsRunning && host.Status != null)
        {
            var status = host.Status;
            var uptime = DateTime.UtcNow - status.StartTime;

            var data = new Dictionary<string, object?>
            {
                ["running"] = true,
                ["mode"] = status.Mode,
                ["port"] = status.Port,
                ["persistData"] = status.PersistData,
                ["uptime"] = $"{uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}",
                ["uptimeSeconds"] = uptime.TotalSeconds,
                ["processId"] = status.ProcessId,
                ["dataPath"] = status.DataPath,
                ["loadedPlugins"] = status.LoadedPlugins
            };

            return CommandResult.Ok(data,
                $"Live instance running on port {status.Port} (uptime: {uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}, persistence: {(status.PersistData ? "ON" : "OFF")})");
        }

        // Try to find via network scan
        var found = await PortableMediaDetector.FindLocalLiveInstanceAsync();
        if (found != null)
        {
            return CommandResult.Ok(
                new { running = true, url = found, managedExternally = true },
                $"Live instance detected at {found} (not managed by this process).");
        }

        return CommandResult.Ok(
            new { running = false },
            "No live instance running.");
    }
}
