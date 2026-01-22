using DataWarehouse.SDK.Contracts;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.ContentProcessing
{
    /// <summary>
    /// Production-ready embedding generator plugin for DataWarehouse.
    /// Generates vector embeddings using TF-IDF and Word2Vec-style algorithms.
    /// No external API dependencies - all processing is done locally.
    /// Supports configurable dimensions and batch processing.
    /// </summary>
    public sealed class EmbeddingGeneratorPlugin : ContentProcessorPluginBase
    {
        public override string Id => "datawarehouse.contentprocessing.embeddinggenerator";
        public override string Name => "Embedding Generator Processor";
        public override string Version => "1.0.0";
        public override ContentProcessingType ProcessingType => ContentProcessingType.EmbeddingGeneration;

        public override string[] SupportedContentTypes => new[]
        {
            "text/*",
            "application/json",
            "application/xml"
        };

        /// <summary>
        /// Supported embedding dimensions.
        /// </summary>
        public static readonly int[] SupportedDimensions = { 128, 256, 512, 1024 };

        private readonly VocabularyManager _vocabulary;
        private readonly TfIdfEmbedder _tfidfEmbedder;
        private readonly Word2VecStyleEmbedder _word2vecEmbedder;
        private readonly TextTokenizer _tokenizer;
        private readonly object _statsLock = new();

        private long _documentsProcessed;
        private long _tokensProcessed;
        private int _currentDimension = 256;
        private EmbeddingMode _currentMode = EmbeddingMode.TfIdf;

        public EmbeddingGeneratorPlugin() : this(256, EmbeddingMode.TfIdf) { }

        public EmbeddingGeneratorPlugin(int dimension, EmbeddingMode mode = EmbeddingMode.TfIdf)
        {
            if (!SupportedDimensions.Contains(dimension))
                throw new ArgumentException($"Dimension must be one of: {string.Join(", ", SupportedDimensions)}");

            _currentDimension = dimension;
            _currentMode = mode;

            _vocabulary = new VocabularyManager(dimension);
            _tfidfEmbedder = new TfIdfEmbedder(_vocabulary);
            _word2vecEmbedder = new Word2VecStyleEmbedder(_vocabulary, dimension);
            _tokenizer = new TextTokenizer();
        }

        /// <summary>
        /// Configures the embedding dimension.
        /// </summary>
        public void SetDimension(int dimension)
        {
            if (!SupportedDimensions.Contains(dimension))
                throw new ArgumentException($"Dimension must be one of: {string.Join(", ", SupportedDimensions)}");

            _currentDimension = dimension;
            _vocabulary.SetDimension(dimension);
        }

        /// <summary>
        /// Configures the embedding mode.
        /// </summary>
        public void SetMode(EmbeddingMode mode)
        {
            _currentMode = mode;
        }

        public override async Task<ContentProcessingResult> ProcessAsync(
            Stream content,
            string contentType,
            Dictionary<string, object>? context = null,
            CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                // Read content
                using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = await reader.ReadToEndAsync(ct);

                // Parse context options
                var dimension = _currentDimension;
                var mode = _currentMode;

                if (context != null)
                {
                    if (context.TryGetValue("dimension", out var dimObj) && dimObj is int dim)
                    {
                        if (SupportedDimensions.Contains(dim))
                            dimension = dim;
                    }

                    if (context.TryGetValue("mode", out var modeObj))
                    {
                        if (modeObj is EmbeddingMode m)
                            mode = m;
                        else if (modeObj is string s && Enum.TryParse<EmbeddingMode>(s, true, out var parsed))
                            mode = parsed;
                    }
                }

                // Generate embedding
                var embedding = await GenerateEmbeddingAsync(text, dimension, mode, ct);

                Interlocked.Increment(ref _documentsProcessed);
                sw.Stop();

                var metadata = new Dictionary<string, object>
                {
                    ["dimension"] = dimension,
                    ["mode"] = mode.ToString(),
                    ["textLength"] = text.Length,
                    ["tokenCount"] = _tokenizer.Tokenize(text).Count
                };

                return SuccessResult(
                    embeddings: embedding,
                    metadata: metadata,
                    duration: sw.Elapsed);
            }
            catch (OperationCanceledException)
            {
                return FailureResult("Embedding generation cancelled", sw.Elapsed);
            }
            catch (Exception ex)
            {
                return FailureResult($"Embedding generation failed: {ex.Message}", sw.Elapsed);
            }
        }

        /// <summary>
        /// Generates an embedding for the given text.
        /// </summary>
        public Task<float[]> GenerateEmbeddingAsync(
            string text,
            int dimension,
            EmbeddingMode mode,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Task.FromResult(new float[dimension]);
            }

            // Tokenize
            var tokens = _tokenizer.Tokenize(text);
            Interlocked.Add(ref _tokensProcessed, tokens.Count);

            if (tokens.Count == 0)
            {
                return Task.FromResult(new float[dimension]);
            }

            // Generate embedding based on mode
            float[] embedding;
            switch (mode)
            {
                case EmbeddingMode.TfIdf:
                    embedding = _tfidfEmbedder.GenerateEmbedding(tokens, dimension);
                    break;

                case EmbeddingMode.Word2Vec:
                    embedding = _word2vecEmbedder.GenerateEmbedding(tokens, dimension);
                    break;

                case EmbeddingMode.Hybrid:
                    var tfidf = _tfidfEmbedder.GenerateEmbedding(tokens, dimension / 2);
                    var w2v = _word2vecEmbedder.GenerateEmbedding(tokens, dimension / 2);
                    embedding = ConcatenateAndNormalize(tfidf, w2v, dimension);
                    break;

                default:
                    embedding = _tfidfEmbedder.GenerateEmbedding(tokens, dimension);
                    break;
            }

            // Normalize for cosine similarity
            NormalizeVector(embedding);

            return Task.FromResult(embedding);
        }

        /// <summary>
        /// Generates embeddings for a batch of documents.
        /// </summary>
        public async Task<float[][]> GenerateBatchEmbeddingsAsync(
            IEnumerable<string> texts,
            int dimension,
            EmbeddingMode mode,
            int maxConcurrency = 4,
            CancellationToken ct = default)
        {
            var textList = texts.ToList();
            var results = new float[textList.Count][];

            using var semaphore = new SemaphoreSlim(maxConcurrency);
            var tasks = new List<Task>();

            for (int i = 0; i < textList.Count; i++)
            {
                var index = i;
                var text = textList[i];

                await semaphore.WaitAsync(ct);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        results[index] = await GenerateEmbeddingAsync(text, dimension, mode, ct);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, ct));
            }

            await Task.WhenAll(tasks);
            return results;
        }

        /// <summary>
        /// Updates the vocabulary with new documents (for TF-IDF).
        /// Call this during indexing to improve IDF calculations.
        /// </summary>
        public void UpdateVocabulary(string text)
        {
            var tokens = _tokenizer.Tokenize(text);
            _vocabulary.AddDocument(tokens);
        }

        /// <summary>
        /// Gets embedding statistics.
        /// </summary>
        public EmbeddingStatistics GetStatistics()
        {
            return new EmbeddingStatistics
            {
                DocumentsProcessed = Interlocked.Read(ref _documentsProcessed),
                TokensProcessed = Interlocked.Read(ref _tokensProcessed),
                VocabularySize = _vocabulary.Size,
                DocumentCount = _vocabulary.DocumentCount,
                CurrentDimension = _currentDimension,
                CurrentMode = _currentMode
            };
        }

        private static float[] ConcatenateAndNormalize(float[] a, float[] b, int targetDimension)
        {
            var result = new float[targetDimension];
            var halfDim = targetDimension / 2;

            Array.Copy(a, 0, result, 0, Math.Min(a.Length, halfDim));
            Array.Copy(b, 0, result, halfDim, Math.Min(b.Length, halfDim));

            return result;
        }

        private static void NormalizeVector(float[] vector)
        {
            var magnitude = 0.0;
            for (int i = 0; i < vector.Length; i++)
            {
                magnitude += vector[i] * vector[i];
            }

            magnitude = Math.Sqrt(magnitude);

            if (magnitude > 1e-10)
            {
                for (int i = 0; i < vector.Length; i++)
                {
                    vector[i] /= (float)magnitude;
                }
            }
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SupportsTfIdf"] = true;
            metadata["SupportsWord2Vec"] = true;
            metadata["SupportsHybrid"] = true;
            metadata["SupportsBatchProcessing"] = true;
            metadata["SupportedDimensions"] = SupportedDimensions;
            metadata["CurrentDimension"] = _currentDimension;
            metadata["CurrentMode"] = _currentMode.ToString();
            metadata["VocabularySize"] = _vocabulary.Size;
            metadata["DocumentsProcessed"] = Interlocked.Read(ref _documentsProcessed);
            return metadata;
        }
    }

    /// <summary>
    /// Embedding generation mode.
    /// </summary>
    public enum EmbeddingMode
    {
        /// <summary>
        /// TF-IDF based embeddings.
        /// </summary>
        TfIdf,

        /// <summary>
        /// Word2Vec-style embeddings using pre-computed vocabulary.
        /// </summary>
        Word2Vec,

        /// <summary>
        /// Hybrid embedding combining TF-IDF and Word2Vec.
        /// </summary>
        Hybrid
    }

    /// <summary>
    /// Embedding generation statistics.
    /// </summary>
    public sealed class EmbeddingStatistics
    {
        public long DocumentsProcessed { get; init; }
        public long TokensProcessed { get; init; }
        public int VocabularySize { get; init; }
        public int DocumentCount { get; init; }
        public int CurrentDimension { get; init; }
        public EmbeddingMode CurrentMode { get; init; }
    }

    #region Text Tokenizer

    /// <summary>
    /// Text tokenizer with normalization, stemming, and n-gram support.
    /// </summary>
    internal sealed class TextTokenizer
    {
        private static readonly Regex TokenRegex = new(@"\b[a-zA-Z][a-zA-Z0-9]*\b", RegexOptions.Compiled);

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "are", "as", "at", "be", "by", "for", "from",
            "has", "he", "in", "is", "it", "its", "of", "on", "that", "the",
            "to", "was", "were", "will", "with", "this", "but", "they",
            "have", "had", "what", "when", "where", "who", "which", "why", "how",
            "all", "each", "every", "both", "few", "more", "most", "other", "some",
            "such", "no", "nor", "not", "only", "own", "same", "so", "than", "too",
            "very", "just", "can", "should", "now", "i", "me", "my", "you", "your",
            "we", "our", "us", "him", "his", "her", "she", "them", "their", "been",
            "being", "do", "does", "did", "doing", "would", "could", "shall", "may",
            "might", "must", "also", "into", "over", "under", "about", "between",
            "through", "after", "before", "above", "below", "up", "down", "out"
        };

        public List<string> Tokenize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();

            var tokens = new List<string>();
            var matches = TokenRegex.Matches(text.ToLowerInvariant());

            foreach (Match match in matches)
            {
                var token = match.Value;

                // Skip very short tokens and stopwords
                if (token.Length < 2 || StopWords.Contains(token))
                    continue;

                // Apply light stemming
                token = LightStem(token);

                tokens.Add(token);
            }

            return tokens;
        }

        /// <summary>
        /// Generates n-grams from tokens.
        /// </summary>
        public List<string> GenerateNGrams(List<string> tokens, int n)
        {
            var ngrams = new List<string>();

            for (int i = 0; i <= tokens.Count - n; i++)
            {
                var ngram = string.Join("_", tokens.Skip(i).Take(n));
                ngrams.Add(ngram);
            }

            return ngrams;
        }

        /// <summary>
        /// Light stemming - removes common suffixes.
        /// </summary>
        private static string LightStem(string word)
        {
            if (word.Length < 5)
                return word;

            // Remove common suffixes
            if (word.EndsWith("ing") && word.Length > 5)
                return word[..^3];
            if (word.EndsWith("tion") && word.Length > 6)
                return word[..^4];
            if (word.EndsWith("ness") && word.Length > 6)
                return word[..^4];
            if (word.EndsWith("ment") && word.Length > 6)
                return word[..^4];
            if (word.EndsWith("able") && word.Length > 6)
                return word[..^4];
            if (word.EndsWith("ible") && word.Length > 6)
                return word[..^4];
            if (word.EndsWith("ful") && word.Length > 5)
                return word[..^3];
            if (word.EndsWith("less") && word.Length > 6)
                return word[..^4];
            if (word.EndsWith("ly") && word.Length > 4)
                return word[..^2];
            if (word.EndsWith("es") && word.Length > 4)
                return word[..^2];
            if (word.EndsWith("ed") && word.Length > 4)
                return word[..^2];
            if (word.EndsWith("s") && !word.EndsWith("ss") && word.Length > 3)
                return word[..^1];

            return word;
        }
    }

    #endregion

    #region Vocabulary Manager

    /// <summary>
    /// Manages vocabulary and document frequencies for TF-IDF calculations.
    /// Also maintains pre-computed word vectors for Word2Vec-style embeddings.
    /// </summary>
    internal sealed class VocabularyManager
    {
        private readonly ConcurrentDictionary<string, VocabularyEntry> _vocabulary;
        private readonly ConcurrentDictionary<string, float[]> _wordVectors;
        private readonly object _lock = new();
        private int _dimension;
        private int _documentCount;
        private readonly Random _random;

        public int Size => _vocabulary.Count;
        public int DocumentCount => _documentCount;

        public VocabularyManager(int dimension)
        {
            _vocabulary = new ConcurrentDictionary<string, VocabularyEntry>();
            _wordVectors = new ConcurrentDictionary<string, float[]>();
            _dimension = dimension;
            _random = new Random(42); // Deterministic for reproducibility
        }

        public void SetDimension(int dimension)
        {
            lock (_lock)
            {
                _dimension = dimension;
                _wordVectors.Clear(); // Clear cached vectors
            }
        }

        /// <summary>
        /// Adds a document to the vocabulary for IDF calculation.
        /// </summary>
        public void AddDocument(List<string> tokens)
        {
            var uniqueTokens = tokens.Distinct().ToList();

            lock (_lock)
            {
                _documentCount++;

                foreach (var token in uniqueTokens)
                {
                    _vocabulary.AddOrUpdate(
                        token,
                        _ => new VocabularyEntry { Token = token, DocumentFrequency = 1 },
                        (_, entry) =>
                        {
                            entry.DocumentFrequency++;
                            return entry;
                        });
                }
            }
        }

        /// <summary>
        /// Gets the IDF value for a term.
        /// </summary>
        public double GetIdf(string term)
        {
            var docCount = Math.Max(1, _documentCount);

            if (_vocabulary.TryGetValue(term, out var entry))
            {
                return Math.Log((double)docCount / (1 + entry.DocumentFrequency)) + 1;
            }

            // Unknown term - high IDF
            return Math.Log(docCount) + 1;
        }

        /// <summary>
        /// Gets a deterministic word vector for the term.
        /// Uses consistent hashing to generate stable vectors.
        /// </summary>
        public float[] GetWordVector(string term, int dimension)
        {
            var key = $"{term}_{dimension}";

            return _wordVectors.GetOrAdd(key, _ =>
            {
                var vector = new float[dimension];

                // Use hash-based seeding for reproducibility
                var seed = GetDeterministicSeed(term);
                var rng = new Random(seed);

                // Initialize with values from normal distribution approximation
                for (int i = 0; i < dimension; i++)
                {
                    // Box-Muller transform for normal distribution
                    var u1 = rng.NextDouble();
                    var u2 = rng.NextDouble();
                    var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                    vector[i] = (float)(z * 0.1); // Scale down
                }

                return vector;
            });
        }

        /// <summary>
        /// Gets term frequency weight (sublinear TF).
        /// </summary>
        public static double GetTfWeight(int frequency)
        {
            if (frequency <= 0)
                return 0;
            return 1 + Math.Log(frequency);
        }

        /// <summary>
        /// Gets vocabulary index for a term (for dimensionality reduction).
        /// </summary>
        public int GetVocabularyIndex(string term, int dimension)
        {
            // Use consistent hashing for stable indices
            var hash = GetDeterministicSeed(term);
            return Math.Abs(hash % dimension);
        }

        private static int GetDeterministicSeed(string term)
        {
            // FNV-1a hash for deterministic seeding
            unchecked
            {
                const int FnvPrime = 16777619;
                const int FnvOffset = -2128831035; // (int)2166136261

                var hash = FnvOffset;
                foreach (var c in term)
                {
                    hash ^= c;
                    hash *= FnvPrime;
                }

                return hash;
            }
        }
    }

    internal sealed class VocabularyEntry
    {
        public required string Token { get; init; }
        public int DocumentFrequency { get; set; }
    }

    #endregion

    #region TF-IDF Embedder

    /// <summary>
    /// Generates TF-IDF based embeddings using feature hashing.
    /// Uses sublinear term frequency and inverse document frequency.
    /// </summary>
    internal sealed class TfIdfEmbedder
    {
        private readonly VocabularyManager _vocabulary;

        public TfIdfEmbedder(VocabularyManager vocabulary)
        {
            _vocabulary = vocabulary;
        }

        /// <summary>
        /// Generates a TF-IDF embedding for the given tokens.
        /// </summary>
        public float[] GenerateEmbedding(List<string> tokens, int dimension)
        {
            var embedding = new float[dimension];

            if (tokens.Count == 0)
                return embedding;

            // Calculate term frequencies
            var termFrequencies = new Dictionary<string, int>();
            foreach (var token in tokens)
            {
                termFrequencies.TryGetValue(token, out var count);
                termFrequencies[token] = count + 1;
            }

            // Generate TF-IDF weighted embedding
            foreach (var kvp in termFrequencies)
            {
                var term = kvp.Key;
                var tf = VocabularyManager.GetTfWeight(kvp.Value);
                var idf = _vocabulary.GetIdf(term);
                var tfidf = (float)(tf * idf);

                // Feature hashing - distribute TF-IDF value across multiple dimensions
                AddTermToEmbedding(embedding, term, tfidf, dimension);
            }

            // Apply L2 normalization is done by caller
            return embedding;
        }

        private void AddTermToEmbedding(float[] embedding, string term, float weight, int dimension)
        {
            // Use multiple hash functions to reduce collision impact
            var hash1 = GetHash(term, 1);
            var hash2 = GetHash(term, 2);
            var hash3 = GetHash(term, 3);

            // Primary index
            var idx1 = Math.Abs(hash1 % dimension);
            var sign1 = (hash2 & 1) == 0 ? 1 : -1;
            embedding[idx1] += weight * sign1;

            // Secondary index (for larger dimensions)
            if (dimension >= 256)
            {
                var idx2 = Math.Abs(hash2 % dimension);
                var sign2 = (hash3 & 1) == 0 ? 1 : -1;
                embedding[idx2] += weight * sign2 * 0.5f;
            }

            // Tertiary index (for even larger dimensions)
            if (dimension >= 512)
            {
                var idx3 = Math.Abs(hash3 % dimension);
                var sign3 = ((hash1 ^ hash2) & 1) == 0 ? 1 : -1;
                embedding[idx3] += weight * sign3 * 0.3f;
            }
        }

        private static int GetHash(string term, int seed)
        {
            unchecked
            {
                var hash = seed * 31337;
                foreach (var c in term)
                {
                    hash = hash * 31 + c;
                }
                return hash;
            }
        }
    }

    #endregion

    #region Word2Vec-Style Embedder

    /// <summary>
    /// Generates Word2Vec-style embeddings using pre-computed word vectors.
    /// Averages word vectors with position weighting and n-gram support.
    /// </summary>
    internal sealed class Word2VecStyleEmbedder
    {
        private readonly VocabularyManager _vocabulary;
        private readonly int _dimension;
        private readonly TextTokenizer _tokenizer;

        public Word2VecStyleEmbedder(VocabularyManager vocabulary, int dimension)
        {
            _vocabulary = vocabulary;
            _dimension = dimension;
            _tokenizer = new TextTokenizer();
        }

        /// <summary>
        /// Generates a Word2Vec-style embedding by averaging word vectors.
        /// </summary>
        public float[] GenerateEmbedding(List<string> tokens, int dimension)
        {
            var embedding = new float[dimension];

            if (tokens.Count == 0)
                return embedding;

            // Get word vectors and average with position weighting
            var totalWeight = 0.0;

            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                var wordVector = _vocabulary.GetWordVector(token, dimension);

                // Position weighting - early tokens slightly more important
                var positionWeight = 1.0 + 0.1 * (1.0 - (double)i / tokens.Count);

                // IDF weighting
                var idfWeight = _vocabulary.GetIdf(token);

                var weight = positionWeight * idfWeight;
                totalWeight += weight;

                for (int j = 0; j < dimension; j++)
                {
                    embedding[j] += (float)(wordVector[j] * weight);
                }
            }

            // Normalize by total weight
            if (totalWeight > 0)
            {
                for (int i = 0; i < dimension; i++)
                {
                    embedding[i] /= (float)totalWeight;
                }
            }

            // Add bigram features if we have enough dimensions
            if (dimension >= 256 && tokens.Count >= 2)
            {
                AddBigramFeatures(embedding, tokens, dimension);
            }

            return embedding;
        }

        private void AddBigramFeatures(float[] embedding, List<string> tokens, int dimension)
        {
            var bigrams = _tokenizer.GenerateNGrams(tokens, 2);
            var bigramWeight = 0.2f / Math.Max(1, bigrams.Count);

            foreach (var bigram in bigrams)
            {
                var bigramVector = _vocabulary.GetWordVector(bigram, dimension);

                for (int i = 0; i < dimension; i++)
                {
                    embedding[i] += bigramVector[i] * bigramWeight;
                }
            }
        }
    }

    #endregion

    #region Similarity Calculations

    /// <summary>
    /// Utility class for vector similarity calculations.
    /// </summary>
    public static class VectorSimilarity
    {
        /// <summary>
        /// Calculates cosine similarity between two vectors.
        /// </summary>
        public static float CosineSimilarity(float[] a, float[] b)
        {
            if (a.Length != b.Length)
                throw new ArgumentException("Vectors must have same length");

            var dotProduct = 0.0;
            var magnitudeA = 0.0;
            var magnitudeB = 0.0;

            for (int i = 0; i < a.Length; i++)
            {
                dotProduct += a[i] * b[i];
                magnitudeA += a[i] * a[i];
                magnitudeB += b[i] * b[i];
            }

            var denominator = Math.Sqrt(magnitudeA) * Math.Sqrt(magnitudeB);

            if (denominator < 1e-10)
                return 0;

            return (float)(dotProduct / denominator);
        }

        /// <summary>
        /// Calculates Euclidean distance between two vectors.
        /// </summary>
        public static float EuclideanDistance(float[] a, float[] b)
        {
            if (a.Length != b.Length)
                throw new ArgumentException("Vectors must have same length");

            var sum = 0.0;
            for (int i = 0; i < a.Length; i++)
            {
                var diff = a[i] - b[i];
                sum += diff * diff;
            }

            return (float)Math.Sqrt(sum);
        }

        /// <summary>
        /// Calculates dot product between two vectors.
        /// </summary>
        public static float DotProduct(float[] a, float[] b)
        {
            if (a.Length != b.Length)
                throw new ArgumentException("Vectors must have same length");

            var result = 0.0f;
            for (int i = 0; i < a.Length; i++)
            {
                result += a[i] * b[i];
            }

            return result;
        }

        /// <summary>
        /// Finds the top K most similar vectors.
        /// </summary>
        public static List<(int Index, float Similarity)> FindTopKSimilar(
            float[] query,
            float[][] candidates,
            int k)
        {
            var similarities = new List<(int Index, float Similarity)>();

            for (int i = 0; i < candidates.Length; i++)
            {
                var sim = CosineSimilarity(query, candidates[i]);
                similarities.Add((i, sim));
            }

            return similarities
                .OrderByDescending(x => x.Similarity)
                .Take(k)
                .ToList();
        }

        /// <summary>
        /// Reduces vector dimension using random projection.
        /// </summary>
        public static float[] ReduceDimension(float[] vector, int targetDimension, int seed = 42)
        {
            if (targetDimension >= vector.Length)
                return vector;

            var result = new float[targetDimension];
            var rng = new Random(seed);

            // Sparse random projection matrix
            var scale = (float)Math.Sqrt(3.0 / targetDimension);

            for (int i = 0; i < targetDimension; i++)
            {
                for (int j = 0; j < vector.Length; j++)
                {
                    // Sparse projection: +1, 0, -1 with probabilities 1/6, 2/3, 1/6
                    var r = rng.NextDouble();
                    if (r < 1.0 / 6.0)
                        result[i] += vector[j] * scale;
                    else if (r > 5.0 / 6.0)
                        result[i] -= vector[j] * scale;
                }
            }

            return result;
        }
    }

    #endregion
}
