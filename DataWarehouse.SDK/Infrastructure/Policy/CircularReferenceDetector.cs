using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Custom exception thrown when a circular policy reference is detected during validation.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 70: Circular reference detection (CASC-07)")]
    public sealed class PolicyCircularReferenceException : InvalidOperationException
    {
        /// <summary>
        /// The chain of paths that form the circular reference.
        /// </summary>
        public IReadOnlyList<string> PathChain { get; }

        /// <summary>
        /// Initializes a new instance of <see cref="PolicyCircularReferenceException"/>.
        /// </summary>
        /// <param name="pathChain">The chain of paths that form the cycle.</param>
        public PolicyCircularReferenceException(IReadOnlyList<string> pathChain)
            : base($"Circular policy reference detected: {string.Join(" -> ", pathChain)}")
        {
            PathChain = pathChain;
        }
    }

    /// <summary>
    /// Result of policy validation, containing blocking errors and informational warnings.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 70: Circular reference detection (CASC-07)")]
    public sealed class PolicyValidationResult
    {
        /// <summary>
        /// Whether the policy passed validation (no blocking errors).
        /// </summary>
        public bool IsValid { get; }

        /// <summary>
        /// Blocking errors that prevent the policy from being stored.
        /// </summary>
        public IReadOnlyList<string> Errors { get; }

        /// <summary>
        /// Informational warnings that do not prevent storage but indicate potential issues.
        /// </summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>
        /// Initializes a new <see cref="PolicyValidationResult"/>.
        /// </summary>
        /// <param name="isValid">Whether validation passed.</param>
        /// <param name="errors">Blocking error messages.</param>
        /// <param name="warnings">Informational warning messages.</param>
        public PolicyValidationResult(bool isValid, IReadOnlyList<string> errors, IReadOnlyList<string> warnings)
        {
            IsValid = isValid;
            Errors = errors ?? Array.Empty<string>();
            Warnings = warnings ?? Array.Empty<string>();
        }

        /// <summary>
        /// Creates a valid result with no errors and no warnings.
        /// </summary>
        public static PolicyValidationResult Valid() =>
            new(true, Array.Empty<string>(), Array.Empty<string>());

        /// <summary>
        /// Creates a valid result with warnings but no blocking errors.
        /// </summary>
        /// <param name="warnings">The warning messages.</param>
        public static PolicyValidationResult ValidWithWarnings(IReadOnlyList<string> warnings) =>
            new(true, Array.Empty<string>(), warnings);
    }

    /// <summary>
    /// Validates policies for circular references before they are written to the store (CASC-07).
    /// <para>
    /// A circular reference occurs when a policy's CustomParameters contain a "redirect" or
    /// "inherit_from" key that points back to a path that eventually references the original.
    /// The detector walks the redirect chain up to a configurable maximum depth (default: 20 hops).
    /// </para>
    /// <para>
    /// Also validates that Enforce cascade policies do not exist at Block level, since Enforce
    /// only makes sense at Container or higher where there are descendants to enforce upon.
    /// Such entries produce warnings but are not rejected.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 70: Circular reference detection (CASC-07)")]
    public static class CircularReferenceDetector
    {
        /// <summary>
        /// Keys in CustomParameters that indicate a redirect/inheritance chain.
        /// </summary>
        private static readonly string[] RedirectKeys = { "redirect", "inherit_from" };

        /// <summary>
        /// Default maximum number of hops to follow in a redirect chain before declaring a cycle.
        /// </summary>
        public const int DefaultMaxDepth = 20;

        /// <summary>
        /// Validates a policy for circular references and structural issues.
        /// </summary>
        /// <param name="featureId">The feature identifier for this policy.</param>
        /// <param name="level">The hierarchy level at which this policy will be stored.</param>
        /// <param name="path">The VDE path at which this policy will be stored.</param>
        /// <param name="policy">The policy to validate.</param>
        /// <param name="store">The policy store used to follow redirect chains.</param>
        /// <param name="maxDepth">Maximum redirect chain depth. Defaults to <see cref="DefaultMaxDepth"/>.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A <see cref="PolicyValidationResult"/> with any errors and warnings.</returns>
        public static async Task<PolicyValidationResult> ValidateAsync(
            string featureId,
            PolicyLevel level,
            string path,
            FeaturePolicy policy,
            IPolicyStore store,
            int maxDepth = DefaultMaxDepth,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(featureId)) throw new ArgumentException("Feature ID is required.", nameof(featureId));
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path is required.", nameof(path));
            if (policy is null) throw new ArgumentNullException(nameof(policy));
            if (store is null) throw new ArgumentNullException(nameof(store));

            var errors = new List<string>();
            var warnings = new List<string>();

            // Check 1: Enforce at Block level warning
            if (policy.Cascade == CascadeStrategy.Enforce && level == PolicyLevel.Block)
            {
                warnings.Add(
                    $"Policy for '{featureId}' at Block level '{path}' uses Enforce cascade. " +
                    "Enforce only makes sense at Container or higher where there are descendants to enforce upon.");
            }

            // Check 2: Circular redirect chain detection
            var redirectTarget = GetRedirectTarget(policy);
            if (redirectTarget is not null)
            {
                var visited = new List<string> { path };
                var visitedSet = new HashSet<string>(StringComparer.Ordinal) { path }; // O(1) lookup
                var currentTarget = redirectTarget;

                for (var hop = 0; hop < maxDepth; hop++)
                {
                    ct.ThrowIfCancellationRequested();

                    if (string.Equals(currentTarget, path, StringComparison.Ordinal))
                    {
                        // Cycle back to origin
                        visited.Add(currentTarget);
                        throw new PolicyCircularReferenceException(visited);
                    }

                    if (visitedSet.Contains(currentTarget))
                    {
                        // Cycle detected (not back to origin but revisiting a node)
                        visited.Add(currentTarget);
                        throw new PolicyCircularReferenceException(visited);
                    }

                    visited.Add(currentTarget);
                    visitedSet.Add(currentTarget);

                    // Look up the target policy in the store to continue the chain
                    var targetPolicy = await store.GetAsync(featureId, level, currentTarget, ct).ConfigureAwait(false);
                    if (targetPolicy is null)
                    {
                        // Chain ends at a non-existent policy -- no cycle
                        break;
                    }

                    var nextTarget = GetRedirectTarget(targetPolicy);
                    if (nextTarget is null)
                    {
                        // Chain ends at a policy without redirects -- no cycle
                        break;
                    }

                    currentTarget = nextTarget;
                }

                // If we exhausted maxDepth hops without finding a cycle, treat as suspicious
                if (visited.Count > maxDepth)
                {
                    errors.Add(
                        $"Redirect chain for '{featureId}' at '{path}' exceeds maximum depth of {maxDepth} hops. " +
                        $"Chain: {string.Join(" -> ", visited)}");
                }
            }

            var isValid = errors.Count == 0;
            return new PolicyValidationResult(isValid, errors, warnings);
        }

        /// <summary>
        /// Extracts the redirect target path from a policy's CustomParameters, if any.
        /// Checks for "redirect" and "inherit_from" keys.
        /// </summary>
        /// <param name="policy">The policy to inspect.</param>
        /// <returns>The redirect target path, or null if no redirect is specified.</returns>
        private static string? GetRedirectTarget(FeaturePolicy policy)
        {
            if (policy.CustomParameters is null || policy.CustomParameters.Count == 0)
                return null;

            foreach (var key in RedirectKeys)
            {
                if (policy.CustomParameters.TryGetValue(key, out var target) &&
                    !string.IsNullOrEmpty(target))
                {
                    return target;
                }
            }

            return null;
        }
    }
}
