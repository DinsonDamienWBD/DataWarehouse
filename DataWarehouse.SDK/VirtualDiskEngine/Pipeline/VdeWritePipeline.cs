using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Pipeline;

/// <summary>
/// Orchestrates the VDE write I/O pipeline by executing a pre-ordered list of
/// <see cref="IVdeWriteStage"/> instances. Each stage is module-gated: if a stage's
/// <see cref="IVdePipelineStage.ModuleGate"/> is non-null and the corresponding bit
/// is OFF in the context's <see cref="VdePipelineContext.Manifest"/>, the stage is
/// skipped with zero overhead.
/// </summary>
/// <remarks>
/// <para>
/// Per AD-53, the write pipeline stages execute in this order:
/// <list type="number">
///   <item><description>InodeResolver — resolve or allocate the target inode</description></item>
///   <item><description>ModulePopulator — populate per-module inode extension fields</description></item>
///   <item><description>TagIndexer — update tag indexes for searchable metadata</description></item>
///   <item><description>DataTransformer — compress, encrypt, or delta-encode the data buffer</description></item>
///   <item><description>ExtentAllocator+RAID — allocate extents and compute RAID parity</description></item>
///   <item><description>IntegrityCalculator — compute integrity hashes (Merkle tree, checksums)</description></item>
///   <item><description>WAL+BlockWriter — write to WAL then flush blocks to disk</description></item>
/// </list>
/// </para>
/// <para>
/// This class is a pure orchestrator; it does NOT implement any stage logic itself.
/// The caller is responsible for constructing the stage list in the correct order.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VDE write pipeline orchestrator (VOPT-88)")]
public sealed class VdeWritePipeline
{
    private readonly IReadOnlyList<IVdeWriteStage> _stages;

    /// <summary>
    /// Initializes a new write pipeline with the given pre-ordered stages.
    /// </summary>
    /// <param name="stages">
    /// The write stages in execution order. The caller must ensure correct ordering
    /// (InodeResolver first, WAL+BlockWriter last).
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="stages"/> is null.</exception>
    public VdeWritePipeline(IReadOnlyList<IVdeWriteStage> stages)
    {
        _stages = stages ?? throw new ArgumentNullException(nameof(stages));
    }

    /// <summary>
    /// The ordered list of write stages in this pipeline. Exposed for introspection,
    /// diagnostics, and pipeline validation.
    /// </summary>
    public IReadOnlyList<IVdeWriteStage> Stages => _stages;

    /// <summary>
    /// Executes all write stages in order, applying module gating and recording
    /// an audit trail of executed and skipped stages in the context.
    /// </summary>
    /// <param name="context">
    /// The pipeline context to flow through all stages. Must have
    /// <see cref="VdePipelineContext.Manifest"/> set before calling.
    /// </param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>The same context instance, with all stage mutations applied.</returns>
    public async Task<VdePipelineContext> ExecuteAsync(VdePipelineContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Direction = PipelineDirection.Write;

        for (int i = 0; i < _stages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var stage = _stages[i];

            // Module gating: skip if the stage's required module bit is OFF
            if (stage.ModuleGate is { } gateModule &&
                !context.Manifest.IsModuleActive(gateModule))
            {
                context.StagesSkipped.Add(stage.StageName);
                continue;
            }

            await stage.ExecuteAsync(context, ct).ConfigureAwait(false);
            context.StagesExecuted.Add(stage.StageName);
        }

        return context;
    }
}
