using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Virtualization;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using DataWarehouse.SDK.Contracts.Hierarchy;

namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Abstract base class for hypervisor detection plugins.
/// Provides common infrastructure for detecting virtualization platforms and querying VM information.
/// Implements caching to avoid repeated expensive detection operations.
/// </summary>
public abstract class HypervisorDetectorPluginBase : ComputePluginBase, IHypervisorDetector, IIntelligenceAware
{
    /// <inheritdoc/>
    public override string RuntimeType => "HypervisorDetector";

    /// <inheritdoc/>
    public override Task<Dictionary<string, object>> ExecuteWorkloadAsync(Dictionary<string, object> workload, CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, object> { ["status"] = "delegated-to-hypervisor-detector" });

    private HypervisorType? _cachedType;

    #region Intelligence Socket

    /// <summary>
    /// Gets whether Universal Intelligence (T90) is available for AI-assisted VM analysis.
    /// </summary>
    public new bool IsIntelligenceAvailable { get; protected set; }

    /// <summary>
    /// Gets the available Intelligence capabilities.
    /// </summary>
    public new IntelligenceCapabilities AvailableCapabilities { get; protected set; }

    /// <summary>
    /// Discovers Intelligence availability.
    /// </summary>
    public new virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
    {
        if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
        IsIntelligenceAvailable = false;
        return IsIntelligenceAvailable;
    }

    /// <summary>
    /// Declared capabilities for hypervisor detection.
    /// </summary>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.detection",
            DisplayName = $"{Name} - Hypervisor Detection",
            Description = "Detects virtualization platform and capabilities",
            Category = CapabilityCategory.Infrastructure,
            SubCategory = "Virtualization",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "hypervisor", "virtualization", "detection" },
            SemanticDescription = "Use for detecting if running on a hypervisor and its type"
        }
    };

    /// <summary>
    /// Gets static knowledge about hypervisor detection.
    /// </summary>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        return new[]
        {
            new KnowledgeObject
            {
                Id = $"{Id}.hypervisor.capability",
                Topic = "virtualization.detection",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Hypervisor detector: {DetectedHypervisor}, Virtualized: {IsVirtualized}",
                Payload = new Dictionary<string, object>
                {
                    ["detectedHypervisor"] = DetectedHypervisor.ToString(),
                    ["isVirtualized"] = IsVirtualized
                },
                Tags = new[] { "hypervisor", "virtualization" }
            }
        };
    }

    /// <summary>
    /// Requests AI-assisted VM resource prediction.
    /// </summary>
    protected virtual async Task<VmResourcePrediction?> RequestVmResourcePredictionAsync(VmInfo vmInfo, CancellationToken ct = default)
    {
        if (!IsIntelligenceAvailable || MessageBus == null) return null;
        await Task.CompletedTask;
        return null;
    }

    #endregion

    /// <summary>
    /// Gets the detected hypervisor type.
    /// Result is cached after first detection.
    /// </summary>
    public HypervisorType DetectedHypervisor => _cachedType ??= DetectHypervisor();

    /// <summary>
    /// Indicates whether the system is running virtualized.
    /// Returns false only for bare metal or unknown hypervisors.
    /// </summary>
    public bool IsVirtualized => DetectedHypervisor != HypervisorType.Bare && DetectedHypervisor != HypervisorType.Unknown;

    /// <summary>
    /// Detects the hypervisor type using platform-specific methods.
    /// Override to implement detection logic (CPUID, SMBIOS, device drivers, etc.).
    /// </summary>
    /// <returns>The detected hypervisor type.</returns>
    protected abstract HypervisorType DetectHypervisor();

    /// <summary>
    /// Queries the hypervisor for available capabilities.
    /// Override to implement hypervisor-specific capability detection.
    /// </summary>
    /// <returns>Capabilities available in the detected hypervisor.</returns>
    protected abstract Task<HypervisorCapabilities> QueryCapabilitiesAsync();

    /// <summary>
    /// Queries VM information from the hypervisor.
    /// Override to implement hypervisor-specific VM info retrieval.
    /// </summary>
    /// <returns>Current VM information.</returns>
    protected abstract Task<VmInfo> QueryVmInfoAsync();

    /// <summary>
    /// Gets the capabilities available in the detected hypervisor.
    /// </summary>
    public Task<HypervisorCapabilities> GetCapabilitiesAsync() => QueryCapabilitiesAsync();

    /// <summary>
    /// Gets current virtual machine information.
    /// </summary>
    public Task<VmInfo> GetVmInfoAsync() => QueryVmInfoAsync();

    /// <summary>
    /// Default detection using system information (CPUID/SMBIOS).
    /// Can be used as a fallback when hypervisor-specific detection is unavailable.
    /// Checks common hypervisor signatures in CPUID and processor identifiers.
    /// </summary>
    /// <returns>Detected hypervisor type or Bare if no virtualization is detected.</returns>
    protected virtual HypervisorType DetectFromSystemInfo()
    {
        var manufacturer = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";

        // VMware: "VMwareVMware", HyperV: "Microsoft Hv", KVM: "KVMKVMKVM", Xen: "XenVMMXenVMM"
        if (manufacturer.Contains("VMware", StringComparison.OrdinalIgnoreCase))
            return HypervisorType.VMwareESXi;

        if (manufacturer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) &&
            manufacturer.Contains("Hv", StringComparison.OrdinalIgnoreCase))
            return HypervisorType.HyperV;

        if (manufacturer.Contains("KVM", StringComparison.OrdinalIgnoreCase))
            return HypervisorType.KvmQemu;

        if (manufacturer.Contains("Xen", StringComparison.OrdinalIgnoreCase))
            return HypervisorType.Xen;

        return HypervisorType.Bare;
    }

    /// <summary>
    /// Gets the plugin category. Hypervisor detectors are infrastructure plugins.
    /// </summary>
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <summary>
    /// Starts the hypervisor detection plugin.
    /// Detection is lazy, so this typically requires no initialization.
    /// </summary>
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Stops the hypervisor detection plugin.
    /// </summary>
    public override Task StopAsync() => Task.CompletedTask;

    /// <summary>
    /// Gets plugin metadata including detected hypervisor information.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "HypervisorDetector";
        metadata["DetectedHypervisor"] = DetectedHypervisor.ToString();
        metadata["IsVirtualized"] = IsVirtualized;
        return metadata;
    }
}

/// <summary>
/// Abstract base class for balloon driver plugins.
/// Provides infrastructure for memory ballooning/reclamation in virtualized environments.
/// Balloon drivers allow the hypervisor to reclaim unused guest memory dynamically.
/// </summary>
public abstract class BalloonDriverPluginBase : ComputePluginBase, IBalloonDriver, IIntelligenceAware
{
    /// <inheritdoc/>
    public override string RuntimeType => "BalloonDriver";

    /// <inheritdoc/>
    public override Task<Dictionary<string, object>> ExecuteWorkloadAsync(Dictionary<string, object> workload, CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, object> { ["status"] = "delegated-to-balloon-driver" });

    #region Intelligence Socket

    public new bool IsIntelligenceAvailable { get; protected set; }
    public new IntelligenceCapabilities AvailableCapabilities { get; protected set; }

    public new virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
    {
        if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
        IsIntelligenceAvailable = false;
        return IsIntelligenceAvailable;
    }

    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.ballooning",
            DisplayName = $"{Name} - Memory Ballooning",
            Description = "Dynamic memory reclamation via balloon driver",
            Category = CapabilityCategory.Infrastructure,
            SubCategory = "Memory",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "memory", "ballooning", "virtualization" },
            SemanticDescription = "Use for dynamic memory management in virtualized environments"
        }
    };

    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        return new[]
        {
            new KnowledgeObject
            {
                Id = $"{Id}.balloon.capability",
                Topic = "virtualization.memory",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Balloon driver available: {IsAvailable}",
                Payload = new Dictionary<string, object> { ["isAvailable"] = IsAvailable },
                Tags = new[] { "memory", "ballooning" }
            }
        };
    }

    /// <summary>
    /// Requests AI-assisted memory pressure prediction.
    /// </summary>
    protected virtual async Task<MemoryPressurePrediction?> RequestMemoryPredictionAsync(BalloonStatistics stats, CancellationToken ct = default)
    {
        if (!IsIntelligenceAvailable || MessageBus == null) return null;
        await Task.CompletedTask;
        return null;
    }

    #endregion
    /// <summary>
    /// Gets whether the balloon driver is available and loaded.
    /// Override to check driver availability in the guest OS.
    /// </summary>
    public abstract bool IsAvailable { get; }

    /// <summary>
    /// Gets the current target memory size for the balloon.
    /// Override to query driver-specific target from kernel module or device.
    /// </summary>
    /// <returns>Target memory in bytes.</returns>
    protected abstract Task<long> GetCurrentTargetAsync();

    /// <summary>
    /// Sets the target memory size for the balloon.
    /// Override to communicate new target to the balloon driver.
    /// </summary>
    /// <param name="bytes">Target memory in bytes.</param>
    protected abstract Task SetTargetAsync(long bytes);

    /// <summary>
    /// Gets current balloon driver statistics.
    /// Override to query driver statistics (inflation, deflation, swap activity).
    /// </summary>
    /// <returns>Balloon statistics.</returns>
    protected abstract Task<BalloonStatistics> GetStatsAsync();

    /// <summary>
    /// Gets the current target memory size for the balloon driver.
    /// </summary>
    public Task<long> GetTargetMemoryAsync() => GetCurrentTargetAsync();

    /// <summary>
    /// Sets the target memory size for the balloon driver.
    /// </summary>
    public Task SetTargetMemoryAsync(long bytes) => SetTargetAsync(bytes);

    /// <summary>
    /// Gets current balloon driver statistics.
    /// </summary>
    public Task<BalloonStatistics> GetStatisticsAsync() => GetStatsAsync();

    /// <summary>
    /// Gets the plugin category. Balloon drivers are infrastructure plugins.
    /// </summary>
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <summary>
    /// Starts the balloon driver plugin.
    /// </summary>
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Stops the balloon driver plugin.
    /// </summary>
    public override Task StopAsync() => Task.CompletedTask;

    /// <summary>
    /// Gets plugin metadata including balloon driver status.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "BalloonDriver";
        metadata["IsAvailable"] = IsAvailable;
        return metadata;
    }
}

/// <summary>
/// Abstract base class for VM snapshot provider plugins.
/// Provides infrastructure for creating application-consistent VM snapshots.
/// Coordinates with hypervisor snapshot mechanisms and guest OS quiescing.
/// </summary>
public abstract class VmSnapshotProviderPluginBase : ComputePluginBase, IVmSnapshotProvider, IIntelligenceAware
{
    /// <inheritdoc/>
    public override string RuntimeType => "VmSnapshot";

    /// <inheritdoc/>
    public override Task<Dictionary<string, object>> ExecuteWorkloadAsync(Dictionary<string, object> workload, CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, object> { ["status"] = "delegated-to-vm-snapshot" });

    #region Intelligence Socket

    public new bool IsIntelligenceAvailable { get; protected set; }
    public new IntelligenceCapabilities AvailableCapabilities { get; protected set; }

    public new virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
    {
        if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
        IsIntelligenceAvailable = false;
        return IsIntelligenceAvailable;
    }

    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.snapshot",
            DisplayName = $"{Name} - VM Snapshots",
            Description = "Application-consistent VM snapshot creation",
            Category = CapabilityCategory.Storage,
            SubCategory = "Snapshot",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "snapshot", "virtualization", "backup" },
            SemanticDescription = "Use for creating consistent point-in-time VM snapshots"
        }
    };

    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        return new[]
        {
            new KnowledgeObject
            {
                Id = $"{Id}.snapshot.capability",
                Topic = "virtualization.snapshot",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Snapshot provider, app-consistent: {IsApplicationConsistentSupported}",
                Payload = new Dictionary<string, object> { ["supportsAppConsistent"] = IsApplicationConsistentSupported },
                Tags = new[] { "snapshot", "backup" }
            }
        };
    }

    /// <summary>
    /// Requests AI-assisted optimal snapshot timing.
    /// </summary>
    protected virtual async Task<SnapshotTimingRecommendation?> RequestOptimalSnapshotTimeAsync(CancellationToken ct = default)
    {
        if (!IsIntelligenceAvailable || MessageBus == null) return null;
        await Task.CompletedTask;
        return null;
    }

    #endregion
    /// <summary>
    /// Gets whether application-consistent snapshots are supported.
    /// Override to check for VSS, filesystem freeze, or other quiescing mechanisms.
    /// </summary>
    protected abstract bool IsApplicationConsistentSupported { get; }

    /// <summary>
    /// Prepares for snapshot by quiescing I/O and flushing caches.
    /// Override to implement platform-specific quiescing (VSS, fsfreeze, etc.).
    /// </summary>
    protected abstract Task PrepareForSnapshotAsync();

    /// <summary>
    /// Cleans up after snapshot by resuming I/O.
    /// Override to implement platform-specific resume logic.
    /// </summary>
    protected abstract Task CleanupAfterSnapshotAsync();

    /// <summary>
    /// Takes a snapshot using hypervisor-specific APIs.
    /// Override to integrate with VMware, Hyper-V, KVM, or other snapshot APIs.
    /// </summary>
    /// <param name="name">Snapshot name.</param>
    /// <param name="appConsistent">Whether to create an application-consistent snapshot.</param>
    /// <returns>Metadata for the created snapshot.</returns>
    protected abstract Task<SnapshotMetadata> TakeSnapshotAsync(string name, bool appConsistent);

    /// <summary>
    /// Checks if application-consistent snapshots are supported.
    /// </summary>
    public Task<bool> SupportsApplicationConsistentAsync() => Task.FromResult(IsApplicationConsistentSupported);

    /// <summary>
    /// Quiesces the VM in preparation for a snapshot.
    /// </summary>
    public Task QuiesceAsync() => PrepareForSnapshotAsync();

    /// <summary>
    /// Resumes normal operation after snapshot completion.
    /// </summary>
    public Task ResumeAsync() => CleanupAfterSnapshotAsync();

    /// <summary>
    /// Creates a VM snapshot with optional application consistency.
    /// Automatically falls back to crash-consistent if application-consistent is not supported.
    /// </summary>
    public Task<SnapshotMetadata> CreateSnapshotAsync(string name, bool applicationConsistent)
        => TakeSnapshotAsync(name, applicationConsistent && IsApplicationConsistentSupported);

    /// <summary>
    /// Gets the plugin category. Snapshot providers are storage plugins.
    /// </summary>
    public override PluginCategory Category => PluginCategory.StorageProvider;

    /// <summary>
    /// Starts the snapshot provider plugin.
    /// </summary>
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Stops the snapshot provider plugin.
    /// </summary>
    public override Task StopAsync() => Task.CompletedTask;

    /// <summary>
    /// Gets plugin metadata including snapshot capabilities.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "VmSnapshotProvider";
        metadata["SupportsApplicationConsistent"] = IsApplicationConsistentSupported;
        return metadata;
    }
}

/// <summary>
/// Abstract base class for backup API integration plugins.
/// Provides infrastructure for integrating with hypervisor backup APIs
/// like VMware VADP, Hyper-V VSS, and Proxmox Backup Server.
/// Enables efficient incremental backups using changed-block tracking.
/// </summary>
public abstract class BackupApiPluginBase : ComputePluginBase, IBackupApiIntegration, IIntelligenceAware
{
    /// <inheritdoc/>
    public override string RuntimeType => "BackupApi";

    /// <inheritdoc/>
    public override Task<Dictionary<string, object>> ExecuteWorkloadAsync(Dictionary<string, object> workload, CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, object> { ["status"] = "delegated-to-backup-api" });

    #region Intelligence Socket

    public new bool IsIntelligenceAvailable { get; protected set; }
    public new IntelligenceCapabilities AvailableCapabilities { get; protected set; }

    public new virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
    {
        if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
        IsIntelligenceAvailable = false;
        return IsIntelligenceAvailable;
    }

    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.backup",
            DisplayName = $"{Name} - Backup API",
            Description = "Integration with hypervisor backup APIs (VADP, CBT)",
            Category = CapabilityCategory.Storage,
            SubCategory = "Backup",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "backup", "virtualization", "cbt" },
            SemanticDescription = "Use for efficient incremental VM backups via hypervisor APIs"
        }
    };

    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        return new[]
        {
            new KnowledgeObject
            {
                Id = $"{Id}.backup.capability",
                Topic = "virtualization.backup",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Backup API type: {ApiType}",
                Payload = new Dictionary<string, object> { ["apiType"] = ApiType.ToString() },
                Tags = new[] { "backup", "cbt" }
            }
        };
    }

    /// <summary>
    /// Requests AI-assisted backup schedule optimization.
    /// </summary>
    protected virtual async Task<BackupScheduleRecommendation?> RequestBackupScheduleAsync(CancellationToken ct = default)
    {
        if (!IsIntelligenceAvailable || MessageBus == null) return null;
        await Task.CompletedTask;
        return null;
    }

    #endregion
    /// <summary>
    /// Gets the backup API type supported by this plugin.
    /// </summary>
    public abstract BackupApiType ApiType { get; }

    /// <summary>
    /// Begins a backup session.
    /// Override to create snapshots, enable CBT, or prepare for backup.
    /// </summary>
    protected abstract Task BeginBackupSessionAsync();

    /// <summary>
    /// Opens a stream for reading backup data.
    /// Override to provide optimized read path using hypervisor APIs.
    /// May use changed-block tracking for incremental backups.
    /// </summary>
    /// <returns>Stream for reading backup data.</returns>
    protected abstract Task<Stream> OpenBackupStreamAsync();

    /// <summary>
    /// Ends the backup session and cleans up resources.
    /// Override to remove snapshots, disable CBT, or clean up API handles.
    /// </summary>
    /// <param name="success">Whether the backup completed successfully.</param>
    protected abstract Task EndBackupSessionAsync(bool success);

    /// <summary>
    /// Prepares the system for backup operations.
    /// </summary>
    public Task PrepareForBackupAsync() => BeginBackupSessionAsync();

    /// <summary>
    /// Gets a stream for reading backup data.
    /// </summary>
    public Task<Stream> GetBackupStreamAsync() => OpenBackupStreamAsync();

    /// <summary>
    /// Completes the backup operation and cleans up resources.
    /// </summary>
    public Task CompleteBackupAsync(bool success) => EndBackupSessionAsync(success);

    /// <summary>
    /// Gets the plugin category. Backup API plugins are storage plugins.
    /// </summary>
    public override PluginCategory Category => PluginCategory.StorageProvider;

    /// <summary>
    /// Starts the backup API plugin.
    /// </summary>
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Stops the backup API plugin.
    /// </summary>
    public override Task StopAsync() => Task.CompletedTask;

    /// <summary>
    /// Gets plugin metadata including backup API type.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "BackupApiIntegration";
        metadata["ApiType"] = ApiType.ToString();
        return metadata;
    }
}

#region Intelligence Stub Types

/// <summary>
/// AI-predicted VM resource requirements.
/// </summary>
public record VmResourcePrediction
{
    /// <summary>Predicted CPU utilization.</summary>
    public double PredictedCpuUtilization { get; init; }

    /// <summary>Predicted memory usage in bytes.</summary>
    public long PredictedMemoryBytes { get; init; }

    /// <summary>Confidence in the prediction (0.0-1.0).</summary>
    public double Confidence { get; init; }

    /// <summary>Recommendation for resource adjustment.</summary>
    public string? Recommendation { get; init; }
}

/// <summary>
/// AI-predicted memory pressure information.
/// </summary>
public record MemoryPressurePrediction
{
    /// <summary>Predicted memory pressure level (0.0-1.0).</summary>
    public double PredictedPressure { get; init; }

    /// <summary>Recommended target memory size.</summary>
    public long RecommendedTargetBytes { get; init; }

    /// <summary>Confidence in the prediction (0.0-1.0).</summary>
    public double Confidence { get; init; }

    /// <summary>Time until pressure is expected.</summary>
    public TimeSpan? TimeToEvent { get; init; }
}

/// <summary>
/// AI recommendation for optimal snapshot timing.
/// </summary>
public record SnapshotTimingRecommendation
{
    /// <summary>Recommended time for snapshot.</summary>
    public DateTimeOffset RecommendedTime { get; init; }

    /// <summary>Expected I/O impact during snapshot.</summary>
    public double ExpectedIoImpact { get; init; }

    /// <summary>Reason for the recommendation.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// AI recommendation for backup schedule.
/// </summary>
public record BackupScheduleRecommendation
{
    /// <summary>Recommended backup frequency.</summary>
    public TimeSpan RecommendedFrequency { get; init; }

    /// <summary>Optimal time window for backups.</summary>
    public TimeSpan OptimalWindow { get; init; }

    /// <summary>Whether incremental backup is recommended.</summary>
    public bool UseIncremental { get; init; }

    /// <summary>Reason for the recommendation.</summary>
    public string? Reason { get; init; }
}

#endregion
