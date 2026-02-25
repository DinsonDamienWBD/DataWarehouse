using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// Result of an ORSet pruning operation, containing metrics about what was cleaned up.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: P0-12 ORSet pruning")]
    internal sealed record OrSetPruneResult(
        int TagsRemoved,
        int ElementsCompacted,
        long BytesBefore,
        long BytesAfter,
        DateTimeOffset PrunedAt);

    /// <summary>
    /// Configuration options for ORSet pruning behavior.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: P0-12 ORSet pruning")]
    internal sealed record OrSetPruneOptions
    {
        /// <summary>
        /// Only prune tombstones older than this threshold. A tombstone is "causally stable"
        /// when all nodes have observed it. Default: 5 minutes.
        /// </summary>
        public TimeSpan CausalStabilityThreshold { get; init; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Trigger prune when the remove-set exceeds this total tombstone count. Default: 10000.
        /// </summary>
        public int MaxTombstonesBeforePrune { get; init; } = 10000;

        /// <summary>
        /// When true, also compact add-set tags for elements with only one surviving tag.
        /// This prevents tag accumulation for stable (non-churning) elements. Default: true.
        /// </summary>
        public bool CompactAddSet { get; init; } = true;
    }

    /// <summary>
    /// Static pruner for <see cref="SdkORSet"/> that garbage-collects tombstones and compacts
    /// add-set tags. Prevents unbounded tombstone growth (P0-12) by removing entries for
    /// fully-removed elements whose tombstones have exceeded the causal stability threshold,
    /// and by compacting stable elements to a single surviving tag.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: P0-12 ORSet pruning")]
    internal static class OrSetPruner
    {
        /// <summary>
        /// Prunes an ORSet by removing causally-stable tombstones for fully-removed elements
        /// and optionally compacting add-sets for elements with a single surviving tag.
        /// </summary>
        /// <param name="orSet">The ORSet to prune (modified in-place).</param>
        /// <param name="options">Pruning configuration options.</param>
        /// <param name="knownActiveNodes">Set of known active node IDs (reserved for future use).</param>
        /// <returns>Metrics describing what was pruned.</returns>
        public static OrSetPruneResult Prune(
            SdkORSet orSet,
            OrSetPruneOptions options,
            IReadOnlySet<string>? knownActiveNodes = null)
        {
            if (orSet == null) throw new ArgumentNullException(nameof(orSet));
            if (options == null) throw new ArgumentNullException(nameof(options));

            var now = DateTimeOffset.UtcNow;
            long bytesBefore = EstimateMemoryUsage(orSet);
            int tagsRemoved = 0;
            int elementsCompacted = 0;

            // Phase 1: Identify and prune fully-removed elements whose tombstones are causally stable.
            // An element is "fully removed" when ALL its add-set tags also appear in the remove-set.
            var elementsToPrune = new List<string>();
            var elementsToCompact = new List<(string Element, string SurvivingTag)>();

            foreach (var element in orSet.GetAllElements())
            {
                var addTags = orSet.GetAddTags(element);
                var removeTags = orSet.GetRemoveTags(element);

                if (addTags == null || addTags.Count == 0)
                    continue;

                // Check if element is fully removed (all add tags are in remove set)
                bool fullyRemoved = removeTags != null && addTags.All(t => removeTags.Contains(t));

                if (fullyRemoved)
                {
                    // Check causal stability: all tombstones must be older than threshold
                    bool allStable = true;
                    foreach (var tag in addTags)
                    {
                        var tombstoneTime = orSet.GetRemoveTimestamp(element, tag);
                        if (now - tombstoneTime < options.CausalStabilityThreshold)
                        {
                            allStable = false;
                            break;
                        }
                    }

                    if (allStable)
                    {
                        tagsRemoved += addTags.Count + (removeTags?.Count ?? 0);
                        elementsToPrune.Add(element);
                    }
                }
                else if (options.CompactAddSet && removeTags != null && removeTags.Count > 0)
                {
                    // Element is still present -- check if it can be compacted.
                    // Compaction: if exactly 1 surviving tag, replace add-set with just that tag
                    // and clear the remove-set for this element.
                    var survivingTags = addTags.Where(t => !removeTags.Contains(t)).ToList();
                    if (survivingTags.Count == 1)
                    {
                        // All removed tags must be causally stable before we can compact
                        bool allStable = true;
                        foreach (var tag in removeTags)
                        {
                            var tombstoneTime = orSet.GetRemoveTimestamp(element, tag);
                            if (now - tombstoneTime < options.CausalStabilityThreshold)
                            {
                                allStable = false;
                                break;
                            }
                        }

                        if (allStable)
                        {
                            tagsRemoved += removeTags.Count + (addTags.Count - 1);
                            elementsToCompact.Add((element, survivingTags[0]));
                        }
                    }
                }
            }

            // Phase 2: Apply mutations (separate from enumeration to avoid concurrent modification)
            foreach (var element in elementsToPrune)
            {
                orSet.PruneElement(element);
            }

            foreach (var (element, survivingTag) in elementsToCompact)
            {
                orSet.CompactElement(element, survivingTag);
                elementsCompacted++;
            }

            long bytesAfter = EstimateMemoryUsage(orSet);

            return new OrSetPruneResult(
                TagsRemoved: tagsRemoved,
                ElementsCompacted: elementsCompacted,
                BytesBefore: bytesBefore,
                BytesAfter: bytesAfter,
                PrunedAt: now);
        }

        /// <summary>
        /// Estimates the approximate memory usage of an ORSet in bytes.
        /// Uses string length * 2 (UTF-16) + object overhead as a rough estimate.
        /// </summary>
        private static long EstimateMemoryUsage(SdkORSet orSet)
        {
            long estimate = 0;
            const int objectOverhead = 24; // Approximate per-object overhead in .NET

            foreach (var element in orSet.GetAllElements())
            {
                estimate += (element.Length * 2) + objectOverhead;

                var addTags = orSet.GetAddTags(element);
                if (addTags != null)
                {
                    foreach (var tag in addTags)
                    {
                        estimate += (tag.Length * 2) + objectOverhead;
                    }
                }

                var removeTags = orSet.GetRemoveTags(element);
                if (removeTags != null)
                {
                    foreach (var tag in removeTags)
                    {
                        estimate += (tag.Length * 2) + objectOverhead;
                    }
                }
            }

            return estimate;
        }
    }
}
