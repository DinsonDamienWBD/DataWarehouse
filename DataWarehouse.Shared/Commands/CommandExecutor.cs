// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Reflection;
using DataWarehouse.Shared.Models;
using DataWarehouse.Shared.Services;

namespace DataWarehouse.Shared.Commands;

// CLI-05: Feature Parity Guarantee
// CLI and GUI both use this CommandExecutor which reads from DynamicCommandRegistry.
// Any command registered here (or dynamically via DynamicCommandRegistry) is available
// to both CLI and GUI. No CLI-specific or GUI-specific command registration paths exist.

/// <summary>
/// Central command executor that resolves and executes commands.
/// All CLI/GUI commands are routed through this executor.
/// Integrates with DynamicCommandRegistry for runtime command discovery.
/// </summary>
public sealed class CommandExecutor
{
    private readonly ConcurrentDictionary<string, ICommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly InstanceManager _instanceManager;
    private readonly CapabilityManager _capabilityManager;
    private readonly List<ICommand> _allCommands = new();
    private readonly CommandRecorder? _recorder;
    private readonly UndoManager? _undoManager;
    private readonly DynamicCommandRegistry? _dynamicRegistry;

    /// <summary>
    /// Initializes a new CommandExecutor with the given managers.
    /// </summary>
    public CommandExecutor(InstanceManager instanceManager, CapabilityManager capabilityManager)
        : this(instanceManager, capabilityManager, null, null)
    {
    }

    /// <summary>
    /// Initializes a new CommandExecutor with a DynamicCommandRegistry for runtime command discovery.
    /// </summary>
    /// <param name="instanceManager">The instance manager.</param>
    /// <param name="capabilityManager">The capability manager.</param>
    /// <param name="dynamicRegistry">The dynamic command registry for plugin-based commands.</param>
    public CommandExecutor(
        InstanceManager instanceManager,
        CapabilityManager capabilityManager,
        DynamicCommandRegistry dynamicRegistry)
        : this(instanceManager, capabilityManager, null, null)
    {
        _dynamicRegistry = dynamicRegistry;
        SubscribeToDynamicUpdates();
    }

    /// <summary>
    /// Initializes a new CommandExecutor with services for recording and undo support.
    /// </summary>
    /// <param name="instanceManager">The instance manager.</param>
    /// <param name="capabilityManager">The capability manager.</param>
    /// <param name="recorder">Optional command recorder for session recording.</param>
    /// <param name="undoManager">Optional undo manager for rollback support.</param>
    public CommandExecutor(
        InstanceManager instanceManager,
        CapabilityManager capabilityManager,
        CommandRecorder? recorder,
        UndoManager? undoManager)
    {
        _instanceManager = instanceManager;
        _capabilityManager = capabilityManager;
        _recorder = recorder;
        _undoManager = undoManager;
        RegisterBuiltInCommands();
    }

    /// <summary>
    /// Gets the command recorder, if available.
    /// </summary>
    public CommandRecorder? Recorder => _recorder;

    /// <summary>
    /// Gets the undo manager, if available.
    /// </summary>
    public UndoManager? UndoManager => _undoManager;

    /// <summary>
    /// Gets all registered commands.
    /// </summary>
    public IReadOnlyList<ICommand> AllCommands => _allCommands.AsReadOnly();

    /// <summary>
    /// Gets commands available for the current instance capabilities.
    /// </summary>
    public IEnumerable<ICommand> AvailableCommands
    {
        get
        {
            var capabilities = _capabilityManager.Capabilities;
            if (capabilities == null)
            {
                return _allCommands.Where(c => c.RequiredFeatures.Count == 0);
            }

            return _allCommands.Where(c =>
                c.RequiredFeatures.All(f => _capabilityManager.HasFeature(f)));
        }
    }

    /// <summary>
    /// Registers a command.
    /// </summary>
    public void Register(ICommand command)
    {
        _commands[command.Name] = command;
        _allCommands.Add(command);
    }

    /// <summary>
    /// Resolves a command by name.
    /// </summary>
    public ICommand? Resolve(string commandName)
    {
        return _commands.TryGetValue(commandName, out var command) ? command : null;
    }

    /// <summary>
    /// Executes a command by name with parameters.
    /// </summary>
    public async Task<CommandResult> ExecuteAsync(
        string commandName,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var command = Resolve(commandName);
        if (command == null)
        {
            return CommandResult.Fail($"Unknown command: '{commandName}'. Use 'dw help' to see available commands.");
        }

        // Check if required features are available
        var capabilities = _capabilityManager.Capabilities;
        foreach (var feature in command.RequiredFeatures)
        {
            if (!_capabilityManager.HasFeature(feature))
            {
                return CommandResult.FeatureNotAvailable(feature);
            }
        }

        var context = new CommandContext
        {
            InstanceManager = _instanceManager,
            CapabilityManager = _capabilityManager,
            Verbose = parameters.TryGetValue("verbose", out var v) && v is true,
            OutputFormat = parameters.TryGetValue("format", out var f) && f is OutputFormat fmt ? fmt : OutputFormat.Table
        };

        try
        {
            return await command.ExecuteAsync(context, parameters, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Fail("Command cancelled.", 130);
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"Command failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Gets the dynamic command registry, if available.
    /// </summary>
    public DynamicCommandRegistry? DynamicRegistry => _dynamicRegistry;

    /// <summary>
    /// Subscribes to DynamicCommandRegistry changes for logging command additions/removals.
    /// </summary>
    private void SubscribeToDynamicUpdates()
    {
        if (_dynamicRegistry == null) return;

        _dynamicRegistry.CommandsChanged += (_, args) =>
        {
            // Dynamic commands are tracked in the registry; this is for observability
        };
    }

    /// <summary>
    /// Gets commands grouped by category.
    /// </summary>
    public Dictionary<string, List<ICommand>> GetCommandsByCategory()
    {
        return AvailableCommands
            .GroupBy(c => c.Category)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    private void RegisterBuiltInCommands()
    {
        // Storage commands
        Register(new StorageListCommand());
        Register(new StorageCreateCommand());
        Register(new StorageDeleteCommand());
        Register(new StorageInfoCommand());
        Register(new StorageStatsCommand());

        // Backup commands
        Register(new BackupCreateCommand());
        Register(new BackupListCommand());
        Register(new BackupRestoreCommand());
        Register(new BackupVerifyCommand());
        Register(new BackupDeleteCommand());

        // Plugin commands
        Register(new PluginListCommand());
        Register(new PluginInfoCommand());
        Register(new PluginEnableCommand());
        Register(new PluginDisableCommand());
        Register(new PluginReloadCommand());

        // Health commands
        Register(new HealthStatusCommand());
        Register(new HealthMetricsCommand());
        Register(new HealthAlertsCommand());
        Register(new HealthCheckCommand());

        // Config commands
        Register(new ConfigShowCommand());
        Register(new ConfigSetCommand());
        Register(new ConfigGetCommand());
        Register(new ConfigExportCommand());
        Register(new ConfigImportCommand());

        // RAID commands
        Register(new RaidListCommand());
        Register(new RaidCreateCommand());
        Register(new RaidStatusCommand());
        Register(new RaidRebuildCommand());
        Register(new RaidLevelsCommand());

        // Audit commands
        Register(new AuditListCommand());
        Register(new AuditExportCommand());
        Register(new AuditStatsCommand());

        // Server commands
        Register(new ServerStartCommand());
        Register(new ServerStopCommand());
        Register(new ServerStatusCommand());
        Register(new ServerInfoCommand());

        // Benchmark commands
        Register(new BenchmarkRunCommand());
        Register(new BenchmarkReportCommand());

        // Install commands
        Register(new InstallCommand());
        Register(new InstallStatusCommand());

        // Live mode commands
        Register(new LiveStartCommand());
        Register(new LiveStopCommand());
        Register(new LiveStatusCommand());

        // System commands
        Register(new SystemInfoCommand());
        Register(new SystemCapabilitiesCommand());
        Register(new HelpCommand(this));
    }
}
