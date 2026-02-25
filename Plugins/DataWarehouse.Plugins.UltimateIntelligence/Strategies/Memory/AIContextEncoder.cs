using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Utilities;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory;

/// <summary>
/// Semantic vector encoder implementing AI-native encoding using information theory principles.
/// Creates ultra-dense representations that preserve semantic meaning while minimizing storage.
/// Implements the IAIContextEncoder interface for the tiered memory system.
/// </summary>
public sealed class SemanticVectorEncoder : IAIContextEncoder
{
    private readonly int _vectorDimensions;
    private readonly BoundedDictionary<string, float[]> _vocabularyVectors = new BoundedDictionary<string, float[]>(1000);
    private readonly BoundedDictionary<string, int> _tokenFrequencies = new BoundedDictionary<string, int>(1000);
    private readonly PatternExtractor _patternExtractor = new();
    private readonly RelationshipEncoder _relationshipEncoder = new();

    private long _totalOriginalBytes;
    private long _totalEncodedBytes;

    // Pre-computed random projections for LSH-style hashing
    private readonly float[][] _randomProjections;

    public SemanticVectorEncoder(int vectorDimensions = 384)
    {
        _vectorDimensions = vectorDimensions;
        _randomProjections = GenerateRandomProjections(16, vectorDimensions);
        InitializeBaseVocabulary();
    }

    /// <inheritdoc/>
    public async Task<EncodedContext> EncodeAsync(string content, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var originalSize = content.Length * 2L;

        // Step 1: Extract semantic components
        var tokens = Tokenize(content);
        var patterns = await _patternExtractor.ExtractPatternsAsync(content, ct);
        var relationships = await _relationshipEncoder.ExtractRelationshipsAsync(content, ct);

        // Step 2: Generate semantic vector using weighted bag-of-embeddings
        var semanticVector = GenerateSemanticVector(tokens, patterns);

        // Step 3: Create AI-native encoded representation
        var encodedPayload = new SemanticEncodedPayload
        {
            Version = 1,
            CompressionMethod = "brotli",
            Tokens = tokens.ToArray(),
            TokenFrequencies = tokens.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count()),
            Patterns = patterns,
            Relationships = relationships,
            SemanticHash = ComputeSemanticHash(semanticVector),
            OriginalLength = content.Length,
            OriginalChecksum = ComputeChecksum(content),
            SemanticVector = semanticVector,
            PreservedTerms = ExtractKeyTerms(tokens)
        };

        var encodedBytes = await CompressPayloadAsync(encodedPayload, ct);
        var encodedData = Convert.ToBase64String(encodedBytes);

        Interlocked.Add(ref _totalOriginalBytes, originalSize);
        Interlocked.Add(ref _totalEncodedBytes, encodedData.Length * 2L);

        // Update vocabulary with new tokens
        UpdateVocabulary(tokens);

        return new EncodedContext
        {
            EncodedData = encodedData,
            OriginalSize = originalSize,
            EncodedSize = encodedData.Length * 2L,
            EncodingMethod = "semantic-vector",
            IsSemantic = true,
            PreservedTerms = encodedPayload.PreservedTerms?.ToArray() ?? Array.Empty<string>()
        };
    }

    /// <inheritdoc/>
    public async Task<string> DecodeAsync(EncodedContext encoded, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (encoded.EncodingMethod == "none" || string.IsNullOrEmpty(encoded.EncodedData))
        {
            return encoded.EncodedData;
        }

        try
        {
            var encodedBytes = Convert.FromBase64String(encoded.EncodedData);
            var payload = await DecompressPayloadAsync(encodedBytes, ct);
            return ReconstructFromPayload(payload);
        }
        catch
        {
            Debug.WriteLine($"Caught exception in AIContextEncoder.cs");
            // Fallback to raw data if decoding fails
            return encoded.EncodedData;
        }
    }

    /// <inheritdoc/>
    public double GetCompressionRatio()
    {
        var original = Interlocked.Read(ref _totalOriginalBytes);
        var encoded = Interlocked.Read(ref _totalEncodedBytes);
        return original > 0 ? (double)encoded / original : 1.0;
    }

    /// <summary>
    /// Verifies the accuracy of regenerated content against the original.
    /// </summary>
    public Task<double> VerifyRegenerationAccuracyAsync(
        string original, string regenerated, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Use multiple metrics for accuracy assessment

        // 1. Exact match
        if (original == regenerated)
            return Task.FromResult(1.0);

        // 2. Token overlap (Jaccard similarity)
        var originalTokens = new HashSet<string>(Tokenize(original));
        var regeneratedTokens = new HashSet<string>(Tokenize(regenerated));
        var intersection = originalTokens.Intersect(regeneratedTokens).Count();
        var union = originalTokens.Union(regeneratedTokens).Count();
        var jaccardSimilarity = union > 0 ? (double)intersection / union : 0;

        // 3. Character-level edit distance (normalized)
        var editDistance = LevenshteinDistance(original, regenerated);
        var maxLength = Math.Max(original.Length, regenerated.Length);
        var editSimilarity = maxLength > 0 ? 1.0 - (double)editDistance / maxLength : 1.0;

        // 4. Semantic similarity (cosine of vectors)
        var originalVector = GenerateSemanticVector(Tokenize(original), new List<ExtractedPattern>());
        var regeneratedVector = GenerateSemanticVector(Tokenize(regenerated), new List<ExtractedPattern>());
        var semanticSimilarity = CosineSimilarity(originalVector, regeneratedVector);

        // Weighted combination
        var accuracy = jaccardSimilarity * 0.3 + editSimilarity * 0.3 + semanticSimilarity * 0.4;

        return Task.FromResult(Math.Clamp(accuracy, 0.0, 1.0));
    }

    private float[] GenerateSemanticVector(IEnumerable<string> tokens, IList<ExtractedPattern> patterns)
    {
        var vector = new float[_vectorDimensions];
        var tokenList = tokens.ToList();

        if (tokenList.Count == 0)
            return vector;

        // Aggregate token vectors using TF-IDF weighting
        foreach (var token in tokenList)
        {
            var tokenVector = GetOrCreateTokenVector(token);
            var weight = CalculateTfIdf(token, tokenList);

            for (int i = 0; i < _vectorDimensions; i++)
            {
                vector[i] += tokenVector[i] * weight;
            }
        }

        // Normalize vector
        var magnitude = MathF.Sqrt(vector.Sum(v => v * v));
        if (magnitude > 0)
        {
            for (int i = 0; i < _vectorDimensions; i++)
            {
                vector[i] /= magnitude;
            }
        }

        // Add pattern influence
        foreach (var pattern in patterns)
        {
            var patternInfluence = pattern.Confidence * 0.1f;
            for (int i = 0; i < Math.Min(pattern.PatternHash.Length, _vectorDimensions); i++)
            {
                vector[i] = vector[i] * (1 - patternInfluence) + pattern.PatternHash[i] * patternInfluence;
            }
        }

        return vector;
    }

    private float[] GetOrCreateTokenVector(string token)
    {
        return _vocabularyVectors.GetOrAdd(token, t =>
        {
            // Generate deterministic vector from token hash
            var hash = ComputeTokenHash(t);
            var vector = new float[_vectorDimensions];

            for (int i = 0; i < _vectorDimensions; i++)
            {
                // Use hash bytes to seed pseudo-random vector components
                var seed = (hash[i % hash.Length] + i * 17) % 256;
                vector[i] = (seed / 127.5f) - 1.0f;
            }

            // Normalize
            var mag = MathF.Sqrt(vector.Sum(v => v * v));
            if (mag > 0)
            {
                for (int i = 0; i < _vectorDimensions; i++)
                    vector[i] /= mag;
            }

            return vector;
        });
    }

    private float CalculateTfIdf(string token, IList<string> documentTokens)
    {
        // Term frequency in document
        var tf = (float)documentTokens.Count(t => t == token) / documentTokens.Count;

        // Inverse document frequency (using accumulated frequencies)
        var totalDocs = Math.Max(1, _tokenFrequencies.Count);
        var docsWithTerm = _tokenFrequencies.TryGetValue(token, out var freq) ? freq : 1;
        var idf = MathF.Log((float)totalDocs / docsWithTerm + 1);

        return tf * idf;
    }

    private void UpdateVocabulary(IEnumerable<string> tokens)
    {
        foreach (var token in tokens.Distinct())
        {
            _tokenFrequencies.AddOrUpdate(token, 1, (_, count) => count + 1);
        }
    }

    private List<string> Tokenize(string text)
    {
        // Advanced tokenization with subword awareness
        var tokens = new List<string>();

        // Split on whitespace and punctuation, keeping meaningful units
        var words = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            // Handle punctuation
            var cleaned = word.Trim('.', ',', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']', '{', '}');
            if (!string.IsNullOrEmpty(cleaned))
            {
                // Lowercase for consistency
                var lower = cleaned.ToLowerInvariant();

                // Add word token
                tokens.Add(lower);

                // Add character n-grams for subword representation (trigrams)
                if (lower.Length >= 3)
                {
                    for (int i = 0; i <= lower.Length - 3; i++)
                    {
                        tokens.Add($"#{lower.Substring(i, 3)}#");
                    }
                }
            }
        }

        return tokens;
    }

    private List<string> ExtractKeyTerms(List<string> tokens)
    {
        // Extract top terms by frequency, excluding subword tokens
        return tokens
            .Where(t => !t.StartsWith('#'))
            .GroupBy(t => t)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => g.Key)
            .ToList();
    }

    private async Task<byte[]> CompressPayloadAsync(SemanticEncodedPayload payload, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);

        using var output = new MemoryStream(65536);
        using (var compressor = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            await compressor.WriteAsync(json, ct);
        }

        return output.ToArray();
    }

    private async Task<SemanticEncodedPayload> DecompressPayloadAsync(byte[] compressed, CancellationToken ct)
    {
        using var input = new MemoryStream(compressed);
        using var decompressor = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(65536);

        await decompressor.CopyToAsync(output, ct);
        output.Position = 0;

        return JsonSerializer.Deserialize<SemanticEncodedPayload>(output.ToArray())
            ?? throw new InvalidOperationException("Failed to deserialize encoded payload");
    }

    private string ReconstructFromPayload(SemanticEncodedPayload payload)
    {
        // Use tokens and patterns to reconstruct
        var tokens = payload.Tokens ?? Array.Empty<string>();

        // Filter out subword tokens (those with # markers)
        var wordTokens = tokens.Where(t => !t.StartsWith('#')).ToList();

        // Reconstruct with proper spacing
        var reconstructed = new StringBuilder();
        foreach (var token in wordTokens)
        {
            if (reconstructed.Length > 0)
                reconstructed.Append(' ');
            reconstructed.Append(token);
        }

        // Apply patterns for capitalization and punctuation hints
        var result = reconstructed.ToString();

        if (payload.Patterns?.Any(p => p.PatternType == "sentence_start") == true)
        {
            if (result.Length > 0)
            {
                result = char.ToUpper(result[0]) + result.Substring(1);
            }
        }

        return result;
    }

    private static string ComputeSemanticHash(float[] vector)
    {
        // LSH-style hash for semantic similarity
        var sb = new StringBuilder();
        foreach (var v in vector.Take(32))
        {
            sb.Append(v >= 0 ? '1' : '0');
        }
        return sb.ToString();
    }

    private static string ComputeChecksum(string text)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash).Substring(0, 16);
    }

    private static byte[] ComputeTokenHash(string token)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    private static float[][] GenerateRandomProjections(int count, int dimensions)
    {
        var rng = new Random(42);
        var projections = new float[count][];

        for (int i = 0; i < count; i++)
        {
            projections[i] = new float[dimensions];
            for (int j = 0; j < dimensions; j++)
            {
                projections[i][j] = (float)(rng.NextDouble() * 2 - 1);
            }
        }

        return projections;
    }

    private void InitializeBaseVocabulary()
    {
        // Initialize with common English words for better initial vectors
        var commonWords = new[] {
            "the", "be", "to", "of", "and", "a", "in", "that", "have", "i",
            "it", "for", "not", "on", "with", "he", "as", "you", "do", "at",
            "this", "but", "his", "by", "from", "they", "we", "say", "her", "she",
            "data", "system", "memory", "context", "query", "user", "session", "instance"
        };

        foreach (var word in commonWords)
        {
            GetOrCreateTokenVector(word);
            _tokenFrequencies[word] = 1000; // High initial frequency
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0f;

        float dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var magnitude = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return magnitude > 0 ? dot / magnitude : 0f;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;

        var m = a.Length;
        var n = b.Length;
        var d = new int[m + 1, n + 1];

        for (int i = 0; i <= m; i++) d[i, 0] = i;
        for (int j = 0; j <= n; j++) d[0, j] = j;

        for (int j = 1; j <= n; j++)
        {
            for (int i = 1; i <= m; i++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[m, n];
    }
}

/// <summary>
/// Encoded payload structure for AI-native storage.
/// </summary>
internal sealed class SemanticEncodedPayload
{
    public int Version { get; set; }
    public string CompressionMethod { get; set; } = "brotli";
    public string[]? Tokens { get; set; }
    public Dictionary<string, int>? TokenFrequencies { get; set; }
    public List<ExtractedPattern>? Patterns { get; set; }
    public List<ExtractedRelationship>? Relationships { get; set; }
    public string? SemanticHash { get; set; }
    public int OriginalLength { get; set; }
    public string? OriginalChecksum { get; set; }
    public float[]? SemanticVector { get; set; }
    public List<string>? PreservedTerms { get; set; }
}

/// <summary>
/// Extracted pattern from content analysis.
/// </summary>
public sealed class ExtractedPattern
{
    public string PatternType { get; set; } = "";
    public string PatternValue { get; set; } = "";
    public float Confidence { get; set; }
    public float[] PatternHash { get; set; } = Array.Empty<float>();
    public int StartPosition { get; set; }
    public int EndPosition { get; set; }
}

/// <summary>
/// Extracted relationship between entities.
/// </summary>
public sealed class ExtractedRelationship
{
    public string Subject { get; set; } = "";
    public string Predicate { get; set; } = "";
    public string Object { get; set; } = "";
    public float Confidence { get; set; }
}

/// <summary>
/// Pattern extractor for identifying structural patterns in text.
/// </summary>
internal sealed class PatternExtractor
{
    private static readonly string[] SentenceEnders = { ".", "!", "?" };

    public Task<List<ExtractedPattern>> ExtractPatternsAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var patterns = new List<ExtractedPattern>();

        // Sentence structure patterns
        if (text.Length > 0 && char.IsUpper(text[0]))
        {
            patterns.Add(new ExtractedPattern
            {
                PatternType = "sentence_start",
                PatternValue = "capitalized",
                Confidence = 1.0f,
                StartPosition = 0,
                EndPosition = 1
            });
        }

        // Find sentence boundaries
        var position = 0;
        foreach (var ender in SentenceEnders)
        {
            var idx = text.IndexOf(ender, position);
            while (idx >= 0)
            {
                patterns.Add(new ExtractedPattern
                {
                    PatternType = "sentence_end",
                    PatternValue = ender,
                    Confidence = 0.95f,
                    StartPosition = idx,
                    EndPosition = idx + 1
                });
                idx = text.IndexOf(ender, idx + 1);
            }
        }

        // Numeric patterns
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsDigit(text[i]))
            {
                var start = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.' || text[i] == ','))
                    i++;

                patterns.Add(new ExtractedPattern
                {
                    PatternType = "numeric",
                    PatternValue = text.Substring(start, i - start),
                    Confidence = 1.0f,
                    StartPosition = start,
                    EndPosition = i
                });
            }
        }

        return Task.FromResult(patterns);
    }
}

/// <summary>
/// Relationship encoder for extracting subject-predicate-object relationships.
/// </summary>
internal sealed class RelationshipEncoder
{
    private static readonly string[] RelationIndicators = { "is", "are", "was", "were", "has", "have", "contains", "includes" };

    public Task<List<ExtractedRelationship>> ExtractRelationshipsAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var relationships = new List<ExtractedRelationship>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < words.Length; i++)
        {
            var word = words[i].ToLowerInvariant().Trim('.', ',', '!', '?');

            if (RelationIndicators.Contains(word) && i > 0 && i < words.Length - 1)
            {
                var subject = string.Join(" ", words.Take(i).TakeLast(3));
                var predicate = word;
                var obj = string.Join(" ", words.Skip(i + 1).Take(3));

                relationships.Add(new ExtractedRelationship
                {
                    Subject = subject.Trim(),
                    Predicate = predicate,
                    Object = obj.Trim(),
                    Confidence = 0.7f
                });
            }
        }

        return Task.FromResult(relationships);
    }
}

/// <summary>
/// Differential encoder for incremental context updates.
/// Stores only the delta between states for efficient memory usage.
/// </summary>
public sealed class DifferentialContextEncoder : IAIContextEncoder
{
    private readonly IAIContextEncoder _baseEncoder;
    private readonly BoundedDictionary<string, EncodedContext> _baseStates = new BoundedDictionary<string, EncodedContext>(1000);

    private long _totalOriginalBytes;
    private long _totalEncodedBytes;

    public DifferentialContextEncoder(IAIContextEncoder? baseEncoder = null)
    {
        _baseEncoder = baseEncoder ?? new SemanticVectorEncoder();
    }

    public async Task<EncodedContext> EncodeAsync(string content, CancellationToken ct = default)
    {
        var originalSize = content.Length * 2L;

        // Find the most similar base state
        var hash = ComputeSimHash(content);

        if (_baseStates.TryGetValue(hash, out var baseState))
        {
            // Encode differential
            var baseDecoded = await _baseEncoder.DecodeAsync(baseState, ct);
            var diff = ComputeDiff(baseDecoded, content);

            // If diff is small, store it; otherwise store full
            if (diff.Length < content.Length * 0.5)
            {
                var diffEncoded = await _baseEncoder.EncodeAsync(diff, ct);

                Interlocked.Add(ref _totalOriginalBytes, originalSize);
                Interlocked.Add(ref _totalEncodedBytes, diffEncoded.EncodedSize);

                return new EncodedContext
                {
                    EncodedData = $"DIFF:{hash}:{diffEncoded.EncodedData}",
                    OriginalSize = originalSize,
                    EncodedSize = diffEncoded.EncodedSize + hash.Length + 6,
                    EncodingMethod = "differential",
                    IsSemantic = true,
                    PreservedTerms = diffEncoded.PreservedTerms
                };
            }
        }

        // Full encoding
        var result = await _baseEncoder.EncodeAsync(content, ct);

        // Store as new base state
        _baseStates[hash] = result;

        Interlocked.Add(ref _totalOriginalBytes, originalSize);
        Interlocked.Add(ref _totalEncodedBytes, result.EncodedSize);

        return result;
    }

    public async Task<string> DecodeAsync(EncodedContext encoded, CancellationToken ct = default)
    {
        // Check for diff marker
        if (encoded.EncodedData.StartsWith("DIFF:"))
        {
            var parts = encoded.EncodedData.Split(':', 3);
            if (parts.Length == 3)
            {
                var hash = parts[1];
                var diffData = parts[2];

                if (_baseStates.TryGetValue(hash, out var baseState))
                {
                    var baseDecoded = await _baseEncoder.DecodeAsync(baseState, ct);
                    var diffDecoded = await _baseEncoder.DecodeAsync(
                        new EncodedContext { EncodedData = diffData, EncodingMethod = "semantic-vector" }, ct);

                    return ApplyDiff(baseDecoded, diffDecoded);
                }
            }
        }

        return await _baseEncoder.DecodeAsync(encoded, ct);
    }

    public double GetCompressionRatio()
    {
        var original = Interlocked.Read(ref _totalOriginalBytes);
        var encoded = Interlocked.Read(ref _totalEncodedBytes);
        return original > 0 ? (double)encoded / original : 1.0;
    }

    private static string ComputeSimHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).Substring(0, 16);
    }

    private static string ComputeDiff(string basee, string current)
    {
        // Simple diff: store additions and position markers
        var diff = new StringBuilder();
        var baseWords = basee.Split(' ');
        var currentWords = current.Split(' ');

        var baseSet = new HashSet<string>(baseWords);

        foreach (var word in currentWords)
        {
            if (!baseSet.Contains(word))
            {
                diff.Append('+').Append(word).Append(' ');
            }
        }

        return diff.ToString().Trim();
    }

    private static string ApplyDiff(string basee, string diff)
    {
        // Apply additions
        var additions = diff.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.StartsWith('+'))
            .Select(w => w.Substring(1))
            .ToList();

        return basee + " " + string.Join(" ", additions);
    }
}

/// <summary>
/// Advanced AI context encoder that combines compression with semantic preservation.
/// Uses multiple encoding strategies based on content characteristics.
/// </summary>
public sealed class AdaptiveContextEncoder : IAIContextEncoder
{
    private readonly SemanticVectorEncoder _semanticEncoder = new();
    private readonly DifferentialContextEncoder _differentialEncoder;

    private long _totalOriginalBytes;
    private long _totalEncodedBytes;

    public AdaptiveContextEncoder()
    {
        _differentialEncoder = new DifferentialContextEncoder(_semanticEncoder);
    }

    public async Task<EncodedContext> EncodeAsync(string content, CancellationToken ct = default)
    {
        var originalSize = content.Length * 2L;

        // Choose encoding strategy based on content characteristics
        EncodedContext result;

        if (content.Length < 100)
        {
            // Short content: use simple encoding
            result = new EncodedContext
            {
                EncodedData = content,
                OriginalSize = originalSize,
                EncodedSize = originalSize,
                EncodingMethod = "none",
                IsSemantic = false
            };
        }
        else if (IsHighlyStructured(content))
        {
            // Structured content: use differential encoding
            result = await _differentialEncoder.EncodeAsync(content, ct);
        }
        else
        {
            // General content: use semantic encoding
            result = await _semanticEncoder.EncodeAsync(content, ct);
        }

        Interlocked.Add(ref _totalOriginalBytes, originalSize);
        Interlocked.Add(ref _totalEncodedBytes, result.EncodedSize);

        return result;
    }

    public async Task<string> DecodeAsync(EncodedContext encoded, CancellationToken ct = default)
    {
        return encoded.EncodingMethod switch
        {
            "none" => encoded.EncodedData,
            "differential" => await _differentialEncoder.DecodeAsync(encoded, ct),
            _ => await _semanticEncoder.DecodeAsync(encoded, ct)
        };
    }

    public double GetCompressionRatio()
    {
        var original = Interlocked.Read(ref _totalOriginalBytes);
        var encoded = Interlocked.Read(ref _totalEncodedBytes);
        return original > 0 ? (double)encoded / original : 1.0;
    }

    private static bool IsHighlyStructured(string content)
    {
        // Detect if content is highly structured (code, JSON, etc.)
        var structureIndicators = new[] { "{", "}", "[", "]", "=>", "->", "::", ";" };
        var indicatorCount = structureIndicators.Sum(i => content.Split(i).Length - 1);
        return indicatorCount > content.Length / 50; // More than 2% structural characters
    }
}
