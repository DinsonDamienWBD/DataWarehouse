using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Pipeline;

/// <summary>
/// Packages the complete output of a VDE read pipeline execution, including the
/// reassembled data bytes, resolved inode metadata, per-module extension fields,
/// inline tags, extent list, and pipeline diagnostics.
/// </summary>
/// <remarks>
/// <para>
/// Constructed via <see cref="FromContext"/> after a successful read pipeline execution.
/// All properties are read-only init-only to prevent mutation after construction.
/// </para>
/// <para>
/// The <see cref="ModuleFields"/> dictionary contains per-module inode extension
/// bytes keyed by <see cref="ModuleId"/>. For modules with registered handlers, these
/// bytes have been parsed and the results are also available in the pipeline context's
/// property bag. For modules without handlers, raw bytes are preserved.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VDE read pipeline rich result type (VOPT-90)")]
public sealed class RichReadResult
{
    /// <summary>
    /// The object's data bytes, post-decryption and post-decompression.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>
    /// Full inode metadata for the read object.
    /// </summary>
    public ExtendedInode512 Inode { get; init; } = null!;

    /// <summary>
    /// Per-module extracted inode extension fields, keyed by <see cref="ModuleId"/>.
    /// Contains raw bytes for each active module; parsed values are in the pipeline
    /// context's property bag.
    /// </summary>
    public IReadOnlyDictionary<ModuleId, ReadOnlyMemory<byte>> ModuleFields { get; init; }
        = new Dictionary<ModuleId, ReadOnlyMemory<byte>>();

    /// <summary>
    /// The 64-byte inline tag area from the inode's extended attributes, or null
    /// if the Tags module was not active for this read.
    /// </summary>
    public ReadOnlyMemory<byte>? InlineTags { get; init; }

    /// <summary>
    /// The resolved extent list for this object, in logical order.
    /// </summary>
    public IReadOnlyList<InodeExtent> Extents { get; init; } = Array.Empty<InodeExtent>();

    /// <summary>
    /// Names of the pipeline stages that executed during this read operation,
    /// in execution order.
    /// </summary>
    public IReadOnlyList<string> StagesExecuted { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Total elapsed time for the read pipeline execution.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Creates a <see cref="RichReadResult"/> from a completed read pipeline context,
    /// extracting all relevant fields and computing the elapsed duration.
    /// </summary>
    /// <param name="ctx">The completed read pipeline context. Must have been executed
    /// through a full read pipeline (all stages completed).</param>
    /// <returns>A fully populated <see cref="RichReadResult"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ctx"/> is null.</exception>
    public static RichReadResult FromContext(VdePipelineContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Compute duration from pipeline start to now
        var duration = DateTimeOffset.UtcNow - ctx.StartedAt;

        // Extract inline tags if Tags module was active
        ReadOnlyMemory<byte>? inlineTags = null;
        if (ctx.Inode is not null && ctx.Manifest.IsModuleActive(ModuleId.Tags))
        {
            var xattrs = ctx.Inode.InlineXattrArea;
            if (xattrs is not null && xattrs.Length > 0)
            {
                inlineTags = xattrs.AsMemory();
            }
        }

        // Copy module fields to a read-only dictionary
        var moduleFields = new Dictionary<ModuleId, ReadOnlyMemory<byte>>(ctx.ModuleFields.Count);
        foreach (var kvp in ctx.ModuleFields)
        {
            moduleFields[kvp.Key] = kvp.Value;
        }

        return new RichReadResult
        {
            Data = ctx.DataBuffer,
            Inode = ctx.Inode!,
            ModuleFields = moduleFields,
            InlineTags = inlineTags,
            Extents = ctx.Extents.ToArray(),
            StagesExecuted = ctx.StagesExecuted.ToArray(),
            Duration = duration,
        };
    }
}
