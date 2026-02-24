using System.Diagnostics;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Indexing;

/// <summary>
/// Time-based organization index for exabyte-scale memory navigation.
/// Indexes content by creation time, modification time, and access time,
/// enabling efficient time-range queries and temporal analysis.
///
/// Features:
/// - Index by creation time, modification time, access time
/// - Efficient time-range queries
/// - Trend detection within time windows
/// - "What changed since X" queries
/// - Time-based summarization (daily/weekly/monthly digests)
/// </summary>
public sealed class TemporalContextIndex : ContextIndexBase, IDisposable
{
    private readonly BoundedDictionary<string, TemporalEntry> _entries = new BoundedDictionary<string, TemporalEntry>(1000);
    private readonly SortedDictionary<DateTimeOffset, HashSet<string>> _creationIndex = new();
    private readonly SortedDictionary<DateTimeOffset, HashSet<string>> _accessIndex = new();
    private readonly BoundedDictionary<string, TimeSpanSummary> _summaries = new BoundedDictionary<string, TimeSpanSummary>(1000);
    private readonly ReaderWriterLockSlim _indexLock = new();

    private const int MaxEntriesPerTimeSlot = 10000;

    /// <inheritdoc/>
    public override string IndexId => "index-temporal-context";

    /// <inheritdoc/>
    public override string IndexName => "Temporal Context Index";

    /// <inheritdoc/>
    public override async Task<ContextQueryResult> QueryAsync(ContextQuery query, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var results = new List<IndexedContextEntry>();
            IEnumerable<TemporalEntry> candidates;

            // Use time range if specified
            if (query.TimeRange != null)
            {
                candidates = GetEntriesInRange(query.TimeRange);
            }
            else
            {
                candidates = _entries.Values;
            }

            var queryWords = query.SemanticQuery.ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var entry in candidates)
            {
                ct.ThrowIfCancellationRequested();

                if (!MatchesFilters(entry, query))
                    continue;

                var relevance = CalculateRelevance(entry, queryWords, query.QueryEmbedding);
                if (relevance < query.MinRelevance)
                    continue;

                results.Add(CreateEntryResult(entry, relevance, query.IncludeHierarchyPath, query.DetailLevel));
            }

            // Sort by relevance (or by time if that's more appropriate)
            results = results
                .OrderByDescending(r => r.RelevanceScore)
                .Take(query.MaxResults)
                .ToList();

            sw.Stop();
            RecordQuery(sw.Elapsed);

            // Generate temporal clusters if requested
            var clusters = query.ClusterResults
                ? GenerateTemporalClusters(results)
                : null;

            return new ContextQueryResult
            {
                Entries = results,
                NavigationSummary = GenerateTemporalNavigationSummary(query.TimeRange, results),
                ClusterCounts = clusters,
                TotalMatchingEntries = results.Count,
                QueryDuration = sw.Elapsed,
                WasTruncated = results.Count >= query.MaxResults
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            sw.Stop();
            RecordQuery(sw.Elapsed);
            throw;
        }
    }

    /// <inheritdoc/>
    public override Task<ContextNode?> GetNodeAsync(string nodeId, CancellationToken ct = default)
    {
        // Node ID format: "period:YYYY-MM-DD" or "period:YYYY-MM" or "period:YYYY"
        if (_summaries.TryGetValue(nodeId, out var summary))
        {
            return Task.FromResult<ContextNode?>(SummaryToNode(summary));
        }
        return Task.FromResult<ContextNode?>(null);
    }

    /// <inheritdoc/>
    public override Task<IEnumerable<ContextNode>> GetChildrenAsync(string? parentId, int depth = 1, CancellationToken ct = default)
    {
        if (parentId == null)
        {
            // Return year-level summaries
            var years = _entries.Values
                .Select(e => e.CreatedAt.Year)
                .Distinct()
                .OrderByDescending(y => y)
                .Take(10)
                .Select(y => GetOrCreateSummary($"period:{y}", new TimeRange
                {
                    Start = new DateTimeOffset(y, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    End = new DateTimeOffset(y, 12, 31, 23, 59, 59, TimeSpan.Zero)
                }))
                .Select(SummaryToNode)
                .ToList();

            return Task.FromResult<IEnumerable<ContextNode>>(years);
        }

        // Parse parent to determine child level
        var parts = parentId.Split(':');
        if (parts.Length < 2) return Task.FromResult<IEnumerable<ContextNode>>(Array.Empty<ContextNode>());

        var children = new List<ContextNode>();
        var periodParts = parts[1].Split('-');

        if (periodParts.Length == 1 && int.TryParse(periodParts[0], out var year))
        {
            // Year level -> return months
            for (int month = 1; month <= 12; month++)
            {
                var monthKey = $"period:{year}-{month:D2}";
                var monthRange = new TimeRange
                {
                    Start = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero),
                    End = new DateTimeOffset(year, month, DateTime.DaysInMonth(year, month), 23, 59, 59, TimeSpan.Zero)
                };
                var summary = GetOrCreateSummary(monthKey, monthRange);
                if (summary.EntryCount > 0)
                {
                    children.Add(SummaryToNode(summary));
                }
            }
        }
        else if (periodParts.Length == 2 && int.TryParse(periodParts[0], out year) && int.TryParse(periodParts[1], out var month))
        {
            // Month level -> return days
            var daysInMonth = DateTime.DaysInMonth(year, month);
            for (int day = 1; day <= daysInMonth; day++)
            {
                var dayKey = $"period:{year}-{month:D2}-{day:D2}";
                var dayRange = new TimeRange
                {
                    Start = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero),
                    End = new DateTimeOffset(year, month, day, 23, 59, 59, TimeSpan.Zero)
                };
                var summary = GetOrCreateSummary(dayKey, dayRange);
                if (summary.EntryCount > 0)
                {
                    children.Add(SummaryToNode(summary));
                }
            }
        }

        return Task.FromResult<IEnumerable<ContextNode>>(children);
    }

    /// <inheritdoc/>
    public override Task IndexContentAsync(string contentId, byte[] content, ContextMetadata metadata, CancellationToken ct = default)
    {
        var createdAt = metadata.CreatedAt ?? DateTimeOffset.UtcNow;

        var entry = new TemporalEntry
        {
            ContentId = contentId,
            ContentSizeBytes = content.Length,
            Summary = metadata.Summary ?? $"Content entry {contentId}",
            Tags = metadata.Tags ?? Array.Empty<string>(),
            Embedding = metadata.Embedding,
            Tier = metadata.Tier,
            Scope = metadata.Scope ?? "default",
            CreatedAt = createdAt,
            ModifiedAt = createdAt,
            LastAccessedAt = null,
            AccessCount = 0,
            ImportanceScore = CalculateImportance(content, metadata),
            Pointer = new ContextPointer
            {
                StorageBackend = "memory",
                Path = $"entries/{contentId}",
                Offset = 0,
                Length = content.Length
            }
        };

        _entries[contentId] = entry;

        // Add to temporal indices
        AddToTemporalIndex(contentId, createdAt);

        // Invalidate affected summaries
        InvalidateSummaries(createdAt);

        MarkUpdated();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task UpdateIndexAsync(string contentId, IndexUpdate update, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(contentId, out var entry))
            return Task.CompletedTask;

        var oldModifiedAt = entry.ModifiedAt;
        var now = DateTimeOffset.UtcNow;

        if (update.NewSummary != null)
            entry = entry with { Summary = update.NewSummary, ModifiedAt = now };

        if (update.NewTags != null)
            entry = entry with { Tags = update.NewTags, ModifiedAt = now };

        if (update.RecordAccess)
        {
            entry = entry with
            {
                AccessCount = entry.AccessCount + 1,
                LastAccessedAt = now
            };

            // Update access index
            UpdateAccessIndex(contentId, now);
        }

        _entries[contentId] = entry;

        if (entry.ModifiedAt != oldModifiedAt)
        {
            InvalidateSummaries(entry.ModifiedAt);
        }

        MarkUpdated();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task RemoveFromIndexAsync(string contentId, CancellationToken ct = default)
    {
        if (_entries.TryRemove(contentId, out var entry))
        {
            RemoveFromTemporalIndex(contentId, entry.CreatedAt);
            InvalidateSummaries(entry.CreatedAt);
            MarkUpdated();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task<IndexStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        var totalContentBytes = _entries.Values.Sum(e => e.ContentSizeBytes);
        var indexSizeBytes = EstimateIndexSize();

        var stats = GetBaseStatistics(
            _entries.Count,
            totalContentBytes,
            indexSizeBytes,
            _summaries.Count,
            3 // Year/Month/Day levels
        );

        var byTier = _entries.Values
            .GroupBy(e => e.Tier)
            .ToDictionary(g => g.Key, g => (long)g.Count());

        return Task.FromResult(stats with
        {
            EntriesByTier = byTier
        });
    }

    #region Temporal Query Methods

    /// <summary>
    /// Gets entries in a specific time range.
    /// </summary>
    /// <param name="range">The time range.</param>
    /// <returns>Entries within the range.</returns>
    public IEnumerable<TemporalEntry> GetEntriesInRange(TimeRange range)
    {
        _indexLock.EnterReadLock();
        try
        {
            var start = range.Start ?? DateTimeOffset.MinValue;
            var end = range.End ?? DateTimeOffset.MaxValue;

            return _entries.Values
                .Where(e => e.CreatedAt >= start && e.CreatedAt <= end)
                .OrderByDescending(e => e.CreatedAt);
        }
        finally
        {
            _indexLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets entries that changed since a specific time.
    /// </summary>
    /// <param name="since">The reference time.</param>
    /// <returns>Entries modified or created since the time.</returns>
    public ChangesSinceResult GetChangesSince(DateTimeOffset since)
    {
        var created = _entries.Values
            .Where(e => e.CreatedAt >= since)
            .OrderByDescending(e => e.CreatedAt)
            .ToList();

        var modified = _entries.Values
            .Where(e => e.ModifiedAt >= since && e.CreatedAt < since)
            .OrderByDescending(e => e.ModifiedAt)
            .ToList();

        var accessed = _entries.Values
            .Where(e => e.LastAccessedAt >= since)
            .OrderByDescending(e => e.LastAccessedAt)
            .ToList();

        return new ChangesSinceResult
        {
            Since = since,
            CreatedEntries = created.Select(ToTemporalEntryInfo).ToList(),
            ModifiedEntries = modified.Select(ToTemporalEntryInfo).ToList(),
            AccessedEntries = accessed.Select(ToTemporalEntryInfo).ToList(),
            TotalChanges = created.Count + modified.Count
        };
    }

    /// <summary>
    /// Gets a time-based summary for a period.
    /// </summary>
    /// <param name="period">Period type (daily, weekly, monthly).</param>
    /// <param name="reference">Reference date.</param>
    /// <returns>Summary for the period.</returns>
    public TimeSpanSummary GetPeriodSummary(TimePeriod period, DateTimeOffset reference)
    {
        var range = GetPeriodRange(period, reference);
        var key = $"period:{period}:{reference:yyyy-MM-dd}";

        return GetOrCreateSummary(key, range);
    }

    /// <summary>
    /// Detects trends in content over time windows.
    /// </summary>
    /// <param name="windowSize">Size of each time window.</param>
    /// <param name="windowCount">Number of windows to analyze.</param>
    /// <returns>Trend analysis results.</returns>
    public TrendAnalysis DetectTrends(TimeSpan windowSize, int windowCount = 10)
    {
        var now = DateTimeOffset.UtcNow;
        var windows = new List<TimeWindowStats>();

        for (int i = 0; i < windowCount; i++)
        {
            var windowEnd = now - (windowSize * i);
            var windowStart = windowEnd - windowSize;

            var entriesInWindow = _entries.Values
                .Where(e => e.CreatedAt >= windowStart && e.CreatedAt < windowEnd)
                .ToList();

            windows.Add(new TimeWindowStats
            {
                WindowStart = windowStart,
                WindowEnd = windowEnd,
                EntryCount = entriesInWindow.Count,
                TotalBytes = entriesInWindow.Sum(e => e.ContentSizeBytes),
                TopTags = entriesInWindow
                    .SelectMany(e => e.Tags)
                    .GroupBy(t => t)
                    .OrderByDescending(g => g.Count())
                    .Take(5)
                    .Select(g => g.Key)
                    .ToArray(),
                AverageImportance = entriesInWindow.Any()
                    ? entriesInWindow.Average(e => e.ImportanceScore)
                    : 0f
            });
        }

        // Calculate trends
        var growthRate = CalculateGrowthRate(windows);
        var peakWindow = windows.OrderByDescending(w => w.EntryCount).FirstOrDefault();
        var emergingTopics = DetectEmergingTopics(windows);

        return new TrendAnalysis
        {
            AnalyzedFrom = now - (windowSize * windowCount),
            AnalyzedTo = now,
            WindowSize = windowSize,
            Windows = windows,
            OverallGrowthRate = growthRate,
            PeakWindow = peakWindow,
            EmergingTopics = emergingTopics,
            IsGrowing = growthRate > 0.05,
            IsDeclining = growthRate < -0.05
        };
    }

    /// <summary>
    /// Gets recent activity summary.
    /// </summary>
    /// <param name="hours">Number of hours to look back.</param>
    /// <returns>Activity summary.</returns>
    public RecentActivitySummary GetRecentActivity(int hours = 24)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-hours);

        var created = _entries.Values.Count(e => e.CreatedAt >= since);
        var modified = _entries.Values.Count(e => e.ModifiedAt >= since && e.CreatedAt < since);
        var accessed = _entries.Values.Count(e => e.LastAccessedAt >= since);

        var mostActive = _entries.Values
            .Where(e => e.LastAccessedAt >= since)
            .OrderByDescending(e => e.AccessCount)
            .Take(10)
            .Select(ToTemporalEntryInfo)
            .ToList();

        var hourlyBreakdown = Enumerable.Range(0, hours)
            .Select(h =>
            {
                var hourStart = DateTimeOffset.UtcNow.AddHours(-h - 1);
                var hourEnd = DateTimeOffset.UtcNow.AddHours(-h);
                return new HourlyActivity
                {
                    Hour = hourStart,
                    Created = _entries.Values.Count(e => e.CreatedAt >= hourStart && e.CreatedAt < hourEnd),
                    Accessed = _entries.Values.Count(e => e.LastAccessedAt >= hourStart && e.LastAccessedAt < hourEnd)
                };
            })
            .ToList();

        return new RecentActivitySummary
        {
            SinceTime = since,
            HoursAnalyzed = hours,
            EntriesCreated = created,
            EntriesModified = modified,
            EntriesAccessed = accessed,
            MostActiveEntries = mostActive,
            HourlyBreakdown = hourlyBreakdown
        };
    }

    /// <summary>
    /// Gets entries by access pattern (frequently accessed, recently accessed, etc.).
    /// </summary>
    /// <param name="pattern">The access pattern to filter by.</param>
    /// <param name="limit">Maximum results.</param>
    /// <returns>Matching entries.</returns>
    public IList<TemporalEntryInfo> GetByAccessPattern(AccessPattern pattern, int limit = 50)
    {
        var query = _entries.Values.AsEnumerable();

        query = pattern switch
        {
            AccessPattern.FrequentlyAccessed => query.OrderByDescending(e => e.AccessCount),
            AccessPattern.RecentlyAccessed => query.Where(e => e.LastAccessedAt.HasValue)
                .OrderByDescending(e => e.LastAccessedAt),
            AccessPattern.NeverAccessed => query.Where(e => !e.LastAccessedAt.HasValue)
                .OrderByDescending(e => e.CreatedAt),
            AccessPattern.RecentlyCreated => query.OrderByDescending(e => e.CreatedAt),
            AccessPattern.OldestCreated => query.OrderBy(e => e.CreatedAt),
            AccessPattern.RecentlyModified => query.OrderByDescending(e => e.ModifiedAt),
            _ => query.OrderByDescending(e => e.CreatedAt)
        };

        return query.Take(limit).Select(ToTemporalEntryInfo).ToList();
    }

    #endregion

    #region Private Helper Methods

    private void AddToTemporalIndex(string contentId, DateTimeOffset timestamp)
    {
        _indexLock.EnterWriteLock();
        try
        {
            // Round to minute for indexing
            var key = new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, timestamp.Minute, 0, timestamp.Offset);

            if (!_creationIndex.ContainsKey(key))
                _creationIndex[key] = new HashSet<string>();

            _creationIndex[key].Add(contentId);
        }
        finally
        {
            _indexLock.ExitWriteLock();
        }
    }

    private void RemoveFromTemporalIndex(string contentId, DateTimeOffset timestamp)
    {
        _indexLock.EnterWriteLock();
        try
        {
            var key = new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, timestamp.Minute, 0, timestamp.Offset);

            if (_creationIndex.TryGetValue(key, out var set))
            {
                set.Remove(contentId);
            }
        }
        finally
        {
            _indexLock.ExitWriteLock();
        }
    }

    private void UpdateAccessIndex(string contentId, DateTimeOffset timestamp)
    {
        _indexLock.EnterWriteLock();
        try
        {
            var key = new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, 0, 0, timestamp.Offset);

            if (!_accessIndex.ContainsKey(key))
                _accessIndex[key] = new HashSet<string>();

            _accessIndex[key].Add(contentId);
        }
        finally
        {
            _indexLock.ExitWriteLock();
        }
    }

    private void InvalidateSummaries(DateTimeOffset timestamp)
    {
        // Invalidate day, month, and year summaries
        var dayKey = $"period:{timestamp:yyyy-MM-dd}";
        var monthKey = $"period:{timestamp:yyyy-MM}";
        var yearKey = $"period:{timestamp:yyyy}";

        _summaries.TryRemove(dayKey, out _);
        _summaries.TryRemove(monthKey, out _);
        _summaries.TryRemove(yearKey, out _);
    }

    private TimeSpanSummary GetOrCreateSummary(string key, TimeRange range)
    {
        if (_summaries.TryGetValue(key, out var existing))
            return existing;

        var entries = GetEntriesInRange(range).ToList();

        var summary = new TimeSpanSummary
        {
            SummaryId = key,
            TimeRange = range,
            EntryCount = entries.Count,
            TotalBytes = entries.Sum(e => e.ContentSizeBytes),
            TopTags = entries
                .SelectMany(e => e.Tags)
                .GroupBy(t => t)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => g.Key)
                .ToArray(),
            AverageImportance = entries.Any() ? entries.Average(e => e.ImportanceScore) : 0,
            Description = GenerateSummaryDescription(entries, range),
            GeneratedAt = DateTimeOffset.UtcNow
        };

        _summaries[key] = summary;
        return summary;
    }

    private TimeRange GetPeriodRange(TimePeriod period, DateTimeOffset reference)
    {
        return period switch
        {
            TimePeriod.Daily => new TimeRange
            {
                Start = new DateTimeOffset(reference.Year, reference.Month, reference.Day, 0, 0, 0, reference.Offset),
                End = new DateTimeOffset(reference.Year, reference.Month, reference.Day, 23, 59, 59, reference.Offset)
            },
            TimePeriod.Weekly => new TimeRange
            {
                Start = reference.AddDays(-(int)reference.DayOfWeek),
                End = reference.AddDays(6 - (int)reference.DayOfWeek)
            },
            TimePeriod.Monthly => new TimeRange
            {
                Start = new DateTimeOffset(reference.Year, reference.Month, 1, 0, 0, 0, reference.Offset),
                End = new DateTimeOffset(reference.Year, reference.Month,
                    DateTime.DaysInMonth(reference.Year, reference.Month), 23, 59, 59, reference.Offset)
            },
            _ => new TimeRange { Start = reference.AddDays(-1), End = reference }
        };
    }

    private bool MatchesFilters(TemporalEntry entry, ContextQuery query)
    {
        if (query.Scope != null && !entry.Scope.Equals(query.Scope, StringComparison.OrdinalIgnoreCase))
            return false;

        if (query.RequiredTags?.Length > 0)
        {
            if (!query.RequiredTags.All(t => entry.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }

    private float CalculateRelevance(TemporalEntry entry, string[] queryWords, float[]? queryEmbedding)
    {
        var relevance = 0f;

        // Text matching
        var summaryLower = entry.Summary.ToLowerInvariant();
        var matchCount = queryWords.Count(w => summaryLower.Contains(w));
        relevance += queryWords.Length > 0 ? (float)matchCount / queryWords.Length * 0.5f : 0f;

        // Tag matching
        var tagMatches = entry.Tags.Count(t => queryWords.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase)));
        relevance += Math.Min(tagMatches * 0.1f, 0.3f);

        // Recency boost
        var age = DateTimeOffset.UtcNow - entry.CreatedAt;
        if (age.TotalDays < 1) relevance += 0.2f;
        else if (age.TotalDays < 7) relevance += 0.1f;

        // Importance
        relevance += entry.ImportanceScore * 0.2f;

        return Math.Clamp(relevance, 0f, 1f);
    }

    private IndexedContextEntry CreateEntryResult(TemporalEntry entry, float relevance, bool includePath, SummaryLevel detailLevel)
    {
        string? path = null;
        if (includePath)
        {
            path = $"temporal/{entry.CreatedAt:yyyy}/{entry.CreatedAt:MM}/{entry.CreatedAt:dd}";
        }

        return new IndexedContextEntry
        {
            ContentId = entry.ContentId,
            HierarchyPath = path,
            RelevanceScore = relevance,
            Summary = TruncateSummary(entry.Summary, detailLevel),
            SemanticTags = entry.Tags,
            ContentSizeBytes = entry.ContentSizeBytes,
            Pointer = entry.Pointer,
            CreatedAt = entry.CreatedAt,
            LastAccessedAt = entry.LastAccessedAt,
            AccessCount = entry.AccessCount,
            ImportanceScore = entry.ImportanceScore,
            Tier = entry.Tier
        };
    }

    private Dictionary<string, int>? GenerateTemporalClusters(IList<IndexedContextEntry> results)
    {
        return results
            .GroupBy(r => r.CreatedAt.ToString("yyyy-MM-dd"))
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private string GenerateTemporalNavigationSummary(TimeRange? range, IList<IndexedContextEntry> results)
    {
        if (results.Count == 0)
        {
            var rangeDesc = range != null
                ? $"between {range.Start:yyyy-MM-dd} and {range.End:yyyy-MM-dd}"
                : "in the specified period";
            return $"No entries found {rangeDesc}. Try expanding the time range.";
        }

        var oldest = results.Min(r => r.CreatedAt);
        var newest = results.Max(r => r.CreatedAt);

        return $"Found {results.Count} entries from {oldest:yyyy-MM-dd} to {newest:yyyy-MM-dd}. " +
               "Navigate by year/month/day for focused results.";
    }

    private ContextNode SummaryToNode(TimeSpanSummary summary)
    {
        return new ContextNode
        {
            NodeId = summary.SummaryId,
            ParentId = GetParentPeriodId(summary.SummaryId),
            Name = FormatPeriodName(summary.SummaryId),
            Summary = summary.Description,
            Level = GetPeriodLevel(summary.SummaryId),
            TotalEntryCount = summary.EntryCount,
            TotalSizeBytes = summary.TotalBytes,
            Tags = summary.TopTags,
            LastUpdated = summary.GeneratedAt,
            CoveredTimeRange = summary.TimeRange
        };
    }

    private static string? GetParentPeriodId(string periodId)
    {
        var parts = periodId.Replace("period:", "").Split('-');
        if (parts.Length == 3) return $"period:{parts[0]}-{parts[1]}";
        if (parts.Length == 2) return $"period:{parts[0]}";
        return null;
    }

    private static int GetPeriodLevel(string periodId)
    {
        var parts = periodId.Replace("period:", "").Split('-');
        return parts.Length; // 1=year, 2=month, 3=day
    }

    private static string FormatPeriodName(string periodId)
    {
        var period = periodId.Replace("period:", "");
        var parts = period.Split('-');

        if (parts.Length == 1) return $"Year {parts[0]}";
        if (parts.Length == 2)
        {
            var month = int.Parse(parts[1]);
            return $"{System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(month)} {parts[0]}";
        }
        if (parts.Length == 3)
        {
            return $"{parts[0]}-{parts[1]}-{parts[2]}";
        }
        return period;
    }

    private static string GenerateSummaryDescription(List<TemporalEntry> entries, TimeRange range)
    {
        if (entries.Count == 0)
            return "No entries in this period.";

        var topTags = entries
            .SelectMany(e => e.Tags)
            .GroupBy(t => t)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => g.Key);

        return $"{entries.Count} entries covering {string.Join(", ", topTags)}.";
    }

    private static double CalculateGrowthRate(List<TimeWindowStats> windows)
    {
        if (windows.Count < 2) return 0;

        var recentAvg = windows.Take(windows.Count / 2).Average(w => w.EntryCount);
        var oldAvg = windows.Skip(windows.Count / 2).Average(w => w.EntryCount);

        return oldAvg > 0 ? (recentAvg - oldAvg) / oldAvg : 0;
    }

    private static string[] DetectEmergingTopics(List<TimeWindowStats> windows)
    {
        if (windows.Count < 2) return Array.Empty<string>();

        var recentTags = windows.Take(windows.Count / 2)
            .SelectMany(w => w.TopTags)
            .GroupBy(t => t)
            .ToDictionary(g => g.Key, g => g.Count());

        var oldTags = windows.Skip(windows.Count / 2)
            .SelectMany(w => w.TopTags)
            .GroupBy(t => t)
            .ToDictionary(g => g.Key, g => g.Count());

        return recentTags
            .Where(kvp => !oldTags.ContainsKey(kvp.Key) || kvp.Value > oldTags[kvp.Key] * 2)
            .OrderByDescending(kvp => kvp.Value)
            .Take(5)
            .Select(kvp => kvp.Key)
            .ToArray();
    }

    private TemporalEntryInfo ToTemporalEntryInfo(TemporalEntry entry)
    {
        return new TemporalEntryInfo
        {
            ContentId = entry.ContentId,
            Summary = entry.Summary,
            CreatedAt = entry.CreatedAt,
            ModifiedAt = entry.ModifiedAt,
            LastAccessedAt = entry.LastAccessedAt,
            AccessCount = entry.AccessCount,
            ImportanceScore = entry.ImportanceScore
        };
    }

    private static string TruncateSummary(string summary, SummaryLevel level)
    {
        var maxLength = level switch
        {
            SummaryLevel.Minimal => 50,
            SummaryLevel.Brief => 150,
            SummaryLevel.Detailed => 500,
            SummaryLevel.Full => int.MaxValue,
            _ => 150
        };

        return summary.Length <= maxLength ? summary : summary[..maxLength] + "...";
    }

    private long EstimateIndexSize()
    {
        var entryIndexSize = _entries.Count * 256;
        var timeIndexSize = _creationIndex.Count * 64;
        var summarySize = _summaries.Count * 512;
        return entryIndexSize + timeIndexSize + summarySize;
    }

    #endregion

    #region Internal Types

    public sealed record TemporalEntry
    {
        public required string ContentId { get; init; }
        public long ContentSizeBytes { get; init; }
        public required string Summary { get; init; }
        public string[] Tags { get; init; } = Array.Empty<string>();
        public float[]? Embedding { get; init; }
        public MemoryTier Tier { get; init; }
        public required string Scope { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset ModifiedAt { get; init; }
        public DateTimeOffset? LastAccessedAt { get; init; }
        public int AccessCount { get; init; }
        public float ImportanceScore { get; init; }
        public required ContextPointer Pointer { get; init; }
    }

    #endregion

    /// <summary>
    /// Releases the ReaderWriterLockSlim held by this index.
    /// </summary>
    public void Dispose()
    {
        _indexLock.Dispose();
    }
}

#region Temporal Types

/// <summary>
/// Time period granularity.
/// </summary>
public enum TimePeriod
{
    /// <summary>Daily granularity.</summary>
    Daily,

    /// <summary>Weekly granularity.</summary>
    Weekly,

    /// <summary>Monthly granularity.</summary>
    Monthly
}

/// <summary>
/// Access pattern for filtering entries.
/// </summary>
public enum AccessPattern
{
    /// <summary>Most frequently accessed entries.</summary>
    FrequentlyAccessed,

    /// <summary>Most recently accessed entries.</summary>
    RecentlyAccessed,

    /// <summary>Entries that have never been accessed.</summary>
    NeverAccessed,

    /// <summary>Most recently created entries.</summary>
    RecentlyCreated,

    /// <summary>Oldest created entries.</summary>
    OldestCreated,

    /// <summary>Most recently modified entries.</summary>
    RecentlyModified
}

/// <summary>
/// Summary of a time span.
/// </summary>
public record TimeSpanSummary
{
    /// <summary>Summary identifier.</summary>
    public required string SummaryId { get; init; }

    /// <summary>Time range covered.</summary>
    public required TimeRange TimeRange { get; init; }

    /// <summary>Number of entries in this period.</summary>
    public long EntryCount { get; init; }

    /// <summary>Total bytes of content.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Top tags in this period.</summary>
    public string[] TopTags { get; init; } = Array.Empty<string>();

    /// <summary>Average importance of entries.</summary>
    public double AverageImportance { get; init; }

    /// <summary>Description of the period.</summary>
    public required string Description { get; init; }

    /// <summary>When this summary was generated.</summary>
    public DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>
/// Result of getting changes since a time.
/// </summary>
public record ChangesSinceResult
{
    /// <summary>Reference time.</summary>
    public DateTimeOffset Since { get; init; }

    /// <summary>Entries created since the reference time.</summary>
    public IList<TemporalEntryInfo> CreatedEntries { get; init; } = Array.Empty<TemporalEntryInfo>();

    /// <summary>Entries modified since the reference time.</summary>
    public IList<TemporalEntryInfo> ModifiedEntries { get; init; } = Array.Empty<TemporalEntryInfo>();

    /// <summary>Entries accessed since the reference time.</summary>
    public IList<TemporalEntryInfo> AccessedEntries { get; init; } = Array.Empty<TemporalEntryInfo>();

    /// <summary>Total number of changes.</summary>
    public int TotalChanges { get; init; }
}

/// <summary>
/// Information about a temporal entry.
/// </summary>
public record TemporalEntryInfo
{
    /// <summary>Content identifier.</summary>
    public required string ContentId { get; init; }

    /// <summary>Content summary.</summary>
    public required string Summary { get; init; }

    /// <summary>When created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When last modified.</summary>
    public DateTimeOffset ModifiedAt { get; init; }

    /// <summary>When last accessed.</summary>
    public DateTimeOffset? LastAccessedAt { get; init; }

    /// <summary>Number of accesses.</summary>
    public int AccessCount { get; init; }

    /// <summary>Importance score.</summary>
    public float ImportanceScore { get; init; }
}

/// <summary>
/// Trend analysis results.
/// </summary>
public record TrendAnalysis
{
    /// <summary>Analysis start time.</summary>
    public DateTimeOffset AnalyzedFrom { get; init; }

    /// <summary>Analysis end time.</summary>
    public DateTimeOffset AnalyzedTo { get; init; }

    /// <summary>Window size used.</summary>
    public TimeSpan WindowSize { get; init; }

    /// <summary>Statistics for each time window.</summary>
    public IList<TimeWindowStats> Windows { get; init; } = Array.Empty<TimeWindowStats>();

    /// <summary>Overall growth rate.</summary>
    public double OverallGrowthRate { get; init; }

    /// <summary>Peak activity window.</summary>
    public TimeWindowStats? PeakWindow { get; init; }

    /// <summary>Topics that are emerging.</summary>
    public string[] EmergingTopics { get; init; } = Array.Empty<string>();

    /// <summary>Whether content is growing.</summary>
    public bool IsGrowing { get; init; }

    /// <summary>Whether content is declining.</summary>
    public bool IsDeclining { get; init; }
}

/// <summary>
/// Statistics for a time window.
/// </summary>
public record TimeWindowStats
{
    /// <summary>Window start time.</summary>
    public DateTimeOffset WindowStart { get; init; }

    /// <summary>Window end time.</summary>
    public DateTimeOffset WindowEnd { get; init; }

    /// <summary>Number of entries in this window.</summary>
    public int EntryCount { get; init; }

    /// <summary>Total bytes in this window.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Top tags in this window.</summary>
    public string[] TopTags { get; init; } = Array.Empty<string>();

    /// <summary>Average importance of entries.</summary>
    public float AverageImportance { get; init; }
}

/// <summary>
/// Summary of recent activity.
/// </summary>
public record RecentActivitySummary
{
    /// <summary>Start of the analysis period.</summary>
    public DateTimeOffset SinceTime { get; init; }

    /// <summary>Number of hours analyzed.</summary>
    public int HoursAnalyzed { get; init; }

    /// <summary>Entries created in the period.</summary>
    public int EntriesCreated { get; init; }

    /// <summary>Entries modified in the period.</summary>
    public int EntriesModified { get; init; }

    /// <summary>Entries accessed in the period.</summary>
    public int EntriesAccessed { get; init; }

    /// <summary>Most active entries.</summary>
    public IList<TemporalEntryInfo> MostActiveEntries { get; init; } = Array.Empty<TemporalEntryInfo>();

    /// <summary>Hourly activity breakdown.</summary>
    public IList<HourlyActivity> HourlyBreakdown { get; init; } = Array.Empty<HourlyActivity>();
}

/// <summary>
/// Activity in a specific hour.
/// </summary>
public record HourlyActivity
{
    /// <summary>The hour.</summary>
    public DateTimeOffset Hour { get; init; }

    /// <summary>Entries created.</summary>
    public int Created { get; init; }

    /// <summary>Entries accessed.</summary>
    public int Accessed { get; init; }
}

#endregion
