using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Primitives.Probabilistic;

/// <summary>
/// Utilities for merging probabilistic structures from distributed nodes.
/// Provides type-safe, parallel merging operations (T85.7).
/// </summary>
public static class SketchMerger
{
    /// <summary>
    /// Merges multiple probabilistic structures into one.
    /// </summary>
    /// <typeparam name="T">Type of the structure.</typeparam>
    /// <param name="structures">Structures to merge.</param>
    /// <returns>Merged structure.</returns>
    public static T Merge<T>(IEnumerable<T> structures) where T : IProbabilisticStructure, IMergeable<T>
    {
        var list = structures.ToList();
        if (list.Count == 0)
            throw new ArgumentException("At least one structure is required.", nameof(structures));

        var result = list[0].Clone();
        for (int i = 1; i < list.Count; i++)
        {
            result.Merge(list[i]);
        }

        return result;
    }

    /// <summary>
    /// Merges multiple probabilistic structures in parallel.
    /// Uses a tree-reduction pattern for optimal parallelism.
    /// </summary>
    public static async Task<T> MergeParallelAsync<T>(IEnumerable<T> structures, int maxParallelism = 4, CancellationToken ct = default)
        where T : IProbabilisticStructure, IMergeable<T>
    {
        var list = structures.ToList();
        if (list.Count == 0)
            throw new ArgumentException("At least one structure is required.", nameof(structures));

        if (list.Count == 1)
            return list[0].Clone();

        // Tree reduction: merge pairs in parallel
        using var semaphore = new SemaphoreSlim(maxParallelism);
        while (list.Count > 1)
        {
            ct.ThrowIfCancellationRequested();

            var tasks = new List<Task<T>>();

            for (int i = 0; i < list.Count; i += 2)
            {
                var idx = i;
                if (idx + 1 < list.Count)
                {
                    await semaphore.WaitAsync(ct);
                    tasks.Add(Task.Run(() =>
                    {
                        try
                        {
                            var merged = list[idx].Clone();
                            merged.Merge(list[idx + 1]);
                            return merged;
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, ct));
                }
                else
                {
                    // Odd one out - pass through
                    tasks.Add(Task.FromResult(list[idx]));
                }
            }

            list = (await Task.WhenAll(tasks)).ToList();
        }

        return list[0];
    }

    /// <summary>
    /// Merges Bloom filters from multiple nodes.
    /// </summary>
    public static BloomFilter<T> MergeBloomFilters<T>(IEnumerable<BloomFilter<T>> filters)
    {
        return Merge(filters);
    }

    /// <summary>
    /// Merges Count-Min Sketches from multiple nodes.
    /// </summary>
    public static CountMinSketch<T> MergeCountMinSketches<T>(IEnumerable<CountMinSketch<T>> sketches)
    {
        return Merge(sketches);
    }

    /// <summary>
    /// Merges HyperLogLog counters from multiple nodes.
    /// </summary>
    public static HyperLogLog<T> MergeHyperLogLogs<T>(IEnumerable<HyperLogLog<T>> hlls)
    {
        return Merge(hlls);
    }

    /// <summary>
    /// Merges t-digests from multiple nodes.
    /// </summary>
    public static TDigest MergeTDigests(IEnumerable<TDigest> digests)
    {
        return Merge(digests);
    }

    /// <summary>
    /// Merges Top-K Heavy Hitters from multiple nodes.
    /// </summary>
    public static TopKHeavyHitters<T> MergeTopKHeavyHitters<T>(IEnumerable<TopKHeavyHitters<T>> trackers) where T : notnull
    {
        return Merge(trackers);
    }

    /// <summary>
    /// Deserializes and merges serialized structures from distributed nodes.
    /// </summary>
    /// <typeparam name="T">Type of the structure.</typeparam>
    /// <param name="serializedData">Serialized structure data from each node.</param>
    /// <param name="deserializer">Function to deserialize each structure.</param>
    /// <returns>Merged structure.</returns>
    public static T DeserializeAndMerge<T>(IEnumerable<byte[]> serializedData, Func<byte[], T> deserializer)
        where T : IProbabilisticStructure, IMergeable<T>
    {
        // Cat 9 (finding 567): materialize eagerly so deserializer exceptions surface here
        // with a useful stack trace rather than deferred to Merge() with misleading context.
        var structures = serializedData.Select(deserializer).ToList();
        return Merge(structures);
    }

    /// <summary>
    /// Creates a merge plan for structures with different sizes/configurations.
    /// Returns which structures can be merged and which need re-creation.
    /// </summary>
    public static MergePlan<T> CreateMergePlan<T>(IEnumerable<T> structures) where T : IProbabilisticStructure
    {
        var list = structures.ToList();
        if (list.Count == 0)
            return new MergePlan<T> { CanMerge = false, Reason = "No structures provided." };

        // Group by configuration
        var groups = list.GroupBy(s => (s.StructureType, s.ConfiguredErrorRate));

        if (groups.Count() == 1)
        {
            return new MergePlan<T>
            {
                CanMerge = true,
                MergeableStructures = list,
                TotalMemoryBefore = list.Sum(s => s.MemoryUsageBytes),
                EstimatedMemoryAfter = list[0].MemoryUsageBytes,
                TotalItems = list.Sum(s => s.ItemCount)
            };
        }

        return new MergePlan<T>
        {
            CanMerge = false,
            Reason = "Structures have different configurations.",
            Groups = groups.Select(g => new MergeGroup<T>
            {
                Configuration = $"{g.Key.StructureType}@{g.Key.ConfiguredErrorRate:F4}",
                Structures = g.ToList()
            }).ToList()
        };
    }
}

/// <summary>
/// Describes a merge plan for probabilistic structures.
/// </summary>
public record MergePlan<T> where T : IProbabilisticStructure
{
    /// <summary>
    /// Whether direct merging is possible.
    /// </summary>
    public bool CanMerge { get; init; }

    /// <summary>
    /// Reason if merging is not possible.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Structures that can be merged directly.
    /// </summary>
    public IReadOnlyList<T>? MergeableStructures { get; init; }

    /// <summary>
    /// Groups of structures with different configurations.
    /// </summary>
    public IReadOnlyList<MergeGroup<T>>? Groups { get; init; }

    /// <summary>
    /// Total memory usage before merging.
    /// </summary>
    public long TotalMemoryBefore { get; init; }

    /// <summary>
    /// Estimated memory after merging.
    /// </summary>
    public long EstimatedMemoryAfter { get; init; }

    /// <summary>
    /// Total items across all structures.
    /// </summary>
    public long TotalItems { get; init; }
}

/// <summary>
/// A group of structures with the same configuration.
/// </summary>
public record MergeGroup<T> where T : IProbabilisticStructure
{
    /// <summary>
    /// Configuration identifier.
    /// </summary>
    public string Configuration { get; init; } = string.Empty;

    /// <summary>
    /// Structures in this group.
    /// </summary>
    public IReadOnlyList<T> Structures { get; init; } = Array.Empty<T>();
}
