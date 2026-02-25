using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Policy
{
    /// <summary>
    /// Core contract for the Policy Engine that resolves effective policies by walking the VDE hierarchy
    /// cascade chain (Block -> Chunk -> Object -> Container -> VDE) and applying <see cref="CascadeStrategy"/> rules.
    /// <para>
    /// The Policy Engine is the single entry point for all policy resolution. Plugins and infrastructure
    /// components call <see cref="ResolveAsync"/> to determine the effective behavior for any feature
    /// at any point in the hierarchy. The engine combines the active <see cref="OperationalProfile"/>,
    /// per-level overrides from <see cref="IPolicyStore"/>, and cascade rules to produce an
    /// <see cref="IEffectivePolicy"/> snapshot.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Policy Engine interfaces (SDKF-01)")]
    public interface IPolicyEngine
    {
        /// <summary>
        /// Resolves the effective policy for a specific feature at a given context (path, user, hardware, security).
        /// The engine walks the cascade chain (Block -> Chunk -> Object -> Container -> VDE) applying
        /// <see cref="CascadeStrategy"/> rules to produce the final resolved policy.
        /// </summary>
        /// <param name="featureId">Unique identifier of the feature to resolve (e.g., "compression", "encryption").</param>
        /// <param name="context">Resolution context providing path, user, hardware, and security information.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>The resolved effective policy snapshot for the specified feature and context.</returns>
        Task<IEffectivePolicy> ResolveAsync(string featureId, PolicyResolutionContext context, CancellationToken ct = default);

        /// <summary>
        /// Resolves effective policies for all registered features at a given context.
        /// Returns a dictionary keyed by feature identifier containing the resolved policy for each feature.
        /// </summary>
        /// <param name="context">Resolution context providing path, user, hardware, and security information.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A read-only dictionary mapping feature identifiers to their resolved effective policies.</returns>
        Task<IReadOnlyDictionary<string, IEffectivePolicy>> ResolveAllAsync(PolicyResolutionContext context, CancellationToken ct = default);

        /// <summary>
        /// Gets the currently active <see cref="OperationalProfile"/> (Speed/Balanced/Standard/Strict/Paranoid/Custom).
        /// The active profile provides default feature policies that serve as the baseline for cascade resolution.
        /// </summary>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>The currently active operational profile.</returns>
        Task<OperationalProfile> GetActiveProfileAsync(CancellationToken ct = default);

        /// <summary>
        /// Sets the active <see cref="OperationalProfile"/>. All subsequent policy resolutions will use
        /// this profile's feature policies as the baseline defaults before applying per-level overrides.
        /// </summary>
        /// <param name="profile">The operational profile to activate.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task SetActiveProfileAsync(OperationalProfile profile, CancellationToken ct = default);

        /// <summary>
        /// Simulates policy resolution without applying changes (PERF-07 placeholder).
        /// Returns a what-if analysis showing what the effective policy would be if the hypothetical
        /// policy were applied at the context's location.
        /// </summary>
        /// <param name="featureId">Unique identifier of the feature to simulate.</param>
        /// <param name="context">Resolution context providing path, user, hardware, and security information.</param>
        /// <param name="hypotheticalPolicy">The hypothetical policy to evaluate without persisting.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>The simulated effective policy snapshot showing the result of the what-if scenario.</returns>
        Task<IEffectivePolicy> SimulateAsync(string featureId, PolicyResolutionContext context, FeaturePolicy hypotheticalPolicy, CancellationToken ct = default);
    }
}
