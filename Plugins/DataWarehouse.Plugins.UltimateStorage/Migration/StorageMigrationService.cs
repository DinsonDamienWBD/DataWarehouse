using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;

namespace DataWarehouse.Plugins.UltimateStorage.Migration;

/// <summary>
/// Service for migrating from individual storage plugins to UltimateStorage strategies.
/// Discovers existing storage plugins, maps them to equivalent UltimateStorage strategies,
/// and provides configuration migration helpers.
/// </summary>
/// <remarks>
/// This service helps users transition from standalone storage plugins (e.g., S3Storage, AzureBlobStorage)
/// to the unified UltimateStorage plugin. It maintains compatibility during the migration period
/// by providing mapping between old plugin IDs and new strategy IDs.
/// </remarks>
public sealed class StorageMigrationService
{
    private readonly Dictionary<string, string> _pluginToStrategyMap;
    private readonly Dictionary<string, Dictionary<string, string>> _configKeyMappings;

    /// <summary>
    /// Initializes a new instance of the StorageMigrationService.
    /// Builds the plugin-to-strategy mapping for all known storage plugins.
    /// </summary>
    public StorageMigrationService()
    {
        _pluginToStrategyMap = BuildPluginToStrategyMap();
        _configKeyMappings = BuildConfigKeyMappings();
    }

    /// <summary>
    /// Gets the mapping from old plugin IDs to new UltimateStorage strategy IDs.
    /// </summary>
    public IReadOnlyDictionary<string, string> PluginToStrategyMap => _pluginToStrategyMap;

    /// <summary>
    /// Discovers all storage plugins currently registered in the system.
    /// </summary>
    /// <param name="pluginRegistry">The plugin registry to scan.</param>
    /// <returns>List of discovered storage plugin information.</returns>
    public List<StoragePluginInfo> DiscoverExistingStoragePlugins(IEnumerable<IPlugin> pluginRegistry)
    {
        ArgumentNullException.ThrowIfNull(pluginRegistry);

        var storagePlugins = new List<StoragePluginInfo>();

        foreach (var plugin in pluginRegistry)
        {
            if (IsStoragePlugin(plugin))
            {
                var subCategory = plugin is PipelinePluginBase pipelinePlugin ? pipelinePlugin.SubCategory : "Unknown";

                var info = new StoragePluginInfo
                {
                    PluginId = plugin.Id,
                    PluginName = plugin.Name,
                    PluginVersion = plugin.Version,
                    SubCategory = subCategory,
                    MappedStrategyId = GetMappedStrategyId(plugin.Id),
                    IsDeprecated = IsDeprecatedPlugin(plugin.Id)
                };

                storagePlugins.Add(info);
            }
        }

        return storagePlugins;
    }

    /// <summary>
    /// Gets the UltimateStorage strategy ID that corresponds to a legacy plugin ID.
    /// </summary>
    /// <param name="pluginId">The legacy plugin ID.</param>
    /// <returns>The corresponding strategy ID, or null if no mapping exists.</returns>
    public string? GetMappedStrategyId(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _pluginToStrategyMap.TryGetValue(pluginId, out var strategyId) ? strategyId : null;
    }

    /// <summary>
    /// Migrates configuration from a legacy plugin to UltimateStorage strategy format.
    /// </summary>
    /// <param name="pluginId">The legacy plugin ID.</param>
    /// <param name="legacyConfig">The legacy plugin configuration.</param>
    /// <returns>Migrated configuration for UltimateStorage strategy.</returns>
    public Dictionary<string, object> MigrateConfiguration(string pluginId, Dictionary<string, object> legacyConfig)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(legacyConfig);

        var migratedConfig = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // Add strategy ID
        var strategyId = GetMappedStrategyId(pluginId);
        if (strategyId != null)
        {
            migratedConfig["strategyId"] = strategyId;
        }

        // Map configuration keys
        if (_configKeyMappings.TryGetValue(pluginId, out var keyMapping))
        {
            foreach (var (oldKey, value) in legacyConfig)
            {
                var newKey = keyMapping.TryGetValue(oldKey, out var mapped) ? mapped : oldKey;
                migratedConfig[newKey] = value;
            }
        }
        else
        {
            // No specific mapping, copy as-is
            foreach (var (key, value) in legacyConfig)
            {
                migratedConfig[key] = value;
            }
        }

        return migratedConfig;
    }

    /// <summary>
    /// Validates that a migrated configuration is compatible with UltimateStorage.
    /// </summary>
    /// <param name="strategyId">The target strategy ID.</param>
    /// <param name="config">The migrated configuration.</param>
    /// <returns>Validation result with any errors or warnings.</returns>
    public MigrationValidationResult ValidateMigratedConfiguration(string strategyId, Dictionary<string, object> config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentNullException.ThrowIfNull(config);

        var result = new MigrationValidationResult
        {
            IsValid = true,
            StrategyId = strategyId
        };

        // Check required keys based on strategy
        var requiredKeys = GetRequiredConfigKeys(strategyId);
        foreach (var requiredKey in requiredKeys)
        {
            if (!config.ContainsKey(requiredKey))
            {
                result.Errors.Add($"Missing required configuration key: {requiredKey}");
                result.IsValid = false;
            }
        }

        // Check for obsolete keys
        var obsoleteKeys = GetObsoleteConfigKeys(strategyId);
        foreach (var obsoleteKey in obsoleteKeys)
        {
            if (config.ContainsKey(obsoleteKey))
            {
                result.Warnings.Add($"Configuration key '{obsoleteKey}' is obsolete and will be ignored");
            }
        }

        return result;
    }

    /// <summary>
    /// Generates a migration plan for transitioning from legacy plugins to UltimateStorage.
    /// </summary>
    /// <param name="currentPlugins">Currently active storage plugins.</param>
    /// <returns>Migration plan with steps and recommendations.</returns>
    public StorageMigrationPlan GenerateMigrationPlan(List<StoragePluginInfo> currentPlugins)
    {
        ArgumentNullException.ThrowIfNull(currentPlugins);

        var plan = new StorageMigrationPlan
        {
            CreatedAt = DateTime.UtcNow,
            TotalPluginsToMigrate = currentPlugins.Count
        };

        foreach (var plugin in currentPlugins)
        {
            var step = new MigrationStep
            {
                LegacyPluginId = plugin.PluginId,
                LegacyPluginName = plugin.PluginName,
                TargetStrategyId = plugin.MappedStrategyId ?? "unknown",
                Priority = GetMigrationPriority(plugin),
                Complexity = EstimateMigrationComplexity(plugin),
                EstimatedDurationMinutes = EstimateMigrationDuration(plugin)
            };

            // Add recommendations
            if (plugin.IsDeprecated)
            {
                step.Recommendations.Add("This plugin is deprecated. Migrate as soon as possible.");
            }

            if (string.IsNullOrEmpty(plugin.MappedStrategyId))
            {
                step.Recommendations.Add("No direct strategy mapping available. Manual migration required.");
                step.RequiresManualIntervention = true;
            }

            plan.Steps.Add(step);
        }

        // Sort steps by priority
        plan.Steps = plan.Steps.OrderByDescending(s => s.Priority).ToList();

        return plan;
    }

    /// <summary>
    /// Creates a migration report documenting the changes made during migration.
    /// </summary>
    /// <param name="plan">The migration plan that was executed.</param>
    /// <param name="executionResults">Results from executing each migration step.</param>
    /// <returns>Comprehensive migration report.</returns>
    public MigrationReport CreateMigrationReport(StorageMigrationPlan plan, List<MigrationStepResult> executionResults)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(executionResults);

        var report = new MigrationReport
        {
            MigrationPlan = plan,
            CompletedAt = DateTime.UtcNow,
            TotalSteps = plan.Steps.Count,
            SuccessfulSteps = executionResults.Count(r => r.Success),
            FailedSteps = executionResults.Count(r => !r.Success),
            TotalDurationMinutes = (DateTime.UtcNow - plan.CreatedAt).TotalMinutes,
            StepResults = executionResults
        };

        report.OverallSuccess = report.FailedSteps == 0;

        return report;
    }

    #region Private Helper Methods

    /// <summary>
    /// Builds the mapping from legacy plugin IDs to UltimateStorage strategy IDs.
    /// </summary>
    private static Dictionary<string, string> BuildPluginToStrategyMap()
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
            ["com.datawarehouse.storage.ramdisk"] = "ramdisk",

            // Network Storage
            ["com.datawarehouse.storage.nfs"] = "nfs",
            ["com.datawarehouse.storage.smb"] = "smb-cifs",
            ["com.datawarehouse.storage.ftp"] = "ftp",
            ["com.datawarehouse.storage.sftp"] = "sftp",
            ["com.datawarehouse.storage.webdav"] = "webdav",

            // Database Storage
            ["com.datawarehouse.storage.mongodb"] = "mongodb-gridfs",
            ["com.datawarehouse.storage.postgresql"] = "postgresql-largeobject",
            ["com.datawarehouse.storage.sqlserver"] = "sqlserver-filestream",

            // Distributed Storage
            ["com.datawarehouse.storage.ipfs"] = "ipfs",
            ["com.datawarehouse.storage.arweave"] = "arweave",
            ["com.datawarehouse.storage.storj"] = "storj",
            ["com.datawarehouse.storage.sia"] = "sia",
            ["com.datawarehouse.storage.filecoin"] = "filecoin",

            // Object Storage
            ["com.datawarehouse.storage.swift"] = "openstack-swift",
            ["com.datawarehouse.storage.ceph"] = "ceph-rados",
            ["com.datawarehouse.storage.wasabi"] = "wasabi",
            ["com.datawarehouse.storage.backblaze"] = "backblaze-b2",

            // Key-Value Stores
            ["com.datawarehouse.storage.redis"] = "redis",
            ["com.datawarehouse.storage.memcached"] = "memcached",
            ["com.datawarehouse.storage.etcd"] = "etcd",
            ["com.datawarehouse.storage.consul"] = "consul",

            // Specialized Storage
            ["com.datawarehouse.storage.tape"] = "tape-lto",
            ["com.datawarehouse.storage.optical"] = "optical-disc",
            ["com.datawarehouse.storage.cold"] = "glacier-deep-archive"
        };
    }

    /// <summary>
    /// Builds configuration key mappings for different plugins.
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> BuildConfigKeyMappings()
    {
        return new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["com.datawarehouse.storage.s3"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AccessKey"] = "accessKeyId",
                ["SecretKey"] = "secretAccessKey",
                ["BucketName"] = "bucket",
                ["RegionEndpoint"] = "region"
            },
            ["com.datawarehouse.storage.azureblob"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AccountName"] = "accountName",
                ["AccountKey"] = "accountKey",
                ["ContainerName"] = "container",
                ["EndpointSuffix"] = "endpointSuffix"
            },
            ["com.datawarehouse.storage.filesystem"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["RootPath"] = "basePath",
                ["CreateDirectories"] = "autoCreateDirectories"
            }
        };
    }

    /// <summary>
    /// Checks if a plugin is a storage plugin based on its properties.
    /// </summary>
    private static bool IsStoragePlugin(IPlugin plugin)
    {
        // Check if plugin has SubCategory property (pipeline plugins)
        if (plugin is PipelinePluginBase pipelinePlugin)
        {
            return pipelinePlugin.SubCategory?.Equals("Storage", StringComparison.OrdinalIgnoreCase) == true;
        }

        // Fallback: check ID contains "storage"
        return plugin.Id.Contains("storage", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if a plugin is deprecated.
    /// </summary>
    private static bool IsDeprecatedPlugin(string pluginId)
    {
        // Plugins marked for deprecation in upcoming releases
        var deprecatedPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "com.datawarehouse.storage.ftp",
            "com.datawarehouse.storage.tape"
        };

        return deprecatedPlugins.Contains(pluginId);
    }

    /// <summary>
    /// Gets required configuration keys for a strategy.
    /// </summary>
    private static List<string> GetRequiredConfigKeys(string strategyId)
    {
        return strategyId.ToLowerInvariant() switch
        {
            "aws-s3" => ["accessKeyId", "secretAccessKey", "bucket", "region"],
            "azure-blob" => ["accountName", "accountKey", "container"],
            "gcs" => ["projectId", "bucket", "credentials"],
            "filesystem" => ["basePath"],
            "mongodb-gridfs" => ["connectionString", "database"],
            _ => []
        };
    }

    /// <summary>
    /// Gets obsolete configuration keys that should no longer be used.
    /// </summary>
    private static List<string> GetObsoleteConfigKeys(string strategyId)
    {
        return strategyId.ToLowerInvariant() switch
        {
            "aws-s3" => ["UseHttp", "ForcePathStyle"],
            "azure-blob" => ["UseDevelopmentStorage"],
            _ => []
        };
    }

    /// <summary>
    /// Gets migration priority for a plugin (1=Low, 5=Critical).
    /// </summary>
    private static int GetMigrationPriority(StoragePluginInfo plugin)
    {
        if (plugin.IsDeprecated) return 5; // Critical
        if (plugin.MappedStrategyId == null) return 2; // Low-Medium
        return 3; // Medium
    }

    /// <summary>
    /// Estimates migration complexity (1=Simple, 5=Complex).
    /// </summary>
    private static int EstimateMigrationComplexity(StoragePluginInfo plugin)
    {
        if (plugin.MappedStrategyId == null) return 5; // Complex
        if (plugin.IsDeprecated) return 3; // Medium
        return 2; // Simple-Medium
    }

    /// <summary>
    /// Estimates migration duration in minutes.
    /// </summary>
    private static double EstimateMigrationDuration(StoragePluginInfo plugin)
    {
        var complexity = EstimateMigrationComplexity(plugin);
        return complexity * 15.0; // 15 minutes per complexity point
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Information about a discovered storage plugin.
/// </summary>
public sealed class StoragePluginInfo
{
    /// <summary>Plugin unique identifier.</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>Plugin human-readable name.</summary>
    public string PluginName { get; set; } = string.Empty;

    /// <summary>Plugin version.</summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>Plugin subcategory.</summary>
    public string SubCategory { get; set; } = string.Empty;

    /// <summary>The UltimateStorage strategy this plugin maps to.</summary>
    public string? MappedStrategyId { get; set; }

    /// <summary>Whether this plugin is deprecated.</summary>
    public bool IsDeprecated { get; set; }
}

/// <summary>
/// Result of validating a migrated configuration.
/// </summary>
public sealed class MigrationValidationResult
{
    /// <summary>Whether the configuration is valid.</summary>
    public bool IsValid { get; set; }

    /// <summary>Target strategy ID.</summary>
    public string StrategyId { get; set; } = string.Empty;

    /// <summary>Validation errors.</summary>
    public List<string> Errors { get; set; } = [];

    /// <summary>Validation warnings.</summary>
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// A plan for migrating storage plugins to UltimateStorage.
/// </summary>
public sealed class StorageMigrationPlan
{
    /// <summary>When the plan was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Total number of plugins to migrate.</summary>
    public int TotalPluginsToMigrate { get; set; }

    /// <summary>Individual migration steps.</summary>
    public List<MigrationStep> Steps { get; set; } = [];
}

/// <summary>
/// A single step in the migration plan.
/// </summary>
public sealed class MigrationStep
{
    /// <summary>Legacy plugin ID.</summary>
    public string LegacyPluginId { get; set; } = string.Empty;

    /// <summary>Legacy plugin name.</summary>
    public string LegacyPluginName { get; set; } = string.Empty;

    /// <summary>Target UltimateStorage strategy ID.</summary>
    public string TargetStrategyId { get; set; } = string.Empty;

    /// <summary>Migration priority (1=Low, 5=Critical).</summary>
    public int Priority { get; set; }

    /// <summary>Estimated complexity (1=Simple, 5=Complex).</summary>
    public int Complexity { get; set; }

    /// <summary>Estimated duration in minutes.</summary>
    public double EstimatedDurationMinutes { get; set; }

    /// <summary>Whether manual intervention is required.</summary>
    public bool RequiresManualIntervention { get; set; }

    /// <summary>Migration recommendations.</summary>
    public List<string> Recommendations { get; set; } = [];
}

/// <summary>
/// Result of executing a migration step.
/// </summary>
public sealed class MigrationStepResult
{
    /// <summary>The migration step that was executed.</summary>
    public MigrationStep Step { get; set; } = new();

    /// <summary>Whether the step succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Duration of the step execution.</summary>
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Report documenting a completed migration.
/// </summary>
public sealed class MigrationReport
{
    /// <summary>The migration plan that was executed.</summary>
    public StorageMigrationPlan MigrationPlan { get; set; } = new();

    /// <summary>When the migration completed.</summary>
    public DateTime CompletedAt { get; set; }

    /// <summary>Total number of steps.</summary>
    public int TotalSteps { get; set; }

    /// <summary>Number of successful steps.</summary>
    public int SuccessfulSteps { get; set; }

    /// <summary>Number of failed steps.</summary>
    public int FailedSteps { get; set; }

    /// <summary>Total migration duration in minutes.</summary>
    public double TotalDurationMinutes { get; set; }

    /// <summary>Whether the overall migration succeeded.</summary>
    public bool OverallSuccess { get; set; }

    /// <summary>Results for each step.</summary>
    public List<MigrationStepResult> StepResults { get; set; } = [];
}

#endregion
