// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Shared.Models;

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// Shows system information.
/// </summary>
public sealed class SystemInfoCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "system.info";

    /// <inheritdoc />
    public string Description => "Get system information";

    /// <inheritdoc />
    public string Category => "system";

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
            // Return local system info if not connected
            return CommandResult.Ok(new
            {
                Platform = Environment.OSVersion.Platform.ToString(),
                OSVersion = Environment.OSVersion.VersionString,
                MachineName = Environment.MachineName,
                ProcessorCount = Environment.ProcessorCount,
                DotNetVersion = Environment.Version.ToString(),
                WorkingSet = Environment.WorkingSet,
                Is64Bit = Environment.Is64BitOperatingSystem,
                Connected = false
            }, "Local system information (not connected to instance)");
        }

        var response = await context.InstanceManager.ExecuteAsync("system.info",
            new Dictionary<string, object>(), cancellationToken);

        var capabilities = context.CapabilityManager.Capabilities;

        return CommandResult.Ok(new
        {
            InstanceId = capabilities?.InstanceId ?? "unknown",
            Name = capabilities?.Name ?? "DataWarehouse",
            Version = capabilities?.Version ?? "1.0.0",
            Platform = Environment.OSVersion.Platform.ToString(),
            OSVersion = Environment.OSVersion.VersionString,
            MachineName = Environment.MachineName,
            ProcessorCount = Environment.ProcessorCount,
            DotNetVersion = Environment.Version.ToString(),
            LoadedPlugins = capabilities?.LoadedPlugins.Count ?? 0,
            StorageCapacity = capabilities?.MaxStorageCapacity ?? 0,
            StorageUsed = capabilities?.CurrentStorageUsage ?? 0,
            Connected = true
        }, "System information");
    }
}

/// <summary>
/// Shows instance capabilities.
/// </summary>
public sealed class SystemCapabilitiesCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "system.capabilities";

    /// <inheritdoc />
    public string Description => "Get instance capabilities";

    /// <inheritdoc />
    public string Category => "system";

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
            return CommandResult.Fail("Not connected to a DataWarehouse instance. Use 'dw connect' first.");
        }

        var response = await context.InstanceManager.ExecuteAsync("system.capabilities",
            new Dictionary<string, object>(), cancellationToken);

        var capabilities = context.CapabilityManager.Capabilities;
        if (capabilities == null)
        {
            return CommandResult.Fail("Unable to retrieve instance capabilities.");
        }

        var details = context.CapabilityManager.GetCapabilitiesDetails();

        return CommandResult.Ok(new
        {
            InstanceId = capabilities.InstanceId,
            Name = capabilities.Name,
            Version = capabilities.Version,
            Features = details,
            LoadedPlugins = capabilities.LoadedPlugins,
            StorageBackends = capabilities.StorageBackends,
            EncryptionAlgorithms = capabilities.EncryptionAlgorithms,
            CompressionAlgorithms = capabilities.CompressionAlgorithms
        }, "Instance capabilities");
    }
}

/// <summary>
/// Shows help for commands.
/// </summary>
public sealed class HelpCommand : ICommand
{
    private readonly CommandExecutor _executor;

    /// <summary>
    /// Creates a new HelpCommand.
    /// </summary>
    public HelpCommand(CommandExecutor executor)
    {
        _executor = executor;
    }

    /// <inheritdoc />
    public string Name => "help";

    /// <inheritdoc />
    public string Description => "Show help for commands";

    /// <inheritdoc />
    public string Category => "system";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var commandName = parameters.GetValueOrDefault("command")?.ToString();

        if (!string.IsNullOrEmpty(commandName))
        {
            var command = _executor.Resolve(commandName);
            if (command == null)
            {
                return Task.FromResult(CommandResult.Fail($"Unknown command: {commandName}"));
            }

            return Task.FromResult(CommandResult.Ok(new
            {
                command.Name,
                command.Description,
                command.Category,
                RequiredFeatures = command.RequiredFeatures.ToList()
            }, $"Help for: {commandName}"));
        }

        // List all available commands by category
        var commandsByCategory = _executor.GetCommandsByCategory();

        var helpInfo = commandsByCategory.Select(kvp => new
        {
            Category = kvp.Key,
            Commands = kvp.Value.Select(c => new { c.Name, c.Description }).ToList()
        }).ToList();

        return Task.FromResult(CommandResult.Ok(helpInfo, "Available commands by category"));
    }
}
