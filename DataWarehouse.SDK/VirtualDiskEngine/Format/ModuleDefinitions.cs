using System.Collections.Frozen;
using System.Numerics;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Identifies a composable VDE module by its bit position in the 32-bit module manifest.
/// Bits 0-18 are defined in the current specification; bits 19-31 are reserved for future use.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 module identifiers (VDE2-04)")]
public enum ModuleId : byte
{
    /// <summary>Security module (policy vault, encryption header). Bit 0.</summary>
    Security = 0,

    /// <summary>Compliance module (compliance vault, audit log). Bit 1.</summary>
    Compliance = 1,

    /// <summary>Intelligence module (intelligence cache). Bit 2.</summary>
    Intelligence = 2,

    /// <summary>Tags module (tag index region). Bit 3.</summary>
    Tags = 3,

    /// <summary>Replication module (replication state). Bit 4.</summary>
    Replication = 4,

    /// <summary>RAID module (RAID metadata). Bit 5.</summary>
    Raid = 5,

    /// <summary>Streaming module (streaming append, data WAL). Bit 6.</summary>
    Streaming = 6,

    /// <summary>Compute module (compute code cache). Bit 7.</summary>
    Compute = 7,

    /// <summary>Fabric module (cross-VDE reference table). Bit 8.</summary>
    Fabric = 8,

    /// <summary>Consensus module (consensus log region). Bit 9.</summary>
    Consensus = 9,

    /// <summary>Compression module (dictionary region). Bit 10.</summary>
    Compression = 10,

    /// <summary>Integrity module (integrity tree). Bit 11.</summary>
    Integrity = 11,

    /// <summary>Snapshot module (snapshot table). Bit 12.</summary>
    Snapshot = 12,

    /// <summary>Query module (B-tree index forest). Bit 13.</summary>
    Query = 13,

    /// <summary>Privacy module (anonymization table). Bit 14.</summary>
    Privacy = 14,

    /// <summary>Sustainability module (no dedicated region). Bit 15.</summary>
    Sustainability = 15,

    /// <summary>Transit module (no dedicated region). Bit 16.</summary>
    Transit = 16,

    /// <summary>Observability module (metrics log region). Bit 17.</summary>
    Observability = 17,

    /// <summary>Audit log module (audit log region). Bit 18.</summary>
    AuditLog = 18,
}

/// <summary>
/// Describes a single composable VDE module: its identity, abbreviation, the block type
/// tags it introduces, the regions it requires, and the number of inode extension bytes it
/// contributes when active.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 module metadata record (VDE2-04)")]
public readonly record struct VdeModule
{
    /// <summary>Module identifier (maps to bit position in the manifest).</summary>
    public ModuleId Id { get; init; }

    /// <summary>Human-readable module name (e.g., "Security").</summary>
    public string Name { get; init; }

    /// <summary>3-4 character abbreviation for compact display (e.g., "SEC").</summary>
    public string Abbreviation { get; init; }

    /// <summary>Block type tags this module introduces (referencing <see cref="BlockTypeTags"/>).</summary>
    public uint[] BlockTypeTags { get; init; }

    /// <summary>Region names this module requires in the region directory.</summary>
    public string[] RegionNames { get; init; }

    /// <summary>Number of bytes this module contributes to the inode extension area.</summary>
    public int InodeFieldBytes { get; init; }

    /// <summary>True if this module contributes inode extension bytes.</summary>
    public bool HasInodeFields => InodeFieldBytes > 0;

    /// <summary>True if this module requires one or more dedicated regions.</summary>
    public bool HasRegion => RegionNames.Length > 0;
}

/// <summary>
/// Static registry of all defined VDE modules. Provides lookup by <see cref="ModuleId"/>,
/// manifest-based queries for active modules, inode byte calculations, and required
/// block type tags / regions for any module combination.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 module registry (VDE2-04)")]
public static class ModuleRegistry
{
    private static readonly FrozenDictionary<ModuleId, VdeModule> Modules = BuildModules();
    private static readonly VdeModule[] AllModulesArray = BuildOrderedArray(Modules);

    private static FrozenDictionary<ModuleId, VdeModule> BuildModules() => new Dictionary<ModuleId, VdeModule>(FormatConstants.DefinedModules)
    {
        [ModuleId.Security] = new VdeModule
        {
            Id = ModuleId.Security,
            Name = "Security",
            Abbreviation = "SEC",
            BlockTypeTags = new[] { Format.BlockTypeTags.POLV, Format.BlockTypeTags.ENCR },
            RegionNames = new[] { "PolicyVault", "EncryptionHeader" },
            InodeFieldBytes = 24,
        },
        [ModuleId.Compliance] = new VdeModule
        {
            Id = ModuleId.Compliance,
            Name = "Compliance",
            Abbreviation = "CMPL",
            // Cat 9 (finding 827): use CMAL (Compliance Audit Log) instead of ALOG to avoid
            // on-disk ambiguity with the AuditLog module's ALOG blocks during crash recovery.
            BlockTypeTags = new[] { Format.BlockTypeTags.CMVT, Format.BlockTypeTags.CMAL },
            RegionNames = new[] { "ComplianceVault", "ComplianceAuditLog" },
            InodeFieldBytes = 12,
        },
        [ModuleId.Intelligence] = new VdeModule
        {
            Id = ModuleId.Intelligence,
            Name = "Intelligence",
            Abbreviation = "INTL",
            BlockTypeTags = new[] { Format.BlockTypeTags.INTE },
            RegionNames = new[] { "IntelligenceCache" },
            InodeFieldBytes = 12,
        },
        [ModuleId.Tags] = new VdeModule
        {
            Id = ModuleId.Tags,
            Name = "Tags",
            Abbreviation = "TAGS",
            BlockTypeTags = new[] { Format.BlockTypeTags.TAGI },
            RegionNames = new[] { "TagIndexRegion" },
            InodeFieldBytes = 136,
        },
        [ModuleId.Replication] = new VdeModule
        {
            Id = ModuleId.Replication,
            Name = "Replication",
            Abbreviation = "REPL",
            BlockTypeTags = new[] { Format.BlockTypeTags.REPL },
            RegionNames = new[] { "ReplicationState" },
            InodeFieldBytes = 8,
        },
        [ModuleId.Raid] = new VdeModule
        {
            Id = ModuleId.Raid,
            Name = "RAID",
            Abbreviation = "RAID",
            BlockTypeTags = new[] { Format.BlockTypeTags.RAID },
            RegionNames = new[] { "RAIDMetadata" },
            InodeFieldBytes = 4,
        },
        [ModuleId.Streaming] = new VdeModule
        {
            Id = ModuleId.Streaming,
            Name = "Streaming",
            Abbreviation = "STRM",
            BlockTypeTags = new[] { Format.BlockTypeTags.STRE, Format.BlockTypeTags.DWAL },
            RegionNames = new[] { "StreamingAppend", "DataWAL" },
            InodeFieldBytes = 8,
        },
        [ModuleId.Compute] = new VdeModule
        {
            Id = ModuleId.Compute,
            Name = "Compute",
            Abbreviation = "COMP",
            BlockTypeTags = new[] { Format.BlockTypeTags.CODE },
            RegionNames = new[] { "ComputeCodeCache" },
            InodeFieldBytes = 0,
        },
        [ModuleId.Fabric] = new VdeModule
        {
            Id = ModuleId.Fabric,
            Name = "Fabric",
            Abbreviation = "FABR",
            BlockTypeTags = new[] { Format.BlockTypeTags.XREF },
            RegionNames = new[] { "CrossVDEReferenceTable" },
            InodeFieldBytes = 0,
        },
        [ModuleId.Consensus] = new VdeModule
        {
            Id = ModuleId.Consensus,
            Name = "Consensus",
            Abbreviation = "CNSS",
            BlockTypeTags = new[] { Format.BlockTypeTags.CLOG },
            RegionNames = new[] { "ConsensusLogRegion" },
            InodeFieldBytes = 0,
        },
        [ModuleId.Compression] = new VdeModule
        {
            Id = ModuleId.Compression,
            Name = "Compression",
            Abbreviation = "CMPR",
            BlockTypeTags = new[] { Format.BlockTypeTags.DICT },
            RegionNames = new[] { "DictionaryRegion" },
            InodeFieldBytes = 4,
        },
        [ModuleId.Integrity] = new VdeModule
        {
            Id = ModuleId.Integrity,
            Name = "Integrity",
            Abbreviation = "INTG",
            BlockTypeTags = new[] { Format.BlockTypeTags.MTRK },
            RegionNames = new[] { "IntegrityTree" },
            InodeFieldBytes = 0,
        },
        [ModuleId.Snapshot] = new VdeModule
        {
            Id = ModuleId.Snapshot,
            Name = "Snapshot",
            Abbreviation = "SNAP",
            BlockTypeTags = new[] { Format.BlockTypeTags.SNAP },
            RegionNames = new[] { "SnapshotTable" },
            InodeFieldBytes = 0,
        },
        [ModuleId.Query] = new VdeModule
        {
            Id = ModuleId.Query,
            Name = "Query",
            Abbreviation = "QURY",
            BlockTypeTags = new[] { Format.BlockTypeTags.BTRE },
            RegionNames = new[] { "BTreeIndexForest" },
            InodeFieldBytes = 4,
        },
        [ModuleId.Privacy] = new VdeModule
        {
            Id = ModuleId.Privacy,
            Name = "Privacy",
            Abbreviation = "PRIV",
            BlockTypeTags = new[] { Format.BlockTypeTags.ANON },
            RegionNames = new[] { "AnonymizationTable" },
            InodeFieldBytes = 2,
        },
        [ModuleId.Sustainability] = new VdeModule
        {
            Id = ModuleId.Sustainability,
            Name = "Sustainability",
            Abbreviation = "SUST",
            BlockTypeTags = Array.Empty<uint>(),
            RegionNames = Array.Empty<string>(),
            InodeFieldBytes = 4,
        },
        [ModuleId.Transit] = new VdeModule
        {
            Id = ModuleId.Transit,
            Name = "Transit",
            Abbreviation = "TRNS",
            BlockTypeTags = Array.Empty<uint>(),
            RegionNames = Array.Empty<string>(),
            InodeFieldBytes = 1,
        },
        [ModuleId.Observability] = new VdeModule
        {
            Id = ModuleId.Observability,
            Name = "Observability",
            Abbreviation = "OBSV",
            BlockTypeTags = new[] { Format.BlockTypeTags.MLOG },
            RegionNames = new[] { "MetricsLogRegion" },
            InodeFieldBytes = 0,
        },
        [ModuleId.AuditLog] = new VdeModule
        {
            Id = ModuleId.AuditLog,
            Name = "AuditLog",
            Abbreviation = "ALOG",
            BlockTypeTags = new[] { Format.BlockTypeTags.ALOG },
            RegionNames = new[] { "AuditLogRegion" },
            InodeFieldBytes = 0,
        },
    }.ToFrozenDictionary();

    private static VdeModule[] BuildOrderedArray(FrozenDictionary<ModuleId, VdeModule> modules)
    {
        var array = new VdeModule[FormatConstants.DefinedModules];
        for (int i = 0; i < FormatConstants.DefinedModules; i++)
        {
            array[i] = modules[(ModuleId)i];
        }
        return array;
    }

    /// <summary>
    /// Returns the module definition for the given module identifier.
    /// </summary>
    /// <param name="id">The module identifier.</param>
    /// <returns>The module definition.</returns>
    /// <exception cref="KeyNotFoundException">The module identifier is not registered.</exception>
    public static VdeModule GetModule(ModuleId id) => Modules[id];

    /// <summary>
    /// All 19 defined modules, ordered by bit position.
    /// </summary>
    public static IReadOnlyList<VdeModule> AllModules => AllModulesArray;

    /// <summary>
    /// Returns the modules that are active in the given 32-bit manifest.
    /// </summary>
    /// <param name="manifest">The 32-bit module manifest value.</param>
    /// <returns>List of active modules.</returns>
    public static IReadOnlyList<VdeModule> GetActiveModules(uint manifest)
    {
        var result = new List<VdeModule>(BitOperations.PopCount(manifest));
        uint bits = manifest;
        while (bits != 0)
        {
            int bit = BitOperations.TrailingZeroCount(bits);
            if (bit < FormatConstants.DefinedModules)
            {
                result.Add(AllModulesArray[bit]);
            }
            bits &= bits - 1; // clear lowest set bit
        }
        return result;
    }

    /// <summary>
    /// Calculates the total inode extension bytes for all active modules in the manifest.
    /// </summary>
    /// <param name="manifest">The 32-bit module manifest value.</param>
    /// <returns>Sum of <see cref="VdeModule.InodeFieldBytes"/> for active modules.</returns>
    public static int CalculateTotalInodeFieldBytes(uint manifest)
    {
        int total = 0;
        uint bits = manifest;
        while (bits != 0)
        {
            int bit = BitOperations.TrailingZeroCount(bits);
            if (bit < FormatConstants.DefinedModules)
            {
                total += AllModulesArray[bit].InodeFieldBytes;
            }
            bits &= bits - 1;
        }
        return total;
    }

    /// <summary>
    /// Returns the union of all block type tags required by the active modules in the manifest.
    /// </summary>
    /// <param name="manifest">The 32-bit module manifest value.</param>
    /// <returns>Distinct set of block type tags.</returns>
    public static IReadOnlyList<uint> GetRequiredBlockTypeTags(uint manifest)
    {
        var tags = new HashSet<uint>();
        uint bits = manifest;
        while (bits != 0)
        {
            int bit = BitOperations.TrailingZeroCount(bits);
            if (bit < FormatConstants.DefinedModules)
            {
                foreach (uint tag in AllModulesArray[bit].BlockTypeTags)
                {
                    tags.Add(tag);
                }
            }
            bits &= bits - 1;
        }
        return tags.ToArray();
    }

    /// <summary>
    /// Returns the union of all region names required by the active modules in the manifest.
    /// </summary>
    /// <param name="manifest">The 32-bit module manifest value.</param>
    /// <returns>Distinct set of region names.</returns>
    public static IReadOnlyList<string> GetRequiredRegions(uint manifest)
    {
        var regions = new HashSet<string>(StringComparer.Ordinal);
        uint bits = manifest;
        while (bits != 0)
        {
            int bit = BitOperations.TrailingZeroCount(bits);
            if (bit < FormatConstants.DefinedModules)
            {
                foreach (string region in AllModulesArray[bit].RegionNames)
                {
                    regions.Add(region);
                }
            }
            bits &= bits - 1;
        }
        return regions.ToArray();
    }

    /// <summary>
    /// Checks whether a specific module is active in the given manifest.
    /// </summary>
    /// <param name="manifest">The 32-bit module manifest value.</param>
    /// <param name="module">The module to check.</param>
    /// <returns>True if the module's bit is set in the manifest.</returns>
    public static bool IsModuleActive(uint manifest, ModuleId module)
        => (manifest & (1u << (int)module)) != 0;
}
