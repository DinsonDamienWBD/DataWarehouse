using System;
using System.Collections.Generic;
using System.Linq;

namespace DataWarehouse.SDK.AI
{
    /// <summary>
    /// Mathematical utilities for AI operations, statistics, and data analysis.
    /// </summary>
    public static class AIMath
    {
        #region Statistical Functions

        /// <summary>
        /// Calculate the mean (average) of a collection.
        /// </summary>
        public static float Mean(ReadOnlySpan<float> values)
        {
            if (values.Length == 0) return 0;
            float sum = 0;
            for (int i = 0; i < values.Length; i++)
                sum += values[i];
            return sum / values.Length;
        }

        /// <summary>
        /// Calculate the median of a collection.
        /// </summary>
        public static float Median(ReadOnlySpan<float> values)
        {
            if (values.Length == 0) return 0;
            var sorted = values.ToArray();
            Array.Sort(sorted);
            int mid = sorted.Length / 2;
            return sorted.Length % 2 == 0
                ? (sorted[mid - 1] + sorted[mid]) / 2
                : sorted[mid];
        }

        /// <summary>
        /// Calculate the standard deviation of a collection.
        /// </summary>
        public static float StandardDeviation(ReadOnlySpan<float> values)
        {
            if (values.Length == 0) return 0;
            float mean = Mean(values);
            float sumSquares = 0;
            for (int i = 0; i < values.Length; i++)
            {
                float diff = values[i] - mean;
                sumSquares += diff * diff;
            }
            return MathF.Sqrt(sumSquares / values.Length);
        }

        /// <summary>
        /// Calculate variance of a collection.
        /// </summary>
        public static float Variance(ReadOnlySpan<float> values)
        {
            float std = StandardDeviation(values);
            return std * std;
        }

        /// <summary>
        /// Calculate percentile value.
        /// </summary>
        public static float Percentile(ReadOnlySpan<float> values, float percentile)
        {
            if (values.Length == 0) return 0;
            if (percentile < 0 || percentile > 100)
                throw new ArgumentOutOfRangeException(nameof(percentile), "Must be between 0 and 100");

            var sorted = values.ToArray();
            Array.Sort(sorted);
            float index = (percentile / 100) * (sorted.Length - 1);
            int lower = (int)Math.Floor(index);
            int upper = (int)Math.Ceiling(index);

            if (lower == upper) return sorted[lower];
            return sorted[lower] + (sorted[upper] - sorted[lower]) * (index - lower);
        }

        #endregion

        #region Normalization Functions

        /// <summary>
        /// Min-max normalization to [0, 1] range.
        /// </summary>
        public static float[] MinMaxNormalize(ReadOnlySpan<float> values)
        {
            if (values.Length == 0) return Array.Empty<float>();

            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] < min) min = values[i];
                if (values[i] > max) max = values[i];
            }

            float range = max - min;
            float[] result = new float[values.Length];
            for (int i = 0; i < values.Length; i++)
                result[i] = range == 0 ? 0 : (values[i] - min) / range;
            return result;
        }

        /// <summary>
        /// Z-score normalization (standardization).
        /// </summary>
        public static float[] ZScoreNormalize(ReadOnlySpan<float> values)
        {
            if (values.Length == 0) return Array.Empty<float>();

            float mean = Mean(values);
            float std = StandardDeviation(values);

            float[] result = new float[values.Length];
            for (int i = 0; i < values.Length; i++)
                result[i] = std == 0 ? 0 : (values[i] - mean) / std;
            return result;
        }

        /// <summary>
        /// Softmax normalization (converts to probability distribution).
        /// </summary>
        public static float[] Softmax(ReadOnlySpan<float> values)
        {
            if (values.Length == 0) return Array.Empty<float>();

            // Find max for numerical stability
            float max = float.MinValue;
            for (int i = 0; i < values.Length; i++)
                if (values[i] > max) max = values[i];

            float[] result = new float[values.Length];
            float sum = 0;
            for (int i = 0; i < values.Length; i++)
            {
                result[i] = MathF.Exp(values[i] - max);
                sum += result[i];
            }

            for (int i = 0; i < result.Length; i++)
                result[i] /= sum;
            return result;
        }

        #endregion

        #region Similarity and Distance Functions

        /// <summary>
        /// Jaccard similarity for sets.
        /// </summary>
        public static float JaccardSimilarity<T>(ISet<T> a, ISet<T> b)
        {
            if (a.Count == 0 && b.Count == 0) return 1.0f;
            int intersection = a.Intersect(b).Count();
            int union = a.Union(b).Count();
            return union == 0 ? 0 : (float)intersection / union;
        }

        /// <summary>
        /// Levenshtein distance (edit distance) between strings.
        /// </summary>
        public static int LevenshteinDistance(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;

            int[,] d = new int[a.Length + 1, b.Length + 1];

            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            return d[a.Length, b.Length];
        }

        /// <summary>
        /// Normalized Levenshtein similarity (0-1 range).
        /// </summary>
        public static float LevenshteinSimilarity(string a, string b)
        {
            int maxLen = Math.Max(a?.Length ?? 0, b?.Length ?? 0);
            if (maxLen == 0) return 1.0f;
            return 1.0f - (float)LevenshteinDistance(a ?? "", b ?? "") / maxLen;
        }

        #endregion

        #region Activation Functions

        /// <summary>
        /// Sigmoid activation function.
        /// </summary>
        public static float Sigmoid(float x) => 1.0f / (1.0f + MathF.Exp(-x));

        /// <summary>
        /// Hyperbolic tangent activation.
        /// </summary>
        public static float Tanh(float x) => MathF.Tanh(x);

        /// <summary>
        /// ReLU (Rectified Linear Unit) activation.
        /// </summary>
        public static float ReLU(float x) => Math.Max(0, x);

        /// <summary>
        /// Leaky ReLU activation.
        /// </summary>
        public static float LeakyReLU(float x, float alpha = 0.01f) => x >= 0 ? x : alpha * x;

        /// <summary>
        /// GELU (Gaussian Error Linear Unit) activation.
        /// </summary>
        public static float GELU(float x) => 0.5f * x * (1.0f + MathF.Tanh(MathF.Sqrt(2.0f / MathF.PI) * (x + 0.044715f * x * x * x)));

        #endregion

        #region Utility Functions

        /// <summary>
        /// Clamp value to a range.
        /// </summary>
        public static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));

        /// <summary>
        /// Linear interpolation between two values.
        /// </summary>
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp(t, 0, 1);

        /// <summary>
        /// Calculate entropy of a probability distribution.
        /// </summary>
        public static float Entropy(ReadOnlySpan<float> probabilities)
        {
            float entropy = 0;
            for (int i = 0; i < probabilities.Length; i++)
            {
                if (probabilities[i] > 0)
                    entropy -= probabilities[i] * MathF.Log2(probabilities[i]);
            }
            return entropy;
        }

        /// <summary>
        /// Calculate cross-entropy between two distributions.
        /// </summary>
        public static float CrossEntropy(ReadOnlySpan<float> predicted, ReadOnlySpan<float> actual)
        {
            if (predicted.Length != actual.Length)
                throw new ArgumentException("Distributions must have same length");

            float ce = 0;
            for (int i = 0; i < predicted.Length; i++)
            {
                if (actual[i] > 0)
                    ce -= actual[i] * MathF.Log(Math.Max(predicted[i], 1e-10f));
            }
            return ce;
        }

        /// <summary>
        /// Generate random float in range [min, max).
        /// </summary>
        public static float Random(float min, float max, Random? rng = null)
        {
            rng ??= System.Random.Shared;
            return min + (float)rng.NextDouble() * (max - min);
        }

        /// <summary>
        /// Generate random vector with values in range [min, max).
        /// </summary>
        public static float[] RandomVector(int dimensions, float min = -1, float max = 1, Random? rng = null)
        {
            rng ??= System.Random.Shared;
            float[] result = new float[dimensions];
            for (int i = 0; i < dimensions; i++)
                result[i] = Random(min, max, rng);
            return result;
        }

        #endregion
    }
}
