// <copyright file="MatrixMath.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

namespace DataWarehouse.Plugins.UltimateIoTIntegration.Strategies.SensorFusion;

/// <summary>
/// Represents a matrix with basic linear algebra operations.
/// No external dependencies - all operations implemented from scratch.
/// </summary>
public sealed class Matrix
{
    private readonly double[,] _data;

    /// <summary>
    /// Initializes a new instance of the <see cref="Matrix"/> class.
    /// </summary>
    /// <param name="rows">Number of rows.</param>
    /// <param name="cols">Number of columns.</param>
    public Matrix(int rows, int cols)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cols);
        Rows = rows;
        Cols = cols;
        _data = new double[rows, cols];
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Matrix"/> class from a 2D array.
    /// </summary>
    /// <param name="data">Initial matrix data.</param>
    public Matrix(double[,] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Rows = data.GetLength(0);
        Cols = data.GetLength(1);
        _data = (double[,])data.Clone();
    }

    /// <summary>Gets the number of rows.</summary>
    public int Rows { get; }

    /// <summary>Gets the number of columns.</summary>
    public int Cols { get; }

    /// <summary>
    /// Gets or sets the element at the specified position.
    /// </summary>
    /// <param name="row">Row index.</param>
    /// <param name="col">Column index.</param>
    public double this[int row, int col]
    {
        get => _data[row, col];
        set => _data[row, col] = value;
    }

    /// <summary>
    /// Creates an identity matrix.
    /// </summary>
    /// <param name="n">Size of the square matrix.</param>
    /// <returns>Identity matrix of size n×n.</returns>
    public static Matrix Identity(int n)
    {
        var result = new Matrix(n, n);
        for (int i = 0; i < n; i++)
        {
            result[i, i] = 1.0;
        }
        return result;
    }

    /// <summary>
    /// Multiplies two matrices.
    /// </summary>
    /// <param name="a">First matrix.</param>
    /// <param name="b">Second matrix.</param>
    /// <returns>Product matrix a × b.</returns>
    public static Matrix Multiply(Matrix a, Matrix b)
    {
        if (a.Cols != b.Rows)
            throw new ArgumentException("Matrix dimensions incompatible for multiplication");

        var result = new Matrix(a.Rows, b.Cols);
        for (int i = 0; i < a.Rows; i++)
        {
            for (int j = 0; j < b.Cols; j++)
            {
                double sum = 0.0;
                for (int k = 0; k < a.Cols; k++)
                {
                    sum += a[i, k] * b[k, j];
                }
                result[i, j] = sum;
            }
        }
        return result;
    }

    /// <summary>
    /// Computes the transpose of a matrix.
    /// </summary>
    /// <param name="m">Matrix to transpose.</param>
    /// <returns>Transposed matrix.</returns>
    public static Matrix Transpose(Matrix m)
    {
        var result = new Matrix(m.Cols, m.Rows);
        for (int i = 0; i < m.Rows; i++)
        {
            for (int j = 0; j < m.Cols; j++)
            {
                result[j, i] = m[i, j];
            }
        }
        return result;
    }

    /// <summary>
    /// Adds two matrices.
    /// </summary>
    /// <param name="a">First matrix.</param>
    /// <param name="b">Second matrix.</param>
    /// <returns>Sum matrix a + b.</returns>
    public static Matrix Add(Matrix a, Matrix b)
    {
        if (a.Rows != b.Rows || a.Cols != b.Cols)
            throw new ArgumentException("Matrix dimensions must match for addition");

        var result = new Matrix(a.Rows, a.Cols);
        for (int i = 0; i < a.Rows; i++)
        {
            for (int j = 0; j < a.Cols; j++)
            {
                result[i, j] = a[i, j] + b[i, j];
            }
        }
        return result;
    }

    /// <summary>
    /// Subtracts two matrices.
    /// </summary>
    /// <param name="a">First matrix.</param>
    /// <param name="b">Second matrix.</param>
    /// <returns>Difference matrix a - b.</returns>
    public static Matrix Subtract(Matrix a, Matrix b)
    {
        if (a.Rows != b.Rows || a.Cols != b.Cols)
            throw new ArgumentException("Matrix dimensions must match for subtraction");

        var result = new Matrix(a.Rows, a.Cols);
        for (int i = 0; i < a.Rows; i++)
        {
            for (int j = 0; j < a.Cols; j++)
            {
                result[i, j] = a[i, j] - b[i, j];
            }
        }
        return result;
    }

    /// <summary>
    /// Multiplies a matrix by a scalar.
    /// </summary>
    /// <param name="s">Scalar multiplier.</param>
    /// <param name="m">Matrix to multiply.</param>
    /// <returns>Scaled matrix s × m.</returns>
    public static Matrix ScalarMultiply(double s, Matrix m)
    {
        var result = new Matrix(m.Rows, m.Cols);
        for (int i = 0; i < m.Rows; i++)
        {
            for (int j = 0; j < m.Cols; j++)
            {
                result[i, j] = s * m[i, j];
            }
        }
        return result;
    }

    /// <summary>
    /// Computes the inverse of a matrix using Gauss-Jordan elimination.
    /// </summary>
    /// <param name="m">Matrix to invert (must be square and non-singular).</param>
    /// <returns>Inverse matrix m⁻¹.</returns>
    public static Matrix Inverse(Matrix m)
    {
        if (m.Rows != m.Cols)
            throw new ArgumentException("Matrix must be square to compute inverse");

        int n = m.Rows;
        var augmented = new Matrix(n, 2 * n);

        // Create augmented matrix [A | I]
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                augmented[i, j] = m[i, j];
                augmented[i, j + n] = i == j ? 1.0 : 0.0;
            }
        }

        // Gauss-Jordan elimination
        for (int i = 0; i < n; i++)
        {
            // Find pivot
            int pivotRow = i;
            double maxVal = Math.Abs(augmented[i, i]);
            for (int k = i + 1; k < n; k++)
            {
                double absVal = Math.Abs(augmented[k, i]);
                if (absVal > maxVal)
                {
                    maxVal = absVal;
                    pivotRow = k;
                }
            }

            if (maxVal < 1e-10)
                throw new InvalidOperationException("Matrix is singular and cannot be inverted");

            // Swap rows if needed
            if (pivotRow != i)
            {
                for (int j = 0; j < 2 * n; j++)
                {
                    (augmented[i, j], augmented[pivotRow, j]) = (augmented[pivotRow, j], augmented[i, j]);
                }
            }

            // Scale pivot row
            double pivot = augmented[i, i];
            for (int j = 0; j < 2 * n; j++)
            {
                augmented[i, j] /= pivot;
            }

            // Eliminate column
            for (int k = 0; k < n; k++)
            {
                if (k != i)
                {
                    double factor = augmented[k, i];
                    for (int j = 0; j < 2 * n; j++)
                    {
                        augmented[k, j] -= factor * augmented[i, j];
                    }
                }
            }
        }

        // Extract inverse from right half
        var result = new Matrix(n, n);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                result[i, j] = augmented[i, j + n];
            }
        }

        return result;
    }

    /// <summary>
    /// Converts matrix to a 1D array (for column vectors).
    /// </summary>
    /// <returns>Array containing matrix elements.</returns>
    public double[] ToArray()
    {
        if (Cols != 1)
            throw new InvalidOperationException("ToArray only works for column vectors");

        var result = new double[Rows];
        for (int i = 0; i < Rows; i++)
        {
            result[i] = _data[i, 0];
        }
        return result;
    }

    /// <summary>
    /// Creates a column vector from an array.
    /// </summary>
    /// <param name="values">Values for the column vector.</param>
    /// <returns>Column vector matrix.</returns>
    public static Matrix FromArray(double[] values)
    {
        var result = new Matrix(values.Length, 1);
        for (int i = 0; i < values.Length; i++)
        {
            result[i, 0] = values[i];
        }
        return result;
    }
}
