// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace DataWarehouse.Plugins.AIAgents.Capabilities.Semantics;

/// <summary>
/// Internal vector math utilities for semantic operations.
/// Provides efficient implementations of common vector operations used
/// in similarity computations, duplicate detection, and clustering.
/// </summary>
internal static class VectorMath
{
    /// <summary>
    /// Calculates cosine similarity between two vectors.
    /// </summary>
    /// <param name="a">First vector.</param>
    /// <param name="b">Second vector.</param>
    /// <returns>Similarity value between -1 (opposite) and 1 (identical).</returns>
    /// <remarks>
    /// Cosine similarity measures the angle between vectors, making it
    /// invariant to magnitude. This is ideal for comparing document
    /// embeddings where direction matters more than length.
    /// </remarks>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a == null || b == null || a.Length == 0 || b.Length == 0)
            return 0f;

        if (a.Length != b.Length)
            throw new ArgumentException("Vectors must have same length");

        float dotProduct = 0;
        float normA = 0;
        float normB = 0;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var magnitude = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return magnitude > 0 ? dotProduct / magnitude : 0f;
    }

    /// <summary>
    /// Calculates Euclidean distance between two vectors.
    /// </summary>
    /// <param name="a">First vector.</param>
    /// <param name="b">Second vector.</param>
    /// <returns>Euclidean distance (L2 norm).</returns>
    public static float EuclideanDistance(float[] a, float[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return float.MaxValue;

        float sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var diff = a[i] - b[i];
            sum += diff * diff;
        }

        return MathF.Sqrt(sum);
    }

    /// <summary>
    /// Normalizes a vector to unit length.
    /// </summary>
    /// <param name="vector">Vector to normalize.</param>
    /// <returns>Unit vector in the same direction.</returns>
    public static float[] Normalize(float[] vector)
    {
        if (vector == null || vector.Length == 0)
            return Array.Empty<float>();

        float norm = 0;
        for (int i = 0; i < vector.Length; i++)
        {
            norm += vector[i] * vector[i];
        }

        norm = MathF.Sqrt(norm);
        if (norm == 0) return vector;

        var result = new float[vector.Length];
        for (int i = 0; i < vector.Length; i++)
        {
            result[i] = vector[i] / norm;
        }

        return result;
    }

    /// <summary>
    /// Calculates dot product of two vectors.
    /// </summary>
    /// <param name="a">First vector.</param>
    /// <param name="b">Second vector.</param>
    /// <returns>Dot product (inner product).</returns>
    public static float DotProduct(float[] a, float[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return 0f;

        float sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary>
    /// Adds two vectors element-wise.
    /// </summary>
    /// <param name="a">First vector.</param>
    /// <param name="b">Second vector.</param>
    /// <returns>Element-wise sum.</returns>
    public static float[] Add(float[] a, float[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
            throw new ArgumentException("Vectors must have same length");

        var result = new float[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            result[i] = a[i] + b[i];
        }

        return result;
    }

    /// <summary>
    /// Calculates average of multiple vectors.
    /// </summary>
    /// <param name="vectors">Collection of vectors to average.</param>
    /// <returns>Centroid vector.</returns>
    public static float[] Average(IEnumerable<float[]> vectors)
    {
        var list = vectors.ToList();
        if (list.Count == 0)
            return Array.Empty<float>();

        var dim = list[0].Length;
        var result = new float[dim];

        foreach (var vector in list)
        {
            for (int i = 0; i < Math.Min(dim, vector.Length); i++)
            {
                result[i] += vector[i];
            }
        }

        for (int i = 0; i < dim; i++)
        {
            result[i] /= list.Count;
        }

        return result;
    }

    /// <summary>
    /// Calculates the magnitude (L2 norm) of a vector.
    /// </summary>
    /// <param name="vector">Input vector.</param>
    /// <returns>Magnitude of the vector.</returns>
    public static float Magnitude(float[] vector)
    {
        if (vector == null || vector.Length == 0)
            return 0f;

        float sum = 0;
        for (int i = 0; i < vector.Length; i++)
        {
            sum += vector[i] * vector[i];
        }

        return MathF.Sqrt(sum);
    }
}
