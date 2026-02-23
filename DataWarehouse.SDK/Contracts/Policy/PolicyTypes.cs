using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.Policy
{
    /// <summary>
    /// Represents a single feature policy that controls how a specific feature (e.g., compression,
    /// encryption, replication) behaves at a given granularity level within the VDE hierarchy.
    /// Feature policies are the atomic units of the Policy Engine.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Policy Engine foundation (SDKF-02)")]
    public sealed record FeaturePolicy
    {
        /// <summary>
        /// Unique identifier for the feature this policy governs (e.g., "compression", "encryption", "replication").
        /// </summary>
        public required string FeatureId { get; init; }

        /// <summary>
        /// The granularity level at which this policy applies within the VDE hierarchy.
        /// </summary>
        public required PolicyLevel Level { get; init; }

        /// <summary>
        /// Feature intensity on a 0-100 scale. Interpretation is feature-specific
        /// (e.g., compression ratio target, replication factor, encryption key strength tier).
        /// </summary>
        public int IntensityLevel { get; init; }

        /// <summary>
        /// Determines how this policy cascades through the VDE hierarchy from parent to child levels.
        /// </summary>
        public CascadeStrategy Cascade { get; init; }

        /// <summary>
        /// The level of AI autonomy permitted for this feature's operations.
        /// </summary>
        public AiAutonomyLevel AiAutonomy { get; init; }

        /// <summary>
        /// Arbitrary key-value parameters for feature-specific configuration beyond the standard properties.
        /// Null when no custom parameters are needed.
        /// </summary>
        public Dictionary<string, string>? CustomParameters { get; init; }
    }

    /// <summary>
    /// Provides contextual information needed to resolve policies at a specific point
    /// in the VDE hierarchy. Includes path, user identity, hardware capabilities, and security posture.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Policy Engine foundation (SDKF-09)")]
    public sealed record PolicyResolutionContext
    {
        /// <summary>
        /// VDE path being resolved (e.g., "/container1/object2/chunk3").
        /// Determines which hierarchy levels participate in cascade resolution.
        /// </summary>
        public required string Path { get; init; }

        /// <summary>
        /// Identity of the current user requesting the operation, or null for system-initiated actions.
        /// </summary>
        public string? UserId { get; init; }

        /// <summary>
        /// Tenant context for multi-tenant deployments, or null for single-tenant environments.
        /// </summary>
        public string? TenantId { get; init; }

        /// <summary>
        /// Hardware capabilities of the current execution environment, used for hardware-aware policy decisions.
        /// </summary>
        public HardwareContext? Hardware { get; init; }

        /// <summary>
        /// Security posture of the current environment, used for compliance-aware policy decisions.
        /// </summary>
        public SecurityContext? Security { get; init; }

        /// <summary>
        /// Timestamp at which policy resolution occurs, enabling time-based policy rules.
        /// </summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Describes hardware capabilities of the execution environment. Used by the Policy Engine
    /// to make hardware-aware decisions (e.g., enabling hardware acceleration, adjusting compression levels).
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Policy Engine foundation (SDKF-09)")]
    public sealed record HardwareContext
    {
        /// <summary>
        /// Available physical memory in bytes on the current node.
        /// </summary>
        public long AvailableMemoryBytes { get; init; }

        /// <summary>
        /// Number of logical CPU cores available for computation.
        /// </summary>
        public int CpuCoreCount { get; init; }

        /// <summary>
        /// Primary storage medium type (e.g., "SSD", "HDD", "NVMe"), or null if unknown.
        /// </summary>
        public string? StorageType { get; init; }

        /// <summary>
        /// Whether hardware acceleration (e.g., AES-NI, AVX, GPU compute) is available.
        /// </summary>
        public bool HasHardwareAcceleration { get; init; }

        /// <summary>
        /// Current thermal throttle percentage (0.0 = no throttle, 100.0 = fully throttled), or null if unavailable.
        /// </summary>
        public double? ThermalThrottlePercent { get; init; }
    }

    /// <summary>
    /// Describes the security posture of the execution environment. Used by the Policy Engine
    /// to enforce compliance-aware policies (e.g., FIPS mode, air-gapped operations).
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Policy Engine foundation (SDKF-09)")]
    public sealed record SecurityContext
    {
        /// <summary>
        /// Security clearance level of the current user or environment (e.g., "Unclassified", "Secret", "TopSecret"),
        /// or null if clearance-based access is not applicable.
        /// </summary>
        public string? ClearanceLevel { get; init; }

        /// <summary>
        /// Whether FIPS 140-2/3 compliant cryptographic operations are required.
        /// </summary>
        public bool IsFipsMode { get; init; }

        /// <summary>
        /// Whether the environment is air-gapped (no external network connectivity).
        /// </summary>
        public bool IsAirGapped { get; init; }

        /// <summary>
        /// Active compliance frameworks that constrain policy decisions (e.g., "HIPAA", "SOC2", "PCI-DSS"),
        /// or null if no frameworks are active.
        /// </summary>
        public string[]? ActiveComplianceFrameworks { get; init; }
    }

    /// <summary>
    /// Represents the chain of authority levels that determines who can override policies.
    /// Ordered from highest authority (lowest priority number) to lowest authority.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Policy Engine foundation (SDKF-07)")]
    public sealed record AuthorityChain
    {
        /// <summary>
        /// Ordered array of authority levels from highest to lowest authority.
        /// </summary>
        public required AuthorityLevel[] Levels { get; init; }

        /// <summary>
        /// Creates the default authority chain: Quorum (highest) -> AiEmergency -> Admin -> SystemDefaults (lowest).
        /// </summary>
        /// <returns>A new <see cref="AuthorityChain"/> with the standard four-level hierarchy.</returns>
        public static AuthorityChain Default() => new()
        {
            Levels = new[]
            {
                new AuthorityLevel { Name = "Quorum", Priority = 0, RequiresQuorum = true },
                new AuthorityLevel { Name = "AiEmergency", Priority = 1, RequiresQuorum = false },
                new AuthorityLevel { Name = "Admin", Priority = 2, RequiresQuorum = false },
                new AuthorityLevel { Name = "SystemDefaults", Priority = 3, RequiresQuorum = false }
            }
        };
    }

    /// <summary>
    /// Represents a single level within an authority chain.
    /// Lower priority numbers indicate higher authority.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Policy Engine foundation (SDKF-07)")]
    public sealed record AuthorityLevel
    {
        /// <summary>
        /// Human-readable name of this authority level (e.g., "Quorum", "Admin", "SystemDefaults").
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Numeric priority where lower values indicate higher authority. Priority 0 is the highest authority.
        /// </summary>
        public required int Priority { get; init; }

        /// <summary>
        /// Whether actions at this authority level require quorum approval before taking effect.
        /// </summary>
        public bool RequiresQuorum { get; init; }
    }

    /// <summary>
    /// Defines quorum requirements for high-impact actions that need multi-party authorization.
    /// Specifies how many approvals are needed within what time window.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Policy Engine foundation (SDKF-08)")]
    public sealed record QuorumPolicy
    {
        /// <summary>
        /// Number of approvals required to authorize a protected action (e.g., 3 for a 3-of-5 quorum).
        /// </summary>
        public required int RequiredApprovals { get; init; }

        /// <summary>
        /// Total number of quorum members eligible to vote (e.g., 5 for a 3-of-5 quorum).
        /// </summary>
        public required int TotalMembers { get; init; }

        /// <summary>
        /// Maximum time window within which all required approvals must be gathered.
        /// If the window expires before reaching quorum, the action is denied.
        /// </summary>
        public TimeSpan ApprovalWindow { get; init; } = TimeSpan.FromHours(24);

        /// <summary>
        /// Array of high-impact actions that require quorum approval before execution.
        /// </summary>
        public required QuorumAction[] ProtectedActions { get; init; }
    }

    /// <summary>
    /// Represents a complete operational profile that bundles feature policies into a named configuration.
    /// Profiles define the overall operational posture of a VDE (speed vs. security trade-offs).
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Policy Engine foundation (SDKF-06)")]
    public sealed record OperationalProfile
    {
        /// <summary>
        /// The named preset this profile is based on, or <see cref="OperationalProfilePreset.Custom"/> for user-defined profiles.
        /// </summary>
        public OperationalProfilePreset Preset { get; init; }

        /// <summary>
        /// Human-readable name for this operational profile.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Per-feature policy overrides keyed by feature identifier.
        /// Features not present in this dictionary use system defaults.
        /// </summary>
        public Dictionary<string, FeaturePolicy> FeaturePolicies { get; init; } = new();

        /// <summary>
        /// Creates a Speed profile optimized for maximum throughput with relaxed security and high AI autonomy.
        /// </summary>
        public static OperationalProfile Speed() => new()
        {
            Preset = OperationalProfilePreset.Speed,
            Name = "Speed",
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["compression"] = new FeaturePolicy { FeatureId = "compression", Level = PolicyLevel.Object, IntensityLevel = 30, Cascade = CascadeStrategy.Inherit, AiAutonomy = AiAutonomyLevel.AutoSilent },
                ["encryption"] = new FeaturePolicy { FeatureId = "encryption", Level = PolicyLevel.VDE, IntensityLevel = 30, Cascade = CascadeStrategy.Inherit, AiAutonomy = AiAutonomyLevel.AutoSilent },
                ["replication"] = new FeaturePolicy { FeatureId = "replication", Level = PolicyLevel.Container, IntensityLevel = 20, Cascade = CascadeStrategy.Inherit, AiAutonomy = AiAutonomyLevel.AutoNotify }
            }
        };

        /// <summary>
        /// Creates a Balanced profile with moderate trade-offs between performance and security.
        /// </summary>
        public static OperationalProfile Balanced() => new()
        {
            Preset = OperationalProfilePreset.Balanced,
            Name = "Balanced",
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["compression"] = new FeaturePolicy { FeatureId = "compression", Level = PolicyLevel.Object, IntensityLevel = 50, Cascade = CascadeStrategy.Inherit, AiAutonomy = AiAutonomyLevel.AutoNotify },
                ["encryption"] = new FeaturePolicy { FeatureId = "encryption", Level = PolicyLevel.VDE, IntensityLevel = 50, Cascade = CascadeStrategy.MostRestrictive, AiAutonomy = AiAutonomyLevel.SuggestExplain },
                ["replication"] = new FeaturePolicy { FeatureId = "replication", Level = PolicyLevel.Container, IntensityLevel = 50, Cascade = CascadeStrategy.Inherit, AiAutonomy = AiAutonomyLevel.AutoNotify }
            }
        };

        /// <summary>
        /// Creates a Standard profile suitable for most production workloads with reasonable defaults.
        /// </summary>
        public static OperationalProfile Standard() => new()
        {
            Preset = OperationalProfilePreset.Standard,
            Name = "Standard",
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["compression"] = new FeaturePolicy { FeatureId = "compression", Level = PolicyLevel.Object, IntensityLevel = 60, Cascade = CascadeStrategy.Inherit, AiAutonomy = AiAutonomyLevel.SuggestExplain },
                ["encryption"] = new FeaturePolicy { FeatureId = "encryption", Level = PolicyLevel.VDE, IntensityLevel = 70, Cascade = CascadeStrategy.MostRestrictive, AiAutonomy = AiAutonomyLevel.Suggest },
                ["replication"] = new FeaturePolicy { FeatureId = "replication", Level = PolicyLevel.Container, IntensityLevel = 60, Cascade = CascadeStrategy.MostRestrictive, AiAutonomy = AiAutonomyLevel.SuggestExplain }
            }
        };

        /// <summary>
        /// Creates a Strict profile with elevated security and reduced AI autonomy for regulated environments.
        /// </summary>
        public static OperationalProfile Strict() => new()
        {
            Preset = OperationalProfilePreset.Strict,
            Name = "Strict",
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["compression"] = new FeaturePolicy { FeatureId = "compression", Level = PolicyLevel.Object, IntensityLevel = 70, Cascade = CascadeStrategy.MostRestrictive, AiAutonomy = AiAutonomyLevel.Suggest },
                ["encryption"] = new FeaturePolicy { FeatureId = "encryption", Level = PolicyLevel.VDE, IntensityLevel = 90, Cascade = CascadeStrategy.Enforce, AiAutonomy = AiAutonomyLevel.ManualOnly },
                ["replication"] = new FeaturePolicy { FeatureId = "replication", Level = PolicyLevel.Container, IntensityLevel = 80, Cascade = CascadeStrategy.MostRestrictive, AiAutonomy = AiAutonomyLevel.Suggest }
            }
        };

        /// <summary>
        /// Creates a Paranoid profile with maximum security, manual-only AI, and quorum requirements
        /// for highest-security environments (air-gapped, classified, critical infrastructure).
        /// </summary>
        public static OperationalProfile Paranoid() => new()
        {
            Preset = OperationalProfilePreset.Paranoid,
            Name = "Paranoid",
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["compression"] = new FeaturePolicy { FeatureId = "compression", Level = PolicyLevel.Block, IntensityLevel = 90, Cascade = CascadeStrategy.Enforce, AiAutonomy = AiAutonomyLevel.ManualOnly },
                ["encryption"] = new FeaturePolicy { FeatureId = "encryption", Level = PolicyLevel.Block, IntensityLevel = 100, Cascade = CascadeStrategy.Enforce, AiAutonomy = AiAutonomyLevel.ManualOnly },
                ["replication"] = new FeaturePolicy { FeatureId = "replication", Level = PolicyLevel.Block, IntensityLevel = 100, Cascade = CascadeStrategy.Enforce, AiAutonomy = AiAutonomyLevel.ManualOnly }
            }
        };
    }
}
