using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy.Compatibility;

/// <summary>
/// Decorator around <see cref="IPolicyEngine"/> that blocks multi-level cascade resolution
/// until an administrator explicitly enables it. When multi-level is disabled (the default),
/// all path-based resolution is clamped to VDE level, making the cascade engine behave
/// identically to a flat single-level system.
/// <para>
/// This achieves MIGR-04 (no multi-level unless admin configures) and MIGR-05
/// (PolicyEngine transparent for existing deployments) without modifying
/// <see cref="PolicyResolutionEngine"/> itself.
/// </para>
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 81: Multi-level opt-in gate (MIGR-04, MIGR-05)")]
public sealed class PolicyCompatibilityGate : IPolicyEngine
{
    private readonly IPolicyEngine _inner;

    /// <summary>
    /// Gets whether multi-level cascade resolution is enabled.
    /// When false (default), all resolution is clamped to VDE level only.
    /// </summary>
    public bool IsMultiLevelEnabled { get; private set; }

    /// <summary>
    /// Initializes a new instance of <see cref="PolicyCompatibilityGate"/>.
    /// </summary>
    /// <param name="inner">The inner policy engine to decorate.</param>
    /// <param name="multiLevelEnabled">
    /// Whether multi-level cascade is initially enabled. Defaults to false,
    /// meaning existing deployments see single-level (VDE-only) behavior.
    /// </param>
    public PolicyCompatibilityGate(IPolicyEngine inner, bool multiLevelEnabled = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        IsMultiLevelEnabled = multiLevelEnabled;
    }

    /// <summary>
    /// Enables multi-level cascade resolution. After calling this method,
    /// policies at Container, Object, Chunk, and Block levels participate in resolution.
    /// </summary>
    public void EnableMultiLevel()
    {
        IsMultiLevelEnabled = true;
    }

    /// <summary>
    /// Disables multi-level cascade resolution, reverting to single-level (VDE-only) behavior.
    /// Existing deployments should call this to restore pre-v6.0 semantics.
    /// </summary>
    public void DisableMultiLevel()
    {
        IsMultiLevelEnabled = false;
    }

    /// <inheritdoc />
    public Task<IEffectivePolicy> ResolveAsync(string featureId, PolicyResolutionContext context, CancellationToken ct = default)
    {
        var effectiveContext = IsMultiLevelEnabled ? context : ClampContext(context);
        return _inner.ResolveAsync(featureId, effectiveContext, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, IEffectivePolicy>> ResolveAllAsync(PolicyResolutionContext context, CancellationToken ct = default)
    {
        var effectiveContext = IsMultiLevelEnabled ? context : ClampContext(context);
        return _inner.ResolveAllAsync(effectiveContext, ct);
    }

    /// <inheritdoc />
    public Task<OperationalProfile> GetActiveProfileAsync(CancellationToken ct = default)
    {
        return _inner.GetActiveProfileAsync(ct);
    }

    /// <inheritdoc />
    public Task SetActiveProfileAsync(OperationalProfile profile, CancellationToken ct = default)
    {
        return _inner.SetActiveProfileAsync(profile, ct);
    }

    /// <inheritdoc />
    public Task<IEffectivePolicy> SimulateAsync(string featureId, PolicyResolutionContext context, FeaturePolicy hypotheticalPolicy, CancellationToken ct = default)
    {
        var effectiveContext = IsMultiLevelEnabled ? context : ClampContext(context);
        return _inner.SimulateAsync(featureId, effectiveContext, hypotheticalPolicy, ct);
    }

    /// <summary>
    /// Clamps a resolution context to VDE level by extracting only the first path segment.
    /// This ensures the cascade engine sees a single-segment path and only resolves at VDE level.
    /// </summary>
    /// <param name="context">The original resolution context with a potentially multi-level path.</param>
    /// <returns>A new context with the path clamped to VDE level only.</returns>
    private static PolicyResolutionContext ClampContext(PolicyResolutionContext context)
    {
        var clampedPath = ClampToVdeLevel(context.Path);
        return context with { Path = clampedPath };
    }

    /// <summary>
    /// Extracts the first path segment to clamp resolution to VDE level.
    /// Given "/vdeName/container/object", returns "/vdeName".
    /// </summary>
    /// <param name="path">The full VDE path.</param>
    /// <returns>The path containing only the VDE name segment.</returns>
    private static string ClampToVdeLevel(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // Normalize: ensure leading slash
        var normalized = path.StartsWith('/') ? path : "/" + path;

        // Find the second slash (after the leading one)
        int secondSlash = normalized.IndexOf('/', 1);

        if (secondSlash < 0)
        {
            // Already a single-segment path (VDE level)
            return normalized;
        }

        // Return only the first segment: "/vdeName"
        return normalized[..secondSlash];
    }
}
