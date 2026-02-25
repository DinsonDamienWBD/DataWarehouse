using DataWarehouse.SDK.AI;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Features;

/// <summary>
/// Full-text search feature strategy (90.R1.2).
/// Provides traditional keyword-based full-text search with stemming, fuzzy matching, and relevance ranking.
/// </summary>
public sealed class FullTextSearchStrategy : FeatureStrategyBase
{
    private readonly Dictionary<string, List<(string DocId, string Content, Dictionary<string, object> Metadata)>> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _invertedIndex = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override string StrategyId => "feature-fulltext-search";

    /// <inheritdoc/>
    public override string StrategyName => "Full-Text Search";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Full-Text Search",
        Description = "Traditional keyword-based full-text search with stemming, fuzzy matching, and BM25 ranking",
        Capabilities = IntelligenceCapabilities.SemanticSearch,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "EnableStemming", Description = "Enable word stemming", Required = false, DefaultValue = "true" },
            new ConfigurationRequirement { Key = "EnableFuzzyMatch", Description = "Enable fuzzy matching", Required = false, DefaultValue = "true" },
            new ConfigurationRequirement { Key = "FuzzyThreshold", Description = "Fuzzy match threshold (0-1)", Required = false, DefaultValue = "0.8" },
            new ConfigurationRequirement { Key = "TopK", Description = "Number of results to return", Required = false, DefaultValue = "10" }
        },
        CostTier = 1,
        LatencyTier = 1,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "fulltext-search", "keyword", "bm25", "stemming", "fuzzy" }
    };

    /// <summary>
    /// Indexes a document for full-text search.
    /// </summary>
    /// <param name="documentId">Unique document identifier.</param>
    /// <param name="content">Document content to index.</param>
    /// <param name="metadata">Optional metadata for filtering.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task IndexDocumentAsync(
        string documentId,
        string content,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            var tokens = Tokenize(content);
            var stemmedTokens = bool.Parse(GetConfig("EnableStemming") ?? "true")
                ? tokens.Select(Stem).ToList()
                : tokens;

            // Store document
            if (!_index.ContainsKey("documents"))
                _index["documents"] = new();

            _index["documents"].RemoveAll(d => d.DocId == documentId);
            _index["documents"].Add((documentId, content, metadata ?? new Dictionary<string, object>()));

            // Build inverted index
            foreach (var token in stemmedTokens.Distinct())
            {
                if (!_invertedIndex.TryGetValue(token, out var docSet))
                {
                    docSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _invertedIndex[token] = docSet;
                }
                docSet.Add(documentId);
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Indexes multiple documents in batch.
    /// </summary>
    public async Task IndexDocumentsBatchAsync(
        IEnumerable<(string Id, string Content, Dictionary<string, object>? Metadata)> documents,
        CancellationToken ct = default)
    {
        foreach (var doc in documents)
        {
            await IndexDocumentAsync(doc.Id, doc.Content, doc.Metadata, ct);
        }
    }

    /// <summary>
    /// Performs a full-text search.
    /// </summary>
    /// <param name="query">Search query.</param>
    /// <param name="topK">Number of results to return.</param>
    /// <param name="filter">Optional metadata filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ranked search results.</returns>
    public Task<IEnumerable<FullTextSearchResult>> SearchAsync(
        string query,
        int? topK = null,
        Dictionary<string, object>? filter = null,
        CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            var k = topK ?? int.Parse(GetConfig("TopK") ?? "10");
            var enableFuzzy = bool.Parse(GetConfig("EnableFuzzyMatch") ?? "true");
            var fuzzyThreshold = float.Parse(GetConfig("FuzzyThreshold") ?? "0.8");

            var queryTokens = Tokenize(query);
            var stemmedQuery = bool.Parse(GetConfig("EnableStemming") ?? "true")
                ? queryTokens.Select(Stem).ToList()
                : queryTokens;

            // Find candidate documents
            var candidateDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var token in stemmedQuery)
            {
                // Exact match
                if (_invertedIndex.TryGetValue(token, out var exactDocs))
                    candidateDocs.UnionWith(exactDocs);

                // Fuzzy match
                if (enableFuzzy)
                {
                    foreach (var indexToken in _invertedIndex.Keys)
                    {
                        if (CalculateSimilarity(token, indexToken) >= fuzzyThreshold)
                        {
                            candidateDocs.UnionWith(_invertedIndex[indexToken]);
                        }
                    }
                }
            }

            // Score documents using BM25
            var documents = _index.GetValueOrDefault("documents") ?? new();
            var results = new List<FullTextSearchResult>();
            var avgDocLength = documents.Count > 0 ? documents.Average(d => d.Content.Length) : 1.0;

            foreach (var docId in candidateDocs)
            {
                var doc = documents.FirstOrDefault(d => d.DocId == docId);
                if (doc == default) continue;

                // Apply metadata filter
                if (filter != null && !MatchesFilter(doc.Metadata, filter)) continue;

                var score = CalculateBM25Score(stemmedQuery, doc.Content, documents.Count, avgDocLength);
                var snippet = GenerateSnippet(doc.Content, queryTokens);

                results.Add(new FullTextSearchResult
                {
                    DocumentId = docId,
                    Score = score,
                    Snippet = snippet,
                    Metadata = doc.Metadata,
                    MatchedTerms = stemmedQuery.Where(t => doc.Content.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList()
                });
            }

            RecordSearch();

            return Task.FromResult<IEnumerable<FullTextSearchResult>>(
                results.OrderByDescending(r => r.Score).Take(k).Select((r, i) => r with { Rank = i + 1 }));
        });
    }

    private static List<string> Tokenize(string text)
    {
        return Regex.Split(text.ToLowerInvariant(), @"[^\w]+")
            .Where(t => t.Length > 1)
            .ToList();
    }

    private static string Stem(string word)
    {
        // Simple Porter-like stemming
        if (word.EndsWith("ing") && word.Length > 5)
            return word[..^3];
        if (word.EndsWith("ed") && word.Length > 4)
            return word[..^2];
        if (word.EndsWith("s") && !word.EndsWith("ss") && word.Length > 3)
            return word[..^1];
        if (word.EndsWith("ly") && word.Length > 4)
            return word[..^2];
        if (word.EndsWith("tion") && word.Length > 6)
            return word[..^4] + "t";
        return word;
    }

    private static float CalculateSimilarity(string a, string b)
    {
        // Levenshtein distance based similarity
        if (a == b) return 1.0f;

        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        int maxLen = Math.Max(a.Length, b.Length);
        return 1.0f - (float)d[a.Length, b.Length] / maxLen;
    }

    private float CalculateBM25Score(List<string> queryTerms, string document, int totalDocs, double avgDocLength)
    {
        const float k1 = 1.5f;
        const float b = 0.75f;

        var docTokens = Tokenize(document);
        var docLength = docTokens.Count;
        var termFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in docTokens)
        {
            termFreq[token] = termFreq.GetValueOrDefault(token) + 1;
        }

        float score = 0;
        foreach (var term in queryTerms)
        {
            var tf = termFreq.GetValueOrDefault(term);
            if (tf == 0) continue;

            var docsWithTerm = _invertedIndex.GetValueOrDefault(term)?.Count ?? 0;
            var idf = MathF.Log((totalDocs - docsWithTerm + 0.5f) / (docsWithTerm + 0.5f) + 1);

            var numerator = tf * (k1 + 1);
            var denominator = tf + k1 * (1 - b + b * (docLength / avgDocLength));

            score += idf * (float)(numerator / denominator);
        }

        return score;
    }

    private static string GenerateSnippet(string content, List<string> queryTerms, int snippetLength = 200)
    {
        var lowerContent = content.ToLowerInvariant();
        var bestPos = 0;
        var bestScore = 0;

        for (int i = 0; i < content.Length - snippetLength; i += 50)
        {
            var window = lowerContent.Substring(i, Math.Min(snippetLength, content.Length - i));
            var score = queryTerms.Count(t => window.Contains(t));
            if (score > bestScore)
            {
                bestScore = score;
                bestPos = i;
            }
        }

        var start = Math.Max(0, bestPos);
        var length = Math.Min(snippetLength, content.Length - start);
        var snippet = content.Substring(start, length);

        return (start > 0 ? "..." : "") + snippet.Trim() + (start + length < content.Length ? "..." : "");
    }

    private static bool MatchesFilter(Dictionary<string, object> metadata, Dictionary<string, object> filter)
    {
        foreach (var (key, value) in filter)
        {
            if (!metadata.TryGetValue(key, out var docValue))
                return false;
            if (!Equals(docValue, value))
                return false;
        }
        return true;
    }
}

/// <summary>
/// Result from full-text search.
/// </summary>
public sealed record FullTextSearchResult
{
    /// <summary>Document identifier.</summary>
    public string DocumentId { get; init; } = "";

    /// <summary>BM25 relevance score.</summary>
    public float Score { get; init; }

    /// <summary>Rank in results (1-based).</summary>
    public int Rank { get; init; }

    /// <summary>Text snippet with highlighted terms.</summary>
    public string Snippet { get; init; } = "";

    /// <summary>Document metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();

    /// <summary>Query terms that matched.</summary>
    public List<string> MatchedTerms { get; init; } = new();
}

/// <summary>
/// Hybrid search feature strategy (90.R1.4).
/// Combines keyword-based full-text search with semantic vector search for optimal results.
/// </summary>
public sealed class HybridSearchStrategy : FeatureStrategyBase
{
    private readonly FullTextSearchStrategy _fullTextSearch = new();
    private readonly SemanticSearchStrategy _semanticSearch = new();

    /// <inheritdoc/>
    public override string StrategyId => "feature-hybrid-search";

    /// <inheritdoc/>
    public override string StrategyName => "Hybrid Search";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Hybrid Search",
        Description = "Combines keyword full-text search with semantic vector search using Reciprocal Rank Fusion",
        Capabilities = IntelligenceCapabilities.SemanticSearch | IntelligenceCapabilities.Embeddings,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "SemanticWeight", Description = "Weight for semantic results (0-1)", Required = false, DefaultValue = "0.5" },
            new ConfigurationRequirement { Key = "KeywordWeight", Description = "Weight for keyword results (0-1)", Required = false, DefaultValue = "0.5" },
            new ConfigurationRequirement { Key = "RrfK", Description = "RRF constant k (typically 60)", Required = false, DefaultValue = "60" },
            new ConfigurationRequirement { Key = "TopK", Description = "Number of results to return", Required = false, DefaultValue = "10" }
        },
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        Tags = new[] { "hybrid-search", "semantic", "fulltext", "rrf", "fusion" }
    };

    /// <summary>
    /// Configures the underlying search strategies with AI provider and vector store.
    /// </summary>
    public void ConfigureStrategies(IAIProvider? aiProvider, IVectorStore? vectorStore)
    {
        if (aiProvider != null)
        {
            SetAIProvider(aiProvider);
            _semanticSearch.SetAIProvider(aiProvider);
        }
        if (vectorStore != null)
        {
            SetVectorStore(vectorStore);
            _semanticSearch.SetVectorStore(vectorStore);
        }
    }

    /// <summary>
    /// Indexes a document for both full-text and semantic search.
    /// </summary>
    public async Task IndexDocumentAsync(
        string documentId,
        string content,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            // Index for full-text search
            await _fullTextSearch.IndexDocumentAsync(documentId, content, metadata, ct);

            // Index for semantic search (if AI provider available)
            if (AIProvider != null && VectorStore != null)
            {
                await _semanticSearch.IndexDocumentAsync(documentId, content, metadata, ct);
            }
        });
    }

    /// <summary>
    /// Performs a hybrid search combining full-text and semantic results.
    /// </summary>
    /// <param name="query">Search query.</param>
    /// <param name="topK">Number of results to return.</param>
    /// <param name="filter">Optional metadata filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Fused and ranked search results.</returns>
    public async Task<IEnumerable<HybridSearchResult>> SearchAsync(
        string query,
        int? topK = null,
        Dictionary<string, object>? filter = null,
        CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var k = topK ?? int.Parse(GetConfig("TopK") ?? "10");
            var semanticWeight = float.Parse(GetConfig("SemanticWeight") ?? "0.5");
            var keywordWeight = float.Parse(GetConfig("KeywordWeight") ?? "0.5");
            var rrfK = int.Parse(GetConfig("RrfK") ?? "60");

            // Get results from both search methods (fetch more to allow for RRF)
            var fetchK = k * 3;

            var fullTextResults = (await _fullTextSearch.SearchAsync(query, fetchK, filter, ct)).ToList();

            List<SemanticSearchResult> semanticResults = new();
            if (AIProvider != null && VectorStore != null)
            {
                semanticResults = (await _semanticSearch.SearchAsync(query, fetchK, null, filter, ct)).ToList();
            }

            // Apply Reciprocal Rank Fusion (RRF)
            var rrfScores = new Dictionary<string, (float Score, string Snippet, Dictionary<string, object> Metadata, SearchSource Sources)>();

            // Process full-text results
            foreach (var result in fullTextResults)
            {
                var rrfScore = keywordWeight * (1.0f / (rrfK + result.Rank));
                rrfScores[result.DocumentId] = (rrfScore, result.Snippet, result.Metadata, SearchSource.FullText);
            }

            // Process semantic results
            foreach (var result in semanticResults)
            {
                var rrfScore = semanticWeight * (1.0f / (rrfK + result.Rank));
                if (rrfScores.TryGetValue(result.DocumentId, out var existing))
                {
                    rrfScores[result.DocumentId] = (
                        existing.Score + rrfScore,
                        existing.Snippet,
                        existing.Metadata,
                        SearchSource.Both
                    );
                }
                else
                {
                    rrfScores[result.DocumentId] = (
                        rrfScore,
                        result.Snippet ?? "",
                        result.Metadata,
                        SearchSource.Semantic
                    );
                }
            }

            RecordSearch();

            // Rank and return results
            return rrfScores
                .OrderByDescending(x => x.Value.Score)
                .Take(k)
                .Select((x, i) => new HybridSearchResult
                {
                    DocumentId = x.Key,
                    CombinedScore = x.Value.Score,
                    Rank = i + 1,
                    Snippet = x.Value.Snippet,
                    Metadata = x.Value.Metadata,
                    Sources = x.Value.Sources
                });
        });
    }
}

/// <summary>
/// Source of search results.
/// </summary>
[Flags]
public enum SearchSource
{
    /// <summary>No source.</summary>
    None = 0,
    /// <summary>Full-text keyword search.</summary>
    FullText = 1,
    /// <summary>Semantic vector search.</summary>
    Semantic = 2,
    /// <summary>Both sources.</summary>
    Both = FullText | Semantic
}

/// <summary>
/// Result from hybrid search.
/// </summary>
public sealed class HybridSearchResult
{
    /// <summary>Document identifier.</summary>
    public string DocumentId { get; init; } = "";

    /// <summary>Combined RRF score.</summary>
    public float CombinedScore { get; init; }

    /// <summary>Rank in results (1-based).</summary>
    public int Rank { get; init; }

    /// <summary>Text snippet.</summary>
    public string Snippet { get; init; } = "";

    /// <summary>Document metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();

    /// <summary>Which search methods found this result.</summary>
    public SearchSource Sources { get; init; }
}
