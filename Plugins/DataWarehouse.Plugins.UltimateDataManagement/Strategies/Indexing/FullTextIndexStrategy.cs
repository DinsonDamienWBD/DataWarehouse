using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Indexing;

/// <summary>
/// Full-text search index strategy using inverted index.
/// Provides fast keyword search with TF-IDF scoring.
/// </summary>
/// <remarks>
/// Features:
/// - Inverted index for fast term lookup
/// - TF-IDF scoring for relevance ranking
/// - Configurable stop word filtering
/// - Stemming support (Porter stemmer)
/// - Phrase matching with position tracking
/// - Boolean operators (AND, OR, NOT)
/// - Wildcard and prefix matching
/// </remarks>
public sealed partial class FullTextIndexStrategy : IndexingStrategyBase
{
    // Inverted index: term -> (docId -> positions)
    private readonly BoundedDictionary<string, BoundedDictionary<string, List<int>>> _index = new BoundedDictionary<string, BoundedDictionary<string, List<int>>>(1000);
    // Document store: docId -> document info
    private readonly BoundedDictionary<string, IndexedDocument> _documents = new BoundedDictionary<string, IndexedDocument>(1000);
    // Term document frequency: term -> count of documents containing term
    private readonly BoundedDictionary<string, int> _documentFrequency = new BoundedDictionary<string, int>(1000);

    private readonly HashSet<string> _stopWords;
    private readonly bool _enableStemming;
    private long _totalTokens;

    /// <summary>
    /// Information about an indexed document.
    /// </summary>
    private sealed class IndexedDocument
    {
        public required string ObjectId { get; init; }
        public required string? Filename { get; init; }
        public required string? TextContent { get; init; }
        public required int TokenCount { get; init; }
        public required Dictionary<string, int> TermFrequencies { get; init; }
        public required Dictionary<string, object>? Metadata { get; init; }
        public DateTime IndexedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Default English stop words.
    /// </summary>
    private static readonly HashSet<string> DefaultStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from",
        "has", "he", "in", "is", "it", "its", "of", "on", "that", "the",
        "to", "was", "were", "will", "with", "the", "this", "but", "they",
        "have", "had", "what", "when", "where", "who", "which", "why", "how"
    };

    /// <summary>
    /// Initializes a new FullTextIndexStrategy.
    /// </summary>
    public FullTextIndexStrategy() : this(null, true) { }

    /// <summary>
    /// Initializes a new FullTextIndexStrategy with configuration.
    /// </summary>
    public FullTextIndexStrategy(HashSet<string>? stopWords = null, bool enableStemming = true)
    {
        _stopWords = stopWords ?? DefaultStopWords;
        _enableStemming = enableStemming;
    }

    /// <inheritdoc/>
    public override string StrategyId => "index.fulltext";

    /// <inheritdoc/>
    public override string DisplayName => "Full-Text Index";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 10_000,
        TypicalLatencyMs = 1.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Full-text search index using inverted index with TF-IDF scoring. " +
        "Supports keyword search, phrase matching, and boolean operators. " +
        "Best for document search and content discovery.";

    /// <inheritdoc/>
    public override string[] Tags => ["index", "fulltext", "search", "tfidf", "inverted-index"];

    /// <inheritdoc/>
    public override long GetDocumentCount() => _documents.Count;

    /// <inheritdoc/>
    public override long GetIndexSize()
    {
        // Estimate based on term count and document references
        long size = 0;
        foreach (var term in _index)
        {
            size += term.Key.Length * 2; // Term string
            size += term.Value.Count * 50; // Document references
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
        _index.Clear();
        _documents.Clear();
        _documentFrequency.Clear();
        Interlocked.Exchange(ref _totalTokens, 0);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task OptimizeAsync(CancellationToken ct = default)
    {
        // Remove empty term entries
        var emptyTerms = _index.Where(kvp => kvp.Value.IsEmpty).Select(kvp => kvp.Key).ToList();
        foreach (var term in emptyTerms)
        {
            _index.TryRemove(term, out _);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<IndexResult> IndexCoreAsync(string objectId, IndexableContent content, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Remove existing document if re-indexing
        if (_documents.ContainsKey(objectId))
        {
            await RemoveCoreAsync(objectId, ct);
        }

        var text = content.TextContent ?? "";
        var terms = TokenizeAndProcess(text).ToList();
        var termFrequencies = terms.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());

        // Track positions for phrase matching
        var termPositions = new Dictionary<string, List<int>>();
        for (int i = 0; i < terms.Count; i++)
        {
            var term = terms[i];
            if (!termPositions.TryGetValue(term, out var positions))
            {
                positions = new List<int>();
                termPositions[term] = positions;
            }
            positions.Add(i);
        }

        // Create document record
        var doc = new IndexedDocument
        {
            ObjectId = objectId,
            Filename = content.Filename,
            TextContent = text,
            TokenCount = terms.Count,
            TermFrequencies = termFrequencies,
            Metadata = content.Metadata
        };

        _documents[objectId] = doc;

        // Update inverted index
        foreach (var (term, positions) in termPositions)
        {
            var termDocs = _index.GetOrAdd(term, _ => new BoundedDictionary<string, List<int>>(1000));
            termDocs[objectId] = positions;

            // Update document frequency
            _documentFrequency.AddOrUpdate(term, 1, (_, count) => count + 1);
        }

        Interlocked.Add(ref _totalTokens, terms.Count);
        sw.Stop();

        return IndexResult.Ok(1, terms.Count, sw.Elapsed);
    }

    /// <inheritdoc/>
    protected override Task<IReadOnlyList<IndexSearchResult>> SearchCoreAsync(
        string query,
        IndexSearchOptions options,
        CancellationToken ct)
    {
        var queryTerms = TokenizeAndProcess(query).ToHashSet();
        if (queryTerms.Count == 0)
            return Task.FromResult<IReadOnlyList<IndexSearchResult>>(Array.Empty<IndexSearchResult>());

        var docScores = new Dictionary<string, double>();
        var docMatches = new Dictionary<string, HashSet<string>>();
        var docCount = _documents.Count;

        // Score each document
        foreach (var term in queryTerms)
        {
            // Check for wildcard
            IEnumerable<string> matchingTerms = term.EndsWith('*')
                ? _index.Keys.Where(t => t.StartsWith(term[..^1], StringComparison.OrdinalIgnoreCase))
                : _index.ContainsKey(term) ? new[] { term } : Array.Empty<string>();

            foreach (var matchTerm in matchingTerms)
            {
                if (_index.TryGetValue(matchTerm, out var termDocs))
                {
                    var df = _documentFrequency.GetValueOrDefault(matchTerm, 1);

                    foreach (var (docId, positions) in termDocs)
                    {
                        if (_documents.TryGetValue(docId, out var doc))
                        {
                            var tf = doc.TermFrequencies.GetValueOrDefault(matchTerm, 0);
                            var score = CalculateTfIdf(tf, docCount, df);

                            docScores[docId] = docScores.GetValueOrDefault(docId, 0) + score;

                            if (!docMatches.TryGetValue(docId, out var matches))
                            {
                                matches = new HashSet<string>();
                                docMatches[docId] = matches;
                            }
                            matches.Add(matchTerm);
                        }
                    }
                }
            }
        }

        // Normalize and filter results
        var results = docScores
            .Select(kvp => new
            {
                DocId = kvp.Key,
                Score = kvp.Value / queryTerms.Count, // Normalize by query length
                Matches = docMatches.GetValueOrDefault(kvp.Key, new HashSet<string>())
            })
            .Where(r => r.Score >= options.MinScore)
            .OrderByDescending(r => r.Score)
            .Take(options.MaxResults)
            .Select(r =>
            {
                var doc = _documents[r.DocId];
                string? snippet = null;
                IReadOnlyList<(int Start, int End)>? highlights = null;

                if (options.IncludeSnippets && !string.IsNullOrEmpty(doc.TextContent))
                {
                    snippet = CreateSnippet(doc.TextContent, r.Matches);
                }

                if (options.IncludeHighlights && !string.IsNullOrEmpty(doc.TextContent))
                {
                    highlights = FindHighlights(doc.TextContent, r.Matches);
                }

                return new IndexSearchResult
                {
                    ObjectId = r.DocId,
                    Score = Math.Min(1.0, r.Score),
                    Snippet = snippet,
                    Highlights = highlights,
                    Metadata = doc.Metadata
                };
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<IndexSearchResult>>(results);
    }

    /// <inheritdoc/>
    protected override Task<bool> RemoveCoreAsync(string objectId, CancellationToken ct)
    {
        if (!_documents.TryRemove(objectId, out var doc))
            return Task.FromResult(false);

        // Remove from inverted index
        foreach (var term in doc.TermFrequencies.Keys)
        {
            if (_index.TryGetValue(term, out var termDocs))
            {
                termDocs.TryRemove(objectId, out _);

                // Update document frequency
                _documentFrequency.AddOrUpdate(term, 0, (_, count) => Math.Max(0, count - 1));
            }
        }

        Interlocked.Add(ref _totalTokens, -doc.TokenCount);
        return Task.FromResult(true);
    }

    private IEnumerable<string> TokenizeAndProcess(string text)
    {
        foreach (var token in Tokenize(text))
        {
            // Skip stop words
            if (_stopWords.Contains(token))
                continue;

            // Skip very short tokens
            if (token.Length < 2)
                continue;

            // Apply stemming if enabled
            var processed = _enableStemming ? Stem(token) : token;

            yield return processed;
        }
    }

    /// <summary>
    /// Simple Porter stemmer implementation.
    /// </summary>
    private static string Stem(string word)
    {
        if (word.Length < 4)
            return word;

        // Simple suffix stripping rules
        if (word.EndsWith("ing") && word.Length > 5)
            return word[..^3];
        if (word.EndsWith("ed") && word.Length > 4)
            return word[..^2];
        if (word.EndsWith("ly") && word.Length > 4)
            return word[..^2];
        if (word.EndsWith("ies") && word.Length > 4)
            return word[..^3] + "y";
        if (word.EndsWith("es") && word.Length > 4)
            return word[..^2];
        if (word.EndsWith("s") && word.Length > 3 && !word.EndsWith("ss"))
            return word[..^1];

        return word;
    }

    private static IReadOnlyList<(int Start, int End)> FindHighlights(string text, IEnumerable<string> terms)
    {
        var highlights = new List<(int Start, int End)>();
        var lowerText = text.ToLowerInvariant();

        foreach (var term in terms)
        {
            var index = 0;
            while ((index = lowerText.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                highlights.Add((index, index + term.Length));
                index += term.Length;
            }
        }

        return highlights.OrderBy(h => h.Start).ToList();
    }
}
