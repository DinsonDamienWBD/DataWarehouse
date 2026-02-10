// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using DataWarehouse.SDK.Infrastructure;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.TamperProof.Services;

/// <summary>
/// Event arguments for degradation state transitions.
/// </summary>
public class StateChangedEventArgs : EventArgs
{
    /// <summary>Identifier of the storage instance whose state changed.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Previous degradation state.</summary>
    public required InstanceDegradationState OldState { get; init; }

    /// <summary>New degradation state.</summary>
    public required InstanceDegradationState NewState { get; init; }

    /// <summary>UTC timestamp of the state transition.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Reason for the state transition.</summary>
    public required string Reason { get; init; }

    /// <summary>Whether this transition was performed via admin override.</summary>
    public bool IsAdminOverride { get; init; }

    /// <summary>Principal who performed admin override (null if automatic transition).</summary>
    public string? AdminPrincipal { get; init; }
}

/// <summary>
/// Manages degradation state transitions for tamper-proof storage instances.
/// Provides centralized state machine with valid transition enforcement,
/// auto-detection from health checks, admin override capability, and event notifications.
/// </summary>
internal class DegradationStateService
{
    private readonly ConcurrentDictionary<string, InstanceDegradationState> _instanceStates = new();
    private readonly ILogger<DegradationStateService> _logger;

    /// <summary>
    /// Valid state transitions. Maps (currentState) to allowed target states.
    /// Admin overrides bypass these restrictions.
    /// </summary>
    private static readonly Dictionary<InstanceDegradationState, HashSet<InstanceDegradationState>> ValidTransitions = new()
    {
        [InstanceDegradationState.Healthy] = new HashSet<InstanceDegradationState>
        {
            InstanceDegradationState.Degraded,
            InstanceDegradationState.Offline,
            InstanceDegradationState.Corrupted
        },
        [InstanceDegradationState.Degraded] = new HashSet<InstanceDegradationState>
        {
            InstanceDegradationState.Healthy,
            InstanceDegradationState.DegradedReadOnly,
            InstanceDegradationState.DegradedNoRecovery,
            InstanceDegradationState.Offline,
            InstanceDegradationState.Corrupted
        },
        [InstanceDegradationState.DegradedReadOnly] = new HashSet<InstanceDegradationState>
        {
            InstanceDegradationState.Healthy,
            InstanceDegradationState.Degraded,
            InstanceDegradationState.DegradedNoRecovery,
            InstanceDegradationState.Offline,
            InstanceDegradationState.Corrupted
        },
        [InstanceDegradationState.DegradedNoRecovery] = new HashSet<InstanceDegradationState>
        {
            InstanceDegradationState.Healthy,
            InstanceDegradationState.Degraded,
            InstanceDegradationState.DegradedReadOnly,
            InstanceDegradationState.Offline,
            InstanceDegradationState.Corrupted
        },
        [InstanceDegradationState.Offline] = new HashSet<InstanceDegradationState>
        {
            InstanceDegradationState.Healthy,
            InstanceDegradationState.Degraded,
            InstanceDegradationState.Corrupted
        },
        [InstanceDegradationState.Corrupted] = new HashSet<InstanceDegradationState>
        {
            // Corrupted can only return to Healthy or Offline via admin override.
            // Normal transitions from Corrupted are not allowed.
        }
    };

    /// <summary>
    /// Event raised when an instance state changes.
    /// </summary>
    public event EventHandler<StateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Creates a new degradation state service instance.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public DegradationStateService(ILogger<DegradationStateService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Transitions a storage instance to a new degradation state.
    /// Validates the transition against the state machine rules.
    /// </summary>
    /// <param name="instanceId">Identifier of the storage instance.</param>
    /// <param name="newState">Target degradation state.</param>
    /// <param name="reason">Reason for the state transition.</param>
    /// <returns>True if the transition was valid and applied; false if rejected.</returns>
    /// <exception cref="ArgumentNullException">Thrown if instanceId is null or empty.</exception>
    public Task<bool> TransitionStateAsync(string instanceId, InstanceDegradationState newState, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId, nameof(instanceId));

        var currentState = _instanceStates.GetOrAdd(instanceId, InstanceDegradationState.Healthy);

        if (currentState == newState)
        {
            _logger.LogDebug(
                "Instance {InstanceId} already in state {State}, no transition needed",
                instanceId, newState);
            return Task.FromResult(true);
        }

        // Validate transition
        if (!ValidTransitions.TryGetValue(currentState, out var allowedTargets) ||
            !allowedTargets.Contains(newState))
        {
            _logger.LogWarning(
                "Invalid state transition for instance {InstanceId}: {OldState} -> {NewState}. Use AdminOverrideStateAsync for forced transitions.",
                instanceId, currentState, newState);
            return Task.FromResult(false);
        }

        // Apply transition
        _instanceStates[instanceId] = newState;

        var transitionReason = reason ?? $"Automatic transition from {currentState} to {newState}";

        _logger.LogInformation(
            "Instance {InstanceId} state transition: {OldState} -> {NewState}. Reason: {Reason}",
            instanceId, currentState, newState, transitionReason);

        OnStateChanged(new StateChangedEventArgs
        {
            InstanceId = instanceId,
            OldState = currentState,
            NewState = newState,
            Timestamp = DateTimeOffset.UtcNow,
            Reason = transitionReason,
            IsAdminOverride = false
        });

        return Task.FromResult(true);
    }

    /// <summary>
    /// Gets the current degradation state of a storage instance.
    /// Returns Healthy if the instance has not been registered.
    /// </summary>
    /// <param name="instanceId">Identifier of the storage instance.</param>
    /// <returns>Current degradation state.</returns>
    /// <exception cref="ArgumentNullException">Thrown if instanceId is null or empty.</exception>
    public InstanceDegradationState GetCurrentState(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId, nameof(instanceId));

        return _instanceStates.GetOrAdd(instanceId, InstanceDegradationState.Healthy);
    }

    /// <summary>
    /// Forces a state transition via administrative override.
    /// Bypasses normal transition validation rules, allowing any state change including
    /// recovery from Corrupted state. All admin overrides are logged for audit purposes.
    /// </summary>
    /// <param name="instanceId">Identifier of the storage instance.</param>
    /// <param name="newState">Target degradation state.</param>
    /// <param name="adminPrincipal">Identity of the administrator performing the override.</param>
    /// <param name="reason">Reason for the administrative override.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown if instanceId, adminPrincipal, or reason is null or empty.</exception>
    public Task AdminOverrideStateAsync(
        string instanceId,
        InstanceDegradationState newState,
        string adminPrincipal,
        string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId, nameof(instanceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(adminPrincipal, nameof(adminPrincipal));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason, nameof(reason));

        var currentState = _instanceStates.GetOrAdd(instanceId, InstanceDegradationState.Healthy);

        // Admin override always succeeds regardless of transition rules
        _instanceStates[instanceId] = newState;

        _logger.LogWarning(
            "ADMIN OVERRIDE: Instance {InstanceId} state forced: {OldState} -> {NewState} by {Admin}. Reason: {Reason}",
            instanceId, currentState, newState, adminPrincipal, reason);

        OnStateChanged(new StateChangedEventArgs
        {
            InstanceId = instanceId,
            OldState = currentState,
            NewState = newState,
            Timestamp = DateTimeOffset.UtcNow,
            Reason = $"[Admin Override by {adminPrincipal}] {reason}",
            IsAdminOverride = true,
            AdminPrincipal = adminPrincipal
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Auto-detects the appropriate degradation state based on health check results.
    /// Evaluates provider health metrics to determine if a state transition is needed.
    /// </summary>
    /// <param name="instanceId">Identifier of the storage instance.</param>
    /// <param name="healthCheck">Health check result to evaluate.</param>
    /// <returns>The detected state. A transition is attempted if the detected state differs from current.</returns>
    /// <exception cref="ArgumentNullException">Thrown if instanceId is null or empty, or healthCheck is null.</exception>
    public async Task<InstanceDegradationState> DetectStateAsync(
        string instanceId,
        HealthCheckResult healthCheck)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId, nameof(instanceId));
        ArgumentNullException.ThrowIfNull(healthCheck, nameof(healthCheck));

        var detectedState = MapHealthToState(healthCheck);
        var currentState = GetCurrentState(instanceId);

        if (detectedState != currentState)
        {
            _logger.LogInformation(
                "Health check auto-detection for instance {InstanceId}: detected {DetectedState} (current: {CurrentState})",
                instanceId, detectedState, currentState);

            var transitioned = await TransitionStateAsync(
                instanceId,
                detectedState,
                $"Auto-detected from health check: Status={healthCheck.Status}, Message={healthCheck.Message ?? "none"}");

            if (!transitioned)
            {
                _logger.LogWarning(
                    "Auto-detected state {DetectedState} for instance {InstanceId} could not be applied " +
                    "(invalid transition from {CurrentState}). Manual intervention may be required.",
                    detectedState, instanceId, currentState);
            }
        }

        return detectedState;
    }

    /// <summary>
    /// Maps a health check result to the appropriate degradation state.
    /// </summary>
    private static InstanceDegradationState MapHealthToState(HealthCheckResult healthCheck)
    {
        // Check for specific conditions in health check data
        var hasWormAccess = !healthCheck.Data.ContainsKey("WormReachable") ||
                            healthCheck.Data["WormReachable"] is true;
        var hasRecoveryCapability = !healthCheck.Data.ContainsKey("RecoveryAvailable") ||
                                    healthCheck.Data["RecoveryAvailable"] is true;
        var isReadOnly = healthCheck.Data.ContainsKey("ReadOnly") &&
                         healthCheck.Data["ReadOnly"] is true;

        return healthCheck.Status switch
        {
            HealthStatus.Healthy when hasWormAccess && hasRecoveryCapability => InstanceDegradationState.Healthy,
            HealthStatus.Healthy when !hasRecoveryCapability => InstanceDegradationState.DegradedNoRecovery,
            HealthStatus.Degraded when isReadOnly => InstanceDegradationState.DegradedReadOnly,
            HealthStatus.Degraded when !hasWormAccess => InstanceDegradationState.DegradedNoRecovery,
            HealthStatus.Degraded => InstanceDegradationState.Degraded,
            HealthStatus.Unhealthy => InstanceDegradationState.Offline,
            _ => InstanceDegradationState.Degraded
        };
    }

    /// <summary>
    /// Raises the StateChanged event.
    /// </summary>
    private void OnStateChanged(StateChangedEventArgs e)
    {
        StateChanged?.Invoke(this, e);
    }
}
