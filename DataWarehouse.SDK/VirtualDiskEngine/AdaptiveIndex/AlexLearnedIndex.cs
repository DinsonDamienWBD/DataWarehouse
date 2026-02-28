using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;

using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Level 4: ALEX (Adaptive Learned Index Structure) overlay on Be-tree.
/// Uses CDF models in a 2-level RMI to predict key positions for O(1) point lookups
/// on skewed access distributions.
/// </summary>
/// <remarks>
/// <para>
/// ALEX overlays learned index predictions on the Be-tree backing store. The Be-tree remains
/// the source of truth for all writes, while ALEX accelerates point reads by predicting
/// positions using trained CDF models. Range queries and counts delegate to the Be-tree.
/// </para>
/// <para>
/// Hit-rate monitoring tracks prediction accuracy. When hit rate drops below 0.7, incremental
/// retraining is triggered for drifted leaf models. If hit rate stays below 0.3 after retraining
/// for 3 consecutive evaluation windows, ALEX auto-deactivates and recommends demotion to Level 3.
/// </para>
/// <para>
/// Thread safety: hit/miss counters use Interlocked. Retraining is serialized via SemaphoreSlim.
/// All write operations go through the backing Be-tree's own locking.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-03 ALEX learned index")]
public sealed class AlexLearnedIndex : IAdaptiveIndex, IAsyncDisposable
{
    private readonly BeTree _backingTree;
    private readonly SemaphoreSlim _retrainLock = new(1, 1);

    // RMI structure: root internal node -> leaf nodes
    private AlexInternalNode? _rmiRoot;
    private readonly int _numLeaves;

    // Hit-rate monitoring
    private long _hitCount;
    private long _missCount;
    private long _operationsSinceLastCheck;
    private int _consecutiveLowHitWindows;
    private DateTime _lastTrainTime;

    /// <summary>
    /// Operations between hit-rate evaluations.
    /// </summary>
    private const long EvaluationInterval = 10_000;

    /// <summary>
    /// Hit rate below which incremental retrain is triggered.
    /// </summary>
    private const double RetrainThreshold = 0.7;

    /// <summary>
    /// Hit rate below which ALEX recommends deactivation to Level 3.
    /// </summary>
    private const double DeactivationThreshold = 0.3;

    /// <summary>
    /// Minimum model age before retraining (prevents retrain storms).
    /// </summary>
    private static readonly TimeSpan MinModelAge = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Consecutive low-hit windows before auto-deactivation.
    /// </summary>
    private const int DeactivationWindowCount = 3;

    /// <summary>
    /// Morph-up threshold: recommend next level when count exceeds this.
    /// </summary>
    private const long MorphUpThreshold = 100_000_000;

    /// <summary>
    /// Morph-down threshold: recommend Be-tree when count drops below this.
    /// </summary>
    private const long MorphDownThreshold = 1_000_000;

    /// <inheritdoc />
    public MorphLevel CurrentLevel => MorphLevel.LearnedIndex;

    /// <inheritdoc />
    public long ObjectCount => _backingTree.ObjectCount;

    /// <inheritdoc />
    public long RootBlockNumber => _backingTree.RootBlockNumber;

    /// <summary>
    /// Gets the current hit rate of the ALEX overlay (0.0 to 1.0).
    /// </summary>
    public double HitRate
    {
        get
        {
            long hits = Interlocked.Read(ref _hitCount);
            long misses = Interlocked.Read(ref _missCount);
            long total = hits + misses;
            return total == 0 ? 1.0 : hits / (double)total;
        }
    }

    /// <summary>
    /// Gets or sets whether the ALEX overlay is active. When false, all operations
    /// delegate directly to the backing Be-tree with zero overhead.
    /// </summary>
    public bool IsActive { get; set; }

    /// <inheritdoc />
#pragma warning disable CS0067 // Event is required by IAdaptiveIndex but only raised by AdaptiveIndexEngine
    public event Action<MorphLevel, MorphLevel>? LevelChanged;
#pragma warning restore CS0067

    /// <summary>
    /// Initializes a new ALEX learned index overlay on the specified Be-tree.
    /// </summary>
    /// <param name="backingTree">The Level 3 Be-tree to overlay.</param>
    /// <param name="numLeaves">Number of leaf nodes in the RMI (default 16).</param>
    public AlexLearnedIndex(BeTree backingTree, int numLeaves = 16)
    {
        _backingTree = backingTree ?? throw new ArgumentNullException(nameof(backingTree));
        _numLeaves = Math.Max(2, numLeaves);
        _lastTrainTime = DateTime.MinValue;
    }

    /// <summary>
    /// Initializes the ALEX overlay by bulk-loading entries from the backing tree
    /// and training the 2-level RMI.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Collect all entries from backing tree
        var entries = new List<(byte[] Key, long Value)>();
        await foreach (var entry in _backingTree.RangeQueryAsync(null, null, ct).ConfigureAwait(false))
        {
            entries.Add(entry);
        }

        BuildRmi(entries);
        IsActive = true;
        _lastTrainTime = DateTime.UtcNow;
        ResetHitCounters();
    }

    /// <inheritdoc />
    public async Task<long?> LookupAsync(byte[] key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!IsActive || _rmiRoot == null)
        {
            return await _backingTree.LookupAsync(key, ct).ConfigureAwait(false);
        }

        // Use RMI to predict position
        int leafIndex = _rmiRoot.PredictChild(key);
        leafIndex = Math.Clamp(leafIndex, 0, _rmiRoot.Children.Length - 1);

        if (_rmiRoot.Children[leafIndex] is AlexLeafNode leaf)
        {
            int predictedPos = leaf.Model.PredictPosition(key, leaf.Data.Capacity);
            int errorBound = leaf.Model.ErrorBound;

            long? result = leaf.Data.Lookup(key, predictedPos, errorBound);
            if (result.HasValue)
            {
                Interlocked.Increment(ref _hitCount);
                TrackOperationAndMaybeEvaluate();
                return result;
            }
        }

        // Miss: fall back to backing tree
        Interlocked.Increment(ref _missCount);
        TrackOperationAndMaybeEvaluate();

        return await _backingTree.LookupAsync(key, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task InsertAsync(byte[] key, long value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        // Insert into backing tree first (source of truth)
        await _backingTree.InsertAsync(key, value, ct).ConfigureAwait(false);

        if (!IsActive || _rmiRoot == null)
            return;

        // Insert into ALEX gapped array at predicted position
        int leafIndex = _rmiRoot.PredictChild(key);
        leafIndex = Math.Clamp(leafIndex, 0, _rmiRoot.Children.Length - 1);

        if (_rmiRoot.Children[leafIndex] is AlexLeafNode leaf)
        {
            int predictedPos = leaf.Model.PredictPosition(key, leaf.Data.Capacity);
            leaf.Data.Insert(key, value, predictedPos);

            // Check if leaf density triggers restructure
            if (leaf.Data.DensityRatio > 0.8 || leaf.Data.DensityRatio < 0.2)
            {
                leaf.NeedsRetrain = true;
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(byte[] key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        // Delete from backing tree
        bool deleted = await _backingTree.DeleteAsync(key, ct).ConfigureAwait(false);

        if (!IsActive || _rmiRoot == null || !deleted)
            return deleted;

        // Remove from ALEX gapped array
        int leafIndex = _rmiRoot.PredictChild(key);
        leafIndex = Math.Clamp(leafIndex, 0, _rmiRoot.Children.Length - 1);

        if (_rmiRoot.Children[leafIndex] is AlexLeafNode leaf)
        {
            int predictedPos = leaf.Model.PredictPosition(key, leaf.Data.Capacity);
            int errorBound = leaf.Model.ErrorBound;
            leaf.Data.Delete(key, predictedPos, errorBound);
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(byte[] key, long newValue, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        // Update backing tree
        bool updated = await _backingTree.UpdateAsync(key, newValue, ct).ConfigureAwait(false);

        if (!IsActive || _rmiRoot == null || !updated)
            return updated;

        // Update in ALEX gapped array (delete + re-insert at same position)
        int leafIndex = _rmiRoot.PredictChild(key);
        leafIndex = Math.Clamp(leafIndex, 0, _rmiRoot.Children.Length - 1);

        if (_rmiRoot.Children[leafIndex] is AlexLeafNode leaf)
        {
            int predictedPos = leaf.Model.PredictPosition(key, leaf.Data.Capacity);
            int errorBound = leaf.Model.ErrorBound;
            leaf.Data.Delete(key, predictedPos, errorBound);
            leaf.Data.Insert(key, newValue, predictedPos);
        }

        return updated;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<(byte[] Key, long Value)> RangeQueryAsync(
        byte[]? startKey,
        byte[]? endKey,
        CancellationToken ct = default)
    {
        // ALEX optimizes point queries, not ranges. Delegate entirely to backing tree.
        return _backingTree.RangeQueryAsync(startKey, endKey, ct);
    }

    /// <inheritdoc />
    public Task<long> CountAsync(CancellationToken ct = default)
    {
        // Delegate to backing tree for accurate count
        return _backingTree.CountAsync(ct);
    }

    /// <inheritdoc />
    public Task MorphToAsync(MorphLevel targetLevel, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            $"AlexLearnedIndex (Level 4) does not directly morph to {targetLevel}. Use AdaptiveIndexEngine for level transitions.");
    }

    /// <inheritdoc />
    public Task<MorphLevel> RecommendLevelAsync(CancellationToken ct = default)
    {
        long count = ObjectCount;

        // If ALEX is inactive (not beneficial), recommend Be-tree
        if (!IsActive)
            return Task.FromResult(MorphLevel.BeTree);

        if (count < MorphDownThreshold)
            return Task.FromResult(MorphLevel.BeTree);
        if (count > MorphUpThreshold)
            return Task.FromResult(MorphLevel.BeTreeForest);

        return Task.FromResult(MorphLevel.LearnedIndex);
    }

    /// <summary>
    /// Incrementally retrains leaf models whose error has drifted beyond thresholds.
    /// Only retrains affected leaves, not the entire RMI.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task RetrainAsync(CancellationToken ct = default)
    {
        if (_rmiRoot == null) return;

        if (!await _retrainLock.WaitAsync(0, ct).ConfigureAwait(false))
            return; // Another retrain is already running

        try
        {
            // Re-read entries from backing tree for accuracy
            var allEntries = new List<(byte[] Key, long Value)>();
            await foreach (var entry in _backingTree.RangeQueryAsync(null, null, ct).ConfigureAwait(false))
            {
                allEntries.Add(entry);
            }

            if (allEntries.Count == 0) return;

            // Partition entries across leaves using root model
            var leafPartitions = new List<(byte[] Key, long Value)>[_rmiRoot.Children.Length];
            for (int i = 0; i < leafPartitions.Length; i++)
                leafPartitions[i] = new List<(byte[] Key, long Value)>();

            foreach (var entry in allEntries)
            {
                int idx = _rmiRoot.PredictChild(entry.Key);
                idx = Math.Clamp(idx, 0, leafPartitions.Length - 1);
                leafPartitions[idx].Add(entry);
            }

            // Retrain only drifted leaves
            for (int i = 0; i < _rmiRoot.Children.Length; i++)
            {
                if (_rmiRoot.Children[i] is AlexLeafNode leaf && leaf.NeedsRetrain)
                {
                    var partition = leafPartitions[i];
                    if (partition.Count > 0)
                    {
                        var keys = new List<byte[]>(partition.Count);
                        foreach (var (key, _) in partition)
                            keys.Add(key);

                        leaf.Model.Train(keys);
                        leaf.Data.BulkLoad(partition);
                        leaf.NeedsRetrain = false;
                    }
                }
            }

            _lastTrainTime = DateTime.UtcNow;
            ResetHitCounters();
        }
        finally
        {
            _retrainLock.Release();
        }
    }

    /// <summary>
    /// Builds the 2-level RMI from sorted entries: one root internal node routing to N leaf nodes.
    /// </summary>
    private void BuildRmi(List<(byte[] Key, long Value)> entries)
    {
        entries.Sort((a, b) => BeTreeMessage.CompareKeys(a.Key, b.Key));

        int numLeaves = Math.Min(_numLeaves, Math.Max(1, entries.Count));

        // Create root internal node
        _rmiRoot = new AlexInternalNode();
        var leaves = new AlexNode[numLeaves];

        if (entries.Count == 0)
        {
            // Empty: create empty leaves
            for (int i = 0; i < numLeaves; i++)
            {
                leaves[i] = new AlexLeafNode();
            }
            _rmiRoot.Children = leaves;
            return;
        }

        // Train root model: maps keys to leaf indices
        var allKeys = new List<byte[]>(entries.Count);
        foreach (var (key, _) in entries)
            allKeys.Add(key);

        _rmiRoot.Model.Train(allKeys);

        // Partition entries across leaves using root model
        var partitions = new List<(byte[] Key, long Value)>[numLeaves];
        for (int i = 0; i < numLeaves; i++)
            partitions[i] = new List<(byte[] Key, long Value)>();

        foreach (var entry in entries)
        {
            int idx = _rmiRoot.PredictChild(entry.Key);
            idx = Math.Clamp(idx, 0, numLeaves - 1);
            partitions[idx].Add(entry);
        }

        // Create and train each leaf
        for (int i = 0; i < numLeaves; i++)
        {
            var leaf = new AlexLeafNode();
            var partition = partitions[i];

            if (partition.Count > 0)
            {
                // Sort partition (may not be fully sorted due to model prediction variance)
                partition.Sort((a, b) => BeTreeMessage.CompareKeys(a.Key, b.Key));

                var keys = new List<byte[]>(partition.Count);
                foreach (var (key, _) in partition)
                    keys.Add(key);

                leaf.Model.Train(keys);
                leaf.Data.BulkLoad(partition);
            }

            leaves[i] = leaf;
        }

        _rmiRoot.Children = leaves;
    }

    /// <summary>
    /// Tracks operation count and triggers hit-rate evaluation at intervals.
    /// </summary>
    private void TrackOperationAndMaybeEvaluate()
    {
        long ops = Interlocked.Increment(ref _operationsSinceLastCheck);
        if (ops >= EvaluationInterval)
        {
            Interlocked.Exchange(ref _operationsSinceLastCheck, 0);
            EvaluateHitRate();
        }
    }

    /// <summary>
    /// Evaluates the current hit rate and decides whether to retrain or deactivate.
    /// </summary>
    private void EvaluateHitRate()
    {
        double rate = HitRate;

        if (rate < DeactivationThreshold)
        {
            Interlocked.Increment(ref _consecutiveLowHitWindows);
            if (_consecutiveLowHitWindows >= DeactivationWindowCount)
            {
                // ALEX is not beneficial; deactivate and recommend Level 3
                IsActive = false;
            }
        }
        else
        {
            Interlocked.Exchange(ref _consecutiveLowHitWindows, 0);
        }

        if (rate < RetrainThreshold && IsActive)
        {
            TimeSpan modelAge = DateTime.UtcNow - _lastTrainTime;
            if (modelAge > MinModelAge)
            {
                // Schedule async retrain (fire-and-forget with error swallowing)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RetrainAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // Retrain failure is non-fatal; ALEX falls back to backing tree
                    }
                });
            }
        }
    }

    /// <summary>
    /// Resets hit/miss counters (e.g., after retraining).
    /// </summary>
    private void ResetHitCounters()
    {
        Interlocked.Exchange(ref _hitCount, 0);
        Interlocked.Exchange(ref _missCount, 0);
        Interlocked.Exchange(ref _operationsSinceLastCheck, 0);
        Interlocked.Exchange(ref _consecutiveLowHitWindows, 0);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _retrainLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
