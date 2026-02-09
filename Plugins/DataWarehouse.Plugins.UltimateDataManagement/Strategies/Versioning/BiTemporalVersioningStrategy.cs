using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Versioning;

/// <summary>
/// Bi-temporal versioning strategy that tracks both valid time (business time) and transaction time (system time).
/// Enables queries like "What did we know about X on date Y?" or "What was the actual value of X at time Y?"
/// </summary>
/// <remarks>
/// Bi-temporal data model supports two orthogonal time dimensions:
/// <list type="bullet">
///   <item><term>Valid Time (VT)</term><description>When the fact was true in reality (business time)</description></item>
///   <item><term>Transaction Time (TT)</term><description>When the fact was recorded in the system</description></item>
/// </list>
///
/// This enables four types of temporal queries:
/// <list type="number">
///   <item>Current state (VT=now, TT=now)</item>
///   <item>Point-in-time state (VT=specific, TT=now)</item>
///   <item>As-known-at state (VT=now, TT=specific)</item>
///   <item>Historical knowledge state (VT=specific, TT=specific)</item>
/// </list>
///
/// Common use cases:
/// - Financial auditing and compliance
/// - Legal document management
/// - Insurance policy management
/// - Medical records with correction history
/// - Regulatory reporting with audit trails
/// </remarks>
public sealed class BiTemporalVersioningStrategy : VersioningStrategyBase
{
    private readonly ConcurrentDictionary<string, BiTemporalObjectStore> _stores = new();
    private TimeSpan _defaultValidTimeDuration = TimeSpan.FromDays(36500); // ~100 years

    /// <summary>
    /// Represents a bi-temporal fact with both valid time and transaction time dimensions.
    /// </summary>
    public sealed class BiTemporalFact
    {
        /// <summary>
        /// Unique identifier for this fact record.
        /// </summary>
        public required string FactId { get; init; }

        /// <summary>
        /// Object ID this fact belongs to.
        /// </summary>
        public required string ObjectId { get; init; }

        /// <summary>
        /// Start of valid time period (when the fact became true in reality).
        /// </summary>
        public required DateTime ValidTimeStart { get; init; }

        /// <summary>
        /// End of valid time period (when the fact stopped being true in reality).
        /// </summary>
        public required DateTime ValidTimeEnd { get; init; }

        /// <summary>
        /// Start of transaction time (when the fact was recorded in the system).
        /// </summary>
        public required DateTime TransactionTimeStart { get; init; }

        /// <summary>
        /// End of transaction time (when the fact was superseded or deleted).
        /// </summary>
        public DateTime TransactionTimeEnd { get; set; } = DateTime.MaxValue;

        /// <summary>
        /// The actual data content.
        /// </summary>
        public required byte[] Data { get; init; }

        /// <summary>
        /// Content hash for integrity verification.
        /// </summary>
        public required string ContentHash { get; init; }

        /// <summary>
        /// Size of the data in bytes.
        /// </summary>
        public long SizeBytes { get; init; }

        /// <summary>
        /// Version metadata.
        /// </summary>
        public VersionMetadata? Metadata { get; init; }

        /// <summary>
        /// Whether this fact is currently active (TT_end = infinity).
        /// </summary>
        public bool IsCurrentTransaction => TransactionTimeEnd == DateTime.MaxValue;

        /// <summary>
        /// Whether this fact is currently valid (VT contains now).
        /// </summary>
        public bool IsCurrentlyValid
        {
            get
            {
                var now = DateTime.UtcNow;
                return ValidTimeStart <= now && ValidTimeEnd > now;
            }
        }

        /// <summary>
        /// Checks if this fact was valid at a specific point in time.
        /// </summary>
        public bool WasValidAt(DateTime validTime) =>
            ValidTimeStart <= validTime && ValidTimeEnd > validTime;

        /// <summary>
        /// Checks if this fact was known at a specific transaction time.
        /// </summary>
        public bool WasKnownAt(DateTime transactionTime) =>
            TransactionTimeStart <= transactionTime && TransactionTimeEnd > transactionTime;

        /// <summary>
        /// Checks if this fact matches both valid and transaction time criteria.
        /// </summary>
        public bool MatchesBiTemporal(DateTime validTime, DateTime transactionTime) =>
            WasValidAt(validTime) && WasKnownAt(transactionTime);
    }

    /// <summary>
    /// Result of a bi-temporal query.
    /// </summary>
    public sealed class BiTemporalQueryResult
    {
        /// <summary>
        /// The matching facts.
        /// </summary>
        public required IReadOnlyList<BiTemporalFact> Facts { get; init; }

        /// <summary>
        /// Valid time used in the query.
        /// </summary>
        public DateTime QueryValidTime { get; init; }

        /// <summary>
        /// Transaction time used in the query.
        /// </summary>
        public DateTime QueryTransactionTime { get; init; }

        /// <summary>
        /// Query execution time.
        /// </summary>
        public TimeSpan ExecutionTime { get; init; }

        /// <summary>
        /// Whether any facts were found.
        /// </summary>
        public bool HasResults => Facts.Count > 0;

        /// <summary>
        /// Gets the most recent fact by transaction time.
        /// </summary>
        public BiTemporalFact? MostRecentFact =>
            Facts.OrderByDescending(f => f.TransactionTimeStart).FirstOrDefault();
    }

    /// <summary>
    /// Options for bi-temporal insertion.
    /// </summary>
    public sealed class BiTemporalInsertOptions
    {
        /// <summary>
        /// Start of the valid time period. Defaults to now.
        /// </summary>
        public DateTime? ValidTimeStart { get; init; }

        /// <summary>
        /// End of the valid time period. Defaults to far future.
        /// </summary>
        public DateTime? ValidTimeEnd { get; init; }

        /// <summary>
        /// If true, automatically close any overlapping valid time periods.
        /// </summary>
        public bool AutoCloseOverlaps { get; init; } = true;

        /// <summary>
        /// If true, this is a correction to historical data (backfill).
        /// </summary>
        public bool IsCorrection { get; init; }

        /// <summary>
        /// Reason for the correction (if applicable).
        /// </summary>
        public string? CorrectionReason { get; init; }
    }

    /// <summary>
    /// Store for a single object's bi-temporal facts.
    /// </summary>
    private sealed class BiTemporalObjectStore
    {
        private readonly object _lock = new();
        private readonly List<BiTemporalFact> _facts = new();
        private long _nextFactNumber = 1;

        /// <summary>
        /// Inserts a new bi-temporal fact.
        /// </summary>
        public BiTemporalFact Insert(
            string objectId,
            byte[] data,
            VersionMetadata? metadata,
            BiTemporalInsertOptions options)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var validStart = options.ValidTimeStart ?? now;
                var validEnd = options.ValidTimeEnd ?? DateTime.MaxValue;
                var factId = $"{objectId}-{_nextFactNumber++:D8}";

                // Auto-close overlapping valid time periods if requested
                if (options.AutoCloseOverlaps)
                {
                    foreach (var existing in _facts.Where(f =>
                        f.IsCurrentTransaction &&
                        f.ValidTimeStart < validEnd &&
                        f.ValidTimeEnd > validStart))
                    {
                        // Close the existing fact's transaction time
                        existing.TransactionTimeEnd = now;
                    }
                }

                var fact = new BiTemporalFact
                {
                    FactId = factId,
                    ObjectId = objectId,
                    ValidTimeStart = validStart,
                    ValidTimeEnd = validEnd,
                    TransactionTimeStart = now,
                    TransactionTimeEnd = DateTime.MaxValue,
                    Data = data,
                    ContentHash = ComputeHashStatic(data),
                    SizeBytes = data.Length,
                    Metadata = metadata != null ? metadata with
                    {
                        Properties = new Dictionary<string, string>(metadata.Properties ?? [])
                        {
                            ["valid_time_start"] = validStart.ToString("O"),
                            ["valid_time_end"] = validEnd.ToString("O"),
                            ["is_correction"] = options.IsCorrection.ToString(),
                            ["correction_reason"] = options.CorrectionReason ?? ""
                        }
                    } : new VersionMetadata
                    {
                        Properties = new Dictionary<string, string>
                        {
                            ["valid_time_start"] = validStart.ToString("O"),
                            ["valid_time_end"] = validEnd.ToString("O"),
                            ["is_correction"] = options.IsCorrection.ToString()
                        }
                    }
                };

                _facts.Add(fact);
                return fact;
            }
        }

        /// <summary>
        /// Queries for facts at a specific bi-temporal point.
        /// </summary>
        public BiTemporalQueryResult Query(DateTime validTime, DateTime transactionTime)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            lock (_lock)
            {
                var matching = _facts
                    .Where(f => f.MatchesBiTemporal(validTime, transactionTime))
                    .OrderByDescending(f => f.TransactionTimeStart)
                    .ToList();

                sw.Stop();
                return new BiTemporalQueryResult
                {
                    Facts = matching,
                    QueryValidTime = validTime,
                    QueryTransactionTime = transactionTime,
                    ExecutionTime = sw.Elapsed
                };
            }
        }

        /// <summary>
        /// Gets the current state (VT=now, TT=now).
        /// </summary>
        public BiTemporalFact? GetCurrentState()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                return _facts
                    .Where(f => f.IsCurrentTransaction && f.IsCurrentlyValid)
                    .OrderByDescending(f => f.TransactionTimeStart)
                    .FirstOrDefault();
            }
        }

        /// <summary>
        /// Gets the state at a specific valid time (as known now).
        /// </summary>
        public BiTemporalFact? GetStateAtValidTime(DateTime validTime)
        {
            lock (_lock)
            {
                return _facts
                    .Where(f => f.IsCurrentTransaction && f.WasValidAt(validTime))
                    .OrderByDescending(f => f.TransactionTimeStart)
                    .FirstOrDefault();
            }
        }

        /// <summary>
        /// Gets the state as it was known at a specific transaction time.
        /// </summary>
        public BiTemporalFact? GetStateAsKnownAt(DateTime transactionTime)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                return _facts
                    .Where(f => f.WasKnownAt(transactionTime) && f.WasValidAt(now))
                    .OrderByDescending(f => f.TransactionTimeStart)
                    .FirstOrDefault();
            }
        }

        /// <summary>
        /// Gets the complete timeline of all facts.
        /// </summary>
        public IReadOnlyList<BiTemporalFact> GetTimeline(bool includeSuperseded = false)
        {
            lock (_lock)
            {
                var query = includeSuperseded
                    ? _facts.AsEnumerable()
                    : _facts.Where(f => f.IsCurrentTransaction);

                return query
                    .OrderBy(f => f.ValidTimeStart)
                    .ThenBy(f => f.TransactionTimeStart)
                    .ToList();
            }
        }

        /// <summary>
        /// Gets fact by ID.
        /// </summary>
        public BiTemporalFact? GetFact(string factId)
        {
            lock (_lock)
            {
                return _facts.FirstOrDefault(f => f.FactId == factId);
            }
        }

        /// <summary>
        /// Logically deletes a fact by closing its transaction time.
        /// </summary>
        public bool DeleteFact(string factId)
        {
            lock (_lock)
            {
                var fact = _facts.FirstOrDefault(f => f.FactId == factId && f.IsCurrentTransaction);
                if (fact == null) return false;

                fact.TransactionTimeEnd = DateTime.UtcNow;
                return true;
            }
        }

        /// <summary>
        /// Gets the count of active facts.
        /// </summary>
        public long GetActiveFactCount()
        {
            lock (_lock)
            {
                return _facts.Count(f => f.IsCurrentTransaction);
            }
        }

        /// <summary>
        /// Gets the total count of all facts including superseded.
        /// </summary>
        public long GetTotalFactCount()
        {
            lock (_lock)
            {
                return _facts.Count;
            }
        }

        /// <summary>
        /// Applies retention policy, removing facts older than the threshold.
        /// </summary>
        public int ApplyRetention(TimeSpan transactionTimeRetention)
        {
            lock (_lock)
            {
                var cutoff = DateTime.UtcNow - transactionTimeRetention;
                var toRemove = _facts
                    .Where(f => f.TransactionTimeEnd < cutoff)
                    .ToList();

                foreach (var fact in toRemove)
                {
                    _facts.Remove(fact);
                }

                return toRemove.Count;
            }
        }

        private static string ComputeHashStatic(byte[] data)
        {
            var hash = SHA256.HashData(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }

    /// <inheritdoc/>
    public override string StrategyId => "versioning.bitemporal";

    /// <inheritdoc/>
    public override string DisplayName => "Bi-Temporal Versioning";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = true,
        SupportsTTL = true,
        MaxThroughput = 50_000,
        TypicalLatencyMs = 0.5
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Bi-temporal versioning strategy tracking both valid time (business time) and transaction time (system time). " +
        "Enables complex temporal queries like 'What did we know about X on date Y?' " +
        "Supports SQL:2011 temporal semantics with AS OF SYSTEM TIME and FOR SYSTEM_TIME queries. " +
        "Ideal for auditing, compliance, legal documents, and financial records.";

    /// <inheritdoc/>
    public override string[] Tags => ["versioning", "bitemporal", "temporal", "valid-time", "transaction-time", "audit", "compliance"];

    /// <summary>
    /// Gets or sets the default valid time duration for new facts.
    /// </summary>
    public TimeSpan DefaultValidTimeDuration
    {
        get => _defaultValidTimeDuration;
        set => _defaultValidTimeDuration = value > TimeSpan.Zero ? value : TimeSpan.FromDays(36500);
    }

    /// <summary>
    /// Inserts a new bi-temporal fact.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="data">Data stream.</param>
    /// <param name="metadata">Version metadata.</param>
    /// <param name="options">Bi-temporal insertion options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created bi-temporal fact.</returns>
    public async Task<BiTemporalFact> InsertBiTemporalAsync(
        string objectId,
        Stream data,
        VersionMetadata? metadata,
        BiTemporalInsertOptions? options = null,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentNullException.ThrowIfNull(data);

        ct.ThrowIfCancellationRequested();

        using var ms = new MemoryStream();
        await data.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        var store = _stores.GetOrAdd(objectId, _ => new BiTemporalObjectStore());
        return store.Insert(objectId, bytes, metadata, options ?? new BiTemporalInsertOptions());
    }

    /// <summary>
    /// Queries the bi-temporal state at specific valid and transaction times.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="validTime">The valid time to query (when was this true in reality).</param>
    /// <param name="transactionTime">The transaction time to query (what did we know at this time).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Query result with matching facts.</returns>
    public Task<BiTemporalQueryResult> QueryBiTemporalAsync(
        string objectId,
        DateTime validTime,
        DateTime transactionTime,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
        {
            return Task.FromResult(new BiTemporalQueryResult
            {
                Facts = [],
                QueryValidTime = validTime,
                QueryTransactionTime = transactionTime,
                ExecutionTime = TimeSpan.Zero
            });
        }

        return Task.FromResult(store.Query(validTime, transactionTime));
    }

    /// <summary>
    /// Gets the current state (both valid time and transaction time = now).
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current fact or null.</returns>
    public Task<BiTemporalFact?> GetCurrentStateAsync(string objectId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult<BiTemporalFact?>(null);

        return Task.FromResult(store.GetCurrentState());
    }

    /// <summary>
    /// Gets the state at a specific valid time, as currently known.
    /// Answers: "What is the current knowledge about the state at valid time X?"
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="validTime">The valid time to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Fact valid at that time or null.</returns>
    public Task<BiTemporalFact?> GetStateAtValidTimeAsync(
        string objectId,
        DateTime validTime,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult<BiTemporalFact?>(null);

        return Task.FromResult(store.GetStateAtValidTime(validTime));
    }

    /// <summary>
    /// Gets the state as it was known at a specific transaction time.
    /// Answers: "What did we know at transaction time X about the current state?"
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="transactionTime">The transaction time to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Fact as known at that time or null.</returns>
    public Task<BiTemporalFact?> GetStateAsKnownAtAsync(
        string objectId,
        DateTime transactionTime,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult<BiTemporalFact?>(null);

        return Task.FromResult(store.GetStateAsKnownAt(transactionTime));
    }

    /// <summary>
    /// Gets the complete bi-temporal timeline of an object.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="includeSuperseded">Whether to include superseded (logically deleted) facts.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Timeline of all facts.</returns>
    public Task<IReadOnlyList<BiTemporalFact>> GetTimelineAsync(
        string objectId,
        bool includeSuperseded = false,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult<IReadOnlyList<BiTemporalFact>>([]);

        return Task.FromResult(store.GetTimeline(includeSuperseded));
    }

    /// <summary>
    /// Corrects historical data by inserting a fact with a past valid time.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="data">Corrected data.</param>
    /// <param name="validTimeStart">When the corrected fact was actually true.</param>
    /// <param name="validTimeEnd">When the corrected fact stopped being true.</param>
    /// <param name="correctionReason">Reason for the correction.</param>
    /// <param name="metadata">Additional metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The correction fact.</returns>
    public async Task<BiTemporalFact> CorrectHistoricalDataAsync(
        string objectId,
        Stream data,
        DateTime validTimeStart,
        DateTime validTimeEnd,
        string correctionReason,
        VersionMetadata? metadata = null,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(correctionReason);

        return await InsertBiTemporalAsync(objectId, data, metadata, new BiTemporalInsertOptions
        {
            ValidTimeStart = validTimeStart,
            ValidTimeEnd = validTimeEnd,
            IsCorrection = true,
            CorrectionReason = correctionReason,
            AutoCloseOverlaps = true
        }, ct);
    }

    /// <summary>
    /// Applies retention policy to all objects.
    /// </summary>
    /// <param name="transactionTimeRetention">How long to keep superseded transaction records.</param>
    /// <returns>Total number of facts removed.</returns>
    public int ApplyRetentionPolicy(TimeSpan transactionTimeRetention)
    {
        var total = 0;
        foreach (var store in _stores.Values)
        {
            total += store.ApplyRetention(transactionTimeRetention);
        }
        return total;
    }

    #region IVersioningStrategy Implementation

    /// <inheritdoc/>
    protected override async Task<VersionInfo> CreateVersionCoreAsync(
        string objectId,
        Stream data,
        VersionMetadata metadata,
        CancellationToken ct)
    {
        var fact = await InsertBiTemporalAsync(objectId, data, metadata, ct: ct);

        return new VersionInfo
        {
            VersionId = fact.FactId,
            ObjectId = objectId,
            VersionNumber = long.Parse(fact.FactId.Split('-').Last()),
            ContentHash = fact.ContentHash,
            SizeBytes = fact.SizeBytes,
            CreatedAt = fact.TransactionTimeStart,
            Metadata = fact.Metadata,
            IsCurrent = fact.IsCurrentTransaction && fact.IsCurrentlyValid,
            IsDeleted = !fact.IsCurrentTransaction
        };
    }

    /// <inheritdoc/>
    protected override Task<Stream> GetVersionCoreAsync(string objectId, string versionId, CancellationToken ct)
    {
        if (!_stores.TryGetValue(objectId, out var store))
            throw new KeyNotFoundException($"No bi-temporal facts exist for object '{objectId}'.");

        var fact = store.GetFact(versionId)
            ?? throw new KeyNotFoundException($"Fact '{versionId}' not found for object '{objectId}'.");

        return Task.FromResult<Stream>(new MemoryStream(fact.Data, writable: false));
    }

    /// <inheritdoc/>
    protected override Task<IEnumerable<VersionInfo>> ListVersionsCoreAsync(
        string objectId,
        VersionListOptions options,
        CancellationToken ct)
    {
        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult<IEnumerable<VersionInfo>>([]);

        var timeline = store.GetTimeline(options.IncludeDeleted);

        var query = timeline.AsEnumerable();

        if (options.FromDate.HasValue)
            query = query.Where(f => f.TransactionTimeStart >= options.FromDate.Value);

        if (options.ToDate.HasValue)
            query = query.Where(f => f.TransactionTimeStart <= options.ToDate.Value);

        var results = query
            .OrderByDescending(f => f.TransactionTimeStart)
            .Take(options.MaxResults)
            .Select(f => new VersionInfo
            {
                VersionId = f.FactId,
                ObjectId = objectId,
                VersionNumber = long.Parse(f.FactId.Split('-').Last()),
                ContentHash = f.ContentHash,
                SizeBytes = f.SizeBytes,
                CreatedAt = f.TransactionTimeStart,
                Metadata = f.Metadata,
                IsCurrent = f.IsCurrentTransaction && f.IsCurrentlyValid,
                IsDeleted = !f.IsCurrentTransaction
            })
            .ToList();

        return Task.FromResult<IEnumerable<VersionInfo>>(results);
    }

    /// <inheritdoc/>
    protected override Task<bool> DeleteVersionCoreAsync(string objectId, string versionId, CancellationToken ct)
    {
        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult(false);

        return Task.FromResult(store.DeleteFact(versionId));
    }

    /// <inheritdoc/>
    protected override async Task<VersionInfo?> GetCurrentVersionCoreAsync(string objectId, CancellationToken ct)
    {
        var fact = await GetCurrentStateAsync(objectId, ct);
        if (fact == null) return null;

        return new VersionInfo
        {
            VersionId = fact.FactId,
            ObjectId = objectId,
            VersionNumber = long.Parse(fact.FactId.Split('-').Last()),
            ContentHash = fact.ContentHash,
            SizeBytes = fact.SizeBytes,
            CreatedAt = fact.TransactionTimeStart,
            Metadata = fact.Metadata,
            IsCurrent = true,
            IsDeleted = false
        };
    }

    /// <inheritdoc/>
    protected override async Task<VersionInfo> RestoreVersionCoreAsync(string objectId, string versionId, CancellationToken ct)
    {
        if (!_stores.TryGetValue(objectId, out var store))
            throw new KeyNotFoundException($"No bi-temporal facts exist for object '{objectId}'.");

        var fact = store.GetFact(versionId)
            ?? throw new KeyNotFoundException($"Fact '{versionId}' not found for object '{objectId}'.");

        // Create a new fact with the same data but current valid/transaction times
        using var ms = new MemoryStream(fact.Data);
        return await CreateVersionCoreAsync(objectId, ms, new VersionMetadata
        {
            Author = fact.Metadata?.Author,
            Message = $"Restored from bi-temporal fact {versionId}",
            ParentVersionId = versionId,
            Properties = new Dictionary<string, string>
            {
                ["restored_from"] = versionId,
                ["original_valid_start"] = fact.ValidTimeStart.ToString("O"),
                ["original_valid_end"] = fact.ValidTimeEnd.ToString("O")
            }
        }, ct);
    }

    /// <inheritdoc/>
    protected override Task<VersionDiff> DiffVersionsCoreAsync(
        string objectId,
        string fromVersionId,
        string toVersionId,
        CancellationToken ct)
    {
        if (!_stores.TryGetValue(objectId, out var store))
            throw new KeyNotFoundException($"No bi-temporal facts exist for object '{objectId}'.");

        var fromFact = store.GetFact(fromVersionId)
            ?? throw new KeyNotFoundException($"Fact '{fromVersionId}' not found.");
        var toFact = store.GetFact(toVersionId)
            ?? throw new KeyNotFoundException($"Fact '{toVersionId}' not found.");

        var isIdentical = fromFact.ContentHash == toFact.ContentHash;

        return Task.FromResult(new VersionDiff
        {
            FromVersionId = fromVersionId,
            ToVersionId = toVersionId,
            SizeDifference = toFact.SizeBytes - fromFact.SizeBytes,
            IsIdentical = isIdentical,
            DeltaBytes = null,
            Summary = isIdentical
                ? "Bi-temporal facts are content-identical"
                : $"Valid time: [{fromFact.ValidTimeStart:O}, {fromFact.ValidTimeEnd:O}] -> [{toFact.ValidTimeStart:O}, {toFact.ValidTimeEnd:O}], " +
                  $"Transaction time: {fromFact.TransactionTimeStart:O} -> {toFact.TransactionTimeStart:O}"
        });
    }

    /// <inheritdoc/>
    protected override Task<long> GetVersionCountCoreAsync(string objectId, CancellationToken ct)
    {
        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult(0L);

        return Task.FromResult(store.GetActiveFactCount());
    }

    #endregion
}
