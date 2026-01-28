using DataWarehouse.Plugins.SemanticUnderstanding.Analyzers;
using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.SemanticUnderstanding.Engine
{
    /// <summary>
    /// Engine for detecting duplicate and near-duplicate documents.
    /// Uses hash-based, n-gram, and semantic similarity methods.
    /// </summary>
    public sealed class DuplicateDetectionEngine
    {
        private readonly IAIProvider? _aiProvider;
        private readonly IVectorStore? _vectorStore;
        private readonly ConcurrentDictionary<string, DocumentFingerprint> _fingerprints;

        public DuplicateDetectionEngine(
            IAIProvider? aiProvider = null,
            IVectorStore? vectorStore = null)
        {
            _aiProvider = aiProvider;
            _vectorStore = vectorStore;
            _fingerprints = new ConcurrentDictionary<string, DocumentFingerprint>();
        }

        /// <summary>
        /// Detects duplicates for a document against a corpus.
        /// </summary>
        public async Task<DuplicateDetectionResult> DetectDuplicatesAsync(
            string documentId,
            string text,
            IEnumerable<(string Id, string Text, float[]? Embedding)> corpus,
            DuplicateDetectionOptions? options = null,
            CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            options ??= new DuplicateDetectionOptions();

            var duplicates = new List<DuplicateMatch>();
            var nearDuplicates = new List<DuplicateMatch>();
            var corpusList = corpus.ToList();

            // Compute fingerprint for source document
            var sourceFingerprint = ComputeFingerprint(documentId, text);
            float[]? sourceEmbedding = null;

            // Get embedding for semantic comparison
            if (options.EnableSemanticDetection && _aiProvider != null &&
                _aiProvider.Capabilities.HasFlag(AICapabilities.Embeddings))
            {
                sourceEmbedding = await _aiProvider.GetEmbeddingsAsync(
                    TruncateText(text, 5000), ct);
            }

            // Compare against corpus
            await Parallel.ForEachAsync(
                corpusList,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (doc, token) =>
                {
                    if (doc.Id == documentId)
                        return;

                    var match = await ComparDocumentsAsync(
                        documentId, sourceFingerprint, sourceEmbedding, text,
                        doc.Id, doc.Text, doc.Embedding,
                        options, token);

                    if (match != null)
                    {
                        if (match.Type == DuplicateType.Exact)
                        {
                            lock (duplicates)
                            {
                                duplicates.Add(match);
                            }
                        }
                        else if (match.Similarity >= options.ExactDuplicateThreshold)
                        {
                            lock (duplicates)
                            {
                                duplicates.Add(match);
                            }
                        }
                        else if (match.Similarity >= options.NearDuplicateThreshold)
                        {
                            lock (nearDuplicates)
                            {
                                nearDuplicates.Add(match);
                            }
                        }
                    }
                });

            sw.Stop();

            return new DuplicateDetectionResult
            {
                Duplicates = duplicates.OrderByDescending(d => d.Similarity).ToList(),
                NearDuplicates = nearDuplicates.OrderByDescending(d => d.Similarity).ToList(),
                Duration = sw.Elapsed,
                DocumentsCompared = corpusList.Count
            };
        }

        /// <summary>
        /// Compares two documents for similarity.
        /// </summary>
        private async Task<DuplicateMatch?> ComparDocumentsAsync(
            string sourceId,
            DocumentFingerprint sourceFingerprint,
            float[]? sourceEmbedding,
            string sourceText,
            string targetId,
            string targetText,
            float[]? targetEmbedding,
            DuplicateDetectionOptions options,
            CancellationToken ct)
        {
            var matchingFeatures = new List<string>();

            // Compute target fingerprint
            var targetFingerprint = _fingerprints.GetOrAdd(targetId,
                _ => ComputeFingerprint(targetId, targetText));

            // Exact hash match
            if (options.EnableHashComparison &&
                sourceFingerprint.ContentHash == targetFingerprint.ContentHash)
            {
                return new DuplicateMatch
                {
                    SourceId = sourceId,
                    DuplicateId = targetId,
                    Similarity = 1.0f,
                    Type = DuplicateType.Exact,
                    MatchingFeatures = new List<string> { "Identical content hash" },
                    Recommendation = DuplicateRecommendation.DeleteDuplicate
                };
            }

            var scores = new List<float>();

            // MinHash similarity (for near-duplicate detection)
            if (options.EnableMinHash)
            {
                var minHashSimilarity = ComputeMinHashSimilarity(
                    sourceFingerprint.MinHashSignature,
                    targetFingerprint.MinHashSignature);

                if (minHashSimilarity >= options.NearDuplicateThreshold)
                {
                    scores.Add(minHashSimilarity);
                    matchingFeatures.Add($"MinHash similarity: {minHashSimilarity:P0}");
                }
            }

            // SimHash similarity
            if (options.EnableSimHash)
            {
                var simHashSimilarity = ComputeSimHashSimilarity(
                    sourceFingerprint.SimHash,
                    targetFingerprint.SimHash);

                if (simHashSimilarity >= options.NearDuplicateThreshold)
                {
                    scores.Add(simHashSimilarity);
                    matchingFeatures.Add($"SimHash similarity: {simHashSimilarity:P0}");
                }
            }

            // Semantic similarity
            if (options.EnableSemanticDetection &&
                sourceEmbedding != null && targetEmbedding != null)
            {
                var semanticSimilarity = VectorMath.CosineSimilarity(
                    sourceEmbedding, targetEmbedding);

                if (semanticSimilarity >= options.SemanticThreshold)
                {
                    scores.Add(semanticSimilarity);
                    matchingFeatures.Add($"Semantic similarity: {semanticSimilarity:P0}");
                }
            }
            else if (options.EnableSemanticDetection &&
                     sourceEmbedding != null && targetEmbedding == null &&
                     _aiProvider != null)
            {
                // Generate embedding for target on-the-fly
                targetEmbedding = await _aiProvider.GetEmbeddingsAsync(
                    TruncateText(targetText, 5000), ct);

                if (targetEmbedding != null)
                {
                    var semanticSimilarity = VectorMath.CosineSimilarity(
                        sourceEmbedding, targetEmbedding);

                    if (semanticSimilarity >= options.SemanticThreshold)
                    {
                        scores.Add(semanticSimilarity);
                        matchingFeatures.Add($"Semantic similarity: {semanticSimilarity:P0}");
                    }
                }
            }

            // N-gram overlap
            if (options.EnableNGramComparison)
            {
                var ngramSimilarity = ComputeNGramSimilarity(sourceText, targetText, 3);
                if (ngramSimilarity >= options.NearDuplicateThreshold)
                {
                    scores.Add(ngramSimilarity);
                    matchingFeatures.Add($"N-gram overlap: {ngramSimilarity:P0}");
                }
            }

            if (scores.Count == 0)
                return null;

            var averageSimilarity = scores.Average();
            var duplicateType = DetermineDuplicateType(averageSimilarity, matchingFeatures, options);
            var recommendation = DetermineRecommendation(duplicateType, sourceFingerprint, targetFingerprint);

            return new DuplicateMatch
            {
                SourceId = sourceId,
                DuplicateId = targetId,
                Similarity = averageSimilarity,
                Type = duplicateType,
                MatchingFeatures = matchingFeatures,
                Recommendation = recommendation
            };
        }

        /// <summary>
        /// Computes a document fingerprint including various hashes.
        /// </summary>
        private DocumentFingerprint ComputeFingerprint(string documentId, string text)
        {
            var normalizedText = NormalizeText(text);

            return new DocumentFingerprint
            {
                DocumentId = documentId,
                ContentHash = ComputeContentHash(normalizedText),
                MinHashSignature = ComputeMinHash(normalizedText, 128),
                SimHash = ComputeSimHash(normalizedText),
                WordCount = CountWords(normalizedText),
                CreatedAt = DateTime.UtcNow
            };
        }

        private string NormalizeText(string text)
        {
            // Convert to lowercase
            var normalized = text.ToLowerInvariant();

            // Remove extra whitespace
            normalized = Regex.Replace(normalized, @"\s+", " ");

            // Remove punctuation (keep alphanumeric and spaces)
            normalized = Regex.Replace(normalized, @"[^\w\s]", "");

            return normalized.Trim();
        }

        private string ComputeContentHash(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Computes MinHash signature for Jaccard similarity estimation.
        /// </summary>
        private ulong[] ComputeMinHash(string text, int numHashes)
        {
            var shingles = GetShingles(text, 3);
            var signature = new ulong[numHashes];

            // Initialize to max value
            for (int i = 0; i < numHashes; i++)
            {
                signature[i] = ulong.MaxValue;
            }

            // For each shingle, compute hash with each hash function
            foreach (var shingle in shingles)
            {
                var baseHash = ComputeHash64(shingle);

                for (int i = 0; i < numHashes; i++)
                {
                    // Different hash function for each position
                    var hash = MurmurHash(baseHash, (uint)i);
                    if (hash < signature[i])
                    {
                        signature[i] = hash;
                    }
                }
            }

            return signature;
        }

        /// <summary>
        /// Computes SimHash for Hamming distance comparison.
        /// </summary>
        private ulong ComputeSimHash(string text)
        {
            var tokens = Tokenize(text);
            var fingerprint = new int[64];

            foreach (var token in tokens)
            {
                var hash = ComputeHash64(token);

                for (int i = 0; i < 64; i++)
                {
                    if ((hash & (1UL << i)) != 0)
                    {
                        fingerprint[i]++;
                    }
                    else
                    {
                        fingerprint[i]--;
                    }
                }
            }

            ulong simhash = 0;
            for (int i = 0; i < 64; i++)
            {
                if (fingerprint[i] > 0)
                {
                    simhash |= (1UL << i);
                }
            }

            return simhash;
        }

        private HashSet<string> GetShingles(string text, int k)
        {
            var shingles = new HashSet<string>();

            for (int i = 0; i <= text.Length - k; i++)
            {
                shingles.Add(text.Substring(i, k));
            }

            return shingles;
        }

        private List<string> Tokenize(string text)
        {
            return Regex.Matches(text, @"\b\w+\b")
                .Select(m => m.Value)
                .Where(t => t.Length > 2)
                .ToList();
        }

        private ulong ComputeHash64(string input)
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(bytes);
            return BitConverter.ToUInt64(hash, 0);
        }

        private ulong MurmurHash(ulong key, uint seed)
        {
            unchecked
            {
                const ulong m = 0xc6a4a7935bd1e995UL;
                const int r = 47;

                ulong h = seed ^ (8UL * m);
                key *= m;
                key ^= key >> r;
                key *= m;
                h ^= key;
                h *= m;
                h ^= h >> r;
                h *= m;
                h ^= h >> r;

                return h;
            }
        }

        private float ComputeMinHashSimilarity(ulong[] sig1, ulong[] sig2)
        {
            if (sig1.Length != sig2.Length)
                return 0;

            var matches = 0;
            for (int i = 0; i < sig1.Length; i++)
            {
                if (sig1[i] == sig2[i])
                    matches++;
            }

            return (float)matches / sig1.Length;
        }

        private float ComputeSimHashSimilarity(ulong hash1, ulong hash2)
        {
            var xor = hash1 ^ hash2;
            var differentBits = CountBits(xor);
            return 1.0f - (float)differentBits / 64;
        }

        private int CountBits(ulong value)
        {
            var count = 0;
            while (value != 0)
            {
                count++;
                value &= value - 1;
            }
            return count;
        }

        private float ComputeNGramSimilarity(string text1, string text2, int n)
        {
            var ngrams1 = GetNGrams(text1, n);
            var ngrams2 = GetNGrams(text2, n);

            if (ngrams1.Count == 0 || ngrams2.Count == 0)
                return 0;

            var intersection = ngrams1.Intersect(ngrams2).Count();
            var union = ngrams1.Union(ngrams2).Count();

            return union > 0 ? (float)intersection / union : 0;
        }

        private HashSet<string> GetNGrams(string text, int n)
        {
            var tokens = Tokenize(text);
            var ngrams = new HashSet<string>();

            for (int i = 0; i <= tokens.Count - n; i++)
            {
                var ngram = string.Join(" ", tokens.Skip(i).Take(n));
                ngrams.Add(ngram);
            }

            return ngrams;
        }

        private int CountWords(string text)
        {
            return Regex.Matches(text, @"\b\w+\b").Count;
        }

        private DuplicateType DetermineDuplicateType(
            float similarity,
            List<string> features,
            DuplicateDetectionOptions options)
        {
            if (similarity >= options.ExactDuplicateThreshold)
                return DuplicateType.NearDuplicate;

            var hasSemanticMatch = features.Any(f => f.Contains("Semantic"));

            if (hasSemanticMatch && similarity >= options.SemanticThreshold)
                return DuplicateType.Semantic;

            if (similarity >= options.NearDuplicateThreshold)
                return DuplicateType.Partial;

            return DuplicateType.NearDuplicate;
        }

        private DuplicateRecommendation DetermineRecommendation(
            DuplicateType type,
            DocumentFingerprint source,
            DocumentFingerprint target)
        {
            switch (type)
            {
                case DuplicateType.Exact:
                    // Keep the older one, delete newer
                    return source.CreatedAt < target.CreatedAt
                        ? DuplicateRecommendation.DeleteDuplicate
                        : DuplicateRecommendation.KeepNewer;

                case DuplicateType.NearDuplicate:
                    // Keep the one with more content
                    return source.WordCount >= target.WordCount
                        ? DuplicateRecommendation.DeleteDuplicate
                        : DuplicateRecommendation.KeepNewer;

                case DuplicateType.Semantic:
                    // Keep both but link them
                    return DuplicateRecommendation.KeepBoth;

                case DuplicateType.Version:
                    return DuplicateRecommendation.LinkAsVersions;

                case DuplicateType.Partial:
                    return DuplicateRecommendation.KeepBoth;

                default:
                    return DuplicateRecommendation.KeepBoth;
            }
        }

        /// <summary>
        /// Clears the fingerprint cache.
        /// </summary>
        public void ClearCache()
        {
            _fingerprints.Clear();
        }

        private static string TruncateText(string text, int maxLength)
        {
            return text.Length <= maxLength ? text : text[..maxLength];
        }

        private sealed class DocumentFingerprint
        {
            public required string DocumentId { get; init; }
            public required string ContentHash { get; init; }
            public required ulong[] MinHashSignature { get; init; }
            public required ulong SimHash { get; init; }
            public int WordCount { get; init; }
            public DateTime CreatedAt { get; init; }
        }
    }

    /// <summary>
    /// Options for duplicate detection.
    /// </summary>
    public sealed class DuplicateDetectionOptions
    {
        /// <summary>Enable exact hash comparison.</summary>
        public bool EnableHashComparison { get; init; } = true;

        /// <summary>Enable MinHash for near-duplicate detection.</summary>
        public bool EnableMinHash { get; init; } = true;

        /// <summary>Enable SimHash for similarity.</summary>
        public bool EnableSimHash { get; init; } = true;

        /// <summary>Enable semantic (embedding-based) detection.</summary>
        public bool EnableSemanticDetection { get; init; } = true;

        /// <summary>Enable N-gram comparison.</summary>
        public bool EnableNGramComparison { get; init; } = true;

        /// <summary>Threshold for exact duplicates.</summary>
        public float ExactDuplicateThreshold { get; init; } = 0.95f;

        /// <summary>Threshold for near-duplicates.</summary>
        public float NearDuplicateThreshold { get; init; } = 0.8f;

        /// <summary>Threshold for semantic similarity.</summary>
        public float SemanticThreshold { get; init; } = 0.85f;
    }
}
