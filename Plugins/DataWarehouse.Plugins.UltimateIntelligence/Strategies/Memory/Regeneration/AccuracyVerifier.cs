using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Regeneration;

/// <summary>
/// Cross-strategy accuracy verification with multi-dimensional validation.
/// Provides comprehensive accuracy assessment using structural, semantic, and statistical methods.
/// </summary>
public sealed class AccuracyVerifier
{
    private readonly RegenerationMetrics _metrics;
    private readonly VerifierConfiguration _config;
    private readonly BoundedDictionary<string, VerificationCache> _cache = new BoundedDictionary<string, VerificationCache>(1000);

    /// <summary>
    /// Initializes a new accuracy verifier.
    /// </summary>
    /// <param name="metrics">Metrics collector for tracking verification results.</param>
    /// <param name="config">Verifier configuration.</param>
    public AccuracyVerifier(RegenerationMetrics metrics, VerifierConfiguration? config = null)
    {
        _metrics = metrics;
        _config = config ?? new VerifierConfiguration();
    }

    /// <summary>
    /// Verifies the accuracy of a regeneration result against the original context.
    /// </summary>
    /// <param name="original">The original encoded context.</param>
    /// <param name="result">The regeneration result to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Comprehensive verification result.</returns>
    public async Task<VerificationResult> VerifyAsync(
        EncodedContext original,
        RegenerationResult result,
        CancellationToken ct = default)
    {
        if (!result.Success || string.IsNullOrEmpty(result.RegeneratedContent))
        {
            return new VerificationResult
            {
                OverallScore = 0,
                IsAccurate = false,
                FailureReason = "Regeneration failed or produced no data"
            };
        }

        var cacheKey = ComputeCacheKey(original, result);
        if (_config.EnableCaching && _cache.TryGetValue(cacheKey, out var cached))
        {
            if (DateTime.UtcNow - cached.Timestamp < _config.CacheExpiry)
            {
                return cached.Result;
            }
        }

        var verificationResult = await PerformVerificationAsync(original, result, ct);

        if (_config.EnableCaching)
        {
            _cache[cacheKey] = new VerificationCache
            {
                Result = verificationResult,
                Timestamp = DateTime.UtcNow
            };
        }

        _metrics.RecordVerification(
            result.StrategyId,
            verificationResult.OverallScore,
            verificationResult.IsAccurate);

        return verificationResult;
    }

    /// <summary>
    /// Performs verification with cross-strategy comparison.
    /// </summary>
    /// <param name="original">The original encoded context.</param>
    /// <param name="result">The regeneration result to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Enhanced verification with cross-check data.</returns>
    public async Task<VerificationResult> VerifyWithCrossCheckAsync(
        EncodedContext original,
        RegenerationResult result,
        CancellationToken ct = default)
    {
        var baseVerification = await VerifyAsync(original, result, ct);

        // Perform additional cross-checks
        var crossChecks = new List<CrossCheckResult>();

        // Hash comparison
        var originalHash = ComputeContentHash(original.EncodedData);
        var regeneratedHash = ComputeContentHash(result.RegeneratedContent);
        crossChecks.Add(new CrossCheckResult
        {
            CheckName = "HashComparison",
            Passed = originalHash == regeneratedHash,
            Score = originalHash == regeneratedHash ? 1.0 : CalculateHashSimilarity(originalHash, regeneratedHash),
            Details = $"Original: {originalHash[..16]}... Regenerated: {regeneratedHash[..16]}..."
        });

        // Length comparison
        var lengthRatio = Math.Min(original.EncodedData.Length, result.RegeneratedContent.Length) /
                         (double)Math.Max(original.EncodedData.Length, result.RegeneratedContent.Length);
        crossChecks.Add(new CrossCheckResult
        {
            CheckName = "LengthComparison",
            Passed = lengthRatio >= 0.99,
            Score = lengthRatio,
            Details = $"Original: {original.EncodedData.Length} chars, Regenerated: {result.RegeneratedContent.Length} chars"
        });

        // Entropy comparison
        var originalEntropy = CalculateEntropy(original.EncodedData);
        var regeneratedEntropy = CalculateEntropy(result.RegeneratedContent);
        var entropyDiff = Math.Abs(originalEntropy - regeneratedEntropy);
        crossChecks.Add(new CrossCheckResult
        {
            CheckName = "EntropyComparison",
            Passed = entropyDiff < 0.1,
            Score = Math.Max(0, 1 - entropyDiff),
            Details = $"Original entropy: {originalEntropy:F4}, Regenerated entropy: {regeneratedEntropy:F4}"
        });

        // N-gram similarity
        var ngramScore = CalculateNGramSimilarity(original.EncodedData, result.RegeneratedContent, 3);
        crossChecks.Add(new CrossCheckResult
        {
            CheckName = "NGramSimilarity",
            Passed = ngramScore >= 0.99,
            Score = ngramScore,
            Details = $"3-gram similarity score: {ngramScore:F6}"
        });

        // Combine with base verification
        var crossCheckAverage = crossChecks.Average(c => c.Score);
        var combinedScore = (baseVerification.OverallScore * 0.7) + (crossCheckAverage * 0.3);

        return baseVerification with
        {
            OverallScore = combinedScore,
            IsAccurate = combinedScore >= _config.AccuracyThreshold,
            CrossChecks = crossChecks
        };
    }

    /// <summary>
    /// Compares multiple regeneration results for the same context.
    /// </summary>
    /// <param name="original">The original encoded context.</param>
    /// <param name="results">Multiple regeneration results to compare.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Comparison result with consensus analysis.</returns>
    public async Task<ComparisonResult> CompareResultsAsync(
        EncodedContext original,
        IReadOnlyList<RegenerationResult> results,
        CancellationToken ct = default)
    {
        if (results.Count == 0)
        {
            return new ComparisonResult
            {
                HasConsensus = false,
                ErrorMessage = "No results to compare"
            };
        }

        var verifications = new List<(RegenerationResult Result, VerificationResult Verification)>();

        foreach (var result in results)
        {
            var verification = await VerifyAsync(original, result, ct);
            verifications.Add((result, verification));
        }

        // Find agreement between results
        var agreementMatrix = new double[results.Count, results.Count];
        for (int i = 0; i < results.Count; i++)
        {
            for (int j = i + 1; j < results.Count; j++)
            {
                var similarity = CalculateSimilarity(
                    results[i].RegeneratedContent,
                    results[j].RegeneratedContent);
                agreementMatrix[i, j] = similarity;
                agreementMatrix[j, i] = similarity;
            }
            agreementMatrix[i, i] = 1.0;
        }

        // Calculate consensus metrics
        var averageAgreement = 0.0;
        var count = 0;
        for (int i = 0; i < results.Count; i++)
        {
            for (int j = i + 1; j < results.Count; j++)
            {
                averageAgreement += agreementMatrix[i, j];
                count++;
            }
        }
        if (count > 0) averageAgreement /= count;

        // Find best result
        var bestIdx = verifications
            .Select((v, i) => (v, i))
            .OrderByDescending(x => x.v.Verification.OverallScore)
            .First().i;

        // Determine if there's strong consensus
        var hasConsensus = averageAgreement >= 0.9999;

        return new ComparisonResult
        {
            HasConsensus = hasConsensus,
            ConsensusScore = averageAgreement,
            BestResult = results[bestIdx],
            BestVerification = verifications[bestIdx].Verification,
            AllVerifications = verifications.Select(v => v.Verification).ToList(),
            AgreementMatrix = FormatAgreementMatrix(agreementMatrix, results.Count),
            Discrepancies = FindDiscrepancies(results, agreementMatrix)
        };
    }

    /// <summary>
    /// Performs statistical validation on regenerated data.
    /// </summary>
    /// <param name="original">Original data.</param>
    /// <param name="regenerated">Regenerated data.</param>
    /// <returns>Statistical validation result.</returns>
    public StatisticalValidation PerformStatisticalValidation(string original, string regenerated)
    {
        // Character frequency analysis
        var originalFreq = CalculateCharFrequency(original);
        var regeneratedFreq = CalculateCharFrequency(regenerated);
        var freqCorrelation = CalculateFrequencyCorrelation(originalFreq, regeneratedFreq);

        // Token analysis (words/identifiers)
        var originalTokens = Tokenize(original);
        var regeneratedTokens = Tokenize(regenerated);
        var tokenOverlap = CalculateTokenOverlap(originalTokens, regeneratedTokens);

        // Line-by-line analysis
        var originalLines = original.Split('\n');
        var regeneratedLines = regenerated.Split('\n');
        var lineMatchRate = CalculateLineMatchRate(originalLines, regeneratedLines);

        // Statistical tests
        var chiSquare = CalculateChiSquare(originalFreq, regeneratedFreq);
        var kolmogorovSmirnov = CalculateKolmogorovSmirnov(originalFreq, regeneratedFreq);

        return new StatisticalValidation
        {
            FrequencyCorrelation = freqCorrelation,
            TokenOverlap = tokenOverlap,
            LineMatchRate = lineMatchRate,
            ChiSquareStatistic = chiSquare,
            KolmogorovSmirnovStatistic = kolmogorovSmirnov,
            IsStatisticallyEquivalent = freqCorrelation >= 0.999 && tokenOverlap >= 0.999,
            ConfidenceLevel = CalculateConfidenceLevel(freqCorrelation, tokenOverlap, lineMatchRate)
        };
    }

    private async Task<VerificationResult> PerformVerificationAsync(
        EncodedContext original,
        RegenerationResult result,
        CancellationToken ct)
    {
        var dimensions = new List<VerificationDimension>();

        // Dimension 1: Exact match
        var exactMatch = original.EncodedData == result.RegeneratedContent;
        dimensions.Add(new VerificationDimension
        {
            Name = "ExactMatch",
            Score = exactMatch ? 1.0 : 0.0,
            Weight = 0.4,
            Details = exactMatch ? "Perfect byte-for-byte match" : "Data differs from original"
        });

        if (exactMatch)
        {
            return new VerificationResult
            {
                OverallScore = 1.0,
                IsAccurate = true,
                Dimensions = dimensions,
                VerificationMethod = "ExactMatch"
            };
        }

        // Dimension 2: Structural similarity
        var structuralScore = CalculateStructuralSimilarity(original.EncodedData, result.RegeneratedContent);
        dimensions.Add(new VerificationDimension
        {
            Name = "StructuralSimilarity",
            Score = structuralScore,
            Weight = 0.25,
            Details = $"Structural similarity: {structuralScore:P4}"
        });

        // Dimension 3: Semantic similarity
        var semanticScore = await CalculateSemanticSimilarityAsync(
            original.EncodedData, result.RegeneratedContent, ct);
        dimensions.Add(new VerificationDimension
        {
            Name = "SemanticSimilarity",
            Score = semanticScore,
            Weight = 0.2,
            Details = $"Semantic similarity: {semanticScore:P4}"
        });

        // Dimension 4: Statistical validation
        var statValidation = PerformStatisticalValidation(original.EncodedData, result.RegeneratedContent);
        dimensions.Add(new VerificationDimension
        {
            Name = "StatisticalValidation",
            Score = statValidation.ConfidenceLevel,
            Weight = 0.15,
            Details = $"Statistical confidence: {statValidation.ConfidenceLevel:P4}"
        });

        // Calculate weighted score
        var weightedScore = dimensions.Sum(d => d.Score * d.Weight);

        return new VerificationResult
        {
            OverallScore = weightedScore,
            IsAccurate = weightedScore >= _config.AccuracyThreshold,
            Dimensions = dimensions,
            VerificationMethod = "MultiDimensional",
            StatisticalValidation = statValidation
        };
    }

    private double CalculateStructuralSimilarity(string original, string regenerated)
    {
        if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(regenerated))
            return 0;

        // Use multiple structural metrics
        var levenshtein = CalculateLevenshteinSimilarity(original, regenerated);
        var jaroWinkler = CalculateJaroWinklerSimilarity(original, regenerated);
        var lcs = CalculateLCSSimilarity(original, regenerated);

        return (levenshtein + jaroWinkler + lcs) / 3.0;
    }

    private double CalculateLevenshteinSimilarity(string s1, string s2)
    {
        var maxLen = Math.Max(s1.Length, s2.Length);
        if (maxLen == 0) return 1.0;

        var distance = CalculateLevenshteinDistance(s1, s2);
        return 1.0 - ((double)distance / maxLen);
    }

    private int CalculateLevenshteinDistance(string s1, string s2)
    {
        var m = s1.Length;
        var n = s2.Length;
        var dp = new int[m + 1, n + 1];

        for (int i = 0; i <= m; i++) dp[i, 0] = i;
        for (int j = 0; j <= n; j++) dp[0, j] = j;

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost);
            }
        }

        return dp[m, n];
    }

    private double CalculateJaroWinklerSimilarity(string s1, string s2)
    {
        if (s1 == s2) return 1.0;
        if (s1.Length == 0 || s2.Length == 0) return 0;

        var matchWindow = Math.Max(s1.Length, s2.Length) / 2 - 1;
        if (matchWindow < 0) matchWindow = 0;

        var s1Matches = new bool[s1.Length];
        var s2Matches = new bool[s2.Length];

        int matches = 0;
        int transpositions = 0;

        for (int i = 0; i < s1.Length; i++)
        {
            var start = Math.Max(0, i - matchWindow);
            var end = Math.Min(i + matchWindow + 1, s2.Length);

            for (int j = start; j < end; j++)
            {
                if (s2Matches[j] || s1[i] != s2[j]) continue;
                s1Matches[i] = true;
                s2Matches[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0) return 0;

        int k = 0;
        for (int i = 0; i < s1.Length; i++)
        {
            if (!s1Matches[i]) continue;
            while (!s2Matches[k]) k++;
            if (s1[i] != s2[k]) transpositions++;
            k++;
        }

        var jaro = ((double)matches / s1.Length +
                   (double)matches / s2.Length +
                   (double)(matches - transpositions / 2) / matches) / 3.0;

        // Winkler modification
        int prefix = 0;
        for (int i = 0; i < Math.Min(4, Math.Min(s1.Length, s2.Length)); i++)
        {
            if (s1[i] == s2[i]) prefix++;
            else break;
        }

        return jaro + prefix * 0.1 * (1 - jaro);
    }

    private double CalculateLCSSimilarity(string s1, string s2)
    {
        var lcsLength = CalculateLCSLength(s1, s2);
        return (2.0 * lcsLength) / (s1.Length + s2.Length);
    }

    private int CalculateLCSLength(string s1, string s2)
    {
        var m = s1.Length;
        var n = s2.Length;
        var dp = new int[m + 1, n + 1];

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                if (s1[i - 1] == s2[j - 1])
                    dp[i, j] = dp[i - 1, j - 1] + 1;
                else
                    dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
            }
        }

        return dp[m, n];
    }

    private Task<double> CalculateSemanticSimilarityAsync(
        string original, string regenerated, CancellationToken ct)
    {
        // Simplified semantic comparison using token-based analysis
        var originalTokens = ExtractSemanticTokens(original);
        var regeneratedTokens = ExtractSemanticTokens(regenerated);

        if (originalTokens.Count == 0 && regeneratedTokens.Count == 0)
            return Task.FromResult(1.0);

        var intersection = originalTokens.Intersect(regeneratedTokens).Count();
        var union = originalTokens.Union(regeneratedTokens).Count();

        var jaccard = union > 0 ? (double)intersection / union : 0;

        // Consider order preservation
        var orderScore = CalculateOrderPreservation(originalTokens, regeneratedTokens);

        return Task.FromResult((jaccard * 0.6) + (orderScore * 0.4));
    }

    private HashSet<string> ExtractSemanticTokens(string text)
    {
        var tokens = new HashSet<string>();
        var pattern = @"\b[\w_][\w\d_]*\b";
        foreach (Match match in Regex.Matches(text, pattern))
        {
            tokens.Add(match.Value.ToLowerInvariant());
        }
        return tokens;
    }

    private double CalculateOrderPreservation(HashSet<string> original, HashSet<string> regenerated)
    {
        var common = original.Intersect(regenerated).ToList();
        if (common.Count < 2) return 1.0;

        // Simplified order check
        return common.Count / (double)Math.Max(original.Count, regenerated.Count);
    }

    private double CalculateSimilarity(string s1, string s2)
    {
        if (s1 == s2) return 1.0;
        return CalculateLevenshteinSimilarity(s1, s2);
    }

    private string ComputeContentHash(string content)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    private double CalculateHashSimilarity(string hash1, string hash2)
    {
        int matching = 0;
        for (int i = 0; i < Math.Min(hash1.Length, hash2.Length); i++)
        {
            if (hash1[i] == hash2[i]) matching++;
        }
        return (double)matching / Math.Max(hash1.Length, hash2.Length);
    }

    private double CalculateEntropy(string data)
    {
        if (string.IsNullOrEmpty(data)) return 0;

        var freq = new Dictionary<char, int>();
        foreach (var c in data)
        {
            freq[c] = freq.GetValueOrDefault(c, 0) + 1;
        }

        double entropy = 0;
        foreach (var count in freq.Values)
        {
            var p = (double)count / data.Length;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }

    private double CalculateNGramSimilarity(string s1, string s2, int n)
    {
        var ngrams1 = GetNGrams(s1, n);
        var ngrams2 = GetNGrams(s2, n);

        if (ngrams1.Count == 0 && ngrams2.Count == 0) return 1.0;

        var intersection = ngrams1.Intersect(ngrams2).Count();
        var union = ngrams1.Union(ngrams2).Count();

        return union > 0 ? (double)intersection / union : 0;
    }

    private HashSet<string> GetNGrams(string text, int n)
    {
        var ngrams = new HashSet<string>();
        for (int i = 0; i <= text.Length - n; i++)
        {
            ngrams.Add(text.Substring(i, n));
        }
        return ngrams;
    }

    private Dictionary<char, double> CalculateCharFrequency(string text)
    {
        var freq = new Dictionary<char, double>();
        if (string.IsNullOrEmpty(text)) return freq;

        foreach (var c in text)
        {
            freq[c] = freq.GetValueOrDefault(c, 0) + 1;
        }

        foreach (var key in freq.Keys.ToList())
        {
            freq[key] /= text.Length;
        }

        return freq;
    }

    private double CalculateFrequencyCorrelation(
        Dictionary<char, double> freq1,
        Dictionary<char, double> freq2)
    {
        var allChars = freq1.Keys.Union(freq2.Keys).ToList();
        if (allChars.Count == 0) return 1.0;

        var v1 = allChars.Select(c => freq1.GetValueOrDefault(c, 0)).ToArray();
        var v2 = allChars.Select(c => freq2.GetValueOrDefault(c, 0)).ToArray();

        // Pearson correlation
        var mean1 = v1.Average();
        var mean2 = v2.Average();

        double numerator = 0, denom1 = 0, denom2 = 0;
        for (int i = 0; i < v1.Length; i++)
        {
            var d1 = v1[i] - mean1;
            var d2 = v2[i] - mean2;
            numerator += d1 * d2;
            denom1 += d1 * d1;
            denom2 += d2 * d2;
        }

        var denom = Math.Sqrt(denom1 * denom2);
        return denom > 0 ? (numerator / denom + 1) / 2 : 1.0; // Normalize to 0-1
    }

    private List<string> Tokenize(string text)
    {
        return Regex.Matches(text, @"\b[\w_][\w\d_]*\b")
            .Select(m => m.Value)
            .ToList();
    }

    private double CalculateTokenOverlap(List<string> tokens1, List<string> tokens2)
    {
        if (tokens1.Count == 0 && tokens2.Count == 0) return 1.0;

        var set1 = new HashSet<string>(tokens1);
        var set2 = new HashSet<string>(tokens2);

        var intersection = set1.Intersect(set2).Count();
        var union = set1.Union(set2).Count();

        return union > 0 ? (double)intersection / union : 0;
    }

    private double CalculateLineMatchRate(string[] lines1, string[] lines2)
    {
        if (lines1.Length == 0 && lines2.Length == 0) return 1.0;

        var matches = 0;
        var maxLen = Math.Max(lines1.Length, lines2.Length);

        for (int i = 0; i < Math.Min(lines1.Length, lines2.Length); i++)
        {
            if (lines1[i].Trim() == lines2[i].Trim())
                matches++;
        }

        return (double)matches / maxLen;
    }

    private double CalculateChiSquare(
        Dictionary<char, double> observed,
        Dictionary<char, double> expected)
    {
        var allChars = observed.Keys.Union(expected.Keys);
        double chiSquare = 0;

        foreach (var c in allChars)
        {
            var o = observed.GetValueOrDefault(c, 0);
            var e = expected.GetValueOrDefault(c, 0.0001); // Avoid division by zero
            chiSquare += Math.Pow(o - e, 2) / e;
        }

        return chiSquare;
    }

    private double CalculateKolmogorovSmirnov(
        Dictionary<char, double> dist1,
        Dictionary<char, double> dist2)
    {
        var allChars = dist1.Keys.Union(dist2.Keys).OrderBy(c => c).ToList();

        double maxDiff = 0;
        double cdf1 = 0, cdf2 = 0;

        foreach (var c in allChars)
        {
            cdf1 += dist1.GetValueOrDefault(c, 0);
            cdf2 += dist2.GetValueOrDefault(c, 0);
            maxDiff = Math.Max(maxDiff, Math.Abs(cdf1 - cdf2));
        }

        return maxDiff;
    }

    private double CalculateConfidenceLevel(double freqCorr, double tokenOverlap, double lineMatch)
    {
        // Weight the metrics
        return (freqCorr * 0.3) + (tokenOverlap * 0.4) + (lineMatch * 0.3);
    }

    private string ComputeCacheKey(EncodedContext original, RegenerationResult result)
    {
        using var sha256 = SHA256.Create();
        var combined = original.EncodedData + "|" + result.RegeneratedContent + "|" + result.StrategyId;
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hash)[..32];
    }

    private string FormatAgreementMatrix(double[,] matrix, int size)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < size; i++)
        {
            for (int j = 0; j < size; j++)
            {
                sb.Append($"{matrix[i, j]:F4} ");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private List<DiscrepancyInfo> FindDiscrepancies(
        IReadOnlyList<RegenerationResult> results,
        double[,] agreementMatrix)
    {
        var discrepancies = new List<DiscrepancyInfo>();

        for (int i = 0; i < results.Count; i++)
        {
            for (int j = i + 1; j < results.Count; j++)
            {
                if (agreementMatrix[i, j] < 0.99)
                {
                    discrepancies.Add(new DiscrepancyInfo
                    {
                        Strategy1 = results[i].StrategyId,
                        Strategy2 = results[j].StrategyId,
                        AgreementScore = agreementMatrix[i, j],
                        Description = $"Results differ with {agreementMatrix[i, j]:P2} agreement"
                    });
                }
            }
        }

        return discrepancies;
    }

    private sealed class VerificationCache
    {
        public VerificationResult Result { get; init; } = new();
        public DateTime Timestamp { get; init; }
    }
}

/// <summary>
/// Configuration for the accuracy verifier.
/// </summary>
public sealed record VerifierConfiguration
{
    /// <summary>Accuracy threshold for passing verification.</summary>
    public double AccuracyThreshold { get; init; } = 0.9999999;

    /// <summary>Enable caching of verification results.</summary>
    public bool EnableCaching { get; init; } = true;

    /// <summary>Cache expiry time.</summary>
    public TimeSpan CacheExpiry { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Enable detailed statistical analysis.</summary>
    public bool EnableStatisticalAnalysis { get; init; } = true;
}

/// <summary>
/// Result from verification process.
/// </summary>
public sealed record VerificationResult
{
    /// <summary>Overall verification score (0-1).</summary>
    public double OverallScore { get; init; }

    /// <summary>Whether the result meets accuracy threshold.</summary>
    public bool IsAccurate { get; init; }

    /// <summary>Verification dimensions with individual scores.</summary>
    public List<VerificationDimension> Dimensions { get; init; } = new();

    /// <summary>Method used for verification.</summary>
    public string VerificationMethod { get; init; } = "";

    /// <summary>Reason for failure if not accurate.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Cross-check results if performed.</summary>
    public List<CrossCheckResult> CrossChecks { get; init; } = new();

    /// <summary>Statistical validation details.</summary>
    public StatisticalValidation? StatisticalValidation { get; init; }
}

/// <summary>
/// A verification dimension with score and weight.
/// </summary>
public sealed record VerificationDimension
{
    /// <summary>Dimension name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Dimension score (0-1).</summary>
    public double Score { get; init; }

    /// <summary>Weight in overall calculation.</summary>
    public double Weight { get; init; }

    /// <summary>Descriptive details.</summary>
    public string Details { get; init; } = "";
}

/// <summary>
/// Result from a cross-check.
/// </summary>
public sealed record CrossCheckResult
{
    /// <summary>Check name.</summary>
    public string CheckName { get; init; } = "";

    /// <summary>Whether the check passed.</summary>
    public bool Passed { get; init; }

    /// <summary>Check score (0-1).</summary>
    public double Score { get; init; }

    /// <summary>Descriptive details.</summary>
    public string Details { get; init; } = "";
}

/// <summary>
/// Result from comparing multiple regeneration results.
/// </summary>
public sealed record ComparisonResult
{
    /// <summary>Whether there is strong consensus among results.</summary>
    public bool HasConsensus { get; init; }

    /// <summary>Consensus score (0-1).</summary>
    public double ConsensusScore { get; init; }

    /// <summary>Best result based on verification.</summary>
    public RegenerationResult? BestResult { get; init; }

    /// <summary>Verification of the best result.</summary>
    public VerificationResult? BestVerification { get; init; }

    /// <summary>All verification results.</summary>
    public List<VerificationResult> AllVerifications { get; init; } = new();

    /// <summary>Formatted agreement matrix.</summary>
    public string AgreementMatrix { get; init; } = "";

    /// <summary>Identified discrepancies between results.</summary>
    public List<DiscrepancyInfo> Discrepancies { get; init; } = new();

    /// <summary>Error message if comparison failed.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Information about a discrepancy between results.
/// </summary>
public sealed record DiscrepancyInfo
{
    /// <summary>First strategy ID.</summary>
    public string Strategy1 { get; init; } = "";

    /// <summary>Second strategy ID.</summary>
    public string Strategy2 { get; init; } = "";

    /// <summary>Agreement score between the two.</summary>
    public double AgreementScore { get; init; }

    /// <summary>Description of the discrepancy.</summary>
    public string Description { get; init; } = "";
}

/// <summary>
/// Statistical validation results.
/// </summary>
public sealed record StatisticalValidation
{
    /// <summary>Character frequency correlation.</summary>
    public double FrequencyCorrelation { get; init; }

    /// <summary>Token overlap ratio.</summary>
    public double TokenOverlap { get; init; }

    /// <summary>Line match rate.</summary>
    public double LineMatchRate { get; init; }

    /// <summary>Chi-square test statistic.</summary>
    public double ChiSquareStatistic { get; init; }

    /// <summary>Kolmogorov-Smirnov test statistic.</summary>
    public double KolmogorovSmirnovStatistic { get; init; }

    /// <summary>Whether data is statistically equivalent.</summary>
    public bool IsStatisticallyEquivalent { get; init; }

    /// <summary>Overall confidence level (0-1).</summary>
    public double ConfidenceLevel { get; init; }
}
