using DataWarehouse.Shared.Models;

namespace DataWarehouse.Shared;

/// <summary>
/// Represents a command definition with its requirements
/// </summary>
public class CommandDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> RequiredPlugins { get; set; } = new();
    public List<string> RequiredFeatures { get; set; } = new();
    public bool IsCore { get; set; }
}

/// <summary>
/// Registry of all available commands with their requirements.
/// </summary>
[Obsolete("Use DynamicCommandRegistry for runtime capability reflection. This static registry is retained for backward compatibility.")]
public class CommandRegistry
{
    private static readonly List<CommandDefinition> _commands = new()
    {
        // Core commands - always available
        new CommandDefinition
        {
            Name = "storage.list",
            Category = "core",
            Description = "List all stored objects",
            IsCore = true
        },
        new CommandDefinition
        {
            Name = "storage.get",
            Category = "core",
            Description = "Retrieve an object by ID",
            IsCore = true
        },
        new CommandDefinition
        {
            Name = "storage.put",
            Category = "core",
            Description = "Store a new object",
            IsCore = true
        },
        new CommandDefinition
        {
            Name = "storage.delete",
            Category = "core",
            Description = "Delete an object by ID",
            IsCore = true
        },
        new CommandDefinition
        {
            Name = "system.info",
            Category = "core",
            Description = "Get system information",
            IsCore = true
        },
        new CommandDefinition
        {
            Name = "system.capabilities",
            Category = "core",
            Description = "Get instance capabilities",
            IsCore = true
        },

        // Encryption commands
        new CommandDefinition
        {
            Name = "encryption.enable",
            Category = "encryption",
            Description = "Enable encryption for storage",
            RequiredFeatures = new List<string> { "encryption" }
        },
        new CommandDefinition
        {
            Name = "encryption.disable",
            Category = "encryption",
            Description = "Disable encryption",
            RequiredFeatures = new List<string> { "encryption" }
        },
        new CommandDefinition
        {
            Name = "encryption.rotate-key",
            Category = "encryption",
            Description = "Rotate encryption keys",
            RequiredFeatures = new List<string> { "encryption" }
        },

        // Compression commands
        new CommandDefinition
        {
            Name = "compression.enable",
            Category = "compression",
            Description = "Enable compression",
            RequiredFeatures = new List<string> { "compression" }
        },
        new CommandDefinition
        {
            Name = "compression.disable",
            Category = "compression",
            Description = "Disable compression",
            RequiredFeatures = new List<string> { "compression" }
        },
        new CommandDefinition
        {
            Name = "compression.set-algorithm",
            Category = "compression",
            Description = "Set compression algorithm",
            RequiredFeatures = new List<string> { "compression" }
        },

        // Metadata commands
        new CommandDefinition
        {
            Name = "metadata.get",
            Category = "metadata",
            Description = "Get object metadata",
            RequiredFeatures = new List<string> { "metadata" }
        },
        new CommandDefinition
        {
            Name = "metadata.set",
            Category = "metadata",
            Description = "Set object metadata",
            RequiredFeatures = new List<string> { "metadata" }
        },
        new CommandDefinition
        {
            Name = "metadata.delete",
            Category = "metadata",
            Description = "Delete object metadata",
            RequiredFeatures = new List<string> { "metadata" }
        },

        // Versioning commands
        new CommandDefinition
        {
            Name = "versioning.list",
            Category = "versioning",
            Description = "List object versions",
            RequiredFeatures = new List<string> { "versioning" }
        },
        new CommandDefinition
        {
            Name = "versioning.get",
            Category = "versioning",
            Description = "Get specific version",
            RequiredFeatures = new List<string> { "versioning" }
        },
        new CommandDefinition
        {
            Name = "versioning.restore",
            Category = "versioning",
            Description = "Restore a previous version",
            RequiredFeatures = new List<string> { "versioning" }
        },

        // RAID commands
        new CommandDefinition
        {
            Name = "raid.status",
            Category = "raid",
            Description = "Get RAID array status",
            RequiredFeatures = new List<string> { "raid" }
        },
        new CommandDefinition
        {
            Name = "raid.create",
            Category = "raid",
            Description = "Create RAID array",
            RequiredFeatures = new List<string> { "raid" }
        },
        new CommandDefinition
        {
            Name = "raid.rebuild",
            Category = "raid",
            Description = "Rebuild RAID array",
            RequiredFeatures = new List<string> { "raid" }
        },

        // Backup commands
        new CommandDefinition
        {
            Name = "backup.create",
            Category = "backup",
            Description = "Create backup",
            RequiredFeatures = new List<string> { "backup" }
        },
        new CommandDefinition
        {
            Name = "backup.restore",
            Category = "backup",
            Description = "Restore from backup",
            RequiredFeatures = new List<string> { "backup" }
        },
        new CommandDefinition
        {
            Name = "backup.list",
            Category = "backup",
            Description = "List available backups",
            RequiredFeatures = new List<string> { "backup" }
        },

        // Replication commands
        new CommandDefinition
        {
            Name = "replication.enable",
            Category = "replication",
            Description = "Enable replication",
            RequiredFeatures = new List<string> { "replication" }
        },
        new CommandDefinition
        {
            Name = "replication.status",
            Category = "replication",
            Description = "Get replication status",
            RequiredFeatures = new List<string> { "replication" }
        },

        // Search commands
        new CommandDefinition
        {
            Name = "search.query",
            Category = "search",
            Description = "Search for objects",
            RequiredFeatures = new List<string> { "search" }
        },
        new CommandDefinition
        {
            Name = "search.index",
            Category = "search",
            Description = "Index objects for search",
            RequiredFeatures = new List<string> { "search" }
        }
    };

    /// <summary>
    /// Gets all registered commands
    /// </summary>
    public static IReadOnlyList<CommandDefinition> AllCommands => _commands.AsReadOnly();

    /// <summary>
    /// Gets available commands for given instance capabilities
    /// </summary>
    /// <param name="capabilities">Instance capabilities to check against</param>
    /// <returns>List of available command definitions</returns>
    public static List<CommandDefinition> GetAvailableCommandsForCapabilities(InstanceCapabilities capabilities)
    {
        var availableCommands = new List<CommandDefinition>();

        foreach (var command in _commands)
        {
            // Core commands are always available
            if (command.IsCore)
            {
                availableCommands.Add(command);
                continue;
            }

            // Check if all required features are available
            bool hasAllFeatures = true;
            foreach (var feature in command.RequiredFeatures)
            {
                var hasFeature = feature.ToLowerInvariant() switch
                {
                    "encryption" => capabilities.SupportsEncryption,
                    "compression" => capabilities.SupportsCompression,
                    "metadata" => capabilities.SupportsMetadata,
                    "versioning" => capabilities.SupportsVersioning,
                    "deduplication" => capabilities.SupportsDeduplication,
                    "raid" => capabilities.SupportsRaid,
                    "replication" => capabilities.SupportsReplication,
                    "backup" => capabilities.SupportsBackup,
                    "tiering" => capabilities.SupportsTiering,
                    "search" => capabilities.SupportsSearch,
                    _ => false
                };

                if (!hasFeature)
                {
                    hasAllFeatures = false;
                    break;
                }
            }

            // Check if all required plugins are loaded
            bool hasAllPlugins = command.RequiredPlugins.All(plugin =>
                capabilities.LoadedPlugins.Any(p => p.Equals(plugin, StringComparison.OrdinalIgnoreCase)));

            if (hasAllFeatures && hasAllPlugins)
            {
                availableCommands.Add(command);
            }
        }

        return availableCommands;
    }

    /// <summary>
    /// Gets commands grouped by category
    /// </summary>
    /// <param name="capabilities">Instance capabilities</param>
    /// <returns>Dictionary of category name to commands</returns>
    public static Dictionary<string, List<CommandDefinition>> GetCommandsByCategory(InstanceCapabilities capabilities)
    {
        var availableCommands = GetAvailableCommandsForCapabilities(capabilities);
        return availableCommands.GroupBy(c => c.Category)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>
    /// Checks if a specific command is available for the given capabilities
    /// </summary>
    /// <param name="commandName">Command name to check</param>
    /// <param name="capabilities">Instance capabilities</param>
    /// <returns>True if the command is available</returns>
    public static bool IsCommandAvailable(string commandName, InstanceCapabilities capabilities)
    {
        var command = _commands.FirstOrDefault(c => c.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase));
        if (command == null)
            return false;

        if (command.IsCore)
            return true;

        var availableCommands = GetAvailableCommandsForCapabilities(capabilities);
        return availableCommands.Any(c => c.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Converts the static CommandDefinition list to DynamicCommandDefinition format
    /// for initial seeding of the DynamicCommandRegistry.
    /// </summary>
    /// <returns>Enumerable of DynamicCommandDefinition records.</returns>
    public static IEnumerable<DynamicCommandDefinition> ToDynamicDefinitions()
    {
        return _commands.Select(c => new DynamicCommandDefinition
        {
            Name = c.Name,
            Description = c.Description,
            Category = c.Category,
            RequiredFeatures = new List<string>(c.RequiredFeatures),
            IsCore = c.IsCore,
            SourcePlugin = null
        });
    }
}
