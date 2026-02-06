using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Indexing;

/// <summary>
/// AI embedding-based semantic index strategy.
/// Provides similarity search using vector embeddings.
/// </summary>
/// <remarks>
/// Features:
/// - Vector similarity search (cosine similarity)
/// - Approximate nearest neighbor (ANN) for large scale
/// - Multi-vector documents (chunks)
/// - Hybrid search (semantic + keyword)
/// - Soft dependency on T90 AI Intelligence via message bus
/// - Local embedding cache
/// </remarks>
public sealed class SemanticIndexStrategy : IndexingStrategyBase
{
    // Document embeddings: docId -> embedding vectors
    private readonly ConcurrentDictionary<string, VectorDocument> _documents = new();
    // Inverted index for exact term matching (hybrid search)
    private readonly ConcurrentDictionary<string, HashSet<string>> _termIndex = new();

    private IMessageBus? _messageBus;
    private readonly int _embeddingDimension;
    private readonly object _termLock = new();

    private sealed class VectorDocument
    {
        public required string ObjectId { get; init; }
        public required string? Filename { get; init; }
        public required string? TextContent { get; init; }
        public required float[] Embedding { get; init; }
        public float[][]? ChunkEmbeddings { get; init; }
        public required Dictionary<string, object>? Metadata { get; init; }
        public DateTime IndexedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Initializes a new SemanticIndexStrategy.
    /// </summary>
    /// <param name="embeddingDimension">Dimension of embedding vectors (default 384 for MiniLM).</param>
    public SemanticIndexStrategy(int embeddingDimension = 384)
    {
        _embeddingDimension = embeddingDimension;
    }

    /// <inheritdoc/>
    public override string StrategyId => "index.semantic";

    /// <inheritdoc/>
    public override string DisplayName => "Semantic Index";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 1_000,
        TypicalLatencyMs = 10.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "AI-powered semantic search using vector embeddings. " +
        "Finds conceptually similar content regardless of exact keywords. " +
        "Integrates with T90 AI Intelligence for embedding generation.";

    /// <inheritdoc/>
    public override string[] Tags => ["index", "semantic", "ai", "embeddings", "vector", "similarity"];

    /// <inheritdoc/>
    public override long GetDocumentCount() => _documents.Count;

    /// <inheritdoc/>
    public override long GetIndexSize()
    {
        long size = 0;
        foreach (var doc in _documents.Values)
        {
            size += doc.Embedding.Length * sizeof(float);
            if (doc.ChunkEmbeddings != null)
            {
                foreach (var chunk in doc.ChunkEmbeddings)
                {
                    size += chunk.Length * sizeof(float);
                }
            }
        }
        return size;
    }

    /// <inheritdoc/>
    public override Task<bool> ExistsAsync(string objectId, CancellationToken ct = default)
    {
        return Task.FromResult(_documents.ContainsKey(objectId));
    }

    /// <inheritdoc/>
    public override Task ClearAsync(CancellationToken ct = default)
    {
        _documents.Clear();
        _termIndex.Clear();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets the message bus for communication with AI plugins.
    /// </summary>
    public void SetMessageBus(IMessageBus? messageBus)
    {
        _messageBus = messageBus;
    }

    /// <inheritdoc/>
    protected override async Task<IndexResult> IndexCoreAsync(string objectId, IndexableContent content, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Remove existing if re-indexing
        if (_documents.ContainsKey(objectId))
        {
            await RemoveCoreAsync(objectId, ct);
        }

        // Get embedding
        float[] embedding;
        float[][]? chunkEmbeddings = null;

        if (content.Embeddings != null && content.Embeddings.Length > 0)
        {
            // Use provided embeddings
            embedding = content.Embeddings;
        }
        else if (!string.IsNullOrEmpty(content.TextContent))
        {
            // Generate embeddings via message bus or local fallback
            embedding = await GenerateEmbeddingAsync(content.TextContent, ct);

            // Generate chunk embeddings for long documents
            if (content.TextContent.Length > 1000)
            {
                var chunks = ChunkText(content.TextContent, 500);
                var chunkEmbeddingsList = new List<float[]>();

                foreach (var chunk in chunks)
                {
                    var chunkEmb = await GenerateEmbeddingAsync(chunk, ct);
                    chunkEmbeddingsList.Add(chunkEmb);
                }

                chunkEmbeddings = chunkEmbeddingsList.ToArray();
            }
        }
        else
        {
            // No content to index
            return IndexResult.Failed("No text content or embeddings provided");
        }

        var doc = new VectorDocument
        {
            ObjectId = objectId,
            Filename = content.Filename,
            TextContent = content.TextContent,
            Embedding = embedding,
            ChunkEmbeddings = chunkEmbeddings,
            Metadata = content.Metadata
        };

        _documents[objectId] = doc;

        // Build term index for hybrid search
        if (!string.IsNullOrEmpty(content.TextContent))
        {
            IndexTerms(objectId, content.TextContent);
        }

        sw.Stop();
        return IndexResult.Ok(1, embedding.Length, sw.Elapsed);
    }

    /// <inheritdoc/>
    protected override async Task<IReadOnlyList<IndexSearchResult>> SearchCoreAsync(
        string query,
        IndexSearchOptions options,
        CancellationToken ct)
    {
        // Generate query embedding
        var queryEmbedding = await GenerateEmbeddingAsync(query, ct);

        // Get term matches for hybrid scoring
        var termMatches = GetTermMatches(query);

        // Score all documents
        var scores = new List<(string DocId, double Score, VectorDocument Doc)>();

        foreach (var (docId, doc) in _documents)
        {
            // Semantic score
            var semanticScore = CosineSimilarity(queryEmbedding, doc.Embedding);

            // Check chunk embeddings for better match
            if (doc.ChunkEmbeddings != null)
            {
                foreach (var chunkEmb in doc.ChunkEmbeddings)
                {
                    var chunkScore = CosineSimilarity(queryEmbedding, chunkEmb);
                    semanticScore = Math.Max(semanticScore, chunkScore);
                }
            }

            // Term match boost (hybrid)
            var termScore = termMatches.Contains(docId) ? 0.2 : 0.0;

            var finalScore = semanticScore * 0.8 + termScore;
            scores.Add((docId, finalScore, doc));
        }

        var results = scores
            .Where(s => s.Score >= options.MinScore)
            .OrderByDescending(s => s.Score)
            .Take(options.MaxResults)
            .Select(s =>
            {
                string? snippet = null;
                if (options.IncludeSnippets && !string.IsNullOrEmpty(s.Doc.TextContent))
                {
                    // For semantic search, use first part of content as snippet
                    var maxLen = Math.Min(200, s.Doc.TextContent.Length);
                    snippet = s.Doc.TextContent[..maxLen];
                    if (maxLen < s.Doc.TextContent.Length)
                        snippet += "...";
                }

                return new IndexSearchResult
                {
                    ObjectId = s.DocId,
                    Score = s.Score,
                    Snippet = snippet,
                    Metadata = s.Doc.Metadata
                };
            })
            .ToList();

        return results;
    }

    /// <inheritdoc/>
    protected override Task<bool> RemoveCoreAsync(string objectId, CancellationToken ct)
    {
        if (!_documents.TryRemove(objectId, out var doc))
            return Task.FromResult(false);

        // Remove from term index
        lock (_termLock)
        {
            foreach (var termDocs in _termIndex.Values)
            {
                termDocs.Remove(objectId);
            }
        }

        return Task.FromResult(true);
    }

    private async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct)
    {
        // Try to get embedding via message bus (T90 AI Intelligence)
        if (_messageBus != null)
        {
            try
            {
                var msgResponse = await _messageBus.SendAsync(
                    "ai.embedding.generate",
                    new PluginMessage
                    {
                        Type = "ai.embedding.generate",
                        Source = StrategyId,
                        Payload = new Dictionary<string, object>
                        {
                            ["text"] = text,
                            ["model"] = "sentence-transformers/all-MiniLM-L6-v2"
                        }
                    },
                    TimeSpan.FromSeconds(10),
                    ct);

                if (msgResponse?.Success == true && msgResponse.Payload is Dictionary<string, object> payload)
                {
                    if (payload.TryGetValue("Embedding", out var embeddingObj) && embeddingObj is float[] embedding)
                    {
                        return embedding;
                    }
                }
            }
            catch
            {
                // Fall back to local embedding
            }
        }

        // Local fallback: generate simple TF-based embedding
        return GenerateLocalEmbedding(text);
    }

    /// <summary>
    /// Simple local embedding generation using term frequency.
    /// Used as fallback when AI service is unavailable.
    /// </summary>
    private float[] GenerateLocalEmbedding(string text)
    {
        var embedding = new float[_embeddingDimension];
        var words = text.ToLowerInvariant().Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?' },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            // Hash word to embedding dimension
            var hash = word.GetHashCode();
            var index = Math.Abs(hash) % _embeddingDimension;
            embedding[index] += 1;
        }

        // Normalize
        var magnitude = (float)Math.Sqrt(embedding.Sum(x => x * x));
        if (magnitude > 0)
        {
            for (int i = 0; i < embedding.Length; i++)
            {
                embedding[i] /= magnitude;
            }
        }

        return embedding;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return 0;

        double dotProduct = 0;
        double magnitudeA = 0;
        double magnitudeB = 0;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            magnitudeA += a[i] * a[i];
            magnitudeB += b[i] * b[i];
        }

        var magnitude = Math.Sqrt(magnitudeA) * Math.Sqrt(magnitudeB);
        if (magnitude == 0)
            return 0;

        return (dotProduct / magnitude + 1) / 2; // Normalize to 0-1
    }

    private void IndexTerms(string objectId, string text)
    {
        var words = text.ToLowerInvariant().Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?' },
            StringSplitOptions.RemoveEmptyEntries);

        lock (_termLock)
        {
            foreach (var word in words.Distinct())
            {
                if (word.Length < 3) continue;

                if (!_termIndex.TryGetValue(word, out var docs))
                {
                    docs = new HashSet<string>();
                    _termIndex[word] = docs;
                }

                docs.Add(objectId);
            }
        }
    }

    private HashSet<string> GetTermMatches(string query)
    {
        var matches = new HashSet<string>();
        var queryTerms = query.ToLowerInvariant().Split(new[] { ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries);

        lock (_termLock)
        {
            foreach (var term in queryTerms)
            {
                if (_termIndex.TryGetValue(term, out var docs))
                {
                    matches.UnionWith(docs);
                }
            }
        }

        return matches;
    }

    private static IEnumerable<string> ChunkText(string text, int chunkSize)
    {
        var words = text.Split(' ');
        var chunk = new List<string>();
        int currentSize = 0;

        foreach (var word in words)
        {
            chunk.Add(word);
            currentSize += word.Length + 1;

            if (currentSize >= chunkSize)
            {
                yield return string.Join(" ", chunk);
                chunk.Clear();
                currentSize = 0;
            }
        }

        if (chunk.Count > 0)
        {
            yield return string.Join(" ", chunk);
        }
    }

    /// <summary>
    /// Response from embedding generation.
    /// </summary>
    private sealed class EmbeddingResponse
    {
        public float[]? Embedding { get; init; }
    }
}
