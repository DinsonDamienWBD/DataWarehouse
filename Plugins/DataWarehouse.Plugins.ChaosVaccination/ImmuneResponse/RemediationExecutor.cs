using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.ChaosVaccination;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.ChaosVaccination.ImmuneResponse;

/// <summary>
/// Executes remediation actions in priority order by dispatching commands through the message bus.
/// Each action type maps to a specific bus topic. Actions are executed with per-action timeouts
/// using linked cancellation tokens, ensuring that a single slow action cannot stall the entire
/// remediation pipeline.
///
/// Topic mapping:
/// - RestartPlugin      -> "plugin.lifecycle.restart"
/// - TripCircuitBreaker -> "resilience.circuit-breaker.trip"
/// - DrainConnections   -> "connectivity.drain"
/// - ScaleDown          -> "cluster.autoscale.down"
/// - IsolateNode        -> "cluster.node.isolate"
/// - RerouteTraffic     -> "loadbalancer.reroute"
/// - RestoreFromCheckpoint -> "disaster-recovery.restore-checkpoint"
/// - NotifyOperator     -> "observability.alert.operator"
/// - Custom             -> "chaos.remediation.custom"
///
/// All remediation is dispatched through the bus -- no hardcoded side effects.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Immune response system")]
public sealed class RemediationExecutor
{
    private readonly IMessageBus? _messageBus;

    private static readonly IReadOnlyDictionary<RemediationActionType, string> ActionTopicMap =
        new Dictionary<RemediationActionType, string>
        {
            [RemediationActionType.RestartPlugin] = "plugin.lifecycle.restart",
            [RemediationActionType.TripCircuitBreaker] = "resilience.circuit-breaker.trip",
            [RemediationActionType.DrainConnections] = "connectivity.drain",
            [RemediationActionType.ScaleDown] = "cluster.autoscale.down",
            [RemediationActionType.IsolateNode] = "cluster.node.isolate",
            [RemediationActionType.RerouteTraffic] = "loadbalancer.reroute",
            [RemediationActionType.RestoreFromCheckpoint] = "disaster-recovery.restore-checkpoint",
            [RemediationActionType.NotifyOperator] = "observability.alert.operator",
            [RemediationActionType.Custom] = "chaos.remediation.custom"
        };

    /// <summary>
    /// Initializes a new instance of the <see cref="RemediationExecutor"/> class.
    /// </summary>
    /// <param name="messageBus">Message bus for dispatching remediation commands. May be null in test/offline scenarios.</param>
    public RemediationExecutor(IMessageBus? messageBus)
    {
        _messageBus = messageBus;
    }

    /// <summary>
    /// Executes the given remediation actions in priority order (lowest Priority number first).
    /// Each action is dispatched to the appropriate message bus topic with its own timeout.
    /// Failed actions are recorded but do not abort subsequent actions.
    /// </summary>
    /// <param name="actions">The remediation actions to execute.</param>
    /// <param name="ct">Cancellation token for the entire remediation operation.</param>
    /// <returns>Aggregate result of all action executions.</returns>
    public async Task<RemediationResult> ExecuteAsync(RemediationAction[] actions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actions);

        if (actions.Length == 0)
        {
            return new RemediationResult
            {
                TotalActions = 0,
                SuccessfulActions = 0,
                FailedActions = 0,
                TotalDurationMs = 0,
                ActionResults = Array.Empty<ActionResult>()
            };
        }

        var sortedActions = actions.OrderBy(a => a.Priority).ToArray();
        var results = new List<ActionResult>(sortedActions.Length);
        var overallStopwatch = Stopwatch.StartNew();

        foreach (var action in sortedActions)
        {
            ct.ThrowIfCancellationRequested();

            var actionStopwatch = Stopwatch.StartNew();
            bool success = false;
            string? error = null;

            try
            {
                var timeoutMs = action.TimeoutMs > 0 ? action.TimeoutMs : 30000;
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linkedCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

                await DispatchActionAsync(action, linkedCts.Token).ConfigureAwait(false);
                success = true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                error = $"Action {action.ActionType} timed out after {action.TimeoutMs}ms for target '{action.TargetId}'";
            }
            catch (OperationCanceledException)
            {
                error = "Remediation cancelled by caller";
                actionStopwatch.Stop();
                results.Add(new ActionResult
                {
                    Action = action,
                    Success = false,
                    DurationMs = actionStopwatch.ElapsedMilliseconds,
                    Error = error
                });
                throw; // Propagate caller cancellation
            }
            catch (Exception ex)
            {
                error = $"Action {action.ActionType} failed for target '{action.TargetId}': {ex.Message}";
            }

            actionStopwatch.Stop();
            results.Add(new ActionResult
            {
                Action = action,
                Success = success,
                DurationMs = actionStopwatch.ElapsedMilliseconds,
                Error = error
            });
        }

        overallStopwatch.Stop();
        var actionResults = results.ToArray();

        return new RemediationResult
        {
            TotalActions = actionResults.Length,
            SuccessfulActions = actionResults.Count(r => r.Success),
            FailedActions = actionResults.Count(r => !r.Success),
            TotalDurationMs = overallStopwatch.ElapsedMilliseconds,
            ActionResults = actionResults
        };
    }

    /// <summary>
    /// Dispatches a single remediation action to the appropriate message bus topic.
    /// </summary>
    private async Task DispatchActionAsync(RemediationAction action, CancellationToken ct)
    {
        if (_messageBus == null)
        {
            // No bus available -- action is a no-op but considered successful
            // This supports offline/test scenarios where the bus is not wired
            return;
        }

        if (!ActionTopicMap.TryGetValue(action.ActionType, out var topic))
        {
            throw new InvalidOperationException($"No topic mapping for remediation action type '{action.ActionType}'");
        }

        var payload = new Dictionary<string, object>
        {
            ["targetId"] = action.TargetId,
            ["actionType"] = action.ActionType.ToString(),
            ["priority"] = action.Priority,
            ["timeoutMs"] = action.TimeoutMs
        };

        // For Custom actions, merge additional parameters into the payload
        if (action.ActionType == RemediationActionType.Custom && action.Parameters.Count > 0)
        {
            foreach (var kvp in action.Parameters)
            {
                // Prefix custom parameters to avoid collision with standard fields
                if (PluginMessage.IsAllowedPayloadType(kvp.Value))
                {
                    payload[$"custom.{kvp.Key}"] = kvp.Value;
                }
            }
        }

        var message = new PluginMessage
        {
            Type = $"remediation.{action.ActionType.ToString().ToLowerInvariant()}",
            SourcePluginId = "chaos-vaccination",
            Payload = payload
        };

        await _messageBus.PublishAsync(topic, message, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Aggregate result of executing a set of remediation actions.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Immune response system")]
public record RemediationResult
{
    /// <summary>Total number of actions attempted.</summary>
    public required int TotalActions { get; init; }

    /// <summary>Number of actions that completed successfully.</summary>
    public required int SuccessfulActions { get; init; }

    /// <summary>Number of actions that failed or timed out.</summary>
    public required int FailedActions { get; init; }

    /// <summary>Total wall-clock time in milliseconds for all actions.</summary>
    public required long TotalDurationMs { get; init; }

    /// <summary>Per-action results in execution order.</summary>
    public required ActionResult[] ActionResults { get; init; }
}

/// <summary>
/// Result of executing a single remediation action.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Immune response system")]
public record ActionResult
{
    /// <summary>The action that was executed.</summary>
    public required RemediationAction Action { get; init; }

    /// <summary>Whether the action completed successfully.</summary>
    public required bool Success { get; init; }

    /// <summary>Duration in milliseconds for this action.</summary>
    public required long DurationMs { get; init; }

    /// <summary>Error description if the action failed. Null on success.</summary>
    public string? Error { get; init; }
}
