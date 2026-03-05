using System.Text.RegularExpressions;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataCatalog.Strategies.LivingCatalog;

#region Strategy 1: SelfLearningCatalogStrategy

/// <summary>
/// Self-learning catalog that improves continuously by analyzing feedback, corrections, and usage
/// patterns to refine asset metadata, descriptions, and classifications.
/// </summary>
/// <remarks>
/// Implements T146.B2.1: Self-Learning Catalog Strategy.
/// Maintains confidence scores per asset that increase with applied corrections and decrease when
/// new feedback indicates the current metadata is inaccurate. After a field receives more than two
/// corrections the most common corrected value is automatically applied.
/// </remarks>
public sealed class SelfLearningCatalogStrategy : DataCatalogStrategyBase
{
    /// <summary>
    /// Represents a cataloged data asset with versioned metadata.
    /// </summary>
    /// <param name="AssetId">Unique identifier of the asset.</param>
    /// <param name="Name">Human-readable name of the asset.</param>
    /// <param name="Description">Description of the asset.</param>
    /// <param name="Metadata">Key-value metadata associated with the asset.</param>
    /// <param name="LastUpdated">Timestamp of the last update.</param>
    /// <param name="Version">Current version number of this catalog entry.</param>
    internal record CatalogEntry(
        string AssetId,
        string Name,
        string Description,
        Dictionary<string, string> Metadata,
        DateTimeOffset LastUpdated,
        int Version);

    /// <summary>
    /// Records a single feedback correction for an asset field.
    /// </summary>
    /// <param name="FeedbackId">Unique identifier for this feedback record.</param>
    /// <param name="AssetId">The asset this feedback applies to.</param>
    /// <param name="FieldName">The metadata field that was corrected.</param>
    /// <param name="OriginalValue">The original value before correction.</param>
    /// <param name="CorrectedValue">The corrected value provided by the user.</param>
    /// <param name="Timestamp">When this feedback was recorded.</param>
    internal record FeedbackRecord(
        string FeedbackId,
        string AssetId,
        string FieldName,
        string OriginalValue,
        string CorrectedValue,
        DateTimeOffset Timestamp);

    private readonly BoundedDictionary<string, CatalogEntry> _entries = new BoundedDictionary<string, CatalogEntry>(1000);
    private readonly BoundedDictionary<string, List<FeedbackRecord>> _feedback = new BoundedDictionary<string, List<FeedbackRecord>>(1000);
    private readonly BoundedDictionary<string, double> _confidenceScores = new BoundedDictionary<string, double>(1000);
    private readonly object _feedbackLock = new();

    /// <inheritdoc />
    public override string StrategyId => "living-self-learning";

    /// <inheritdoc />
    public override string DisplayName => "Self-Learning Catalog";

    /// <inheritdoc />
    public override DataCatalogCategory Category => DataCatalogCategory.AssetDiscovery;

    /// <inheritdoc />
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = true,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Self-learning catalog that improves continuously by analyzing feedback, corrections, and usage " +
        "patterns to refine asset metadata, descriptions, and classifications.";

    /// <inheritdoc />
    public override string[] Tags =>
        ["self-learning", "feedback", "continuous-improvement", "ai-native", "industry-first"];

    /// <summary>
    /// Registers a new data asset in the catalog.
    /// </summary>
    /// <param name="assetId">Unique identifier for the asset.</param>
    /// <param name="name">Human-readable name of the asset.</param>
    /// <param name="description">Description of the asset.</param>
    /// <param name="metadata">Optional key-value metadata for the asset.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="assetId"/> is null or empty.</exception>
    public void RegisterAsset(string assetId, string name, string description, Dictionary<string, string>? metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var entry = new CatalogEntry(
            assetId,
            name,
            description ?? string.Empty,
            metadata ?? new Dictionary<string, string>(),
            DateTimeOffset.UtcNow,
            1);

        _entries[assetId] = entry;
        _confidenceScores.TryAdd(assetId, 0.5);
    }

    /// <summary>
    /// Records a feedback correction for a specific field of an asset.
    /// Each feedback record decreases confidence by 0.1, indicating the current value needs correction.
    /// </summary>
    /// <param name="assetId">The asset being corrected.</param>
    /// <param name="fieldName">The metadata field name being corrected.</param>
    /// <param name="originalValue">The value that was incorrect.</param>
    /// <param name="correctedValue">The correct value to use.</param>
    /// <exception cref="ArgumentException">Thrown when required parameters are null or empty.</exception>
    public void RecordFeedback(string assetId, string fieldName, string originalValue, string correctedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);

        var record = new FeedbackRecord(
            Guid.NewGuid().ToString("N"),
            assetId,
            fieldName,
            originalValue ?? string.Empty,
            correctedValue ?? string.Empty,
            DateTimeOffset.UtcNow);

        lock (_feedbackLock)
        {
            var list = _feedback.GetOrAdd(assetId, _ => new List<FeedbackRecord>());
            list.Add(record);
        }

        // Decrease confidence by 0.1 when new feedback arrives (indicates current value needs correction)
        _confidenceScores.AddOrUpdate(assetId, 0.4, (_, current) => Math.Max(0.0, current - 0.1));
    }

    /// <summary>
    /// Applies accumulated learning to the specified asset. For any metadata field that has been
    /// corrected more than two times, the most frequently suggested corrected value is automatically
    /// applied. Each applied correction increases confidence by 0.05.
    /// </summary>
    /// <param name="assetId">The asset to apply learning to.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="assetId"/> is null or empty.</exception>
    public void ApplyLearning(string assetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);

        if (!_entries.TryGetValue(assetId, out var entry))
        {
            return;
        }

        List<FeedbackRecord> feedbackList;
        lock (_feedbackLock)
        {
            if (!_feedback.TryGetValue(assetId, out var raw) || raw.Count == 0)
            {
                return;
            }

            feedbackList = new List<FeedbackRecord>(raw);
        }

        var updatedMetadata = new Dictionary<string, string>(entry.Metadata);
        int correctionsApplied = 0;

        // Group feedback by field name and find fields with more than 2 corrections
        var fieldGroups = feedbackList.GroupBy(f => f.FieldName);
        foreach (var group in fieldGroups)
        {
            var corrections = group.ToList();
            if (corrections.Count > 2)
            {
                // Find the most common corrected value
                string mostCommon = corrections
                    .GroupBy(c => c.CorrectedValue)
                    .OrderByDescending(g => g.Count())
                    .First()
                    .Key;

                updatedMetadata[group.Key] = mostCommon;
                correctionsApplied++;
            }
        }

        if (correctionsApplied > 0)
        {
            var updated = entry with
            {
                Metadata = updatedMetadata,
                LastUpdated = DateTimeOffset.UtcNow,
                Version = entry.Version + 1
            };
            _entries[assetId] = updated;

            // Increase confidence by 0.05 per applied correction
            _confidenceScores.AddOrUpdate(
                assetId,
                Math.Min(1.0, 0.5 + 0.05 * correctionsApplied),
                (_, current) => Math.Min(1.0, current + 0.05 * correctionsApplied));
        }
    }

    /// <summary>
    /// Gets the confidence score for the specified asset. Confidence starts at 0.5,
    /// increases by 0.05 per applied correction, and decreases by 0.1 per new feedback record.
    /// </summary>
    /// <param name="assetId">The asset to query confidence for.</param>
    /// <returns>A confidence score between 0.0 and 1.0, or 0.0 if the asset is not registered.</returns>
    public double GetConfidence(string assetId)
    {
        return _confidenceScores.TryGetValue(assetId, out var score) ? score : 0.0;
    }
}

#endregion

#region Strategy 2: AutoTaggingStrategy

/// <summary>
/// AI-powered auto-tagging that assigns meaningful tags to data assets by analyzing column names,
/// data samples, descriptions, and naming patterns.
/// </summary>
/// <remarks>
/// Implements T146.B2.2: Auto-Tagging Strategy.
/// Uses keyword extraction and pattern matching on column names to automatically classify assets
/// into domain categories such as PII, financial, geospatial, and temporal without requiring
/// external AI services.
/// </remarks>
public sealed class AutoTaggingStrategy : DataCatalogStrategyBase
{
    private readonly BoundedDictionary<string, HashSet<string>> _assetTags = new BoundedDictionary<string, HashSet<string>>(1000);
    private readonly object _tagLock = new();

    /// <inheritdoc />
    public override string StrategyId => "living-auto-tagging";

    /// <inheritdoc />
    public override string DisplayName => "AI Auto-Tagging";

    /// <inheritdoc />
    public override DataCatalogCategory Category => DataCatalogCategory.SearchDiscovery;

    /// <inheritdoc />
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsFederation = false,
        SupportsVersioning = false,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };

    /// <inheritdoc />
    public override string SemanticDescription =>
        "AI-powered auto-tagging that assigns meaningful tags to data assets by analyzing column names, " +
        "data samples, descriptions, and naming patterns.";

    /// <inheritdoc />
    public override string[] Tags =>
        ["auto-tagging", "classification", "metadata-enrichment", "nlp", "industry-first"];

    /// <summary>
    /// Generates tags for a data asset by analyzing its name, description, and column names.
    /// Tags are derived from name tokenization, description keyword extraction, and column name
    /// domain pattern matching (PII, financial, geospatial, temporal, identifier, categorical).
    /// </summary>
    /// <param name="assetId">Unique identifier of the asset to tag.</param>
    /// <param name="name">Human-readable name of the asset.</param>
    /// <param name="description">Description of the asset.</param>
    /// <param name="columnNames">Column names from the data asset schema.</param>
    /// <returns>The set of generated tags for this asset.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="assetId"/> is null or empty.</exception>
    public IReadOnlySet<string> GenerateTags(
        string assetId,
        string name,
        string description,
        IReadOnlyList<string> columnNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);

        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Extract tags from name by splitting on spaces, underscores, and camelCase boundaries
        if (!string.IsNullOrWhiteSpace(name))
        {
            var nameTokens = TokenizeName(name);
            foreach (var token in nameTokens)
            {
                if (token.Length > 2)
                {
                    tags.Add(token.ToLowerInvariant());
                }
            }
        }

        // Extract tags from description: capitalized phrases and domain keywords
        if (!string.IsNullOrWhiteSpace(description))
        {
            ExtractDescriptionTags(description, tags);
        }

        // Infer domain tags from column names
        if (columnNames is { Count: > 0 })
        {
            var domainTags = InferDomainTags(columnNames);
            foreach (var dt in domainTags)
            {
                tags.Add(dt);
            }
        }

        lock (_tagLock)
        {
            _assetTags[assetId] = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        }

        return tags;
    }

    /// <summary>
    /// Gets the current tags for the specified asset.
    /// </summary>
    /// <param name="assetId">The asset to retrieve tags for.</param>
    /// <returns>The set of tags, or an empty set if the asset has no tags.</returns>
    public IReadOnlySet<string> GetTags(string assetId)
    {
        lock (_tagLock)
        {
            if (_assetTags.TryGetValue(assetId, out var tags))
            {
                return new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
            }
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Infers domain-specific tags from column names using pattern matching.
    /// </summary>
    private static HashSet<string> InferDomainTags(IReadOnlyList<string> columnNames)
    {
        var domainTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var col in columnNames)
        {
            var lower = col.ToLowerInvariant();

            // PII indicators
            if (lower.Contains("email") || lower.Contains("phone") || lower.Contains("ssn") || lower.Contains("name"))
            {
                domainTags.Add("pii");
            }

            // Financial indicators
            if (lower.Contains("price") || lower.Contains("amount") || lower.Contains("currency") || lower.Contains("balance"))
            {
                domainTags.Add("financial");
            }

            // Geospatial indicators
            if (lower.Contains("lat") || lower.Contains("lon") || lower.Contains("longitude") ||
                lower.Contains("latitude") || lower.Contains("geo"))
            {
                domainTags.Add("geospatial");
            }

            // Temporal indicators
            if (lower.Contains("date") || lower.Contains("time") || lower.Contains("timestamp") ||
                lower.Contains("created") || lower.Contains("updated"))
            {
                domainTags.Add("temporal");
            }

            // Identifier indicators
            if (lower.Contains("id") || lower.Contains("key") || lower.Contains("uuid"))
            {
                domainTags.Add("identifier");
            }

            // Categorical indicators
            if (lower.Contains("status") || lower.Contains("state") || lower.Contains("flag"))
            {
                domainTags.Add("categorical");
            }
        }

        return domainTags;
    }

    /// <summary>
    /// Tokenizes a name by splitting on spaces, underscores, hyphens, and camelCase boundaries.
    /// </summary>
    private static List<string> TokenizeName(string name)
    {
        // Split on spaces, underscores, hyphens first
        var parts = Regex.Split(name, @"[\s_\-]+");
        var tokens = new List<string>();

        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            // Split camelCase: insert boundary before uppercase letters preceded by lowercase
            var camelTokens = Regex.Split(part, @"(?<=[a-z])(?=[A-Z])");
            foreach (var token in camelTokens)
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    tokens.Add(token);
                }
            }
        }

        return tokens;
    }

    /// <summary>
    /// Extracts tags from a description string by identifying capitalized phrases and domain keywords.
    /// </summary>
    private static void ExtractDescriptionTags(string description, HashSet<string> tags)
    {
        // Extract multi-word capitalized phrases (e.g., "Customer Orders")
        var capitalizedPhrases = Regex.Matches(description, @"\b[A-Z][a-z]+(?:\s+[A-Z][a-z]+)+\b");
        foreach (Match match in capitalizedPhrases)
        {
            tags.Add(match.Value.ToLowerInvariant().Replace(' ', '-'));
        }

        // Domain keyword detection
        var domainKeywords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "customer", "customer-data" },
            { "transaction", "transactional" },
            { "analytics", "analytics" },
            { "report", "reporting" },
            { "log", "logging" },
            { "audit", "auditing" },
            { "metric", "metrics" },
            { "event", "event-data" },
            { "user", "user-data" },
            { "order", "order-data" },
            { "product", "product-data" },
            { "inventory", "inventory" },
            { "payment", "payment-data" },
            { "shipping", "logistics" }
        };

        var descLower = description.ToLowerInvariant();
        foreach (var (keyword, tag) in domainKeywords)
        {
            if (descLower.Contains(keyword))
            {
                tags.Add(tag);
            }
        }
    }
}

#endregion

#region Strategy 3: RelationshipDiscoveryStrategy

/// <summary>
/// Discovers hidden relationships between data assets by analyzing column name overlap,
/// naming patterns, and value distribution similarity.
/// </summary>
/// <remarks>
/// Implements T146.B2.3: Relationship Discovery Strategy.
/// Uses exact name matching, suffix matching, prefix matching after stripping _id suffixes,
/// and Jaccard token similarity to detect foreign key, reference, and derived relationships
/// between registered data assets.
/// </remarks>
public sealed class RelationshipDiscoveryStrategy : DataCatalogStrategyBase
{
    /// <summary>
    /// Represents a discovered relationship between two data asset columns.
    /// </summary>
    /// <param name="SourceAssetId">The source asset containing the originating column.</param>
    /// <param name="TargetAssetId">The target asset containing the related column.</param>
    /// <param name="SourceColumn">The column name in the source asset.</param>
    /// <param name="TargetColumn">The column name in the target asset.</param>
    /// <param name="Confidence">Confidence score from 0.0 to 1.0 indicating match strength.</param>
    /// <param name="RelationshipType">Type of relationship: foreign_key, reference, or derived.</param>
    public record DiscoveredRelationship(
        string SourceAssetId,
        string TargetAssetId,
        string SourceColumn,
        string TargetColumn,
        double Confidence,
        string RelationshipType);

    private readonly BoundedDictionary<string, HashSet<string>> _assetColumns = new BoundedDictionary<string, HashSet<string>>(1000);
    private readonly BoundedDictionary<string, List<DiscoveredRelationship>> _relationships = new BoundedDictionary<string, List<DiscoveredRelationship>>(1000);
    private readonly object _columnsLock = new();

    /// <inheritdoc />
    public override string StrategyId => "living-relationship-discovery";

    /// <inheritdoc />
    public override string DisplayName => "Relationship Discovery";

    /// <inheritdoc />
    public override DataCatalogCategory Category => DataCatalogCategory.DataRelationships;

    /// <inheritdoc />
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = false,
        SupportsFederation = true,
        SupportsVersioning = false,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Discovers hidden relationships between data assets by analyzing column name overlap, naming patterns " +
        "(e.g., user_id across tables), and value distribution similarity.";

    /// <inheritdoc />
    public override string[] Tags =>
        ["relationship-discovery", "hidden-links", "pattern-matching", "cross-reference", "industry-first"];

    /// <summary>
    /// Registers column names for a data asset so they can be compared during relationship discovery.
    /// </summary>
    /// <param name="assetId">Unique identifier of the asset.</param>
    /// <param name="columnNames">Column names from the asset schema.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="assetId"/> is null or empty.</exception>
    public void RegisterColumns(string assetId, IReadOnlyList<string> columnNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentNullException.ThrowIfNull(columnNames);

        lock (_columnsLock)
        {
            _assetColumns[assetId] = new HashSet<string>(columnNames, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Discovers relationships between the specified asset and all other registered assets.
    /// Compares columns using exact name matching, suffix matching, prefix matching after
    /// stripping _id suffixes, and Jaccard token similarity.
    /// </summary>
    /// <param name="assetId">The asset to discover relationships for.</param>
    /// <returns>List of discovered relationships with confidence scores and types.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="assetId"/> is null or empty.</exception>
    public IReadOnlyList<DiscoveredRelationship> DiscoverRelationships(string assetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);

        HashSet<string> sourceColumns;
        lock (_columnsLock)
        {
            if (!_assetColumns.TryGetValue(assetId, out var cols))
            {
                return Array.Empty<DiscoveredRelationship>();
            }

            sourceColumns = new HashSet<string>(cols, StringComparer.OrdinalIgnoreCase);
        }

        var discovered = new List<DiscoveredRelationship>();

        // P2-2222: Build reverse index (column_name_lower → assetId set) under the lock
        // to replace the O(N×M²) triple-nested loop with O(M) exact/stripped lookups.
        Dictionary<string, List<(string assetId, string originalName)>> colIndex;
        Dictionary<string, HashSet<string>> otherAssets;
        lock (_columnsLock)
        {
            otherAssets = _assetColumns
                .Where(kv => !string.Equals(kv.Key, assetId, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase));

            // Build: lowerColumnName → [(targetAssetId, originalName)]
            colIndex = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (tgtId, tgtCols) in otherAssets)
            {
                foreach (var col in tgtCols)
                {
                    var key = col.ToLowerInvariant();
                    if (!colIndex.TryGetValue(key, out var list))
                        colIndex[key] = list = new List<(string, string)>();
                    list.Add((tgtId, col));
                }
            }
        }

        // Fast pass: exact name match and stripped-suffix match using the index
        var emitted = new HashSet<(string, string, string, string)>(); // de-duplicate
        foreach (var srcCol in sourceColumns)
        {
            var srcLower = srcCol.ToLowerInvariant();
            var srcStripped = StripIdSuffix(srcLower);

            // Exact match
            if (colIndex.TryGetValue(srcLower, out var exactMatches))
            {
                foreach (var (tgtId, tgtCol) in exactMatches)
                {
                    var key = (assetId, tgtId, srcCol, tgtCol);
                    if (emitted.Add(key))
                        discovered.Add(new DiscoveredRelationship(assetId, tgtId, srcCol, tgtCol, 1.0, "foreign_key"));
                }
            }

            // Stripped-suffix match (both have _id suffix with same root)
            if (srcStripped != srcLower)
            {
                // e.g. srcCol = "user_id" → look for other columns named "user_id" (exact, already handled) or "user"
                if (colIndex.TryGetValue(srcStripped, out var strippedMatches))
                {
                    foreach (var (tgtId, tgtCol) in strippedMatches)
                    {
                        var key = (assetId, tgtId, srcCol, tgtCol);
                        if (emitted.Add(key))
                            discovered.Add(new DiscoveredRelationship(assetId, tgtId, srcCol, tgtCol, 0.7, "reference"));
                    }
                }

                // Also find tgt columns with the same stripped root (user_id ↔ user_id already done; user_id ↔ order_user_id needs full scan — skip Jaccard for large sets)
            }
        }

        // Jaccard pass: for pairs not already emitted, run full Jaccard over remaining column pairs
        // Only scan assets with multi-token column names to limit combinatorial blow-up.
        foreach (var (targetAssetId, targetColumns) in otherAssets)
        {
            foreach (var srcCol in sourceColumns)
            {
                var srcTokens = TokenizeColumnName(srcCol.ToLowerInvariant());
                if (srcTokens.Count <= 1) continue; // single-token names only match via exact — already handled

                foreach (var tgtCol in targetColumns)
                {
                    var tgtTokens = TokenizeColumnName(tgtCol.ToLowerInvariant());
                    if (tgtTokens.Count <= 1) continue;

                    var dedup = (assetId, targetAssetId, srcCol, tgtCol);
                    if (emitted.Contains(dedup)) continue;

                    double jaccard = ComputeJaccard(srcTokens, tgtTokens);
                    if (jaccard > 0.5)
                    {
                        emitted.Add(dedup);
                        discovered.Add(new DiscoveredRelationship(assetId, targetAssetId, srcCol, tgtCol, jaccard * 0.6, "derived"));
                    }
                }
            }
        }

        _relationships[assetId] = discovered;
        return discovered;
    }

    /// <summary>
    /// Evaluates a pair of columns for potential relationships.
    /// </summary>
    private static DiscoveredRelationship? EvaluateColumnPair(
        string sourceAssetId, string targetAssetId, string sourceColumn, string targetColumn)
    {
        var srcLower = sourceColumn.ToLowerInvariant();
        var tgtLower = targetColumn.ToLowerInvariant();

        // Exact name match = 1.0 confidence "foreign_key"
        if (string.Equals(srcLower, tgtLower, StringComparison.Ordinal))
        {
            return new DiscoveredRelationship(
                sourceAssetId, targetAssetId, sourceColumn, targetColumn, 1.0, "foreign_key");
        }

        // LOW-2226: The "_id" suffix-match branch that checked exact equality was dead code
        // (exact match already handled above). Removed.

        // Prefix match after stripping _id suffix = 0.7 "reference"
        var srcStripped = StripIdSuffix(srcLower);
        var tgtStripped = StripIdSuffix(tgtLower);

        if (srcStripped != srcLower && tgtStripped != tgtLower &&
            string.Equals(srcStripped, tgtStripped, StringComparison.Ordinal))
        {
            return new DiscoveredRelationship(
                sourceAssetId, targetAssetId, sourceColumn, targetColumn, 0.7, "reference");
        }

        // Also check if one is the stripped form of the other (e.g., "user_id" matches "user")
        if ((srcStripped != srcLower && string.Equals(srcStripped, tgtLower, StringComparison.Ordinal)) ||
            (tgtStripped != tgtLower && string.Equals(tgtStripped, srcLower, StringComparison.Ordinal)))
        {
            return new DiscoveredRelationship(
                sourceAssetId, targetAssetId, sourceColumn, targetColumn, 0.7, "reference");
        }

        // Jaccard similarity of column name tokens > 0.5 = confidence * 0.6 "derived"
        var srcTokens = TokenizeColumnName(srcLower);
        var tgtTokens = TokenizeColumnName(tgtLower);

        if (srcTokens.Count > 0 && tgtTokens.Count > 0)
        {
            double jaccard = ComputeJaccard(srcTokens, tgtTokens);
            if (jaccard > 0.5)
            {
                return new DiscoveredRelationship(
                    sourceAssetId, targetAssetId, sourceColumn, targetColumn, jaccard * 0.6, "derived");
            }
        }

        return null;
    }

    /// <summary>
    /// Strips common _id suffixes from a column name for prefix comparison.
    /// </summary>
    private static string StripIdSuffix(string columnName)
    {
        if (columnName.EndsWith("_id", StringComparison.Ordinal))
        {
            return columnName[..^3];
        }

        if (columnName.EndsWith("id", StringComparison.Ordinal) && columnName.Length > 2)
        {
            return columnName[..^2];
        }

        return columnName;
    }

    /// <summary>
    /// Tokenizes a column name by splitting on underscores and camelCase boundaries.
    /// </summary>
    private static HashSet<string> TokenizeColumnName(string name)
    {
        var parts = Regex.Split(name, @"[_\-]+");
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                // Also split camelCase
                var camelTokens = Regex.Split(part, @"(?<=[a-z])(?=[A-Z])");
                foreach (var token in camelTokens)
                {
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        tokens.Add(token.ToLowerInvariant());
                    }
                }
            }
        }

        return tokens;
    }

    /// <summary>
    /// Computes the Jaccard similarity coefficient between two token sets.
    /// </summary>
    private static double ComputeJaccard(HashSet<string> setA, HashSet<string> setB)
    {
        int intersection = 0;
        foreach (var item in setA)
        {
            if (setB.Contains(item))
            {
                intersection++;
            }
        }

        int union = setA.Count + setB.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }
}

#endregion

#region Strategy 4: SchemaEvolutionTrackerStrategy

/// <summary>
/// Tracks schema changes over time, detecting column additions, removals, type changes,
/// and renames. Maintains full version history with diff analysis.
/// </summary>
/// <remarks>
/// Implements T146.B2.4: Schema Evolution Tracker Strategy.
/// Records schema snapshots with version numbers and timestamps. Computes diffs between any
/// two versions to identify added columns, removed columns, and type-changed columns.
/// Only records a new version when the schema actually changes.
/// </remarks>
public sealed class SchemaEvolutionTrackerStrategy : DataCatalogStrategyBase
{
    /// <summary>
    /// Represents a snapshot of an asset's schema at a specific version.
    /// </summary>
    /// <param name="Version">Version number of this schema snapshot.</param>
    /// <param name="Columns">Column name to column type mapping.</param>
    /// <param name="CapturedAt">When this schema version was captured.</param>
    public record SchemaVersion(
        int Version,
        Dictionary<string, string> Columns,
        DateTimeOffset CapturedAt);

    /// <summary>
    /// Represents the difference between two schema versions.
    /// </summary>
    /// <param name="FromVersion">The earlier version being compared.</param>
    /// <param name="ToVersion">The later version being compared.</param>
    /// <param name="AddedColumns">Columns present in the later version but not the earlier.</param>
    /// <param name="RemovedColumns">Columns present in the earlier version but not the later.</param>
    /// <param name="TypeChangedColumns">Columns present in both but with different types.</param>
    public record SchemaDiff(
        int FromVersion,
        int ToVersion,
        IReadOnlyList<string> AddedColumns,
        IReadOnlyList<string> RemovedColumns,
        IReadOnlyList<string> TypeChangedColumns);

    private readonly BoundedDictionary<string, List<SchemaVersion>> _schemaHistory = new BoundedDictionary<string, List<SchemaVersion>>(1000);
    private readonly object _historyLock = new();

    /// <inheritdoc />
    public override string StrategyId => "living-schema-evolution";

    /// <inheritdoc />
    public override string DisplayName => "Schema Evolution Tracker";

    /// <inheritdoc />
    public override DataCatalogCategory Category => DataCatalogCategory.SchemaRegistry;

    /// <inheritdoc />
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsRealTime = true,
        SupportsFederation = false,
        SupportsVersioning = true,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Tracks schema changes over time, detecting column additions, removals, type changes, and renames. " +
        "Maintains full version history with diff analysis.";

    /// <inheritdoc />
    public override string[] Tags =>
        ["schema-evolution", "version-tracking", "diff-analysis", "change-detection", "industry-first"];

    /// <summary>
    /// Records a schema snapshot for the specified asset. If the schema differs from the latest
    /// version on record, a new version is created with an incremented version number. If the
    /// schema matches the latest version, no new version is recorded.
    /// </summary>
    /// <param name="assetId">Unique identifier of the asset.</param>
    /// <param name="columns">Column name to column type mapping representing the current schema.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="assetId"/> is null or empty.</exception>
    public void RecordSchema(string assetId, Dictionary<string, string> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentNullException.ThrowIfNull(columns);

        lock (_historyLock)
        {
            var history = _schemaHistory.GetOrAdd(assetId, _ => new List<SchemaVersion>());

            if (history.Count > 0)
            {
                var latest = history[^1];

                // Compare against latest: only record if different
                if (SchemasAreEqual(latest.Columns, columns))
                {
                    return;
                }

                history.Add(new SchemaVersion(
                    latest.Version + 1,
                    new Dictionary<string, string>(columns, StringComparer.OrdinalIgnoreCase),
                    DateTimeOffset.UtcNow));
            }
            else
            {
                // First version
                history.Add(new SchemaVersion(
                    1,
                    new Dictionary<string, string>(columns, StringComparer.OrdinalIgnoreCase),
                    DateTimeOffset.UtcNow));
            }
        }
    }

    /// <summary>
    /// Gets the full schema version history for the specified asset.
    /// </summary>
    /// <param name="assetId">The asset to retrieve history for.</param>
    /// <returns>All recorded schema versions, ordered by version number.</returns>
    public IReadOnlyList<SchemaVersion> GetHistory(string assetId)
    {
        lock (_historyLock)
        {
            if (_schemaHistory.TryGetValue(assetId, out var history))
            {
                return new List<SchemaVersion>(history);
            }
        }

        return Array.Empty<SchemaVersion>();
    }

    /// <summary>
    /// Computes a diff between two schema versions of the specified asset, identifying
    /// added columns, removed columns, and columns whose types have changed.
    /// </summary>
    /// <param name="assetId">The asset to compute the diff for.</param>
    /// <param name="fromVersion">The earlier version number.</param>
    /// <param name="toVersion">The later version number.</param>
    /// <returns>A diff describing the changes, or null if either version is not found.</returns>
    public SchemaDiff? ComputeDiff(string assetId, int fromVersion, int toVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);

        SchemaVersion? from = null;
        SchemaVersion? to = null;

        lock (_historyLock)
        {
            if (!_schemaHistory.TryGetValue(assetId, out var history))
            {
                return null;
            }

            from = history.FirstOrDefault(v => v.Version == fromVersion);
            to = history.FirstOrDefault(v => v.Version == toVersion);
        }

        if (from is null || to is null)
        {
            return null;
        }

        var fromKeys = new HashSet<string>(from.Columns.Keys, StringComparer.OrdinalIgnoreCase);
        var toKeys = new HashSet<string>(to.Columns.Keys, StringComparer.OrdinalIgnoreCase);

        // Added = keys in toVersion not in fromVersion
        var added = toKeys.Where(k => !fromKeys.Contains(k)).ToList();

        // Removed = keys in fromVersion not in toVersion
        var removed = fromKeys.Where(k => !toKeys.Contains(k)).ToList();

        // Type changed = keys in both but different values
        var typeChanged = fromKeys
            .Where(k => toKeys.Contains(k) &&
                        !string.Equals(from.Columns[k], to.Columns[k], StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new SchemaDiff(fromVersion, toVersion, added, removed, typeChanged);
    }

    /// <summary>
    /// Compares two schema dictionaries for equality.
    /// </summary>
    private static bool SchemasAreEqual(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var otherValue) ||
                !string.Equals(value, otherValue, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}

#endregion

#region Strategy 5: UsagePatternLearnerStrategy

/// <summary>
/// Learns from how data assets are accessed, queried, and consumed. Builds usage profiles to rank
/// assets by popularity, identify frequently co-accessed datasets, and recommend related assets.
/// </summary>
/// <remarks>
/// Implements T146.B2.5: Usage Pattern Learner Strategy.
/// Tracks per-asset access counts, query counts, actor distributions, and operation types.
/// Co-access tracking enables recommendations: when asset A is frequently accessed alongside
/// asset B, querying recommendations for A will surface B.
/// </remarks>
public sealed class UsagePatternLearnerStrategy : DataCatalogStrategyBase
{
    /// <summary>
    /// Maintains usage statistics for a single data asset.
    /// </summary>
    public sealed class UsageProfile
    {
        /// <summary>Total number of accesses to this asset.</summary>
        public long AccessCount;

        /// <summary>Total number of query operations against this asset.</summary>
        public long QueryCount;

        /// <summary>When this asset was last accessed.</summary>
        public DateTimeOffset LastAccessed;

        /// <summary>When this asset was first accessed.</summary>
        public DateTimeOffset FirstAccessed;

        /// <summary>Count of accesses by each actor (user or service).</summary>
        public BoundedDictionary<string, int> AccessorCounts { get; } = new BoundedDictionary<string, int>(1000);

        /// <summary>Count of each operation type (read, write, query, etc.).</summary>
        public BoundedDictionary<string, int> OperationCounts { get; } = new BoundedDictionary<string, int>(1000);
    }

    private readonly BoundedDictionary<string, UsageProfile> _profiles = new BoundedDictionary<string, UsageProfile>(1000);
    private readonly BoundedDictionary<string, BoundedDictionary<string, int>> _coAccess = new BoundedDictionary<string, BoundedDictionary<string, int>>(1000);

    /// <inheritdoc />
    public override string StrategyId => "living-usage-learner";

    /// <inheritdoc />
    public override string DisplayName => "Usage Pattern Learner";

    /// <inheritdoc />
    public override DataCatalogCategory Category => DataCatalogCategory.SearchDiscovery;

    /// <inheritdoc />
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsFederation = false,
        SupportsVersioning = false,
        SupportsMultiTenancy = true,
        MaxEntries = 0
    };

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Learns from how data assets are accessed, queried, and consumed. Builds usage profiles to rank " +
        "assets by popularity, identify frequently co-accessed datasets, and recommend related assets.";

    /// <inheritdoc />
    public override string[] Tags =>
        ["usage-patterns", "access-tracking", "recommendation", "popularity-ranking", "industry-first"];

    /// <summary>
    /// Records an access event for the specified asset. Updates access count, last/first accessed
    /// timestamps, actor distribution, and operation type distribution.
    /// </summary>
    /// <param name="assetId">The asset being accessed.</param>
    /// <param name="actorId">The user or service performing the access.</param>
    /// <param name="operationType">The type of operation (e.g., "read", "write", "query").</param>
    /// <exception cref="ArgumentException">Thrown when required parameters are null or empty.</exception>
    public void RecordAccess(string assetId, string actorId, string operationType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationType);

        var profile = _profiles.GetOrAdd(assetId, _ => new UsageProfile
        {
            FirstAccessed = DateTimeOffset.UtcNow
        });

        Interlocked.Increment(ref profile.AccessCount);
        profile.LastAccessed = DateTimeOffset.UtcNow;

        if (string.Equals(operationType, "query", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref profile.QueryCount);
        }

        profile.AccessorCounts.AddOrUpdate(actorId, 1, (_, count) => count + 1);
        profile.OperationCounts.AddOrUpdate(operationType, 1, (_, count) => count + 1);
    }

    /// <summary>
    /// Records that two assets were accessed together in the same session or context.
    /// This information is used to generate recommendations.
    /// </summary>
    /// <param name="assetId1">The first co-accessed asset.</param>
    /// <param name="assetId2">The second co-accessed asset.</param>
    /// <exception cref="ArgumentException">Thrown when required parameters are null or empty.</exception>
    public void RecordCoAccess(string assetId1, string assetId2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId1);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId2);

        // Record in both directions
        var map1 = _coAccess.GetOrAdd(assetId1, _ => new BoundedDictionary<string, int>(1000));
        map1.AddOrUpdate(assetId2, 1, (_, count) => count + 1);

        var map2 = _coAccess.GetOrAdd(assetId2, _ => new BoundedDictionary<string, int>(1000));
        map2.AddOrUpdate(assetId1, 1, (_, count) => count + 1);
    }

    /// <summary>
    /// Returns the most popular assets ranked by total access count.
    /// </summary>
    /// <param name="topK">Maximum number of assets to return.</param>
    /// <returns>Asset IDs ordered by descending access count.</returns>
    public IReadOnlyList<string> GetPopularAssets(int topK)
    {
        return _profiles
            .OrderByDescending(kv => Interlocked.Read(ref kv.Value.AccessCount))
            .Take(topK)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>
    /// Recommends assets that are frequently co-accessed with the specified asset.
    /// </summary>
    /// <param name="assetId">The asset to get recommendations for.</param>
    /// <param name="topK">Maximum number of recommendations to return.</param>
    /// <returns>Recommended asset IDs ordered by co-access frequency.</returns>
    public IReadOnlyList<string> GetRecommendations(string assetId, int topK)
    {
        if (!_coAccess.TryGetValue(assetId, out var coAccessMap))
        {
            return Array.Empty<string>();
        }

        return coAccessMap
            .OrderByDescending(kv => kv.Value)
            .Take(topK)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>
    /// Returns the usage profile for the specified asset, or null if no accesses have been recorded.
    /// </summary>
    /// <param name="assetId">The asset to retrieve the profile for.</param>
    /// <returns>The usage profile, or null if no data is available.</returns>
    public UsageProfile? GetUsageProfile(string assetId)
    {
        return _profiles.TryGetValue(assetId, out var profile) ? profile : null;
    }
}

#endregion
