using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace DataWarehouse.SDK.Mathematics;

/// <summary>
/// Reed-Solomon erasure coding implementation for RAID-like data protection.
/// Supports arbitrary data/parity shard configurations with efficient encoding and decoding.
/// Thread-safe for concurrent encoding/decoding operations on different data sets.
/// </summary>
public sealed class ReedSolomon
{
    private readonly int _dataShards;
    private readonly int _parityShards;
    private readonly int _totalShards;
    private readonly byte[][] _encodingMatrix;
    // P2-522: _lock field removed — all public methods are stateless over immutable fields;
    // no shared mutable state requires locking. ReedSolomon instances are thread-safe by construction.

    /// <summary>
    /// Gets the number of data shards configured for this instance.
    /// </summary>
    public int DataShards => _dataShards;

    /// <summary>
    /// Gets the number of parity shards configured for this instance.
    /// </summary>
    public int ParityShards => _parityShards;

    /// <summary>
    /// Gets the total number of shards (data + parity).
    /// </summary>
    public int TotalShards => _totalShards;

    /// <summary>
    /// Creates a new Reed-Solomon encoder/decoder.
    /// </summary>
    /// <param name="dataShards">Number of data shards (must be > 0)</param>
    /// <param name="parityShards">Number of parity shards (must be > 0)</param>
    /// <exception cref="ArgumentException">Thrown when shard counts are invalid</exception>
    public ReedSolomon(int dataShards, int parityShards)
    {
        if (dataShards <= 0)
        {
            throw new ArgumentException("Data shards must be greater than 0", nameof(dataShards));
        }

        if (parityShards <= 0)
        {
            throw new ArgumentException("Parity shards must be greater than 0", nameof(parityShards));
        }

        if (dataShards + parityShards > 256)
        {
            throw new ArgumentException("Total shards cannot exceed 256 (GF(2^8) limitation)");
        }

        _dataShards = dataShards;
        _parityShards = parityShards;
        _totalShards = dataShards + parityShards;

        _encodingMatrix = BuildEncodingMatrix();
    }

    /// <summary>
    /// Builds the Vandermonde encoding matrix for Reed-Solomon encoding.
    /// The matrix is constructed such that any k rows form an invertible matrix.
    /// </summary>
    /// <returns>Encoding matrix with dimensions [totalShards x dataShards]</returns>
    private byte[][] BuildEncodingMatrix()
    {
        var matrix = new byte[_totalShards][];

        // Identity matrix for data shards (first dataShards rows)
        for (int i = 0; i < _dataShards; i++)
        {
            matrix[i] = new byte[_dataShards];
            matrix[i][i] = 1;
        }

        // Vandermonde matrix for parity shards
        // Use row index (starting from 1) as the base for powers
        for (int i = _dataShards; i < _totalShards; i++)
        {
            matrix[i] = new byte[_dataShards];
            byte baseValue = (byte)(i - _dataShards + 1); // Start from 1, not 0
            for (int j = 0; j < _dataShards; j++)
            {
                matrix[i][j] = GaloisField.Power(baseValue, j);
            }
        }

        return matrix;
    }

    /// <summary>
    /// Encodes data shards to generate parity shards.
    /// </summary>
    /// <param name="shards">Array of all shards. First dataShards must contain data, remaining will be filled with parity.</param>
    /// <exception cref="ArgumentNullException">Thrown when shards is null</exception>
    /// <exception cref="ArgumentException">Thrown when shard configuration is invalid</exception>
    public void Encode(Span<byte[]> shards)
    {
        if (shards.Length != _totalShards)
        {
            throw new ArgumentException($"Expected {_totalShards} shards, got {shards.Length}", nameof(shards));
        }

        // Verify all data shards are non-null and same length
        int shardLength = shards[0]?.Length ?? throw new ArgumentException("First shard cannot be null", nameof(shards));

        for (int i = 0; i < _dataShards; i++)
        {
            if (shards[i] == null)
            {
                throw new ArgumentException($"Data shard {i} cannot be null", nameof(shards));
            }

            if (shards[i].Length != shardLength)
            {
                throw new ArgumentException($"All shards must be same length. Shard {i} has length {shards[i].Length}, expected {shardLength}", nameof(shards));
            }
        }

        // Initialize parity shards
        for (int i = _dataShards; i < _totalShards; i++)
        {
            if (shards[i] == null || shards[i].Length != shardLength)
            {
                shards[i] = new byte[shardLength];
            }
            else
            {
                Array.Clear(shards[i], 0, shardLength);
            }
        }

        // Compute parity shards using encoding matrix
        for (int i = _dataShards; i < _totalShards; i++)
        {
            var parityRow = _encodingMatrix[i];
            var parityShard = shards[i].AsSpan();

            for (int j = 0; j < _dataShards; j++)
            {
                var dataShard = shards[j].AsSpan();
                var coefficient = parityRow[j];

                if (coefficient == 0)
                {
                    continue;
                }

                // parity[k] ^= coefficient * data[j][k]
                for (int k = 0; k < shardLength; k++)
                {
                    parityShard[k] = GaloisField.Add(parityShard[k], GaloisField.Multiply(coefficient, dataShard[k]));
                }
            }
        }
    }

    /// <summary>
    /// Decodes data from any k-of-n available shards, reconstructing missing shards.
    /// </summary>
    /// <param name="shards">Array of all shards. Missing shards should be null or marked in shardPresent.</param>
    /// <param name="shardPresent">Boolean array indicating which shards are available (true) or missing (false).</param>
    /// <exception cref="ArgumentNullException">Thrown when required parameters are null</exception>
    /// <exception cref="ArgumentException">Thrown when insufficient shards are available for reconstruction</exception>
    public void Decode(Span<byte[]> shards, Span<bool> shardPresent)
    {
        if (shards.Length != _totalShards)
        {
            throw new ArgumentException($"Expected {_totalShards} shards, got {shards.Length}", nameof(shards));
        }

        if (shardPresent.Length != _totalShards)
        {
            throw new ArgumentException($"Expected {_totalShards} present flags, got {shardPresent.Length}", nameof(shardPresent));
        }

        // Count available shards
        int availableCount = 0;
        int shardLength = 0;

        for (int i = 0; i < _totalShards; i++)
        {
            if (shardPresent[i])
            {
                availableCount++;
                if (shards[i] != null && shardLength == 0)
                {
                    shardLength = shards[i].Length;
                }
            }
        }

        if (availableCount < _dataShards)
        {
            throw new ArgumentException($"Insufficient shards for reconstruction. Need {_dataShards}, have {availableCount}", nameof(shardPresent));
        }

        if (shardLength == 0)
        {
            throw new ArgumentException("Cannot determine shard length from available shards", nameof(shards));
        }

        // If all data shards are present, nothing to reconstruct
        bool needsReconstruction = false;
        for (int i = 0; i < _dataShards; i++)
        {
            if (!shardPresent[i])
            {
                needsReconstruction = true;
                break;
            }
        }

        if (!needsReconstruction)
        {
            return;
        }

        // Build decoding matrix from available shards
        var decodingMatrix = BuildDecodingMatrix(shardPresent);

        // Reconstruct missing shards
        for (int i = 0; i < _totalShards; i++)
        {
            if (!shardPresent[i])
            {
                // Allocate missing shard
                if (shards[i] == null || shards[i].Length != shardLength)
                {
                    shards[i] = new byte[shardLength];
                }
                else
                {
                    Array.Clear(shards[i], 0, shardLength);
                }

                var missingShard = shards[i].AsSpan();
                var decodingRow = decodingMatrix[i];

                // Reconstruct using linear combination of available shards
                int availableIndex = 0;
                for (int j = 0; j < _totalShards; j++)
                {
                    if (shardPresent[j])
                    {
                        var sourceShard = shards[j].AsSpan();
                        var coefficient = decodingRow[availableIndex];

                        for (int k = 0; k < shardLength; k++)
                        {
                            missingShard[k] = GaloisField.Add(missingShard[k], GaloisField.Multiply(coefficient, sourceShard[k]));
                        }

                        availableIndex++;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Builds a decoding matrix from the encoding matrix based on available shards.
    /// </summary>
    /// <param name="shardPresent">Boolean array indicating which shards are available</param>
    /// <returns>Decoding matrix that can reconstruct missing shards</returns>
    private byte[][] BuildDecodingMatrix(Span<bool> shardPresent)
    {
        // Select rows from encoding matrix corresponding to available shards
        var subMatrix = new byte[_dataShards][];
        int row = 0;

        for (int i = 0; i < _totalShards && row < _dataShards; i++)
        {
            if (shardPresent[i])
            {
                subMatrix[row] = new byte[_dataShards];
                Array.Copy(_encodingMatrix[i], subMatrix[row], _dataShards);
                row++;
            }
        }

        // Invert the submatrix
        var invertedMatrix = InvertMatrix(subMatrix);

        // Build full decoding matrix by multiplying inverted matrix with encoding matrix
        var decodingMatrix = MultiplyMatrices(invertedMatrix, _encodingMatrix);

        return decodingMatrix;
    }

    /// <summary>
    /// Inverts a square matrix in GF(2^8) using Gaussian elimination.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private byte[][] InvertMatrix(byte[][] matrix)
    {
        int n = matrix.Length;
        var augmented = new byte[n][];

        // Create augmented matrix [A|I]
        for (int i = 0; i < n; i++)
        {
            augmented[i] = new byte[n * 2];
            Array.Copy(matrix[i], augmented[i], n);
            augmented[i][n + i] = 1; // Identity matrix on right side
        }

        // Gaussian elimination with partial pivoting
        for (int col = 0; col < n; col++)
        {
            // Find pivot - start with current row
            int pivotRow = col;

            // If current pivot is zero, find a non-zero row below
            if (augmented[pivotRow][col] == 0)
            {
                for (int row = col + 1; row < n; row++)
                {
                    if (augmented[row][col] != 0)
                    {
                        pivotRow = row;
                        break;
                    }
                }
            }

            if (augmented[pivotRow][col] == 0)
            {
                throw new InvalidOperationException("Matrix is singular and cannot be inverted");
            }

            // Swap rows if needed
            if (pivotRow != col)
            {
                var temp = augmented[col];
                augmented[col] = augmented[pivotRow];
                augmented[pivotRow] = temp;
            }

            // Scale pivot row
            byte pivot = augmented[col][col];
            if (pivot != 1)
            {
                byte pivotInv = GaloisField.Inverse(pivot);
                for (int i = 0; i < n * 2; i++)
                {
                    augmented[col][i] = GaloisField.Multiply(augmented[col][i], pivotInv);
                }
            }

            // Eliminate column
            for (int row = 0; row < n; row++)
            {
                if (row != col && augmented[row][col] != 0)
                {
                    byte factor = augmented[row][col];
                    for (int i = 0; i < n * 2; i++)
                    {
                        augmented[row][i] = GaloisField.Subtract(augmented[row][i], GaloisField.Multiply(factor, augmented[col][i]));
                    }
                }
            }
        }

        // Extract inverse matrix from right side
        var inverse = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            inverse[i] = new byte[n];
            Array.Copy(augmented[i], n, inverse[i], 0, n);
        }

        return inverse;
    }

    /// <summary>
    /// Multiplies two matrices in GF(2^8).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private byte[][] MultiplyMatrices(byte[][] a, byte[][] b)
    {
        int aRows = a.Length;
        int aCols = a[0].Length;
        int bCols = b[0].Length;

        var result = new byte[aRows][];
        for (int i = 0; i < aRows; i++)
        {
            result[i] = new byte[bCols];
            for (int j = 0; j < bCols; j++)
            {
                byte sum = 0;
                for (int k = 0; k < aCols; k++)
                {
                    sum = GaloisField.Add(sum, GaloisField.Multiply(a[i][k], b[k][j]));
                }
                result[i][j] = sum;
            }
        }

        return result;
    }

    /// <summary>
    /// Validates that the encoding/decoding system is working correctly.
    /// Useful for testing and verification.
    /// </summary>
    /// <returns>True if the system passes self-test</returns>
    public bool SelfTest()
    {
        const int testSize = 64;
        var shards = new byte[_totalShards][];
        var shardPresent = new bool[_totalShards];

        // Create test data
        var random = new Random(42);
        for (int i = 0; i < _dataShards; i++)
        {
            shards[i] = new byte[testSize];
            random.NextBytes(shards[i]);
            shardPresent[i] = true;
        }

        // Make copies of original data
        var originalData = new byte[_dataShards][];
        for (int i = 0; i < _dataShards; i++)
        {
            originalData[i] = new byte[testSize];
            Array.Copy(shards[i], originalData[i], testSize);
        }

        // Encode
        Encode(shards);

        // Mark parity shards as present (they were just created)
        for (int i = _dataShards; i < _totalShards; i++)
        {
            shardPresent[i] = true;
        }

        // Mark some data shards as missing and decode
        for (int i = 0; i < _parityShards && i < _dataShards; i++)
        {
            shardPresent[i] = false;
            Array.Clear(shards[i], 0, testSize);
        }

        Decode(shards, shardPresent);

        // Verify reconstruction
        for (int i = 0; i < _dataShards; i++)
        {
            for (int j = 0; j < testSize; j++)
            {
                if (shards[i][j] != originalData[i][j])
                {
                    return false;
                }
            }
        }

        return true;
    }
}
