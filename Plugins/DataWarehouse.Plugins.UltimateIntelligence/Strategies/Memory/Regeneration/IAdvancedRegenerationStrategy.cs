using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Regeneration;

/// <summary>
/// Advanced regeneration strategy interface for sophisticated data type regeneration.
/// Each implementation targets a specific data format with 5-sigma (99.99999%) accuracy.
/// </summary>
public interface IAdvancedRegenerationStrategy
{
    /// <summary>
    /// Gets the unique identifier for this regeneration strategy.
    /// </summary>
    string StrategyId { get; }

    /// <summary>
    /// Gets the human-readable display name for this strategy.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Gets the list of supported formats/file extensions for this strategy.
    /// </summary>
    string[] SupportedFormats { get; }

    /// <summary>
    /// Gets the expected accuracy target for this strategy.
    /// Target: 0.9999999 (5-sigma accuracy).
    /// </summary>
    double ExpectedAccuracy { get; }

    /// <summary>
    /// Regenerates content from the encoded context.
    /// </summary>
    /// <param name="context">The encoded context containing compressed/semantic data.</param>
    /// <param name="options">Regeneration options including accuracy requirements.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing regenerated content with accuracy metrics.</returns>
    Task<RegenerationResult> RegenerateAsync(
        EncodedContext context,
        RegenerationOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Assesses the capability to regenerate from the given context.
    /// </summary>
    /// <param name="context">The encoded context to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Assessment of regeneration capability with expected accuracy.</returns>
    Task<RegenerationCapability> AssessCapabilityAsync(
        EncodedContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Verifies the accuracy of regenerated content against the original.
    /// </summary>
    /// <param name="original">The original content.</param>
    /// <param name="regenerated">The regenerated content.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Accuracy score from 0.0 to 1.0.</returns>
    Task<double> VerifyAccuracyAsync(
        string original,
        string regenerated,
        CancellationToken ct = default);
}

/// <summary>
/// Result of an advanced regeneration operation with comprehensive metrics.
/// </summary>
public record RegenerationResult
{
    /// <summary>Whether the regeneration was successful.</summary>
    public bool Success { get; init; }

    /// <summary>The regenerated content.</summary>
    public string RegeneratedContent { get; init; } = string.Empty;

    /// <summary>Confidence score for the regeneration (0.0 to 1.0).</summary>
    public double ConfidenceScore { get; init; }

    /// <summary>Actual measured accuracy (0.0 to 1.0, target: 0.9999999).</summary>
    public double ActualAccuracy { get; init; }

    /// <summary>Warnings encountered during regeneration.</summary>
    public List<string> Warnings { get; init; } = new();

    /// <summary>Diagnostic information for debugging and analysis.</summary>
    public Dictionary<string, object> Diagnostics { get; init; } = new();

    /// <summary>Time taken for regeneration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Number of regeneration passes performed.</summary>
    public int PassCount { get; init; }

    /// <summary>The strategy ID that performed the regeneration.</summary>
    public string StrategyId { get; init; } = string.Empty;

    /// <summary>Detected format of the regenerated content.</summary>
    public string DetectedFormat { get; init; } = string.Empty;

    /// <summary>Hash of the regenerated content for verification.</summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>Whether the regenerated content matches the original hash (if available).</summary>
    public bool? HashMatch { get; init; }

    /// <summary>Semantic integrity score (0.0 to 1.0).</summary>
    public double SemanticIntegrity { get; init; }

    /// <summary>Structural integrity score (0.0 to 1.0).</summary>
    public double StructuralIntegrity { get; init; }
}

/// <summary>
/// Assessment of regeneration capability for a given context.
/// </summary>
public record RegenerationCapability
{
    /// <summary>Whether regeneration is possible.</summary>
    public bool CanRegenerate { get; init; }

    /// <summary>Expected accuracy if regeneration is attempted.</summary>
    public double ExpectedAccuracy { get; init; }

    /// <summary>Elements missing that would improve accuracy.</summary>
    public List<string> MissingElements { get; init; } = new();

    /// <summary>Recommendations to improve regeneration accuracy.</summary>
    public string RecommendedEnrichment { get; init; } = string.Empty;

    /// <summary>Confidence in this assessment.</summary>
    public double AssessmentConfidence { get; init; }

    /// <summary>Detected content type.</summary>
    public string DetectedContentType { get; init; } = string.Empty;

    /// <summary>Estimated duration for regeneration.</summary>
    public TimeSpan EstimatedDuration { get; init; }

    /// <summary>Estimated memory required for regeneration.</summary>
    public long EstimatedMemoryBytes { get; init; }

    /// <summary>Recommended strategy for regeneration.</summary>
    public string RecommendedStrategy { get; init; } = string.Empty;

    /// <summary>List of applicable strategies in order of suitability.</summary>
    public List<string> ApplicableStrategies { get; init; } = new();

    /// <summary>Complexity score of the content (0.0 to 1.0).</summary>
    public double ComplexityScore { get; init; }
}

/// <summary>
/// Options for controlling the regeneration process.
/// </summary>
public record RegenerationOptions
{
    /// <summary>Expected output format (e.g., "json", "sql", "csv").</summary>
    public string ExpectedFormat { get; init; } = "text";

    /// <summary>Minimum acceptable accuracy (default: 0.9999999 = 5-sigma).</summary>
    public double MinAccuracy { get; init; } = 0.9999999;

    /// <summary>Maximum number of regeneration passes to attempt.</summary>
    public int MaxPasses { get; init; } = 5;

    /// <summary>Whether to enable semantic verification.</summary>
    public bool EnableSemanticVerification { get; init; } = true;

    /// <summary>Whether to enable structural verification.</summary>
    public bool EnableStructuralVerification { get; init; } = true;

    /// <summary>Original content hash for verification (if available).</summary>
    public string? OriginalHash { get; init; }

    /// <summary>Timeout for the regeneration operation.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether to use parallel processing where applicable.</summary>
    public bool EnableParallelProcessing { get; init; } = true;

    /// <summary>Target dialect for format-specific regeneration (e.g., "T-SQL", "PostgreSQL").</summary>
    public string? TargetDialect { get; init; }

    /// <summary>Schema information for structured formats (JSON Schema, XSD, etc.).</summary>
    public string? SchemaDefinition { get; init; }

    /// <summary>Whether to preserve comments in source code/config files.</summary>
    public bool PreserveComments { get; init; } = true;

    /// <summary>Whether to preserve formatting/whitespace.</summary>
    public bool PreserveFormatting { get; init; } = true;

    /// <summary>Custom hints for regeneration.</summary>
    public Dictionary<string, object> Hints { get; init; } = new();
}

/// <summary>
/// Base class for regeneration strategies providing common functionality.
/// </summary>
public abstract class RegenerationStrategyBase : IAdvancedRegenerationStrategy
{
    private readonly BoundedDictionary<string, RegenerationStatistics> _statisticsByFormat = new BoundedDictionary<string, RegenerationStatistics>(1000);
    private long _totalRegenerations;
    private long _successfulRegenerations;
    private double _cumulativeAccuracy;

    /// <inheritdoc/>
    public abstract string StrategyId { get; }

    /// <inheritdoc/>
    public abstract string DisplayName { get; }

    /// <inheritdoc/>
    public abstract string[] SupportedFormats { get; }

    /// <inheritdoc/>
    public virtual double ExpectedAccuracy => 0.9999999; // 5-sigma

    /// <inheritdoc/>
    public abstract Task<RegenerationResult> RegenerateAsync(
        EncodedContext context,
        RegenerationOptions options,
        CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<RegenerationCapability> AssessCapabilityAsync(
        EncodedContext context,
        CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<double> VerifyAccuracyAsync(
        string original,
        string regenerated,
        CancellationToken ct = default);

    /// <summary>
    /// Gets statistics for this strategy.
    /// </summary>
    public RegenerationStatistics GetStatistics()
    {
        var total = Interlocked.Read(ref _totalRegenerations);
        var successful = Interlocked.Read(ref _successfulRegenerations);

        return new RegenerationStatistics
        {
            TotalRegenerations = total,
            SuccessfulRegenerations = successful,
            FailedRegenerations = total - successful,
            AverageAccuracy = total > 0 ? _cumulativeAccuracy / total : 0,
            SuccessRate = total > 0 ? (double)successful / total : 0,
            StrategyId = StrategyId
        };
    }

    /// <summary>
    /// Records a regeneration attempt for statistics.
    /// </summary>
    protected void RecordRegeneration(bool success, double accuracy, string format)
    {
        Interlocked.Increment(ref _totalRegenerations);
        if (success)
        {
            Interlocked.Increment(ref _successfulRegenerations);
        }

        // Thread-safe accumulation (approximate)
        Interlocked.Exchange(ref _cumulativeAccuracy, _cumulativeAccuracy + accuracy);

        // Update per-format statistics
        var stats = _statisticsByFormat.GetOrAdd(format, _ => new RegenerationStatistics { StrategyId = StrategyId });
        stats.TotalRegenerations++;
        if (success) stats.SuccessfulRegenerations++;
    }

    /// <summary>
    /// Computes SHA-256 hash of content.
    /// </summary>
    protected static string ComputeHash(string content)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = sha256.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Calculates Levenshtein distance between two strings.
    /// </summary>
    protected static int LevenshteinDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;

        var n = a.Length;
        var m = b.Length;
        var d = new int[n + 1, m + 1];

        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }

    /// <summary>
    /// Calculates normalized string similarity (0.0 to 1.0).
    /// </summary>
    protected static double CalculateStringSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;

        var maxLen = Math.Max(a.Length, b.Length);
        var distance = LevenshteinDistance(a, b);
        return 1.0 - ((double)distance / maxLen);
    }

    /// <summary>
    /// Calculates Jaccard similarity between token sets.
    /// </summary>
    protected static double CalculateJaccardSimilarity(IEnumerable<string> setA, IEnumerable<string> setB)
    {
        var a = new HashSet<string>(setA);
        var b = new HashSet<string>(setB);

        var intersection = a.Intersect(b).Count();
        var union = a.Union(b).Count();

        return union > 0 ? (double)intersection / union : 0.0;
    }

    /// <summary>
    /// Tokenizes text into words.
    /// </summary>
    protected static IEnumerable<string> Tokenize(string text)
    {
        return text.Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?' },
            StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant());
    }

    /// <summary>
    /// Creates a standardized diagnostic dictionary.
    /// </summary>
    protected static Dictionary<string, object> CreateDiagnostics(
        string format,
        int passes,
        TimeSpan duration,
        params (string key, object value)[] additional)
    {
        var diagnostics = new Dictionary<string, object>
        {
            ["format"] = format,
            ["passes"] = passes,
            ["duration_ms"] = duration.TotalMilliseconds,
            ["timestamp"] = DateTimeOffset.UtcNow
        };

        foreach (var (key, value) in additional)
        {
            diagnostics[key] = value;
        }

        return diagnostics;
    }
}

/// <summary>
/// Statistics for regeneration operations.
/// </summary>
public class RegenerationStatistics
{
    /// <summary>Total number of regeneration attempts.</summary>
    public long TotalRegenerations { get; set; }

    /// <summary>Number of successful regenerations.</summary>
    public long SuccessfulRegenerations { get; set; }

    /// <summary>Number of failed regenerations.</summary>
    public long FailedRegenerations { get; set; }

    /// <summary>Average accuracy achieved.</summary>
    public double AverageAccuracy { get; set; }

    /// <summary>Success rate (0.0 to 1.0).</summary>
    public double SuccessRate { get; set; }

    /// <summary>Strategy ID these statistics apply to.</summary>
    public string StrategyId { get; set; } = string.Empty;
}
