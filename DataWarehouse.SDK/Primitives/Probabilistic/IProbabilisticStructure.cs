using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Primitives.Probabilistic;

/// <summary>
/// Common interface for all probabilistic data structures.
/// Provides unified access to error bounds, memory usage, and serialization.
/// Part of T85: Probabilistic Storage Features.
/// </summary>
public interface IProbabilisticStructure
{
    /// <summary>
    /// Gets the unique identifier for this structure type.
    /// </summary>
    string StructureType { get; }

    /// <summary>
    /// Gets the current memory usage in bytes.
    /// </summary>
    long MemoryUsageBytes { get; }

    /// <summary>
    /// Gets the configured error rate (false positive rate, relative error, etc.).
    /// Interpretation depends on structure type.
    /// </summary>
    double ConfiguredErrorRate { get; }

    /// <summary>
    /// Gets the number of items that have been added to this structure.
    /// </summary>
    long ItemCount { get; }

    /// <summary>
    /// Serializes the structure to a byte array for persistence.
    /// </summary>
    byte[] Serialize();

    /// <summary>
    /// Clears all data from the structure.
    /// </summary>
    void Clear();
}

/// <summary>
/// Interface for structures that can be merged (distributed scenarios).
/// </summary>
/// <typeparam name="T">The concrete structure type.</typeparam>
public interface IMergeable<T> where T : IProbabilisticStructure
{
    /// <summary>
    /// Merges another structure into this one.
    /// Used for combining results from distributed nodes.
    /// </summary>
    /// <param name="other">Structure to merge.</param>
    void Merge(T other);

    /// <summary>
    /// Creates a clone that can be merged later.
    /// </summary>
    T Clone();
}

/// <summary>
/// Interface for structures that support confidence intervals on their results.
/// </summary>
public interface IConfidenceReporting
{
    /// <summary>
    /// Gets the confidence interval for the last query result.
    /// </summary>
    /// <param name="confidenceLevel">Confidence level (e.g., 0.95 for 95%).</param>
    /// <returns>Tuple of (lower bound, upper bound).</returns>
    (double Lower, double Upper) GetConfidenceInterval(double confidenceLevel = 0.95);
}

/// <summary>
/// Represents the result of a probabilistic query with accuracy metadata.
/// </summary>
/// <typeparam name="T">Type of the result value.</typeparam>
public record ProbabilisticResult<T>
{
    /// <summary>
    /// The estimated value.
    /// </summary>
    public required T Value { get; init; }

    /// <summary>
    /// The confidence or accuracy of this result (0.0 to 1.0).
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    /// Lower bound of the confidence interval.
    /// </summary>
    public double? LowerBound { get; init; }

    /// <summary>
    /// Upper bound of the confidence interval.
    /// </summary>
    public double? UpperBound { get; init; }

    /// <summary>
    /// The type of probabilistic structure that produced this result.
    /// </summary>
    public string? SourceStructure { get; init; }

    /// <summary>
    /// Whether this result might be a false positive (for membership queries).
    /// </summary>
    public bool MayBeFalsePositive { get; init; }

    /// <summary>
    /// Implicit conversion to the value type for convenience.
    /// </summary>
    public static implicit operator T(ProbabilisticResult<T> result) => result.Value;
}

/// <summary>
/// Configuration for probabilistic structure accuracy vs space tradeoffs.
/// </summary>
public record ProbabilisticConfig
{
    /// <summary>
    /// Target false positive rate (for membership structures like Bloom filters).
    /// Default: 0.01 (1% false positive rate).
    /// </summary>
    public double FalsePositiveRate { get; init; } = 0.01;

    /// <summary>
    /// Target relative error rate (for frequency/cardinality structures).
    /// Default: 0.01 (1% relative error).
    /// </summary>
    public double RelativeError { get; init; } = 0.01;

    /// <summary>
    /// Expected number of items to be added (for sizing).
    /// </summary>
    public long ExpectedItems { get; init; } = 1_000_000;

    /// <summary>
    /// Maximum memory to use in bytes (0 = no limit).
    /// </summary>
    public long MaxMemoryBytes { get; init; } = 0;

    /// <summary>
    /// Confidence level for error bounds.
    /// Default: 0.95 (95% confidence).
    /// </summary>
    public double ConfidenceLevel { get; init; } = 0.95;

    /// <summary>
    /// Creates a high-accuracy configuration (0.1% error, more memory).
    /// </summary>
    public static ProbabilisticConfig HighAccuracy => new()
    {
        FalsePositiveRate = 0.001,
        RelativeError = 0.001,
        ConfidenceLevel = 0.99
    };

    /// <summary>
    /// Creates a balanced configuration (1% error, moderate memory).
    /// </summary>
    public static ProbabilisticConfig Balanced => new()
    {
        FalsePositiveRate = 0.01,
        RelativeError = 0.01,
        ConfidenceLevel = 0.95
    };

    /// <summary>
    /// Creates a low-memory configuration (5% error, minimal memory).
    /// </summary>
    public static ProbabilisticConfig LowMemory => new()
    {
        FalsePositiveRate = 0.05,
        RelativeError = 0.05,
        ConfidenceLevel = 0.90
    };
}
