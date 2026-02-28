using System;
using System.Collections.Generic;
using System.Linq;

namespace DataWarehouse.SDK.Primitives.Probabilistic;

/// <summary>
/// Production-ready t-digest for quantile/percentile estimation.
/// Estimates quantiles (median, 99th percentile, etc.) with bounded error using logarithmic space.
/// Supports merging for distributed scenarios (T85.5, T85.6, T85.7).
/// </summary>
/// <remarks>
/// Properties:
/// - Error is smaller at extreme quantiles (1st, 99th percentile)
/// - Space complexity: O(compression * log(n))
/// - Merging maintains accuracy properties
///
/// Use cases:
/// - Latency percentile monitoring (P50, P95, P99)
/// - Response time SLA tracking
/// - Anomaly detection thresholds
/// - Data distribution analysis
///
/// Thread-safety: NOT thread-safe. Use external synchronization for concurrent access.
/// </remarks>
public sealed class TDigest : IProbabilisticStructure, IMergeable<TDigest>, IConfidenceReporting
{
    private readonly List<Centroid> _centroids;
    private readonly double _compression;
    private long _count;
    private double _min = double.MaxValue;
    private double _max = double.MinValue;
    private double _lastQueryQuantile;
    private bool _needsSort;

    /// <inheritdoc/>
    public string StructureType => "TDigest";

    /// <inheritdoc/>
    public long MemoryUsageBytes => _centroids.Count * 16 + 64;

    /// <inheritdoc/>
    public double ConfiguredErrorRate => 1.0 / _compression;

    /// <inheritdoc/>
    public long ItemCount => _count;

    /// <summary>
    /// Gets the compression factor (higher = more accurate, more memory).
    /// </summary>
    public double Compression => _compression;

    /// <summary>
    /// Gets the number of centroids currently in the digest.
    /// </summary>
    public int CentroidCount => _centroids.Count;

    /// <summary>
    /// Gets the minimum value seen.
    /// </summary>
    public double Min => _count > 0 ? _min : double.NaN;

    /// <summary>
    /// Gets the maximum value seen.
    /// </summary>
    public double Max => _count > 0 ? _max : double.NaN;

    /// <summary>
    /// Creates a t-digest with specified compression.
    /// </summary>
    /// <param name="compression">Compression factor (typically 100-500). Higher = more accurate.</param>
    public TDigest(double compression = 100)
    {
        if (compression < 10)
            throw new ArgumentOutOfRangeException(nameof(compression), "Compression must be at least 10.");

        _compression = compression;
        _centroids = new List<Centroid>((int)compression * 2);
    }

    /// <summary>
    /// Creates a t-digest from configuration.
    /// </summary>
    public TDigest(ProbabilisticConfig config)
        : this(1.0 / config.RelativeError)
    {
    }

    // Private constructor for cloning
    private TDigest(List<Centroid> centroids, double compression, long count, double min, double max)
    {
        _centroids = new List<Centroid>(centroids);
        _compression = compression;
        _count = count;
        _min = min;
        _max = max;
    }

    /// <summary>
    /// Adds a value to the digest.
    /// </summary>
    /// <param name="value">Value to add.</param>
    public void Add(double value)
    {
        Add(value, 1);
    }

    /// <summary>
    /// Adds a value with specified weight.
    /// </summary>
    /// <param name="value">Value to add.</param>
    /// <param name="weight">Weight/count of this value.</param>
    public void Add(double value, long weight = 1)
    {
        if (weight <= 0)
            throw new ArgumentOutOfRangeException(nameof(weight), "Weight must be positive.");
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentException("Value must be finite.", nameof(value));

        _min = Math.Min(_min, value);
        _max = Math.Max(_max, value);
        _count += weight;

        // Find insertion point using binary search
        if (_needsSort)
        {
            _centroids.Sort((a, b) => a.Mean.CompareTo(b.Mean));
            _needsSort = false;
        }

        var index = FindInsertionIndex(value);

        if (_centroids.Count == 0 || index == _centroids.Count)
        {
            // Add new centroid
            _centroids.Add(new Centroid { Mean = value, Weight = weight });
        }
        else
        {
            // Try to merge with nearest centroid
            var nearest = _centroids[index];
            var q = Quantile(nearest);
            var maxSize = MaxCentroidSize(q);

            if (nearest.Weight + weight <= maxSize)
            {
                // Merge into existing centroid
                _centroids[index] = new Centroid
                {
                    Mean = (nearest.Mean * nearest.Weight + value * weight) / (nearest.Weight + weight),
                    Weight = nearest.Weight + weight
                };
            }
            else
            {
                // Create new centroid
                _centroids.Insert(index, new Centroid { Mean = value, Weight = weight });
            }
        }

        // Compress if too many centroids
        if (_centroids.Count > 2 * _compression)
        {
            Compress();
        }
    }

    /// <summary>
    /// Adds multiple values to the digest.
    /// </summary>
    public void AddRange(IEnumerable<double> values)
    {
        foreach (var value in values)
        {
            Add(value);
        }
    }

    /// <summary>
    /// Estimates the value at a given quantile.
    /// </summary>
    /// <param name="quantile">Quantile (0.0 to 1.0). E.g., 0.5 for median, 0.99 for 99th percentile.</param>
    /// <returns>Estimated value at the quantile.</returns>
    public double Quantile(double quantile)
    {
        if (quantile < 0 || quantile > 1)
            throw new ArgumentOutOfRangeException(nameof(quantile), "Quantile must be between 0 and 1.");

        if (_count == 0)
            return double.NaN;

        if (_centroids.Count == 0)
            return double.NaN;

        if (_needsSort)
        {
            _centroids.Sort((a, b) => a.Mean.CompareTo(b.Mean));
            _needsSort = false;
        }

        _lastQueryQuantile = quantile;

        if (quantile == 0) return _min;
        if (quantile == 1) return _max;

        double targetWeight = quantile * _count;
        double runningWeight = 0;

        for (int i = 0; i < _centroids.Count; i++)
        {
            var centroid = _centroids[i];
            var halfWeight = centroid.Weight / 2.0;

            if (runningWeight + halfWeight >= targetWeight)
            {
                // Linear interpolation within centroid
                if (i == 0)
                    return _min + (centroid.Mean - _min) * (targetWeight / halfWeight);

                var prev = _centroids[i - 1];
                var slope = (centroid.Mean - prev.Mean) / (prev.Weight / 2.0 + halfWeight);
                return prev.Mean + slope * (targetWeight - runningWeight + prev.Weight / 2.0);
            }

            runningWeight += centroid.Weight;
        }

        return _max;
    }

    /// <summary>
    /// Gets the quantile value with probabilistic result metadata.
    /// </summary>
    public ProbabilisticResult<double> QueryQuantile(double quantile)
    {
        var value = Quantile(quantile);
        var (lower, upper) = GetConfidenceInterval();

        return new ProbabilisticResult<double>
        {
            Value = value,
            LowerBound = lower,
            UpperBound = upper,
            Confidence = 0.95,
            SourceStructure = StructureType
        };
    }

    /// <summary>
    /// Common quantile shortcuts.
    /// </summary>
    public double Median => Quantile(0.5);
    public double P90 => Quantile(0.90);
    public double P95 => Quantile(0.95);
    public double P99 => Quantile(0.99);
    public double P999 => Quantile(0.999);

    /// <summary>
    /// Estimates the quantile of a given value (inverse of Quantile).
    /// </summary>
    /// <param name="value">Value to find quantile for.</param>
    /// <returns>Estimated quantile (0.0 to 1.0).</returns>
    public double CumulativeDistribution(double value)
    {
        if (_count == 0 || _centroids.Count == 0)
            return double.NaN;

        if (value <= _min) return 0;
        if (value >= _max) return 1;

        if (_needsSort)
        {
            _centroids.Sort((a, b) => a.Mean.CompareTo(b.Mean));
            _needsSort = false;
        }

        double runningWeight = 0;

        for (int i = 0; i < _centroids.Count; i++)
        {
            var centroid = _centroids[i];

            if (centroid.Mean > value)
            {
                if (i == 0)
                    return (value - _min) / (centroid.Mean - _min) * (centroid.Weight / 2.0) / _count;

                var prev = _centroids[i - 1];
                var weight = runningWeight - prev.Weight / 2.0;
                var range = centroid.Mean - prev.Mean;
                var fraction = (value - prev.Mean) / range;
                return (weight + fraction * (prev.Weight / 2.0 + centroid.Weight / 2.0)) / _count;
            }

            runningWeight += centroid.Weight;
        }

        return 1;
    }

    /// <inheritdoc/>
    public (double Lower, double Upper) GetConfidenceInterval(double confidenceLevel = 0.95)
    {
        // Approximate error bounds based on centroid density at the queried quantile
        if (_count == 0) return (double.NaN, double.NaN);

        var value = Quantile(_lastQueryQuantile);
        var delta = (_max - _min) / _compression;

        // Error is smaller at extreme quantiles
        var scaleFactor = 4 * _lastQueryQuantile * (1 - _lastQueryQuantile);
        var error = delta * scaleFactor;

        return (value - error, value + error);
    }

    /// <inheritdoc/>
    public void Merge(TDigest other)
    {
        foreach (var centroid in other._centroids)
        {
            Add(centroid.Mean, centroid.Weight);
        }
    }

    /// <inheritdoc/>
    public TDigest Clone()
    {
        return new TDigest(_centroids, _compression, _count, _min, _max);
    }

    /// <inheritdoc/>
    public byte[] Serialize()
    {
        using var ms = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(ms);

        writer.Write(_compression);
        writer.Write(_count);
        writer.Write(_min);
        writer.Write(_max);
        writer.Write(_centroids.Count);

        foreach (var centroid in _centroids)
        {
            writer.Write(centroid.Mean);
            writer.Write(centroid.Weight);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Deserializes a t-digest from bytes.
    /// </summary>
    public static TDigest Deserialize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var ms = new System.IO.MemoryStream(data);
        using var reader = new System.IO.BinaryReader(ms);

        var compression = reader.ReadDouble();
        var count = reader.ReadInt64();
        var min = reader.ReadDouble();
        var max = reader.ReadDouble();
        var centroidCount = reader.ReadInt32();

        // Guard against OOM bomb from corrupt/malicious data with huge centroidCount
        // Max realistic t-digest has ~compression * 4 centroids; 100,000 is a conservative upper bound
        const int MaxCentroidCount = 100_000;
        if (centroidCount < 0 || centroidCount > MaxCentroidCount)
            throw new InvalidDataException(
                $"TDigest deserialization failed: centroidCount {centroidCount} is out of valid range [0, {MaxCentroidCount}].");

        var centroids = new List<Centroid>(centroidCount);
        for (int i = 0; i < centroidCount; i++)
        {
            centroids.Add(new Centroid
            {
                Mean = reader.ReadDouble(),
                Weight = reader.ReadInt64()
            });
        }

        return new TDigest(centroids, compression, count, min, max);
    }

    /// <inheritdoc/>
    public void Clear()
    {
        _centroids.Clear();
        _count = 0;
        _min = double.MaxValue;
        _max = double.MinValue;
    }

    private void Compress()
    {
        if (_centroids.Count < 2) return;

        _centroids.Sort((a, b) => a.Mean.CompareTo(b.Mean));

        var compressed = new List<Centroid>((int)_compression);
        var current = _centroids[0];
        // Track cumulative weight to compute quantile in O(1) per centroid,
        // avoiding the O(n) Quantile() scan inside the loop (finding P2-557).
        double cumulativeWeight = 0;

        for (int i = 1; i < _centroids.Count; i++)
        {
            var next = _centroids[i];
            // q = (cumulativeWeight + current.Weight / 2) / _count
            var q = _count > 0
                ? (cumulativeWeight + current.Weight / 2.0) / _count
                : 0.5;
            var maxSize = MaxCentroidSize(q);

            if (current.Weight + next.Weight <= maxSize)
            {
                // Merge — cumulative weight stays at its pre-merge value until we commit current
                var mergedWeight = current.Weight + next.Weight;
                current = new Centroid
                {
                    Mean = (current.Mean * current.Weight + next.Mean * next.Weight) / mergedWeight,
                    Weight = mergedWeight
                };
                // Don't advance cumulativeWeight yet — current is still being built
            }
            else
            {
                cumulativeWeight += current.Weight;
                compressed.Add(current);
                current = next;
            }
        }

        compressed.Add(current);
        _centroids.Clear();
        _centroids.AddRange(compressed);
    }

    private double MaxCentroidSize(double q)
    {
        // k-size function: allows larger centroids at extreme quantiles
        return 4 * _count * q * (1 - q) / _compression;
    }

    private double Quantile(Centroid centroid)
    {
        double weight = 0;
        for (int i = 0; i < _centroids.Count; i++)
        {
            var c = _centroids[i];
            if (c.Mean == centroid.Mean && c.Weight == centroid.Weight)
            {
                return (weight + c.Weight / 2.0) / _count;
            }
            weight += c.Weight;
        }
        return 0.5;
    }

    private int FindInsertionIndex(double value)
    {
        int low = 0, high = _centroids.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (_centroids[mid].Mean < value)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

    private struct Centroid
    {
        public double Mean { get; set; }
        public long Weight { get; set; }
    }
}
