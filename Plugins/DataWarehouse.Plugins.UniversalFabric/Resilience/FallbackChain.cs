using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Storage.Fabric;

using IStorageStrategy = DataWarehouse.SDK.Contracts.Storage.IStorageStrategy;

namespace DataWarehouse.Plugins.UniversalFabric.Resilience;

/// <summary>
/// Provides ordered fallback across alternative storage backends when the primary backend fails.
/// When an operation fails on the primary backend with a fallback-worthy error, the chain
/// tries alternative backends of the same tier in priority order.
/// </summary>
/// <remarks>
/// <para>
/// FallbackChain works with <see cref="IBackendRegistry"/> to discover alternative backends
/// and with <see cref="ErrorNormalizer"/> to determine whether a failure warrants fallback.
/// Only <see cref="BackendUnavailableException"/> triggers fallback -- other errors (not found,
/// access denied, validation) are surfaced immediately since switching backends won't help.
/// </para>
/// <para>
/// Fallback behavior is controlled by <see cref="FallbackOptions"/>:
/// <list type="bullet">
///   <item><see cref="FallbackOptions.MaxFallbackAttempts"/> limits how many alternatives are tried</item>
///   <item><see cref="FallbackOptions.AllowCrossTierFallback"/> enables falling back to backends on different tiers</item>
///   <item><see cref="FallbackOptions.AllowCrossRegionFallback"/> enables falling back to backends in different regions</item>
/// </list>
/// </para>
/// </remarks>
public class FallbackChain
{
    private readonly IBackendRegistry _registry;
    private readonly ErrorNormalizer _normalizer;

    /// <summary>
    /// Initializes a new instance of <see cref="FallbackChain"/> with the specified backend registry.
    /// </summary>
    /// <param name="registry">The backend registry used to discover alternative backends.</param>
    /// <param name="normalizer">Optional error normalizer. If null, a default instance is created.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="registry"/> is null.</exception>
    public FallbackChain(IBackendRegistry registry, ErrorNormalizer? normalizer = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _normalizer = normalizer ?? new ErrorNormalizer();
    }

    /// <summary>
    /// Executes an operation against the primary backend, falling back to alternatives on failure.
    /// Tries the primary backend first, then iterates through same-tier alternatives ordered by priority.
    /// </summary>
    /// <typeparam name="T">The return type of the storage operation.</typeparam>
    /// <param name="primaryBackendId">The ID of the preferred backend to try first.</param>
    /// <param name="operation">
    /// The storage operation to execute, receiving an <see cref="IStorageStrategy"/> and returning a result.
    /// </param>
    /// <param name="options">Optional fallback configuration. If null, defaults are used.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result from the first backend that succeeds.</returns>
    /// <exception cref="BackendNotFoundException">
    /// Thrown when the primary backend is not registered in the registry.
    /// </exception>
    /// <exception cref="BackendUnavailableException">
    /// Thrown when the primary backend and all fallback alternatives fail.
    /// </exception>
    public async Task<T> ExecuteWithFallbackAsync<T>(
        string primaryBackendId,
        Func<IStorageStrategy, Task<T>> operation,
        FallbackOptions? options = null,
        CancellationToken ct = default)
    {
        var opts = options ?? FallbackOptions.Default;

        var primary = _registry.GetStrategy(primaryBackendId)
            ?? throw new BackendNotFoundException(
                $"Primary backend '{primaryBackendId}' not found in registry",
                primaryBackendId, null);

        // Try primary backend first
        Exception primaryException;
        try
        {
            return await operation(primary);
        }
        catch (Exception ex) when (_normalizer.ShouldFallback(ex))
        {
            primaryException = ex;
        }

        // Primary failed with fallback-worthy error; find alternatives
        ct.ThrowIfCancellationRequested();

        var primaryDescriptor = _registry.GetById(primaryBackendId);
        if (primaryDescriptor == null)
        {
            throw _normalizer.Normalize(primaryException, primaryBackendId, "fallback");
        }

        var alternatives = FindAlternatives(primaryDescriptor, opts);
        var attemptsRemaining = opts.MaxFallbackAttempts;

        foreach (var alt in alternatives)
        {
            if (attemptsRemaining <= 0) break;
            attemptsRemaining--;

            ct.ThrowIfCancellationRequested();

            var altStrategy = _registry.GetStrategy(alt.BackendId);
            if (altStrategy == null) continue;

            try
            {
                return await operation(altStrategy);
            }
            catch
            {
                // Try next fallback
                continue;
            }
        }

        // All fallbacks exhausted
        throw _normalizer.Normalize(primaryException, primaryBackendId, "fallback");
    }

    /// <summary>
    /// Executes a void operation against the primary backend with fallback support.
    /// </summary>
    /// <param name="primaryBackendId">The ID of the preferred backend to try first.</param>
    /// <param name="operation">
    /// The void storage operation to execute, receiving an <see cref="IStorageStrategy"/>.
    /// </param>
    /// <param name="options">Optional fallback configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ExecuteWithFallbackAsync(
        string primaryBackendId,
        Func<IStorageStrategy, Task> operation,
        FallbackOptions? options = null,
        CancellationToken ct = default)
    {
        await ExecuteWithFallbackAsync<bool>(
            primaryBackendId,
            async strategy =>
            {
                await operation(strategy);
                return true;
            },
            options, ct);
    }

    /// <summary>
    /// Finds alternative backends based on the primary backend's descriptor and fallback options.
    /// By default, only same-tier backends are considered. Cross-tier and cross-region fallback
    /// can be enabled via <see cref="FallbackOptions"/>.
    /// </summary>
    private IOrderedEnumerable<BackendDescriptor> FindAlternatives(
        BackendDescriptor primary, FallbackOptions opts)
    {
        IEnumerable<BackendDescriptor> candidates;

        if (opts.AllowCrossTierFallback)
        {
            // All backends except the failed one
            candidates = _registry.All
                .Where(b => b.BackendId != primary.BackendId);
        }
        else
        {
            // Same tier only
            candidates = _registry.FindByTier(primary.Tier)
                .Where(b => b.BackendId != primary.BackendId);
        }

        if (!opts.AllowCrossRegionFallback && primary.Region != null)
        {
            candidates = candidates.Where(b =>
                b.Region == null || string.Equals(b.Region, primary.Region, StringComparison.OrdinalIgnoreCase));
        }

        return candidates.OrderBy(b => b.Priority);
    }
}

/// <summary>
/// Configuration options controlling fallback behavior when a primary backend fails.
/// </summary>
public record FallbackOptions
{
    /// <summary>
    /// Gets the maximum number of alternative backends to try before giving up.
    /// Default is 3.
    /// </summary>
    public int MaxFallbackAttempts { get; init; } = 3;

    /// <summary>
    /// Gets whether fallback to backends on a different storage tier is allowed.
    /// When false (default), only backends on the same tier as the primary are considered.
    /// </summary>
    public bool AllowCrossTierFallback { get; init; }

    /// <summary>
    /// Gets whether fallback to backends in a different geographic region is allowed.
    /// When false (default), only backends in the same region as the primary are considered.
    /// </summary>
    public bool AllowCrossRegionFallback { get; init; }

    /// <summary>
    /// Gets the default fallback options (3 attempts, same tier, same region).
    /// </summary>
    public static FallbackOptions Default { get; } = new();
}
