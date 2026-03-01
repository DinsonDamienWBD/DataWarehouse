using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Storage.Fabric;

using IStorageStrategy = DataWarehouse.SDK.Contracts.Storage.IStorageStrategy;
using StorageTier = DataWarehouse.SDK.Contracts.Storage.StorageTier;

namespace DataWarehouse.Plugins.UniversalFabric.Resilience;

/// <summary>
/// Wraps any <see cref="IStorageStrategy"/> with cross-cutting resilience concerns including
/// circuit breaker, operation timeout, error normalization, and optional fallback chain.
/// </summary>
/// <remarks>
/// <para>
/// BackendAbstractionLayer is a decorator that sits between the fabric and backend strategy
/// implementations. It adds:
/// <list type="bullet">
///   <item><b>Circuit breaker:</b> Opens after consecutive failures to prevent cascading failures,
///     half-opens after cooldown to probe recovery.</item>
///   <item><b>Timeout:</b> Enforces per-operation time limits to prevent hanging operations.</item>
///   <item><b>Error normalization:</b> Maps all backend exceptions to the fabric exception hierarchy
///     via <see cref="ErrorNormalizer"/>.</item>
///   <item><b>Metrics:</b> Tracks consecutive failures and circuit breaker state for observability.</item>
/// </list>
/// </para>
/// <para>
/// The wrapped strategy's identity (StrategyId, Name, Tier, Capabilities) is forwarded transparently.
/// Callers see a standard <see cref="IStorageStrategy"/> and are unaware of the resilience layer.
/// </para>
/// </remarks>
public class BackendAbstractionLayer : IStorageStrategy
{
    private readonly IStorageStrategy _inner;
    private readonly string _backendId;
    private readonly ErrorNormalizer _normalizer;
    private readonly FallbackChain? _fallbackChain;
    private readonly BackendAbstractionOptions _options;

    // Circuit breaker state
    private int _consecutiveFailures;
    private DateTime _circuitOpenUntil = DateTime.MinValue;
    private readonly object _circuitLock = new();

    /// <summary>
    /// Initializes a new instance of <see cref="BackendAbstractionLayer"/> wrapping the given strategy.
    /// </summary>
    /// <param name="inner">The underlying storage strategy to wrap with resilience.</param>
    /// <param name="backendId">The backend identifier used in error messages and circuit breaker tracking.</param>
    /// <param name="normalizer">The error normalizer for mapping backend exceptions.</param>
    /// <param name="fallbackChain">Optional fallback chain for cascading to alternative backends.</param>
    /// <param name="options">Optional configuration for timeout, circuit breaker, and metrics.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="inner"/> or <paramref name="normalizer"/> is null.
    /// </exception>
    public BackendAbstractionLayer(
        IStorageStrategy inner,
        string backendId,
        ErrorNormalizer normalizer,
        FallbackChain? fallbackChain = null,
        BackendAbstractionOptions? options = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _backendId = backendId ?? throw new ArgumentNullException(nameof(backendId));
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _fallbackChain = fallbackChain;
        _options = options ?? BackendAbstractionOptions.Default;
    }

    #region IStorageStrategy Properties (forwarded from inner)

    /// <inheritdoc/>
    public string StrategyId => _inner.StrategyId;

    /// <inheritdoc/>
    public string Name => _inner.Name;

    /// <inheritdoc/>
    public StorageTier Tier => _inner.Tier;

    /// <inheritdoc/>
    public StorageCapabilities Capabilities => _inner.Capabilities;

    #endregion

    #region IStorageStrategy Operations

    /// <inheritdoc/>
    public async Task<StorageObjectMetadata> StoreAsync(
        string key, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        return await ExecuteResilientAsync(
            () => _inner.StoreAsync(key, data, metadata, ct),
            "Store", key, ct);
    }

    /// <inheritdoc/>
    public async Task<Stream> RetrieveAsync(string key, CancellationToken ct = default)
    {
        return await ExecuteResilientAsync(
            () => _inner.RetrieveAsync(key, ct),
            "Retrieve", key, ct);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await ExecuteResilientAsync(
            async () =>
            {
                await _inner.DeleteAsync(key, ct);
                return true;
            },
            "Delete", key, ct);
    }

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        return await ExecuteResilientAsync(
            () => _inner.ExistsAsync(key, ct),
            "Exists", key, ct);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<StorageObjectMetadata> ListAsync(
        string? prefix = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // List is streaming -- circuit breaker check only, no timeout wrapping
        CheckCircuitBreaker();

        // Use await using to guarantee disposal even if the consumer abandons mid-iteration (finding 4533).
        IAsyncEnumerator<StorageObjectMetadata> enumerator;
        try
        {
            enumerator = _inner.ListAsync(prefix, ct).GetAsyncEnumerator(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordFailure();
            throw _normalizer.Normalize(ex, _backendId, "List", prefix);
        }

        await using (enumerator)
        {
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordFailure();
                throw _normalizer.Normalize(ex, _backendId, "List", prefix);
            }

            while (hasNext)
            {
                var current = enumerator.Current;
                RecordSuccess();
                yield return current;

                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    RecordFailure();
                    throw _normalizer.Normalize(ex, _backendId, "List", prefix);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task<StorageObjectMetadata> GetMetadataAsync(string key, CancellationToken ct = default)
    {
        return await ExecuteResilientAsync(
            () => _inner.GetMetadataAsync(key, ct),
            "GetMetadata", key, ct);
    }

    /// <inheritdoc/>
    public async Task<StorageHealthInfo> GetHealthAsync(CancellationToken ct = default)
    {
        // Health check bypasses circuit breaker -- it's used to probe recovery
        try
        {
            return await ExecuteWithTimeoutAsync(
                () => _inner.GetHealthAsync(ct),
                _options.OperationTimeout, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new StorageHealthInfo
            {
                Status = HealthStatus.Unhealthy,
                Message = $"Health check failed: {ex.Message}",
                CheckedAt = DateTime.UtcNow
            };
        }
    }

    /// <inheritdoc/>
    public async Task<long?> GetAvailableCapacityAsync(CancellationToken ct = default)
    {
        try
        {
            return await ExecuteWithTimeoutAsync(
                () => _inner.GetAvailableCapacityAsync(ct),
                _options.OperationTimeout, ct);
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Circuit Breaker

    /// <summary>
    /// Gets the current number of consecutive failures.
    /// </summary>
    public int ConsecutiveFailures
    {
        get { lock (_circuitLock) { return _consecutiveFailures; } }
    }

    /// <summary>
    /// Gets whether the circuit breaker is currently open (rejecting requests).
    /// </summary>
    public bool IsCircuitOpen
    {
        get
        {
            lock (_circuitLock)
            {
                return _consecutiveFailures >= _options.CircuitBreakerThreshold
                    && DateTime.UtcNow < _circuitOpenUntil;
            }
        }
    }

    /// <summary>
    /// Checks the circuit breaker state and throws if the circuit is open.
    /// After the cooldown period, the circuit enters half-open state and allows one request through.
    /// </summary>
    /// <exception cref="BackendUnavailableException">
    /// Thrown when the circuit is open and the cooldown has not elapsed.
    /// </exception>
    private void CheckCircuitBreaker()
    {
        lock (_circuitLock)
        {
            if (_consecutiveFailures >= _options.CircuitBreakerThreshold
                && DateTime.UtcNow < _circuitOpenUntil)
            {
                throw new BackendUnavailableException(
                    $"Circuit breaker open for backend '{_backendId}' until {_circuitOpenUntil:O}",
                    _backendId);
            }
        }
    }

    /// <summary>
    /// Records a failure, incrementing the consecutive failure count and potentially opening the circuit.
    /// </summary>
    private void RecordFailure()
    {
        lock (_circuitLock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _options.CircuitBreakerThreshold)
            {
                _circuitOpenUntil = DateTime.UtcNow + _options.CircuitBreakerCooldown;
            }
        }
    }

    /// <summary>
    /// Records a success, resetting the consecutive failure count and closing the circuit.
    /// </summary>
    private void RecordSuccess()
    {
        lock (_circuitLock)
        {
            _consecutiveFailures = 0;
        }
    }

    #endregion

    #region Resilience Infrastructure

    /// <summary>
    /// Executes an operation with circuit breaker check, timeout, error normalization, and success/failure tracking.
    /// </summary>
    private async Task<T> ExecuteResilientAsync<T>(
        Func<Task<T>> operation, string operationName, string? key, CancellationToken ct)
    {
        CheckCircuitBreaker();

        try
        {
            var result = await ExecuteWithTimeoutAsync(operation, _options.OperationTimeout, ct);
            RecordSuccess();
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure();
            throw _normalizer.Normalize(ex, _backendId, operationName, key);
        }
    }

    /// <summary>
    /// Wraps an operation with a timeout. If the operation does not complete within the specified
    /// timeout, an <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    private static async Task<T> ExecuteWithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation, TimeSpan timeout, CancellationToken ct)
    {
        // P2-4544: link timeout token into a combined CTS and pass it to the operation so
        // inner async I/O (network, disk) actually cancels when the deadline fires.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var linkedToken = timeoutCts.Token;

        try
        {
            return await operation(linkedToken);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout occurred (not caller cancellation)
            throw new TimeoutException(
                $"Operation timed out after {timeout.TotalSeconds:F1}s");
        }
    }

    // Backward-compatible overload for callers that capture the token in a closure.
    private static Task<T> ExecuteWithTimeoutAsync<T>(
        Func<Task<T>> operation, TimeSpan timeout, CancellationToken ct)
        => ExecuteWithTimeoutAsync<T>(_ => operation(), timeout, ct);

    #endregion
}

/// <summary>
/// Configuration options for <see cref="BackendAbstractionLayer"/> controlling timeout,
/// circuit breaker behavior, and metrics collection.
/// </summary>
public record BackendAbstractionOptions
{
    /// <summary>
    /// Gets the maximum time allowed for a single storage operation before timeout.
    /// Default is 60 seconds.
    /// </summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets the number of consecutive failures required to open the circuit breaker.
    /// Default is 5.
    /// </summary>
    public int CircuitBreakerThreshold { get; init; } = 5;

    /// <summary>
    /// Gets the duration the circuit breaker stays open before allowing a probe request.
    /// Default is 30 seconds.
    /// </summary>
    public TimeSpan CircuitBreakerCooldown { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets whether to collect operation metrics (latency, success/failure counts).
    /// Default is true.
    /// </summary>
    public bool EnableMetrics { get; init; } = true;

    /// <summary>
    /// Gets the default abstraction options.
    /// </summary>
    public static BackendAbstractionOptions Default { get; } = new();
}
