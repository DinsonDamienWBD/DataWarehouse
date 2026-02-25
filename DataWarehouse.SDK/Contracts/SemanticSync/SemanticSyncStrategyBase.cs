using System;

namespace DataWarehouse.SDK.Contracts.SemanticSync;

/// <summary>
/// Abstract base class for all semantic sync strategy implementations.
/// Inherits from <see cref="StrategyBase"/> per AD-05 (flat hierarchy, no intelligence in strategies).
/// Provides semantic domain identification, local inference capability declaration,
/// and shared helper methods for classification validation and cosine similarity computation.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public abstract class SemanticSyncStrategyBase : StrategyBase
{
    /// <summary>
    /// Gets the semantic domain this strategy handles (e.g., "text", "binary", "structured", "media").
    /// Override to indicate the data domain for strategy selection and routing.
    /// Default returns "general".
    /// </summary>
    public virtual string SemanticDomain => "general";

    /// <summary>
    /// Gets whether this strategy supports local (edge) AI inference without cloud connectivity.
    /// Override to return true if the strategy can operate in disconnected environments.
    /// Default returns false.
    /// </summary>
    public virtual bool SupportsLocalInference => false;

    /// <summary>
    /// Validates a semantic classification, ensuring the confidence score is within the valid range.
    /// </summary>
    /// <param name="classification">The classification to validate.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="classification"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <see cref="SemanticClassification.Confidence"/> is less than 0.0 or greater than 1.0.</exception>
    protected static void ValidateClassification(SemanticClassification classification)
    {
        ArgumentNullException.ThrowIfNull(classification);

        if (classification.Confidence < 0.0 || classification.Confidence > 1.0)
        {
            throw new ArgumentException(
                $"Classification confidence must be between 0.0 and 1.0, but was {classification.Confidence}.",
                nameof(classification));
        }
    }

    /// <summary>
    /// Computes the cosine similarity between two float vectors.
    /// Returns a value between -1.0 (opposite) and 1.0 (identical direction).
    /// </summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>The cosine similarity score.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="a"/> or <paramref name="b"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when vectors have different lengths or are empty.</exception>
    protected static double ComputeCosineSimilarity(float[] a, float[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length == 0 || b.Length == 0)
        {
            throw new ArgumentException("Vectors must not be empty.");
        }

        if (a.Length != b.Length)
        {
            throw new ArgumentException(
                $"Vectors must have the same length. Got {a.Length} and {b.Length}.");
        }

        double dotProduct = 0.0;
        double magnitudeA = 0.0;
        double magnitudeB = 0.0;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += (double)a[i] * b[i];
            magnitudeA += (double)a[i] * a[i];
            magnitudeB += (double)b[i] * b[i];
        }

        magnitudeA = Math.Sqrt(magnitudeA);
        magnitudeB = Math.Sqrt(magnitudeB);

        if (magnitudeA == 0.0 || magnitudeB == 0.0)
        {
            return 0.0;
        }

        return dotProduct / (magnitudeA * magnitudeB);
    }
}
