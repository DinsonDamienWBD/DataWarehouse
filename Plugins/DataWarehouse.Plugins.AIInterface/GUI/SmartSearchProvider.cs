// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.AIInterface.Channels;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.AIInterface.GUI;

/// <summary>
/// Provides AI-powered search suggestions and intelligent query assistance for GUI search boxes.
/// </summary>
/// <remarks>
/// <para>
/// The SmartSearchProvider enhances search functionality with:
/// <list type="bullet">
/// <item>Real-time search suggestions as the user types</item>
/// <item>Query expansion with synonyms and related terms</item>
/// <item>Typo correction and "did you mean" suggestions</item>
/// <item>Search intent classification (file, content, metadata)</item>
/// <item>Historical query suggestions based on user behavior</item>
/// <item>Faceted search suggestions based on available filters</item>
/// </list>
/// </para>
/// <para>
/// When Intelligence is unavailable, the provider falls back to basic
/// prefix matching and recent search history.
/// </para>
/// </remarks>
public sealed class SmartSearchProvider : IDisposable
{
    private readonly IMessageBus? _messageBus;
    private readonly ConcurrentDictionary<string, SearchSession> _sessions = new();
    private readonly ConcurrentDictionary<string, List<string>> _userHistory = new();
    private readonly SmartSearchConfig _config;
    private bool _intelligenceAvailable;
    private IntelligenceCapabilities _capabilities = IntelligenceCapabilities.None;
    private bool _disposed;

    // Cached common suggestions
    private static readonly string[] CommonSearchPrefixes = new[]
    {
        "files containing",
        "documents about",
        "images from",
        "reports for",
        "data from",
        "encrypted files",
        "recent uploads",
        "shared with me"
    };

    /// <summary>
    /// Gets whether Intelligence is available for smart search.
    /// </summary>
    public bool IsIntelligenceAvailable => _intelligenceAvailable;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmartSearchProvider"/> class.
    /// </summary>
    /// <param name="messageBus">Message bus for Intelligence communication.</param>
    /// <param name="config">Optional configuration.</param>
    public SmartSearchProvider(IMessageBus? messageBus, SmartSearchConfig? config = null)
    {
        _messageBus = messageBus;
        _config = config ?? new SmartSearchConfig();
    }

    /// <summary>
    /// Initializes the provider and discovers Intelligence availability.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_messageBus == null)
        {
            _intelligenceAvailable = false;
            return;
        }

        try
        {
            var response = await _messageBus.RequestAsync(
                IntelligenceTopics.Discover,
                new Dictionary<string, object>
                {
                    ["requestorId"] = "gui.smartsearch",
                    ["requestorName"] = "SmartSearchProvider",
                    ["timestamp"] = DateTimeOffset.UtcNow
                },
                ct);

            if (response != null &&
                response.TryGetValue("available", out var available) && available is true)
            {
                _intelligenceAvailable = true;

                if (response.TryGetValue("capabilities", out var caps))
                {
                    if (caps is IntelligenceCapabilities ic)
                        _capabilities = ic;
                    else if (caps is long longVal)
                        _capabilities = (IntelligenceCapabilities)longVal;
                }
            }
        }
        catch
        {
            _intelligenceAvailable = false;
        }
    }

    /// <summary>
    /// Gets search suggestions based on the current query.
    /// </summary>
    /// <param name="query">The current search query.</param>
    /// <param name="userId">Optional user ID for personalization.</param>
    /// <param name="context">Optional search context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of search suggestions.</returns>
    public async Task<SearchSuggestions> GetSuggestionsAsync(
        string query,
        string? userId = null,
        SearchContext? context = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return GetEmptySuggestions(userId);
        }

        var suggestions = new SearchSuggestions
        {
            Query = query,
            Timestamp = DateTime.UtcNow
        };

        // Get basic suggestions (fast path)
        var basicSuggestions = GetBasicSuggestions(query, userId);
        suggestions.Completions.AddRange(basicSuggestions);

        // If Intelligence is available, enhance with AI
        if (_intelligenceAvailable && _messageBus != null)
        {
            try
            {
                var aiSuggestions = await GetAISuggestionsAsync(query, userId, context, ct);
                MergeSuggestions(suggestions, aiSuggestions);
            }
            catch
            {
                // Fall back to basic suggestions on error
            }
        }

        // Record for history
        RecordQuery(userId ?? "anonymous", query);

        return suggestions;
    }

    /// <summary>
    /// Classifies the search intent.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The search intent classification.</returns>
    public async Task<SearchIntentResult> ClassifyIntentAsync(
        string query,
        CancellationToken ct = default)
    {
        // Basic keyword-based classification
        var intent = ClassifyIntentBasic(query);

        // Enhance with AI if available
        if (_intelligenceAvailable && _messageBus != null)
        {
            try
            {
                var response = await _messageBus.RequestAsync(
                    IntelligenceTopics.RequestIntent,
                    new Dictionary<string, object>
                    {
                        ["text"] = query,
                        ["domain"] = "search",
                        ["intents"] = new[] { "file_search", "content_search", "metadata_search", "semantic_search", "time_based", "user_based" }
                    },
                    ct);

                if (response != null && response.TryGetValue("intent", out var aiIntent))
                {
                    intent = new SearchIntentResult
                    {
                        Intent = MapAIIntent(aiIntent?.ToString() ?? "file_search"),
                        Confidence = response.TryGetValue("confidence", out var conf) && conf is double c ? c : 0.7,
                        Entities = ExtractEntities(response),
                        SuggestedFilters = ExtractFilters(response)
                    };
                }
            }
            catch
            {
                // Use basic classification
            }
        }

        return intent;
    }

    /// <summary>
    /// Expands a query with synonyms and related terms.
    /// </summary>
    /// <param name="query">The original query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The expanded query result.</returns>
    public async Task<QueryExpansionResult> ExpandQueryAsync(
        string query,
        CancellationToken ct = default)
    {
        var result = new QueryExpansionResult
        {
            OriginalQuery = query,
            ExpandedTerms = new List<ExpandedTerm>()
        };

        if (!_intelligenceAvailable || _messageBus == null)
        {
            return result;
        }

        try
        {
            var response = await _messageBus.RequestAsync(
                "intelligence.request.query-expansion",
                new Dictionary<string, object>
                {
                    ["query"] = query,
                    ["maxExpansions"] = _config.MaxQueryExpansions
                },
                ct);

            if (response != null && response.TryGetValue("expansions", out var expansions))
            {
                if (expansions is IEnumerable<object> expList)
                {
                    foreach (var exp in expList.OfType<Dictionary<string, object>>())
                    {
                        result.ExpandedTerms.Add(new ExpandedTerm
                        {
                            Term = exp.TryGetValue("term", out var t) ? t?.ToString() ?? string.Empty : string.Empty,
                            Type = exp.TryGetValue("type", out var tp) ? tp?.ToString() ?? "synonym" : "synonym",
                            Relevance = exp.TryGetValue("relevance", out var r) && r is double rel ? rel : 0.5
                        });
                    }
                }
            }

            if (response != null && response.TryGetValue("expandedQuery", out var expanded))
            {
                result.ExpandedQuery = expanded?.ToString();
            }
        }
        catch
        {
            // Return unexpanded result
        }

        return result;
    }

    /// <summary>
    /// Checks for spelling errors and suggests corrections.
    /// </summary>
    /// <param name="query">The query to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Spelling correction result.</returns>
    public async Task<SpellingCorrectionResult> CheckSpellingAsync(
        string query,
        CancellationToken ct = default)
    {
        var result = new SpellingCorrectionResult
        {
            OriginalQuery = query,
            HasCorrections = false
        };

        if (!_intelligenceAvailable || _messageBus == null)
        {
            return result;
        }

        try
        {
            var response = await _messageBus.RequestAsync(
                "intelligence.request.spell-check",
                new Dictionary<string, object>
                {
                    ["text"] = query
                },
                ct);

            if (response != null)
            {
                if (response.TryGetValue("correctedQuery", out var corrected) &&
                    corrected?.ToString() != query)
                {
                    result.HasCorrections = true;
                    result.CorrectedQuery = corrected?.ToString();
                }

                if (response.TryGetValue("corrections", out var corrections) &&
                    corrections is IEnumerable<object> corrList)
                {
                    result.Corrections = corrList
                        .OfType<Dictionary<string, object>>()
                        .Select(c => new SpellingCorrection
                        {
                            Original = c.TryGetValue("original", out var o) ? o?.ToString() ?? string.Empty : string.Empty,
                            Suggestion = c.TryGetValue("suggestion", out var s) ? s?.ToString() ?? string.Empty : string.Empty,
                            Position = c.TryGetValue("position", out var p) && p is int pos ? pos : 0
                        })
                        .ToList();

                    result.HasCorrections = result.Corrections.Count > 0;
                }
            }
        }
        catch
        {
            // Return uncorrected result
        }

        return result;
    }

    /// <summary>
    /// Gets facet suggestions based on the query and available data.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of facet suggestions.</returns>
    public async Task<IReadOnlyList<FacetSuggestion>> GetFacetSuggestionsAsync(
        string query,
        CancellationToken ct = default)
    {
        var facets = new List<FacetSuggestion>();

        // Basic facets always available
        facets.Add(new FacetSuggestion
        {
            Name = "fileType",
            Label = "File Type",
            Values = new[] { "Documents", "Images", "Videos", "Data Files", "Archives" }
        });

        facets.Add(new FacetSuggestion
        {
            Name = "dateRange",
            Label = "Date",
            Values = new[] { "Today", "This Week", "This Month", "This Year", "Older" }
        });

        if (!_intelligenceAvailable || _messageBus == null)
        {
            return facets.AsReadOnly();
        }

        try
        {
            var response = await _messageBus.RequestAsync(
                "intelligence.request.facet-suggestions",
                new Dictionary<string, object>
                {
                    ["query"] = query
                },
                ct);

            if (response != null && response.TryGetValue("facets", out var facetData) &&
                facetData is IEnumerable<object> facetList)
            {
                foreach (var f in facetList.OfType<Dictionary<string, object>>())
                {
                    var name = f.TryGetValue("name", out var n) ? n?.ToString() : null;
                    if (string.IsNullOrEmpty(name) || facets.Any(x => x.Name == name))
                        continue;

                    facets.Add(new FacetSuggestion
                    {
                        Name = name!,
                        Label = f.TryGetValue("label", out var l) ? l?.ToString() ?? name! : name!,
                        Values = f.TryGetValue("values", out var v) && v is string[] vals ? vals : Array.Empty<string>()
                    });
                }
            }
        }
        catch
        {
            // Return basic facets
        }

        return facets.AsReadOnly();
    }

    /// <summary>
    /// Records a completed search for history and learning.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="query">The search query.</param>
    /// <param name="resultCount">Number of results found.</param>
    /// <param name="selectedResult">The result that was selected, if any.</param>
    public void RecordSearch(
        string userId,
        string query,
        int resultCount,
        string? selectedResult = null)
    {
        RecordQuery(userId, query);

        // Send feedback to Intelligence for learning
        if (_intelligenceAvailable && _messageBus != null)
        {
            try
            {
                _messageBus.Publish("intelligence.feedback.search", new Dictionary<string, object>
                {
                    ["userId"] = userId,
                    ["query"] = query,
                    ["resultCount"] = resultCount,
                    ["selectedResult"] = selectedResult ?? string.Empty,
                    ["timestamp"] = DateTime.UtcNow
                });
            }
            catch
            {
                // Silent failure
            }
        }
    }

    private SearchSuggestions GetEmptySuggestions(string? userId)
    {
        var suggestions = new SearchSuggestions
        {
            Query = string.Empty,
            Timestamp = DateTime.UtcNow
        };

        // Add recent searches
        if (userId != null && _userHistory.TryGetValue(userId, out var history))
        {
            suggestions.RecentSearches.AddRange(history.TakeLast(5));
        }

        // Add common prefixes as suggestions
        suggestions.Completions.AddRange(CommonSearchPrefixes.Take(5).Select(p => new SearchCompletion
        {
            Text = p,
            Type = CompletionType.Prefix,
            Score = 0.5
        }));

        return suggestions;
    }

    private List<SearchCompletion> GetBasicSuggestions(string query, string? userId)
    {
        var completions = new List<SearchCompletion>();
        var lowerQuery = query.ToLowerInvariant();

        // Add prefix completions
        foreach (var prefix in CommonSearchPrefixes)
        {
            if (prefix.StartsWith(lowerQuery, StringComparison.OrdinalIgnoreCase))
            {
                completions.Add(new SearchCompletion
                {
                    Text = prefix,
                    Type = CompletionType.Prefix,
                    Score = 0.6
                });
            }
        }

        // Add history-based suggestions
        if (userId != null && _userHistory.TryGetValue(userId, out var history))
        {
            foreach (var h in history.Where(h => h.StartsWith(lowerQuery, StringComparison.OrdinalIgnoreCase)))
            {
                if (!completions.Any(c => c.Text == h))
                {
                    completions.Add(new SearchCompletion
                    {
                        Text = h,
                        Type = CompletionType.History,
                        Score = 0.8
                    });
                }
            }
        }

        return completions.OrderByDescending(c => c.Score).Take(_config.MaxSuggestions).ToList();
    }

    private async Task<SearchSuggestions> GetAISuggestionsAsync(
        string query,
        string? userId,
        SearchContext? context,
        CancellationToken ct)
    {
        var suggestions = new SearchSuggestions { Query = query };

        var payload = new Dictionary<string, object>
        {
            ["query"] = query,
            ["maxSuggestions"] = _config.MaxSuggestions
        };

        if (userId != null)
            payload["userId"] = userId;

        if (context != null)
        {
            payload["context"] = new Dictionary<string, object>
            {
                ["currentPath"] = context.CurrentPath ?? string.Empty,
                ["filters"] = context.ActiveFilters ?? new Dictionary<string, string>(),
                ["recentFiles"] = context.RecentFiles ?? Array.Empty<string>()
            };
        }

        var response = await _messageBus!.RequestAsync(
            "intelligence.request.search-suggestions",
            payload,
            ct);

        if (response == null)
            return suggestions;

        // Parse completions
        if (response.TryGetValue("completions", out var completions) &&
            completions is IEnumerable<object> compList)
        {
            foreach (var comp in compList.OfType<Dictionary<string, object>>())
            {
                suggestions.Completions.Add(new SearchCompletion
                {
                    Text = comp.TryGetValue("text", out var t) ? t?.ToString() ?? string.Empty : string.Empty,
                    Type = ParseCompletionType(comp.TryGetValue("type", out var tp) ? tp?.ToString() : null),
                    Score = comp.TryGetValue("score", out var s) && s is double score ? score : 0.5,
                    Highlight = comp.TryGetValue("highlight", out var h) ? h?.ToString() : null
                });
            }
        }

        // Parse related queries
        if (response.TryGetValue("relatedQueries", out var related) &&
            related is IEnumerable<object> relList)
        {
            suggestions.RelatedQueries.AddRange(
                relList.Select(r => r?.ToString() ?? string.Empty).Where(r => !string.IsNullOrEmpty(r))
            );
        }

        // Parse categories
        if (response.TryGetValue("categories", out var categories) &&
            categories is IEnumerable<object> catList)
        {
            foreach (var cat in catList.OfType<Dictionary<string, object>>())
            {
                suggestions.Categories.Add(new SearchCategory
                {
                    Name = cat.TryGetValue("name", out var n) ? n?.ToString() ?? string.Empty : string.Empty,
                    Count = cat.TryGetValue("count", out var c) && c is int count ? count : 0,
                    Icon = cat.TryGetValue("icon", out var i) ? i?.ToString() : null
                });
            }
        }

        return suggestions;
    }

    private void MergeSuggestions(SearchSuggestions target, SearchSuggestions source)
    {
        // Merge completions, avoiding duplicates
        foreach (var comp in source.Completions)
        {
            if (!target.Completions.Any(c => c.Text == comp.Text))
            {
                target.Completions.Add(comp);
            }
        }

        // Re-sort by score
        target.Completions = target.Completions
            .OrderByDescending(c => c.Score)
            .Take(_config.MaxSuggestions)
            .ToList();

        // Merge related queries
        target.RelatedQueries.AddRange(source.RelatedQueries.Except(target.RelatedQueries));

        // Merge categories
        foreach (var cat in source.Categories)
        {
            if (!target.Categories.Any(c => c.Name == cat.Name))
            {
                target.Categories.Add(cat);
            }
        }
    }

    private void RecordQuery(string userId, string query)
    {
        var history = _userHistory.GetOrAdd(userId, _ => new List<string>());

        lock (history)
        {
            history.Remove(query); // Remove if exists
            history.Add(query);    // Add to end

            // Limit history size
            while (history.Count > _config.MaxHistoryPerUser)
            {
                history.RemoveAt(0);
            }
        }
    }

    private SearchIntentResult ClassifyIntentBasic(string query)
    {
        var lower = query.ToLowerInvariant();
        var intent = SearchIntent.General;
        var filters = new Dictionary<string, string>();

        if (lower.Contains("filename:") || lower.Contains("name:"))
            intent = SearchIntent.FileName;
        else if (lower.Contains("content:") || lower.Contains("contains:"))
            intent = SearchIntent.Content;
        else if (lower.Contains("metadata:") || lower.Contains("tag:"))
            intent = SearchIntent.Metadata;
        else if (lower.Contains("type:") || lower.Contains("ext:"))
        {
            intent = SearchIntent.FileType;
            var match = System.Text.RegularExpressions.Regex.Match(lower, @"(?:type:|ext:)\s*(\w+)");
            if (match.Success)
                filters["fileType"] = match.Groups[1].Value;
        }
        else if (lower.Contains("from:") || lower.Contains("after:") || lower.Contains("before:"))
            intent = SearchIntent.TimeBased;
        else if (lower.Contains("by:") || lower.Contains("owner:"))
            intent = SearchIntent.UserBased;

        return new SearchIntentResult
        {
            Intent = intent,
            Confidence = 0.7,
            SuggestedFilters = filters
        };
    }

    private SearchIntent MapAIIntent(string aiIntent)
    {
        return aiIntent.ToLowerInvariant() switch
        {
            "file_search" or "filename" => SearchIntent.FileName,
            "content_search" or "content" => SearchIntent.Content,
            "metadata_search" or "metadata" => SearchIntent.Metadata,
            "semantic_search" or "semantic" => SearchIntent.Semantic,
            "time_based" or "date" => SearchIntent.TimeBased,
            "user_based" or "owner" => SearchIntent.UserBased,
            "file_type" or "type" => SearchIntent.FileType,
            _ => SearchIntent.General
        };
    }

    private Dictionary<string, string> ExtractEntities(Dictionary<string, object> response)
    {
        var entities = new Dictionary<string, string>();

        if (response.TryGetValue("entities", out var entObj) &&
            entObj is Dictionary<string, object> entDict)
        {
            foreach (var kv in entDict)
            {
                entities[kv.Key] = kv.Value?.ToString() ?? string.Empty;
            }
        }

        return entities;
    }

    private Dictionary<string, string> ExtractFilters(Dictionary<string, object> response)
    {
        var filters = new Dictionary<string, string>();

        if (response.TryGetValue("suggestedFilters", out var filterObj) &&
            filterObj is Dictionary<string, object> filterDict)
        {
            foreach (var kv in filterDict)
            {
                filters[kv.Key] = kv.Value?.ToString() ?? string.Empty;
            }
        }

        return filters;
    }

    private CompletionType ParseCompletionType(string? type)
    {
        return type?.ToLowerInvariant() switch
        {
            "completion" => CompletionType.Completion,
            "suggestion" => CompletionType.Suggestion,
            "correction" => CompletionType.Correction,
            "history" => CompletionType.History,
            "prefix" => CompletionType.Prefix,
            _ => CompletionType.Suggestion
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sessions.Clear();
        _userHistory.Clear();
    }
}

/// <summary>
/// Search suggestions result.
/// </summary>
public sealed class SearchSuggestions
{
    /// <summary>Gets or sets the original query.</summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>Gets or sets the timestamp.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Gets or sets the completions.</summary>
    public List<SearchCompletion> Completions { get; set; } = new();

    /// <summary>Gets or sets related queries.</summary>
    public List<string> RelatedQueries { get; init; } = new();

    /// <summary>Gets or sets recent searches.</summary>
    public List<string> RecentSearches { get; init; } = new();

    /// <summary>Gets or sets search categories.</summary>
    public List<SearchCategory> Categories { get; init; } = new();
}

/// <summary>
/// Search completion suggestion.
/// </summary>
public sealed class SearchCompletion
{
    /// <summary>Gets or sets the completion text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Gets or sets the completion type.</summary>
    public CompletionType Type { get; init; }

    /// <summary>Gets or sets the relevance score.</summary>
    public double Score { get; init; }

    /// <summary>Gets or sets HTML highlight markup.</summary>
    public string? Highlight { get; init; }
}

/// <summary>
/// Completion type.
/// </summary>
public enum CompletionType
{
    /// <summary>Direct completion.</summary>
    Completion,

    /// <summary>Suggested query.</summary>
    Suggestion,

    /// <summary>Spelling correction.</summary>
    Correction,

    /// <summary>From history.</summary>
    History,

    /// <summary>Prefix match.</summary>
    Prefix
}

/// <summary>
/// Search category suggestion.
/// </summary>
public sealed class SearchCategory
{
    /// <summary>Gets or sets the category name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets or sets the result count.</summary>
    public int Count { get; init; }

    /// <summary>Gets or sets the icon name.</summary>
    public string? Icon { get; init; }
}

/// <summary>
/// Search intent classification result.
/// </summary>
public sealed class SearchIntentResult
{
    /// <summary>Gets or sets the detected intent.</summary>
    public SearchIntent Intent { get; init; }

    /// <summary>Gets or sets the confidence score.</summary>
    public double Confidence { get; init; }

    /// <summary>Gets or sets extracted entities.</summary>
    public Dictionary<string, string> Entities { get; init; } = new();

    /// <summary>Gets or sets suggested filters.</summary>
    public Dictionary<string, string> SuggestedFilters { get; init; } = new();
}

/// <summary>
/// Search intent types.
/// </summary>
public enum SearchIntent
{
    /// <summary>General search.</summary>
    General,

    /// <summary>File name search.</summary>
    FileName,

    /// <summary>Content search.</summary>
    Content,

    /// <summary>Metadata/tag search.</summary>
    Metadata,

    /// <summary>Semantic/meaning-based search.</summary>
    Semantic,

    /// <summary>Time/date-based search.</summary>
    TimeBased,

    /// <summary>User/owner-based search.</summary>
    UserBased,

    /// <summary>File type search.</summary>
    FileType
}

/// <summary>
/// Query expansion result.
/// </summary>
public sealed class QueryExpansionResult
{
    /// <summary>Gets or sets the original query.</summary>
    public string OriginalQuery { get; init; } = string.Empty;

    /// <summary>Gets or sets the expanded query.</summary>
    public string? ExpandedQuery { get; set; }

    /// <summary>Gets or sets expanded terms.</summary>
    public List<ExpandedTerm> ExpandedTerms { get; init; } = new();
}

/// <summary>
/// An expanded search term.
/// </summary>
public sealed class ExpandedTerm
{
    /// <summary>Gets or sets the term.</summary>
    public string Term { get; init; } = string.Empty;

    /// <summary>Gets or sets the expansion type (synonym, related, etc.).</summary>
    public string Type { get; init; } = "synonym";

    /// <summary>Gets or sets the relevance score.</summary>
    public double Relevance { get; init; }
}

/// <summary>
/// Spelling correction result.
/// </summary>
public sealed class SpellingCorrectionResult
{
    /// <summary>Gets or sets the original query.</summary>
    public string OriginalQuery { get; init; } = string.Empty;

    /// <summary>Gets or sets whether corrections were found.</summary>
    public bool HasCorrections { get; set; }

    /// <summary>Gets or sets the corrected query.</summary>
    public string? CorrectedQuery { get; set; }

    /// <summary>Gets or sets individual corrections.</summary>
    public List<SpellingCorrection> Corrections { get; set; } = new();
}

/// <summary>
/// Individual spelling correction.
/// </summary>
public sealed class SpellingCorrection
{
    /// <summary>Gets or sets the original text.</summary>
    public string Original { get; init; } = string.Empty;

    /// <summary>Gets or sets the suggestion.</summary>
    public string Suggestion { get; init; } = string.Empty;

    /// <summary>Gets or sets the position in the query.</summary>
    public int Position { get; init; }
}

/// <summary>
/// Facet suggestion for filtering.
/// </summary>
public sealed class FacetSuggestion
{
    /// <summary>Gets or sets the facet name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets or sets the display label.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Gets or sets possible values.</summary>
    public string[] Values { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Search context for suggestions.
/// </summary>
public sealed class SearchContext
{
    /// <summary>Gets or sets the current path.</summary>
    public string? CurrentPath { get; init; }

    /// <summary>Gets or sets active filters.</summary>
    public Dictionary<string, string>? ActiveFilters { get; init; }

    /// <summary>Gets or sets recently accessed files.</summary>
    public string[]? RecentFiles { get; init; }
}

/// <summary>
/// Smart search configuration.
/// </summary>
public sealed class SmartSearchConfig
{
    /// <summary>Maximum number of suggestions.</summary>
    public int MaxSuggestions { get; init; } = 10;

    /// <summary>Maximum query expansions.</summary>
    public int MaxQueryExpansions { get; init; } = 5;

    /// <summary>Maximum history per user.</summary>
    public int MaxHistoryPerUser { get; init; } = 50;

    /// <summary>Enable spell checking.</summary>
    public bool EnableSpellCheck { get; init; } = true;

    /// <summary>Enable query expansion.</summary>
    public bool EnableQueryExpansion { get; init; } = true;
}

/// <summary>
/// Internal search session.
/// </summary>
internal sealed class SearchSession
{
    public string SessionId { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public List<string> Queries { get; } = new();
}
