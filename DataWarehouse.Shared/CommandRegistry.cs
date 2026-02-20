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
