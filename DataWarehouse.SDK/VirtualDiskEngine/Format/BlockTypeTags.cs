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
    public const uint Supb = 0x53555042;

    /// <summary>Region map / directory ("RMAP").</summary>
    public const uint Rmap = 0x524D4150;

    /// <summary>Policy vault ("POLV").</summary>
    public const uint Polv = 0x504F4C56;

    /// <summary>Encryption header ("ENCR").</summary>
    public const uint Encr = 0x454E4352;

    /// <summary>Block allocation bitmap ("BMAP").</summary>
    public const uint Bmap = 0x424D4150;

    // ── Data Structures ────────────────────────────────────────────────

    /// <summary>Inode ("INOD").</summary>
    public const uint Inod = 0x494E4F44;

    /// <summary>Tag index ("TAGI").</summary>
    public const uint Tagi = 0x54414749;

    /// <summary>Metadata write-ahead log ("MWAL").</summary>
    public const uint Mwal = 0x4D57414C;

    /// <summary>Metrics tracker ("MTRK").</summary>
    public const uint Mtrk = 0x4D54524B;

    /// <summary>B-tree node ("BTRE").</summary>
    public const uint Btre = 0x42545245;

    // ── Features ───────────────────────────────────────────────────────

    /// <summary>Snapshot ("SNAP").</summary>
    public const uint Snap = 0x534E4150;

    /// <summary>Replication metadata ("REPL").</summary>
    public const uint Repl = 0x5245504C;

    /// <summary>RAID metadata ("RAID").</summary>
    public const uint Raid = 0x52414944;

    /// <summary>Compression metadata ("COMP").</summary>
    public const uint Comp = 0x434F4D50;

    /// <summary>Intelligence cache ("INTE").</summary>
    public const uint Inte = 0x494E5445;

    /// <summary>Streaming region ("STRE").</summary>
    public const uint Stre = 0x53545245;

    /// <summary>Cross-reference table ("XREF").</summary>
    public const uint Xref = 0x58524546;

    /// <summary>WORM (write-once-read-many) region ("WORM").</summary>
    public const uint Worm = 0x574F524D;

    /// <summary>Compute cache / code region ("CODE").</summary>
    public const uint Code = 0x434F4445;

    /// <summary>Data write-ahead log ("DWAL").</summary>
    public const uint Dwal = 0x4457414C;

    /// <summary>Data block ("DATA").</summary>
    public const uint Data = 0x44415441;

    /// <summary>Free / unallocated block ("FREE").</summary>
    public const uint Free = 0x46524545;

    // ── Module Registry Extensions ─────────────────────────────────────

    /// <summary>Compliance vault ("CMVT").</summary>
    public const uint Cmvt = 0x434D5654;

    /// <summary>Audit log ("ALOG") — owned by the AuditLog module.</summary>
    public const uint Alog = 0x414C4F47;

    /// <summary>
    /// Compliance audit log ("CMAL") — compliance-module-specific audit trail,
    /// distinct from the standalone AuditLog module's ALOG blocks.
    /// Cat 9 (finding 827): separate tag prevents ambiguity during crash recovery.
    /// </summary>
    public const uint Cmal = 0x434D414C;

    /// <summary>Consensus log ("CLOG").</summary>
    public const uint Clog = 0x434C4F47;

    /// <summary>Dictionary / metadata store ("DICT").</summary>
    public const uint Dict = 0x44494354;

    /// <summary>Anonymous / anonymization region ("ANON").</summary>
    public const uint Anon = 0x414E4F4E;

    /// <summary>Module log ("MLOG").</summary>
    public const uint Mlog = 0x4D4C4F47;

    // ── Identity & Recovery ─────────────────────────────────────────────

    /// <summary>Emergency Recovery ("ERCV").</summary>
    public const uint Ercv = 0x45524356;

    /// <summary>Recovery Control ("RCVR") — always at block 14, never encrypted (AD-67).</summary>
    public const uint Rcvr = 0x52435652;

    /// <summary>Extent tree node ("EXTN").</summary>
    public const uint Extn = 0x4558544E;

    /// <summary>Extended metadata ("EXMD").</summary>
    public const uint Exmd = 0x45584D44;

    /// <summary>Integrity anchor ("IANT").</summary>
    public const uint Iant = 0x49414E54;

    // ── SQL / Analytics ─────────────────────────────────────────────────

    /// <summary>Columnar region ("COLR").</summary>
    public const uint Colr = 0x434F4C52;

    /// <summary>Zone map index ("ZMAP").</summary>
    public const uint Zmap = 0x5A4D4150;

    // ── v2.1 Module Extensions (VOPT-87) ────────────────────────────────

    /// <summary>WAL Subscriber Cursors ("WALS") — WalSubscribers module (bit 21).</summary>
    public const uint Wals = 0x57414C53;

    /// <summary>ZNS Zone Map ("ZNSM") — ZnsZoneMap module (bit 23).</summary>
    public const uint Znsm = 0x5A4E534D;

    /// <summary>Online Operations Journal ("OPJR") — OnlineOps module (bit 28).</summary>
    public const uint Opjr = 0x4F504A52;

    /// <summary>Trailer block ("TRLR") — format trailer sentinel.</summary>
    public const uint Trlr = 0x54524C52;

    // ── Lookup Set ─────────────────────────────────────────────────────

    private static readonly FrozenSet<uint> KnownTags = new HashSet<uint>
    {
        Supb, Rmap, Polv, Encr, Bmap, Inod, Tagi, Mwal, Mtrk, Btre,
        Snap, Repl, Raid, Comp, Inte, Stre, Xref, Worm, Code, Dwal,
        Data, Free, Cmvt, Alog, Cmal, Clog, Dict, Anon, Mlog, Ercv, Extn,
        Colr, Zmap, Exmd, Iant, Wals, Znsm, Opjr, Trlr, Rcvr
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

        // Cat 9 (finding 815): validate all characters are printable ASCII (0x20-0x7E) so that
        // non-ASCII chars do not silently produce incorrect tag values via char-to-uint truncation.
        for (int i = 0; i < 4; i++)
        {
            if (s[i] < 0x20 || s[i] > 0x7E)
                throw new ArgumentException(
                    $"Tag string must contain only printable ASCII characters (0x20-0x7E). Character at index {i} is 0x{(int)s[i]:X2}.",
                    nameof(s));
        }

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
