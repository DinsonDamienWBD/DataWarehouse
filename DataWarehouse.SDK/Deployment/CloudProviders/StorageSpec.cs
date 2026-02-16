using DataWarehouse.SDK.Contracts;
using System.Collections.Immutable;

namespace DataWarehouse.SDK.Deployment.CloudProviders;

/// <summary>
/// Cloud storage specification for provisioning.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Cloud storage specification (ENV-04)")]
public sealed record StorageSpec
{
    /// <summary>Gets the storage type ("block" for volumes, "object" for buckets).</summary>
    public string StorageType { get; init; } = "block";

    /// <summary>Gets the storage size in GB.</summary>
    public long SizeGb { get; init; }

    /// <summary>Gets the performance tier ("standard", "premium", "ultra").</summary>
    public string? PerformanceTier { get; init; }

    /// <summary>Gets whether encryption at rest is enabled.</summary>
    public bool Encrypted { get; init; } = true;

    /// <summary>Gets the availability zone for block storage.</summary>
    public string? AvailabilityZone { get; init; }

    /// <summary>Gets resource tags for identification and cost tracking.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = ImmutableDictionary<string, string>.Empty;
}
