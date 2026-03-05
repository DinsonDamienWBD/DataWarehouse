using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Pipeline.Stages;

/// <summary>
/// Write Stage 5: allocates physical extents for the data buffer. This stage always
/// runs (no module gate) and builds <see cref="InodeExtent"/> entries from the
/// allocated block ranges, applying extent flags from the pipeline context.
/// </summary>
/// <remarks>
/// <para>
/// The number of blocks required is computed from
/// <c>context.DataBuffer.Length / context.BlockSize</c> (rounded up). The allocator
/// attempts to provide a contiguous extent; if not possible, multiple extents are created.
/// </para>
/// <para>
/// If the RAID module is active, the stage signals downstream by setting
/// <c>context.Properties["RaidParityRequired"]</c> to <c>true</c>.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VDE write pipeline extent allocator stage (VOPT-90)")]
public sealed class ExtentAllocatorStage : IVdeWriteStage
{
    private readonly IBlockAllocator _allocator;

    /// <summary>Property key for RAID parity requirement signal.</summary>
    public const string RaidParityRequiredKey = "RaidParityRequired";

    /// <summary>
    /// Initializes a new <see cref="ExtentAllocatorStage"/> with the given block allocator.
    /// </summary>
    /// <param name="allocator">Block allocator for data extent allocation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is null.</exception>
    public ExtentAllocatorStage(IBlockAllocator allocator)
    {
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
    }

    /// <inheritdoc />
    public string StageName => "ExtentAllocator";

    /// <inheritdoc />
    /// <remarks>Always null: extent allocation is unconditional for every write.</remarks>
    public ModuleId? ModuleGate => null;

    /// <inheritdoc />
    public Task ExecuteAsync(VdePipelineContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        int dataLength = context.DataBuffer.Length;
        if (dataLength == 0)
            return Task.CompletedTask;

        int blockSize = context.BlockSize > 0 ? context.BlockSize : 4096;
        int blocksNeeded = (dataLength + blockSize - 1) / blockSize;

        // Attempt contiguous allocation
        long[] blocks = _allocator.AllocateExtent(blocksNeeded, ct);

        // Build extent flags from context properties
        ExtentFlags flags = ExtentFlags.None;
        if (context.IsCompressed)
            flags |= ExtentFlags.Compressed;
        if (context.IsEncrypted)
            flags |= ExtentFlags.Encrypted;

        // Build InodeExtent entries from the allocated blocks
        // Group contiguous blocks into single extents
        long logicalOffset = 0;
        int i = 0;
        while (i < blocks.Length)
        {
            long startBlock = blocks[i];
            int count = 1;

            // Coalesce contiguous blocks
            while (i + count < blocks.Length && blocks[i + count] == startBlock + count)
            {
                count++;
            }

            var extent = new InodeExtent(startBlock, count, flags, logicalOffset);
            context.Extents.Add(extent);

            logicalOffset += (long)count * blockSize;
            i += count;
        }

        // Update inode allocated size
        if (context.Inode is not null)
        {
            context.Inode.AllocatedSize = (long)blocksNeeded * blockSize;
            context.Inode.ExtentCount = Math.Min(context.Extents.Count, FormatConstants.MaxExtentsPerInode);

            // Copy inline extents to inode
            for (int e = 0; e < context.Inode.ExtentCount; e++)
            {
                context.Inode.Extents[e] = context.Extents[e];
            }
        }

        // Signal RAID parity requirement if RAID module is active
        if (context.Manifest.IsModuleActive(ModuleId.Raid))
        {
            context.SetProperty(RaidParityRequiredKey, true);
        }

        return Task.CompletedTask;
    }
}
