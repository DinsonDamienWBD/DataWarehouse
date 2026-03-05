using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Inode format type for mixed-layout VDEs where each inode region block
/// can contain inodes of different sizes.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-10 mixed inode format enumeration")]
public enum InodeFormat
{
    /// <summary>64-byte compact inode for tiny inline objects.</summary>
    Compact64 = 64,

    /// <summary>256-byte standard inode (InodeV2 core with minimal modules).</summary>
    Standard256 = 256,

    /// <summary>512-byte extended inode with rich metadata fields.</summary>
    Extended512 = 512,
}

/// <summary>
/// Packing information for how many inodes of each format fit in a single block.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-10 inode packing info per block size")]
public readonly struct InodePackingInfo : IEquatable<InodePackingInfo>
{
    /// <summary>Number of Compact64 inodes that fit in one block.</summary>
    public int Compact64PerBlock { get; }

    /// <summary>Number of Standard256 inodes that fit in one block.</summary>
    public int Standard256PerBlock { get; }

    /// <summary>Number of Extended512 inodes that fit in one block.</summary>
    public int Extended512PerBlock { get; }

    /// <summary>Creates a new packing info instance.</summary>
    public InodePackingInfo(int compact64PerBlock, int standard256PerBlock, int extended512PerBlock)
    {
        Compact64PerBlock = compact64PerBlock;
        Standard256PerBlock = standard256PerBlock;
        Extended512PerBlock = extended512PerBlock;
    }

    /// <inheritdoc />
    public bool Equals(InodePackingInfo other)
        => Compact64PerBlock == other.Compact64PerBlock
        && Standard256PerBlock == other.Standard256PerBlock
        && Extended512PerBlock == other.Extended512PerBlock;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is InodePackingInfo other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Compact64PerBlock, Standard256PerBlock, Extended512PerBlock);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(InodePackingInfo left, InodePackingInfo right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(InodePackingInfo left, InodePackingInfo right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString()
        => $"Packing(Compact64={Compact64PerBlock}, Standard256={Standard256PerBlock}, Extended512={Extended512PerBlock})";
}

/// <summary>
/// Auto-selects inode format (Compact64, Standard256, Extended512) based on object
/// characteristics at write time. Handles the Mixed inode layout mode where each
/// inode region block contains mixed-size inodes.
/// </summary>
/// <remarks>
/// Selection logic:
/// <list type="bullet">
///   <item>Object size &lt;= 48 bytes AND no extended metadata AND no MVCC: Compact64</item>
///   <item>Object size &lt;= 64 GB (with 4KB blocks, 8 direct extents + indirect) AND no extended metadata: Standard256</item>
///   <item>Otherwise (large objects, extended metadata, MVCC): Extended512</item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-10 mixed inode allocator auto-selection")]
public sealed class MixedInodeAllocator
{
    /// <summary>
    /// Maximum file size addressable by Standard256 inodes: 8 direct extents + 1 indirect block.
    /// With 4KB blocks: direct = 8 * 4096 = 32KB, indirect = (4096/8) * 4096 = ~2GB.
    /// With double-indirect: up to ~64GB.
    /// </summary>
    private const long MaxStandard256FileSize = 64L * 1024 * 1024 * 1024; // 64 GB

    private readonly InodeLayoutDescriptor _descriptor;

    /// <summary>
    /// Creates a new MixedInodeAllocator that reads layout information from the superblock.
    /// </summary>
    /// <param name="descriptor">The inode layout descriptor from the superblock.</param>
    public MixedInodeAllocator(InodeLayoutDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    /// <summary>
    /// Selects the optimal inode format based on the object's characteristics.
    /// </summary>
    /// <param name="objectSize">Size of the object in bytes.</param>
    /// <param name="hasExtendedMetadata">True if the object needs extended metadata (xattrs, nanosecond timestamps).</param>
    /// <param name="hasMvcc">True if the object participates in MVCC versioning.</param>
    /// <returns>The selected inode format.</returns>
    public InodeFormat SelectFormat(long objectSize, bool hasExtendedMetadata, bool hasMvcc)
    {
        // Tiny inline objects: use Compact64 (zero block allocation)
        if (objectSize <= CompactInode64.MaxInlineDataSize && !hasExtendedMetadata && !hasMvcc)
        {
            return InodeFormat.Compact64;
        }

        // Standard-size files without extended metadata: use Standard256
        if (objectSize <= MaxStandard256FileSize && !hasExtendedMetadata && !hasMvcc)
        {
            return InodeFormat.Standard256;
        }

        // Large objects, extended metadata, or MVCC: use Extended512
        return InodeFormat.Extended512;
    }

    /// <summary>
    /// Allocates a zero-initialized buffer of the correct size for the given inode format.
    /// </summary>
    /// <param name="format">The inode format to allocate.</param>
    /// <returns>A zero-initialized byte array of the inode size.</returns>
    public byte[] AllocateInode(InodeFormat format)
    {
        return new byte[(int)format];
    }

    /// <summary>
    /// Detects which inode format was used to serialize the given buffer by inspecting
    /// the buffer length. In a mixed-layout block, the allocator tracks inode boundaries.
    /// </summary>
    /// <param name="inodeBuffer">The raw inode buffer to inspect.</param>
    /// <returns>The detected inode format.</returns>
    /// <exception cref="ArgumentException">If the buffer size does not match any known format.</exception>
    public InodeFormat DetectFormat(ReadOnlySpan<byte> inodeBuffer)
    {
        return inodeBuffer.Length switch
        {
            CompactInode64.SerializedSize => InodeFormat.Compact64,    // 64
            (int)InodeFormat.Standard256 => InodeFormat.Standard256,    // 256
            >= ExtendedInode512.SerializedSize => InodeFormat.Extended512, // 512+
            _ when inodeBuffer.Length >= (int)InodeFormat.Standard256 => InodeFormat.Standard256,
            _ when inodeBuffer.Length >= CompactInode64.SerializedSize => InodeFormat.Compact64,
            _ => throw new ArgumentException(
                $"Buffer size {inodeBuffer.Length} does not match any known inode format (64, 256, 512).",
                nameof(inodeBuffer)),
        };
    }

    /// <summary>
    /// Gets packing information for how many inodes of each format fit in a single block.
    /// </summary>
    /// <param name="blockSize">The block size in bytes (e.g., 4096).</param>
    /// <returns>Packing info with counts per format per block.</returns>
    public InodePackingInfo GetPackingInfo(int blockSize)
    {
        if (blockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be positive.");
        return new InodePackingInfo(
            compact64PerBlock: blockSize / (int)InodeFormat.Compact64,       // 4096/64 = 64
            standard256PerBlock: blockSize / (int)InodeFormat.Standard256,   // 4096/256 = 16
            extended512PerBlock: blockSize / (int)InodeFormat.Extended512);  // 4096/512 = 8
    }
}
