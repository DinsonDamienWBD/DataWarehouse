using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Defines the resolution mode for a merge conflict on a specific tag key.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 70: Merge conflict resolution (CASC-08)")]
    public enum MergeConflictMode
    {
        /// <summary>
        /// Selects the most restrictive value: lowest numeric value or lexicographically smallest string.
        /// </summary>
        MostRestrictive = 0,

        /// <summary>
        /// Selects the closest (most-specific) value. The first value in the ordered list wins.
        /// </summary>
        Closest = 1,

        /// <summary>
        /// Combines all distinct values, joined by a semicolon separator.
        /// </summary>
        Union = 2
    }

    /// <summary>
    /// Resolves merge conflicts for the Merge cascade strategy on a per-tag-key basis (CASC-08).
    /// <para>
    /// Each tag key can be configured with a different <see cref="MergeConflictMode"/>:
    /// <list type="bullet">
    ///   <item><description><see cref="MergeConflictMode.MostRestrictive"/>: numeric values use Math.Min;
    ///   string values use the lexicographically smallest.</description></item>
    ///   <item><description><see cref="MergeConflictMode.Closest"/>: the most-specific (first) value wins.</description></item>
    ///   <item><description><see cref="MergeConflictMode.Union"/>: all distinct values are joined by ";".</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Unmatched keys default to <see cref="MergeConflictMode.Closest"/> (child wins) for backward compatibility.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 70: Merge conflict resolution (CASC-08)")]
    public sealed class MergeConflictResolver
    {
        private readonly Dictionary<string, MergeConflictMode> _keyModes;
        private readonly MergeConflictMode _defaultMode;

        /// <summary>
        /// Initializes a new <see cref="MergeConflictResolver"/> with per-key conflict resolution modes.
        /// </summary>
        /// <param name="keyModes">
        /// Dictionary mapping tag keys to their resolution mode. Keys not present default to
        /// <see cref="MergeConflictMode.Closest"/>.
        /// </param>
        /// <param name="defaultMode">
        /// The default mode for keys not in <paramref name="keyModes"/>.
        /// Defaults to <see cref="MergeConflictMode.Closest"/>.
        /// </param>
        public MergeConflictResolver(
            Dictionary<string, MergeConflictMode>? keyModes = null,
            MergeConflictMode defaultMode = MergeConflictMode.Closest)
        {
            _keyModes = keyModes is not null
                ? new Dictionary<string, MergeConflictMode>(keyModes, StringComparer.Ordinal)
                : new Dictionary<string, MergeConflictMode>(StringComparer.Ordinal);
            _defaultMode = defaultMode;
        }

        /// <summary>
        /// Gets the configured modes for inspection or diagnostics.
        /// </summary>
        public IReadOnlyDictionary<string, MergeConflictMode> KeyModes => _keyModes;

        /// <summary>
        /// Gets the default mode used for unconfigured keys.
        /// </summary>
        public MergeConflictMode DefaultMode => _defaultMode;

        /// <summary>
        /// Resolves a merge conflict for a single tag key given conflicting values from multiple levels.
        /// </summary>
        /// <param name="tagKey">The custom parameter key being resolved.</param>
        /// <param name="values">
        /// The conflicting values ordered most-specific-first. Must contain at least one value.
        /// </param>
        /// <returns>The resolved value according to the configured mode for this key.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="tagKey"/> is null/empty or <paramref name="values"/> is empty.</exception>
        public string Resolve(string tagKey, IReadOnlyList<string> values)
        {
            if (string.IsNullOrEmpty(tagKey))
                throw new ArgumentException("Tag key must not be null or empty.", nameof(tagKey));
            if (values is null || values.Count == 0)
                throw new ArgumentException("Values must contain at least one entry.", nameof(values));

            if (values.Count == 1)
                return values[0];

            var mode = _keyModes.TryGetValue(tagKey, out var configuredMode)
                ? configuredMode
                : _defaultMode;

            return mode switch
            {
                MergeConflictMode.MostRestrictive => ResolveMostRestrictive(values),
                MergeConflictMode.Closest => values[0],
                MergeConflictMode.Union => ResolveUnion(values),
                _ => values[0] // Unknown mode falls back to Closest
            };
        }

        /// <summary>
        /// Resolves all conflicting keys across a set of custom parameter dictionaries from
        /// multiple hierarchy levels. Returns the merged result.
        /// </summary>
        /// <param name="parametersByLevel">
        /// Custom parameter dictionaries ordered most-specific-first.
        /// </param>
        /// <returns>Merged dictionary with all conflicts resolved.</returns>
        public Dictionary<string, string> ResolveAll(IReadOnlyList<Dictionary<string, string>?> parametersByLevel)
        {
            if (parametersByLevel is null || parametersByLevel.Count == 0)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            // Collect all unique keys and their values (most-specific first)
            var keyValues = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var parameters in parametersByLevel)
            {
                if (parameters is null) continue;
                foreach (var kvp in parameters)
                {
                    if (!keyValues.TryGetValue(kvp.Key, out var list))
                    {
                        list = new List<string>();
                        keyValues[kvp.Key] = list;
                    }
                    list.Add(kvp.Value);
                }
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kvp in keyValues)
            {
                result[kvp.Key] = Resolve(kvp.Key, kvp.Value);
            }

            return result;
        }

        /// <summary>
        /// MostRestrictive: if all values are numeric, return Math.Min. Otherwise, lexicographically smallest.
        /// </summary>
        private static string ResolveMostRestrictive(IReadOnlyList<string> values)
        {
            // Try numeric comparison
            var allNumeric = true;
            var minNumeric = double.MaxValue;
            string? minNumericStr = null;

            foreach (var value in values)
            {
                if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
                {
                    if (num < minNumeric)
                    {
                        minNumeric = num;
                        minNumericStr = value;
                    }
                }
                else
                {
                    allNumeric = false;
                    break;
                }
            }

            if (allNumeric && minNumericStr is not null)
                return minNumericStr;

            // Fall back to lexicographic comparison
            var smallest = values[0];
            for (var i = 1; i < values.Count; i++)
            {
                if (string.Compare(values[i], smallest, StringComparison.Ordinal) < 0)
                {
                    smallest = values[i];
                }
            }

            return smallest;
        }

        /// <summary>
        /// Union: returns all distinct values joined by ";".
        /// </summary>
        private static string ResolveUnion(IReadOnlyList<string> values)
        {
            var distinct = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var value in values)
            {
                if (seen.Add(value))
                {
                    distinct.Add(value);
                }
            }

            return string.Join(";", distinct);
        }
    }
}
