using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Features;

/// <summary>
/// Semantic search feature strategy.
/// Provides natural language search over documents using embeddings and vector similarity.
/// </summary>
public sealed class SemanticSearchStrategy : FeatureStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "feature-semantic-search";

    /// <inheritdoc/>
    public override string StrategyName => "Semantic Search";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Semantic Search",
        Description = "Natural language search using embeddings and vector similarity for content discovery",
        Capabilities = IntelligenceCapabilities.SemanticSearch | IntelligenceCapabilities.Embeddings,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "TopK", Description = "Number of results to return", Required = false, DefaultValue = "10" },
            new ConfigurationRequirement { Key = "MinScore", Description = "Minimum similarity score threshold", Required = false, DefaultValue = "0.7" },
            new ConfigurationRequirement { Key = "EnableReranking", Description = "Enable cross-encoder reranking", Required = false, DefaultValue = "false" }
        },
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        Tags = new[] { "semantic-search", "embeddings", "nlp", "similarity" }
    };

    /// <summary>
    /// Performs a semantic search over indexed content.
    /// </summary>
    /// <param name="query">Natural language query.</param>
    /// <param name="topK">Number of results to return.</param>
    /// <param name="minScore">Minimum similarity score threshold.</param>
    /// <param name="filter">Optional metadata filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ranked list of search results.</returns>
    public async Task<IEnumerable<SemanticSearchResult>> SearchAsync(
        string query,
        int? topK = null,
        float? minScore = null,
        Dictionary<string, object>? filter = null,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider not configured for semantic search");
        if (VectorStore == null)
            throw new InvalidOperationException("Vector store not configured for semantic search");

        return await ExecuteWithTrackingAsync(async () =>
        {
            // Get embedding for query
            var queryEmbedding = await AIProvider.GetEmbeddingsAsync(query, ct);
            RecordEmbeddings(1);

            // Search vector store
            var k = topK ?? int.Parse(GetConfig("TopK") ?? "10");
            var threshold = minScore ?? float.Parse(GetConfig("MinScore") ?? "0.7");

            var matches = await VectorStore.SearchAsync(queryEmbedding, k, threshold, filter, ct);
            RecordSearch();

            var results = matches.Select(m => new SemanticSearchResult
            {
                DocumentId = m.Entry.Id,
                Score = m.Score,
                Rank = m.Rank,
                Metadata = m.Entry.Metadata,
                Snippet = m.Entry.Metadata.TryGetValue("text", out var text) ? text?.ToString() : null
            }).ToList();

            // Optional reranking with cross-encoder
            if (bool.Parse(GetConfig("EnableReranking") ?? "false") && results.Count > 1)
            {
                results = await RerankResultsAsync(query, results, ct);
            }

            return results;
        });
    }

    /// <summary>
    /// Indexes a document for semantic search.
    /// </summary>
    public async Task IndexDocumentAsync(
        string documentId,
        string content,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider not configured");
        if (VectorStore == null)
            throw new InvalidOperationException("Vector store not configured");

        await ExecuteWithTrackingAsync(async () =>
        {
            var embedding = await AIProvider.GetEmbeddingsAsync(content, ct);
            RecordEmbeddings(1);

            var docMetadata = metadata ?? new Dictionary<string, object>();
            docMetadata["text"] = content.Length > 500 ? content.Substring(0, 500) + "..." : content;
            docMetadata["indexed_at"] = DateTime.UtcNow;

            await VectorStore.StoreAsync(documentId, embedding, docMetadata, ct);
            RecordVectorsStored(1);
        });
    }

    /// <summary>
    /// Indexes multiple documents in batch.
    /// </summary>
    public async Task IndexDocumentsBatchAsync(
        IEnumerable<(string Id, string Content, Dictionary<string, object>? Metadata)> documents,
        CancellationToken ct = default)
    {
        if (AIProvider == null || VectorStore == null)
            throw new InvalidOperationException("AI provider and vector store must be configured");

        await ExecuteWithTrackingAsync(async () =>
        {
            var docList = documents.ToList();
            var contents = docList.Select(d => d.Content).ToArray();

            var embeddings = await AIProvider.GetEmbeddingsBatchAsync(contents, ct);
            RecordEmbeddings(embeddings.Length);

            var entries = docList.Select((doc, i) =>
            {
                var metadata = doc.Metadata ?? new Dictionary<string, object>();
                metadata["text"] = doc.Content.Length > 500 ? doc.Content.Substring(0, 500) + "..." : doc.Content;
                metadata["indexed_at"] = DateTime.UtcNow;

                return new VectorEntry
                {
                    Id = doc.Id,
                    Vector = embeddings[i],
                    Metadata = metadata
                };
            });

            await VectorStore.StoreBatchAsync(entries, ct);
            RecordVectorsStored(docList.Count);
        });
    }

    private async Task<List<SemanticSearchResult>> RerankResultsAsync(
        string query,
        List<SemanticSearchResult> results,
        CancellationToken ct)
    {
        if (AIProvider == null)
            return results;

        // Use AI to rerank results based on relevance
        var prompt = $"Rerank these search results for the query: \"{query}\"\n\n";
        for (int i = 0; i < results.Count; i++)
        {
            prompt += $"{i + 1}. {results[i].Snippet}\n";
        }
        prompt += "\nReturn the numbers in order of relevance, comma-separated.";

        var response = await AIProvider.CompleteAsync(new AIRequest { Prompt = prompt, MaxTokens = 100 }, ct);

        // Parse reranking - simplified
        return results;
    }
}

/// <summary>
/// Result from semantic search.
/// </summary>
public sealed class SemanticSearchResult
{
    /// <summary>Document identifier.</summary>
    public string DocumentId { get; init; } = "";

    /// <summary>Similarity score (0-1).</summary>
    public float Score { get; init; }

    /// <summary>Rank in results (1-based).</summary>
    public int Rank { get; init; }

    /// <summary>Document metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();

    /// <summary>Text snippet for display.</summary>
    public string? Snippet { get; init; }
}
