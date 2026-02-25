using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Cumulative Distribution Function (CDF) model using linear regression for ALEX learned index.
/// Maps keys to predicted positions within a gapped array.
/// </summary>
/// <remarks>
/// The CDF model learns the distribution of keys and predicts positions using a linear function
/// y = slope * x + intercept, where x is the normalized key value in [0, 1]. Training uses
/// ordinary least-squares regression on sorted key positions.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-03 ALEX CDF model")]
public sealed class AlexCdfModel
{
    /// <summary>
    /// Gets the slope of the learned linear function.
    /// </summary>
    public double Slope { get; private set; }

    /// <summary>
    /// Gets the intercept of the learned linear function.
    /// </summary>
    public double Intercept { get; private set; }

    /// <summary>
    /// Gets the mean absolute error computed during training. Indicates model accuracy.
    /// </summary>
    public double MeanAbsoluteError { get; private set; }

    /// <summary>
    /// Gets the error bound: ceil(MAE * 1.5). Defines the search window around predictions.
    /// </summary>
    public int ErrorBound { get; private set; }

    /// <summary>
    /// Gets whether this model has been trained.
    /// </summary>
    public bool IsTrained { get; private set; }

    /// <summary>
    /// Predicts the position of a key within an array of the given size.
    /// </summary>
    /// <param name="key">The key to predict position for.</param>
    /// <param name="arraySize">The size of the target array.</param>
    /// <returns>The predicted position clamped to [0, arraySize - 1].</returns>
    public int PredictPosition(byte[] key, int arraySize)
    {
        if (arraySize <= 0) return 0;

        double normalizedKey = NormalizeKey(key);
        double predicted = Slope * normalizedKey + Intercept;

        // Scale to array size and clamp
        int position = (int)Math.Round(predicted * (arraySize - 1));
        return Math.Clamp(position, 0, arraySize - 1);
    }

    /// <summary>
    /// Trains the CDF model on a sorted list of keys using least-squares linear regression.
    /// </summary>
    /// <param name="sortedKeys">Keys in sorted order.</param>
    public void Train(IReadOnlyList<byte[]> sortedKeys)
    {
        int n = sortedKeys.Count;

        if (n <= 0)
        {
            Slope = 0;
            Intercept = 0;
            MeanAbsoluteError = 0;
            ErrorBound = 1;
            IsTrained = true;
            return;
        }

        if (n == 1)
        {
            Slope = 0;
            Intercept = 0.5;
            MeanAbsoluteError = 0;
            ErrorBound = 1;
            IsTrained = true;
            return;
        }

        // xi = normalized key value, yi = normalized position (i / (n-1))
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;

        for (int i = 0; i < n; i++)
        {
            double xi = NormalizeKey(sortedKeys[i]);
            double yi = (double)i / (n - 1);

            sumX += xi;
            sumY += yi;
            sumXY += xi * yi;
            sumX2 += xi * xi;
        }

        double denominator = (double)n * sumX2 - sumX * sumX;

        if (Math.Abs(denominator) < 1e-15)
        {
            // All keys normalize to the same value (degenerate case)
            Slope = 0;
            Intercept = 0.5;
        }
        else
        {
            Slope = ((double)n * sumXY - sumX * sumY) / denominator;
            Intercept = (sumY - Slope * sumX) / n;
        }

        // Compute mean absolute error
        double totalError = 0;
        for (int i = 0; i < n; i++)
        {
            double xi = NormalizeKey(sortedKeys[i]);
            double predicted = Slope * xi + Intercept;
            double actual = (double)i / (n - 1);
            totalError += Math.Abs(predicted - actual);
        }

        MeanAbsoluteError = totalError / n;
        ErrorBound = Math.Max(1, (int)Math.Ceiling(MeanAbsoluteError * 1.5 * n));
        IsTrained = true;
    }

    /// <summary>
    /// Normalizes a key to a double in [0, 1] using the first 8 bytes as a big-endian ulong.
    /// </summary>
    internal static double NormalizeKey(byte[] key)
    {
        if (key == null || key.Length == 0)
            return 0.0;

        // Extract first 8 bytes as big-endian ulong
        ulong value = 0;
        int len = Math.Min(key.Length, 8);
        for (int i = 0; i < len; i++)
        {
            value |= (ulong)key[i] << ((7 - i) * 8);
        }

        // Normalize to [0, 1]
        return (double)value / ulong.MaxValue;
    }
}

/// <summary>
/// Gapped array data structure for ALEX leaf nodes.
/// Maintains sorted entries with intentional gaps for efficient insertion without restructuring.
/// </summary>
/// <remarks>
/// <para>
/// The gapped array leaves 30% of slots empty (gaps) to absorb insertions near predicted positions
/// without shifting large portions of the array. When the array becomes too dense (>80%),
/// it expands to double its size, redistributing entries with new gaps.
/// </para>
/// <para>
/// Lookups use exponential search from the predicted position within the error bound,
/// then fall back to linear scan for the final match.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-03 ALEX gapped array")]
public sealed class AlexGappedArray
{
    private (byte[]? Key, long Value)[] _array;
    private int _count;
    private readonly double _gapRatio;

    /// <summary>
    /// Shift threshold: maximum distance to search for a gap during insertion before triggering expansion.
    /// </summary>
    private const int ShiftThreshold = 64;

    /// <summary>
    /// Initializes a new gapped array with the specified initial capacity.
    /// </summary>
    /// <param name="initialCapacity">Initial capacity including gaps.</param>
    /// <param name="gapRatio">Fraction of slots reserved as gaps (default 0.3).</param>
    public AlexGappedArray(int initialCapacity = 64, double gapRatio = 0.3)
    {
        _gapRatio = Math.Clamp(gapRatio, 0.1, 0.5);
        _array = new (byte[]? Key, long Value)[Math.Max(initialCapacity, 4)];
        _count = 0;
    }

    /// <summary>
    /// Gets the number of non-null entries.
    /// </summary>
    public int Count => _count;

    /// <summary>
    /// Gets the total array size including gaps.
    /// </summary>
    public int Capacity => _array.Length;

    /// <summary>
    /// Gets the density ratio (count / capacity). Triggers restructure if too high (>0.8) or too low (&lt;0.2).
    /// </summary>
    public double DensityRatio => _count / (double)Capacity;

    /// <summary>
    /// Looks up a key starting from the predicted position within the error bound.
    /// Uses exponential search outward from predicted position, then linear scan.
    /// </summary>
    /// <param name="key">The key to find.</param>
    /// <param name="predictedPos">The CDF model's predicted position.</param>
    /// <param name="errorBound">Maximum positions to search from prediction.</param>
    /// <returns>The value if found, or null.</returns>
    public long? Lookup(byte[] key, int predictedPos, int errorBound)
    {
        int pos = Math.Clamp(predictedPos, 0, _array.Length - 1);
        int bound = Math.Max(errorBound, 1);

        // Check predicted position first
        if (_array[pos].Key != null && CompareKeys(_array[pos].Key!, key) == 0)
            return _array[pos].Value;

        // Exponential search outward from predicted position
        for (int step = 1; step <= bound; step *= 2)
        {
            int lo = Math.Max(0, pos - step);
            int hi = Math.Min(_array.Length - 1, pos + step);
            int prevLo = Math.Max(0, pos - (step / 2));
            int prevHi = Math.Min(_array.Length - 1, pos + (step / 2));

            // Search left side (newly exposed range)
            for (int i = lo; i < prevLo; i++)
            {
                if (_array[i].Key != null && CompareKeys(_array[i].Key!, key) == 0)
                    return _array[i].Value;
            }

            // Search right side (newly exposed range)
            for (int i = prevHi + 1; i <= hi; i++)
            {
                if (_array[i].Key != null && CompareKeys(_array[i].Key!, key) == 0)
                    return _array[i].Value;
            }
        }

        // Linear scan fallback within full error bound
        int searchLo = Math.Max(0, pos - bound);
        int searchHi = Math.Min(_array.Length - 1, pos + bound);
        for (int i = searchLo; i <= searchHi; i++)
        {
            if (_array[i].Key != null && CompareKeys(_array[i].Key!, key) == 0)
                return _array[i].Value;
        }

        return null;
    }

    /// <summary>
    /// Inserts a key-value pair at or near the predicted position.
    /// Shifts entries to the nearest gap if the slot is occupied. Expands if no gap is available nearby.
    /// </summary>
    /// <param name="key">The key to insert.</param>
    /// <param name="value">The value to associate.</param>
    /// <param name="predictedPos">The CDF model's predicted position.</param>
    public void Insert(byte[] key, long value, int predictedPos)
    {
        // Check if we need expansion before insert
        if (DensityRatio > 0.8)
        {
            Expand();
            // Recalculate predicted position for expanded array (approximately)
            predictedPos = Math.Clamp((int)(predictedPos * 2.0), 0, _array.Length - 1);
        }

        int pos = Math.Clamp(predictedPos, 0, _array.Length - 1);

        // If slot is empty, insert directly
        if (_array[pos].Key == null)
        {
            _array[pos] = (key, value);
            _count++;
            return;
        }

        // If key already exists at this position, update
        if (CompareKeys(_array[pos].Key!, key) == 0)
        {
            _array[pos] = (key, value);
            return;
        }

        // Find nearest gap and shift entries
        int gapPos = FindNearestGap(pos);
        if (gapPos < 0)
        {
            // No gap found within threshold - expand and retry
            Expand();
            pos = Math.Clamp(predictedPos, 0, _array.Length - 1);
            gapPos = FindNearestGap(pos);
            if (gapPos < 0)
            {
                // After expansion there must be gaps; use linear search for any gap
                for (int i = 0; i < _array.Length; i++)
                {
                    if (_array[i].Key == null)
                    {
                        gapPos = i;
                        break;
                    }
                }
            }
        }

        // Shift entries from insertion point to gap
        if (gapPos < pos)
        {
            // Gap is to the left: shift entries left
            for (int i = gapPos; i < pos; i++)
            {
                _array[i] = _array[i + 1];
            }
        }
        else if (gapPos > pos)
        {
            // Gap is to the right: shift entries right
            for (int i = gapPos; i > pos; i--)
            {
                _array[i] = _array[i - 1];
            }
        }

        // Determine correct position maintaining sort order
        int insertPos = pos;
        if (_array[pos].Key != null && CompareKeys(key, _array[pos].Key!) < 0)
        {
            // Key is smaller, insert at current pos (already shifted)
            insertPos = pos;
        }

        _array[insertPos] = (key, value);
        _count++;
    }

    /// <summary>
    /// Deletes a key from the gapped array. The vacated slot becomes a natural gap.
    /// </summary>
    /// <param name="key">The key to delete.</param>
    /// <param name="predictedPos">The CDF model's predicted position.</param>
    /// <param name="errorBound">Maximum positions to search from prediction.</param>
    /// <returns>True if the key was found and deleted.</returns>
    public bool Delete(byte[] key, int predictedPos, int errorBound)
    {
        int searchLo = Math.Max(0, predictedPos - errorBound);
        int searchHi = Math.Min(_array.Length - 1, predictedPos + errorBound);

        for (int i = searchLo; i <= searchHi; i++)
        {
            if (_array[i].Key != null && CompareKeys(_array[i].Key!, key) == 0)
            {
                _array[i] = (null, 0);
                _count--;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns all non-null entries in sorted order. Used for retraining the CDF model.
    /// </summary>
    /// <returns>Sorted list of key-value entries.</returns>
    public List<(byte[] Key, long Value)> GetSortedEntries()
    {
        var entries = new List<(byte[] Key, long Value)>(_count);
        for (int i = 0; i < _array.Length; i++)
        {
            if (_array[i].Key != null)
            {
                entries.Add((_array[i].Key!, _array[i].Value));
            }
        }

        // Entries should already be mostly sorted due to gapped array invariant,
        // but sort to guarantee correctness
        entries.Sort((a, b) => CompareKeys(a.Key, b.Key));
        return entries;
    }

    /// <summary>
    /// Bulk-loads sorted entries into the gapped array, distributing gaps evenly.
    /// </summary>
    /// <param name="sortedEntries">Entries in sorted key order.</param>
    internal void BulkLoad(IReadOnlyList<(byte[] Key, long Value)> sortedEntries)
    {
        int dataCount = sortedEntries.Count;
        int capacity = Math.Max(4, (int)(dataCount / (1.0 - _gapRatio)));
        _array = new (byte[]? Key, long Value)[capacity];
        _count = 0;

        if (dataCount == 0) return;

        // Distribute entries evenly with gaps
        double step = (double)capacity / dataCount;
        for (int i = 0; i < dataCount; i++)
        {
            int pos = Math.Min((int)(i * step), capacity - 1);
            // Find next free slot if position is already taken
            while (pos < capacity && _array[pos].Key != null)
                pos++;
            if (pos >= capacity)
            {
                // Wrap around to find any free slot
                for (pos = 0; pos < capacity; pos++)
                {
                    if (_array[pos].Key == null)
                        break;
                }
            }
            _array[pos] = (sortedEntries[i].Key, sortedEntries[i].Value);
            _count++;
        }
    }

    /// <summary>
    /// Finds the nearest gap (null slot) to the given position within the shift threshold.
    /// </summary>
    private int FindNearestGap(int pos)
    {
        for (int dist = 1; dist <= ShiftThreshold; dist++)
        {
            int left = pos - dist;
            int right = pos + dist;

            if (left >= 0 && _array[left].Key == null)
                return left;
            if (right < _array.Length && _array[right].Key == null)
                return right;
        }

        return -1;
    }

    /// <summary>
    /// Expands the array to double its size, redistributing entries with new gaps.
    /// </summary>
    private void Expand()
    {
        var entries = GetSortedEntries();
        int newCapacity = Math.Max(_array.Length * 2, 8);
        _array = new (byte[]? Key, long Value)[newCapacity];
        _count = 0;

        if (entries.Count == 0) return;

        double step = (double)newCapacity / entries.Count;
        for (int i = 0; i < entries.Count; i++)
        {
            int pos = Math.Min((int)(i * step), newCapacity - 1);
            while (pos < newCapacity && _array[pos].Key != null)
                pos++;
            if (pos >= newCapacity)
            {
                for (pos = 0; pos < newCapacity; pos++)
                {
                    if (_array[pos].Key == null) break;
                }
            }
            _array[pos] = entries[i];
            _count++;
        }
    }

    /// <summary>
    /// Compares two byte array keys lexicographically.
    /// </summary>
    private static int CompareKeys(byte[] a, byte[] b)
    {
        return BeTreeMessage.CompareKeys(a, b);
    }
}

/// <summary>
/// Base class for ALEX RMI (Recursive Model Index) nodes.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-03 ALEX RMI node")]
public abstract class AlexNode
{
    /// <summary>
    /// Gets whether this node is a leaf node.
    /// </summary>
    public abstract bool IsLeaf { get; }
}

/// <summary>
/// Internal node in the ALEX RMI. Uses a CDF model to route lookups to the correct child.
/// </summary>
/// <remarks>
/// The internal node's model predicts which child subtree a key belongs to,
/// enabling O(1) routing at each level of the RMI instead of binary search.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-03 ALEX RMI internal node")]
public sealed class AlexInternalNode : AlexNode
{
    /// <inheritdoc />
    public override bool IsLeaf => false;

    /// <summary>
    /// Gets the CDF model that predicts which child to descend to.
    /// </summary>
    public AlexCdfModel Model { get; } = new();

    /// <summary>
    /// Gets the array of child nodes (internal or leaf).
    /// </summary>
    public AlexNode[] Children { get; internal set; } = Array.Empty<AlexNode>();

    /// <summary>
    /// Gets the number of children (fan-out).
    /// </summary>
    public int NumChildren => Children.Length;

    /// <summary>
    /// Predicts the child index for a given key using the CDF model.
    /// </summary>
    /// <param name="key">The key to route.</param>
    /// <returns>Index into <see cref="Children"/>.</returns>
    public int PredictChild(byte[] key)
    {
        if (Children.Length == 0) return 0;
        return Model.PredictPosition(key, Children.Length);
    }
}

/// <summary>
/// Leaf node in the ALEX RMI. Contains a local CDF model and a gapped array of entries.
/// </summary>
/// <remarks>
/// Each leaf maintains its own CDF model trained on its local key distribution.
/// The gapped array stores actual key-value entries with gaps for efficient insertion.
/// When the local model's error drifts beyond thresholds, <see cref="NeedsRetrain"/> is set.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-03 ALEX RMI leaf node")]
public sealed class AlexLeafNode : AlexNode
{
    /// <inheritdoc />
    public override bool IsLeaf => true;

    /// <summary>
    /// Gets the local CDF model for this leaf's key distribution.
    /// </summary>
    public AlexCdfModel Model { get; } = new();

    /// <summary>
    /// Gets the gapped array holding this leaf's key-value entries.
    /// </summary>
    public AlexGappedArray Data { get; internal set; } = new();

    /// <summary>
    /// Gets or sets whether this leaf's model needs retraining due to error drift.
    /// </summary>
    public bool NeedsRetrain { get; set; }
}
