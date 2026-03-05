using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.BlockExport;

/// <summary>
/// Configuration options for a self-describing VDE export operation.
/// Controls which inodes are included, size limits, and output layout.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-53 self-describing portable export")]
public sealed class ExportConfig
{
    /// <summary>
    /// Optional predicate to filter inodes by inode number.
    /// When null, all inodes are included (subject to other predicates).
    /// </summary>
    public Func<long, bool>? InodePredicate { get; set; }

    /// <summary>
    /// Optional predicate to filter inodes by name.
    /// When null, all inodes pass the name filter.
    /// </summary>
    public Func<string, bool>? NamePredicate { get; set; }

    /// <summary>
    /// Maximum total size in bytes of the exported .dwvd file.
    /// Default is 10 GiB. Export halts early and sets
    /// the export result's <c>SizeLimitReached</c> flag when this limit is reached.
    /// </summary>
    public long MaxExportSizeBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// When true (default), inline extended-attribute data is copied into
    /// the exported inode table as-is.
    /// </summary>
    public bool IncludeInlineTags { get; set; } = true;

    /// <summary>
    /// When true, the exporter writes raw (decrypted) data blocks to the output.
    /// Requires that the source device has an accessible decryption key.
    /// Default is false (encrypted data is copied as-is).
    /// </summary>
    public bool StripEncryption { get; set; } = false;

    /// <summary>
    /// When true (default), exported data blocks are laid out sequentially
    /// (compacted) in the output file, eliminating fragmentation.
    /// </summary>
    public bool CompactExport { get; set; } = true;

    /// <summary>
    /// Block size in bytes for the exported VDE.
    /// Must be a power of two between 512 and 65536 bytes.
    /// Default is 4096 bytes.
    /// </summary>
    public int BlockSize { get; set; } = 4096;
}
