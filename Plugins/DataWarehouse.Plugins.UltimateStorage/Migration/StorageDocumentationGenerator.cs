using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DataWarehouse.SDK.Contracts.Storage;

namespace DataWarehouse.Plugins.UltimateStorage.Migration;

/// <summary>
/// Generates comprehensive documentation for UltimateStorage strategies.
/// Creates strategy documentation, capability matrices, usage examples, and comparison tables
/// to help users understand and choose the right storage strategy.
/// </summary>
/// <remarks>
/// This generator produces:
/// - Individual strategy documentation pages
/// - Strategy comparison matrices
/// - Capability and feature lists
/// - Usage examples and code snippets
/// - Best practice recommendations
/// - Performance characteristics
/// </remarks>
public sealed class StorageDocumentationGenerator
{
    private readonly StorageStrategyRegistry _registry;

    /// <summary>
    /// Initializes a new instance of the StorageDocumentationGenerator.
    /// </summary>
    /// <param name="registry">The strategy registry to document.</param>
    public StorageDocumentationGenerator(StorageStrategyRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// Generates comprehensive documentation for all registered strategies.
    /// </summary>
    /// <returns>Complete documentation as formatted text.</returns>
    public string GenerateCompleteDocumentation()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# UltimateStorage Plugin - Complete Documentation");
        sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();

        // Overview
        sb.AppendLine("## Overview");
        sb.AppendLine();
        sb.AppendLine("The UltimateStorage plugin provides a unified interface to 50+ storage backends,");
        sb.AppendLine("allowing you to choose the best storage strategy for your use case.");
        sb.AppendLine();

        // Strategy count by category
        sb.AppendLine("## Available Strategies");
        sb.AppendLine();
        var strategies = _registry.GetAllStrategies().OfType<IStorageStrategyExtended>().ToList();
        var byCategory = strategies.GroupBy(s => s.Category).ToList();

        foreach (var group in byCategory.OrderBy(g => g.Key))
        {
            sb.AppendLine($"### {group.Key} ({group.Count()} strategies)");
            sb.AppendLine();

            foreach (var strategy in group.OrderBy(s => s.StrategyName))
            {
                sb.AppendLine($"- **{strategy.StrategyName}** (`{strategy.StrategyId}`)");
            }
            sb.AppendLine();
        }

        // Detailed strategy documentation
        sb.AppendLine("## Strategy Details");
        sb.AppendLine();

        foreach (var strategy in strategies.OrderBy(s => s.Category).ThenBy(s => s.StrategyName))
        {
            sb.Append(GenerateStrategyDocumentation(strategy));
        }

        // Comparison matrix
        sb.AppendLine("## Strategy Comparison Matrix");
        sb.AppendLine();
        sb.Append(GenerateComparisonMatrix(strategies));

        // Usage examples
        sb.AppendLine("## Usage Examples");
        sb.AppendLine();
        sb.Append(GenerateUsageExamples());

        // Best practices
        sb.AppendLine("## Best Practices");
        sb.AppendLine();
        sb.Append(GenerateBestPractices());

        return sb.ToString();
    }

    /// <summary>
    /// Generates documentation for a specific strategy.
    /// </summary>
    /// <param name="strategyId">The strategy ID.</param>
    /// <returns>Strategy documentation.</returns>
    public string GenerateStrategyDocumentation(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);

        var strategy = _registry.GetStrategy(strategyId) as IStorageStrategyExtended;
        if (strategy == null)
        {
            return $"Strategy '{strategyId}' not found.";
        }

        return GenerateStrategyDocumentation(strategy);
    }

    /// <summary>
    /// Generates a comparison matrix for all strategies.
    /// </summary>
    /// <returns>Comparison matrix as formatted table.</returns>
    public string GenerateComparisonMatrix()
    {
        var strategies = _registry.GetAllStrategies().OfType<IStorageStrategyExtended>().ToList();
        return GenerateComparisonMatrix(strategies);
    }

    /// <summary>
    /// Lists all capabilities available across all strategies.
    /// </summary>
    /// <returns>List of capabilities with strategy support.</returns>
    public string ListAllCapabilities()
    {
        var sb = new StringBuilder();
        var strategies = _registry.GetAllStrategies().OfType<IStorageStrategyExtended>().ToList();

        sb.AppendLine("# Storage Strategy Capabilities");
        sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();

        // Group by capability
        var capabilities = new Dictionary<string, List<string>>();

        foreach (var strategy in strategies)
        {
            AddCapability(capabilities, "Tiering", strategy.SupportsTiering, strategy.StrategyName);
            AddCapability(capabilities, "Versioning", strategy.SupportsVersioning, strategy.StrategyName);
            AddCapability(capabilities, "Replication", strategy.SupportsReplication, strategy.StrategyName);
            AddCapability(capabilities, "Available", strategy.IsAvailable, strategy.StrategyName);
        }

        foreach (var (capability, supportingStrategies) in capabilities.OrderBy(kvp => kvp.Key))
        {
            sb.AppendLine($"## {capability}");
            sb.AppendLine($"Supported by {supportingStrategies.Count} strategies:");
            sb.AppendLine();

            foreach (var strategyName in supportingStrategies.OrderBy(s => s))
            {
                sb.AppendLine($"- {strategyName}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates usage examples for common scenarios.
    /// </summary>
    /// <returns>Usage examples with code.</returns>
    public string GenerateUsageExamples()
    {
        var sb = new StringBuilder();

        // Example 1: Basic usage
        sb.AppendLine("### Example 1: Basic Storage Operation");
        sb.AppendLine("```csharp");
        sb.AppendLine("var plugin = new UltimateStoragePlugin();");
        sb.AppendLine("var args = new Dictionary<string, object>");
        sb.AppendLine("{");
        sb.AppendLine("    [\"strategyId\"] = \"filesystem\",");
        sb.AppendLine("    [\"basePath\"] = \"/data/storage\"");
        sb.AppendLine("};");
        sb.AppendLine();
        sb.AppendLine("// Write data");
        sb.AppendLine("using var dataStream = new MemoryStream(Encoding.UTF8.GetBytes(\"Hello World\"));");
        sb.AppendLine("var result = await plugin.OnWriteAsync(dataStream, context, args);");
        sb.AppendLine("```");
        sb.AppendLine();

        // Example 2: Cloud storage
        sb.AppendLine("### Example 2: AWS S3 Storage");
        sb.AppendLine("```csharp");
        sb.AppendLine("var args = new Dictionary<string, object>");
        sb.AppendLine("{");
        sb.AppendLine("    [\"strategyId\"] = \"aws-s3\",");
        sb.AppendLine("    [\"accessKeyId\"] = \"YOUR_ACCESS_KEY\",");
        sb.AppendLine("    [\"secretAccessKey\"] = \"YOUR_SECRET_KEY\",");
        sb.AppendLine("    [\"bucket\"] = \"my-bucket\",");
        sb.AppendLine("    [\"region\"] = \"us-east-1\"");
        sb.AppendLine("};");
        sb.AppendLine();
        sb.AppendLine("await plugin.OnWriteAsync(dataStream, context, args);");
        sb.AppendLine("```");
        sb.AppendLine();

        // Example 3: Strategy listing
        sb.AppendLine("### Example 3: List Available Strategies");
        sb.AppendLine("```csharp");
        sb.AppendLine("var message = new PluginMessage");
        sb.AppendLine("{");
        sb.AppendLine("    Type = \"storage.list-strategies\"");
        sb.AppendLine("};");
        sb.AppendLine();
        sb.AppendLine("await plugin.OnMessageAsync(message);");
        sb.AppendLine("var strategies = message.Payload[\"strategies\"] as List<Dictionary<string, object>>;");
        sb.AppendLine();
        sb.AppendLine("foreach (var strategy in strategies)");
        sb.AppendLine("{");
        sb.AppendLine("    Console.WriteLine($\"{strategy[\"name\"]} - {strategy[\"category\"]}\");");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();

        // Example 4: Replication
        sb.AppendLine("### Example 4: Replicate Data Across Backends");
        sb.AppendLine("```csharp");
        sb.AppendLine("var message = new PluginMessage");
        sb.AppendLine("{");
        sb.AppendLine("    Type = \"storage.replicate\",");
        sb.AppendLine("    Payload = new Dictionary<string, object>");
        sb.AppendLine("    {");
        sb.AppendLine("        [\"path\"] = \"important-file.dat\",");
        sb.AppendLine("        [\"sourceStrategy\"] = \"filesystem\",");
        sb.AppendLine("        [\"targetStrategies\"] = new[] { \"aws-s3\", \"azure-blob\" }");
        sb.AppendLine("    }");
        sb.AppendLine("};");
        sb.AppendLine();
        sb.AppendLine("await plugin.OnMessageAsync(message);");
        sb.AppendLine("var results = message.Payload[\"replicationResults\"] as Dictionary<string, bool>;");
        sb.AppendLine("```");
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Generates a strategy selection guide based on requirements.
    /// </summary>
    /// <param name="requirements">Storage requirements.</param>
    /// <returns>Recommended strategies with explanations.</returns>
    public string GenerateStrategySelectionGuide(StorageRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);

        var sb = new StringBuilder();
        var strategies = _registry.GetAllStrategies().OfType<IStorageStrategyExtended>().ToList();

        sb.AppendLine("# Storage Strategy Selection Guide");
        sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();

        sb.AppendLine("## Your Requirements");
        sb.AppendLine($"- **Performance Tier**: {requirements.PerformanceTier}");
        sb.AppendLine($"- **Requires Versioning**: {requirements.RequiresVersioning}");
        sb.AppendLine($"- **Requires Replication**: {requirements.RequiresReplication}");
        sb.AppendLine($"- **Expected Size**: {requirements.ExpectedDataSize} bytes");
        sb.AppendLine();

        // Filter strategies
        var recommended = strategies.Where(s =>
            s.IsAvailable &&
            (!requirements.RequiresVersioning || s.SupportsVersioning) &&
            (!requirements.RequiresReplication || s.SupportsReplication) &&
            s.Tier == requirements.PerformanceTier
        ).ToList();

        sb.AppendLine("## Recommended Strategies");
        sb.AppendLine();

        if (recommended.Count == 0)
        {
            sb.AppendLine("No strategies match all requirements. Consider relaxing some constraints.");
        }
        else
        {
            foreach (var strategy in recommended.OrderBy(s => s.StrategyName))
            {
                sb.AppendLine($"### {strategy.StrategyName}");
                sb.AppendLine($"- **ID**: `{strategy.StrategyId}`");
                sb.AppendLine($"- **Category**: {strategy.Category}");
                sb.AppendLine($"- **Tier**: {strategy.Tier}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    #region Private Helper Methods

    private static string GenerateStrategyDocumentation(IStorageStrategyExtended strategy)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"### {strategy.StrategyName}");
        sb.AppendLine();
        sb.AppendLine($"**ID**: `{strategy.StrategyId}`");
        sb.AppendLine($"**Category**: {strategy.Category}");
        sb.AppendLine($"**Tier**: {strategy.Tier}");
        sb.AppendLine($"**Available**: {(strategy.IsAvailable ? "Yes" : "No")}");
        sb.AppendLine();

        sb.AppendLine("**Capabilities**:");
        sb.AppendLine($"- Versioning: {(strategy.SupportsVersioning ? "✓" : "✗")}");
        sb.AppendLine($"- Tiering: {(strategy.SupportsTiering ? "✓" : "✗")}");
        sb.AppendLine($"- Replication: {(strategy.SupportsReplication ? "✓" : "✗")}");

        if (strategy.MaxObjectSize.HasValue)
        {
            sb.AppendLine($"- Max Object Size: {FormatBytes(strategy.MaxObjectSize.Value)}");
        }

        sb.AppendLine();

        return sb.ToString();
    }

    private static string GenerateComparisonMatrix(List<IStorageStrategyExtended> strategies)
    {
        var sb = new StringBuilder();

        sb.AppendLine("| Strategy | Category | Tier | Versioning | Replication | Available |");
        sb.AppendLine("|----------|----------|------|------------|-------------|-----------|");

        foreach (var strategy in strategies.OrderBy(s => s.Category).ThenBy(s => s.StrategyName))
        {
            sb.AppendLine($"| {strategy.StrategyName} | {strategy.Category} | {strategy.Tier} | " +
                         $"{(strategy.SupportsVersioning ? "✓" : "✗")} | " +
                         $"{(strategy.SupportsReplication ? "✓" : "✗")} | " +
                         $"{(strategy.IsAvailable ? "✓" : "✗")} |");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static string GenerateBestPractices()
    {
        var sb = new StringBuilder();

        sb.AppendLine("1. **Choose the Right Tier**");
        sb.AppendLine("   - Hot tier: Frequently accessed data, low latency required");
        sb.AppendLine("   - Warm tier: Occasionally accessed data, moderate latency acceptable");
        sb.AppendLine("   - Cold tier: Rarely accessed data, archival purposes");
        sb.AppendLine();

        sb.AppendLine("2. **Enable Versioning for Critical Data**");
        sb.AppendLine("   - Use strategies with versioning support for important data");
        sb.AppendLine("   - Configure retention policies appropriately");
        sb.AppendLine();

        sb.AppendLine("3. **Use Replication for High Availability**");
        sb.AppendLine("   - Replicate critical data across multiple backends");
        sb.AppendLine("   - Choose geographically distributed strategies");
        sb.AppendLine();

        sb.AppendLine("4. **Monitor Storage Health**");
        sb.AppendLine("   - Regularly run health checks on storage backends");
        sb.AppendLine("   - Set up alerts for storage failures");
        sb.AppendLine();

        sb.AppendLine("5. **Optimize for Your Use Case**");
        sb.AppendLine("   - Local storage: Best for development and testing");
        sb.AppendLine("   - Cloud storage: Best for production and scalability");
        sb.AppendLine("   - Distributed storage: Best for decentralization and immutability");
        sb.AppendLine();

        return sb.ToString();
    }

    private static void AddCapability(Dictionary<string, List<string>> capabilities, string capability, bool supported, string strategyName)
    {
        if (supported)
        {
            if (!capabilities.ContainsKey(capability))
            {
                capabilities[capability] = [];
            }
            capabilities[capability].Add(strategyName);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB", "PB"];
        var order = 0;
        double size = bytes;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {sizes[order]}";
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Storage requirements for strategy selection.
/// </summary>
public sealed class StorageRequirements
{
    /// <summary>Required performance tier.</summary>
    public StorageTier PerformanceTier { get; set; } = StorageTier.Warm;

    /// <summary>Whether versioning is required.</summary>
    public bool RequiresVersioning { get; set; }

    /// <summary>Whether replication is required.</summary>
    public bool RequiresReplication { get; set; }

    /// <summary>Expected data size in bytes.</summary>
    public long ExpectedDataSize { get; set; }

    /// <summary>Whether high availability is required.</summary>
    public bool RequiresHighAvailability { get; set; }

    /// <summary>Maximum acceptable latency in milliseconds.</summary>
    public int MaxLatencyMs { get; set; } = 1000;

    /// <summary>Budget constraints.</summary>
    public BudgetLevel Budget { get; set; } = BudgetLevel.Medium;
}

/// <summary>
/// Budget level for storage selection.
/// </summary>
public enum BudgetLevel
{
    /// <summary>Low budget - prioritize cost-effective options.</summary>
    Low,

    /// <summary>Medium budget - balance cost and performance.</summary>
    Medium,

    /// <summary>High budget - prioritize performance and features.</summary>
    High,

    /// <summary>Unlimited budget - best available options.</summary>
    Unlimited
}

#endregion
