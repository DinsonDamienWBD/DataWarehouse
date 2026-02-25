using System;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Contracts.Scaling
{
    /// <summary>
    /// Defines the numeric limits that govern a subsystem's resource consumption.
    /// All values are runtime-reconfigurable via <see cref="IScalableSubsystem.ReconfigureLimitsAsync"/>.
    /// </summary>
    /// <param name="MaxCacheEntries">Maximum number of entries allowed in bounded caches. Default: 10,000.</param>
    /// <param name="MaxMemoryBytes">Maximum memory budget in bytes. Default: 100 MB.</param>
    /// <param name="MaxConcurrentOperations">Maximum concurrent async operations. Default: 64.</param>
    /// <param name="MaxQueueDepth">Maximum backpressure queue depth before shedding. Default: 1,000.</param>
    [SdkCompatibility("6.0.0", Notes = "Phase 88: Scaling limits for bounded subsystems")]
    public record ScalingLimits(
        int MaxCacheEntries = 10_000,
        long MaxMemoryBytes = 100 * 1024 * 1024,
        int MaxConcurrentOperations = 64,
        int MaxQueueDepth = 1_000);

    /// <summary>
    /// Hierarchical scaling policy that applies different <see cref="ScalingLimits"/>
    /// at instance, tenant, and user levels. Supports auto-sizing from available RAM.
    /// </summary>
    /// <param name="InstanceLimits">Limits applied at the global instance level.</param>
    /// <param name="TenantLimits">Optional per-tenant limits. When <c>null</c>, instance limits apply.</param>
    /// <param name="UserLimits">Optional per-user limits. When <c>null</c>, tenant or instance limits apply.</param>
    /// <param name="AutoSizeFromRam">When <c>true</c>, <see cref="InstanceLimits"/> are auto-tuned from available RAM at startup.</param>
    /// <param name="RamPercentage">Fraction of total available RAM to use when <paramref name="AutoSizeFromRam"/> is <c>true</c>. Default: 10%.</param>
    [SdkCompatibility("6.0.0", Notes = "Phase 88: Hierarchical scaling policy")]
    public record SubsystemScalingPolicy(
        ScalingLimits InstanceLimits,
        ScalingLimits? TenantLimits = null,
        ScalingLimits? UserLimits = null,
        bool AutoSizeFromRam = true,
        double RamPercentage = 0.1);

    /// <summary>
    /// Identifies which level in the scaling hierarchy a limit or metric applies to.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 88: Scaling hierarchy level enumeration")]
    public enum ScalingHierarchyLevel
    {
        /// <summary>Global instance-level limits shared by all tenants and users.</summary>
        Instance,

        /// <summary>Per-tenant limits that subdivide instance capacity.</summary>
        Tenant,

        /// <summary>Per-user limits within a tenant boundary.</summary>
        User
    }

    /// <summary>
    /// Event arguments raised when a subsystem's scaling limits are reconfigured at runtime.
    /// </summary>
    /// <param name="SubsystemName">The name of the subsystem whose limits changed.</param>
    /// <param name="OldLimits">The previous scaling limits before reconfiguration.</param>
    /// <param name="NewLimits">The new scaling limits after reconfiguration.</param>
    /// <param name="Timestamp">UTC timestamp when the reconfiguration occurred.</param>
    [SdkCompatibility("6.0.0", Notes = "Phase 88: Scaling reconfiguration event")]
    public record ScalingReconfiguredEventArgs(
        string SubsystemName,
        ScalingLimits OldLimits,
        ScalingLimits NewLimits,
        DateTime Timestamp);
}
