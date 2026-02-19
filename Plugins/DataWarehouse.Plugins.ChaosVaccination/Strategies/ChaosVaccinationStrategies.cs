using System;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.ChaosVaccination;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.ChaosVaccination.Engine;
using DataWarehouse.Plugins.ChaosVaccination.ImmuneResponse;
using DataWarehouse.Plugins.ChaosVaccination.BlastRadius;

namespace DataWarehouse.Plugins.ChaosVaccination.Strategies;

/// <summary>
/// Abstract base for chaos vaccination strategies. Each strategy wraps an operation
/// with a specific resilience behavior local to the chaos vaccination system.
/// These do NOT inherit from UltimateResilience's ResilienceStrategyBase (which is in a
/// separate plugin assembly). Instead, they provide a self-contained ExecuteAsync pattern.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination orchestrator")]
public abstract class ChaosVaccinationStrategyBase
{
    /// <summary>
    /// The strategy name for identification and logging.
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// The category this strategy belongs to.
    /// </summary>
    public string Category => "ChaosVaccination";

    /// <summary>
    /// Executes the given operation with the strategy's resilience behavior applied.
    /// </summary>
    /// <typeparam name="T">The return type of the operation.</typeparam>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    public abstract Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default);
}

/// <summary>
/// Wraps experiment execution with a local circuit breaker: if too many experiments fail
/// in succession, the strategy trips open and rejects further experiment runs until a
/// cooldown period elapses. This prevents cascading failures from run-away experiment schedules.
///
/// Circuit breaker states: Closed -> Open (after N failures) -> HalfOpen (after cooldown) -> Closed (on success).
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination orchestrator")]
public sealed class VaccinationRunStrategy : ChaosVaccinationStrategyBase
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldownPeriod;
    private int _consecutiveFailures;
    private DateTimeOffset _openedAt;
    private volatile CircuitState _state = CircuitState.Closed;
    private readonly object _lock = new();

    private enum CircuitState { Closed, Open, HalfOpen }

    /// <inheritdoc />
    public override string Name => "VaccinationRunStrategy";

    /// <summary>
    /// Creates a new VaccinationRunStrategy.
    /// </summary>
    /// <param name="failureThreshold">Number of consecutive failures before tripping open. Default: 3.</param>
    /// <param name="cooldownSeconds">Seconds to wait before transitioning from Open to HalfOpen. Default: 60.</param>
    public VaccinationRunStrategy(int failureThreshold = 3, int cooldownSeconds = 60)
    {
        _failureThreshold = failureThreshold > 0 ? failureThreshold : 3;
        _cooldownPeriod = TimeSpan.FromSeconds(cooldownSeconds > 0 ? cooldownSeconds : 60);
    }

    /// <inheritdoc />
    public override async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            switch (_state)
            {
                case CircuitState.Open:
                    if (DateTimeOffset.UtcNow - _openedAt >= _cooldownPeriod)
                    {
                        _state = CircuitState.HalfOpen;
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"VaccinationRunStrategy circuit breaker is OPEN after {_consecutiveFailures} consecutive failures. " +
                            $"Cooldown expires at {_openedAt + _cooldownPeriod:O}.");
                    }
                    break;

                case CircuitState.HalfOpen:
                    // Allow one probe request through
                    break;
            }
        }

        try
        {
            var result = await operation(ct).ConfigureAwait(false);

            lock (_lock)
            {
                _consecutiveFailures = 0;
                _state = CircuitState.Closed;
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw; // Cancellation is not a failure
        }
        catch
        {
            lock (_lock)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= _failureThreshold)
                {
                    _state = CircuitState.Open;
                    _openedAt = DateTimeOffset.UtcNow;
                }
            }
            throw;
        }
    }

    /// <summary>
    /// Gets the current circuit state for diagnostics.
    /// </summary>
    public string CurrentState => _state.ToString();

    /// <summary>
    /// Gets the current consecutive failure count.
    /// </summary>
    public int ConsecutiveFailures => _consecutiveFailures;
}

/// <summary>
/// Wraps any operation with immune system recognition: on failure, checks immune memory
/// for a matching fault signature and applies automatic remediation if available.
/// If remediation succeeds, retries the operation once. If no memory match or remediation fails,
/// the original exception propagates.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination orchestrator")]
public sealed class ImmuneAutoRemediationStrategy : ChaosVaccinationStrategyBase
{
    private readonly ImmuneResponseSystem _immuneSystem;
    private readonly FaultSignatureAnalyzer _signatureAnalyzer;

    /// <inheritdoc />
    public override string Name => "ImmuneAutoRemediationStrategy";

    /// <summary>
    /// Creates a new ImmuneAutoRemediationStrategy.
    /// </summary>
    /// <param name="immuneSystem">The immune response system for fault recognition and remediation.</param>
    /// <param name="signatureAnalyzer">The fault signature analyzer for generating signatures from errors.</param>
    public ImmuneAutoRemediationStrategy(ImmuneResponseSystem immuneSystem, FaultSignatureAnalyzer signatureAnalyzer)
    {
        _immuneSystem = immuneSystem ?? throw new ArgumentNullException(nameof(immuneSystem));
        _signatureAnalyzer = signatureAnalyzer ?? throw new ArgumentNullException(nameof(signatureAnalyzer));
    }

    /// <inheritdoc />
    public override async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            return await operation(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Generate a signature from the failure
            var signature = _signatureAnalyzer.GenerateSignatureFromEvent(
                pluginId: "operation-executor",
                nodeId: "local",
                type: FaultType.Custom,
                errorPattern: ex.GetType().Name);

            // Check immune memory for a match
            var memoryEntry = await _immuneSystem.RecognizeFaultAsync(signature, ct).ConfigureAwait(false);
            if (memoryEntry == null)
            {
                throw; // No immune memory -- propagate original failure
            }

            // Apply remediation
            var remediationSuccess = await _immuneSystem.ApplyRemediationAsync(memoryEntry, ct).ConfigureAwait(false);
            if (!remediationSuccess)
            {
                throw; // Remediation failed -- propagate original failure
            }

            // Retry the operation once after successful remediation
            return await operation(ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Wraps operation execution with blast radius monitoring: creates a temporary isolation zone
/// before running the operation, monitors for blast radius breaches during execution, and
/// releases the zone after completion (in finally). If a breach is detected during execution,
/// the operation is aborted.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination orchestrator")]
public sealed class BlastRadiusGuardStrategy : ChaosVaccinationStrategyBase
{
    private readonly BlastRadiusEnforcer _enforcer;
    private readonly string[] _targetPlugins;
    private readonly BlastRadiusLevel _maxLevel;

    /// <inheritdoc />
    public override string Name => "BlastRadiusGuardStrategy";

    /// <summary>
    /// Creates a new BlastRadiusGuardStrategy.
    /// </summary>
    /// <param name="enforcer">The blast radius enforcer for zone management.</param>
    /// <param name="targetPlugins">Plugin IDs to isolate within the guard zone.</param>
    /// <param name="maxLevel">Maximum blast radius level for the guard zone. Default: SinglePlugin.</param>
    public BlastRadiusGuardStrategy(
        BlastRadiusEnforcer enforcer,
        string[] targetPlugins,
        BlastRadiusLevel maxLevel = BlastRadiusLevel.SinglePlugin)
    {
        _enforcer = enforcer ?? throw new ArgumentNullException(nameof(enforcer));
        _targetPlugins = targetPlugins ?? throw new ArgumentNullException(nameof(targetPlugins));
        _maxLevel = maxLevel;

        if (targetPlugins.Length == 0)
            throw new ArgumentException("At least one target plugin is required.", nameof(targetPlugins));
    }

    /// <inheritdoc />
    public override async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var policy = new BlastRadiusPolicy
        {
            MaxLevel = _maxLevel,
            MaxAffectedPlugins = _targetPlugins.Length,
            MaxAffectedNodes = 1,
            MaxDurationMs = 120_000, // 2-minute guard zone
            AutoAbortOnBreach = true,
            IsolationStrategy = IsolationStrategy.CircuitBreaker
        };

        var zone = await _enforcer.CreateIsolationZoneAsync(policy, _targetPlugins, ct).ConfigureAwait(false);

        try
        {
            // Execute the operation within the isolation zone
            var result = await operation(ct).ConfigureAwait(false);

            // Enforce -- check for breaches
            var enforcement = await _enforcer.EnforceAsync(zone.ZoneId, ct).ConfigureAwait(false);
            if (!enforcement.Contained)
            {
                throw new InvalidOperationException(
                    $"Blast radius breach detected in guard zone '{zone.ZoneId}': " +
                    $"actual radius {enforcement.ActualRadius}, max {_maxLevel}. " +
                    $"Breached plugins: {string.Join(", ", enforcement.BreachedPlugins)}");
            }

            return result;
        }
        finally
        {
            // Always release the zone
            try
            {
                await _enforcer.ReleaseZoneAsync(zone.ZoneId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Zone release is best-effort; failure must not mask the original exception
            }
        }
    }
}
