using System.Text.Json.Serialization;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Configuration;

// ============================================================================
// FAULT TOLERANCE CONFIGURATION
// Unified configuration for fault tolerance strategies across all deployment tiers
// Supports: None, 3-Replica, Reed-Solomon, RAID-Z3 with user selection
// ============================================================================

#region Enums

/// <summary>
/// Available fault tolerance modes that users can select.
/// </summary>
public enum FaultToleranceMode
{
    /// <summary>
    /// No redundancy - single copy storage. Fastest but no protection.
    /// Suitable for: Temporary data, caches, development environments.
    /// </summary>
    None = 0,

    /// <summary>
    /// Simple mirroring with 2 copies.
    /// Suitable for: Basic redundancy, small deployments.
    /// </summary>
    Mirror2 = 1,

    /// <summary>
    /// Triple replication (3 copies). Industry standard for cloud storage.
    /// Suitable for: Production workloads, cloud-native applications.
    /// </summary>
    Replica3 = 2,

    /// <summary>
    /// Reed-Solomon erasure coding with configurable data/parity shards.
    /// Suitable for: Large datasets, cost-sensitive storage with durability.
    /// </summary>
    ReedSolomon = 3,

    /// <summary>
    /// RAID-6 style dual parity protection.
    /// Suitable for: Enterprise storage arrays, high-capacity deployments.
    /// </summary>
    RAID6 = 4,

    /// <summary>
    /// ZFS RAID-Z3 triple parity - maximum protection.
    /// Suitable for: Mission-critical data, compliance requirements.
    /// </summary>
    RAIDZ3 = 5,

    /// <summary>
    /// Automatic selection based on deployment context and data criticality.
    /// The system chooses the optimal mode.
    /// </summary>
    Automatic = 6
}

/// <summary>
/// Data criticality levels that influence automatic fault tolerance selection.
/// </summary>
public enum DataCriticality
{
    /// <summary>Low criticality - can be regenerated or is non-essential.</summary>
    Low = 0,

    /// <summary>Normal criticality - standard business data.</summary>
    Normal = 1,

    /// <summary>High criticality - important business data, customer data.</summary>
    High = 2,

    /// <summary>Critical - compliance data, financial records, healthcare data.</summary>
    Critical = 3,

    /// <summary>Mission-critical - must never be lost under any circumstances.</summary>
    MissionCritical = 4
}

/// <summary>
/// Deployment tier classification for automatic mode selection.
/// </summary>
public enum DeploymentTier
{
    /// <summary>Single user on laptop/desktop.</summary>
    Individual = 0,

    /// <summary>Small/medium business (2-10 nodes).</summary>
    SMB = 1,

    /// <summary>Enterprise deployment (10-100 nodes).</summary>
    Enterprise = 2,

    /// <summary>High-stakes (healthcare, banking, government).</summary>
    HighStakes = 3,

    /// <summary>Hyperscale (100+ nodes, petabyte scale).</summary>
    Hyperscale = 4
}

/// <summary>
/// Reed-Solomon coding profile presets.
/// </summary>
public enum ReedSolomonProfile
{
    /// <summary>Standard profile: 6 data + 3 parity (can lose 3 shards).</summary>
    Standard = 0,

    /// <summary>High durability: 8 data + 4 parity (can lose 4 shards).</summary>
    HighDurability = 1,

    /// <summary>Storage optimized: 10 data + 2 parity (higher efficiency).</summary>
    StorageOptimized = 2,

    /// <summary>Hyperscale: 16 data + 4 parity (maximum efficiency at scale).</summary>
    Hyperscale = 3,

    /// <summary>Custom profile with user-defined parameters.</summary>
    Custom = 4
}

#endregion

#region Configuration Classes

/// <summary>
/// Unified fault tolerance configuration supporting all modes and user preferences.
/// Thread-safe and immutable after creation.
/// </summary>
public sealed class FaultToleranceConfig
{
    /// <summary>
    /// Primary fault tolerance mode. Can be user-selected or automatic.
    /// </summary>
    public FaultToleranceMode Mode { get; init; } = FaultToleranceMode.Automatic;

    /// <summary>
    /// Whether the user explicitly set this mode (overrides automatic selection).
    /// </summary>
    public bool IsUserOverride { get; init; }

    /// <summary>
    /// Data criticality level - influences automatic mode selection.
    /// </summary>
    public DataCriticality Criticality { get; init; } = DataCriticality.Normal;

    /// <summary>
    /// Deployment tier - influences automatic mode selection.
    /// </summary>
    public DeploymentTier Tier { get; init; } = DeploymentTier.SMB;

    /// <summary>
    /// Reed-Solomon configuration (used when Mode is ReedSolomon).
    /// </summary>
    public ReedSolomonConfig? ReedSolomonConfig { get; init; }

    /// <summary>
    /// Minimum number of storage providers required for the selected mode.
    /// </summary>
    public int MinimumProviders { get; init; } = 1;

    /// <summary>
    /// Whether to automatically fall back to simpler modes if insufficient providers.
    /// </summary>
    public bool AllowFallback { get; init; } = true;

    /// <summary>
    /// Fallback mode when primary mode cannot be satisfied.
    /// </summary>
    public FaultToleranceMode FallbackMode { get; init; } = FaultToleranceMode.Mirror2;

    /// <summary>
    /// Enable write verification (read-after-write to confirm durability).
    /// </summary>
    public bool EnableWriteVerification { get; init; }

    /// <summary>
    /// Enable automatic repair of degraded data.
    /// </summary>
    public bool EnableAutoRepair { get; init; } = true;

    /// <summary>
    /// Interval between scrub operations to detect silent corruption.
    /// </summary>
    public TimeSpan ScrubInterval { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Creates default configuration with automatic mode selection.
    /// </summary>
    public static FaultToleranceConfig Default => new();

    /// <summary>
    /// Creates configuration for individual/laptop deployment.
    /// </summary>
    public static FaultToleranceConfig ForIndividual() => new()
    {
        Mode = FaultToleranceMode.None,
        Tier = DeploymentTier.Individual,
        Criticality = DataCriticality.Normal,
        AllowFallback = false,
        EnableWriteVerification = false,
        EnableAutoRepair = false
    };

    /// <summary>
    /// Creates configuration for SMB deployment.
    /// </summary>
    public static FaultToleranceConfig ForSMB() => new()
    {
        Mode = FaultToleranceMode.Mirror2,
        Tier = DeploymentTier.SMB,
        Criticality = DataCriticality.Normal,
        MinimumProviders = 2,
        AllowFallback = true,
        EnableWriteVerification = false,
        EnableAutoRepair = true
    };

    /// <summary>
    /// Creates configuration for enterprise deployment.
    /// </summary>
    public static FaultToleranceConfig ForEnterprise() => new()
    {
        Mode = FaultToleranceMode.Replica3,
        Tier = DeploymentTier.Enterprise,
        Criticality = DataCriticality.High,
        MinimumProviders = 3,
        AllowFallback = true,
        EnableWriteVerification = true,
        EnableAutoRepair = true
    };

    /// <summary>
    /// Creates configuration for high-stakes deployment (healthcare, banking).
    /// </summary>
    public static FaultToleranceConfig ForHighStakes() => new()
    {
        Mode = FaultToleranceMode.RAIDZ3,
        Tier = DeploymentTier.HighStakes,
        Criticality = DataCriticality.Critical,
        MinimumProviders = 4,
        AllowFallback = false,
        EnableWriteVerification = true,
        EnableAutoRepair = true,
        ScrubInterval = TimeSpan.FromDays(1)
    };

    /// <summary>
    /// Creates configuration for hyperscale deployment.
    /// </summary>
    public static FaultToleranceConfig ForHyperscale() => new()
    {
        Mode = FaultToleranceMode.ReedSolomon,
        Tier = DeploymentTier.Hyperscale,
        Criticality = DataCriticality.High,
        MinimumProviders = 10,
        ReedSolomonConfig = Configuration.ReedSolomonConfig.Hyperscale,
        AllowFallback = true,
        FallbackMode = FaultToleranceMode.Replica3,
        EnableWriteVerification = false, // Too expensive at scale
        EnableAutoRepair = true,
        ScrubInterval = TimeSpan.FromDays(3)
    };

    /// <summary>
    /// Validates the configuration and returns any errors.
    /// </summary>
    public FaultToleranceValidationResult Validate(int availableProviders)
    {
        var result = new FaultToleranceValidationResult();

        // Check minimum providers for the selected mode
        var required = GetMinimumProvidersForMode(Mode);
        if (availableProviders < required)
        {
            if (AllowFallback)
            {
                result.Warnings.Add($"Insufficient providers for {Mode} (have {availableProviders}, need {required}). Will fallback to {FallbackMode}.");
                result.EffectiveMode = FallbackMode;
            }
            else
            {
                result.Errors.Add($"Insufficient providers for {Mode}. Have {availableProviders}, need {required}. Fallback disabled.");
                result.IsValid = false;
            }
        }
        else
        {
            result.EffectiveMode = Mode;
        }

        // Validate Reed-Solomon config if applicable
        if (Mode == FaultToleranceMode.ReedSolomon || result.EffectiveMode == FaultToleranceMode.ReedSolomon)
        {
            if (ReedSolomonConfig == null)
            {
                result.Warnings.Add("Reed-Solomon mode selected but no config provided. Using Standard profile.");
            }
            else if (availableProviders < ReedSolomonConfig.DataShards + ReedSolomonConfig.ParityShards)
            {
                result.Errors.Add($"Reed-Solomon requires {ReedSolomonConfig.DataShards + ReedSolomonConfig.ParityShards} providers but only {availableProviders} available.");
                result.IsValid = false;
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the minimum number of providers required for a given mode.
    /// </summary>
    public static int GetMinimumProvidersForMode(FaultToleranceMode mode)
    {
        return mode switch
        {
            FaultToleranceMode.None => 1,
            FaultToleranceMode.Mirror2 => 2,
            FaultToleranceMode.Replica3 => 3,
            FaultToleranceMode.ReedSolomon => 4, // Minimum 2+2
            FaultToleranceMode.RAID6 => 4,       // Minimum 2 data + 2 parity
            FaultToleranceMode.RAIDZ3 => 4,      // Minimum 1 data + 3 parity
            FaultToleranceMode.Automatic => 1,
            _ => 1
        };
    }
}

/// <summary>
/// Configuration for Reed-Solomon erasure coding.
/// </summary>
public sealed class ReedSolomonConfig
{
    /// <summary>
    /// Number of data shards (k).
    /// </summary>
    public int DataShards { get; init; } = 6;

    /// <summary>
    /// Number of parity shards (m). Can lose up to m shards.
    /// </summary>
    public int ParityShards { get; init; } = 3;

    /// <summary>
    /// Selected profile preset.
    /// </summary>
    public ReedSolomonProfile Profile { get; init; } = ReedSolomonProfile.Standard;

    /// <summary>
    /// Total number of shards required.
    /// </summary>
    [JsonIgnore]
    public int TotalShards => DataShards + ParityShards;

    /// <summary>
    /// Storage overhead percentage.
    /// </summary>
    [JsonIgnore]
    public double StorageOverhead => (double)ParityShards / DataShards * 100;

    /// <summary>
    /// Standard profile: 6+3 configuration.
    /// </summary>
    public static ReedSolomonConfig Standard => new()
    {
        DataShards = 6,
        ParityShards = 3,
        Profile = ReedSolomonProfile.Standard
    };

    /// <summary>
    /// High durability profile: 8+4 configuration.
    /// </summary>
    public static ReedSolomonConfig HighDurability => new()
    {
        DataShards = 8,
        ParityShards = 4,
        Profile = ReedSolomonProfile.HighDurability
    };

    /// <summary>
    /// Storage optimized profile: 10+2 configuration.
    /// </summary>
    public static ReedSolomonConfig StorageOptimized => new()
    {
        DataShards = 10,
        ParityShards = 2,
        Profile = ReedSolomonProfile.StorageOptimized
    };

    /// <summary>
    /// Hyperscale profile: 16+4 configuration.
    /// </summary>
    public static ReedSolomonConfig Hyperscale => new()
    {
        DataShards = 16,
        ParityShards = 4,
        Profile = ReedSolomonProfile.Hyperscale
    };

    /// <summary>
    /// Creates a custom Reed-Solomon configuration.
    /// </summary>
    public static ReedSolomonConfig Custom(int dataShards, int parityShards)
    {
        if (dataShards < 1) throw new ArgumentException("Data shards must be at least 1", nameof(dataShards));
        if (parityShards < 1) throw new ArgumentException("Parity shards must be at least 1", nameof(parityShards));

        return new ReedSolomonConfig
        {
            DataShards = dataShards,
            ParityShards = parityShards,
            Profile = ReedSolomonProfile.Custom
        };
    }
}

/// <summary>
/// Result of fault tolerance configuration validation.
/// </summary>
public sealed class FaultToleranceValidationResult
{
    /// <summary>
    /// Whether the configuration is valid and can be applied.
    /// </summary>
    public bool IsValid { get; set; } = true;

    /// <summary>
    /// The effective mode after considering fallbacks.
    /// </summary>
    public FaultToleranceMode EffectiveMode { get; set; }

    /// <summary>
    /// Validation errors that prevent operation.
    /// </summary>
    public List<string> Errors { get; } = new();

    /// <summary>
    /// Warnings that don't prevent operation but should be noted.
    /// </summary>
    public List<string> Warnings { get; } = new();
}

#endregion

#region Fault Tolerance Manager

/// <summary>
/// Manages fault tolerance strategy selection and application.
/// Provides intelligent automatic selection based on deployment context.
/// </summary>
public sealed class FaultToleranceManager
{
    private readonly BoundedDictionary<string, FaultToleranceConfig> _containerConfigs = new BoundedDictionary<string, FaultToleranceConfig>(1000);
    private FaultToleranceConfig _defaultConfig;
    private readonly object _lock = new();

    public FaultToleranceManager(FaultToleranceConfig? defaultConfig = null)
    {
        _defaultConfig = defaultConfig ?? FaultToleranceConfig.Default;
    }

    /// <summary>
    /// Gets the default fault tolerance configuration.
    /// </summary>
    public FaultToleranceConfig DefaultConfig => _defaultConfig;

    /// <summary>
    /// Sets the default fault tolerance configuration.
    /// </summary>
    public void SetDefaultConfig(FaultToleranceConfig config)
    {
        lock (_lock)
        {
            _defaultConfig = config ?? throw new ArgumentNullException(nameof(config));
        }
    }

    /// <summary>
    /// Sets a container-specific fault tolerance configuration.
    /// </summary>
    public void SetContainerConfig(string containerId, FaultToleranceConfig config)
    {
        ArgumentException.ThrowIfNullOrEmpty(containerId);
        ArgumentNullException.ThrowIfNull(config);

        _containerConfigs[containerId] = config;
    }

    /// <summary>
    /// Gets the fault tolerance configuration for a container.
    /// </summary>
    public FaultToleranceConfig GetConfig(string? containerId = null)
    {
        if (containerId != null && _containerConfigs.TryGetValue(containerId, out var containerConfig))
        {
            return containerConfig;
        }
        return _defaultConfig;
    }

    /// <summary>
    /// Automatically selects the optimal fault tolerance mode based on context.
    /// </summary>
    public FaultToleranceMode SelectAutomaticMode(
        DeploymentTier tier,
        DataCriticality criticality,
        int availableProviders)
    {
        // If not enough providers, return None
        if (availableProviders < 2)
        {
            return FaultToleranceMode.None;
        }

        // Selection matrix based on tier and criticality
        return (tier, criticality) switch
        {
            // Individual tier - minimal overhead
            (DeploymentTier.Individual, DataCriticality.Low) => FaultToleranceMode.None,
            (DeploymentTier.Individual, DataCriticality.Normal) => availableProviders >= 2 ? FaultToleranceMode.Mirror2 : FaultToleranceMode.None,
            (DeploymentTier.Individual, _) => availableProviders >= 2 ? FaultToleranceMode.Mirror2 : FaultToleranceMode.None,

            // SMB tier - balance cost and protection
            (DeploymentTier.SMB, DataCriticality.Low) => FaultToleranceMode.None,
            (DeploymentTier.SMB, DataCriticality.Normal) => availableProviders >= 2 ? FaultToleranceMode.Mirror2 : FaultToleranceMode.None,
            (DeploymentTier.SMB, DataCriticality.High) => availableProviders >= 3 ? FaultToleranceMode.Replica3 : FaultToleranceMode.Mirror2,
            (DeploymentTier.SMB, _) => availableProviders >= 3 ? FaultToleranceMode.Replica3 : FaultToleranceMode.Mirror2,

            // Enterprise tier - reliability focus
            (DeploymentTier.Enterprise, DataCriticality.Low) => FaultToleranceMode.Mirror2,
            (DeploymentTier.Enterprise, DataCriticality.Normal) => availableProviders >= 3 ? FaultToleranceMode.Replica3 : FaultToleranceMode.Mirror2,
            (DeploymentTier.Enterprise, DataCriticality.High) => availableProviders >= 4 ? FaultToleranceMode.RAID6 : FaultToleranceMode.Replica3,
            (DeploymentTier.Enterprise, _) => availableProviders >= 4 ? FaultToleranceMode.RAID6 : FaultToleranceMode.Replica3,

            // High-stakes tier - maximum protection
            (DeploymentTier.HighStakes, DataCriticality.Low) => availableProviders >= 3 ? FaultToleranceMode.Replica3 : FaultToleranceMode.Mirror2,
            (DeploymentTier.HighStakes, DataCriticality.Normal) => availableProviders >= 4 ? FaultToleranceMode.RAID6 : FaultToleranceMode.Replica3,
            (DeploymentTier.HighStakes, _) => availableProviders >= 4 ? FaultToleranceMode.RAIDZ3 : FaultToleranceMode.RAID6,

            // Hyperscale tier - efficiency at scale with durability
            (DeploymentTier.Hyperscale, DataCriticality.Low) => availableProviders >= 4 ? FaultToleranceMode.ReedSolomon : FaultToleranceMode.Mirror2,
            (DeploymentTier.Hyperscale, DataCriticality.Normal) => availableProviders >= 9 ? FaultToleranceMode.ReedSolomon : FaultToleranceMode.Replica3,
            (DeploymentTier.Hyperscale, DataCriticality.High) => availableProviders >= 12 ? FaultToleranceMode.ReedSolomon : FaultToleranceMode.RAID6,
            (DeploymentTier.Hyperscale, _) => availableProviders >= 4 ? FaultToleranceMode.RAIDZ3 : FaultToleranceMode.Replica3,

            // Default fallback
            _ => availableProviders >= 2 ? FaultToleranceMode.Mirror2 : FaultToleranceMode.None
        };
    }

    /// <summary>
    /// Resolves the effective fault tolerance mode, considering automatic selection and fallbacks.
    /// </summary>
    public FaultToleranceResolution ResolveMode(FaultToleranceConfig config, int availableProviders)
    {
        FaultToleranceMode effectiveMode;
        var warnings = new List<string>();

        if (config.Mode == FaultToleranceMode.Automatic && !config.IsUserOverride)
        {
            // Automatic selection
            effectiveMode = SelectAutomaticMode(config.Tier, config.Criticality, availableProviders);
            warnings.Add($"Automatic mode selected: {effectiveMode} based on tier={config.Tier}, criticality={config.Criticality}, providers={availableProviders}");
        }
        else
        {
            effectiveMode = config.Mode;
        }

        // Check if we have enough providers
        var required = FaultToleranceConfig.GetMinimumProvidersForMode(effectiveMode);
        if (availableProviders < required)
        {
            if (config.AllowFallback)
            {
                warnings.Add($"Falling back from {effectiveMode} to {config.FallbackMode} (insufficient providers: have {availableProviders}, need {required})");
                effectiveMode = config.FallbackMode;

                // Check fallback requirements
                var fallbackRequired = FaultToleranceConfig.GetMinimumProvidersForMode(effectiveMode);
                if (availableProviders < fallbackRequired)
                {
                    warnings.Add($"Even fallback mode {effectiveMode} requires {fallbackRequired} providers. Degrading to None.");
                    effectiveMode = FaultToleranceMode.None;
                }
            }
            else
            {
                return new FaultToleranceResolution
                {
                    IsValid = false,
                    EffectiveMode = FaultToleranceMode.None,
                    ErrorMessage = $"Mode {effectiveMode} requires {required} providers but only {availableProviders} available. Fallback disabled.",
                    Warnings = warnings
                };
            }
        }

        // Get Reed-Solomon config if applicable
        ReedSolomonConfig? rsConfig = null;
        if (effectiveMode == FaultToleranceMode.ReedSolomon)
        {
            rsConfig = config.ReedSolomonConfig ?? ReedSolomonConfig.Standard;

            // Validate RS config against available providers
            if (availableProviders < rsConfig.TotalShards)
            {
                // Try to find a smaller RS config that fits
                if (availableProviders >= 6)
                {
                    rsConfig = new ReedSolomonConfig
                    {
                        DataShards = availableProviders - 2,
                        ParityShards = 2,
                        Profile = ReedSolomonProfile.Custom
                    };
                    warnings.Add($"Adjusted Reed-Solomon config to {rsConfig.DataShards}+{rsConfig.ParityShards} to fit {availableProviders} providers");
                }
                else
                {
                    effectiveMode = FaultToleranceMode.Replica3;
                    warnings.Add($"Reed-Solomon requires {rsConfig.TotalShards} providers but only {availableProviders} available. Using Replica3 instead.");
                    rsConfig = null;
                }
            }
        }

        return new FaultToleranceResolution
        {
            IsValid = true,
            EffectiveMode = effectiveMode,
            ReedSolomonConfig = rsConfig,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Gets a human-readable description of a fault tolerance mode.
    /// </summary>
    public static string GetModeDescription(FaultToleranceMode mode)
    {
        return mode switch
        {
            FaultToleranceMode.None => "No redundancy - single copy",
            FaultToleranceMode.Mirror2 => "2-way mirroring (RAID-1)",
            FaultToleranceMode.Replica3 => "3-way replication",
            FaultToleranceMode.ReedSolomon => "Reed-Solomon erasure coding",
            FaultToleranceMode.RAID6 => "RAID-6 dual parity",
            FaultToleranceMode.RAIDZ3 => "RAID-Z3 triple parity",
            FaultToleranceMode.Automatic => "Automatic selection",
            _ => "Unknown mode"
        };
    }

    /// <summary>
    /// Gets the storage overhead for a fault tolerance mode.
    /// </summary>
    public static double GetStorageOverhead(FaultToleranceMode mode, ReedSolomonConfig? rsConfig = null)
    {
        return mode switch
        {
            FaultToleranceMode.None => 0,
            FaultToleranceMode.Mirror2 => 100,         // 2x storage
            FaultToleranceMode.Replica3 => 200,        // 3x storage
            FaultToleranceMode.ReedSolomon => rsConfig?.StorageOverhead ?? 50,
            FaultToleranceMode.RAID6 => 33.3,          // ~1.33x for 4 disks
            FaultToleranceMode.RAIDZ3 => 75,           // ~1.75x for 4 disks
            _ => 0
        };
    }

    /// <summary>
    /// Gets the fault tolerance (number of failures tolerated) for a mode.
    /// </summary>
    public static int GetFaultTolerance(FaultToleranceMode mode, ReedSolomonConfig? rsConfig = null)
    {
        return mode switch
        {
            FaultToleranceMode.None => 0,
            FaultToleranceMode.Mirror2 => 1,
            FaultToleranceMode.Replica3 => 2,
            FaultToleranceMode.ReedSolomon => rsConfig?.ParityShards ?? 3,
            FaultToleranceMode.RAID6 => 2,
            FaultToleranceMode.RAIDZ3 => 3,
            _ => 0
        };
    }
}

/// <summary>
/// Result of fault tolerance mode resolution.
/// </summary>
public sealed class FaultToleranceResolution
{
    /// <summary>
    /// Whether the resolution is valid and can be applied.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// The effective fault tolerance mode to use.
    /// </summary>
    public FaultToleranceMode EffectiveMode { get; init; }

    /// <summary>
    /// Reed-Solomon configuration if applicable.
    /// </summary>
    public ReedSolomonConfig? ReedSolomonConfig { get; init; }

    /// <summary>
    /// Error message if resolution failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Warnings generated during resolution.
    /// </summary>
    public List<string> Warnings { get; init; } = new();
}

#endregion

#region Fault Tolerance Statistics

/// <summary>
/// Statistics about fault tolerance operations.
/// </summary>
public sealed class FaultToleranceStats
{
    /// <summary>
    /// Total objects protected with fault tolerance.
    /// </summary>
    public long ProtectedObjects { get; set; }

    /// <summary>
    /// Total bytes protected.
    /// </summary>
    public long ProtectedBytes { get; set; }

    /// <summary>
    /// Number of successful repairs.
    /// </summary>
    public long SuccessfulRepairs { get; set; }

    /// <summary>
    /// Number of failed repairs.
    /// </summary>
    public long FailedRepairs { get; set; }

    /// <summary>
    /// Number of corruption detections.
    /// </summary>
    public long CorruptionDetections { get; set; }

    /// <summary>
    /// Storage overhead in bytes.
    /// </summary>
    public long StorageOverheadBytes { get; set; }

    /// <summary>
    /// Last scrub timestamp.
    /// </summary>
    public DateTime? LastScrubTime { get; set; }

    /// <summary>
    /// Last scrub duration.
    /// </summary>
    public TimeSpan? LastScrubDuration { get; set; }

    /// <summary>
    /// Objects by fault tolerance mode.
    /// </summary>
    public Dictionary<FaultToleranceMode, long> ObjectsByMode { get; } = new();
}

#endregion
