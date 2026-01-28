namespace DataWarehouse.Plugins.SemanticUnderstanding.Engine
{
    /// <summary>
    /// Internal vector math utilities for semantic operations.
    /// </summary>
    internal static class VectorMath
    {
        /// <summary>
        /// Calculate cosine similarity between two vectors.
        /// Returns value between -1 (opposite) and 1 (identical).
        /// </summary>
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
        /// Calculate Euclidean distance between two vectors.
        /// </summary>
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
        /// Normalize a vector to unit length.
        /// </summary>
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
        /// Calculate dot product of two vectors.
        /// </summary>
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
        /// Add two vectors element-wise.
        /// </summary>
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
        /// Calculate average of multiple vectors.
        /// </summary>
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
    }
}
