using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Storage.Placement;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.ZeroGravity;

/// <summary>
/// Wires zero-gravity storage events to the DataWarehouse message bus for cross-plugin observability.
/// All events are published as fire-and-forget; message bus failures never break storage operations.
/// </summary>
/// <remarks>
/// Published topics follow the naming convention: storage.zerogravity.{event}
///
/// Events published:
/// <list type="bullet">
///   <item><description>storage.zerogravity.rebalance.started - Rebalance job started</description></item>
///   <item><description>storage.zerogravity.rebalance.completed - Rebalance job completed</description></item>
///   <item><description>storage.zerogravity.rebalance.failed - Rebalance job failed</description></item>
///   <item><description>storage.zerogravity.migration.progress - Migration progress update</description></item>
///   <item><description>storage.zerogravity.cost.report - Cost optimization report generated</description></item>
///   <item><description>storage.zerogravity.cost.savings - Savings opportunity identified</description></item>
///   <item><description>storage.zerogravity.placement.computed - Placement decision made</description></item>
///   <item><description>storage.zerogravity.gravity.scored - Object gravity score computed</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-gravity message bus wiring")]
public sealed class ZeroGravityMessageBusWiring : IDisposable
{
    private readonly IMessageBus _messageBus;
    private readonly string _topicPrefix;
    private readonly string _sourcePluginId;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ZeroGravityMessageBusWiring"/> class.
    /// </summary>
    /// <param name="messageBus">The SDK message bus for cross-plugin communication.</param>
    /// <param name="sourcePluginId">The plugin ID to stamp on outgoing messages.</param>
    /// <param name="topicPrefix">Topic prefix for all zero-gravity events.</param>
    public ZeroGravityMessageBusWiring(
        IMessageBus messageBus,
        string sourcePluginId = "UltimateStorage.ZeroGravity",
        string topicPrefix = "storage.zerogravity")
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _sourcePluginId = sourcePluginId;
        _topicPrefix = topicPrefix;
    }

    /// <summary>
    /// Publishes a rebalance job started event.
    /// </summary>
    /// <param name="job">The rebalance job that was started.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PublishRebalanceStartedAsync(RebalanceJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await PublishAsync($"{_topicPrefix}.rebalance.started", "rebalance.started", new Dictionary<string, object>
        {
            ["JobId"] = job.JobId,
            ["TotalMoves"] = job.TotalMoves,
            ["EstimatedDurationSeconds"] = job.Plan.EstimatedDurationSeconds,
            ["EstimatedCost"] = job.Plan.EstimatedCost,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);
    }

    /// <summary>
    /// Publishes a rebalance job completed event.
    /// </summary>
    /// <param name="job">The completed rebalance job.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PublishRebalanceCompletedAsync(RebalanceJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var durationSeconds = job.CompletedUtc.HasValue && job.StartedUtc.HasValue
            ? (job.CompletedUtc.Value - job.StartedUtc.Value).TotalSeconds
            : 0.0;

        await PublishAsync($"{_topicPrefix}.rebalance.completed", "rebalance.completed", new Dictionary<string, object>
        {
            ["JobId"] = job.JobId,
            ["CompletedMoves"] = job.CompletedMoves,
            ["FailedMoves"] = job.FailedMoves,
            ["DurationSeconds"] = durationSeconds,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);
    }

    /// <summary>
    /// Publishes a rebalance job failed event.
    /// </summary>
    /// <param name="job">The failed rebalance job.</param>
    /// <param name="reason">Optional failure reason.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PublishRebalanceFailedAsync(RebalanceJob job, string? reason = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await PublishAsync($"{_topicPrefix}.rebalance.failed", "rebalance.failed", new Dictionary<string, object>
        {
            ["JobId"] = job.JobId,
            ["CompletedMoves"] = job.CompletedMoves,
            ["FailedMoves"] = job.FailedMoves,
            ["Reason"] = reason ?? "Unknown failure",
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);
    }

    /// <summary>
    /// Publishes a migration progress update event.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="completedMoves">Number of completed moves so far.</param>
    /// <param name="totalMoves">Total moves in the plan.</param>
    /// <param name="failedMoves">Number of failed moves so far.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PublishMigrationProgressAsync(
        string jobId, int completedMoves, int totalMoves, int failedMoves = 0, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var progressPercent = totalMoves > 0 ? (double)completedMoves / totalMoves * 100.0 : 0.0;

        await PublishAsync($"{_topicPrefix}.migration.progress", "migration.progress", new Dictionary<string, object>
        {
            ["JobId"] = jobId,
            ["CompletedMoves"] = completedMoves,
            ["TotalMoves"] = totalMoves,
            ["FailedMoves"] = failedMoves,
            ["ProgressPercent"] = progressPercent,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);
    }

    /// <summary>
    /// Publishes a cost optimization report event.
    /// </summary>
    /// <param name="planId">Unique identifier for the optimization plan.</param>
    /// <param name="currentMonthlyCost">Current monthly storage cost.</param>
    /// <param name="projectedMonthlyCost">Projected monthly cost after optimization.</param>
    /// <param name="totalRecommendations">Number of optimization recommendations.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PublishCostReportAsync(
        string planId,
        decimal currentMonthlyCost,
        decimal projectedMonthlyCost,
        int totalRecommendations,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        var estimatedSavings = currentMonthlyCost - projectedMonthlyCost;
        var savingsPercent = currentMonthlyCost > 0
            ? (double)(estimatedSavings / currentMonthlyCost) * 100.0
            : 0.0;

        await PublishAsync($"{_topicPrefix}.cost.report", "cost.report", new Dictionary<string, object>
        {
            ["PlanId"] = planId,
            ["CurrentMonthlyCost"] = currentMonthlyCost,
            ["ProjectedMonthlyCost"] = projectedMonthlyCost,
            ["EstimatedSavings"] = estimatedSavings,
            ["SavingsPercent"] = savingsPercent,
            ["TotalRecommendations"] = totalRecommendations,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);
    }

    /// <summary>
    /// Publishes a savings opportunity identified event.
    /// </summary>
    /// <param name="category">Category of the savings opportunity (e.g., "tiering", "dedup", "archive").</param>
    /// <param name="monthlySavings">Estimated monthly savings in currency units.</param>
    /// <param name="description">Human-readable description of the opportunity.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PublishSavingsOpportunityAsync(
        string category, decimal monthlySavings, string description, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        await PublishAsync($"{_topicPrefix}.cost.savings", "cost.savings", new Dictionary<string, object>
        {
            ["Category"] = category,
            ["MonthlySavings"] = monthlySavings,
            ["Description"] = description,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);
    }

    /// <summary>
    /// Publishes a placement decision computed event.
    /// </summary>
    /// <param name="decision">The placement decision that was computed.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PublishPlacementComputedAsync(PlacementDecision decision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        await PublishAsync($"{_topicPrefix}.placement.computed", "placement.computed", new Dictionary<string, object>
        {
            ["PlacementRuleId"] = decision.PlacementRuleId,
            ["PrimaryNode"] = decision.PrimaryNode.NodeId,
            ["ReplicaCount"] = decision.ReplicaNodes.Count,
            ["Deterministic"] = decision.Deterministic,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);
    }

    /// <summary>
    /// Publishes a gravity score computed event.
    /// </summary>
    /// <param name="score">The data gravity score that was computed.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PublishGravityScoredAsync(DataGravityScore score, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(score);
        await PublishAsync($"{_topicPrefix}.gravity.scored", "gravity.scored", new Dictionary<string, object>
        {
            ["ObjectKey"] = score.ObjectKey,
            ["CurrentNode"] = score.CurrentNode,
            ["CompositeScore"] = score.CompositeScore,
            ["AccessFrequency"] = score.AccessFrequency,
            ["ColocatedDependencies"] = score.ColocatedDependencies,
            ["EgressCostPerGB"] = score.EgressCostPerGB,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);
    }

    /// <summary>
    /// Core publish method that wraps the SDK IMessageBus with fire-and-forget semantics.
    /// Message bus failures are silently swallowed to prevent storage operation disruption.
    /// </summary>
    private async Task PublishAsync(string topic, string messageType, Dictionary<string, object> payload, CancellationToken ct)
    {
        if (_disposed) return;

        try
        {
            var message = new PluginMessage
            {
                Type = messageType,
                SourcePluginId = _sourcePluginId,
                Payload = payload,
                Description = $"Zero-gravity storage event: {messageType}"
            };
            await _messageBus.PublishAsync(topic, message, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected during shutdown; do not swallow
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ZeroGravityMessageBusWiring.PublishAsync] {ex.GetType().Name}: {ex.Message}");
            // Message bus failures must not break storage operations.
            // Observability events are best-effort.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
    }
}
