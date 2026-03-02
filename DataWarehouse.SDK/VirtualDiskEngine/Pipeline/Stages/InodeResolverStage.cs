using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using DataWarehouse.SDK.VirtualDiskEngine.Metadata;

namespace DataWarehouse.SDK.VirtualDiskEngine.Pipeline.Stages;

/// <summary>
/// Write Stage 1: resolves or allocates the target inode. This stage always runs
/// (no module gate) and is the first stage in every write pipeline. It creates a new
/// <see cref="ExtendedInode512"/> if one does not exist in the context, sets core
/// inode metadata (timestamps, size, flags), and copies the module manifest so that
/// downstream stages can read manifest-derived configuration from the inode.
/// </summary>
/// <remarks>
/// <para>
/// The inode number is taken from <see cref="VdePipelineContext.InodeNumber"/>. If a
/// new inode is required (the context's <see cref="VdePipelineContext.Inode"/> is null),
/// the stage allocates a block via <see cref="IBlockAllocator"/> for the inode metadata.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VDE write pipeline inode resolver stage (VOPT-90)")]
public sealed class InodeResolverStage : IVdeWriteStage
{
    private readonly IBlockAllocator _allocator;

    /// <summary>
    /// Initializes a new <see cref="InodeResolverStage"/> with the given block allocator.
    /// </summary>
    /// <param name="allocator">Block allocator for inode storage allocation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is null.</exception>
    public InodeResolverStage(IBlockAllocator allocator)
    {
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
    }

    /// <inheritdoc />
    public string StageName => "InodeResolver";

    /// <inheritdoc />
    /// <remarks>Always null: inode resolution is unconditional for every write.</remarks>
    public ModuleId? ModuleGate => null;

    /// <inheritdoc />
    public Task ExecuteAsync(VdePipelineContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        long nowTicks = now.UtcTicks;
        long nowNs = now.UtcTicks * 100; // 100ns ticks -> nanoseconds

        if (context.Inode is null)
        {
            // New inode: allocate a block for inode metadata storage
            long inodeBlock = _allocator.AllocateBlock(ct);

            var inode = new ExtendedInode512
            {
                InodeNumber = context.InodeNumber > 0 ? context.InodeNumber : inodeBlock,
                Type = InodeType.File,
                Flags = InodeFlags.None,
                LinkCount = 1,
                Size = context.DataBuffer.Length,
                AllocatedSize = 0, // will be set by ExtentAllocatorStage
                CreatedUtc = nowTicks,
                ModifiedUtc = nowTicks,
                AccessedUtc = nowTicks,
                ChangedUtc = nowTicks,
                CreatedNs = nowNs,
                ModifiedNs = nowNs,
                AccessedNs = nowNs,
            };

            // Set inode flags based on active modules in the manifest
            SetInodeFlagsFromManifest(inode, context.Manifest);

            context.Inode = inode;
            context.InodeNumber = inode.InodeNumber;
            context.SetProperty("InodeBlock", inodeBlock);
        }
        else
        {
            // Existing inode: update modification timestamps and size
            context.Inode.ModifiedUtc = nowTicks;
            context.Inode.ChangedUtc = nowTicks;
            context.Inode.ModifiedNs = nowNs;
            context.Inode.Size = context.DataBuffer.Length;

            // Refresh flags from current manifest
            SetInodeFlagsFromManifest(context.Inode, context.Manifest);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets inode-level flags based on which modules are active in the manifest.
    /// </summary>
    private static void SetInodeFlagsFromManifest(ExtendedInode512 inode, ModuleManifestField manifest)
    {
        if (manifest.IsModuleActive(ModuleId.Compression))
            inode.Flags |= InodeFlags.Compressed;

        if (manifest.IsModuleActive(ModuleId.Security))
            inode.Flags |= InodeFlags.Encrypted;
    }
}
