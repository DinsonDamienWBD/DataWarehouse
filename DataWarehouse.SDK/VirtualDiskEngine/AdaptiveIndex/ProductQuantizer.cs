using DataWarehouse.SDK.Contracts;
using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Product Quantization (PQ) for vector compression with Asymmetric Distance Computation (ADC).
/// Splits D-dimensional vectors into M sub-vectors, each quantized to one of K centroids.
/// </summary>
/// <remarks>
/// <para>
/// Product Quantization reduces storage from D*4 bytes (float32) to M bytes (uint8 centroid indices).
/// For example, a 128-dimensional vector (512 bytes) with M=8 sub-quantizers compresses to 8 bytes (64x).
/// </para>
/// <para>
/// Training uses k-means clustering on each sub-vector partition to produce a codebook of
/// M * K * (D/M) floats. Encoding maps each sub-vector to its nearest centroid index.
/// </para>
/// <para>
/// Asymmetric Distance Computation (ADC) pre-computes distances from the query sub-vectors
/// to all centroids, producing an M x K lookup table. Computing the distance to a compressed
/// vector then requires only M table lookups and additions (O(M)) instead of O(D) full
/// distance computation.
/// </para>
/// <para>
/// Thread safety: the codebook is immutable after training. Encode and ADC operations are
/// stateless and safe for concurrent use.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-13 HNSW+PQ")]
public sealed class ProductQuantizer
{
    private readonly int _m;           // Number of sub-quantizers
    private readonly int _k;           // Number of centroids per sub-quantizer
    private readonly int _dimension;   // Full vector dimension
    private readonly int _subDim;      // Dimension per sub-vector (D/M)
    private readonly int _maxIterations;

    // Codebook: [subQuantizer][centroid][subDimension]
    private float[][][] _codebook;
    private bool _trained;

    /// <summary>Gets the number of sub-quantizers (M).</summary>
    public int SubQuantizers => _m;

    /// <summary>Gets the number of centroids per sub-quantizer (K).</summary>
    public int Centroids => _k;

    /// <summary>Gets the full vector dimension.</summary>
    public int Dimension => _dimension;

    /// <summary>Gets the sub-vector dimension (D/M).</summary>
    public int SubDimension => _subDim;

    /// <summary>Gets whether the quantizer has been trained.</summary>
    public bool IsTrained => _trained;

    /// <summary>
    /// Gets the compression ratio: original bytes / compressed bytes.
    /// </summary>
    public float CompressionRatio => (float)(_dimension * sizeof(float)) / _m;

    /// <summary>
    /// Initializes a new Product Quantizer.
    /// </summary>
    /// <param name="dimension">Full vector dimension D. Must be divisible by M.</param>
    /// <param name="m">Number of sub-quantizers (default 8).</param>
    /// <param name="k">Number of centroids per sub-quantizer (default 256 for 1-byte codes).</param>
    /// <param name="maxIterations">Maximum k-means iterations (default 25).</param>
    public ProductQuantizer(int dimension, int m = 8, int k = 256, int maxIterations = 25)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(m);
        if (k <= 0 || k > 256) throw new ArgumentOutOfRangeException(nameof(k), "K must be in [1, 256] for byte codes.");
        if (dimension % m != 0) throw new ArgumentException($"Dimension {dimension} must be divisible by M={m}.", nameof(dimension));

        _dimension = dimension;
        _m = m;
        _k = k;
        _subDim = dimension / m;
        _maxIterations = maxIterations;
        _codebook = Array.Empty<float[][]>();
    }

    /// <summary>
    /// Trains the codebook using k-means clustering on each sub-vector partition.
    /// </summary>
    /// <param name="vectors">Training vectors. Each must have dimension D.</param>
    public void Train(float[][] vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        if (vectors.Length < _k)
            throw new ArgumentException($"Need at least {_k} training vectors, got {vectors.Length}.", nameof(vectors));

        _codebook = new float[_m][][];
        var random = new Random(42); // Deterministic for reproducibility

        for (int m = 0; m < _m; m++)
        {
            int offset = m * _subDim;

            // Extract sub-vectors for this partition
            float[][] subVectors = new float[vectors.Length][];
            for (int i = 0; i < vectors.Length; i++)
            {
                subVectors[i] = new float[_subDim];
                Array.Copy(vectors[i], offset, subVectors[i], 0, _subDim);
            }

            // k-means clustering
            _codebook[m] = KMeans(subVectors, _k, _maxIterations, random);
        }

        _trained = true;
    }

    /// <summary>
    /// Encodes a vector into PQ codes (centroid indices per sub-quantizer).
    /// </summary>
    /// <param name="vector">Vector of dimension D to encode.</param>
    /// <returns>Byte array of length M, each byte is a centroid index [0, K).</returns>
    public byte[] Encode(float[] vector)
    {
        if (!_trained) throw new InvalidOperationException("ProductQuantizer must be trained before encoding.");
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Length != _dimension) throw new ArgumentException($"Vector dimension {vector.Length} != {_dimension}.");

        byte[] code = new byte[_m];

        for (int m = 0; m < _m; m++)
        {
            int offset = m * _subDim;
            float bestDist = float.MaxValue;
            int bestIdx = 0;

            for (int c = 0; c < _k; c++)
            {
                float dist = SubVectorDistanceSq(vector, offset, _codebook[m][c]);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = c;
                }
            }

            code[m] = (byte)bestIdx;
        }

        return code;
    }

    /// <summary>
    /// Computes the ADC (Asymmetric Distance Computation) lookup table for a query vector.
    /// The table contains pre-computed distances from each query sub-vector to all centroids.
    /// </summary>
    /// <param name="query">Query vector of dimension D.</param>
    /// <returns>Lookup table of dimensions [M, K] where table[m, k] is the squared distance
    /// from query sub-vector m to centroid k.</returns>
    public float[,] ComputeAdcTable(float[] query)
    {
        if (!_trained) throw new InvalidOperationException("ProductQuantizer must be trained before computing ADC table.");
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length != _dimension) throw new ArgumentException($"Query dimension {query.Length} != {_dimension}.");

        var table = new float[_m, _k];

        for (int m = 0; m < _m; m++)
        {
            int offset = m * _subDim;
            for (int c = 0; c < _k; c++)
            {
                table[m, c] = SubVectorDistanceSq(query, offset, _codebook[m][c]);
            }
        }

        return table;
    }

    /// <summary>
    /// Computes approximate distance from a query to a compressed vector using an ADC table.
    /// O(M) per candidate instead of O(D).
    /// </summary>
    /// <param name="code">PQ code (byte[M] centroid indices).</param>
    /// <param name="adcTable">Pre-computed ADC table from <see cref="ComputeAdcTable"/>.</param>
    /// <returns>Approximate squared Euclidean distance.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float AdcDistance(byte[] code, float[,] adcTable)
    {
        float distance = 0f;
        for (int m = 0; m < _m; m++)
        {
            distance += adcTable[m, code[m]];
        }
        return distance;
    }

    /// <summary>
    /// Serializes the trained codebook to a stream.
    /// </summary>
    public void Serialize(Stream stream)
    {
        if (!_trained) throw new InvalidOperationException("Cannot serialize untrained quantizer.");
        ArgumentNullException.ThrowIfNull(stream);

        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(_dimension);
        writer.Write(_m);
        writer.Write(_k);
        writer.Write(_subDim);

        for (int m = 0; m < _m; m++)
        {
            for (int c = 0; c < _k; c++)
            {
                for (int d = 0; d < _subDim; d++)
                {
                    writer.Write(_codebook[m][c][d]);
                }
            }
        }
    }

    /// <summary>
    /// Deserializes a trained codebook from a stream.
    /// </summary>
    public static ProductQuantizer Deserialize(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        int dimension = reader.ReadInt32();
        int m = reader.ReadInt32();
        int k = reader.ReadInt32();
        int subDim = reader.ReadInt32();

        var pq = new ProductQuantizer(dimension, m, k);
        pq._codebook = new float[m][][];

        for (int mi = 0; mi < m; mi++)
        {
            pq._codebook[mi] = new float[k][];
            for (int c = 0; c < k; c++)
            {
                pq._codebook[mi][c] = new float[subDim];
                for (int d = 0; d < subDim; d++)
                {
                    pq._codebook[mi][c][d] = reader.ReadSingle();
                }
            }
        }

        pq._trained = true;
        return pq;
    }

    #region K-Means

    private static float[][] KMeans(float[][] data, int k, int maxIterations, Random random)
    {
        int dim = data[0].Length;
        int n = data.Length;

        // Initialize centroids using k-means++ initialization
        float[][] centroids = KMeansPlusPlusInit(data, k, random);
        int[] assignments = new int[n];

        for (int iter = 0; iter < maxIterations; iter++)
        {
            bool changed = false;

            // Assignment step: assign each point to nearest centroid
            for (int i = 0; i < n; i++)
            {
                float bestDist = float.MaxValue;
                int bestIdx = 0;
                for (int c = 0; c < k; c++)
                {
                    float dist = SquaredDistance(data[i], centroids[c]);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestIdx = c;
                    }
                }
                if (assignments[i] != bestIdx)
                {
                    assignments[i] = bestIdx;
                    changed = true;
                }
            }

            if (!changed) break;

            // Update step: recompute centroids
            int[] counts = new int[k];
            float[][] sums = new float[k][];
            for (int c = 0; c < k; c++)
                sums[c] = new float[dim];

            for (int i = 0; i < n; i++)
            {
                int c = assignments[i];
                counts[c]++;
                for (int d = 0; d < dim; d++)
                    sums[c][d] += data[i][d];
            }

            for (int c = 0; c < k; c++)
            {
                if (counts[c] > 0)
                {
                    for (int d = 0; d < dim; d++)
                        centroids[c][d] = sums[c][d] / counts[c];
                }
            }
        }

        return centroids;
    }

    private static float[][] KMeansPlusPlusInit(float[][] data, int k, Random random)
    {
        int dim = data[0].Length;
        int n = data.Length;
        float[][] centroids = new float[k][];

        // Pick first centroid randomly
        centroids[0] = (float[])data[random.Next(n)].Clone();

        float[] minDists = new float[n];
        Array.Fill(minDists, float.MaxValue);

        for (int c = 1; c < k; c++)
        {
            // Update distances to nearest chosen centroid
            float totalDist = 0;
            for (int i = 0; i < n; i++)
            {
                float dist = SquaredDistance(data[i], centroids[c - 1]);
                if (dist < minDists[i]) minDists[i] = dist;
                totalDist += minDists[i];
            }

            // Weighted random selection
            float threshold = (float)(random.NextDouble() * totalDist);
            float cumulative = 0;
            int selected = n - 1;
            for (int i = 0; i < n; i++)
            {
                cumulative += minDists[i];
                if (cumulative >= threshold)
                {
                    selected = i;
                    break;
                }
            }

            centroids[c] = (float[])data[selected].Clone();
        }

        return centroids;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SquaredDistance(float[] a, float[] b)
    {
        float sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            float d = a[i] - b[i];
            sum += d * d;
        }
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SubVectorDistanceSq(float[] vector, int offset, float[] centroid)
    {
        float sum = 0;
        for (int d = 0; d < centroid.Length; d++)
        {
            float diff = vector[offset + d] - centroid[d];
            sum += diff * diff;
        }
        return sum;
    }

    #endregion
}
