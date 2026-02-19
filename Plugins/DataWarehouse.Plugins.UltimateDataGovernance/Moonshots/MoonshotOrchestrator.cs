using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Moonshots;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateDataGovernance.Moonshots;

/// <summary>
/// Production implementation of <see cref="IMoonshotOrchestrator"/> that executes
/// cross-moonshot pipelines by coordinating registered <see cref="IMoonshotPipelineStage"/>
/// instances in a defined order.
///
/// Key behaviors:
/// <list type="bullet">
///   <item>Stages execute in the order defined by <see cref="MoonshotPipelineDefinition.StageOrder"/>.</item>
///   <item>Disabled moonshots (per feature flags or configuration) are skipped gracefully.</item>
///   <item>Stage precondition checks (<see cref="IMoonshotPipelineStage.CanExecuteAsync"/>) are honored.</item>
///   <item>Stage failures are captured without halting the pipeline (fail-open with logging).</item>
///   <item>Pipeline execution publishes observability events via <see cref="IMessageBus"/>.</item>
/// </list>
/// </summary>
public sealed class MoonshotOrchestrator : IMoonshotOrchestrator
{
    /// <summary>Bus topic published after each stage completes (success or failure).</summary>
    public const string TopicStageCompleted = "moonshot.pipeline.stage.completed";

    /// <summary>Bus topic published after the entire pipeline completes.</summary>
    public const string TopicPipelineCompleted = "moonshot.pipeline.completed";

    private readonly IMoonshotRegistry _registry;
    private readonly MoonshotConfiguration _configuration;
    private readonly ILogger<MoonshotOrchestrator> _logger;
    private readonly Dictionary<MoonshotId, IMoonshotPipelineStage> _stages = new();

    /// <summary>
    /// Initializes a new <see cref="MoonshotOrchestrator"/>.
    /// </summary>
    /// <param name="registry">Moonshot registry for status lookups.</param>
    /// <param name="configuration">Moonshot configuration for enable/disable checks.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public MoonshotOrchestrator(
        IMoonshotRegistry registry,
        MoonshotConfiguration configuration,
        ILogger<MoonshotOrchestrator> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void RegisterStage(IMoonshotPipelineStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        _stages[stage.Id] = stage;
        _logger.LogDebug("Registered pipeline stage for moonshot {MoonshotId}", stage.Id);
    }

    /// <inheritdoc />
    public IReadOnlyList<MoonshotId> GetRegisteredStages()
    {
        return _stages.Keys.OrderBy(id => (int)id).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public Task<MoonshotPipelineResult> ExecuteDefaultPipelineAsync(
        MoonshotPipelineContext context,
        CancellationToken ct)
    {
        return ExecutePipelineAsync(context, DefaultPipelineDefinition.IngestToLifecycle, ct);
    }

    /// <inheritdoc />
    public async Task<MoonshotPipelineResult> ExecutePipelineAsync(
        MoonshotPipelineContext context,
        MoonshotPipelineDefinition pipeline,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pipeline);

        var pipelineStopwatch = Stopwatch.StartNew();
        var stageResults = new List<MoonshotStageResult>();

        _logger.LogInformation(
            "Starting moonshot pipeline '{PipelineName}' for object {ObjectId} with {StageCount} stages",
            pipeline.Name, context.ObjectId, pipeline.StageOrder.Count);

        foreach (var moonshotId in pipeline.StageOrder)
        {
            ct.ThrowIfCancellationRequested();

            // Check if stage is registered
            if (!_stages.TryGetValue(moonshotId, out var stage))
            {
                _logger.LogWarning(
                    "Pipeline stage for moonshot {MoonshotId} is not registered -- skipping",
                    moonshotId);
                continue;
            }

            // Check feature flag in pipeline definition
            if (pipeline.FeatureFlags.TryGetValue(moonshotId, out var flag) && !flag.Enabled)
            {
                _logger.LogInformation(
                    "Moonshot {MoonshotId} is disabled by pipeline feature flag (reason: {Reason}) -- skipping",
                    moonshotId, flag.DisabledReason ?? "none");
                continue;
            }

            // Check configuration-level enable/disable
            if (!_configuration.IsEnabled(moonshotId))
            {
                _logger.LogInformation(
                    "Moonshot {MoonshotId} is disabled in configuration -- skipping",
                    moonshotId);
                continue;
            }

            // Check precondition
            try
            {
                var canExecute = await stage.CanExecuteAsync(context, ct).ConfigureAwait(false);
                if (!canExecute)
                {
                    _logger.LogInformation(
                        "Moonshot {MoonshotId} precondition not met -- skipping",
                        moonshotId);
                    continue;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Moonshot {MoonshotId} precondition check threw an exception -- skipping",
                    moonshotId);
                continue;
            }

            // Execute the stage
            var stageStopwatch = Stopwatch.StartNew();
            MoonshotStageResult result;

            try
            {
                result = await stage.ExecuteAsync(context, ct).ConfigureAwait(false);
                stageStopwatch.Stop();

                // Override duration with our measured wall-clock time
                result = result with { Duration = stageStopwatch.Elapsed };

                _logger.LogInformation(
                    "Moonshot {MoonshotId} completed in {Duration}ms -- success: {Success}",
                    moonshotId, stageStopwatch.ElapsedMilliseconds, result.Success);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Propagate cancellation
            }
            catch (Exception ex)
            {
                stageStopwatch.Stop();
                result = new MoonshotStageResult(
                    Stage: moonshotId,
                    Success: false,
                    Duration: stageStopwatch.Elapsed,
                    Error: $"Stage execution failed: {ex.Message}");

                _logger.LogError(ex,
                    "Moonshot {MoonshotId} stage execution failed after {Duration}ms -- continuing pipeline (fail-open)",
                    moonshotId, stageStopwatch.ElapsedMilliseconds);
            }

            stageResults.Add(result);
            context.AddStageResult(result);

            // Publish stage completion event for observability
            await PublishStageCompletedAsync(context, moonshotId, result, ct).ConfigureAwait(false);
        }

        pipelineStopwatch.Stop();

        var pipelineResult = new MoonshotPipelineResult(
            StageResults: stageResults.AsReadOnly(),
            AllSucceeded: stageResults.Count > 0 && stageResults.All(r => r.Success),
            TotalDuration: pipelineStopwatch.Elapsed,
            CompletedAt: DateTimeOffset.UtcNow);

        _logger.LogInformation(
            "Moonshot pipeline '{PipelineName}' completed for object {ObjectId} in {Duration}ms -- " +
            "{ExecutedCount} stages executed, all succeeded: {AllSucceeded}",
            pipeline.Name, context.ObjectId, pipelineStopwatch.ElapsedMilliseconds,
            stageResults.Count, pipelineResult.AllSucceeded);

        // Publish pipeline completion event
        await PublishPipelineCompletedAsync(context, pipeline.Name, pipelineResult, ct).ConfigureAwait(false);

        return pipelineResult;
    }

    private async Task PublishStageCompletedAsync(
        MoonshotPipelineContext context,
        MoonshotId moonshotId,
        MoonshotStageResult result,
        CancellationToken ct)
    {
        try
        {
            var message = new PluginMessage
            {
                Type = TopicStageCompleted,
                SourcePluginId = "com.datawarehouse.governance.ultimate",
                Payload = new Dictionary<string, object>
                {
                    ["objectId"] = context.ObjectId,
                    ["moonshotId"] = moonshotId.ToString(),
                    ["success"] = result.Success,
                    ["durationMs"] = result.Duration.TotalMilliseconds,
                    ["error"] = result.Error ?? string.Empty
                }
            };
            await context.MessageBus.PublishAsync(TopicStageCompleted, message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish stage completion event for {MoonshotId}", moonshotId);
        }
    }

    private async Task PublishPipelineCompletedAsync(
        MoonshotPipelineContext context,
        string pipelineName,
        MoonshotPipelineResult result,
        CancellationToken ct)
    {
        try
        {
            var message = new PluginMessage
            {
                Type = TopicPipelineCompleted,
                SourcePluginId = "com.datawarehouse.governance.ultimate",
                Payload = new Dictionary<string, object>
                {
                    ["objectId"] = context.ObjectId,
                    ["pipelineName"] = pipelineName,
                    ["allSucceeded"] = result.AllSucceeded,
                    ["totalDurationMs"] = result.TotalDuration.TotalMilliseconds,
                    ["stageCount"] = result.StageResults.Count,
                    ["completedAt"] = result.CompletedAt.ToString("O")
                }
            };
            await context.MessageBus.PublishAsync(TopicPipelineCompleted, message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish pipeline completion event for '{PipelineName}'", pipelineName);
        }
    }
}
