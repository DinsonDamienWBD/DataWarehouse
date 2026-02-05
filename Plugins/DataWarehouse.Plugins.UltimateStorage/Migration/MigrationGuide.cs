using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DataWarehouse.Plugins.UltimateStorage.Migration;

/// <summary>
/// Static class providing migration guide documentation as code.
/// Generates migration reports, configuration mappings, and step-by-step migration instructions
/// for transitioning from legacy storage plugins to UltimateStorage strategies.
/// </summary>
/// <remarks>
/// This class serves as a comprehensive migration guide, documenting:
/// - Plugin-to-strategy mappings
/// - Configuration key transformations
/// - Breaking changes and compatibility notes
/// - Best practices and recommendations
/// - Common pitfalls and how to avoid them
/// </remarks>
public static class MigrationGuide
{
    /// <summary>
    /// Gets the current migration guide version.
    /// </summary>
    public const string Version = "1.0.0";

    /// <summary>
    /// Gets the migration guide last updated date.
    /// </summary>
    public static readonly DateTime LastUpdated = new(2026, 2, 5);

    /// <summary>
    /// Generates a comprehensive migration report for a specific legacy plugin.
    /// </summary>
    /// <param name="pluginId">The legacy plugin ID to generate report for.</param>
    /// <returns>Detailed migration guide text.</returns>
    public static string GenerateMigrationReport(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        var sb = new StringBuilder();

        sb.AppendLine($"# Migration Guide for {pluginId}");
        sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Guide Version: {Version}");
        sb.AppendLine();

        // Get strategy mapping
        var strategyId = GetStrategyMapping(pluginId);
        if (strategyId == null)
        {
            sb.AppendLine("**WARNING**: No direct mapping available for this plugin.");
            sb.AppendLine("Manual migration required. Contact support for assistance.");
            return sb.ToString();
        }

        sb.AppendLine($"## Target Strategy: {strategyId}");
        sb.AppendLine();

        // Migration steps
        sb.AppendLine("## Migration Steps");
        sb.AppendLine();
        sb.AppendLine("1. **Install UltimateStorage Plugin**");
        sb.AppendLine("   - Ensure the UltimateStorage plugin is registered in your pipeline");
        sb.AppendLine("   - Version requirement: >= 1.0.0");
        sb.AppendLine();

        sb.AppendLine("2. **Update Configuration**");
        sb.AppendLine("   - Migrate configuration keys using the mapping below");
        sb.AppendLine("   - Update any hardcoded plugin references");
        sb.AppendLine();

        // Configuration mappings
        var configMappings = GetConfigurationMappings(pluginId);
        if (configMappings.Count > 0)
        {
            sb.AppendLine("### Configuration Key Mappings");
            sb.AppendLine();
            sb.AppendLine("| Legacy Key | New Key | Notes |");
            sb.AppendLine("|------------|---------|-------|");

            foreach (var (oldKey, newKey) in configMappings)
            {
                var notes = GetConfigKeyNotes(pluginId, oldKey);
                sb.AppendLine($"| `{oldKey}` | `{newKey}` | {notes} |");
            }
            sb.AppendLine();
        }

        // Breaking changes
        var breakingChanges = GetBreakingChanges(pluginId);
        if (breakingChanges.Count > 0)
        {
            sb.AppendLine("## Breaking Changes");
            sb.AppendLine();
            foreach (var change in breakingChanges)
            {
                sb.AppendLine($"- {change}");
            }
            sb.AppendLine();
        }

        // Compatibility notes
        var compatNotes = GetCompatibilityNotes(pluginId);
        if (compatNotes.Count > 0)
        {
            sb.AppendLine("## Compatibility Notes");
            sb.AppendLine();
            foreach (var note in compatNotes)
            {
                sb.AppendLine($"- {note}");
            }
            sb.AppendLine();
        }

        // Code examples
        sb.AppendLine("## Code Examples");
        sb.AppendLine();
        sb.AppendLine("### Before (Legacy Plugin)");
        sb.AppendLine("```csharp");
        sb.AppendLine(GetLegacyCodeExample(pluginId));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### After (UltimateStorage)");
        sb.AppendLine("```csharp");
        sb.AppendLine(GetMigratedCodeExample(pluginId, strategyId));
        sb.AppendLine("```");
        sb.AppendLine();

        // Testing recommendations
        sb.AppendLine("## Testing Recommendations");
        sb.AppendLine();
        sb.AppendLine("1. Test in a non-production environment first");
        sb.AppendLine("2. Verify read/write operations work correctly");
        sb.AppendLine("3. Confirm existing data is accessible");
        sb.AppendLine("4. Monitor performance metrics");
        sb.AppendLine("5. Check error handling and retry logic");
        sb.AppendLine();

        // Rollback plan
        sb.AppendLine("## Rollback Plan");
        sb.AppendLine();
        sb.AppendLine("If issues occur during migration:");
        sb.AppendLine("1. Keep the legacy plugin installed during transition period");
        sb.AppendLine("2. Maintain backups of configuration files");
        sb.AppendLine("3. Document any custom modifications");
        sb.AppendLine("4. Test rollback procedure in advance");
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Maps old configuration keys to new UltimateStorage configuration keys.
    /// </summary>
    /// <param name="pluginId">The legacy plugin ID.</param>
    /// <param name="legacyConfig">The legacy configuration dictionary.</param>
    /// <returns>Mapped configuration for UltimateStorage.</returns>
    public static Dictionary<string, object> MapConfigurationKeys(
        string pluginId,
        Dictionary<string, object> legacyConfig)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(legacyConfig);

        var mappedConfig = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var keyMappings = GetConfigurationMappings(pluginId);

        foreach (var (key, value) in legacyConfig)
        {
            var newKey = keyMappings.TryGetValue(key, out var mapped) ? mapped : key;
            mappedConfig[newKey] = value;
        }

        // Add strategy ID
        var strategyId = GetStrategyMapping(pluginId);
        if (strategyId != null)
        {
            mappedConfig["strategyId"] = strategyId;
        }

        return mappedConfig;
    }

    /// <summary>
    /// Validates a migrated configuration against UltimateStorage requirements.
    /// </summary>
    /// <param name="pluginId">The legacy plugin ID.</param>
    /// <param name="migratedConfig">The migrated configuration.</param>
    /// <returns>List of validation errors (empty if valid).</returns>
    public static List<string> ValidateMigratedConfiguration(
        string pluginId,
        Dictionary<string, object> migratedConfig)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(migratedConfig);

        var errors = new List<string>();

        // Check strategy ID is present
        if (!migratedConfig.ContainsKey("strategyId"))
        {
            errors.Add("Missing required 'strategyId' key");
        }

        // Check required keys
        var requiredKeys = GetRequiredKeys(pluginId);
        foreach (var requiredKey in requiredKeys)
        {
            if (!migratedConfig.ContainsKey(requiredKey))
            {
                errors.Add($"Missing required configuration key: {requiredKey}");
            }
        }

        // Check for deprecated keys
        var deprecatedKeys = GetDeprecatedKeys(pluginId);
        foreach (var deprecatedKey in deprecatedKeys)
        {
            if (migratedConfig.ContainsKey(deprecatedKey))
            {
                errors.Add($"Configuration contains deprecated key: {deprecatedKey}");
            }
        }

        return errors;
    }

    /// <summary>
    /// Gets a complete list of all plugin-to-strategy mappings.
    /// </summary>
    /// <returns>Dictionary mapping plugin IDs to strategy IDs.</returns>
    public static Dictionary<string, string> GetAllMappings()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Cloud Storage
            ["com.datawarehouse.storage.s3"] = "aws-s3",
            ["com.datawarehouse.storage.azureblob"] = "azure-blob",
            ["com.datawarehouse.storage.gcs"] = "gcs",
            ["com.datawarehouse.storage.minio"] = "minio",
            ["com.datawarehouse.storage.digitalocean-spaces"] = "digitalocean-spaces",

            // Local Storage
            ["com.datawarehouse.storage.filesystem"] = "filesystem",
            ["com.datawarehouse.storage.memory"] = "memory",
            ["com.datawarehouse.storage.memorymapped"] = "memory-mapped",

            // Network Storage
            ["com.datawarehouse.storage.nfs"] = "nfs",
            ["com.datawarehouse.storage.smb"] = "smb-cifs",
            ["com.datawarehouse.storage.ftp"] = "ftp",
            ["com.datawarehouse.storage.sftp"] = "sftp",

            // Distributed Storage
            ["com.datawarehouse.storage.ipfs"] = "ipfs",
            ["com.datawarehouse.storage.arweave"] = "arweave",
            ["com.datawarehouse.storage.storj"] = "storj"
        };
    }

    /// <summary>
    /// Generates a comparison matrix showing feature parity between legacy plugin and new strategy.
    /// </summary>
    /// <param name="pluginId">The legacy plugin ID.</param>
    /// <returns>Feature comparison matrix as formatted text.</returns>
    public static string GenerateFeatureComparisonMatrix(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        var sb = new StringBuilder();
        sb.AppendLine($"# Feature Comparison: {pluginId}");
        sb.AppendLine();
        sb.AppendLine("| Feature | Legacy Plugin | UltimateStorage | Notes |");
        sb.AppendLine("|---------|---------------|-----------------|-------|");

        var features = GetFeatureComparison(pluginId);
        foreach (var feature in features)
        {
            sb.AppendLine($"| {feature.Name} | {FormatSupport(feature.LegacySupport)} | {FormatSupport(feature.NewSupport)} | {feature.Notes} |");
        }

        return sb.ToString();
    }

    #region Private Helper Methods

    private static string? GetStrategyMapping(string pluginId)
    {
        var mappings = GetAllMappings();
        return mappings.TryGetValue(pluginId, out var strategy) ? strategy : null;
    }

    private static Dictionary<string, string> GetConfigurationMappings(string pluginId)
    {
        return pluginId.ToLowerInvariant() switch
        {
            "com.datawarehouse.storage.s3" => new Dictionary<string, string>
            {
                ["AccessKey"] = "accessKeyId",
                ["SecretKey"] = "secretAccessKey",
                ["BucketName"] = "bucket",
                ["RegionEndpoint"] = "region"
            },
            "com.datawarehouse.storage.azureblob" => new Dictionary<string, string>
            {
                ["AccountName"] = "accountName",
                ["AccountKey"] = "accountKey",
                ["ContainerName"] = "container"
            },
            "com.datawarehouse.storage.filesystem" => new Dictionary<string, string>
            {
                ["RootPath"] = "basePath",
                ["CreateDirectories"] = "autoCreateDirectories"
            },
            _ => new Dictionary<string, string>()
        };
    }

    private static string GetConfigKeyNotes(string pluginId, string oldKey)
    {
        return (pluginId.ToLowerInvariant(), oldKey.ToLowerInvariant()) switch
        {
            ("com.datawarehouse.storage.s3", "regionendpoint") => "Use AWS region code (e.g., 'us-east-1')",
            ("com.datawarehouse.storage.azureblob", "accountkey") => "Can use SAS token instead",
            _ => "Direct mapping"
        };
    }

    private static List<string> GetBreakingChanges(string pluginId)
    {
        return pluginId.ToLowerInvariant() switch
        {
            "com.datawarehouse.storage.s3" =>
            [
                "Configuration keys have been renamed (see mapping table)",
                "Strategy must be explicitly specified via 'strategyId' parameter",
                "Synchronous API has been removed - use async methods only"
            ],
            "com.datawarehouse.storage.filesystem" =>
            [
                "Path separators are now normalized automatically",
                "Root path must be absolute"
            ],
            _ => []
        };
    }

    private static List<string> GetCompatibilityNotes(string pluginId)
    {
        return pluginId.ToLowerInvariant() switch
        {
            "com.datawarehouse.storage.s3" =>
            [
                "Existing S3 objects remain accessible without migration",
                "Metadata format is compatible",
                "Multipart upload behavior is unchanged"
            ],
            "com.datawarehouse.storage.filesystem" =>
            [
                "File paths use forward slashes on all platforms",
                "Existing files can be read without changes"
            ],
            _ => ["Binary compatibility maintained for stored data"]
        };
    }

    private static string GetLegacyCodeExample(string pluginId)
    {
        return pluginId.ToLowerInvariant() switch
        {
            "com.datawarehouse.storage.s3" =>
                "var plugin = new S3StoragePlugin();\n" +
                "plugin.Configure(new Dictionary<string, object>\n" +
                "{\n" +
                "    [\"AccessKey\"] = \"your-access-key\",\n" +
                "    [\"SecretKey\"] = \"your-secret-key\",\n" +
                "    [\"BucketName\"] = \"my-bucket\",\n" +
                "    [\"RegionEndpoint\"] = \"us-east-1\"\n" +
                "});\n" +
                "await plugin.StoreAsync(\"file.txt\", dataStream);",

            _ => "// Legacy plugin usage\nvar plugin = new LegacyStoragePlugin();\nawait plugin.StoreAsync(key, data);"
        };
    }

    private static string GetMigratedCodeExample(string pluginId, string strategyId)
    {
        return pluginId.ToLowerInvariant() switch
        {
            "com.datawarehouse.storage.s3" =>
                "var plugin = new UltimateStoragePlugin();\n" +
                "var args = new Dictionary<string, object>\n" +
                "{\n" +
                "    [\"strategyId\"] = \"aws-s3\",\n" +
                "    [\"accessKeyId\"] = \"your-access-key\",\n" +
                "    [\"secretAccessKey\"] = \"your-secret-key\",\n" +
                "    [\"bucket\"] = \"my-bucket\",\n" +
                "    [\"region\"] = \"us-east-1\"\n" +
                "};\n" +
                "await plugin.OnWriteAsync(dataStream, context, args);",

            _ => $"// UltimateStorage usage\nvar plugin = new UltimateStoragePlugin();\nvar args = new Dictionary<string, object> {{ [\"strategyId\"] = \"{strategyId}\" }};\nawait plugin.OnWriteAsync(dataStream, context, args);"
        };
    }

    private static List<string> GetRequiredKeys(string pluginId)
    {
        return pluginId.ToLowerInvariant() switch
        {
            "com.datawarehouse.storage.s3" => ["accessKeyId", "secretAccessKey", "bucket", "region"],
            "com.datawarehouse.storage.azureblob" => ["accountName", "accountKey", "container"],
            "com.datawarehouse.storage.filesystem" => ["basePath"],
            _ => []
        };
    }

    private static List<string> GetDeprecatedKeys(string pluginId)
    {
        return pluginId.ToLowerInvariant() switch
        {
            "com.datawarehouse.storage.s3" => ["UseHttp", "ForcePathStyle"],
            "com.datawarehouse.storage.azureblob" => ["UseDevelopmentStorage"],
            _ => []
        };
    }

    private static List<FeatureComparison> GetFeatureComparison(string pluginId)
    {
        var features = new List<FeatureComparison>
        {
            new() { Name = "Read", LegacySupport = true, NewSupport = true, Notes = "Fully compatible" },
            new() { Name = "Write", LegacySupport = true, NewSupport = true, Notes = "Fully compatible" },
            new() { Name = "Delete", LegacySupport = true, NewSupport = true, Notes = "Fully compatible" },
            new() { Name = "List", LegacySupport = true, NewSupport = true, Notes = "Enhanced filtering" },
            new() { Name = "Metadata", LegacySupport = true, NewSupport = true, Notes = "Extended metadata support" },
            new() { Name = "Versioning", LegacySupport = false, NewSupport = true, Notes = "New feature" },
            new() { Name = "Replication", LegacySupport = false, NewSupport = true, Notes = "New feature" },
            new() { Name = "Health Check", LegacySupport = false, NewSupport = true, Notes = "New feature" },
            new() { Name = "Batch Operations", LegacySupport = false, NewSupport = true, Notes = "Performance optimization" }
        };

        return features;
    }

    private static string FormatSupport(bool supported)
    {
        return supported ? "✓ Yes" : "✗ No";
    }

    private sealed class FeatureComparison
    {
        public string Name { get; set; } = string.Empty;
        public bool LegacySupport { get; set; }
        public bool NewSupport { get; set; }
        public string Notes { get; set; } = string.Empty;
    }

    #endregion
}
