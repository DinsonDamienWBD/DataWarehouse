// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using DataWarehouse.Shared.Models;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Shared;

/// <summary>
/// Defines a dynamically-registered command from a loaded plugin or core system.
/// </summary>
public sealed record DynamicCommandDefinition
{
    /// <summary>The unique command name (e.g., "storage.list").</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description of the command.</summary>
    public required string Description { get; init; }

    /// <summary>The category this command belongs to (e.g., "storage", "backup").</summary>
    public required string Category { get; init; }

    /// <summary>Features required for this command to be available.</summary>
    public List<string> RequiredFeatures { get; init; } = new();

    /// <summary>The plugin that provided this command (null for core commands).</summary>
    public string? SourcePlugin { get; init; }

    /// <summary>Whether this is a core command that is always available.</summary>
    public bool IsCore { get; init; }
}

/// <summary>
/// Event arguments for when the command registry changes.
/// </summary>
public sealed class CommandsChangedEventArgs : EventArgs
{
    /// <summary>Commands that were added.</summary>
    public IReadOnlyList<DynamicCommandDefinition> Added { get; init; } = Array.Empty<DynamicCommandDefinition>();

    /// <summary>Commands that were removed.</summary>
    public IReadOnlyList<string> Removed { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Dynamic command registry that subscribes to capability change events via MessageBridge
/// and maintains a live registry of available commands based on loaded plugins.
/// Thread-safe via ConcurrentDictionary. Handles startup race by doing an initial
/// full capability query after subscribing to events.
/// </summary>
public sealed class DynamicCommandRegistry
{
    private readonly BoundedDictionary<string, DynamicCommandDefinition> _commands = new BoundedDictionary<string, DynamicCommandDefinition>(1000);

    /// <summary>
    /// Raised when commands are added or removed from the registry.
    /// </summary>
    public event EventHandler<CommandsChangedEventArgs>? CommandsChanged;

    /// <summary>
    /// Starts listening for capability change events via the message bridge.
    /// Subscribes to capability.changed, plugin.loaded, and plugin.unloaded topics,
    /// then performs an initial full capability query to bootstrap the registry.
    /// </summary>
    /// <param name="bridge">The message bridge to subscribe through.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task StartListeningAsync(MessageBridge bridge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bridge);

        // Subscribe to capability change events
        bridge.SubscribeToTopic("capability.changed", async msg =>
        {
            if (msg.Data.TryGetValue("pluginId", out var pluginIdObj) &&
                msg.Data.TryGetValue("capability", out var capObj) &&
                msg.Data.TryGetValue("available", out var availObj))
            {
                var pluginId = pluginIdObj?.ToString() ?? "";
                var capability = capObj?.ToString() ?? "";
                var available = availObj is bool b ? b : bool.TryParse(availObj?.ToString(), out var parsed) && parsed;
                OnCapabilityChanged(pluginId, capability, available);
            }
            await Task.CompletedTask;
        });

        bridge.SubscribeToTopic("plugin.loaded", async msg =>
        {
            if (msg.Data.TryGetValue("pluginId", out var pluginIdObj))
            {
                var pluginId = pluginIdObj?.ToString() ?? "";
                var capabilities = new List<string>();
                if (msg.Data.TryGetValue("capabilities", out var capsObj) && capsObj is IEnumerable<object> capList)
                {
                    capabilities.AddRange(capList.Select(c => c.ToString() ?? ""));
                }
                OnPluginLoaded(pluginId, capabilities);
            }
            await Task.CompletedTask;
        });

        bridge.SubscribeToTopic("plugin.unloaded", async msg =>
        {
            if (msg.Data.TryGetValue("pluginId", out var pluginIdObj))
            {
                var pluginId = pluginIdObj?.ToString() ?? "";
                OnPluginUnloaded(pluginId);
            }
            await Task.CompletedTask;
        });

        // Initial full capability query to bootstrap the registry (handles startup race)
        if (bridge.IsConnected)
        {
            try
            {
                var response = await bridge.SendAsync(new Message
                {
                    Id = Guid.NewGuid().ToString(),
                    Type = MessageType.Request,
                    Command = "system.capabilities",
                    Data = new Dictionary<string, object>()
                });

                if (response?.Data != null && response.Data.TryGetValue("commands", out var commandsObj))
                {
                    // Process initial command list from capabilities response
                    if (commandsObj is IEnumerable<object> commandList)
                    {
                        foreach (var cmd in commandList)
                        {
                            if (cmd is Dictionary<string, object> cmdDict)
                            {
                                var def = new DynamicCommandDefinition
                                {
                                    Name = cmdDict.GetValueOrDefault("name")?.ToString() ?? "",
                                    Description = cmdDict.GetValueOrDefault("description")?.ToString() ?? "",
                                    Category = cmdDict.GetValueOrDefault("category")?.ToString() ?? "plugin",
                                    SourcePlugin = cmdDict.GetValueOrDefault("sourcePlugin")?.ToString()
                                };
                                if (!string.IsNullOrEmpty(def.Name))
                                {
                                    _commands.TryAdd(def.Name, def);
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Initial query failed; rely on event-based updates
            }
        }
    }

    /// <summary>
    /// Called when a plugin is loaded, adding its commands to the registry.
    /// </summary>
    /// <param name="pluginId">The plugin identifier.</param>
    /// <param name="capabilities">List of capabilities the plugin provides.</param>
    public void OnPluginLoaded(string pluginId, List<string> capabilities)
    {
        var added = new List<DynamicCommandDefinition>();

        foreach (var capability in capabilities)
        {
            var def = new DynamicCommandDefinition
            {
                Name = capability,
                Description = $"Command from plugin {pluginId}",
                Category = pluginId,
                SourcePlugin = pluginId
            };

            if (_commands.TryAdd(def.Name, def))
            {
                added.Add(def);
            }
        }

        if (added.Count > 0)
        {
            CommandsChanged?.Invoke(this, new CommandsChangedEventArgs { Added = added });
        }
    }

    /// <summary>
    /// Called when a plugin is unloaded, removing all its commands from the registry.
    /// </summary>
    /// <param name="pluginId">The plugin identifier.</param>
    public void OnPluginUnloaded(string pluginId)
    {
        var removed = new List<string>();

        foreach (var kvp in _commands)
        {
            if (kvp.Value.SourcePlugin == pluginId)
            {
                if (_commands.TryRemove(kvp.Key, out _))
                {
                    removed.Add(kvp.Key);
                }
            }
        }

        if (removed.Count > 0)
        {
            CommandsChanged?.Invoke(this, new CommandsChangedEventArgs { Removed = removed });
        }
    }

    /// <summary>
    /// Called when a specific capability changes availability.
    /// </summary>
    /// <param name="pluginId">The plugin identifier.</param>
    /// <param name="capability">The capability name.</param>
    /// <param name="available">Whether the capability is now available.</param>
    public void OnCapabilityChanged(string pluginId, string capability, bool available)
    {
        if (available)
        {
            var def = new DynamicCommandDefinition
            {
                Name = capability,
                Description = $"Command from plugin {pluginId}",
                Category = pluginId,
                SourcePlugin = pluginId
            };

            if (_commands.TryAdd(def.Name, def))
            {
                CommandsChanged?.Invoke(this, new CommandsChangedEventArgs
                {
                    Added = new[] { def }
                });
            }
        }
        else
        {
            if (_commands.TryRemove(capability, out _))
            {
                CommandsChanged?.Invoke(this, new CommandsChangedEventArgs
                {
                    Removed = new[] { capability }
                });
            }
        }
    }

    /// <summary>
    /// Gets all available commands in the registry.
    /// </summary>
    /// <returns>All registered command definitions.</returns>
    public IEnumerable<DynamicCommandDefinition> GetAvailableCommands()
    {
        return _commands.Values;
    }

    /// <summary>
    /// Gets commands grouped by category.
    /// </summary>
    /// <returns>Dictionary of category name to list of command definitions.</returns>
    public Dictionary<string, List<DynamicCommandDefinition>> GetCommandsByCategory()
    {
        return _commands.Values
            .GroupBy(c => c.Category)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>
    /// Checks if a specific command is available in the registry.
    /// </summary>
    /// <param name="commandName">The command name to check.</param>
    /// <returns>True if the command is registered and available.</returns>
    public bool IsCommandAvailable(string commandName)
    {
        return _commands.ContainsKey(commandName);
    }

    /// <summary>
    /// Registers a core command that is always available regardless of plugin state.
    /// </summary>
    /// <param name="def">The command definition to register.</param>
    public void RegisterCoreCommand(DynamicCommandDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);
        _commands[def.Name] = def with { IsCore = true };
    }
}
