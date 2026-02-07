using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Indexing;

/// <summary>
/// Time-series indexing strategy with B-tree on timestamps.
/// Provides O(log n) time-range queries with aggregation and downsampling support.
/// </summary>
/// <remarks>
/// Features:
/// - Time-range queries with O(log n) performance
/// - B-tree index on timestamps
/// - Downsampling for aggregations (min, max, avg, sum, count)
/// - Retention policy integration
/// - Time bucket aggregations (hour, day, week, month, year)
/// - Support for both point-in-time and range queries
/// - Automatic index optimization
/// </remarks>
public sealed class TemporalIndexStrategy : IndexingStrategyBase
{
    /// <summary>
    /// Document storage by object ID.
    /// </summary>
    private readonly ConcurrentDictionary<string, TemporalDocument> _documents = new();

    /// <summary>
    /// B-tree index: timestamp ticks -> list of object IDs at that timestamp.
    /// </summary>
    private readonly SortedList<long, List<string>> _timeIndex = new();

    /// <summary>
    /// Lock for B-tree modifications.
    /// </summary>
    private readonly object _indexLock = new();

    /// <summary>
    /// Downsampled data caches for different granularities.
    /// </summary>
    private readonly ConcurrentDictionary<TimeBucket, ConcurrentDictionary<long, AggregatedBucket>> _downsampledCache = new();

    /// <summary>
    /// Current retention policy.
    /// </summary>
    private readonly RetentionPolicy _retentionPolicy;

    /// <summary>
    /// Timer for periodic cleanup.
    /// </summary>
    private readonly System.Timers.Timer? _cleanupTimer;

    /// <summary>
    /// Defines time bucket granularities for aggregation.
    /// </summary>
    public enum TimeBucket
    {
        /// <summary>One second granularity.</summary>
        Second,
        /// <summary>One minute granularity.</summary>
        Minute,
        /// <summary>One hour granularity.</summary>
        Hour,
        /// <summary>One day granularity.</summary>
        Day,
        /// <summary>One week granularity.</summary>
        Week,
        /// <summary>One month granularity.</summary>
        Month,
        /// <summary>One quarter granularity.</summary>
        Quarter,
        /// <summary>One year granularity.</summary>
        Year
    }

    /// <summary>
    /// Defines retention policy for temporal data.
    /// </summary>
    public sealed class RetentionPolicy
    {
        /// <summary>Gets the maximum age for raw data retention.</summary>
        public TimeSpan RawDataRetention { get; init; } = TimeSpan.FromDays(30);

        /// <summary>Gets the maximum age for minute aggregates.</summary>
        public TimeSpan MinuteAggregateRetention { get; init; } = TimeSpan.FromDays(90);

        /// <summary>Gets the maximum age for hourly aggregates.</summary>
        public TimeSpan HourlyAggregateRetention { get; init; } = TimeSpan.FromDays(365);

        /// <summary>Gets the maximum age for daily aggregates.</summary>
        public TimeSpan DailyAggregateRetention { get; init; } = TimeSpan.FromDays(365 * 5);

        /// <summary>Whether to enable automatic cleanup.</summary>
        public bool EnableAutoCleanup { get; init; } = true;

        /// <summary>Cleanup interval.</summary>
        public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromHours(1);
    }

    /// <summary>
    /// Represents an aggregated time bucket.
    /// </summary>
    public sealed class AggregatedBucket
    {
        /// <summary>Gets the bucket start time.</summary>
        public DateTime BucketStart { get; init; }

        /// <summary>Gets the bucket end time.</summary>
        public DateTime BucketEnd { get; init; }

        /// <summary>Gets the count of data points.</summary>
        public long Count { get; set; }

        /// <summary>Gets the sum of values.</summary>
        public double Sum { get; set; }

        /// <summary>Gets the minimum value.</summary>
        public double Min { get; set; } = double.MaxValue;

        /// <summary>Gets the maximum value.</summary>
        public double Max { get; set; } = double.MinValue;

        /// <summary>Gets the first value in the bucket.</summary>
        public double First { get; set; }

        /// <summary>Gets the last value in the bucket.</summary>
        public double Last { get; set; }

        /// <summary>Gets the timestamp of the first value.</summary>
        public DateTime FirstTimestamp { get; set; }

        /// <summary>Gets the timestamp of the last value.</summary>
        public DateTime LastTimestamp { get; set; }

        /// <summary>Gets the average value.</summary>
        public double Average => Count > 0 ? Sum / Count : 0;

        /// <summary>
        /// Adds a data point to the aggregation.
        /// </summary>
        /// <param name="value">The value to add.</param>
        /// <param name="timestamp">The timestamp of the value.</param>
        public void AddDataPoint(double value, DateTime timestamp)
        {
            if (Count == 0)
            {
                First = value;
                FirstTimestamp = timestamp;
            }

            Count++;
            Sum += value;
            Min = Math.Min(Min, value);
            Max = Math.Max(Max, value);
            Last = value;
            LastTimestamp = timestamp;
        }
    }

    /// <summary>
    /// A temporally indexed document.
    /// </summary>
    private sealed class TemporalDocument
    {
        public required string ObjectId { get; init; }
        public required DateTime Timestamp { get; init; }
        public required double? Value { get; init; }
        public required string? SeriesId { get; init; }
        public required Dictionary<string, object>? Metadata { get; init; }
        public required string? TextContent { get; init; }
        public DateTime IndexedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Represents a time range for queries.
    /// </summary>
    public readonly struct TimeRange
    {
        /// <summary>Gets the start of the range.</summary>
        public DateTime Start { get; }

        /// <summary>Gets the end of the range.</summary>
        public DateTime End { get; }

        /// <summary>Gets whether the start is inclusive.</summary>
        public bool StartInclusive { get; }

        /// <summary>Gets whether the end is inclusive.</summary>
        public bool EndInclusive { get; }

        /// <summary>
        /// Initializes a new TimeRange.
        /// </summary>
        /// <param name="start">Range start.</param>
        /// <param name="end">Range end.</param>
        /// <param name="startInclusive">Whether start is inclusive.</param>
        /// <param name="endInclusive">Whether end is inclusive.</param>
        public TimeRange(DateTime start, DateTime end, bool startInclusive = true, bool endInclusive = true)
        {
            Start = start;
            End = end;
            StartInclusive = startInclusive;
            EndInclusive = endInclusive;
        }

        /// <summary>
        /// Creates a range for the last N units of time.
        /// </summary>
        public static TimeRange Last(TimeSpan duration) =>
            new(DateTime.UtcNow - duration, DateTime.UtcNow);

        /// <summary>
        /// Creates a range for a specific day.
        /// </summary>
        public static TimeRange ForDay(DateTime date) =>
            new(date.Date, date.Date.AddDays(1).AddTicks(-1));

        /// <summary>
        /// Creates a range for a specific month.
        /// </summary>
        public static TimeRange ForMonth(int year, int month) =>
            new(new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1).AddTicks(-1));

        /// <summary>
        /// Checks if a timestamp falls within this range.
        /// </summary>
        public bool Contains(DateTime timestamp)
        {
            var afterStart = StartInclusive ? timestamp >= Start : timestamp > Start;
            var beforeEnd = EndInclusive ? timestamp <= End : timestamp < End;
            return afterStart && beforeEnd;
        }
    }

    /// <summary>
    /// Initializes a new TemporalIndexStrategy with default retention policy.
    /// </summary>
    public TemporalIndexStrategy() : this(new RetentionPolicy()) { }

    /// <summary>
    /// Initializes a new TemporalIndexStrategy with custom retention policy.
    /// </summary>
    /// <param name="retentionPolicy">The retention policy to apply.</param>
    public TemporalIndexStrategy(RetentionPolicy retentionPolicy)
    {
        _retentionPolicy = retentionPolicy;

        // Initialize downsampled caches
        foreach (TimeBucket bucket in Enum.GetValues<TimeBucket>())
        {
            _downsampledCache[bucket] = new ConcurrentDictionary<long, AggregatedBucket>();
        }

        // Setup automatic cleanup if enabled
        if (_retentionPolicy.EnableAutoCleanup)
        {
            _cleanupTimer = new System.Timers.Timer(_retentionPolicy.CleanupInterval.TotalMilliseconds);
            _cleanupTimer.Elapsed += (_, _) => ApplyRetentionPolicy();
            _cleanupTimer.AutoReset = true;
            _cleanupTimer.Start();
        }
    }

    /// <inheritdoc/>
    public override string StrategyId => "index.temporal";

    /// <inheritdoc/>
    public override string DisplayName => "Temporal Index (B-tree)";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = true,
        MaxThroughput = 100_000,
        TypicalLatencyMs = 0.2
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Time-series index using B-tree for efficient temporal queries. " +
        "Supports range queries, aggregations, and downsampling with retention policies. " +
        "Best for time-series data, logs, events, and metrics.";

    /// <inheritdoc/>
    public override string[] Tags => ["index", "temporal", "timeseries", "btree", "time", "aggregation", "retention"];

    /// <inheritdoc/>
    public override long GetDocumentCount() => _documents.Count;

    /// <inheritdoc/>
    public override long GetIndexSize()
    {
        long size = 0;
        lock (_indexLock)
        {
            size += _timeIndex.Count * 16; // Timestamp keys
            foreach (var list in _timeIndex.Values)
            {
                size += list.Count * 40; // Object ID references
            }
        }

        // Add downsampled cache size
        foreach (var cache in _downsampledCache.Values)
        {
            size += cache.Count * 100;
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
        lock (_indexLock)
        {
            _timeIndex.Clear();
        }
        foreach (var cache in _downsampledCache.Values)
        {
            cache.Clear();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task OptimizeAsync(CancellationToken ct = default)
    {
        // Apply retention policy
        ApplyRetentionPolicy();

        // Rebuild downsampled caches
        RebuildDownsampledCaches();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task<IndexResult> IndexCoreAsync(string objectId, IndexableContent content, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Extract timestamp from metadata
        DateTime timestamp = DateTime.UtcNow;
        double? value = null;
        string? seriesId = null;

        if (content.Metadata != null)
        {
            if (content.Metadata.TryGetValue("timestamp", out var tsObj))
            {
                timestamp = ParseTimestamp(tsObj);
            }
            else if (content.Metadata.TryGetValue("time", out tsObj))
            {
                timestamp = ParseTimestamp(tsObj);
            }
            else if (content.Metadata.TryGetValue("datetime", out tsObj))
            {
                timestamp = ParseTimestamp(tsObj);
            }
            else if (content.Metadata.TryGetValue("date", out tsObj))
            {
                timestamp = ParseTimestamp(tsObj);
            }
            else if (content.Metadata.TryGetValue("_timestamp", out tsObj))
            {
                timestamp = ParseTimestamp(tsObj);
            }

            if (content.Metadata.TryGetValue("value", out var valObj))
            {
                value = Convert.ToDouble(valObj);
            }
            else if (content.Metadata.TryGetValue("metric", out valObj))
            {
                value = Convert.ToDouble(valObj);
            }
            else if (content.Metadata.TryGetValue("measurement", out valObj))
            {
                value = Convert.ToDouble(valObj);
            }

            if (content.Metadata.TryGetValue("series", out var serObj) && serObj is string ser)
            {
                seriesId = ser;
            }
            else if (content.Metadata.TryGetValue("seriesId", out serObj) && serObj is string serId)
            {
                seriesId = serId;
            }
        }

        // Remove existing document if re-indexing
        if (_documents.TryRemove(objectId, out var existingDoc))
        {
            RemoveFromTimeIndex(objectId, existingDoc.Timestamp);
        }

        // Create document
        var doc = new TemporalDocument
        {
            ObjectId = objectId,
            Timestamp = timestamp,
            Value = value,
            SeriesId = seriesId,
            Metadata = content.Metadata,
            TextContent = content.TextContent
        };

        _documents[objectId] = doc;

        // Add to time index
        AddToTimeIndex(objectId, timestamp);

        // Update downsampled caches
        if (value.HasValue)
        {
            UpdateDownsampledCaches(timestamp, value.Value);
        }

        sw.Stop();
        return Task.FromResult(IndexResult.Ok(1, 1, sw.Elapsed));
    }

    /// <inheritdoc/>
    protected override Task<IReadOnlyList<IndexSearchResult>> SearchCoreAsync(
        string query,
        IndexSearchOptions options,
        CancellationToken ct)
    {
        var results = new List<IndexSearchResult>();
        var queryParams = ParseSearchQuery(query);

        // Determine query type
        if (queryParams.TryGetValue("type", out var queryType))
        {
            switch (queryType.ToLowerInvariant())
            {
                case "range":
                    results = SearchTimeRange(queryParams, options);
                    break;
                case "point":
                    results = SearchPointInTime(queryParams, options);
                    break;
                case "aggregate":
                    results = SearchAggregated(queryParams, options);
                    break;
                case "latest":
                    results = SearchLatest(queryParams, options);
                    break;
                case "earliest":
                    results = SearchEarliest(queryParams, options);
                    break;
                default:
                    results = SearchTimeRange(queryParams, options);
                    break;
            }
        }
        else if (queryParams.ContainsKey("start") || queryParams.ContainsKey("end"))
        {
            results = SearchTimeRange(queryParams, options);
        }
        else if (queryParams.ContainsKey("at") || queryParams.ContainsKey("timestamp"))
        {
            results = SearchPointInTime(queryParams, options);
        }
        else if (queryParams.ContainsKey("bucket") || queryParams.ContainsKey("granularity"))
        {
            results = SearchAggregated(queryParams, options);
        }
        else
        {
            // Default: return latest documents
            results = SearchLatest(queryParams, options);
        }

        return Task.FromResult<IReadOnlyList<IndexSearchResult>>(results);
    }

    /// <summary>
    /// Searches for documents within a time range.
    /// </summary>
    /// <param name="params">Query parameters containing start and end times.</param>
    /// <param name="options">Search options.</param>
    /// <returns>List of matching results ordered by timestamp.</returns>
    public List<IndexSearchResult> SearchTimeRange(Dictionary<string, string> @params, IndexSearchOptions options)
    {
        DateTime start = DateTime.MinValue;
        DateTime end = DateTime.MaxValue;

        if (@params.TryGetValue("start", out var startStr))
            start = ParseTimestampString(startStr);
        if (@params.TryGetValue("end", out var endStr))
            end = ParseTimestampString(endStr);
        if (@params.TryGetValue("from", out startStr))
            start = ParseTimestampString(startStr);
        if (@params.TryGetValue("to", out endStr))
            end = ParseTimestampString(endStr);

        // Support relative time
        if (@params.TryGetValue("last", out var lastStr))
        {
            var duration = ParseDuration(lastStr);
            start = DateTime.UtcNow - duration;
            end = DateTime.UtcNow;
        }

        string? seriesFilter = null;
        if (@params.TryGetValue("series", out var serStr))
            seriesFilter = serStr;

        var matchingIds = new List<(string Id, DateTime Timestamp)>();

        lock (_indexLock)
        {
            var startTicks = start.Ticks;
            var endTicks = end.Ticks;

            // Binary search for start position
            int startIndex = BinarySearchStartIndex(startTicks);

            for (int i = startIndex; i < _timeIndex.Count && _timeIndex.Keys[i] <= endTicks; i++)
            {
                var timestamp = new DateTime(_timeIndex.Keys[i], DateTimeKind.Utc);
                foreach (var objectId in _timeIndex.Values[i])
                {
                    matchingIds.Add((objectId, timestamp));
                }
            }
        }

        var results = matchingIds
            .Where(m => _documents.ContainsKey(m.Id))
            .Select(m => _documents[m.Id])
            .Where(doc => seriesFilter == null || doc.SeriesId == seriesFilter)
            .OrderByDescending(doc => doc.Timestamp)
            .Take(options.MaxResults)
            .Select(doc => CreateSearchResult(doc))
            .Where(r => r.Score >= options.MinScore)
            .ToList();

        return results;
    }

    /// <summary>
    /// Searches for documents at or near a specific point in time.
    /// </summary>
    /// <param name="params">Query parameters containing the target timestamp.</param>
    /// <param name="options">Search options.</param>
    /// <returns>Documents closest to the specified time.</returns>
    public List<IndexSearchResult> SearchPointInTime(Dictionary<string, string> @params, IndexSearchOptions options)
    {
        DateTime targetTime = DateTime.UtcNow;
        TimeSpan tolerance = TimeSpan.FromMinutes(1);

        if (@params.TryGetValue("at", out var atStr))
            targetTime = ParseTimestampString(atStr);
        if (@params.TryGetValue("timestamp", out var tsStr))
            targetTime = ParseTimestampString(tsStr);
        if (@params.TryGetValue("tolerance", out var tolStr))
            tolerance = ParseDuration(tolStr);

        var range = new TimeRange(targetTime - tolerance, targetTime + tolerance);

        var rangeParams = new Dictionary<string, string>
        {
            ["start"] = range.Start.ToString("O"),
            ["end"] = range.End.ToString("O")
        };

        if (@params.TryGetValue("series", out var serStr))
            rangeParams["series"] = serStr;

        var results = SearchTimeRange(rangeParams, options);

        // Sort by proximity to target time
        return results
            .OrderBy(r =>
            {
                if (r.Metadata?.TryGetValue("_timestamp", out var ts) == true && ts is DateTime dt)
                    return Math.Abs((dt - targetTime).TotalMilliseconds);
                return double.MaxValue;
            })
            .ToList();
    }

    /// <summary>
    /// Searches for aggregated data over time buckets.
    /// </summary>
    /// <param name="params">Query parameters containing bucket size and time range.</param>
    /// <param name="options">Search options.</param>
    /// <returns>Aggregated results for each time bucket.</returns>
    public List<IndexSearchResult> SearchAggregated(Dictionary<string, string> @params, IndexSearchOptions options)
    {
        TimeBucket bucket = TimeBucket.Hour;
        DateTime start = DateTime.UtcNow.AddDays(-1);
        DateTime end = DateTime.UtcNow;

        if (@params.TryGetValue("bucket", out var bucketStr) ||
            @params.TryGetValue("granularity", out bucketStr))
        {
            bucket = ParseBucket(bucketStr);
        }

        if (@params.TryGetValue("start", out var startStr))
            start = ParseTimestampString(startStr);
        if (@params.TryGetValue("end", out var endStr))
            end = ParseTimestampString(endStr);
        if (@params.TryGetValue("last", out var lastStr))
        {
            var duration = ParseDuration(lastStr);
            start = DateTime.UtcNow - duration;
            end = DateTime.UtcNow;
        }

        string? seriesFilter = null;
        if (@params.TryGetValue("series", out var serStr))
            seriesFilter = serStr;

        // Get or compute aggregations
        var aggregations = GetAggregations(bucket, start, end, seriesFilter);

        return aggregations
            .OrderByDescending(a => a.BucketStart)
            .Take(options.MaxResults)
            .Select(agg => new IndexSearchResult
            {
                ObjectId = $"agg_{bucket}_{agg.BucketStart:yyyyMMddHHmmss}",
                Score = 1.0,
                Snippet = $"Count: {agg.Count}, Avg: {agg.Average:F2}, Min: {agg.Min:F2}, Max: {agg.Max:F2}",
                Metadata = new Dictionary<string, object>
                {
                    ["_bucketStart"] = agg.BucketStart,
                    ["_bucketEnd"] = agg.BucketEnd,
                    ["_count"] = agg.Count,
                    ["_sum"] = agg.Sum,
                    ["_min"] = agg.Min,
                    ["_max"] = agg.Max,
                    ["_avg"] = agg.Average,
                    ["_first"] = agg.First,
                    ["_last"] = agg.Last,
                    ["_firstTimestamp"] = agg.FirstTimestamp,
                    ["_lastTimestamp"] = agg.LastTimestamp
                }
            })
            .ToList();
    }

    /// <summary>
    /// Searches for the latest documents.
    /// </summary>
    /// <param name="params">Query parameters.</param>
    /// <param name="options">Search options.</param>
    /// <returns>Most recent documents.</returns>
    public List<IndexSearchResult> SearchLatest(Dictionary<string, string> @params, IndexSearchOptions options)
    {
        string? seriesFilter = null;
        if (@params.TryGetValue("series", out var serStr))
            seriesFilter = serStr;

        return _documents.Values
            .Where(doc => seriesFilter == null || doc.SeriesId == seriesFilter)
            .OrderByDescending(doc => doc.Timestamp)
            .Take(options.MaxResults)
            .Select(doc => CreateSearchResult(doc))
            .Where(r => r.Score >= options.MinScore)
            .ToList();
    }

    /// <summary>
    /// Searches for the earliest documents.
    /// </summary>
    /// <param name="params">Query parameters.</param>
    /// <param name="options">Search options.</param>
    /// <returns>Oldest documents.</returns>
    public List<IndexSearchResult> SearchEarliest(Dictionary<string, string> @params, IndexSearchOptions options)
    {
        string? seriesFilter = null;
        if (@params.TryGetValue("series", out var serStr))
            seriesFilter = serStr;

        return _documents.Values
            .Where(doc => seriesFilter == null || doc.SeriesId == seriesFilter)
            .OrderBy(doc => doc.Timestamp)
            .Take(options.MaxResults)
            .Select(doc => CreateSearchResult(doc))
            .Where(r => r.Score >= options.MinScore)
            .ToList();
    }

    /// <summary>
    /// Gets aggregations for a time range at a specific bucket granularity.
    /// </summary>
    /// <param name="bucket">The bucket granularity.</param>
    /// <param name="start">Start of the range.</param>
    /// <param name="end">End of the range.</param>
    /// <param name="seriesFilter">Optional series filter.</param>
    /// <returns>List of aggregated buckets.</returns>
    public List<AggregatedBucket> GetAggregations(TimeBucket bucket, DateTime start, DateTime end, string? seriesFilter = null)
    {
        var results = new Dictionary<long, AggregatedBucket>();

        foreach (var doc in _documents.Values)
        {
            if (doc.Timestamp < start || doc.Timestamp > end)
                continue;
            if (seriesFilter != null && doc.SeriesId != seriesFilter)
                continue;
            if (!doc.Value.HasValue)
                continue;

            var bucketStart = TruncateToBucket(doc.Timestamp, bucket);
            var bucketKey = bucketStart.Ticks;

            if (!results.TryGetValue(bucketKey, out var agg))
            {
                agg = new AggregatedBucket
                {
                    BucketStart = bucketStart,
                    BucketEnd = GetBucketEnd(bucketStart, bucket)
                };
                results[bucketKey] = agg;
            }

            agg.AddDataPoint(doc.Value.Value, doc.Timestamp);
        }

        return results.Values.OrderBy(a => a.BucketStart).ToList();
    }

    /// <inheritdoc/>
    protected override Task<bool> RemoveCoreAsync(string objectId, CancellationToken ct)
    {
        if (!_documents.TryRemove(objectId, out var doc))
            return Task.FromResult(false);

        RemoveFromTimeIndex(objectId, doc.Timestamp);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Adds an object ID to the time index.
    /// </summary>
    private void AddToTimeIndex(string objectId, DateTime timestamp)
    {
        lock (_indexLock)
        {
            var ticks = timestamp.Ticks;
            if (!_timeIndex.TryGetValue(ticks, out var list))
            {
                list = new List<string>();
                _timeIndex[ticks] = list;
            }
            list.Add(objectId);
        }
    }

    /// <summary>
    /// Removes an object ID from the time index.
    /// </summary>
    private void RemoveFromTimeIndex(string objectId, DateTime timestamp)
    {
        lock (_indexLock)
        {
            var ticks = timestamp.Ticks;
            if (_timeIndex.TryGetValue(ticks, out var list))
            {
                list.Remove(objectId);
                if (list.Count == 0)
                {
                    _timeIndex.Remove(ticks);
                }
            }
        }
    }

    /// <summary>
    /// Binary search for the starting index in the time index.
    /// </summary>
    private int BinarySearchStartIndex(long targetTicks)
    {
        var keys = _timeIndex.Keys;
        int left = 0;
        int right = keys.Count - 1;

        while (left <= right)
        {
            int mid = left + (right - left) / 2;
            if (keys[mid] < targetTicks)
                left = mid + 1;
            else
                right = mid - 1;
        }

        return left;
    }

    /// <summary>
    /// Truncates a timestamp to the start of its bucket.
    /// </summary>
    private static DateTime TruncateToBucket(DateTime timestamp, TimeBucket bucket)
    {
        return bucket switch
        {
            TimeBucket.Second => new DateTime(timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, timestamp.Minute, timestamp.Second, DateTimeKind.Utc),
            TimeBucket.Minute => new DateTime(timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, timestamp.Minute, 0, DateTimeKind.Utc),
            TimeBucket.Hour => new DateTime(timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, 0, 0, DateTimeKind.Utc),
            TimeBucket.Day => new DateTime(timestamp.Year, timestamp.Month, timestamp.Day,
                0, 0, 0, DateTimeKind.Utc),
            TimeBucket.Week => timestamp.Date.AddDays(-(int)timestamp.DayOfWeek),
            TimeBucket.Month => new DateTime(timestamp.Year, timestamp.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            TimeBucket.Quarter => new DateTime(timestamp.Year, ((timestamp.Month - 1) / 3) * 3 + 1, 1, 0, 0, 0, DateTimeKind.Utc),
            TimeBucket.Year => new DateTime(timestamp.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => timestamp
        };
    }

    /// <summary>
    /// Gets the end time for a bucket.
    /// </summary>
    private static DateTime GetBucketEnd(DateTime bucketStart, TimeBucket bucket)
    {
        return bucket switch
        {
            TimeBucket.Second => bucketStart.AddSeconds(1).AddTicks(-1),
            TimeBucket.Minute => bucketStart.AddMinutes(1).AddTicks(-1),
            TimeBucket.Hour => bucketStart.AddHours(1).AddTicks(-1),
            TimeBucket.Day => bucketStart.AddDays(1).AddTicks(-1),
            TimeBucket.Week => bucketStart.AddDays(7).AddTicks(-1),
            TimeBucket.Month => bucketStart.AddMonths(1).AddTicks(-1),
            TimeBucket.Quarter => bucketStart.AddMonths(3).AddTicks(-1),
            TimeBucket.Year => bucketStart.AddYears(1).AddTicks(-1),
            _ => bucketStart
        };
    }

    /// <summary>
    /// Updates downsampled caches with a new data point.
    /// </summary>
    private void UpdateDownsampledCaches(DateTime timestamp, double value)
    {
        foreach (var bucket in Enum.GetValues<TimeBucket>())
        {
            var bucketStart = TruncateToBucket(timestamp, bucket);
            var bucketKey = bucketStart.Ticks;
            var cache = _downsampledCache[bucket];

            var agg = cache.GetOrAdd(bucketKey, _ => new AggregatedBucket
            {
                BucketStart = bucketStart,
                BucketEnd = GetBucketEnd(bucketStart, bucket)
            });

            lock (agg)
            {
                agg.AddDataPoint(value, timestamp);
            }
        }
    }

    /// <summary>
    /// Rebuilds all downsampled caches from raw data.
    /// </summary>
    private void RebuildDownsampledCaches()
    {
        foreach (var cache in _downsampledCache.Values)
        {
            cache.Clear();
        }

        foreach (var doc in _documents.Values)
        {
            if (doc.Value.HasValue)
            {
                UpdateDownsampledCaches(doc.Timestamp, doc.Value.Value);
            }
        }
    }

    /// <summary>
    /// Applies the retention policy to remove old data.
    /// </summary>
    private void ApplyRetentionPolicy()
    {
        var now = DateTime.UtcNow;
        var cutoff = now - _retentionPolicy.RawDataRetention;

        var expiredIds = _documents.Values
            .Where(d => d.Timestamp < cutoff)
            .Select(d => d.ObjectId)
            .ToList();

        foreach (var id in expiredIds)
        {
            if (_documents.TryRemove(id, out var doc))
            {
                RemoveFromTimeIndex(id, doc.Timestamp);
            }
        }

        // Clean up old aggregations
        CleanupAggregationCache(TimeBucket.Minute, _retentionPolicy.MinuteAggregateRetention);
        CleanupAggregationCache(TimeBucket.Hour, _retentionPolicy.HourlyAggregateRetention);
        CleanupAggregationCache(TimeBucket.Day, _retentionPolicy.DailyAggregateRetention);
    }

    /// <summary>
    /// Cleans up aggregation cache based on retention.
    /// </summary>
    private void CleanupAggregationCache(TimeBucket bucket, TimeSpan retention)
    {
        var cutoff = DateTime.UtcNow - retention;
        var cache = _downsampledCache[bucket];

        var expiredKeys = cache.Where(kvp => kvp.Value.BucketEnd < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            cache.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Parses a timestamp from various object types.
    /// </summary>
    private static DateTime ParseTimestamp(object obj)
    {
        return obj switch
        {
            DateTime dt => dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime(),
            DateTimeOffset dto => dto.UtcDateTime,
            long ticks => new DateTime(ticks, DateTimeKind.Utc),
            double unixTime => DateTimeOffset.FromUnixTimeMilliseconds((long)unixTime).UtcDateTime,
            string s => ParseTimestampString(s),
            _ => DateTime.UtcNow
        };
    }

    /// <summary>
    /// Parses a timestamp from a string.
    /// </summary>
    private static DateTime ParseTimestampString(string s)
    {
        if (DateTime.TryParse(s, out var dt))
            return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();

        if (long.TryParse(s, out var ticks))
        {
            // Could be Unix timestamp (seconds or milliseconds) or ticks
            if (ticks > 1000000000000)
                return DateTimeOffset.FromUnixTimeMilliseconds(ticks).UtcDateTime;
            if (ticks > 1000000000)
                return DateTimeOffset.FromUnixTimeSeconds(ticks).UtcDateTime;
        }

        return DateTime.UtcNow;
    }

    /// <summary>
    /// Parses a duration string (e.g., "1h", "30m", "7d").
    /// </summary>
    private static TimeSpan ParseDuration(string s)
    {
        if (TimeSpan.TryParse(s, out var ts))
            return ts;

        var value = 0.0;
        var unit = 'h';

        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsDigit(s[i]) || s[i] == '.')
            {
                var numStr = "";
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.'))
                {
                    numStr += s[i++];
                }
                value = double.TryParse(numStr, out var v) ? v : 0;
                if (i < s.Length)
                    unit = char.ToLower(s[i]);
                break;
            }
        }

        return unit switch
        {
            's' => TimeSpan.FromSeconds(value),
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            'd' => TimeSpan.FromDays(value),
            'w' => TimeSpan.FromDays(value * 7),
            _ => TimeSpan.FromHours(value)
        };
    }

    /// <summary>
    /// Parses a bucket granularity string.
    /// </summary>
    private static TimeBucket ParseBucket(string s)
    {
        return s.ToLowerInvariant() switch
        {
            "s" or "sec" or "second" or "seconds" => TimeBucket.Second,
            "m" or "min" or "minute" or "minutes" => TimeBucket.Minute,
            "h" or "hr" or "hour" or "hours" => TimeBucket.Hour,
            "d" or "day" or "days" => TimeBucket.Day,
            "w" or "wk" or "week" or "weeks" => TimeBucket.Week,
            "mo" or "month" or "months" => TimeBucket.Month,
            "q" or "qtr" or "quarter" or "quarters" => TimeBucket.Quarter,
            "y" or "yr" or "year" or "years" => TimeBucket.Year,
            _ => TimeBucket.Hour
        };
    }

    /// <summary>
    /// Parses a search query string into parameters.
    /// </summary>
    private static Dictionary<string, string> ParseSearchQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var parts = query.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var colonIndex = part.IndexOf(':');
            if (colonIndex > 0 && colonIndex < part.Length - 1)
            {
                var key = part[..colonIndex];
                var value = part[(colonIndex + 1)..];
                result[key] = value;
            }
            else if (part.Contains('='))
            {
                var eqIndex = part.IndexOf('=');
                if (eqIndex > 0 && eqIndex < part.Length - 1)
                {
                    var key = part[..eqIndex];
                    var value = part[(eqIndex + 1)..];
                    result[key] = value;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Creates a search result from a temporal document.
    /// </summary>
    private static IndexSearchResult CreateSearchResult(TemporalDocument doc)
    {
        var metadata = new Dictionary<string, object>(doc.Metadata ?? new Dictionary<string, object>())
        {
            ["_timestamp"] = doc.Timestamp,
            ["_indexedAt"] = doc.IndexedAt
        };

        if (doc.Value.HasValue)
            metadata["_value"] = doc.Value.Value;
        if (doc.SeriesId != null)
            metadata["_series"] = doc.SeriesId;

        return new IndexSearchResult
        {
            ObjectId = doc.ObjectId,
            Score = 1.0,
            Snippet = doc.TextContent ?? $"Timestamp: {doc.Timestamp:O}" + (doc.Value.HasValue ? $", Value: {doc.Value}" : ""),
            Metadata = metadata
        };
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _cleanupTimer?.Stop();
        _cleanupTimer?.Dispose();
        return Task.CompletedTask;
    }
}
