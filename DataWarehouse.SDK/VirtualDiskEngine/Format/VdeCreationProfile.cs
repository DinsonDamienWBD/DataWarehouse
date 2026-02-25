using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Identifies the type of VDE creation profile, ranging from minimal (bare container)
/// to maximum security (all modules at highest level) and custom (user-specified).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 creation profile types (VDE2-06)")]
public enum VdeProfileType : byte
{
    /// <summary>Bare container with no modules. Suitable for testing or simple storage.</summary>
    Minimal = 0,

    /// <summary>Standard profile with security, integrity, snapshots, and compression.</summary>
    Standard = 1,

    /// <summary>Enterprise profile with compliance, intelligence, tags, audit logging, and more.</summary>
    Enterprise = 2,

    /// <summary>Maximum security: all 19 modules at maximum configuration level.</summary>
    MaxSecurity = 3,

    /// <summary>Edge/IoT profile: streaming and integrity with 512-byte blocks.</summary>
    EdgeIoT = 4,

    /// <summary>Analytics profile: intelligence, compression, snapshots, and query with 64K blocks.</summary>
    Analytics = 5,

    /// <summary>User-defined module combination, block size, and configuration.</summary>
    Custom = 6,
}

/// <summary>
/// Defines a complete configuration for creating a DWVD v2.0 file: which modules are active,
/// per-module configuration levels, block size, total blocks, thin provisioning, and tamper
/// response level. Seven factory presets are provided matching the VDE v2.0 specification.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 creation profile (VDE2-06)")]
public readonly record struct VdeCreationProfile
{
    /// <summary>The profile type identifying this configuration.</summary>
    public VdeProfileType ProfileType { get; init; }

    /// <summary>Human-readable profile name.</summary>
    public string Name { get; init; }

    /// <summary>Description of the profile's purpose and characteristics.</summary>
    public string Description { get; init; }

    /// <summary>32-bit module manifest value (each bit enables one module).</summary>
    public uint ModuleManifest { get; init; }

    /// <summary>Per-module nibble configuration levels (0-15 per module).</summary>
    public Dictionary<ModuleId, byte> ModuleConfigLevels { get; init; }

    /// <summary>Block size in bytes (must be power of 2, 512-65536).</summary>
    public int BlockSize { get; init; }

    /// <summary>Total number of logical blocks in the VDE.</summary>
    public long TotalBlocks { get; init; }

    /// <summary>Whether to use thin provisioning (sparse file) for the VDE.</summary>
    public bool ThinProvisioned { get; init; }

    /// <summary>
    /// Tamper response level: 0x00=WARN, 0x01=QUARANTINE_BLOCK, 0x02=QUARANTINE_REGION,
    /// 0x03=QUARANTINE_VDE, 0x04=AUTO_QUARANTINE.
    /// </summary>
    public byte TamperResponseLevel { get; init; }

    // ── Factory Methods ─────────────────────────────────────────────────

    /// <summary>
    /// Creates a Minimal profile: bare VDE container with no modules.
    /// Manifest=0x00000000, BlockSize=4096, TamperResponse=WARN (0x00).
    /// </summary>
    public static VdeCreationProfile Minimal(long totalBlocks) => new()
    {
        ProfileType = VdeProfileType.Minimal,
        Name = "Minimal",
        Description = "Bare container with no modules. Suitable for testing or simple storage.",
        ModuleManifest = 0x0000_0000,
        ModuleConfigLevels = new Dictionary<ModuleId, byte>(),
        BlockSize = FormatConstants.DefaultBlockSize,
        TotalBlocks = totalBlocks,
        ThinProvisioned = true,
        TamperResponseLevel = 0x00,
    };

    /// <summary>
    /// Creates a Standard profile: SEC + INTG + SNAP + CMPR.
    /// Manifest bits: 0 (SEC), 10 (CMPR), 11 (INTG), 12 (SNAP) = 0x00001C01.
    /// </summary>
    public static VdeCreationProfile Standard(long totalBlocks) => new()
    {
        ProfileType = VdeProfileType.Standard,
        Name = "Standard",
        Description = "Standard profile with security, integrity, snapshots, and compression.",
        ModuleManifest = 0x0000_1C01, // bits 0,10,11,12
        ModuleConfigLevels = new Dictionary<ModuleId, byte>
        {
            [Format.ModuleId.Security] = 0x2,
            [Format.ModuleId.Compression] = 0x2,
            [Format.ModuleId.Integrity] = 0x2,
            [Format.ModuleId.Snapshot] = 0x1,
        },
        BlockSize = FormatConstants.DefaultBlockSize,
        TotalBlocks = totalBlocks,
        ThinProvisioned = true,
        TamperResponseLevel = 0x00,
    };

    /// <summary>
    /// Creates an Enterprise profile: SEC + CMPL + INTL + TAGS + CMPR + INTG + ALOG.
    /// Manifest bits: 0,1,2,3,10,11,18 = 0x00040C0F.
    /// </summary>
    public static VdeCreationProfile Enterprise(long totalBlocks) => new()
    {
        ProfileType = VdeProfileType.Enterprise,
        Name = "Enterprise",
        Description = "Enterprise profile with compliance, intelligence, tags, audit logging, and more.",
        ModuleManifest = 0x0004_0C0F, // bits 0,1,2,3,10,11,18
        ModuleConfigLevels = new Dictionary<ModuleId, byte>
        {
            [Format.ModuleId.Security] = 0x3,
            [Format.ModuleId.Compliance] = 0x3,
            [Format.ModuleId.Intelligence] = 0x2,
            [Format.ModuleId.Tags] = 0x3,
            [Format.ModuleId.Compression] = 0x2,
            [Format.ModuleId.Integrity] = 0x2,
            [Format.ModuleId.AuditLog] = 0x1,
        },
        BlockSize = FormatConstants.DefaultBlockSize,
        TotalBlocks = totalBlocks,
        ThinProvisioned = true,
        TamperResponseLevel = 0x00,
    };

    /// <summary>
    /// Creates a MaxSecurity profile: all 19 modules at maximum level (0xF).
    /// Manifest=0x0007FFFF, TamperResponse=AUTO_QUARANTINE (0x04).
    /// </summary>
    public static VdeCreationProfile MaxSecurity(long totalBlocks) => new()
    {
        ProfileType = VdeProfileType.MaxSecurity,
        Name = "MaxSecurity",
        Description = "Maximum security: all 19 modules at maximum configuration level.",
        ModuleManifest = 0x0007_FFFF,
        ModuleConfigLevels = BuildAllModulesMaxConfig(),
        BlockSize = FormatConstants.DefaultBlockSize,
        TotalBlocks = totalBlocks,
        ThinProvisioned = true,
        TamperResponseLevel = 0x04,
    };

    /// <summary>
    /// Creates an Edge/IoT profile: STRM + INTG with 512-byte blocks.
    /// Manifest bits: 6 (STRM), 11 (INTG) = 0x00000840.
    /// </summary>
    public static VdeCreationProfile EdgeIoT(long totalBlocks) => new()
    {
        ProfileType = VdeProfileType.EdgeIoT,
        Name = "EdgeIoT",
        Description = "Edge/IoT profile: streaming and integrity with 512-byte blocks for embedded devices.",
        ModuleManifest = 0x0000_0840, // bits 6,11
        ModuleConfigLevels = new Dictionary<ModuleId, byte>
        {
            [Format.ModuleId.Streaming] = 0x1,
            [Format.ModuleId.Integrity] = 0x1,
        },
        BlockSize = FormatConstants.MinBlockSize, // 512
        TotalBlocks = totalBlocks,
        ThinProvisioned = true,
        TamperResponseLevel = 0x00,
    };

    /// <summary>
    /// Creates an Analytics profile: INTL + CMPR + SNAP + QURY with 64KB blocks.
    /// Manifest bits: 2 (INTL), 10 (CMPR), 12 (SNAP), 13 (QURY) = 0x00003404.
    /// </summary>
    public static VdeCreationProfile Analytics(long totalBlocks) => new()
    {
        ProfileType = VdeProfileType.Analytics,
        Name = "Analytics",
        Description = "Analytics profile: intelligence, compression, snapshots, and query with 64KB blocks.",
        ModuleManifest = 0x0000_3404, // bits 2,10,12,13
        ModuleConfigLevels = new Dictionary<ModuleId, byte>
        {
            [Format.ModuleId.Intelligence] = 0x2,
            [Format.ModuleId.Compression] = 0x3,
            [Format.ModuleId.Snapshot] = 0x1,
            [Format.ModuleId.Query] = 0x2,
        },
        BlockSize = FormatConstants.MaxBlockSize, // 65536
        TotalBlocks = totalBlocks,
        ThinProvisioned = true,
        TamperResponseLevel = 0x00,
    };

    /// <summary>
    /// Creates a Custom profile with user-specified module combination and block size.
    /// </summary>
    /// <param name="totalBlocks">Total logical blocks.</param>
    /// <param name="manifest">32-bit module manifest.</param>
    /// <param name="configLevels">Per-module configuration levels.</param>
    /// <param name="blockSize">Block size in bytes (power of 2, 512-65536).</param>
    public static VdeCreationProfile Custom(
        long totalBlocks,
        uint manifest,
        Dictionary<ModuleId, byte> configLevels,
        int blockSize = FormatConstants.DefaultBlockSize) => new()
    {
        ProfileType = VdeProfileType.Custom,
        Name = "Custom",
        Description = "User-defined module combination, block size, and configuration.",
        ModuleManifest = manifest,
        ModuleConfigLevels = configLevels ?? new Dictionary<ModuleId, byte>(),
        BlockSize = blockSize,
        TotalBlocks = totalBlocks,
        ThinProvisioned = true,
        TamperResponseLevel = 0x00,
    };

    // ── Derived Properties ──────────────────────────────────────────────

    /// <summary>
    /// Calculates the inode layout for this profile's module manifest.
    /// </summary>
    public InodeSizeResult GetInodeLayout() => InodeSizeCalculator.Calculate(ModuleManifest);

    /// <summary>
    /// Returns the module manifest as a <see cref="ModuleManifestField"/>.
    /// </summary>
    public ModuleManifestField GetManifest() => new(ModuleManifest);

    /// <summary>
    /// Builds the <see cref="ModuleConfigField"/> from this profile's per-module configuration levels.
    /// </summary>
    public ModuleConfigField GetConfig() => ModuleConfigField.FromLevels(
        ModuleConfigLevels ?? new Dictionary<ModuleId, byte>());

    // ── Helpers ─────────────────────────────────────────────────────────

    private static Dictionary<ModuleId, byte> BuildAllModulesMaxConfig()
    {
        var config = new Dictionary<ModuleId, byte>(FormatConstants.DefinedModules);
        for (int i = 0; i < FormatConstants.DefinedModules; i++)
        {
            config[(ModuleId)i] = 0xF;
        }
        return config;
    }
}
