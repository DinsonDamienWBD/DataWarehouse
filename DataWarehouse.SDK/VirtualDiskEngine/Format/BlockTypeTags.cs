using System.Collections.Frozen;
using System.Text;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// All block type tags for the DWVD v2.0 format. Each tag is a 4-character
/// ASCII string encoded as a big-endian uint32 (first char in MSB).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 block type tags (VDE2-01)")]
public static class BlockTypeTags
{
    // ── Core Layout ────────────────────────────────────────────────────

    /// <summary>Superblock ("SUPB").</summary>
    public const uint SUPB = 0x53555042;

    /// <summary>Region map / directory ("RMAP").</summary>
    public const uint RMAP = 0x524D4150;

    /// <summary>Policy vault ("POLV").</summary>
    public const uint POLV = 0x504F4C56;

    /// <summary>Encryption header ("ENCR").</summary>
    public const uint ENCR = 0x454E4352;

    /// <summary>Block allocation bitmap ("BMAP").</summary>
    public const uint BMAP = 0x424D4150;

    // ── Data Structures ────────────────────────────────────────────────

    /// <summary>Inode ("INOD").</summary>
    public const uint INOD = 0x494E4F44;

    /// <summary>Tag index ("TAGI").</summary>
    public const uint TAGI = 0x54414749;

    /// <summary>Metadata write-ahead log ("MWAL").</summary>
    public const uint MWAL = 0x4D57414C;

    /// <summary>Metrics tracker ("MTRK").</summary>
    public const uint MTRK = 0x4D54524B;

    /// <summary>B-tree node ("BTRE").</summary>
    public const uint BTRE = 0x42545245;

    // ── Features ───────────────────────────────────────────────────────

    /// <summary>Snapshot ("SNAP").</summary>
    public const uint SNAP = 0x534E4150;

    /// <summary>Replication metadata ("REPL").</summary>
    public const uint REPL = 0x5245504C;

    /// <summary>RAID metadata ("RAID").</summary>
    public const uint RAID = 0x52414944;

    /// <summary>Compression metadata ("COMP").</summary>
    public const uint COMP = 0x434F4D50;

    /// <summary>Intelligence cache ("INTE").</summary>
    public const uint INTE = 0x494E5445;

    /// <summary>Streaming region ("STRE").</summary>
    public const uint STRE = 0x53545245;

    /// <summary>Cross-reference table ("XREF").</summary>
    public const uint XREF = 0x58524546;

    /// <summary>WORM (write-once-read-many) region ("WORM").</summary>
    public const uint WORM = 0x574F524D;

    /// <summary>Compute cache / code region ("CODE").</summary>
    public const uint CODE = 0x434F4445;

    /// <summary>Data write-ahead log ("DWAL").</summary>
    public const uint DWAL = 0x4457414C;

    /// <summary>Data block ("DATA").</summary>
    public const uint DATA = 0x44415441;

    /// <summary>Free / unallocated block ("FREE").</summary>
    public const uint FREE = 0x46524545;

    // ── Module Registry Extensions ─────────────────────────────────────

    /// <summary>Compliance vault ("CMVT").</summary>
    public const uint CMVT = 0x434D5654;

    /// <summary>Audit log ("ALOG").</summary>
    public const uint ALOG = 0x414C4F47;

    /// <summary>Consensus log ("CLOG").</summary>
    public const uint CLOG = 0x434C4F47;

    /// <summary>Dictionary / metadata store ("DICT").</summary>
    public const uint DICT = 0x44494354;

    /// <summary>Anonymous / anonymization region ("ANON").</summary>
    public const uint ANON = 0x414E4F4E;

    /// <summary>Module log ("MLOG").</summary>
    public const uint MLOG = 0x4D4C4F47;

    // ── Lookup Set ─────────────────────────────────────────────────────

    private static readonly FrozenSet<uint> KnownTags = new HashSet<uint>
    {
        SUPB, RMAP, POLV, ENCR, BMAP, INOD, TAGI, MWAL, MTRK, BTRE,
        SNAP, REPL, RAID, COMP, INTE, STRE, XREF, WORM, CODE, DWAL,
        DATA, FREE, CMVT, ALOG, CLOG, DICT, ANON, MLOG
    }.ToFrozenSet();

    /// <summary>
    /// Converts a block type tag uint to its 4-character ASCII string representation.
    /// </summary>
    /// <param name="tag">The tag value (big-endian encoded).</param>
    /// <returns>A 4-character string.</returns>
    public static string TagToString(uint tag)
    {
        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = (byte)(tag >> 24);
        bytes[1] = (byte)(tag >> 16);
        bytes[2] = (byte)(tag >> 8);
        bytes[3] = (byte)tag;
        return Encoding.ASCII.GetString(bytes);
    }

    /// <summary>
    /// Converts a 4-character ASCII string to a block type tag uint.
    /// </summary>
    /// <param name="s">A 4-character ASCII string.</param>
    /// <returns>The tag value (big-endian encoded).</returns>
    /// <exception cref="ArgumentException">String is not exactly 4 ASCII characters.</exception>
    public static uint StringToTag(string s)
    {
        if (s is null) throw new ArgumentNullException(nameof(s));
        if (s.Length != 4) throw new ArgumentException("Tag string must be exactly 4 characters.", nameof(s));

        return ((uint)s[0] << 24)
             | ((uint)s[1] << 16)
             | ((uint)s[2] << 8)
             | s[3];
    }

    /// <summary>
    /// Returns true if the tag is a known DWVD v2.0 block type.
    /// </summary>
    public static bool IsKnownTag(uint tag) => KnownTags.Contains(tag);
}
