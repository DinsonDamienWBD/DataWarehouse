using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Constants for the VDE 2.0B Federation Router subsystem.
/// Defines magic bytes, version info, capacity limits, hash seeds, and shard topology bounds.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Federation Router (VFED-01)")]
public static class FederationConstants
{
    /// <summary>
    /// Maximum number of hops in a cold-path resolution.
    /// Root -> Domain -> Index -> Data = 4 hops max, 5 for safety margin.
    /// </summary>
    public const int MaxColdPathHops = 5;

    /// <summary>
    /// Maximum entries in the warm route cache (1M entries, ~100MB footprint).
    /// </summary>
    public const int MaxWarmCacheEntries = 1_048_576;

    /// <summary>
    /// Time-to-live in seconds for cached route entries (5 minutes).
    /// </summary>
    public const int WarmCacheTtlSeconds = 300;

    /// <summary>
    /// Default number of domain slots in the consistent hashing ring (64K slots).
    /// </summary>
    public const int DefaultDomainSlotCount = 65536;

    /// <summary>
    /// XxHash64 seed for path segment hashing. Derived from "92FED20B" federation identifier.
    /// </summary>
    public const ulong HashSeed = 0x92FED_2_0BUL;

    /// <summary>
    /// Magic bytes identifying federation metadata: "FED2" in ASCII little-endian (0x46454432).
    /// </summary>
    public const uint FederationMagic = 0x46454432u;

    /// <summary>
    /// Current federation wire format version.
    /// </summary>
    public const ushort FormatVersion = 1;

    /// <summary>
    /// Maximum shard hierarchy depth. Root=0, Domain=1, Index=2, Data=3.
    /// </summary>
    public const int MaxShardLevels = 4;
}
