using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory;

/// <summary>
/// Advanced context evolution manager with AI-driven capabilities.
/// Provides sophisticated context growth, consolidation, and predictive modeling
/// beyond the basic EvolvingContextManager in TieredMemoryStrategy.cs.
/// </summary>
public sealed class AdvancedEvolvingContextManager : IAsyncDisposable
{
    private readonly IPersistentMemoryStore _store;
    private readonly ConcurrentDictionary<string, ScopeMetrics> _scopeMetrics = new();
    private readonly ConcurrentDictionary<string, List<EvolutionEvent>> _evolutionHistory = new();
    private readonly ConcurrentDictionary<string, double> _domainExpertise = new();
    private readonly SemaphoreSlim _consolidationLock = new(1, 1);

    private long _totalConsolidations;
    private long _totalRefinements;
    private long _totalPredictions;
    private DateTimeOffset _firstContextCreated;
    private DateTimeOffset _lastEvolutionEvent;
    private bool _disposed;

    /// <summary>
    /// Initializes a new evolving context manager.
    /// </summary>
    /// <param name="store">The underlying persistent memory store.</param>
    public AdvancedEvolvingContextManager(IPersistentMemoryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _firstContextCreated = DateTimeOffset.UtcNow;
        _lastEvolutionEvent = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Stores a new context entry and updates evolution metrics.
    /// </summary>
    public async Task StoreContextAsync(ContextEntry entry, CancellationToken ct = default)
    {
        await _store.StoreAsync(entry, ct);

        // Update scope metrics
        UpdateScopeMetrics(entry.Scope, entry.SizeBytes, isNew: true);

        // Record evolution event
        RecordEvolutionEvent(entry.Scope, EvolutionEventType.ContextAdded, $"Added entry {entry.EntryId}");

        // Update domain expertise based on content patterns
        await UpdateDomainExpertiseAsync(entry, ct);
    }

    /// <summary>
    /// Gets context evolution metrics across all scopes.
    /// </summary>
    public async Task<AdvancedContextEvolutionMetrics> GetEvolutionMetricsAsync(CancellationToken ct = default)
    {
        var stats = await _store.GetStatisticsAsync(ct);

        return new AdvancedContextEvolutionMetrics
        {
            TotalContextEntries = stats.TotalEntries,
            TotalContextBytes = stats.TotalBytes,
            AverageComplexity = await CalculateAverageComplexityAsync(ct),
            ConsolidationCycles = (int)_totalConsolidations,
            RefinementCycles = (int)_totalRefinements,
            FirstContextCreated = _firstContextCreated,
            LastEvolutionEvent = _lastEvolutionEvent,
            DomainExpertiseScores = new Dictionary<string, double>(_domainExpertise),
            ScopeCount = _scopeMetrics.Count,
            EvolutionVelocity = CalculateEvolutionVelocity(),
            MaturityLevel = CalculateMaturityLevel()
        };
    }

    /// <summary>
    /// Gets metrics for a specific scope.
    /// </summary>
    public AdvancedContextEvolutionMetrics GetEvolutionMetrics()
    {
        return new AdvancedContextEvolutionMetrics
        {
            TotalContextEntries = _scopeMetrics.Values.Sum(m => m.EntryCount),
            TotalContextBytes = _scopeMetrics.Values.Sum(m => m.TotalBytes),
            AverageComplexity = _scopeMetrics.Values.Count > 0
                ? _scopeMetrics.Values.Average(m => m.ComplexityScore)
                : 0,
            ConsolidationCycles = (int)_totalConsolidations,
            RefinementCycles = (int)_totalRefinements,
            FirstContextCreated = _firstContextCreated,
            LastEvolutionEvent = _lastEvolutionEvent,
            DomainExpertiseScores = new Dictionary<string, double>(_domainExpertise),
            ScopeCount = _scopeMetrics.Count,
            EvolutionVelocity = CalculateEvolutionVelocity(),
            MaturityLevel = CalculateMaturityLevel()
        };
    }

    /// <summary>
    /// Consolidates related memories into higher-level abstractions.
    /// Performs semantic clustering, duplicate detection, and knowledge synthesis.
    /// </summary>
    public async Task ConsolidateAsync(StorageTier tier, CancellationToken ct = default)
    {
        await _consolidationLock.WaitAsync(ct);
        try
        {
            // Find entries at the specified tier
            var entries = new List<ContextEntry>();
            await foreach (var entry in _store.SearchByMetadataAsync(
                new Dictionary<string, object> { ["tier"] = (int)tier }, limit: 1000, ct: ct))
            {
                entries.Add(entry);
            }

            if (entries.Count < 2)
            {
                return;
            }

            // Group by scope
            var groupedByScope = entries.GroupBy(e => e.Scope);

            foreach (var scopeGroup in groupedByScope)
            {
                var scopeEntries = scopeGroup.ToList();

                // Find semantic clusters
                var clusters = await ClusterSemanticallyAsync(scopeEntries, ct);

                foreach (var cluster in clusters.Where(c => c.Count > 1))
                {
                    // Create consolidated entry from cluster
                    var consolidated = await CreateConsolidatedEntryAsync(cluster, ct);

                    // Store consolidated entry at higher tier
                    consolidated = consolidated with { Tier = (StorageTier)Math.Min((int)tier + 1, 3) };
                    await _store.StoreAsync(consolidated, ct);

                    // Optionally delete or demote original entries
                    foreach (var original in cluster)
                    {
                        await _store.DeleteAsync(original.EntryId, ct);
                    }

                    RecordEvolutionEvent(scopeGroup.Key, EvolutionEventType.Consolidated,
                        $"Consolidated {cluster.Count} entries into {consolidated.EntryId}");
                }
            }

            Interlocked.Increment(ref _totalConsolidations);
            _lastEvolutionEvent = DateTimeOffset.UtcNow;
        }
        finally
        {
            _consolidationLock.Release();
        }
    }

    /// <summary>
    /// Performs AI-driven context refinement for a specific scope.
    /// Enhances context quality through entity extraction, relationship discovery,
    /// and semantic enrichment.
    /// </summary>
    public async Task RefineContextAsync(string scope, CancellationToken ct = default)
    {
        var entries = new List<ContextEntry>();
        await foreach (var entry in _store.SearchByMetadataAsync(
            new Dictionary<string, object>(), limit: 100, ct: ct))
        {
            if (entry.Scope == scope)
            {
                entries.Add(entry);
            }
        }

        foreach (var entry in entries)
        {
            // Extract entities
            var entities = ExtractEntities(Encoding.UTF8.GetString(entry.Content));

            // Discover relationships
            var relationships = await DiscoverRelationshipsAsync(entry, entries, ct);

            // Create enriched metadata
            var enrichedMetadata = new Dictionary<string, object>(entry.Metadata ?? new Dictionary<string, object>())
            {
                ["entities"] = entities,
                ["relationships"] = relationships,
                ["refined_at"] = DateTimeOffset.UtcNow,
                ["refinement_version"] = (entry.Metadata?.TryGetValue("refinement_version", out var v) == true ? (int)v : 0) + 1
            };

            // Update entry with enriched metadata
            var refinedEntry = entry with
            {
                Metadata = enrichedMetadata,
                LastModifiedAt = DateTimeOffset.UtcNow,
                ImportanceScore = Math.Min(entry.ImportanceScore + 0.1, 1.0)
            };

            await _store.StoreAsync(refinedEntry, ct);
        }

        Interlocked.Increment(ref _totalRefinements);
        _lastEvolutionEvent = DateTimeOffset.UtcNow;

        RecordEvolutionEvent(scope, EvolutionEventType.Refined, $"Refined {entries.Count} entries");

        // Update scope metrics
        if (_scopeMetrics.TryGetValue(scope, out var metrics))
        {
            metrics.RefinementCount++;
            metrics.LastRefinement = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Gets the context complexity score for a scope.
    /// Higher scores indicate more evolved, interconnected context.
    /// </summary>
    public async Task<double> GetComplexityScoreAsync(string scope, CancellationToken ct = default)
    {
        if (_scopeMetrics.TryGetValue(scope, out var metrics))
        {
            return metrics.ComplexityScore;
        }

        // Calculate from scratch if not cached
        var entries = new List<ContextEntry>();
        await foreach (var entry in _store.SearchByMetadataAsync(
            new Dictionary<string, object>(), limit: 1000, ct: ct))
        {
            if (entry.Scope == scope)
            {
                entries.Add(entry);
            }
        }

        return CalculateComplexity(entries);
    }

    /// <summary>
    /// Predicts future context needs based on access patterns and semantic analysis.
    /// </summary>
    public async Task<ContextPrediction[]> PredictContextNeedsAsync(string scope, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _totalPredictions);

        var predictions = new List<ContextPrediction>();

        // Analyze recent access patterns
        if (_scopeMetrics.TryGetValue(scope, out var metrics))
        {
            // Predict based on access frequency trends
            if (metrics.AccessRate > 10) // High access rate
            {
                predictions.Add(new ContextPrediction
                {
                    PredictedNeed = "cache_promotion",
                    Probability = 0.85,
                    RecommendedAction = "Promote frequently accessed entries to hot tier",
                    EstimatedImpact = ContextImpact.High,
                    TimeHorizon = TimeSpan.FromMinutes(5)
                });
            }

            // Predict consolidation needs
            if (metrics.EntryCount > 100 && metrics.ConsolidationCount < 1)
            {
                predictions.Add(new ContextPrediction
                {
                    PredictedNeed = "consolidation",
                    Probability = 0.75,
                    RecommendedAction = "Consolidate similar entries to reduce redundancy",
                    EstimatedImpact = ContextImpact.Medium,
                    TimeHorizon = TimeSpan.FromHours(1)
                });
            }

            // Predict refinement needs
            var hoursSinceRefinement = metrics.LastRefinement.HasValue
                ? (DateTimeOffset.UtcNow - metrics.LastRefinement.Value).TotalHours
                : double.MaxValue;

            if (hoursSinceRefinement > 24)
            {
                predictions.Add(new ContextPrediction
                {
                    PredictedNeed = "refinement",
                    Probability = 0.7,
                    RecommendedAction = "Perform AI-driven refinement to enhance context quality",
                    EstimatedImpact = ContextImpact.Medium,
                    TimeHorizon = TimeSpan.FromHours(6)
                });
            }
        }

        // Predict based on domain expertise gaps
        foreach (var (domain, expertise) in _domainExpertise.Where(d => d.Value < 0.3))
        {
            predictions.Add(new ContextPrediction
            {
                PredictedNeed = $"domain_expertise_{domain}",
                Probability = 0.6,
                RecommendedAction = $"Acquire more context about {domain} domain",
                EstimatedImpact = ContextImpact.Low,
                TimeHorizon = TimeSpan.FromDays(1)
            });
        }

        await Task.CompletedTask;
        return predictions.OrderByDescending(p => p.Probability).ToArray();
    }

    /// <summary>
    /// Promotes entries from one tier to another based on access patterns.
    /// </summary>
    public async Task PromoteEntriesAsync(StorageTier fromTier, StorageTier toTier, int count = 10, CancellationToken ct = default)
    {
        if (fromTier <= toTier)
        {
            return; // Can only promote to hotter tiers
        }

        var candidates = new List<ContextEntry>();
        await foreach (var entry in _store.SearchByMetadataAsync(
            new Dictionary<string, object> { ["tier"] = (int)fromTier }, limit: count * 2, ct: ct))
        {
            candidates.Add(entry);
        }

        // Select entries with highest access counts and importance
        var toPromote = candidates
            .OrderByDescending(e => e.AccessCount * e.ImportanceScore)
            .Take(count);

        foreach (var entry in toPromote)
        {
            var promoted = entry with
            {
                Tier = toTier,
                LastModifiedAt = DateTimeOffset.UtcNow
            };

            await _store.StoreAsync(promoted, ct);

            RecordEvolutionEvent(entry.Scope, EvolutionEventType.TierChanged,
                $"Promoted {entry.EntryId} from {fromTier} to {toTier}");
        }

        _lastEvolutionEvent = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Demotes entries from one tier to another based on access patterns.
    /// </summary>
    public async Task DemoteEntriesAsync(StorageTier fromTier, StorageTier toTier, int count = 10, CancellationToken ct = default)
    {
        if (fromTier >= toTier)
        {
            return; // Can only demote to colder tiers
        }

        var candidates = new List<ContextEntry>();
        await foreach (var entry in _store.SearchByMetadataAsync(
            new Dictionary<string, object> { ["tier"] = (int)fromTier }, limit: count * 2, ct: ct))
        {
            candidates.Add(entry);
        }

        // Select entries with lowest recent access
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        var toDemote = candidates
            .Where(e => !e.LastAccessedAt.HasValue || e.LastAccessedAt < cutoff)
            .OrderBy(e => e.AccessCount)
            .Take(count);

        foreach (var entry in toDemote)
        {
            var demoted = entry with
            {
                Tier = toTier,
                LastModifiedAt = DateTimeOffset.UtcNow
            };

            await _store.StoreAsync(demoted, ct);

            RecordEvolutionEvent(entry.Scope, EvolutionEventType.TierChanged,
                $"Demoted {entry.EntryId} from {fromTier} to {toTier}");
        }

        _lastEvolutionEvent = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Gets the evolution history for a scope.
    /// </summary>
    public IReadOnlyList<EvolutionEvent> GetEvolutionHistory(string scope, int limit = 100)
    {
        if (_evolutionHistory.TryGetValue(scope, out var history))
        {
            return history.TakeLast(limit).ToList();
        }
        return Array.Empty<EvolutionEvent>();
    }

    /// <summary>
    /// Analyzes context gaps and suggests improvements.
    /// </summary>
    public async Task<ContextGapAnalysis> AnalyzeContextGapsAsync(string scope, CancellationToken ct = default)
    {
        var entries = new List<ContextEntry>();
        await foreach (var entry in _store.SearchByMetadataAsync(
            new Dictionary<string, object>(), limit: 1000, ct: ct))
        {
            if (entry.Scope == scope)
            {
                entries.Add(entry);
            }
        }

        var gaps = new List<ContextGap>();

        // Analyze temporal coverage
        var timestamps = entries
            .Select(e => e.CreatedAt)
            .OrderBy(t => t)
            .ToList();

        for (int i = 1; i < timestamps.Count; i++)
        {
            var gap = timestamps[i] - timestamps[i - 1];
            if (gap > TimeSpan.FromDays(7))
            {
                gaps.Add(new ContextGap
                {
                    GapType = "temporal",
                    Description = $"Context gap from {timestamps[i - 1]:yyyy-MM-dd} to {timestamps[i]:yyyy-MM-dd}",
                    Severity = gap.TotalDays > 30 ? GapSeverity.High : GapSeverity.Medium,
                    StartTime = timestamps[i - 1],
                    EndTime = timestamps[i]
                });
            }
        }

        // Analyze domain coverage
        var domainCoverage = new Dictionary<string, int>();
        foreach (var entry in entries)
        {
            var domains = ExtractDomains(Encoding.UTF8.GetString(entry.Content));
            foreach (var domain in domains)
            {
                domainCoverage[domain] = domainCoverage.GetValueOrDefault(domain, 0) + 1;
            }
        }

        var avgCoverage = domainCoverage.Count > 0 ? domainCoverage.Values.Average() : 0;
        foreach (var (domain, count) in domainCoverage.Where(d => d.Value < avgCoverage * 0.5))
        {
            gaps.Add(new ContextGap
            {
                GapType = "domain",
                Description = $"Low coverage for domain: {domain} ({count} entries)",
                Severity = GapSeverity.Medium,
                AffectedDomain = domain
            });
        }

        return new ContextGapAnalysis
        {
            Scope = scope,
            TotalEntries = entries.Count,
            Gaps = gaps,
            OverallCoverageScore = CalculateCoverageScore(entries, gaps),
            AnalyzedAt = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _consolidationLock.Dispose();
        await _store.DisposeAsync();
    }

    // Private helper methods

    private void UpdateScopeMetrics(string scope, long bytes, bool isNew)
    {
        var metrics = _scopeMetrics.GetOrAdd(scope, _ => new ScopeMetrics
        {
            Scope = scope,
            FirstEntry = DateTimeOffset.UtcNow
        });

        if (isNew)
        {
            Interlocked.Increment(ref metrics.EntryCount);
            Interlocked.Add(ref metrics.TotalBytes, bytes);
        }

        metrics.LastActivity = DateTimeOffset.UtcNow;
    }

    private void RecordEvolutionEvent(string scope, EvolutionEventType type, string description)
    {
        var history = _evolutionHistory.GetOrAdd(scope, _ => new List<EvolutionEvent>());

        lock (history)
        {
            history.Add(new EvolutionEvent
            {
                EventType = type,
                Description = description,
                Timestamp = DateTimeOffset.UtcNow
            });

            // Keep only last 1000 events per scope
            if (history.Count > 1000)
            {
                history.RemoveRange(0, history.Count - 1000);
            }
        }
    }

    private async Task UpdateDomainExpertiseAsync(ContextEntry entry, CancellationToken ct)
    {
        var content = Encoding.UTF8.GetString(entry.Content);
        var domains = ExtractDomains(content);

        foreach (var domain in domains)
        {
            _domainExpertise.AddOrUpdate(
                domain,
                0.1,
                (_, current) => Math.Min(current + 0.01, 1.0)
            );
        }

        await Task.CompletedTask;
    }

    private async Task<double> CalculateAverageComplexityAsync(CancellationToken ct)
    {
        if (_scopeMetrics.IsEmpty)
            return 0;

        var total = 0.0;
        var count = 0;

        foreach (var metrics in _scopeMetrics.Values)
        {
            total += metrics.ComplexityScore;
            count++;
        }

        await Task.CompletedTask;
        return count > 0 ? total / count : 0;
    }

    private double CalculateEvolutionVelocity()
    {
        var elapsed = (DateTimeOffset.UtcNow - _firstContextCreated).TotalHours;
        if (elapsed <= 0) return 0;

        var totalEvents = _totalConsolidations + _totalRefinements;
        return totalEvents / elapsed; // Events per hour
    }

    private ContextMaturityLevel CalculateMaturityLevel()
    {
        var metrics = GetEvolutionMetrics();

        if (metrics.TotalContextEntries < 10)
            return ContextMaturityLevel.Nascent;

        if (metrics.ConsolidationCycles < 1 || metrics.RefinementCycles < 1)
            return ContextMaturityLevel.Growing;

        if (metrics.AverageComplexity < 0.5)
            return ContextMaturityLevel.Developing;

        if (metrics.DomainExpertiseScores.Values.Average() < 0.5)
            return ContextMaturityLevel.Maturing;

        return ContextMaturityLevel.Evolved;
    }

    private async Task<List<List<ContextEntry>>> ClusterSemanticallyAsync(
        List<ContextEntry> entries,
        CancellationToken ct)
    {
        // Simple clustering based on embedding similarity
        var clusters = new List<List<ContextEntry>>();
        var assigned = new HashSet<string>();

        foreach (var entry in entries.Where(e => e.Embedding != null))
        {
            if (assigned.Contains(entry.EntryId))
                continue;

            var cluster = new List<ContextEntry> { entry };
            assigned.Add(entry.EntryId);

            foreach (var other in entries.Where(e => e.Embedding != null && !assigned.Contains(e.EntryId)))
            {
                var similarity = CosineSimilarity(entry.Embedding!, other.Embedding!);
                if (similarity > 0.85) // High similarity threshold
                {
                    cluster.Add(other);
                    assigned.Add(other.EntryId);
                }
            }

            clusters.Add(cluster);
        }

        await Task.CompletedTask;
        return clusters;
    }

    private async Task<ContextEntry> CreateConsolidatedEntryAsync(
        List<ContextEntry> cluster,
        CancellationToken ct)
    {
        // Combine content from all entries
        var combinedContent = string.Join("\n---\n",
            cluster.Select(e => Encoding.UTF8.GetString(e.Content)));

        // Average the embeddings
        float[]? avgEmbedding = null;
        var entriesWithEmbeddings = cluster.Where(e => e.Embedding != null).ToList();
        if (entriesWithEmbeddings.Any())
        {
            var dim = entriesWithEmbeddings.First().Embedding!.Length;
            avgEmbedding = new float[dim];

            foreach (var entry in entriesWithEmbeddings)
            {
                for (int i = 0; i < dim; i++)
                {
                    avgEmbedding[i] += entry.Embedding![i];
                }
            }

            for (int i = 0; i < dim; i++)
            {
                avgEmbedding[i] /= entriesWithEmbeddings.Count;
            }
        }

        // Merge metadata
        var mergedMetadata = new Dictionary<string, object>
        {
            ["consolidated_from"] = cluster.Select(e => e.EntryId).ToArray(),
            ["consolidation_time"] = DateTimeOffset.UtcNow
        };

        foreach (var entry in cluster)
        {
            if (entry.Metadata != null)
            {
                foreach (var (key, value) in entry.Metadata)
                {
                    if (!mergedMetadata.ContainsKey(key))
                    {
                        mergedMetadata[key] = value;
                    }
                }
            }
        }

        await Task.CompletedTask;

        return new ContextEntry
        {
            EntryId = $"consolidated_{Guid.NewGuid():N}",
            Scope = cluster.First().Scope,
            ContentType = "consolidated",
            Content = Encoding.UTF8.GetBytes(combinedContent),
            Embedding = avgEmbedding,
            CreatedAt = DateTimeOffset.UtcNow,
            ImportanceScore = cluster.Max(e => e.ImportanceScore),
            AccessCount = cluster.Sum(e => e.AccessCount),
            Metadata = mergedMetadata,
            Tags = cluster.SelectMany(e => e.Tags ?? Array.Empty<string>()).Distinct().ToArray()
        };
    }

    private async Task<List<string>> DiscoverRelationshipsAsync(
        ContextEntry entry,
        List<ContextEntry> allEntries,
        CancellationToken ct)
    {
        var relationships = new List<string>();

        if (entry.Embedding == null)
            return relationships;

        foreach (var other in allEntries.Where(e => e.EntryId != entry.EntryId && e.Embedding != null))
        {
            var similarity = CosineSimilarity(entry.Embedding, other.Embedding!);
            if (similarity > 0.7)
            {
                relationships.Add($"similar_to:{other.EntryId}:{similarity:F3}");
            }
        }

        await Task.CompletedTask;
        return relationships;
    }

    private List<string> ExtractEntities(string content)
    {
        // Simple entity extraction (would use NLP in production)
        return content
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3 && char.IsUpper(w[0]))
            .Distinct()
            .Take(20)
            .ToList();
    }

    private List<string> ExtractDomains(string content)
    {
        var domains = new List<string>();
        var lower = content.ToLowerInvariant();

        // Domain detection keywords
        var domainKeywords = new Dictionary<string, string[]>
        {
            ["technology"] = new[] { "software", "code", "programming", "api", "database" },
            ["business"] = new[] { "revenue", "customer", "market", "sales", "strategy" },
            ["science"] = new[] { "research", "experiment", "data", "analysis", "hypothesis" },
            ["finance"] = new[] { "investment", "stock", "trading", "portfolio", "risk" },
            ["healthcare"] = new[] { "patient", "medical", "treatment", "diagnosis", "health" }
        };

        foreach (var (domain, keywords) in domainKeywords)
        {
            if (keywords.Any(k => lower.Contains(k)))
            {
                domains.Add(domain);
            }
        }

        return domains;
    }

    private double CalculateComplexity(List<ContextEntry> entries)
    {
        if (entries.Count == 0) return 0;

        // Complexity factors:
        // - Entry count (more = more complex)
        // - Average content size
        // - Interconnections (entities, relationships)
        // - Domain diversity

        var entryScore = Math.Min(entries.Count / 100.0, 0.3);
        var sizeScore = Math.Min(entries.Average(e => e.SizeBytes) / 10000.0, 0.2);
        var metadataScore = entries.Average(e => e.Metadata?.Count ?? 0) / 10.0 * 0.25;
        var tagScore = entries.Average(e => e.Tags?.Length ?? 0) / 5.0 * 0.25;

        return Math.Min(entryScore + sizeScore + metadataScore + tagScore, 1.0);
    }

    private double CalculateCoverageScore(List<ContextEntry> entries, List<ContextGap> gaps)
    {
        if (entries.Count == 0) return 0;

        var gapPenalty = gaps.Sum(g => g.Severity switch
        {
            GapSeverity.Low => 0.05,
            GapSeverity.Medium => 0.1,
            GapSeverity.High => 0.2,
            GapSeverity.Critical => 0.3,
            _ => 0
        });

        return Math.Max(1.0 - gapPenalty, 0);
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom > 0 ? (float)(dot / denom) : 0;
    }
}

/// <summary>
/// Detailed metrics tracking advanced context evolution over time.
/// Extends beyond basic AdvancedContextEvolutionMetrics with predictive analytics.
/// </summary>
public record AdvancedAdvancedContextEvolutionMetrics
{
    public long TotalContextEntries { get; init; }
    public long TotalContextBytes { get; init; }
    public double AverageComplexity { get; init; }
    public int ConsolidationCycles { get; init; }
    public int RefinementCycles { get; init; }
    public DateTimeOffset FirstContextCreated { get; init; }
    public DateTimeOffset LastEvolutionEvent { get; init; }
    public Dictionary<string, double> DomainExpertiseScores { get; init; } = new();
    public int ScopeCount { get; init; }
    public double EvolutionVelocity { get; init; }
    public ContextMaturityLevel MaturityLevel { get; init; }
}

/// <summary>
/// Prediction about future context needs.
/// </summary>
public record ContextPrediction
{
    public required string PredictedNeed { get; init; }
    public double Probability { get; init; }
    public required string RecommendedAction { get; init; }
    public ContextImpact EstimatedImpact { get; init; }
    public TimeSpan TimeHorizon { get; init; }
}

/// <summary>
/// Impact level for context predictions.
/// </summary>
public enum ContextImpact
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// Maturity level of context evolution.
/// </summary>
public enum ContextMaturityLevel
{
    Nascent,
    Growing,
    Developing,
    Maturing,
    Evolved
}

/// <summary>
/// Evolution event type.
/// </summary>
public enum EvolutionEventType
{
    ContextAdded,
    ContextModified,
    ContextDeleted,
    Consolidated,
    Refined,
    TierChanged,
    Predicted
}

/// <summary>
/// Record of an evolution event.
/// </summary>
public record EvolutionEvent
{
    public EvolutionEventType EventType { get; init; }
    public required string Description { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Metrics for a specific scope.
/// </summary>
internal class ScopeMetrics
{
    public required string Scope { get; init; }
    public long EntryCount;
    public long TotalBytes;
    public double ComplexityScore;
    public int ConsolidationCount;
    public int RefinementCount;
    public double AccessRate;
    public DateTimeOffset FirstEntry { get; init; }
    public DateTimeOffset LastActivity;
    public DateTimeOffset? LastRefinement;
}

/// <summary>
/// Analysis of context gaps.
/// </summary>
public record ContextGapAnalysis
{
    public required string Scope { get; init; }
    public int TotalEntries { get; init; }
    public List<ContextGap> Gaps { get; init; } = new();
    public double OverallCoverageScore { get; init; }
    public DateTimeOffset AnalyzedAt { get; init; }
}

/// <summary>
/// Represents a gap in context coverage.
/// </summary>
public record ContextGap
{
    public required string GapType { get; init; }
    public required string Description { get; init; }
    public GapSeverity Severity { get; init; }
    public DateTimeOffset? StartTime { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public string? AffectedDomain { get; init; }
}

/// <summary>
/// Severity of a context gap.
/// </summary>
public enum GapSeverity
{
    Low,
    Medium,
    High,
    Critical
}
