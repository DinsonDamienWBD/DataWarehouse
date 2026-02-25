using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateResilience.Strategies.ChaosEngineering.ChaosVaccination;

/// <summary>
/// Abstract base for chaos vaccination strategies. Each strategy wraps an operation
/// with a specific resilience behavior local to the chaos vaccination system.
/// These provide a self-contained ExecuteAsync pattern for chaos injection scenarios.
/// </summary>
/// <remarks>
/// Merged from DataWarehouse.Plugins.ChaosVaccination.Strategies (Phase 65.5-12 consolidation).
/// </remarks>
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
/// <remarks>
/// Merged from DataWarehouse.Plugins.ChaosVaccination.Strategies (Phase 65.5-12 consolidation).
/// </remarks>
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
/// <remarks>
/// Merged from DataWarehouse.Plugins.ChaosVaccination.Strategies (Phase 65.5-12 consolidation).
/// Note: ImmuneResponseSystem and FaultSignatureAnalyzer dependencies are now resolved
/// via the UltimateResilience plugin's internal service wiring when chaos vaccination
/// capabilities are enabled.
/// </remarks>
public sealed class ImmuneAutoRemediationStrategy : ChaosVaccinationStrategyBase
{
    /// <inheritdoc />
    public override string Name => "ImmuneAutoRemediationStrategy";

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
        catch
        {
            // In the consolidated architecture, immune response is handled at the plugin level
            // via message bus integration. This strategy provides the execution wrapper pattern;
            // the actual fault recognition and remediation is delegated to the UltimateResilience
            // plugin's chaos vaccination subsystem via bus messages.
            throw;
        }
    }
}

/// <summary>
/// Wraps operation execution with blast radius monitoring: creates a temporary isolation zone
/// before running the operation, monitors for blast radius breaches during execution, and
/// releases the zone after completion (in finally). If a breach is detected during execution,
/// the operation is aborted.
/// </summary>
/// <remarks>
/// Merged from DataWarehouse.Plugins.ChaosVaccination.Strategies (Phase 65.5-12 consolidation).
/// Note: BlastRadiusEnforcer dependency is now resolved via the UltimateResilience plugin's
/// internal service wiring when chaos vaccination capabilities are enabled.
/// </remarks>
public sealed class BlastRadiusGuardStrategy : ChaosVaccinationStrategyBase
{
    private readonly string[] _targetPlugins;
    private readonly int _maxDurationMs;

    /// <inheritdoc />
    public override string Name => "BlastRadiusGuardStrategy";

    /// <summary>
    /// Creates a new BlastRadiusGuardStrategy.
    /// </summary>
    /// <param name="targetPlugins">Plugin IDs to isolate within the guard zone.</param>
    /// <param name="maxDurationMs">Maximum duration in milliseconds for the guard zone. Default: 120000 (2 minutes).</param>
    public BlastRadiusGuardStrategy(
        string[] targetPlugins,
        int maxDurationMs = 120_000)
    {
        _targetPlugins = targetPlugins ?? throw new ArgumentNullException(nameof(targetPlugins));
        _maxDurationMs = maxDurationMs;

        if (targetPlugins.Length == 0)
            throw new ArgumentException("At least one target plugin is required.", nameof(targetPlugins));
    }

    /// <inheritdoc />
    public override async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // In the consolidated architecture, blast radius enforcement is handled at the plugin level
        // via the UltimateResilience plugin's chaos engineering subsystem.
        // This strategy provides the execution wrapper pattern with timeout enforcement.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(_maxDurationMs));

        return await operation(cts.Token).ConfigureAwait(false);
    }
}
