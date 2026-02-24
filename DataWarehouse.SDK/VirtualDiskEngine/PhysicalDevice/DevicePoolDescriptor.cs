using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;

/// <summary>
/// Storage tier classification for device pools, mapping physical media characteristics
/// to logical performance tiers for automatic hot/warm/cold data placement.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device pool descriptors (BMDV-05/BMDV-06)")]
public enum StorageTier
{
    /// <summary>Highest-performance tier (NVMe, RAMDisk).</summary>
    Hot,
    /// <summary>Mid-performance tier (SATA/SAS SSD).</summary>
    Warm,
    /// <summary>Bulk storage tier (HDD).</summary>
    Cold,
    /// <summary>Near-archive tier (tape, slow media).</summary>
    Frozen,
    /// <summary>Long-term archive tier.</summary>
    Archive
}

/// <summary>
/// Hierarchical locality tag for placement-aware scheduling of device pools.
/// Enables rack-aware, datacenter-aware, and region-aware data placement.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device pool descriptors (BMDV-05/BMDV-06)")]
public sealed record LocalityTag(
    string Rack = "default",
    string Datacenter = "default",
    string Region = "default",
    string Zone = "default");

/// <summary>
/// Describes a single physical device that is a member of a device pool,
/// including its identity, capacity, and reservation for pool metadata.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device pool descriptors (BMDV-05/BMDV-06)")]
public sealed record PoolMemberDescriptor(
    string DeviceId,
    string DevicePath,
    MediaType MediaType,
    long CapacityBytes,
    long ReservedBytes,
    bool IsActive);

/// <summary>
/// Describes a named device pool consisting of one or more physical block devices,
/// with tier classification, locality tags, and extensible properties.
/// Pool metadata is persisted on reserved sectors of member devices to survive OS reinstall.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device pool descriptors (BMDV-05/BMDV-06)")]
public sealed record DevicePoolDescriptor(
    Guid PoolId,
    string PoolName,
    StorageTier Tier,
    LocalityTag Locality,
    IReadOnlyList<PoolMemberDescriptor> Members,
    long TotalCapacityBytes,
    long UsableCapacityBytes,
    DateTime CreatedUtc,
    DateTime LastModifiedUtc,
    int MetadataVersion = 1,
    IReadOnlyDictionary<string, string>? Properties = null)
{
    /// <summary>
    /// Extensible key-value properties for user configuration.
    /// Defaults to an empty dictionary if not provided.
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        Properties ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();
}

/// <summary>
/// Classifies physical media types into logical storage tiers for automatic
/// pool tier assignment based on device characteristics.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device pool descriptors (BMDV-05/BMDV-06)")]
public static class StorageTierClassifier
{
    /// <summary>
    /// Maps a <see cref="MediaType"/> to the corresponding <see cref="StorageTier"/>.
    /// NVMe and RAMDisk map to Hot, SSD to Warm, HDD to Cold, Tape to Frozen,
    /// and unknown types default to Cold.
    /// </summary>
    /// <param name="mediaType">The media type to classify.</param>
    /// <returns>The storage tier for the given media type.</returns>
    public static StorageTier ClassifyFromMediaType(MediaType mediaType)
    {
        return mediaType switch
        {
            MediaType.NVMe => StorageTier.Hot,
            MediaType.SSD => StorageTier.Warm,
            MediaType.HDD => StorageTier.Cold,
            MediaType.Tape => StorageTier.Frozen,
            MediaType.RAMDisk => StorageTier.Hot,
            _ => StorageTier.Cold
        };
    }
}
