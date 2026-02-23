using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Provides the default <see cref="CascadeStrategy"/> for each feature category.
    /// When a feature policy does not declare an explicit cascade strategy, the engine
    /// consults this class to determine the correct default based on the feature's category.
    /// <para>
    /// Built-in category defaults (CASC-02):
    /// <list type="bullet">
    ///   <item><description>Security, Encryption, Access Control: <see cref="CascadeStrategy.MostRestrictive"/></description></item>
    ///   <item><description>Performance, Compression: <see cref="CascadeStrategy.Override"/></description></item>
    ///   <item><description>Governance: <see cref="CascadeStrategy.Merge"/></description></item>
    ///   <item><description>Compliance, Audit: <see cref="CascadeStrategy.Enforce"/></description></item>
    ///   <item><description>Replication (and all unrecognized categories): <see cref="CascadeStrategy.Inherit"/></description></item>
    /// </list>
    /// </para>
    /// <para>
    /// User-provided overrides can supplement or replace the built-in defaults via the constructor.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 70: Category default cascade strategies (CASC-02)")]
    public sealed class PolicyCategoryDefaults
    {
        /// <summary>
        /// Built-in category-to-cascade-strategy mappings. Keys are lowercase category names.
        /// </summary>
        private static readonly Dictionary<string, CascadeStrategy> BuiltInDefaults = new(StringComparer.OrdinalIgnoreCase)
        {
            ["security"] = CascadeStrategy.MostRestrictive,
            ["encryption"] = CascadeStrategy.MostRestrictive,
            ["access_control"] = CascadeStrategy.MostRestrictive,
            ["performance"] = CascadeStrategy.Override,
            ["compression"] = CascadeStrategy.Override,
            ["governance"] = CascadeStrategy.Merge,
            ["compliance"] = CascadeStrategy.Enforce,
            ["audit"] = CascadeStrategy.Enforce,
            ["replication"] = CascadeStrategy.Inherit,
        };

        private readonly Dictionary<string, CascadeStrategy> _effectiveDefaults;

        /// <summary>
        /// Initializes a new instance of <see cref="PolicyCategoryDefaults"/> with optional
        /// user-provided overrides that supplement or replace the built-in category defaults.
        /// </summary>
        /// <param name="overrides">
        /// Optional dictionary of category-to-strategy overrides. Keys are case-insensitive
        /// category names. When provided, these entries take precedence over the built-in defaults.
        /// Pass null or empty to use only built-in defaults.
        /// </param>
        public PolicyCategoryDefaults(IReadOnlyDictionary<string, CascadeStrategy>? overrides = null)
        {
            _effectiveDefaults = new Dictionary<string, CascadeStrategy>(BuiltInDefaults, StringComparer.OrdinalIgnoreCase);

            if (overrides is not null)
            {
                foreach (var kvp in overrides)
                {
                    _effectiveDefaults[kvp.Key] = kvp.Value;
                }
            }
        }

        /// <summary>
        /// Gets the default cascade strategy for the specified feature ID.
        /// <para>
        /// Lookup logic:
        /// <list type="number">
        ///   <item><description>Direct match on the full feature ID (e.g., "security.encryption").</description></item>
        ///   <item><description>Category prefix match: if the feature ID contains a dot, the portion before
        ///   the first dot is used as the category (e.g., "security" from "security.encryption").</description></item>
        ///   <item><description>If no match is found, returns <see cref="CascadeStrategy.Inherit"/> as the safe default.</description></item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="featureId">
        /// The feature identifier to look up. May contain a category prefix separated by a dot
        /// (e.g., "security.encryption") or be a plain category name (e.g., "compression").
        /// </param>
        /// <returns>The default <see cref="CascadeStrategy"/> for the feature's category.</returns>
        public CascadeStrategy GetDefaultStrategy(string featureId)
        {
            if (string.IsNullOrEmpty(featureId))
                return CascadeStrategy.Inherit;

            // Try direct match first (e.g., "encryption" matches directly)
            if (_effectiveDefaults.TryGetValue(featureId, out var directMatch))
                return directMatch;

            // Try category prefix (e.g., "security.encryption" -> "security")
            var dotIndex = featureId.IndexOf('.');
            if (dotIndex > 0)
            {
                var category = featureId.Substring(0, dotIndex);
                if (_effectiveDefaults.TryGetValue(category, out var categoryMatch))
                    return categoryMatch;
            }

            // Safe default
            return CascadeStrategy.Inherit;
        }

        /// <summary>
        /// Gets the complete set of effective category defaults (built-in + user overrides).
        /// </summary>
        /// <returns>A read-only view of the current category-to-strategy mappings.</returns>
        public IReadOnlyDictionary<string, CascadeStrategy> GetAllDefaults()
        {
            return _effectiveDefaults;
        }
    }
}
