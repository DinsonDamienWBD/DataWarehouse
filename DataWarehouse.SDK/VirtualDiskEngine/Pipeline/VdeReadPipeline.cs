using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Pipeline;

/// <summary>
/// Orchestrates the VDE read I/O pipeline by executing a pre-ordered list of
/// <see cref="IVdeReadStage"/> instances. Each stage is module-gated: if a stage's
/// <see cref="IVdePipelineStage.ModuleGate"/> is non-null and the corresponding bit
/// is OFF in the context's <see cref="VdePipelineContext.Manifest"/>, the stage is
/// skipped with zero overhead.
/// </summary>
/// <remarks>
/// <para>
/// Per AD-53, the read pipeline stages execute in this order:
/// <list type="number">
///   <item><description>InodeLookup — look up the target inode from the inode table</description></item>
///   <item><description>AccessControl — verify the caller has read permission</description></item>
///   <item><description>ExtentResolver — resolve the extent list from the inode</description></item>
///   <item><description>BlockReader+Integrity — read blocks from disk and verify integrity</description></item>
///   <item><description>ModuleExtractor — extract per-module inode extension fields</description></item>
///   <item><description>PostReadUpdates — update access timestamps and statistics</description></item>
/// </list>
/// </para>
/// <para>
/// This class is a pure orchestrator; it does NOT implement any stage logic itself.
/// The caller is responsible for constructing the stage list in the correct order.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VDE read pipeline orchestrator (VOPT-88)")]
public sealed class VdeReadPipeline
{
    private readonly IReadOnlyList<IVdeReadStage> _stages;

    /// <summary>
    /// Initializes a new read pipeline with the given pre-ordered stages.
    /// </summary>
    /// <param name="stages">
    /// The read stages in execution order. The caller must ensure correct ordering
    /// (InodeLookup first, PostReadUpdates last).
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="stages"/> is null.</exception>
    public VdeReadPipeline(IReadOnlyList<IVdeReadStage> stages)
    {
        _stages = stages ?? throw new ArgumentNullException(nameof(stages));
    }

    /// <summary>
    /// The ordered list of read stages in this pipeline. Exposed for introspection,
    /// diagnostics, and pipeline validation.
    /// </summary>
    public IReadOnlyList<IVdeReadStage> Stages => _stages;

    /// <summary>
    /// Executes all read stages in order, applying module gating and recording
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

        context.Direction = PipelineDirection.Read;

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
