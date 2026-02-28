using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Flash;

/// <summary>
/// Wear-leveling strategy for flash block allocation.
/// </summary>
/// <remarks>
/// <para>
/// Distributes erase cycles evenly across flash blocks to maximize device lifespan.
/// Flash blocks have limited erase cycles (typically 10K-100K for consumer NAND, 100K+ for enterprise).
/// </para>
/// <para>
/// <strong>Algorithm:</strong> Selects block with lowest erase count from available free blocks.
/// If multiple blocks have the same count, selects randomly to avoid clustering.
/// </para>
/// <para>
/// <strong>Tracking:</strong> Maintains per-block erase counters. In production, counters should
/// be persisted to flash (e.g., in superblock or out-of-band area) for power-fail resilience.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Wear-leveling strategy (EDGE-05)")]
internal sealed class WearLevelingStrategy
{
    private readonly Dictionary<long, int> _blockEraseCount = new();
    private readonly Random _random = new();
    // Pre-allocated candidate list reused across calls to avoid per-write heap allocation
    // on memory-constrained edge devices (finding P2-281). Not thread-safe by design;
    // callers must ensure single-threaded access or add external locking.
    private readonly List<long> _candidateBuffer = new(16);

    /// <summary>
    /// Selects a block for write operation based on wear-leveling policy.
    /// </summary>
    /// <param name="availableBlocks">Set of free blocks eligible for allocation.</param>
    /// <returns>Block number with lowest erase count (or random if tie).</returns>
    /// <exception cref="InvalidOperationException">Thrown if no available blocks.</exception>
    public long SelectBlockForWrite(IEnumerable<long> availableBlocks)
    {
        var minCount = int.MaxValue;
        _candidateBuffer.Clear(); // reuse pre-allocated buffer — avoids per-call heap allocation

        foreach (var block in availableBlocks)
        {
            var count = _blockEraseCount.GetValueOrDefault(block, 0);
            if (count < minCount)
            {
                minCount = count;
                _candidateBuffer.Clear();
                _candidateBuffer.Add(block);
            }
            else if (count == minCount)
            {
                _candidateBuffer.Add(block);
            }
        }

        if (_candidateBuffer.Count == 0)
        {
            throw new InvalidOperationException("No available blocks for wear-leveling allocation");
        }

        var selected = _candidateBuffer[_random.Next(_candidateBuffer.Count)];
        _blockEraseCount[selected] = _blockEraseCount.GetValueOrDefault(selected, 0) + 1;
        return selected;
    }

    /// <summary>
    /// Gets the erase count for a specific block.
    /// </summary>
    /// <param name="blockNumber">Block number to query.</param>
    /// <returns>Number of times block has been erased.</returns>
    public int GetEraseCount(long blockNumber) => _blockEraseCount.GetValueOrDefault(blockNumber, 0);

    /// <summary>
    /// Gets the average erase count across all tracked blocks.
    /// </summary>
    /// <returns>Average erase count; 0 if no blocks tracked.</returns>
    public double GetAverageEraseCount()
    {
        if (_blockEraseCount.Count == 0) return 0;
        return _blockEraseCount.Values.Average();
    }

    /// <summary>
    /// Gets the maximum erase count across all tracked blocks.
    /// </summary>
    /// <returns>Maximum erase count; 0 if no blocks tracked.</returns>
    public int GetMaxEraseCount()
    {
        if (_blockEraseCount.Count == 0) return 0;
        return _blockEraseCount.Values.Max();
    }
}
