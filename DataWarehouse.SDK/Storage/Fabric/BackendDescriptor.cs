using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;

using StorageTier = DataWarehouse.SDK.Contracts.Storage.StorageTier;
using HealthStatus = DataWarehouse.SDK.Contracts.Storage.HealthStatus;

namespace DataWarehouse.SDK.Storage.Fabric;

/// <summary>
/// Immutable descriptor capturing metadata about a storage backend registered in the fabric.
/// Includes identity, capabilities, tier classification, geographic region, tags, and capacity limits.
/// </summary>
/// <remarks>
/// <para>
/// BackendDescriptor is used by <see cref="IBackendRegistry"/> to catalog available backends
/// and by the fabric to make placement and routing decisions. Tags provide flexible categorization
/// (e.g., "cloud", "aws", "encrypted", "compliance") while <see cref="Tier"/> and
/// <see cref="Capabilities"/> provide structured capability matching.
/// </para>
/// <para>
/// The <see cref="Priority"/> field controls preference when multiple backends match placement
/// criteria -- lower values are preferred. This enables active-passive, primary-replica,
/// and tiered fallback topologies.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 63: Universal Storage Fabric")]
public record BackendDescriptor
{
    /// <summary>
    /// Gets the unique identifier for this backend (e.g., "s3-us-east-1", "local-nvme", "azure-westeu").
    /// Must be unique across the fabric.
    /// </summary>
    public required string BackendId { get; init; }

    /// <summary>
    /// Gets the human-readable display name for this backend.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the strategy identifier that maps to the <see cref="DataWarehouse.SDK.Contracts.Storage.IStorageStrategy.StrategyId"/>
    /// handling this backend's operations.
    /// </summary>
    public required string StrategyId { get; init; }

    /// <summary>
    /// Gets the storage tier this backend operates on (Hot, Warm, Cold, Archive, RamDisk, Tape).
    /// </summary>
    public required StorageTier Tier { get; init; }

    /// <summary>
    /// Gets the capabilities supported by this backend (versioning, encryption, multipart, etc.).
    /// </summary>
    public required StorageCapabilities Capabilities { get; init; }

    /// <summary>
    /// Gets the set of tags for flexible categorization and discovery.
    /// Tags are case-insensitive identifiers (e.g., "cloud", "aws", "encrypted", "compliance").
    /// </summary>
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the geographic region for this backend, if applicable (e.g., "us-east-1", "eu-west-2").
    /// Null for backends without geographic affinity.
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// Gets the connection endpoint for this backend, if applicable.
    /// Null for backends that do not require explicit endpoint configuration.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// Gets the priority for this backend in placement decisions. Lower values are preferred.
    /// Default is 100. Use values below 100 for primary backends and above 100 for fallback backends.
    /// </summary>
    public int Priority { get; init; } = 100;

    /// <summary>
    /// Gets the maximum storage capacity in bytes for this backend, if known.
    /// Null indicates no practical limit or unknown capacity.
    /// </summary>
    public long? MaxCapacityBytes { get; init; }

    /// <summary>
    /// Gets whether this backend is read-only (no store/delete/move operations allowed).
    /// </summary>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// Gets backend-specific configuration properties.
    /// These are opaque key-value pairs interpreted by the backend's <see cref="IStorageStrategy"/>.
    /// </summary>
    public IDictionary<string, string>? Properties { get; init; }
}

/// <summary>
/// Hints for the fabric to select the optimal backend when storing data.
/// All properties are optional -- the fabric uses available hints to narrow backend selection
/// and falls back to priority-based selection when no hints match.
/// </summary>
/// <remarks>
/// <para>
/// Placement hints express caller intent without coupling to specific backends. For example,
/// requesting <see cref="PreferredTier"/> = <see cref="StorageTier.Hot"/> and
/// <see cref="RequireEncryption"/> = true will select the highest-priority hot-tier backend
/// that supports encryption.
/// </para>
/// <para>
/// <see cref="PreferredBackendId"/> provides an explicit override for cases where the caller
/// knows the exact target backend. When set, other hints are ignored.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 63: Universal Storage Fabric")]
public record StoragePlacementHints
{
    /// <summary>
    /// Gets the preferred storage tier for placement (Hot, Warm, Cold, Archive).
    /// Null means no tier preference.
    /// </summary>
    public StorageTier? PreferredTier { get; init; }

    /// <summary>
    /// Gets the set of tags that the target backend must have.
    /// All tags must be present for a backend to match. Null means no tag requirement.
    /// </summary>
    public IReadOnlySet<string>? RequiredTags { get; init; }

    /// <summary>
    /// Gets the preferred geographic region for data placement (e.g., "us-east-1").
    /// Null means no region preference.
    /// </summary>
    public string? PreferredRegion { get; init; }

    /// <summary>
    /// Gets an explicit backend ID override. When set, the fabric routes directly to this backend,
    /// ignoring other placement hints.
    /// </summary>
    public string? PreferredBackendId { get; init; }

    /// <summary>
    /// Gets whether the target backend must support server-side encryption.
    /// </summary>
    public bool RequireEncryption { get; init; }

    /// <summary>
    /// Gets whether the target backend must support object versioning.
    /// </summary>
    public bool RequireVersioning { get; init; }

    /// <summary>
    /// Gets the expected size of the object being stored, in bytes.
    /// Used for capacity-aware placement to avoid backends near their capacity limit.
    /// Null means size is unknown.
    /// </summary>
    public long? ExpectedSizeBytes { get; init; }

    /// <summary>
    /// Gets the maximum acceptable latency for the target backend.
    /// Used to filter out backends with high latency (e.g., excluding archive tier for real-time access).
    /// Null means no latency requirement.
    /// </summary>
    public TimeSpan? MaxAcceptableLatency { get; init; }
}

/// <summary>
/// Aggregated health report across all backends registered in the fabric.
/// Provides per-backend health details and an overall status summary.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="OverallStatus"/> is computed from per-backend health:
/// if any backend is unhealthy or degraded, the overall status is <see cref="HealthStatus.Degraded"/>.
/// Only when all backends are healthy is the overall status <see cref="HealthStatus.Healthy"/>.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 63: Universal Storage Fabric")]
public record FabricHealthReport
{
    /// <summary>
    /// Gets the total number of registered backends in the fabric.
    /// </summary>
    public required int TotalBackends { get; init; }

    /// <summary>
    /// Gets the number of backends reporting healthy status.
    /// </summary>
    public required int HealthyBackends { get; init; }

    /// <summary>
    /// Gets the number of backends reporting degraded status.
    /// </summary>
    public required int DegradedBackends { get; init; }

    /// <summary>
    /// Gets the number of backends reporting unhealthy or unreachable status.
    /// </summary>
    public required int UnhealthyBackends { get; init; }

    /// <summary>
    /// Gets per-backend health information keyed by backend ID.
    /// </summary>
    public required IReadOnlyDictionary<string, StorageHealthInfo> BackendHealth { get; init; }

    /// <summary>
    /// Gets the timestamp when this health report was generated.
    /// </summary>
    public required DateTime CheckedAt { get; init; }

    /// <summary>
    /// Gets the overall fabric health status computed from per-backend health.
    /// Degraded if any backend is unhealthy or degraded; Healthy only when all backends are healthy.
    /// </summary>
    public HealthStatus OverallStatus => UnhealthyBackends > 0
        ? HealthStatus.Degraded
        : DegradedBackends > 0
            ? HealthStatus.Degraded
            : HealthStatus.Healthy;
}
