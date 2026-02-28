using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace DataWarehouse.SDK.Primitives.Probabilistic;

/// <summary>
/// Production-ready HyperLogLog for cardinality (distinct count) estimation.
/// Estimates the number of unique elements in a dataset using constant memory.
/// Supports merging for distributed scenarios (T85.2, T85.6, T85.7).
/// </summary>
/// <remarks>
/// Properties:
/// - Constant memory usage (~1.5 * 2^precision bytes)
/// - Standard error: 1.04 / sqrt(m) where m = 2^precision
/// - Can count up to 2^64 unique elements
///
/// Use cases:
/// - Unique visitor counting
/// - Query optimization (distinct count estimation)
/// - Network flow analysis
/// - Data deduplication estimation
///
/// Thread-safety: NOT thread-safe. Use external synchronization for concurrent access.
/// </remarks>
public sealed class HyperLogLog<T> : IProbabilisticStructure, IMergeable<HyperLogLog<T>>, IConfidenceReporting
{
    private readonly byte[] _registers;
    private readonly int _precision;
    private readonly int _registerCount;
    private readonly double _alpha;
    private readonly Func<T, byte[]> _serializer;
    private long _itemCount;
    private long _lastEstimate;

    /// <inheritdoc/>
    public string StructureType => "HyperLogLog";

    /// <inheritdoc/>
    public long MemoryUsageBytes => _registerCount + 32;

    /// <inheritdoc/>
    public double ConfiguredErrorRate => 1.04 / Math.Sqrt(_registerCount);

    /// <inheritdoc/>
    public long ItemCount => _itemCount;

    /// <summary>
    /// Gets the precision (log2 of register count).
    /// </summary>
    public int Precision => _precision;

    /// <summary>
    /// Gets the number of registers.
    /// </summary>
    public int RegisterCount => _registerCount;

    /// <summary>
    /// Gets the standard error of the estimate.
    /// </summary>
    public double StandardError => 1.04 / Math.Sqrt(_registerCount);

    /// <summary>
    /// Creates a HyperLogLog counter with specified precision.
    /// </summary>
    /// <param name="precision">Precision (4-18). Higher = more accurate, more memory. Default: 14 (~16KB, ~0.8% error)</param>
    /// <param name="serializer">Optional custom serializer for type T.</param>
    public HyperLogLog(int precision = 14, Func<T, byte[]>? serializer = null)
    {
        if (precision < 4 || precision > 18)
            throw new ArgumentOutOfRangeException(nameof(precision), "Precision must be between 4 and 18.");

        _precision = precision;
        _registerCount = 1 << precision;
        _registers = new byte[_registerCount];
        _alpha = GetAlpha(_registerCount);
        _serializer = serializer ?? (item => Encoding.UTF8.GetBytes(item?.ToString() ?? string.Empty));
    }

    /// <summary>
    /// Creates a HyperLogLog from configuration.
    /// </summary>
    public HyperLogLog(ProbabilisticConfig config, Func<T, byte[]>? serializer = null)
        : this(ErrorToPrecision(config.RelativeError), serializer)
    {
    }

    // Private constructor for cloning/deserialization
    private HyperLogLog(byte[] registers, int precision, long itemCount, Func<T, byte[]> serializer)
    {
        _registers = registers;
        _precision = precision;
        _registerCount = registers.Length;
        _alpha = GetAlpha(_registerCount);
        _itemCount = itemCount;
        _serializer = serializer;
    }

    /// <summary>
    /// Adds an item to the counter.
    /// </summary>
    /// <param name="item">Item to add.</param>
    public void Add(T item)
    {
        var bytes = _serializer(item);
        var hash = ComputeHash(bytes);

        // First p bits determine register index
        var index = (int)(hash >> (64 - _precision));

        // Remaining bits used to find first 1 bit position
        var remaining = (hash << _precision) | (1UL << (_precision - 1)); // Add sentinel bit
        var rank = (byte)(LeadingZeros(remaining) + 1);

        if (rank > _registers[index])
            _registers[index] = rank;

        _itemCount++;
    }

    /// <summary>
    /// Estimates the cardinality (number of unique items).
    /// </summary>
    /// <returns>Estimated distinct count.</returns>
    public long Count()
    {
        // Calculate harmonic mean of 2^(-register[i])
        double sum = 0;
        int zeros = 0;

        for (int i = 0; i < _registerCount; i++)
        {
            sum += Math.Pow(2, -_registers[i]);
            if (_registers[i] == 0)
                zeros++;
        }

        double estimate = _alpha * _registerCount * _registerCount / sum;

        // Small range correction
        if (estimate <= 2.5 * _registerCount)
        {
            if (zeros > 0)
            {
                // Linear counting for small cardinalities
                estimate = _registerCount * Math.Log((double)_registerCount / zeros);
            }
        }
        // Large range correction (for 64-bit hash)
        else if (estimate > (1.0 / 30.0) * Math.Pow(2, 64))
        {
            estimate = -Math.Pow(2, 64) * Math.Log(1 - estimate / Math.Pow(2, 64));
        }

        _lastEstimate = Math.Max(0, (long)estimate);
        return _lastEstimate;
    }

    /// <summary>
    /// Gets the cardinality estimate with probabilistic result metadata.
    /// </summary>
    /// <returns>Probabilistic result with confidence interval.</returns>
    public ProbabilisticResult<long> Query()
    {
        var estimate = Count();
        var (lower, upper) = GetConfidenceInterval();

        return new ProbabilisticResult<long>
        {
            Value = estimate,
            Confidence = 0.95,
            LowerBound = lower,
            UpperBound = upper,
            SourceStructure = StructureType
        };
    }

    /// <inheritdoc/>
    public (double Lower, double Upper) GetConfidenceInterval(double confidenceLevel = 0.95)
    {
        var estimate = (double)_lastEstimate;
        if (estimate == 0) return (0, 0);

        // 95% confidence interval: estimate +/- 1.96 * sigma
        // For HyperLogLog: sigma = standard_error * estimate
        double zScore = confidenceLevel switch
        {
            >= 0.99 => 2.576,
            >= 0.95 => 1.96,
            >= 0.90 => 1.645,
            _ => 1.0
        };

        double sigma = StandardError * estimate;
        return (Math.Max(0, estimate - zScore * sigma), estimate + zScore * sigma);
    }

    /// <inheritdoc/>
    public void Merge(HyperLogLog<T> other)
    {
        if (other._registerCount != _registerCount)
            throw new ArgumentException("Cannot merge HyperLogLog counters with different precisions.");

        for (int i = 0; i < _registerCount; i++)
        {
            _registers[i] = Math.Max(_registers[i], other._registers[i]);
        }

        _itemCount += other._itemCount;
    }

    /// <inheritdoc/>
    public HyperLogLog<T> Clone()
    {
        var clonedRegisters = new byte[_registerCount];
        Array.Copy(_registers, clonedRegisters, _registerCount);
        return new HyperLogLog<T>(clonedRegisters, _precision, _itemCount, _serializer);
    }

    /// <inheritdoc/>
    public byte[] Serialize()
    {
        var bytes = new byte[_registerCount + 12]; // registers + header

        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), _precision);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 8), _itemCount);
        Array.Copy(_registers, 0, bytes, 12, _registerCount);

        return bytes;
    }

    /// <summary>
    /// Deserializes a HyperLogLog counter from bytes.
    /// </summary>
    public static HyperLogLog<T> Deserialize(byte[] data, Func<T, byte[]>? serializer = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 12)
            throw new ArgumentException("Data too short for HyperLogLog deserialization header (minimum 12 bytes).", nameof(data));

        var precision = BitConverter.ToInt32(data, 0);
        if (precision < 4 || precision > 18) // Standard HLL precision range; 18 = 256K registers
            throw new ArgumentException($"Invalid precision {precision} in serialized HyperLogLog data. Must be 4-18.", nameof(data));

        var itemCount = BitConverter.ToInt64(data, 4);
        var registerCount = 1 << precision;

        if (data.Length < 12 + registerCount)
            throw new ArgumentException($"Data too short: need {12 + registerCount} bytes but got {data.Length}.", nameof(data));

        var registers = new byte[registerCount];
        Array.Copy(data, 12, registers, 0, registerCount);

        return new HyperLogLog<T>(
            registers,
            precision,
            itemCount,
            serializer ?? (item => Encoding.UTF8.GetBytes(item?.ToString() ?? string.Empty))
        );
    }

    /// <inheritdoc/>
    public void Clear()
    {
        Array.Clear(_registers, 0, _registerCount);
        _itemCount = 0;
        _lastEstimate = 0;
    }

    /// <summary>
    /// Gets the estimated space savings compared to exact counting.
    /// </summary>
    /// <param name="uniqueItemSizeBytes">Average size of each unique item in bytes.</param>
    /// <returns>Space savings ratio (1.0 = same size, 100.0 = 100x smaller).</returns>
    public double GetSpaceSavings(int uniqueItemSizeBytes = 8)
    {
        var exactSize = _lastEstimate * uniqueItemSizeBytes;
        if (exactSize == 0) return 1.0;
        return (double)exactSize / MemoryUsageBytes;
    }

    private static double GetAlpha(int registerCount)
    {
        return registerCount switch
        {
            16 => 0.673,
            32 => 0.697,
            64 => 0.709,
            _ => 0.7213 / (1 + 1.079 / registerCount)
        };
    }

    private static int ErrorToPrecision(double error)
    {
        // Standard error = 1.04 / sqrt(2^p)
        // 2^p = (1.04 / error)^2
        // p = log2((1.04 / error)^2) = 2 * log2(1.04 / error)
        var precision = (int)Math.Ceiling(2 * Math.Log(1.04 / error) / Math.Log(2));
        return Math.Clamp(precision, 4, 18);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ComputeHash(byte[] data)
    {
        const ulong c1 = 0x87c37b91114253d5UL;
        const ulong c2 = 0x4cf5ad432745937fUL;

        ulong h = 0;
        int length = data.Length;
        int blocks = length / 8;

        for (int i = 0; i < blocks; i++)
        {
            ulong k = BitConverter.ToUInt64(data, i * 8);
            k *= c1;
            k = (k << 31) | (k >> 33);
            k *= c2;
            h ^= k;
            h = ((h << 27) | (h >> 37)) * 5 + 0x52dce729;
        }

        ulong tail = 0;
        int tailStart = blocks * 8;
        for (int i = 0; i < length - tailStart; i++)
        {
            tail |= (ulong)data[tailStart + i] << (i * 8);
        }

        tail *= c1;
        tail = (tail << 31) | (tail >> 33);
        tail *= c2;
        h ^= tail;

        h ^= (ulong)length;
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33;
        h *= 0xc4ceb9fe1a85ec53UL;
        h ^= h >> 33;

        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LeadingZeros(ulong value)
    {
        if (value == 0) return 64;
        int count = 0;
        while ((value & 0x8000000000000000UL) == 0)
        {
            count++;
            value <<= 1;
        }
        return count;
    }
}
