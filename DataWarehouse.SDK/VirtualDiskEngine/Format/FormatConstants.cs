using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Constants for the DWVD v2.0 on-disk format. Every structure in the format
/// references these values for sizes, offsets, and limits.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE Format v2.0 constants (VDE2-01)")]
public static class FormatConstants
{
    // ── Version ────────────────────────────────────────────────────────

    /// <summary>Format major version (breaking changes).</summary>
    public const byte FormatMajorVersion = 2;

    /// <summary>Format minor version (backward-compatible additions).</summary>
    public const byte FormatMinorVersion = 0;

    /// <summary>Specification revision within this major.minor.</summary>
    public const ushort SpecRevision = 1;

    // ── Signature ──────────────────────────────────────────────────────

    /// <summary>Size in bytes of the 16-byte magic signature that starts every DWVD file.</summary>
    public const int MagicSignatureSize = 16;

    /// <summary>ASCII bytes for "DWVD" (0x44, 0x57, 0x56, 0x44).</summary>
    public static ReadOnlySpan<byte> DwvdAsciiBytes => new byte[] { 0x44, 0x57, 0x56, 0x44 };

    /// <summary>ASCII bytes for "dw://" namespace anchor (0x64, 0x77, 0x3A, 0x2F, 0x2F).</summary>
    public static ReadOnlySpan<byte> NamespaceAnchorBytes => new byte[] { 0x64, 0x77, 0x3A, 0x2F, 0x2F };

    // ── Blocks ─────────────────────────────────────────────────────────

    /// <summary>Default block size in bytes (4 KiB).</summary>
    public const int DefaultBlockSize = 4096;

    /// <summary>Minimum supported block size (512 bytes).</summary>
    public const int MinBlockSize = 512;

    /// <summary>Maximum supported block size (64 KiB).</summary>
    public const int MaxBlockSize = 65536;

    /// <summary>Size of the universal block trailer appended to every block.</summary>
    public const int UniversalBlockTrailerSize = 16;

    // ── Superblock ─────────────────────────────────────────────────────

    /// <summary>Number of blocks in a superblock group.</summary>
    public const int SuperblockGroupBlocks = 4;

    /// <summary>Block number where the superblock mirror starts.</summary>
    public const long SuperblockMirrorStartBlock = 4;

    // ── Region Directory ───────────────────────────────────────────────

    /// <summary>Block number where the region directory starts.</summary>
    public const long RegionDirectoryStartBlock = 8;

    /// <summary>Number of blocks the region directory occupies.</summary>
    public const int RegionDirectoryBlocks = 2;

    /// <summary>Maximum number of region slots in the directory.</summary>
    public const int MaxRegionSlots = 127;

    /// <summary>Size of a single region slot in bytes.</summary>
    public const int RegionSlotSize = 32;

    // ── Encryption ─────────────────────────────────────────────────────

    /// <summary>Maximum number of key slots in the encryption header.</summary>
    public const int MaxKeySlots = 63;

    // ── Inode ──────────────────────────────────────────────────────────

    /// <summary>Core inode size in bytes (fields before module extension area).</summary>
    public const int InodeCoreSize = 304;

    /// <summary>Inode total size must be a multiple of this value.</summary>
    public const int InodeAlignmentMultiple = 64;

    /// <summary>Maximum number of direct extents stored inline in an inode.</summary>
    public const int MaxExtentsPerInode = 8;

    /// <summary>Size of a single extent entry in bytes.</summary>
    public const int ExtentSize = 24;

    /// <summary>Size of the inline tag area in an inode.</summary>
    public const int InlineTagAreaSize = 128;

    // ── Modules ────────────────────────────────────────────────────────

    /// <summary>Maximum number of module slots in the module registry.</summary>
    public const int MaxModules = 32;

    /// <summary>Number of modules defined in the current specification.</summary>
    public const int DefinedModules = 19;
}
