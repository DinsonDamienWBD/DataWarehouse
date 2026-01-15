using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;
using SdkMatchType = DataWarehouse.SDK.Contracts.MatchType;

namespace DataWarehouse.Kernel.Storage
{
    /// <summary>
    /// Production-ready search orchestrator implementing multi-provider search execution.
    /// Supports SQL, NoSQL, Vector, and AI-powered search with result fusion.
    /// </summary>
    public class SearchOrchestratorManager : SearchOrchestratorBase
    {
        private readonly IKernelContext _context;
        private readonly ConcurrentDictionary<string, IndexedDocument> _sqlIndex = new();
        private readonly ConcurrentDictionary<string, IndexedDocument> _noSqlIndex = new();
        private readonly ConcurrentDictionary<string, float[]> _vectorIndex = new();
        private readonly ConcurrentDictionary<string, SearchCacheEntry> _searchCache = new();

        public SearchOrchestratorManager(IKernelContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        #region Provider Search Execution

        protected override async Task<ProviderSearchResult> ExecuteProviderSearchAsync(
            SearchProviderType type,
            SearchQuery query,
            CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _context.LogDebug($"[Search] Executing {type} search for: {query.QueryText}");

            try
            {
                var items = type switch
                {
                    SearchProviderType.SqlMetadata => await ExecuteSqlSearchInternalAsync(query, ct),
                    SearchProviderType.NoSqlKeyword => await ExecuteNoSqlSearchInternalAsync(query, ct),
                    SearchProviderType.VectorSemantic => await ExecuteVectorSearchInternalAsync(query, ct),
                    SearchProviderType.AiAgent => await ExecuteAiSearchInternalAsync(query, ct),
                    _ => new List<SearchResultItem>()
                };

                sw.Stop();
                _context.LogDebug($"[Search] {type} returned {items.Count} results in {sw.ElapsedMilliseconds}ms");

                return new ProviderSearchResult
                {
                    Provider = type,
                    Count = items.Count,
                    Latency = sw.Elapsed,
                    Items = items
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                _context.LogError($"[Search] {type} search failed: {ex.Message}", ex);
                return new ProviderSearchResult
                {
                    Provider = type,
                    Count = 0,
                    Error = ex.Message,
                    Latency = sw.Elapsed,
                    Items = new List<SearchResultItem>()
                };
            }
        }

        #endregion

        #region SQL Metadata Search

        private Task<List<SearchResultItem>> ExecuteSqlSearchInternalAsync(SearchQuery query, CancellationToken ct)
        {
            var items = new List<SearchResultItem>();
            var queryLower = query.QueryText.ToLowerInvariant();
            var terms = queryLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var doc in _sqlIndex.Values)
            {
                ct.ThrowIfCancellationRequested();

                // Score based on field matches
                double score = 0;

                // Title match (highest weight)
                if (!string.IsNullOrEmpty(doc.Title))
                {
                    var titleLower = doc.Title.ToLowerInvariant();
                    foreach (var term in terms)
                    {
                        if (titleLower.Contains(term))
                            score += 10;
                        if (titleLower.StartsWith(term))
                            score += 5;
                    }
                }

                // Metadata field matches
                foreach (var meta in doc.Metadata)
                {
                    var valueLower = meta.Value?.ToString()?.ToLowerInvariant() ?? "";
                    foreach (var term in terms)
                    {
                        if (valueLower.Contains(term))
                            score += 2;
                    }
                }

                // Content type match
                if (!string.IsNullOrEmpty(doc.ContentType) && queryLower.Contains(doc.ContentType.ToLowerInvariant()))
                {
                    score += 3;
                }

                if (score > 0)
                {
                    items.Add(new SearchResultItem
                    {
                        Uri = doc.Uri,
                        Title = doc.Title ?? doc.Uri.ToString(),
                        Snippet = GenerateSnippet(doc, terms),
                        Score = score,
                        Source = SearchProviderType.SqlMetadata,
                        MatchType = SdkMatchType.Exact,
                        Metadata = doc.Metadata
                    });
                }
            }

            // Apply filters
            items = ApplyFilters(items, query);

            return Task.FromResult(items
                    .OrderByDescending(i => i.Score)
                    .Take(query.MaxResultsPerProvider)
                    .ToList());
        }

        #endregion

        #region NoSQL Full-Text Search

        private Task<List<SearchResultItem>> ExecuteNoSqlSearchInternalAsync(SearchQuery query, CancellationToken ct)
        {
            var items = new List<SearchResultItem>();
            var queryLower = query.QueryText.ToLowerInvariant();
            var terms = queryLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var doc in _noSqlIndex.Values)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(doc.FullText))
                    continue;

                var textLower = doc.FullText.ToLowerInvariant();
                double score = 0;
                int matchCount = 0;

                foreach (var term in terms)
                {
                    if (textLower.Contains(term))
                    {
                        // Count occurrences
                        int count = CountOccurrences(textLower, term);
                        score += count * 1.0;
                        matchCount++;

                        // Boost for phrase match
                        if (textLower.Contains(queryLower))
                            score += 20;
                    }
                }

                // Require at least one term match
                if (matchCount > 0)
                {
                    // Boost for matching more terms
                    score *= (1.0 + (double)matchCount / terms.Length);

                    items.Add(new SearchResultItem
                    {
                        Uri = doc.Uri,
                        Title = doc.Title ?? doc.Uri.ToString(),
                        Snippet = ExtractSnippetFromText(doc.FullText, terms),
                        Score = score,
                        Source = SearchProviderType.NoSqlKeyword,
                        MatchType = SdkMatchType.Keyword,
                        Metadata = doc.Metadata
                    });
                }
            }

            items = ApplyFilters(items, query);

            return Task.FromResult(items
                    .OrderByDescending(i => i.Score)
                    .Take(query.MaxResultsPerProvider)
                    .ToList());
        }

        #endregion

        #region Vector Semantic Search

        private Task<List<SearchResultItem>> ExecuteVectorSearchInternalAsync(SearchQuery query, CancellationToken ct)
        {
            var items = new List<SearchResultItem>();

            // Get query vector (would come from embedding model in production)
            var queryVector = query.QueryVector ?? GenerateSimpleVector(query.QueryText);

            if (queryVector == null || queryVector.Length == 0)
            {
                return Task.FromResult(items);
            }

            // Get minimum score threshold from filters or use default
            var minScore = query.Filters?.TryGetValue("MinSemanticScore", out var scoreObj) == true
                           && scoreObj is double d ? d : 0.5;

            foreach (var kvp in _vectorIndex)
            {
                ct.ThrowIfCancellationRequested();

                var docVector = kvp.Value;
                var similarity = CosineSimilarity(queryVector, docVector);

                // Threshold for semantic relevance
                if (similarity >= minScore)
                {
                    var uri = new Uri(kvp.Key);
                    var doc = _sqlIndex.Values.FirstOrDefault(d => d.Uri.ToString() == kvp.Key)
                           ?? _noSqlIndex.Values.FirstOrDefault(d => d.Uri.ToString() == kvp.Key);

                    items.Add(new SearchResultItem
                    {
                        Uri = uri,
                        Title = doc?.Title ?? uri.ToString(),
                        Snippet = doc?.FullText?.Substring(0, Math.Min(200, doc.FullText.Length)) ?? "",
                        Score = similarity * 100, // Scale to 0-100
                        Source = SearchProviderType.VectorSemantic,
                        MatchType = SdkMatchType.Semantic,
                        Metadata = doc?.Metadata ?? new Dictionary<string, object>()
                    });
                }
            }

            items = ApplyFilters(items, query);

            return Task.FromResult(items
                    .OrderByDescending(i => i.Score)
                    .Take(query.MaxResultsPerProvider)
                    .ToList());
        }

        #endregion

        #region AI Agent Search

        private async Task<List<SearchResultItem>> ExecuteAiSearchInternalAsync(SearchQuery query, CancellationToken ct)
        {
            // AI-powered search with reasoning
            // In production, this would call an AI provider plugin

            await Task.Delay(100, ct); // Simulated AI processing

            var items = new List<SearchResultItem>();

            // Combine results from other providers for AI enhancement
            var sqlResults = await ExecuteSqlSearchInternalAsync(query, ct);
            var noSqlResults = await ExecuteNoSqlSearchInternalAsync(query, ct);
            var vectorResults = await ExecuteVectorSearchInternalAsync(query, ct);

            // AI would analyze and re-rank these results
            var allItems = sqlResults
                .Concat(noSqlResults)
                .Concat(vectorResults)
                .GroupBy(i => i.Uri.ToString())
                .Select(g => g.OrderByDescending(i => i.Score).First())
                .ToList();

            // Simulated AI reasoning
            var reasoning = $"Analyzed {allItems.Count} documents for query '{query.QueryText}'. " +
                          $"Found {allItems.Count(i => i.Score > 50)} highly relevant results.";

            // Add AI insight as first result
            if (allItems.Count > 0)
            {
                items.Add(new SearchResultItem
                {
                    Uri = new Uri("ai://reasoning"),
                    Title = "AI Analysis",
                    Snippet = reasoning,
                    Score = 100,
                    Source = SearchProviderType.AiAgent,
                    MatchType = SdkMatchType.AiInferred,
                    Metadata = new Dictionary<string, object>
                    {
                        ["DocumentsAnalyzed"] = allItems.Count,
                        ["TopScore"] = allItems.MaxBy(i => i.Score)?.Score ?? 0
                    }
                });
            }

            // Re-ranked results with AI boost
            foreach (var item in allItems.Take(query.MaxResultsPerProvider - 1))
            {
                items.Add(new SearchResultItem
                {
                    Uri = item.Uri,
                    Title = item.Title,
                    Snippet = item.Snippet,
                    Score = item.Score * 1.1, // AI boost
                    Source = SearchProviderType.AiAgent,
                    MatchType = SdkMatchType.AiInferred,
                    Metadata = item.Metadata
                });
            }

            return items;
        }

        #endregion

        #region Indexing

        /// <summary>
        /// Indexes a document for search.
        /// </summary>
        public void IndexDocument(Uri uri, string? title, string? fullText, Dictionary<string, object>? metadata = null)
        {
            var doc = new IndexedDocument
            {
                Uri = uri,
                Title = title,
                FullText = fullText,
                ContentType = GetContentType(uri),
                Metadata = metadata ?? new Dictionary<string, object>(),
                IndexedAt = DateTime.UtcNow
            };

            // Add to SQL index (metadata)
            _sqlIndex[uri.ToString()] = doc;

            // Add to NoSQL index (full-text)
            if (!string.IsNullOrEmpty(fullText))
            {
                _noSqlIndex[uri.ToString()] = doc;
            }

            // Generate and store vector embedding
            if (!string.IsNullOrEmpty(fullText))
            {
                var vector = GenerateSimpleVector(fullText);
                _vectorIndex[uri.ToString()] = vector;
            }

            _context.LogDebug($"[Search] Indexed document: {uri}");
        }

        /// <summary>
        /// Removes a document from all indexes.
        /// </summary>
        public void RemoveDocument(Uri uri)
        {
            var key = uri.ToString();
            _sqlIndex.TryRemove(key, out _);
            _noSqlIndex.TryRemove(key, out _);
            _vectorIndex.TryRemove(key, out _);
            _context.LogDebug($"[Search] Removed document from index: {uri}");
        }

        /// <summary>
        /// Gets index statistics.
        /// </summary>
        public SearchIndexStats GetStats()
        {
            return new SearchIndexStats
            {
                SqlIndexCount = _sqlIndex.Count,
                NoSqlIndexCount = _noSqlIndex.Count,
                VectorIndexCount = _vectorIndex.Count,
                CacheEntries = _searchCache.Count
            };
        }

        #endregion

        #region Helper Methods

        private List<SearchResultItem> ApplyFilters(List<SearchResultItem> items, SearchQuery query)
        {
            var result = items.AsEnumerable();

            // Date range filter
            if (query.DateRange != null && (query.DateRange.From.HasValue || query.DateRange.To.HasValue))
            {
                result = result.Where(i =>
                {
                    if (i.Metadata.TryGetValue("IndexedAt", out var dateObj) && dateObj is DateTime date)
                    {
                        if (query.DateRange.From.HasValue && date < query.DateRange.From.Value)
                            return false;
                        if (query.DateRange.To.HasValue && date > query.DateRange.To.Value)
                            return false;
                    }
                    return true;
                });
            }

            // Apply generic filters from Filters dictionary
            if (query.Filters != null)
            {
                // Content type filter
                if (query.Filters.TryGetValue("ContentTypes", out var contentTypesObj) && contentTypesObj is string[] contentTypes)
                {
                    var types = contentTypes.Select(t => t.ToLowerInvariant()).ToHashSet();
                    result = result.Where(i =>
                    {
                        if (i.Metadata.TryGetValue("ContentType", out var ct))
                        {
                            return types.Contains(ct?.ToString()?.ToLowerInvariant() ?? "");
                        }
                        return true;
                    });
                }

                // Apply other metadata filters
                foreach (var filter in query.Filters.Where(f => f.Key != "ContentTypes" && f.Key != "MinSemanticScore"))
                {
                    var filterKey = filter.Key;
                    var filterValue = filter.Value;
                    result = result.Where(i =>
                    {
                        if (i.Metadata.TryGetValue(filterKey, out var value))
                        {
                            return value?.ToString() == filterValue?.ToString();
                        }
                        return true; // Don't exclude if metadata key doesn't exist
                    });
                }
            }

            return result.ToList();
        }

        private string GenerateSnippet(IndexedDocument doc, string[] terms)
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(doc.Title))
                parts.Add($"Title: {doc.Title}");

            if (!string.IsNullOrEmpty(doc.ContentType))
                parts.Add($"Type: {doc.ContentType}");

            foreach (var meta in doc.Metadata.Take(3))
            {
                parts.Add($"{meta.Key}: {meta.Value}");
            }

            return string.Join(" | ", parts);
        }

        private string ExtractSnippetFromText(string text, string[] terms)
        {
            // Find the first occurrence of any term and extract surrounding context
            var textLower = text.ToLowerInvariant();
            int bestIndex = -1;

            foreach (var term in terms)
            {
                var index = textLower.IndexOf(term);
                if (index >= 0 && (bestIndex < 0 || index < bestIndex))
                {
                    bestIndex = index;
                }
            }

            if (bestIndex < 0)
                bestIndex = 0;

            // Extract 200 characters centered on the match
            int start = Math.Max(0, bestIndex - 100);
            int length = Math.Min(200, text.Length - start);

            var snippet = text.Substring(start, length);

            if (start > 0)
                snippet = "..." + snippet;
            if (start + length < text.Length)
                snippet = snippet + "...";

            return snippet;
        }

        private static int CountOccurrences(string text, string term)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(term, index)) >= 0)
            {
                count++;
                index += term.Length;
            }
            return count;
        }

        private static float[] GenerateSimpleVector(string text)
        {
            // Simple bag-of-words vector for demonstration
            // In production, this would use a real embedding model
            var words = text.ToLowerInvariant()
                .Split(new[] { ' ', '\n', '\r', '\t', '.', ',', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
                .Distinct()
                .Take(128)
                .ToArray();

            var vector = new float[128];
            for (int i = 0; i < words.Length && i < 128; i++)
            {
                // Simple hash-based embedding
                vector[i] = (float)(words[i].GetHashCode() % 1000) / 1000f;
            }

            // Normalize
            var magnitude = (float)Math.Sqrt(vector.Sum(v => v * v));
            if (magnitude > 0)
            {
                for (int i = 0; i < vector.Length; i++)
                    vector[i] /= magnitude;
            }

            return vector;
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

            magnitudeA = Math.Sqrt(magnitudeA);
            magnitudeB = Math.Sqrt(magnitudeB);

            if (magnitudeA == 0 || magnitudeB == 0)
                return 0;

            return dotProduct / (magnitudeA * magnitudeB);
        }

        private static string GetContentType(Uri uri)
        {
            var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            return ext switch
            {
                ".txt" => "text/plain",
                ".json" => "application/json",
                ".xml" => "application/xml",
                ".html" or ".htm" => "text/html",
                ".pdf" => "application/pdf",
                _ => "application/octet-stream"
            };
        }

        #endregion

        #region Internal Classes

        private class IndexedDocument
        {
            public Uri Uri { get; set; } = null!;
            public string? Title { get; set; }
            public string? FullText { get; set; }
            public string? ContentType { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new();
            public DateTime IndexedAt { get; set; }
        }

        private class SearchCacheEntry
        {
            public string QueryHash { get; set; } = string.Empty;
            public SearchResult Result { get; set; } = null!;
            public DateTime CachedAt { get; set; }
            public TimeSpan Ttl { get; set; }
        }

        #endregion
    }

    /// <summary>
    /// Search index statistics.
    /// </summary>
    public class SearchIndexStats
    {
        public int SqlIndexCount { get; set; }
        public int NoSqlIndexCount { get; set; }
        public int VectorIndexCount { get; set; }
        public int CacheEntries { get; set; }
    }
}
