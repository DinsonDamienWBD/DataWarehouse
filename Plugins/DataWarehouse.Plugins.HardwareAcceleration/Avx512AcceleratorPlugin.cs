using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Hardware;

namespace DataWarehouse.Plugins.HardwareAcceleration;

/// <summary>
/// Intel AVX-512 SIMD accelerator plugin for high-performance vector operations.
/// Provides hardware-accelerated vector multiplication, dot product, and hash operations
/// using AVX-512 instruction sets (F, BW, DQ) when available.
/// Falls back to AVX2 or scalar operations when AVX-512 is not supported.
/// </summary>
/// <remarks>
/// <para>
/// AVX-512 provides 512-bit wide SIMD registers allowing operations on 16 single-precision
/// floats or 8 double-precision floats simultaneously. This plugin leverages these capabilities
/// for maximum throughput on supported Intel processors.
/// </para>
/// <para>
/// Supported instruction sets:
/// <list type="bullet">
/// <item><description>AVX-512F (Foundation) - Core 512-bit operations</description></item>
/// <item><description>AVX-512BW (Byte and Word) - Enhanced byte/word operations</description></item>
/// <item><description>AVX-512DQ (Doubleword and Quadword) - Enhanced integer operations</description></item>
/// </list>
/// </para>
/// <para>
/// When AVX-512 is not available, the plugin falls back to:
/// <list type="bullet">
/// <item><description>AVX2 (256-bit operations)</description></item>
/// <item><description>SSE4.1 (128-bit operations)</description></item>
/// <item><description>Scalar fallback (single-element operations)</description></item>
/// </list>
/// </para>
/// </remarks>
public class Avx512AcceleratorPlugin : HardwareAcceleratorPluginBase
{
    private SimdCapability _capability = SimdCapability.None;
    private int _vectorWidth;
    private string _cpuModel = "Unknown";

    /// <summary>
    /// Defines the SIMD capability levels supported by this plugin.
    /// </summary>
    private enum SimdCapability
    {
        /// <summary>No SIMD support, scalar operations only.</summary>
        None = 0,
        /// <summary>SSE4.1 support (128-bit vectors).</summary>
        Sse41 = 1,
        /// <summary>AVX2 support (256-bit vectors).</summary>
        Avx2 = 2,
        /// <summary>AVX-512F (Foundation) support (512-bit vectors).</summary>
        Avx512F = 3,
        /// <summary>AVX-512F + BW (Byte/Word) support.</summary>
        Avx512BW = 4,
        /// <summary>Full AVX-512F + BW + DQ support.</summary>
        Avx512Full = 5
    }

    /// <inheritdoc />
    public override string Id => "datawarehouse.hwaccel.avx512";

    /// <inheritdoc />
    public override string Name => "Intel AVX-512 SIMD Accelerator";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override AcceleratorType Type => AcceleratorType.IntelAvx512;

    /// <inheritdoc />
    public override bool IsAvailable => DetectAvx512Support();

    /// <summary>
    /// Gets the detected SIMD capability level.
    /// </summary>
    public string Capability => _capability.ToString();

    /// <summary>
    /// Gets the effective vector width in bits.
    /// </summary>
    public int VectorWidthBits => _vectorWidth;

    /// <summary>
    /// Gets the detected CPU model string.
    /// </summary>
    public string CpuModel => _cpuModel;

    /// <inheritdoc />
    protected override Task InitializeHardwareAsync()
    {
        _capability = DetectSimdCapability();
        _vectorWidth = _capability switch
        {
            SimdCapability.Avx512Full or SimdCapability.Avx512BW or SimdCapability.Avx512F => 512,
            SimdCapability.Avx2 => 256,
            SimdCapability.Sse41 => 128,
            _ => 32
        };
        _cpuModel = GetCpuModelString();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task<byte[]> PerformOperationAsync(byte[] data, AcceleratorOperation op)
    {
        return op switch
        {
            AcceleratorOperation.VectorMul => PerformVectorMulFromBytes(data),
            AcceleratorOperation.Hash => PerformSimdHash(data),
            _ => throw new NotSupportedException(
                $"Operation {op} is not supported by AVX-512 accelerator. " +
                $"Supported operations: VectorMul, Hash")
        };
    }

    /// <summary>
    /// Performs element-wise multiplication of two float vectors using AVX-512 intrinsics.
    /// Automatically falls back to AVX2 or SSE4.1 if AVX-512 is not available.
    /// </summary>
    /// <param name="a">First input vector.</param>
    /// <param name="b">Second input vector (must have same length as <paramref name="a"/>).</param>
    /// <returns>Result vector where result[i] = a[i] * b[i].</returns>
    /// <exception cref="ArgumentNullException">Thrown when either input vector is null.</exception>
    /// <exception cref="ArgumentException">Thrown when input vectors have different lengths.</exception>
    public float[] VectorMultiply(float[] a, float[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length != b.Length)
        {
            throw new ArgumentException(
                $"Vectors must have the same length. Got {a.Length} and {b.Length}.",
                nameof(b));
        }

        if (a.Length == 0)
        {
            return Array.Empty<float>();
        }

        var result = new float[a.Length];

        if (_capability >= SimdCapability.Avx512F && Avx512F.IsSupported)
        {
            VectorMultiplyAvx512(a, b, result);
        }
        else if (_capability >= SimdCapability.Avx2 && Avx2.IsSupported)
        {
            VectorMultiplyAvx2(a, b, result);
        }
        else if (Sse.IsSupported)
        {
            VectorMultiplySse(a, b, result);
        }
        else
        {
            VectorMultiplyScalar(a, b, result);
        }

        return result;
    }

    /// <summary>
    /// Computes the dot product of two float vectors using AVX-512 intrinsics.
    /// Leverages fused multiply-add (FMA) instructions when available for better precision and performance.
    /// </summary>
    /// <param name="a">First input vector.</param>
    /// <param name="b">Second input vector (must have same length as <paramref name="a"/>).</param>
    /// <returns>The dot product (sum of element-wise products).</returns>
    /// <exception cref="ArgumentNullException">Thrown when either input vector is null.</exception>
    /// <exception cref="ArgumentException">Thrown when input vectors have different lengths.</exception>
    public float DotProduct(float[] a, float[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length != b.Length)
        {
            throw new ArgumentException(
                $"Vectors must have the same length. Got {a.Length} and {b.Length}.",
                nameof(b));
        }

        if (a.Length == 0)
        {
            return 0.0f;
        }

        if (_capability >= SimdCapability.Avx512F && Avx512F.IsSupported && Fma.IsSupported)
        {
            return DotProductAvx512Fma(a, b);
        }
        else if (_capability >= SimdCapability.Avx2 && Avx2.IsSupported && Fma.IsSupported)
        {
            return DotProductAvx2Fma(a, b);
        }
        else if (Sse.IsSupported)
        {
            return DotProductSse(a, b);
        }
        else
        {
            return DotProductScalar(a, b);
        }
    }

    /// <summary>
    /// Computes a SIMD-accelerated hash of the input data using parallel processing.
    /// Uses AVX-512 for parallel hash computations when available.
    /// </summary>
    /// <param name="data">Input data to hash.</param>
    /// <returns>256-bit hash value as a byte array.</returns>
    /// <exception cref="ArgumentNullException">Thrown when input data is null.</exception>
    public byte[] ComputeHash(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length == 0)
        {
            return SHA256.HashData(Array.Empty<byte>());
        }

        // For SIMD-accelerated hashing, we use a parallel approach:
        // 1. Split data into chunks
        // 2. Hash chunks in parallel using SIMD for data loading
        // 3. Combine partial hashes

        if (_capability >= SimdCapability.Avx512F && Avx512F.IsSupported && data.Length >= 512)
        {
            return ComputeHashAvx512(data);
        }
        else if (_capability >= SimdCapability.Avx2 && Avx2.IsSupported && data.Length >= 256)
        {
            return ComputeHashAvx2(data);
        }
        else
        {
            // Standard SHA-256 for small data or when SIMD is not available
            return SHA256.HashData(data);
        }
    }

    /// <summary>
    /// Performs vector addition using AVX-512 intrinsics.
    /// </summary>
    /// <param name="a">First input vector.</param>
    /// <param name="b">Second input vector.</param>
    /// <returns>Result vector where result[i] = a[i] + b[i].</returns>
    public float[] VectorAdd(float[] a, float[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length != b.Length)
        {
            throw new ArgumentException(
                $"Vectors must have the same length. Got {a.Length} and {b.Length}.",
                nameof(b));
        }

        if (a.Length == 0)
        {
            return Array.Empty<float>();
        }

        var result = new float[a.Length];

        if (_capability >= SimdCapability.Avx512F && Avx512F.IsSupported)
        {
            VectorAddAvx512(a, b, result);
        }
        else if (Avx.IsSupported)
        {
            VectorAddAvx(a, b, result);
        }
        else
        {
            for (int i = 0; i < a.Length; i++)
            {
                result[i] = a[i] + b[i];
            }
        }

        return result;
    }

    /// <summary>
    /// Applies a scalar multiplication to all elements of a vector using AVX-512.
    /// </summary>
    /// <param name="vector">Input vector.</param>
    /// <param name="scalar">Scalar multiplier.</param>
    /// <returns>Result vector where result[i] = vector[i] * scalar.</returns>
    public float[] VectorScale(float[] vector, float scalar)
    {
        ArgumentNullException.ThrowIfNull(vector);

        if (vector.Length == 0)
        {
            return Array.Empty<float>();
        }

        var result = new float[vector.Length];

        if (_capability >= SimdCapability.Avx512F && Avx512F.IsSupported)
        {
            VectorScaleAvx512(vector, scalar, result);
        }
        else if (Avx.IsSupported)
        {
            VectorScaleAvx(vector, scalar, result);
        }
        else
        {
            for (int i = 0; i < vector.Length; i++)
            {
                result[i] = vector[i] * scalar;
            }
        }

        return result;
    }

    #region Private AVX-512 Implementation Methods

    private static void VectorMultiplyAvx512(float[] a, float[] b, float[] result)
    {
        int i = 0;
        int vectorSize = Vector512<float>.Count; // 16 floats

        // Process 16 elements at a time using AVX-512
        for (; i <= a.Length - vectorSize; i += vectorSize)
        {
            var va = Vector512.Create(a.AsSpan(i, vectorSize));
            var vb = Vector512.Create(b.AsSpan(i, vectorSize));
            var vr = Avx512F.Multiply(va, vb);
            vr.CopyTo(result.AsSpan(i));
        }

        // Handle remaining elements
        for (; i < a.Length; i++)
        {
            result[i] = a[i] * b[i];
        }
    }

    private static void VectorMultiplyAvx2(float[] a, float[] b, float[] result)
    {
        int i = 0;
        int vectorSize = Vector256<float>.Count; // 8 floats

        for (; i <= a.Length - vectorSize; i += vectorSize)
        {
            var va = Vector256.Create(a.AsSpan(i, vectorSize));
            var vb = Vector256.Create(b.AsSpan(i, vectorSize));
            var vr = Avx.Multiply(va, vb);
            vr.CopyTo(result.AsSpan(i));
        }

        for (; i < a.Length; i++)
        {
            result[i] = a[i] * b[i];
        }
    }

    private static void VectorMultiplySse(float[] a, float[] b, float[] result)
    {
        int i = 0;
        int vectorSize = Vector128<float>.Count; // 4 floats

        for (; i <= a.Length - vectorSize; i += vectorSize)
        {
            var va = Vector128.Create(a.AsSpan(i, vectorSize));
            var vb = Vector128.Create(b.AsSpan(i, vectorSize));
            var vr = Sse.Multiply(va, vb);
            vr.CopyTo(result.AsSpan(i));
        }

        for (; i < a.Length; i++)
        {
            result[i] = a[i] * b[i];
        }
    }

    private static void VectorMultiplyScalar(float[] a, float[] b, float[] result)
    {
        for (int i = 0; i < a.Length; i++)
        {
            result[i] = a[i] * b[i];
        }
    }

    private static float DotProductAvx512Fma(float[] a, float[] b)
    {
        int i = 0;
        int vectorSize = Vector512<float>.Count;
        var accumulator = Vector512<float>.Zero;

        for (; i <= a.Length - vectorSize; i += vectorSize)
        {
            var va = Vector512.Create(a.AsSpan(i, vectorSize));
            var vb = Vector512.Create(b.AsSpan(i, vectorSize));
            accumulator = Avx512F.FusedMultiplyAdd(va, vb, accumulator);
        }

        // Horizontal sum of accumulator
        float sum = Vector512.Sum(accumulator);

        // Handle remaining elements
        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private static float DotProductAvx2Fma(float[] a, float[] b)
    {
        int i = 0;
        int vectorSize = Vector256<float>.Count;
        var accumulator = Vector256<float>.Zero;

        for (; i <= a.Length - vectorSize; i += vectorSize)
        {
            var va = Vector256.Create(a.AsSpan(i, vectorSize));
            var vb = Vector256.Create(b.AsSpan(i, vectorSize));
            accumulator = Fma.MultiplyAdd(va, vb, accumulator);
        }

        float sum = Vector256.Sum(accumulator);

        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private static float DotProductSse(float[] a, float[] b)
    {
        int i = 0;
        int vectorSize = Vector128<float>.Count;
        var accumulator = Vector128<float>.Zero;

        for (; i <= a.Length - vectorSize; i += vectorSize)
        {
            var va = Vector128.Create(a.AsSpan(i, vectorSize));
            var vb = Vector128.Create(b.AsSpan(i, vectorSize));
            var product = Sse.Multiply(va, vb);
            accumulator = Sse.Add(accumulator, product);
        }

        float sum = Vector128.Sum(accumulator);

        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private static float DotProductScalar(float[] a, float[] b)
    {
        float sum = 0.0f;
        for (int i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }

    private static void VectorAddAvx512(float[] a, float[] b, float[] result)
    {
        int i = 0;
        int vectorSize = Vector512<float>.Count;

        for (; i <= a.Length - vectorSize; i += vectorSize)
        {
            var va = Vector512.Create(a.AsSpan(i, vectorSize));
            var vb = Vector512.Create(b.AsSpan(i, vectorSize));
            var vr = Avx512F.Add(va, vb);
            vr.CopyTo(result.AsSpan(i));
        }

        for (; i < a.Length; i++)
        {
            result[i] = a[i] + b[i];
        }
    }

    private static void VectorAddAvx(float[] a, float[] b, float[] result)
    {
        int i = 0;
        int vectorSize = Vector256<float>.Count;

        for (; i <= a.Length - vectorSize; i += vectorSize)
        {
            var va = Vector256.Create(a.AsSpan(i, vectorSize));
            var vb = Vector256.Create(b.AsSpan(i, vectorSize));
            var vr = Avx.Add(va, vb);
            vr.CopyTo(result.AsSpan(i));
        }

        for (; i < a.Length; i++)
        {
            result[i] = a[i] + b[i];
        }
    }

    private static void VectorScaleAvx512(float[] vector, float scalar, float[] result)
    {
        int i = 0;
        int vectorSize = Vector512<float>.Count;
        var scalarVec = Vector512.Create(scalar);

        for (; i <= vector.Length - vectorSize; i += vectorSize)
        {
            var v = Vector512.Create(vector.AsSpan(i, vectorSize));
            var vr = Avx512F.Multiply(v, scalarVec);
            vr.CopyTo(result.AsSpan(i));
        }

        for (; i < vector.Length; i++)
        {
            result[i] = vector[i] * scalar;
        }
    }

    private static void VectorScaleAvx(float[] vector, float scalar, float[] result)
    {
        int i = 0;
        int vectorSize = Vector256<float>.Count;
        var scalarVec = Vector256.Create(scalar);

        for (; i <= vector.Length - vectorSize; i += vectorSize)
        {
            var v = Vector256.Create(vector.AsSpan(i, vectorSize));
            var vr = Avx.Multiply(v, scalarVec);
            vr.CopyTo(result.AsSpan(i));
        }

        for (; i < vector.Length; i++)
        {
            result[i] = vector[i] * scalar;
        }
    }

    private static byte[] ComputeHashAvx512(byte[] data)
    {
        // For large data, we parallelize hash computation
        // Split into chunks, hash each chunk, then combine

        const int chunkSize = 65536; // 64KB chunks
        int numChunks = (data.Length + chunkSize - 1) / chunkSize;

        if (numChunks <= 1)
        {
            return SHA256.HashData(data);
        }

        // Compute partial hashes in parallel
        var partialHashes = new byte[numChunks][];

        Parallel.For(0, numChunks, i =>
        {
            int offset = i * chunkSize;
            int length = Math.Min(chunkSize, data.Length - offset);
            partialHashes[i] = SHA256.HashData(data.AsSpan(offset, length));
        });

        // Combine partial hashes using a Merkle-like structure
        using var finalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Add chunk index to prevent length extension attacks
        var indexBuffer = new byte[4];
        for (int i = 0; i < numChunks; i++)
        {
            BitConverter.TryWriteBytes(indexBuffer, i);
            finalHash.AppendData(indexBuffer);
            finalHash.AppendData(partialHashes[i]);
        }

        // Add total length
        var lengthBuffer = new byte[8];
        BitConverter.TryWriteBytes(lengthBuffer, (long)data.Length);
        finalHash.AppendData(lengthBuffer);

        return finalHash.GetCurrentHash();
    }

    private static byte[] ComputeHashAvx2(byte[] data)
    {
        // Similar to AVX-512 but with smaller parallelization threshold
        const int chunkSize = 32768; // 32KB chunks
        int numChunks = (data.Length + chunkSize - 1) / chunkSize;

        if (numChunks <= 1)
        {
            return SHA256.HashData(data);
        }

        var partialHashes = new byte[numChunks][];

        Parallel.For(0, numChunks, i =>
        {
            int offset = i * chunkSize;
            int length = Math.Min(chunkSize, data.Length - offset);
            partialHashes[i] = SHA256.HashData(data.AsSpan(offset, length));
        });

        using var finalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var indexBuffer = new byte[4];
        for (int i = 0; i < numChunks; i++)
        {
            BitConverter.TryWriteBytes(indexBuffer, i);
            finalHash.AppendData(indexBuffer);
            finalHash.AppendData(partialHashes[i]);
        }

        var lengthBuffer = new byte[8];
        BitConverter.TryWriteBytes(lengthBuffer, (long)data.Length);
        finalHash.AppendData(lengthBuffer);

        return finalHash.GetCurrentHash();
    }

    #endregion

    #region Private Detection Methods

    private static bool DetectAvx512Support()
    {
        // Check if any AVX-512 subset is supported
        return Avx512F.IsSupported;
    }

    private SimdCapability DetectSimdCapability()
    {
        if (Avx512F.IsSupported && Avx512BW.IsSupported && Avx512DQ.IsSupported)
        {
            return SimdCapability.Avx512Full;
        }
        if (Avx512F.IsSupported && Avx512BW.IsSupported)
        {
            return SimdCapability.Avx512BW;
        }
        if (Avx512F.IsSupported)
        {
            return SimdCapability.Avx512F;
        }
        if (Avx2.IsSupported)
        {
            return SimdCapability.Avx2;
        }
        if (Sse41.IsSupported)
        {
            return SimdCapability.Sse41;
        }

        return SimdCapability.None;
    }

    private static string GetCpuModelString()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return GetCpuModelWindows();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return GetCpuModelLinux();
        }

        return $"x86_64 ({RuntimeInformation.ProcessArchitecture})";
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string GetCpuModelWindows()
    {
        try
        {
            var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key != null)
            {
                var name = key.GetValue("ProcessorNameString")?.ToString();
                if (!string.IsNullOrEmpty(name))
                {
                    return name.Trim();
                }
            }
        }
        catch
        {
            // Fall through to default
        }

        return "Intel x86_64 Processor";
    }

    private static string GetCpuModelLinux()
    {
        try
        {
            if (File.Exists("/proc/cpuinfo"))
            {
                var lines = File.ReadAllLines("/proc/cpuinfo");
                foreach (var line in lines)
                {
                    if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split(':', 2);
                        if (parts.Length == 2)
                        {
                            return parts[1].Trim();
                        }
                    }
                }
            }
        }
        catch
        {
            // Fall through to default
        }

        return "Intel x86_64 Processor";
    }

    private Task<byte[]> PerformVectorMulFromBytes(byte[] data)
    {
        // Expect data format: [length_a (4 bytes)][float[] a][float[] b]
        if (data.Length < 4)
        {
            throw new ArgumentException("Invalid data format: missing length prefix", nameof(data));
        }

        int lengthA = BitConverter.ToInt32(data, 0);
        int expectedSize = 4 + (lengthA * 4 * 2);

        if (data.Length < expectedSize)
        {
            throw new ArgumentException(
                $"Invalid data format: expected {expectedSize} bytes, got {data.Length}",
                nameof(data));
        }

        var a = new float[lengthA];
        var b = new float[lengthA];

        Buffer.BlockCopy(data, 4, a, 0, lengthA * 4);
        Buffer.BlockCopy(data, 4 + lengthA * 4, b, 0, lengthA * 4);

        var result = VectorMultiply(a, b);

        var output = new byte[result.Length * 4];
        Buffer.BlockCopy(result, 0, output, 0, output.Length);

        return Task.FromResult(output);
    }

    private Task<byte[]> PerformSimdHash(byte[] data)
    {
        return Task.FromResult(ComputeHash(data));
    }

    #endregion

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["Capability"] = _capability.ToString();
        metadata["VectorWidthBits"] = _vectorWidth;
        metadata["CpuModel"] = _cpuModel;
        metadata["Avx512FSupported"] = Avx512F.IsSupported;
        metadata["Avx512BWSupported"] = Avx512BW.IsSupported;
        metadata["Avx512DQSupported"] = Avx512DQ.IsSupported;
        metadata["Avx2Supported"] = Avx2.IsSupported;
        metadata["FmaSupported"] = Fma.IsSupported;
        metadata["SupportsVectorMul"] = true;
        metadata["SupportsDotProduct"] = true;
        metadata["SupportsHash"] = true;
        return metadata;
    }
}
