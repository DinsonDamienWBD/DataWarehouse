using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Moonshots;

/// <summary>
/// Represents a single moonshot stage that can participate in a cross-moonshot pipeline.
/// Each moonshot plugin implements this interface to register its processing stage
/// with the moonshot orchestrator.
/// </summary>
public interface IMoonshotPipelineStage
{
    /// <summary>
    /// The moonshot identifier this stage represents.
    /// </summary>
    MoonshotId Id { get; }

    /// <summary>
    /// Executes this moonshot stage against the given pipeline context.
    /// The stage reads input from the context, performs its processing,
    /// and returns a result describing success/failure and timing.
    /// </summary>
    /// <param name="context">The mutable pipeline context flowing between stages.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>A result describing the outcome of this stage's execution.</returns>
    Task<MoonshotStageResult> ExecuteAsync(MoonshotPipelineContext context, CancellationToken ct);

    /// <summary>
    /// Checks whether this stage's preconditions are met and it can execute.
    /// Called by the orchestrator before invoking <see cref="ExecuteAsync"/>.
    /// Returning false causes the orchestrator to skip this stage.
    /// </summary>
    /// <param name="context">The current pipeline context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if preconditions are met and the stage can execute; false to skip.</returns>
    Task<bool> CanExecuteAsync(MoonshotPipelineContext context, CancellationToken ct);
}

/// <summary>
/// Orchestrates the execution of cross-moonshot pipelines, coordinating multiple
/// moonshot stages in a defined order with feature-flag control.
///
/// The orchestrator is the central coordination point for Phase 64 moonshot wiring.
/// It executes registered stages in pipeline-defined order, respecting feature flags,
/// precondition checks, and cancellation.
/// </summary>
public interface IMoonshotOrchestrator
{
    /// <summary>
    /// Executes a moonshot pipeline with the specified definition and context.
    /// Stages are executed in the order defined by the pipeline definition,
    /// subject to feature flags and precondition checks.
    /// </summary>
    /// <param name="context">The mutable pipeline context shared across all stages.</param>
    /// <param name="pipeline">The pipeline definition specifying stage order and feature flags.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>Aggregate result of all stage executions.</returns>
    Task<MoonshotPipelineResult> ExecutePipelineAsync(
        MoonshotPipelineContext context,
        MoonshotPipelineDefinition pipeline,
        CancellationToken ct);

    /// <summary>
    /// Registers a moonshot pipeline stage with the orchestrator.
    /// Each moonshot plugin calls this during initialization to make
    /// its stage available for pipeline execution.
    /// </summary>
    /// <param name="stage">The pipeline stage to register.</param>
    void RegisterStage(IMoonshotPipelineStage stage);

    /// <summary>
    /// Returns the list of moonshot IDs that have registered stages.
    /// </summary>
    /// <returns>Ordered list of registered moonshot stage IDs.</returns>
    IReadOnlyList<MoonshotId> GetRegisteredStages();

    /// <summary>
    /// Executes the default pipeline using the standard ingest-to-lifecycle stage order
    /// (UniversalTags through UniversalFabric) with all registered stages.
    /// </summary>
    /// <param name="context">The mutable pipeline context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregate result of the default pipeline execution.</returns>
    Task<MoonshotPipelineResult> ExecuteDefaultPipelineAsync(
        MoonshotPipelineContext context,
        CancellationToken ct);
}
