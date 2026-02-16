// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// Connects to a running DataWarehouse instance via network.
/// Supports remote HTTP connections and local in-process connections.
/// </summary>
public sealed class ConnectCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "connect";

    /// <inheritdoc />
    public string Description => "Connect to a DataWarehouse instance";

    /// <inheritdoc />
    public string Category => "core";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var host = parameters.TryGetValue("host", out var h) ? h?.ToString() : null;
        var port = parameters.TryGetValue("port", out var p) ? Convert.ToInt32(p) : 8080;
        var localPath = parameters.TryGetValue("localPath", out var lp) ? lp?.ToString() : null;

        bool connected;

        if (!string.IsNullOrEmpty(host))
        {
            // Remote connection
            connected = await context.InstanceManager.ConnectRemoteAsync(host, port);

            if (connected)
            {
                await context.CapabilityManager.RefreshCapabilitiesAsync();
                var capabilities = context.CapabilityManager.Capabilities;
                var pluginCount = capabilities?.LoadedPlugins?.Count ?? 0;
                var connectionHost = context.InstanceManager.CurrentConnection?.Host ?? host;

                var data = new Dictionary<string, object?>
                {
                    ["connectionType"] = "remote",
                    ["host"] = host,
                    ["port"] = port,
                    ["connectedTo"] = $"{host}:{port}",
                    ["pluginCount"] = pluginCount
                };

                return CommandResult.Ok(data,
                    $"Connected to remote instance at {host}:{port} ({pluginCount} plugins)");
            }

            return CommandResult.Fail($"Failed to connect to {host}:{port}. Ensure the instance is running.");
        }
        else if (!string.IsNullOrEmpty(localPath))
        {
            // Local path connection
            connected = await context.InstanceManager.ConnectLocalAsync(localPath);

            if (connected)
            {
                await context.CapabilityManager.RefreshCapabilitiesAsync();
                return CommandResult.Ok(
                    new { connectionType = "local", path = localPath },
                    $"Connected to local instance at {localPath}");
            }

            return CommandResult.Fail($"Failed to connect to local instance at {localPath}.");
        }
        else
        {
            // In-process connection
            connected = await context.InstanceManager.ConnectInProcessAsync();

            if (connected)
            {
                await context.CapabilityManager.RefreshCapabilitiesAsync();
                return CommandResult.Ok(
                    new { connectionType = "inprocess" },
                    "Connected to in-process instance.");
            }

            return CommandResult.Fail("Failed to connect to in-process instance.");
        }
    }
}

/// <summary>
/// Disconnects from the currently connected DataWarehouse instance.
/// </summary>
public sealed class DisconnectCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "disconnect";

    /// <inheritdoc />
    public string Description => "Disconnect from the current instance";

    /// <inheritdoc />
    public string Category => "core";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!context.InstanceManager.IsConnected)
        {
            return CommandResult.Ok(
                new { wasConnected = false },
                "Not connected to any instance.");
        }

        var connectionInfo = context.InstanceManager.CurrentConnection?.Host ?? "unknown";
        await context.InstanceManager.DisconnectAsync();

        return CommandResult.Ok(
            new { wasConnected = true, disconnectedFrom = connectionInfo },
            $"Disconnected from instance ({connectionInfo}).");
    }
}
