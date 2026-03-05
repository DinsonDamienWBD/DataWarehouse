using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Pipeline.Stages;

/// <summary>
/// Write Stage 3: updates inline tag data and queues tag index updates. This stage
/// is gated by the <see cref="ModuleId.Tags"/> module bit and only executes when
/// tag indexing is active on the volume.
/// </summary>
/// <remarks>
/// <para>
/// Tag data is read from <c>context.Properties["InlineTags"]</c> if present.
/// The inode's InlineXattrArea (64 bytes at the extended fields area) is used
/// for compact inline tag storage. Larger tag sets are queued for the RoaringBitmap
/// tag index via a property bag update.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VDE write pipeline tag indexer stage (VOPT-90)")]
public sealed class TagIndexerStage : IVdeWriteStage
{
    /// <summary>
    /// Well-known property key for inline tag data supplied by the caller.
    /// Expected value type: byte array.
    /// </summary>
    public const string InlineTagsKey = "InlineTags";

    /// <summary>
    /// Well-known property key for queued tag index updates emitted by this stage.
    /// </summary>
    public const string TagIndexUpdateKey = "TagIndexUpdate";

    /// <summary>Maximum bytes for inline tag storage in the inode.</summary>
    private const int MaxInlineTagBytes = ExtendedInode512.MaxInlineXattrSize;

    /// <inheritdoc />
    public string StageName => "TagIndexer";

    /// <inheritdoc />
    public ModuleId? ModuleGate => ModuleId.Tags;

    /// <inheritdoc />
    public Task ExecuteAsync(VdePipelineContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (context.Inode is null)
            return Task.CompletedTask;

        // Read inline tag data from the property bag if supplied by the caller
        if (context.TryGetProperty<byte[]>(InlineTagsKey, out var tagData) && tagData.Length > 0)
        {
            // Write to the inode's InlineXattrArea (truncated to max inline size)
            int inlineBytes = Math.Min(tagData.Length, MaxInlineTagBytes);
            context.Inode.SetInlineXattrs(tagData.AsSpan(0, inlineBytes));

            // Queue RoaringBitmap tag index update for the full tag set
            var tagUpdate = new TagIndexUpdate
            {
                InodeNumber = context.Inode.InodeNumber,
                TagData = tagData,
                InlineBytes = inlineBytes,
            };
            context.SetProperty(TagIndexUpdateKey, tagUpdate);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Describes a pending tag index update to be applied by the tag index manager
    /// after the write pipeline completes.
    /// </summary>
    public sealed class TagIndexUpdate
    {
        /// <summary>Inode number being tagged.</summary>
        public long InodeNumber { get; init; }

        /// <summary>Full tag data bytes.</summary>
        public byte[] TagData { get; init; } = Array.Empty<byte>();

        /// <summary>Number of bytes stored inline in the inode.</summary>
        public int InlineBytes { get; init; }
    }
}
