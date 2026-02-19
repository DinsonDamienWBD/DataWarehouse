using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.ChaosVaccination;

namespace DataWarehouse.Plugins.ChaosVaccination.ImmuneResponse;

/// <summary>
/// Analyzes fault events and chaos experiment results to generate deterministic fault signatures.
/// Supports both exact hash matching and fuzzy matching (by fault type + component overlap or
/// pattern prefix) so that the immune response system can recognize previously-seen faults even
/// when conditions are not identical.
///
/// Signature generation:
/// - Hash = SHA256(FaultType + sorted AffectedComponents + Pattern)
/// - Pattern describes the failure shape (e.g., "network-partition:storage-plugins:3-nodes")
///
/// Matching precedence:
/// 1. Exact hash match
/// 2. Same FaultType + >=60% component overlap
/// 3. Same FaultType + same pattern prefix (before last segment)
/// Returns the entry with the highest SuccessRate among all matches.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Immune response system")]
public sealed class FaultSignatureAnalyzer
{
    private const double FuzzyComponentOverlapThreshold = 0.60;

    /// <summary>
    /// Generates a deterministic <see cref="FaultSignature"/> from a completed chaos experiment result.
    /// </summary>
    /// <param name="result">The experiment result to derive the signature from.</param>
    /// <returns>A fault signature representing the observed failure pattern.</returns>
    public FaultSignature GenerateSignature(ChaosExperimentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // Derive FaultType from the pre-existing signature on the result, or fall back to Custom
        var faultType = result.FaultSignature?.FaultType ?? FaultType.Custom;

        var affectedComponents = BuildAffectedComponents(result.AffectedPlugins, result.AffectedNodes);
        var pattern = BuildPattern(faultType, affectedComponents);
        var hash = ComputeHash(faultType, affectedComponents, pattern);
        var severity = MapBlastRadiusToSeverity(result.ActualBlastRadius);

        return new FaultSignature
        {
            Hash = hash,
            FaultType = faultType,
            Pattern = pattern,
            AffectedComponents = affectedComponents,
            Severity = severity,
            FirstObserved = result.StartedAt,
            ObservationCount = 1
        };
    }

    /// <summary>
    /// Generates a fault signature from a production incident (not a chaos experiment).
    /// </summary>
    /// <param name="pluginId">The ID of the plugin where the fault occurred.</param>
    /// <param name="nodeId">The ID of the node where the fault occurred.</param>
    /// <param name="type">The type of fault observed.</param>
    /// <param name="errorPattern">A string describing the error pattern.</param>
    /// <returns>A fault signature representing the observed failure pattern.</returns>
    public FaultSignature GenerateSignatureFromEvent(string pluginId, string nodeId, FaultType type, string errorPattern)
    {
        ArgumentNullException.ThrowIfNull(pluginId);
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentNullException.ThrowIfNull(errorPattern);

        // Use the plugin category (prefix before first dot) for grouping
        var pluginCategory = ExtractPluginCategory(pluginId);
        var affectedComponents = new[] { pluginCategory, nodeId };
        var pattern = $"{type.ToString().ToLowerInvariant()}:{pluginCategory}:{errorPattern}";
        var hash = ComputeHash(type, affectedComponents, pattern);

        return new FaultSignature
        {
            Hash = hash,
            FaultType = type,
            Pattern = pattern,
            AffectedComponents = affectedComponents,
            Severity = FaultSeverity.Medium, // Default for production events; caller can adjust
            FirstObserved = DateTimeOffset.UtcNow,
            ObservationCount = 1
        };
    }

    /// <summary>
    /// Matches a candidate fault signature against a set of immune memory entries.
    /// Tries exact hash match first, then fuzzy matching (component overlap, pattern prefix).
    /// Returns the entry with the highest success rate among all matches, or null if none match.
    /// </summary>
    /// <param name="candidate">The fault signature to look up.</param>
    /// <param name="memory">The immune memory entries to search.</param>
    /// <returns>The best-matching entry, or null if no match found.</returns>
    public ImmuneMemoryEntry? MatchSignature(FaultSignature candidate, IReadOnlyList<ImmuneMemoryEntry> memory)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (memory == null || memory.Count == 0)
            return null;

        // 1. Exact hash match
        var exactMatch = memory.FirstOrDefault(e => string.Equals(e.Signature.Hash, candidate.Hash, StringComparison.Ordinal));
        if (exactMatch != null)
            return exactMatch;

        // 2. Fuzzy matching: collect all matches, return the one with highest success rate
        ImmuneMemoryEntry? bestMatch = null;
        double bestSuccessRate = -1.0;

        for (int i = 0; i < memory.Count; i++)
        {
            var entry = memory[i];
            if (entry.Signature.FaultType != candidate.FaultType)
                continue;

            bool isMatch = false;

            // 2a. Same FaultType + 60%+ component overlap
            double overlap = ComputeComponentOverlap(candidate.AffectedComponents, entry.Signature.AffectedComponents);
            if (overlap >= FuzzyComponentOverlapThreshold)
            {
                isMatch = true;
            }

            // 2b. Same FaultType + same pattern prefix (before last segment)
            if (!isMatch)
            {
                var candidatePrefix = GetPatternPrefix(candidate.Pattern);
                var entryPrefix = GetPatternPrefix(entry.Signature.Pattern);
                if (!string.IsNullOrEmpty(candidatePrefix) &&
                    string.Equals(candidatePrefix, entryPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    isMatch = true;
                }
            }

            if (isMatch && entry.SuccessRate > bestSuccessRate)
            {
                bestSuccessRate = entry.SuccessRate;
                bestMatch = entry;
            }
        }

        return bestMatch;
    }

    /// <summary>
    /// Computes a deterministic SHA256 hash from the fault type, sorted components, and pattern.
    /// </summary>
    private static string ComputeHash(FaultType faultType, string[] components, string pattern)
    {
        var sortedComponents = components.OrderBy(c => c, StringComparer.Ordinal).ToArray();
        var input = $"{faultType}|{string.Join(",", sortedComponents)}|{pattern}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Builds the affected components array from plugin IDs and node IDs.
    /// </summary>
    private static string[] BuildAffectedComponents(string[] affectedPlugins, string[] affectedNodes)
    {
        var components = new List<string>(affectedPlugins.Length + affectedNodes.Length);
        components.AddRange(affectedPlugins);
        components.AddRange(affectedNodes);
        return components.ToArray();
    }

    /// <summary>
    /// Builds a human-readable pattern string from the fault type and affected components.
    /// Format: "fault-type:component-summary:count"
    /// </summary>
    private static string BuildPattern(FaultType faultType, string[] affectedComponents)
    {
        var typeName = faultType.ToString().ToLowerInvariant();

        // Derive a summary from component categories
        var categories = affectedComponents
            .Select(ExtractPluginCategory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var categorySummary = categories.Length > 0
            ? string.Join("+", categories)
            : "unknown";

        return $"{typeName}:{categorySummary}:{affectedComponents.Length}-components";
    }

    /// <summary>
    /// Extracts the category prefix from a plugin or node ID (text before the first dot or dash).
    /// </summary>
    private static string ExtractPluginCategory(string id)
    {
        if (string.IsNullOrEmpty(id))
            return "unknown";

        var dotIndex = id.IndexOf('.');
        var dashIndex = id.IndexOf('-');

        int separatorIndex;
        if (dotIndex >= 0 && dashIndex >= 0)
            separatorIndex = Math.Min(dotIndex, dashIndex);
        else if (dotIndex >= 0)
            separatorIndex = dotIndex;
        else if (dashIndex >= 0)
            separatorIndex = dashIndex;
        else
            return id.ToLowerInvariant();

        return id[..separatorIndex].ToLowerInvariant();
    }

    /// <summary>
    /// Maps the observed blast radius level to a fault severity.
    /// </summary>
    private static FaultSeverity MapBlastRadiusToSeverity(BlastRadiusLevel level)
    {
        return level switch
        {
            BlastRadiusLevel.SingleStrategy => FaultSeverity.Low,
            BlastRadiusLevel.SinglePlugin => FaultSeverity.Medium,
            BlastRadiusLevel.PluginCategory => FaultSeverity.High,
            BlastRadiusLevel.SingleNode => FaultSeverity.High,
            BlastRadiusLevel.NodeGroup => FaultSeverity.Critical,
            BlastRadiusLevel.Cluster => FaultSeverity.Catastrophic,
            _ => FaultSeverity.Medium
        };
    }

    /// <summary>
    /// Computes the Jaccard-like component overlap ratio between two component sets.
    /// Returns 1.0 for identical sets, 0.0 for disjoint sets.
    /// </summary>
    private static double ComputeComponentOverlap(string[] a, string[] b)
    {
        if (a.Length == 0 && b.Length == 0)
            return 1.0;
        if (a.Length == 0 || b.Length == 0)
            return 0.0;

        var setA = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);

        int intersection = setA.Count(x => setB.Contains(x));
        int union = setA.Count + setB.Count - intersection;

        return union > 0 ? (double)intersection / union : 0.0;
    }

    /// <summary>
    /// Gets the prefix of a pattern string (everything before the last colon-separated segment).
    /// For "network-partition:storage:3-nodes" returns "network-partition:storage".
    /// </summary>
    private static string GetPatternPrefix(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return string.Empty;

        int lastColon = pattern.LastIndexOf(':');
        return lastColon > 0 ? pattern[..lastColon] : string.Empty;
    }
}
