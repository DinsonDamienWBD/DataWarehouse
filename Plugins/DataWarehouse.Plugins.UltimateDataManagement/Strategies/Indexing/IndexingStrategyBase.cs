using System.Diagnostics;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Indexing;

/// <summary>
/// Result of an indexing operation.
/// </summary>
public sealed class IndexResult
{
    /// <summary>
    /// Whether the operation succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Number of documents indexed.
    /// </summary>
    public int DocumentCount { get; init; }

    /// <summary>
    /// Number of tokens/terms indexed.
    /// </summary>
    public long TokenCount { get; init; }

    /// <summary>
    /// Duration of the indexing operation.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static IndexResult Ok(int documentCount, long tokenCount, TimeSpan duration) =>
        new() { Success = true, DocumentCount = documentCount, TokenCount = tokenCount, Duration = duration };

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static IndexResult Failed(string error) =>
        new() { Success = false, ErrorMessage = error };
}

/// <summary>
/// A search result from an indexing strategy.
/// </summary>
public sealed class IndexSearchResult
{
    /// <summary>
    /// Object/document ID.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Relevance score (0-1).
    /// </summary>
    public double Score { get; init; }

    /// <summary>
    /// Matching snippet or preview.
    /// </summary>
    public string? Snippet { get; init; }

    /// <summary>
    /// Highlight positions.
    /// </summary>
    public IReadOnlyList<(int Start, int End)>? Highlights { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Search options for index queries.
/// </summary>
public sealed class IndexSearchOptions
{
    /// <summary>
    /// Maximum number of results to return.
    /// </summary>
    public int MaxResults { get; init; } = 50;

    /// <summary>
    /// Minimum score threshold.
    /// </summary>
    public double MinScore { get; init; } = 0.0;

    /// <summary>
    /// Whether to include snippets.
    /// </summary>
    public bool IncludeSnippets { get; init; } = true;

    /// <summary>
    /// Whether to include highlight positions.
    /// </summary>
    public bool IncludeHighlights { get; init; } = false;

    /// <summary>
    /// Filters to apply.
    /// </summary>
    public Dictionary<string, object>? Filters { get; init; }
}

/// <summary>
/// Interface for indexing strategies.
/// </summary>
public interface IIndexingStrategy : IDataManagementStrategy
{
    /// <summary>
    /// Indexes content for later search.
    /// </summary>
    Task<IndexResult> IndexAsync(string objectId, IndexableContent content, CancellationToken ct = default);

    /// <summary>
    /// Indexes multiple documents in a batch.
    /// </summary>
    Task<IndexResult> IndexBatchAsync(IEnumerable<(string ObjectId, IndexableContent Content)> batch, CancellationToken ct = default);

    /// <summary>
    /// Searches the index.
    /// </summary>
    Task<IReadOnlyList<IndexSearchResult>> SearchAsync(string query, IndexSearchOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Removes a document from the index.
    /// </summary>
    Task<bool> RemoveAsync(string objectId, CancellationToken ct = default);

    /// <summary>
    /// Checks if a document is indexed.
    /// </summary>
    Task<bool> ExistsAsync(string objectId, CancellationToken ct = default);

    /// <summary>
    /// Gets the count of indexed documents.
    /// </summary>
    long GetDocumentCount();

    /// <summary>
    /// Gets the total size of the index in bytes.
    /// </summary>
    long GetIndexSize();

    /// <summary>
    /// Optimizes the index for query performance.
    /// </summary>
    Task OptimizeAsync(CancellationToken ct = default);

    /// <summary>
    /// Clears the entire index.
    /// </summary>
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// Abstract base class for indexing strategies.
/// </summary>
public abstract class IndexingStrategyBase : DataManagementStrategyBase, IIndexingStrategy
{
    /// <inheritdoc/>
    public override DataManagementCategory Category => DataManagementCategory.Indexing;

    /// <inheritdoc/>
    public async Task<IndexResult> IndexAsync(string objectId, IndexableContent content, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentNullException.ThrowIfNull(content);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await IndexCoreAsync(objectId, content, ct);
            sw.Stop();

            RecordWrite(content.TextContent?.Length ?? 0, sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordFailure();
            return IndexResult.Failed(ex.Message);
        }
    }

    /// <inheritdoc/>
    public async Task<IndexResult> IndexBatchAsync(IEnumerable<(string ObjectId, IndexableContent Content)> batch, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(batch);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await IndexBatchCoreAsync(batch, ct);
            sw.Stop();

            RecordWrite(result.TokenCount, sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordFailure();
            return IndexResult.Failed(ex.Message);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IndexSearchResult>> SearchAsync(string query, IndexSearchOptions? options = null, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var sw = Stopwatch.StartNew();
        try
        {
            var results = await SearchCoreAsync(query, options ?? new IndexSearchOptions(), ct);
            sw.Stop();

            RecordRead(0, sw.Elapsed.TotalMilliseconds);
            return results;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(string objectId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await RemoveCoreAsync(objectId, ct);
            sw.Stop();

            RecordDelete(sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public abstract Task<bool> ExistsAsync(string objectId, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract long GetDocumentCount();

    /// <inheritdoc/>
    public abstract long GetIndexSize();

    /// <inheritdoc/>
    public virtual Task OptimizeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public abstract Task ClearAsync(CancellationToken ct = default);

    /// <summary>
    /// Core indexing implementation.
    /// </summary>
    protected abstract Task<IndexResult> IndexCoreAsync(string objectId, IndexableContent content, CancellationToken ct);

    /// <summary>
    /// Core batch indexing implementation.
    /// </summary>
    protected virtual async Task<IndexResult> IndexBatchCoreAsync(
        IEnumerable<(string ObjectId, IndexableContent Content)> batch,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int count = 0;
        long tokens = 0;

        foreach (var (objectId, content) in batch)
        {
            ct.ThrowIfCancellationRequested();
            var result = await IndexCoreAsync(objectId, content, ct);
            if (result.Success)
            {
                count++;
                tokens += result.TokenCount;
            }
        }

        sw.Stop();
        return IndexResult.Ok(count, tokens, sw.Elapsed);
    }

    /// <summary>
    /// Core search implementation.
    /// </summary>
    protected abstract Task<IReadOnlyList<IndexSearchResult>> SearchCoreAsync(
        string query,
        IndexSearchOptions options,
        CancellationToken ct);

    /// <summary>
    /// Core remove implementation.
    /// </summary>
    protected abstract Task<bool> RemoveCoreAsync(string objectId, CancellationToken ct);

    /// <summary>
    /// Tokenizes text into terms.
    /// </summary>
    protected static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        // Simple whitespace and punctuation tokenizer
        var word = new System.Text.StringBuilder();

        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(c);
            }
            else if (word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }
        }

        if (word.Length > 0)
            yield return word.ToString();
    }

    /// <summary>
    /// Calculates TF-IDF score for a term in a document.
    /// </summary>
    protected static double CalculateTfIdf(int termFrequency, int documentCount, int documentsWithTerm)
    {
        if (termFrequency == 0 || documentCount == 0 || documentsWithTerm == 0)
            return 0;

        var tf = Math.Log(1 + termFrequency);
        var idf = Math.Log((double)documentCount / documentsWithTerm);
        return tf * idf;
    }

    /// <summary>
    /// Creates a text snippet around matching terms.
    /// </summary>
    protected static string CreateSnippet(string text, IEnumerable<string> matchingTerms, int contextLength = 50)
    {
        var terms = matchingTerms.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lowerText = text.ToLowerInvariant();

        int bestStart = 0;
        int bestScore = 0;

        // Find the position with most matches
        for (int i = 0; i < Math.Max(1, text.Length - contextLength * 2); i++)
        {
            var window = lowerText.Substring(i, Math.Min(contextLength * 2, text.Length - i));
            var score = terms.Count(t => window.Contains(t, StringComparison.OrdinalIgnoreCase));

            if (score > bestScore)
            {
                bestScore = score;
                bestStart = i;
            }
        }

        var start = Math.Max(0, bestStart - 10);
        var length = Math.Min(contextLength * 2 + 20, text.Length - start);
        var snippet = text.Substring(start, length);

        if (start > 0) snippet = "..." + snippet;
        if (start + length < text.Length) snippet += "...";

        return snippet;
    }
}
