using System;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Threading;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Immutable snapshot of all morph-relevant metrics at a point in time.
/// Consumed by the index morph advisor for decision-making.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-06 MorphMetricsSnapshot")]
public sealed record MorphMetricsSnapshot
{
    /// <summary>Current number of objects in the index.</summary>
    public long ObjectCount { get; init; }

    /// <summary>Ratio of reads to total operations (reads + writes) over the last 60 seconds. Range [0..1].</summary>
    public double ReadWriteRatio { get; init; }

    /// <summary>50th percentile (median) operation latency in milliseconds.</summary>
    public double P50LatencyMs { get; init; }

    /// <summary>99th percentile operation latency in milliseconds.</summary>
    public double P99LatencyMs { get; init; }

    /// <summary>Shannon entropy of key prefix distribution. High = uniform, low = skewed.</summary>
    public double KeyEntropy { get; init; }

    /// <summary>Insert operations per second over a sliding 10-second window.</summary>
    public double InsertRate { get; init; }

    /// <summary>Current morph level at the time of snapshot.</summary>
    public MorphLevel CurrentLevel { get; init; }

    /// <summary>Timestamp when this snapshot was captured.</summary>
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Collects real-time metrics for morph decisions using thread-safe lock-free data structures.
/// Tracks object count, read/write ratio, latency percentiles, key entropy, and insert rate.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-06 MorphMetricsCollector")]
public sealed class MorphMetricsCollector
{
    private long _objectCount;
    private long _readCount;
    private long _writeCount;
    private long _rwWindowStartTicks;

    // Latency circular buffer (lock-free ring)
    private const int LatencyBufferSize = 10_000;
    private readonly long[] _latencyBuffer = new long[LatencyBufferSize];
    private long _latencyHead; // monotonically increasing write position
    private long _latencyCount; // total recorded (capped at LatencyBufferSize for percentile calc)

    // Insert rate: sliding 10-second window
    private const int InsertWindowSeconds = 10;
    private readonly long[] _insertBuckets = new long[InsertWindowSeconds];
    private long _insertWindowStart;

    // Key entropy: sample last 1,000 keys by first 4 bytes
    private const int EntropySampleSize = 1_000;
    private readonly uint[] _keySamples = new uint[EntropySampleSize];
    private long _keySampleHead;
    private long _keySampleCount;

    /// <summary>
    /// Initializes a new <see cref="MorphMetricsCollector"/>.
    /// </summary>
    public MorphMetricsCollector()
    {
        long now = Stopwatch.GetTimestamp();
        _rwWindowStartTicks = now;
        _insertWindowStart = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>
    /// Gets the current object count.
    /// </summary>
    public long ObjectCount => Interlocked.Read(ref _objectCount);

    /// <summary>
    /// Sets the object count directly (e.g. after a morph migration).
    /// </summary>
    public void SetObjectCount(long count) => Interlocked.Exchange(ref _objectCount, count);

    /// <summary>
    /// Increments the object count by one.
    /// </summary>
    public void IncrementObjectCount() => Interlocked.Increment(ref _objectCount);

    /// <summary>
    /// Decrements the object count by one.
    /// </summary>
    public void DecrementObjectCount() => Interlocked.Decrement(ref _objectCount);

    /// <summary>
    /// Records a read operation with the given latency in Stopwatch ticks.
    /// </summary>
    public void RecordRead(long latencyTicks)
    {
        Interlocked.Increment(ref _readCount);
        RecordLatency(latencyTicks);
        MaybeResetRwWindow();
    }

    /// <summary>
    /// Records a write operation with the given latency in Stopwatch ticks.
    /// </summary>
    public void RecordWrite(long latencyTicks)
    {
        Interlocked.Increment(ref _writeCount);
        RecordLatency(latencyTicks);
        MaybeResetRwWindow();
    }

    /// <summary>
    /// Records an insert operation and samples the key for entropy calculation.
    /// </summary>
    public void RecordInsert(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Interlocked.Increment(ref _objectCount);

        // Record insert for rate calculation
        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long windowStart = Interlocked.Read(ref _insertWindowStart);
        int bucketIdx = (int)((nowSec - windowStart) % InsertWindowSeconds);
        if (bucketIdx >= 0 && bucketIdx < InsertWindowSeconds)
        {
            Interlocked.Increment(ref _insertBuckets[bucketIdx]);
        }

        // Reset window if expired
        if (nowSec - windowStart >= InsertWindowSeconds)
        {
            if (Interlocked.CompareExchange(ref _insertWindowStart, nowSec, windowStart) == windowStart)
            {
                for (int i = 0; i < InsertWindowSeconds; i++)
                {
                    Interlocked.Exchange(ref _insertBuckets[i], 0);
                }
                Interlocked.Exchange(ref _insertBuckets[0], 1);
            }
        }

        // Sample key prefix for entropy
        uint prefix = 0;
        if (key.Length >= 4)
        {
            prefix = (uint)(key[0] << 24 | key[1] << 16 | key[2] << 8 | key[3]);
        }
        else if (key.Length > 0)
        {
            for (int i = 0; i < key.Length; i++)
            {
                prefix |= (uint)(key[i] << ((3 - i) * 8));
            }
        }

        long idx = Interlocked.Increment(ref _keySampleHead) - 1;
        _keySamples[idx % EntropySampleSize] = prefix;
        if (Interlocked.Read(ref _keySampleCount) < EntropySampleSize)
        {
            Interlocked.Increment(ref _keySampleCount);
        }
    }

    /// <summary>
    /// Records a delete operation (decrements object count).
    /// </summary>
    public void RecordDelete()
    {
        Interlocked.Decrement(ref _objectCount);
    }

    /// <summary>
    /// Gets the current read/write ratio (reads / (reads + writes)) for the current 60s window.
    /// </summary>
    public double ReadWriteRatio
    {
        get
        {
            long reads = Interlocked.Read(ref _readCount);
            long writes = Interlocked.Read(ref _writeCount);
            long total = reads + writes;
            return total == 0 ? 0.5 : (double)reads / total;
        }
    }

    /// <summary>
    /// Gets the median (P50) operation latency in milliseconds.
    /// </summary>
    public double P50LatencyMs => GetPercentileLatencyMs(0.50);

    /// <summary>
    /// Gets the P99 operation latency in milliseconds.
    /// </summary>
    public double P99LatencyMs => GetPercentileLatencyMs(0.99);

    /// <summary>
    /// Gets the Shannon entropy of the sampled key prefix distribution.
    /// </summary>
    public double KeyEntropy
    {
        get
        {
            long sampleCount = Interlocked.Read(ref _keySampleCount);
            if (sampleCount < 2) return 0.0;

            // Bucket by first 4 bytes (use dictionary for sparse distribution)
            var buckets = new System.Collections.Generic.Dictionary<uint, int>();
            int count = (int)Math.Min(sampleCount, EntropySampleSize);
            for (int i = 0; i < count; i++)
            {
                uint key = _keySamples[i];
                buckets.TryGetValue(key, out int c);
                buckets[key] = c + 1;
            }

            double entropy = 0.0;
            foreach (var kvp in buckets)
            {
                double p = (double)kvp.Value / count;
                if (p > 0)
                {
                    entropy -= p * Math.Log2(p);
                }
            }

            return entropy;
        }
    }

    /// <summary>
    /// Gets the current insert rate (inserts per second over the 10s window).
    /// </summary>
    public double InsertRate
    {
        get
        {
            long total = 0;
            for (int i = 0; i < InsertWindowSeconds; i++)
            {
                total += Interlocked.Read(ref _insertBuckets[i]);
            }
            return (double)total / InsertWindowSeconds;
        }
    }

    /// <summary>
    /// Captures an immutable snapshot of all current metrics.
    /// </summary>
    /// <param name="currentLevel">The current morph level to include in the snapshot.</param>
    /// <returns>A <see cref="MorphMetricsSnapshot"/> with all current values.</returns>
    public MorphMetricsSnapshot TakeSnapshot(MorphLevel currentLevel) => new()
    {
        ObjectCount = Interlocked.Read(ref _objectCount),
        ReadWriteRatio = ReadWriteRatio,
        P50LatencyMs = P50LatencyMs,
        P99LatencyMs = P99LatencyMs,
        KeyEntropy = KeyEntropy,
        InsertRate = InsertRate,
        CurrentLevel = currentLevel,
        Timestamp = DateTimeOffset.UtcNow
    };

    private void RecordLatency(long ticks)
    {
        long idx = Interlocked.Increment(ref _latencyHead) - 1;
        _latencyBuffer[idx % LatencyBufferSize] = ticks;
        if (Interlocked.Read(ref _latencyCount) < LatencyBufferSize)
        {
            Interlocked.Increment(ref _latencyCount);
        }
    }

    private double GetPercentileLatencyMs(double percentile)
    {
        long count = Interlocked.Read(ref _latencyCount);
        if (count == 0) return 0.0;

        int sampleSize = (int)Math.Min(count, LatencyBufferSize);
        var sorted = new long[sampleSize];
        Array.Copy(_latencyBuffer, sorted, sampleSize);
        Array.Sort(sorted);

        int index = (int)(percentile * (sampleSize - 1));
        long ticks = sorted[index];
        return (double)ticks / Stopwatch.Frequency * 1000.0;
    }

    private void MaybeResetRwWindow()
    {
        long now = Stopwatch.GetTimestamp();
        long start = Interlocked.Read(ref _rwWindowStartTicks);
        double elapsedSec = (double)(now - start) / Stopwatch.Frequency;

        if (elapsedSec >= 60.0)
        {
            if (Interlocked.CompareExchange(ref _rwWindowStartTicks, now, start) == start)
            {
                Interlocked.Exchange(ref _readCount, 0);
                Interlocked.Exchange(ref _writeCount, 0);
            }
        }
    }
}
