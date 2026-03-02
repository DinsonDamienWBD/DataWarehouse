using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Pipeline;

/// <summary>
/// Direction of data flow through the VDE I/O pipeline.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VDE I/O pipeline direction (VOPT-88)")]
public enum PipelineDirection : byte
{
    /// <summary>Data is being written to the VDE.</summary>
    Write = 0,

    /// <summary>Data is being read from the VDE.</summary>
    Read = 1,
}

/// <summary>
/// A single composable stage in the VDE I/O pipeline. Each stage performs one
/// discrete operation (e.g., inode resolution, module population, integrity
/// calculation) and is optionally gated by a <see cref="ModuleId"/> bit in the
/// volume's module manifest.
/// </summary>
/// <remarks>
/// Stages are executed in a fixed order by <see cref="VdeWritePipeline"/> or
/// <see cref="VdeReadPipeline"/>. If <see cref="ModuleGate"/> is non-null and
/// the corresponding module bit is OFF in the manifest, the stage is skipped
/// with zero overhead.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VDE I/O pipeline stage interface (VOPT-88)")]
public interface IVdePipelineStage
{
    /// <summary>
    /// Human-readable name for this stage (e.g., "InodeResolver", "ModulePopulator").
    /// Used for diagnostics, logging, and audit trail entries.
    /// </summary>
    string StageName { get; }

    /// <summary>
    /// If non-null, this stage only executes when the specified module bit is active
    /// in the pipeline context's <see cref="ModuleManifestField"/>. If null, the
    /// stage always executes (e.g., InodeResolver is unconditional).
    /// </summary>
    ModuleId? ModuleGate { get; }

    /// <summary>
    /// Executes this pipeline stage, reading from and mutating the shared
    /// <see cref="VdePipelineContext"/>.
    /// </summary>
    /// <param name="context">The mutable pipeline context carried across all stages.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    Task ExecuteAsync(VdePipelineContext context, CancellationToken ct);
}

/// <summary>
/// Marker interface for stages that participate in the write pipeline.
/// Write stages execute in order: InodeResolver, ModulePopulator, TagIndexer,
/// DataTransformer, ExtentAllocator+RAID, IntegrityCalculator, WAL+BlockWriter.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VDE write pipeline stage marker (VOPT-88)")]
public interface IVdeWriteStage : IVdePipelineStage
{
}

/// <summary>
/// Marker interface for stages that participate in the read pipeline.
/// Read stages execute in order: InodeLookup, AccessControl, ExtentResolver,
/// BlockReader+Integrity, ModuleExtractor, PostReadUpdates.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VDE read pipeline stage marker (VOPT-88)")]
public interface IVdeReadStage : IVdePipelineStage
{
}
