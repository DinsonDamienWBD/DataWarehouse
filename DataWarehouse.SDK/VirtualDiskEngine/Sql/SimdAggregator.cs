using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Sql;

/// <summary>
/// SIMD-accelerated aggregation functions using AVX2 intrinsics with scalar fallback.
/// Processes 8 int32 values per instruction for 4-8x aggregation speedup on supported hardware.
/// Runtime Avx2.IsSupported check ensures graceful degradation on non-AVX2 platforms.
/// </summary>
/// <remarks>
/// Implements VOPT-18: SIMD vectorized SUM/COUNT/MIN/MAX/AVG.
/// All methods handle tail elements (count not multiple of 8) via scalar loop on remainder.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: SIMD vectorized SQL execution (VOPT-18)")]
public static class SimdAggregator
{
    /// <summary>Whether AVX2 SIMD intrinsics are supported on the current hardware.</summary>
    public static bool SimdSupported
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Avx2.IsSupported;
    }

    /// <summary>Number of int32 elements processed per SIMD iteration.</summary>
    private const int Int32VectorCount = 8; // Vector256<int> = 8 x int32

    /// <summary>Number of float elements processed per SIMD iteration.</summary>
    private const int FloatVectorCount = 8; // Vector256<float> = 8 x float

    // ── SUM ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the sum of all int32 values using SIMD acceleration.
    /// Uses Vector256&lt;long&gt; accumulators to avoid overflow during summation.
    /// </summary>
    /// <param name="values">Input values to sum.</param>
    /// <returns>The sum as a 64-bit integer.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long SumInt32(ReadOnlySpan<int> values)
    {
        if (values.IsEmpty) return 0;

        if (SimdSupported)
            return SumInt32Simd(values);

        return SumInt32Scalar(values);
    }

    private static long SumInt32Simd(ReadOnlySpan<int> values)
    {
        int count = values.Length;
        int vectorized = count - (count % Int32VectorCount);

        // Use long accumulators to prevent overflow
        var sumLo = Vector256<long>.Zero;
        var sumHi = Vector256<long>.Zero;

        unsafe
        {
            fixed (int* ptr = values)
            {
                for (int i = 0; i < vectorized; i += Int32VectorCount)
                {
                    var vec = Avx.LoadVector256(ptr + i);

                    // Sign-extend lower 4 ints to long
                    var lo128 = vec.GetLower();
                    var hi128 = vec.GetUpper();

                    var loLong = Avx2.ConvertToVector256Int64(lo128);
                    var hiLong = Avx2.ConvertToVector256Int64(hi128);

                    sumLo = Avx2.Add(sumLo, loLong);
                    sumHi = Avx2.Add(sumHi, hiLong);
                }
            }
        }

        // Horizontal sum of both long vectors
        long result = HorizontalSumLong(sumLo) + HorizontalSumLong(sumHi);

        // Scalar tail
        for (int i = vectorized; i < count; i++)
            result += values[i];

        return result;
    }

    private static long SumInt32Scalar(ReadOnlySpan<int> values)
    {
        long sum = 0;
        for (int i = 0; i < values.Length; i++)
            sum += values[i];
        return sum;
    }

    // ── COUNT ───────────────────────────────────────────────────────────

    /// <summary>
    /// Counts values, optionally filtering for a specific value.
    /// Uses SIMD CompareEqual and MoveMask for accelerated counting.
    /// </summary>
    /// <param name="values">Input values.</param>
    /// <param name="filterValue">If specified, counts only elements equal to this value. If null, counts all elements.</param>
    /// <returns>The count of matching values.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Count(ReadOnlySpan<int> values, int? filterValue = null)
    {
        if (values.IsEmpty) return 0;
        if (filterValue == null) return values.Length;

        if (SimdSupported)
            return CountSimd(values, filterValue.Value);

        return CountScalar(values, filterValue.Value);
    }

    private static long CountSimd(ReadOnlySpan<int> values, int filterValue)
    {
        int count = values.Length;
        int vectorized = count - (count % Int32VectorCount);
        long result = 0;

        var filterVec = Vector256.Create(filterValue);

        unsafe
        {
            fixed (int* ptr = values)
            {
                for (int i = 0; i < vectorized; i += Int32VectorCount)
                {
                    var vec = Avx.LoadVector256(ptr + i);
                    var cmp = Avx2.CompareEqual(vec, filterVec);
                    int mask = Avx2.MoveMask(cmp.AsByte());
                    // Each matched int32 produces 4 set bytes in the mask (32-bit mask)
                    result += System.Numerics.BitOperations.PopCount((uint)mask) / 4;
                }
            }
        }

        // Scalar tail
        for (int i = vectorized; i < count; i++)
        {
            if (values[i] == filterValue) result++;
        }

        return result;
    }

    private static long CountScalar(ReadOnlySpan<int> values, int filterValue)
    {
        long count = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] == filterValue) count++;
        }
        return count;
    }

    // ── MIN ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the minimum int32 value using SIMD Min across 8-wide vectors.
    /// </summary>
    /// <param name="values">Input values.</param>
    /// <returns>The minimum value.</returns>
    /// <exception cref="ArgumentException">Thrown when values is empty.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MinInt32(ReadOnlySpan<int> values)
    {
        if (values.IsEmpty) throw new ArgumentException("Cannot compute minimum of empty span.", nameof(values));

        if (SimdSupported)
            return MinInt32Simd(values);

        return MinInt32Scalar(values);
    }

    private static int MinInt32Simd(ReadOnlySpan<int> values)
    {
        int count = values.Length;
        int vectorized = count - (count % Int32VectorCount);

        var minVec = Vector256.Create(int.MaxValue);

        unsafe
        {
            fixed (int* ptr = values)
            {
                for (int i = 0; i < vectorized; i += Int32VectorCount)
                {
                    var vec = Avx.LoadVector256(ptr + i);
                    minVec = Avx2.Min(minVec, vec);
                }
            }
        }

        int result = HorizontalMinInt32(minVec);

        // Scalar tail
        for (int i = vectorized; i < count; i++)
        {
            if (values[i] < result) result = values[i];
        }

        return result;
    }

    private static int MinInt32Scalar(ReadOnlySpan<int> values)
    {
        int min = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] < min) min = values[i];
        }
        return min;
    }

    // ── MAX ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the maximum int32 value using SIMD Max across 8-wide vectors.
    /// </summary>
    /// <param name="values">Input values.</param>
    /// <returns>The maximum value.</returns>
    /// <exception cref="ArgumentException">Thrown when values is empty.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MaxInt32(ReadOnlySpan<int> values)
    {
        if (values.IsEmpty) throw new ArgumentException("Cannot compute maximum of empty span.", nameof(values));

        if (SimdSupported)
            return MaxInt32Simd(values);

        return MaxInt32Scalar(values);
    }

    private static int MaxInt32Simd(ReadOnlySpan<int> values)
    {
        int count = values.Length;
        int vectorized = count - (count % Int32VectorCount);

        var maxVec = Vector256.Create(int.MinValue);

        unsafe
        {
            fixed (int* ptr = values)
            {
                for (int i = 0; i < vectorized; i += Int32VectorCount)
                {
                    var vec = Avx.LoadVector256(ptr + i);
                    maxVec = Avx2.Max(maxVec, vec);
                }
            }
        }

        int result = HorizontalMaxInt32(maxVec);

        // Scalar tail
        for (int i = vectorized; i < count; i++)
        {
            if (values[i] > result) result = values[i];
        }

        return result;
    }

    private static int MaxInt32Scalar(ReadOnlySpan<int> values)
    {
        int max = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] > max) max = values[i];
        }
        return max;
    }

    // ── AVERAGE ─────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the average of int32 values using SIMD-accelerated SUM and COUNT.
    /// </summary>
    /// <param name="values">Input values.</param>
    /// <returns>The average as a double.</returns>
    /// <exception cref="ArgumentException">Thrown when values is empty.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Average(ReadOnlySpan<int> values)
    {
        if (values.IsEmpty) throw new ArgumentException("Cannot compute average of empty span.", nameof(values));
        return (double)SumInt32(values) / values.Length;
    }

    // ── SUM FLOAT ───────────────────────────────────────────────────────

    /// <summary>
    /// Computes the sum of float values using SIMD acceleration.
    /// Falls back to Kahan summation for accuracy on non-SIMD platforms.
    /// </summary>
    /// <param name="values">Input float values.</param>
    /// <returns>The sum as a double for precision.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double SumFloat(ReadOnlySpan<float> values)
    {
        if (values.IsEmpty) return 0.0;

        if (SimdSupported)
            return SumFloatSimd(values);

        return SumFloatKahan(values);
    }

    private static double SumFloatSimd(ReadOnlySpan<float> values)
    {
        int count = values.Length;
        int vectorized = count - (count % FloatVectorCount);

        var sumVec = Vector256<float>.Zero;

        unsafe
        {
            fixed (float* ptr = values)
            {
                for (int i = 0; i < vectorized; i += FloatVectorCount)
                {
                    var vec = Avx.LoadVector256(ptr + i);
                    sumVec = Avx.Add(sumVec, vec);
                }
            }
        }

        // Horizontal sum
        double result = HorizontalSumFloat(sumVec);

        // Scalar tail with Kahan for accuracy
        double c = 0.0;
        for (int i = vectorized; i < count; i++)
        {
            double y = values[i] - c;
            double t = result + y;
            c = (t - result) - y;
            result = t;
        }

        return result;
    }

    private static double SumFloatKahan(ReadOnlySpan<float> values)
    {
        double sum = 0.0;
        double c = 0.0; // Compensation for lost low-order bits

        for (int i = 0; i < values.Length; i++)
        {
            double y = values[i] - c;
            double t = sum + y;
            c = (t - sum) - y;
            sum = t;
        }

        return sum;
    }

    // ── FILTER GREATER THAN ─────────────────────────────────────────────

    /// <summary>
    /// Returns indices of values greater than the threshold using SIMD CompareGreaterThan.
    /// </summary>
    /// <param name="values">Input values.</param>
    /// <param name="threshold">Threshold to compare against.</param>
    /// <returns>Array of indices where values[i] &gt; threshold.</returns>
    public static int[] FilterGreaterThan(ReadOnlySpan<int> values, int threshold)
    {
        if (values.IsEmpty) return Array.Empty<int>();

        if (SimdSupported)
            return FilterGreaterThanSimd(values, threshold);

        return FilterGreaterThanScalar(values, threshold);
    }

    private static int[] FilterGreaterThanSimd(ReadOnlySpan<int> values, int threshold)
    {
        int count = values.Length;
        int vectorized = count - (count % Int32VectorCount);

        var indices = new List<int>(count / 4); // Estimate 25% selectivity
        var thresholdVec = Vector256.Create(threshold);

        unsafe
        {
            fixed (int* ptr = values)
            {
                for (int i = 0; i < vectorized; i += Int32VectorCount)
                {
                    var vec = Avx.LoadVector256(ptr + i);
                    var cmp = Avx2.CompareGreaterThan(vec, thresholdVec);
                    int mask = Avx2.MoveMask(cmp.AsByte());

                    if (mask == 0) continue; // No matches in this vector

                    // Extract matching indices from byte mask (4 bytes per int32)
                    for (int j = 0; j < Int32VectorCount; j++)
                    {
                        if ((mask & (0xF << (j * 4))) != 0)
                            indices.Add(i + j);
                    }
                }
            }
        }

        // Scalar tail
        for (int i = vectorized; i < count; i++)
        {
            if (values[i] > threshold) indices.Add(i);
        }

        return indices.ToArray();
    }

    private static int[] FilterGreaterThanScalar(ReadOnlySpan<int> values, int threshold)
    {
        var indices = new List<int>();
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] > threshold) indices.Add(i);
        }
        return indices.ToArray();
    }

    // ── FILTER EQUALS ───────────────────────────────────────────────────

    /// <summary>
    /// Returns indices of values equal to the target using SIMD CompareEqual.
    /// </summary>
    /// <param name="values">Input values.</param>
    /// <param name="target">Target value to match.</param>
    /// <returns>Array of indices where values[i] == target.</returns>
    public static int[] FilterEquals(ReadOnlySpan<int> values, int target)
    {
        if (values.IsEmpty) return Array.Empty<int>();

        if (SimdSupported)
            return FilterEqualsSimd(values, target);

        return FilterEqualsScalar(values, target);
    }

    private static int[] FilterEqualsSimd(ReadOnlySpan<int> values, int target)
    {
        int count = values.Length;
        int vectorized = count - (count % Int32VectorCount);

        var indices = new List<int>(count / 4);
        var targetVec = Vector256.Create(target);

        unsafe
        {
            fixed (int* ptr = values)
            {
                for (int i = 0; i < vectorized; i += Int32VectorCount)
                {
                    var vec = Avx.LoadVector256(ptr + i);
                    var cmp = Avx2.CompareEqual(vec, targetVec);
                    int mask = Avx2.MoveMask(cmp.AsByte());

                    if (mask == 0) continue;

                    for (int j = 0; j < Int32VectorCount; j++)
                    {
                        if ((mask & (0xF << (j * 4))) != 0)
                            indices.Add(i + j);
                    }
                }
            }
        }

        // Scalar tail
        for (int i = vectorized; i < count; i++)
        {
            if (values[i] == target) indices.Add(i);
        }

        return indices.ToArray();
    }

    private static int[] FilterEqualsScalar(ReadOnlySpan<int> values, int target)
    {
        var indices = new List<int>();
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] == target) indices.Add(i);
        }
        return indices.ToArray();
    }

    // ── Horizontal Reduction Helpers ────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long HorizontalSumLong(Vector256<long> vec)
    {
        // Sum 4 long elements
        var hi128 = vec.GetUpper();
        var lo128 = vec.GetLower();
        var sum128 = Sse2.Add(lo128, hi128);
        // Extract and sum the two remaining longs
        return sum128.GetElement(0) + sum128.GetElement(1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HorizontalMinInt32(Vector256<int> vec)
    {
        // Reduce 8 -> 4 -> 2 -> 1
        var hi128 = vec.GetUpper();
        var lo128 = vec.GetLower();
        var min128 = Sse41.Min(lo128, hi128);
        // Shuffle and min to reduce 4 -> 2 -> 1
        var shuffled = Sse2.Shuffle(min128, 0b_01_00_11_10); // swap pairs
        min128 = Sse41.Min(min128, shuffled);
        shuffled = Sse2.Shuffle(min128, 0b_00_01_00_01); // swap within pairs
        min128 = Sse41.Min(min128, shuffled);
        return min128.GetElement(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HorizontalMaxInt32(Vector256<int> vec)
    {
        var hi128 = vec.GetUpper();
        var lo128 = vec.GetLower();
        var max128 = Sse41.Max(lo128, hi128);
        var shuffled = Sse2.Shuffle(max128, 0b_01_00_11_10);
        max128 = Sse41.Max(max128, shuffled);
        shuffled = Sse2.Shuffle(max128, 0b_00_01_00_01);
        max128 = Sse41.Max(max128, shuffled);
        return max128.GetElement(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double HorizontalSumFloat(Vector256<float> vec)
    {
        var hi128 = vec.GetUpper();
        var lo128 = vec.GetLower();
        var sum128 = Sse.Add(lo128, hi128);
        // hadd: [a+b, c+d, a+b, c+d]
        sum128 = Sse3.HorizontalAdd(sum128, sum128);
        sum128 = Sse3.HorizontalAdd(sum128, sum128);
        return sum128.GetElement(0);
    }
}
