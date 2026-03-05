using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

/// <summary>
/// Configuration for the <see cref="ForensicNecromancyRecovery"/> tool.
/// Controls scan range, verification options, and output behaviour.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 (VOPT-43): TRLR stride-scan forensic recovery configuration")]
public sealed class ForensicRecoveryConfig
{
    /// <summary>
    /// Assumed block size in bytes. The user may need to try common values (4096, 16384, 65536)
    /// when the Superblock is destroyed and the block size is unknown.
    /// Defaults to 4096.
    /// </summary>
    public int BlockSize { get; set; } = 4096;

    /// <summary>
    /// Zero-based block number at which the TRLR stride scan begins.
    /// Defaults to 0 (start of device).
    /// </summary>
    public long ScanStartBlock { get; set; } = 0;

    /// <summary>
    /// Exclusive end block number for the TRLR stride scan.
    /// Clamped to the device's actual block count at runtime.
    /// Defaults to <see cref="long.MaxValue"/> (scan to end of device).
    /// </summary>
    public long ScanEndBlock { get; set; } = long.MaxValue;

    /// <summary>
    /// When <see langword="true"/>, each recovered data block is re-read and its
    /// XxHash64 checksum is verified against the value stored in the TRLR record.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool VerifyDataBlockChecksums { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, a full-device scan (not limited to stride 256) is
    /// performed to locate metadata blocks (INOD, BTRE, SUPB, RMAP, etc.) by matching
    /// their trailer BlockTypeTag against <see cref="Format.BlockTypeTags"/> known values.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool RecoverMetadataBlocks { get; set; } = true;

    /// <summary>
    /// Optional directory path where recovered block files are written.
    /// When <see langword="null"/> the recovery result is kept in memory only and
    /// no files are emitted.
    /// </summary>
    public string? OutputDirectory { get; set; }
}
