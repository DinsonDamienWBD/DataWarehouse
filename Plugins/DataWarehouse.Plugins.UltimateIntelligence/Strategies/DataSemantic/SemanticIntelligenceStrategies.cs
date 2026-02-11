using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.DataSemantic;

// ==================================================================================
// T146.B4: SEMANTIC INTELLIGENCE STRATEGIES
// Four production-ready strategies for semantic meaning extraction, contextual
// relevance scoring, domain knowledge integration, and cross-system semantic matching.
// ==================================================================================

#region B4.1: Semantic Meaning Extractor Strategy

/// <summary>
/// Extracts semantic meaning from data including entities, concepts, relationships, and domain context (T146.B4.1).
/// Uses regex-based entity extraction, keyword frequency analysis, and domain dictionary scoring
/// to produce rich semantic representations of textual data.
/// </summary>
/// <remarks>
/// Production-ready features:
/// - Regex-based entity extraction (capitalized word sequences)
/// - Concept extraction via repeated noun-like phrase detection
/// - Keyword frequency analysis with stopword removal
/// - Domain classification against financial, healthcare, technology, and legal dictionaries
/// - Semantic richness scoring based on vocabulary diversity and entity density
/// - Jaccard similarity for meaning comparison
/// </remarks>
internal sealed class SemanticMeaningExtractorStrategy : IntelligenceStrategyBase
{
    private readonly ConcurrentDictionary<string, SemanticMeaning> _meanings = new();

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "is", "are", "was", "were", "in", "on", "at",
        "to", "for", "of", "and", "or", "but", "not", "with", "this", "that"
    };

    private static readonly Dictionary<string, string[]> DomainDictionaries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["financial"] = new[] { "revenue", "profit", "margin", "equity", "asset", "liability" },
        ["healthcare"] = new[] { "patient", "diagnosis", "treatment", "clinical", "medical" },
        ["technology"] = new[] { "server", "database", "api", "deploy", "cloud", "container" },
        ["legal"] = new[] { "contract", "clause", "regulation", "compliance", "statute" }
    };

    /// <inheritdoc/>
    public override string StrategyId => "data-semantic-meaning-extractor";

    /// <inheritdoc/>
    public override string StrategyName => "Semantic Meaning Extractor";

    /// <inheritdoc/>
    public override IntelligenceStrategyCategory Category => IntelligenceStrategyCategory.Feature;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Semantic Meaning Extractor",
        Description = "Extracts semantic meaning from data including entities, concepts, relationships, and domain context",
        Capabilities = IntelligenceCapabilities.Classification | IntelligenceCapabilities.SemanticSearch,
        CostTier = 3,
        LatencyTier = 3,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "semantic", "meaning", "extraction", "nlp", "industry-first" },
        ConfigurationRequirements = Array.Empty<ConfigurationRequirement>()
    };

    /// <summary>
    /// Extracts semantic meaning from the given content, including entities, concepts,
    /// keywords, dominant domain, and a semantic richness score.
    /// </summary>
    /// <param name="dataId">Unique identifier for the data being analyzed.</param>
    /// <param name="content">The textual content to analyze.</param>
    /// <returns>The extracted <see cref="SemanticMeaning"/> cached for future retrieval.</returns>
    public SemanticMeaning ExtractMeaning(string dataId, string content)
    {
        var entities = ExtractEntities(content);
        var concepts = ExtractConcepts(content);
        var keywords = ExtractKeywords(content);
        var domainScores = ScoreDomains(content);
        var dominantDomain = domainScores.Count > 0
            ? domainScores.OrderByDescending(kv => kv.Value).First().Key
            : "general";
        var semanticRichness = CalculateSemanticRichness(content, entities, concepts);

        var meaning = new SemanticMeaning(
            DataId: dataId,
            Entities: entities,
            Concepts: concepts,
            Keywords: keywords,
            DominantDomain: dominantDomain,
            SemanticRichness: semanticRichness);

        _meanings[dataId] = meaning;
        return meaning;
    }

    /// <summary>
    /// Compares the semantic meanings of two previously extracted data items using
    /// Jaccard similarity over keyword and entity sets.
    /// </summary>
    /// <param name="dataId1">First data identifier.</param>
    /// <param name="dataId2">Second data identifier.</param>
    /// <returns>Average Jaccard similarity of keyword and entity sets (0.0 to 1.0).</returns>
    /// <exception cref="InvalidOperationException">If either data item has not been extracted.</exception>
    public double CompareMeanings(string dataId1, string dataId2)
    {
        if (!_meanings.TryGetValue(dataId1, out var meaning1))
            throw new InvalidOperationException($"No meaning extracted for '{dataId1}'. Call ExtractMeaning first.");
        if (!_meanings.TryGetValue(dataId2, out var meaning2))
            throw new InvalidOperationException($"No meaning extracted for '{dataId2}'. Call ExtractMeaning first.");

        var keywordSimilarity = JaccardSimilarity(
            new HashSet<string>(meaning1.Keywords, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(meaning2.Keywords, StringComparer.OrdinalIgnoreCase));

        var entitySimilarity = JaccardSimilarity(
            new HashSet<string>(meaning1.Entities, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(meaning2.Entities, StringComparer.OrdinalIgnoreCase));

        return (keywordSimilarity + entitySimilarity) / 2.0;
    }

    /// <summary>
    /// Retrieves a previously extracted semantic meaning by data identifier.
    /// </summary>
    /// <param name="dataId">The data identifier.</param>
    /// <returns>The cached <see cref="SemanticMeaning"/>, or null if not yet extracted.</returns>
    public SemanticMeaning? GetMeaning(string dataId)
    {
        return _meanings.TryGetValue(dataId, out var meaning) ? meaning : null;
    }

    private static IReadOnlyList<string> ExtractEntities(string content)
    {
        var matches = Regex.Matches(content, @"\b[A-Z][a-z]+(?:\s+[A-Z][a-z]+)*\b");
        return matches
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> ExtractConcepts(string content)
    {
        // Extract noun-like phrases: 2-3 word sequences that appear more than once
        var words = content.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var phraseCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < words.Length - 1; i++)
        {
            // 2-word phrases
            var biGram = $"{words[i]} {words[i + 1]}";
            phraseCounts[biGram] = phraseCounts.GetValueOrDefault(biGram) + 1;

            // 3-word phrases
            if (i < words.Length - 2)
            {
                var triGram = $"{words[i]} {words[i + 1]} {words[i + 2]}";
                phraseCounts[triGram] = phraseCounts.GetValueOrDefault(triGram) + 1;
            }
        }

        return phraseCounts
            .Where(kv => kv.Value > 1)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();
    }

    private static IReadOnlyList<string> ExtractKeywords(string content)
    {
        var words = content.Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'' },
            StringSplitOptions.RemoveEmptyEntries);

        var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var word in words)
        {
            var lower = word.ToLowerInvariant();
            if (Stopwords.Contains(lower) || lower.Length < 2)
                continue;
            freq[lower] = freq.GetValueOrDefault(lower) + 1;
        }

        return freq
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .Select(kv => kv.Key)
            .ToList();
    }

    private static Dictionary<string, double> ScoreDomains(string content)
    {
        var lowerContent = content.ToLowerInvariant();
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var (domain, terms) in DomainDictionaries)
        {
            var count = terms.Count(term => lowerContent.Contains(term));
            if (count > 0)
            {
                scores[domain] = (double)count / terms.Length;
            }
        }

        return scores;
    }

    private static double CalculateSemanticRichness(string content, IReadOnlyList<string> entities, IReadOnlyList<string> concepts)
    {
        var words = content.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var totalWordCount = words.Length;
        if (totalWordCount == 0)
            return 0.0;

        var uniqueWordCount = words.Select(w => w.ToLowerInvariant()).Distinct().Count();
        var vocabularyDiversity = (double)uniqueWordCount / totalWordCount;
        var entityConceptDensity = (double)(entities.Count + concepts.Count) / Math.Max(1.0, totalWordCount / 100.0);
        var richness = vocabularyDiversity * entityConceptDensity;

        return Math.Clamp(richness, 0.0, 1.0);
    }

    private static double JaccardSimilarity(HashSet<string> setA, HashSet<string> setB)
    {
        if (setA.Count == 0 && setB.Count == 0)
            return 1.0;

        var intersection = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();
        return union == 0 ? 0.0 : (double)intersection / union;
    }
}

/// <summary>
/// Represents extracted semantic meaning from a data item, including identified entities,
/// concepts, keywords, dominant domain classification, and a richness score.
/// </summary>
internal sealed record SemanticMeaning(
    string DataId,
    IReadOnlyList<string> Entities,
    IReadOnlyList<string> Concepts,
    IReadOnlyList<string> Keywords,
    string DominantDomain,
    double SemanticRichness);

/// <summary>
/// Intermediate result containing a semantic meaning and its domain classification scores.
/// </summary>
internal sealed record ExtractionResult(
    SemanticMeaning Meaning,
    Dictionary<string, double> DomainScores);

#endregion

#region B4.2: Contextual Relevance Strategy

/// <summary>
/// Scores contextual relevance of data using TF-IDF-inspired term weighting and query-document matching (T146.B4.2).
/// Maintains an inverted index of document term frequencies and computes relevance
/// scores using standard TF-IDF formulas with cosine similarity for cross-document comparison.
/// </summary>
/// <remarks>
/// Production-ready features:
/// - Document indexing with concurrent term frequency tracking
/// - TF-IDF scoring: tf = termFreq / totalTermsInDoc, idf = log(totalDocs / (1 + docFreq))
/// - Query-document relevance ranking with per-term score breakdown
/// - Cosine similarity between TF-IDF document vectors
/// - Thread-safe document count tracking via Interlocked.Increment
/// </remarks>
internal sealed class ContextualRelevanceStrategy : IntelligenceStrategyBase
{
    private readonly ConcurrentDictionary<string, Dictionary<string, int>> _documentTermFreqs = new();
    private readonly ConcurrentDictionary<string, int> _documentFreqs = new();
    private int _totalDocuments;

    /// <inheritdoc/>
    public override string StrategyId => "data-semantic-contextual-relevance";

    /// <inheritdoc/>
    public override string StrategyName => "Contextual Relevance Scorer";

    /// <inheritdoc/>
    public override IntelligenceStrategyCategory Category => IntelligenceStrategyCategory.Feature;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Contextual Relevance Scorer",
        Description = "Scores contextual relevance of data using TF-IDF-inspired term weighting and query-document matching",
        Capabilities = IntelligenceCapabilities.SemanticSearch,
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "relevance", "context", "scoring", "tf-idf", "industry-first" },
        ConfigurationRequirements = Array.Empty<ConfigurationRequirement>()
    };

    /// <summary>
    /// Indexes a document for future relevance queries. Tokenizes content, counts term
    /// frequencies, and updates global document frequency counts.
    /// </summary>
    /// <param name="docId">Unique identifier for the document.</param>
    /// <param name="content">The textual content to index.</param>
    public void IndexDocument(string docId, string content)
    {
        var tokens = Tokenize(content);
        var termFreqs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in tokens)
        {
            termFreqs[token] = termFreqs.GetValueOrDefault(token) + 1;
        }

        // Track which terms are new for this document (for document frequency updates)
        var previousTerms = _documentTermFreqs.TryGetValue(docId, out var prev)
            ? new HashSet<string>(prev.Keys, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var isNewDoc = !_documentTermFreqs.ContainsKey(docId);
        _documentTermFreqs[docId] = termFreqs;

        if (isNewDoc)
        {
            Interlocked.Increment(ref _totalDocuments);
        }

        // Update document frequency: add new terms, remove terms no longer present
        var currentTerms = new HashSet<string>(termFreqs.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (var term in currentTerms.Except(previousTerms))
        {
            _documentFreqs.AddOrUpdate(term, 1, (_, count) => count + 1);
        }

        foreach (var term in previousTerms.Except(currentTerms))
        {
            _documentFreqs.AddOrUpdate(term, 0, (_, count) => Math.Max(0, count - 1));
        }
    }

    /// <summary>
    /// Scores all indexed documents against the given query using TF-IDF weighting.
    /// Returns the top K most relevant documents sorted by descending score.
    /// </summary>
    /// <param name="query">The search query text.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <returns>A list of <see cref="RelevanceResult"/> sorted by descending relevance score.</returns>
    public List<RelevanceResult> ScoreRelevance(string query, int topK = 10)
    {
        var queryTokens = Tokenize(query);
        var totalDocs = Volatile.Read(ref _totalDocuments);
        var results = new List<RelevanceResult>();

        foreach (var (docId, termFreqs) in _documentTermFreqs)
        {
            var totalTermsInDoc = termFreqs.Values.Sum();
            if (totalTermsInDoc == 0) continue;

            var score = 0.0;
            var termScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (var token in queryTokens)
            {
                var tf = termFreqs.TryGetValue(token, out var freq)
                    ? (double)freq / totalTermsInDoc
                    : 0.0;

                var docFreq = _documentFreqs.TryGetValue(token, out var df) ? df : 0;
                var idf = Math.Log((double)totalDocs / (1 + docFreq));

                var tfidf = tf * idf;
                score += tfidf;

                if (tfidf > 0)
                {
                    termScores[token] = tfidf;
                }
            }

            if (score > 0)
            {
                results.Add(new RelevanceResult(docId, score, termScores));
            }
        }

        return results
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
    }

    /// <summary>
    /// Computes the cosine similarity between two indexed documents based on their TF-IDF vectors.
    /// </summary>
    /// <param name="docId1">First document identifier.</param>
    /// <param name="docId2">Second document identifier.</param>
    /// <returns>Cosine similarity score between 0.0 and 1.0.</returns>
    /// <exception cref="InvalidOperationException">If either document has not been indexed.</exception>
    public double GetContextualScore(string docId1, string docId2)
    {
        if (!_documentTermFreqs.TryGetValue(docId1, out var termFreqs1))
            throw new InvalidOperationException($"Document '{docId1}' has not been indexed.");
        if (!_documentTermFreqs.TryGetValue(docId2, out var termFreqs2))
            throw new InvalidOperationException($"Document '{docId2}' has not been indexed.");

        var totalDocs = Volatile.Read(ref _totalDocuments);
        var allTerms = new HashSet<string>(termFreqs1.Keys, StringComparer.OrdinalIgnoreCase);
        allTerms.UnionWith(termFreqs2.Keys);

        var totalTerms1 = termFreqs1.Values.Sum();
        var totalTerms2 = termFreqs2.Values.Sum();

        if (totalTerms1 == 0 || totalTerms2 == 0)
            return 0.0;

        var dotProduct = 0.0;
        var magnitude1 = 0.0;
        var magnitude2 = 0.0;

        foreach (var term in allTerms)
        {
            var docFreq = _documentFreqs.TryGetValue(term, out var df) ? df : 0;
            var idf = Math.Log((double)totalDocs / (1 + docFreq));

            var tf1 = termFreqs1.TryGetValue(term, out var freq1) ? (double)freq1 / totalTerms1 : 0.0;
            var tf2 = termFreqs2.TryGetValue(term, out var freq2) ? (double)freq2 / totalTerms2 : 0.0;

            var tfidf1 = tf1 * idf;
            var tfidf2 = tf2 * idf;

            dotProduct += tfidf1 * tfidf2;
            magnitude1 += tfidf1 * tfidf1;
            magnitude2 += tfidf2 * tfidf2;
        }

        var mag1 = Math.Sqrt(magnitude1);
        var mag2 = Math.Sqrt(magnitude2);

        if (mag1 == 0 || mag2 == 0)
            return 0.0;

        return dotProduct / (mag1 * mag2);
    }

    private static string[] Tokenize(string content)
    {
        return Regex.Split(content, @"[^a-zA-Z0-9]+")
            .Where(t => t.Length > 0)
            .Select(t => t.ToLowerInvariant())
            .ToArray();
    }
}

/// <summary>
/// Represents a document's relevance score against a query, including per-term TF-IDF breakdowns.
/// </summary>
internal sealed record RelevanceResult(
    string DocId,
    double Score,
    Dictionary<string, double> TermScores);

#endregion

#region B4.3: Domain Knowledge Integrator Strategy

/// <summary>
/// Integrates domain-specific glossaries, business rules, and ontologies to enrich data understanding (T146.B4.3).
/// Maintains registered domain glossaries and business rules, then scans content for term matches
/// using case-insensitive word boundary and substring matching with confidence scoring.
/// </summary>
/// <remarks>
/// Production-ready features:
/// - Domain glossary registration with term-definition pairs
/// - Business rule registration with domain association and priority ordering
/// - Content enrichment with glossary term matching (1.0 confidence for exact word boundary, 0.7 for substring)
/// - Applicable business rule detection via condition keyword scanning
/// - Domain inference based on glossary match counts
/// - Thread-safe concurrent dictionaries for all state
/// </remarks>
internal sealed class DomainKnowledgeIntegratorStrategy : IntelligenceStrategyBase
{
    private readonly ConcurrentDictionary<string, DomainGlossary> _glossaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<BusinessRule>> _rules = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override string StrategyId => "data-semantic-domain-integrator";

    /// <inheritdoc/>
    public override string StrategyName => "Domain Knowledge Integrator";

    /// <inheritdoc/>
    public override IntelligenceStrategyCategory Category => IntelligenceStrategyCategory.Feature;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Domain Knowledge Integrator",
        Description = "Integrates domain-specific glossaries, business rules, and ontologies to enrich data understanding",
        Capabilities = IntelligenceCapabilities.Classification | IntelligenceCapabilities.SemanticSearch,
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "domain-knowledge", "glossary", "ontology", "enrichment", "industry-first" },
        ConfigurationRequirements = Array.Empty<ConfigurationRequirement>()
    };

    /// <summary>
    /// Registers a domain glossary containing term-definition pairs for the specified domain.
    /// </summary>
    /// <param name="domain">The domain name (e.g., "financial", "healthcare").</param>
    /// <param name="terms">Dictionary of term-to-definition mappings.</param>
    public void RegisterGlossary(string domain, Dictionary<string, string> terms)
    {
        var glossaryTerms = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (term, definition) in terms)
        {
            glossaryTerms[term] = definition;
        }

        _glossaries[domain] = new DomainGlossary(domain, glossaryTerms);
    }

    /// <summary>
    /// Registers a business rule for the specified domain.
    /// </summary>
    /// <param name="domain">The domain the rule belongs to.</param>
    /// <param name="ruleId">Unique identifier for the rule.</param>
    /// <param name="condition">The condition expression describing when this rule applies.</param>
    /// <param name="action">The action to take when the rule's condition is met.</param>
    /// <param name="priority">Priority of the rule (higher values = higher priority).</param>
    public void RegisterRule(string domain, string ruleId, string condition, string action, int priority = 0)
    {
        var ruleList = _rules.GetOrAdd(domain, _ => new List<BusinessRule>());
        lock (ruleList)
        {
            ruleList.Add(new BusinessRule(ruleId, domain, condition, action, priority));
        }
    }

    /// <summary>
    /// Enriches the given data content by matching glossary terms, finding applicable business rules,
    /// and inferring the dominant domain based on match frequency.
    /// </summary>
    /// <param name="dataId">Unique identifier for the data being enriched.</param>
    /// <param name="content">The textual content to analyze against registered glossaries and rules.</param>
    /// <returns>An <see cref="EnrichmentResult"/> containing matches, applicable rules, and inferred domain.</returns>
    public EnrichmentResult EnrichData(string dataId, string content)
    {
        var allMatches = new List<GlossaryMatch>();
        var domainMatchCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (domain, glossary) in _glossaries)
        {
            var matchCount = 0;

            foreach (var (term, definition) in glossary.Terms)
            {
                // Check for exact word boundary match first
                var wordBoundaryPattern = $@"\b{Regex.Escape(term)}\b";
                if (Regex.IsMatch(content, wordBoundaryPattern, RegexOptions.IgnoreCase))
                {
                    allMatches.Add(new GlossaryMatch(term, definition, domain, 1.0));
                    matchCount++;
                }
                else if (content.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    // Substring match with lower confidence
                    allMatches.Add(new GlossaryMatch(term, definition, domain, 0.7));
                    matchCount++;
                }
            }

            if (matchCount > 0)
            {
                domainMatchCounts[domain] = matchCount;
            }
        }

        // Find applicable business rules
        var applicableRules = new List<BusinessRule>();
        var lowerContent = content.ToLowerInvariant();

        foreach (var (domain, ruleList) in _rules)
        {
            List<BusinessRule> snapshot;
            lock (ruleList)
            {
                snapshot = ruleList.ToList();
            }

            foreach (var rule in snapshot)
            {
                // Check if condition keywords appear in content
                var conditionWords = rule.Condition
                    .Split(new[] { ' ', ',', ';', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.ToLowerInvariant())
                    .Where(w => w.Length > 2);

                if (conditionWords.Any(word => lowerContent.Contains(word)))
                {
                    applicableRules.Add(rule);
                }
            }
        }

        // Infer domain from glossary match counts
        var inferredDomain = domainMatchCounts.Count > 0
            ? domainMatchCounts.OrderByDescending(kv => kv.Value).First().Key
            : "unknown";

        return new EnrichmentResult(
            DataId: dataId,
            Matches: allMatches,
            ApplicableRules: applicableRules.OrderByDescending(r => r.Priority).ToList(),
            InferredDomain: inferredDomain);
    }

    /// <summary>
    /// Retrieves all registered glossary terms for the specified domain.
    /// </summary>
    /// <param name="domain">The domain to look up.</param>
    /// <returns>A read-only dictionary of terms and definitions, or an empty dictionary if the domain is not registered.</returns>
    public IReadOnlyDictionary<string, string> GetDomainTerms(string domain)
    {
        if (_glossaries.TryGetValue(domain, out var glossary))
        {
            return glossary.Terms;
        }

        return new Dictionary<string, string>();
    }
}

/// <summary>
/// A domain glossary containing term-definition pairs for a specific domain.
/// </summary>
internal sealed record DomainGlossary(
    string DomainName,
    ConcurrentDictionary<string, string> Terms);

/// <summary>
/// A business rule associated with a domain, containing a condition and action.
/// </summary>
internal sealed record BusinessRule(
    string RuleId,
    string DomainName,
    string Condition,
    string Action,
    int Priority);

/// <summary>
/// Result of enriching data against registered domain glossaries and business rules.
/// </summary>
internal sealed record EnrichmentResult(
    string DataId,
    IReadOnlyList<GlossaryMatch> Matches,
    IReadOnlyList<BusinessRule> ApplicableRules,
    string InferredDomain);

/// <summary>
/// A match of a glossary term found within analyzed content.
/// </summary>
internal sealed record GlossaryMatch(
    string Term,
    string Definition,
    string Domain,
    double Confidence);

#endregion

#region B4.4: Cross-System Semantic Match Strategy

/// <summary>
/// Matches semantically equivalent fields across different systems using name normalization,
/// type compatibility, and sample value analysis (T146.B4.4).
/// Provides cross-system field mapping for data integration scenarios by comparing field names
/// after stripping common prefixes and formatting, assessing type compatibility, and analyzing
/// overlap in sample values.
/// </summary>
/// <remarks>
/// Production-ready features:
/// - System schema registration with field metadata and optional sample values
/// - Name normalization: lowercase, strip prefixes (fk_, pk_, col_, fld_), remove underscores/hyphens
/// - Exact normalized match (confidence 1.0), partial name containment (0.8), type+name similarity (0.6)
/// - Shared character ratio for name similarity assessment (character frequency comparison)
/// - Sample value overlap bonus (0.2 confidence boost when > 30% overlap)
/// - Configurable minimum confidence threshold for match filtering
/// </remarks>
internal sealed class CrossSystemSemanticMatchStrategy : IntelligenceStrategyBase
{
    private readonly ConcurrentDictionary<string, SystemSchema> _systems = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] CommonPrefixes = { "fk_", "pk_", "col_", "fld_" };

    /// <inheritdoc/>
    public override string StrategyId => "data-semantic-cross-system-match";

    /// <inheritdoc/>
    public override string StrategyName => "Cross-System Semantic Matcher";

    /// <inheritdoc/>
    public override IntelligenceStrategyCategory Category => IntelligenceStrategyCategory.Feature;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Cross-System Semantic Matcher",
        Description = "Matches semantically equivalent fields across different systems using name normalization, type compatibility, and sample value analysis",
        Capabilities = IntelligenceCapabilities.SemanticSearch | IntelligenceCapabilities.Classification,
        CostTier = 3,
        LatencyTier = 3,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "cross-system", "semantic-matching", "field-mapping", "integration", "industry-first" },
        ConfigurationRequirements = Array.Empty<ConfigurationRequirement>()
    };

    /// <summary>
    /// Registers a system's schema with its field definitions for cross-system matching.
    /// </summary>
    /// <param name="systemId">Unique identifier for the system.</param>
    /// <param name="fields">Dictionary of field name to <see cref="FieldInfo"/> mappings.</param>
    public void RegisterSystem(string systemId, Dictionary<string, FieldInfo> fields)
    {
        var fieldDict = new ConcurrentDictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, info) in fields)
        {
            fieldDict[name] = info;
        }

        _systems[systemId] = new SystemSchema(systemId, fieldDict);
    }

    /// <summary>
    /// Finds semantically matching fields between two registered systems by comparing
    /// normalized field names, data types, and sample value overlap.
    /// </summary>
    /// <param name="system1Id">First system identifier.</param>
    /// <param name="system2Id">Second system identifier.</param>
    /// <param name="minConfidence">Minimum confidence threshold for a match to be included (0.0 to 1.0).</param>
    /// <returns>A <see cref="MatchReport"/> containing all discovered field matches.</returns>
    /// <exception cref="InvalidOperationException">If either system has not been registered.</exception>
    public MatchReport FindMatches(string system1Id, string system2Id, double minConfidence = 0.5)
    {
        if (!_systems.TryGetValue(system1Id, out var system1))
            throw new InvalidOperationException($"System '{system1Id}' has not been registered.");
        if (!_systems.TryGetValue(system2Id, out var system2))
            throw new InvalidOperationException($"System '{system2Id}' has not been registered.");

        var matches = new List<SemanticFieldMatch>();
        var totalFieldsCompared = 0;

        foreach (var (field1Name, field1Info) in system1.Fields)
        {
            var norm1 = NormalizeName(field1Name);

            foreach (var (field2Name, field2Info) in system2.Fields)
            {
                totalFieldsCompared++;
                var norm2 = NormalizeName(field2Name);

                var confidence = 0.0;
                var matchReason = "";

                // 1. Exact normalized match
                if (string.Equals(norm1, norm2, StringComparison.OrdinalIgnoreCase))
                {
                    confidence = 1.0;
                    matchReason = "exact_name_match";
                }
                // 2. One name contains the other
                else if (norm1.Contains(norm2, StringComparison.OrdinalIgnoreCase) ||
                         norm2.Contains(norm1, StringComparison.OrdinalIgnoreCase))
                {
                    confidence = 0.8;
                    matchReason = "partial_name_match";
                }
                // 3. Same data type + similar name (shared character ratio > 0.6)
                else if (string.Equals(field1Info.DataType, field2Info.DataType, StringComparison.OrdinalIgnoreCase) &&
                         SharedCharRatio(norm1, norm2) > 0.6)
                {
                    confidence = 0.6;
                    matchReason = "type_and_name_similarity";
                }

                // 4. Sample value overlap bonus
                if (confidence > 0 &&
                    field1Info.SampleValues is { Length: > 0 } &&
                    field2Info.SampleValues is { Length: > 0 })
                {
                    var set1 = new HashSet<string>(field1Info.SampleValues, StringComparer.OrdinalIgnoreCase);
                    var set2 = new HashSet<string>(field2Info.SampleValues, StringComparer.OrdinalIgnoreCase);
                    var intersection = set1.Intersect(set2).Count();
                    var union = set1.Union(set2).Count();

                    if (union > 0 && (double)intersection / union > 0.3)
                    {
                        confidence += 0.2;
                        matchReason += "+value_overlap";
                    }
                }

                if (confidence >= minConfidence)
                {
                    matches.Add(new SemanticFieldMatch(
                        System1Id: system1Id,
                        Field1Name: field1Name,
                        System2Id: system2Id,
                        Field2Name: field2Name,
                        Confidence: confidence,
                        MatchReason: matchReason));
                }
            }
        }

        return new MatchReport(
            Matches: matches,
            TotalFieldsCompared: totalFieldsCompared,
            MatchesFound: matches.Count);
    }

    private static string NormalizeName(string name)
    {
        var normalized = name.ToLowerInvariant();

        // Strip common prefixes
        foreach (var prefix in CommonPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                normalized = normalized[prefix.Length..];
                break;
            }
        }

        // Remove underscores and hyphens
        normalized = normalized.Replace("_", "").Replace("-", "");

        return normalized;
    }

    private static double SharedCharRatio(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;

        // Count character frequencies
        var freqA = new Dictionary<char, int>();
        foreach (var c in a.ToLowerInvariant())
        {
            freqA[c] = freqA.GetValueOrDefault(c) + 1;
        }

        var freqB = new Dictionary<char, int>();
        foreach (var c in b.ToLowerInvariant())
        {
            freqB[c] = freqB.GetValueOrDefault(c) + 1;
        }

        // Count shared characters (minimum of each character's frequency)
        var sharedCount = 0;
        foreach (var (ch, countA) in freqA)
        {
            if (freqB.TryGetValue(ch, out var countB))
            {
                sharedCount += Math.Min(countA, countB);
            }
        }

        return (double)sharedCount / Math.Max(a.Length, b.Length);
    }
}

/// <summary>
/// Represents a system's schema containing its registered fields.
/// </summary>
internal sealed record SystemSchema(
    string SystemId,
    ConcurrentDictionary<string, FieldInfo> Fields);

/// <summary>
/// Information about a field in a system schema, including its name, data type, optional description,
/// and optional sample values for value-based matching.
/// </summary>
internal sealed record FieldInfo(
    string FieldName,
    string DataType,
    string? Description,
    string[]? SampleValues);

/// <summary>
/// Represents a semantic match between fields from two different systems.
/// </summary>
internal sealed record SemanticFieldMatch(
    string System1Id,
    string Field1Name,
    string System2Id,
    string Field2Name,
    double Confidence,
    string MatchReason);

/// <summary>
/// Report of all semantic field matches found between two systems.
/// </summary>
internal sealed record MatchReport(
    IReadOnlyList<SemanticFieldMatch> Matches,
    int TotalFieldsCompared,
    int MatchesFound);

#endregion
